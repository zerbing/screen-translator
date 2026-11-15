# 屏幕翻译

Windows 屏幕英中翻译工具，适合玩游戏或使用英文软件时随手查词。鼠标停在英文上就能看中文释义，也可以翻译整段、朗读原文。

## 开始使用

下载并运行 `ScreenTranslator.exe`。需要 Windows 10 2004 或更新版本（x64）、.NET 8 Desktop Runtime 和 Windows 英文 OCR 语言包。朗读需要 Windows 英语语音包。[下载程序](https://github.com/zerbing/screen-translator/releases/latest) · [.NET 8 下载](https://dotnet.microsoft.com/download/dotnet/8.0) · [安装英文 OCR](https://learn.microsoft.com/zh-cn/windows/powertoys/text-extractor)

| 操作 | 默认用法 |
| --- | --- |
| 查单词和词组 | 鼠标停在英文上 |
| 翻译整段 | 按住右 Ctrl 或鼠标侧键 1 |
| 朗读英文 | 按右 Shift 或鼠标侧键 2 |

默认可在所有应用中使用。托盘菜单可以暂停翻译、更改默认模式，或勾选「仅在已适配的应用中启用」。右 Ctrl 用来临时切换查词和段落翻译。

## 词库与联网

程序按文字所在的应用加载词库，目前适配原神、Minecraft、Excel、VS Code、Blender 等游戏和软件。[查看完整列表](Glossaries/Applications.tsv) · [原神词库数据来源](https://genshin-dictionary.com/opendata)

截图在本机处理，内置词条可以离线查。通用翻译需要联网，翻译服务只接收文本。可选 DeepSeek，设置 `SCREEN_TRANSLATOR_DEEPSEEK_API_KEY` 后重启程序，在托盘菜单中启用。[DeepSeek API 文档](https://api-docs.deepseek.com/quick_start/pricing/)

## 从源码构建

安装 .NET 8 SDK 后，在项目目录运行：

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o bin/app
```

[运行回归测试](tests/README.md)。
