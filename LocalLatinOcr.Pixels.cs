using System.Drawing.Imaging;
using Tesseract;

namespace ScreenTranslator;

internal sealed partial class LocalLatinOcr
{
    internal static unsafe Pix CopyPixels(Bitmap bitmap, CancellationToken token)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var pix = Pix.Create(width, height, 32);
        try
        {
            pix.XRes = (int)Math.Round(bitmap.HorizontalResolution);
            pix.YRes = (int)Math.Round(bitmap.VerticalResolution);
            var output = pix.GetData();
            var input = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                for (var y = 0; y < height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    var source = (byte*)input.Scan0 + y * input.Stride;
                    var destination = (uint*)output.Data + y * output.WordsPerLine;
                    for (var x = 0; x < width; x++)
                        destination[x] = (uint)(source[x * 3 + 2] << 24 | source[x * 3 + 1] << 16 | source[x * 3] << 8);
                }
            }
            finally { bitmap.UnlockBits(input); }
            return pix;
        }
        catch
        {
            pix.Dispose();
            throw;
        }
    }
}
