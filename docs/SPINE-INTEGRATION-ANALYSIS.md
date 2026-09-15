# Spine 接入逻辑分析（现状 · 实测 · 决策清单）

- 分析方式：**只读**（不修改源码 / 不跑 build / 不跑 test；索引库一律 `mode=ro`）
- 日期：2026-09-14
- 数据源：应用运行时实况 `artifacts/publish-win-x64/cache/{unity-cache-index,relation-index}.db`
- 完整证据（SQL 原文 + 逐条输出）：`logs/analysis/spine-realdata.md`、`logs/analysis/spine-code-test.md`、`logs/analysis/spine-integration.md`

---

## 0. 一句话结论

Spine 有**三层能力**（路径归类 → 结构预览 → 真渲染播放）和**两个渲染器**，分层与失败处理写得干净；但真机实测显示**三层口径互不衔接**：

> 按路径归类出的 **8,210** 个 Spine 资源里，只有 **4** 个能进「Spine 文本预览」的 Type 闸门；代码依赖的「同目录三件套（json + atlas.txt + png）」在整份索引里**只存在 1 个目录**；卡片流里 **174** 条 Spine 链接的「动画预览…」按钮指向的 prefab 目录中**没有任何骨架 JSON**——点下去必然弹中文失败提示。

全库**唯一**能从头跑通到「真播放」的真实资产是 1 个文件：`cg_40.atlas.txt`。

---

## 1. 现状：三条链路 + 两个渲染器

### 1.1 用户可见入口（全部）

| 入口 | 触发条件 | 经过的代码 |
|---|---|---|
| 资源页选中某资源 | `LooksLikeSpineText`：`Type ∈ {Text, Json}` **且**（`.atlas.txt` 后缀 **或** `IsSpinePath`） | `SpinePreviewProvider` → `SpinePreviewService.Build` → 结构行 + 图集区域框叠加图 |
| 资源页 →「▶ 动画预览…」 | 上一步 `preview.Kind == Spine` 且 `_selectedAsset` 非空 | `SpineAnimationSourceService.Resolve` → `SpineAnimationPreviewWindow`（SpineRuntime 真渲染） |
| 卡片流 Spine 行 →「动画预览…」 | `link.Kind == Spine` 且该行能定位到资源 | 同上 |
| 卡片流 Spine 行 →「导出…」 | 同上 | `SpineExportService.ExportAsync`（导出三件套给外部 Spine 工具） |

关键代码位置：`AssetsWorkbenchPage.xaml.cs:934`（`AssetPreviewKind.Spine => BuildSpineView`）、`:1453-1490`（`BuildSpineView`）、`:1496`（`PreviewSpineAnimation`）；`PresetWorkbenchPage.xaml.cs:754-767`（Spine 行两个按钮）、`:775`、`:832`。

### 1.2 代码资产

| 层 | 位置 | 内容 |
|---|---|---|
| 业务层 | `src/LimbusModEditor.Application/Spine/`（5 文件 ≈1,400 行） | `SpinePreviewService`（`SpineSiblingIndex` 同目录索引 + 结构解析 + 区域框叠加图）、`SpineModels`（**手写** Spine 文本解析器 `SpineTextParser`）、`SpineAnimationSourceService`（找素材）、`SpineExportService`（导出）、`SpinePreviewProvider` |
| 渲染层 | `src/LimbusModEditor.SpineRuntime/`（net8.0 独立工程） | vendored spine-csharp 4.0（`third_party/spine-csharp`，42 个 `.cs`，README 声明面向 **Spine 4.0.xx**）+ SkiaSharp **3.119.0**；`SpineDocument` / `SpineFrameRenderer`（region + mesh + `SkeletonClipping` + `pma` 预乘 + 顶点色） |
| UI | `src/LimbusModEditor.App/SpineAnimationPreviewWindow.cs` | 30fps 播放 / 暂停 / 切动画 / 拖时间轴；非模态 |

### 1.3 设计上做对了的（建议保留）

1. **Provider 插在文本预览之前**，解析不出返回 `null` 回落文本预览——声称命中的路径不会因「看起来像」而丢内容（`AssetPreview.cs:114-116`）。
2. **失败一律给中文原因、绝不抛**：`SpineLoadResult` / `SpineRenderResult` / `Resolve` 返回 `(source, error)`（`SpineResults.cs:1-45`、`SpineAnimationSourceService.cs:65-111`）。
3. **素材解析与渲染分离**：Application 层不依赖 Skia / WPF，可被单测覆盖（`SpineAnimationSourceService.cs:9-16` 明说了这个理由）。
4. **导出不做成导出槽位**：避免污染「模组只导出被修改资源」的语义（`SpineExportService.cs:22-25`）。

---

## 2. 实测：代码假设 vs 真实数据

基线：`assets` 表 **1,275,623** 行，其中 `container_entry` 非空只有 **51,376**（4.0%）——`SpineSiblingIndex` 能索引的就这 5 万行。

路径判据（`RelationDisplayRules.IsSpinePath`，`RelationDisplayRules.cs:14-21`）四条：① 含 `/SpineIllustPrefab/` ② 以 `.psb` 结尾 ③ 含 `SkeletonData` ④ 含 `/Story/Spine/`。
文本闸门（`SpinePreviewService.LooksLikeSpineText`，`SpinePreviewService.cs:141-147`）：`Type ∈ {Text, Json}`。

| 代码假设 | 真实数据 | 判定 |
|---|---|---|
| 路径四判据能圈出 Spine 资源 | 并集 **8,210** 行（①123 ②8,066 ③1 ④20，**互不重叠**） | 成立【已验证】 |
| 闸门能接住路径归类的 Spine | 8,210 中 `Type∈{Text,Json}` 只有 **3** 行（**0.04%**）；其余 8,207 是 GameObject(3,983) / Sprite(3,505) / Texture(358) / MonoBehaviour(353) / Material(6) / Animation(2) | **严重错配**【已验证】 |
| 同目录三件套 `*.json + *.atlas.txt + *.png` | 全库 `.atlas.txt` **仅 1 行**；同时含 `.json` 与 `.atlas.txt` 的目录 **仅 1 个** | **基本不成立**【已验证】 |
| Spine 骨架是可枚举的顶层 `.json` | `.json` 共 3,710 行（165 个目录），抽样几乎全是 `StaticData/...` 配置；`.json` 与 `.psb` 同目录 **0** 行 | 不可行【推测·高】 |
| relation-index 有 spine 链接 | `kind='Spine'`（**首字母大写**）**174** 条 / 138 个 subject，`ref_path` **全是 `.prefab`**，其中 **123** 条能在索引里定位（均 `GameObject`） | 成立，但**层级不同**【已验证】 |

真实可达面（我的独立复核，`logs/analysis/spine_verify.py`）：

```
union spine-path rows: 8210      union text_json: 3      atlas.txt rows: 1
json dirs=165  atlas dirs=1  inter=1 -> StorySpine_Sinclair
gate-passers = 42_Spine.json / 42_Spine3.json / 42_Spine4.json  (均在 /Story/Spine/Ep5_2/SpineCG/Data/)
cg40.json = type 4 (Text), type_id 49, size 202,292   cg40.atlas.txt = type 4, size 2,080
```

→ **`LooksLikeSpineText` 在全库只有 4 个资产为真**：3 个 `42_Spine*.json` + 1 个 `cg_40.atlas.txt`。

**另一个映射校正【已验证】**：`assets.type = Json(5)` 全库 **0 行**。Spine 的 `.json` 在缓存里是 `type=4 (Text) / type_id=49 (TextAsset)`。也就是说 `LooksLikeSpineText` 里的 `AssetType.Json` 分支对缓存资产是**死代码**（对导入的旧式资源仍可能有用）。

---

## 3. 缺口（按影响排序）

### G1 · 闸门与判据错配：骨架 JSON 逃逸（影响：入口不可达）

`cg_40.json` 的路径是 `…/Story/CG/Ep9_3/StorySpine_Sinclair/cg_40.json`——不含 `/Story/Spine/`、不以 `.psb` 结尾、不含 `SkeletonData` → `IsSpinePath` 返回 **false**；又不以 `.atlas.txt` 结尾 → **`LooksLikeSpineText` 为 false**。
后果：从这份**全库唯一完整三件套**的骨架入口点进去，得到的是普通 JSON 文本预览，既没有 Spine 结构视图，也没有「动画预览…」按钮。**能播的入口只剩旁边那个 `cg_40.atlas.txt`**（靠 `.atlas.txt` 后缀单独被接住）。
→ 路径判据对 `/Story/CG/.../StorySpine_X/` 这类真实目录存在**系统性漏判**（推测·中）。

### G2 · 「同目录三件套」在真实数据里几乎不存在（影响：解析策略前提错）

`SpineAnimationSourceService` 的定位策略是「同目录找 `.json` + `.atlas.txt` + `.png`」（`SpineAnimationSourceService.cs:113-135`）。实测各判据目录中的 `.json` / `.atlas.txt` 兄弟数量：

| 判据目录 | 行数 | 目录内有 `.json` | 目录内有 `.atlas.txt` |
|---|---|---|---|
| `/SpineIllustPrefab/` | 123 | **0** | **0** |
| `.psb`（`Story/StandingModel/…`） | 8,066 | **0** | **0** |
| `/Story/Spine/` | 20 | 15 | **0** |
| `SkeletonData`（即 `StorySpine_Sinclair`） | 1 | 1 | 1 |

→ 全库**只有 `StorySpine_Sinclair` 一个目录**能凑齐三件套。真实骨架/图集更多是被 prefab、`*_SkeletonData.asset`、`.psb` 经 **PPtr 引用**，而非目录同列。

### G3 · 卡片流 174 条 Spine 行的动画按钮恒失败（影响：必然失败的错误按钮）

`PresetDetailRow.Asset` = `index.TryGetValue(link.RefKey)`（`PresetWorkbenchService.cs:285-286`），而 spine 链接的 `ref_path` 全是 `Prefab/SpineIllustPrefab/*.prefab` → `row.Asset` 是 **prefab（GameObject）**。
点「动画预览…」→ `Resolve(prefab)` → `PickSkeleton` 在 `Prefab/SpineIllustPrefab/` 里找 `.json` → **0 个** → 返回 `"同目录里找不到骨架 JSON（*.json），无法播放动画。"`（`SpineAnimationSourceService.cs:73`）→ 每次都弹 Warning 对话框（`PresetWorkbenchPage.xaml.cs:781`）。
→ 约 **123** 行（能定位到资源的那部分）会呈现一个**点了必然失败**的按钮。

### G4 · 两个渲染器口径分裂：mesh（影响：文案/能力不一致）

- 结构预览层：`SpinePreviewService.cs:207` 明确「*N 个非区域附件（网格等）不支持渲染*」，`SpineModels.cs:44-50` 的解析器只收 `region` 附件。
- 渲染播放层：`SpineFrameRenderer` **实际支持** `MeshAttachment` + `SkeletonClipping` + `PMA`（`SpineFrameRenderer.cs:138-154、208-228`）。
→ 同一份资源，「结构预览说渲染不了 mesh」而「动画播放能渲染 mesh」。口径分裂 + 两份独立解析实现（手写 `SpineTextParser` vs vendored spine-csharp）。

### G5 · 测试零真实数据（影响：正确性无回归）

Spine 相关用例共 **25** 个（`SpineRuntime.Tests` 7 + `Domain.Tests/SpineTests` 12 + `SpineAnimationSourceServiceTests` 6），**全部使用合成/手写数据**（`SyntheticData.cs` 现画纯色 PNG；贴图占位串 `"not-a-real-png"`），**零用例跑真实游戏资源**。
「测试通过」证明：解析器/渲染器能处理良构最小骨架与合成边界（非法 JSON、缺页、`rotate:90`、`pma`、时间→像素变化）。
「测试通过」**不证明**：真实 4.0.64 资产（mesh / IK / clipping / 全套动画）渲染正确，也不覆盖 `PageBytes → ReadBundle*` 取真实页 PNG 这条链路。
旁证：git 史上 SpineRuntime 由 `956b0d8`（09-14）引入、UI 由 `a2f32d4` 接入后，**没有任何渲染 / Alpha / PMA / PNG 修复提交**。

---

## 4. 已验证 vs 待验证

### 已验证（有 SQL + 真实输出或 `文件:行号` 支撑）
- 上文全部数字：1,275,623 / 51,376 / 8,210 / 3 / 4 / 174 / 123 / 1 个三件套目录。
- `cg_40.json` 为顶层可枚举行（`type=4 Text`、`size=202,292`）；`StorySpine_Sinclair` 目录 17 行与代码注释完全吻合。
- `assets.type` = `AssetType` 枚举、`type_id` = Unity class id；`type=5 (Json)` 全库 0 行。
- provider 顺序、`BuildSpineView` 元素与按钮条件、测试清单与输入性质、4 条 spine 提交、依赖版本、mesh 在结构层的「不支持」。

### 待验证（需跑一次代码才能确证，本次只读分析不做）
1. `42_Spine.json / 42_Spine3 / 42_Spine4` 是否**真的**是 Spine 骨架（`SpineTextParser.ParseSkeleton` 要求顶层有 `bones` 数组）——若不是，这 3 个也会回落到文本预览。**验证方法**：在应用里点开这 3 个文件看是否有结构行，或写一个只读 CLI 调 `ReadBundleTextAsset` 打印前 200 字符。
2. `cg_40.atlas.txt` 实际点开 →「动画预览…」是否真的能出图（本次仅由代码路径与数据推断为可达；**未经真人点击验证**）。对照 `PLAN-RELATIONS-V2.md:263` 的声明式验收项「`cg_40` 渲染出非空位图且尺寸与 atlas 一致」。
3. `SpineIllustPrefab/10103_gacksung.prefab` 是否真能取到骨架（`PLAN-RELATIONS-V2.md:250` 声称「能取到 spine=4.0.64, bones=12, slots=21」）——**与 G3 的结论直接冲突**，必须弄清：是文档口径过时，还是存在一条本次没读到的 PPtr 解析路径。
4. 「96% 资产无 `container_entry`」中是否**还藏着** Spine 骨架行（无容器路径 → 兄弟索引取不到 → 即使存在也走不通）。

---

## 5. 需要拍板的决策

**D1 · 入口判据怎么修？（G1）**
- A. 给 `LooksLikeSpineText` 加旁路：`Type ∈ {Text,Json}` 且**同目录存在 `.atlas.txt` 或有 `.png` 兄弟的 `.json`** → 不再依赖路径前缀。（推荐：改动小、直接补上 `cg_40.json` 这类漏判）
- B. 只让 `.atlas.txt` 当入口锚点，骨架 JSON 不单独给 Spine 视图。（更保守，但用户从骨架点进去仍然看不到结构）
- C. 不修判据，只把 `IsSpinePath` 的四条前缀补全（治不了 `StorySpine_X` 这种任意目录名）。

**D2 · 卡片流的 Spine 行动画按钮怎么办？（G3）**
- A. 解析不出素材时**置灰按钮 + tooltip 说明原因**（诚实降级，成本最低，立刻消除「点了必失败」）。
- B. 把按钮改成「跳到该 prefab 所在的 Spine 目录 / 关联骨架」，让用户能点到真正能播的资源。
- C. 真做 PPtr 解析（从 prefab / `SkeletonData.asset` 里把骨架与图集引用解出来）——**唯一能真正扩大可达面的方案，但成本最大**，且依赖 `UnityAssetService` 是否已有 PPtr 遍历能力。

**D3 · 要不要补「真实数据回归」？（G5）**
- A. 把 `StorySpine_Sinclair` 三件套（`cg_40.json` / `.atlas.txt` / `.png`）作为 fixture，走 `ReadBundleTextAsset` 真链路跑一次渲染断言。
- B. 维持合成数据，但在 `docs/STATUS.md` / `REALDATA-VERIFY.md` 里显式登记「Spine 渲染未经真实资产验证」（这两个文件目前**完全没有 Spine 段落**）。

**D4 · 两个渲染器要不要合并？（G4）**
- A. 结构预览也改走 vendored spine-csharp 解析（删掉手写 `SpineTextParser` 的骨架部分）→ 一份口径、mesh 信息不再自相矛盾。
- B. 保留两份（手写解析器无第三方依赖、可纯函数单测），但把「不支持 mesh」的文案改成「结构视图不展开 mesh（播放视图支持）」。

> 另需注意：`SpinePreviewService` 在 `AssetsWorkbenchPage` 与 `PresetWorkbenchPage` 各有**一份独立实例**（各自 `new`，`AssetsWorkbenchPage.xaml.cs:108`、`PresetWorkbenchPage.xaml.cs:102`），因此「同目录索引」实际上被建了两份、且互不共享缓存。若后续要优化，这是个低垂果实（不属于本次必改项）。
