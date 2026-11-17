using System.Text.RegularExpressions;

namespace ScreenTranslator;

internal sealed partial class ScreenOcr
{
    private async Task<ScreenLine[]> ReadParagraphAsync(Bitmap image, Point origin, Point cursor,
        float scale, CancellationToken token, ScreenLine[]? originalRows = null, bool detectControls = true,
        ScreenLine[]? alternateRows = null, bool allowMapLabels = false, float? originalRowsScale = null)
    {
        var candidates = new List<(ScreenLine[] Rows, Selection Selection, float Quality)>();
        var requestedScale = scale;
        var seedScale = originalRowsScale ?? scale;
        var originalScale = Math.Min(1.65f,
            (float)Windows.Media.Ocr.OcrEngine.MaxImageDimension / Math.Max(image.Width, image.Height));
        if (scale < originalScale * .8f && originalRowsScale != originalScale)
        {
            scale = originalScale;
            await Read(image).ConfigureAwait(false);
            seedScale = originalScale;
            scale = requestedScale;
        }
        else await Read(image, originalRows).ConfigureAwait(false);
        var originalReading = candidates.FirstOrDefault();
        if (alternateRows is { Length: > 0 } && TextSelection.At(alternateRows, cursor, true, null) is { } alternate
            && (candidates.Count == 0 || alternate.Text != candidates[0].Selection.Text))
            await Read(image, alternateRows).ConfigureAwait(false);
        var hovered = candidates.FirstOrDefault().Rows?.Where(row => RectangleF.Inflate(row.Bounds, 12, 12).Contains(cursor))
            .MinBy(row => Math.Abs(row.Bounds.Top + row.Bounds.Height / 2 - cursor.Y));
        var first = candidates.FirstOrDefault();
        var heights = first.Rows?.SelectMany(row => row.Words)
            .Where(word => first.Selection.Bounds.Contains(word.Bounds) && word.Text.Count(char.IsLetter) >= 3)
            .Select(word => word.Bounds.Height).Order().ToArray() ?? [];
        if (detectControls && (FindDialogueOption(image, new Point(cursor.X - origin.X, cursor.Y - origin.Y),
                hovered?.Bounds.Left - origin.X, heights.Length == 0 ? 0 : heights[heights.Length / 2])
            ?? FindDialogueOption(image, new Point(cursor.X - origin.X, cursor.Y - origin.Y))) is { } control)
        {
            if (control.Bounds.Width == 0 || cursor.X - origin.X < control.Bounds.Left)
                return [];
            using var text = image.Clone(control.Bounds, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            return await ReadParagraphAsync(text, new Point(origin.X + control.Bounds.X, origin.Y + control.Bounds.Y),
                cursor, scale, token, detectControls: false).ConfigureAwait(false);
        }
        scale = seedScale;
        if (heights.Length != 0 && Math.Abs(40f / heights[heights.Length / 2] / seedScale - 1) > .2f)
        {
            var alternateScale = Math.Max(.35f, 48f / heights[heights.Length / 2]);
            scale = Math.Max(.35f, 40f / heights[heights.Length / 2]);
            await Read(image).ConfigureAwait(false);
            var normalizedScale = scale;
            scale = alternateScale;
            await Read(image).ConfigureAwait(false);
            scale = normalizedScale;
        }
        var seed = candidates.FirstOrDefault();
        var samples = seed.Rows?.SelectMany(row => row.Words)
            .Where(word => seed.Selection.Bounds.Contains(word.Bounds) && word.Text.Count(char.IsLetter) >= 2)
            .SelectMany(word => SampleLetteringColors(image, new Point(
                (int)(word.Bounds.Left + word.Bounds.Width / 2) - origin.X,
                (int)(word.Bounds.Top + word.Bounds.Height / 2) - origin.Y)))
            .GroupBy(color => color.ToArgb())
            .OrderByDescending(group => group.Count()).Select(group => (Color: group.First(), Count: group.Count())).ToArray() ?? [];
        var clusters = new List<(Color Color, int Count)>();
        foreach (var sample in samples)
        {
            var nearest = clusters.FindIndex(group => Math.Max(Math.Abs(group.Color.R - sample.Color.R),
                Math.Max(Math.Abs(group.Color.G - sample.Color.G), Math.Abs(group.Color.B - sample.Color.B))) <= 40);
            if (nearest < 0)
            {
                clusters.Add(sample);
                continue;
            }
            var group = clusters[nearest];
            var count = group.Count + sample.Count;
            clusters[nearest] = (Color.FromArgb((group.Color.R * group.Count + sample.Color.R * sample.Count) / count,
                (group.Color.G * group.Count + sample.Color.G * sample.Count) / count,
                (group.Color.B * group.Count + sample.Color.B * sample.Count) / count), count);
        }
        var families = new List<Color>();
        var learned = clusters.OrderByDescending(group => group.Count).Select(group => group.Color)
            .Concat(seed.Selection is null ? [] : SampleTextPalette(image, origin, seed.Selection.Bounds, seed.Rows ?? []));
        foreach (var color in learned)
        {
            var neutral = Chroma(color) < .25f;
            if (families.Any(other => neutral && Chroma(other) < .25f
                || !neutral && Chroma(other) >= .25f && HueDistance(color, other) <= 20)) continue;
            families.Add(color);
        }
        var normalized = scale;
        var glyph = heights.Length == 0 ? 24f : heights[heights.Length / 2];
        scale = Math.Min(Math.Clamp(72f / Math.Max(1, glyph), .35f, 5f),
            (float)Windows.Media.Ocr.OcrEngine.MaxImageDimension / Math.Max(image.Width, image.Height));
        var palettes = new[] { 1, 2, 4, families.Count }.Distinct().Where(count => count > 0 && count <= families.Count)
            .Select(count => (Colors: families.Take(count).ToArray(), Flexible: true))
            .Concat(families.Take(4).Select(color => (Colors: new[] { color }, Flexible: false)));
        var views = new List<(Func<Bitmap> Image, float Scale)>();
        var paletteViews = new Dictionary<string, int>();
        var paletteOrder = new List<int>();
        foreach (var (colors, flexible) in palettes)
        {
            var palette = colors.Select(color => (Color: color, Chroma: Chroma(color),
                Minimum: Math.Max(color.R, Math.Max(color.G, color.B)) * .85f)).ToArray();
            // At this fixed scale on this immutable image, the flexible branch
            // has no effect when every sampled color has low chroma. Reuse only
            // masks with identical rules, while keeping every candidate below.
            var key = string.Join(',', colors.Select(color => color.ToArgb()).Order())
                + ":" + (flexible && palette.Any(entry => entry.Chroma >= .55f));
            if (paletteViews.TryGetValue(key, out var reused))
            {
                paletteOrder.Add(reused);
                continue;
            }
            paletteViews.Add(key, views.Count);
            paletteOrder.Add(views.Count);
            views.Add((() => ConvertPixels(image, token, (red, green, blue) =>
            {
                foreach (var entry in palette)
                {
                    var color = entry.Color;
                    if (Math.Abs(color.R - red) <= 24 && Math.Abs(color.G - green) <= 24 && Math.Abs(color.B - blue) <= 24)
                        return (byte)0;
                    if (!flexible || Math.Max(red, Math.Max(green, blue)) < entry.Minimum || entry.Chroma < .55f)
                        continue;
                    var pixel = Color.FromArgb(red, green, blue);
                    if (Chroma(pixel) >= .2f && HueDistance(color, pixel) <= 20) return (byte)0;
                }
                return (byte)255;
            }), scale));
        }
        var secondaryStart = views.Count;
        scale = normalized;
        foreach (var cutoff in new[] { 150, 190, 205, 225, 245 })
        {
            views.Add((() => ConvertPixels(image, token,
                (red, green, blue) => Luminance(red, green, blue) >= cutoff ? (byte)0 : (byte)255), scale));
        }
        foreach (var cutoff in new[] { 205, 235 })
        {
            views.Add((() => LightTextMask(image, cutoff, token), scale));
        }
        // Equal luminance can hide text in grayscale. Try each channel symmetrically;
        // no hue is designated as foreground, and agreement still determines the result.
        for (var channel = 0; channel < 3; channel++)
        {
            var selectedChannel = channel;
            views.Add((() => ConvertPixels(image, token, (red, green, blue) =>
                selectedChannel == 0 ? red : selectedChannel == 1 ? green : blue), scale));
        }
        var readings = await ReadParagraphViewsAsync(views, origin, token).ConfigureAwait(false);
        // Workers may finish in any order; evidence retains the original order
        // and duplicate-view weights used by selection and text repair.
        foreach (var index in paletteOrder.Concat(Enumerable.Range(secondaryStart, views.Count - secondaryStart)))
            await Read(image, readings[index]).ConfigureAwait(false);
        var glyphHeight = heights.Length == 0 ? 18f : heights[heights.Length / 2];
        var observedArea = candidates.Where(candidate => seed.Selection is null
                || candidate.Selection.Bounds.IntersectsWith(seed.Selection.Bounds))
            .Select(candidate => candidate.Selection.Bounds).Aggregate(RectangleF.Empty,
                (bounds, next) => bounds.IsEmpty ? next : RectangleF.Union(bounds, next));
        var textArea = observedArea.IsEmpty ? new Rectangle(Point.Empty, image.Size)
            : Rectangle.Intersect(new Rectangle(Point.Empty, image.Size), Rectangle.Inflate(Rectangle.Ceiling(
                new RectangleF(observedArea.X - origin.X, observedArea.Y - origin.Y,
                    observedArea.Width, observedArea.Height)), 8, 8));
        using var textBlock = image.Clone(textArea, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var blockViews = new List<(Func<Bitmap> Image, Point Origin, float Scale, bool SingleLine, int Floor)>();
        foreach (var targetHeight in new[] { 32f, 44f })
        {
            blockViews.Add((() => CloneFrame(textBlock), new Point(origin.X + textArea.X, origin.Y + textArea.Y),
                Math.Clamp(targetHeight / glyphHeight, .5f, 5f), false, 0));
        }
        if (glyphHeight < 12)
            for (var channel = 0; channel < 3; channel++)
            {
                var selectedChannel = channel;
                foreach (var targetHeight in new[] { 32f, 44f })
                {
                    blockViews.Add((() => ConvertPixels(textBlock, token, (red, green, blue) =>
                            selectedChannel == 0 ? red : selectedChannel == 1 ? green : blue),
                        new Point(origin.X + textArea.X, origin.Y + textArea.Y),
                        Math.Clamp(targetHeight / glyphHeight, .5f, 6f), false, 0));
                }
            }
        foreach (var reading in await ReadParagraphLatinViewsAsync(blockViews, token).ConfigureAwait(false))
            if (reading.Confidence >= .7f) await Read(image, reading.Rows).ConfigureAwait(false);
        var refinements = new List<(ScreenLine[] Rows, Selection Selection, float Quality)>();
        var detailViews = new List<(Func<Bitmap> Image, Point Origin, float Scale, bool SingleLine, int Floor)>();
        var detailRows = new List<(ScreenLine Row, int Padding)>();
        var provisional = candidates.OrderByDescending(candidate => candidate.Quality + Agreement(candidate.Rows, candidate.Selection)).FirstOrDefault();
        var confirmations = candidates.Select(candidate =>
        {
            var words = candidate.Rows.SelectMany(line => line.Words)
                .Where(word => candidate.Selection.Bounds.Contains(word.Bounds)).ToArray();
            return (Words: words, Latin: words.Any(word => float.IsFinite(word.Confidence)));
        }).ToArray();
        foreach (var row in provisional.Rows?.Where(row => provisional.Selection.Bounds.Contains(row.Bounds)) ?? [])
        {
            // Two recognizers, each stable at both tested scales, already agree
            // on every observed token. Re-reading the same line adds no new
            // evidence; reserve the detailed pass for unresolved disagreements.
            bool Confirmed(ScreenWord word) => confirmations.Count(candidate => !candidate.Latin && Supports(candidate.Words, word)) >= 2
                && confirmations.Count(candidate => candidate.Latin && Supports(candidate.Words, word)) >= 2;
            if (row.Words.All(Confirmed)) continue;
            var uncertain = row.Words.Any(word => word.Text.Count(char.IsAsciiLetter) >= 3
                && (spelling.FrequencyRank(word.Text.Trim('.', ',', '!', '?', ':', ';', '(', ')', '"')) is 0 or > 15000
                    || candidates.Count(candidate => candidate.Rows.SelectMany(line => line.Words).Any(other =>
                        candidate.Selection.Bounds.Contains(other.Bounds) && other.Bounds.IntersectsWith(word.Bounds)
                        && other.Text == word.Text)) < candidates.Count * .9f));
            if (!uncertain) continue;
            var padding = Math.Max(3, (int)(row.Bounds.Height * .2f));
            var spans = new List<(int First, int Last)>();
            for (var index = 0; index < row.Words.Length; index++)
            {
                if (Confirmed(row.Words[index])) continue;
                // Keep neighboring words as recognition context. Only the
                // disputed span needs all four detailed readings again.
                var firstIndex = Math.Max(0, index - 1);
                var lastIndex = Math.Min(row.Words.Length - 1, index + 1);
                if (spans.Count != 0 && firstIndex <= spans[^1].Last + 1)
                    spans[^1] = (spans[^1].First, lastIndex);
                else spans.Add((firstIndex, lastIndex));
            }
            var regions = spans.Select(span => RectangleF.FromLTRB(row.Words[span.First].Bounds.Left,
                row.Bounds.Top, row.Words[span.Last].Bounds.Right, row.Bounds.Bottom)).ToArray();
            if (regions.Sum(region => region.Width) >= row.Bounds.Width * .7f) regions = [row.Bounds];
            foreach (var region in regions)
            {
                var area = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size), Rectangle.Inflate(Rectangle.Ceiling(
                    new RectangleF(region.X - origin.X, region.Y - origin.Y, region.Width, region.Height)), padding, padding));
                if (area.IsEmpty) continue;
                foreach (var floor in new[] { 0, 190 })
                foreach (var height in new[] { 32f, 44f })
                {
                    detailViews.Add((() => CloneFrame(image, area),
                        new Point(origin.X + area.X, origin.Y + area.Y), height / area.Height, true, floor));
                    detailRows.Add((row, padding));
                }
            }
        }
        var details = await ReadParagraphLatinViewsAsync(detailViews, token).ConfigureAwait(false);
        for (var index = 0; index < details.Length; index++)
        {
            var reading = details[index];
            if (reading.Confidence < .8f) continue;
            var (row, padding) = detailRows[index];
            var words = reading.Rows.SelectMany(line => line.Words).Select(word => ClipWordToLine(word, row.Bounds)).ToArray();
            if (words.Length == 0) continue;
            var bounds = RectangleF.Inflate(row.Bounds, padding, padding);
            var text = string.Join(' ', words.Select(word => word.Text));
            refinements.Add(([new ScreenLine(words, words.Select(word => word.Bounds).Aggregate(RectangleF.Union))],
                new Selection(text, text, bounds), 0));
        }
        var evidence = candidates.Concat(refinements).ToArray();
        var observedRows = candidates.SelectMany(candidate => candidate.Rows).ToArray();
        if (allowMapLabels && provisional.Selection is not null && provisional.Selection.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4
            && HasUncertainLabel(observedRows, cursor)
            && ReadMapLatin(image, origin, cursor, token, observedRows) is { } mapLabel)
            return [mapLabel];
        IEnumerable<(ScreenLine[] Rows, Selection Selection, float Quality)> eligible = candidates;
        var hasHeading = seed.Selection is not null && seed.Rows?.Any(row => row.Words.Length >= 4
            && row.Bounds.Bottom <= seed.Selection.Bounds.Top
            && seed.Selection.Bounds.Top - row.Bounds.Bottom <= row.Bounds.Height * .7f
            && row.Bounds.Width >= seed.Selection.Bounds.Width * .8f) == true;
        if (candidates.Count != 0 && !hasHeading)
        {
            var lists = candidates.Where(candidate => candidate.Selection.Text.Split('\n')
                .Count(line => TextSelection.ListItemParts(line).Prefix.Length != 0) >= 2).ToArray();
            if (lists.Length != 0 && lists.Max(candidate => Letters(candidate.Selection.Text).Length)
                >= candidates.Max(candidate => Letters(candidate.Selection.Text).Length) * .8f)
            {
                var itemCount = lists.Max(candidate => candidate.Selection.Text.Split('\n').Length);
                eligible = lists.Where(candidate => candidate.Selection.Text.Split('\n').Length == itemCount);
            }
            else if (HasListPrefix(candidates[0].Selection.Text))
                eligible = eligible.Where(candidate => HasListPrefix(candidate.Selection.Text));
        }
        var winner = eligible.OrderByDescending(candidate => candidate.Quality + Agreement(candidate.Rows, candidate.Selection)).FirstOrDefault();
        var best = winner.Rows ?? [];
        return best.Select(row => winner.Selection is not null && winner.Selection.Bounds.Contains(row.Bounds) ? RefineLine(row) : row).ToArray();

        static float HueDistance(Color firstColor, Color secondColor)
        {
            var difference = Math.Abs(firstColor.GetHue() - secondColor.GetHue());
            return Math.Min(difference, 360 - difference);
        }

        static bool HasListPrefix(string text) => TextSelection.ListItemParts(text.Split('\n')[0]).Prefix.Length != 0;

        static bool Supports(ScreenWord[] words, ScreenWord target) => words.Any(word => word.Text == target.Text
            && Math.Abs(word.Bounds.Top + word.Bounds.Height / 2 - target.Bounds.Top - target.Bounds.Height / 2) <= target.Bounds.Height * .4f
            && Math.Min(word.Bounds.Right, target.Bounds.Right) - Math.Max(word.Bounds.Left, target.Bounds.Left)
                >= Math.Min(word.Bounds.Width, target.Bounds.Width) * .5f
            && (!float.IsFinite(word.Confidence)
                || (float.IsFinite(word.LetterConfidence) ? word.LetterConfidence : word.Confidence) >= .85f));

        ScreenLine RefineLine(ScreenLine row)
        {
            var result = new List<ScreenWord>();
            for (var index = 0; index < row.Words.Length; index++)
            {
                (ScreenWord[] Words, int Length, int Gain, float Votes)? replacement = null;
                for (var length = 1; length <= 3 && index + length <= row.Words.Length; length++)
                {
                    var span = row.Words[index..(index + length)];
                    if (span.Any(word => TextSelection.IsStructuredCode(word.Text))) continue;
                    var oldText = string.Join(' ', span.Select(word => word.Text));
                    var observed = Letters(oldText);
                    var punctuationCluster = oldText.Count(char.IsPunctuation) >= 3 && observed.Length <= 1;
                    if (observed.Length == 0 && !punctuationCluster) continue;
                    var bounds = span.Select(word => word.Bounds).Aggregate(RectangleF.Union);
                    if (span.Zip(span.Skip(1), (firstWord, next) => next.Bounds.Left - firstWord.Bounds.Right)
                        .Any(gap => gap > bounds.Height * .7f)) continue;
                    var before = KnownLetters(oldText);
                    var readings = new List<ScreenWord[]>();
                    var sourceCounts = new float[2];
                    var originalCounts = new float[2];
                    foreach (var candidate in evidence)
                    {
                        var parts = candidate.Rows.SelectMany(line => line.Words).Where(other =>
                            candidate.Selection.Bounds.Contains(other.Bounds)
                            && Math.Abs(other.Bounds.Top + other.Bounds.Height / 2 - bounds.Top - bounds.Height / 2) <= bounds.Height * .4f
                            && other.Bounds.Left + other.Bounds.Width / 2 >= bounds.Left
                            && other.Bounds.Left + other.Bounds.Width / 2 <= bounds.Right).OrderBy(other => other.Bounds.Left).ToArray();
                        if (parts.Length is < 1 or > 3) continue;
                        var text = string.Join(' ', parts.Select(word => word.Text));
                        var source = parts.Any(word => float.IsFinite(word.Confidence)) ? 1 : 0;
                        var weight = EvidenceWeight(parts);
                        sourceCounts[source] += weight;
                        if (text == oldText)
                        {
                            originalCounts[source] += weight;
                            continue;
                        }
                        var tokens = Regex.Matches(text, @"[A-Za-z]+(?:['-][A-Za-z]+)*").Select(match => match.Value).ToArray();
                        var completion = ObservedCompletion(oldText, span, parts, row.Words);
                        var caseOnly = observed.Length >= 2 && oldText.Equals(text, StringComparison.OrdinalIgnoreCase);
                        if (observed.Length < 3 && !punctuationCluster && !completion && !caseOnly) continue;
                        var punctuation = Letters(text) == observed && text != oldText
                            && text.Count(char.IsWhiteSpace) == oldText.Count(char.IsWhiteSpace);
                        if (tokens.Length == 0 && !punctuation
                            && !(punctuationCluster && text.Count(char.IsPunctuation) >= 3)) continue;
                        var area = parts.Select(word => word.Bounds).Aggregate(RectangleF.Union);
                        var tolerance = bounds.Height * (punctuation ? 1f : .4f);
                        var editBudget = completion ? 3 : Math.Min(3, Math.Max(1, (int)Math.Ceiling(observed.Length * .4f)));
                        if (!completion && (Math.Abs(area.Left - bounds.Left) > tolerance || Math.Abs(area.Right - bounds.Right) > tolerance)
                            || EditDistance(observed, Letters(text), editBudget) > editBudget) continue;
                        readings.Add(parts);
                    }
                    foreach (var agreement in readings.GroupBy(parts => string.Join(' ', parts.Select(word => word.Text)), StringComparer.Ordinal)
                        .Where(group => group.Count() >= 2))
                    {
                        if (Letters(agreement.Key) == observed && RemovesObservedWordBoundary(span, agreement.Key, bounds.Height))
                            continue;
                        // A line recognizer can capitalize a cropped fragment as a new
                        // sentence. Keep the casing observed in the original image when
                        // the glyph sequence itself agrees.
                        if (oldText.Equals(agreement.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            var originalParts = originalReading.Rows?.SelectMany(line => line.Words)
                                .Where(word => Math.Abs(word.Bounds.Top + word.Bounds.Height / 2 - bounds.Top - bounds.Height / 2) <= bounds.Height * .4f
                                    && word.Bounds.Left + word.Bounds.Width / 2 >= bounds.Left
                                    && word.Bounds.Left + word.Bounds.Width / 2 <= bounds.Right)
                                .OrderBy(word => word.Bounds.Left).Select(word => word.Text) ?? [];
                            var originalText = string.Join(' ', originalParts);
                            if (originalText.Length != 0 && !HasAlternatingCase(originalText) && originalText != agreement.Key
                                || !HasAlternatingCase(oldText) && HasAlternatingCase(agreement.Key)) continue;
                        }
                        var revised = string.Join(' ', row.Words.Take(index).Select(word => word.Text)
                            .Concat([agreement.Key]).Concat(row.Words.Skip(index + length).Select(word => word.Text)));
                        if (UnpairedDelimiters(revised) > UnpairedDelimiters(row.Text)) continue;
                        // Preprocessing views of one recognizer are correlated. Give each
                        // recognizer the same total vote instead of outvoting the second model.
                        var votes = agreement.GroupBy(parts => parts.Any(word => float.IsFinite(word.Confidence)) ? 1 : 0)
                            .Sum(group => group.Sum(EvidenceWeight) / Math.Max(.01f, sourceCounts[group.Key]));
                        var initialVotes = Enumerable.Range(0, 2).Sum(source => originalCounts[source] / Math.Max(.01f, sourceCounts[source]));
                        // A larger box can include background noise. Extending a
                        // word must still beat the evidence for its observed form.
                        if (!RestoresNumericGlyph(oldText, agreement.Key)
                            && !ObservedSpacing(oldText, agreement.First(), bounds.Height)
                            && (votes < .5f || votes <= initialVotes + .001f)) continue;
                        var gain = Math.Max(1, KnownLetters(agreement.Key) - before);
                        if (replacement is null || votes > replacement.Value.Votes + .001f
                            || Math.Abs(votes - replacement.Value.Votes) <= .001f && gain > replacement.Value.Gain)
                            replacement = (agreement.First(), length, gain, votes);
                    }
                }
                if (replacement is { } repair)
                {
                    result.AddRange(repair.Words);
                    index += repair.Length - 1;
                }
                else result.Add(row.Words[index]);
            }
            var paragraph = winner.Selection.Bounds;
            var alternatives = evidence.SelectMany(candidate => candidate.Rows.SelectMany(line => line.Words)
                    .Where(word => candidate.Selection.Bounds.Contains(word.Bounds)))
                .Where(word => Math.Abs(word.Bounds.Top + word.Bounds.Height / 2 - row.Bounds.Top - row.Bounds.Height / 2) <= row.Bounds.Height * .4f)
                .OrderBy(word => word.Bounds.Left).ToArray();
            foreach (var word in alternatives.Concat(alternatives.Reverse()))
            {
                var token = word.Text.Trim('.', ',', '!', '?', ':', ';', '(', ')', '[', ']', '\"');
                var code = TextSelection.IsStructuredCode(token) || RelativeCodePath().IsMatch(token);
                if (token.Length == 0 || !code && (!token.All(char.IsAsciiLetter) || spelling.FrequencyRank(token) <= 0)
                    || word.Bounds.Height < row.Bounds.Height * .45f || word.Bounds.Height > row.Bounds.Height * 1.4f
                    || token.Length == 1 && word.Bounds.Width > row.Bounds.Height * .5f
                    || word.Bounds.Left < paragraph.Left - 2 || word.Bounds.Right > paragraph.Right + 2)
                    continue;
                if (result.Any(other => Math.Min(other.Bounds.Right, word.Bounds.Right) - Math.Max(other.Bounds.Left, word.Bounds.Left)
                        > Math.Min(other.Bounds.Width, word.Bounds.Width) * .25f
                    || other.Text.Equals(word.Text, StringComparison.OrdinalIgnoreCase)
                        && Math.Max(0, Math.Max(other.Bounds.Left - word.Bounds.Right, word.Bounds.Left - other.Bounds.Right))
                            <= row.Bounds.Height * .12f)) continue;
                var nearest = result.Select(other => Math.Max(0,
                    Math.Max(other.Bounds.Left - word.Bounds.Right, word.Bounds.Left - other.Bounds.Right))).DefaultIfEmpty(float.PositiveInfinity).Min();
                if (nearest > row.Bounds.Height * (code ? 1.2f : .8f)) continue;
                var support = evidence.Count(candidate => candidate.Rows.SelectMany(line => line.Words).Any(other =>
                    other.Text.Equals(word.Text, StringComparison.OrdinalIgnoreCase)
                    && candidate.Selection.Bounds.Contains(other.Bounds)
                    && Math.Abs(other.Bounds.Left + other.Bounds.Width / 2 - word.Bounds.Left - word.Bounds.Width / 2) < row.Bounds.Height * .35f
                    && Math.Abs(other.Bounds.Top + other.Bounds.Height / 2 - word.Bounds.Top - word.Bounds.Height / 2) < row.Bounds.Height * .35f));
                if (support >= 2) result.Add(word);
            }
            var ordered = result.OrderBy(word => word.Bounds.Left).ToArray();
            var clipped = ordered.Select(word => ClipWordToLine(word, row.Bounds)).ToArray();
            return row with { Words = clipped, Bounds = clipped.Select(word => word.Bounds).Aggregate(RectangleF.Union) };
        }

        static string Letters(string text) => string.Concat(text.Where(char.IsAsciiLetterOrDigit)).ToLowerInvariant();
        static float EvidenceWeight(ScreenWord[] words) => words.Where(word => float.IsFinite(word.Confidence))
            .Select(word => Math.Clamp(word.Confidence, .05f, 1)).DefaultIfEmpty(1).Average();
        static bool HasAlternatingCase(string text) => Regex.Matches(text, @"[A-Za-z]+")
            .Any(match => match.Value.Zip(match.Value.Skip(1), (first, next) => char.IsUpper(first) != char.IsUpper(next))
                .Count(change => change) >= 3);
        static bool RemovesObservedWordBoundary(ScreenWord[] span, string replacement, float height)
        {
            var breaks = new HashSet<int>();
            var position = 0;
            foreach (var part in replacement.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipLast(1))
            {
                position += Letters(part).Length;
                breaks.Add(position);
            }
            position = 0;
            for (var index = 0; index + 1 < span.Length; index++)
            {
                position += Letters(span[index].Text).Length;
                if (span[index + 1].Bounds.Left - span[index].Bounds.Right >= height * .2f && !breaks.Contains(position))
                    return true;
            }
            return false;
        }
        static bool RestoresNumericGlyph(string original, string observed) => original.Any(char.IsAsciiDigit)
            && observed.Count(char.IsAsciiDigit) > original.Count(char.IsAsciiDigit)
            && observed.Count(char.IsAsciiLetter) < original.Count(char.IsAsciiLetter);
        static bool ObservedSpacing(string original, ScreenWord[] parts, float height)
        {
            var observed = string.Join(' ', parts.Select(word => word.Text));
            return Letters(original) == Letters(observed) && observed.Count(char.IsWhiteSpace) > original.Count(char.IsWhiteSpace)
                && parts.Length >= 2 && parts.Zip(parts.Skip(1), (first, next) => next.Bounds.Left - first.Bounds.Right)
                    .All(gap => gap >= height * .2f);
        }
        static bool ObservedCompletion(string original, ScreenWord[] span, ScreenWord[] reading, ScreenWord[] row)
        {
            var before = Letters(original);
            var after = Letters(string.Join(' ', reading.Select(word => word.Text)));
            if (before.Length == 0 || after.Length <= before.Length || after.Length - before.Length > 3) return false;
            var bounds = span.Select(word => word.Bounds).Aggregate(RectangleF.Union);
            var area = reading.Select(word => word.Bounds).Aggregate(RectangleF.Union);
            var advance = bounds.Width / before.Length;
            var maximum = advance * (after.Length - before.Length + 2);
            var tail = char.IsAsciiLetterOrDigit(original.TrimEnd()[^1])
                && after.StartsWith(before, StringComparison.Ordinal) && Math.Abs(area.Left - bounds.Left) <= bounds.Height * .4f
                && area.Right - bounds.Right >= advance * .5f && area.Right - bounds.Right <= maximum;
            var head = char.IsAsciiLetterOrDigit(original.TrimStart()[0])
                && after.EndsWith(before, StringComparison.Ordinal) && Math.Abs(area.Right - bounds.Right) <= bounds.Height * .4f
                && bounds.Left - area.Left >= advance * .5f && bounds.Left - area.Left <= maximum;
            return (tail || head) && !row.Except(span).Any(word => area.IntersectsWith(word.Bounds));
        }
        int KnownLetters(string text) => KnownLetterCount(text);

        static int UnpairedDelimiters(string text) => new[] { ('(', ')'), ('[', ']'), ('{', '}'), ('<', '>') }
            .Sum(pair => Math.Abs(text.Count(letter => letter == pair.Item1) - text.Count(letter => letter == pair.Item2)));

        float Agreement(ScreenLine[] rows, Selection selection)
        {
            var score = 0f;
            foreach (var word in rows.SelectMany(row => row.Words).Where(word => selection.Bounds.Contains(word.Bounds)))
            {
                var text = string.Concat(word.Text.Where(char.IsLetterOrDigit)).ToLowerInvariant();
                if (text.Length == 0)
                {
                    var confirmations = candidates.Count(candidate => candidate.Rows.SelectMany(row => row.Words).Any(other =>
                        candidate.Selection.Bounds.Contains(other.Bounds) && word.Bounds.IntersectsWith(other.Bounds)
                        && other.Text == word.Text));
                    score += confirmations >= 2 ? Math.Min(3, word.Text.Length) * (.4f + .1f * Math.Min(5, confirmations)) : -1;
                    continue;
                }
                var votes = candidates.Count(candidate => candidate.Rows.SelectMany(row => row.Words).Any(other =>
                    word.Bounds.IntersectsWith(other.Bounds)
                    && candidate.Selection.Bounds.Contains(other.Bounds)
                    && text == string.Concat(other.Text.Where(char.IsLetterOrDigit)).ToLowerInvariant()));
                score += votes >= 2 ? text.Length * (.5f + .1f * Math.Min(votes - 2, 3)) : -text.Length * 2;
            }
            return score;
        }

        async Task Read(Bitmap source, ScreenLine[]? recognizedRows = null)
        {
            var rows = TextSelection.MergeInlineFragments(
                recognizedRows ?? await RecognizeBitmapAsync(source, origin, scale, token).ConfigureAwait(false));
            rows = RestoreObservedListMarkers(image, origin, rows);
            if (TextSelection.At(rows, cursor, true, null) is not { } selection)
                return;
            var words = Regex.Matches(selection.Text, @"[A-Za-z]+(?:['-][A-Za-z]+)*").Select(match => match.Value).ToArray();
            if (words.Length == 0)
                return;
            var letters = words.Sum(word => word.Length);
            var recognized = words.Where(word => spelling.Find(word) is not null).Sum(word => word.Length);
            var noise = selection.Text.Count(letter => !char.IsLetterOrDigit(letter) && !char.IsWhiteSpace(letter)
                && !".,!?;:'\"()-_/<>%*•".Contains(letter));
            var quality = letters * .25f + recognized * .75f - noise * 3;
            foreach (var (open, close) in new[] { ('(', ')'), ('[', ']'), ('{', '}'), ('<', '>') })
                quality -= Math.Abs(selection.Text.Count(letter => letter == open) - selection.Text.Count(letter => letter == close)) * 3;
            quality -= selection.Text.Count(letter => letter == '"') % 2 * 2;
            foreach (var row in rows.Where(row => selection.Bounds.Contains(row.Bounds)))
            {
                var tokens = Regex.Matches(row.Text, @"[A-Za-z]+").Select(match => match.Value).ToArray();
                var total = tokens.Sum(text => text.Length);
                if (tokens.Length >= 5 && tokens.Where(text => spelling.Find(text) is null).Sum(text => text.Length) > total * .4f)
                    quality -= total;
            }
            candidates.Add((rows, selection, quality));
        }
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.-]*(?:/[A-Za-z0-9_.<>-]+)+/?$", RegexOptions.CultureInvariant)]
    private static partial Regex RelativeCodePath();

    private static ScreenWord ClipWordToLine(ScreenWord word, RectangleF line)
    {
        var top = Math.Max(word.Bounds.Top, line.Top);
        var bottom = Math.Min(word.Bounds.Bottom, line.Bottom);
        return word with { Bounds = RectangleF.FromLTRB(word.Bounds.Left, bottom > top ? top : line.Top,
            word.Bounds.Right, bottom > top ? bottom : line.Bottom) };
    }
}
