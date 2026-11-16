using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Windows.Media.Ocr;

namespace ScreenTranslator;

internal sealed record EnvironmentComponent(string Id, string DisplayName);
internal sealed record EnvironmentInstallationResult(string Message, string LogPath);

internal static class StartupEnvironment
{
    private static readonly EnvironmentComponent[] Components =
    [
        new("EnglishOcr", "英文 OCR 语言包（含所需的基础语言组件）"),
        new("ChineseOcr", "简体中文 OCR 语言包（含所需的基础语言组件）"),
        new("EnglishSpeech", "英语朗读语音包（含所需的基础语言组件）"),
        new("VisualCpp", "Visual C++ x64 运行库")
    ];

    internal static EnvironmentComponent[] FindMissingComponents(IEnumerable<string> ocrLanguages,
        bool englishSpeechAvailable, bool visualCppInstalled)
    {
        var languages = ocrLanguages.ToArray();
        bool[] available =
        [
            languages.Any(tag => tag.StartsWith("en-", StringComparison.OrdinalIgnoreCase)),
            languages.Any(tag => tag.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
                || tag.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)),
            englishSpeechAvailable,
            visualCppInstalled
        ];
        return Components.Where((_, index) => !available[index]).ToArray();
    }

    internal static EnvironmentComponent[] DetectMissingComponents() => FindMissingComponents(
        OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag),
        EnglishSpeech.IsAvailable(), IsVisualCppInstalled());

    private static bool IsVisualCppInstalled()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var runtime = machine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
        return runtime?.GetValue("Installed") is int installed && installed == 1
            && runtime.GetValue("Version") is string value
            && Version.TryParse(value.TrimStart('v', 'V'), out var version)
            && version >= new Version(14, 29);
    }

    internal static bool EnsureReady()
    {
        try
        {
            return EnsureReady(DetectMissingComponents, ConfirmInstallation, InstallAndVerify);
        }
        catch (Exception error)
        {
            MessageBox.Show("无法检查或安装运行环境：\n\n" + error.Message, "运行环境检查失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    internal static bool EnsureReady(Func<EnvironmentComponent[]> detect,
        Func<EnvironmentComponent[], bool> confirm, Func<EnvironmentComponent[], bool> install)
    {
        var missing = detect();
        return missing.Length == 0 || (confirm(missing) && install(missing));
    }

    private static bool ConfirmInstallation(EnvironmentComponent[] missing)
    {
        var description = string.Join(Environment.NewLine, missing.Select(component => "• " + component.DisplayName));
        return MessageBox.Show("运行屏幕翻译还缺少以下组件：\n\n" + description
            + "\n\n是否现在下载安装？安装需要联网和管理员权限，可能需要几分钟。"
            + "\n选择“否”将退出程序，不安装任何组件。", "安装运行环境", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private static bool InstallAndVerify(EnvironmentComponent[] missing)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ScreenTranslator-Setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var script = Path.Combine(directory, "Install-Environment.ps1");
            var resultPath = Path.Combine(directory, "installation.json");
            using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("ScreenTranslator.Install-Environment.ps1")
                ?? throw new FileNotFoundException("程序缺少环境安装资源，请重新下载完整程序包。"))
            using (var target = File.Create(script))
            {
                // Windows PowerShell 5.1 needs the BOM to read Chinese messages as UTF-8.
                target.Write(Encoding.UTF8.GetPreamble());
                source.CopyTo(target);
            }

            // The installer starts only after confirmation; the app itself keeps its normal privileges.
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"),
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = directory,
                Arguments = "-NoProfile -STA -ExecutionPolicy Bypass -File \"" + script + "\" -Components "
                    + string.Join(',', missing.Select(component => component.Id)) + " -ResultPath \"" + resultPath + "\""
                    + " -OwnerProcessId " + Environment.ProcessId
            };
            Process? installer;
            try { installer = Process.Start(start); }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return false; }
            using (installer)
            {
                if (installer is null)
                    throw new InvalidOperationException("无法启动环境安装程序。");
                installer.WaitForExit();
                if (installer.ExitCode == 1223)
                    return false;
                if (installer.ExitCode == 3010)
                {
                    MessageBox.Show("组件已安装，但 Windows 要求重启。请保存工作、重启电脑后再打开屏幕翻译。"
                        + "\n程序不会自动重启电脑。", "需要重启", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                if (installer.ExitCode != 0)
                {
                    var result = ReadInstallationResult(resultPath);
                    throw new InvalidOperationException(result.Message + "\n\n安装日志：" + result.LogPath);
                }
            }

            // Probe in a fresh process so WinRT/SAPI caches cannot hide newly installed language packs.
            var reportPath = Path.Combine(directory, "environment.json");
            var probe = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            probe.ArgumentList.Add("--check-environment");
            probe.ArgumentList.Add(reportPath);
            using var check = Process.Start(probe) ?? throw new InvalidOperationException("无法复查运行环境。");
            check.WaitForExit();
            if (check.ExitCode is not (0 or 2))
                throw new InvalidOperationException("安装后复查运行环境失败，请重新启动程序。");
            var output = File.ReadAllText(reportPath);
            var remaining = JsonSerializer.Deserialize<EnvironmentComponent[]>(output)
                ?? throw new InvalidOperationException("无法读取运行环境复查结果。");
            if (check.ExitCode != 0 || remaining.Length > 0)
            {
                MessageBox.Show("安装后以下组件仍未就绪：\n\n"
                    + string.Join(Environment.NewLine, remaining.Select(component => "• " + component.DisplayName))
                    + "\n\n请重启电脑后再试；如果仍然出现提示，请在 Windows 设置中检查语言选项。",
                    "运行环境尚未就绪", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static EnvironmentInstallationResult ReadInstallationResult(string path)
    {
        var fallback = new EnvironmentInstallationResult("安装程序未能完成运行，未取得具体错误。请查看安装日志。",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"ScreenTranslator\Logs"));
        try
        {
            var result = JsonSerializer.Deserialize<EnvironmentInstallationResult>(File.ReadAllText(path));
            return result is not null && !string.IsNullOrWhiteSpace(result.Message)
                ? result with { LogPath = string.IsNullOrWhiteSpace(result.LogPath) ? fallback.LogPath : result.LogPath }
                : fallback;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return fallback;
        }
    }

    internal static int WriteStatus(string outputPath)
    {
        try
        {
            var missing = DetectMissingComponents();
            File.WriteAllText(outputPath, JsonSerializer.Serialize(missing), new UTF8Encoding(false));
            return missing.Length == 0 ? 0 : 2;
        }
        catch
        {
            return 1;
        }
    }
}
