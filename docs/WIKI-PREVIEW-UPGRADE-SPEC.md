# 维基预览页升级规格（采样 + 字段可得性 + 布局蓝图）

> 状态：**草稿，逐步填充中**。每完成一节即写回本文件。
> 原则（需求方口径）：**只显示本地能推导出来的数据**；推不出来就不渲染、不占位、不编造；禁止 id 数字窗口猜测。
> 本轮只新增文档与截图，不修改 `src/` `tests/` 与既有文档。

## 1 采样清单

（待填）

## 2 逐类信息清单（人格/EGO装备/EGO饰品/敌人/异想体/播报员/剧情/关卡/物品/机制/关键词/CG/分类/模板）

（待填）

## 3 字段 → 本地来源对照表

### 3.1 本地数据源实测（本节数字均为本轮 `sqlite` 实测，非文档转述）

路径根：`E:\desktop\work\LimbusModEditor\artifacts\publish-win-x64\cache\`

| 文件 | 大小 | 表 | 行数 | 列 |
| --- | --- | --- | --- | --- |
| `wiki-pages.db` | 54.9 MB | `pages` | **2074** | `page_id, category, title, subtitle, sort_key, cover_ref` |
| | | `sub_pages` | **10082** | `sub_page_id, page_id, title, sort_order` |
| | | `entries` | **75166** | `entry_id, sub_page_id, title, body, sort_order, source, candidate_id, authority, confidence, writable_source, writable_source_path, source_detail` |
| | | `resource_bindings` | **62241** | `binding_id, entry_id, ref_key, kind, display, sort_order, deep_link, preview_text, media_kind, duration_sec` |
| `relation-index.db` | 80.5 MB | `subjects` | **1313** | `subject_id, subject_kind, category_label, display_name, subtitle, character, sort_key, cover_ref, preview_text, link_count` |
| | | `links` | **62970** | `subject_id, category, kind, ref_key, display, detail, size_bytes, preview_text, preview_kind, media_kind, duration_sec, ref_path, deep_link, target_subject_id` |
| | | `xref` | **36597** | `from_ref, to_ref, relation, from_kind, to_kind, confidence, detail` |
| | | `subjects_by_ref` | 62121 | `ref_key, subject_id, category` |
| | | `index_meta` | 1 | `source_key, signature, language_prefix` |
| `static-tables.db` | 450 KB | `tables` | **1392** | `container_entry, name, data_class, serialized_file, path_id, size_bytes, is_utf8, cached_ticks` |
| | | `documents` | **0**（空） | `container_entry, text, size_bytes` |
| `bank-index.db` | 22.7 MB | （待补充） | | |
| `text-index.db` | 45.0 MB | （待补充） | | |
| `unity-cache-index.db` | 283.6 MB | （待补充） | | |

`wiki-pages.db` 分类分布（实测）：story 765 / ego_gift 576 / enemy 239 / persona 201 / abnormality 119 / ego 112 / announcer 62 —— **共 7 类，无「关卡/物品/机制/关键词/CG/分类/模板」类页面**（这些类别若规格要求，属「本地没有」或需派生）。

`resource_bindings.kind` 分布（实测）：StaticData 33026 / Audio 12965 / Image 11822 / Text 2343 / Prefab 1447 / Video 357 / **Spine 134** / Mesh 92 / Animation 54 / Other 1。

### 3.2 ⚠ 正文可得性的实测结论（决定一切）

对 `entries.body` 全量统计：`总 75166 / 空串 22635（30.1%）/ 平均长度 28.1 字符 / 最长 8926`。
高频 body 值（实测 top）：`''` 22635、`'1 句对白'` 1540、`'2 句对白'` 1031、`'2291 条可解析文本'` 861、`'95 条可解析文本'` 775、`'3 句对白'` 760……

**结论：绝大多数条目的 `body` 是「计数占位串」，不是正文。** 逐类实测样例：

| 类别 | 样例页 | 分节 | 条目 body 实测 |
| --- | --- | --- | --- |
| persona | `persona:1`（EGO · YiSang） | 概览 | `摘要` → `'54 条可解析文本'`（占位） |
| ego | `ego:20101`（乌瞰刀） | 概览/数据/文本 | `'乌瞰刀'`（=标题回显）、`'45 条可解析文本'`（占位） |
| enemy | `enemy:1079`（Sancho） | 概览/数据/语音 | `'796 条可解析文本'`（占位）；语音条目 `S703V-1079` body **空** |
| abnormality | `abnormality:1080` | 概览 | 摘要**为真文本**：「早上总是很安静。出乎意料的没有路人，是个适合暗杀的时间段。」 |
| ego_gift | `ego_gift:1001`（信徒面具） | 概览/数据/文本 | subtitle 为真效果文本；数据节为占位；文本节多条 body **空** |
| **story** | `story:1D101` | 概览/章节导航/**章节正文** | **真实台词**：「格里高尔点上了烟，比平时更长久地吐出了烟气。／이스마엘：哪怕他们在其他安保公司就职……」（217 字符） |
| announcer | `announcer:3detectives_announcer` | 立绘与图集 | 唯一条目 body **空** |

> 这与上一轮验收报告的 D-4（「标题写着 N 条事实、正文 0 字符」）互为印证：**根因是生成链路把事实写成了计数/标题回显，未落成正文**。因此按需求方口径，除 story 类（及 abnormality 摘要、ego_gift subtitle 等少数真文本字段）外，**正文当前不可直接展示**，须先修生成器，或按「推不出来就不渲染」处理。

### 3.3 字段 → 来源对照（三选一：有真实来源 / 只能派生 / 本地没有）

图例：**A**=有真实来源（写明表/文件） / **B**=只能派生（写规则+置信度） / **C**=本地没有（不显示）

| 维基字段 | 判定 | 本地来源 / 派生规则 | 置信度 |
| --- | --- | --- | --- |
| 页面 id | A | `wiki-pages.pages.page_id`（形态实测：`persona:1`、`ego:20101`、`enemy:1079`、`story:1D101`、`announcer:3detectives_announcer`） | 高 |
| 标题 | A | `pages.title` | 高 |
| 副标题 | A | `pages.subtitle`（部分为空，如 `story:1D101`、`enemy:1079`） | 高 |
| 类别 | A | `pages.category`（7 类） | 高 |
| 封面图 | A（待核） | `pages.cover_ref` + `resource_bindings(kind='Image')` 11822 条 | 中 |
| 分节列表 | A | `sub_pages`（10082） | 高 |
| 分节标题 | A | `sub_pages.title` | 高 |
| 条目列表/标题 | A | `entries.title`（75166） | 高 |
| **条目正文** | **C（当前）** | 见 §3.2：多为计数占位/空；仅 story 章节正文与少数摘要为真 | — |
| 剧情台词 | A | `story:1D101` 章节正文实测为真；底层 `LimbusCompany_Data/lang/LLc-CN-LCTA/StoryData/*.json` 待核实 | 高（页内已落地） |
| 资源绑定（图/音/视频/Spine） | A | `resource_bindings`（62241，`ref_key`/`kind`/`media_kind`/`duration_sec`/`deep_link`） | 高 |
| 音频（语音） | A | `resource_bindings.kind='Audio'` 12965 条，含 `duration_sec` | 高 |
| Spine 动画 | A（量少） | `resource_bindings.kind='Spine'` **仅 134 条** | 高（覆盖低） |
| 语音文本（对白内容） | B | `entries.body` 形如 `'N 句对白'` 仅为计数 → 需从 lang/StoryData 反查，规则待定 | 低 |
| 数值面板（体力/攻击/防御/速度） | B/C（待核） | 可能来自 `static-tables.db` 1392 表（需抽样确认含人格属性表）；未确认前标 C | 待确认 |
| 技能表（技能名/威力/硬币/条件） | C（待核） | 未见明确来源；`static-tables.db` 需进一步抽样 | 待确认 |
| 相关页面 / 交叉链接 | A | `relation-index.db.xref`（36597）+ `links.target_subject_id` | 中高 |
| 关键词 / 机制 / 关卡 / 物品 / CG / 分类 / 模板 | C | `wiki-pages.db` 无这些 category | — |

（lang 目录、Spine 资源、FMOD bank 的实测补充待 worker 回报后填入本节）



## 4 布局蓝图

> **本节结构来自本地数据模型**（`wiki-pages.db` 的 `pages → sub_pages → entries → resource_bindings`，实测 2074/10082/75166/62241），不是照抄站点。
> **观感细节（间距/圆角/层级/色彩用量/表格密度/图片比例）待 §2 采样校准**，采样不可达时保持本节的"结构确定、观感待定"状态，**不臆造**。

### 4.1 实体页（人格 / E.G.O 装备 / E.G.O 饰品 / 敌人 / 异想体 / 播报员）通用骨架

```
┌──────────────────────────────────────────────────────────────┐
│ WikiShell 顶栏：面包屑 · 搜索框[G-8 待接线]                    │
├───────────┬──────────────────────────────────────────────────┤
│ 侧栏      │ <h1> 标题                                         │
│ WikiToc   │ 副标题 (pages.subtitle)                           │
│ ├ 概览    │ 标签行 WikiTagList [待后端补 Tags]                 │
│ ├ 数据    ├───────────────────────────┬──────────────────────┤
│ ├ 语音    │ 主体（分节流）             │ 右栏 WikiInfoboxCard  │
│ └ 图集    │ ┌ WikiSectionBlock ─────┐ │ ┌──────────────────┐ │
│           │ │ 分节标题 (sub_pages)  │ │ │ 封面 (cover_ref) │ │
│           │ │ ├ entry.title         │ │ │ label : value    │ │
│           │ │ │   body  ← 真文本    │ │ │ label : value    │ │
│           │ │ └ bindings → 媒体     │ │ └──────────────────┘ │
│           │ └───────────────────────┘ │ [WikiTabGroup 待定]  │
│           │ ┌ WikiAudioPlayer ──────┐ │                      │
│           │ │ 语音 12965 条可用     │ │                      │
│           │ └───────────────────────┘ │                      │
│           │ ┌ WikiGallery ──────────┐ │                      │
│           │ │ 图集 11822 条可用     │ │                      │
│           │ └───────────────────────┘ │                      │
├───────────┴───────────────────────────┴──────────────────────┤
│ WikiRelatedList（xref 36597）· WikiSourceBadge（来源标注）     │
└──────────────────────────────────────────────────────────────┘
```

区块顺序（自上而下）：标题 → 副标题 → 标签 → **右栏信息框（与主体并排）** → 分节流 → 音频 → 图集 → 相关页面 → 来源标注。
栅格：**主体 : 右栏 ≈ 7 : 3**（右栏固定宽度信息框 + 封面）；窄屏（<1024px）右栏下沉到标题下方。

### 4.2 剧情页（story，765 页）专用骨架

```
┌──────────────────────────────────────────────────────────────┐
│ WikiShell 顶栏：面包屑（剧情 / 章节键）· 搜索                  │
├───────────┬──────────────────────────────────────────────────┤
│ WikiToc   │ 章节键 <h1> · 第 n/N 章                           │
│ ├ 概览    │ ── 章节导航 ─────────────────────────────────────  │
│ ├ 导航    │   上一章 ← · → 下一章                              │
│ ├ 正文    │ ── 章节正文（WikiStoryDialog）────────────────────  │
│ └ 角色    │   说话人：台词                                     │
│           │   说话人：台词   ← 实测为真文本（story:1D101）     │
│           │ ── 登场角色 ────────────────────────────────────   │
│           │   角色名（N 句）→ 待改为真台词摘录（P0 轮次）      │
└───────────┴──────────────────────────────────────────────────┘
```

剧情页**不用右栏信息框**（本地无剧情的数值面板来源），改为单栏 + 宽对话区。

### 4.3 必须复刻的观感要素（待采样校准）

| 要素 | 当前状态 | 待校准项 |
| --- | --- | --- |
| 间距 / 圆角 | `tokens.css` 139 行**无维基专用变量**，各组件各写各的 | 采样后定一套间距梯度与圆角值 |
| 层级（z-index） | **无 z-index 令牌** | 目录吸顶、悬浮卡、播放器浮层的层级顺序 |
| 色彩用量 | 未定 | 类别色（7 类）与强调色占比 |
| 表格密度 | 无表格组件（G-5） | 行高/斑马纹/表头样式 |
| 图片比例 | 画廊网格已存在但比例未定 | 封面与图集的宽高比 |
| 悬浮卡 / tooltip | **完全没有**（G-11） | 是否需要，取决于 §2 采样 |



## 5 前端组件清单

数据来源列：`wiki-pages.db` 的 `pages/sub_pages/entries/resource_bindings`；后端 IPC 为 `wiki.getPage`（P1 轮次已收敛，请求 `{ pageId }`，响应为页面对象本身）。图片一律走既有 `lme.data` 虚拟主机地址，**禁止 base64**。

| 组件 | 职责 | 主要 props | 数据来源（字段 / IPC） |
| --- | --- | --- | --- |
| `WikiShell` | 维基页外壳：顶栏 + 侧边目录 + 内容槽 + **搜索框** | `title`, `category`, `breadcrumb`, `toc`, `searchQuery` | 页面级；搜索框接 `wiki.search`（G-8） |
| `WikiInfoboxCard` | 右上角信息框：字段行（label/value）+ 封面图 | `cover`, `rows[{label,value}]` | `WikiPageDto.Infobox` + `Cover`（后端补，G-6） |
| `WikiToc` | 目录锚点（含层级、当前高亮） | `items[{id,title,level}]` | `WikiPageDto.Toc`，或前端按 `sub_pages` 生成（G-7，二选一待定案） |
| `WikiTabGroup` | 分节分组切换（技能/被动/语音/图集/剧情） | `tabs[{key,title}]`, `modelValue` | 按 `sub_pages.title` 语义分组（G-1） |
| `WikiSectionBlock` | 单个分节：标题 + 条目列表 + 折叠 | `section{id,title,entries,bindings}` | `WikiSectionDto.Entries` / `.Bindings` |
| `WikiStatTable` | 数值面板（体力/攻击/防御/速度等） | `rows[{label,value,unit}]` | **数据待确认**（§3.3 标 待确认）→ 无数据时不渲染 |
| `WikiSkillTable` | 技能表（技能名/威力/硬币/条件） | `skills[]` | **数据待确认**（§3.3 标 待确认）→ 无数据时不渲染 |
| `WikiAudioPlayer` | 语音列表 + 单条播放 + 时长 + 关联台词 | `items[{refKey,display,durationSec,url,caption}]` | `resource_bindings.kind='Audio'`（12965 条，`duration_sec`）+ 台词来自 lang 锚点 |
| `WikiSpineViewer` | Spine 动画预览 | `skelUrl`, `atlasUrl`, `animation` | `resource_bindings.kind='Spine'`（**仅 134 条**）；复用现有 `SpineRenderer.vue`（253 行，当前零引用，G-3） |
| `WikiGallery` | 图集网格（立绘/图标/图集） | `images[{url,caption,kind}]` | `resource_bindings.kind='Image'`（11822 条）；地址走 `lme.data` |
| `WikiStoryDialog` | 剧情对话渲染（说话人 + 台词 + 章节导航） | `lines[{speaker,content}]`, `nav{prev,next}` | `entries.body`（story 类，实测为真台词） |
| `WikiTagList` | 标签（类别/派系/章节等） | `tags[]` | `WikiPageDto.Tags`（后端补） |
| `WikiRelatedList` | 相关页面 | `pages[{id,title,category,url}]` | `relation-index.db.xref` / `links.target_subject_id`（取不到给空数组，不猜 id） |
| `WikiSourceBadge` | 来源/权威标注（`writable_source` / `authority` / `confidence`） | `entry` | `entries.writable_source` / `authority` / `confidence`；承接 §7.3 的迁移能力 |

补充：
- **设计令牌**：`styles/tokens.css`（139 行）需补 z-index 层级与维基专用变量（间距/圆角/表格密度/图片比例）。
- **未接线组件**：`SpineRenderer.vue`（253 行）→ 接线为 `WikiSpineViewer`；`WikiEditor.vue`（299 行）无引用 → 若无编辑入口则删除。



## 6 与现状的差距（带文件:行）

> 本节事实由需求方核实后提供（2026-09-17 轮次），行号为提供当时的位置。

### 6.1 现有维基前端「有什么」

| 文件 | 规模 | 现状 |
| --- | --- | --- |
| `src/LimbusModEditor.Web/src/views/WikiEntityPage.vue` | 886 行 | 唯一的实体页渲染器，含：标题 + 信息框 + TOC + 相关页 + 可折叠分节 + 画廊网格 + 标签 |
| `src/LimbusModEditor.Web/src/components/WikiShell.vue` | — | 布局外壳；`:62-64` 声明了 `searchQuery` / `searchLoading` / `onSearchInput()`，但模板**没有 `<input>`** → 搜索是死代码 |
| `src/LimbusModEditor.Web/src/views/WikiSearchView.vue` | 72 行 | 极薄，且**未套用 `WikiShell`**（与其它维基页观感不一致） |
| `src/LimbusModEditor.Web/src/components/SpineRenderer.vue` | 253 行 | **全项目零引用**（写了但没人用） |
| `src/LimbusModEditor.Web/src/views/WikiEditor.vue`（或 `components/WikiEditor.vue`） | 299 行 | **孤儿组件**，无路由/无引用 |
| `src/LimbusModEditor.Web/src/views/BankView.vue` | — | 音频播放仅 `:566-575` 的原生 `<audio>`，无波形/无列表/无下载/无与维基的联动 |
| `src/LimbusModEditor.Web/src/views/StaticView.vue` | — | 无独立 Tab 组件；`:607-621` 是**内联三按钮**的临时实现 |
| `src/LimbusModEditor.Web/src/styles/tokens.css` | 139 行 | **无 z-index 层级变量、无 wiki 专用变量**（间距/圆角/表格密度靠各组件各写各的） |

### 6.2 逐条差距（"太简陋"具体缺什么）

| # | 差距 | 证据位置 | 需要补什么 |
| --- | --- | --- | --- |
| G-1 | **无 Tab 分组**：人格的「技能 / 被动 / 语音 / 立绘 / 剧情出场」只能堆成长列表 | `WikiEntityPage.vue`（886 行全是可折叠分节）；无 Tab 组件，`StaticView.vue:607-621` 只有内联三按钮 | 抽 `WikiTabGroup`，实体页按分节语义分组 |
| G-2 | **无音频播放器**：12965 条 Audio 绑定（`resource_bindings.kind='Audio'`，含 `duration_sec`）当前**完全不展示** | `BankView.vue:566-575` 只有原生 `<audio>`；`WikiEntityPage.vue` 无音频块 | `WikiAudioPlayer`（列表 + 单条播放 + 时长 + 关联台词文本） |
| G-3 | **无 Spine 预览**：134 条 Spine 绑定不展示，且现成组件没人用 | `SpineRenderer.vue` 253 行全项目零引用 | 接线成 `WikiSpineViewer`（覆盖低：仅 134 条，按口径"有才显示"） |
| G-4 | **无剧情专用视图**：剧情页（765 页）是最有真文本的一类，却复用实体页模板 | `WikiEntityPage.vue` 无对话渲染 | `WikiStoryDialog`（说话人 + 台词 + 章节导航） |
| G-5 | **无统计/技能表组件**：数值面板与技能表缺本地来源（§3.3 标 待确认），即便有数据也无组件承载 | 无 `WikiStatTable` / `WikiSkillTable` | 组件先备好，数据到位即接 |
| G-6 | **信息框能力弱**：仅渲染前端 TS 里那几个字段，后端 `WikiPageDto` 无 `infobox/tags/gallery`（见 §3.3 与 P1 报告） | `IpcDtos.cs` 的 `WikiPageDto` 只有 `Id/Title/Category/Subtitle/Sections/RelatedPages` | 后端补 `Infobox/Tags/Gallery`，前端 `WikiInfoboxCard` 承载 |
| G-7 | **TOC 是前端兜底**：后端不给 `toc`，前端按分节自动生成 | `WikiEntityPage.vue` 的 `toc` computed 有 fallback | 可接受；或后端按分节输出（二选一，需定案） |
| G-8 | **搜索是死功能**：`searchQuery`/`searchLoading`/`onSearchInput()` 声明了但模板没渲染 | `WikiShell.vue:62-64` | 渲染输入框并接事件；`WikiSearchView.vue`（72 行）需套 `WikiShell` 统一观感 |
| G-9 | **孤儿/未接线组件**：`SpineRenderer.vue`（253 行）、`WikiEditor.vue`（299 行）零引用 | 全项目 grep 无引用 | 接线或删除，不留死代码（见 §7 同一处理口径） |
| G-10 | **设计令牌不完整**：无 z-index 层级、无 wiki 专用变量 | `tokens.css` 139 行 | 补层级与维基专用令牌（间距/圆角/表格密度/图片比例） |
| G-11 | **无折叠/悬浮卡语义**：可折叠分节存在，但 tooltip/悬浮卡（维基常见）完全没有 | `WikiEntityPage.vue` | 视采样结果决定是否需要（见 §2） |

## 7 要移除的旧关联页面

> 需求方口径：维基页面**已取代**早先的「关联/关系」类页面与入口，残留应清理。**本节只列方案，不改代码。**

### 7.1 现状（已核实）

- **前端没有任何 `relation` 路由**（`src/router/index.ts` 内无 relation/关联/图谱相关 path）。
- **唯一**还存在的关联 UI 是 `src/LimbusModEditor.Web/src/views/PresetsView.vue`：
  - 路由 `/presets`，1003 行
  - 形态：左栏卡片网格 + 右栏「关联资源详情」，IPC 调用 `relation.reveal`
  - 导航入口：`App.vue:9-20`
  - `WikiShell.vue:34-47` 的维基导航里**没有**关联入口（即维基侧已不依赖它）

### 7.2 移除方案

| 项 | 文件 / 位置 | 现状价值 | 方案 |
| --- | --- | --- | --- |
| 预设页 | `views/PresetsView.vue`（1003 行） | 承载「左栏卡片流 + 右栏关联资源详情」，是**唯一**还在暴露关系视角的页面 | **移除页面**（其能力已由维基实体页的「分节 + 资源绑定 + 相关页面」覆盖）。移除前确认：右栏的 `relation.reveal` 详情是否有维基尚未覆盖的独占信息（如可写出处/权威标注）→ 若有，先迁到维基页的「来源标注」区块再删 |
| 路由 | `/presets` | 同上 | 随页面一并删除；如暂不删，至少从主导航摘除并标注 deprecated |
| 导航入口 | `App.vue:9-20` | 指向已废弃视角 | 删除该菜单项 |
| 后端 IPC `preset.*` | `IpcGateway` 中 `preset.*` 系列 | 若只服务预设页 → 无其它消费者 | **保留后端**，先只删前端入口；待确认无其它调用方（含 CLI/测试）后再删。理由：后端方法删除属破坏性改动，需单独一轮 |
| 后端 IPC `relation.*` | `relation.reveal` 等 | **维基生成链路依赖**（`relation-index.db` → `wiki-pages.db`） | **必须保留**，不得移除 |
| store | `stores/` 下 preset/relation 相关 | 视页面删除而定 | 随页面删除；`relation` 相关 store 若被维基用到则保留 |
| 孤儿组件 | `SpineRenderer.vue`（253 行）、`WikiEditor.vue`（299 行） | 零引用 | 与 §7 同一口径：**接线或删除**，不留死代码。建议本轮先接线 `SpineRenderer`（G-3 需要它），`WikiEditor` 若维基暂无编辑入口则删除 |

### 7.3 移除前必须先迁移的能力

1. **`relation.reveal` 展示的「资源 → 出处/权威标注」**：维基页当前未展示 `writable_source` / `authority` / `confidence`（`wiki-pages.db.entries` 有这些列）。迁到维基页的「来源标注」折叠块后再删预设页。
2. **卡片流的「快速跳转」**：维基已有搜索（G-8 修好后）与相关页面，可替代。
3. **`cover_ref` 封面预览**：维基画廊可用 `resource_bindings(kind='Image')` 11822 条覆盖。



## 8 实现优先级建议

排序依据：**先让"能显示的数据真的显示出来"，再谈观感复刻**；本地没有来源的一律不渲染（§3.3 的 C 类）。

| 优先级 | 事项 | 依赖 | 说明 |
| --- | --- | --- | --- |
| **P0** | 消灭"计数占位正文"：生成器用 lang 锚点真文本替换 `N 条可解析文本` / `N 句对白`；取不到真文本的条目**不落库** | 无 | **决定一切观感的前提**。当前 75166 条目中 22635 空 + 41213 占位，正文几乎不可用（§3.2） |
| **P0** | 后端 DTO 补齐 `Infobox / Tags / Gallery / Cover`，`WikiSectionDto` 补 `Entries / Bindings`；收敛 `wiki.getPage` 别名与信封 | 无 | 前端在渲染但后端没有 → 信息框/画廊/标签永远为空 |
| **P1** | `WikiAudioPlayer`：12965 条 Audio 绑定（含 `duration_sec`）当前**完全不可见** | P0 的 `Bindings` | 覆盖面大、本地数据确定可得，性价比最高 |
| **P1** | `WikiGallery` + 封面：11822 条 Image 绑定，走 `lme.data` | P0 的 `Gallery` | 同上 |
| **P1** | `WikiStoryDialog`：剧情 765 页，**唯一已有真台词**的一类 | P0 正文 | 先做剧情页能立刻看到"像维基"的效果 |
| **P1** | `WikiShell` 搜索框接线 + `WikiSearchView` 套 `WikiShell` | 无 | 死代码修复，成本极低 |
| **P2** | `WikiTabGroup` 分组 + `WikiInfoboxCard` 字段扩充 + `WikiTagList` | P0 DTO | 观感提升；需 §2 采样结果校准分组语义 |
| **P2** | `WikiSpineViewer`（复用 `SpineRenderer.vue`） | P0 `Bindings` | 仅 134 条，覆盖低但有则显示 |
| **P2** | `tokens.css` 补 z-index 与维基专用令牌 | 无 | 为 P3 观感复刻打底 |
| **P3** | `WikiStatTable` / `WikiSkillTable` | **数据仍需核实**（`static-tables.db` 1392 表待抽样确认含人格属性/技能表） | 数据不到位就**不渲染**，组件可先备 |
| **P3** | 按 §2 采样结果复刻观感细节（间距/圆角/表格密度/图片比例/悬浮卡） | 采样 | 未采样前不动，避免臆造 |
| **P3** | §7 移除旧关联页面（`/presets`、`App.vue:9-20` 入口） | 先把 `relation.reveal` 的「来源标注」迁到维基页 | 破坏性改动，单独一轮 |
| **P3** | 孤儿组件处理：接线 `SpineRenderer` / 删除 `WikiEditor`（299 行） | 无 | 不留死代码 |

**不建议现在做**：关卡/物品/机制/关键词/CG/分类/模板 这些类别 —— `wiki-pages.db` 实测只有 7 类（story/ego_gift/enemy/persona/abnormality/ego/announcer），这些类别**本地没有来源**，按需求方口径不显示。


