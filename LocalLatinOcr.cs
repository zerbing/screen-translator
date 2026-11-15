using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Tesseract;

namespace ScreenTranslator;

// A second local recognizer for small Latin runs that Windows OCR splits or drops.
// Images stay in memory; neither recognizer sends screen contents to a service.
internal sealed class LocalLatinOcr : IDisposable
{
    private readonly TesseractEngine engine;
    private readonly bool mixed;

    internal LocalLatinOcr(bool mixed = false)
    {
        this.mixed = mixed;
        TesseractEnviornment.CustomSearchPath = AppContext.BaseDirectory;
        engine = new TesseractEngine(Path.Combine(AppContext.BaseDirectory, "OcrModels"),
            mixed ? "chi_sim+eng" : "eng", EngineMode.LstmOnly);
        engine.SetVariable("debug_file", "NUL");
    }

    internal unsafe (ScreenLine[] Rows, float Confidence) Read(Bitmap image, Point origin, float scale,
        CancellationToken token, bool singleLine = true, int lightFloor = 0)
    {
        token.ThrowIfCancellationRequested();
        const int padding = 12;
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var padded = new Bitmap(width + padding * 2, height + padding * 2, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(padded))
        {
            graphics.Clear(image.GetPixel(0, 0));
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image, new Rectangle(padding, padding, width, height));
        }
        if (lightFloor > 0)
        {
            var data = padded.LockBits(new Rectangle(Point.Empty, padded.Size), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                for (var y = 0; y < padded.Height; y++)
                    for (var x = 0; x < padded.Width; x++)
                    {
                        var pixel = (byte*)data.Scan0 + y * data.Stride + x * 3;
                        var value = (byte)(255 - Math.Clamp((Math.Min(pixel[0], Math.Min(pixel[1], pixel[2])) - lightFloor)
                            * 255 / (240 - lightFloor), 0, 255));
                        pixel[0] = pixel[1] = pixel[2] = value;
                    }
            }
            finally { padded.UnlockBits(data); }
        }
        using var stream = new MemoryStream();
        padded.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        using var pix = Pix.LoadFromMemory(stream.ToArray());
        using var page = engine.Process(pix, singleLine ? PageSegMode.SingleLine : PageSegMode.SingleBlock);
        using var iterator = page.GetIterator();
        var words = new List<ScreenWord>();
        var confidences = new List<float>();
        string? text = null;
        var confidence = 0f;
        Rectangle? symbolBounds = null;
        iterator.Begin();
        do
        {
            if (iterator.IsAtBeginningOf(PageIteratorLevel.Word))
            {
                text = iterator.GetText(PageIteratorLevel.Word)?.Trim().Replace('“', '"').Replace('”', '"').Replace('’', '\'');
                confidence = iterator.GetConfidence(PageIteratorLevel.Word) / 100;
                symbolBounds = null;
            }
            if (iterator.TryGetBoundingBox(PageIteratorLevel.Symbol, out var symbol))
            {
                var rectangle = new Rectangle(symbol.X1, symbol.Y1, symbol.Width, symbol.Height);
                symbolBounds = symbolBounds is { } previous ? Rectangle.Union(previous, rectangle) : rectangle;
            }
            if (iterator.IsAtFinalOf(PageIteratorLevel.Word, PageIteratorLevel.Symbol)
                && !string.IsNullOrEmpty(text) && symbolBounds is { } box)
            {
                words.Add(new ScreenWord(text, new RectangleF(origin.X + (box.X - padding) / scale,
                    origin.Y + (box.Y - padding) / scale, box.Width / scale, box.Height / scale)));
                confidences.Add(confidence);
            }
        } while (iterator.Next(PageIteratorLevel.Symbol));
        token.ThrowIfCancellationRequested();
        if (mixed)
        {
            // Tesseract's Latin run can inherit the final Han word's right edge.
            // Use the following Han run to restore the script boundary.
            var han = words.Where(word => word.Text.Any(ScreenOcr.IsHan)).ToArray();
            for (var index = 0; index < words.Count; index++)
            {
                var word = words[index];
                if (!word.Text.Any(char.IsAsciiLetter) || word.Text.Any(ScreenOcr.IsHan))
                    continue;
                var next = han.Where(other => other.Bounds.Left > word.Bounds.Left + word.Bounds.Height * .5f
                        && other.Bounds.Left < word.Bounds.Right && other.Bounds.Bottom > word.Bounds.Top
                        && other.Bounds.Top < word.Bounds.Bottom).MinBy(other => other.Bounds.Left);
                if (next is not null)
                    words[index] = word with
                    {
                        Bounds = RectangleF.FromLTRB(word.Bounds.Left, word.Bounds.Top,
                        next.Bounds.Left, word.Bounds.Bottom)
                    };
            }
            var latin = words.Where(word => word.Text.Any(char.IsAsciiLetter) && !word.Text.Any(ScreenOcr.IsHan)).ToArray();
            for (var index = 0; index < words.Count; index++)
            {
                var word = words[index];
                if (!word.Text.Any(ScreenOcr.IsHan))
                    continue;
                var next = latin.Where(other => other.Bounds.Left > word.Bounds.Left
                        && other.Bounds.Left < word.Bounds.Right
                        && word.Bounds.Right - other.Bounds.Left < word.Bounds.Height * .4f).MinBy(other => other.Bounds.Left);
                if (next is not null)
                    words[index] = word with
                    {
                        Bounds = RectangleF.FromLTRB(word.Bounds.Left, word.Bounds.Top,
                        next.Bounds.Left, word.Bounds.Bottom)
                    };
            }
        }
        return (ScreenOcr.BuildLines(words).ToArray(), confidences.Count == 0 ? 0 : confidences.Average());
    }

    public void Dispose() => engine.Dispose();
}
