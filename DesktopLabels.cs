using Accessibility;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenTranslator;

internal static partial class DesktopLabels
{
    private const int ListItemRole = 0x22;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromPoint(Point point, out IAccessible accessible,
        [MarshalAs(UnmanagedType.Struct)] out object child);

    public static bool IsDesktopWindow(IntPtr window) =>
        ClassOf(window) is "Progman" or "WorkerW";

    public static bool TryRead(Point cursor, out Selection? selection)
    {
        selection = null;
        var visual = Desktop.WindowFromPoint(cursor);
        var view = GetParent(visual);
        if (ClassOf(visual) != "SysListView32" || ClassOf(view) != "SHELLDLL_DefView"
            || !IsDesktopWindow(GetParent(view)))
            return false;

        try
        {
            return AccessibleObjectFromPoint(cursor, out var accessible, out var child) == 0
                && accessible is not null && TryReadItem(accessible, child, cursor, out selection);
        }
        catch (Exception error) when (error is COMException or ArgumentException)
        {
            return false;
        }
    }

    internal static bool TryReadItem(IAccessible accessible, object child, Point cursor, out Selection? selection)
    {
        selection = null;
        if (accessible.get_accRole(child) is not ListItemRole)
            return false;
        var name = accessible.get_accName(child);
        if (string.IsNullOrWhiteSpace(name))
            return false;
        accessible.accLocation(out var x, out var y, out var width, out var height, child);
        var bounds = new Rectangle(x, y, width, height);
        if (!bounds.Contains(cursor))
            return false;
        selection = FromName(name, bounds);
        return true;
    }

    internal static Selection? FromName(string? name, Rectangle bounds)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var text = string.Join(' ', EnglishRuns().Matches(name).Select(match => match.Value));
        return text.Any(char.IsAsciiLetter) ? new Selection(text, text, bounds) : null;
    }

    [GeneratedRegex("[A-Za-z0-9]+(?:[.'_-][A-Za-z0-9]+)*")]
    private static partial Regex EnglishRuns();

    private static string ClassOf(IntPtr window)
    {
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }
}
