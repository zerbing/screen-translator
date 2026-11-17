using System.Drawing.Imaging;

namespace ScreenTranslator;

internal sealed partial class ScreenOcr
{
    private static float Chroma(Color color)
    {
        var maximum = Math.Max(color.R, Math.Max(color.G, color.B));
        var minimum = Math.Min(color.R, Math.Min(color.G, color.B));
        return maximum == 0 ? 0 : (float)(maximum - minimum) / maximum;
    }

    private static unsafe Color[] SampleTextPalette(Bitmap image, Point origin, RectangleF paragraph, ScreenLine[] rows)
    {
        var relevant = rows.Where(row => paragraph.IntersectsWith(row.Bounds)).ToArray();
        var heights = relevant.SelectMany(row => row.Words).Where(word => word.Text.Count(char.IsLetter) >= 2)
            .Select(word => word.Bounds.Height).Order().ToArray();
        var padding = Math.Clamp((int)((heights.Length == 0 ? 16 : heights[heights.Length / 2]) * .45f), 6, 32);
        paragraph.Offset(-origin.X, -origin.Y);
        var area = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size), Rectangle.Inflate(Rectangle.Ceiling(paragraph), padding, padding));
        if (area.IsEmpty) return [];
        var bands = new bool[image.Height];
        foreach (var row in relevant)
        {
            var letters = row.Words.Where(word => word.Text.Count(char.IsLetter) >= 2).ToArray();
            if (letters.Length == 0) continue;
            var tops = letters.Select(word => word.Bounds.Top).Order().ToArray();
            var bottoms = letters.Select(word => word.Bounds.Bottom).Order().ToArray();
            var top = Math.Clamp((int)tops[tops.Length / 2] - origin.Y, 0, image.Height);
            var bottom = Math.Clamp((int)Math.Ceiling(bottoms[bottoms.Length / 2]) - origin.Y, top, image.Height);
            for (var y = top; y < bottom; y++) bands[y] = true;
        }
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var channels = new[] { new int[256], new int[256], new int[256] };
            var outsideChannels = new[] { new int[256], new int[256], new int[256] };
            var colors = new Dictionary<int, (int Inside, int Outside, int Edges)>();
            var insideSamples = 0;
            var outsideSamples = 0;
            ReadOnlySpan<(int X, int Y)> neighbors = [(-3, 0), (3, 0), (0, -3), (0, 3)];
            for (var y = area.Top; y < area.Bottom; y += 2)
                for (var x = area.Left; x < area.Right; x += 2)
                {
                    var pixel = (byte*)data.Scan0 + y * data.Stride + x * 4;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        channels[channel][pixel[channel]]++;
                        if (!bands[y]) outsideChannels[channel][pixel[channel]]++;
                    }
                    if (bands[y]) insideSamples++; else outsideSamples++;
                    var contrast = 0;
                    foreach (var (dx, dy) in neighbors)
                    {
                        var nextX = x + dx;
                        var nextY = y + dy;
                        if (nextX < area.Left || nextX >= area.Right || nextY < area.Top || nextY >= area.Bottom) continue;
                        var next = (byte*)data.Scan0 + nextY * data.Stride + nextX * 4;
                        for (var channel = 0; channel < 3; channel++)
                            contrast = Math.Max(contrast, Math.Abs(pixel[channel] - next[channel]));
                    }
                    var key = pixel[2] / 16 << 8 | pixel[1] / 16 << 4 | pixel[0] / 16;
                    var counts = colors.GetValueOrDefault(key);
                    if (bands[y]) counts.Inside++; else counts.Outside++;
                    if (bands[y] && contrast >= 30) counts.Edges++;
                    colors[key] = counts;
                }
            var background = (outsideSamples == 0 ? channels : outsideChannels).Select(histogram =>
            {
                var middle = histogram.Sum() / 2;
                var median = 0;
                while (median < 255 && (middle -= histogram[median]) > 0) median++;
                return median;
            }).ToArray();
            var groups = new List<(Color Color, int Count)>();
            foreach (var entry in colors.Where(entry => entry.Value.Edges >= 3
                    && (entry.Value.Inside + 1f) * (outsideSamples + 1) >= (entry.Value.Outside + 1f) * (insideSamples + 1) * 1.5f)
                .OrderByDescending(entry => entry.Value.Edges))
            {
                var color = Color.FromArgb((entry.Key >> 8) * 16 + 8, (entry.Key >> 4 & 15) * 16 + 8, (entry.Key & 15) * 16 + 8);
                if (Math.Max(Math.Abs(color.R - background[2]), Math.Max(Math.Abs(color.G - background[1]), Math.Abs(color.B - background[0]))) < 60)
                    continue;
                var index = groups.FindIndex(group => Math.Max(Math.Abs(group.Color.R - color.R),
                    Math.Max(Math.Abs(group.Color.G - color.G), Math.Abs(group.Color.B - color.B))) <= 40);
                if (index < 0) groups.Add((color, entry.Value.Edges));
                else groups[index] = (groups[index].Color, groups[index].Count + entry.Value.Edges);
            }
            return groups.OrderByDescending(group => group.Count).Take(8).Select(group => group.Color).ToArray();
        }
        finally { image.UnlockBits(data); }
    }
}
