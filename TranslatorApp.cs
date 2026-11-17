using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenTranslator;

internal sealed class TranslatorApp : ApplicationContext
{
    private const int ActiveInterval = 55;
    private const int StandbyInterval = 250;
    private const int HoverDelay = 190;
    private const int MovementTolerance = 4;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = StandbyInterval };
    private readonly ApplicationGlossaries glossaries = new();
    private readonly Translator translator;
    private readonly EnglishSpeech speech = new();
    private readonly TranslationPopup popup = new();
    private readonly NotifyIcon tray;
    private readonly Icon? appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem pause = new("暂停") { CheckOnClick = true };
    private readonly ToolStripMenuItem supportedAppsOnly = new("仅在已适配的应用中启用") { CheckOnClick = true };
    private readonly ToolStripMenuItem paragraph = new("默认翻译段落") { CheckOnClick = true };
    private readonly AppSettingsStore settingsStore = AppSettingsStore.ForCurrentUser();
    private readonly TranslationCredentialStore credentialStore = TranslationCredentialStore.ForCurrentUser();
    private readonly TranslationCredentials environmentCredentials = new()
    {
        BaiduAppId = ReadCredential("SCREEN_TRANSLATOR_BAIDU_APP_ID"),
        BaiduSecretKey = ReadCredential("SCREEN_TRANSLATOR_BAIDU_SECRET_KEY"),
        DeepSeekApiKey = ReadCredential("SCREEN_TRANSLATOR_DEEPSEEK_API_KEY")
    };
    private TranslationCredentials savedCredentials;
    private string? apiKey;
    private BaiduCredentials? baiduCredentials;
    private OnlineEngine activeEngine;
    private readonly ScreenOcr? ocr;
    private DemoWindow? mainWindow;
    private IntPtr targetWindow;
    private bool targetIsGenshin;
    private bool targetIsSupported;
    private bool targetIsDesktop;
    private string? targetProcessName;
    private string? targetWindowTitle;
    private bool usingParagraph;
    private bool attempted;
    private bool speechButtonWasDown;
    private bool speechKeyWasDown;
    private bool speechRequested;
    private Selection? hoverWord;
    private Point anchor;
    private long stableSince;
    private CancellationTokenSource? pending;
    private bool busy;
    private bool exiting;
    private bool editingSettings;
    private AppSettings settings;

    public TranslatorApp()
    {
        translator = new Translator();
        settings = settingsStore.Load();
        savedCredentials = credentialStore.Load();
        pause.Checked = settings.Paused;
        supportedAppsOnly.Checked = settings.SupportedAppsOnly;
        paragraph.Checked = settings.DefaultParagraph;
        ApplyOnlineSettings();

        pause.CheckedChanged += SettingsChanged;
        supportedAppsOnly.CheckedChanged += SettingsChanged;
        paragraph.CheckedChanged += SettingsChanged;
        menu.Opening += (_, _) => ResetHover();
        menu.Items.AddRange([new ToolStripMenuItem("打开主界面", null, (_, _) => ShowMainWindow()),
            new ToolStripMenuItem("翻译设置…", null, (_, _) => ShowTranslationSettings()), new ToolStripSeparator(),
            pause, supportedAppsOnly, paragraph, new ToolStripSeparator(),
            new ToolStripMenuItem("退出", null, (_, _) => ExitThread())]);

        tray = new NotifyIcon { Icon = appIcon ?? SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowMainWindow();
        UpdateTray();
        ocr = ScreenOcr.Create();
        if (ocr is null)
        {
            tray.ShowBalloonTip(5000, "缺少英文 OCR 语言包", "请在 Windows 设置中为 English (United States) 安装光学字符识别。", ToolTipIcon.Warning);
        }
        timer.Tick += Tick;
        timer.Start();
    }

    internal void ShowMainWindow()
    {
        if (exiting)
            return;

        ResetHover();
        if (mainWindow is null)
        {
            mainWindow = new DemoWindow(ShowTranslationSettings);
            mainWindow.FormClosed += (_, _) => { mainWindow = null; ResetHover(); };
        }
        if (!mainWindow.Visible)
            mainWindow.Show();
        if (mainWindow.WindowState == FormWindowState.Minimized)
            mainWindow.WindowState = FormWindowState.Normal;
        // The process startup show command can hide the first native window despite Form.Show().
        const int show = 5;
        if (!Desktop.IsWindowVisible(mainWindow.Handle))
            Desktop.ShowWindow(mainWindow.Handle, show);
        mainWindow.Activate();
    }

    private async void Tick(object? sender, EventArgs args)
    {
        var mouseSpeak = Desktop.WasPressed(Desktop.MouseSide2, ref speechButtonWasDown);
        var keyboardSpeak = Desktop.WasPressed(Desktop.RightShift, ref speechKeyWasDown);
        var speak = mouseSpeak || keyboardSpeak;
        if (!TryHover(out var window, out var point, out var readParagraph))
        {
            timer.Interval = StandbyInterval;
            if (stableSince != 0)
                ResetHover();
            return;
        }
        timer.Interval = ActiveInterval;
        if (stableSince == 0 || readParagraph != usingParagraph || HasMoved(point, anchor))
        {
            ResetHover();
            anchor = point;
            stableSince = Environment.TickCount64;
            usingParagraph = readParagraph;
        }
        if (speak && hoverWord is { } spoken)
            Speak(spoken, point, window);
        else if (speak)
            speechRequested = true;

        if (busy || (attempted && !speechRequested) || (!speechRequested && Environment.TickCount64 - stableSince < HoverDelay))
            return;

        attempted = true;
        var hadSpeechRequest = speechRequested;
        var available = Rectangle.Intersect(Screen.FromPoint(anchor).Bounds, Desktop.ClientOnScreen(window));
        var dpi = Desktop.GetDpiForWindow(window) / 96f;
        var capture = HoverRecognition.CaptureArea(available, anchor, dpi, readParagraph);
        if (capture.Width < 16 || capture.Height < 12 || ocr is not { } recognizer)
        {
            speechRequested = false;
            return;
        }

        busy = true;
        using var request = new CancellationTokenSource();
        pending = request;
        var cursor = anchor;
        var isGenshin = targetIsGenshin;
        var processName = targetProcessName;
        var windowTitle = targetWindowTitle;
        var isDemoWindow = IsMainWindow(window);
        var isDesktop = targetIsDesktop;
        try
        {
            if (readParagraph) popup.Dismiss();
            using var frame = readParagraph
                ? await Task.Run(() => recognizer.CaptureFrame(available, request.Token), request.Token)
                : null;
            if (!StillHovering(request, window, cursor, readParagraph)) return;
            if (readParagraph)
                popup.Present("", "正在识别…", null, RectangleF.Empty, cursor, window);
            var wider = readParagraph ? capture : HoverRecognition.CaptureArea(available, cursor, dpi, paragraph: false, expanded: true);
            var (target, word, glossary) = await Task.Run(async () =>
            {
                var activeGlossary = glossaries.ForProcess(processName, windowTitle, isDemoWindow);
                if (isDesktop && DesktopLabels.TryRead(cursor, out var desktopLabel))
                    return (desktopLabel, desktopLabel, activeGlossary);
                var result = await HoverRecognition.ReadAsync(available, capture, wider, cursor, readParagraph,
                    isGenshin, activeGlossary, frame is null ? recognizer.ReadAsync : frame.ReadAsync, request.Token,
                    frame is null ? recognizer.ReadOptionAsync : frame.ReadOptionAsync,
                    frame is null ? null : frame.ReadLayoutAsync).ConfigureAwait(false);
                return (result.Target, result.Word, activeGlossary);
            }, request.Token);
            if (!StillHovering(request, window, cursor, readParagraph))
                return;
            hoverWord = word is null ? null : word with { Text = glossary?.CorrectOcr(word.Text) ?? word.Text };
            if (speechRequested)
            {
                speechRequested = false;
                if (hoverWord is { } recognized)
                    Speak(recognized, cursor, window);
            }

            if (target is null)
            {
                if (readParagraph)
                    popup.Present("", "未识别到文字", null, RectangleF.Empty, cursor, window);
                return;
            }
            if (glossary is not null)
                target = target with { Text = glossary.CorrectOcr(target.Text), Context = glossary.CorrectOcr(target.Context) };

            var operation = translator.TranslateAsync(target, readParagraph, glossary, apiKey,
                request.Token, baiduCredentials, activeEngine);
            if (!operation.IsCompleted)
            {
                // Only show loading feedback when the network takes long enough to notice.
                await Task.WhenAny(operation, Task.Delay(230, request.Token));
                if (!operation.IsCompleted && StillHovering(request, window, cursor, readParagraph))
                    popup.Present(target.Text, "正在翻译…", null, target.Bounds, cursor, window);
            }

            var translated = await operation;
            if (StillHovering(request, window, cursor, readParagraph))
            {
                var source = translated.TermsMayBeMissing ? $"{translated.Source} · 术语可能有遗漏" : translated.Source;
                popup.Present(target.Text, translated.Text, source, target.Bounds, cursor, window);
            }
        }
        catch (Exception error) when (Translator.IsServiceError(error) || error is Win32Exception or ExternalException)
        {
            if (StillHovering(request, window, cursor, readParagraph))
            {
                var message = error switch
                {
                    ExternalException or Win32Exception => "读取屏幕失败",
                    NoTranslationException => "未查到中文译文",
                    TranslationAuthenticationException or HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }
                        => "翻译服务认证失败，请检查翻译设置中的密钥",
                    _ => "联网翻译暂不可用，请检查连接后重试"
                };
                popup.Present("", message, null, RectangleF.Empty, cursor, window);
            }
        }
        finally
        {
            if (hadSpeechRequest && !request.IsCancellationRequested)
                speechRequested = false;
            pending = null;
            busy = false;
        }
    }

    private void Speak(Selection word, Point cursor, IntPtr window)
    {
        if (!speech.Speak(word.Text))
            tray.ShowBalloonTip(3500, "无法朗读英文", "请检查 Windows 英语语音包和音频输出。", ToolTipIcon.Warning);
        if (translator.FindPronunciation(word.Text) is { } pronunciation)
            popup.ShowPronunciation(word.Text, pronunciation, word.Bounds, cursor, window);
    }

    private bool TryHover(out IntPtr window, out Point point, out bool readParagraph)
    {
        window = IntPtr.Zero;
        point = default;
        readParagraph = false;
        if (exiting || editingSettings || pause.Checked || ocr is null || menu.Visible
            || !Desktop.TryCursor(out point, out var visible)
            || Desktop.IsDown(Desktop.LeftMouse) || Desktop.IsDown(Desktop.RightMouse))
            return false;
        if (!visible && !Desktop.IsDown(Desktop.LeftAlt))
            return false;

        window = supportedAppsOnly.Checked || !visible ? Desktop.GetForegroundWindow() : Desktop.TopLevelWindowAt(point);
        if (window == IntPtr.Zero || !Desktop.ClientOnScreen(window).Contains(point))
            return false;
        if (window != targetWindow)
        {
            targetWindow = window;
            targetProcessName = WindowProcessName(window);
            targetWindowTitle = null;
            if (ApplicationGlossaries.IsJava(targetProcessName))
            {
                var title = new StringBuilder(256);
                Desktop.GetWindowText(window, title, title.Capacity);
                targetWindowTitle = title.ToString();
            }
            targetIsGenshin = glossaries.IsGenshin(targetProcessName);
            targetIsSupported = glossaries.IsSupported(targetProcessName, targetWindowTitle);
            targetIsDesktop = DesktopLabels.IsDesktopWindow(window);
            ResetHover();
        }
        if ((!visible && !targetIsGenshin) || (supportedAppsOnly.Checked && !targetIsSupported && !IsMainWindow(window)))
            return false;

        var sideButtonDown = Desktop.IsDown(Desktop.MouseSide1);
        readParagraph = sideButtonDown || (paragraph.Checked ^ Desktop.IsDown(Desktop.RightControl));
        return true;
    }

    private bool StillHovering(CancellationTokenSource request, IntPtr window, Point cursor, bool readParagraph) =>
        !request.IsCancellationRequested && TryHover(out var currentWindow, out var currentPoint, out var currentMode)
        && currentWindow == window && currentMode == readParagraph && !HasMoved(currentPoint, cursor);

    private bool IsMainWindow(IntPtr window) => mainWindow is { IsHandleCreated: true } && window == mainWindow.Handle;

    private static string? WindowProcessName(IntPtr window)
    {
        Desktop.GetWindowThreadProcessId(window, out var id);
        if (id <= 0)
            return null;

        try
        {
            using var process = Process.GetProcessById(id);
            return process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static bool HasMoved(Point point, Point origin) =>
        Math.Abs(point.X - origin.X) > MovementTolerance || Math.Abs(point.Y - origin.Y) > MovementTolerance;

    private void ResetHover()
    {
        pending?.Cancel();
        popup.Dismiss();
        stableSince = 0;
        attempted = false;
        speechRequested = false;
        hoverWord = null;
    }

    private void UpdateTray() => tray.Text = pause.Checked ? "屏幕翻译 · 已暂停" : "屏幕翻译";

    private void SettingsChanged(object? sender, EventArgs args)
    {
        ResetHover();
        UpdateTray();
        settings = settings with { SupportedAppsOnly = supportedAppsOnly.Checked, DefaultParagraph = paragraph.Checked, Paused = pause.Checked };
        if (!settingsStore.Save(settings))
            tray.ShowBalloonTip(4000, "设置保存失败", "请检查本地应用数据目录是否可写。", ToolTipIcon.Warning);
    }

    private void ApplyOnlineSettings()
    {
        var selected = settings.SelectedEngine;
        apiKey = selected == OnlineEngine.DeepSeek ? savedCredentials.DeepSeekApiKey ?? environmentCredentials.DeepSeekApiKey : null;
        baiduCredentials = selected == OnlineEngine.Baidu ? savedCredentials.GetBaidu() ?? environmentCredentials.GetBaidu() : null;
        activeEngine = selected switch
        {
            OnlineEngine.Baidu when baiduCredentials is null => OnlineEngine.Automatic,
            OnlineEngine.DeepSeek when apiKey is null => OnlineEngine.Automatic,
            _ => selected
        };
    }

    private void ShowTranslationSettings()
    {
        if (editingSettings)
            return;

        ResetHover();
        editingSettings = true;
        try
        {
            using var dialog = new TranslationSettingsWindow(settings.SelectedEngine, savedCredentials, environmentCredentials);
            if (dialog.ShowDialog(mainWindow) != DialogResult.OK)
                return;
            if (!credentialStore.Save(dialog.Credentials))
            {
                MessageBox.Show(mainWindow, "无法保存本机密钥，请检查应用数据目录是否可写。", "翻译设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            savedCredentials = dialog.Credentials;
            settings = settings with { Engine = dialog.Engine, UseDeepSeek = dialog.Engine == OnlineEngine.DeepSeek };
            ApplyOnlineSettings();
            SettingsChanged(null, EventArgs.Empty);
        }
        finally { editingSettings = false; }
    }

    private static string? ReadCredential(string name) => (Environment.GetEnvironmentVariable(name)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User))
        ?.Trim() is { Length: > 0 } key ? key : null;

    protected override void ExitThreadCore()
    {
        exiting = true;
        timer.Stop();
        ResetHover();
        tray.Visible = false;
        mainWindow?.Close();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            tray.Dispose();
            appIcon?.Dispose();
            menu.Dispose();
            popup.Dispose();
            speech.Dispose();
            ocr?.Dispose();
            translator.Dispose();
        }
        base.Dispose(disposing);
    }
}
