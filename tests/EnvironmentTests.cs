using ScreenTranslator;

internal static class EnvironmentTests
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool passed, string description)
        {
            checks++;
            if (!passed)
                throw new InvalidOperationException("运行环境测试失败：" + description);
        }

        var complete = StartupEnvironment.FindMissingComponents(["en-US", "zh-Hans-CN"], true, true);
        Check(complete.Length == 0, "组件完整时没有缺失项");
        Check(StartupEnvironment.FindMissingComponents(["EN-GB", "zh-CN"], true, true).Length == 0,
            "支持其他英语 OCR 及大小写不同的语言标签");
        var missing = StartupEnvironment.FindMissingComponents(["zh-Hans-CN"], true, true);
        Check(missing.Select(component => component.Id).SequenceEqual(["EnglishOcr"]), "本机缺少英文 OCR");
        Check(StartupEnvironment.FindMissingComponents(["en-US", "zh-Hant-TW"], false, false)
            .Select(component => component.Id).SequenceEqual(["ChineseOcr", "EnglishSpeech", "VisualCpp"]),
            "繁体中文不能替代简体中文 OCR，并能列出语音和运行库");
        Check(StartupEnvironment.FindMissingComponents([], false, false).Length == 4, "完全缺少组件");

        var confirmations = 0;
        var installations = 0;
        bool Confirm(EnvironmentComponent[] components)
        {
            confirmations++;
            Check(components.SequenceEqual(missing), "确认窗口只列出缺失项");
            return true;
        }
        bool Install(EnvironmentComponent[] components)
        {
            installations++;
            Check(components.SequenceEqual(missing), "只安装用户确认的缺失项");
            return true;
        }

        Check(StartupEnvironment.EnsureReady(() => complete, Confirm, Install), "环境完整时允许启动");
        Check(confirmations == 0 && installations == 0, "环境完整时不询问、不安装");
        Check(!StartupEnvironment.EnsureReady(() => missing, _ => false, Install), "拒绝安装时退出");
        Check(installations == 0, "没有用户确认时绝不安装");
        Check(StartupEnvironment.EnsureReady(() => missing, Confirm, Install), "确认后完成安装可以继续启动");
        Check(confirmations == 1 && installations == 1, "只确认和安装一次");
        Check(!StartupEnvironment.EnsureReady(() => missing, _ => true, _ => false), "取消 UAC 或安装失败时停止启动");

        var report = Path.Combine(Path.GetTempPath(), "screen-translator-result-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(report, """{"Message":"语言组件下载失败（0x800f0954）","LogPath":"example.log"}""");
            var result = StartupEnvironment.ReadInstallationResult(report);
            Check(result.Message.Contains("0x800f0954") && result.LogPath == "example.log", "保留安装错误及对应的日志路径");
            File.WriteAllText(report, "{");
            Check(StartupEnvironment.ReadInstallationResult(report).Message.Contains("未取得具体错误"), "损坏的错误报告使用备用提示");
            File.Delete(report);
            Check(StartupEnvironment.ReadInstallationResult(report).Message.Contains("未取得具体错误"), "缺少错误报告也能正常提示");
        }
        finally { File.Delete(report); }

        Console.WriteLine($"运行环境测试通过：{checks} 项。");
        return 0;
    }
}
