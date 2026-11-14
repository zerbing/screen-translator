namespace ScreenTranslator;

internal sealed class DemoWindow : Form
{
    private static readonly Color Ink = Color.FromArgb(232, 240, 249);
    private static readonly Color Muted = Color.FromArgb(163, 186, 209);
    private static readonly Color Mint = Color.FromArgb(144, 219, 202);
    private static readonly Color Card = Color.FromArgb(31, 43, 58);

    public DemoWindow()
    {
        Text = "屏幕翻译";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoScaleMode = AutoScaleMode.None;
        MaximizeBox = false;
        BackColor = Color.FromArgb(19, 28, 40);
        ForeColor = Ink;
        var scale = Math.Max(1, DeviceDpi / 96f);
        ClientSize = new Size((int)(680 * scale), (int)(350 * scale));

        AddLabel(this, "屏幕翻译", 32, 25, 22, Ink, FontStyle.Bold);
        AddLabel(this, "悬停查词或词组，侧键 1 / 右 Ctrl 翻译段落，侧键 2 / 右 Shift 朗读英文", 34, 76, 10.5f, Muted);

        var sample = new Panel
        {
            BackColor = Card,
            Location = new Point((int)(32 * scale), (int)(124 * scale)),
            Size = new Size((int)(616 * scale), (int)(162 * scale))
        };
        Controls.Add(sample);
        AddLabel(sample, "Adventurers' Guild", 21, 16, 14, Mint, FontStyle.Bold, "Segoe UI");
        AddLabel(sample, "Investigate the Ley Line Disorder near Stormterror's Lair.",
            21, 58, 13, Ink, family: "Segoe UI");
        AddLabel(sample, "Then return to Mondstadt and report what you discovered.",
            21, 85, 13, Ink, family: "Segoe UI");

        AddLabel(this, "关闭窗口后，应用仍在任务栏通知区域运行。", 34, 309, 9.5f, Muted);
    }

    private void AddLabel(Control parent, string text, int x, int y, float size, Color color,
        FontStyle style = FontStyle.Regular, string family = "Microsoft YaHei UI")
    {
        var font = new Font(family, size, style);
        var label = new Label
        {
            AutoSize = true,
            Text = text,
            Location = new Point((int)(x * DeviceDpi / 96f), (int)(y * DeviceDpi / 96f)),
            Font = font,
            ForeColor = color,
            UseMnemonic = false
        };
        label.Disposed += (_, _) => font.Dispose();
        parent.Controls.Add(label);
    }
}
