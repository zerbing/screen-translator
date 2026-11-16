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

启动时会自动检查运行环境，缺少 OCR 语言包、英语语音包或 Visual C++ 运行库时，经用户确认后安装。

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
