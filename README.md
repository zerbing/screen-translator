# 屏幕翻译

Windows 英中悬停翻译工具，支持单词、词组和段落翻译，以及英文朗读。内置词库适配了原神术语。

## 开始使用

1. 从 [Releases](https://github.com/zerbing/screen-translator/releases/latest) 下载 `ScreenTranslator.exe` 并运行。关闭主界面后，程序会继续在任务栏通知区域运行。
2. 要在所有应用中使用，右键托盘图标，取消勾选「仅在原神中启用」。将鼠标停在英文上约 0.2 秒，即可查看中文释义。常见词组如 `in front of` 会一起显示。
3. **整段翻译**：按住右 Ctrl 或鼠标侧键 1 悬停。也可以在托盘勾选「默认翻译段落」。
4. **朗读英文**：鼠标停在单词或词组上，按一下右 Shift 或鼠标侧键 2。朗读使用 Windows 本地英语语音。前台应用也会收到这些按键。

## 识别与翻译

- Windows OCR 在本机识别光标附近的文字。段落模式沿光标所在的文本块延伸，按需补读上下文，其他栏和独立选项分别处理。识别原神画面时会按需抑制背景纹路。常见词组可离线查阅，最近 512 条联网译文可在重启后继续使用。
- 通用翻译使用 Google 网页接口，暂不可用时优先直连 MyMemory，必要时再使用系统代理。联网服务收到选中的英文文本，截图在本机处理。首次查询需要能连接至少一个翻译服务。
- 可选 DeepSeek 上下文翻译，优先直连。设置用户环境变量 `SCREEN_TRANSLATOR_DEEPSEEK_API_KEY`，重启后在托盘启用。查词时会发送所在行作为上下文，该服务按 API 账户计费。[DeepSeek API 文档](https://api-docs.deepseek.com/quick_start/pricing/)

## 环境与构建

运行需要 Windows 10 2004 或更新版本的 x64 系统、.NET 8 Desktop Runtime 和英文 OCR 语言包。朗读需要 Windows 英语语音包。[.NET 8 下载](https://dotnet.microsoft.com/download/dotnet/8.0) · [英文 OCR 安装说明](https://learn.microsoft.com/zh-cn/windows/powertoys/text-extractor)

在项目目录运行以下命令重新构建，需要 .NET 8 SDK。

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o bin/app
```

## 词库来源

只有前台窗口属于原神时才使用原神术语。词库包含手工维护的词条及 Genshin Dictionary 社区数据。后者只提取英中词条，来源与快照哈希记录在 `GenshinDictionaryTerms.tsv`。下载数据集 `words.json` 后可用 `python import_genshin_terms.py words.json` 更新。[数据集及使用条款](https://genshin-dictionary.com/opendata)
