# 代码结构介绍（CODE-STRUCTURE）

> 目的：让接手的 agent **只读代码**就能建立全项目心智模型——分层边界、依赖方向、
> 关键运行流程、跨文件契约与不变量，以及「改哪里」的决策路径。
>
> 配套文档：
> - 逐文件功能索引（按目录逐条列文件职责与关键类型）：`docs/PROJECT-INDEX.md`
> - 待办 / 铁律 / 真实环境事实：`docs/STATUS.md`
> - 用户可见行为与界面：`docs/USAGE.md`
> - 写回链路真实验证证据：`docs/REALDATA-VERIFY.md`
> - 自审与风险记录：`docs/REVIEW.md`
>
> 本文所有结论来自源码阅读（read/grep），不依赖运行程序、探针或截图。
> 文中行号为撰写时的真实行号，改代码后请以符号名检索为准。

---

## 1. 一句话总览

Limbus Mod Editor 是一个 **Windows-only 的 C#/.NET 8 + WPF 桌面工作台**，
用于给《Limbus Company》做模组：读取真实游戏数据（Unity 缓存 bundle / FMOD bank /
lang 文本 / 静态数据表）→ 在四个工作台里浏览与编辑 → 一键导出加载器可消费的
模组包（`.carra` / `.bank` / `.rebank` / `.staticmod` / lang 补丁）→ 或直接铺到游戏
目录做调试（带逐文件备份与还原）。

它不是启动器替代品：**游戏目录只读、Unity 缓存只读、catalog 只读**（唯一例外是用户
显式触发的调试应用，见 §6）。

---

## 2. 项目分层与依赖方向

`LimbusModEditor.slnx` 声明 12 个 src 项目 + 2 个测试项目。依赖方向严格单向
（下层永不引用上层）：

```
                        ┌──────────────────────────────┐
                        │  App（WPF, net8.0-windows）   │  ← 界面、页面宿主、对话框
                        └───────────────┬──────────────┘
                    ┌────────────┬──────┴────────┬───────────────┐
                    ▼            ▼               ▼               ▼
              Application  Infrastructure      Editing      Formats.Unity
             （编排/服务层） （路径安全等基础）  （图像编解码） （Unity 后端）
                    │            │               │               │
      ┌─────────────┼────────────┴───────────────┴───────────────┤
      ▼             ▼            ▼            ▼            ▼      ▼
 Formats.Carra  Formats.Bank Formats.Rebank Formats.Lunartique ─┘
      └─────────────┴────────────┴────────────┴──────┬─────────┘
                                                     ▼
                                        Formats.Abstractions
                                                     │
                                                     ▼
                                                  Domain（零 NuGet）
```

| 项目 | TFM | 关键 NuGet | 角色 |
|---|---|---|---|
| `LimbusModEditor.Domain` | net8.0 | 无 | 全仓库共享数据模型与纯数据规则（资产、编辑、格式枚举、导出布局、项目文档） |
| `LimbusModEditor.Formats.Abstractions` | net8.0 | 无 | 格式插件唯一接口契约 `IModFormatHandler` |
| `LimbusModEditor.Infrastructure` | net8.0 | 无 | 基础实现（当前只有 `SafePathService`，且**未被调用**，见 §9） |
| `LimbusModEditor.Editing` | net8.0 | ImageSharp 3.1.11 | Unity 纹理 ↔ PNG 编解码、图集切分/回填、缩略图 |
| `LimbusModEditor.Formats.Unity` | net8.0 | AssetsTools.NET 3.0.5 | Unity bundle/SerializedFile 唯一后端（不手写解析器） |
| `LimbusModEditor.Formats.Carra` | net8.0 | Joveler.Compression.XZ 5.0.2 + SharpCompress 0.38.0 | Carra/Carra2 容器 |
| `LimbusModEditor.Formats.Bank` | net8.0 | 无（P/Invoke 运行时绑 FMOD C ABI） | FMOD bank/FSB5 索引层 + 音频编解码抽象 |
| `LimbusModEditor.Formats.Rebank` | net8.0 | 无 | .rebank 差分包 |
| `LimbusModEditor.Formats.Lunartique` | net8.0 | SharpCompress 0.38.0 | Lunartique 安装/卸载配对包 |
| `LimbusModEditor.Application` | net8.0 | Microsoft.Data.Sqlite 8.0.11 + ImageSharp | WPF 无关的服务编排层（**能被直接单测**） |
| `LimbusModEditor.App` | net8.0-windows | WPF-UI 4.3.0 | 界面（**没有测试工程**，逻辑要下沉到 Application 才能测） |
| `LimbusModEditor.Cli` | net8.0 | 无 | 三个子命令的命令行入口 |
| `tests/LimbusModEditor.Domain.Tests` | net8.0 | xunit 2.9.3 | 行为测试主战场（引用 Domain/Editing/Application/Formats.Unity） |
| `tests/LimbusModEditor.Format.Tests` | net8.0 | xunit 2.9.3 + AssetsTools.NET | 格式层与真实样本测试 |

构建约定（`Directory.Build.props`）：`net8.0`（WPF 项目覆盖为 `net8.0-windows`）、
`Nullable=enable`、`TreatWarningsAsErrors=true`（**任何警告即失败**）、
Windows 下默认 `RuntimeIdentifier=win-x64`、`CopyLocalLockFileAssemblies=true`。

---

## 3. 目录地图（先记这 8 条）

| 想看什么 | 去哪里 |
|---|---|
| 数据模型 / 枚举 / 导出目录布局 | `src/LimbusModEditor.Domain/`（`Assets/` `Edits/` `Formats/` `Projects/`） |
| Unity bundle 读写、容器路径、纹理/Sprite/文本/音频对象 | `src/LimbusModEditor.Formats.Unity/AssetsToolsBackend.cs`（2053 行，唯一后端） |
| 业务编排（扫描、索引、搜索、预览、编辑、导出、调试） | `src/LimbusModEditor.Application/`（按功能分子目录） |
| 界面与页面 | `src/LimbusModEditor.App/`（`MainWindow` + `WorkbenchPages/` + 对话框） |
| 格式包（Carra/Bank/Rebank/Lunartique） | `src/LimbusModEditor.Formats.*/` |
| 缓存库（四个 SQLite） | `src/LimbusModEditor.Application/Caching/` + `Scanning/` + `Texts/` + `StaticMods/` + `Assets/BankIndex*` |
| 导出流水线 | `Domain/Formats/ExportLayout.cs` + `Application/Build/ModExportPlanService.cs` + `ModPackExportService.cs` |
| 调试应用（写游戏目录） | `Application/Debugging/ModApplyService.cs` + `StaticModApplyService.cs` + `DebugApplyService.cs` |

---

## 4. 界面外壳：三列页面模型（plan-02）

`App/MainWindow.xaml` 是唯一的窗口骨架，三列布局：

```
[48px 活动栏] [220px 共享侧边栏] [* 页面宿主 PageHost]
   6 个 RadioButton         项目工作区 + ① 获取资源 / ② 产出模组 / 更多      ContentControl
```

- **页面宿主与注册表**：`MainWindow.xaml.cs:43` 的 `PageOrder = ["assets","bank","text","static","help","settings"]`；
  `ShowPage(string key):124` 惰性创建并**常驻**（切换不销毁，保住各页的搜索/选中/预览状态）；
  工厂是 `CreatePage(key):140` 的 switch。新增页面的标准做法 =
  在 `CreatePage` 注册 key + 在 `MainWindow.xaml` 活动栏加一个 `RadioButton` + `SyncActivityBar:161` 补映射。
- **宿主契约**：`App/WorkbenchPages/IWorkbenchHost.cs`。页面通过构造函数拿 `IWorkbenchHost`
  （项目、项目文件、`AppEnvironment`、`LangEdits`/`StaticEdits` 编辑集会话、状态栏、
  刷新、保存、`ShowPage`），**不允许反向依赖 `MainWindow` 具体类型**。
- **无项目遮罩**：`NoProjectOverlay`（`MainWindow.xaml:149`）只遮页面宿主；
  `NeedsProject(key)`（`MainWindow.xaml.cs:177`）决定哪些页面需要项目，设置/教程页始终可用。
- **启动顺序**（性能敏感）：构造函数只做 `InitializeComponent` + 建资源页；
  所有磁盘动作（目录定位、启动扫描、模态窗）排在 `Loaded` 且
  `DispatcherPriority.Background` 之后（`MainWindow.xaml.cs:111-118`），
  并有 `StartupTrace`（`App/StartupTrace.cs`）埋点。**不要把秒级工作提前到窗口出现之前**。
- **共享侧边栏**是设计约束：`① 获取资源`（自动加载游戏资源 = 启动扫描入口）与
  `② 产出模组`（**只有两个按钮**：`导出模组…`、`使用当前修改启动游戏进行调试`）在
  `MainWindow.xaml:117-142`。lang 的独立导出按钮已删除，导出统一走「导出模组…」。
- **UI 状态持久化**：`Application/AppConfig/UiStateService.cs` 把每页的预览列宽写
  `<程序目录>/config/ui-state.json`（页面 key 见 `WorkbenchPageKeys`），
  与 `shared-config.json` 同风格（原子写 + 损坏回退）。**UI 状态绝不写进 `.lmeproj`**。

### 四个工作台页与公共骨架

| 页面 | 文件 | 职责 |
|---|---|---|
| 资源 | `App/WorkbenchPages/AssetsWorkbenchPage.xaml(.cs)`（1336 行） | 容器路径树/列表 + 搜索筛选排序 + 七形态预览 + 属性编辑 + 替换/导入 |
| 音频 | `WorkbenchPages/BankWorkbenchPage.xaml(.cs)`（1040 行） | bank 树 + 跨 bank 样本总表 + 试听 + FSB 替换 + 静态/占位 |
| 文本 | `WorkbenchPages/TextWorkbenchPage.xaml(.cs)`（744 行） | lang 文件树 + 键值树（就地编辑）+ 源文本预览 + 搜索 |
| 静态 | `WorkbenchPages/StaticWorkbenchPage.xaml(.cs)`（523 行） | 静态数据表索引/搜索 + JSON 文档编辑 + staticmod 产物 |

公共骨架：`WorkbenchPages/WorkbenchShell.xaml(.cs)`（列宽/视图模式/分节）、
`WorkbenchPages/JsonTreeEditor.xaml(.cs)`（键值树 + 行内编辑 + 右键菜单）、
`Themes/Theme.xaml` 与 `Themes/WorkbenchStyles.xaml`（**设计色只允许出现在这两个文件**：
`WorkbenchPages/` 下不得出现硬编码 `#RRGGBB` 或 `Color.FromRgb(0x`，这是收口验收门）。

---

## 5. 核心运行流程

### 5.1 启动 → 扫描（每次启动都跑）

```
MainWindow ctor
  └─ ShowPage("assets")  ← 只建页面，不碰磁盘
Loaded → OnWindowLoadedAsync()
  ├─ UpdateHint() / 恢复上次项目（AppEnvironment.Config.LastProjectFile）
  ├─ RunStartupScanAsync()   ← 统一模态 StartupScanDialog（打开即扫、不可取消）
  │    └─ StartupScanService.RunAsync()           Application/Scanning/StartupScanService.cs（680 行）
  │         ├─ 步骤 ①：建库/校表四个 SQLite（cache/，幂等；库在表不在也会补建）
  │         ├─ 步骤 ②：Unity 缓存增量扫描 → UnityCacheScanService（530 行）
  │         │     └─ UnityCacheSqliteIndexStore（279 行）读写 cache/unity-cache-index.db
  │         ├─ 步骤 ③：音频索引 → BankIndexService → BankIndexStore（cache/bank-index.db）
  │         ├─ 步骤 ④：静态表索引 → StaticIndexService → StaticTableIndexStore（cache/static-tables.db）
  │         └─ 步骤 ⑤：文本索引 → LangTextWorkbenchService + TextIndexStore（cache/text-index.db）
  └─ 预热四个工作台（CreatePage 全部 key），不依赖切页懒加载
```

- **引用模式**：扫描只枚举磁盘并把事实写进索引库，**不复制任何游戏文件**。
- **失效规则只有一条**：源签名（`(size,mtime)` 或 catalog 内层内容哈希）变化才重解析；
  缓存**只存 vanilla 事实**，编辑集绝不落缓存（见 `Caching/WorkbenchCacheSchema.cs` 的类注释）。
- **逐条容错**：单个 bundle 解析失败进 diagnostics，不中断扫描。
- 索引条目口径变化时靠「口径版本常量 + 整库重建」升级（文本索引有 `IndexFormatVersion`；
  静态/音频库用 `index_meta.source_key/signature`）。

### 5.2 打开项目 → 资源视图

```
ProjectService.LoadAsync(.lmeproj)     Application/Projects/ProjectService.cs
  ├─ SchemaVersion 校验（>CurrentSchemaVersion 拒绝）
  ├─ SkipReferenceAssetsConverter：纯引用资产不落盘（项目文件从 GB 降到 KB 级）
  └─ RehydrateFromIndexAsync：后台从 unity-cache-index.db 回灌引用资产（不阻塞 UI）
资源页数据流：
  ModProject.Assets
    → AssetSearchService.Search(query)        过滤 + 排序（可后台线程，快照式）
    → AssetDisplay                           显示路径/名称/中文类型与状态标签（容器视图口径）
    → AssetTreeBuilder                       按显示路径逐段惰性建树
    → AssetPreviewRegistry → IAssetPreviewProvider  七形态预览（图像/文本/JSON行/音频/摘要/脚本/十六进制）
```

**关键口径区分**（曾出错，务必分清）：
`AssetRecord.ContainerPath` = CAB/SerializedFile 名（导出/构建链路消费）；
`AssetRecord.Metadata["containerEntry"]` = Unity `m_Container` 给出的**游戏内资源路径**
（显示层消费）。两者不可混用。

### 5.3 编辑 → 导出（plan-16 的流水线）

```
编辑动作四类：
 ① 纹理/Sprite/字段替换  → AssetEditService / SpriteMetadataEditService / UnityFieldEditService
 ② 文本（lang）           → LangEditSession（宿主持有）→ LangTextWorkbenchService
 ③ 静态数据表             → StaticEditSession → StaticIndexService
 ④ 音频（bank/FSB）       → BankAudioService / BankIndexService

导出：
  ExportMod_Click（侧边栏「导出模组…」，先选目标目录；SaveFileDialog 的「选择此文件夹」惯例）
  → MainWindow.PrepareExportPlanAsync()   导出/调试**共用前奏**（两条链路必须同一份分析与上下文）
      ├─ MaterializeEditedAssetsAsync    已编辑的缓存资源实体化进项目
      ├─ SaveProjectQuietlyAsync         存项目
      └─ ModExportPlanService.BuildAsync()  分析修改 → 点亮哪些槽位（S1）
          └─ ExportLayout（Domain）      <目标>/<项目名>_<种类>/<格式>/<产物>
  → ModPackExportService.ExportAsync()                       按槽位执行（S4）
      ├─ Bank/Rebank   → Formats.Bank / Formats.Rebank
      ├─ Carra         → UnityBundleBuildService + UnitySerializedFileBuildService
      │                  + UnityCacheMaterializationService（先把引用 bundle 复制进项目）
      │                  + Formats.Carra
      ├─ Lunartique    → LunartiqueCarraConversionService + Formats.Lunartique
      ├─ Lang bus/patch/pathset → LangExportFormatter（带「可表达性门」：表达不了则整份不产出）
      └─ StaticMod     → StaticModService
  → ModExportReportWindow                                    报告（含空槽位说明）
```

- `plan.PlannedSlotCount == 0` 时直接给「没有可导出的修改」引导，不写任何文件。

- 槽位是**闭集合**：`ExportLayout.All` 是唯一清单，`For` 遇到未登记值直接抛异常。
- `ExportAdvisor`（`Application/Build/ExportAdvisor.cs`）是给用户的「出口建议」，
  与 `ModExportPlanService` 是两套：前者出文案、后者出执行计划。
- 旧通道 `ModExportService`（多格式导出/`ExportAllAsync`）仍是 CLI 与部分测试的入口，
  界面不再暴露，**不要顺手删**（会连带 17 处引用与 5 个测试文件）。

### 5.4 调试应用（唯一会写游戏目录的路径）

`DebugMod_Click` → `ModApplyService`（500 行）：
前置检查（项目存在 / 游戏目录存在 / **`IsGameRunning()` 为假**（检测 `LimbusCompany.exe`）/ 用户确认弹窗）→
与「导出模组」**同一条前奏**（`MainWindow.PrepareExportPlanAsync`，只是根目录换成
`<项目>/builds/debug-pack`）→ `ModPackExportService.ExportAsync` → `ModApplyService.ApplyAsync`
按 `ExportSlotDescriptor.DebugOverwritePatch` 决定语义（true = 导出成 `__data`/`.bank`
后**备份覆盖**；false = 标准 patch 写盘 + `.bak`）。护栏：逐文件 sha256 + 备份 +
`steps.tsv` 还原清单 + 关闭时**逆序逐字节还原** + 目标被外部改动则记冲突不覆盖 +
中途失败先整体回滚。静态模组走 `StaticModApplyService`
（catalog 双写，写入前用 size 合理性自校准布局，不符即拒绝写）。

---

## 6. 跨文件契约与不变量（改代码前必读）

| # | 不变量 | 位置 | 破坏后果 |
|---|---|---|---|
| 1 | Unity 纹理负载**左下原点**行序：解码翻一次、编码翻回一次 | `Editing/Images/UnityTextureCodec.cs`（`StorageRow` / `ToImage` / `FromPng` / `DecodeDxt`） | 预览与写回上下颠倒；游戏内贴图倒置 |
| 2 | `UnityTexturePixelFormat` 显式数值 = Unity `TextureFormat`；`Argb32` 是 A,R,G,B | 同上（枚举与 `ReadPixel`/`WritePixel`） | 格式判定错、颜色通道互换 |
| 3 | Unity 修改必须走 `AssetsToolsBackend` 适配层；**写回必须先未压缩 `Write` 再按原压缩 `Pack`** | `Formats.Unity/AssetsToolsBackend.cs`（`WritePackedBundle`） | 修改被静默丢弃（历史上发生过，见 `docs/REALDATA-VERIFY.md` §4） |
| 4 | Carra2 键 = `<缓存外层>/<内层>/<pathId>.<类型表索引>`，逐条目 XZ | `Formats.Carra/CarraModels.cs`、`Application/Build/UnityCacheExportService.cs` | 加载器一条都匹配不上 |
| 5 | `.rebank` 条目名必须是**真实样本名**（`{fsbIndex}/{sampleName}.wav`），不是 `{i}.fsb` | `Formats.Bank/Fsb5Models.cs` + `ModPackExportService` rebank 槽位 | 加载器按「替换数 0」报错回滚 |
| 6 | lang 索引条目口径 = **相对活动语言目录**；加载器口径 = 相对 lang 根 | `Application/Texts/TextIndexStore.cs`（+`index_meta.language_prefix`）与 `LangTextWorkbenchService.ToPatchKey` | 补丁键错位；`LangEntryCaliberTests` 会红 |
| 7 | `IsModified` ≠ 有修改；界面一切「已修改」标记用 `HasRealEdits` | `LangEditSession` / `LangTextWorkbenchService` | 打开即显示「已修改」（历史缺陷） |
| 8 | 四个缓存库位置固定 `<程序目录>/cache/`，绝不写游戏目录/catalog | `Application/Caching/WorkbenchCachePaths.cs` | 污染用户游戏数据 |
| 9 | 导出目录布局由 `ExportLayout` 唯一决定；`Sanitize` 同时影响目录名与文件名 | `Domain/Formats/ExportLayout.cs` | 输出树与加载器匹配口径不一致 |
| 10 | `AssetRecord.Metadata` 字典是**大小写不敏感**，且是扩展槽 | `Domain/Assets/AssetModels.cs` | 已有工程读不到数据 |
| 11 | `AssetType`/`AssetEditState` 枚举**隐式数值被 UI 当筛选值下发** | `Domain/Assets/AssetModels.cs` + `AssetsWorkbenchPage` | 筛选语义整体偏移；新成员只能追加末尾 |
| 12 | 项目文件版本 `ModProject.CurrentSchemaVersion`；未知更高版本拒绝打开 | `Domain/Projects/ModProject.cs` + `Application/Projects/ProjectService.cs` | 旧版本编辑器静默丢数据 |
| 13 | 设计色只允许在 `Themes/*.xaml`；`WorkbenchPages/` 不得硬编码色值 | `App/Themes/`、收口验收门 | 违反工作台一致化验收 |
| 14 | 磁盘写入统一走 `AtomicOutput`（临时文件 + 替换） | `Application/Build/AtomicOutput.cs` | 中断留下半个文件 |
| 15 | 未知负载/压缩/字段一律 **fail fast + 中文错误**，不猜测、不静默降级 | 全仓库（各 handler 的 `ValidateAsync`/异常路径） | 产物「看起来成功」实则无效 |
| 16 | **缓存只影响速度，不影响正确性**：任何损坏一律删库重建，且「删库」不得改变功能结果 | 四个 Store 的类注释 + `SqliteTableCache.RecreateOrThrow`；由「有缓存 vs 删库」对照测试钉死 | 缓存变成事实来源，删库即出错 |

---

## 7. 数据落盘位置一览

| 位置 | 内容 | 谁写 |
|---|---|---|
| `<程序目录>/config/shared-config.json` | 游戏目录 / Unity 缓存 / 模组目录 / FMOD 目录 / 最近项目 | `Application/AppConfig/SharedAppConfig.cs`、`AppEnvironment` |
| `<程序目录>/config/ui-state.json` | 每页预览列宽等 UI 偏好 | `Application/AppConfig/UiStateService.cs` |
| `<程序目录>/cache/unity-cache-index.db` | Unity 缓存 bundle/资产索引（含 `container_entry`） | `Application/Scanning/UnityCacheSqliteIndexStore.cs` |
| `<程序目录>/cache/bank-index.db` | bank 头信息 + 逐样本行 | `Application/Assets/BankIndexStore.cs` |
| `<程序目录>/cache/text-index.db` | lang 文件级索引 + 命中（`index_meta.language_prefix`） | `Application/Texts/TextIndexStore.cs` |
| `<程序目录>/cache/static-tables.db` | 静态表元数据 + 按需有界正文缓存 | `Application/StaticMods/StaticTableIndexStore.cs` |
| `<程序目录>/projects/<名>/` | 新建项目的默认位置 | `Application/Build/NewModTemplateService.cs` |
| `<项目>/<名>.lmeproj` | 项目文档（`ModProject` JSON） | `Application/Projects/ProjectService.cs` |
| `<项目>/sources/cache/<外>_<内>.bundle` | 编辑过的缓存 bundle 实体化副本 | `Application/Scanning/UnityCacheMaterializationService.cs` |
| `<目标>/<项目名>_<种类>/<格式>/` | 导出产物树 | `Application/Build/ModPackExportService.cs` |
| `%APPDATA%\LimbusCompanyMods` | 真实加载器的模组目录（默认导出目标） | 用户配置 / `Debugging/ModDirectoryLocator.cs` |

**游戏目录、Unity 缓存、catalog 一律只读**，唯一例外是 §5.4 的调试应用。

### 7.1 四个缓存库的失效规则与「口径版本」

| 库 | 源签名 | 失效粒度 | 口径/结构版本 |
|---|---|---|---|
| `unity-cache-index.db` | **不用 `index_meta`**：新鲜度直接落在 `bundles(size, mtime_ticks)` 行上 | 逐 bundle | 层内 `EnsureContainerEntryColumn` / `EnsureStaticBundleColumn` 轻量补列（旧库自动迁移） |
| `bank-index.db` | 目录签名 `0:mtimeTicks`（源键 = 目录全路径小写） | 逐 bank 文件 `(size_bytes, mtime_ticks)`，只删该 bank 的 `samples` 行；行集对账删已消失文件 | `WorkbenchCacheSchema` 建表脚本 |
| `static-tables.db` | **内层内容哈希**（小写） | 源变即整库重建；正文缓存 LRU 淘汰（上限 64MB，淘汰到 80%） | 同上 |
| `text-index.db` | 内容口径版本 + 活动语言目录签名 + **`config.json` 内容哈希**（切语言必须用内容哈希，size/mtime 不可靠） | 整库 / 单文件 `(size,mtime_ticks)` / 行集不一致整表重建 | `TextIndexStore.IndexFormatVersion = "v4"`（v1 首版 → v2 不收录根级 `config.json` → v3 口径去根文件夹 → v4 并入活动语言目录名 + `language_prefix`） |

四库共用的底座能力（`Caching/SqliteTableCache.cs`）：幂等建表、`EnsureColumn` 轻量迁移、
`Write` 单事务批量写（WAL + `synchronous=NORMAL`）、`EnsureSource(sourceKey, signature, languagePrefix)`
（源/签名/前缀任一不一致 → 清空业务表 + `index_meta` 并返回 true）、
损坏判定只认 SQLITE_CORRUPT(11)/SQLITE_NOTADB(26) → 删库重建。
**「文件在但表不在」（0 字节库 / 上次建库被打断）是 `no such table: index_meta` 的直接来源**，
四个库的读路径都内置缺表自愈，启动扫描的 `EnsureCacheDatabases` 也会补建。

### 7.2 中间产物与工作目录（排查「文件去哪了」用）

| 中间产物 | 位置 |
|---|---|
| Unity 重打包结果 | `<项目根>/builds/unity-bundles/<源文件名>` |
| bundle 链式重打包临时 | `<输出>.step<N>.tmp`、`<输出>.lme-build.tmp` |
| 替换文件暂存 | `<项目>/edits/assets/<assetId:N><ext>` |
| 图集拆分产物 | `<项目>/edits/atlases/<assetId:N>/`（+`layout.json`） |
| 静态模组打包临时 | `%TEMP%/lme-staticmod-pack-<guid>/` |
| Lunartique 打包暂存 | `%TEMP%/lme-lunartique-<guid>/` |
| 静态模组应用临时 bundle | `%TEMP%/lme-static-apply-<guid>.bundle` |
| 调试包（「启动游戏调试」的导出根） | `<项目>/builds/debug-pack/<项目名>_<种类>/<格式>/` |
| 调试备份 + 还原清单 | `<备份根>/<yyyyMMdd-HHmmss-fff>/` + `steps.tsv`（备份根默认 `<项目>/backups`） |
| 通用调试备份（旧通道） | `<项目>/backups/<时间戳>/` |
| 写回验证产物 | `<repo>/artifacts/realdata-verify/` |

### 7.3 编辑集在哪、由谁持有、「已修改」怎么判

| 编辑类型 | 存放位置 | 「已修改」判定 |
|---|---|---|
| 资源替换/批量替换 | `AssetRecord.Metadata["replacementPath"]` + `ModProject.Edits` | `AssetEditService.HasEdits`（替换文件存在 + 字段编辑 + Sprite 元数据三者之一） |
| Unity 字段编辑 | `Metadata["unityFieldEdits"]`（`UnityFieldEditSet` JSON） | 同上 |
| Sprite 元数据 | `Metadata["spriteMetadata"]` | 同上 |
| lang 文本 | `LangTextWorkbenchService._edits`（内存），**宿主持有的 `LangEditSession`** 是可见入口 | `LangEditSession.HasRealEdits`（vanilla vs modified 逐字节比较）；**`IsModified` 只表示「打开过」** |
| 静态数据表 | `StaticEditSession._entries`（内存） | `StaticEditSession.Snapshot()`（已剔除无差异条目） |

- 两类 Session **只存在内存，关窗即丢**；导出与调试读取它们的**唯一入口是 `Snapshot()`**。
- 资源索引缓存与文本索引缓存都**不存编辑集**（只存 vanilla 事实）。
- `DebugOverwritePatch` 标记（`ExportLayout`）当前**没有代码读取点**：
  `ModApplyService` 按 `ExportSlot` 显式分派（改槽位语义时以那里的 switch 为准）。

---

## 8. 「我要改 X，去哪看」决策表

| 需求 / 症状 | 首选文件 | 连带检查 |
|---|---|---|
| 资源列表显示的名字/路径/类型标签不对 | `Application/Assets/AssetDisplay.cs` | `Formats.Unity/UnityClassId.cs`（class id → `AssetType`）、`Domain/Assets/AssetModels.cs`（枚举） |
| 搜索/筛选/排序行为 | `Application/Assets/AssetSearchService.cs`（`AssetSearchQuery`） | `AssetsWorkbenchPage` 筛选栏绑定、`AssetSortKind` |
| 目录树层级/懒展开/重名消歧 | `Application/Assets/AssetTreeBuilder.cs` | `AssetDisplay.TreePath`、`AssetTreeBuilderTests` |
| 预览形态（图像/文本/JSON/音频/十六进制） | `Application/Assets/Preview/AssetPreview.cs`（`AssetPreviewRegistry`）+ `AssetPreviewProviders.cs` | `Assets/TextPreviewService.cs`、`Preview/HexDumpService.cs`、`Assets/AssetPropertyService.cs` |
| 纹理/Sprite 预览或替换异常 | `Editing/Images/UnityTextureCodec.cs`、`Formats.Unity/AssetsToolsBackend.cs`（resS 读写、`ReadBundleSpriteComposite`） | 行序不变量 §6-1 |
| Unity 字段树/字段编辑/PPtr 依赖 | `Formats.Unity/UnityAssetService.cs`、`Application/Assets/UnityFieldEditService.cs` | `Domain/Edits/UnityFieldEditModels.cs`、`Build/UnityBundleBuildService.cs` |
| 音频 bank 解析/样本表/试听 | `Formats.Bank/BankModels.cs`、`Fsb5Models.cs`、`Application/Assets/BankDirectoryService.cs`、`BankAudioService.cs` | FMOD DLL 发现 `AppConfig/AppEnvironment.cs`（`FmodLibraryLocator`） |
| lang 文本工作台：树空白/搜索/编辑/已修改标记 | `App/WorkbenchPages/TextWorkbenchPage.xaml.cs`、`Application/Texts/LangTextWorkbenchService.cs` | `TextIndexStore.cs`（口径+版本）、`LangTextTreeBuilder.cs`、`JsonTreeEditor.xaml.cs` |
| 静态数据表索引/正文缓存/staticmod 生成 | `Application/StaticMods/StaticIndexService.cs`、`StaticTableIndexStore.cs`、`StaticModService.cs`、`StaticBundleLocator.cs` | `Caching/WorkbenchCacheSchema.cs` |
| 导出目录结构/新增槽位 | `Domain/Formats/ExportLayout.cs` | `Build/ModExportPlanService.cs`、`ModPackExportService.cs`、`Debugging/ModApplyService.cs`（三处消费方） |
| 导出产物内容不对（某种格式） | `Application/Build/ModPackExportService.cs` 对应 `ExportXxxAsync` | 该格式项目 `Formats.*/` |
| 出口建议文案/自动分析 | `Application/Build/ExportAdvisor.cs` | `App/ModExportReportWindow.cs` |
| 调试应用写盘/还原/冲突 | `Application/Debugging/ModApplyService.cs`、`StaticModApplyService.cs` | `DebugApplyService.cs`、`GameLaunchService.cs` |
| 目录自动发现（游戏/缓存/模组/FMOD） | `Application/Debugging/GameDirectoryLocator.cs`、`UnityCacheLocator.cs`、`ModDirectoryLocator.cs` + `AppConfig/AppEnvironment.cs` | `App/WorkbenchPages/SettingsPage.cs` |
| 启动变慢 / 弹窗时序 | `App/MainWindow.xaml.cs`（`Loaded` 路径）、`App/StartupTrace.cs`、`Application/Scanning/StartupScanService.cs` | `StartupScanDialog.cs` |
| 项目文件变大 / 打开慢 | `Application/Projects/ProjectService.cs`（`SkipReferenceAssetsConverter`、回灌） | `Domain/Projects/ModProject.cs` |
| CLI 行为 | `src/LimbusModEditor.Cli/Program.cs` | `Application/Build/ModExportService.cs`、`Assets/ModImportService.cs` |

---

## 9. 已知结构边界与「不要做」的事

- **`Infrastructure/FileSystem/SafePathService.cs` 是未接线代码**：全仓库除自身外无调用点，
  越界校验以私有内联方法形式散落在 `Build/ProjectBuildService.cs`、`Debugging/DebugApplyService.cs`、
  `Assets/ModImportService.cs` 等处。要统一防护，应该是**把内联实现改为调用它**，
  只改它本身不会改变任何运行时行为。
- **`Formats.Unity/AssetsToolsBackend.cs` 是 2053 行的单类后端**：所有 Unity 能力都从这里进；
  新增 Unity 能力优先在此扩展，不要新建第二个解析器。
- **mipmap 只到「布局与切片」**：`Editing/Images/UnityTextureCodec.cs` 的 `ToImage`/`FromPng`
  只处理 level 0。
- **`App` 项目没有测试工程**：任何需要被测试覆盖的逻辑都应下沉到 `Application`。
- **不要**：手写完整 Unity 解析器、伪造/分发 FMOD 专有 DLL、把未知对象静默转成「成功输出」、
  在无真实样本时宣称格式兼容、用宽泛递归删除或跳过备份去写用户游戏目录。

---

## 10. 常用命令

```text
dotnet build LimbusModEditor.slnx --no-restore --nologo
dotnet test  LimbusModEditor.slnx --no-build --nologo
dotnet publish src/LimbusModEditor.App/LimbusModEditor.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish-win-x64 --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1
```
