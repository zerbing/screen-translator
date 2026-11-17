using System.Drawing.Imaging;
using ScreenTranslator;

internal static class OcrBatchTests
{
    internal static async Task<(string Name, string Expected, string Actual)[]> RunAsync(ScreenOcr ocr)
    {
        const string sentence = "Recognition remains complete after cancellation.";
        var windows = Enumerable.Range(0, 24).Select(_ => ((Func<Bitmap>)Image, 1f)).ToArray();
        var latin = Enumerable.Range(0, 24).Select(_ => ((Func<Bitmap>)Image, Point.Empty, 1f, true, 0)).ToArray();
        await ocr.ReadParagraphViewsAsync(windows[..1], Point.Empty, default);
        await ocr.ReadParagraphLatinViewsAsync(latin[..1], default);
        var results = new List<(string, string, string)>();
        foreach (var useLatin in new[] { false, true })
        {
            using var cancelled = new CancellationTokenSource();
            var prepared = 0;
            Bitmap CancellingImage()
            {
                var image = Image();
                if (++prepared == 2) cancelled.Cancel();
                return image;
            }
            var pendingWindows = windows.Select(view => ((Func<Bitmap>)CancellingImage, view.Item2)).ToArray();
            var pendingLatin = latin.Select(view => ((Func<Bitmap>)CancellingImage, view.Item2, view.Item3, view.Item4, view.Item5)).ToArray();
            var stopped = false;
            try
            {
                if (useLatin) await ocr.ReadParagraphLatinViewsAsync(pendingLatin, cancelled.Token);
                else await ocr.ReadParagraphViewsAsync(pendingWindows, Point.Empty, cancelled.Token);
            }
            catch (OperationCanceledException) { stopped = true; }
            var rows = useLatin
                ? (await ocr.ReadParagraphLatinViewsAsync(latin[..1], default))[0].Rows
                : (await ocr.ReadParagraphViewsAsync(windows[..1], Point.Empty, default))[0];
            results.Add(($"{(useLatin ? "Latin" : "Windows")} cancelled batch drains before reuse", "True", stopped.ToString()));
            results.Add(($"{(useLatin ? "Latin" : "Windows")} batch reads complete text after cancellation", sentence,
                string.Join(' ', rows.Select(row => row.Text))));
        }
        return results.ToArray();

        static Bitmap Image()
        {
            var image = new Bitmap(800, 160, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(image);
            using var font = new Font("Segoe UI", 25, GraphicsUnit.Pixel);
            graphics.Clear(Color.White);
            graphics.DrawString(sentence, font, Brushes.Black, 20, 35);
            return image;
        }
    }
}
