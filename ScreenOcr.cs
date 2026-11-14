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

internal sealed partial class ScreenOcr
{
    private readonly OcrEngine engine;

    private ScreenOcr(OcrEngine engine) => this.engine = engine;

    [GeneratedRegex(@"(?<=[A-Za-z])[•·](?=[sS]\b)")]
    private static partial Regex PossessiveDot();

    public static ScreenOcr? Create()
    {
        var english = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(language =>
            language.LanguageTag.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            ?? OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(language =>
                language.LanguageTag.StartsWith("en-", StringComparison.OrdinalIgnoreCase));
        var engine = english is null ? null : OcrEngine.TryCreateFromLanguage(new Language(english.LanguageTag));
        return engine is null ? null : new ScreenOcr(engine);
    }

    public async Task<ScreenLine[]> ReadAsync(Rectangle area, float scale, Point cursor,
        Func<ScreenLine[], bool>? hasTarget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var original = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(original))
            graphics.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
        return await ReadBitmapAsync(original, area.Location, scale, cursor, hasTarget, token).ConfigureAwait(false);
    }

    internal async Task<ScreenLine[]> ReadBitmapAsync(Bitmap original, Point origin, float scale,
        Point cursor = default, Func<ScreenLine[], bool>? hasTarget = null, CancellationToken token = default)
    {
        if (hasTarget is not null && LightLetteringCutoff(original, new Point(cursor.X - origin.X, cursor.Y - origin.Y)) is { } cutoff)
        {
            token.ThrowIfCancellationRequested();
            using var mask = LightTextMask(original, cutoff, token);
            var lightLines = await RecognizeBitmapAsync(mask, origin, scale, token).ConfigureAwait(false);
            if (hasTarget(lightLines))
                return lightLines;
        }

        token.ThrowIfCancellationRequested();
        var originalLines = await RecognizeBitmapAsync(original, origin, scale, token).ConfigureAwait(false);
        if (hasTarget is null || hasTarget(originalLines))
            return originalLines;

        token.ThrowIfCancellationRequested();
        using var enhanced = EnhanceLocalContrast(original, token);
        return await RecognizeBitmapAsync(enhanced, origin, scale, token).ConfigureAwait(false);
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

    private static bool IsPale(int red, int green, int blue, int cutoff)
    {
        var darkest = Math.Min(red, Math.Min(blue, green));
        var brightest = Math.Max(red, Math.Max(blue, green));
        return darkest >= cutoff && brightest - darkest <= 16;
    }

    private static Bitmap LightTextMask(Bitmap original, int cutoff, CancellationToken token)
    {
        // Pale UI lettering is nearly neutral, unlike most scenery and the colored status labels.
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
                        var value = IsPale(row[offset + 2], row[offset + 1], row[offset], cutoff) ? (byte)0 : (byte)255;
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

    private async Task<ScreenLine[]> RecognizeBitmapAsync(Bitmap original, Point origin, float scale, CancellationToken token)
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
        var result = await engine.RecognizeAsync(input).AsTask(token).ConfigureAwait(false);
        var scaleX = (float)scaled.Width / original.Width;
        var scaleY = (float)scaled.Height / original.Height;

        return result.Lines.SelectMany(line => BuildLines(line.Words.Select(word =>
        {
            var bounds = word.BoundingRect;
            return new ScreenWord(word.Text, new RectangleF(
                origin.X + (float)bounds.X / scaleX, origin.Y + (float)bounds.Y / scaleY,
                (float)bounds.Width / scaleX, (float)bounds.Height / scaleY));
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
