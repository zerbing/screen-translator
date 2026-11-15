using Windows.Media.Ocr;

namespace ScreenTranslator;

internal static class HoverRecognition
{
    internal delegate Task<ScreenLine[]> ReadRegion(Rectangle area, float scale, Point cursor,
        Func<ScreenLine[], bool>? hasTarget, CancellationToken token, bool allowMixedText, bool largeDisplayOnly, bool paragraph);

    public static async Task<(Selection? Target, Selection? Word)> ReadAsync(Rectangle available,
        Rectangle capture, Rectangle wider, Point cursor, bool paragraph, bool useGamePreprocessing,
        TermGlossary? glossary, ReadRegion readRegion, CancellationToken token)
    {
        var wordScale = useGamePreprocessing ? 2.4f : 2.2f;
        var lines = await Read(capture, paragraph ? 1.65f : wordScale).ConfigureAwait(false);
        var target = Select(lines);
        if (paragraph && target is not null)
        {
            var start = capture;
            var expanded = TextSelection.ExtendParagraphCapture(capture, available, target.Bounds);
            while (expanded != capture)
            {
                var expandedLines = await Read(expanded, 1.65f).ConfigureAwait(false);
                var more = Select(expandedLines);
                if (more is null || !TextSelection.IncludesParagraph(more.Bounds, target.Bounds))
                    break;

                lines = expandedLines;
                target = more;
                capture = expanded;
                expanded = TextSelection.ExtendParagraphCapture(capture, available, target.Bounds);
            }

            if (capture != start && target.Bounds.Height >= start.Height * 0.65f)
            {
                // Preserve thin serifs and punctuation in a tight crop at native resolution.
                var detail = TextSelection.ParagraphDetailArea(capture, target.Bounds);
                var detailLines = await Read(detail, 1f, preprocess: false).ConfigureAwait(false);
                if (Select(detailLines) is { } refined && TextSelection.PreservesParagraph(refined, target))
                {
                    lines = detailLines;
                    target = refined;
                }
            }
        }
        if (!paragraph && target is not null && wider != capture && MightBeClipped(target.Bounds, capture, wider))
        {
            var retry = await Read(wider, wordScale).ConfigureAwait(false);
            target = Select(retry) ?? target;
        }
        if (!paragraph && !useGamePreprocessing && (target is null || target.Bounds.Height >= capture.Height * .55f))
        {
            var halfHeight = Math.Max(220, capture.Height);
            var expanded = Rectangle.Intersect(available, new Rectangle(cursor.X - 760, cursor.Y - halfHeight, 1520, halfHeight * 2));
            if (expanded != capture)
            {
                var retry = Select(await Read(expanded, wordScale, largeDisplayOnly: true).ConfigureAwait(false));
                if (retry is not null && retry.Bounds.Height >= capture.Height * .45f && retry.Bounds.Contains(cursor)
                    && (target is null || retry.Text.Length > target.Text.Length
                        && retry.Text.StartsWith(target.Text, StringComparison.OrdinalIgnoreCase)))
                    target = retry;
            }
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
        var band = paragraph
            ? new Rectangle(area.Left, center - halfHeight, area.Width, halfHeight * 2)
            : new Rectangle(point.X - halfWidth, point.Y - halfHeight, halfWidth * 2, halfHeight * 2);
        return Rectangle.Intersect(area, band);
    }

    private static bool MightBeClipped(RectangleF word, Rectangle capture, Rectangle wider)
    {
        var margin = Math.Clamp(word.Height * 0.3f, 3f, 12f);
        return (wider.Left < capture.Left && word.Left - capture.Left <= margin)
            || (wider.Right > capture.Right && capture.Right - word.Right <= margin)
            || (wider.Top < capture.Top && word.Top - capture.Top <= margin)
            || (wider.Bottom > capture.Bottom && capture.Bottom - word.Bottom <= margin);
    }
}
