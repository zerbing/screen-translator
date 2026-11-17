using System.Drawing.Imaging;

namespace ScreenTranslator;

internal sealed partial class ScreenOcr
{
    private static unsafe ScreenLine[] RestoreObservedListMarkers(Bitmap image, Point origin, ScreenLine[] rows)
    {
        var found = new List<(ScreenLine Row, RectangleF Marker)>();
        ReadOnlySpan<(int X, int Y)> neighbors = [(-1, 0), (1, 0), (0, -1), (0, 1)];
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            foreach (var row in rows)
            {
                if (TextSelection.ListItemParts(row.Text).Prefix.Length != 0
                    || row.Text.Count(char.IsLetter) < 6 || row.Words.Length < 2) continue;
                var height = row.Words.Where(word => word.Text.Count(char.IsLetter) >= 2)
                    .Select(word => word.Bounds.Height).Order().ToArray();
                if (height.Length == 0) continue;
                var glyph = height[height.Length / 2];
                var region = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size), Rectangle.Ceiling(
                    RectangleF.FromLTRB(row.Bounds.Left - origin.X - glyph * 2.5f, row.Bounds.Top - origin.Y,
                        row.Bounds.Left - origin.X - glyph * .2f, row.Bounds.Bottom - origin.Y)));
                if (region.Width < 3 || region.Height < 3) continue;
                var samples = new List<Color>();
                foreach (var x in new[] { region.Left, (region.Left + region.Right) / 2, region.Right - 1 })
                    foreach (var y in new[] { region.Top, (region.Top + region.Bottom) / 2, region.Bottom - 1 })
                    {
                        var pixel = (byte*)data.Scan0 + y * data.Stride + x * 4;
                        samples.Add(Color.FromArgb(pixel[2], pixel[1], pixel[0]));
                    }
                var background = new[] { samples.Select(color => color.B).Order().ElementAt(4),
                    samples.Select(color => color.G).Order().ElementAt(4), samples.Select(color => color.R).Order().ElementAt(4) };
                var visited = new bool[region.Width * region.Height];
                var queue = new int[visited.Length];
                for (var start = 0; start < visited.Length; start++)
                {
                    if (visited[start]) continue;
                    visited[start] = true;
                    if (!Ink(start)) continue;
                    var count = 1;
                    queue[0] = start;
                    var left = start % region.Width;
                    var right = left;
                    var top = start / region.Width;
                    var bottom = top;
                    for (var index = 0; index < count; index++)
                    {
                        var x = queue[index] % region.Width;
                        var y = queue[index] / region.Width;
                        left = Math.Min(left, x); right = Math.Max(right, x);
                        top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                        foreach (var (dx, dy) in neighbors)
                        {
                            var nx = x + dx; var ny = y + dy;
                            if (nx < 0 || nx >= region.Width || ny < 0 || ny >= region.Height) continue;
                            var next = ny * region.Width + nx;
                            if (visited[next]) continue;
                            visited[next] = true;
                            if (Ink(next)) queue[count++] = next;
                        }
                    }
                    var width = right - left + 1;
                    var size = bottom - top + 1;
                    if (left == 0 || top == 0 || right == region.Width - 1 || bottom == region.Height - 1
                        || width < Math.Max(2, glyph * .1f) || size < Math.Max(2, glyph * .1f)
                        || width > glyph * .65f || size > glyph * .65f
                        || width > size * 1.7f || size > width * 1.7f || count < width * size * .5f) continue;
                    // A marker is an isolated glyph, not punctuation attached to a word
                    // from the neighboring column.
                    var padding = Math.Max(1, (int)(glyph * .15f));
                    if (Enumerable.Range(Math.Max(0, left - padding), Math.Min(region.Width - 1, right + padding) - Math.Max(0, left - padding) + 1)
                        .Any(x => Enumerable.Range(Math.Max(0, top - padding), Math.Min(region.Height - 1, bottom + padding) - Math.Max(0, top - padding) + 1)
                            .Any(y => (x < left || x > right || y < top || y > bottom) && Ink(y * region.Width + x)))) continue;
                    found.Add((row, new RectangleF(origin.X + region.Left + left, origin.Y + region.Top + top, width, size)));
                }

                bool Ink(int index)
                {
                    var pixel = (byte*)data.Scan0 + (region.Top + index / region.Width) * data.Stride
                        + (region.Left + index % region.Width) * 4;
                    return Math.Max(Math.Abs(pixel[0] - background[0]), Math.Max(Math.Abs(pixel[1] - background[1]),
                        Math.Abs(pixel[2] - background[2]))) >= 48;
                }
            }
        }
        finally { image.UnlockBits(data); }
        if (found.Count < 2) return rows;
        var recovered = rows.Select(row =>
        {
            var marker = found.Where(item => item.Row == row && found.Any(other => other.Row != row
                    && Math.Abs(other.Marker.Left - item.Marker.Left) <= row.Bounds.Height * .25f
                    && Math.Abs(other.Row.Bounds.Left - row.Bounds.Left) <= row.Bounds.Height * .6f))
                .OrderBy(item => row.Bounds.Left - item.Marker.Right).FirstOrDefault();
            return marker;
        }).Where(item => item.Row is not null).ToArray();
        if (recovered.Length == 0) return rows;
        return rows.Select(row =>
        {
            var words = row.Words.Where(word => !recovered.Any(item => item.Row != row
                && word.Text.Length <= 2 && word.Bounds.IntersectsWith(item.Marker)
                && word.Bounds.Width * word.Bounds.Height <= item.Marker.Width * item.Marker.Height * 4)).ToArray();
            if (words.Length == 0) return null;
            var bounds = words.Select(word => word.Bounds).Aggregate(RectangleF.Union);
            var marker = recovered.FirstOrDefault(item => item.Row == row);
            return marker.Row is null ? new ScreenLine(words, bounds)
                : new ScreenLine([new ScreenWord("•", marker.Marker), .. words], RectangleF.Union(bounds, marker.Marker));
        }).OfType<ScreenLine>().ToArray();
    }
}
