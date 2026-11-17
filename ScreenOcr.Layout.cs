using System.Drawing.Imaging;

namespace ScreenTranslator;

internal sealed partial class ScreenOcr
{
    internal CapturedFrame CaptureFrame(Rectangle area, CancellationToken token) =>
        new(this, Capture(area, token), area);

    internal async Task<ScreenLine[]> ReadLayoutBitmapAsync(Bitmap image, Point origin, float scale,
        Point cursor, CancellationToken token)
    {
        var rows = TextSelection.MergeInlineFragments(
            await RecognizeBitmapAsync(image, origin, scale, token).ConfigureAwait(false));
        rows = RestoreObservedListMarkers(image, origin, rows);
        if (TextSelection.At(rows, cursor, true, null) is not null) return rows;
        var local = new Point(cursor.X - origin.X, cursor.Y - origin.Y);
        if (SampleLetteringColor(image, local) is not { } fill) return rows;
        using var mask = LightTextMask(image, 0, token, fill);
        return RestoreObservedListMarkers(image, origin, TextSelection.MergeInlineFragments(
            await RecognizeBitmapAsync(mask, origin, scale, token).ConfigureAwait(false)));
    }

    // One immutable screen frame prevents progress feedback or scene animation
    // from entering later layout and recognition passes of the same request.
    internal sealed class CapturedFrame(ScreenOcr owner, Bitmap bitmap, Rectangle area) : IDisposable
    {
        public async Task<ScreenLine[]> ReadAsync(Rectangle region, float scale, Point cursor,
            Func<ScreenLine[], bool>? hasTarget, CancellationToken token, bool mixed, bool large, bool paragraph)
        {
            using var image = Crop(region);
            return await owner.ReadBitmapAsync(image, region.Location, scale, cursor,
                hasTarget, token, mixed, large, paragraph).ConfigureAwait(false);
        }

        public async Task<ScreenLine[]?> ReadOptionAsync(Rectangle region, Point cursor, CancellationToken token)
        {
            using var image = Crop(region);
            return await owner.ReadOptionBitmapAsync(image, region.Location, cursor, token).ConfigureAwait(false);
        }

        public async Task<ScreenLine[]> ReadLayoutAsync(Rectangle region, float scale, Point cursor, CancellationToken token)
        {
            using var image = Crop(region);
            return await owner.ReadLayoutBitmapAsync(image, region.Location, scale, cursor, token).ConfigureAwait(false);
        }

        private Bitmap Crop(Rectangle region) => bitmap.Clone(new Rectangle(region.X - area.X,
            region.Y - area.Y, region.Width, region.Height), PixelFormat.Format32bppArgb);

        public void Dispose() => bitmap.Dispose();
    }
}
