# plan-16：导出格式体系 + 导出/调试一体化重构

覆盖需求（2026-09 第五批，用户原话逐条对应）：

1. **lang 模组编辑的导出格式改为参考 LCTA**——允许多种导出格式，**不再支持现有的导出格式**。
2. **lang 页面不再保留根文件夹**，直接从 `lang/a_folder_name` 这一层开始。
3. **导出模组 / 应用模组整套设计重做**：
   - 删掉资源工作台预览页的「调试」板块（调试状态 + 构建调试覆盖层 + 应用并启动调试）；
   - 删掉 bank 处理页面预览里的「导出整包 .bank…」与「导出 .rebank…」两个按钮；
   - 删掉左侧侧边栏的整个「② 产出模组」导出板块；
   - 侧边栏只留**两个**按钮：**导出模组**、**使用当前修改启动游戏进行调试**；
   - 「导出模组」= 选一个目录 → 分析当前全部修改 → 修改与导出格式一一对应 → 在目标目录下按「种类 → 格式」两层建文件夹并放产物。

已确认的口径（本轮提问得到的答复，实施时按此执行，不再自行发挥）：

| 问题 | 答复 |
|---|---|
| lang 树结构 | **A**：顶层只显示文件夹 + 根级 json 文件平铺，不再有任何包裹/根节点 |
| **需求 2 的真实含义（用户澄清）** | **不是树的问题，是「后端数据库条目」的口径问题**：`text-index.db` 里的条目不带根文件夹 —— 见 §5 |
| `_fmod` 下的格式槽 | `bank/`（完整 .bank）、`rebank/`（差分 .rebank） |
| `_data` 下的格式槽 | `carra/`（.carra）、`lunartique/`（.zip） |
| 其它种类 | `staticmod`（.staticmod）；lang 补丁**参考 LCTA 支持的所有种类格式**（见 §4） |
| 调试按钮 | bank / 资源 patch 走「导出为 `__data` 与 `.bank` → 备份后覆盖 → 关闭后恢复」；lang / staticmod 走「标准 patch → 关闭后恢复」；核心是**模拟加载器的应用语义**，够制作者自测即可 |

---

## 0. 事实依据（实施时直接引用，不猜）

### 0.1 真实加载器（LCTA）认什么

| 种类 | 扩展名 | 载荷事实 | 证据 |
|---|---|---|---|
| 完整音频包 | `.bank` | 文件名 = 游戏内 FMODBuilds/Desktop 下的目标 bank 文件名，整包替换 | `launcher/sound.py:98-113` |
| 音频差分 | `.rebank` | zip：`rebank.json`（`format/name/version/author/description/base_bank/created/count/files[]`）+ `{fsb序号}/{wav文件名}.wav` | `webutils/bank/rebank.py:196-213`、`launcher/bankmod.py:72-83`；`base_bank` 可带可不带 `.bank`（`bankmod.py:78-81`） |
| 资源对象包 | `.carra` / `.carra2` | zip：条目键 `<缓存外层键>/<内层键>/<pathId>[.<类型表索引>]`，条目值为 XZ 压缩的对象原始字节；`Uninstallation` 目录语义见 `compress.py` | `launcher/modcache.py:101-107`、`launcher/patch.py:255-309`、`compress.py:49-83` |
| Lunartique | `.zip` | 包里同时有 `Uninstallation`/`Installation` 根目录，两侧同构 `<account>/<bundle>/<path_id>.<type_id>`，条目为 `__data`（bundle 字节） | `launcher/compress.py:10-83`、`launcher/patch.py:150-229` |
| 文本补丁 | `.json` | `{"patchs": {"<相对 lang 根路径>": [RFC6902 操作…]}}` | `launcher/changes.py:24-52` |
| 文本美化规则集 | `.json` | `lcta-bus`（`format/version/name/rules[]/files[]/exclude_dirs[]`）、调爪（`rules[]` 每项含 `action`）、FL（键为 `*.json`、值内含 `[{id,changes}]`）、LCJE（`mods[]` 或路径映射）、v2 美化（`version:2` + `name` + `rules`） | `launcher/modfancy.py:23-78`、`webutils/fancy/bus.py:185-267,354-408` |
| 静态数据 | `.staticmod` | zip：`manifest.json`（`format: staticmod/v1`、`name/version`、`patches[]`（`dataClass/file/opType∈{jsonpatch,pathset}/source/container?`）、`fullFiles[]`）+ `patches/<dc>.json` + `full/<dc>/<file>.json` | `launcher/staticmod.py:23-35,601-680` |

**发现机制**：加载器一律 `rglob` 扫描模组根下任意层（`modcache.enabled_mod_files`，`launcher/modcache.py:101-108`；
`_disable` 段即禁用），因此「种类/格式两层子目录」本身不影响被发现 —— 但**加载器不认识本编辑器的目录约定**，
本计划的产物是「交付给用户/加载器使用」，不是「加载器直接读的模组根」。这一点必须在导出报告里对用户讲清。

### 0.2 编辑器侧现状（改造对象）

- 侧边栏导出板块：`MainWindow.xaml:121-134`（导出思路 / 一键导出 / 向导 / 导出全部 / 导出 lang 补丁 / 直接应用 lang）。
- 资源页调试板块：`AssetsWorkbenchPage.xaml:213-220` + `:147-149` 事件转发 + `MainWindow.xaml.cs:865-916` 实现。
- bank 页导出按钮：`BankWorkbenchPage.xaml.cs:295-301,1196-1238`。
- lang 编辑集在**页面对象**里（`TextWorkbenchPage._service`），所以现在导出必须由页面转调（`ExportPatchInteractive`）。
- lang 导出通道：`LangTextWorkbenchService.ExportPatch` → `LangTextPatchService.Write`（单一「多文件打包成一份 .json」格式）。
- 现有一键导出：`UnityCacheExportService.ExportCarra2Async`（Unity 对象 → `.carra2`）。
- 现有调试：`ProjectBuildService.BuildOverlayAsync` + `UnityBundleBuildService` + `UnitySerializedFileBuildService` +
  `DebugApplyService`（文件级「备份 → 覆盖 → 关闭时恢复」，本计划**复用它**）+ `GameLaunchService`。

---

## 1. 目录与产物约定（需求 3 的落地口径）

用户在「导出模组」里选定一个目录 `D` 后，产物为：

```
D/
  <项目名>_fmod/                    ← 种类：音频（FMOD）
    bank/<目标bank文件名>.bank      ← 格式槽 1：完整包（每个被改的 bank 一个文件）
    rebank/<bank基名>.rebank        ← 格式槽 2：差分（每个被改的 bank 一个文件）
  <项目名>_data/                    ← 种类：资源 / 数据
    carra/<项目名>.carra            ← 格式槽 1：Unity 对象级差分包
    lunartique/<项目名>.zip         ← 格式槽 2：Lunartique 安装/卸载配对包
  <项目名>_text/                    ← 种类：语言文本
    bus/<相对lang根路径>.json       ← 每条被改文本表一份 lcta-bus 规则集
    patch/<相对lang根路径>.json     ← 每条被改文本表一份 RFC6902 patchs 文档
    pathset/<相对lang根路径>.json   ← 每条被改文本表一份 pathset 覆盖文档
  <项目名>_static/                  ← 种类：静态数据
    staticmod/<项目名>.staticmod
```

规则：

1. **空槽不建目录**：某格式没有对应修改时，该槽目录不创建（不产生空文件夹，也不产生空包）。
2. **种类只有产物时才存在**：例如只改了 bank，则只出现 `<项目名>_fmod/`。
3. **结构化种类内的多个格式**：`_data` 下 `carra/` 与 `lunartique/` 是同一份修改的两种表达，
   只有真的有可表达的修改时才写（例：`_data/lunartique/` 只在「用户显式选择」时写，见 §3.3）。
4. 目录名 `<项目名>_fmod` / `_data` / `_text` / `_static` 中的项目名走 `SanitizeFileName`（非法字符替换为 `_`）。
5. 导出结束弹**一份总报告**（复用/改写 `ExportReportWindow`）：逐槽位列出目录、产物数、跳过原因、加载器兼容提示。

---

## 2. 新增/改造的代码结构

### 2.1 新增 Domain：槽位契约（纯数据，无 IO）

`src/LimbusModEditor.Domain/Formats/ExportLayout.cs`

```csharp
public enum ExportGroup { Fmod, Data, Text, Static }      // 种类
public enum ExportSlot { Bank, Rebank, Carra, Lunartique, LangBus, LangPatch, LangPathset, StaticMod }
public sealed record ExportSlotDescriptor(
    ExportSlot Slot, ExportGroup Group, string FolderName,        // "bank" / "rebank" / "carra" …
    ModFormatKind? HandlerFormat, string DisplayName, string Extension,
    bool DebugOverwritePatch);                                    // 调试时是否走「覆盖 → 关闭恢复」
public static class ExportLayout
{
    public const string GroupSuffixFmod = "_fmod";   // …_data/_text/_static 同款常量
    public static string GroupFolder(string group, string modName);
    public static IReadOnlyList<ExportSlotDescriptor> All { get; }
}
```

`DebugOverwritePatch` 只对 `Bank / Carra / Rebank(展开成整包后)` 为 true，其余（lang / staticmod）为 false —— 直接编码用户答复的调试语义。

### 2.2 新增 Application：`ModExportPlan`（先分析，后导出）

`src/LimbusModEditor.Application/Build/ModExportPlanService.cs`

```csharp
public sealed record ModExportChangeSet(          // 当前项目「改了什么」的精确统计
    IReadOnlyList<BankEdit> Banks,                // 每个被改 bank：目标 bank 文件名 + 原始 bank 路径 + 改过的 fsb 序号
    IReadOnlyList<UnityObjectEdit> UnityObjects,  // 已在 UnityCacheExportService 口径内（cacheOuter/cacheInner/pathId）
    IReadOnlyList<LangTextFileEdit> LangFiles,    // 每条被改文本表：相对 lang 根路径 + 修改后 JSON 文本
    int StaticTableEdits);                        // 静态数据表的编辑数（若已有编辑通道）

public sealed record ModExportPlanItem(ExportSlotDescriptor Slot, string OutputPath, string SourceDescription, bool Planned, string? SkipReason);
public sealed record ModExportPlan(string RootDirectory, IReadOnlyList<ModExportPlanItem> Items)
{
    public int PlannedCount => Items.Count(x => x.Planned);
    public string FirstFolder => …;               // 报告的「已写入」标题
}
```

`PlanAsync(project, targetDirectory, langEditSession)` 的规则：

- `Banks` 来自 `project.Assets`：`Type == Audio` + `HasEdits` + 元数据 `bankSource`（bank 页写入的是 `bank.FileName`，
  见 `BankWorkbenchPage.xaml.cs:1183`）。同一 bank 的多个样本合并成一条 `BankEdit`；原始 bank 路径取 `asset.SourcePath`
  （bank 页实体化到 `<项目>/sources/banks/<文件名>`，`BankWorkbenchPage.xaml.cs:1147-1165`）。
- `UnityObjects` 复用 `UnityCacheExportService.IsEditedCacheAsset` 的口径（把该判定提为 public 静态方法，避免两份漂移）。
- `LangFiles` 来自共享的 `LangEditSession`（§2.4）。
- `StaticTableEdits`：`StaticWorkbenchPage` 的编辑集（若本轮回读发现静态表编辑尚未落成可导的数据，
  `_static/staticmod` 槽位**如实报告「暂无可导出修改」**，不假装成功）。

### 2.3 新增 Application：统一导出执行器 `ModPackExportService`

`src/LimbusModEditor.Application/Build/ModPackExportService.cs`

```csharp
public sealed record ModPackSlotResult(ExportSlotDescriptor Slot, bool Written, string? OutputPath, int ItemCount, IReadOnlyList<string> Diagnostics, IReadOnlyList<ExportAssetStatus> Statuses);
public sealed record ModPackExportResult(string RootDirectory, IReadOnlyList<ModPackSlotResult> Slots)
{
    public int WrittenSlotCount, WrittenFileCount; public string Describe();
}

public sealed class ModPackExportService(FormatRegistry registry)
{
    public Task<ModPackExportResult> ExportAsync(ModProject project, string projectRoot, ModExportPlan plan,
        object? fmodCodec = null, IProgress<string>? progress = null, CancellationToken ct = default);
}
```

各槽位的实现（**全部复用已有写出能力，不新写格式代码**）：

| 槽位 | 实现 | 复用点 |
|---|---|---|
| `bank/` | 每个被改 bank：`BankFormatHandler.ImportAsync(原版 bank)` → 用改后的 FSB 覆盖（WAV 走 `NativeFmodAudioCodec` 编码）→ `ExportAsync` 到 `<目标bank名>.bank` | `BankPackage` / `BankAssembler` / `NativeFmodAudioCodec` |
| `rebank/` | 同一个「改后 bank」再与「原版 bank」做 `RebankDiffService.CreateFromBank` → `RebankArchive.Write` 到 `<bank基名>.rebank` | `RebankDiffService` / `RebankArchive`；**当前 `CreateFromBank` 把 `{i}.fsb` 当 wav 名，需按 §2.3.1 修正为真实 wav 条目名** |
| `carra/` | 既有一键导出的同一条路径，只是输出到 `<项目名>.carra` | `UnityCacheExportService.ExportCarra2Async`（改名 `ExportCarraAsync`，扩展名参数化） |
| `lunartique/` | 用 `LunartiquePackage{Root = <项目名>}`：每个被改 Unity 对象写一条 `<outer>/<inner>/<pathId>.<type>` 资源（`Uninstallation` 留空、`Installation` 放改后字节） | `LunartiqueArchive.Write` |
| `bus/`、`patch/`、`pathset/` | 见 §4（lang 三种格式） | `TextDiffService` + 新写 `LangExportFormatter` |
| `staticmod/` | **复用已有导出器**：`StaticEditSession`（从静态页抽离的编辑集）→ `StaticModService.CreateJsonPatchPackage` → `Write`，输出 `<项目名>.staticmod`（详见 §10.2） | `StaticMods/StaticModService.cs`（`Read/Write/CreateJsonPatchPackage` 全齐） |

#### 2.3.1 rebank 槽位的两个真问题（必须按真实语义修）

1. **条目名必须是真实 wav 名**：LCTA `iter_rebank_wavs` 产出 `(idx, fname)`，`bankmod._patch_into` 用 `collect_wavs(wav_dir)`
   的 `(idx, fname)` 键匹配（`launcher/bankmod.py:186-196`）。编辑器侧 `BankPackage` 里只有 FSB5 整块，没有 wav 条目表；
   现有 `RebankDiffService.CreateFromBank` 写的是 `{i}.fsb`，**加载器会一条都匹配不上**（`T` 里是 `*.wav` 名）。
   → 修法：用 `Fsb5Models` 解析改后 FSB 的样本名与下标（`Fsb5Parser` 已有），条目名写 `<样本名>.wav`，
   下标写 FSB 序号。若无法解析样本名 → 该 bank 的 rebank 槽位**明确报错不写**（不产生加载器会静默跳过的包）。
2. **`base_bank` 必须是目标 bank 名**：用 `Path.GetFileName(原版bank路径)`（含 `.bank` 也可以，加载器两吃）。

### 2.4 新增 Application：共享 lang 编辑会话 `LangEditSession`

需求 3 让导出不再从页面取状态。把 `LangTextWorkbenchService` 的编辑集抽成**宿主持有的会话对象**：

`src/LimbusModEditor.Application/Texts/LangEditSession.cs`

```csharp
public sealed class LangEditSession
{
    public string? LangRoot { get; private set; }
    public void AttachLangRoot(string langRoot);
    public IReadOnlyList<string> EditedFiles { get; }
    public bool IsModified(string relativePath);
    public string? TryGetVanillaText(string relativePath);
    public string? TryGetModifiedText(string relativePath);
    public void SetModified(string relativePath, string jsonText);
    public bool Revert(string relativePath);
    public void ClearEdits();
    public int Revision { get; }          // 导出/调试据此判断「本次是否与上次一致」
}
```

- `MainWindow` 持有一个实例（`IWorkbenchHost` 增加 `LangEditSession LangEdits { get; }`），`TextWorkbenchPage` 改为**注入**该会话，
  自己的 `_service` 只保留「枚举/搜索/基线读取 + 差分」职责。
- `LangTextWorkbenchService` 的编辑集成员（`BeginEdit/SetModified/Revert/IsModified/ExportPatch`）改为薄封装或直接删除，
  由会话承担；**不让两份状态同时存在**（这正是 plan-14 14.5 的既有教训）。
- 编辑集仍然只存内存；关窗不落盘（本轮不变）。

### 2.5 新增 Application：调试应用 `ModDebugApplyService`（模拟加载器语义）

`src/LimbusModEditor.Application/Debugging/ModDebugApplyService.cs`

对导出计划里的每个**已写入槽位**按种类应用；全部动作登记进现有 `DebugApplySession`（备份/校验/关闭恢复），
再加一条「就地重写」记录类型（.rebank / carra `__data` 不是「复制文件」，需要按原始字节比对恢复）：

```csharp
public enum ModApplyKind { FileOverwrite, InPlaceRewrite, LangPatch, StaticCatalog }
public sealed record ModApplyStep(ModApplyKind Kind, string SourcePath, string TargetPath, string BackupPath, string? Note);
public sealed record ModApplyReport(IReadOnlyList<ModApplyStep> Steps, IReadOnlyList<string> Skipped, IReadOnlyList<string> Diagnostics);

public sealed class ModDebugApplyService
{
    public Task<ModApplyReport> ApplyAsync(ModProject project, ModPackExportResult export,
        string gameDirectory, string unityCacheDirectory, IProgress<string>? progress = null, CancellationToken ct = default);
    public Task RestoreAsync(ModProject project, CancellationToken ct = default);   // 关闭时
}
```

| 槽位 | 调试应用语义（用户答复） |
|---|---|
| `bank/<名>.bank` | **FileOverwrite**：备份 `<游戏>/…/FMODBuilds/Desktop/<名>.bank` → 覆盖 |
| `rebank/*.rebank` | **展开成整包后 FileOverwrite**（等价语义，无需 FMOD 编解码）：`BankPackage(原版) + rebank 里的 wav 替换 → BankAssembler.Rebuild → 覆盖目标 bank`，WAV 是原始 PCM 时用 `NativeFmodAudioCodec` 编码为 FSB；无法编码则**明确跳过并报告** |
| `carra/<名>.carra` | **InPlaceRewrite**：对每个条目解析 `<outer>/<inner>/<pathId>.<type>` → 备份 `<缓存>/<outer>/<inner>/__data` → 用 `AssetsToolsBackend`/`UnitySerializedFileBuildService` **就地替换该 pathId 的对象原始字节**（整包「导出为 `__data` 后覆盖」） |
| `lunartique/<名>.zip` | 同 carra 的就地重写：把 `Installation` 侧条目替换进对应 `<outer>/<inner>/__data`（`account` 段 = 缓存外层键） |
| `bus|patch|pathset/*.json` | **标准 patch**：文件原样写入 `<游戏>/LimbusCompany_Data/lang/`（`_disable` 段禁用可选），写前逐文件建 `.bak`，`RestoreAsync` 还原；对 `patch` 槽位可直接 `LangTextPatchService.ApplyToDirectory` 就地应用后建 `.bak`（两条路择一，实施时以「与 LCTA 完全同一条应用链」为准：**优先写文件**，因为加载器就是那样工作的） |
| `staticmod/*.staticmod` | **标准 patch（带 catalog 双写）**：按 `launcher/staticmod.py` 的事实（catalog 动态定位 `static_s1_0_assets_all_<32hex>` → `Hash128` → `crc@+0x44`/`size@+0x48`；改 TextAsset → `_save_bundle(lz4)` → 解压块 CRC32 → 双写 `__data`/`__info` 与 catalog）在 C# 侧实现最小可用版本，关闭时从本地备份还原并回写 catalog。**若按 §10.2.3 排到 S7b，则本轮调试报告里必须明确写「静态模组本次调试未应用（catalog 写入待 S7b）」，不静默跳过** |

**风险与护栏**：
- 这是**真的写游戏目录**（与现调试行为同类，但覆盖面更大）。保留既有护栏：备份目录在 `<项目>/backups/<时间戳>/`、
  应用前逐文件 sha256、关闭恢复时若目标被外部改动 → 记 `RestoreConflicts` 并**不覆盖**（`DebugApplyService` 现成逻辑）。
- 游戏在运行时不允许应用（检测 `LimbusCompany.exe`）→ 直接报错退出；应用后立刻 `GameLaunchService.TryLaunch`。
- 任何一步失败：**先整体回滚本次已应用的步骤**再报错（复用 `DebugApplyService.RestoreAsync` 的逆序还原）。

### 2.6 界面改造（需求 3 的 UI 部分）

| 位置 | 动作 |
|---|---|
| `MainWindow.xaml:121-134` | **删除整个「② 产出模组」板块**（导出思路 / 一键导出 / 导出模组（向导）/ 导出全部（多格式）/ 导出 lang 补丁 / 直接应用到 lang 目录），替换为两个按钮：`导出模组…`、`使用当前修改启动游戏进行调试` |
| `AssetsWorkbenchPage.xaml:213-220` | 删除「调试」Expander（调试状态文本 + 两个按钮）与其事件 `BuildOverlayRequested/DebugApplyRequested`、`SetDebugState` |
| `AssetsWorkbenchPage.xaml.cs` | 删除 `BuildOverlay_Click` / `DebugApply_Click` / 事件定义 |
| `BankWorkbenchPage.xaml.cs:295-301` | 删除 `_exportBankButton` / `_exportRebankButton` 与其 ToolTip、`buttonRow` 里两项、`ExportAsync` 方法、`_exporter` 字段；`RefreshActionButtons` 里的启用状态同步一并删除（`_exportWavButton` 保留——它是「导出样本 WAV」不是模组导出） |
| `StaticWorkbenchPage.xaml.cs:151-155, 470-477, 495-558` | 删除「导出 .staticmod…」按钮与 `ExportStaticModAsync`（含临时 official/modified 文件那套）；`RefreshActionButtons` 不再维护该按钮；编辑集改走宿主持有的 `StaticEditSession`（页面只保留「保存修改到编辑集 / 还原此表」） |
| `TextWorkbenchPage` | 删除编辑列里指向侧边栏的说明文案；删除 `ExportPatchInteractive` / `ApplyToGameInteractive` / `ExportPatch_Click` / `ApplyToGame_Click`（导出与调试统一走侧边栏两个按钮） |
| `MainWindow.xaml.cs` | 删除 `ExportLangPatch_Click` / `ApplyLangToGame_Click` / `RunLangWorkbenchChannel` / `Export_Click` / `ExportAll_Click` / `ExportIdeas_Click` / `OneClickExport_Click` / `ShouldOfferExportIdeas` / `RunExportIdea` / `BuildOverlayAsync` / `DebugApplyAsync`；新增 `ExportMod_Click`（日志 → 计划 → 导出 → 报告）与 `DebugMod_Click`（导出到 `<项目>/builds/debug-pack` → 应用 → 启动） |
| 删除的窗口 | `ExportWizardWindow`、`MultiExportReportWindow`、`ExportAdvisorWindow`（导出思路）；`ExportProgressWindow`、`ExportReportWindow` 保留/改写为槽位报告 |
| 保留 | `ExportAdvisor` 的**分析能力**下沉到 `ModExportPlanService`（不再有「思路」交互），`ExportMatrix` 仅存于向导 → 随向导删除 |

**删除后不得留下死代码**：build 必须零警告新增；`grep` 确认 `ExportWizard|ExportAll\(|ExportIdeas|OneClickExport` 无残留引用（`ExportAllAsync` 是否保留见 §2.7）。

### 2.7 `ModExportService` / `ExportAllAsync` 的去留

- 现有 `ModExportService.ExportWithEditsAsync`（源包 → 目标格式的向导式导出）**本轮删除**：它的唯一入口是向导，
  且新链路（计划 → 槽位 → 复用格式处理器）不再需要「源包驱动」的导出。
- `ExportAllAsync`（逐 `project.Sources` 导出）同样删除。
- 但**格式处理器层一个都不动**（`IModFormatHandler` 及其 5 个实现照旧）——槽位执行器直接调用它们。
- `ProjectBuildService.BuildOverlayAsync` 保留（调试需要「实体化 + 覆盖层」这条老路里可复用的部分？
  → 实际上新调试不再用覆盖层；**若 §2.5 落地后确认无调用方，则一并删除**，避免留死代码）。

---

## 3. 需求 1 & 4 的落地：lang 导出格式（参考 LCTA，多格式，放弃现有格式）

### 3.1 现有的格式（要放弃）

现在是「把全部被改文本表打成一个 `X-lang.json`，内含 `{"patchs": {路径: 操作数组}}`」→ **单文件、混合多表**。
需求 1 明确「不再需要支持现有的导出格式」：该「导出补丁…」入口与 `LangTextWorkbenchService.ExportPatch` 的
`FileName = <项目名>-lang.json` 口径全部删除。

### 3.2 新的格式（都在 §1 的 `<项目名>_text/` 下按格式分文件夹）

对**每一条被改文本表**（相对 lang 根路径 `p`，如 `LLc-CN-LCTA/Skills_personality-01.json`）分别产出：

1. `bus/<p>.json` —— `webutils/fancy/bus.py` 认得的 **lcta-bus 规则集**（`is_bus_ruleset`，bus.py:185-190）：
   ```json
   {
     "format": "lcta-bus", "version": 1,
     "name": "<项目名> · Skills_personality-01.json",
     "files": ["Skills_personality-01.json"],
     "rules": [ { "name": "patch[0] Rules[3].name", "files": ["Skills_personality-01.json"],
                  "path": "Rules[3].name", "replacements": [ { "set": "改后的值" } ] } ]
   }
   ```
   - `files` 用**文件名或 `**/文件名`**（bus 的 file matcher 是 glob，`bus.py:307-322`；`modfancy` 传入的相对路径是
     「语言包目录内的相对路径」，`launcher/modfancy.py:109`）——实施时以 **`**/<文件名>`** 写，兼容子目录同名文件。
   - `path` 用 bus 路径语法（`Key` / `[i]` / `[*]` / `[?field=value]`，`bus.py:270-304`），由**改动的地址**生成；
     地址来自 `TextDiffService` 的差分结果（`op.path` 的 jsonpointer → bus 路径），数组下标唯一时写 `[i]`，
     无法唯一化（差分给出 `-` 追加）时写 `[*]` + `[?field=value]`，再不行则该表**跳过 bus 格式并在报告里说明原因**。
   - 每个 op 一条 rule（可追溯），replacements 固定 `[{ "set": <改后值> }]`（`bus.py:620-622` 直赋，不做字符串替换语义）。
2. `patch/<p>.json` —— LCTA `changes.py` 直接消费的 **单文件 RFC6902 文档**：
   `{"patchs": {"<相对 lang 根路径>": [ 操作… ]}}`（一个文件只含一个键）。
   直接复用 `LangTextPatchService.Write`（现有实现已与 LCTA 对齐，只是由「合一份」改为「一表一份」）。
3. `pathset/<p>.json` —— **pathset 覆盖文档**（`launcher/staticmod.py` 的 `pathset` 语义，`staticmod.py:192-236`）：
   ```json
   { "format": "lcta-pathset", "version": 1, "files": ["Skills_personality-01.json"],
     "pathset": { "Rules[3].name": "改后的值" } }
   ```
   路径语法与 `staticmod._pathset_to_jsonpatch` 一致（`.` 分段 + `[下标]`）。
   ⚠ `launcher/changes.py` 目前**只认 `patchs`**，因此该格式不是「加载器直接可用」，报告里必须如实标注
   「LCTA 现版本不会自动应用 pathset 文本表（仅作为可读的改动清单/后续格式）」。
4. `v2/<p>.json`（LCTA `modfancy` 认得的 v2 文本美化规则集，`launcher/modfancy.py:71-73`）——
   **建议不提供**：该格式的引擎语义是「按规则批量改写字符串」，无法无损承载我们的「精确逐字段改动集合」，
   详见 §10.1（含四处具体不匹配与「导出成功但改动静默丢失」的后果）。

**导出键口径的统一（与 §5 强耦合）**：库与界面用「相对活动语言目录」的新口径（`Skills_personality-01.json`），
而 `patch` 槽位的 `patchs` 键、`bus/pathset/v2` 的 `files` 匹配串一律按**加载器语义**写：

- `patchs` 键 = **补回语言目录前缀**的完整相对 lang 根路径（`LLc-CN-LCTA/Skills_personality-01.json`）；
- `bus/pathset/v2` 的 `files` = `**/<文件名>`（加载器传入的相对路径是「语言包目录内部」，`launcher/modfancy.py:107-109`）。

同一个 `<p>` 在两个槽位里因此表现为不同前缀，这是**刻意**的：前者是加载器的定位口径，后者是加载器的匹配口径。

新增 `src/LimbusModEditor.Application/Texts/LangExportFormatter.cs`（纯函数，可单测）：

```csharp
public enum LangExportFormat { Bus, Patch, Pathset }
public sealed record LangFormatResult(LangExportFormat Format, string JsonText, IReadOnlyList<string> Notes);
public static class LangExportFormatter
{
    public static IReadOnlyList<LangFormatResult> Format(string modName, string relativePath,
        string vanillaJson, string modifiedJson);          // 内部走 TextDiffService
    public static string BusPathFromJsonPointer(string pointer, JsonNode vanilla);  // 可单测的地址换算
}
```

### 3.3 版本相关的注意

- `bus` / `pathset` / `patch` 三种格式的 `files` 字段一律使用**文件名**（加载器按语言包内相对路径匹配），
  但报告里同时打印**完整相对 lang 根路径**，方便用户核对。
- 「一表一份」= 文件名冲突风险：不同语言目录同名文件会落在同一 `<p>.json` 上 → 键就是相对 lang 根的完整路径，
  因此同一语言目录内不会冲突；跨语言目录（如 `LLc-CN-LCTA` 与 `LLC_zh-CN` 同时被改）会落在不同子路径，天然隔离。

---

## 4. 需求 2：lang 页面（树结构）去掉根节点

现状（plan-14 之后）：`_fileTree.ItemsSource = _treeRoot.Children`，顶层是活动语言目录的直接子项
（文件夹 `StoryData`、`PersonalityVoiceDlg`… + 根级 `AbDlg_Faust.json` 等文件）。
**注意：这一节只做 UI 收口（小改）；用户澄清「根文件夹」的本体是数据库条目口径，见 §5。**

改动（集中在三处）：

1. `TextWorkbenchPage.RebuildTree`：显式表达「无根节点」——枚举 `_treeRoot.Children`，
   **文件夹在前、根级文件在后**（`LangTextTreeBuilder.Sort` 已经是「文件夹优先 + 字典序」，直接复用），
   并在代码注释里钉死「不再有根节点，也不再有语言目录那一层包裹」。
2. `TextWorkbenchPage.CreateTreeItem` 的 ToolTip：**删除** `node.RelativePath.Length == 0 ? "lang 根"` 这一支
   （不会再有 `RelativePath == ""` 的可见节点）；文件夹/文件分别给出「显示路径 + 补丁键」两段。
3. `LangTextTreeBuilder`：`CreateRoot` 只作内部容器，文档注释明确「UI 不渲染根节点；顶层 = 根的直接子项」。

---

## 5. 需求 2 的本体：`text-index.db` 的条目口径去掉根文件夹

### 5.1 实测现状（本机 `artifacts/publish-win-x64/cache/text-index.db`，本轮用 python sqlite3 只读核对）

| 项 | 实测值 |
|---|---|
| `index_meta.source_key` | `c:\program files (x86)\steam\steamapps\common\limbus company\limbuscompany_data\lang`（= lang 根，不是语言目录） |
| `index_meta.signature` | `v2\|0:639246305446856524\|0f34fe51…`（内容口径版本 + 目录签名 + config.json 哈希） |
| `files` 行数 | 2048 |
| `files.rel_path` | **全部**以 `LLc-CN-LCTA/` 开头（`SELECT DISTINCT` 第一段只有 `LLc-CN-LCTA` 一个值），形如<br>`LLc-CN-LCTA/BattleAnnouncerDlg/Announcer_Aengdu_26.json` |
| `hits` 行数 / 值 | 362246；`hits.rel_path` 同样是 `LLc-CN-LCTA/…`，`hits.key_path` 是 JSON Pointer 口径（`dataList/0/id`） |

→ **`files.rel_path` 与 `hits.rel_path` 现在存的是「相对 lang 根」的路径，比用户要的口径多一层活动语言目录
（本机是 `LLc-CN-LCTA`）。这一层就是用户说的「根文件夹」。**

### 5.2 目标口径

```
现存：LLc-CN-LCTA/BattleAnnouncerDlg/Announcer_Aengdu_26.json
目标：BattleAnnouncerDlg/Announcer_Aengdu_26.json        ← 「lang/a_folder_name」里的 a_folder_name 直接打头
      现存：LLc-CN-LCTA/AbDlg_Faust.json  →  目标：AbDlg_Faust.json（根级文件不带任何前缀）
```

- 库里的条目 = **相对活动语言目录**的路径，`source_key` 仍是 lang 根（它决定去哪读文件，不参与条目口径）。
- **不得**再出现任何以语言目录名开头的条目（这是本轮的可验证断言，见 §7 审查门）。
- `hits.key_path` 口径不变（JSON Pointer）；`hits.snip` 里的文件名命中片段要跟着改成新口径（现在它存的就是 `rel_path`）。

### 5.3 必须同时改的读写路径（少改一处就会出现「点文件预览空白」这类 plan-13 踩过的坑）

| 位置 | 改动 |
|---|---|
| `LangTextWorkbenchService.EnumerateFiles` / `BuildFileInfo` | `RelativePath` 由「相对 lang 根」改为「相对 `ResolveLanguageDirectory(langRoot)`」；`FullPath` 仍是绝对路径（`Path.Combine(语言目录, 相对路径)`），**文件访问一律走 FullPath** |
| `LangTextWorkbenchService.BeginEdit / SetModified / Revert` | 编辑集键改为新口径；基线读取用 FullPath（不允许再 `Path.Combine(langRoot, relPath)`） |
| `LangTextWorkbenchService.Search` / `ReadFileHits` / `KeyHitCandidate` / `FileNameHitCandidate` | 命中行与候选一律走新口径（库里 `hits` 与内存枚举必须逐字段一致，否则「有缓存 vs 无缓存」的比对测试会红） |
| `LangTextWorkbenchService.ExportPatch`（本轮被 §3 取代） | 删除前先把「补前缀」逻辑搬到 `LangExportFormatter`/槽位执行器 |
| `TextIndexStore.ReadFiles` | 拼完整路径改成 `语言目录 + rel_path`；SQL 里的 `ORDER BY CASE WHEN rel_path='config.json' …` 死代码删除（config.json 自 plan-14 起不收录） |
| `TextIndexStore.PersistFiles` | 无结构改动（列不变），但写入的 `rel_path` 必须是新口径；`hits` 的 `snip`（File 命中）同口径 |
| `TextIndexStore.IndexFormatVersion` | `v2` → **`v3`**：签名里带上版本，旧库（2048 行旧口径）自动判「不新鲜」→ 整库重建（约 2 秒级；库仍是纯加速旁路，删掉也不影响功能） |
| `LangTextDisplay` | 职责收窄为**单向**：`ToPatchKey(前缀, 新口径) = 前缀 + 路径`（导出/加载器兼容用）；`ToDisplayPath` 在文件树与列表上变为恒等（保留方法以兼容既有测试，或按「不留死代码」原则删除并同步测试） |
| 导出与调试（§3 / §2.5） | 产出 lang 补丁时**必须补回前缀**：`patchs` 的键 = `<语言目录>/<新口径>`（如 `LLc-CN-LCTA/Skills_personality-01.json`）——加载器按 `<游戏>/LimbusCompany_Data/lang/<键>` 应用（`launcher/changes.py:37-38`）。**这是新旧口径唯一的接缝，必须有往返测试钉死** |
| 启动扫描 `StartupScanService` 的 lang 步骤（`StartupScanService.cs:752-791`） | 无结构改动（它只数文件），但文案里的「相对路径」口径说明要同步 |

### 5.4 明确不做的「简化」

- 不改 `source_key`（仍是 lang 根）：索引的是「这个 lang 目录下的活动语言」，换语言要整库重建这条既有语义不变。
- 不把 `hits.key_path` 从 JSON Pointer 改成别的口径（它是内部搜索键，与需求无关；换成 bus 口径是 §3 导出层的事）。
- 不引入「多语言目录共存」的新语义（`ReadFiles` 仍只读活动语言目录，换语言 = 换 `config.json` = 整库失效重建）。

---

## 6. 执行顺序（每步单独 build+test 绿再进下一步）

| 步 | 内容 | 依赖 | 预估 |
|---|---|---|---|
| **S1** | lang 页面树去根节点（§4）+ 文案；更新 `LangTextTreeBuilderTests` | 无 | 小 |
| **S2** | **DB 条目口径去根文件夹（§5）**：`EnumerateFiles` / 编辑集 / 搜索 / `hits` 全链路改口径 + `IndexFormatVersion → v3`；改 `LangTextWorkbenchServiceTests` / `TextIndexStoreTests` | 无（与 S1 都改 `TextWorkbenchPage.xaml.cs` 周边 → 与 S1 串行） | 中 |
| **S3** | 槽位契约 + 计划服务 + `LangEditSession` 抽离（含 `IWorkbenchHost` 扩展、TextWorkbenchPage 改注入） | S2 | 中 |
| **S4** | `ModPackExportService`：`_fmod`（bank + rebank，含 §2.3.1 两处真问题修正）与 `_data/carra` 三个槽位 | S3 | 中大 |
| **S5** | lang 三种导出格式（`LangExportFormatter` + `_text` 三槽位，导出键补回语言目录前缀）+ 报告 | S3 | 中 |
| **S6** | 侧边栏 / 资源页 / bank 页 / lang 页的删除与两按钮接线；删除向导等窗口与死代码 | S4、S5 | 中 |
| **S7** | 调试应用 `ModDebugApplyService`（bank/rebank/carra/lunartique 覆盖；lang 标准 patch）+ 备份与关闭恢复 | S4 | **大** |
| **S7b** | 静态模组的**调试应用**（catalog 动态定位 + TextAsset 重打包 + 解压块 CRC32 + 双写 `__data`/`__info` 与 catalog + 关闭还原），详见 §10.2.3 | S7 | 中大 |
| **S8** | `_data/lunartique` 槽位与 `_static/staticmod` 槽位（后者复用 `StaticModService`；v2 槽位按 §10.1 **不做**） | S4 | 中 |
| **S9** | 文档（`docs/ROADMAP.md` 新节 / `docs/USAGE.md` 界面与导出布局 / `docs/plans/README.md` 索引与基线测试数） | 全部 | 小 |

并行度建议：S1 → S2 串行（同一批文件）；S4 与 S7 的静态部分（catalog 定位、`__data` 就地重写）
可由不同执行者并行（不同文件），但 S7 依赖 S4 的产物接口 → **先定 S4 的 `ModPackExportResult` 形态**。

---

## 7. 测试计划

新增/改造测试（都落在 `tests/LimbusModEditor.Domain.Tests`）：

1. `ModExportPlanServiceTests`：混合项目 → 槽位计划正确（只改 bank → 只有 `_fmod` 两槽；只改文本 → 只有 `_text` 三槽；
   空项目 → 0 槽 + 明确原因）。
2. `ModPackExportServiceTests`：临时目录跑一遍，断言 §1 的目录形状与文件数；断言「空槽不建目录」；
   产物可用既有 reader 回读（`RebankArchive.Read`、`CarraArchive.Read`、`BankParser.TryParse`）——**至少一个真实样本门控测试**。
3. `RebankEntryNamingTests`（真实 bank，`Real*` 门控）：导出的 `.rebank` 条目名必须是真实 wav 名（正则 `\d+/.*\.wav`），
   `base_bank` = 目标 bank 文件名；用 LCTA 的 `iter_rebank_wavs` 语义逐条核对（可在测试里实现等价解析，不引入 Python）。
4. `LangExportFormatterTests`：差分 → bus / patch / pathset 三份 JSON 的结构断言 +
   **每一处改动都能被对应语义重新应用回 vanilla 得到 modified**（三种格式各一条往返断言；这是「不猜」的关键门）。
5. `LangEditSessionTests`：会话生命周期（Attach/SetModified/Revert/Clear/Revision）与「页面重载不丢编辑集」。
6. `LangTextTreeBuilderTests` 改造：顶层项 = 文件夹在前 + 根级文件平铺，且**不存在 RelativePath 为空的可见节点**。
7. `LangEntryCaliberTests`（§5 的回归门，纯临时目录）：
   - `EnumerateFiles` 的 `RelativePath` 第一段**不是**语言目录名；`Path.Combine(langRoot, 语言目录, RelativePath)` 指向真实文件；
   - `PersistFiles` → `ReadFiles` 往返后逐字段相同（口径一致，不出现「有缓存/无缓存不一致」）；
   - `hits.rel_path` 与 `files.rel_path` 同口径；**补丁键往返**：`ToPatchKey(前缀, RelativePath)` 重新得到
     `LLc-CN-LCTA/<文件>` 这种加载器可用键（这条钉死「DB 去根、导出补键」这个接缝）。
8. `ModDebugApplyServiceTests`（全部临时目录，不碰真游戏）：bank 覆盖、rebank 展开后覆盖、
   carra 就地重写 `__data`、lang 标准 patch + `.bak`、**关闭恢复逐字节回到原状**；
   失败注入（半途抛错）→ 断言整体回滚。
9. `RealDebugApplySmokeTests`（真实游戏目录门控，默认跳过）：对 1 个 bank + 1 个语言表跑真实应用 → 恢复 → 逐字节校验。
10. **真实库核对脚本**（不进测试套件，实施时执行并抄进验证记录）：用 python sqlite3 只读打开
    `artifacts/publish-win-x64/cache/text-index.db`，断言
    ① `SELECT count(*) FROM files` 行数与改口径前一致（本机基线 2048）；
    ② `SELECT DISTINCT` 第一段：不再出现 `LLc-CN-LCTA`，而是 `BattleAnnouncerDlg`、`StoryData`、`AbDlg_Faust.json`…；
    ③ 抽样 5 行 `Path.Combine(langRoot, 语言目录, rel_path)` 均 `os.path.isfile` 为真。

---

## 8. 审查门（全部通过才算完成）

- [ ] `dotnet build LimbusModEditor.slnx --no-restore --nologo` 零错误、零新增警告。
- [ ] `dotnet test LimbusModEditor.slnx --no-build --nologo` 全绿（当前基线 573；本计划新增/改造后更新 README 基线数）。
- [ ] `dotnet publish src/LimbusModEditor.App/… -c Release -r win-x64 --self-contained false -o artifacts/publish-win-x64 --no-restore` 成功。
- [ ] 侧边栏「② 产出模组」板块已不存在；只剩**两个**按钮（导出模组 / 使用当前修改启动游戏进行调试）。
- [ ] 资源工作台右栏不再有「调试」板块；bank 页预览区不再有「导出整包 .bank…」「导出 .rebank…」。
- [ ] lang 树顶层无根节点（截图核对：顶层为 `StoryData` 等文件夹 + `AbDlg_*.json` 等根级文件）。
- [ ] **`text-index.db` 条目无根文件夹（§5）**：用 python sqlite3 只读核对新建的库 —— `files.rel_path` / `hits.rel_path`
      第一段里**不再出现 `LLc-CN-LCTA`**；行数与改口径前一致（本机 2048）；抽样条目 `语言目录 + rel_path` 都能命中真实文件；
      「清空缓存 → 重新加载」后仍然如此（证明不是残留旧库）。
- [ ] **导出键接缝**：`patch/*.json` 的 `patchs` 键仍是 `LLc-CN-LCTA/<文件>`（加载器可用），
      而库/界面显示的是去前缀口径 —— 两者由一条往返测试钉死。
- [ ] 手工验收：改 1 个 bank 样本 + 1 个 Unity 对象 + 1 条文本 → 导出到空目录 → 目录形状与 §1 完全一致。
- [ ] 兼容性核对（用 LCTA 侧代码当事实源，不复制代码）：`.rebank` 能被 `bankmod._base_bank_of` 正确读出目标 bank；
      `.carra` 键能被 `launcher/modcache.extract` 的 `glob("*/*/*")` 展平；`patch/*.json` 能被 `changes.apply_patch` 应用。
- [ ] 调试验收：对真实游戏跑一次「使用当前修改启动游戏进行调试」→ 游戏启动 → 关闭编辑器/游戏后
      **逐字节**核对被改文件已还原（`backups/` 下有完整备份，冲突项有提示）。
- [ ] `grep` 确认无死代码残留：`ExportWizard|ExportIdeas|OneClickExport|ExportAll\(|ExportPatchInteractive|ApplyToGameInteractive|BuildOverlayRequested|_exportRebankButton`。
- [ ] 文档更新：`docs/ROADMAP.md`（新节）、`docs/USAGE.md`（导出布局与两个按钮的新行为）、
      `docs/plans/README.md`（索引表 + 基线测试数）、`docs/REVIEW.md`（导出/调试设计变更的理由与风险）。

---

## 9. 明确不做（本轮范围外，避免范围蔓延）

- 不做 FMOD FEV 事件表（非音频）的模组导出；bank 槽位只针对「被改样本所属的 bank」。
- 不把导出产物直接写进「加载器一眼就能读到的模组根」——产物是「种类/格式两层目录」的交付形态，
  报告里说明「把需要的格式文件夹里的文件复制/移动到模组目录即可」。
- 不改 Unity 对象写出（AssetsTools 适配层）、图像（ImageSharp）、XZ（Joveler）、FMOD C ABI 的既有边界。

---

## 10. 两个待拍板点的详细分析与建议（v2 槽位 / `_static` 槽位）

### 10.1 `_text/v2/` 槽位（LCTA 的 v2 文本美化规则集）：**建议不做**

#### 10.1.1 客观事实（`webutils/fancy/engine.py` 全读）

- `compile_rulesets` 只接受两种形状（`engine.py:216-232`）：**规则集对象**（必须有 `version == 2`，否则抛
  「缺少 version: 2」）或**裸规则对象**；`modfancy` 另外要求 `version == 2 and "name" in data and "rules" in data`
  才会走 v2（`launcher/modfancy.py:71-73`）。
- 一条规则（`compile_rule`，`engine.py:184-213`）的字段与约束：
  `files`（非空字符串数组，`fnmatch` 匹配文件名，`engine.py:50-58`）、`scope`（可选路径）、
  `targets`（非空路径数组，**必需**）、`where`（可选条件数组，`operator ∈ {equals,in,contains,regex}`）、
  `actions`（**非空数组，必需**）。
- 动作**只有四种**（`_compile_action`，`engine.py:140-181`）：`replace`（`from/to` **都必须是字符串**，mode `literal|regex`）、
  `wrap`（`prefix/suffix`）、`gradient`（`rate`）、`skill_color`（`idPath`）。
- 应用语义：**只对字符串值生效**——`_apply_actions` 第一句就是 `if not isinstance(value, str): return value`（`engine.py:326-327`）。
- 路径语法（`parse_structured_path`，`engine.py:87-111`）：`a.b[3][*]`，**只有 `*` 与数字下标，没有选择器
  `[?field=value]`**（后者是 bus 通道独有的，`bus.py:294-300`）。

#### 10.1.2 与「精确文本编辑」的四处不匹配（这就是「验不过就别提供」的具体内容）

1. **只能改字符串**：把 `dataList[3].cost` 从 `5` 改成 `6`、把某个布尔翻面、把数组元素从对象换成字符串、
   给对象新增/删除键 —— v2 的 `_apply_actions` 一律**静默返回原值**（`engine.py:326`），
   即「导出成功但改动丢失」。这类改动在文本表里很常见（数值/枚举字段、增删条目）。
2. **没有 `set` 动作**：把 `name` 从 `"旧"` 改成 `"新"` 只能用 `replace{from:"旧",to:"新",mode:literal}`，
   而 `literal` 是 `str.replace` 的**全量替换**（`engine.py:331-332`）——同一字符串里出现两次就一起被改。
   要精确表达「只改这一个字段」必须补 `where` 谓词把它绑回那一条记录；`where` 的定位能力
   **只有 `*` 通配 + 值相等条件**，当同一文件里有两段结构一模一样的内容时**无法唯一化**（这正是 diff 里
   数组下标存在的意义，而 v2 的路径语法根本不支持选择器）。
3. **`targets` 必需 + 语义是「扫全部命中位置」**：`resolver.resolve(scope_path, target)` 会把 scope 下
   **所有**匹配 target 的位置都改掉（`engine.py:364-373`）——它表达的是「批量美化规则」，
   不是「第 3 条记录的第 2 个字段改成这个值」。
4. **无法表达「删除键 / 删除数组元素 / 新增数组元素」**：没有对应动作，也没有 `add/remove` 语义。

结论：**v2 的语义是「按规则批量美化/改写文本」，而我们手上的输入是「精确的逐字段改动集合」。**
把后者编译成前者，会遇到「改不动的类型」「要不要用正则/字符串替换去伪造赋值」「数组歧义」三类不可判定情形，
而这些失败在加载器侧**不报错、直接静默不生效**——对制作者是最坏结果。
按仓库既有规则（README §共同执行规则 4：**不猜测原则**），**不提供该槽位**，
并在导出报告里写明「v2 文本美化规则集：本编辑器不生成（引擎只支持字符串替换/包裹类动作，
无法无损承载精确字段赋值）」。

> 保留升级路径：若以后要做，正确做法是**在 C# 侧先实现 v2 引擎的等价解释器**（含 `where` 求值、`*` 展开、
> 四种动作、`skill_color` 依赖 `builtin_func.skillColorHandler`），然后以「生成 → 用解释器回放 → 与目标 JSON 逐字节比对」
> 作为验收门。那是一个独立计划（可命名 plan-17），不应混进本轮的导出重构。

### 10.2 `_static/staticmod` 槽位：**做，而且是两件事里比较干净的一件**

#### 10.2.1 纠正我在计划上一版的判断

上一版我写「若静态表编辑通道尚未就绪则如实报不可导出」——**这个前提是错的**：
静态表编辑通道**已经存在**，而且**导出器已经在跑**，只是挂在静态页自己的按钮上：

| 已经有的东西 | 位置 | 现状 |
|---|---|---|
| 编辑集（内存） | `StaticWorkbenchPage.xaml.cs:55-56`（`_modified` / `_vanilla`） | 与 lang 页同一模式：**状态在页面对象里**，宿主看不见 |
| 导出实现 | `StaticWorkbenchPage.ExportStaticModAsync`（`:495-558`） | 已用 `StaticModService.CreateJsonPatchPackage` + `Write`，产物是 LCTA 兼容的 `.staticmod` |
| 包读写 | `StaticMods/StaticModService.cs` | `Read/Write/LooksLikeStaticMod/ApplyPatchToDocument/PathsetToJsonPatch` 全齐 |
| 数据来源 | `StaticIndexService` / `StaticTableIndexStore`（`cache/static-tables.db`） | 1392 张表元数据 + 按需正文缓存，已实测热读 11ms |

所以本轮 `_static` 槽位要做的不是「新写导出器」，而是：

1. **把静态编辑集从页面搬到宿主**（与 §2.4 的 `LangEditSession` 同构）：新增
   `StaticEditSession`（键 = `dataClass/file`，值 = 修改后 JSON + official 基线），
   `IWorkbenchHost` 一并暴露；`StaticWorkbenchPage` 改为注入使用，导出/调试不再依赖该页是否被打开过。
2. **槽位执行器复用 `CreateJsonPatchPackage`**：把「写临时 official/modified 文件再 diff」这条
   （现在在页面里、还带临时目录清理）下沉到 `ModPackExportService`，直接产出
   `<项目名>_static/staticmod/<项目名>.staticmod`（`patches/*.json` 用 `opType=jsonpatch` + `container` 精确寻址）。
3. **删掉静态页的「导出 .staticmod…」按钮**（与 bank 页两个导出按钮同一处理），页面只保留编辑与「保存到编辑集」。
4. **调试期的应用（真正的难点，也是唯一有风险的部分）**：按 `launcher/staticmod.py` 的事实实现
   「定位 static 条目 → 解包官方 bundle → 应用 jsonpatch → 改 TextAsset → `_save_bundle(lz4)` 重打包 →
   算解压块 CRC32 → 双写缓存 `__data`/`__info` 与 catalog 的 `crc@+0x44`/`size@+0x48`」，
   关闭时从本地备份还原并回写 catalog。**可以直读的部分**：`_read_textasset_json` 的 container 优先匹配、
   `_build_textasset_raw`（`<i32 len><name>` + 4 字节对齐 + `<i32 len><script>`）都在 `staticmod.py:258-310`，
   C# 侧照语义实现即可（不整段抄）；catalog 定位是纯字节搜索（`static_s1_0_assets_all_<32hex>` → `Hash128` →
   校验外层键 → 读 crc/size），无第三方依赖。
5. **风险护栏必须照抄加载器的那一层**：LCTA 的静态模组默认**关闭**
   （`launcher.work.staticmod`，`launcher/staticmod.py:67-80`），因为它要改 catalog。编辑器侧：
   - 调试应用前弹一次风险确认（与 `ApplyToGame` 时代的二次确认同级，文案说明「会写 catalog 的 crc/size」）；
   - 备份目录里额外保存 **官方 `__data` + `__info` + catalog 原始字节**，恢复时先还原再回写 crc/size，
     **不做「删缓存让游戏重下」**（加载器本体也是本地备份还原，`staticmod.py:687-727`）；
   - 找不到现行 static 条目 / 缓存里没有官方 bundle → **不猜测、不写**，直接在报告里说明跳过原因。

#### 10.2.2 与 `.rebank` 的关键对比（为什么这个更干净）

| | `.rebank` | `.staticmod` |
|---|---|---|
| 编辑器现有写出是否与加载器语义一致 | ❌ 条目名 `{i}.fsb` vs 加载器要的真实 wav 名（§2.3.1） | ✅ `opType=jsonpatch` + `container` 已对齐（`StaticModService` 注释与 `staticmod.py:611-632` 一致） |
| 依赖外部 DLL | 需要（WAV→FSB 编码要 `fsbank64.dll`） | 不需要（纯 JSON + 已有 bundle 重打包 + CRC32） |
| 调试应用的风险面 | 游戏目录里的 `.bank` 文件 | **catalog + Unity 缓存条目**（会触发引擎缓存校验，加载器为此专门写了 cross-version 逻辑） |
| 结论 | 要修条目名，否则静默失效 | 可直接接入，重点在调试验证与回滚 |

#### 10.2.3 建议的取舍（可执行）

- **本轮必做**：`_static/staticmod` 槽位（复用现有导出器 + `StaticEditSession` 抽离 + 删页面按钮）。
- **本轮可延后**：静态模组的**调试应用**（§10.2.1 第 4/5 点）单独作为一个里程碑
  （建议排在 S7 之后，命名 S7b）：先把「导出 → 用户手动放进模组目录 → LCTA 应用」跑通验收，
  再接 catalog 双写的调试通道；在调试报告里对静态模组明确写「本次调试未应用（catalog 写入待 S7b）」，
  **不静默跳过**。
