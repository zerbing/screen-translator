using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Media.Ocr;

namespace ScreenTranslator;

internal sealed class TranslatorApp : ApplicationContext
{
    private const int ActiveInterval = 55;
    private const int StandbyInterval = 250;
    private const int HoverDelay = 190;
    private const int MovementTolerance = 4;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = StandbyInterval };
    private readonly TermGlossary glossary = new();
    private readonly Translator translator;
    private readonly EnglishSpeech speech = new();
    private readonly TranslationPopup popup = new();
    private readonly NotifyIcon tray;
    private readonly Icon? appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem pause = new("暂停") { CheckOnClick = true };
    private readonly ToolStripMenuItem genshin = new("仅在原神中启用") { Checked = true, CheckOnClick = true };
    private readonly ToolStripMenuItem paragraph = new("默认翻译段落") { CheckOnClick = true };
    private readonly ToolStripMenuItem ai = new("使用 DeepSeek 上下文翻译") { CheckOnClick = true };
    private readonly string? apiKey = GetApiKey();
    private readonly ScreenOcr? ocr;
    private DemoWindow? mainWindow;
    private IntPtr foreground;
    private bool foregroundIsGenshin;
    private bool usingParagraph;
    private bool attempted;
    private bool speechButtonWasDown;
    private bool speechKeyWasDown;
    private bool speechRequested;
    private string? hoverEnglish;
    private Point anchor;
    private long stableSince;
    private CancellationTokenSource? pending;
    private bool busy;
    private bool exiting;

    public TranslatorApp()
    {
        translator = new Translator(glossary);
        ai.Enabled = apiKey is not null;
        if (apiKey is null)
            ai.ToolTipText = "设置 SCREEN_TRANSLATOR_DEEPSEEK_API_KEY 后重启软件即可启用";

        pause.CheckedChanged += (_, _) =>
        {
            pause.Text = pause.Checked ? "恢复翻译" : "暂停";
            ResetHover();
            UpdateTray();
        };
        genshin.CheckedChanged += (_, _) => { ResetHover(); UpdateTray(); };
        paragraph.CheckedChanged += (_, _) => ResetHover();
        ai.CheckedChanged += (_, _) => ResetHover();
        menu.Opening += (_, _) => ResetHover();
        menu.Items.AddRange([new ToolStripMenuItem("打开主界面", null, (_, _) => ShowMainWindow()), new ToolStripSeparator(),
            pause, genshin, paragraph, ai, new ToolStripSeparator(),
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
            mainWindow = new DemoWindow();
            mainWindow.FormClosed += (_, _) => { mainWindow = null; ResetHover(); };
            mainWindow.Show();
        }
        if (mainWindow.WindowState == FormWindowState.Minimized)
            mainWindow.WindowState = FormWindowState.Normal;
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
        if (speak && hoverEnglish is { } english)
            Speak(english);
        else if (speak)
            speechRequested = true;

        if (busy || (attempted && !speechRequested) || (!speechRequested && Environment.TickCount64 - stableSince < HoverDelay))
            return;

        attempted = true;
        var hadSpeechRequest = speechRequested;
        var available = Rectangle.Intersect(Screen.FromPoint(anchor).Bounds, Desktop.ClientOnScreen(window));
        var capture = CaptureArea(window, available, anchor, readParagraph);
        if (capture.Width < 16 || capture.Height < 12 || ocr is not { } recognizer)
        {
            speechRequested = false;
            return;
        }

        busy = true;
        using var request = new CancellationTokenSource();
        pending = request;
        var cursor = anchor;
        var isGenshin = foregroundIsGenshin;
        try
        {
            var wordScale = isGenshin ? 2.4f : 2.2f;
            var lines = await ReadAsync(capture, readParagraph ? 1.65f : wordScale);
            if (!StillHovering(request, window, cursor, readParagraph))
                return;

            var target = Select(lines);
            if (readParagraph && target is not null)
            {
                var start = capture;
                var expanded = TextSelection.ExtendParagraphCapture(capture, available, target.Bounds);
                while (expanded != capture)
                {
                    var expandedLines = await ReadAsync(expanded, 1.65f);
                    if (!StillHovering(request, window, cursor, readParagraph))
                        return;
                    var more = Select(expandedLines);
                    if (more is null || !TextSelection.IncludesParagraph(more.Bounds, target.Bounds))
                        break;

                    lines = expandedLines;
                    target = more;
                    capture = expanded;
                    expanded = TextSelection.ExtendParagraphCapture(capture, available, target.Bounds);
                }

                if (capture != start && target.Bounds.Height >= start.Height * 0.65f)
                {
                    // Larger book paragraphs get a tight, native-resolution pass. At native scale,
                    // punctuation and thin serifs are less likely to be smoothed out.
                    var detail = TextSelection.ParagraphDetailArea(capture, target.Bounds);
                    var detailLines = await ReadAsync(detail, 1f, useGamePreprocessing: false);
                    if (!StillHovering(request, window, cursor, readParagraph))
                        return;
                    if (Select(detailLines) is { } refined && TextSelection.PreservesParagraph(refined, target))
                    {
                        lines = detailLines;
                        target = refined;
                    }
                }
            }
            if (!readParagraph && target is not null)
            {
                var wider = CaptureArea(window, available, cursor, paragraph: false, expanded: true);
                if (wider != capture && MightBeClipped(target.Bounds, capture, wider))
                {
                    var retry = await ReadAsync(wider, wordScale);
                    if (!StillHovering(request, window, cursor, readParagraph))
                        return;
                    target = Select(retry) ?? target;
                }
            }
            var word = readParagraph ? TextSelection.At(lines, cursor, false, isGenshin ? glossary : null) : target;
            hoverEnglish = word is null ? null : isGenshin ? glossary.CorrectOcr(word.Text) : word.Text;
            if (speechRequested)
            {
                speechRequested = false;
                if (hoverEnglish is { } recognizedEnglish)
                    Speak(recognizedEnglish);
            }

            if (target is null)
                return;
            if (isGenshin)
                target = target with { Text = glossary.CorrectOcr(target.Text), Context = glossary.CorrectOcr(target.Context) };

            var operation = translator.TranslateAsync(target, readParagraph, isGenshin, ai.Checked ? apiKey : null, request.Token);
            if (!operation.IsCompleted)
            {
                // Only show loading feedback when the network takes long enough to notice.
                await Task.WhenAny(operation, Task.Delay(230, request.Token));
                if (!operation.IsCompleted && StillHovering(request, window, cursor, readParagraph))
                    popup.Present(target.Text, "正在翻译…", null, target.Bounds, cursor);
            }

            var translated = await operation;
            if (StillHovering(request, window, cursor, readParagraph))
                popup.Present(target.Text, translated.Text, translated.Source, target.Bounds, cursor);

            Selection? Select(ScreenLine[] recognized) => TextSelection.At(recognized, cursor, readParagraph, isGenshin ? glossary : null);

            Task<ScreenLine[]> ReadAsync(Rectangle area, float preferredScale, bool useGamePreprocessing = true)
            {
                var scale = Math.Min(preferredScale, (float)OcrEngine.MaxImageDimension / Math.Max(area.Width, area.Height));
                return Task.Run(() => recognizer.ReadAsync(area, scale, cursor,
                    isGenshin && useGamePreprocessing ? recognized => Select(recognized) is not null : null, request.Token), request.Token);
            }
        }
        catch (Exception error) when (Translator.IsServiceError(error) || error is Win32Exception or ExternalException)
        {
            if (StillHovering(request, window, cursor, readParagraph))
            {
                var message = error is ExternalException or Win32Exception ? "读取屏幕失败" : "联网翻译暂不可用，请检查连接后重试";
                popup.Present("", message, null, RectangleF.Empty, cursor);
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

    private void Speak(string english)
    {
        if (!speech.Speak(english))
            tray.ShowBalloonTip(3500, "无法朗读英文", "请检查 Windows 英语语音包和音频输出。", ToolTipIcon.Warning);
    }

    private bool TryHover(out IntPtr window, out Point point, out bool readParagraph)
    {
        window = Desktop.GetForegroundWindow();
        point = default;
        readParagraph = false;
        if (exiting || pause.Checked || ocr is null || menu.Visible || window == IntPtr.Zero)
            return false;
        if (window != foreground)
        {
            foreground = window;
            foregroundIsGenshin = IsGenshin(window);
            ResetHover();
        }
        if (genshin.Checked && !foregroundIsGenshin && !IsMainWindow(window))
            return false;
        if (!Desktop.TryCursor(out point, out var visible) || (!visible && !(foregroundIsGenshin && Desktop.IsDown(Desktop.LeftAlt))))
            return false;
        if (Desktop.IsDown(Desktop.LeftMouse) || Desktop.IsDown(Desktop.RightMouse))
            return false;
        if (!Desktop.ClientOnScreen(window).Contains(point))
            return false;

        var sideButtonDown = Desktop.IsDown(Desktop.MouseSide1);
        readParagraph = sideButtonDown || (paragraph.Checked ^ Desktop.IsDown(Desktop.RightControl));
        return true;
    }

    private bool StillHovering(CancellationTokenSource request, IntPtr window, Point cursor, bool readParagraph) =>
        !request.IsCancellationRequested && TryHover(out var currentWindow, out var currentPoint, out var currentMode)
        && currentWindow == window && currentMode == readParagraph && !HasMoved(currentPoint, cursor);

    private bool IsMainWindow(IntPtr window) => mainWindow is { IsHandleCreated: true } && window == mainWindow.Handle;

    private static bool IsGenshin(IntPtr window)
    {
        Desktop.GetWindowThreadProcessId(window, out var id);
        if (id <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(id);
            return process.ProcessName.Equals("GenshinImpact", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("YuanShen", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool HasMoved(Point point, Point origin) =>
        Math.Abs(point.X - origin.X) > MovementTolerance || Math.Abs(point.Y - origin.Y) > MovementTolerance;

    private static Rectangle CaptureArea(IntPtr window, Rectangle area, Point point, bool paragraph, bool expanded = false)
    {
        if (area.IsEmpty)
            return area;

        var dpi = Math.Clamp(Desktop.GetDpiForWindow(window) / 96f, 1, 2.5f);
        dpi = Math.Max(dpi, Math.Clamp(area.Width / 1920f, 1, 2.5f));
        var halfHeight = (int)((paragraph ? 170 : expanded ? 100 : 55) * dpi);
        var halfWidth = (int)((expanded ? 570 : 330) * dpi);
        var step = (int)(24 * dpi);
        var center = area.Top + (point.Y - area.Top) / step * step + step / 2;
        var band = paragraph
            ? new Rectangle(area.Left, center - halfHeight, area.Width, halfHeight * 2)
            : new Rectangle(point.X - halfWidth, point.Y - halfHeight, halfWidth * 2, halfHeight * 2);
        return Rectangle.Intersect(area, band);
    }

    private static bool MightBeClipped(RectangleF word, Rectangle capture, Rectangle wider)
    {
        var margin = Math.Clamp(word.Height * 0.3f, 3f, 12f);
        return (wider.Left < capture.Left && word.Left - capture.Left <= margin)
            || (wider.Right > capture.Right && capture.Right - word.Right <= margin)
            || (wider.Top < capture.Top && word.Top - capture.Top <= margin)
            || (wider.Bottom > capture.Bottom && capture.Bottom - word.Bottom <= margin);
    }

    private void ResetHover()
    {
        pending?.Cancel();
        popup.Dismiss();
        stableSince = 0;
        attempted = false;
        speechRequested = false;
        hoverEnglish = null;
    }

    private void UpdateTray() => tray.Text = pause.Checked ? "屏幕翻译 · 已暂停" : "屏幕翻译";

    private static string? GetApiKey() => (Environment.GetEnvironmentVariable("SCREEN_TRANSLATOR_DEEPSEEK_API_KEY")
        ?? Environment.GetEnvironmentVariable("SCREEN_TRANSLATOR_DEEPSEEK_API_KEY", EnvironmentVariableTarget.User))
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
            translator.Dispose();
        }
        base.Dispose(disposing);
    }
}
