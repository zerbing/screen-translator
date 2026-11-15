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

    private ScreenOcr(OcrEngine engine) => this.engine = engine;

    public void Dispose() => hdrCapture.Dispose();

    [GeneratedRegex(@"(?<=[A-Za-z])[•·](?=[sS]\b)")]
    private static partial Regex PossessiveDot();

    [GeneratedRegex(@"\bScreenTrans[1Il]ator\s*[.．·]\s*exe(?:[oO0](?=\s|$))?(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppFileName();

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
        Func<ScreenLine[], bool>? hasTarget, CancellationToken token, bool allowMixedText, bool largeDisplayOnly)
    {
        token.ThrowIfCancellationRequested();
        using var hdr = hdrCapture.TryRead(area, token);
        if (hdr is not null)
            return await ReadBitmapAsync(hdr, area.Location, scale, cursor, hasTarget, token, allowMixedText, largeDisplayOnly)
                .ConfigureAwait(false);

        using var original = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(original))
            graphics.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
        return await ReadBitmapAsync(original, area.Location, scale, cursor, hasTarget, token, allowMixedText, largeDisplayOnly)
            .ConfigureAwait(false);
    }

    internal async Task<ScreenLine[]> ReadBitmapAsync(Bitmap original, Point origin, float scale,
        Point cursor = default, Func<ScreenLine[], bool>? hasTarget = null, CancellationToken token = default,
        bool allowMixedText = false, bool largeDisplayOnly = false)
    {
        var local = new Point(cursor.X - origin.X, cursor.Y - origin.Y);
        if (largeDisplayOnly)
            return await ReadDisplayLabelAsync(original, origin, local, cursor, null, token, largeDisplayOnly: true)
                .ConfigureAwait(false) ?? [];

        if (hasTarget is not null && HasMapLabelColors(original, local))
        {
            var radius = Math.Clamp(original.Width / 2, 330, 650);
            var halfHeight = Math.Clamp(original.Width / 8, 75, 116);
            var region = Rectangle.Intersect(new Rectangle(Point.Empty, original.Size),
                new Rectangle(local.X - radius, local.Y - halfHeight, 2 * radius, 2 * halfHeight));
            using var patch = original.Clone(region, PixelFormat.Format32bppArgb);
            using var muted = LightTextMask(patch, 145, token, spread: 9);
            var patchOrigin = new Point(origin.X + region.X, origin.Y + region.Y);
            // Preserve the label's geometry when a map symbol overlaps the glyphs.
            var mapRows = await RecognizeBitmapAsync(muted, patchOrigin, scale < 1.1f ? 1.35f : 1.85f, token).ConfigureAwait(false);
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
            if (label is not null && hasTarget([label]))
                return [label];
        }

        if (hasTarget is not null)
        {
            var sample = SampleLetteringColor(original, local);
            if (sample is { } fill)
            {
                using var flat = LightTextMask(original, 0, token, fill);
                var rows = await RecognizeBitmapAsync(flat, origin, scale, token).ConfigureAwait(false);
                if (hasTarget(rows))
                    return rows.Select(line => FixSeriesNumeral(line, rows)).ToArray();
            }
        }

        if (hasTarget is not null && LightLetteringCutoff(original, new Point(cursor.X - origin.X, cursor.Y - origin.Y)) is { } cutoff)
        {
            token.ThrowIfCancellationRequested();
            using var mask = LightTextMask(original, cutoff, token);
            var lightLines = await RecognizeBitmapAsync(mask, origin, scale, token).ConfigureAwait(false);
            if (hasTarget(lightLines))
                return lightLines;
        }

        token.ThrowIfCancellationRequested();
        var luminance = allowMixedText ? SampleLuminance(original, local) : null;
        using var boosted = luminance is null ? null : BoostFaintText(original, luminance, token);
        var wordImage = boosted ?? original;
        var originalLines = await RecognizeBitmapAsync(wordImage, origin, scale, token).ConfigureAwait(false);
        if (allowMixedText)
        {
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
                    foreach (var readingScale in new[] { 1.5f, 2.5f, 3.5f })
                    {
                        var lineRows = await RecognizeBitmapAsync(lineImage, lineOrigin, readingScale, token).ConfigureAwait(false);
                        if (TextSelection.At(lineRows, cursor, false, null) is { } hit && IsUppercaseWord(hit.Text))
                            return lineRows;
                    }

                    var normalized = await ReadCondensedLineAsync(displayCrop, line, displayOrigin, cursor, token).ConfigureAwait(false);
                    if (normalized is not null)
                        return normalized;
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
                    var stableLines = await RecognizeBitmapAsync(fullLine, lineOrigin,
                        Math.Min(1.5f, (float)OcrEngine.MaxImageDimension / stable.Width), token, mixed).ConfigureAwait(false);
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
                        if (boosted is not null || rows.Any(line => AppFileName().IsMatch(line.Text)))
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
        if (hasTarget is null || hasTarget(originalLines))
            return originalLines;

        token.ThrowIfCancellationRequested();
        using var enhanced = EnhanceLocalContrast(original, token);
        return await RecognizeBitmapAsync(enhanced, origin, scale, token).ConfigureAwait(false);
    }

    private static bool IsUppercaseWord(string text) => text.Count(char.IsAsciiLetterUpper) >= 2
        && text.All(letter => char.IsAsciiLetterUpper(letter) || char.IsAsciiDigit(letter));

    private static Rectangle? FindDisplayLine(Bitmap mask, Point cursor, int maxHeight = 64, float gapRatio = 1)
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
        if (height < 8 || height > maxHeight)
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
        // Image viewers can scale a narrow raster font to different pixel grids. Restore a
        // readable stroke width before OCR and map its word bounds back to the screen.
        var horizontalMargin = Math.Max(1, line.Height / 11);
        var verticalMargin = Math.Max(2, line.Height / 8);
        var padded = Rectangle.Inflate(line, horizontalMargin, verticalMargin);
        if (line.Height < 19)
            padded.Height++;
        var region = Rectangle.Intersect(new Rectangle(Point.Empty, original.Size),
            padded);
        using var raw = original.Clone(region, PixelFormat.Format32bppArgb);
        using var mask = LightTextMask(raw, 245, token, whiteOnBlack: true);

        foreach (var (height, aspect) in new[] { (32, 1.5), (48, 1.5), (48, 1.16), (32, 1.33) })
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
            if (rows.Length == 0)
                continue;
            var horizontalScale = (float)region.Width / width;
            var verticalScale = (float)region.Height / height;
            RectangleF ToScreen(RectangleF bounds) => new(
                origin.X + region.X + (bounds.X - left) * horizontalScale,
                origin.Y + region.Y + (bounds.Y - top) * verticalScale,
                bounds.Width * horizontalScale, bounds.Height * verticalScale);
            var projected = rows.Select(row => new ScreenLine(
                row.Words.Select(word => word with { Bounds = ToScreen(word.Bounds) }).ToArray(), ToScreen(row.Bounds))).ToArray();
            if (TextSelection.At(projected, cursor, false, null) is { } hit && IsUppercaseWord(hit.Text))
                return projected;
        }
        return null;
    }

    private static bool HasBrightTextOnBlack(Bitmap bitmap, Point cursor, int[] samples)
    {
        var total = samples.Sum();
        if (total == 0 || samples.Skip(231).Sum() * 100 < total * 7)
            return false;
        if (samples.Take(24).Sum() * 100 >= total * 50)
            return true;

        // When a pointer is over the edge of a large label, the glyphs dominate the
        // small sample. Check a little farther out for the dark canvas.
        var surrounding = SampleLuminance(bitmap, cursor, 64, 60);
        return surrounding.Take(43).Sum() * 100 >= surrounding.Sum() * 50;
    }

    private static Bitmap? BoostFaintText(Bitmap original, int[] samples, CancellationToken token)
    {
        var total = samples.Sum();
        var brightestBand = 230;
        for (var value = 231; value < samples.Length; value++)
            if (samples[value] > samples[brightestBand])
                brightestBand = value;
        var background = 0;
        var mutedInk = 0;
        var darkerInk = 0;
        for (var value = 0; value < samples.Length; value++)
        {
            if (Math.Abs(brightestBand - value) <= 4)
                background += samples[value];
            else if (value < brightestBand - 80)
                darkerInk += samples[value];
            else if (value <= brightestBand - 16)
                mutedInk += samples[value];
        }
        if (total == 0 || background * 100 < total * 58 || mutedInk * 100 < total * 5 || darkerInk * 100 > total * 2)
            return null;

        return ConvertPixels(original, token, (red, green, blue) =>
            (byte)Math.Clamp(255 - (brightestBand - 2 - Luminance(red, green, blue)) * 8, 0, 255));
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

    private static int Luminance(int red, int green, int blue) => (77 * red + 150 * green + 29 * blue + 128) >> 8;

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

    private static ScreenLine[] CompleteMixedWords(ScreenLine[] mixed, ScreenLine[] english)
    {
        // The bilingual pass can split a Latin word at a glyph gap. Stitch it back only
        // when the English pass sees the same letters as one word in the same position.
        var reference = english.SelectMany(line => line.Words)
            .Where(word => word.Text.Length >= 2 && word.Text.All(char.IsAsciiLetter)).ToArray();
        return mixed.Select(line =>
        {
            if (!line.Text.Any(IsHan))
                return CorrectAppFileName(line);

            var words = new List<ScreenWord>();
            for (var index = 0; index < line.Words.Length; index++)
            {
                var first = line.Words[index];
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

    private static bool HasMapLabelColors(Bitmap bitmap, Point cursor)
    {
        var radius = Math.Clamp(bitmap.Width / 34, 24, 40);
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
                && (b - r >= 22 && g - r >= 19 && Math.Abs(b - g) <= 24
                    || g - r >= 2 && r - b >= 9 && g - b >= 11))
                terrain++;
            samples++;
        }
        return lettering >= 6 && lettering * 100 >= samples * 2 && terrain * 100 >= samples * 45;
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
        }))).ToArray();
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
            if (words.Count > 0 && word.Bounds.Left - bounds.Right > Math.Max(36, bounds.Height * 2.8f))
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
        var joiner = IsJoiner(last) || IsJoiner(next);
        if (!(IsWordCharacter(last) && IsWordCharacter(next)
            || IsJoiner(last) && IsWordCharacter(next)
            || IsWordCharacter(last) && IsJoiner(next)))
            return false;

        var height = Math.Max(first.Bounds.Height, second.Bounds.Height);
        var gap = second.Bounds.Left - first.Bounds.Right;
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

    public static Selection? At(ScreenLine[] lines, Point cursor, bool paragraph, TermGlossary? glossary)
    {
        if (paragraph)
            return ParagraphAt(lines, cursor);

        var candidates = lines.SelectMany(line => line.Words.Select((word, index) => (line, word, index)))
            .Select(item => (item.line, item.word, item.index, distance: Distance(item.word.Bounds, cursor, 6, 9)))
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
        var text = string.Join(' ', block.Select(line => line.Text));
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
            var height = Math.Max(10, Math.Max(current.Height, next.Height));
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
            if (first.Words.Length == 1 && second.Words.Length >= 4
                && gap > height * 0.5f && char.IsUpper(second.Text.FirstOrDefault(char.IsLetter))
                && !char.IsPunctuation(first.Text[^1]))
                return false;
            // Dialogue choices often end in an ellipsis and are separated by a visibly larger gap.
            if (gap > Math.Min(current.Height, next.Height) * 0.55f
                && (first.Text.EndsWith("...", StringComparison.Ordinal) || first.Text.EndsWith('…'))
                && char.IsUpper(second.Text.FirstOrDefault(char.IsLetter)))
                return false;

            // A short wrapped line can be offset from the preceding line. Require stronger overlap
            // in that case to avoid joining text from a neighboring column.
            return overlap >= narrower * 0.82f || overlap >= wider * 0.6f;
        }).MinBy(candidate => Math.Abs(candidate.Bounds.Top + candidate.Bounds.Height / 2 - current.Top - current.Height / 2));
    }

    internal static Rectangle ExtendParagraphCapture(Rectangle capture, Rectangle available, RectangleF text)
    {
        var margin = Math.Clamp(capture.Width * 0.022f, 30f, 90f);
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
