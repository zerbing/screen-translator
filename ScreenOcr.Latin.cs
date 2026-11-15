using System.Drawing.Imaging;

namespace ScreenTranslator;

internal sealed partial class ScreenOcr
{
    private ScreenLine[]? ReadLocalMixedLine(Bitmap image, Point origin, Point cursor, ScreenLine[] rows,
        CancellationToken token)
    {
        var current = TextSelection.At(rows, cursor, false, null);
        var row = rows.Where(line => ContainsMixedTextAt(line, cursor)).MinBy(line => Math.Abs(line.Bounds.Top - cursor.Y));
        row ??= rows.Where(line => RectangleF.Inflate(line.Bounds, 10, 10).Contains(cursor))
            .MinBy(line => Math.Abs(line.Bounds.Top - cursor.Y));
        if (row is null && rows.Length != 0 || rows.Any(line => AppFileName().IsMatch(line.Text)))
            return null;
        var hyphen = row?.Text.Contains('-') == true;
        if (current is not null && !hyphen && (current.Text.Length >= 4 && current.Text.All(char.IsAsciiLetter)
            && spelling.FrequencyRank(current.Text) is > 0 and <= 50000
            || current.Text.Length is >= 2 and <= 4 && IsUppercaseWord(current.Text)))
            return null;
        var area = row is null ? Rectangle.Intersect(new Rectangle(Point.Empty, image.Size),
            new Rectangle(0, cursor.Y - origin.Y - 22, image.Width, 44))
            : Rectangle.Intersect(new Rectangle(Point.Empty, image.Size), Rectangle.FromLTRB(
                (int)row.Bounds.Left - origin.X - 6, (int)row.Bounds.Top - origin.Y - 6,
                (int)Math.Ceiling(row.Bounds.Right) - origin.X + 6, (int)Math.Ceiling(row.Bounds.Bottom) - origin.Y + 6));
        if (area.Width <= 0 || area.Height <= 0)
            return null;
        using var strip = image.Clone(area, PixelFormat.Format32bppArgb);
        var readings = new List<(string? Text, ScreenLine[] Rows)>();
        foreach (var height in new[] { 32f, 40f, 48f, 64f, 80f })
        {
            var read = mixedLatinOcr.Value.Read(strip, new Point(origin.X + area.X, origin.Y + area.Y),
                height / area.Height, token);
            if (read.Confidence < .72f || !read.Rows.Any(line => line.Text.Any(IsHan)))
                continue;
            var hit = TextSelection.At(read.Rows, cursor, false, null);
            if (hit is null && !(current?.Text.Contains('-') == true
                && read.Rows.SelectMany(line => line.Words).Any(word => word.Text.Contains('-')
                    && word.Bounds.Left > cursor.X)))
                continue;
            if (hit is not null && !hit.Text.All(letter => char.IsAsciiLetter(letter) || letter is '-' or '\''))
                continue;
            readings.Add((hit?.Text, read.Rows));
        }
        var best = readings.GroupBy(read => read.Text).Where(group => group.Count() >= 2)
            .OrderByDescending(group => group.Key?.Contains('-') == true).ThenByDescending(group => group.Count()).FirstOrDefault();
        if (best is null || best.Key == current?.Text)
            return null;
        return PreserveWordCase(best.First().Rows, current);
    }

    private ScreenLine[]? ReadMixedLatinBridge(Bitmap image, Point origin, Point cursor, ScreenLine[] lines,
        CancellationToken token)
    {
        foreach (var row in lines.Where(line => ContainsMixedTextAt(line, cursor)))
            for (var index = 0; index + 2 < row.Words.Length; index++)
            {
                var left = row.Words[index];
                var middle = row.Words[index + 1];
                var right = row.Words[index + 2];
                var dash = left.Text.EndsWith('-');
                if (!(dash ? left.Text[..^1].All(char.IsAsciiLetter) : left.Text.Length is >= 1 and <= 2
                        && left.Text.All(char.IsAsciiLetter))
                    || !middle.Text.Any(IsHan) || !right.Text.All(char.IsAsciiLetter))
                    continue;
                var hanWidths = row.Words.Where(word => word.Text.Any(IsHan)).Select(word => word.Bounds.Width).Order().ToArray();
                if (!dash && (hanWidths.Length < 2 || middle.Bounds.Width >= hanWidths[hanWidths.Length / 2] * .85f))
                    continue;
                var bounds = RectangleF.Union(left.Bounds, RectangleF.Union(middle.Bounds, right.Bounds));
                if (!RectangleF.Inflate(bounds, 2, 3).Contains(cursor))
                    continue;
                var area = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size), Rectangle.FromLTRB(
                    (int)Math.Floor(bounds.Left) - origin.X, (int)Math.Floor(bounds.Top) - origin.Y - 3,
                    (int)Math.Ceiling(bounds.Right) - origin.X, (int)Math.Ceiling(bounds.Bottom) - origin.Y + 3));
                using var strip = image.Clone(area, PixelFormat.Format32bppArgb);
                var votes = new HashSet<string>();
                foreach (var height in new[] { 24f, 32f, 40f, 48f })
                {
                    var read = latinOcr.Value.Read(strip, new Point(origin.X + area.X, origin.Y + area.Y),
                        height / area.Height, token);
                    var text = string.Concat(read.Rows.SelectMany(line => line.Words).Select(word => word.Text));
                    if (read.Confidence < .6f || !(dash ? HyphenatedWord().IsMatch(text) : text.All(char.IsAsciiLetter))
                        || !text.StartsWith(left.Text, StringComparison.OrdinalIgnoreCase)
                        || !text.EndsWith(right.Text, StringComparison.OrdinalIgnoreCase) || votes.Add(text))
                        continue;
                    var words = row.Words.Take(index).Append(new ScreenWord(text, bounds)).Concat(row.Words.Skip(index + 3)).ToArray();
                    return lines.Select(line => line == row ? row with { Words = words } : line).ToArray();
                }
            }
        return null;
    }

    private ScreenLine[] RefineGameLatin(Bitmap image, Point origin, Point cursor, ScreenLine[] rows,
        bool paragraph, CancellationToken token)
    {
        if (TextSelection.At(rows, cursor, paragraph, null) is not { } selection
            || GenshinMapLabels.IsKnownLabel(selection.Text)
            || rows.Any(row => row.Bounds.IntersectsWith(selection.Bounds) && GenshinMapLabels.IsKnownLabel(row.Text)))
            return rows;
        return rows.Select(row =>
        {
            if (!row.Bounds.IntersectsWith(selection.Bounds) || row.Text.Length < 3)
                return row;
            var padding = Math.Max(3, (int)(row.Bounds.Height * .18f));
            var area = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size), Rectangle.FromLTRB(
                (int)row.Bounds.Left - origin.X - padding, (int)row.Bounds.Top - origin.Y - padding,
                (int)Math.Ceiling(row.Bounds.Right) - origin.X + padding,
                (int)Math.Ceiling(row.Bounds.Bottom) - origin.Y + padding));
            if (area.Width <= 0 || area.Height <= 0)
                return row;
            using var crop = image.Clone(area, PixelFormat.Format32bppArgb);
            var readings = new List<(string Text, ScreenLine[] Rows, float Confidence)>();
            var details = new List<(ScreenLine[] Rows, float Confidence)>();
            using var gold = HasGoldLettering(crop, new Point(crop.Width / 2, crop.Height / 2))
                && SampleLuminance(crop, new Point(crop.Width / 2, crop.Height / 2), 64, 40).Skip(235).Sum() < 6
                ? ConvertPixels(crop, token, (red, green, blue) =>
                    (byte)(255 - Math.Clamp((green - blue - 15) * 255 / 50, 0, 255))) : null;
            foreach (var floor in new[] { 0, 190 })
                foreach (var height in new[] { 32f, 44f })
                {
                    var reading = latinOcr.Value.Read(gold ?? crop, new Point(origin.X + area.X, origin.Y + area.Y),
                        height / area.Height, token, lightFloor: gold is null ? floor : 0);
                    if (reading.Confidence < .84f || reading.Rows.Length == 0)
                        continue;
                    details.Add(reading);
                    var text = string.Join(' ', reading.Rows.Select(line => line.Text));
                    var first = string.Concat(row.Text.Where(letter => !char.IsWhiteSpace(letter)));
                    var second = string.Concat(text.Where(letter => !char.IsWhiteSpace(letter)));
                    if (!IsAmbiguousGlyphCorrection(first, second) && first != second
                        && !(second.Contains("(...)", StringComparison.Ordinal)
                            && second.Length > first.Length && second.Replace("(...)", "", StringComparison.Ordinal)
                                == first.Replace("(...)", "", StringComparison.Ordinal))
                        && !(EditDistance(first.ToLowerInvariant(), second.ToLowerInvariant(), 2) <= 2
                            && row.Words.Any(word => word.Text.Length >= 4 && word.Text.Any(char.IsLetter)
                                && spelling.Find(word.Text.Trim('.', ',', '!', '?', '"')) is null)))
                        continue;
                    if (first.Equals(second, StringComparison.OrdinalIgnoreCase))
                        continue;
                    readings.Add((text, reading.Rows, reading.Confidence));
                }
            var best = readings.GroupBy(reading => reading.Text).FirstOrDefault(group => group.Count() >= 2);
            var initialWords = best is not null ? best.First().Rows.SelectMany(line => line.Words).ToArray() : row.Words;
            var words = initialWords.Select(word =>
            {
                var candidates = details.Select(reading => (reading.Confidence, Word: reading.Rows.SelectMany(line => line.Words)
                        .Where(other => other.Bounds.IntersectsWith(word.Bounds))
                        .MinBy(other => Math.Abs(other.Bounds.Left + other.Bounds.Width / 2 - word.Bounds.Left - word.Bounds.Width / 2))))
                            .Where(candidate => candidate.Word is not null && (candidate.Word.Text == word.Text
                                || SupportsFrequentWordCorrection(word.Text, candidate.Word.Text, candidate.Confidence)
                        || IsAmbiguousGlyphCorrection(word.Text, candidate.Word.Text)
                        || spelling.Find(word.Text.Trim('.', ',', '!', '?', '"')) is null
                            && word.Text.Length >= 4 && EditDistance(word.Text.ToLowerInvariant(),
                                candidate.Word.Text.ToLowerInvariant(), 2) <= 2))
                    .GroupBy(candidate => candidate.Word!.Text).Where(group => group.Count() >= 2)
                    .OrderByDescending(group => group.Count()).ThenByDescending(group => group.Average(candidate => candidate.Confidence))
                    .FirstOrDefault();
                if (candidates is not null && !candidates.Key.Equals(word.Text, StringComparison.OrdinalIgnoreCase))
                    return word with { Text = candidates.Key };
                if (word.Text.Length >= 4 && (word.Text.StartsWith('t')
                    || spelling.FrequencyRank(word.Text.Trim('.', ',', '!', '?', '"')) > 15000
                    || spelling.Find(word.Text.Trim('.', ',', '!', '?', '"')) is null))
                    return ConfirmGameWord(image, origin, word, token) ?? word;
                return word;
            }).ToArray();
            return row with { Words = words };
        }).ToArray();
    }

    private ScreenWord? ConfirmGameWord(Bitmap image, Point origin, ScreenWord word, CancellationToken token)
    {
        var padding = Math.Max(4, (int)(word.Bounds.Height * .25f));
        var area = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size), Rectangle.FromLTRB(
            (int)word.Bounds.Left - origin.X - padding, (int)word.Bounds.Top - origin.Y - padding,
            (int)Math.Ceiling(word.Bounds.Right) - origin.X + padding,
            (int)Math.Ceiling(word.Bounds.Bottom) - origin.Y + padding));
        if (area.Width <= 0 || area.Height <= 0)
            return null;
        using var crop = image.Clone(area, PixelFormat.Format32bppArgb);
        using var gold = HasGoldLettering(crop, new Point(crop.Width / 2, crop.Height / 2))
            && SampleLuminance(crop, new Point(crop.Width / 2, crop.Height / 2), 64, 40).Skip(235).Sum() < 6
            ? ConvertPixels(crop, token, (red, green, blue) =>
                (byte)(255 - Math.Clamp((green - blue - 15) * 255 / 50, 0, 255))) : null;
        var readings = new List<(string Text, float Confidence)>();
        foreach (var floor in new[] { 100, 190 })
            foreach (var height in new[] { 32f, 44f })
            {
                var read = latinOcr.Value.Read(gold ?? crop, new Point(origin.X + area.X, origin.Y + area.Y),
                    height / area.Height, token, lightFloor: gold is null ? floor : 0);
                var text = string.Concat(read.Rows.SelectMany(row => row.Words).Select(item => item.Text));
                if (read.Confidence < .85f || !IsAmbiguousGlyphCorrection(word.Text, text)
                    && !SupportsFrequentWordCorrection(word.Text, text, read.Confidence)
                        && !(word.Text == text || spelling.Find(word.Text) is null
                            && EditDistance(word.Text.ToLowerInvariant(), text.ToLowerInvariant(), 2) <= 2))
                    continue;
                readings.Add((text, read.Confidence));
            }
        var best = readings.GroupBy(reading => reading.Text).Where(group => group.Count() >= 2)
            .OrderByDescending(group => group.Count()).ThenByDescending(group => group.Average(reading => reading.Confidence)).FirstOrDefault();
        return best is null || best.Key.Equals(word.Text, StringComparison.OrdinalIgnoreCase)
            ? null : word with { Text = best.Key };
    }

    private bool SupportsFrequentWordCorrection(string first, string second, float confidence)
    {
        var oldRank = spelling.FrequencyRank(first.Trim('.', ',', '!', '?', '"'));
        var newRank = spelling.FrequencyRank(second.Trim('.', ',', '!', '?', '"'));
        return confidence >= .92f && oldRank > 15000 && newRank > 0 && newRank * 5 < oldRank
            && EditDistance(first.ToLowerInvariant(), second.ToLowerInvariant(), 1) == 1;
    }

    internal static int EditDistance(string first, string second, int limit)
    {
        if (Math.Abs(first.Length - second.Length) > limit)
            return limit + 1;
        var previous = Enumerable.Range(0, second.Length + 1).ToArray();
        for (var left = 1; left <= first.Length; left++)
        {
            var current = new int[second.Length + 1];
            current[0] = left;
            for (var right = 1; right <= second.Length; right++)
                current[right] = Math.Min(Math.Min(previous[right] + 1, current[right - 1] + 1),
                    previous[right - 1] + (first[left - 1] == second[right - 1] ? 0 : 1));
            if (current.Min() > limit)
                return limit + 1;
            previous = current;
        }
        return previous[^1];
    }
}
