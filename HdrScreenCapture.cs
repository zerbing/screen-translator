using System.Drawing.Imaging;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Box = Vortice.Mathematics.Box;

namespace ScreenTranslator;

internal sealed class HdrScreenCapture : IDisposable
{
    private const int FrameTimeout = 126;
    private const int PositiveHalfLimit = 0x7c00;
    private const int SrgbSteps = 8192;
    private readonly object gate = new();
    private IDXGIFactory1? factory;
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private Luid deviceAdapter;
    private float referenceWhite;
    private float[]? gainTable;
    private bool disposed;

    public Bitmap? TryRead(Rectangle area, CancellationToken token)
    {
        lock (gate)
            return disposed ? null : TryReadCore(area, token);
    }

    private Bitmap? TryReadCore(Rectangle area, CancellationToken token)
    {
        try
        {
            if (factory is null || !factory.IsCurrent)
            {
                factory?.Dispose();
                factory = null;
                factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            }
            for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
            using (adapter)
            for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
            using (output)
            using (var hdr = output.QueryInterfaceOrNull<IDXGIOutput6>())
            {
                if (hdr is null)
                    continue;
                var description = hdr.Description1;
                var edges = description.DesktopCoordinates;
                var screen = Rectangle.FromLTRB(edges.Left, edges.Top, edges.Right, edges.Bottom);
                if (!description.AttachedToDesktop || !screen.Contains(area))
                    continue;
                if (description.ColorSpace != ColorSpaceType.RgbFullG2084NoneP2020
                    || description.Rotation != ModeRotation.Identity)
                    return null;

                token.ThrowIfCancellationRequested();
                var relative = new Rectangle(area.X - screen.X, area.Y - screen.Y, area.Width, area.Height);
                var (graphicsDevice, graphicsContext) = GetDevice(adapter);
                return ReadRegion(graphicsDevice, graphicsContext, hdr, relative,
                    GetGainTable(DisplayWhiteLevel.Read(description.DeviceName)), token);
            }
        }
        catch (Exception error) when (error is SharpGenException or DllNotFoundException or EntryPointNotFoundException)
        {
            DiagnosticLog.Write("capture.hdr.error", $"type={error.GetType().Name} hresult={error.HResult:X8}");
            // The desktop can change modes or reject duplication while a game is opening.
            if (device?.DeviceRemovedReason.Failure == true)
                ReleaseDevice();
        }
        return null;
    }

    private (ID3D11Device Device, ID3D11DeviceContext Context) GetDevice(IDXGIAdapter1 adapter)
    {
        var adapterId = adapter.Description1.Luid;
        if (device is not null && context is not null && deviceAdapter.Equals(adapterId))
            return (device, context);

        ReleaseDevice();
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out var createdDevice, out _, out var createdContext).CheckError();
        device = createdDevice;
        context = createdContext;
        deviceAdapter = adapterId;
        return (createdDevice, createdContext);
    }

    private static Bitmap? ReadRegion(ID3D11Device device, ID3D11DeviceContext context, IDXGIOutput6 output,
        Rectangle area, float[] gains, CancellationToken token)
    {
        // Only keep the desktop frame while submitting the copy. GPU readback
        // and CPU conversion must not hold a game's fullscreen surface lease.
        using var staging = CopyRegion(device, context, output, area, token);
        if (staging is null)
            return null;

        var result = ReadStaging(context, staging, area.Size, gains, token);
        if (result is null) DiagnosticLog.Write("capture.hdr.empty");
        return result;
    }

    private static ID3D11Texture2D? CopyRegion(ID3D11Device device, ID3D11DeviceContext context,
        IDXGIOutput6 output, Rectangle area, CancellationToken token)
    {
        Format[] formats = [Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm];
        using var duplication = output.DuplicateOutput1(device, (uint)formats.Length, formats);
        var deadline = Environment.TickCount64 + FrameTimeout;
        while (Environment.TickCount64 < deadline)
        {
            token.ThrowIfCancellationRequested();
            var remaining = (uint)Math.Max(1, deadline - Environment.TickCount64);
            var result = duplication.AcquireNextFrame(remaining, out var frame, out var resource);
            if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                DiagnosticLog.Write("capture.hdr.frame.timeout");
                break;
            }
            result.CheckError();
            try
            {
                using (resource)
                {
                    // A pointer-only update can have an uninitialized desktop texture.
                    if (frame.LastPresentTime == 0 || frame.AccumulatedFrames == 0)
                        continue;
                    using var texture = resource.QueryInterface<ID3D11Texture2D>();
                    var desc = texture.Description;
                    if (desc.Format != Format.R16G16B16A16_Float || area.Right > desc.Width || area.Bottom > desc.Height)
                        return null;

                    var cropped = new Texture2DDescription(desc.Format, (uint)area.Width, (uint)area.Height, 1, 1,
                        BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
                    var staging = device.CreateTexture2D(cropped);
                    try
                    {
                        context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0,
                            new Box(area.Left, area.Top, 0, area.Right, area.Bottom, 1));
                        context.Flush();
                        return staging;
                    }
                    catch
                    {
                        staging.Dispose();
                        throw;
                    }
                }
            }
            finally { duplication.ReleaseFrame(); }
        }
        return null;
    }

    internal static Bitmap? ReadStaging(ID3D11DeviceContext context, ID3D11Texture2D staging,
        Size size, float[] gains, CancellationToken token)
    {
        var deadline = Environment.TickCount64 + FrameTimeout;
        while (Environment.TickCount64 < deadline)
        {
            token.ThrowIfCancellationRequested();
            var result = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out var map);
            if (result == Vortice.DXGI.ResultCode.WasStillDrawing)
            {
                // A mode transition can stall the GPU. Stay cancellable and
                // let the caller use the normal screen capture fallback.
                Thread.Sleep(1);
                continue;
            }
            result.CheckError();
            try { return ConvertFrame(map, size, gains, token); }
            finally { context.Unmap(staging, 0); }
        }
        token.ThrowIfCancellationRequested();
        DiagnosticLog.Write("capture.hdr.readback.timeout");
        return null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            ReleaseDevice();
            factory?.Dispose();
            factory = null;
        }
    }

    private void ReleaseDevice()
    {
        context?.Dispose();
        context = null;
        device?.Dispose();
        device = null;
    }

    private float[] GetGainTable(float sdrWhite)
    {
        if (gainTable is not null && referenceWhite == sdrWhite)
            return gainTable;

        var gains = new float[PositiveHalfLimit];
        for (var index = 1; index < gains.Length; index++)
        {
            var peak = ColorTables.Linear[index];
            gains[index] = ToneMapPeak(peak, sdrWhite) / peak;
        }
        referenceWhite = sdrWhite;
        gainTable = gains;
        return gains;
    }

    private static unsafe Bitmap? ConvertFrame(MappedSubresource map, Size size, float[] gains, CancellationToken token)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        try
        {
            var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var containsImage = false;
            try
            {
                for (var y = 0; y < size.Height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    var source = new ReadOnlySpan<ushort>((byte*)map.DataPointer + y * map.RowPitch, size.Width * 4);
                    var target = new Span<byte>((byte*)data.Scan0 + y * data.Stride, size.Width * 4);
                    for (var x = 0; x < source.Length; x += 4)
                    {
                        var r = source[x] < PositiveHalfLimit ? source[x] : 0;
                        var g = source[x + 1] < PositiveHalfLimit ? source[x + 1] : 0;
                        var b = source[x + 2] < PositiveHalfLimit ? source[x + 2] : 0;
                        // Nonnegative finite FP16 values sort in the same order as their unsigned bit patterns.
                        var peak = Math.Max(r, Math.Max(g, b));
                        containsImage |= peak > 0;
                        var gain = gains[peak];
                        target[x] = EncodeSrgb(ColorTables.Linear[b] * gain);
                        target[x + 1] = EncodeSrgb(ColorTables.Linear[g] * gain);
                        target[x + 2] = EncodeSrgb(ColorTables.Linear[r] * gain);
                        target[x + 3] = 255;
                    }
                }
            }
            finally { bitmap.UnlockBits(data); }
            if (containsImage)
                return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        bitmap.Dispose();
        return null;
    }

    internal static float ToneMapPeak(float linearPeak, float sdrWhite)
    {
        const float knee = 0.59f;
        const float targetKnee = 0.74f;
        var relative = linearPeak / sdrWhite;
        // Reserve headroom around the display's SDR white and compress brighter values smoothly.
        return relative <= knee ? relative * (targetKnee / knee)
            : targetKnee + (1f - targetKnee) * (1f - MathF.Exp(-(relative - knee) / 1.33f));
    }

    private static byte EncodeSrgb(float value) => ColorTables.Srgb[Math.Min(SrgbSteps, (int)(value * SrgbSteps + 0.5f))];

    private static class ColorTables
    {
        public static readonly float[] Linear = Enumerable.Range(0, PositiveHalfLimit)
            .Select(bits => (float)BitConverter.UInt16BitsToHalf((ushort)bits)).ToArray();
        public static readonly byte[] Srgb = Enumerable.Range(0, SrgbSteps + 1).Select(index =>
        {
            var value = (float)index / SrgbSteps;
            return (byte)MathF.Round((value <= .0031308f ? value * 12.92f
                : 1.055f * MathF.Pow(value, 1f / 2.4f) - .055f) * 255f);
        }).ToArray();
    }
}
