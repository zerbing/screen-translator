namespace ScreenTranslator;

internal static partial class TextSelection
{
    private static string? RepeatedItemText(ScreenLine[] block)
    {
        var items = block.Select((line, index) =>
        {
            var words = line.Words;
            var marker = words.Length >= 4 && words[0].Text.Count(char.IsLetterOrDigit) <= 1
                && words[0].Bounds.Width <= line.Bounds.Height * 1.1f
                && words[1].Bounds.Left - words[0].Bounds.Right >= line.Bounds.Height * .3f;
            var body = marker ? words.Skip(1).ToArray() : words;
            return (line, index, body, marker, prefix: string.Join(' ',
                EnglishWord().Matches(string.Join(' ', body.Select(word => word.Text))).Take(3)
                    .Select(word => word.Value.ToLowerInvariant())));
        }).ToArray();
        var repeated = items.Where(item => item.prefix.Count(letter => letter == ' ') >= 1)
            .GroupBy(item => item.prefix).Where(group => group.Count() >= 3)
            .OrderByDescending(group => group.Count()).FirstOrDefault();
        if (repeated is null)
            return null;
        var starts = repeated.OrderBy(item => item.index).ToArray();
        var anchor = starts[0];
        if (starts.Any(item => Math.Abs(item.body[0].Bounds.Left - anchor.body[0].Bounds.Left) > anchor.line.Bounds.Height * .35f)
            || starts.Zip(starts.Skip(1), (first, next) => next.index - first.index).Any(step => step is < 1 or > 3))
            return null;
        var parts = new List<string>();
        if (starts[0].index > 0)
            parts.Add(string.Join(' ', block.Take(starts[0].index).Select(line => line.Text)));
        for (var index = 0; index < starts.Length; index++)
        {
            var end = index + 1 < starts.Length ? starts[index + 1].index : block.Length;
            var heading = string.Join(' ', starts[index].body.Select(word => word.Text));
            parts.Add(string.Join(' ', new[] { heading }.Concat(block[(starts[index].index + 1)..end].Select(line => line.Text))));
        }
        return string.Join('\n', parts);
    }

    internal static ScreenLine[] MergeInlineFragments(ScreenLine[] source)
    {
        source = source.SelectMany(line =>
        {
            var letters = line.Words.Where(word => word.Text.Any(char.IsLetter)).ToArray();
            if (letters.Length < 2)
                return new[] { line };
            var tallest = letters.Max(word => word.Bounds.Height);
            var shortest = letters.Min(word => word.Bounds.Height);
            if (shortest >= tallest * .58f || letters.Max(word => word.Bounds.Top) - letters.Min(word => word.Bounds.Top) > tallest * .2f
                || letters.Max(word => word.Bounds.Bottom) - letters.Min(word => word.Bounds.Bottom) <= tallest * .4f)
                return new[] { line };
            return line.Words.GroupBy(word => word.Bounds.Height < tallest * .58f)
                .Select(group => new ScreenLine(group.ToArray(), group.Select(word => word.Bounds).Aggregate(RectangleF.Union)));
        }).ToArray();
        var rows = source.Select(line =>
        {
            var words = line.Words;
            if (words.Length < 3 || words[0].Text.Length != 1
                || words[0].Bounds.Width < line.Bounds.Height * .6f
                || words[1].Bounds.Left - words[0].Bounds.Right < line.Bounds.Height * .3f
                || !source.Any(other => other != line
                    && Math.Abs(other.Bounds.Left - words[1].Bounds.Left) <= line.Bounds.Height * .2f
                    && other.Bounds.Top >= line.Bounds.Top + line.Bounds.Height * .65f
                    && other.Bounds.Top - line.Bounds.Bottom <= line.Bounds.Height * .5f))
                return line;
            var body = words.Skip(1).ToArray();
            return new ScreenLine(body, body.Select(word => word.Bounds).Aggregate(RectangleF.Union));
        }).ToList();
        for (var index = 0; index < rows.Count; index++)
        {
            var first = rows[index];
            for (var other = index + 1; other < rows.Count; other++)
            {
                var second = rows[other];
                if (CommandRow().IsMatch(first.Text) || CommandRow().IsMatch(second.Text))
                    continue;
                var height = Math.Max(first.Bounds.Height, second.Bounds.Height);
                var overlap = Math.Min(first.Bounds.Bottom, second.Bounds.Bottom) - Math.Max(first.Bounds.Top, second.Bounds.Top);
                var left = first.Bounds.Left <= second.Bounds.Left ? first : second;
                var right = left == first ? second : first;
                var gap = right.Bounds.Left - left.Bounds.Right;
                if (gap < -1 || gap > height * .85f
                    || Math.Min(first.Bounds.Height, second.Bounds.Height) < height * .45f
                    || Math.Abs(first.Bounds.Bottom - second.Bounds.Bottom) > height * .3f
                    || overlap < Math.Min(first.Bounds.Height, second.Bounds.Height) * .65f
                    || Math.Abs(first.Bounds.Top + first.Bounds.Height / 2 - second.Bounds.Top - second.Bounds.Height / 2) > height * .4f)
                    continue;
                var words = ScreenOcr.BuildLines(left.Words.Concat(right.Words)).SelectMany(line => line.Words).ToArray();
                rows[index] = new ScreenLine(words, RectangleF.Union(first.Bounds, second.Bounds));
                rows.RemoveAt(other);
                index--;
                break;
            }
        }
        return rows.ToArray();
    }
}
