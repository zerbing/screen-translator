using System.Drawing.Imaging;
using System.Text.RegularExpressions;

namespace ScreenTranslator;

internal sealed partial class ScreenOcr
{
    private ScreenLine[]? ReadCodeCell(Bitmap image, Point origin, Point cursor, CancellationToken token)
    {
        var local = new Point(cursor.X - origin.X, cursor.Y - origin.Y);
        if (FindTableCell(image, local) is not { } cell)
            return null;
        using var crop = image.Clone(cell, PixelFormat.Format32bppArgb);
        var start = new Point(origin.X + cell.X, origin.Y + cell.Y);
        var readings = new List<(string Text, RectangleF Bounds, float Confidence)>();
        foreach (var height in new[] { 48f, 64f, 80f, 96f })
            Read(crop, height);
        if (cell.Height < 28)
            for (var channel = 0; channel < 3; channel++)
            {
                using var contrast = ConvertPixels(crop, token, (red, green, blue) => channel == 0 ? red : channel == 1 ? green : blue);
                foreach (var height in new[] { 48f, 80f }) Read(contrast, height);
            }
        var best = readings.GroupBy(read => read.Text).Where(group => group.Count() >= 2)
            .OrderByDescending(group => group.Count()).ThenByDescending(group => group.Average(read => read.Confidence)).FirstOrDefault();
        if (best is null)
            return null;
        var chosen = best.OrderByDescending(read => read.Confidence).First();
        return [new ScreenLine([new ScreenWord(chosen.Text, chosen.Bounds)], chosen.Bounds)
            { CompleteCell = cell.Bottom < image.Height - 2 }];

        void Read(Bitmap source, float height)
        {
            var read = latinOcr.Value.Read(source, start, height / cell.Height, token);
            var text = string.Concat(read.Rows.SelectMany(row => row.Words).Select(word => word.Text));
            var letters = read.Rows.SelectMany(row => row.Words).ToArray();
            var confidence = letters.Select(word => float.IsNaN(word.LetterConfidence)
                ? word.Confidence : word.LetterConfidence).DefaultIfEmpty(0).Average();
            if (confidence < .7f || !TextSelection.IsStructuredCode(text))
                return;
            var bounds = read.Rows.SelectMany(row => row.Words).Select(word => word.Bounds).Aggregate(RectangleF.Union);
            readings.Add((text, bounds, confidence));
        }
    }

    private static unsafe Rectangle? FindTableCell(Bitmap image, Point cursor)
    {
        var width = image.Width;
        var height = image.Height;
        if (width < 60 || height < 25)
            return null;
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var vertical = ThinRules(width, x =>
            {
                var first = -1;
                var last = -1;
                bool Complete() => first >= 0 && first <= cursor.Y && last >= cursor.Y
                    && last - first >= Math.Min(80, height * .6f) && IsTableBoundary(x, first, last);
                for (var y = 2; y < height - 2; y++)
                {
                    if (!IsRule(x, y, true)) continue;
                    if (last < 0 || y - last > 4)
                    {
                        if (Complete()) return true;
                        first = y;
                    }
                    last = y;
                }
                return Complete();
            });
            var left = vertical.Where(x => x < cursor.X).DefaultIfEmpty(-1).Max();
            var right = vertical.Where(x => x > cursor.X).DefaultIfEmpty(-1).Min();
            if (left < 0 || right - left < 40)
                return null;
            var horizontal = ThinRules(height, y =>
            {
                if (!IsRule(left + 3, y, false) || !IsRule(right - 3, y, false)) return false;
                var count = 0;
                var total = 0;
                for (var x = left + 3; x < right - 2; x += 3)
                {
                    if (IsRule(x, y, false)) count++;
                    total++;
                }
                return total > 0 && count * 100 >= total * 60;
            });
            var top = horizontal.Where(y => y < cursor.Y).DefaultIfEmpty(-1).Max();
            var bottom = horizontal.Where(y => y > cursor.Y).DefaultIfEmpty(-1).Min();
            if (bottom < 0 && top >= 0 && height - top is >= 12 and <= 140)
                bottom = height;
            if (top < 0 || bottom - top is < 12 or > 140)
                return null;
            return Rectangle.FromLTRB(left + 2, top + 2, right - 1, bottom - 1);

            bool IsTableBoundary(int x, int first, int last)
            {
                var crosses = ThinRules(height, y => y >= first - 4 && y <= last + 4
                    && (IsRule(x, y - 4, true) || IsRule(x, y + 4, true))
                    && (IsRule(x - 4, y, false) || IsRule(x + 4, y, false)));
                var leftEnds = 0;
                var rightEnds = 0;
                foreach (var y in crosses)
                {
                    var leftSide = IsRule(x - 4, y, false);
                    var rightSide = IsRule(x + 4, y, false);
                    if (leftSide && rightSide) return true;
                    if (leftSide) leftEnds++;
                    if (rightSide) rightEnds++;
                }
                var clipped = IsRule(x, 2, true) || IsRule(x, height - 3, true);
                return leftEnds >= (clipped ? 1 : 2) && rightEnds == 0 || rightEnds >= (clipped ? 1 : 2) && leftEnds == 0;
            }

            bool IsRule(int x, int y, bool vertical)
            {
                var deltaX = vertical ? 2 : 0;
                var deltaY = vertical ? 0 : 2;
                if (x < deltaX || y < deltaY || x + deltaX >= width || y + deltaY >= height)
                    return false;
                var center = (byte*)data.Scan0 + y * data.Stride + x * 4;
                var before = (byte*)data.Scan0 + (y - deltaY) * data.Stride + (x - deltaX) * 4;
                var after = (byte*)data.Scan0 + (y + deltaY) * data.Stride + (x + deltaX) * 4;
                var first = 0;
                var second = 0;
                for (var channel = 0; channel < 3; channel++)
                {
                    first = Math.Max(first, Math.Abs(center[channel] - before[channel]));
                    second = Math.Max(second, Math.Abs(center[channel] - after[channel]));
                }
                return first >= 6 && second >= 6;
            }

        }
        finally { image.UnlockBits(data); }

        static int[] ThinRules(int length, Func<int, bool> matches)
        {
            var result = new List<int>();
            for (var offset = 0; offset < length; offset++)
            {
                if (!matches(offset)) continue;
                var start = offset;
                while (offset + 1 < length && matches(offset + 1)) offset++;
                if (offset - start < 5) result.Add((start + offset) / 2);
            }
            return result.ToArray();
        }
    }

    internal static bool HasCodeContext(ScreenLine[] rows, Point cursor) => rows.Any(row =>
        RectangleF.Inflate(row.Bounds, 12, 12).Contains(cursor)
        && row.Text.Any(letter => letter is '-' or '_' or '–' or '—') && row.Text.Length >= 8
        && row.Text.All(letter => char.IsAsciiLetterOrDigit(letter) || char.IsWhiteSpace(letter)
            || char.IsPunctuation(letter) || char.IsSymbol(letter)))
        || rows.Any(row => RectangleF.Inflate(row.Bounds, 12, 12).Contains(cursor)
            && row.Words.Any(word => word.Text.Length is >= 2 and <= 5 && word.Text.All(char.IsAsciiLetter))
            && row.Text.Count(char.IsAsciiDigit) >= 2
            && row.Text.Count(letter => char.IsPunctuation(letter) || char.IsSymbol(letter)) >= 2);
}

internal static partial class TextSelection
{
    [GeneratedRegex(@"^(?:[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+|(?=[A-Za-z0-9_.-]*[-_])[A-Za-z0-9][A-Za-z0-9_.-]*\.[A-Za-z][A-Za-z0-9]{1,7})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex StructuredCode();

    internal static bool IsStructuredCode(string text) => StructuredCode().IsMatch(text);
}
