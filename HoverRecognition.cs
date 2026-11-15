using Windows.Media.Ocr;

namespace ScreenTranslator;

internal static class HoverRecognition
{
    internal delegate Task<ScreenLine[]> ReadRegion(Rectangle area, float scale, Point cursor,
        Func<ScreenLine[], bool>? hasTarget, CancellationToken token, bool allowMixedText, bool largeDisplayOnly, bool paragraph);
    internal delegate Task<ScreenLine[]?> ReadOptionRegion(Rectangle area, Point cursor, CancellationToken token);

    public static async Task<(Selection? Target, Selection? Word)> ReadAsync(Rectangle available,
        Rectangle capture, Rectangle wider, Point cursor, bool paragraph, bool useGamePreprocessing,
        TermGlossary? glossary, ReadRegion readRegion, CancellationToken token, ReadOptionRegion? readOption = null)
    {
        var wordScale = useGamePreprocessing ? 2.4f : 2.2f;
        var paragraphScale = 1.65f;
        var lines = await Read(capture, paragraph ? paragraphScale : wordScale).ConfigureAwait(false);
        var target = Select(lines);
        if (!paragraph && readOption is not null && (capture.Left > available.Left || capture.Right < available.Right)
            && (target is null || lines.Any(line => line.Bounds.Height >= 17
                && RectangleF.Inflate(line.Bounds, 15, 12).Contains(cursor))))
        {
            var band = Rectangle.FromLTRB(available.Left, wider.Top, available.Right, wider.Bottom);
            if (await readOption(band, cursor, token).ConfigureAwait(false) is { } option)
            {
                var wordInOption = Select(option);
                if (option.Length == 0 || wordInOption is not null)
                    return (wordInOption, wordInOption);
            }
        }
        if (paragraph && target is not null)
        {
            var heights = lines.Where(line => line.Bounds.IntersectsWith(target.Bounds))
                .SelectMany(line => line.Words).Where(word => word.Text.Count(char.IsLetter) >= 3)
                .Select(word => word.Bounds.Height).Order().ToArray();
            if (heights.Length != 0 && !(lines is [{ Words.Length: 1 }] && GenshinMapLabels.MightBeLabel(target.Text)))
            {
                var preferred = Math.Min(paragraphScale, 48f / heights[heights.Length / 2]);
                if (preferred < paragraphScale * .8f)
                {
                    var normalized = await Read(capture, preferred).ConfigureAwait(false);
                    if (Select(normalized) is { } candidate && TextSelection.IncludesParagraph(candidate.Bounds, target.Bounds)
                        && (candidate.Text.Count(char.IsLetterOrDigit) >= target.Text.Count(char.IsLetterOrDigit) * .96f
                            || TextSelection.RepairsBracketedLabel(candidate, target))
                        && (TextSelection.RepairsBracketedLabel(candidate, target)
                            || TextSelection.RestoresParagraphWords(candidate, target, normalized)
                            || TextSelection.ImprovesParagraphSpelling(candidate, target)
                            || !ScreenOcr.HasUnstableLetterCase(candidate.Text)
                                && candidate.Text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)
                                    .SequenceEqual(target.Text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant))))
                    {
                        lines = normalized;
                        target = candidate;
                        paragraphScale = preferred;
                    }
                }
            }
            var start = capture;
            var lineHeight = lines.Where(line => line.Bounds.IntersectsWith(target.Bounds))
                .Select(line => line.Bounds.Height).DefaultIfEmpty().Max();
            var expanded = TextSelection.ExtendParagraphCapture(capture, available, target.Bounds, lineHeight);
            while (expanded != capture)
            {
                var expandedLines = await Read(expanded, paragraphScale).ConfigureAwait(false);
                var more = Select(expandedLines);
                if (more is null || !TextSelection.IncludesParagraph(more.Bounds, target.Bounds))
                    break;

                lines = expandedLines;
                target = more;
                capture = expanded;
                expanded = TextSelection.ExtendParagraphCapture(capture, available, target.Bounds, lineHeight);
            }

            if (capture != start && target.Bounds.Height >= start.Height * 0.65f)
            {
                // Preserve thin serifs and punctuation in a tight crop at native resolution.
                var detail = TextSelection.ParagraphDetailArea(capture, target.Bounds);
                var detailLines = await Read(detail, Math.Min(1f, paragraphScale), preprocess: false).ConfigureAwait(false);
                if (Select(detailLines) is { } refined && TextSelection.PreservesParagraph(refined, target)
                    && refined.Text.Count(letter => letter is '(' or ')') >= target.Text.Count(letter => letter is '(' or ')')
                    && (TextSelection.SameParagraphWords(refined, target)
                        || TextSelection.AgreesOnParagraphWords(refined, target)
                        || TextSelection.RestoresParagraphWords(refined, target, detailLines)))
                {
                    lines = detailLines;
                    target = refined;
                }
            }
        }
        if (!paragraph && target is not null && wider != capture
            && (MightBeClipped(target.Bounds, capture, wider)
                || lines.Any(line => line.Bounds.IntersectsWith(target.Bounds)
                    && GenshinMapLabels.MightBeLabel(line.Text)
                    && MightBeClipped(line.Bounds, capture, wider))))
        {
            var retry = await Read(wider, wordScale).ConfigureAwait(false);
            target = Select(retry) ?? target;
        }
        if (!paragraph && target is not null && wider != capture && ScreenOcr.NeedsFileNameContext(target.Text))
        {
            var retry = Select(await Read(wider, wordScale).ConfigureAwait(false));
            if (retry is { Text: "ScreenTranslator" } && retry.Bounds.IntersectsWith(target.Bounds))
                target = retry;
        }
        if (!paragraph && useGamePreprocessing && target is not null && wider != capture
            && !GenshinMapLabels.IsKnownLabel(target.Text) && GenshinMapLabels.IsLabelFragment(target.Text))
        {
            var retry = await Read(wider, wordScale).ConfigureAwait(false);
            if (Select(retry) is { } label && GenshinMapLabels.IsKnownLabel(label.Text)
                && label.Bounds.IntersectsWith(target.Bounds))
                target = label;
            else if (wider.Width < available.Width)
            {
                var band = Rectangle.FromLTRB(available.Left, wider.Top, available.Right, wider.Bottom);
                var complete = Select(await Read(band, wordScale).ConfigureAwait(false));
                if (complete is not null && GenshinMapLabels.IsKnownLabel(complete.Text)
                    && complete.Bounds.IntersectsWith(target.Bounds))
                    target = complete;
            }
        }
        if (!paragraph && !useGamePreprocessing && (target is null || target.Bounds.Height >= capture.Height * .35f))
        {
            var height = Math.Min(available.Height, Math.Max(440, capture.Height * 2) * 2);
            var width = Math.Min(available.Width, 1920);
            var expanded = new Rectangle(Math.Clamp(cursor.X - width / 2, available.Left, available.Right - width),
                Math.Clamp(cursor.Y - height / 2, available.Top, available.Bottom - height), width, height);
            if (expanded != capture)
            {
                var retry = Select(await Read(expanded, wordScale, largeDisplayOnly: true).ConfigureAwait(false));
                if (retry is not null && retry.Bounds.Height >= capture.Height * .3f && retry.Bounds.Contains(cursor)
                    && (target is null || retry.Text.Length >= target.Text.Length
                        && (retry.Text.Contains(target.Text, StringComparison.OrdinalIgnoreCase)
                            || ScreenOcr.IsSingleGlyphVariant(target.Text, retry.Text))))
                    target = retry;
            }
        }
        if (!paragraph && !useGamePreprocessing && target is null && lines.Length == 0 && wider != capture)
        {
            // A mixed-language run can disappear entirely if the crop cuts off its neighboring glyphs.
            var retry = Select(await Read(wider, wordScale).ConfigureAwait(false));
            if (retry is not null && retry.Bounds.Contains(cursor))
                target = retry;
        }

        var word = paragraph ? TextSelection.At(lines, cursor, false, glossary) : target;
        return (target, word);

        Selection? Select(ScreenLine[] recognized) => TextSelection.At(recognized, cursor, paragraph, glossary);

        Task<ScreenLine[]> Read(Rectangle area, float preferredScale, bool preprocess = true, bool largeDisplayOnly = false)
        {
            token.ThrowIfCancellationRequested();
            var scale = Math.Min(preferredScale, (float)OcrEngine.MaxImageDimension / Math.Max(area.Width, area.Height));
            return readRegion(area, scale, cursor,
                useGamePreprocessing && preprocess ? recognized => Select(recognized) is not null : null,
                token, !useGamePreprocessing && !paragraph, largeDisplayOnly, paragraph);
        }
    }

    public static Rectangle CaptureArea(Rectangle area, Point point, float dpi, bool paragraph, bool expanded = false)
    {
        if (area.IsEmpty)
            return area;

        dpi = Math.Max(Math.Clamp(dpi, 1, 2.5f), Math.Clamp(area.Width / 1920f, 1, 2.5f));
        var halfHeight = (int)((paragraph ? 170 : expanded ? 100 : 55) * dpi);
        var halfWidth = (int)((expanded ? 570 : 330) * dpi);
        var step = (int)(24 * dpi);
        var center = area.Top + (point.Y - area.Top) / step * step + step / 2;
        var width = Math.Min(area.Width, halfWidth * 2);
        var band = paragraph
            ? new Rectangle(area.Left, center - halfHeight, area.Width, halfHeight * 2)
            : new Rectangle(Math.Clamp(point.X - halfWidth, area.Left, area.Right - width),
                point.Y - halfHeight, width, halfHeight * 2);
        return Rectangle.Intersect(area, band);
    }

    private static bool MightBeClipped(RectangleF word, Rectangle capture, Rectangle wider)
    {
        var margin = Math.Clamp(word.Height * 0.4f, 3f, 48f);
        return (wider.Left < capture.Left && word.Left - capture.Left <= margin)
            || (wider.Right > capture.Right && capture.Right - word.Right <= margin)
            || (wider.Top < capture.Top && word.Top - capture.Top <= margin)
            || (wider.Bottom > capture.Bottom && capture.Bottom - word.Bottom <= margin);
    }
}
