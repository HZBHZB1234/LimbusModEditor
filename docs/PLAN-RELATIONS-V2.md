# 关联体系 v2 · 实施计划（Relations v2）

> 状态：**待确认**（本文件是计划，不含实现）。
> 依据：5 份真实数据只读分析（`logs/analysis/{category-taxonomy, crosslink-mapping, image-failures, spine-library, deeplink-gaps}.md`，**本地生成、未入库**——结论已摘录进本文件）。
> 全部数字来自真实索引库与游戏目录实测，非估算；推测项均已标注。

---

## 1. 目标：六个问题 → 六个修复项

| # | 用户问题 | 修复项 | 阶段 |
|---|---|---|---|
| 1 | 关联数据不智能，用户要亲自点开、亲自翻到目标位置 | 精确跳转（deep-link） | **D** |
| 2 | 关联数据应在卡片的预览部分展示内容与预览功能 | 卡片/详情内联预览 | **E** |
| 3 | 卡片只有人格一种 | 多类别：敌人 / 异常 / 播报员 | **B** |
| 4 | Spine 预览没成功，改用第三方库 | 引入 spine-csharp 4.0 + SkiaSharp | **F** |
| 5 | 需要交叉关联（汉化文本 ↔ 数据 ↔ 音频） | 跨资源交叉关联 + 台词原文 | **C** |
| 6 | 部分资源与图片没有成功加载 | 预览覆盖修复 | **G** |

**前置阶段 A** = 关联库 v2（schema 与多类别骨架），六个修复项的共同地基。
**阶段 0** = 先提交现有 Phase 2/3 基线（当前工作区未提交）。

---

## 2. 真实数据勘误（先纠正，否则设计会错）

这四条是本次分析**证伪**的既有认知，必须写进代码注释与文档，避免后来者再踩：

| 旧认知 | 实测真相 | 证据 |
|---|---|---|
| `assets.type`：1=精灵图、2=纹理 | **反了**。`type` 列是 `AssetType` 枚举值（0 起）：**1=Texture、2=Sprite**、13=GameObject、14=Component…；真正的 Unity class id 在 `type_id` 列（28=Texture2D、213=Sprite） | `AssetModels.cs:3-35` 无显式赋值 + `SELECT type,type_id,count(*) GROUP BY type` 完全吻合 |
| 人格语音样本名是 `voice_<角色>_<id>_<n>` | 实际族是 `<族>_<type>_<5位id>_<n>`（`battle_break_10201_1`、`lobby_morning_10201_1`），**`<别名>` 是事件类型不是角色**；只有 `voice_devai_11011_*` 共 5 条带 `voice_` 前缀 | `bank-index.samples` 前缀 GROUP BY |
| 图像 ↔ 文本无法交叉 | 那次查询用错了列（查 `container` 而非 `container_entry`）。Sprite 清单**存在**：`Sprite/SkillIcon/*` 6717 条、`Sprite/Unit/*` 4990 条 | `unity-cache-index.assets.container_entry` |
| Spine 资源是"文本解析失败" | 是 **provider 根本没被选中**：`LooksLikeSpineText` 硬要求 `Type is Text/Json`，而 8066 条 `.psb` + 123 条 prefab 全是二进制类型 | `SpinePreviewService.cs:141` + 类型分布实查 |

**另一条关键事实**：人格语音的**样本名与 lang 语音文本的 `id` 同名同值**，因此「音频 → 台词原文」是**精确一对一**（实测命中 4694 条），不需要启发式。

---

## 3. 目标架构

```text
四个源索引库（不变）
  unity-cache-index.db / bank-index.db / static-tables.db / text-index.db
        │
        ├─ lang 目录（真实 JSON，新增读取：语音台词 id→dlg、静态表外键目标）
        ▼
  RelationIndexBuilder          ← 分析层（Application/Relations）
    ├─ 多类别规则：persona / enemy / abnormality / announcer
    ├─ 交叉关联：audio→voice_text、static→lang、audio→subject
    └─ 每类主体：id 权威来源 + 可挂资源
        ▼
  relation-index.db  v2（新增 preview_text / duration_sec / deep_link / xref 表）
        ├─ RelationQueryService（UI 门面，不变契约）
        ├─ RelationPreviewService（新增：把链接变成"能画/能播/能读"的载荷）
        └─ PresetService（多类别卡片流）
        ▼
  WPF 页面
    ├─ PresetWorkbenchPage：类别切换 + 卡片流 + 详情内联预览
    └─ Assets/Bank/Text/Static：新增 RevealReference（精确跳转）
```

---

## 4. 阶段 0：提交现有基线

当前工作区有 Phase 2/3 全部未提交改动（关联图、卡片流、Spine 结构预览/导出，测试 616+74 全绿）。

- `git add` 全部源码 + 新文件（`Relations/*`、`Spine/*`、`PresetWorkbenchPage.*`、`ISearchableWorkbench.cs`、两个新测试文件）+ `docs/`。
- commit：`feat(relations): 资源关联图 + 人格卡片流 + Spine 结构预览/导出`
- 根目录三个临时文件 `_build_out.txt` / `_probe_out.txt` / `_test_out.txt` **不提交**（构建/测试临时产物，需你确认是否删除）。

---

## 5. 阶段 A：关联库 v2（地基）

改 `cache/relation-index.db` 的 schema（用户已授权），`RelationIndexSource.FormatVersion` → `v2`。

```sql
-- subjects：subject_id 加类别前缀，避免跨类别 id 撞车（播报员 90001 vs 敌人 90001+ 号段）
subjects(
  subject_id    TEXT PRIMARY KEY,   -- 'persona:10201' | 'enemy:91015' | 'abnormality:8159' | 'announcer:gregor_announcer'
  subject_kind  TEXT NOT NULL,      -- persona | enemy | abnormality | announcer
  category_label TEXT,              -- 中文类别名（人格 / 敌人单位 / 异常 / 播报员）
  display_name  TEXT, subtitle TEXT, character TEXT, sort_key TEXT,
  cover_ref     TEXT,               -- 预选封面（省去每页重算）
  preview_text  TEXT,               -- 卡片副文本（代表台词 / 首个技能名）
  link_count    INTEGER NOT NULL DEFAULT 0
);

-- links：加「能预览 / 能跳转」的载荷
links(
  subject_id TEXT, category TEXT, kind TEXT, ref_key TEXT,
  display TEXT, detail TEXT, size_bytes INTEGER,
  preview_text TEXT,        -- 台词原文 / 记录 name·desc / 文件名
  preview_kind TEXT,        -- exact_text | personality_level | none
  media_kind   TEXT,        -- audio | text | static | image | video | spine | other
  duration_sec REAL,        -- 音频时长（sample_count/sample_rate）
  ref_path     TEXT,        -- 读字节/跳转用的真实路径（文本=lang 相对路径，音频=bank 路径）
  deep_link    TEXT,        -- 跳转载荷，'\0' 分隔（见 §8）
  target_subject_id TEXT,   -- 交叉关联指向的其它主体
  PRIMARY KEY(subject_id, kind, ref_key)
);

-- xref：显式跨资源边。**多对多**——一条 from 可对多条 to，一条 to 也可被多条 from 指向
-- （例：一个 lang 键可同时关联到「音频 + 技能 + 图标」；一个音频可同时关联「台词 + 人格 + 关卡」）
xref(
  from_ref TEXT, to_ref TEXT, relation TEXT,
  from_kind TEXT, to_kind TEXT,     -- 便于不 join 也能筛
  confidence TEXT, detail TEXT,
  PRIMARY KEY(from_ref, relation, to_ref)   -- 唯一性只在「同一关系的同一条边」，不限制度数
);
-- relation ∈ audio->voice_text | static->lang | lang->subject | lang->resource | resource->subject
-- confidence ∈ exact | derived | personality_level | chapter_level | ambiguous
```

**为什么 `preview_text` 放进 `links` 而不是每次现读**：卡片流一屏几十张卡、每张几十行，现读 lang/表正文会把 UI 线程打爆；缓存的铁律（只影响速度、不影响正确性）在这里同样成立——删库重建后内容一致。

**验收**：schema 往返测试（写→读→字段一致）；`FormatVersion` 升级后旧库被判定为过期并重建；`subjects_by_ref` 反向索引仍然工作。

---

## 6. 阶段 B：多类别分析器（问题 3）

`PersonaRelationAnalyzer` 泛化为 `SubjectRelationAnalyzer` + 每类别一份**规则**（`ISubjectCategoryRule`：权威来源 / id 校验 / 资源匹配 / 文本匹配 / 音频匹配）。

### 6.1 四个类别的规格（真实规模实测）

| 类别 | 关联键 | 权威来源（id 从哪来） | id 形态 | 真实主体数 | 可挂资源 |
|---|---|---|---|---|---|
| **persona**（现有） | 5 位 id | `Prefab/SD/Personality/Fools_<id>_…`、`PersonalityVideo/<id>.mp4`、`Voice_*_<id>.json` | 5 位 10000–12999 | **185** | 全类别 |
| **enemy**（新） | 数字 id | `Prefab/SD/Enemy/<id>_<名>Appearance.prefab`（去 `Fools_` 前缀） | **变长 4/5 位**，实测 1079–99003 | **239** | 预制体 486 行、立绘 `Sprite/Unit/Portrait/<id>_portrait.png`、技能图标 `<id>01.png`、音频 `enemy` 807、静态 39 表；文本**按关卡**组织 |
| **abnormality**（新） | 数字 id | `Prefab/SD/Abnormality/<id>_<名>Appearance.prefab` | **统一 4 位**，1080–8633 | **119** | 预制体 202、立绘/技能图标、静态 172 表、文本 107（按关卡）；音频仅 8 |
| **announcer**（新） | sprite 基名（`imgStr`） | 静态 `announcer/*.json` 的 `imgStr`（如 `gregor_announcer`）+ `Sprite/BattleAnnouncer/*` | 基名 / id 90001+ | **65 张图** | 图像 65、文本 61 角色 + 4 关卡、音频 `announcer` **1721**、静态 29 表 |

**硬约束（来自真实数据）**：绝不能复用现有「扫 5 位数字窗口」的 `ExtractPersonaIds`。敌人/异常是 4 位 id，会与事件 id、彼此号段混；**类别必须由资源自身的目录前缀判定**（`Prefab/SD/Enemy/`、`Prefab/SD/Abnormality/`），中立路径（`Sprite/Unit/Portrait/<id>_portrait.png`）必须靠"id 属于哪个类别的集合"去歧义。

**待校验的前置项**：敌人 id 集合与异常 id 集合是否有交集。若**有**，中立路径一律标 `confidence='ambiguous'`，卡片上显示「同号资源」折行，不静默二选一。

### 6.2 UI

`PresetWorkbenchPage` 顶部加**类别切换**（人格 / 敌人单位 / 异常 / 播报员，SegmentedControl 风格，复用现有 `Theme.xaml` 样式），切换即重建卡片流；搜索框同时过滤 id / 名称 / 角色。

**验收**：每类一条真实数据冒烟（门控 `LME_RELATION_SMOKE=1`）：persona 185 / enemy 239 / abnormality 119 / announcer 65，且各类都至少有 1 条文本、1 条图像、1 条预制体关联；`subjects_by_ref` 反查命中。

---

## 7. 阶段 C：交叉关联（问题 5）

### 7.1 已验证的三条规则

**① 音频 → 台词原文（精确一对一）**
```
voice_index: lang/ 下 203 个 Voice_*.json 的 dataList[] 建 { id -> (rel_file, desc, dlg) }
if sample.name in voice_index:  preview_text = dlg；confidence = exact
```
实测命中 **4694 / 52826**（smalltalk 99.9%、lobby 100%、battle 35%）；例：`battle_allydead_10201_1` →「这并非最有效率的选择。」、`lobby_morning_10201_1` →「入睡三小时后我就会醒来。…」、`7SV-016` →「你已经可以停止奔跑了。」

**② 静态表 → 文本（数值外键链）**
```
静态表字段值（personalityID / passiveIDList / id）  ──▶  Skills.json / Passives.json 的 dataList[].id  ──▶  name / desc（中文）
```
实测：`passiveIDList:[1020101]` → `Passives.json` → name「分析」、desc「攻击带有负面状态的目标时，使自身造成的伤害+10%。」

**③ 音频时长**：`sample_count / sample_rate`（如 `lobby_morning_10201_1` = 4000 Hz / 276480 → **69.12 s**），无需解码音频即可展示。

### 7.2 降级与呈现

- 精确未命中时降级到**人格级**（样本名内含 5 位人格 id → 该人格全部台词），标 `personality_level`，UI 用浅色标注「同人格语音」而不是伪装成精确。
- 敌人/异常文本按**关卡**组织（`Passives_Enemy-a1c5p1`、`AbnormalityGuides-a1c5p3`），无法按 id 精确挂 → 标 `chapter_level`，UI 明确写「按关卡归属」。
- **图像 → 文本**：本次勘误后 Sprite 清单可用（`Sprite/SkillIcon/<id>01.png`），但「图标 → 技能名」的 id 映射**尚未验证**，列入本阶段的一个探针任务（不达标就不做，不硬凑）。

### 7.3 lang 键覆盖矩阵（问题 8 的核心）

现状只有 3 条规则，`text-index` 里 **362 246 条 hit / 2048 个 lang 文件**中的绝大多数**还没有任何关联**。本阶段的目标不是"再加几条"，而是**先量出覆盖矩阵再补齐**：

1. 按 `text-index.files.rel_path` 的顶层目录 + `hits` 的 `key_path` 形态，把 lang 文件分成若干**家族**（`PersonalityVoiceDlg/`、`Skills.json`、`Passives.json`、`Items`、`Stage*`、`Enemy*`、`Abnormality*`、`Announcer*`、`EGOGift*`、`StoryData/**`(920 文件，体量最大) …）。
2. 每个家族给出**可达的关联规则**（键内嵌 id？值内嵌 id？文件名内嵌 id？外键指向？），以及**该家族覆盖多少条 hit**。
3. 输出一张「家族 × 规则 × 覆盖 hit 数」的矩阵，**明确标出覆盖不到的部分与原因**（例如 `StoryData/**` 体量最大但按剧情脚本组织、无稳定实体键 → 可能只能到"章节级"）。
4. **验收口径**：可关联的 lang hit 占比达到**多数**（矩阵实测后定阈值，写进测试断言），且每条规则都有真实配对样本作证。

**辅助手段**：用《Limbus Company Wiki》（`limbuscompany.fandom.com` / `limbuscompany.wiki.gg`）+ 真实数据互证，锁定各实体的 id 号段与 lang 键命名约定（例如技能 id `personaId*100+skillIndex`、被动 `passiveIDList`、礼物 9001–9814、播报员 id 90001+、EGO 装备 id 20601+）。**wiki 只用于定则与交叉验证，最终以真实数据实测为准**（wiki 版本可能落后于游戏）。


**验收**：把 7.1 的三条规则做成纯函数单测（含真实文本样本作为夹具）；真实数据冒烟断言「`preview_text` 非空的音频链接 ≥ 4000 条」「至少 1 个静态表能解出中文 desc」。

---

## 8. 阶段 D：精确跳转（问题 1）

现状（已核实）：四页的 `ApplySearchKeyword` **只填搜索框 + 触发过滤**，从不选中/展开/滚动；且 `SearchKeywordFor` 用的还是显示文件名、不是精确 `RefKey`。

**新增（非破坏性，保留旧 `ShowWorkbenchSearch`）**：
```csharp
// IWorkbenchHost
void ShowWorkbenchReveal(RelationLink link);

// 可选实现接口（与 ISearchableWorkbench 同风格）
public interface IReferenceRevealable { void RevealReference(RelationLink link); }
```
四页实现（全部复用**已存在**的定位原语，不新造轮子）：

| 页面 | 新方法 | 复用的既有原语 |
|---|---|---|
| Assets | `RevealAsset(containerEntry)` — 切列表视图、按 `ContainerPath` **等值**匹配、选中 + 滚动 | `RestoreListSelection` / `RestoreTreeSelection` / `TreeExpansionState` |
| Bank | `RevealSample(bankPath, sampleName)` — 切样本总表、按 `(BankPath, Name)` 定位 | `_audioGrid.SelectedItem` + `ScrollIntoView`（并参数化改造 `LocateInTree` 以支持下钻到样本叶子） |
| Text | `RevealTextKey(relPath, keyPath)` — 直接复用 | `SelectFileAsync(relPath, keyPath)` → `JsonTreeEditor.SelectPath` |
| Static | `RevealStaticKey(containerEntry, recordKey)` — 选表 + 展开到记录 | `RestoreTreeSelection` + `SelectTableAsync` + `_editor.SelectPath` |

**两个已知坑（必须处理）**：
1. **冷启动竞态**：Text/Static/Bank 首刷在 `Loaded` 里，跳转时数据可能未就绪 → 各 `RevealReference` 先 `EnsureLoadedAsync()`。
2. **不要污染展开态**：定位用的展开 key 走一次性 `TreeExpansionState.Restore`，**不写回** `_expandedTreeKeys` 累积集。

**验收**：单测钉住「旧 `ShowWorkbenchSearch` 只过滤、不选中」不变量；deep-link 载荷解析单测（见下）；手测冷启动首次跳转。

---

## 9. 阶段 E：卡片预览区（问题 2 + 问题 6①）

**卡片正面**：封面 + 标题 + 资源构成（现状）+ **副文本**（`subjects.preview_text`，如代表台词）。

**卡片详情**：每个分组下的行改为**可预览行**，由新的 `RelationPreviewService`（Application 层，无 WPF 依赖）决定呈现形态：

| 行类型 | 内联预览 |
|---|---|
| 音频 | ▶ 试听（复用 bank 试听能力）+ **时长** + **台词原文**（`preview_text`，含 `desc` 小字） |
| 图像 | 缩略图（长边 ≤176，复用 `PresetWorkbenchPage.Decode` 口径）+ 尺寸 |
| 文本 | 文件 + 命中键路径 + **片段** |
| 静态数据 | 表名 + `name`/`desc` 中文 |
| 视频 / Spine | 标记类型 + 「打开」「导出…」 |

**封面修复（问题 6 的头号根因，已证实）**：`IndexByContainerEntry` 在同一 `container_entry` 下有 Sprite/Texture 两条记录时**先到先得**，选到 Sprite 记录 → 用 Sprite 的 pathId 去取 Texture2D → 必然 null → 卡片留白（185/185 人格其实都有可渲染纹理，纯粹是选错记录）。修法：同路径优先保留 `Type == Texture` 的记录。

**验收**：真实数据盲扫——185/185 人格封面解码成功（不再是"部分"）。

---

## 10. 阶段 F：Spine 第三方库（问题 4）

### 10.1 根因（已证实）

1. `SpinePreviewProvider.CanPreview` 硬要求 `Type is Text or Json`，而真实立绘（`Story/StandingModel/*.psb` 8066、`Prefab/SpineIllustPrefab/*.prefab` 123）**全是二进制类型** → provider 永不被选中，落到十六进制。
2. Spine 数据在**二进制 bundle 内**（磁盘上没有任何松散骨架文件），现有链路没有「跟 PPtr 取 json+atlas+png」这一环。
3. 即便解析成功也**画不出**：现有解析器把 mesh 计为不支持，而真实立绘 mesh 占多数（`cg_40.json`：region 18 / **mesh 35**）。

### 10.2 方案

| 步骤 | 内容 |
|---|---|
| F1 | `UnityAssetService` 增 `ReadSpineBundle(asset)` → `(jsonText, atlasText, pngByPage)`，走 PPtr：prefab → `SkeletonDataAsset`(MonoBehaviour) → `skeletonJSON`(TextAsset) + atlasAsset + 页纹理。**已实证**：`SpineIllustPrefab/10103_gacksung.prefab` 能取到 `spine=4.0.64, bones=12, slots=21` |
| F2 | vendoring 官方 **spine-csharp 4.0.x 源码**（NuGet 上的 `Spine` 包是 2016 年的 3.3.0，只支持 .NET Framework 4.0，**读不了 4.0.64**，不可用）→ 新建 `src/LimbusModEditor.SpineRuntime`（`net8.0`，`TreatWarningsAsErrors=false` 仅限该项目），只保留核心 + Attachments，去掉 XNA 渲染层 |
| F3 | 自写 `SkiaSpineRenderer`（`SkiaSharp` NuGet，支持 net8）：`Spine.TextureLoader` 实现 + `RegionAttachment`/`MeshAttachment` 顶点 + `SkeletonClipping` + `atlas pma:true` 混合。约 400–600 行 |
| F4 | `SpineRuntimePreviewProvider : IAssetPreviewProvider`：优先于结构预览；UI 给时间轴 + 播放/暂停（`AnimationState.Update`）+ 当前帧导出。原结构预览作**降级**保留 |

### 10.3 许可证（需要你拍板）

Spine Runtimes License 要求：**产品的每个最终用户都必须自行持有 Spine Editor 授权**，且**任何再分发都要附带许可证与版权声明**（Copyright © 2013–2025 Esoteric Software LLC）。

对公开的 mod 编辑器意味着：必须打包 `THIRD-PARTY-NOTICES.md`、在 UI 明确提示、且不能默认静默启用。**这条决定了 F2 走不走**，见 §12 问题 1。

**降级**：若不做运行时，则强化现状——「导出 json+atlas+png 三件套到磁盘 + 提示用外部查看器打开」（`SpineExportService` 已部分实现）+ 结构预览明确标注「非运行时预览」。

**验收**：`cg_40`（已证可取）渲染出非空位图且尺寸与 `atlas size` 一致；`SpineIllustPrefab/10103_gacksung` 渲染成功；解析器遇到 mesh 不再静默跳过（计入 `AttachmentKindCounts` 并可渲染）。

---

## 11. 阶段 G：预览覆盖修复（问题 6）

| # | 根因 | 状态 | 修法 | 收益 |
|---|---|---|---|---|
| ① | 封面选到 Sprite 记录 → 解码 null | 已证实 | `IndexByContainerEntry` 优先 Texture（见阶段 E） | 185/185 封面 |
| ② | Sprite 预览遇跨 SerializedFile 的 Texture2D 直接抛异常 | 代码路径已证实 | 用 bundle 依赖表跨文件解析；或回退到"单独解码该 Sprite 引用的 Texture2D" | 消除"点 Sprite 出说明卡" |
| ③ | **Component 510,738 行（40%）无 provider** → 整包十六进制 dump，观感就是"没加载" | 已证实 | 新增轻量「对象字段摘要」provider（复用 `ReadBundleObjectFields`），至少显示 `m_Name` + 组件类型 | 57% 对象的预览从整包 hex 变为可读 |
| ④ | 纹理 codec 只支持 DXT1/DXT5 + 未压缩，BC7/ASTC 静默返回 null | 假设（待运行时确认） | 失败时给出**中文原因**并缓存；按需补 BC7 | 消除无声失败 |
| ⑤ | 资源页 `LoadBitmap` 不设 `DecodePixelWidth`，大图原分辨率解码 | 假设 | 对齐卡片页的降采样口径 | 避免 OOM/卡顿 |

**验收**：③ 的 provider 单测；④ 的失败原因文案单测；真实数据统计"预览有内容的对象占比"提升（当前可渲染类型仅 11%）。

---

## 12. 需要你拍板的三个决策

1. **Spine（阶段 F）走哪条？**
   - A｜vendoring spine-csharp + SkiaSharp，做**真运行时预览**，附 `THIRD-PARTY-NOTICES.md` 与 UI 提示（需接受 Spine Runtimes License 的"最终用户自持授权"约束）
   - B｜不引第三方库，改为「导出三件套 + 外部查看器」+ 结构预览（零授权风险）
   - C｜两者都做，运行时预览做成**显式开关**（默认关）
2. **阶段 B 的类别范围？**
   - 只做 敌人 + 异常 + 播报员（本次分析已给出完整规格）
   - 再加 EGO + E.G.O.Gift（分析显示命名碎片化、需专用分析器，**语音 id 与装备 id 不对应**，收益不确定）
   - 先只做敌人（最快见效，验证多类别骨架）
3. **本轮执行范围？**
   - 全部 A–G 一次做完
   - 先做 A/B/C/D/E/G（数据 + 交叉关联 + 跳转 + 卡片预览 + 覆盖修复），F 单独一轮
   - 只做阶段 A + D（地基 + 精确跳转，最小可验证闭环）

---

## 13. 测试与提交策略

- **测试**：所有新逻辑下沉到 `Application`（App 无测试工程）。新增单测：多类别分析器（合成夹具 + 真实 id 形态）、交叉关联三条规则（真实文本作夹具）、schema 往返、deep-link 载荷解析、预览载荷构建、Spine 提取的解析边界。真实数据冒烟沿用 `LME_RELATION_SMOKE=1` 门控，并按类别给出规模断言。
- **不变量（写进 `CODE-STRUCTURE.md` §6）**：缓存只影响速度；`FormatVersion` 变更必须 +1；跨类别 id 必须带类别前缀；中立路径的歧义 id 不得静默二选一；Reveal 不得污染树展开态。
- **提交粒度**：每阶段一个 commit（阶段 0 基线 → A → B → C → D → E → F → G），每个 commit 都必须"构建 0 警告 0 错误 + 全绿测试"。
- **文档**：`PROJECT-INDEX.md`（§8.14/§8.15 扩充、§9.2 页面、§11 计数、§14 症状表）、`CODE-STRUCTURE.md`（目录地图、决策表、不变量）、本文件在完成后移入归档。

---

## 14. 决策记录与追加要求

### 14.1 已拍板

| 决策 | 结论 |
|---|---|
| 阶段 F（Spine） | vendoring 官方 spine-csharp 4.0.x 源码 + 自写 SkiaSharp 渲染器做**真运行时预览**；接受 Spine Runtimes License → 附 `THIRD-PARTY-NOTICES.md` + UI 提示 + 版权声明 |
| 阶段 B 类别 | 敌人 + 异常 + 播报员 + **EGO + E.G.O.Gift** |
| 本轮范围 | **A–G 全部**，每阶段一个 commit |
| 关联基数 | **不限制 1:1** → `xref` 多对多，单个键值可关联多条其它数据 |
| 关联规则覆盖 | 补到**覆盖大部分 lang 键值** |

### 14.2 追加要求（开工后用户补充）

1. **不允许 1:1 限制**：单个键值要能与多条其它数据关联 → 见 §5 `xref` 多对多设计。
2. **关联规则太少**：要能覆盖**大部分 lang 键值** → 见 §7.3 覆盖矩阵；矩阵数字要进测试。
3. **参考《Limbus Company Wiki》+ 真实数据搜索**来辅助确定关联 → wiki 仅用于**定则与交叉验证**，最终以真实数据实测为准。

### 14.3 覆盖矩阵实测结果（新增分析）

`text-index` 2048 个 lang 文件 / 362 246 条 hit 的家族与可关联性（详见 `logs/analysis/lang-coverage-matrix.md`）：

| 结论 | 数字 |
|---|---|
| 可被现有 + 新增规则关联的 hit | **≈43.9%** |
| 唯一的结构性盲区 | **`StoryData/**`（约 55.9% 的 hit）**，实测 200 个文件里只有 353 个不重复 id —— 用**局部节点序号** 0..N，**没有稳定实体键** |
| 其它零散盲区 | ≈0.2%（`BattleHint`、车站名等纯 UI 局部 id） |

**重要反例（已在分析中记录，实现时不得当事实用）**：
- 「lang 值指向另一个 lang 键」这一设想**不成立**：全量扫描 Value 命中能精确等于其它文件键的 = **0 例**；12297 条"值==id"几乎全是 StoryData 局部序号 0..7 的**号段碰撞假阳性**。
- 2047/2048 个 lang 文件是 `{"dataList":[...]}` 结构（唯一例外 `Info/version.json`）。
- 该分析另称「人格 id 实测用 400000+ 而非 10000–12999」——与 `relation-index.subjects`（185 个人格，5 位 id）及 wiki 互证结果冲突，**判为错误**（疑似把技能/被动 id 当人格 id），**不采用**。

**因此对目标口径的修正**：`StoryData/**` 只能做到**角色级 / 章节级**关联（按文件名里的角色 token 挂到该角色，与现有 `AbDlg_<角色>.json` 同法）。
「覆盖大部分 lang 键值」的验收口径定为：**除 `StoryData/**` 外，可关联 hit 占比达到多数**，且 `StoryData/**` 至少有角色级/章节级关联。

### 14.4 wiki 交叉验证的关键结论（详见 `logs/analysis/wiki-id-spec.md`）

- **wiki 完全没有数字 id 体系**（只记名称/风险等级/分类码）→ **号段只能以真实数据为准**。
- 技能 / 被动 id = `personalityId * 100 + 序号`；EGO 技能再加 `+11`（已觉醒）/`+21`（未觉醒）。
- E.G.O.Gift 4 位基础 id（1001–9999）+ 增强偏移 `+10000/+20000` → 5 位（`EGOgift_*.json` 全量 857 条）。
- EGO id `2` + 角色码 + 变体；**罪孽属性不在 id 内**（独立字段）。
- 异常：wiki 用字符串分类码（`F-04-03-04`），与游戏 4 位数字 id **是两套体系，不能互转**。
- 冲突项：wiki 的 Sinner 编号与游戏数据不一致（`11201`=Gregor=码 12）→ **以数据为准**。

