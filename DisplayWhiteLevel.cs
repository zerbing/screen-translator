using System.Runtime.InteropServices;

namespace ScreenTranslator;

internal static class DisplayWhiteLevel
{
    private const uint ActivePaths = 2;
    private const int InsufficientBuffer = 122;
    private const uint GetSourceName = 1;
    private const uint GetSdrWhiteLevel = 11;
    private const float NominalWhite = 1f; // scRGB 1.0 is the Windows nominal 80-nit SDR white.

    public static float Read(string displayName)
    {
        // Display topology can change between the size query and the data query.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (GetDisplayConfigBufferSizes(ActivePaths, out var pathCount, out var modeCount) != 0)
                break;
            var paths = new DisplayPath[pathCount];
            var modes = new DisplayMode[modeCount];
            var result = QueryDisplayConfig(ActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == InsufficientBuffer)
                continue;
            if (result != 0)
                break;

            for (var index = 0; index < pathCount; index++)
            {
                var path = paths[index];
                var source = new SourceName
                {
                    Header = new DeviceInfoHeader(GetSourceName, (uint)Marshal.SizeOf<SourceName>(), path.Source)
                };
                if (DisplayConfigGetDeviceInfo(ref source) != 0
                    || !string.Equals(source.Name, displayName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var white = new WhiteLevel
                {
                    Header = new DeviceInfoHeader(GetSdrWhiteLevel, (uint)Marshal.SizeOf<WhiteLevel>(), path.Target)
                };
                if (DisplayConfigGetDeviceInfo(ref white) == 0 && white.Level != 0)
                    return white.Level / 1000f;
            }
            break;
        }
        return NominalWhite;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterId
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, Size = 20)]
    private struct SourcePath
    {
        public AdapterId Adapter;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, Size = 48)]
    private struct TargetPath
    {
        public AdapterId Adapter;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayPath
    {
        public SourcePath Source;
        public TargetPath Target;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DisplayMode { }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public AdapterId Adapter;
        public uint Id;

        public DeviceInfoHeader(uint type, uint size, SourcePath source) =>
            (Type, Size, Adapter, Id) = (type, size, source.Adapter, source.Id);

        public DeviceInfoHeader(uint type, uint size, TargetPath target) =>
            (Type, Size, Adapter, Id) = (type, size, target.Adapter, target.Id);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WhiteLevel
    {
        public DeviceInfoHeader Header;
        public uint Level;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPath[] paths,
        ref uint modeCount, [Out] DisplayMode[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceName source);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref WhiteLevel white);
}
