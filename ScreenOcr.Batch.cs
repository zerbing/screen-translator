using System.Drawing.Imaging;
using System.Security.Cryptography;
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

    internal Task<ScreenLine[][]> ReadParagraphViewsAsync(
        IReadOnlyList<(Func<Bitmap> Image, float Scale)> views, Point origin, CancellationToken token)
    {
        // Different thresholds/channels sometimes produce exactly the same
        // pixels. Reuse that observation within this request, while returning
        // every view in its original position for the unchanged evidence logic.
        var readings = new Dictionary<(Size Size, float Scale, string Pixels), TaskCompletionSource<ScreenLine[]>>();
        return RunOcrWorkersAsync(views, ParagraphOcrEngines, view => view.Image(), async (reader, view, image) =>
        {
            var key = (image.Size, view.Scale, HashPixels(image));
            TaskCompletionSource<ScreenLine[]> completion;
            bool owner;
            lock (readings)
            {
                owner = !readings.TryGetValue(key, out completion!);
                if (owner)
                    readings.Add(key, completion = new(TaskCreationOptions.RunContinuationsAsynchronously));
            }
            if (!owner) return await completion.Task.ConfigureAwait(false);
            try
            {
                var result = await RecognizeBitmapAsync(image, origin, view.Scale, CancellationToken.None, reader).ConfigureAwait(false);
                completion.SetResult(result);
                return result;
            }
            catch (Exception error)
            {
                completion.SetException(error);
                _ = completion.Task.Exception;
                throw;
            }
        }, token);
    }

    internal Task<(ScreenLine[] Rows, float Confidence)[]> ReadParagraphLatinViewsAsync(
        IReadOnlyList<(Func<Bitmap> Image, Point Origin, float Scale, bool SingleLine, int Floor)> views,
        CancellationToken token) =>
        RunOcrWorkersAsync(views, ParagraphLatinEngines, view => view.Image(),
            (reader, view, image) => Task.FromResult(reader.Read(image, view.Origin, view.Scale,
                CancellationToken.None, view.SingleLine, view.Floor)), token);

    internal static async Task<TResult[]> RunOcrWorkersAsync<TView, TReader, TResult>(
        IReadOnlyList<TView> views, IReadOnlyList<TReader> readers, Func<TView, Bitmap> prepare,
        Func<TReader, TView, Bitmap, Task<TResult>> read, CancellationToken token)
    {
        if (views.Count != 0 && readers.Count == 0)
            throw new ArgumentException("At least one OCR reader is required.", nameof(readers));
        var results = new TResult[views.Count];
        var preparation = new object();
        var next = 0;
        var stopped = false;
        var tasks = readers.Take(Math.Min(readers.Count, views.Count)).Select(reader => Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    int index;
                    Bitmap image;
                    lock (preparation)
                    {
                        if (stopped || next == views.Count) return;
                        token.ThrowIfCancellationRequested();
                        index = next++;
                        // Factories can read the same immutable source bitmap.
                        // Serialize GDI+ access; each worker owns its output image.
                        image = prepare(views[index]);
                    }
                    using (image)
                    {
                        token.ThrowIfCancellationRequested();
                        // One reader stays on one worker. Start the next view as
                        // soon as it is free, without waiting for a batch barrier.
                        results[index] = await read(reader, views[index], image).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                lock (preparation) stopped = true;
                throw;
            }
        })).ToArray();
        // Cancellation or a failed factory must still drain every native read
        // already in flight before the caller can dispose/reuse its resources.
        await Task.WhenAll(tasks).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return results;
    }

    private static unsafe string HashPixels(Bitmap image)
    {
        var pixels = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = checked(image.Width * 4);
            if (pixels.Stride == rowBytes)
                return Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>((byte*)pixels.Scan0, checked(rowBytes * image.Height))));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var y = 0; y < image.Height; y++)
                hash.AppendData(new ReadOnlySpan<byte>((byte*)pixels.Scan0 + y * pixels.Stride, rowBytes));
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { image.UnlockBits(pixels); }
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
