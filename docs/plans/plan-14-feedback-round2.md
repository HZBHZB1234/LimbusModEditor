# plan-14：用户反馈修复批次（第二轮，5 项）

> 来源：2026-09 用户第三批反馈之后的**第二轮截图反馈**（音频工作台 bank 区域 + 文本工作台 lang 区域）。
> 前置：plan-13（第一轮 8 项）已实施入库，基线 549 个测试全绿。

## 0. 本轮需求（用户原话 → 逐条落地）

| # | 用户原话 | 落地口径 | 归属 |
|---|---|---|---|
| 1 | 「bank 这里的对比度不清晰」 | 树/列表的**选中行与 hover 行**要有明确底色；文字层级（主/次/弱）在深色底上拉开对比；明细键值格的标签与值分别用次色/主色 | 14.1 |
| 2 | 「两个导出按钮不必要，因为这两个按钮在左侧侧边栏导出模组板块应该有」 | 文本工作台编辑列的「导出 lang 补丁…」「直接应用到 lang 目录…」两个按钮**删除**，能力移到侧边栏「② 产出模组」板块 | 14.5 |
| 3 | 「bank 树板块默认不显示事件 bank」 | bank 树默认只列音频 bank / 加密无法识别 bank；新增「显示事件 bank」勾选框（默认不勾），选「仅事件 bank」筛选时自动显示 | 14.2 |
| 4 | 「如果只有一个 fsb 的话那默认展开」 | 展开一个 bank 节点时，若该 bank 只有 1 个 FSB，则该 FSB 节点一并展开到样本层 | 14.3 |
| 5 | 「lang 页面预览异常。lang 页面应该默认从对应的文件夹内部进行解析，类似现在应该是从 LLc-CN_LCTA 内部开始解析，不需要 config.json 和外层目录」 | ① 修 preview 打不开的**根因 bug**（索引命中路径没有把 lang 根交给服务，编辑集没有基线 → `BeginEdit` 直接抛错 → 预览区空白、「—」、编辑集空）；② 浏览列/树的口径改为**活动语言目录内部**：树顶层直接是 `AbDlg_Faust.json` / `StoryData` 这些，不再有 `config.json` 行，也不再有 `LLc-CN-LCTA` 这一层 | 14.4 |

## 14.0 两条不可动的既有契约（本轮所有改动都必须遵守）

1. **补丁键口径 = 相对 lang 根**（`LangTextPatchService`：加载器按 `<游戏>/LimbusCompany_Data/lang/<键>` 备份并应用）。
   因此 14.4 的「从语言目录内部开始」**只是显示口径**：内部键（索引行 / 编辑集 / 搜索命中 / 补丁文档键）
   仍然带语言目录前缀（`LLc-CN-LCTA/AbDlg_Faust.json`）。若把补丁键也改成语言目录内部相对路径，
   加载器会把补丁打到 `lang/AbDlg_Faust.json`（不存在）→ 直接跳过，等于模组失效。
2. **缓存只存 vanilla 事实 + 失效只认源签名**：`cache/text-index.db` 的签名仍是
   「lang 根目录签名 + config.json 内容哈希」，切换活动语言照样整库重建。
   本轮只是**不再把 `config.json` 当工作台文件收录**（它的内容哈希仍在签名里，功能不减）。

## 14.1 音频工作台对比度

**现象**：bank 树里选中一行只有一层几乎看不出的灰底；样本行、bank 行、FSB 行的文字层次糊在一起；
「选中样本」明细块的标签（弱色）与值（默认色）对比不足。

**根因**：
- 共享树样式 `WorkbenchTreeItem` 只设了 `Foreground`，选中/hover 的底色完全交给 WPF-UI 的默认
  模板（在自定义的深色列表底 `WbListBrush #11151A` 上，默认选中底色几乎不可见）。
- 明细键值格的标签用 `WbTextMutedBrush #7F93A4`，与列表底对比不足；64px 的标签列也偏窄。

**改法**：
- `Themes/WorkbenchStyles.xaml`：新增令牌 `WbRowHoverBrush` / `WbRowSelectedBrush`（选中态另加左侧
  2px 强调条）；`WorkbenchTreeItem` 改成**自带 ControlTemplate** 的样式（展开箭头 + Header + ItemsPresenter），
  `IsMouseOver` → hover 底、`IsSelected` → 选中底 + 主文字色，不再依赖 Fluent 模板的内部画刷。
- 音频页明细格：标签列 64 → 72px，标签色 `WbTextMutedBrush` → `WbTextSecondaryBrush`，值显式主色。
- 不引入任何新色值到页面代码（色值仍只在 `Themes/`）。

**验收**：离屏 WPF 渲染校验（`RenderTargetBitmap` 取选中行像素 ≈ `WbRowSelectedBrush`、
未选中行 ≈ 列表底、hover 行 ≈ `WbRowHoverBrush`）+ `grep #RRGGBB` 在 `WorkbenchPages/` 无命中。

## 14.2 bank 树默认隐藏事件 bank

**现象**：bank 树里混着大量事件 bank（SNDH 为空、没有可试听/替换的样本），把音频 bank 淹掉。

**改法**：
- 纯规则抽到 `Application/Assets/BankTreeRules.cs`（可单测）：`ShouldShowBank(kind, kindFilter, showEventBanks)`。
- 音频页筛选行新增勾选框「显示事件 bank」（默认不勾）；选「仅事件 bank」时强制显示（否则那个筛选会显示空树）。
- 树被过滤掉事件 bank 时，状态条/空态明确说明隐藏了几个（不静默丢数据）。

**验收**：`BankTreeRulesTests`（默认隐藏 / 勾选后显示 / 「仅事件 bank」强制显示 / 加密无法识别不受影响）。

## 14.3 单个 FSB 的 bank 默认展开到样本

**改法**：展开 bank 节点后，若该 bank 的 FSB 分组只有 1 个，直接把它 `IsExpanded = true`
（占位子项被现有 `TreeItem_Expanded` 填成样本行；填充幂等，不会重复生成）。
多 FSB 的 bank 行为不变（仍只展开到 FSB 层，避免一次性物化上千样本行）。

**验收**：`BankTreeRules.ShouldAutoExpandSingleFsb(fsbCount)` 单测 + 人工核对单 FSB bank 展开即见样本。

## 14.4 lang 工作台：预览修复 + 数据源改为语言目录内部

### (a) 预览异常的真根因（先修）

`TextWorkbenchPage.RefreshAsync` 有两条路径：

```csharp
if (fresh) files = _store.ReadFiles(langRoot);            // ← 只读索引，没碰服务
else       files = _service.EnumerateFiles(langRoot, …);  // ← 顺带把 lang 根交给服务
```

`LangTextWorkbenchService.BeginEdit/SetModified/…` 依赖 `EnumerateFiles` 设置的 `_langRoot`；
走索引命中路径时 `_langRoot` 仍是 null → `RequireLangRoot()` 抛
`InvalidOperationException("请先调用 EnumerateFiles 定位 lang 根…")` → `SelectFileAsync` 进 catch →
编辑列只留下 `—`、树空、编辑集空（= 用户截图里那块「预览异常」的空白）。

**为什么 plan-13 之后必然复现**：plan-13 把「启动时自动扫描四个缓存库」做成了默认行为，
于是进文本工作台时 `text-index.db` **总是 fresh** → 总是走索引路径 → 预览永远打不开。

**改法**：服务新增 `AttachLangRoot(langRoot)`（幂等），页面在 `ResolveLangRoot` 成功后**无条件**调用，
再按 fresh 分支读索引。单测钉死「只调 AttachLangRoot + BeginEdit 也能工作」这条页面依赖的契约。

### (b) 数据源口径：从活动语言目录内部开始

- `EnumerateFiles` 不再把根级 `config.json` 收进工作台（它仍被 `ReadActiveLanguage` /
  `ComputeConfigContentHash` 使用，只是不再是「可编辑文件」）。
- 旧库的自愈靠**内容口径版本**（`TextIndexStore.IndexFormatVersion`，v1 → v2）：
  目录 mtime 与 config.json 内容都没变，只靠「行集比对」是清不掉旧 `config.json` 行的
  ——签名没变，库会被判为「新鲜」，旧行就会被一直读出来。把口径版本并进
  `index_meta.signature` 后，旧库在启动扫描/首次进页面时自动整库重建（一次性扫一遍 lang 文件）。
- 页面新增**显示前缀**：`活动语言目录名 + "/"`（大小写按磁盘真实目录名解析，避免 config.json 里
  写 `LLc-CN_LCTA` 这类大小写差异导致映射错位）；显示路径 = 内部键去掉该前缀。
- 树：绑定「活动语言目录」这一层的**直接子项**（不再有内层目录包裹节点），顶层就是
  `AbDlg_Faust.json`、`StoryData/` 等；`_langInfo` 一行写清「数据源目录 / 内部键前缀 / 补丁键仍含前缀」。
- 列表 / 搜索命中 / 状态文案一律显示显示路径；导航（选中文件、跳转命中）经「显示路径 → 内部键」映射。
- 活动语言目录解析不出来时（config.json 缺 lang / 目录不存在）：**不浏览任何文件**，
  在 `_langInfo` 与空态里明确指出「config.json 未指定可用的活动语言目录，请检查 …\lang\config.json
  的 lang 字段」（不猜、不退化成把别的语言目录当数据源）。

**验收**：
- `LangTextWorkbenchServiceTests`：`AttachLangRoot` 契约、枚举不再含 `config.json`、
  `ResolveLanguageDirectory` 大小写照磁盘解析。
- `LangTextDisplayTests`（新增）：前缀生成 / 显示路径 / 补丁键三个方向的映射 + 往返一致性。
- `TextIndexStoreTests` / `StartupScanServiceTests` / `RealTextIndexSmokeTests`：按新口径更新断言。
- `LangTextPatchServiceTests` 与 `LangTextWorkbenchServiceTests.Export_writes_lcta_compatible_patch_and_replays_to_modified`
  **一字未改**：补丁键仍是 lang 根相对路径（含语言目录前缀）——这条是加载器兼容门，测试通过即证据。

## 14.5 lang 导出通道移到侧边栏

**改法**：
- 删除文本工作台编辑列的 `_exportPatch` / `_applyToGame` 两个按钮及其行容器（编辑列只留
  「还原此文件」与 JsonTreeEditor 自己的按钮）。
- 侧边栏「② 产出模组」新增「导出 lang 补丁…」与「直接应用到 lang 目录…」两个按钮，
  转调文本工作台既有通道（`TextWorkbenchPage.ExportPatchInteractive()` /
  `ApplyToGameInteractive()`；编辑集仍在页面对象里，未打开过页面时给出明确提示）。
- 「导出思路」里的 lang 思路改为：有编辑集就直接导出，否则打开文本工作台。

**验收**：build + test 全绿；`grep` 确认页面里不再有这两个按钮；人工核对侧边栏两个入口可用。

## 审查门（本轮）

- [x] 基线三命令全绿（build / test / publish），测试数 549 → **561**（+6 BankTreeRules / +4 LangTextDisplay / +2 LangTextWorkbench）
- [x] `WorkbenchPages/` 无设计色字面量（色值只在 `Themes/`）
- [x] `LangTextPatchService` 补丁键口径未变（含语言目录前缀），相关测试未放宽
- [x] 缓存失效规则未变（签名仍是目录签名 + config.json 内容哈希）
- [x] 文档更新：`docs/plans/README.md`、`docs/ROADMAP.md`、`docs/USAGE.md`
- [ ] 人工核对清单（见文末）

## 实测证据（本机）

### 14.1 行对比度（离屏渲染逐像素采样）

脚手架：`artifacts/wpf-check/`（git 忽略，`dotnet run -c Release`）——载入真实的
`App.xaml` 资源（WPF-UI 深色主题 + `Theme.xaml` + `WorkbenchStyles.xaml`），
在 `WbListBrush` 底上放一棵树，`RenderTargetBitmap` 渲染后采样每行像素：

| 行 | 底色 | 与列表底 `#11151A` 的亮度差 | 最亮文字像素 |
|---|---|---|---|
| 普通行（bank / FSB / 样本） | `#11151A` | 0 | `#E8EDF2`（主文字） |
| **选中行（新模板）** | `#20304A` | **+26** | `#4C8DDA`（强调色） |
| 选中行（WPF-UI 默认模板，改动前） | `#11151A` | **0** | `#E8EDF2` |

即「用户说的对比度不清晰」被数值复现：默认模板的选中行底色与列表底**完全相同**；
新模板另加左侧 2px 强调条。层级缩进实测 22px/层（父子行偏移 44px = 2 层）。
`IsMouseOver` / `IsSelected` 两条触发器都在模板里（脚本断言存在）。
清单表（`ListView`）用 WPF-UI 默认模板实测选中底色 `#414448`（差 +47），**本来就够清楚，未改**。

### 14.2 / 14.3 bank 树规则

`BankTreeRulesTests` 6 条全绿：默认隐藏事件 bank、勾选框显示、`仅音频 / 加密无法识别` 不受勾选框影响、
`仅事件 bank` **强制显示**（否则会显示一棵空树）、隐藏数量如实统计、只有 1 个 FSB 才自动展开。

### 14.4 / 14.5 lang 工作台

- 预览失败根因复现与修复各有一格测试：`Attach_lang_root_enables_editing_on_the_index_hit_path`
  （未 Attach 时 `BeginEdit` 抛「请先调用 EnumerateFiles…」= 现象；Attach 后可编辑且幂等）。
- 枚举口径：`Enumerate_lists_active_language_files_and_excludes_config_and_other_languages`、
  `Real_lang_directory_enumerates_active_language_with_story_data_and_key_counts`（真实 lang 目录上
  `config.json` 已不在清单里，其余断言照旧）。
- 加载器兼容：`Export_writes_lcta_compatible_patch_and_replays_to_modified` 未改动且通过——
  补丁键仍是 `LLC_zh-CN/AbDlg_Faust.json`（含语言目录前缀）。
- 旧索引库自愈：`TextIndexStore.IndexFormatVersion`（v1 → v2）并进源签名，
  旧库被判为过期 → 整库重建（一次性）；`TextIndexStoreTests`
  新增 `Legacy_index_content_format_is_invalidated_and_the_config_row_disappears` 钉死这条。

## 人工核对清单（用户侧）

1. 音频工作台切换/选中 bank 树里的行：选中行有清晰底色 + 左侧强调条，hover 有底色，文字不再糊成一片。
2. bank 树默认看不到事件 bank；勾「显示事件 bank」后出现；选「仅事件 bank」筛选时能看到事件 bank。
3. 打开一个只有 1 个 FSB 的 bank（如 `1D101A.assets.bank`）：展开后直接看到 7 个样本行。
4. 文本工作台：树顶层直接是 `AbDlg_Faust.json`、`StoryData` 等（没有 `config.json`、没有语言目录包裹层）；
   点任一文件**能看到键值树**（不再空白 + 「—」）；改一个值后编辑集显示 1 个文件已改。
5. 侧边栏「② 产出模组」：改完文本后点「导出 lang 补丁…」能导出；补丁 JSON 的键形如
   `LLc-CN-LCTA/AbDlg_Faust.json`（含语言目录前缀，加载器按 lang 根应用）。
