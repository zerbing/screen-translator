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

    public async Task<ScreenLine[]> ReadAsync(Rectangle area, float scale)
    {
        using var original = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(original))
            graphics.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
        return await ReadBitmapAsync(original, area.Location, scale).ConfigureAwait(false);
    }

    internal async Task<ScreenLine[]> ReadBitmapAsync(Bitmap original, Point origin, float scale)
    {
        using var scaled = new Bitmap(Math.Max(1, (int)(original.Width * scale)), Math.Max(1, (int)(original.Height * scale)), PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
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
        var result = await engine.RecognizeAsync(input).AsTask().ConfigureAwait(false);
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
        var ordered = lines.Where(line => line.Words.Any(word => WordAt(word, (int)word.Bounds.Left) is not null))
            .OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left).ToArray();
        var anchor = ordered.Select(line => (line, distance: Distance(line.Bounds, cursor, 16, 10)))
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
            var aligned = Math.Abs(current.Left - next.Left) <= height * 1.5f
                || Math.Abs(current.Right - next.Right) <= height * 1.5f
                || Math.Abs(current.Left + current.Width / 2 - next.Left - next.Width / 2) <= height * 1.5f;
            return aligned && overlap > Math.Min(current.Width, next.Width) * 0.55f;
        }).MinBy(candidate => Math.Abs(candidate.Bounds.Top + candidate.Bounds.Height / 2 - current.Top - current.Height / 2));
    }

    internal static Selection RefineParagraph(ScreenLine[] lines, Selection first)
    {
        var block = lines.OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left).ToArray();
        if (block.Length == 0)
            return first;
        var bounds = block.Select(line => line.Bounds).Aggregate(RectangleF.Union);
        var shared = RectangleF.Intersect(bounds, first.Bounds);
        if (shared.Height < first.Bounds.Height * 0.84f || shared.Width < first.Bounds.Width * 0.8f)
            return first;

        var text = string.Join(' ', block.Select(line => line.Text));
        if (text.Count(char.IsLetterOrDigit) < first.Text.Count(char.IsLetterOrDigit) * 0.9)
            return first;
        return new Selection(text, text, first.Bounds);
    }

    private static float Distance(RectangleF rect, Point point, float padX, float padY)
    {
        var dx = Math.Max(0, Math.Max(rect.Left - point.X, point.X - rect.Right));
        var dy = Math.Max(0, Math.Max(rect.Top - point.Y, point.Y - rect.Bottom));
        return dx <= padX && dy <= padY ? dx * dx + dy * dy * 2 : float.PositiveInfinity;
    }
}
