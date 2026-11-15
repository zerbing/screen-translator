# 回归测试样本

使用 Windows 英文 OCR 语言包与 .NET 8 SDK。从项目根目录运行 `dotnet run -c Release --project tests/ScreenTranslator.Regression.csproj`。任一断言失败或缺少样本时，测试会以非零状态退出。

截图存放在本机 `%LOCALAPPDATA%\ScreenTranslatorDev\ocr-fixtures`。鼠标坐标和预期英文文本记录在 `Program.cs`。游戏截图在原始 3840×2160 及缩放后的 1920×1080 各跑一次。

在命令末尾加上 `-- --zoom-matrix`，可将截图用例统一跑在 175%、150%、125%、100%、75% 和 50% 六档。默认运行仍使用各用例原有的缩放比例。

| 文件 | 回归场景 |
| --- | --- |
| `quest-rock.png` | 石壁背景下的两行任务文字，单词可独立选择，距离数字留在译文之外 |
| `quest-crystals.png` | 同一任务换成高亮晶体背景，鼠标停在句首、行尾和第二行都能读全 |
| `dialogue.png` | 连续三行字幕、说话人名字和两个独立对话选项 |
| `long-note.png` | 长笔记分为上下两段，段内换行可补读，段间留白有效 |
| `tutorial-columns.png` | 左栏目录和右栏标题、正文各自选中，右侧译文不混入目录 |
| `achievements.png` | 多张成就卡片的 Challenger 分词、罗马序号和相邻卡片隔离 |
| `npc-name.png` | 亮背景中的 Lawrence 和青色头衔分别识别 |
| `dark-text-ice-you.png`、`dark-text-ice-domovoy.png` | 浅色冰晶背景下，从不同鼠标位置查 `you`、`Domovoy` 及相邻单词 |
| `domovoy-foreman.png` | 对话行末的 `foreman` 可独立选中 |
| `dialogue-survive.png` | 从对话里查 `survive`，显示通用离线释义 |
| `dialogue-shadow-realm.png` | 识别包含两个 `Shadow Realm` 的对话，原神词库能命中两处 |
| `tutorial-colored-words.png` | 同一段落有浅色正文和黄色强调词，保留完整的三行说明 |
| `tutorial-colored-heading.png` | 同一画面有独立标题和多色正文，在不同截图尺寸和截取范围下分别选择 |
| `dialogue-cyan-emphasis.png` | 复杂背景上白字和跨行的青色强调词完整识别，正文不混入说话人名字 |
| `dialogue-stacked-options.png` | 原神与普通图片窗口的查词和段落识别，覆盖位置变化、缩放及对话图标 |
| `dialogue-explain.png` | 发光边框内的两行对话选项，在 50%、75%、100% 和 150% 下识别 `explain` 和整段 |
| `world-map.png` | 从地名两端查词和翻译整段，覆盖被地图图标遮挡的地名及换行地名 |
| `desktop-icon.png` | 从桌面快捷方式两行标签中识别完整的 Desktop |
| `mixed-text.png` | 从紧贴汉字的文本中取出完整的 hatch-pet |
| `mixed-terminal-hyphen.png` | 深色终端中英混排，鼠标停在 sites-remix 的不同字母上都能读出完整词 |
| `terminal-command-list.png`、`terminal-command-list-bracket.png` | 从命令列和说明列均可选中同一组条目，命令斜杠和条目换行保留 |
| `terminal-status-header.png` | 标题、路径和字段分处独立行，分别保留换行 |
| `mixed-sidebar.png` | 在多行中英混排侧栏里从单词的不同位置查 Markdown、codex 和 WSL，并确保相邻的 chdir 能独立选中 |
| `mixed-sidebar-codex.png` | 不同缩放下查 Codex、codex 和 Markdown，覆盖大小写及紧贴汉字的词尾 |
| `mixed-filename.png` | 从中文说明中区分 ScreenTranslator 与 exe，并修正文件名中的 OCR 易混字符 |
| `muted-mixed-text.png` | 浅灰色中英混排中，分别从多处查 AdGuard 和 AdBlock |
| `muted-mixed-text-followup.png` | 在模拟图片窗口中，按不同缩放比例和词内位置查 AdGuard 和 AdBlock |
| `muted-mixed-text-hdr.png` | 浅灰提示在 HDR 桌面实际取图后仍能从多个位置识别 AdGuard 和 AdBlock |
| `heavy-display-text.png` | 黑底粗体上的 BLUE、SIDE、PICK，覆盖原图、较大的窗口截取范围，以及 175% 系统缩放下以 75% 至 300% 显示的情况 |
| `desktop-grid.png` | 图标名称不可读取时，从密集桌面截图里查 Chrome、Desktop 等英文词 |
| `white-cutout-logo.png` | 缩放和悬停位置变化时，从白色字母延伸至白底的标志中读取完整单词 |
| `hdr-overlay-text.png`、`hdr-overlay-text-gdi.png` | 从采用两种取图方式的比赛画面中独立识别 BLG 和 Viper |
| `league-scoreboard.png` | 查彩色队标和白色缩写，覆盖缩放及相邻编号导致的误读 |

程序另外生成深色斜体和金色斜体样本，并验证 `in`、`in front of`、各应用词库及缓存隔离。

桌面图标和中英混排的截图按原始尺寸测试。中英混排用例需要安装 Windows 简体中文 OCR 语言包。
