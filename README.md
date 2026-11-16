# 屏幕翻译

Windows 屏幕英中翻译工具，适合玩游戏或使用英文软件时随手查词。

起初是想玩英文版《原神》，顺带学英语。游戏里碰到不少生词，试了几款屏幕翻译工具都不太顺手，就自己做了一个。

## 项目特点

- **悬停翻译**。鼠标停在英文上就能看中文释义，无需复制文字或切换窗口，也可以快捷朗读原文。
- **自动识别整段**。翻译整段时，程序根据鼠标位置找到对应段落，无需手动框选。适合阅读游戏对话、任务说明和软件帮助。
- **适应复杂画面**。针对小字、花体字和低对比度文字优化识别，减少漏字和断词。
- **按应用匹配术语**。在已适配的游戏或软件中自动加载专用词库，优先使用对应的译名。

## 开始使用

解压完整程序包后运行 `ScreenTranslator.exe`，保留同目录的 `OcrModels` 和 `x64` 文件夹。需要 Windows 10 2004 或更新版本（x64）、.NET 8 Desktop Runtime、Windows 英文 OCR 语言包及 Visual C++ x64 运行库。朗读需要 Windows 英语语音包。[下载程序](https://github.com/zerbing/screen-translator/releases/latest) · [.NET 8 下载](https://dotnet.microsoft.com/download/dotnet/8.0) · [Visual C++ x64 运行库](https://aka.ms/vs/17/release/vc_redist.x64.exe) · [安装英文 OCR](https://learn.microsoft.com/zh-cn/windows/powertoys/text-extractor)

启动时会检查英文 OCR、简体中文 OCR（用于中英混排）、英语朗读语音和 Visual C++ x64 运行库。发现缺失项后会列出清单，只有点击“是”并同意 Windows 管理员权限提示后才开始安装；点击“否”或取消权限提示会退出，不安装组件。语言包通过 Windows Update 安装，并先补齐对应的基础语言组件；VC++ 安装包来自 Microsoft，执行前校验数字签名。安装完成会重新检查，环境就绪后自动打开主界面。需要重启时会提示，不会自动重启电脑。安装日志位于 `%PROGRAMDATA%\ScreenTranslator\Logs`。

发布包不包含 .NET 运行时。若缺少 .NET 8 Desktop Runtime（x64），双击程序时会由 .NET 启动器提示前往下载安装；安装后再次打开程序即可。

安装语言包前如果检测到企业 WSUS 更新源，会先询问是否仅本次从 Microsoft Windows Update 下载语言组件，避免先在企业源上长时间等待。其他来源安装返回 `0x800f0954` 时也会提供此选项。同意后临时调整“可选组件”的来源，安装完成或报错后恢复，常规 Windows 更新来源保持不变。如果系统有明确禁止直连或指定更新类别来源的策略，程序会提示联系管理员，不覆盖这些限制。[微软对 WSUS 和语言包的说明](https://learn.microsoft.com/en-us/windows/deployment/update/fod-and-lang-packs)。

安装窗口显示 Windows 实际报告的进度。可以按 Ctrl+C 请求取消；退出主程序也会请求取消。Windows 若暂时无法取消当前操作，程序会等待其安全返回后恢复配置，不强制结束系统安装进程。同一时间只允许一个环境安装运行。

| 操作 | 默认用法 |
| --- | --- |
| 查单词和词组 | 鼠标停在英文上 |
| 翻译整段 | 按住右 Ctrl 或鼠标侧键 1 |
| 朗读英文并显示音标（词典有收录时） | 按右 Shift 或鼠标侧键 2 |

默认可在所有应用中使用。托盘菜单可以暂停翻译、更改默认模式，或勾选「仅在已适配的应用中启用」。右 Ctrl 用来临时切换查词和段落翻译。

## 词库与联网

程序按文字所在的应用加载词库，目前适配原神、Minecraft、Excel、VS Code、Blender 等游戏和软件。[查看完整列表](Glossaries/Applications.tsv) · [原神词库数据来源](https://genshin-dictionary.com/opendata)

截图使用 Windows OCR 与内置离线模型在本机处理。[OCR 模型与许可](OcrModels/README.md)。查单词时优先使用应用专用词库和内置的 ECDICT 英汉词典，查不到再联网。通用段落翻译需要联网，翻译服务只接收文本。[ECDICT 数据](https://github.com/skywind3000/ECDICT/tree/39cfb9672c0906b63569e0453c2e6abfb9ab182a) · [MIT 许可](Dictionaries/ECDICT-LICENSE.txt)

在主界面或托盘菜单打开「翻译设置」，可以选择自动、Google、MyMemory、百度翻译或 DeepSeek。默认的自动模式无需配置，按 Google、MyMemory 的顺序尝试。手动选择时，联网请求只使用所选引擎。填写的密钥会在本机加密保存。

- **百度翻译**需要 App ID 和密钥。[开通通用翻译 API](https://fanyi-api.baidu.com/doc/13) · [计费与免费额度](https://fanyi-api.baidu.com/product/112)
- **DeepSeek** 需要 API Key。[DeepSeek API 文档](https://api-docs.deepseek.com/quick_start/pricing/)

## 从源码构建

安装 .NET 8 SDK 后，在项目目录运行：

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o bin/app
```

[运行回归测试](tests/README.md)。
