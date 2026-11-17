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
        if (HasBrightTextOnBlack(image, local, SampleLuminance(image, local)) && !HasChromaticLabelPixels(image, local))
            return null;
        if (original is not null && original.Text.Length >= 4 && original.Text.All(char.IsAsciiLetter)
            && spelling.FrequencyRank(original.Text) is > 0 and <= 50000 && original.Bounds.Contains(cursor))
            return null;
        var legacy = await ReadSmallPaleLabelLegacyAsync(image, origin, local, cursor, original, token).ConfigureAwait(false);
        var first = legacy is null ? null : TextSelection.At(legacy, cursor, false, null);
        if (first is not null && (IsUppercaseWord(first.Text) && first.Text.Length <= 4
            || first.Text.Length >= 4 && spelling.FrequencyRank(first.Text) > 0))
            return legacy;
        if (legacy is not null || original is { Bounds.Height: < 65 })
        {
            var localReading = await ReadPaleLatinLineAsync(image, origin, local, cursor, token).ConfigureAwait(false);
            if (localReading is not null && TextSelection.At(localReading, cursor, false, null) is { } localHit
                && (IsUppercaseWord(localHit.Text) && localHit.Text.Length <= 4 || spelling.FrequencyRank(localHit.Text) > 0))
                return PreserveWordCase(localReading, original);
        }
        var alternate = await ReadSmallPaleLabelAlternativeAsync(image, origin, local, cursor, original, token).ConfigureAwait(false);
        var next = alternate is null ? null : TextSelection.At(alternate, cursor, false, null);
        if (next is not null && (first is null || next.Text.Length >= 4 && spelling.FrequencyRank(next.Text) > 0))
            return alternate;
        return legacy ?? alternate;
    }

    private static bool HasChromaticLabelPixels(Bitmap image, Point local)
    {
        var area = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size),
            new Rectangle(local.X - 36, local.Y - 12, 73, 25));
        var colored = 0;
        var samples = 0;
        for (var y = area.Top; y < area.Bottom; y += 2)
            for (var x = area.Left; x < area.Right; x += 2)
            {
                var pixel = image.GetPixel(x, y);
                if (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) >= 25)
                    colored++;
                samples++;
            }
        return colored * 100 >= samples * 12;
    }

    private Task<ScreenLine[]?> ReadPaleLatinLineAsync(Bitmap image, Point origin, Point local, Point cursor,
        CancellationToken token)
    {
        var search = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size),
            new Rectangle(local.X - 220, local.Y - 55, 441, 111));
        using var crop = image.Clone(search, PixelFormat.Format32bppArgb);
        var position = new Point(local.X - search.X, local.Y - search.Y);
        var readings = new List<(string Text, ScreenLine[] Rows, float Confidence)>();
        foreach (var cutoff in new[] { 100, 125, 155, 185 })
        {
            using var mask = LightTextMask(crop, cutoff, token, spread: 40, whiteOnBlack: true);
            if (FindLatinInkLine(mask, position) is not { } line
                || line.Width < 12 || line.Width > 400)
                continue;
            var bounds = Rectangle.Intersect(new Rectangle(Point.Empty, crop.Size), Rectangle.Inflate(line, 3, 2));
            using var strip = crop.Clone(bounds, PixelFormat.Format32bppArgb);
            using var normalized = ConvertPixels(strip, token, (r, g, b) =>
                (byte)(255 - Math.Clamp((Math.Min(r, Math.Min(g, b)) - 90) * 255 / 150, 0, 255)));
            var start = new Point(origin.X + search.X + bounds.X, origin.Y + search.Y + bounds.Y);
            foreach (var height in new[] { 24f, 32f, 44f })
            {
                var read = latinOcr.Value.Read(strip, start, height / bounds.Height, token, lightFloor: 100);
                if (read.Confidence < .35f || TextSelection.At(read.Rows, cursor, false, null) is not { } hit
                    || hit.Text.Length < 2 || !hit.Text.All(char.IsAsciiLetter))
                    continue;
                if (hit.Text.Length < 4 && !IsUppercaseWord(hit.Text))
                    continue;
                if (read.Confidence < .72f && spelling.FrequencyRank(hit.Text) <= 0)
                    continue;
                readings.Add((hit.Text, read.Rows, read.Confidence));
            }
        }
        var best = readings.GroupBy(reading => reading.Text, StringComparer.Ordinal)
            .Where(group => group.Count() >= 2)
            .OrderByDescending(group => group.Count() * (spelling.FrequencyRank(group.Key) > 0 ? 3 : 1))
            .ThenByDescending(group => group.Average(reading => reading.Confidence)).FirstOrDefault();
        return Task.FromResult(best?.OrderByDescending(reading => reading.Confidence).First().Rows);
    }

    private static Rectangle? FindLatinInkLine(Bitmap mask, Point cursor)
    {
        var top = Math.Max(0, cursor.Y - 55);
        var bottom = Math.Min(mask.Height, cursor.Y + 56);
        var rows = new bool[bottom - top];
        for (var y = top; y < bottom; y++)
        {
            var count = 0;
            for (var x = Math.Max(0, cursor.X - 60); x < Math.Min(mask.Width, cursor.X + 61) && count < 2; x++)
                if (mask.GetPixel(x, y).R != 0)
                    count++;
            rows[y - top] = count >= 2;
        }
        for (var index = 1; index + 1 < rows.Length; index++)
            if (rows[index - 1] && rows[index + 1])
                rows[index] = true;
        var anchor = Enumerable.Range(top, rows.Length).Where(y => rows[y - top])
            .OrderBy(y => Math.Abs(y - cursor.Y)).FirstOrDefault(-1);
        if (anchor < 0 || Math.Abs(anchor - cursor.Y) > 8)
            return null;
        var first = anchor;
        var last = anchor + 1;
        while (first > top && rows[first - top - 1]) first--;
        while (last < bottom && rows[last - top]) last++;
        var height = last - first;
        if (height is < 4 or > 60)
            return null;
        var columns = new List<int>();
        for (var x = 0; x < mask.Width; x++)
            for (var y = first; y < last; y++)
                if (mask.GetPixel(x, y).R != 0)
                {
                    columns.Add(x);
                    break;
                }
        if (columns.Count == 0)
            return null;
        var center = Enumerable.Range(0, columns.Count).MinBy(index => Math.Abs(columns[index] - cursor.X));
        if (Math.Abs(columns[center] - cursor.X) > 8)
            return null;
        var left = center;
        var right = center;
        while (left > 0 && columns[left] - columns[left - 1] <= height * 1.35f) left--;
        while (right + 1 < columns.Count && columns[right + 1] - columns[right] <= height * 1.35f) right++;
        return Rectangle.Intersect(new Rectangle(Point.Empty, mask.Size),
            Rectangle.FromLTRB(columns[left] - 3, first - 2, columns[right] + 4, last + 2));
    }

    private static ScreenLine[] PreserveWordCase(ScreenLine[] rows, Selection? reference)
    {
        if (reference is null || !reference.Text.All(char.IsAsciiLetter))
            return rows;
        return rows.Select(row => row with
        {
            Words = row.Words.Select(word => word.Bounds.IntersectsWith(reference.Bounds)
                && word.Text.Equals(reference.Text, StringComparison.OrdinalIgnoreCase)
                    ? word with { Text = reference.Text }
                    : word.Bounds.IntersectsWith(reference.Bounds) && char.IsUpper(reference.Text[0])
                        && reference.Text.Skip(1).All(char.IsLower) && word.Text.All(char.IsLower)
                        ? word with { Text = char.ToUpperInvariant(word.Text[0]) + word.Text[1..] } : word).ToArray()
        }).ToArray();
    }

    private async Task<ScreenLine[]?> ReadSmallPaleLabelLegacyAsync(Bitmap image, Point origin, Point local, Point cursor,
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

    private async Task<ScreenLine[]?> ReadSmallPaleLabelAlternativeAsync(Bitmap image, Point origin, Point local, Point cursor,
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

        var maximumHeight = original is null ? 30 : Math.Clamp((int)(original.Bounds.Height * 1.5f), 20, 60);
        var search = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size),
            new Rectangle(local.X - 126, local.Y - maximumHeight, 253, maximumHeight * 2 + 1));
        using var crop = image.Clone(search, PixelFormat.Format32bppArgb);
        using var mask = LightTextMask(crop, (int)(peak * .78f), token, spread: 33, whiteOnBlack: true);
        var position = new Point(local.X - search.X, local.Y - search.Y);
        var detected = FindDisplayLine(mask, position, maxHeight: maximumHeight, gapRatio: .8f);
        var readings = new Dictionary<string, (int Count, ScreenLine[] Rows)>(StringComparer.Ordinal);
        if (detected is not { Height: > 12 }
            && await ReadShortPaleLabelAsync(image, origin, local, cursor, token).ConfigureAwait(false) is { } shortRows)
            Record(shortRows, 2);
        if (detected is { } line && line.Width is >= 23 and <= 188)
        {
            var bounds = Rectangle.Intersect(new Rectangle(Point.Empty, crop.Size), Rectangle.Inflate(line, 10, 4));
            using var strip = crop.Clone(bounds, PixelFormat.Format32bppArgb);
            var lowLevel = peak > 230 ? 100 : 55;
            var highLevel = peak > 230 ? (int)(peak * .90f) : 155;
            using var normalized = ConvertPixels(strip, token, (r, g, b) => (byte)Math.Clamp(
                (Math.Min(r, Math.Min(g, b)) - lowLevel) * 255 / (highLevel - lowLevel), 0, 255));
            const int horizontalMargin = 8;
            const int verticalMargin = 12;
            using var padded = new Bitmap(bounds.Width + horizontalMargin * 2, bounds.Height + verticalMargin * 2,
                PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(padded))
            {
                graphics.Clear(Color.Black);
                graphics.DrawImageUnscaled(normalized, horizontalMargin, verticalMargin);
            }
            var start = new Point(origin.X + search.X + bounds.X - horizontalMargin,
                origin.Y + search.Y + bounds.Y - verticalMargin);
            var scales = new[] { 1.7f, 2.2f, 2.8f, 3.3f }
                .Concat(new[] { 26f, 32f, 38f, 44f }.Select(height => height / Math.Max(5, line.Height - 4))).Distinct();
            foreach (var scale in scales)
                Record(await RecognizeBitmapAsync(padded, start, scale, token).ConfigureAwait(false));
        }
        var best = readings.Where(reading => reading.Value.Count >= 2)
            .OrderByDescending(reading => reading.Value.Count * (spelling.FrequencyRank(reading.Key) is > 0 and <= 50000 ? 3 : 1))
            .ThenByDescending(reading => reading.Key.Length).FirstOrDefault();
        return best.Value.Count >= 2 ? best.Value.Rows : null;

        void Record(ScreenLine[] rows, int votes = 1)
        {
            if (TextSelection.At(rows, cursor, false, null) is not { } hit || hit.Text.Length < 2
                || !hit.Text.All(char.IsAsciiLetter))
                return;
            if (original is not null && original.Text.Length > hit.Text.Length
                && !(IsUppercaseWord(hit.Text) && original.Text.StartsWith(hit.Text, StringComparison.OrdinalIgnoreCase)
                    && char.IsAsciiLetterUpper(original.Text[hit.Text.Length]))
                && !rows.SelectMany(row => row.Words).Any(word => word.Text.Length is >= 2 and <= 4
                    && IsUppercaseWord(word.Text) && word.Bounds.Right <= hit.Bounds.Left + hit.Bounds.Height * .3f
                    && original.Bounds.IntersectsWith(word.Bounds) && hit.Bounds.Contains(cursor)))
                return;
            var previous = readings.GetValueOrDefault(hit.Text);
            readings[hit.Text] = (previous.Count + votes, rows);
        }
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
        var colored = 0;
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
                var floor = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                if (ceiling - floor >= 48) colored++;
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
            // A large colored glyph can fill the probe. It still needs the colored-letter
            // path when there is no white lettering characteristic of a flat logo.
            if (!largeDisplayOnly && neutral * 100 < samples * 14 && colored * 100 >= samples * 12)
                return await ReadColoredOverlayAsync(image, origin, local, cursor, token).ConfigureAwait(false);
            if (saturated.Count == 0 || neutral * 100 < samples * 14)
                return null;
            var fill = saturated.MaxBy(pair => pair.Value);
            return fill.Value * 100 >= samples * 20
                ? await ReadFlatDisplayTextAsync(image, origin, local, cursor, originalWord, fill.Key, token).ConfigureAwait(false)
                : null;
        }
        if (largeDisplayOnly)
            return null;
        if (colored * 100 >= samples * 12)
        {
            var color = await ReadColoredOverlayAsync(image, origin, local, cursor, token).ConfigureAwait(false);
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
        var width = Math.Min(image.Width, 1920);
        var height = Math.Min(image.Height, 1080);
        var search = new Rectangle(Math.Clamp(local.X - width / 2, 0, image.Width - width),
            Math.Clamp(local.Y - height / 2, 0, image.Height - height), width, height);
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
        var readings = new List<(Selection Hit, ScreenLine[] Rows)>();
        foreach (var bottomInset in new[] { .17f, .10f, .04f })
        {
            var bottom = Math.Max(bounds.Bottom - (int)(bounds.Height * bottomInset), local.Y + 1);
            var band = Rectangle.FromLTRB(left, top, right, bottom);
            if (!band.Contains(local))
                continue;
            using var crop = image.Clone(band, PixelFormat.Format32bppArgb);
            var start = new Point(origin.X + band.X, origin.Y + band.Y);
            foreach (var readingHeight in new[] { 40f, 52f })
            {
                var localRead = latinOcr.Value.Read(crop, start, readingHeight / band.Height, token, lightFloor: 100);
                if (localRead.Confidence >= .7f && TextSelection.At(localRead.Rows, cursor, false, null) is { } localHit
                    && localHit.Text.Length >= 3 && localHit.Text.All(char.IsAsciiLetter)
                    && localHit.Bounds.Height >= band.Height * .55f && spelling.Find(localHit.Text) is not null
                    && (original is null || localHit.Text.Contains(original.Text, StringComparison.OrdinalIgnoreCase)
                        || EditDistance(localHit.Text.ToLowerInvariant(), original.Text.ToLowerInvariant(), 2) <= 2))
                    readings.Add((localHit, localRead.Rows));
            }
            foreach (var readingHeight in new[] { 44f, 52f, 60f, 68f })
            {
                var rows = await RecognizeBitmapAsync(crop, start, Math.Clamp(readingHeight / band.Height, .1f, 1.25f), token)
                    .ConfigureAwait(false);
                if (TextSelection.At(rows, cursor, false, null) is not { } hit || hit.Text.Length < 3
                    || !hit.Text.All(char.IsAsciiLetter) || hit.Bounds.Height < band.Height * .55f)
                    continue;
                if (original is null || hit.Text.Length >= original.Text.Length
                    && (hit.Text.Contains(original.Text, StringComparison.OrdinalIgnoreCase)
                        || IsSingleGlyphVariant(original.Text, hit.Text))
                    || spelling.Find(hit.Text) is not null && spelling.Find(original.Text) is null)
                    readings.Add((hit, rows));
            }
        }
        if (readings.Count == 0)
            return null;
        var groups = readings.GroupBy(reading => reading.Hit.Text, StringComparer.OrdinalIgnoreCase).ToArray();
        var known = groups.Where(group => spelling.Find(group.Key) is not null).ToArray();
        var pool = known.Length == 0 ? groups : known;
        var best = pool.Where(group => !pool.Any(other => other.Key.Length > group.Key.Length
                && other.Count() >= 2 && other.Key.StartsWith(group.Key, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(group => group.Count()).ThenByDescending(group => group.Key.Length).First();
        var selected = best.GroupBy(reading => reading.Hit.Text, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key.Any(char.IsUpper)).First().First();
        if (char.IsLower(selected.Hit.Text[0]) && selected.Hit.Text.Any(char.IsUpper)
            && best.Any(reading => reading.Hit.Text.All(char.IsLower))
            && spelling.FindHeadword(selected.Hit.Text) is { } headword && !headword.Skip(1).Any(char.IsUpper))
            return selected.Rows.Select(row => row with
            {
                Words = row.Words.Select(word => word.Text == selected.Hit.Text
                    ? word with { Text = word.Text.ToLowerInvariant() } : word).ToArray()
            }).ToArray();
        return selected.Rows;
    }

    private async Task<ScreenLine[]?> ReadColoredOverlayAsync(Bitmap image, Point origin, Point local, Point cursor,
        CancellationToken token)
    {
        var hues = new int[24];
        for (var y = Math.Max(0, local.Y - 26); y < Math.Min(image.Height, local.Y + 27); y += 2)
            for (var x = Math.Max(0, local.X - 48); x < Math.Min(image.Width, local.X + 49); x += 2)
            {
                var pixel = image.GetPixel(x, y);
                if (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) >= 100 && Chroma(pixel) >= .25f)
                    hues[(int)(pixel.GetHue() / 15) % hues.Length]++;
            }
        foreach (var hue in Enumerable.Range(0, hues.Length).Where(index => hues[index] >= 6).OrderByDescending(index => hues[index]).Take(3))
            if (await ReadForegroundOverlayAsync(image, origin, local, cursor, hue * 15 + 7.5f, token).ConfigureAwait(false) is { } rows)
                return rows;
        return null;
    }

    private async Task<ScreenLine[]?> ReadForegroundOverlayAsync(Bitmap image, Point origin, Point local, Point cursor,
        float hue, CancellationToken token)
    {
        bool Matches(byte r, byte g, byte b, float tolerance)
        {
            var difference = Math.Abs(Color.FromArgb(r, g, b).GetHue() - hue);
            return Math.Max(r, Math.Max(g, b)) >= 100 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) >= 25
                && Math.Min(difference, 360 - difference) <= tolerance;
        }
        var patch = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size),
            new Rectangle(local.X - 160, local.Y - 112, 321, 225));
        using var crop = image.Clone(patch, PixelFormat.Format32bppArgb);
        using var mask = ConvertPixels(crop, token, (r, g, b) =>
            Matches(r, g, b, 26) ? (byte)255 : (byte)0);
        var relative = new Point(local.X - patch.X, local.Y - patch.Y);
        if (FindDisplayLine(mask, relative, maxHeight: 105) is not { } line || line.Width > 260)
            return null;

        line = Rectangle.Intersect(new Rectangle(Point.Empty, crop.Size), Rectangle.Inflate(line, 2, 4));
        using var lettering = crop.Clone(line, PixelFormat.Format32bppArgb);
        using var channel = ConvertPixels(lettering, token, (r, g, b) => Math.Max(r, Math.Max(g, b)));
        using var isolated = ConvertPixels(lettering, token, (r, g, b) =>
            Matches(r, g, b, 15) ? (byte)255 : (byte)0);
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
