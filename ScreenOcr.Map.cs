using System.Drawing.Imaging;

namespace ScreenTranslator;

internal sealed partial class ScreenOcr
{
    private static bool HasMapLabelContext(ScreenLine[] rows, Point cursor) =>
        TextSelection.At(rows, cursor, true, null) is not { } paragraph
        || paragraph.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4;

    private bool HasUncertainLabel(ScreenLine[] rows, Point cursor) => rows.Any(row => row.Words.Length <= 4
        && RectangleF.Inflate(row.Bounds, Math.Max(12, row.Bounds.Height * 4), Math.Max(12, row.Bounds.Height * .5f)).Contains(cursor)
        && row.Text.Count(char.IsAsciiLetter) >= 4
        && System.Text.RegularExpressions.Regex.Matches(row.Text, @"[A-Za-z]{4,}").Any(match =>
            GenshinMapLabels.MightBeLabel(match.Value) || GenshinMapLabels.IsLabelFragment(match.Value)
            || GenshinMapLabels.HasLabelPrefix(match.Value)));

    private static ScreenLine? IsolatedMapLabel(ScreenLine[] rows, Point cursor)
    {
        if (GenshinMapLabels.At(rows, cursor) is not { } label)
            return null;
        return rows.SelectMany(row => row.Words).Any(word => word.Text.Any(char.IsAsciiLetter)
            && Math.Abs(word.Bounds.Top + word.Bounds.Height / 2 - label.Bounds.Top - label.Bounds.Height / 2)
                <= Math.Max(word.Bounds.Height, label.Bounds.Height) * .6f
            && !label.Bounds.IntersectsWith(word.Bounds)
            && Math.Max(label.Bounds.Left - word.Bounds.Right, word.Bounds.Left - label.Bounds.Right)
                < label.Bounds.Height * 2) ? null : label;
    }

    private ScreenLine? ReadMapLatin(Bitmap image, Point origin, Point cursor, CancellationToken token, ScreenLine[] source)
    {
        var anchor = source.Where(row => row.Text.Count(char.IsAsciiLetter) >= 4
                && RectangleF.Inflate(row.Bounds, Math.Max(16, row.Bounds.Height * 4),
                Math.Max(16, row.Bounds.Height * .5f)).Contains(cursor))
            .MinBy(row => Math.Abs(row.Bounds.Top + row.Bounds.Height / 2 - cursor.Y)
                + Math.Max(0, Math.Max(row.Bounds.Left - cursor.X, cursor.X - row.Bounds.Right)) * .5f);
        if (anchor is null)
            return null;
        var hovered = TextSelection.At([anchor], cursor, false, null);
        var reference = hovered is not null && hovered.Text.Count(char.IsLetter) >= 3 ? hovered.Bounds
            : anchor.Words.Where(word => word.Text.Count(char.IsLetter) >= 3)
                .MinBy(word => Math.Abs(word.Bounds.Top + word.Bounds.Height / 2 - cursor.Y))?.Bounds ?? anchor.Bounds;
        var letterHeight = reference.Height;
        var votes = new Dictionary<string, int>();
        foreach (var singleLine in new[] { true, false })
        {
            var bounds = anchor.Bounds;
            if (singleLine)
                bounds = RectangleF.FromLTRB(bounds.Left, reference.Top, bounds.Right, reference.Bottom);
            var context = bounds.Width < letterHeight * 6 ? 12 : 2;
            var padded = RectangleF.Inflate(bounds, letterHeight * context, letterHeight * (singleLine ? .4f : 1.8f));
            padded.Offset(-origin.X, -origin.Y);
            var crop = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size), Rectangle.Ceiling(padded));
            using var strip = image.Clone(crop, PixelFormat.Format32bppArgb);
            var start = new Point(origin.X + crop.X, origin.Y + crop.Y);
            foreach (var floor in new[] { 0, 100, 160, 190 })
                foreach (var targetHeight in new[] { 32f, 48f, 64f })
                {
                    var reading = latinOcr.Value.Read(strip, start, targetHeight / Math.Max(1, letterHeight), token,
                        singleLine: singleLine, lightFloor: floor);
                    if (Vote(reading.Rows) is { } label) return label;
                }
            using (var contrast = EnhanceLocalContrast(strip, token))
                foreach (var floor in new[] { 0, 190 })
                    foreach (var targetHeight in new[] { 32f, 48f, 64f })
                    {
                        var reading = latinOcr.Value.Read(contrast, start, targetHeight / Math.Max(1, letterHeight), token,
                            singleLine: singleLine, lightFloor: floor);
                        if (Vote(reading.Rows) is { } label) return label;
                    }
            for (var channel = 0; channel < 3; channel++)
            {
                using var contrast = ConvertPixels(strip, token, (red, green, blue) => channel == 0 ? red : channel == 1 ? green : blue);
                foreach (var targetHeight in new[] { 32f, 48f, 64f })
                {
                    var reading = latinOcr.Value.Read(contrast, start, targetHeight / Math.Max(1, letterHeight), token, singleLine: singleLine);
                    if (Vote(reading.Rows) is { } label) return label;
                }
            }
        }
        return null;

        ScreenLine? Vote(ScreenLine[] rows)
        {
            if (GenshinMapLabels.At(rows, cursor) is not { } label) return null;
            var confidence = rows.SelectMany(row => row.Words).Where(word => word.Bounds.IntersectsWith(label.Bounds)
                    && word.Text.Count(char.IsLetter) >= 3)
                .Select(word => float.IsNaN(word.LetterConfidence) ? word.Confidence : word.LetterConfidence).DefaultIfEmpty(0).Average();
            if (float.IsNaN(confidence) || confidence < .45f) return null;
            var previous = votes.GetValueOrDefault(label.Text);
            votes[label.Text] = previous + 1;
            return previous >= 1 ? label : null;
        }
    }
}
