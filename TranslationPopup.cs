using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ScreenTranslator;

internal sealed class TranslationPopup : Form
{
    private static readonly Color Surface = Color.FromArgb(21, 27, 37);
    private static readonly Color Foreground = Color.FromArgb(243, 247, 250);
    private static readonly Color Secondary = Color.FromArgb(175, 197, 216);
    private const TextFormatFlags TextFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    private readonly Font bodyFont = new("Microsoft YaHei UI", 11, FontStyle.Regular);
    private readonly Font subFont = new("Microsoft YaHei UI", 9, FontStyle.Regular);
    private string english = "";
    private string chinese = "";
    private string? source;
    private Rectangle englishArea;
    private Rectangle chineseArea;
    private Rectangle sourceArea;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    public TranslationPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        Opacity = 0.97;
        BackColor = Surface;
        DoubleBuffered = true;
        Text = "屏幕翻译 · 译文";
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int toolWindow = 0x80, transparent = 0x20, layered = 0x80000, noActivate = 0x08000000;
            var options = base.CreateParams;
            options.ExStyle |= toolWindow | transparent | layered | noActivate;
            return options;
        }
    }

    public void Present(string original, string translation, string? provider, RectangleF target, Point cursor, IntPtr targetWindow)
    {
        english = original;
        chinese = translation;
        source = provider;
        var screen = Screen.FromPoint(cursor).WorkingArea;
        var dpi = Math.Clamp(Desktop.GetDpiForWindow(targetWindow) / 96f, 1, 3);
        var pad = (int)(15 * dpi);
        var preferredWidth = chinese.Length > 180 ? 680 : 455;
        var maxWidth = Math.Max(100, Math.Min((int)(preferredWidth * dpi), screen.Width - pad * 4));
        using var graphics = Graphics.FromHwnd(targetWindow);
        var body = TextRenderer.MeasureText(graphics, chinese, bodyFont, new Size(maxWidth, int.MaxValue), TextFlags);
        var caption = TextRenderer.MeasureText(graphics, english, subFont, new Size(maxWidth, int.MaxValue), TextFlags);
        var footer = TextRenderer.MeasureText(graphics, source ?? "", subFont, new Size(maxWidth, int.MaxValue), TextFlags);
        var width = Math.Min(maxWidth, Math.Max((int)(100 * dpi), Math.Max(body.Width, Math.Max(caption.Width, footer.Width)))) + (2 * pad);

        var lineHeight = TextRenderer.MeasureText(graphics, "Ay", subFont, new Size(maxWidth, int.MaxValue), TextFlags).Height;
        var captionHeight = Math.Min(caption.Height, lineHeight * 3);
        englishArea = new Rectangle(pad, pad, width - 2 * pad, captionHeight);
        var textTop = english.Length == 0 ? pad : englishArea.Bottom + pad / 2;
        var bodyHeight = Math.Min(body.Height, screen.Height - 6 * pad - 5 * lineHeight);
        chineseArea = new Rectangle(pad, textTop, width - 2 * pad, Math.Max(lineHeight, bodyHeight));
        sourceArea = new Rectangle(pad, chineseArea.Bottom + pad / 2, width - 2 * pad, footer.Height);
        var height = (source is null ? chineseArea.Bottom : sourceArea.Bottom) + pad;

        var x = Math.Clamp(cursor.X + pad, screen.Left + pad / 2, Math.Max(screen.Left + pad / 2, screen.Right - width - pad / 2));
        var y = (int)Math.Max(cursor.Y + pad, target.Bottom + pad / 2);
        if (y + height > screen.Bottom - pad / 2)
            y = (int)Math.Min(cursor.Y - pad, target.IsEmpty ? cursor.Y - pad : target.Top - pad / 2) - height;
        if (y < screen.Top + pad / 2)
            y = Math.Clamp(cursor.Y + pad, screen.Top + pad / 2, Math.Max(screen.Top + pad / 2, screen.Bottom - height - pad / 2));

        // Keep the native window out of WinForms' activation path while the game owns focus.
        var handle = Handle;
        const uint noActivate = 0x0010;
        const uint showWindow = 0x0040;
        SetWindowPos(handle, new IntPtr(-1), x, y, width, height, noActivate | showWindow);
        var corner = (int)(15 * dpi);
        using var shape = RoundedRect(ClientSize, corner);
        var oldRegion = Region;
        Region = new Region(shape);
        oldRegion?.Dispose();
        Invalidate();
    }

    public void Dismiss()
    {
        if (IsHandleCreated)
            ShowWindow(Handle, 0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var shape = RoundedRect(ClientSize, Math.Max(12, (int)(15 * DeviceDpi / 96f)));
        using var border = new Pen(Color.FromArgb(65, 83, 100), 1.5f);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(border, shape);
        if (english.Length > 0)
            TextRenderer.DrawText(e.Graphics, english, subFont, englishArea, Secondary, TextFlags | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, chinese, bodyFont, chineseArea, Foreground, TextFlags | TextFormatFlags.EndEllipsis);
        if (source is not null)
            TextRenderer.DrawText(e.Graphics, source, subFont, sourceArea, Color.FromArgb(136, 209, 194), TextFlags);
    }

    private static GraphicsPath RoundedRect(Size size, int diameter)
    {
        var right = size.Width - 1;
        var bottom = size.Height - 1;
        var path = new GraphicsPath();
        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(right - diameter, 0, diameter, diameter, 270, 90);
        path.AddArc(right - diameter, bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(0, bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            bodyFont.Dispose();
            subFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
