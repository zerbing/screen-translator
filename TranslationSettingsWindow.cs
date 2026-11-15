namespace ScreenTranslator;

internal sealed class TranslationSettingsWindow : Form
{
    private static readonly Font BodyFont = new("Microsoft YaHei UI", 10);
    private static readonly Font LabelFont = new(BodyFont, FontStyle.Bold);
    private static readonly Color Ink = Color.FromArgb(27, 41, 54);
    private static readonly Color Muted = Color.FromArgb(80, 98, 112);
    private static readonly Color Rule = Color.FromArgb(225, 232, 236);
    private static readonly Color Accent = Color.FromArgb(23, 106, 97);

    private readonly ComboBox engine = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox baiduId = new() { Dock = DockStyle.Fill };
    private readonly TextBox baiduSecret = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox deepSeekKey = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TranslationCredentials environment;
    public OnlineEngine Engine => (OnlineEngine)engine.SelectedIndex;
    public TranslationCredentials Credentials { get; private set; }

    public TranslationSettingsWindow(OnlineEngine selected, TranslationCredentials saved, TranslationCredentials environment)
    {
        this.environment = environment;
        Credentials = saved;
        Text = "翻译设置";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MaximizeBox = MinimizeBox = false;
        Font = BodyFont;
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.White;
        ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Ink;
        MinimumSize = SizeFromClientSize(new Size(448, 0));

        engine.Items.AddRange(["自动（Google / MyMemory）", "Google", "MyMemory", "百度翻译", "DeepSeek"]);
        engine.SelectedIndex = (int)selected;
        baiduId.Text = saved.BaiduAppId ?? "";
        baiduSecret.Text = saved.BaiduSecretKey ?? "";
        deepSeekKey.Text = saved.DeepSeekApiKey ?? "";

        var layout = Stack();
        layout.Padding = new Padding(24, 22, 24, 18);
        Controls.Add(layout);

        AddField(layout, "联网翻译引擎", engine, heading: true);
        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(400, 0),
            ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Muted,
            Margin = new Padding(0, 0, 0, 14)
        };
        layout.Controls.Add(hint);

        var baiduFields = Stack();
        AddField(baiduFields, "App ID", baiduId);
        AddField(baiduFields, "密钥", baiduSecret);
        layout.Controls.Add(baiduFields);

        var deepSeekFields = Stack();
        AddField(deepSeekFields, "API Key", deepSeekKey);
        layout.Controls.Add(deepSeekFields);

        var divider = new Panel
        {
            Height = 1,
            Dock = DockStyle.Top,
            BackColor = SystemInformation.HighContrast ? SystemColors.WindowText : Rule,
            Margin = new Padding(0, 2, 0, 14)
        };
        layout.Controls.Add(divider);
        var save = CreateButton("保存", primary: true);
        var cancel = CreateButton("取消", primary: false);
        cancel.DialogResult = DialogResult.Cancel;
        save.Click += (_, _) => Save();
        AcceptButton = save;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty
        };
        buttons.Controls.AddRange([cancel, save]);
        layout.Controls.Add(buttons);

        engine.SelectedIndexChanged += (_, _) => RefreshFields();
        RefreshFields();

        void RefreshFields()
        {
            layout.SuspendLayout();
            baiduFields.Visible = Engine == OnlineEngine.Baidu;
            deepSeekFields.Visible = Engine == OnlineEngine.DeepSeek;
            hint.Text = Engine switch
            {
                OnlineEngine.Automatic => "Google 优先，MyMemory 备用。",
                OnlineEngine.Baidu or OnlineEngine.DeepSeek => "密钥仅在本机加密保存。",
                _ => "无需密钥。"
            };
            layout.ResumeLayout(performLayout: true);
        }
    }

    private static TableLayoutPanel Stack()
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ColumnCount = 1,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return panel;
    }

    private static void AddField(TableLayoutPanel layout, string text, Control field, bool heading = false)
    {
        var label = new Label { Text = text, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        if (heading)
            label.Font = LabelFont;
        layout.Controls.Add(label);
        field.Margin = new Padding(0, 0, 0, 10);
        layout.Controls.Add(field);
    }

    private static Button CreateButton(string text, bool primary)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(82, 36),
            Margin = new Padding(primary ? 0 : 10, 0, 0, 0),
            Padding = new Padding(12, 4, 12, 4)
        };
        if (!SystemInformation.HighContrast)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.ForeColor = primary ? Color.White : Ink;
            button.BackColor = primary ? Accent : Color.White;
            button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(195, 207, 215);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(18, 86, 79) : Color.FromArgb(240, 245, 248);
        }
        return button;
    }

    private void Save()
    {
        var values = new TranslationCredentials
        {
            BaiduAppId = NonEmpty(baiduId.Text),
            BaiduSecretKey = NonEmpty(baiduSecret.Text),
            DeepSeekApiKey = NonEmpty(deepSeekKey.Text)
        };
        var error = (values.BaiduAppId is null) != (values.BaiduSecretKey is null)
            ? "请同时填写百度 App ID 和密钥。"
            : Engine switch
            {
                OnlineEngine.Baidu when values.GetBaidu() is null && environment.GetBaidu() is null => "使用百度翻译需要 App ID 和密钥。",
                OnlineEngine.DeepSeek when values.DeepSeekApiKey is null && environment.DeepSeekApiKey is null => "使用 DeepSeek 需要 API Key。",
                _ => null
            };
        if (error is not null)
        {
            if ((values.BaiduAppId is null) != (values.BaiduSecretKey is null))
                engine.SelectedIndex = (int)OnlineEngine.Baidu;
            MessageBox.Show(this, error, "翻译设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Credentials = values;
        DialogResult = DialogResult.OK;
    }

    private static string? NonEmpty(string value) => value.Trim() is { Length: > 0 } trimmed ? trimmed : null;
}
