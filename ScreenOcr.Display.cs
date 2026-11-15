using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Windows.Media.Ocr;

namespace ScreenTranslator;

internal sealed partial class ScreenOcr
{
    private async Task<ScreenLine[]?> ReadColoredHyphenatedWordAsync(Bitmap image, Point origin, Point local,
        Point cursor, CancellationToken token)
    {
        var imageBounds = new Rectangle(Point.Empty, image.Size);
        var probe = Rectangle.Intersect(imageBounds, new Rectangle(local.X - 20, local.Y - 12, 41, 25));
        var dark = 0;
        var colors = new int[24];
        var samples = 0;
        for (var y = probe.Top; y < probe.Bottom; y += 2)
        for (var x = probe.Left; x < probe.Right; x += 2)
        {
            var pixel = image.GetPixel(x, y);
            var luminance = Luminance(pixel.R, pixel.G, pixel.B);
            if (luminance < 80)
                dark++;
            if (luminance >= 125 && Math.Max(pixel.R, Math.Max(pixel.G, pixel.B))
                - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) >= 38)
                colors[(int)(pixel.GetHue() / 15) % colors.Length]++;
            samples++;
        }
        var hue = Enumerable.Range(0, colors.Length).MaxBy(index => colors[index]);
        if (dark * 100 < samples * 52 || colors[hue] < 7)
            return null;

        var search = Rectangle.Intersect(imageBounds, new Rectangle(local.X - 400, local.Y - 53, 801, 107));
        using var crop = image.Clone(search, PixelFormat.Format32bppArgb);
        using var mask = ConvertPixels(crop, token, (red, green, blue) =>
        {
            if (Luminance(red, green, blue) < 105
                || Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) < 25)
                return 0;
            var difference = Math.Abs(Color.FromArgb(red, green, blue).GetHue() - (hue * 15 + 7.5f));
            return Math.Min(difference, 360 - difference) <= 23 ? (byte)255 : (byte)0;
        });
        var relative = new Point(local.X - search.X, local.Y - search.Y);
        if (FindDisplayLine(mask, relative, maxHeight: 48, gapRatio: .95f, minHeight: 5) is not { } line
            || line.Width < line.Height * 3)
            return null;

        // Include disconnected dots and retain the original antialiased strokes for the English pass.
        var region = Rectangle.Intersect(new Rectangle(Point.Empty, crop.Size),
            Rectangle.Inflate(line, 1, Math.Max(3, line.Height / 3)));
        const int margin = 8;
        using var lettering = new Bitmap(region.Width + 2 * margin, region.Height + 2 * margin, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(lettering))
        {
            graphics.Clear(crop.GetPixel(region.Left, region.Top));
            graphics.DrawImage(crop, new Rectangle(margin, margin, region.Width, region.Height), region, GraphicsUnit.Pixel);
        }
        var start = new Point(origin.X + search.X + region.X - margin, origin.Y + search.Y + region.Y - margin);
        var readingHeight = Math.Max(5, line.Height - 4);
        var reads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var height in new[] { 23f, 29f, 35f, 41f })
        {
            var scale = Math.Min(height / readingHeight,
                (float)OcrEngine.MaxImageDimension / Math.Max(lettering.Width, lettering.Height));
            var rows = await RecognizeBitmapAsync(lettering, start, scale, token).ConfigureAwait(false);
            if (rows is not [var row] || !RectangleF.Inflate(row.Bounds, 3, 3).Contains(cursor))
                continue;
            var text = string.Concat(row.Words.Select(word => word.Text));
            if (HyphenatedWord().IsMatch(text) && !reads.Add(text))
                return [new ScreenLine([new ScreenWord(text, row.Bounds)], row.Bounds)];
        }
        return null;
    }

    private async Task<ScreenLine[]?> ReadSmallPaleLabelAsync(Bitmap image, Point origin, Point local, Point cursor,
        Selection? original, CancellationToken token)
    {
        var probe = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size),
            new Rectangle(local.X - 36, local.Y - 12, 73, 25));
        var peak = 0;
        var colors = 0;
        var dark = 0;
        var samples = 0;
        for (var y = probe.Top; y < probe.Bottom; y += 2)
        for (var x = probe.Left; x < probe.Right; x += 2)
        {
            var pixel = image.GetPixel(x, y);
            var low = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
            var spread = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - low;
            if (spread <= 30)
                peak = Math.Max(peak, low);
            if (spread >= 25)
                colors++;
            if (low < 80)
                dark++;
            samples++;
        }
        if (peak < 158 || dark * 100 < samples * 34 || colors * 100 < samples * 12)
            return null;

        var search = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size),
            new Rectangle(local.X - 126, local.Y - 31, 253, 63));
        using var crop = image.Clone(search, PixelFormat.Format32bppArgb);
        using var mask = LightTextMask(crop, (int)(peak * 0.78f), token, spread: 33, whiteOnBlack: true);
        var position = new Point(local.X - search.X, local.Y - search.Y);
        var detected = FindDisplayLine(mask, position, maxHeight: 30, gapRatio: .8f);
        if (detected is not { Height: > 12 }
            && await ReadShortPaleLabelAsync(image, origin, local, cursor, token).ConfigureAwait(false) is { } shortLabel)
            return shortLabel;
        if (detected is not { } line || line.Width < 23 || line.Width > 188)
            return null;

        var bounds = Rectangle.Intersect(new Rectangle(Point.Empty, crop.Size), Rectangle.Inflate(line, 10, 4));
        using var strip = crop.Clone(bounds, PixelFormat.Format32bppArgb);
        var lowLevel = peak > 230 ? 100 : 55;
        var highLevel = peak > 230 ? (int)(peak * 0.90f) : 155;
        using var normalized = ConvertPixels(strip, token, (r, g, b) => (byte)Math.Clamp(
            (Math.Min(r, Math.Min(g, b)) - lowLevel) * 255 / (highLevel - lowLevel), 0, 255));
        const int horizontalMargin = 8;
        const int verticalMargin = 12;
        using var padded = new Bitmap(bounds.Width + horizontalMargin * 2, bounds.Height + verticalMargin * 2,
            PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(padded))
        {
            graphics.Clear(Color.Black);
            graphics.DrawImage(normalized, new Rectangle(horizontalMargin, verticalMargin, normalized.Width, normalized.Height),
                0, 0, normalized.Width, normalized.Height, GraphicsUnit.Pixel);
        }
        var start = new Point(origin.X + search.X + bounds.X - horizontalMargin, origin.Y + search.Y + bounds.Y - verticalMargin);
        var reads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scale in new[] { 1.7f, 2.2f, 2.8f, 3.3f })
        {
            var rows = await RecognizeBitmapAsync(padded, start, scale, token).ConfigureAwait(false);
            if (TextSelection.At(rows, cursor, false, null) is not { } hit || hit.Text.Length < 2 || !hit.Text.All(char.IsAsciiLetter))
                continue;
            if (original is not null && original.Text.Length > hit.Text.Length
                && !(IsUppercaseWord(hit.Text) && original.Text.StartsWith(hit.Text, StringComparison.OrdinalIgnoreCase)
                    && char.IsAsciiLetterUpper(original.Text[hit.Text.Length])))
                continue;
            if (!reads.Add(hit.Text))
                return rows;
        }
        return null;
    }

    private async Task<ScreenLine[]?> ReadShortPaleLabelAsync(Bitmap image, Point origin, Point local, Point cursor,
        CancellationToken token)
    {
        var imageBounds = new Rectangle(Point.Empty, image.Size);
        var probe = Rectangle.Intersect(imageBounds, new Rectangle(local.X - 30, local.Y - 9, 61, 18));
        var peak = 0;
        for (var y = probe.Top; y < probe.Bottom; y++)
        for (var x = probe.Left; x < probe.Right; x++)
        {
            var p = image.GetPixel(x, y);
            var low = Math.Min(p.R, Math.Min(p.G, p.B));
            if (Math.Max(p.R, Math.Max(p.G, p.B)) - low <= 40)
                peak = Math.Max(peak, low);
        }
        if (peak < 147)
            return null;

        var area = Rectangle.Intersect(imageBounds, new Rectangle(local.X - 90, local.Y - 26, 181, 53));
        using var crop = image.Clone(area, PixelFormat.Format32bppArgb);
        var relative = new Point(local.X - area.Left, local.Y - area.Top);
        foreach (var factor in peak < 225 ? new[] { .86f, .79f } : new[] { .79f })
        {
            using var mask = LightTextMask(crop, (int)MathF.Ceiling(peak * factor), token, spread: 38, whiteOnBlack: true);
            if (FindDisplayLine(mask, relative, maxHeight: 20, gapRatio: .85f, minHeight: 5) is not { Height: <= 13 } line
                || line.Width < 14 || line.Width > 124)
                continue;

            using var raw = crop.Clone(line, PixelFormat.Format32bppArgb);
            using var normalized = ConvertPixels(raw, token, (red, green, blue) => (byte)Math.Clamp(
                (Math.Min(red, Math.Min(green, blue)) - 72) * 255 / (peak - 72), 0, 255));
            const int margin = 8;
            using var padded = new Bitmap(line.Width + margin * 2, line.Height + margin * 2, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(padded))
            {
                graphics.Clear(Color.Black);
                graphics.DrawImageUnscaled(normalized, margin, margin);
            }
            var start = new Point(origin.X + area.X + line.X - margin, origin.Y + area.Y + line.Y - margin);
            var reads = new HashSet<string>(StringComparer.Ordinal);
            foreach (var height in new[] { 24f, 42f, 30f, 36f })
            {
                var rows = await RecognizeBitmapAsync(padded, start, height / (line.Height - 4), token).ConfigureAwait(false);
                if (TextSelection.At(rows, cursor, false, null) is { } hit && hit.Text.Length >= 3
                    && char.IsAsciiLetterUpper(hit.Text[0]) && hit.Text.All(char.IsAsciiLetter) && !reads.Add(hit.Text))
                    return rows;
            }
        }
        return null;
    }

    private async Task<ScreenLine[]?> ReadDisplayLabelAsync(Bitmap image, Point origin, Point local, Point cursor,
        Selection? originalWord, CancellationToken token, bool largeDisplayOnly = false)
    {
        var sample = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size),
            new Rectangle(local.X - 48, local.Y - 26, 97, 53));
        var dark = 0;
        var red = 0;
        var cyan = 0;
        var neutral = 0;
        var samples = 0;
        var saturated = new Dictionary<int, int>();
        for (var y = sample.Top; y < sample.Bottom; y += 3)
        for (var x = sample.Left; x < sample.Right; x += 3)
        {
            var pixel = image.GetPixel(x, y);
            var ceiling = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
            if (ceiling < 70)
                dark++;
            if (pixel.R - Math.Max(pixel.G, pixel.B) > 48)
                red++;
            if (Math.Min(pixel.G, pixel.B) - pixel.R > 48)
                cyan++;
            var floor = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
            if (floor >= 150 && ceiling - floor <= 45)
                neutral++;
            if (ceiling >= 130 && ceiling - floor >= 75)
            {
                var key = pixel.R / 32 << 6 | pixel.G / 32 << 3 | pixel.B / 32;
                saturated[key] = saturated.GetValueOrDefault(key) + 1;
            }
            samples++;
        }
        if (samples == 0)
            return null;

        if (dark * 100 < samples * 22)
        {
            if (saturated.Count == 0 || neutral * 100 < samples * 14)
                return null;
            var fill = saturated.MaxBy(pair => pair.Value);
            return fill.Value * 100 >= samples * 20
                ? await ReadFlatDisplayTextAsync(image, origin, local, cursor, originalWord, fill.Key, token).ConfigureAwait(false)
                : null;
        }
        if (largeDisplayOnly)
            return null;
        if (Math.Max(red, cyan) * 100 >= samples * 12)
        {
            var color = await ReadColoredOverlayAsync(image, origin, local, cursor, red > cyan, token).ConfigureAwait(false);
            if (color is not null)
                return color;
        }
        if (neutral * 100 >= samples * 8 && originalWord is { } word
            && word.Text.Length >= 4 && IsUppercaseWord(word.Text))
            return await ReadIsolatedDisplayWordAsync(image, origin, local, cursor, word, token).ConfigureAwait(false);
        return null;
    }

    private async Task<ScreenLine[]?> ReadFlatDisplayTextAsync(Bitmap image, Point origin, Point local, Point cursor,
        Selection? original, int fill, CancellationToken token)
    {
        var search = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size),
            new Rectangle(local.X - 760, local.Y - 220, 1520, 440));
        var red = (fill >> 6) * 32 + 16;
        var green = (fill >> 3 & 7) * 32 + 16;
        var blue = (fill & 7) * 32 + 16;
        var bounds = Rectangle.Empty;
        var matches = 0;
        const int step = 3;
        for (var y = search.Top; y < search.Bottom; y += step)
        {
            token.ThrowIfCancellationRequested();
            for (var x = search.Left; x < search.Right; x += step)
            {
                var pixel = image.GetPixel(x, y);
                if (Math.Abs(pixel.R - red) > 37 || Math.Abs(pixel.G - green) > 37 || Math.Abs(pixel.B - blue) > 37)
                    continue;
                var cell = new Rectangle(x, y, step, step);
                bounds = matches++ == 0 ? cell : Rectangle.Union(bounds, cell);
            }
        }
        if (bounds.Height < 35 || bounds.Width < bounds.Height * 1.8f || bounds.Width > bounds.Height * 7f
            || !bounds.Contains(local) || !search.Contains(Rectangle.Inflate(bounds, step, step)))
            return null;
        var fillRatio = (float)(matches * step * step) / (bounds.Width * bounds.Height);
        if (fillRatio is < 0.38f or > 0.88f)
            return null;

        // Trim the outer contour so white lettering that meets the background remains readable.
        var left = bounds.Left + (int)(bounds.Width * .05f);
        var right = bounds.Right - (int)(bounds.Width * .05f);
        var top = bounds.Top + (int)(bounds.Height * .07f);
        foreach (var bottomInset in new[] { .17f, .10f })
        {
            var bottom = Math.Max(bounds.Bottom - (int)(bounds.Height * bottomInset), local.Y + 1);
            var band = Rectangle.FromLTRB(left, top, right, bottom);
            if (!band.Contains(local))
                continue;
            using var crop = image.Clone(band, PixelFormat.Format32bppArgb);
            var start = new Point(origin.X + band.X, origin.Y + band.Y);
            var rows = await RecognizeBitmapAsync(crop, start, Math.Clamp(52f / band.Height, .3f, 1.25f), token)
                .ConfigureAwait(false);
            if (TextSelection.At(rows, cursor, false, null) is not { } hit || hit.Text.Length < 3
                || !hit.Text.All(char.IsAsciiLetter) || hit.Bounds.Height < band.Height * .55f)
                continue;
            if (original is null || hit.Text.Length > original.Text.Length
                && hit.Text.StartsWith(original.Text, StringComparison.OrdinalIgnoreCase))
                return rows;
        }
        return null;
    }

    private async Task<ScreenLine[]?> ReadColoredOverlayAsync(Bitmap image, Point origin, Point local, Point cursor,
        bool red, CancellationToken token)
    {
        var patch = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size),
            new Rectangle(local.X - 160, local.Y - 112, 321, 225));
        using var crop = image.Clone(patch, PixelFormat.Format32bppArgb);
        using var mask = ConvertPixels(crop, token, (r, g, b) =>
            (red ? r - Math.Max(g, b) : Math.Min(g, b) - r) >= 45 ? (byte)255 : (byte)0);
        var relative = new Point(local.X - patch.X, local.Y - patch.Y);
        if (FindDisplayLine(mask, relative, maxHeight: 105) is not { } line || line.Width > 260)
            return null;

        line = Rectangle.Intersect(new Rectangle(Point.Empty, crop.Size), Rectangle.Inflate(line, 2, 4));
        using var lettering = crop.Clone(line, PixelFormat.Format32bppArgb);
        using var channel = ConvertPixels(lettering, token, (r, _, b) => red ? r : b);
        using var isolated = ConvertPixels(lettering, token, (r, g, b) =>
            (red ? r - Math.Max(g, b) : Math.Min(g, b) - r) >= 96 ? (byte)255 : (byte)0);
        var start = new Point(origin.X + patch.X + line.X, origin.Y + patch.Y + line.Y);
        foreach (var (variant, height, aspect) in new[] { (isolated, 25, 1.6f), (channel, 25, 1.6f),
                     (channel, 38, 1.6f), (channel, 39, 1.35f) })
        {
            var width = Math.Max(1, (int)Math.Round(variant.Width * (float)height / variant.Height * aspect));
            const int padding = 13;
            using var adjusted = new Bitmap(width + padding * 2, height + padding * 2, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(adjusted))
            {
                graphics.Clear(Color.Black);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(variant, new Rectangle(padding, padding, width, height));
            }
            var rows = await RecognizeBitmapAsync(adjusted, Point.Empty, 1f, token).ConfigureAwait(false);
            RectangleF Project(RectangleF rect) => new(start.X + (rect.X - padding) * channel.Width / width,
                start.Y + (rect.Y - padding) * channel.Height / height,
                rect.Width * channel.Width / width, rect.Height * channel.Height / height);
            var projected = rows.Select(row => new ScreenLine(
                row.Words.Select(word => word with { Bounds = Project(word.Bounds) }).ToArray(), Project(row.Bounds))).ToArray();
            if (TextSelection.At(projected, cursor, false, null) is { } hit && IsUppercaseWord(hit.Text))
                return projected;
        }
        return null;
    }

    private async Task<ScreenLine[]?> ReadIsolatedDisplayWordAsync(Bitmap image, Point origin, Point local, Point cursor,
        Selection original, CancellationToken token)
    {
        var patch = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size),
            new Rectangle(local.X - 120, local.Y - 46, 241, 93));
        using var crop = image.Clone(patch, PixelFormat.Format32bppArgb);
        using var mask = LightTextMask(crop, 150, token, spread: 45, whiteOnBlack: true);
        if (FindDisplayLine(mask, new Point(local.X - patch.X, local.Y - patch.Y), gapRatio: 0.22f) is not { } line)
            return null;

        var start = new Point(origin.X + patch.X + line.X, origin.Y + patch.Y + line.Y);
        if (original.Bounds.Right - (start.X + line.Width) < line.Height * 0.3f)
            return null;
        using var focused = mask.Clone(line, PixelFormat.Format32bppArgb);
        const int margin = 12;
        using var padded = new Bitmap(line.Width + margin * 2, line.Height + margin * 2, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(padded))
        {
            graphics.Clear(Color.Black);
            graphics.DrawImageUnscaled(focused, margin, margin);
        }
        var rows = await RecognizeBitmapAsync(padded, new Point(start.X - margin, start.Y - margin), 1.5f, token).ConfigureAwait(false);
        var hit = TextSelection.At(rows, cursor, false, null);
        return hit is not null && hit.Text.Length >= 2 && hit.Text.Length < original.Text.Length
            && IsUppercaseWord(hit.Text) && original.Text.StartsWith(hit.Text, StringComparison.OrdinalIgnoreCase) ? rows : null;
    }

}
