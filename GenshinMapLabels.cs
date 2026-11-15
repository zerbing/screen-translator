using System.Text;

namespace ScreenTranslator;

internal static class GenshinMapLabels
{
    private static readonly Lazy<(string Text, string Key)[]> Names = new(Load);

    internal static bool MightBeLabel(string text)
    {
        var key = Key(text);
        return key.Length >= 6 && Names.Value.Any(name =>
            Math.Abs(key.Length - name.Key.Length) <= 3 && EditDistance(key, name.Key) <= 3
            || key.Length >= name.Key.Length * .5f && name.Key.Contains(key, StringComparison.Ordinal));
    }

    internal static bool IsKnownLabel(string text) => Names.Value.Any(name => name.Text == text);

    internal static bool IsLabelFragment(string text)
    {
        var key = Key(text);
        return key.Length >= 3 && Names.Value.Any(name => name.Key.Contains(key, StringComparison.Ordinal));
    }

    public static ScreenLine? At(ScreenLine[] source, Point cursor)
    {
        var words = source.SelectMany(line => line.Words)
            .Where(word => Math.Abs(cursor.Y - word.Bounds.Top - word.Bounds.Height / 2) <= 125
                && word.Bounds.Height is >= 8 and <= 110).ToArray();
        // Map icons can lead OCR to put adjacent parts of a label in separate lines.
        var rows = new List<List<ScreenWord>>();
        foreach (var word in words.OrderBy(word => word.Bounds.Top + word.Bounds.Height / 2))
        {
            var row = rows.LastOrDefault();
            if (row is null || CenterY(word.Bounds) - CenterY(row[0].Bounds) > Math.Min(word.Bounds.Height, row[0].Bounds.Height) * 0.38f)
                rows.Add([word]);
            else
                row.Add(word);
        }

        var spans = new List<ScreenWord>();
        foreach (var row in rows)
        {
            var ordered = row.OrderBy(word => word.Bounds.Left).ToArray();
            for (var start = 0; start < ordered.Length; start++)
            {
                var span = ordered[start];
                spans.Add(span);
                for (var next = start + 1; next < ordered.Length && next < start + 5; next++)
                {
                    var word = ordered[next];
                    if (word.Bounds.Left - span.Bounds.Right > Math.Max(12, word.Bounds.Height * 0.85f))
                        break;
                    span = new ScreenWord(span.Text + " " + word.Text, RectangleF.Union(span.Bounds, word.Bounds));
                    spans.Add(span);
                }
            }
        }

        ScreenWord? best = null;
        var bestScore = float.PositiveInfinity;
        foreach (var first in spans)
        {
            Check(first.Text, first.Bounds, first.Bounds.Height);
            foreach (var second in spans)
            {
                var gap = second.Bounds.Top - first.Bounds.Bottom;
                var height = Math.Max(first.Bounds.Height, second.Bounds.Height);
                if (CenterY(second.Bounds) - CenterY(first.Bounds) < height * 0.66f
                    || gap < -height * 0.5f || gap > height * 0.7f
                    || Math.Abs(CenterX(second.Bounds) - CenterX(first.Bounds)) > height * 0.68f)
                    continue;
                Check(first.Text + " " + second.Text, RectangleF.Union(first.Bounds, second.Bounds), height);
            }
        }
        return best is null ? null : new ScreenLine([best], best.Bounds);

        void Check(string raw, RectangleF bounds, float height)
        {
            var observed = Key(raw);
            if (observed.Length < 6 || observed.Length > 26)
                return;
            var allowed = observed.Length >= 13 ? 3 : observed.Length >= 8 ? 2 : 1;
            foreach (var (text, key) in Names.Value)
            {
                if (Math.Abs(observed.Length - key.Length) > Math.Max(allowed, 3))
                    continue;
                var distance = EditDistance(observed, key);
                var area = bounds;
                if (distance > allowed)
                {
                    // A marker can cover the tail of a name. Require most of a distinctive prefix.
                    if (observed.Length < 6 || key.Length - observed.Length is < 2 or > 3
                        || EditDistance(observed, key[..observed.Length]) > 1)
                        continue;
                    area.Width += (key.Length - observed.Length) * (bounds.Width / observed.Length);
                    distance = allowed + 1;
                }
                if (distance > 0 && !observed.StartsWith(key[..2], StringComparison.Ordinal))
                    continue;
                var dx = Math.Max(0, Math.Max(area.Left - cursor.X, cursor.X - area.Right));
                var dy = Math.Max(0, Math.Max(area.Top - cursor.Y, cursor.Y - area.Bottom));
                if (dx > Math.Clamp(height * 0.14f, 4f, 8f) || dy > 8)
                    continue;
                var score = distance + (dx + dy * 2) / 10 - key.Length * 0.015f;
                if (score < bestScore)
                {
                    best = new ScreenWord(text, area);
                    bestScore = score;
                }
            }
        }
    }

    private static float CenterX(RectangleF bounds) => bounds.Left + bounds.Width / 2;
    private static float CenterY(RectangleF bounds) => bounds.Top + bounds.Height / 2;

    private static (string Text, string Key)[] Load()
    {
        using var reader = new StreamReader(TermGlossary.Open("Glossaries.GenshinMapLabels.tsv"));
        var names = new List<(string, string)>();
        while (reader.ReadLine() is { } line)
            if (line.Length != 0 && !line.StartsWith('#'))
                names.Add((line, Key(line)));
        return names.ToArray();
    }

    private static string Key(string text) => new string(text.Normalize(NormalizationForm.FormD)
        .Where(char.IsAsciiLetter)
        .Select(char.ToLowerInvariant).ToArray()).Replace("rn", "m", StringComparison.Ordinal);

    private static int EditDistance(string left, string right)
    {
        Span<int> row = stackalloc int[right.Length + 1];
        for (var column = 0; column < row.Length; column++)
            row[column] = column;
        for (var index = 0; index < left.Length; index++)
        {
            var diagonal = row[0];
            row[0] = index + 1;
            for (var column = 1; column < row.Length; column++)
            {
                var previous = row[column];
                row[column] = Math.Min(Math.Min(row[column - 1], previous) + 1,
                    diagonal + (left[index] == right[column - 1] ? 0 : 1));
                diagonal = previous;
            }
        }
        return row[^1];
    }
}
