# TxtLiveSync — TXT 实时同步查看 / 编辑器

轻量级 Windows 桌面工具：打开 `.txt`（可多标签），外部改了自动同步，也能直接编辑保存。

**当前版本：WPF（.NET 8）** — 双击 exe 即开，**没有控制台窗口**，任务栏是独立图标。

## 怎么用

```
双击  dist-wpf\TxtLiveSync.exe           ← 就这一步
或    dist-wpf\TxtLiveSync.exe a.txt b.txt  ← 每个文件开一个标签
或    把 txt 拖进窗口                      ← 新开一个标签，已有则切过去
```

首次启动约 1 秒。想把 `.txt` 双击就打开：右键 `.txt` → 打开方式 → 选择其他应用 → 浏览到 `TxtLiveSync.exe` → 勾选「始终使用」。

设成默认程序后，**双击文件不会再多开一个程序**：文件会被送进已经开着的那个窗口（新标签；如果这个文件本来就在标签里，直接切过去），窗口同时被拉到前台，拉起它的进程立刻退出。这项行为在「设置 → 双击文件时用已开的窗口打开」里可以关掉，关掉后每次双击都会开一个新窗口（想并排看两个文件时用）。

每个标签独立监视自己的文件：切换标签不打断同步，非当前标签被外部改动也会照常同步（静默模式下直接更新，回来就是新内容）。

## 交付物

| 路径 | 说明 |
|---|---|
| `dist-wpf\TxtLiveSync.exe` | **主程序（框架依赖），单文件约 290 KB**，已内嵌图标；要求机器装有 .NET Desktop Runtime |
| `dist-wpf-standalone\TxtLiveSync.exe` | **主程序（自包含），单文件约 63 MB**；拷贝即用，目标机什么都不用装（不入库，按需构建） |
| `TxtLiveSync.Wpf\` | 源码（`MainWindow` 外壳 + `DocumentTab` 单个文档页 + `Core\` 4 个类 + `SingleInstance`） |
| `示例.txt` | 演示文件（UTF-8 / CRLF） |
| `界面截图.png` | 实际运行截图 |
| `tests\LogicTests\` | 逻辑单元测试（C#，80 项断言，零依赖） |
| `v1-python\` | 第一版 Python 实现，仅作备份，日常工作不用它 |

## 技术栈与依赖

- **.NET 8 + WPF**，`net8.0-windows`，单文件发布（框架依赖）
- **零第三方 NuGet 包**，只用 BCL（`System.Text.Encoding`、`System.Text.Json`）
- 默认发布形态是**框架依赖**：体积小（290 KB），目标机器装 .NET Desktop Runtime **8 / 9 / 10 任一版本**即可（`RollForward=LatestMajor`，不要求版本严格对齐）。本机已装 8.0.33 / 9.0.22 / 10.0.11，直接双击即可
- 需要给**完全没装 .NET 的机器**时，用自包含形态：`python tests\build_wpf.py --standalone`，产物在 `dist-wpf-standalone\`，单文件 63 MB，拷贝即用、零依赖，代价是体积和首次启动稍慢（要解压原生库）
- 两种形态功能完全一致，只是运行时的打包方式不同
- 轮询间隔 400 ms，常驻内存约 60 MB

源码体量对比：v1（Python/tkinter）约 900 行，v2（WPF）约 1000 行 —— 因为 WPF 的布局/状态栏/图标都不用手搓，多出来的主要是多标签与每个标签独立的同步状态。

## 功能与实现对照

| 需求 | 实现 |
|---|---|
| 图形界面 | WPF 窗口：**一行顶栏**（左边菜单「文件 / 编辑 / 视图 / 设置 / 帮助」，右边打开·保存·重载·查找·关闭五个图标按钮）+ 标签条 + 状态栏（左状态、右编码·换行·字节数·标签序号） |
| 查找 / 替换 | `Ctrl+F` 在编辑区右上角浮出小面板（同 VS Code）：计数 `3/12`、`Aa` 区分大小写、上一个/下一个、无匹配时输入框变红显示「无结果」；`Ctrl+H` 多一行替换框，可逐处替换或全部替换 |
| 多标签 | 一个文件一个标签，标签上有文件名 + 未保存标记 `●` + `✕` 关闭按钮；`Ctrl+W` 关当前、`Ctrl+Tab` 切换；拖入多个文件一次开多个标签；同一个文件不重复开，自动切过去 |
| 关标签保护 | 标签有未保存修改时，关闭会问「保存 / 不保存 / 取消」，取消则不关；关窗口时逐个标签问，任一个取消就整体不关 |
| 快速打开 | 同步读文件塞进 TextBox；启动参数（可多个）直接开成标签 |
| 单实例 | 命名互斥体 + 命名管道：双击 .txt 时若已有窗口在跑，把路径转发过去再退出，不产生第二个进程；互斥体/管道名带 exe 路径哈希，不同目录的副本互不干扰；转发失败自动退回"各开一个窗口" |
| 外部自动同步 | 400 ms `DispatcherTimer` 轮询所有标签，每个标签独立比较自己的 `(mtime_ticks, size)`，变了就重读 |
| 冲突策略（核心） | **默认静默以外部为准**，本地未保存内容也直接覆盖，仅状态栏提示"本地未保存的内容已被磁盘版本覆盖" |
| 弹窗确认（可选） | 菜单「设置 → 外部变更时弹窗确认」，写 `config.wpf.json` 持久化；选「否」保留本地且同一版本不再追问 |
| 自保存不误触发 | 保存后写入新 `(mtime,size)` 并记录内容摘要；时间戳没刷新时用内容摘要兜底判定 |
| 文件异常容错 | 删除 / 重命名 / 不可读 → 状态栏「等待恢复」，界面保留原内容、不崩，恢复后自动同步 |
| 编码 | 严格 UTF-8 优先 → 失败退回 GBK(GB18030)；UTF-16 BOM 支持；BOM 字节原样保留；非法字节显示替换符不报错 |
| 换行 | 识别 CRLF / LF / CR；正文未改动时保存为**逐字节回写**，编辑后按原换行重写 |
| 窗口状态记忆 | 几何位置 + 尺寸 + 最大化、上次打开的标签列表与当前标签、字号、自动换行、弹窗开关 |
| 监视性能 | 固定 400 ms 轮询，每个标签一次 `stat`，只有签名变化才读文件；全在主线程，无后台线程 |

## 验收结果（本机实测）

> 第 2~4 节用的是本机 UI Automation / 截图脚本，里面写死了本机绝对路径，因此**没有随仓库发布**；下面是当时的真实输出记录。

### 1. 逻辑层（C# 测试，80 项断言全过）

```
dotnet run --project tests\LogicTests
```

覆盖：编码识别与 BOM 保留、GBK 逐字节回写、换行保持、静默覆盖、弹窗开关、自保存不误触发、删除/恢复、配置序列化、双击选词的词边界。

### 2. 界面层（UIAutomation 直接读文本框，不是截图猜）

真实启动 exe、外部改文件、再把界面里的文字读出来比对：

```
CASE1 before=[AAA-original\nsecond line\n]
CASE1 after =[BBB-external-change\nsecond line\n]
CASE1 PASS: editor auto-reloaded silently
CASE1 PASS: no dialog in silent mode
CASE2 窗口 = #32770::外部修改 | HwndWrapper[TxtLiveSync]::tl_ui_test.txt — TxtLiveSync
CASE2 PASS: confirm dialog shown
CASE2 editor while dialog=[CCC-local\nline2\n]
CASE2 PASS: local content kept while asking
```

### 3. 多标签（UIAutomation，17 项全过）

```
INFO  tabs = alpha.txt | beta.txt
PASS  two files open as two tabs
PASS  switching to first tab shows its own text
PASS  active tab silently follows external change
PASS  inactive tab also follows external change
PASS  dirty tab is marked with a dot
PASS  unsaved-changes dialog appears on closing a dirty tab
PASS  tab closed after answering No
PASS  answering No did not write to disk
RESULT=PASS
```

### 4. 端到端 / 外形

结论：启动后 **控制台窗口 0 个**，窗口标题 `示例.txt — TxtLiveSync`，exe 图标已内嵌（PE 资源里 7 个尺寸齐全，任务栏/资源管理器显示一致）。

### 5. 单实例

干净状态下先开 `alpha.txt`（1 个进程），再双击 `beta.txt`：第二个进程 `exit=0`、**进程数仍为 1**、窗口标题变成 `beta.txt — TxtLiveSync` —— 文件进了同一个窗口的新标签。

### 四条核心验收

| 验收项 | 结果 | 证据 |
|---|---|---|
| 静默同步（含覆盖本地未保存修改） | ✅ | UI Automation CASE1 + 逻辑测试；**没有加任何"保护性弹窗"** |
| 弹窗开关生效且持久化 | ✅ | UI Automation CASE2 检测到 `#32770` 对话框 + 逻辑测试持久化用例 |
| 自保存不误触发 | ✅ | 保存后轮询零事件（弹窗模式下也不弹） |
| 文件删除不崩溃 | ✅ | 删除/重命名/恢复用例全过，界面保留原内容并提示 |

## 编译

```bat
python tests\build_wpf.py                :: 框架依赖 → dist-wpf\        （约 290 KB，默认）
python tests\build_wpf.py --standalone   :: 自包含   → dist-wpf-standalone\（约 63 MB）
python tests\build_wpf.py --both         :: 两个都出
```

脚本内部先 `build --no-incremental` 再 `publish --no-build` —— XAML 改动偶尔不触发重编译，必须强制，否则可能拿到旧产物。想直接用 dotnet：

```
dotnet build   TxtLiveSync.Wpf\TxtLiveSync.Wpf.csproj -c Release -r win-x64 --self-contained false --no-incremental
dotnet publish TxtLiveSync.Wpf\TxtLiveSync.Wpf.csproj -c Release -r win-x64 --self-contained false --no-build -p:PublishSingleFile=true -o dist-wpf

:: 自包含版本
dotnet publish TxtLiveSync.Wpf\TxtLiveSync.Wpf.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist-wpf-standalone
```

## 快捷键

`Ctrl+O` 打开（可多选） · `Ctrl+S` 保存 · `Ctrl+Shift+S` 另存为 · `F5` 从磁盘重新加载 · `Ctrl+F` 查找 · `Ctrl+H` 替换 · `F3` 查找下一个 · `Esc` 关闭查找面板 · `Ctrl+W` 关闭当前标签 · `Ctrl+Tab` / `Ctrl+Shift+Tab` 切换标签 · `Ctrl+=` / `Ctrl+-` 字号 · `End` 跳到行尾 · `Ctrl+End` 跳到全文最后（加 `Shift` 为扩选）

## 配置位置

`%APPDATA%\TxtLiveSync\config.wpf.json`（删掉即恢复默认）；未捕获异常写同目录 `error.log`。

## 已知边界

- **静默覆盖是设计行为**，不是漏了保护：需求明确要求"即使本地有未保存修改也一样"；这条对每个标签单独生效。
- 同一路径不会开两个标签（避免两边互相覆盖）；需要对照同一文件的两个版本，先把其中一个另存为。
- 无语法高亮；不对超大文件做专项优化；标签数没有硬上限，但一次开几十个大文件会慢。
- 查找是纯文本匹配（支持区分大小写），不支持正则和"全词匹配"；超过 150 万字符的文件不再统计总数，只显示 `…`。
- 监视基于轮询，同一时刻"同尺寸 + 同 mtime"的极端改动可能漏检（实际几乎不会发生）。
- 单实例的作用范围是"同一个 exe 路径"，所以不同目录下的副本可以各开一个窗口；同一个 exe 又在跑时，双击文件只会进那个窗口（想并排看两个文件就先关掉单实例开关）。

## 关于 v1（Python 版）

`v1-python\` 里是第一版实现，保留原因：它验证过同一套行为语义。**不推荐日常使用** —— 本机端点安全策略拦截了 `pythonw.exe`，导致它必然附带一个关不掉的黑色控制台窗口，且任务栏图标是宿主 python 的。这正是重写为 WPF 的原因。
