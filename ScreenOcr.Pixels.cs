using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using WinRT;

namespace ScreenTranslator;

internal sealed partial class ScreenOcr
{
    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr pointer, out uint capacity);
    }

    internal static unsafe SoftwareBitmap CopyOcrPixels(Bitmap source, CancellationToken token)
    {
        var input = new SoftwareBitmap(BitmapPixelFormat.Bgra8, source.Width, source.Height, BitmapAlphaMode.Ignore);
        try
        {
            using var buffer = input.LockBuffer(BitmapBufferAccessMode.Write);
            using var reference = buffer.CreateReference();
            reference.As<IMemoryBufferByteAccess>().GetBuffer(out var pointer, out var capacity);
            var plane = buffer.GetPlaneDescription(0);
            var pixels = source.LockBits(new Rectangle(Point.Empty, source.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var rowBytes = checked(source.Width * 4);
                for (var y = 0; y < source.Height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    var offset = checked(plane.StartIndex + y * plane.Stride);
                    Buffer.MemoryCopy((byte*)pixels.Scan0 + y * pixels.Stride, (byte*)pointer + offset,
                        capacity - offset, rowBytes);
                }
            }
            finally { source.UnlockBits(pixels); }
            return input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }
}
