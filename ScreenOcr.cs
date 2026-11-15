using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ScreenTranslator;

internal sealed record ScreenWord(string Text, RectangleF Bounds);
internal sealed record ScreenLine(ScreenWord[] Words, RectangleF Bounds)
{
    public string Text => string.Join(' ', Words.Select(word => word.Text));
}

internal sealed partial class ScreenOcr : IDisposable
{
    private readonly OcrEngine engine;
    private readonly Lazy<OcrEngine?> mixedLanguageEngine = new(CreateMixedLanguageEngine);
    private readonly HdrScreenCapture hdrCapture = new();
    private readonly OfflineDictionary spelling = new();
    private readonly Lazy<LocalLatinOcr> latinOcr = new(() => new LocalLatinOcr());
    private readonly Lazy<LocalLatinOcr> mixedLatinOcr = new(() => new LocalLatinOcr(mixed: true));

    private ScreenOcr(OcrEngine engine) => this.engine = engine;

    public void Dispose()
    {
        hdrCapture.Dispose();
        spelling.Dispose();
        if (latinOcr.IsValueCreated)
            latinOcr.Value.Dispose();
        if (mixedLatinOcr.IsValueCreated)
            mixedLatinOcr.Value.Dispose();
    }

    [GeneratedRegex(@"(?<=[A-Za-z])[•·](?=[sS]\b)")]
    private static partial Regex PossessiveDot();

    [GeneratedRegex(@"\bScreenTrans[1Il]ator\s*[.．·]\s*exe(?:[oO0](?=\s|$))?(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppFileName();

    internal static bool NeedsFileNameContext(string text) => text != "ScreenTranslator"
        && AppFileName().IsMatch(text + ".exe");

    [GeneratedRegex(@"^[A-Za-z]{3,}/[A-Za-z]{3,}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlashSeparatedWords();

    [GeneratedRegex(@"^[A-Za-z0-9]+(?:-[A-Za-z0-9]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex HyphenatedWord();

    public static ScreenOcr? Create()
    {
        var english = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(language =>
            language.LanguageTag.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            ?? OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(language =>
                language.LanguageTag.StartsWith("en-", StringComparison.OrdinalIgnoreCase));
        var engine = english is null ? null : OcrEngine.TryCreateFromLanguage(new Language(english.LanguageTag));
        return engine is null ? null : new ScreenOcr(engine);
    }

    private static OcrEngine? CreateMixedLanguageEngine()
    {
        var chinese = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(language =>
            language.LanguageTag.Equals("zh-Hans-CN", StringComparison.OrdinalIgnoreCase)
            || language.LanguageTag.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
            ?? OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(language =>
                language.LanguageTag.StartsWith("zh-", StringComparison.OrdinalIgnoreCase));
        return chinese is null ? null : OcrEngine.TryCreateFromLanguage(chinese);
    }

    public async Task<ScreenLine[]> ReadAsync(Rectangle area, float scale, Point cursor,
        Func<ScreenLine[], bool>? hasTarget, CancellationToken token, bool allowMixedText, bool largeDisplayOnly, bool paragraph)
    {
        using var bitmap = Capture(area, token);
        return await ReadBitmapAsync(bitmap, area.Location, scale, cursor, hasTarget, token, allowMixedText, largeDisplayOnly, paragraph)
            .ConfigureAwait(false);
    }

    public async Task<ScreenLine[]?> ReadOptionAsync(Rectangle area, Point cursor, CancellationToken token)
    {
        using var bitmap = Capture(area, token);
        return await ReadOptionBitmapAsync(bitmap, area.Location, cursor, token).ConfigureAwait(false);
    }

    private Bitmap Capture(Rectangle area, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (hdrCapture.TryRead(area, token) is { } hdr)
            return hdr;
        var original = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(original);
            graphics.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
            return original;
        }
        catch
        {
            original.Dispose();
            throw;
        }
    }

    internal async Task<ScreenLine[]?> ReadOptionBitmapAsync(Bitmap original, Point origin, Point cursor, CancellationToken token)
    {
        var local = new Point(cursor.X - origin.X, cursor.Y - origin.Y);
        if (FindDialogueOption(original, local) is not { } option)
            return null;
        if (option.Bounds.Width == 0 || local.X < option.Bounds.Left)
            return [];
        var rows = await ReadOptionTextAsync(original, origin, option, token).ConfigureAwait(false);
        return rows.Length == 0 ? null : rows;
    }

    internal async Task<ScreenLine[]> ReadBitmapAsync(Bitmap original, Point origin, float scale,
        Point cursor = default, Func<ScreenLine[], bool>? hasTarget = null, CancellationToken token = default,
        bool allowMixedText = false, bool largeDisplayOnly = false, bool paragraph = false)
    {
        var rows = await ReadBitmapCoreAsync(original, origin, scale, cursor, hasTarget, token,
            allowMixedText, largeDisplayOnly, paragraph).ConfigureAwait(false);
        if (!allowMixedText && !largeDisplayOnly && hasTarget is not null)
            return RefineGameLatin(original, origin, cursor, rows, paragraph, token);
        if (!allowMixedText || largeDisplayOnly)
            return rows;
        if (ReadLocalMixedLine(original, origin, cursor, rows, token) is { } localMixed)
            return localMixed;
        if (rows.Any(row => ContainsMixedTextAt(row, cursor))
            && await ReadMixedConsensusAsync(original, origin, cursor, rows, token).ConfigureAwait(false) is { } mixed)
            rows = mixed;
        var selected = TextSelection.At(rows, cursor, false, null);
        if (selected is not null && selected.Text.Length >= 4 && selected.Text.All(char.IsAsciiLetter)
            && spelling.FrequencyRank(selected.Text) is > 0 and <= 50000
            || selected is not null && selected.Text.Length is >= 2 and <= 4 && IsUppercaseWord(selected.Text))
            return rows;
        var local = new Point(cursor.X - origin.X, cursor.Y - origin.Y);
        if (selected is null)
        {
            foreach (var red in new[] { false, true })
                if (await ReadColoredOverlayAsync(original, origin, local, cursor, red, token).ConfigureAwait(false) is { } colored)
                    return colored;
        }
        var refined = await ReadPaleLatinLineAsync(original, origin, local, cursor, token).ConfigureAwait(false);
        if (refined is not null && TextSelection.At(refined, cursor, false, null) is { } improved
            && (spelling.FrequencyRank(improved.Text) > 0 || IsUppercaseWord(improved.Text) && improved.Text.Length <= 4))
            return PreserveWordCase(refined, selected);
        return rows;
    }

    private async Task<ScreenLine[]> ReadBitmapCoreAsync(Bitmap original, Point origin, float scale,
        Point cursor, Func<ScreenLine[], bool>? hasTarget, CancellationToken token,
        bool allowMixedText, bool largeDisplayOnly, bool paragraph)
    {
        var local = new Point(cursor.X - origin.X, cursor.Y - origin.Y);
        if (largeDisplayOnly)
            return await ReadDisplayLabelAsync(original, origin, local, cursor, null, token, largeDisplayOnly: true)
                .ConfigureAwait(false) ?? [];

        var largeMapLabels = paragraph ? original.Width > 3840 : original.Height > 230;
        var mapScene = hasTarget is not null && (HasMapLabelColors(original, local)
            || HasMapLabelColors(original, local, 64) || HasMapLabelColors(original, local, 128));
        if (mapScene)
        {
            var radius = Math.Clamp(original.Width / 2, 330, largeMapLabels ? 1000 : 650);
            var halfHeight = largeMapLabels ? 144 : Math.Clamp(original.Width / 8, 75, 116);
            var width = Math.Min(original.Width, 2 * radius);
            var height = Math.Min(original.Height, 2 * halfHeight);
            var region = new Rectangle(Math.Clamp(local.X - radius, 0, original.Width - width),
                Math.Clamp(local.Y - halfHeight, 0, original.Height - height), width, height);
            using var patch = original.Clone(region, PixelFormat.Format32bppArgb);
            using var muted = LightTextMask(patch, 145, token, spread: 9);
            var patchOrigin = new Point(origin.X + region.X, origin.Y + region.Y);
            // Preserve the label's geometry when a map symbol overlaps the glyphs.
            var mapScale = largeMapLabels ? .85f : scale < 1.1f ? 1.35f : 1.85f;
            var mapRows = await RecognizeBitmapAsync(muted, patchOrigin, mapScale, token).ConfigureAwait(false);
            var label = GenshinMapLabels.At(mapRows, cursor);
            if (label is null)
            {
                var raw = await RecognizeBitmapAsync(patch, patchOrigin, 1.55f, token).ConfigureAwait(false);
                label = GenshinMapLabels.At(raw, cursor);
                if (label is null)
                {
                    var prompt = mapRows.Concat(raw).Where(line => line.Text.Contains("Thousand", StringComparison.OrdinalIgnoreCase)
                        && line.Text.Contains("Wind", StringComparison.OrdinalIgnoreCase))
                        .MinBy(line => Math.Abs(line.Bounds.Top - cursor.Y));
                    if (prompt is not null)
                    {
                        var next = Rectangle.Intersect(new Rectangle(Point.Empty, muted.Size), Rectangle.FromLTRB(
                            (int)prompt.Bounds.Left - patchOrigin.X, (int)prompt.Bounds.Bottom - patchOrigin.Y + 3,
                            (int)prompt.Bounds.Right - patchOrigin.X, (int)prompt.Bounds.Bottom - patchOrigin.Y + (int)(prompt.Bounds.Height * 1.8f)));
                        if (next.Width > 0 && next.Height > 0)
                        {
                            using var lowerLine = patch.Clone(next, PixelFormat.Format32bppArgb);
                            var lowerRows = await RecognizeBitmapAsync(lowerLine, new Point(patchOrigin.X + next.X, patchOrigin.Y + next.Y),
                                1f, token).ConfigureAwait(false);
                            label = GenshinMapLabels.At([prompt, .. lowerRows], cursor);
                        }
                    }
                }
            }
            if (label is null)
            {
                using var contrast = EnhanceLocalContrast(patch, token);
                var contrastRows = await RecognizeBitmapAsync(contrast, patchOrigin, 1.25f, token).ConfigureAwait(false);
                label = GenshinMapLabels.At(contrastRows, cursor);
            }
            if (label is null && scale <= 1.65f)
            {
                // A smaller vertical crop prevents neighboring map symbols from setting the line geometry.
                var band = Rectangle.Intersect(new Rectangle(Point.Empty, muted.Size),
                    new Rectangle(0, local.Y - region.Y - 55, muted.Width, 110));
                using var focused = muted.Clone(band, PixelFormat.Format32bppArgb);
                var alternate = await RecognizeBitmapAsync(focused, new Point(patchOrigin.X, patchOrigin.Y + band.Y),
                    1.85f, token).ConfigureAwait(false);
                label = GenshinMapLabels.At(alternate, cursor);
            }
            if (label is null)
            {
                foreach (var readingScale in new[] { 1f, .85f })
                {
                    var raw = await RecognizeBitmapAsync(patch, patchOrigin, readingScale, token).ConfigureAwait(false);
                    label = GenshinMapLabels.At(raw, cursor);
                    if (label is not null)
                        break;
                }
            }
            if (label is null)
            {
                // Snow and pale map icons need a stricter white mask than dark terrain.
                foreach (var mapCutoff in new[] { 235, 205 })
                {
                    using var white = LightTextMask(patch, mapCutoff, token, spread: 24);
                    var rows = await RecognizeBitmapAsync(white, patchOrigin, 1.25f, token).ConfigureAwait(false);
                    label = GenshinMapLabels.At(rows, cursor);
                    if (label is not null)
                        break;
                }
            }
            if (label is not null && hasTarget!([label]))
                return [label];
        }

        if (hasTarget is not null && HasGoldLettering(original, local)
            && !HasMixedBrightText(original, local, verticalRadius: 12))
        {
            using var gold = ConvertPixels(original, token, (red, green, blue) =>
                (byte)(255 - Math.Clamp((green - blue - 15) * 255 / 50, 0, 255)));
            var rows = await RecognizeBitmapAsync(gold, origin, scale, token).ConfigureAwait(false);
            if (hasTarget(rows))
                return await RefineGameTextAsync(rows, gold).ConfigureAwait(false);
        }

        if (paragraph && hasTarget is not null && HasMixedBrightText(original, local, cyanOnly: true))
        {
            // Preserve saturated lettering alongside the pale text on the same lines.
            using var mask = ConvertPixels(original, token, (red, green, blue) =>
                IsPale(red, green, blue, 220, 28) || IsBrightCyan(red, green, blue) ? (byte)0 : (byte)255);
            var rows = await RecognizeBitmapAsync(mask, origin, scale, token).ConfigureAwait(false);
            if (TextSelection.At(rows, cursor, true, null) is { } candidate
                && HasCyanEmphasis(original, origin, candidate.Bounds, rows))
            {
                if (candidate.Bounds.Height > 300)
                {
                    var detail = TextSelection.ParagraphDetailArea(new Rectangle(origin, mask.Size), candidate.Bounds);
                    using var focused = mask.Clone(new Rectangle(detail.X - origin.X, detail.Y - origin.Y,
                        detail.Width, detail.Height), PixelFormat.Format32bppArgb);
                    var detailScale = Math.Min(.8f, (float)OcrEngine.MaxImageDimension / Math.Max(detail.Width, detail.Height));
                    var refined = await RecognizeBitmapAsync(focused, detail.Location, detailScale, token).ConfigureAwait(false);
                    if (TextSelection.At(refined, cursor, true, null) is { } reread
                        && TextSelection.PreservesParagraph(reread, candidate)
                        && reread.Text.Where(char.IsLetterOrDigit).SequenceEqual(candidate.Text.Where(char.IsLetterOrDigit)))
                        rows = refined;
                }
                return await RefineGameTextAsync(rows, mask).ConfigureAwait(false);
            }
        }

        var gameBackground = hasTarget is null ? null : SampleLuminance(original, local, 64, 40);
        if (hasTarget is not null)
        {
            var sample = SampleLetteringColor(original, local);
            if (sample is { } fill && (fill.R <= 70
                || !HasDarkTextOnLightBackground(gameBackground!)))
            {
                using var flat = LightTextMask(original, 0, token, fill);
                var rows = await RecognizeBitmapAsync(flat, origin, scale, token).ConfigureAwait(false);
                if (hasTarget(rows))
                    return await RefineGameTextAsync(rows.Select(line => FixSeriesNumeral(line, rows)).ToArray(), flat).ConfigureAwait(false);
            }
        }

        if (hasTarget is not null && LightLetteringCutoff(original, local) is { } cutoff
            && !HasDarkTextOnLightBackground(gameBackground!))
        {
            token.ThrowIfCancellationRequested();
            using var mask = LightTextMask(original, cutoff, token);
            var lightLines = await RecognizeBitmapAsync(mask, origin, scale, token).ConfigureAwait(false);
            if (hasTarget(lightLines))
                return await RefineGameTextAsync(lightLines, mask).ConfigureAwait(false);
        }

        if (paragraph && hasTarget is not null && await ReadWhiteChoiceAsync().ConfigureAwait(false) is { } choice)
            return choice;

        token.ThrowIfCancellationRequested();
        var luminance = allowMixedText ? SampleLuminance(original, local) : null;
        using var boosted = luminance is null ? null : BoostFaintText(original, luminance, token);
        var wordImage = boosted ?? original;
        var originalLines = await RecognizeBitmapAsync(wordImage, origin, scale, token).ConfigureAwait(false);
        if (mapScene && GenshinMapLabels.At(originalLines, cursor) is { } mapped)
            return [mapped];
        if (await ReadDialogueOptionAsync(originalLines).ConfigureAwait(false) is { } recognizedOption)
            return recognizedOption;
        if (allowMixedText)
        {
            if (mixedLanguageEngine.Value is { } bilingual)
            {
                var surroundings = SampleLuminance(original, local, 64, 40);
                using var recovered = BoostFaintText(original, surroundings, token, compressedHighlights: true);
                using var widerContrast = recovered is null && boosted is null
                    ? BoostFaintText(original, surroundings, token) : null;
                var compound = (recovered ?? boosted ?? widerContrast) is { } faint
                    ? await ReadFaintCompoundAsync(faint, origin, cursor, bilingual, token).ConfigureAwait(false) : null;
                var reading = compound is null ? null : TextSelection.At(compound, cursor, false, null)?.Text;
                var ambiguousStroke = reading is null ? -1 : InternalCapitalI(reading);
                if (compound is not null && ambiguousStroke < 0)
                    return compound;

                // Keep the first reading unless a second pass isolates the same letters with a lowercase l.
                var expectedReading = reading is null ? null : reading[..ambiguousStroke] + 'l' + reading[(ambiguousStroke + 1)..];
                bool AcceptRefinedWord(string text) => expectedReading is null ? InternalCapitalI(text) < 0 : text == expectedReading;
                using var alternative = BoostFaintText(original, surroundings, token, compressedHighlights: true,
                    sparseInk: true, transfer: (52, 0));
                var sharper = alternative is null ? null : await ReadFaintCompoundAsync(alternative, origin, cursor,
                    bilingual, token, acceptWord: AcceptRefinedWord).ConfigureAwait(false);
                if (sharper is not null && reading is not null)
                    return sharper;

                using var subtle = BoostFaintText(original, surroundings, token, compressedHighlights: true, sparseInk: true);
                var softer = subtle is null ? null : await ReadFaintCompoundAsync(subtle, origin, cursor,
                    bilingual, token, smallText: true, acceptWord: AcceptRefinedWord).ConfigureAwait(false);
                if (softer is not null && (sharper is null || ExtendsFaintCompound(sharper, softer)))
                    return softer;
                if (sharper is not null)
                    return sharper;
                if (compound is not null)
                    return compound;
            }
            var radius = Math.Clamp(original.Width / 4, 150, 250);
            var halfHeight = Math.Clamp(original.Height / 3, 27, 58);
            var region = Rectangle.Intersect(new Rectangle(Point.Empty, original.Size),
                new Rectangle(local.X - radius, local.Y - halfHeight, radius * 2, halfHeight * 2));
            using var patch = wordImage.Clone(region, PixelFormat.Format32bppArgb);
            var patchOrigin = new Point(origin.X + region.X, origin.Y + region.Y);
            var originalWord = TextSelection.At(originalLines, cursor, false, null);
            var overlay = await ReadDisplayLabelAsync(original, origin, local, cursor, originalWord, token).ConfigureAwait(false);
            if (overlay is not null)
                return overlay;
            var paleLabel = await ReadSmallPaleLabelAsync(original, origin, local, cursor, originalWord, token).ConfigureAwait(false);
            if (paleLabel is not null)
                return paleLabel;
            var coloredWord = await ReadColoredHyphenatedWordAsync(original, origin, local, cursor, token).ConfigureAwait(false);
            if (coloredWord is not null)
                return coloredWord;
            if (HasBrightTextOnBlack(original, local, luminance!))
            {
                var displayArea = original.Width <= 700 ? new Rectangle(Point.Empty, original.Size)
                    : Rectangle.Intersect(new Rectangle(Point.Empty, original.Size),
                        new Rectangle(local.X - 340, 0, 680, original.Height));
                using var displayCrop = original.Clone(displayArea, PixelFormat.Format32bppArgb);
                using var displayText = LightTextMask(displayCrop, 230, token, whiteOnBlack: true);
                var displayOrigin = new Point(origin.X + displayArea.X, origin.Y + displayArea.Y);
                if (FindDisplayLine(displayText, new Point(cursor.X - displayOrigin.X, cursor.Y - displayOrigin.Y)) is { } line)
                {
                    const int horizontalPadding = 4;
                    const int verticalPadding = 15;
                    using var lineImage = new Bitmap(line.Width + horizontalPadding * 2, line.Height + verticalPadding * 2,
                        PixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(lineImage))
                    {
                        graphics.Clear(Color.Black);
                        graphics.DrawImage(displayText, new Rectangle(horizontalPadding, verticalPadding, line.Width, line.Height),
                            line, GraphicsUnit.Pixel);
                    }
                    var lineOrigin = new Point(displayOrigin.X + line.X - horizontalPadding, displayOrigin.Y + line.Y - verticalPadding);
                    var normalized = await ReadCondensedLineAsync(displayCrop, line, displayOrigin, cursor, token).ConfigureAwait(false);
                    if (normalized is not null)
                        return normalized;
                    foreach (var readingScale in new[] { 1.5f, 2.5f, 3.5f })
                    {
                        var lineRows = await RecognizeBitmapAsync(lineImage, lineOrigin, readingScale, token).ConfigureAwait(false);
                        if (TextSelection.At(lineRows, cursor, false, null) is { } hit && IsUppercaseWord(hit.Text))
                            return lineRows;
                    }

                }
                foreach (var readingScale in new[] { 3.5f, 2.2f, 1.5f })
                {
                    var boundedScale = Math.Min(readingScale, (float)OcrEngine.MaxImageDimension / Math.Max(displayText.Width, displayText.Height));
                    var displayLines = await RecognizeBitmapAsync(displayText, displayOrigin, boundedScale, token).ConfigureAwait(false);
                    if (TextSelection.At(displayLines, cursor, false, null) is { } displayWord && IsUppercaseWord(displayWord.Text))
                        return displayLines;
                }
            }
            if (mixedLanguageEngine.Value is { } mixed)
            {
                var mixedLines = await RecognizeBitmapAsync(patch, patchOrigin, Math.Max(scale, 2f), token, mixed).ConfigureAwait(false);
                // Trust the mixed-language read only when it actually finds Chinese near the pointer.
                if (mixedLines.Any(line => ContainsMixedTextAt(line, cursor)))
                {
                    // Read the full line at a moderate scale. Enlarging Latin and Han characters
                    // together can cause the bilingual recognizer to split a label mid-word.
                    var stable = Rectangle.Intersect(new Rectangle(Point.Empty, original.Size),
                        new Rectangle(0, local.Y - 25, original.Width, 50));
                    using var fullLine = wordImage.Clone(stable, PixelFormat.Format32bppArgb);
                    var lineOrigin = new Point(origin.X, origin.Y + stable.Y);
                    var mixedGlyphHeights = mixedLines.Where(line => ContainsMixedTextAt(line, cursor))
                        .SelectMany(line => line.Words).Where(word => word.Text.Any(char.IsLetter))
                        .Select(word => word.Bounds.Height).Order().ToArray();
                    var mixedScale = mixedGlyphHeights.Length == 0 ? 1.5f
                        : Math.Clamp(32f / mixedGlyphHeights[mixedGlyphHeights.Length / 2], .6f, 4f);
                    var stableLines = await RecognizeBitmapAsync(fullLine, lineOrigin,
                        Math.Min(1.5f, (float)OcrEngine.MaxImageDimension / stable.Width), token, mixed).ConfigureAwait(false);
                    var stableWord = TextSelection.At(stableLines, cursor, false, null)?.Text;
                    bool TrustedWord(string? text) => text is not null && (text.Length >= 4 && text.All(char.IsAsciiLetter)
                        && spelling.Find(text.ToLowerInvariant()) is not null || text.Length is >= 2 and <= 4 && IsUppercaseWord(text));
                    var hanUnderPointer = stableLines.SelectMany(row => row.Words).Any(word => word.Text.Any(IsHan)
                        && !word.Text.Any(char.IsAsciiLetter) && word.Bounds.Contains(cursor));
                    var englishHyphen = originalLines.SelectMany(row => row.Words).Any(word => word.Text.Contains('-')
                        && word.Text.All(letter => char.IsAsciiLetter(letter) || letter == '-') && word.Bounds.Contains(cursor));
                    if (!TrustedWord(stableWord) && Math.Abs(mixedScale - 1.5f) > .15f && (!hanUnderPointer || englishHyphen))
                    {
                        var adaptive = await RecognizeBitmapAsync(fullLine, lineOrigin,
                            Math.Min(mixedScale, (float)OcrEngine.MaxImageDimension / stable.Width), token, mixed).ConfigureAwait(false);
                        var adaptiveWord = TextSelection.At(adaptive, cursor, false, null)?.Text;
                        if (TrustedWord(adaptiveWord) || stableWord is null
                            || adaptiveWord is not null && adaptiveWord.Length >= stableWord.Length)
                            stableLines = adaptive;
                    }
                    if (HasSeparatedEnglish(stableLines, cursor) || stableLines.Any(line => AppFileName().IsMatch(line.Text)))
                        return await CompleteLineAsync(stableLines).ConfigureAwait(false);
                    if (HasSeparatedEnglish(mixedLines, cursor))
                        return await CompleteLineAsync(mixedLines).ConfigureAwait(false);

                    var extended = Rectangle.Intersect(new Rectangle(Point.Empty, original.Size),
                        new Rectangle(0, local.Y - 30, original.Width, 60));
                    using var extendedLine = wordImage.Clone(extended, PixelFormat.Format32bppArgb);
                    var extendedLines = await RecognizeBitmapAsync(extendedLine, new Point(origin.X, origin.Y + extended.Y),
                        Math.Min(2.2f, (float)OcrEngine.MaxImageDimension / extended.Width), token, mixed).ConfigureAwait(false);
                    if (HasSeparatedEnglish(extendedLines, cursor))
                        return await CompleteLineAsync(extendedLines).ConfigureAwait(false);
                    if (boosted is not null)
                    {
                        var faintLine = Rectangle.Intersect(new Rectangle(Point.Empty, wordImage.Size),
                            new Rectangle(0, local.Y - 15, wordImage.Width, 30));
                        using var focused = wordImage.Clone(faintLine, PixelFormat.Format32bppArgb);
                        var focusedLines = await RecognizeBitmapAsync(focused, new Point(origin.X, origin.Y + faintLine.Y),
                            1.5f, token, mixed).ConfigureAwait(false);
                        if (HasSeparatedEnglish(focusedLines, cursor))
                            return await CompleteLineAsync(focusedLines).ConfigureAwait(false);
                    }
                    var chosen = stableLines.Any(line => ContainsMixedTextAt(line, cursor)) ? stableLines : mixedLines;
                    return await CompleteLineAsync(chosen).ConfigureAwait(false);

                    async Task<ScreenLine[]> CompleteLineAsync(ScreenLine[] rows)
                    {
                        var completed = CompleteMixedWords(rows, originalLines);
                        if (rows.Any(line => AppFileName().IsMatch(line.Text)))
                            return completed;

                        var hyphenated = await ReadMixedHyphenatedSpanAsync(wordImage, origin, cursor, completed, token).ConfigureAwait(false);
                        if (hyphenated is not null)
                            return hyphenated;
                        var span = await ReadMixedLatinSpanAsync(wordImage, origin, cursor, completed, token).ConfigureAwait(false);
                        if (span is not null)
                            return span;
                        if (boosted is not null)
                            return completed;

                        var isolated = await ReadIsolatedMixedWordAsync(wordImage, origin, cursor, completed, token)
                            .ConfigureAwait(false);
                        if (isolated is not null)
                            return isolated;

                        var selected = TextSelection.At(completed, cursor, false, null);
                        var native = await RecognizeBitmapAsync(fullLine, lineOrigin,
                            Math.Min(1f, (float)OcrEngine.MaxImageDimension / stable.Width), token, mixed).ConfigureAwait(false);
                        if (!HasSeparatedEnglish(native, cursor) || TextSelection.At(native, cursor, false, null) is not { } alternate)
                            return completed;
                        if (selected is null || alternate.Text.Length > selected.Text.Length
                            && alternate.Text.StartsWith(selected.Text, StringComparison.OrdinalIgnoreCase)
                            && alternate.Bounds.IntersectsWith(selected.Bounds))
                            return native;
                        return completed;
                    }
                }
            }
            if (LightLetteringCutoff(original, local) is { } desktopCutoff)
            {
                if (await ReadPaleLatinLineAsync(original, origin, local, cursor, token).ConfigureAwait(false) is { } complete)
                {
                    var improved = TextSelection.At(complete, cursor, false, null);
                    if (improved is not null && (originalWord is null
                        || improved.Text.Length >= originalWord.Text.Length
                            && (improved.Text.Contains(originalWord.Text, StringComparison.OrdinalIgnoreCase)
                                || spelling.Find(improved.Text) is not null && spelling.Find(originalWord.Text) is null)))
                        return PreserveWordCase(complete, originalWord);
                }
                using var light = LightTextMask(patch, Math.Max(desktopCutoff, 225), token, spread: 35);
                var lightLines = await RecognizeBitmapAsync(light, patchOrigin, 1f, token).ConfigureAwait(false);
                var lightWord = TextSelection.At(lightLines, cursor, false, null);
                if (lightWord is not null && (originalWord is null
                     || lightWord.Bounds.Contains(cursor) && (!originalWord.Bounds.Contains(cursor)
                         || lightWord.Text.Length > originalWord.Text.Length
                         || lightWord.Text.All(char.IsAscii) && originalWord.Text.Any(letter => !char.IsAscii(letter)))))
                    return lightLines;
                if (originalWord is null)
                {
                    var band = Rectangle.Intersect(new Rectangle(Point.Empty, original.Size),
                        new Rectangle(local.X - 104, local.Y - 32, 208, 64));
                    using var near = original.Clone(band, PixelFormat.Format32bppArgb);
                    using var highContrast = LightTextMask(near, 210, token, spread: 35);
                    var nearLines = await RecognizeBitmapAsync(highContrast, new Point(origin.X + band.X, origin.Y + band.Y),
                        1.5f, token).ConfigureAwait(false);
                    if (TextSelection.At(nearLines, cursor, false, null) is not null)
                        return nearLines;
                }
            }
        }
        if (paragraph && hasTarget is not null && TextSelection.At(originalLines, cursor, true, null) is { } originalParagraph)
        {
            if (HasMixedBrightText(original, local))
            {
                var bright = await ReadBrightTextAsync().ConfigureAwait(false);
                if (TextSelection.At(bright, cursor, true, null) is { } candidate
                    && (TextSelection.RestoresParagraphWords(candidate, originalParagraph, bright)
                        || TextSelection.AgreesOnParagraphWords(candidate, originalParagraph)))
                    return bright;
            }
        }

        if (hasTarget is null || hasTarget(originalLines))
            return originalLines;

        token.ThrowIfCancellationRequested();
        using var enhanced = EnhanceLocalContrast(original, token);
        return await RecognizeBitmapAsync(enhanced, origin, scale, token).ConfigureAwait(false);

        async Task<ScreenLine[]> RefineGameTextAsync(ScreenLine[] filtered, Bitmap readingSource)
        {
            if (mapScene && GenshinMapLabels.At(filtered, cursor) is { } mapLabel)
                return [mapLabel];
            var accurate = await RefineGlyphsAsync(original, filtered).ConfigureAwait(false);
            if (ReferenceEquals(accurate, filtered) && !ReferenceEquals(readingSource, original))
                accurate = await RefineGlyphsAsync(readingSource, filtered).ConfigureAwait(false);
            if (!ReferenceEquals(accurate, filtered))
                return accurate;
            var initial = paragraph ? TextSelection.At(filtered, cursor, true, null) : null;
            if (initial is not null && initial.Text.AsSpan().IndexOfAny('(', ')') >= 0
                && await ReadWhiteChoiceAsync().ConfigureAwait(false) is { } sharperChoice)
                return sharperChoice;
            if (await ReadDialogueOptionAsync(filtered, initial).ConfigureAwait(false) is { } option)
                return option;

            // A single-color mask can erase emphasized words on an otherwise dark text panel.
            if (initial is null || !HasMixedBrightText(original, local))
                return filtered;

            var bright = await ReadBrightTextAsync().ConfigureAwait(false);
            if (TextSelection.At(bright, cursor, true, null) is { } restored
                && TextSelection.RestoresParagraphWords(restored, initial, bright))
                return bright;

            var raw = await RecognizeBitmapAsync(original, origin, scale, token).ConfigureAwait(false);
            return TextSelection.At(raw, cursor, true, null) is { } candidate
                && TextSelection.RestoresParagraphWords(candidate, initial, raw) ? raw : filtered;
        }

        async Task<ScreenLine[]> RefineGlyphsAsync(Bitmap readingSource, ScreenLine[] filtered)
        {
            if (TextSelection.At(filtered, cursor, paragraph, null) is { } first)
            {
                var maximumScale = (float)OcrEngine.MaxImageDimension / Math.Max(readingSource.Width, readingSource.Height);
                var heights = filtered.SelectMany(line => line.Words)
                    .Where(word => word.Bounds.IntersectsWith(first.Bounds) && word.Text.Count(char.IsLetter) >= 2)
                    .Select(word => word.Bounds.Height).Order().ToArray();
                var height = heights.Length == 0 ? first.Bounds.Height : heights[heights.Length / 2];
                foreach (var preferred in new[] { 1f, Math.Clamp(40f / Math.Max(1, height), .35f, 3.5f) }.Distinct())
                {
                    var readingScale = Math.Min(preferred, maximumScale);
                    var native = await RecognizeBitmapAsync(readingSource, origin, readingScale, token).ConfigureAwait(false);
                    if (TextSelection.At(native, cursor, paragraph, null) is { } alternate
                        && (IsAmbiguousGlyphCorrection(first.Text, alternate.Text)
                                && (paragraph ? TextSelection.PreservesParagraph(alternate, first)
                                    : alternate.Bounds.IntersectsWith(first.Bounds))
                            || !paragraph && CompletesClippedWord(first, alternate)))
                    {
                        var confirmation = await RecognizeBitmapAsync(readingSource, origin, Math.Min(readingScale * 1.25f, maximumScale), token)
                            .ConfigureAwait(false);
                        if (TextSelection.At(confirmation, cursor, paragraph, null)?.Text == alternate.Text)
                            return native;
                    }
                }
            }
            return filtered;
        }

        async Task<ScreenLine[]> ReadBrightTextAsync()
        {
            using var mask = ConvertPixels(original, token,
                (red, green, blue) => Luminance(red, green, blue) >= 170 ? (byte)0 : (byte)255);
            var rows = await RecognizeBitmapAsync(mask, origin, scale, token).ConfigureAwait(false);
            return await RefineGlyphsAsync(mask, rows).ConfigureAwait(false);
        }

        async Task<ScreenLine[]?> ReadDialogueOptionAsync(ScreenLine[] lines, Selection? selected = null)
        {
            selected ??= TextSelection.At(lines, cursor, paragraph, null);
            var anchorLine = lines.Where(line => line.Bounds.Left <= cursor.X + line.Bounds.Height * 2
                    && line.Bounds.Right >= cursor.X - line.Bounds.Height * .6f
                    && line.Words.Any(word => word.Text.Length >= 3))
                .MinBy(line => Math.Abs(line.Bounds.Top + line.Bounds.Height / 2 - cursor.Y));
            DialogueOption? located = null;
            if (anchorLine is not null)
            {
                var heights = anchorLine.Words.Where(word => word.Text.Length >= 3)
                    .Select(word => word.Bounds.Height).Order().ToArray();
                var textLeft = Math.Min(anchorLine.Bounds.Left, paragraph ? selected?.Bounds.Left ?? float.MaxValue : float.MaxValue);
                located = FindDialogueOption(original, local, textLeft - origin.X, heights[heights.Length / 2]);
            }
            if (located is null && lines.Any(line => line.Bounds.Height >= 7
                    && (paragraph || line.Bounds.Height < 17 && RectangleF.Inflate(line.Bounds, 6, 6).Contains(cursor)))
                && SampleLuminance(original, local).Skip(235).Sum() >= 10)
                located = FindDialogueOption(original, local);
            if (located is not { } area)
                return null;

            if (area.Bounds.Width == 0 || local.X < area.Bounds.Left)
                return [];
            var rows = await ReadOptionTextAsync(original, origin, area, token).ConfigureAwait(false);
            if (TextSelection.At(rows, cursor, paragraph, null) is not { } choice)
                return null;

            RectangleF optionBounds = area.Bounds;
            optionBounds.Offset(origin);
            return paragraph && selected is not null && optionBounds.Contains(selected.Bounds)
                && TextSelection.PreservesParagraph(selected, choice)
                && selected.Text.Count(char.IsLetter) > choice.Text.Count(char.IsLetter) ? lines : rows;
        }

        async Task<ScreenLine[]?> ReadWhiteChoiceAsync()
        {
            if (!HasNearWhiteTextOnDarkBackground(SampleLuminance(original, local)))
                return null;

            const int whiteCutoff = 245;
            using var whiteText = LightTextMask(original, whiteCutoff, token);
            var rows = await RecognizeBitmapAsync(whiteText, origin, scale, token).ConfigureAwait(false);
            // Exclude the dimmer border only when the alternative contains one complete choice.
            return TextSelection.At(rows, cursor, true, null)?.Text is { Length: > 2 } text
                && text[0] == '(' && text[^1] == ')' && text.AsSpan(1, text.Length - 2).IndexOfAny('(', ')') < 0
                ? rows : null;
        }
    }

    private static bool IsUppercaseWord(string text) => text.Count(char.IsAsciiLetterUpper) >= 2
        && text.All(letter => char.IsAsciiLetterUpper(letter) || char.IsAsciiDigit(letter));

    private static bool IsAmbiguousGlyphCorrection(string first, string second)
    {
        if (first.Length != second.Length)
            return false;
        var changes = 0;
        for (var index = 0; index < first.Length; index++)
        {
            var before = char.ToLowerInvariant(first[index]);
            var after = char.ToLowerInvariant(second[index]);
            if (before == after)
                continue;
            if (++changes > 2 || (before, after) is not (('l', 't') or ('t', 'l') or ('i', 'l') or ('l', 'i')
                    or ('.', ',') or (',', '.')))
                return false;
        }
        return changes != 0 || first != second && UnstableLetterCase().IsMatch(first)
            && !UnstableLetterCase().IsMatch(second);
    }

    [GeneratedRegex(@"\b(?:[A-Z]{2,}[a-z]{2,}|[A-Z]?[a-z]{3,}[A-Z])\b", RegexOptions.CultureInvariant)]
    private static partial Regex UnstableLetterCase();

    internal static bool HasUnstableLetterCase(string text) => UnstableLetterCase().IsMatch(text);

    internal static bool IsSingleGlyphVariant(string first, string second) => first.Length == second.Length
        && first.Zip(second).Count(pair => char.ToLowerInvariant(pair.First) != char.ToLowerInvariant(pair.Second)) <= 1;

    private static bool CompletesClippedWord(Selection first, Selection candidate)
    {
        if (first.Text.Length < 2 || candidate.Text.Length <= first.Text.Length
            || candidate.Text.Length > first.Text.Length * 2 + 2
            || !TextSelection.IncludesParagraph(candidate.Bounds, first.Bounds))
            return false;
        var index = 0;
        foreach (var letter in candidate.Text)
            if (char.ToLowerInvariant(letter) == char.ToLowerInvariant(first.Text[index]) && ++index == first.Text.Length)
                return true;
        return false;
    }

    private async Task<ScreenLine[]> ReadOptionTextAsync(Bitmap original, Point origin,
        DialogueOption option, CancellationToken token)
    {
        var area = option.Bounds;
        using var image = original.Clone(area, PixelFormat.Format32bppArgb);
        var small = option.IconWidth < 24;
        // Preserve partially covered pixels when the small letters have few fully white strokes.
        using var mask = small ? ConvertPixels(image, token, (red, green, blue) =>
            (byte)(255 - Math.Max(0, Math.Min(red, Math.Min(green, blue)) - 176) * 2))
            : LightTextMask(image, option.IconWidth < 48 ? 235 : 245, token);
        var preferredScale = small ? Math.Min(4.5f, 74f / option.IconWidth) : Math.Min(1f, 72f / option.IconWidth);
        var scale = Math.Min(preferredScale,
            (float)OcrEngine.MaxImageDimension / Math.Max(area.Width, area.Height));
        var start = new Point(origin.X + area.X, origin.Y + area.Y);
        var rows = await RecognizeBitmapAsync(mask, start, scale, token).ConfigureAwait(false);
        var raw = await RecognizeBitmapAsync(image, start, scale, token).ConfigureAwait(false);
        var filteredText = string.Join(' ', rows.Select(line => line.Text));
        var rawText = string.Join(' ', raw.Select(line => line.Text));
        if (IsAmbiguousGlyphCorrection(filteredText, rawText))
        {
            var confirmed = await RecognizeBitmapAsync(image, start, Math.Min(scale * 1.25f,
                (float)OcrEngine.MaxImageDimension / Math.Max(area.Width, area.Height)), token).ConfigureAwait(false);
            if (string.Join(' ', confirmed.Select(line => line.Text)) == rawText)
                return raw;
        }
        return rows;
    }

    private readonly record struct DialogueOption(Rectangle Bounds, int IconWidth);

    private static unsafe DialogueOption? FindDialogueOption(Bitmap bitmap, Point cursor, float? textLeft = null, float letterHeight = 0)
    {
        // The speech icon has three dark dots bounded by a solid white bubble.
        var left = textLeft is { } start ? Math.Max(0, (int)(start - letterHeight * 3)) : 0;
        var right = textLeft is { } edge ? Math.Min(bitmap.Width, (int)(edge + letterHeight * 2))
            : Math.Min(bitmap.Width, cursor.X + 180);
        var radius = textLeft.HasValue ? (int)(letterHeight * 2) : Math.Min(180, bitmap.Height / 2);
        var minWidth = textLeft.HasValue ? letterHeight : 14;
        var maxWidth = textLeft.HasValue ? letterHeight * 2.9f : 180;
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var distance = 0; distance <= radius; distance++)
            {
                if (TryRow(cursor.Y - distance) is { } above)
                    return CenterOnDots(cursor.Y - distance, above);
                if (distance != 0 && TryRow(cursor.Y + distance) is { } below)
                    return CenterOnDots(cursor.Y + distance, below);
            }
        }
        finally { bitmap.UnlockBits(data); }
        return null;

        DialogueOption CenterOnDots(int row, DialogueOption found)
        {
            var first = row;
            var last = row;
            while (Math.Abs(first - row) < found.IconWidth / 5 && SameIcon(first - 1))
                first--;
            while (Math.Abs(last - row) < found.IconWidth / 5 && SameIcon(last + 1))
                last++;
            return TryRow((first + last) / 2) ?? found;

            bool SameIcon(int nearby) => TryRow(nearby) is { } dot
                && Math.Abs(dot.Bounds.Left - found.Bounds.Left) <= found.IconWidth / 8;
        }

        DialogueOption? TryRow(int y)
        {
            if (y < 0 || y >= bitmap.Height)
                return null;
            Span<int> lengths = stackalloc int[7];
            for (var x = left; x < right;)
            {
                if (!IsWhite(x, y))
                {
                    x++;
                    continue;
                }
                var end = x;
                for (var run = 0; run < lengths.Length; run++)
                {
                    var start = end;
                    var white = run % 2 == 0;
                    while (end < right && IsWhite(end, y) == white)
                        end++;
                    lengths[run] = end - start;
                }
                var width = end - x;
                if (end < right && cursor.X >= x - width / 8 && Math.Abs(cursor.Y - y) < width
                    && width >= minWidth && width <= maxWidth
                    && lengths[0] >= width * .12f && lengths[6] >= width * .12f
                    && IsDot(lengths[1], width) && IsDot(lengths[3], width) && IsDot(lengths[5], width)
                    && lengths[2] >= width * .07f && lengths[4] >= width * .07f
                    && IsCenterSolid(x, y - width / 5, width) && IsCenterSolid(x, y + width / 5, width)
                    && IsOutsideBubble(x, y - width * 3 / 5, width) && IsOutsideBubble(x, y + width * 3 / 5, width))
                {
                    var startOfText = end + width / 9;
                    var area = Rectangle.FromLTRB(Math.Min(startOfText, bitmap.Width), Math.Max(0, y - width),
                        bitmap.Width, Math.Min(bitmap.Height, y + width));
                    return new DialogueOption(area, width);
                }
                x += lengths[0];
            }
            return null;
        }

        bool IsCenterSolid(int x, int y, int width)
        {
            if (y < 0 || y >= bitmap.Height)
                return false;
            for (var column = x + width / 4; column <= x + width * 3 / 4; column += 2)
                if (!IsWhite(column, y))
                    return false;
            return true;
        }

        bool IsOutsideBubble(int x, int y, int width) => y >= 0 && y < bitmap.Height
            && !IsWhite(x + width / 4, y) && !IsWhite(x + width / 2, y) && !IsWhite(x + width * 3 / 4, y);

        bool IsWhite(int x, int y)
        {
            var pixel = (byte*)data.Scan0 + y * data.Stride + x * 4;
            return IsPale(pixel[2], pixel[1], pixel[0], 230);
        }

        static bool IsDot(int run, int width) => run >= width * .09f && run <= width * .22f;
    }

    private static Rectangle? FindDisplayLine(Bitmap mask, Point cursor, int maxHeight = 64, float gapRatio = 1, int minHeight = 8)
    {
        // Use the ink bands of small high-contrast labels to separate them from nearby controls.
        var left = Math.Max(0, cursor.X - 32);
        var right = Math.Min(mask.Width, cursor.X + 33);
        var topLimit = Math.Max(0, cursor.Y - maxHeight + 2);
        var bottomLimit = Math.Min(mask.Height, cursor.Y + maxHeight - 1);
        var ink = new bool[bottomLimit - topLimit];
        for (var y = topLimit; y < bottomLimit; y++)
        {
            var count = 0;
            for (var x = left; x < right && count < 2; x++)
                if (mask.GetPixel(x, y).R != 0)
                    count++;
            ink[y - topLimit] = count == 2;
        }

        var anchor = Enumerable.Range(topLimit, bottomLimit - topLimit).Where(y => ink[y - topLimit])
            .OrderBy(y => Math.Abs(y - cursor.Y)).FirstOrDefault(-1);
        if (anchor < 0 || Math.Abs(anchor - cursor.Y) > 5)
            return null;
        var top = anchor;
        var bottom = anchor + 1;
        while (top > topLimit && ink[top - 1 - topLimit])
            top--;
        while (bottom < bottomLimit && ink[bottom - topLimit])
            bottom++;
        var height = bottom - top;
        if (height < minHeight || height > maxHeight)
            return null;

        var columns = new List<int>();
        for (var x = 0; x < mask.Width; x++)
        {
            for (var y = top; y < bottom; y++)
            {
                if (mask.GetPixel(x, y).R != 0)
                {
                    columns.Add(x);
                    break;
                }
            }
        }
        var anchorIndex = Enumerable.Range(0, columns.Count).MinBy(index => Math.Abs(columns[index] - cursor.X));
        if (Math.Abs(columns[anchorIndex] - cursor.X) > 6)
            return null;
        var start = anchorIndex;
        var end = anchorIndex;
        var maxGap = Math.Max(3, height * gapRatio);
        while (start > 0 && columns[start] - columns[start - 1] <= maxGap)
            start--;
        while (end + 1 < columns.Count && columns[end + 1] - columns[end] <= maxGap)
            end++;
        return Rectangle.Intersect(new Rectangle(Point.Empty, mask.Size),
            Rectangle.FromLTRB(columns[start] - 3, top - 2, columns[end] + 4, bottom + 2));
    }

    private async Task<ScreenLine[]?> ReadCondensedLineAsync(Bitmap original, Rectangle line, Point origin, Point cursor,
        CancellationToken token)
    {
        // Narrow raster fonts need both stroke and aspect normalization. Compare independent
        // masks and sizes instead of accepting the first plausible uppercase fragment.
        var region = Rectangle.Intersect(new Rectangle(Point.Empty, original.Size),
            Rectangle.Inflate(line, Math.Max(1, line.Height / 11), Math.Max(2, line.Height / 8)));
        using var raw = original.Clone(region, PixelFormat.Format32bppArgb);
        var readings = new Dictionary<string, (int Count, ScreenLine[] Rows)>(StringComparer.Ordinal);
        foreach (var cutoff in new[] { 225, 240, 245, 250, 253 })
        {
            using var mask = LightTextMask(raw, cutoff, token, whiteOnBlack: true);
            foreach (var height in new[] { 24, 32, 40, 48 })
                foreach (var aspect in new[] { 1f, 1.5f, 2f, 2.5f, 3f, 3.5f })
                {
                    var width = Math.Max(1, (int)Math.Round(mask.Width * (double)height / mask.Height * aspect));
                    if (width > 1600)
                        continue;
                    const int left = 6;
                    const int top = 12;
                    using var normalized = new Bitmap(width + left * 2, height + top * 2, PixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(normalized))
                    {
                        graphics.Clear(Color.Black);
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(mask, new Rectangle(left, top, width, height));
                    }
                    var rows = await RecognizeBitmapAsync(normalized, Point.Empty, 1f, token).ConfigureAwait(false);
                    var horizontalScale = (float)region.Width / width;
                    var verticalScale = (float)region.Height / height;
                    RectangleF ToScreen(RectangleF bounds) => new(
                        origin.X + region.X + (bounds.X - left) * horizontalScale,
                        origin.Y + region.Y + (bounds.Y - top) * verticalScale,
                        bounds.Width * horizontalScale, bounds.Height * verticalScale);
                    var projected = rows.Select(row => new ScreenLine(
                        row.Words.Select(word => word with { Bounds = ToScreen(word.Bounds) }).ToArray(), ToScreen(row.Bounds))).ToArray();
                    if (TextSelection.At(projected, cursor, false, null) is not { } hit || !IsUppercaseWord(hit.Text)
                        || !RectangleF.Inflate(hit.Bounds, 2, 2).Contains(cursor))
                        continue;
                    var previous = readings.GetValueOrDefault(hit.Text);
                    readings[hit.Text] = (previous.Count + 1, projected);
                }
        }
        var best = readings.OrderByDescending(reading => reading.Value.Count).FirstOrDefault();
        if (best.Value.Count < 2)
            return null;
        // Use the existing English dictionary to resolve similarly supported raster readings,
        // while retaining consensus for acronyms and unfamiliar names.
        var known = readings.Where(reading => reading.Value.Count >= 2 && reading.Key.Length >= 4
                && reading.Key.Length == best.Key.Length && spelling.Find(reading.Key.ToLowerInvariant()) is not null)
            .OrderByDescending(reading => reading.Value.Count * (spelling.FrequencyRank(reading.Key) is > 0 and <= 50000 ? 3 : 1))
            .FirstOrDefault();
        return known.Value.Count >= 2 ? known.Value.Rows : best.Value.Rows;
    }

    private static bool HasBrightTextOnBlack(Bitmap bitmap, Point cursor, int[] samples)
    {
        var total = samples.Sum();
        var light = samples.Skip(231).Sum();
        if (total == 0 || light < 6)
            return false;
        if (samples.Take(24).Sum() * 100 >= total * 50)
            return true;
        if (light * 100 < total * 7)
            return false;

        // When a pointer is over the edge of a large label, the glyphs dominate the
        // small sample. Check a little farther out for the dark canvas.
        var surrounding = SampleLuminance(bitmap, cursor, 64, 60);
        return surrounding.Take(43).Sum() * 100 >= surrounding.Sum() * 50;
    }

    private static Bitmap? BoostFaintText(Bitmap original, int[] samples, CancellationToken token,
        bool compressedHighlights = false, bool sparseInk = false, (int Gain, int Margin)? transfer = null)
    {
        var total = samples.Sum();
        var brightestBand = compressedHighlights ? 210 : 230;
        var minimumContrast = compressedHighlights ? 6 : 16;
        for (var value = brightestBand + 1; value < samples.Length; value++)
            if (samples[value] > samples[brightestBand])
                brightestBand = value;
        if (compressedHighlights && !sparseInk && brightestBand > 235)
            return null;
        var background = 0;
        var mutedInk = 0;
        var darkerInk = 0;
        for (var value = 0; value < samples.Length; value++)
        {
            if (Math.Abs(brightestBand - value) <= 4)
                background += samples[value];
            else if (value < brightestBand - 80)
                darkerInk += samples[value];
            else if (value <= brightestBand - minimumContrast)
                mutedInk += samples[value];
        }
        if (total == 0 || background * 100 < total * 58 || mutedInk * 100 < total * (sparseInk ? 2 : 5)
            || darkerInk * 100 > total * 2)
            return null;

        // Tone mapping can bring both the panel and its lettering into the same narrow highlight band.
        var (gain, backgroundMargin) = transfer ?? (sparseInk ? (12, 1) : compressedHighlights ? (42, 1) : (8, 2));
        return ConvertPixels(original, token, (red, green, blue) =>
            (byte)Math.Clamp(255 - (brightestBand - backgroundMargin - Luminance(red, green, blue)) * gain, 0, 255));
    }

    private static int InternalCapitalI(string word)
    {
        for (var i = 1; i < word.Length - 1; i++)
            if (word[i] == 'I' && char.IsAsciiLetterUpper(word[i - 1]) && char.IsAsciiLetterLower(word[i + 1]))
                return i;
        return -1;
    }

    private static bool ExtendsFaintCompound(ScreenLine[] initial, ScreenLine[] candidate)
    {
        if (initial.Length != 1 || candidate.Length != 1)
            return false;
        var first = initial[0];
        var extended = candidate[0];
        if (extended.Bounds.Right - first.Bounds.Right < first.Bounds.Height * .5f
            || Math.Abs(first.Bounds.Left - extended.Bounds.Left) > first.Bounds.Height * .25f)
            return false;
        var prefix = first.Text.Replace(" ", "", StringComparison.Ordinal);
        var text = extended.Text.Replace(" ", "", StringComparison.Ordinal);
        return text.Length > prefix.Length && (text.StartsWith(prefix, StringComparison.Ordinal)
            || prefix.EndsWith('I') && text.StartsWith(prefix[..^1] + 'l', StringComparison.Ordinal));
    }

    private static int[] SampleLuminance(Bitmap bitmap, Point cursor, int halfWidth = 27, int halfHeight = 19)
    {
        var histogram = new int[256];
        for (var y = Math.Max(0, cursor.Y - halfHeight); y < Math.Min(bitmap.Height, cursor.Y + halfHeight + 1); y += 2)
            for (var x = Math.Max(0, cursor.X - halfWidth); x < Math.Min(bitmap.Width, cursor.X + halfWidth + 1); x += 2)
            {
                var color = bitmap.GetPixel(x, y);
                histogram[Luminance(color.R, color.G, color.B)]++;
            }
        return histogram;
    }

    private async Task<ScreenLine[]?> ReadFaintCompoundAsync(Bitmap image, Point origin, Point cursor,
        OcrEngine bilingual, CancellationToken token, bool smallText = false, Func<string, bool>? acceptWord = null)
    {
        var y = cursor.Y - origin.Y;
        var x = cursor.X - origin.X;
        if ((uint)x >= (uint)image.Width || (uint)y >= (uint)image.Height)
            return null;
        var top = Math.Max(0, y - 46);
        var bottom = Math.Min(image.Height, y + 47);
        var leftEdge = smallText ? Math.Max(0, x - 88) : 0;
        var rightEdge = smallText ? Math.Min(image.Width, x + 89) : image.Width;
        var inkStep = smallText ? 1 : 2;
        var threshold = smallText ? 216 : 150;
        var ink = new bool[bottom - top];
        for (var row = top; row < bottom; row++)
            for (var column = leftEdge; column < rightEdge; column += inkStep)
            {
                if (image.GetPixel(column, row).R >= threshold)
                    continue;
                ink[row - top] = true;
                break;
            }
        if (!ink[y - top])
            return null;
        var upper = y;
        var lower = y + 1;
        while (upper > top && ink[upper - 1 - top])
            upper--;
        while (lower < bottom && ink[lower - top])
            lower++;
        var height = lower - upper;
        if (height < (smallText ? 3 : 8) || height > 68)
            return null;

        var inkLeft = 0;
        var inkRight = image.Width - 1;
        while (inkLeft < inkRight && !ColumnHasInk(inkLeft))
            inkLeft++;
        while (inkRight > inkLeft && !ColumnHasInk(inkRight))
            inkRight--;
        var rowArea = Rectangle.FromLTRB(Math.Max(0, inkLeft - 6), Math.Max(0, upper - 4),
            Math.Min(image.Width, inkRight + 7), Math.Min(image.Height, lower + 4));
        using var strip = image.Clone(rowArea, PixelFormat.Format32bppArgb);
        var rowOrigin = new Point(origin.X + rowArea.X, origin.Y + rowArea.Y);
        var maxScale = (float)OcrEngine.MaxImageDimension / strip.Width;
        var readingHeights = smallText ? new[] { 28f, 34f, 40f, 48f } : new[] { 40f, 36f, 45f };
        foreach (var readingHeight in readingHeights)
        {
            var scale = Math.Min(readingHeight / height, maxScale);
            var rows = await RecognizeBitmapAsync(strip, rowOrigin, scale, token, bilingual).ConfigureAwait(false);
            var han = rows.SelectMany(line => line.Words).Where(word => word.Text.Any(IsHan))
                .OrderBy(word => word.Bounds.Left).ToArray();
            var left = han.Where(word => word.Bounds.Right <= cursor.X + 2).Reverse().Take(2);
            var right = han.Where(word => word.Bounds.Left >= cursor.X - 2).Take(2).ToArray();
            foreach (var before in left)
                foreach (var after in right)
                {
                    var first = BlankColumn(before.Bounds.Right - rowOrigin.X, fromRight: true);
                    var last = BlankColumn(after.Bounds.Left - rowOrigin.X, fromRight: false);
                    if (last - first < height * 3 || last - first > height * 16)
                        continue;

                    // The English pass sees only the span between the nearby Han characters.
                    const int padding = 6;
                    using var latin = new Bitmap(last - first + 2 * padding, strip.Height, PixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(latin))
                    {
                        graphics.Clear(Color.White);
                        graphics.DrawImage(strip, new Rectangle(padding, 0, last - first, strip.Height),
                            first, 0, last - first, strip.Height, GraphicsUnit.Pixel);
                    }
                    var found = await RecognizeBitmapAsync(latin, new Point(rowOrigin.X + first - padding, rowOrigin.Y), scale, token)
                        .ConfigureAwait(false);
                    var text = string.Concat(found.SelectMany(line => line.Words).Select(word => word.Text));
                    if (SlashSeparatedWords().IsMatch(text) && TextSelection.At(found, cursor, false, null) is { } hit
                        && (acceptWord is null || acceptWord(hit.Text)))
                        return found;
                }
        }
        return null;

        bool ColumnHasInk(int column)
        {
            for (var row = upper; row < lower; row++)
                if (image.GetPixel(column, row).R < threshold)
                    return true;
            return false;
        }

        int BlankColumn(float edge, bool fromRight)
        {
            var center = Math.Clamp((int)MathF.Round(edge), 0, strip.Width - 1);
            var radius = Math.Max(2, height / 9);
            var best = center;
            var leastInk = int.MaxValue;
            var start = Math.Max(0, center - radius);
            var end = Math.Min(strip.Width - 1, center + radius);
            for (var i = 0; i <= end - start; i++)
            {
                var column = fromRight ? end - i : start + i;
                var count = 0;
                for (var row = 0; row < strip.Height; row++)
                    if (strip.GetPixel(column, row).R < threshold)
                        count++;
                if (count < leastInk || count == leastInk && Math.Abs(column - center) < Math.Abs(best - center) - 1)
                {
                    best = column;
                    leastInk = count;
                }
            }
            return best;
        }
    }

    private static int Luminance(int red, int green, int blue) => (77 * red + 150 * green + 29 * blue + 128) >> 8;

    private static bool HasDarkTextOnLightBackground(int[] luminance)
    {
        var samples = luminance.Sum();
        var light = luminance.Skip(190).Sum();
        var dark = luminance.Take(135).Sum();
        return light * 3 >= samples * 2 && dark * 10 >= samples;
    }

    private static bool HasNearWhiteTextOnDarkBackground(int[] luminance)
    {
        var samples = luminance.Sum();
        return luminance.Skip(245).Sum() * 100 >= samples * 6 && luminance.Take(150).Sum() * 4 >= samples;
    }

    internal static bool IsHan(char letter) => letter is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF'
        or >= '\uF900' and <= '\uFAFF';

    private static bool ContainsMixedTextAt(ScreenLine line, Point cursor) => line.Text.Any(IsHan)
        && cursor.Y >= line.Bounds.Top - 8 && cursor.Y <= line.Bounds.Bottom + 8
        && cursor.X >= line.Bounds.Left - 8 && cursor.X <= line.Bounds.Right + 8;

    private static bool HasSeparatedEnglish(ScreenLine[] lines, Point cursor)
    {
        foreach (var line in lines.Where(line => ContainsMixedTextAt(line, cursor)))
        {
            var words = line.Words;
            for (var index = 1; index + 1 < words.Length; index++)
            {
                var word = words[index];
                if (word.Bounds.Contains(cursor) && word.Text.Any(char.IsAsciiLetter)
                    && words[index - 1].Text.Any(IsHan) && words[index + 1].Text.Any(IsHan))
                    return true;
            }
        }
        return false;
    }

    private async Task<ScreenLine[]?> ReadIsolatedMixedWordAsync(Bitmap image, Point origin, Point cursor,
        ScreenLine[] lines, CancellationToken token)
    {
        foreach (var line in lines.Where(line => ContainsMixedTextAt(line, cursor)))
        {
            var words = line.Words;
            for (var index = 1; index + 1 < words.Length; index++)
            {
                var word = words[index];
                var left = words[index - 1];
                var next = words[index + 1];
                if (word.Text.Length < 3 || !word.Text.All(char.IsAsciiLetter) || !left.Text.Any(IsHan))
                    continue;

                var maximumGap = Math.Max(2, word.Bounds.Height * .22f);
                var cross = next.Text == "×" && index + 2 < words.Length
                    && next.Bounds.Left - word.Bounds.Right <= maximumGap;
                var right = words[index + (cross ? 2 : 1)];
                if (!right.Text.Any(IsHan) || (!cross && (word.Bounds.Left - left.Bounds.Right < maximumGap
                    || right.Bounds.Left - word.Bounds.Right < maximumGap)))
                    continue;

                var bounds = cross ? RectangleF.Union(word.Bounds, next.Bounds) : word.Bounds;
                if (!bounds.Contains(cursor))
                    continue;
                var area = Rectangle.FromLTRB((int)Math.Floor(bounds.Left) - origin.X, (int)Math.Floor(bounds.Top) - origin.Y - 2,
                    (int)Math.Ceiling(bounds.Right) - origin.X, (int)Math.Ceiling(bounds.Bottom) - origin.Y + 2);
                if (!new Rectangle(Point.Empty, image.Size).Contains(area))
                    continue;

                const int margin = 8;
                using var padded = new Bitmap(area.Width + margin * 2, area.Height + margin * 2, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(padded))
                {
                    var background = image.GetPixel(area.Left, area.Top);
                    graphics.Clear(background);
                    graphics.DrawImage(image, new Rectangle(margin, margin, area.Width, area.Height), area, GraphicsUnit.Pixel);
                }
                var start = new Point(origin.X + area.X - margin, origin.Y + area.Y - margin);
                var normalizedScale = Math.Clamp(29f / bounds.Height, .8f, 2.5f);
                var scales = cross && normalizedScale < 1f ? new[] { normalizedScale, 1f } : [normalizedScale];
                foreach (var scale in scales)
                {
                    var result = await RecognizeBitmapAsync(padded, start, scale, token).ConfigureAwait(false);
                    if (TextSelection.At(result, cursor, false, null) is not { } revised || !revised.Text.All(char.IsAsciiLetter)
                        || (cross ? revised.Text.Length != word.Text.Length + 1
                            || !revised.Text.StartsWith(word.Text, StringComparison.OrdinalIgnoreCase)
                            : revised.Text.Length != word.Text.Length))
                        continue;

                    var updated = words.Take(index).Append(new ScreenWord(revised.Text, bounds))
                        .Concat(words.Skip(index + (cross ? 2 : 1))).ToArray();
                    return lines.Select(row => row == line ? line with { Words = updated } : row).ToArray();
                }
            }
        }
        return null;
    }

    private async Task<ScreenLine[]?> ReadMixedHyphenatedSpanAsync(Bitmap image, Point origin, Point cursor,
        ScreenLine[] lines, CancellationToken token)
    {
        var splitRun = ReadMixedLatinBridge(image, origin, cursor, lines, token);
        if (splitRun is not null)
            return splitRun;
        foreach (var line in lines.Where(line => ContainsMixedTextAt(line, cursor)))
            for (var mark = 1; mark + 1 < line.Words.Length; mark++)
            {
                var dash = line.Words[mark];
                if (dash.Text.Any(char.IsLetterOrDigit) || dash.Bounds.Height > line.Bounds.Height * .35f
                    || dash.Bounds.Width < dash.Bounds.Height * 1.5f)
                    continue;
                var left = Enumerable.Range(Math.Max(0, mark - 2), Math.Min(2, mark))
                    .Where(index => line.Words[index].Text.All(char.IsAsciiLetter)).LastOrDefault(-1);
                var right = Enumerable.Range(mark + 1, Math.Min(2, line.Words.Length - mark - 1))
                    .Where(index => line.Words[index].Text.All(char.IsAsciiLetter)).FirstOrDefault(-1);
                if (left < 0 || right < 0 || line.Words[left].Text.Length + line.Words[right].Text.Length < 3)
                    continue;
                var bounds = line.Words.Skip(left).Take(right - left + 1).Select(word => word.Bounds).Aggregate(RectangleF.Union);
                if (!RectangleF.Inflate(bounds, 2, 2).Contains(cursor))
                    continue;
                var area = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size), Rectangle.FromLTRB(
                    (int)MathF.Floor(bounds.Left) - origin.X, (int)MathF.Floor(bounds.Top) - origin.Y - 2,
                    (int)MathF.Ceiling(bounds.Right) - origin.X, (int)MathF.Ceiling(bounds.Bottom) - origin.Y + 2));
                if (area.Width <= 0 || area.Height <= 0)
                    continue;
                const int margin = 8;
                using var strip = new Bitmap(area.Width + margin * 2, area.Height + margin * 2, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(strip))
                {
                    graphics.Clear(image.GetPixel(area.Left, area.Top));
                    graphics.DrawImage(image, new Rectangle(margin, margin, area.Width, area.Height), area, GraphicsUnit.Pixel);
                }
                var start = new Point(origin.X + area.X - margin, origin.Y + area.Y - margin);
                var votes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var height in new[] { 32f, 40f, 48f })
                {
                    var scale = Math.Min(height / Math.Max(3, bounds.Height),
                        (float)OcrEngine.MaxImageDimension / Math.Max(strip.Width, strip.Height));
                    var found = await RecognizeBitmapAsync(strip, start, scale, token).ConfigureAwait(false);
                    var text = string.Concat(found.SelectMany(row => row.Words).Select(word => word.Text));
                    if (!HyphenatedWord().IsMatch(text)
                        || !text.StartsWith(line.Words[left].Text, StringComparison.OrdinalIgnoreCase)
                        || !text.EndsWith(line.Words[right].Text, StringComparison.OrdinalIgnoreCase) || votes.Add(text))
                        continue;
                    var updated = line.Words.Take(left).Append(new ScreenWord(text, bounds)).Concat(line.Words.Skip(right + 1)).ToArray();
                    return lines.Select(row => row == line ? line with { Words = updated } : row).ToArray();
                }
            }
        return null;
    }

    private async Task<ScreenLine[]?> ReadMixedConsensusAsync(Bitmap image, Point origin, Point cursor,
        ScreenLine[] lines, CancellationToken token)
    {
        var current = TextSelection.At(lines, cursor, false, null);
        if (lines.Any(row => AppFileName().IsMatch(row.Text)))
            return null;
        var line = lines.Where(row => ContainsMixedTextAt(row, cursor)).MinBy(row => Math.Abs(row.Bounds.Top - cursor.Y));
        if (line is null || mixedLanguageEngine.Value is not { } mixed)
            return null;
        var hasDash = line.Words.Any(word => word.Text.Contains('-') && RectangleF.Inflate(word.Bounds, 80, 3).Contains(cursor));
        if (!hasDash && current is not null && (current.Text.Length >= 4
                && spelling.FrequencyRank(current.Text) is > 0 and <= 50000
            || current.Text.Length is >= 2 and <= 4 && IsUppercaseWord(current.Text)))
            return null;
        var area = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size), Rectangle.FromLTRB(0,
            (int)line.Bounds.Top - origin.Y - 6, image.Width, (int)Math.Ceiling(line.Bounds.Bottom) - origin.Y + 6));
        if (area.Width == 0 || area.Height == 0)
            return null;
        using var crop = image.Clone(area, PixelFormat.Format32bppArgb);
        using var mask = LightTextMask(crop, 145, token);
        var start = new Point(origin.X, origin.Y + area.Y);
        var candidates = new List<(string Text, ScreenLine[] Rows)>();
        var hanReads = new List<ScreenLine[]>();
        foreach (var source in new[] { crop, mask })
            foreach (var targetHeight in new[] { 24f, 32f, 40f })
            {
                var scale = Math.Min(targetHeight / Math.Max(5, line.Bounds.Height),
                    (float)OcrEngine.MaxImageDimension / Math.Max(source.Width, source.Height));
                var read = await RecognizeBitmapAsync(source, start, scale, token, mixed).ConfigureAwait(false);
                if (current is { Text.Length: >= 5 } && current.Text.All(char.IsAsciiLetter)
                    && spelling.Find(current.Text) is null
                    && read.SelectMany(row => row.Words).Any(word => word.Text.Any(IsHan)
                        && !word.Text.Any(char.IsAsciiLetter) && word.Bounds.Contains(cursor))
                    && read.SelectMany(row => row.Words).Any(word => word.Text.Length >= 4
                        && current.Text.EndsWith(word.Text, StringComparison.OrdinalIgnoreCase)
                        && spelling.FrequencyRank(word.Text) > 0))
                    hanReads.Add(read);
                var completed = await ReadMixedHyphenatedSpanAsync(image, origin, cursor, read, token).ConfigureAwait(false)
                    ?? await ReadMixedLatinSpanAsync(image, origin, cursor, read, token).ConfigureAwait(false) ?? read;
                if (TextSelection.At(completed, cursor, false, null) is { } hit
                    && hit.Text.All(letter => char.IsAsciiLetter(letter) || letter is '-' or '\'')
                    && (HyphenatedWord().IsMatch(hit.Text) || spelling.FrequencyRank(hit.Text) > 0))
                    candidates.Add((hit.Text, completed));
            }
        var best = candidates.GroupBy(candidate => candidate.Text, StringComparer.Ordinal)
            .Where(group => group.Count() >= 2).OrderByDescending(group => group.Key.Contains('-'))
            .ThenByDescending(group => group.Count()).ThenByDescending(group => group.Key.Length).FirstOrDefault();
        if (best is not null && best.Key != current?.Text)
            return PreserveWordCase(best.First().Rows, current);
        return hanReads.Count >= 2 ? hanReads[0] : null;
    }

    private async Task<ScreenLine[]?> ReadMixedLatinSpanAsync(Bitmap image, Point origin, Point cursor,
        ScreenLine[] lines, CancellationToken token)
    {
        foreach (var line in lines.Where(line => ContainsMixedTextAt(line, cursor)))
        {
            var han = line.Words.Where(word => word.Text.Any(IsHan)).ToArray();
            if (han.Any(word => word.Bounds.Contains(cursor)))
                continue;
            var before = han.Where(word => word.Bounds.Right <= cursor.X + 1).MaxBy(word => word.Bounds.Right);
            var after = han.Where(word => word.Bounds.Left >= cursor.X - 1).MinBy(word => word.Bounds.Left);
            if (before is null && after is null)
                continue;
            var height = line.Bounds.Height;
            var left = before?.Bounds.Right ?? line.Bounds.Left;
            var right = after?.Bounds.Left ?? Math.Max(line.Bounds.Right + height, cursor.X + height * 8);
            if (cursor.X < left - .75f || cursor.X > right + .75f || right - left < height * .6f)
                continue;
            var area = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size), Rectangle.FromLTRB(
                (int)MathF.Ceiling(left) - origin.X, (int)MathF.Floor(line.Bounds.Top) - origin.Y - 3,
                (int)MathF.Floor(right) - origin.X, (int)MathF.Ceiling(line.Bounds.Bottom) - origin.Y + 3));
            if (area.Width <= 0 || area.Height <= 0)
                continue;
            const int margin = 8;
            using var strip = new Bitmap(area.Width + margin * 2, area.Height + margin * 2, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(strip))
            {
                graphics.Clear(image.GetPixel(area.Left, area.Top));
                graphics.DrawImage(image, new Rectangle(margin, margin, area.Width, area.Height), area, GraphicsUnit.Pixel);
            }
            var start = new Point(origin.X + area.X - margin, origin.Y + area.Y - margin);
            var readings = new Dictionary<string, (int Count, ScreenLine[] Rows)>(StringComparer.Ordinal);
            foreach (var targetHeight in new[] { 24f, 32f, 40f })
            {
                var localRead = latinOcr.Value.Read(strip, start, targetHeight / Math.Max(5, height), token);
                if (localRead.Confidence < .72f || TextSelection.At(localRead.Rows, cursor, false, null) is not { } hit
                    || !hit.Text.All(letter => char.IsAsciiLetter(letter) || letter is '-' or '\''))
                    continue;
                var previous = readings.GetValueOrDefault(hit.Text);
                readings[hit.Text] = (previous.Count + 2, localRead.Rows);
            }
            foreach (var targetHeight in new[] { 24f, 28f, 32f, 40f, 48f })
            {
                var readingScale = Math.Min(Math.Clamp(targetHeight / height, .5f, 6f),
                    (float)OcrEngine.MaxImageDimension / Math.Max(strip.Width, strip.Height));
                var found = await RecognizeBitmapAsync(strip, start, readingScale, token).ConfigureAwait(false);
                if (TextSelection.At(found, cursor, false, null) is not { } hit
                    || !hit.Text.All(letter => char.IsAsciiLetter(letter) || letter is '-' or '\'')
                    || !RectangleF.Inflate(hit.Bounds, 1, 2).Contains(cursor))
                    continue;
                var previous = readings.GetValueOrDefault(hit.Text);
                readings[hit.Text] = (previous.Count + 1, found);
            }
            var best = readings.OrderByDescending(reading => reading.Value.Count
                * (reading.Key.Length >= 4 && spelling.FrequencyRank(reading.Key) is > 0 and <= 50000 ? 3 : 1)).FirstOrDefault();
            if (best.Value.Count < 2)
                continue;
            var current = TextSelection.At([line], cursor, false, null);
            if (current?.Text == best.Key)
                continue;
            if (current is not null && best.Key.Length <= current.Text.Length
                && current.Text.All(char.IsAsciiLetter)
                && (spelling.Find(best.Key.ToLowerInvariant()) is null || spelling.Find(current.Text.ToLowerInvariant()) is not null))
                continue;
            var words = line.Words.Where(word => word.Text.Any(IsHan) || word.Bounds.Right <= left || word.Bounds.Left >= right)
                .Concat(best.Value.Rows.SelectMany(row => row.Words)).OrderBy(word => word.Bounds.Left).ToArray();
            return lines.Select(row => row == line ? new ScreenLine(words, words.Select(word => word.Bounds).Aggregate(RectangleF.Union))
                : row).ToArray();
        }
        return null;
    }

    private static ScreenLine[] CompleteMixedWords(ScreenLine[] mixed, ScreenLine[] english)
    {
        // The bilingual pass can split a Latin word at a glyph gap. Stitch it back only
        // when the English pass sees the same letters as one word in the same position.
        var englishWords = english.SelectMany(line => line.Words).ToArray();
        var reference = englishWords
            .Where(word => word.Text.Length >= 2 && word.Text.All(char.IsAsciiLetter)).ToArray();
        var hyphenated = englishWords.Where(word => word.Text.Contains('-')
            && word.Text.All(letter => char.IsAsciiLetter(letter) || letter == '-')).ToArray();
        return mixed.Select(line =>
        {
            if (!line.Text.Any(IsHan))
                return CorrectAppFileName(line);

            var words = new List<ScreenWord>();
            for (var index = 0; index < line.Words.Length; index++)
            {
                var first = line.Words[index];
                if (first.Text.EndsWith('-') && index + 2 < line.Words.Length
                    && RestoreHyphenatedWord(first, line.Words[index + 1], line.Words[index + 2], hyphenated) is { } restored)
                {
                    words.Add(restored);
                    index += 2;
                    continue;
                }
                if (index + 1 < line.Words.Length && first.Text.All(char.IsAsciiLetter))
                {
                    var second = line.Words[index + 1];
                    var bounds = RectangleF.Union(first.Bounds, second.Bounds);
                    var gap = second.Bounds.Left - first.Bounds.Right;
                    var height = Math.Min(first.Bounds.Height, second.Bounds.Height);
                    if (second.Text.All(char.IsAsciiLetter) && gap >= 0 && gap <= height * 0.2f
                        && Math.Abs(first.Bounds.Bottom - second.Bounds.Bottom) <= height * 0.2f)
                    {
                        var joined = first.Text + second.Text;
                        var tolerance = Math.Max(2, height * 0.22f);
                        if (reference.Any(word => word.Text.Equals(joined, StringComparison.OrdinalIgnoreCase)
                            && Math.Abs(bounds.Left - word.Bounds.Left) <= tolerance
                            && Math.Abs(bounds.Right - word.Bounds.Right) <= tolerance
                            && Math.Abs(bounds.Bottom - word.Bounds.Bottom) <= tolerance))
                        {
                            words.Add(new ScreenWord(joined, bounds));
                            index++;
                            continue;
                        }
                    }
                }
                words.Add(first);
            }
            return CorrectAppFileName(line with { Words = words.ToArray() });
        }).ToArray();
    }

    private static ScreenWord? RestoreHyphenatedWord(ScreenWord first, ScreenWord middle, ScreenWord last,
        ScreenWord[] english)
    {
        // The bilingual engine can read a Latin letter as a CJK punctuation mark.
        if (middle.Text.Length != 1 || !char.IsPunctuation(middle.Text[0])
            || !first.Text[..^1].All(char.IsAsciiLetter) || !last.Text.All(char.IsAsciiLetter))
            return null;
        var bounds = RectangleF.Union(first.Bounds, last.Bounds);
        var tolerance = Math.Max(2, bounds.Height * .22f);
        return english.FirstOrDefault(word => word.Text.Length == first.Text.Length + 1 + last.Text.Length
            && word.Text.StartsWith(first.Text, StringComparison.OrdinalIgnoreCase)
            && word.Text.EndsWith(last.Text, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(word.Bounds.Left - bounds.Left) <= tolerance && Math.Abs(word.Bounds.Right - bounds.Right) <= tolerance
            && Math.Abs(word.Bounds.Bottom - bounds.Bottom) <= tolerance && word.Bounds.IntersectsWith(middle.Bounds));
    }

    private static ScreenLine CorrectAppFileName(ScreenLine line)
    {
        var match = AppFileName().Match(line.Text);
        if (!match.Success)
            return line;

        var words = new List<ScreenWord>();
        var bounds = RectangleF.Empty;
        var offset = 0;
        foreach (var word in line.Words)
        {
            if (offset < match.Index + match.Length && offset + word.Text.Length > match.Index)
                bounds = bounds.IsEmpty ? word.Bounds : RectangleF.Union(bounds, word.Bounds);
            else
                words.Add(word);
            offset += word.Text.Length + 1;
        }
        // The product's executable name is known. Outside that exact filename, digits are kept.
        words.Add(new ScreenWord("ScreenTranslator.exe", bounds));
        return line with { Words = words.OrderBy(word => word.Bounds.Left).ToArray() };
    }

    private static ScreenLine FixSeriesNumeral(ScreenLine line, ScreenLine[] rows)
    {
        var words = line.Words;
        if (words.Length < 2 || !words[0].Text.Equals("Series", StringComparison.OrdinalIgnoreCase)
            || words.Skip(1).Any(word => word.Text.Any(letter => letter is not ('I' or 'V' or 'X' or '1'))))
            return line;
        var numeral = string.Concat(words.Skip(1).Select(word => word.Text)).Replace('1', 'I');
        if (numeral is not ("II" or "III" or "IV" or "VI" or "VII" or "VIII" or "IX" or "XI" or "XII" or "XIII" or "XIV" or "XV"))
            return line;

        // The achievement card shows a centered title directly above its Roman numeral.
        var center = line.Bounds.Left + line.Bounds.Width / 2;
        if (!rows.Any(row => row.Text.Equals("Challenger:", StringComparison.OrdinalIgnoreCase)
                && row.Bounds.Bottom <= line.Bounds.Top + 3
                && line.Bounds.Top - row.Bounds.Bottom <= line.Bounds.Height
                && Math.Abs(row.Bounds.Left + row.Bounds.Width / 2 - center) < line.Bounds.Height))
            return line;
        var bounds = words.Skip(1).Select(word => word.Bounds).Aggregate(RectangleF.Union);
        return line with { Words = [words[0], new ScreenWord(numeral, bounds)] };
    }

    private static unsafe bool HasMixedBrightText(Bitmap bitmap, Point cursor, bool cyanOnly = false,
        int? verticalRadius = null)
    {
        const int step = 4;
        var samples = (bitmap.Width + step - 1) / step;
        var radius = verticalRadius ?? (cyanOnly ? Math.Max(120, bitmap.Width / 16) : 120);
        var top = Math.Max(0, cursor.Y - radius);
        var bottom = Math.Min(bitmap.Height, cursor.Y + radius);
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = top; y < bottom; y += step)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                var dark = 0;
                var pale = 0;
                var colored = 0;
                for (var x = 0; x < bitmap.Width; x += step)
                {
                    var pixel = row + x * 4;
                    int red = pixel[2], green = pixel[1], blue = pixel[0];
                    var luma = Luminance(red, green, blue);
                    var spread = Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
                    if (luma < 120)
                        dark++;
                    else if (luma >= 185 && spread <= 37)
                        pale++;
                    else if (cyanOnly ? IsBrightCyan(red, green, blue) : luma >= 155 && spread >= 58)
                        colored++;
                }
                if (colored >= Math.Max(4, samples / 100)
                    && (cyanOnly ? pale >= 6 : dark * 2 >= samples && pale >= colored * 2))
                    return true;
            }
            return false;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static bool IsBrightCyan(int red, int green, int blue) =>
        green >= 190 && blue >= 195 && Math.Abs(green - blue) <= 42 && Math.Min(green, blue) - red >= 110;

    private static bool HasGoldLettering(Bitmap image, Point cursor)
    {
        var area = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size),
            new Rectangle(cursor.X - 35, cursor.Y - 24, 71, 49));
        var gold = 0;
        for (var y = area.Top; y < area.Bottom; y += 2)
            for (var x = area.Left; x < area.Right; x += 2)
            {
                var color = image.GetPixel(x, y);
                if (color.R >= 150 && color.G - color.B >= 35 && color.R - color.B >= 65)
                    gold++;
            }
        return gold >= Math.Max(6, area.Width * area.Height / 200);
    }

    private static bool HasCyanEmphasis(Bitmap bitmap, Point origin, RectangleF selection, ScreenLine[] lines)
    {
        // Require distinct pale and cyan words on a selected line. Bright scenery can also
        // trigger the pixel scan, and a separate cyan label should use the usual reading.
        foreach (var line in lines)
        {
            if (!selection.Contains(line.Bounds) || line.Words.Length < 3)
                continue;
            var paleWord = false;
            var cyanWord = false;
            foreach (var word in line.Words)
            {
                if (word.Text.Count(char.IsAsciiLetter) < 2)
                    continue;
                var bounds = Rectangle.Ceiling(word.Bounds);
                bounds.Offset(-origin.X, -origin.Y);
                bounds.Intersect(new Rectangle(Point.Empty, bitmap.Size));
                var pale = 0;
                var cyan = 0;
                for (var y = bounds.Top; y < bounds.Bottom; y += 4)
                    for (var x = bounds.Left; x < bounds.Right; x += 4)
                    {
                        var color = bitmap.GetPixel(x, y);
                        if (IsBrightCyan(color.R, color.G, color.B))
                            cyan++;
                        else if (IsPale(color.R, color.G, color.B, 220, 28))
                            pale++;
                    }
                paleWord |= pale >= 3 && pale > cyan * 3;
                cyanWord |= cyan >= 3 && cyan > pale * 3;
                if (paleWord && cyanWord)
                    return true;
            }
        }
        return false;
    }

    private static int? LightLetteringCutoff(Bitmap bitmap, Point cursor)
    {
        var radius = Math.Clamp(bitmap.Width / 160, 10, 24);
        var area = Rectangle.Intersect(new Rectangle(Point.Empty, bitmap.Size),
            new Rectangle(cursor.X - radius, cursor.Y - radius, radius * 2 + 1, radius * 2 + 1));
        var light = 0;
        var white = 0;
        var samples = 0;
        for (var y = area.Top; y < area.Bottom; y += 2)
            for (var x = area.Left; x < area.Right; x += 2)
            {
                var color = bitmap.GetPixel(x, y);
                if (IsPale(color.R, color.G, color.B, 205))
                    light++;
                if (IsPale(color.R, color.G, color.B, 245))
                    white++;
                samples++;
            }

        if (light < 6 || light * 100 < samples * 4 || light * 100 > samples * 58)
            return null;
        return bitmap.Width > 2560 && white * 5 >= light * 3 ? 236 : 205;
    }

    private static bool HasMapLabelColors(Bitmap bitmap, Point cursor, int? sampleRadius = null)
    {
        var radius = sampleRadius ?? Math.Clamp(bitmap.Width / 34, 24, 40);
        var lettering = 0;
        var terrain = 0;
        var samples = 0;
        for (var y = Math.Max(0, cursor.Y - radius); y < Math.Min(bitmap.Height, cursor.Y + radius); y += 2)
            for (var x = Math.Max(0, cursor.X - radius); x < Math.Min(bitmap.Width, cursor.X + radius); x += 2)
            {
                var color = bitmap.GetPixel(x, y);
                if (IsPale(color.R, color.G, color.B, 145, 9)
                    || color.R >= 225 && color.R - color.B is >= 17 and <= 26 && color.R > color.G)
                    lettering++;
                if (color is { R: var r, G: var g, B: var b }
                    && (b - r >= 22 && g - r >= 19 && Math.Abs(b - g) <= 60
                        || g - r >= 2 && r - b >= 9 && g - b >= 11
                        || r - b >= 30 && g - b >= 25 && g >= 100))
                    terrain++;
                samples++;
            }
        var minimumTerrain = sampleRadius is >= 96 ? 30 : 45;
        return lettering >= 6 && lettering * 100 >= samples * 2 && terrain * 100 >= samples * minimumTerrain;
    }

    private static bool IsPale(int red, int green, int blue, int cutoff, int spread = 16)
    {
        var darkest = Math.Min(red, Math.Min(blue, green));
        var brightest = Math.Max(red, Math.Max(blue, green));
        return darkest >= cutoff && brightest - darkest <= spread;
    }

    private static Color? SampleLetteringColor(Bitmap bitmap, Point cursor)
    {
        var radius = Math.Clamp(bitmap.Width / 90, 12, 36);
        // Sample solid cream lettering and cyan labels separately from the surrounding scenery.
        var histogram = new Dictionary<int, int>();
        var sampleCount = 0;
        for (var y = Math.Max(0, cursor.Y - radius); y < Math.Min(bitmap.Height, cursor.Y + radius); y += 2)
            for (var x = Math.Max(0, cursor.X - radius); x < Math.Min(bitmap.Width, cursor.X + radius); x += 2)
            {
                var color = bitmap.GetPixel(x, y);
                sampleCount++;
                var warm = color.R >= 225 && color.R > color.G + 4 && color.G >= color.B + 8 && color.R - color.B <= 27;
                var cyan = color.R <= 70 && color.G >= 203 && Math.Abs(color.G - color.B) < 8;
                if (!warm && !cyan)
                    continue;
                var key = color.R / 8 << 10 | color.G / 8 << 5 | color.B / 8;
                histogram[key] = histogram.GetValueOrDefault(key) + 1;
            }
        if (histogram.Count == 0)
            return null;
        var best = histogram.MaxBy(pair => pair.Value);
        if (best.Value < 8 || best.Value * 100 > sampleCount * 58)
            return null;
        return Color.FromArgb((best.Key >> 10) * 8 + 4, (best.Key >> 5 & 31) * 8 + 4, (best.Key & 31) * 8 + 4);
    }

    private static Bitmap LightTextMask(Bitmap original, int cutoff, CancellationToken token, Color? fill = null,
        int spread = 16, bool whiteOnBlack = false) =>
        ConvertPixels(original, token, (red, green, blue) =>
        {
            var keep = fill is { } color
                ? Math.Abs(red - color.R) <= 16 && Math.Abs(green - color.G) <= 16 && Math.Abs(blue - color.B) <= 16
                : IsPale(red, green, blue, cutoff, spread);
            return keep == whiteOnBlack ? (byte)255 : (byte)0;
        });

    private static Bitmap ConvertPixels(Bitmap original, CancellationToken token, Func<byte, byte, byte, byte> transform)
    {
        // Threshold at native resolution so interpolation cannot blend the lettering with scenery.
        var mask = original.Clone(new Rectangle(Point.Empty, original.Size), PixelFormat.Format32bppArgb);
        try
        {
            var data = mask.LockBits(new Rectangle(Point.Empty, mask.Size), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[mask.Width * 4];
                for (var y = 0; y < mask.Height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    var address = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(address, row, 0, row.Length);
                    for (var offset = 0; offset < row.Length; offset += 4)
                    {
                        var value = transform(row[offset + 2], row[offset + 1], row[offset]);
                        row[offset] = row[offset + 1] = row[offset + 2] = value;
                        row[offset + 3] = 255;
                    }
                    Marshal.Copy(row, 0, address, row.Length);
                }
            }
            finally { mask.UnlockBits(data); }
            return mask;
        }
        catch
        {
            mask.Dispose();
            throw;
        }
    }

    internal static Bitmap EnhanceLocalContrast(Bitmap original, CancellationToken token)
    {
        var bitmap = original.Clone(new Rectangle(Point.Empty, original.Size), PixelFormat.Format32bppArgb);
        try
        {
            var width = bitmap.Width;
            var height = bitmap.Height;
            var radius = Math.Clamp(width / 130, 10, 30);
            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[width * 4];
                var stride = width + 1;
                var sums = new int[stride * (height + 1)];
                for (var y = 0; y < height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    var running = 0;
                    for (var x = 0; x < width; x++)
                    {
                        var pixel = x * 4;
                        running += (29 * row[pixel] + 150 * row[pixel + 1] + 77 * row[pixel + 2]) >> 8;
                        sums[(y + 1) * stride + x + 1] = sums[y * stride + x + 1] + running;
                    }
                }

                for (var y = 0; y < height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    var top = Math.Max(0, y - radius);
                    var bottom = Math.Min(height, y + radius + 1);
                    for (var x = 0; x < width; x++)
                    {
                        var left = Math.Max(0, x - radius);
                        var right = Math.Min(width, x + radius + 1);
                        var mean = Sum(left, top, right, bottom) / ((right - left) * (bottom - top));
                        var luminance = Sum(x, y, x + 1, y + 1);
                        var value = (byte)Math.Clamp(128 + (luminance - mean) * 3, 0, 255);
                        var pixel = x * 4;
                        row[pixel] = row[pixel + 1] = row[pixel + 2] = value;
                        row[pixel + 3] = 255;
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                }

                int Sum(int left, int top, int right, int bottom) => sums[bottom * stride + right] - sums[bottom * stride + left]
                    - sums[top * stride + right] + sums[top * stride + left];
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private async Task<ScreenLine[]> RecognizeBitmapAsync(Bitmap original, Point origin, float scale, CancellationToken token,
        OcrEngine? languageEngine = null)
    {
        using var resized = scale == 1f ? null : new Bitmap(Math.Max(1, (int)(original.Width * scale)),
            Math.Max(1, (int)(original.Height * scale)), PixelFormat.Format32bppArgb);
        var scaled = resized ?? original;
        if (resized is not null)
        {
            using var graphics = Graphics.FromImage(resized);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(original, new Rectangle(Point.Empty, scaled.Size), 0, 0, original.Width, original.Height, GraphicsUnit.Pixel);
        }

        var bitmapData = scaled.LockBits(new Rectangle(Point.Empty, scaled.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        using var writer = new DataWriter();
        try
        {
            var pixels = new byte[bitmapData.Stride * bitmapData.Height];
            Marshal.Copy(bitmapData.Scan0, pixels, 0, pixels.Length);
            writer.WriteBytes(pixels);
        }
        finally
        {
            scaled.UnlockBits(bitmapData);
        }

        using var input = new SoftwareBitmap(BitmapPixelFormat.Bgra8, scaled.Width, scaled.Height, BitmapAlphaMode.Ignore);
        input.CopyFromBuffer(writer.DetachBuffer());
        var result = await (languageEngine ?? engine).RecognizeAsync(input).AsTask(token).ConfigureAwait(false);
        var scaleX = (float)scaled.Width / original.Width;
        var scaleY = (float)scaled.Height / original.Height;
        var radians = (result.TextAngle is > -12 and < 12 ? result.TextAngle.Value : 0) * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);

        return result.Lines.SelectMany(line => BuildLines(line.Words.Select(word =>
        {
            var bounds = word.BoundingRect;
            var offsetX = bounds.X + bounds.Width / 2 - scaled.Width / 2.0;
            var offsetY = bounds.Y + bounds.Height / 2 - scaled.Height / 2.0;
            var centerX = offsetX * cosine - offsetY * sine + scaled.Width / 2.0;
            var centerY = offsetX * sine + offsetY * cosine + scaled.Height / 2.0;
            var width = Math.Abs(bounds.Width * cosine) + Math.Abs(bounds.Height * sine);
            var height = Math.Abs(bounds.Width * sine) + Math.Abs(bounds.Height * cosine);
            return new ScreenWord(word.Text, new RectangleF(origin.X + (float)(centerX - width / 2) / scaleX,
                origin.Y + (float)(centerY - height / 2) / scaleY, (float)width / scaleX, (float)height / scaleY));
        }))).Select(CorrectAppFileName).ToArray();
    }

    internal static IEnumerable<ScreenLine> BuildLines(IEnumerable<ScreenWord> source)
    {
        var words = new List<ScreenWord>();
        var bounds = RectangleF.Empty;
        foreach (var recognized in source.OrderBy(word => word.Bounds.Left))
        {
            var text = recognized.Text.Replace("\u00AD", "", StringComparison.Ordinal)
                .Replace('\u2010', '-').Replace('\u2011', '-');
            if (text.Contains('•') || text.Contains('·'))
                text = PossessiveDot().Replace(text, "'");
            if (text.Length == 0)
                continue;
            var word = recognized with { Text = text };

            // A wide horizontal gap often separates independent controls or dialogue columns.
            if (words.Count > 0
                && word.Bounds.Left - bounds.Right > Math.Clamp(36f, bounds.Height * 2.8f, bounds.Height * 3.6f))
            {
                yield return new ScreenLine(words.ToArray(), bounds);
                words.Clear();
            }

            bounds = words.Count == 0 ? word.Bounds : RectangleF.Union(bounds, word.Bounds);
            if (words.Count > 0 && CanJoin(words[^1], word))
            {
                var previous = words[^1];
                words[^1] = new ScreenWord(previous.Text + word.Text, RectangleF.Union(previous.Bounds, word.Bounds));
            }
            else
            {
                words.Add(word);
            }
        }
        if (words.Count > 0)
            yield return new ScreenLine(words.ToArray(), bounds);
    }

    private static bool CanJoin(ScreenWord first, ScreenWord second)
    {
        var last = first.Text[^1];
        var next = second.Text[0];
        var height = Math.Max(first.Bounds.Height, second.Bounds.Height);
        var gap = second.Bounds.Left - first.Bounds.Right;
        if (second.Text.All(letter => ".,!?;:%)]}…".Contains(letter))
            && gap >= -height * .2f && gap <= height * .6f)
            return true;
        var joiner = IsJoiner(last) || IsJoiner(next);
        if (!(IsWordCharacter(last) && IsWordCharacter(next)
            || IsJoiner(last) && IsWordCharacter(next)
            || IsWordCharacter(last) && IsJoiner(next)))
            return false;

        var limit = joiner ? Math.Clamp(height * 0.14f, 1f, 3f) : Math.Clamp(height * 0.055f, 0.65f, 1.35f);
        if (gap < -limit || gap > limit)
            return false;

        var overlap = Math.Min(first.Bounds.Bottom, second.Bounds.Bottom) - Math.Max(first.Bounds.Top, second.Bounds.Top);
        return overlap >= Math.Min(first.Bounds.Height, second.Bounds.Height) * 0.6f;
    }

    private static bool IsWordCharacter(char value) => char.IsAsciiLetterOrDigit(value)
        || value is >= '\u00C0' and <= '\u024F' && char.IsLetter(value);

    private static bool IsJoiner(char value) => value is '\'' or '’' or '-';
}

internal sealed record Selection(string Text, string Context, RectangleF Bounds, string? Term = null, string? TermSource = null);

internal static partial class TextSelection
{
    private const string WordCharacters = @"[A-Za-z\u00C0-\u00D6\u00D8-\u00F6\u00F8-\u024F\p{M}0-9]";

    [GeneratedRegex(WordCharacters + @"+(?:['’\-]" + WordCharacters + @"+)*")]
    private static partial Regex EnglishWord();

    [GeneratedRegex(@"^\s*\d+(?:[.,]\d+)?\s*(?:m|km)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DistanceLabel();

    [GeneratedRegex(@"^\s*\d+\s+(?:day|hour|minute|second)s?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CountdownLabel();

    public static Selection? At(ScreenLine[] lines, Point cursor, bool paragraph, TermGlossary? glossary)
    {
        if (paragraph)
            return ParagraphAt(lines, cursor);

        // A nearby Latin word's hover padding must not override the Han glyph under the pointer.
        if (lines.SelectMany(line => line.Words).Any(word => word.Text.Any(ScreenOcr.IsHan)
            && !word.Text.Any(char.IsAsciiLetter) && word.Bounds.Contains(cursor)))
            return null;

        var candidates = lines.SelectMany(line => line.Words.Select((word, index) => (line, word, index)))
            .Select(item => (item.line, item.word, item.index, distance: Distance(item.word.Bounds, cursor,
                Math.Clamp(item.word.Bounds.Height * .24f, 6, 12), Math.Clamp(item.word.Bounds.Height * .3f, 9, 14))))
            .Where(item => float.IsFinite(item.distance))
            .OrderBy(item => item.distance);
        foreach (var (line, word, index, _) in candidates)
        {
            var token = WordAt(word, cursor.X);
            if (token is null)
                continue;

            var term = glossary?.At(line, index);
            if (term is not null)
                return term;

            return HoverPhrases.At(line, index) ?? new Selection(token, line.Text, word.Bounds);
        }
        return null;
    }

    private static string? WordAt(ScreenWord word, int cursorX)
    {
        // OCR can place slash-separated words in one bounding box. Use the hovered part of the box.
        var position = Math.Clamp((cursorX - word.Bounds.Left) / Math.Max(1f, word.Bounds.Width), 0f, 1f)
            * word.Text.Length;
        return EnglishWord().Matches(word.Text).Cast<Match>().Where(match => match.Value.Any(char.IsLetter))
            .MinBy(match => Math.Max(0, Math.Max(match.Index - position, position - (match.Index + match.Length))))?.Value;
    }

    private static Selection? ParagraphAt(ScreenLine[] lines, Point cursor)
    {
        if (ListAt(lines, cursor) is { } list)
            return list;

        var ordered = lines.Where(line => !DistanceLabel().IsMatch(line.Text)
            && line.Words.Any(word => WordAt(word, (int)word.Bounds.Left) is not null))
            .OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left).ToArray();
        var anchor = ordered.Select(line => (line, distance: Distance(line.Bounds, cursor,
                Math.Max(16, line.Bounds.Height * 0.6f), Math.Max(10, line.Bounds.Height * 0.27f))))
            .Where(item => float.IsFinite(item.distance)).OrderBy(item => item.distance).FirstOrDefault().line;
        if (anchor is null)
            return null;

        var above = new List<ScreenLine>();
        var below = new List<ScreenLine>();
        // Only follow reciprocal links so the block has the same boundaries from every line.
        var previous = anchor;
        while (LinkedNeighbor(ordered, previous, false) is { } nextAbove)
        {
            above.Add(nextAbove);
            previous = nextAbove;
        }
        previous = anchor;
        while (LinkedNeighbor(ordered, previous, true) is { } nextBelow)
        {
            below.Add(nextBelow);
            previous = nextBelow;
        }
        above.Reverse();
        var block = above.Append(anchor).Concat(below).ToArray();
        var text = string.Join(IsMetadataBlock(block) ? '\n' : ' ', block.Select(line => line.Text));
        var bounds = block.Select(line => line.Bounds).Aggregate(RectangleF.Union);
        return new Selection(text, text, bounds);
    }

    internal static Rectangle ParagraphDetailArea(Rectangle capture, RectangleF bounds)
    {
        var padding = Math.Clamp((int)(capture.Width / 150f), 12, 32);
        var padded = RectangleF.Inflate(bounds, padding, padding);
        // Snap OCR's slightly varying bounds to the same pixel grid before the final read.
        const int step = 8;
        return Rectangle.Intersect(capture, Rectangle.FromLTRB(step * (int)MathF.Floor(padded.Left / step),
            step * (int)MathF.Floor(padded.Top / step), step * (int)MathF.Ceiling(padded.Right / step),
            step * (int)MathF.Ceiling(padded.Bottom / step)));
    }

    internal static bool PreservesParagraph(Selection candidate, Selection initial) =>
        IncludesParagraph(candidate.Bounds, initial.Bounds)
        && IncludesParagraph(initial.Bounds, candidate.Bounds)
        && candidate.Text.Count(char.IsLetterOrDigit) >= initial.Text.Count(char.IsLetterOrDigit) * 0.96f;

    internal static bool SameParagraphWords(Selection candidate, Selection initial) =>
        EnglishWord().Matches(candidate.Text).Select(word => word.Value)
            .SequenceEqual(EnglishWord().Matches(initial.Text).Select(word => word.Value), StringComparer.OrdinalIgnoreCase);

    internal static bool RepairsBracketedLabel(Selection candidate, Selection initial)
    {
        var end = initial.Text.LastIndexOf('>');
        return candidate.Text.StartsWith('<') && candidate.Text.EndsWith('>')
            && end >= 0 && initial.Text.Length - end is >= 2 and <= 3
            && candidate.Text.Equals(initial.Text[..(end + 1)], StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ImprovesParagraphSpelling(Selection candidate, Selection initial)
    {
        if (!PreservesParagraph(candidate, initial))
            return false;
        var before = EnglishWord().Matches(initial.Text).Select(word => word.Value).ToArray();
        var after = EnglishWord().Matches(candidate.Text).Select(word => word.Value).ToArray();
        var first = string.Concat(before).ToLowerInvariant();
        var second = string.Concat(after).ToLowerInvariant();
        if (ScreenOcr.EditDistance(first, second, 3) > 3)
            return false;
        if (candidate.Text.StartsWith(initial.Text + " ", StringComparison.OrdinalIgnoreCase)
            && after.LastOrDefault() is { Length: > 0 and <= 8 } last && last.All(letter => "IVXLCDM".Contains(letter)))
            return true;
        return before.Count(word => word.Length >= 3 && !OfflineDictionary.IsFrequentWord(word))
                > after.Count(word => word.Length >= 3 && !OfflineDictionary.IsFrequentWord(word))
            && before.Where(OfflineDictionary.IsFrequentWord)
                .All(word => after.Contains(word, StringComparer.OrdinalIgnoreCase));
    }

    internal static bool RestoresParagraphWords(Selection candidate, Selection initial, ScreenLine[] rawLines)
    {
        if (!IncludesParagraph(candidate.Bounds, initial.Bounds))
            return false;

        // Require added words on the original lines. A nearby title or column is not evidence of missing text.
        var sameRows = rawLines.Where(line => line.Bounds.IntersectsWith(initial.Bounds)
                && line.Bounds.Top + line.Bounds.Height / 2 >= initial.Bounds.Top
                && line.Bounds.Top + line.Bounds.Height / 2 <= initial.Bounds.Bottom)
            .OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left);
        var before = EnglishWord().Matches(initial.Text);
        var after = EnglishWord().Matches(string.Join(' ', sameRows.Select(line => line.Text)));
        if (before.Count < 3 || after.Count <= before.Count)
            return false;

        var index = 0;
        foreach (Match word in after)
            if (word.Value.Equals(before[index].Value, StringComparison.OrdinalIgnoreCase) && ++index == before.Count)
                return true;
        return false;
    }

    internal static bool AgreesOnParagraphWords(Selection candidate, Selection initial)
    {
        if (!PreservesParagraph(candidate, initial))
            return false;

        var before = EnglishWord().Matches(initial.Text);
        var after = EnglishWord().Matches(candidate.Text);
        if (before.Count < 8 || before.Count != after.Count)
            return false;

        // The contrast mask can make one ambiguous letter clearer without changing the selection.
        var changes = 0;
        for (var index = 0; index < before.Count; index++)
        {
            var oldWord = before[index].Value.ToLowerInvariant();
            var newWord = after[index].Value.ToLowerInvariant();
            if (oldWord == newWord)
                continue;
            if (++changes > 1 || oldWord.Length < 6 || oldWord.Length != newWord.Length
                || oldWord.Where((letter, position) => letter != newWord[position]).Count() != 1)
                return false;
        }
        return changes == 1;
    }

    private static ScreenLine? LinkedNeighbor(ScreenLine[] lines, ScreenLine line, bool below)
    {
        var next = Neighbor(lines, line, below);
        return next is not null && ReferenceEquals(Neighbor(lines, next, !below), line) ? next : null;
    }

    private static ScreenLine? Neighbor(ScreenLine[] lines, ScreenLine line, bool below)
    {
        var current = line.Bounds;
        return lines.Where(candidate =>
        {
            var next = candidate.Bounds;
            var height = Math.Max(current.Height, next.Height);
            var centerDifference = next.Top + next.Height / 2 - current.Top - current.Height / 2;
            var gap = below ? next.Top - current.Bottom : current.Top - next.Bottom;
            if ((below ? centerDifference : -centerDifference) <= height * 0.5f
                || gap < -height * 0.25f || gap > height * 0.95f
                || Math.Min(next.Height, current.Height) < height * 0.58f)
                return false;

            var overlap = Math.Min(current.Right, next.Right) - Math.Max(current.Left, next.Left);
            var narrower = Math.Min(current.Width, next.Width);
            var wider = Math.Max(current.Width, next.Width);
            var first = below ? line : candidate;
            var second = below ? candidate : line;
            if (first.Words.Length <= 4 && gap > height * .55f
                && CountdownLabel().IsMatch(second.Text) && !char.IsPunctuation(first.Text[^1]))
                return false;
            if (first.Words.Length == 1 && second.Text.StartsWith('<'))
                return false;
            if (first.Words.Length == 1 && second.Words.Length >= 4
                && gap > height * 0.5f && char.IsUpper(second.Text.FirstOrDefault(char.IsLetter))
                && !char.IsPunctuation(first.Text[^1]))
                return false;
            // A larger, short heading can precede a body line that OCR has already clipped.
            var heading = first.Bounds.Height >= second.Bounds.Height * 1.2f && first.Bounds.Width < second.Bounds.Width * 0.85f;
            if (gap > Math.Min(current.Height, next.Height) * 0.55f
                && (first.Text.EndsWith("...", StringComparison.Ordinal) || first.Text.EndsWith('…'))
                && (heading || first.Words.Length <= 5 && first.Bounds.Width < second.Bounds.Width * .85f))
                return false;

            // A short wrapped line can be offset from the preceding line. Require stronger overlap
            // in that case to avoid joining text from a neighboring column.
            return overlap >= narrower * 0.82f || overlap >= wider * 0.6f;
        }).MinBy(candidate => Math.Abs(candidate.Bounds.Top + candidate.Bounds.Height / 2 - current.Top - current.Height / 2));
    }

    internal static Rectangle ExtendParagraphCapture(Rectangle capture, Rectangle available, RectangleF text, float lineHeight)
    {
        var margin = Math.Max(Math.Clamp(capture.Width * 0.022f, 30f, 90f), lineHeight * 1.6f);
        var top = capture.Top;
        var bottom = capture.Bottom;
        // Read more only in the direction where the selected paragraph reaches the image edge.
        var extension = Math.Max(120, (int)(capture.Width * 0.09f));
        if (text.Top - capture.Top <= margin)
            top = Math.Max(available.Top, top - extension);
        if (capture.Bottom - text.Bottom <= margin)
            bottom = Math.Min(available.Bottom, bottom + extension);
        return Rectangle.FromLTRB(capture.Left, top, capture.Right, bottom);
    }

    internal static bool IncludesParagraph(RectangleF expanded, RectangleF initial) =>
        expanded.Left <= initial.Left + 12 && expanded.Right >= initial.Right - 12
        && expanded.Top <= initial.Top + 12 && expanded.Bottom >= initial.Bottom - 12;

    private static float Distance(RectangleF rect, Point point, float padX, float padY)
    {
        var dx = Math.Max(0, Math.Max(rect.Left - point.X, point.X - rect.Right));
        var dy = Math.Max(0, Math.Max(rect.Top - point.Y, point.Y - rect.Bottom));
        return dx <= padX && dy <= padY ? dx * dx + dy * dy * 2 : float.PositiveInfinity;
    }
}
