using System.Text.RegularExpressions;

namespace ScreenTranslator;

internal static partial class TextSelection
{
    private enum ListKind { Command, Bullet, Numbered, Field }

    private sealed record ListRow(ListKind Kind, string Prefix, string Body, RectangleF Bounds, float BodyLeft,
        float LineHeight, bool ReadSlash = false);

    [GeneratedRegex(@"^(?<slash>[/\[])\s*(?<name>[A-Za-z][A-Za-z0-9_-]*)(?:\s+[-–—]\s*(?<body>.*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CommandRow();

    [GeneratedRegex(@"^(?<marker>[•·▪◦‣*+–-]|\d{1,3}[.)]|[A-Za-z][.)])(?:\s+(?<body>\S.*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex MarkedRow();

    [GeneratedRegex(@"^(?<key>[A-Za-z][A-Za-z0-9_-]{0,19}(?: [A-Za-z][A-Za-z0-9_-]{0,14})?:)\s+(?<body>\S.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex FieldRow();

    [GeneratedRegex(@"^(?:[A-Za-z]:[\\/]\S+|[.~]?[\\/][^\s\\/]+[\\/]\S+)$", RegexOptions.CultureInvariant)]
    internal static partial Regex StandalonePath();

    internal static (string Prefix, string Body) ListItemParts(string text)
    {
        if (StandalonePath().IsMatch(text))
            return (text, "");
        var command = CommandRow().Match(text);
        if (command.Success && command.Groups["slash"].Value == "/")
        {
            var body = command.Groups["body"].Value;
            return ("/" + command.Groups["name"].Value + (body.Length == 0 ? "" : " - "), body);
        }
        var mark = MarkedRow().Match(text);
        return mark.Success && mark.Groups["body"].Length != 0
            ? (mark.Groups["marker"].Value + " ", mark.Groups["body"].Value) : ("", text);
    }

    private static bool IsMetadataBlock(ScreenLine[] lines) =>
        lines.Length is >= 2 and <= 7
        && lines.Any(line => StandalonePath().IsMatch(line.Text))
        && lines.Any(line => FieldRow().IsMatch(line.Text))
        && lines.All(line => StandalonePath().IsMatch(line.Text) || FieldRow().IsMatch(line.Text)
            || line.Words.Length <= 6 && !line.Text.EndsWith('.'));

    private static Selection? ListAt(ScreenLine[] lines, Point cursor)
    {
        var markers = lines.Where(line => line.Words.Length >= 3 && line.Words[0].Text.Length <= 2
            && line.Words[0].Bounds.Width <= line.Bounds.Height * 1.1f
            && (!line.Words[0].Text.Any(char.IsLetterOrDigit) || line.Words[0].Bounds.Height <= line.Bounds.Height * .6f)
            && line.Words[1].Bounds.Left - line.Words[0].Bounds.Right >= line.Bounds.Height * .2f).ToArray();
        lines = lines.Select(line => markers.Contains(line) && markers.Count(other =>
                Math.Abs(other.Bounds.Left - line.Bounds.Left) <= line.Bounds.Height * .4f
                && Math.Abs(other.Words[1].Bounds.Left - line.Words[1].Bounds.Left) <= line.Bounds.Height * .4f) >= 3
            ? line with { Words = line.Words.Select((word, index) => index == 0 ? word with { Text = "•" } : word).ToArray() }
            : line).ToArray();
        var detected = lines.Select(line => ReadListRow(line, lines)).OfType<ListRow>().ToArray();
        var rows = detected.OrderBy(row => row.Bounds.Top).ToArray();
        var hovered = rows.MinBy(row => Distance(row.Bounds, cursor, 12, row.LineHeight * 2f));
        if (hovered is null || !float.IsFinite(Distance(hovered.Bounds, cursor, 12, hovered.LineHeight * 2f)))
            return null;

        var aligned = rows.Where(row => row.Kind == hovered.Kind
            && Math.Abs(row.Bounds.Left - hovered.Bounds.Left) <= hovered.LineHeight * .45f).ToArray();
        var start = Array.IndexOf(aligned, hovered);
        var end = start + 1;
        while (start > 0 && Adjacent(aligned[start - 1], aligned[start]))
            start--;
        while (end < aligned.Length && Adjacent(aligned[end - 1], aligned[end]))
            end++;
        var group = aligned[start..end];
        if (group.Length < 2 && hovered.Kind != ListKind.Bullet
            || hovered.Kind == ListKind.Command && group.Count(row => row.ReadSlash) < 2)
            return null;

        var bounds = group[0].Bounds;
        var containsCursor = group.Any(row => float.IsFinite(Distance(row.Bounds, cursor, 12, row.LineHeight * .45f)));
        var items = new List<string>(group.Length);
        for (var i = 0; i < group.Length; i++)
        {
            var row = group[i];
            var body = row.Body;
            if (body.Length > 0)
            {
                var limit = i + 1 < group.Length ? group[i + 1].Bounds.Top : float.PositiveInfinity;
                var continuation = lines.Where(line => line.Bounds.Top >= row.Bounds.Bottom - row.LineHeight * .1f
                    && line.Bounds.Bottom <= limit + row.LineHeight * .1f
                    && Math.Abs(line.Bounds.Left - row.BodyLeft) <= row.LineHeight * .6f
                    && line.Text.Any(char.IsLetter)
                    && !CommandRow().IsMatch(line.Text) && !MarkedRow().IsMatch(line.Text) && !FieldRow().IsMatch(line.Text))
                    .OrderBy(line => line.Bounds.Top);
                var previousBottom = row.Bounds.Bottom;
                foreach (var line in continuation)
                {
                    if (line.Bounds.Top - previousBottom > Math.Max(line.Bounds.Height, row.LineHeight) * .95f)
                        break;
                    body += " " + line.Text;
                    bounds = RectangleF.Union(bounds, line.Bounds);
                    containsCursor |= float.IsFinite(Distance(line.Bounds, cursor, 12, row.LineHeight * .45f));
                    previousBottom = line.Bounds.Bottom;
                }
            }
            items.Add(body.Length == 0 ? row.Prefix : row.Prefix + (row.Kind == ListKind.Command ? " - " : " ") + body);
            bounds = RectangleF.Union(bounds, row.Bounds);
        }
        if (!containsCursor)
            return null;
        var text = string.Join('\n', items);
        return new Selection(text, text, bounds);

        bool Adjacent(ListRow first, ListRow second)
        {
            var height = Math.Max(first.LineHeight, second.LineHeight);
            var step = second.Bounds.Top - first.Bounds.Top;
            if (step < height * .55f) return false;
            if (step <= height * 3.5f) return true;
            // Long wrapped items stay in the list through their actual continuation
            // lines. Spacing alone is never evidence of an additional bullet.
            var bottom = first.Bounds.Bottom;
            foreach (var line in lines.Where(line => line.Bounds.Top >= first.Bounds.Bottom - height * .1f
                    && line.Bounds.Bottom <= second.Bounds.Top + height * .1f
                    && Math.Abs(line.Bounds.Left - first.BodyLeft) <= height * .6f
                    && line.Text.Any(char.IsLetter)).OrderBy(line => line.Bounds.Top))
            {
                if (line.Bounds.Top - bottom > Math.Max(height, line.Bounds.Height) * .95f) return false;
                bottom = Math.Max(bottom, line.Bounds.Bottom);
            }
            return second.Bounds.Top - bottom <= height * .95f;
        }
    }

    private static ListRow? ReadListRow(ScreenLine line, ScreenLine[] lines)
    {
        var command = CommandRow().Match(line.Text);
        var mark = MarkedRow().Match(line.Text);
        var field = FieldRow().Match(line.Text);
        if (!command.Success && !mark.Success && !field.Success)
            return null;

        var prefix = command.Success ? "/" + command.Groups["name"].Value
            : mark.Success ? mark.Groups["marker"].Value : field.Groups["key"].Value;
        var kind = command.Success ? ListKind.Command : field.Success ? ListKind.Field
            : prefix.Length == 1 ? ListKind.Bullet : ListKind.Numbered;
        var match = command.Success ? command : mark.Success ? mark : field;
        var body = match.Groups["body"].Value.Trim();
        var bodyLeft = line.Words.Length >= 2 ? line.Words[1].Bounds.Left : line.Bounds.Left;
        var bounds = line.Bounds;
        if (body.Length == 0)
        {
            var rowCenter = bounds.Top + bounds.Height / 2;
            var description = lines.Where(other => other.Bounds.Left >= bounds.Right - 1
                && other.Bounds.Left - bounds.Right <= Math.Max(bounds.Height, other.Bounds.Height) * 2.5f
                && Math.Abs(other.Bounds.Top + other.Bounds.Height / 2 - rowCenter) <= other.Bounds.Height * .5f
                && other.Text.Any(char.IsLetter)
                && !CommandRow().IsMatch(other.Text) && !MarkedRow().IsMatch(other.Text))
                .MinBy(other => other.Bounds.Left);
            if (description is not null)
            {
                body = description.Text.TrimStart(' ', '-', '–', '—');
                bodyLeft = description.Bounds.Left;
                bounds = RectangleF.Union(bounds, description.Bounds);
            }
        }
        if (kind == ListKind.Bullet && lines.Any(other => other.Bounds.Left - bounds.Right > bounds.Height * 2
            && Math.Abs(other.Bounds.Top + other.Bounds.Height / 2 - bounds.Top - bounds.Height / 2) <= bounds.Height * .35f
            && other.Text.Any(char.IsLetter)))
            return null;
        return new ListRow(kind, prefix, body, bounds, bodyLeft, bounds.Height, command.Groups["slash"].Value == "/");
    }
}
