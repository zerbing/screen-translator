using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenTranslator;
using Windows.Storage.Streams;

internal static class OcrPixelTests
{
    internal static IEnumerable<(string Name, string Expected, string Actual)> Run()
    {
        Tesseract.TesseractEnviornment.CustomSearchPath = AppContext.BaseDirectory;
        foreach (var grayscale in new[] { false, true })
        {
            using var bitmap = new Bitmap(127, 37, PixelFormat.Format24bppRgb);
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var value = (x * 31 + y * 17) % 256;
                bitmap.SetPixel(x, y, grayscale ? Color.FromArgb(value, value, value)
                    : Color.FromArgb(value, (value + 57) % 256, (value + 113) % 256));
            }
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            using var reference = Tesseract.Pix.LoadFromMemory(stream.ToArray());
            using var direct = LocalLatinOcr.CopyPixels(bitmap, default);
            var expected = new int[reference.GetData().WordsPerLine * reference.Height];
            var actual = new int[direct.GetData().WordsPerLine * direct.Height];
            Marshal.Copy(reference.GetData().Data, expected, 0, expected.Length);
            Marshal.Copy(direct.GetData().Data, actual, 0, actual.Length);
            yield return ($"direct Latin pixels preserve PNG decoded data and DPI for grayscale={grayscale}",
                $"{reference.Depth}/{reference.XRes}/{reference.YRes}/" + PixelHash(expected),
                $"{direct.Depth}/{direct.XRes}/{direct.YRes}/" + PixelHash(actual));
        }
        foreach (var width in new[] { 3, 127 })
        foreach (var reversed in new[] { false, true })
        {
            const int height = 13;
            var stride = width * 4 + 16;
            var storage = Enumerable.Repeat((byte)0xd7, stride * height).ToArray();
            var expected = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var source = (reversed ? height - y - 1 : y) * stride + x * 4;
                var target = (y * width + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                    expected[target + channel] = storage[source + channel] =
                        (byte)((x * 37 + y * 19 + channel * 53) % 256);
            }
            var pointer = Marshal.AllocHGlobal(storage.Length);
            try
            {
                Marshal.Copy(storage, 0, pointer, storage.Length);
                using var bitmap = new Bitmap(width, height, reversed ? -stride : stride, PixelFormat.Format32bppArgb,
                    reversed ? IntPtr.Add(pointer, (height - 1) * stride) : pointer);
                using var copied = ScreenOcr.CopyOcrPixels(bitmap, default);
                var buffer = new Windows.Storage.Streams.Buffer((uint)expected.Length);
                copied.CopyToBuffer(buffer);
                using var reader = DataReader.FromBuffer(buffer);
                var actual = new byte[expected.Length];
                reader.ReadBytes(actual);
                yield return ($"OCR pixels preserve BGRA and padded {(reversed ? "negative" : "positive")} stride at {width}",
                    Convert.ToHexString(expected), Convert.ToHexString(actual));
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                var rejected = false;
                try { using var ignored = ScreenOcr.CopyOcrPixels(bitmap, cancelled.Token); }
                catch (OperationCanceledException) { rejected = true; }
                using var retry = ScreenOcr.CopyOcrPixels(bitmap, default);
                yield return ($"cancelled OCR copy releases bitmap lock at {width}/{reversed}", "True", rejected.ToString());
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
    }

    private static string PixelHash(int[] pixels) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(MemoryMarshal.AsBytes(pixels.AsSpan())));
}
