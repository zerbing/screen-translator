# 回归测试样本

使用 Windows 英文 OCR 语言包与 .NET 8 SDK，构建时会复制仓库中的离线 OCR 模型和本地库。从项目根目录运行 `dotnet run -c Release --project tests/ScreenTranslator.Regression.csproj`。任一断言失败或缺少样本时，测试会以非零状态退出。

启动环境的检测与确认流程可以独立测试：`dotnet run -c Release --project tests/ScreenTranslator.Regression.csproj -- --environment-only`。这组测试不需要 OCR 语言包或截图，不请求管理员权限、不安装组件。发布后的 `ScreenTranslator.exe --check-environment report.json` 只将缺失组件写入指定 JSON 文件，不弹窗、不安装；退出码 `0` 表示环境完整，`2` 表示存在缺失项，`1` 表示检测失败。

安装脚本测试：`powershell -NoProfile -ExecutionPolicy Bypass -File tests/Install-Environment.Tests.ps1`。测试使用替身验证基础组件的安装顺序、跳过已安装组件、只安装请求项、失败处理与重启提示，以及 WSUS 重试的单独确认、失败后恢复配置和拒绝覆盖明确的限制。不会下载或安装组件，也不修改真实注册表或更新策略。

这组测试还验证 WSUS 确认发生在首次安装之前、真实进度的读取和主程序退出后的取消。异步监控使用只输出测试进度或等待的 PowerShell 管道验证，不调用真实的系统安装命令。

截图存放在本机 `%LOCALAPPDATA%\ScreenTranslatorDev\ocr-fixtures`。鼠标坐标和预期英文文本记录在 `Program.cs`。游戏截图在原始 3840×2160 及缩放后的 1920×1080 各跑一次。

在命令末尾加上 `-- --zoom-matrix`，可将所有现有截图用例统一跑在 175%、150%、125%、100%、75% 和 50% 六档，包括图片窗口中的缩放场景。运行结束后分别报告每档通过的断言数。默认运行仍使用各用例原有的缩放比例和断言。

加上 `-- --progress` 可逐项输出累计检查、通过和失败数量，便于查看长回归的运行进度；可与 `--zoom-matrix` 一起使用。

加上 `-- --timings` 可记录每个识别场景的耗时，用于比较性能优化前后的结果。耗时记录不改变识别流程，也不设置超时或截断识别。

性能比较同时覆盖裁剪原图及 1920×1080、3840×2160 完整画布中的列表。从不同条目和续行悬停，检查所有条目的文字及项目符号；不得用裁剪图耗时代替整屏场景。像素缓冲区测试另外验证 BGRA、带填充的正负步长，以及取消后重新读取不遗留图像锁。

本地补读还会比较直接传入的像素和旧 PNG 路径解码后的像素及 DPI，确保省去编码、解码时不改变模型输入。批量读取测试确定性地在准备图像期间取消，再用同一个识别器读取完整文字，验证取消与资源复用。

| 文件 | 回归场景 |
| --- | --- |
| `web-learning-list.png`、`web-schema-steps.png` | 项目列表和编号列表保留条目换行；同一行的粗体、普通字体及项内软换行完整选中 |
| `web-rule-semantics-overlay.png` | 首项的括号、后半句和续行完整选中，邻近浮窗遮挡的条目不混入 |
| `web-factory-list.png` | 原图的五个项目保持五项，从条目内部续行悬停也选中同一列表，续行不得凭行距新增项目符号 |
| `jar-table.png`、`jar-table-overlay.png` | 逐行检查两列的标识符、文件名和版本号；单词与段落模式均以鼠标所在单元格为范围 |
| `abyss-challenge-list.png` | 标题与三条条件分别换行，条件内软换行合并；从标题、各条件和行尾查词 |
| `wavebinder-description.png`、`wavebinder-reactions.png` | 同一段落包含多种字色，从首行、中间和末行触发均须读全，强调文字不使段落截断 |
| `disturbance-outbreak.png` | 金色活动标题和倒计时分别完整识别，标题不丢失开头，也不与下一行串选 |
| `genshin-guild-greeting.png` | 冒险家协会两行欢迎语完整识别，与上方人物名分离；固定问候语采用原神译法 |
| `pale-crown-palace.png`、`pale-crown-palace-context.png`、`teplo-oasis.png` | 浅色地图和图标背景上的地名完整识别，覆盖有无翻译浮窗的截图 |
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
| `terminal-status-header.png` | 标题及同一行可见的灰色版本号完整保留，路径和字段分别换行 |
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

程序还生成六组深浅背景与不同强调字色的多行段落，以及使用不同边框颜色、大小写标识符和文件名的表格。生成样本不依赖用户截图内容，用于检查换色、缩放和悬停位置变化后的行为。测试源码中的预期文本只用于断言，不提供给识别器。

默认运行时，桌面图标和中英混排截图按各用例原有尺寸测试；`--zoom-matrix` 同样覆盖六档。中英混排用例需要安装 Windows 简体中文 OCR 语言包。

窄体字识别使用已有 ECDICT 数据中的 BNC 和当代语料库词频辅助判断候选结果。索引与词典使用相同的数据来源和 MIT 许可，可用 `python scripts/import_ecdict_frequencies.py ecdict.csv Dictionaries/ECDICT-Frequencies.tsv.gz` 重建。[数据字段说明](https://github.com/skywind3000/ECDICT/blob/39cfb9672c0906b63569e0453c2e6abfb9ab182a/README.md#数据格式)
