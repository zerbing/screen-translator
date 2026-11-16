using System.Runtime.InteropServices;

namespace ScreenTranslator;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--check-environment", StringComparer.Ordinal))
        {
            Environment.ExitCode = args.Length == 2 && args[0] == "--check-environment"
                ? StartupEnvironment.WriteStatus(args[1]) : 1;
            return;
        }

        ApplicationConfiguration.Initialize();
        using var instance = new Mutex(false, @"Local\ScreenTranslator.Hover");
        if (!TakeOwnershipOrActivate(instance))
            return;

        try
        {
            if (!StartupEnvironment.EnsureReady())
                return;

            using var app = new TranslatorApp();
            using var activation = new ActivationWindow(app.ShowMainWindow);
            app.ShowMainWindow();
            Application.Run(app);
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    private static bool TakeOwnershipOrActivate(Mutex instance)
    {
        var deadline = Environment.TickCount64 + 2000;
        do
        {
            try
            {
                if (instance.WaitOne(0))
                    return true;
            }
            catch (AbandonedMutexException)
            {
                return true;
            }

            if (ActivationWindow.TryActivate())
                return false;

            // The first instance may still be starting or shutting down.
            Thread.Sleep(35);
        }
        while (Environment.TickCount64 < deadline);

        return false;
    }
}

internal sealed class ActivationWindow : NativeWindow, IDisposable
{
    private const string WindowName = "ScreenTranslator.Activation.8CF09CA6";
    private const int OpenMainWindow = 0x8001;
    private static readonly IntPtr MessageOnly = new(-3);
    private readonly Action showMainWindow;

    public ActivationWindow(Action showMainWindow)
    {
        this.showMainWindow = showMainWindow;
        CreateHandle(new CreateParams { Caption = WindowName, Parent = MessageOnly });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    public static bool TryActivate()
    {
        var window = FindWindowEx(MessageOnly, IntPtr.Zero, null, WindowName);
        if (window == IntPtr.Zero || Desktop.GetWindowThreadProcessId(window, out var processId) == 0)
            return false;

        AllowSetForegroundWindow(processId);
        return PostMessage(window, OpenMainWindow, IntPtr.Zero, IntPtr.Zero);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == OpenMainWindow)
            showMainWindow();
        else
            base.WndProc(ref message);
    }

    public void Dispose() => DestroyHandle();
}

internal static class Desktop
{
    internal const int RightShift = 0xA1;
    internal const int RightControl = 0xA3;
    internal const int LeftAlt = 0xA4;
    internal const int LeftMouse = 0x01;
    internal const int RightMouse = 0x02;
    internal const int MouseSide1 = 0x05;
    internal const int MouseSide2 = 0x06;

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Handle;
        public Point Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out ClientRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref Point point);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr window);

    internal static bool IsDown(int key) => GetAsyncKeyState(key) < 0;

    internal static bool WasPressed(int key, ref bool wasDown)
    {
        var state = GetAsyncKeyState(key);
        var pressed = !wasDown && (state < 0 || (state & 1) != 0);
        wasDown = state < 0;
        return pressed;
    }

    internal static bool TryCursor(out Point position, out bool visible)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        var success = GetCursorInfo(ref info);
        position = info.Position;
        visible = (info.Flags & 1) != 0;
        return success;
    }

    internal static IntPtr TopLevelWindowAt(Point point)
    {
        const uint root = 2;
        var window = WindowFromPoint(point);
        return window == IntPtr.Zero ? IntPtr.Zero : GetAncestor(window, root);
    }

    internal static Rectangle ClientOnScreen(IntPtr window)
    {
        if (!GetClientRect(window, out var client))
            return Rectangle.Empty;
        var start = new Point(client.Left, client.Top);
        var end = new Point(client.Right, client.Bottom);
        return ClientToScreen(window, ref start) && ClientToScreen(window, ref end)
            ? Rectangle.FromLTRB(start.X, start.Y, end.X, end.Y) : Rectangle.Empty;
    }
}
