using System.Drawing.Imaging;
using Windows.Media.Ocr;

namespace ScreenTranslator;

internal sealed partial class ScreenOcr
{
    private static readonly int ParagraphWorkers = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
    private OcrEngine[]? paragraphOcrEngines;
    private LocalLatinOcr[]? paragraphLatinEngines;

    private OcrEngine[] ParagraphOcrEngines => paragraphOcrEngines ??= Enumerable.Range(0, ParagraphWorkers)
        .Select(index => index == 0 ? engine : OcrEngine.TryCreateFromLanguage(engine.RecognizerLanguage))
        .OfType<OcrEngine>().ToArray();

    private LocalLatinOcr[] ParagraphLatinEngines => paragraphLatinEngines ??= Enumerable.Range(0, ParagraphWorkers)
        .Select(index => index == 0 ? latinOcr.Value : new LocalLatinOcr()).ToArray();

    internal async Task<ScreenLine[][]> ReadParagraphViewsAsync(
        IReadOnlyList<(Func<Bitmap> Image, float Scale)> views, Point origin, CancellationToken token)
    {
        var engines = ParagraphOcrEngines;
        var results = new ScreenLine[views.Count][];
        for (var start = 0; start < views.Count; start += engines.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(engines.Length, views.Count - start);
            var images = new Bitmap[count];
            try
            {
                // GDI+ bitmaps are not shared across workers. Prepare from the
                // source serially, and retain at most one batch of images.
                for (var index = 0; index < count; index++) images[index] = views[start + index].Image();
                var tasks = Enumerable.Range(0, count).Select(index => Task.Run(() => RecognizeBitmapAsync(images[index], origin,
                    views[start + index].Scale, CancellationToken.None, engines[index]))).ToArray();
                var batch = await Task.WhenAll(tasks).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Array.Copy(batch, 0, results, start, count);
            }
            finally
            {
                foreach (var image in images) image?.Dispose();
            }
        }
        return results;
    }

    internal async Task<(ScreenLine[] Rows, float Confidence)[]> ReadParagraphLatinViewsAsync(
        IReadOnlyList<(Func<Bitmap> Image, Point Origin, float Scale, bool SingleLine, int Floor)> views,
        CancellationToken token)
    {
        var engines = ParagraphLatinEngines;
        var results = new (ScreenLine[] Rows, float Confidence)[views.Count];
        for (var start = 0; start < views.Count; start += engines.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(engines.Length, views.Count - start);
            var images = new Bitmap[count];
            try
            {
                for (var index = 0; index < count; index++) images[index] = views[start + index].Image();
                var tasks = Enumerable.Range(0, count).Select(index => Task.Run(() =>
                {
                    var view = views[start + index];
                    return engines[index].Read(images[index], view.Origin, view.Scale,
                        CancellationToken.None, view.SingleLine, view.Floor);
                })).ToArray();
                var batch = await Task.WhenAll(tasks).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Array.Copy(batch, 0, results, start, count);
            }
            finally
            {
                foreach (var image in images) image?.Dispose();
            }
        }
        return results;
    }

    private static unsafe Bitmap CloneFrame(Bitmap image, Rectangle? area = null)
    {
        var region = area ?? new Rectangle(Point.Empty, image.Size);
        var copy = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        try
        {
            var source = image.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var destination = copy.LockBits(new Rectangle(Point.Empty, copy.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (var row = 0; row < region.Height; row++)
                        Buffer.MemoryCopy((byte*)source.Scan0 + row * source.Stride,
                            (byte*)destination.Scan0 + row * destination.Stride, region.Width * 4, region.Width * 4);
                }
                finally { copy.UnlockBits(destination); }
            }
            finally { image.UnlockBits(source); }
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }
}
