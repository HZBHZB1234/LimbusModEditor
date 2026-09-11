# plan-15：启动即全量扫描四张表 + 统一模态窗口（并入「加载资源」按钮）

> 来源：2026-09 用户第四批反馈（原话见 §0）。
> 前置：plan-13（启动自动扫描四库）、plan-14（lang 数据源改口径 + 预览修复）已实施入库，基线 562 测试全绿。

## 0. 本轮需求（用户原话 → 落地口径）

| # | 用户原话 | 落地口径 | 归属 |
|---|---|---|---|
| 1 | 「让软件启动后，无论何时，自动对四张表进行扫描更新，随后加载到软件与前端」 | 启动扫描**成为唯一的资源准备通道**：每次启动 / 恢复或打开项目，都对四个缓存库（= 四张表所在库）做一次增量扫描，扫完**主动把四个工作台的数据全部预热**（不再依赖切页 `Loaded` 懒加载） | 15.1 / 15.4 |
| 2 | 「这一系列行为在一个模态窗口中进行。在完成后模态窗口自动消失」 | 新建 `StartupScanDialog`：打开即跑、**不可取消**（用户确认），成功 → 停留 800ms 显示摘要后**自动关闭**；有失败/跳过 → 保留窗口给「关闭」+ 中文原因 | 15.2 |
| 3 | 「将现有的加载资源按钮与模态窗口与这个功能合并」 | 侧边栏「① 获取资源 → 自动加载游戏资源…」不再弹只扫资源的 `ScanDialog`，改走同一个模态；删除 `ScanDialog` 与两步式「开始扫描」流程 | 15.3 |

## 15.0 现状事实（已核对代码，实施前的事实基线）

- **启动扫描已存在**：`Application/Scanning/StartupScanService.ScanAllAsync` 五步
  （`cache-databases` / `unity-assets` / `bank-index` / `static-tables` / `text-index`），
  由 `MainWindow.RunStartupScanAsync`（`MainWindow.xaml.cs:241-267`）在 `AfterProjectOpenedAsync` 里调用，
  **进度只写状态栏**，无窗口、不可见、不可干预。
- **两个入口割裂**：侧边栏「自动加载游戏资源」`Scan_Click`（`MainWindow.xaml.cs:449`）
  → `PromptScanAsync` → `ScanDialog`（`App/ScanDialog.cs`）**只扫游戏资源、不碰音频/静态表/文本三库**，
  而且是两步式（先枚举再点「开始扫描」）。
- **「四张表」= 四个缓存库**（`WorkbenchCachePaths` / `WorkbenchCacheSchema`），
  每库两张业务表 + `index_meta`：

  | 库 | 业务表 | 归属扫描步骤 |
  |---|---|---|
  | `cache/unity-cache-index.db` | `bundles` / `assets` | `unity-assets` |
  | `cache/bank-index.db` | `banks` / `samples` | `bank-index` |
  | `cache/static-tables.db` | `tables` / `documents` | `static-tables` |
  | `cache/text-index.db` | `files` / `hits` | `text-index` |

- **页面无对外刷新口**：三个工作台页的 `RefreshAsync` 都是 private，且 `TextWorkbenchPage`
  用 `_loaded` 守卫「只在首次显示时载入」→ 扫描完成后前端不会自己更新。

## 15.1 Application：拆扫描原语 + 四张表的显式归属

`Application/Scanning/StartupScanService.cs`：

- 新增 `PrepareDatabaseAsync(project?, progress, ct)`：把 `EnsureCacheDatabases`（建库/校表）包成
  异步步骤。**没有项目也要能跑**（启动阶段主窗口尚无项目），没有游戏目录时它不算「跳过」。
- 新增三个**可单独调用**的公开扫描步骤（原 private 实现保留为内部实现，行为不变）：
  `ScanUnityAssetsStepAsync` / `ScanWorkbenchIndexesStepAsync`（音频 + 静态表 + 文本）、
  `ScanWorkbenchIndexesAsync(kindList, …)`；`projectMatchesIndex` 作为参数透出。
- `StartupScanStepResult` 增加：`CacheDatabase`（`WorkbenchCacheKind?`；音频 / 静态表 / 文本各一格，
  游戏资源不是三库之一 → null）、`RowCount`、`LastWriteUtc`；
  `StartupScanReport` 增加按 `CacheDatabase` 归并的「四张表」投影（模态四行直接用它，
  库、表、行数、时间只有一处来源，杜绝 UI 侧另写一套映射）。
- `ScanAllAsync` 的签名与语义**保持不变**（内部改走新原语），现有 `StartupScanServiceTests`
  四条断言（步骤顺序 / 5 步 / 前提缺失只跳过 / 增量复用）一字不改。
- 新增 `ProbeDatabaseRowsAsync()`：读四库现状（行数 + 文件时间 + 是否完好）。
  **只在 UI 侧读**，读不到不失败（显示「—」），且绝不影响扫描本身。

## 15.2 App：统一模态 `StartupScanDialog`

新文件 `App/StartupScanDialog.cs`（沿用 `ScanDialog` 的代码建 UI + `AppTheme` 口径，色值不落字面量）：

- **形态**：打开即跑的无按钮进度模态；标题「正在扫描并加载资源」。用户已确认**不可取消**
  （不提供取消按钮：半截索引比多等几秒更糟）；关窗（Alt+F4 / 主窗关闭）仍走同一个 CTS 取消。
- **内容**：
  1. 总体进度条 + 当前步骤名 + 细节 + ETA（口径与既有 `ScanDialog` 的 ETA 一致）；
  2. **四张表 4 行**：`资源索引 / 音频索引 / 静态表索引 / 文本索引`，
     每行显示「状态（新建·补表 / 已是最新 / 跳过 / 失败）+ 行数 + 时间」；
  3. 逐步骤明细（复用 `StartupScanReport.DescribeSteps()` 的 ✓/＝/－/✗ 口径）。
- **结束行为**：
  - 无失败且无跳过 → 显示「完成」摘要 → 800ms 后 `Close()`，`Result` 非空；
  - 有失败或跳过 → **不自动关**，标题改「扫描完成（N 项未完成）」，列中文原因 + 「关闭」按钮；
  - `OperationCanceledException` → `Result = null`（宿主按「已取消」处理，不保存不刷新）。
- 不写任何 WPF 无关逻辑：扫描全部在 `StartupScanService` 内，窗口只做呈现与取消传递。

## 15.3 App：合并入口（用户要求的合并点）

`App/MainWindow.xaml.cs`：

- **删除**：`Scan_Click`、`PromptScanAsync`、`EnsureStartupCacheDatabasesAsync` 的展示分支、
  `App/ScanDialog.cs`（含 `autoStart` 两步式流程）。侧边栏按钮 `Click` 改指统一入口。
- **统一入口** `RunStartupScanAsync(StartupScanTrigger trigger)`：
  1. 打开模态（无项目时只跑 15.1 的建库前奏 + 探针，保证「无论何时」都不炸）；
  2. 模态内跑：四库建库/校表 → 游戏资源 → 音频 → 静态表 → 文本（`projectMatchesIndex` = 回灌成功才 true）；
  3. 关窗后：`ScannedCount > 0` 且项目文件存在 → 静默保存项目；`RefreshProjectState()`；
     **预热四个工作台**（15.4）；状态栏写 `report.Describe()`；`UpdateHint()` 引导下一步。
- `AfterProjectOpenedAsync`：`回灌 → 统一模态入口`（原「无窗口状态栏扫描」被替换）。
- `OnWindowLoadedAsync`：删除 `EnsureStartupCacheDatabasesAsync` 调用（改由模态前奏负责），
  恢复上次项目 / 无项目两条路都收敛到同一个入口。
- 目录缺失不再用 `MessageBox` 挡路（原来「没有 Unity 缓存目录」弹窗会打断流程）：
  模态在对应行写「跳过 + 原因」。

## 15.4 「加载到前端」= 启动即全量预热（用户确认：不懒加载）

用户口径：**每次打开软件时刷新所有表单，而不是切页懒加载**。因此：

- 三个工作台页的 `RefreshAsync` 收窄为公开的 `ReloadFromIndexAsync(bool force = true)`
  （内部仍只读各自表缓存，**不重新解析任何文件**）；`TextWorkbenchPage.ReloadFromIndexAsync`
  越过 `_loaded` 守卫（首次显示时若已预热则直接呈现）。
- `MainWindow` 新增 `WarmUpWorkbenchesAsync()`：**主动创建全部 6 个常驻页面**并逐个预热
  （资源页走 `OnProjectRefreshed()`；音频 / 静态表 / 文本走 `ReloadFromIndexAsync()`），
  逐页 try/catch 隔离（单页失败只写状态栏，不打断其它页、不影响扫描结论）。
- 预热在主窗口 `Loaded` 后、模态关闭后各触发一次；因为预热是「读现成表」级操作
  （`bank-index.db` 热读 917ms / `static-tables.db` 热读 11ms / `text-index.db` 608ms），
  不阻塞 UI（async + 后台线程）。

## 15.5 测试（Domain）

`tests/LimbusModEditor.Domain.Tests/StartupScanServiceTests.cs` 追加：

- `PrepareDatabaseAsync_creates_databases_without_a_project`：无项目也能建出四个库（启动阶段前置）。
- `Report_projects_four_cache_table_rows`：报告的「四张表」投影恰好 4 行、每行一个
  `WorkbenchCacheKind`、键名与 `WorkbenchCachePaths` 一一对应。
- `Scan_workbench_indexes_can_run_independently`：只跑工作台索引步骤时，音频 / 静态表 / 文本
  各自状态与独立调用一致（不与游戏资源步骤耦合）。
- `Scan_all_propagates_cancellation`：已取消的 token 让 `ScanAllAsync` 抛 `OperationCanceledException`
  （模态关窗取消依赖这条）。

UI 侧不写 WPF 测试；用本文末的人工核对清单兜。

## 15.6 修复「双击启动后过一会儿才弹模态窗口」（用户实测反馈后追加）

**现象**（用户原话）：「双击启动后，过了一会儿才跳出模态窗口。」

**根因**（实测，非推断）：启动路径把「打开项目」的慢动作排在**窗口弹出之前**：

1. `AfterProjectOpenedAsync` 先 `AutoConfigureAsync`（目录定位：Steam 库 / LocalLow 候选）；
2. 再 `RehydrateAssetsFromIndexAsync` —— 从 `cache/unity-cache-index.db` 回灌引用资产，
   真实项目（119 万行）本机实测 **13.7～14.7 秒**；
3. 两者跑完，才轮到 `RunStartupScanAsync` 弹模态。

于是用户看到的不是「窗口」而是「双击 → 一片空白 → 十几秒后窗口才出来」。
另外两处同样挡在窗口出现之前：构造函数里的 `UpdateDirectoryStatus()`（要扫盘定位四个目录）、
模态构造里的四张表探针（SQLite count，实测 ~430ms）。

**改法**：

- `RunStartupScanAsync` 增加 `prepare` 前奏参数（`status` 写窗口内进度行 / `matchesIndex` 报告
  「索引与项目已一致」/ `CancellationToken` 关窗即取消）。打开项目的慢动作搬进模态第一步：
  `AfterProjectOpenedAsync` 现在只做秒级的「加载项目文件 + 记录最近项目」，随后
  `RunStartupScanAsync(projectMatchesIndex: false, prepare: PrepareProjectInsideModalAsync)`。
- 回灌失败不再弹模态 MessageBox（会把正在跑的模态顶掉）：只写状态栏 + 继续扫描。
- **占位窗口**：先弹一个零成本的最小「正在准备…」窗口，模态在它的 `Loaded` 里构造 ——
  用户看到的是「主窗口 → 等待窗口 → 扫描窗口」连续三下，中间没有空白停顿。
- 四张表的探针改成**后台任务**，并在占位窗口显示前就起跑：模态立刻构造（先显示「—」），
  探测完成后把行数填进去（数字跳进来，而不是「窗口晚半秒才出现」）。
- `UpdateDirectoryStatus()` 挪到模态关闭之后（首帧渲染早已完成）。

**改后实测时序**（发布产物 + `LME_TRACE_STARTUP=1`，本机真实项目 119 万行）：

| 时刻 | 事件 |
|---|---|
| 0 ms | 进程启动 |
| 723 ms | `MainWindow` 构造完成（含资源页就位） |
| 1294 ms | 主窗口 `Loaded`（**窗口可见**） |
| 1923 ms | 占位窗口已显示 → 立刻构造模态 |
| 1964 ms | 模态已构造，关掉占位窗口 |
| 2039 ms | 模态 `RunAsync` 开始（**扫描模态可见**） |
| 2135 ms | 前奏：自动配置目录完成（92 ms） |
| 14698 ms | 前奏：回灌完成（12.5 s，**全程在窗口里显示进度**） |

即：**弹窗从「回灌之后」（14 秒+）提前到「主窗口出现后约 0.7 秒」**，两个窗口之间只有 120ms 间隔。

**诊断打点**：新增 `src/LimbusModEditor.App/StartupTrace.cs`（`LME_TRACE_STARTUP=1` 开关，
不设置时零开销），把启动关键路径时间戳写到 `%TEMP%\lme-startup-trace.log`。
它不是为了修这一条 bug 的一次性脚手架 —— 下一轮再出现「启动某一段慢」时，先开它看数据。

## 审查门（本轮）

- [x] 基线三命令全绿（build / test / publish）
- [x] `ScanDialog.cs` 已删除（全仓 `grep ScanDialog` 只剩文档引用）；侧边栏按钮与模态共用同一入口
- [x] `StartupScanServiceTests` 原有四条断言未放宽（步骤顺序 / 5 步 / 只跳过不抛 / 增量复用）
- [x] 模态成功路径自动关闭、失败/跳过路径保留窗口并给中文原因（代码路径 + 人工核对项 2/6）
- [x] 启动后四个工作台已预热（`WarmUpWorkbenchesAsync`：主动创建 6 个常驻页面并逐页刷新）
- [x] 文档更新：`docs/plans/README.md`、`docs/USAGE.md` §2、`docs/ROADMAP.md` P3.15
- [x] 15.6 启动时序修复：真实发布产物实测「主窗口 1.29s → 占位窗口 1.92s → 扫描模态 2.04s」，
      回灌 12.5s 全程在窗口内显示（详见 §15.6 时序表）

测试 561 → **573**（74 Format + 499 Domain，+5 Domain）。

## 实测证据（本机）

- 新增 5 条 Domain 断言全绿：`PrepareDatabaseAsync_creates_all_four_databases_without_a_project` /
  `Report_projects_exactly_four_cache_table_rows`（四行、文件名与中文名、两张业务表名、
  行数 0 而不是 null、行与扫描步骤对应）/ `Report_without_a_scan_still_projects_four_rows`
  （**没有报告也能出四行**，状态「等待」而不是「跳过」）/ `ScanWorkbenchIndexesAsync_can_run_independently_of_the_asset_scan` /
  `ScanAllAsync_propagates_cancellation_so_a_closed_modal_stops_the_scan`。
- 整包：`74 Format 全绿` + `499 Domain`（其中 `RealBankIndexSmokeTests` 的墙钟预算断言在整包并行下偶发，
  单独跑 2/2 通过 —— 仓库既有 flake，见 `docs/plans/README.md` 注 2）。
- `dotnet publish -c Release -r win-x64` 成功（产物 `artifacts/publish-win-x64`）。

## 人工核对清单（用户侧）

0. **双击启动后模态应立即出现**（主窗口出现后约 0.7 秒内）：先看到最小「正在准备…」等待窗口，
   紧接着变成四张表的扫描窗口；「正在准备项目 / 正在从扫描索引重建资源列表」这类慢动作
   都在窗口里显示进度，不再有「先空白等十几秒」。`%TEMP%\lme-startup-trace.log`
   是启动打点日志（只在设了 `LME_TRACE_STARTUP=1` 时才写），不需要时可直接删。

1. 双击启动软件：**立即出现**扫描模态（无需点任何按钮），四张表逐行显示状态与行数。
2. 扫描完成 → 模态**自动消失**；状态栏显示「启动扫描完成：… 已更新 / 用时 N 秒」。
3. 切到音频 / 文本 / 静态数据工作台：**数据已就绪**（不需等待、不需点「重新扫描」）。
4. 关掉软件再启动一次：四张表显示「已是最新」，整体秒级完成（增量扫描）。
5. 侧边栏「① 获取资源 → 自动加载游戏资源…」：打开的还是同一个模态，跑完自动关闭。
6. 故意改错游戏目录（设置页）→ 模态列出「跳过 + 原因」，**不自动关闭**，点「关闭」回到主界面。
