# 前端设计重构规格（UI-REDESIGN-SPEC）

> 版本：r1 ｜ 日期：2026-09-18 ｜ 基线 commit：`2bfac78`
> 性质：**设计语言 + 版式规格**，供重构落地与复制到其余页面。
> 范围：仅 `src/LimbusModEditor.Web/`（Vue 3 + TS，宿主为 WebView2 桌面应用）。
> 证据规则：本文所有取值要么来自**真实维基在线观测**（见 §1 与 §7 观测方法），要么来自**当前代码的文件:行**。不确定项一律标「待确认」，不编造。

---

## 0. 怎么读这份文档

| 章节 | 用途 |
| --- | --- |
| §1 设计语言基线 | 照抄取值：色彩/排版/间距/圆角/海拔/动效/态。**色值只能写进 `src/styles/tokens.css`** |
| §2 现状审计 | 逐条问题 + 文件:行，作为验收清单 |
| §3 信息架构与外壳 | 三套候选 + 推荐方案 + 切换/搜索/维基共存策略 |
| §4 版式模式库 | 6 种模式 + ASCII 线框 + 13 视图归属（保留/重排/重做） |
| §5 组件与依赖 | 组件清单、props 草案、依赖取舍（含体积代价） |
| §6 落地计划 | P0→P3 阶段、验收标准、风险 |
| §7 附录 | 观测命令、证据、参考截图 |

---

## 1. 设计语言基线

### 1.1 参考源与观测结果（真实维基，非推测）

观测目标：`https://limbuscompany.huijiwiki.com`（灰机 wiki，MediaWiki + 自定义皮肤 `client-js`）
观测方式：Playwright + 本机 Chrome，视口 1440×1000，取 `getComputedStyle` 实测值（脚本见 §7.1）。
观测页面：`/wiki/首页`、`/wiki/浮士德`（实体页，含信息框与分节）。

**实测取值（这是本规格的权威依据）：**

| 类别 | 元素 | 实测值 |
| --- | --- | --- |
| 底色 | 全站背景层 `.huiji-css-hook` | 背景图 `Background-max.jpg`（`background-size: cover`）+ 基色 `rgb(17,17,17)` |
| 底色 | 内容容器 `.container.wiki-body` | **`rgba(35,35,35,0.92)`**（半透明深灰覆在背景图上，形成「玻璃感」）、**宽 1200px** |
| 强调色 | CSS 变量 `--detail-color` | **`#f2cb95`**（暖金） |
| 强调色 | 主按钮 `.download-btn` | 底 `rgb(247,187,00)`、文字 `#fff`、高 39px、`radius 5px`、`padding 8px 20px` |
| 排版 | `h1` 页面标题 | **48px / 行高 52.8px(≈1.1) / 字重 500 / 色 `#f2cb95`** |
| 排版 | `h2` 分节标题 | **30px / 33px(1.1) / 500 / 色 `#f2cb95` / `padding: 50px 0 15px`** |
| 排版 | 正文段落 | **16px / 28.8px(1.8) / 400 / 色 `#ffffff`**；首段引言 17.5px / 31.5px(1.8) |
| 排版 | 正文链接 `a` | 14px / `rgb(226,192,22)`（首页 `rgb(255,255,42)`，同为高亮黄） |
| 排版 | 目录 TOC | 宽 **193px**、16px、`position: relative`（**不吸顶**，待确认是否为皮肤默认） |
| 信息框 | `.infobox` | **固定 300px**、底 `rgb(46,36,23)`（深棕金）、正文 14px / 25.2px(1.8)、标题 `th` 16px / 28.8px / **700** / 深色字压金底 |
| 布局 | 内容列宽 | **937px**（1200 容器内减去 193px TOC 与内边距） |
| 布局 | 页头 | `page-header` **86px**（站点条 30px + 主头） |
| 节奏 | 分节之间 | `h2` 上内边距 **50px**、下 15px —— 分节间距远大于行距，是「维基呼吸感」的来源 |

**提炼成可复用的设计语言（5 条）：**

1. **深色底 + 暖金强调的单强调色体系**：底色只有「背景图 → 半透明深灰容器 → 卡片」三层，强调色只有一个金色 `#f2cb95`，链接/标题/按钮同源不同亮度。**没有第二种强调色**（这点与我们现状「工作台紫 `--lme-accent` + 维基金 `--wiki-accent` 并存」直接冲突，见 §2.3）。
2. **超大标题 + 无卡片正文流**：h1 48px、分节 30px，正文直接铺在容器上，**分节不被包成卡片**（我们用卡片包分节，是"像后台不像维基"的主因，§2.2 问题 W-3）。
3. **宽行距（1.8）+ 大分节间距（50px）**：行距 1.8 配合 937px 行宽（约 60 中文字符/行），分节之间 50px 留白。
4. **信息框固定 300px、压金底深字**：右栏卡片是"数据盒"而非"卡片壳"，标题栏是实心金底黑字。
5. **圆角极小、阴影几乎不用**：按钮 `radius 5px`，信息框/容器 `radius 0`，全站未测到 `box-shadow` 卡片阴影 —— 层级靠**底色差与边框**而非阴影。

### 1.2 建议取值（落到本项目）

> 说明：本项目是**桌面生产工具**（信息密度高于阅读型维基），因此**不照抄**维基的 48px 标题与 1.8 行距，而是在「维基观感」与「工具密度」之间取折中，并把差值写清楚。

#### 排版比例（建议写进 tokens.css）

| 令牌 | 现有 | 建议 | 说明 |
| --- | --- | --- | --- |
| `--lme-font-size-xs` | 11px | **11px**（保留） | 仅用于角标/序号 |
| `--lme-font-size-sm` | 12px | **12px**（保留） | 表头、次要说明 |
| `--lme-font-size-md` | 13px | **13px**（保留，全局基准） | 桌面工具基准，高于维基但考虑密度 |
| `--lme-font-size-lg` | 15px | **15px**（保留） | 卡片标题 |
| `--lme-font-size-xl` | 18px | **18px**（保留） | 页面/分区标题 |
| `--lme-font-size-2xl` | 22px（r1 已加） | **22px** | 工作台页面主标题 |
| `--lme-font-size-3xl` | 28px（r1 已加） | **28px** | 维基实体页标题（维基真实 48px，折中取 28px：桌面窗口通常 1280–1440、且要留给信息框） |
| `--wiki-title-size` | 30px | **28px**（对齐 3xl） | 现状 30px 与新 3xl 重复，收敛 |
| `--lme-line-height-tight` | 1.25（r1 已加） | **1.25** | 标题（维基实测 1.1，工具场景略放宽） |
| `--lme-line-height-normal` | 1.5（r1 已加） | **1.5** | 正文/表格 |
| `--lme-line-height-relaxed` | 1.7（r1 已加） | **1.7** | 维基正文（真实 1.8，取 1.7 保密度） |
| `--lme-font-weight-*` | 400/500/600/700（r1 已加） | 保留 | 维基标题 500、信息框标题 700，与之一致 |

**关键：正文 13px/1.5（工具）、维基正文 14px/1.7、维基标题 28px/1.25/500、分节标题 20px/1.25/500（维基真实 30px，折中 20px）。**

#### 间距刻度（8pt 栅格 + 半档）

现有 `xs4 / sm8 / md12 / lg16 / xl24`，**缺 2px 与 6px**，直接导致全站约 40 处裸写像素（§2.1 C-1）。建议：

```
--lme-gap-3xs: 2px    /* 图标与文字、胶囊内距 */
--lme-gap-2xs: 4px    /* = 现有 xs */
--lme-gap-xs:  6px    /* 新增：小控件内距 */
--lme-gap-sm:  8px
--lme-gap-md:  12px
--lme-gap-lg:  16px
--lme-gap-xl:  24px
--lme-gap-2xl: 32px
--lme-gap-3xl: 48px   /* 分节间距（维基实测 50px） */
```

#### 圆角 / 海拔 / 动效

- 圆角：`sm 4 / md 6 / lg 8 / xl 12 / full 999`（r1 已加 xl、full）。**建议统一：控件 6px、卡片 8px、胶囊 999px、维基信息框 4px**（维基原站近乎 0）。
- 海拔（高度层级）：真实维基不用阴影，本项目是悬浮层多的工具，建议 4 级：
  `z0 平面（--lme-bg-base）→ z1 面板（--lme-bg-panel）→ z2 卡片/下拉（--lme-bg-elevated + --lme-shadow-md）→ z3 模态/浮层（--lme-shadow-xl + scrim）`。
- 动效：r1 已有 `--lme-dur-fast 120ms / base 180ms / slow 280ms` 与两条缓动。**补充规则：位移/尺寸用 base，颜色/透明度用 fast，模态入场用 slow；禁止 `transition: all`（现状 12 处，§2.1 C-11）**。

#### 状态态（hover / selected / focus / disabled）

| 态 | 规则 | 现状缺口 |
| --- | --- | --- |
| hover | 背景 `--lme-bg-hover`，文字提一级；**禁用态不进 hover** | 部分列表项无 hover（ProjectView:460、TreeNode 无 selected） |
| selected | **统一「背景 `--lme-bg-selected` + 左缘 2px `--lme-accent`」**（现状有 2px/3px 与紫/绿两套，§2.1 C-4） | 需全局归一 |
| focus | **全局 `:focus-visible { box-shadow: var(--lme-shadow-focus) }`** | 全站仅 SearchFilters:177 一处正确，其余 6 处 `outline:none` 无替代（§2.1 C-9） |
| disabled | `opacity: 0.45` + `cursor: not-allowed` | 各视图 0.4/0.5/0.6 三种 |
| 加载 | 列表类：顶部 2px 进度条 + 行骨架；块级：卡片内 spinner —— **消灭 ⏳ emoji 占位**（现状 4 种语言，§2.1 C-6） | AssetsView 已做进度条，其余未跟进 |
| 空态 | 三段式：图标（统一 32px）+ 主文案 + 操作建议 | 5 套实现、最素的只有一个 `<p>`（§2.1 C-8） |
| 错误 | 统一 `<ErrorBanner>`（图标 + 文案 + 可选重试），**消灭原生 `alert()`**（TextView:354、StaticView:511/513） | 4 种实现 |

#### 必须写进 tokens.css 的新增令牌（清单）

```
/* 控件尺寸 */
--lme-control-h-sm: 26px;  --lme-control-h-md: 32px;  --lme-control-h-lg: 40px;
--lme-row-h-sm: 28px;      --lme-row-h-md: 32px;      --lme-row-h-lg: 40px;
/* 行/面 */
--lme-thead-bg: var(--lme-bg-panel);
--lme-content-max-width: 1200px;   /* 对齐真实维基容器 */
--lme-content-max-width-prose: 900px; /* 长文/表单，约 60 中文字 */
/* 语义文字（配彩底） */
--lme-on-accent-text: #ffffff;
--lme-on-gold-text: #241f10;
/* 补充间距 */
--lme-gap-3xs: 2px; --lme-gap-xs: 6px;
```

> 铁律提醒：以上色值/尺寸**只**写在 `src/styles/tokens.css`；`views/` 与 `components/` 内禁止出现 `#xxx`、`rgb()`、`rgba()`（现状 5 处违规，§2.1 C-0）。

---

## 2. 现状审计（基线 `2bfac78`）

审计范围：`src/App.vue`、8 个工作台视图、4 个维基视图（+ 内嵌 `WikiStoryView`）、13 个组件、`src/styles/tokens.css`。
行号取自实际读取的文件内容；标注「待确认」者为静态推断、未实测。

### 2.1 跨文件共性问题（总纲）

| # | 问题 | 证据（文件:行） | 建议 |
| --- | --- | --- | --- |
| C-0 | **硬编码色值**（违反铁律） | ExportView:681/686/691（三处 `rgba()`）、StaticView:1010（`#fff`）、SettingsView:524（`color: white`） | 抽 `--lme-advisor-*-bg`、`--lme-on-accent-text` |
| C-0b | **z-index 硬编码** | BankView:1187、StaticView:1187 区域（`z-index: 10`）；对照 AssetsView:594 已用 `--lme-z-raised` | 全部改 `--lme-z-*` |
| C-0c | **悬空变量**（写了不存在的令牌，静默失效） | SettingsView:538（`--lme-text-tertiary`）、SettingsView:540（`--lme-font-family-mono`，真名 `--lme-font-mono`） | 加 lint 或一次性核对（现有 123 个引用 vs 202 个定义，除上述 2 处外全部命中） |
| C-1 | **间距刻度不全 → 约 40 处裸写像素** | AssetsView:634/672/757/795/896/1086/1107；BankView:814/894/916；StaticView:865/898/913/1057/1089；ProjectView:338/479/532；ExportView:469/583/646/702；HelpView:313；SettingsView:522 | 补 2px/6px 两档 + 全局替换 |
| C-2 | **分隔条 5 套实现**（1px 静态 vs 6px 可拖拽） | AssetsView:870；BankView:847；StaticView:955；TextView:892；ExportView:599 | 抽 `<Splitter>` |
| C-3 | **行高/表头背景无令牌** | item-height：Assets 30 / Bank 40·28 / Text 32 / Static 32·28；表头背景 Assets:711 `--lme-bg-panel` vs Bank:987、Text:1053 `--lme-bg-elevated` | 定义 `--lme-row-h-*`、`--lme-thead-bg` |
| C-4 | **选中态三套视觉** | 左边框宽度 Assets:730(2px) vs Bank:787/Text:1096/Static:915·1067(3px)；颜色 accent vs Bank:1045(type-audio 绿) | 统一「背景 + 左缘 2px accent」 |
| C-5 | **按钮 5 套** | `.action-btn`(Assets:895) / `.export-btn`(Bank:893、Static:1005) / `.tool-btn`(Text:1007) / `.btn`(Export:759、Project:366、Settings:427) / `.save-btn`(Static:1239)；内距 `1px 6px`～`8px 16px` 五种 | `<AppButton>`（primary/secondary/danger/ghost × sm/md） |
| C-6 | **加载态 4 种语言 + 全用 emoji** | 进度条 Assets:587；全屏遮罩+⏳ Bank:520、Static:631；内联⏳ Text:443/583；Export/Project/Settings **完全没有** | §1.2 加载态规则 |
| C-7 | **错误态 4 种 + 2 处原生 alert** | Assets:856；Bank:1196；Static:1373；Export:789；Project:400；`alert()` 见 TextView:354、StaticView:511/513 | `<ErrorBanner>` |
| C-8 | **空态 5 套且过素** | Bank:1148 / Static:1324 / Export:862 / Project:553 / Text:1303；Project 三处只是裸 `<p>` | `<EmptyState icon/title/desc/action>` |
| C-9 | **键盘可访问性几乎为零** | 仅 Bank/Text/Static/Settings 有 `:focus` 且全是 `outline:none`+换边框；Assets/Export/Project/Help **零 focus**；`--lme-shadow-focus` 定义后仅 SearchFilters:177 一处使用 | 全局 `:focus-visible` 兜底 |
| C-10 | **内容最大宽三个值、只有 3 个页面有** | Help:197(800px) / Project:292(900px) / Settings:292(720px)；Assets/Bank/Text/Static/Export 全屏铺满无栅格 | 统一 `--lme-content-max-width*` |
| C-11 | **`transition: all 0.15s` 12 处** | Bank:757/788/852/902；Text:1015/1177；Project:372；Settings:433；Help:243/319；Export:734 等 | 改具体属性 + 动效令牌 |
| C-12 | **重复片段逐字复制** | 搜索三件套 `.search-input+.filter-select+.filter-label` 在 Bank:713-763、Text:731-767、Static:815-852 重复三次；`.empty-state` 家族重复 5 次 | 抽组件 |
| C-13 | **emoji 当图标但无规范** | 导航 App:13-21；状态 ⏳⚠️✓✗ 混用；`.empty-icon` 32px vs 48px 两种 | 见 §5.1 图标策略 |
| C-14 | **r1 新令牌「建而未用」** | `--lme-dur-*` 在维基侧 0 引用却有 24 处手写 `0.15s`；`--lme-line-height-*` 有 15 处手写 1.3–1.8；`--lme-font-weight-*` 有 8 处裸 600 | 随各阶段回填（最大一致性风险） |

### 2.2 逐视图问题（工作台）

| 视图 | 关键问题（文件:行） | 档位 |
| --- | --- | --- |
| **AssetsView** | 虚拟列表高度写死 `:337`(`ref(600)`) 不随窗口；`:634/672/757/795/896` 小间距裸像素；`:870` 分隔条 1px 静态与其余 4 视图不一致；`:1048` 性能面板定位硬编码；`:1078` 裸 `<table>` 无卡片外框；**全视图无 `:focus`** | **保留 + 重排**（基准视图，补全细节） |
| **BankView** | `:422-423` 双列表高度写死 600/400；`:584-589` 六列固定宽 312px+ 塞进 `min-width:260px` 预览列 → 窄屏必溢出；`:496/555` 后端不返回 `bankType`，该列**恒显示"未知"**；`:503/568` `fsbInfo` 同样恒空却留 120px 空列；`:520` 全屏遮罩+⏳ 与 Assets 两套语言；`:1187` `z-index:10` | **重排** |
| **TextView** | `:465` 空态嵌在 `v-else-if` 内 → **"未找到匹配"永不显示（死分支）**；`:479/497` 缩进 `depth*16+8` 内联硬编码；`:594` 高度写死 600；`:354` 保存失败用原生 `alert('暂未实现')`；`:224-230/356-358` catch 静默、模板无错误态；`:904/907` 同时 `min-width: 0` 与 `320px` 后者覆盖前者；`:1109-1120` 状态仅靠边框色、无文字标签 | **重做** |
| **StaticView** | `:588-592` VirtualList 对可展开分组用**固定 item-height=32** → 展开后高度模型错误（错位，待实测）；`:128-182` `JsonTreeNode` 用 `h()` 在 setup 内定义，而样式写在父级 scoped（`:1163-1219`）→ **样式作用域存疑（待确认）**；`:493/772` "差异对比"写死"暂未实现"假内容；`:511/513` `alert()`；`:1010` `#fff`；`:1365` `z-index:10` | **重做**（含可信度问题） |
| **ExportView** | `:250/:258-260` **7 个 `<th>` 对 3 个 `<td>`**，目标矩阵语义全错；`:366/384` "开始导出"按钮所在分支与其判断互斥 → **按钮永不渲染**，而 `:441` 空态仍提示"点击开始导出"；`:187/399` `exportReport` 恒被清空 → **报告区永不显示**；`:503` sticky 表头未设 z-index 被盖住；`:638` 网格写死 4 列无 `minmax`；`:681/686/691` rgba | **重做**（当前不可用作向导） |
| **ProjectView** | `:50/88/107` `isLoading` 定义后**模板从未使用** → 无加载反馈；`:460-467/511-518` 列表项无 hover；`:292` max-width 900（三页三值）；`:185-187/228-230/248-251/274-277` 四处空态都是裸 `<p>`；`:507` `max-height:400px`；**零 focus** | **重排** |
| **SettingsView** | `:538/540` 两个悬空令牌；`:524` `color: white`；`:368-372/380-382` `.meta-full{grid-column:1/-1}` 在 flex 容器 → **死样式**；`:261-278` 动作栏在长表单底部无 sticky；`:272-277` 保存提示不会自动消失（应为 Toast）；`:133-135` catch 静默 | **重排** |
| **HelpView** | `:148-155`+`:295-297` 用 `{{ p || '\u00A0' }}` + 空行 `<div height:16px>` **靠空字符撑排版**；`:124-133` 目录不 sticky（`position` 未设）；`:197` max-width 800；`:137-158/161` 外部链接区复用 `.help-section` 层级混乱；`:292/313` 裸行高与间距 | **重排** |
| **App.vue / tokens.css** | `App.vue:64-72/75-88/94-104` 三个交互元素**无 `:focus-visible`**；`:157` `letter-spacing` 硬编码（已有 `--lme-tracking-caps`）；`:240-250` tab 条 `overflow-x:auto` 无渐隐提示；`:13-21` 纯 emoji 导航无 `aria-label`；`tokens.css:59-63` 间距缺 2/6px 档；`:213-234` v2 令牌定义后落地率低 | **重排**（r1 已完成主体，补无障碍与提示） |

### 2.3 维基侧问题（4 视图 + 组件）

| 文件 | 关键问题（:行） | 档位 |
| --- | --- | --- |
| **WikiEntityPage** | `:807-810` max-width 1440 **无 `margin:auto`** → 宽屏左贴右空；`:903-908`+`WikiInfoboxCard:56` 固定 300px 信息框 + `minmax(0,3fr)` → **窗口 <~1456px 即溢出**（1440/1366/1280 全中，正是 `docs/img/wiki-vs-ours-persona.png` 压扁成因）；`:939-959` **分节被包成卡片**（bg-panel+边框+标题栏），与真实维基"平面金标题+无卡正文"相反；`:954` 分节标题仅 15px（真实 30px）；`:994-999/1085-1092` 正文 line-height 1.8 硬编码且**无行宽上限**；`:511/512`+`WikiShell:15-18` 监听了 WikiShell 未 `defineEmits` 的事件 → 死监听；`:557/562` 未传 `:active-id` → **目录高亮永不出现**；`:989-991` 折叠只隐藏部分内容；`:841/1032` 用"黄底深字"令牌配紫/绿底 | **重做（维基最优先）** |
| **WikiCategoryView** | `:31` 一页 200 张卡；`:277/561/628` **同名 `.page-info` 两义**互相污染；`:290-300` 网格图无 `@error` 兜底（列表有）；`:599-603/620-622` `.thumb-img` 同块定义两次；`:355-400` 自实现分页（第 2 套）；`:243-260` 分段控件与 `WikiTabGroup` 两套视觉；`:39-53` 第 2 份分类元数据硬编码；`:211-218` 无面包屑 | **重排** |
| **WikiHomeView** | `:386` 标题 34px 硬编码（绕过 `--wiki-title-size`）；`:419-433` 搜索按钮用紫 `--lme-accent`，而 `WikiSearchView:195` 同一按钮用金 → **同控件跨页异色**；`:507` 进度条紫色出现在金色页；`:593` 网格写死 4 列；`:599-615` 卡片是 `div+@click` 无键盘；`:709-717` 第 4 份手写胶囊；`:727-732` 第 3 份空态 | **保留**（令牌遵守度最好，只需统一） |
| **WikiSearchView** | `:45-48` catch 吞异常 → **无错误态**，IPC 失败显示成"未找到"；`:36` limit 200 硬编码无截断提示；`:142` 唯一使用 `--wiki-content-max-width`(980)；`:116-127` 结果卡无缩略图/无高亮/`div+@click`；`:223-241` 第 2 份空态 | **重排** |
| **WikiStoryView** | `:177-180` max-width 1100 无居中，且被 `EntityPage:808` 再套一层 → **双重约束**；`:164-169` 用 `resolveMediaUrl` 而非 `resolveAudioUrl` → 拿不到解码音频；`:291-298` 左金条 3px（NoticeBox 4px、QuoteBlock 4px）三档；`:26` views 互相 import（应下沉为组件） | **重排** |
| **WikiShell** | `:116` 宽 180px 硬编码（与 `--wiki-toc-width` 双写）→ 实体页"侧栏 180 + TOC 180 + gap 48"，1280 下正文仅剩约 577px；`:21-34` 第 3 份分类元数据；`:97` 面包屑是可点 `<span>` 非链接；`:113-143` **无任何 `@media`** 不自动折叠 | **重排** |
| **components/wiki/** | `WikiToc:88` `max-height: calc(100vh - 32px)` 魔数（实际顶部还有 84px 外壳）→ 底部溢出；`WikiToc:112/117` 裸像素；`WikiInfoboxCard:37-39` 同一图既做 32px 头图又做全宽立绘（重复呈现）；`:116-125` label `nowrap`+34% 挤压值列；`WikiGallery:196-202` 固定 height:140px + cover 裁图；`:120-157` lightbox 无 `role=dialog`/焦点陷阱；`WikiAudioPlayer:114-124` `<li @click>` 播放**键盘不可达**；`WikiChipList:84-87` `outline:none` 无替代；`WikiChipList:53-64` vs `WikiSourceBadge:46-55` 两套 pill；`WikiTabGroup:110-124` 无 focus、`:92` 缺 `aria-controls`；`WikiSpineViewer:23` canvas 固定 420×520 不响应容器 | **重排**（先修可达性与 Chip 合并） |
| **通用组件** | `VirtualList:66-70` `onMounted` 内空语句（死代码）；`:12-19` 默认 28/600 固化；`TreeNode:40` 缩进硬编码、**无 selected/focus**；`:98-104` 圆角写死 8px；`PageBar` 与 `CategoryView:355` 两套分页；`SearchFilters:162` 把 32px 间距令牌当内距用（遮图标）；`SearchFilters:177-181` 是全项目**唯一正确焦点环**，应下沉为全局规则 | **重排** |

### 2.4 与真实维基的差距表（最容易感知的 6 条）

| 维度 | 真实维基（实测） | 本项目现状 | 落点 |
| --- | --- | --- | --- |
| 页面标题 | 48px / 500 / 金 | `--wiki-title-size: 30px` | → 28px（`--lme-font-size-3xl`） |
| 分节标题 | 30px / 500 / 金 / 上间距 50px | 15px、被包进卡片 | → 20px、去卡片壳、上间距 32px |
| 正文行距 · 行宽 | 1.8 · 937px（约 60 字） | 1.8 硬编码、无行宽上限 | → `--lme-line-height-relaxed` + `max-width: 68ch` |
| 信息框 | 固定 300px、深棕金底、标题金底黑字 700 | 300px 但在 3fr 轨道里溢出 | → `minmax(300px, 3fr)` 或 `width:100%` |
| 容器 | 1200px、`rgba(35,35,35,0.92)` 半透明 | 980/1100/1440 三套且不都居中 | → `--lme-content-max-width: 1200px` + 统一居中 |
| 强调色 | 单金 `#f2cb95` | 紫 `#7c6ff7` + 金 `#e6b646` 同页并存 | → 见 §3.4「双强调色」策略 |

---

## 3. 新信息架构与外壳

### 3.1 候选 A：顶部命令栏 + 工作区 tab + 内容区（**r1 已落地形态**）

```
┌──────────────────────────────────────────────────────────────┐
│ LME  Limbus Mod Editor   [ 🔍 搜索或跳转…   Ctrl+K ]   ❓ ⚙️ │ 46px 命令栏
├──────────────────────────────────────────────────────────────┤
│ 📦资源  🎵音频  📝文本  📊静态  📤导出  📁项目  ⚙️设置  ❓帮助  📖维基 │ 38px tab
├──────────────────────────────────────────────────────────────┤
│                        页面内容（全宽）                        │
└──────────────────────────────────────────────────────────────┘
```

- **优点**：纵向空间全给内容（桌面窗口通常 900–1000px 高，纵向最贵）；与真实维基"顶导"观感一致；命令面板天然居中，键盘可达；tab 横向容量大（9 项不挤）。
- **缺点**：二级层级（工作台内的分区/子页）缺乏承载位；tab 超过 ~12 项需横向滚动（现状 9 项 + 未来可能增加）。
- **代价**：**低**——已落地（commit `20c2fdb`），剩余工作量只在补无障碍与溢出提示。

### 3.2 候选 B：左侧可折叠导航（分区 + 最近使用）+ 顶部工具栏 + 主从栅格

```
┌───────┬──────────────────────────────────────────────────────┐
│ ◀ 折叠 │ [工具栏：搜索 · 筛选 · 视图切换 · 操作]               │
│ 浏览   ├───────────────────────────┬──────────────────────────┤
│  资源  │                           │                          │
│  音频  │      主区（列表/网格）      │   详情/预览（可折叠）     │
│  文本  │                           │                          │
│ 编辑   ├───────────────────────────┴──────────────────────────┤
│  导出  │ 状态栏：共 N 条 · 12ms · 最近操作                     │
│ 维基   │                                                       │
└───────┴───────────────────────────────────────────────────────┘
```

- **优点**：可承载**三级层级**（分区 → 工作台 → 子页），未来加功能不挤；"最近使用/收藏"天然有位置；主从栅格是工具型应用的成熟形态（VS Code / JetBrains）。
- **缺点**：吃掉 200–240px 横向空间（维基实体页"侧栏 180 + TOC 180"已导致正文只剩 577px，§2.3）；与真实维基的顶导观感不一致；折叠态又退化成"窄活动栏"（即现状被吐槽的形态）。
- **代价**：**中高**——需重写外壳 + 新增导航分区元数据 + 所有页面横向空间重新适配。

### 3.3 候选 C：命令面板驱动 + 全宽工作区切换（无常驻导航）

```
┌──────────────────────────────────────────────────────────────┐
│ LME        [ Ctrl+K：输入即跳转 / 执行 ]            ◀ 返回  ▣ │ 极简顶栏
├──────────────────────────────────────────────────────────────┤
│                    当前工作区（全宽，无导航）                   │
└──────────────────────────────────────────────────────────────┘
```

- **优点**：信息密度与沉浸感最高；键盘优先（适合高级用户）。
- **缺点**：**可发现性最差**——新用户看不到有哪些能力；鼠标用户每次切换都要唤面板；与"功能一个都不能丢、且要可达可用"的硬约束张力最大。
- **代价**：**高**（需完整命令注册表 + 撤销/回退 + 引导系统），且风险高。

### 3.4 推荐：**A 为主干，吸收 B 的主从栅格与 C 的命令面板**（A+）

| 决策项 | 结论 | 理由 |
| --- | --- | --- |
| 主导航 | **顶部工作区 tab**（A） | 省纵向空间、与维基顶导一致、已落地 |
| 二级层级 | **页面内分区**（不做左栏） | 9 个工作台各自层级不超过 2 级，左栏不划算 |
| 全局搜索 | **命令栏中央 Ctrl+K**（A+C），面板内支持「跳转 + 工作台动作」 | 唯一全局入口，键盘可达 |
| 主从布局 | **页面级主从栅格**（B 的栅格，不是 B 的导航） | 见 §4 模式 M1，用 `<SplitPane>` 承载 |
| 维基与工作台共存 | 维基作为**独立工作区 tab**，内部保留 `WikiShell` 左栏分类导航；进入 /wiki 时**主题切换为金色** | 对齐真实维基"顶导 + 左栏"结构 |
| **双强调色策略** | 工作台紫 `--lme-accent` 只在**工作台区**出现；维基金 `--wiki-accent` 只在**维基区**出现；**禁止同页混用** | 现状混用见 WikiHomeView:419（紫按钮）、:507（紫进度条）、WikiCategoryView:828（紫分页高亮）——统一改为金 |

**为什么不是纯 A**：纯 A 只解决了导航，没解决"内容区没有版式"（§2.1 C-10：5 个工作台全屏铺满无栅格）。所以 A+ 的关键增量是 **把 B 的主从栅格和 §4 模式库下沉到每个页面**，而不是把 B 的导航搬回来。

---

## 4. 版式模式库（6 种）+ 13 视图归属

> 通用栅格约定：
> - 断点：`xl ≥1440 / lg ≥1200 / md ≥900 / sm <900`；`<1200` 主从变上下堆叠（详情降为底部抽屉或全屏面板）。
> - 内容容器：`--lme-content-max-width: 1200px` 居中（工具型页面不居中，需铺满的见各模式说明）。
> - 栅格：12 列，`gap: var(--lme-gap-lg)`；卡片网格用 `repeat(auto-fill, minmax(220px, 1fr))`（**禁止写死列数**，现状 WikiHomeView:593、ExportView:638）。

### M1 · 主从列表（列表 + 详情）：适用「浏览—选中—操作」类

```
┌─────────────────────────────────────────────────────────────────┐
│ 工具栏  [🔍 搜索...........] [类型▾] [排序▾] [☰列表|🗂树]  共 N 条 │ 40px
├──────────────────────────────────────┬──────────────────────────┤
│ 表头  # | 类型 | 名称 | 大小 | 状态                              │ 28px
│ ┌──────────────────────────────────┐ │  ┌─ 详情卡 ─────────────┐ │
│ │ ● Texture  sample_001   12 KB ✓  │ │  │ 名称 / 类型 / 状态徽章 │ │
│ │ ● Sprite   sample_002   48 KB ●  │◄├─┤ [替换…][批量…][撤销]  │ │
│ │ ...（虚拟滚动，行高 30）          │ │  │ 预览区（图像/文本/元数）│ │
│ └──────────────────────────────────┘ │  │ ─ 关联对象 ─          │ │
│                                       │  │ 条目卡 × N            │ │
│                                       │  └───────────────────────┘ │
├──────────────────────────────────────┴──────────────────────────┤
│ 页码条  ⏮ ◀ 3/64 ▶ ⏭   跳转[__]               12ms              │
└─────────────────────────────────────────────────────────────────┘
       主区 flex:1（min 480px）        详情 320–480px（可拖拽 Splitter）
```

- **归属**：`AssetsView`（**保留+重排**：补自适应高度与可拖拽分隔条）、`TextView`（**重做**）、`StaticView`（**重做**，改动态行高）、`BankView`（**重排**：三栏变体 `银行列表 | 采样列表 | 预览`，需解决 312px 塞 260px 的溢出，§2.2）。
- **栅格**：`grid-template-columns: minmax(0,1fr) 1px minmax(320px, 420px)`；`<1200` 详情转底部面板。

### M2 · 卡片网格：适用「分类浏览 / 入口聚合」

```
┌─────────────────────────────────────────────────────────────────┐
│ Hero / 页面标题（金，28px）          副标题                       │
├─────────────────────────────────────────────────────────────────┤
│ 统计条： 2,074 页 · 10,082 分节 · 75,166 条目                    │
├─────────────────────────────────────────────────────────────────┤
│ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐   auto-fill/minmax  │
│ │ 封面   │ │ 封面   │ │ 封面   │ │ 封面   │   220px            │
│ │ 标题   │ │ 标题   │ │ 标题   │ │ 标题   │                    │
│ │ 12 项  │ │ 8 项   │ │ 无来源 │ │ 24 项  │                    │
│ └────────┘ └────────┘ └────────┘ └────────┘                    │
├─────────────────────────────────────────────────────────────────┤
│ 页码条                                                           │
└─────────────────────────────────────────────────────────────────┘
```

- **归属**：`WikiHomeView`（**保留**）、`WikiCategoryView`（**重排**：一页 200 → 24–48 张 + 图片 `@error` 兜底）、`ProjectView`（**重排**：项目卡/最近使用用 M2，分区内容用 M6）。
- **栅格**：`repeat(auto-fill, minmax(220px, 1fr))`，`gap: var(--lme-gap-lg)`；卡内封面 `aspect-ratio: 3/4` + `object-fit: cover`（修 WikiGallery:196 的固定 140px）。

### M3 · 表格式数据页：适用「多行多列、需要对比/批量」

```
┌─────────────────────────────────────────────────────────────────┐
│ 工具栏 [筛选] [列显示▾] [导出]          共 N 行 · 已改 M 行       │
├─────────────────────────────────────────────────────────────────┤
│ (sticky 表头, z-index: var(--lme-z-sticky))                      │
│ 字段A │ 字段B │ 字段C │ 状态 │ 操作                              │
│  val  │  val  │  val  │ ●改  │ [编辑][撤销]                      │
│  ...（虚拟滚动或分页，行高 32）                                   │
├─────────────────────────────────────────────────────────────────┤
│ 页码条 / 或虚拟滚动条位置提示                                     │
└─────────────────────────────────────────────────────────────────┘
```

- **归属**：`StaticView` 记录表（**重做**，配 M1 做主从）、`ExportView` 目标矩阵（**重做**，先修 7th/3td 的列对齐，§2.2）。
- **要点**：sticky 表头必须 `z-index: var(--lme-z-sticky)`（现状 ExportView:503 缺失被盖）；行高统一 `--lme-row-h-md`。

### M4 · 查看器为主页：适用「预览/播放是主角」

```
┌─────────────────────────────────────────────────────────────────┐
│ 面包屑  浮士德 / 立绘                          [去编辑] [撤销]    │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│              预览区（图像 / Spine canvas / 音频播放条）            │
│              背景 --lme-bg-input，居中，contain                   │
│                                                                  │
├─────────────────────────────────────────────────────────────────┤
│ 元数据 strip：类型 · 尺寸 · 修改时间 · 来源徽章                   │
└─────────────────────────────────────────────────────────────────┘
```

- **归属**：`AssetsView` 预览列（**保留**）、`BankView` 音频预览（**重排**：修 6 列塞 260px 与键盘不可达）、`WikiSpineViewer`（**重排**：canvas 420×520 → 响应式 `aspect-ratio`）。
- **要点**：预览区背景与页面背景要有明度差（`--lme-bg-input` 或 `--wiki-canvas-bg`），不留白边。

### M5 · 文档式长页（维基实体页）

```
┌────────────────────────────────────────────────────────────────────┐
│ 面包屑：维基 / 人格 / 浮士德                                        │
├───────────┬──────────────────────────────────────┬─────────────────┤
│ TOC       │  浮士德                    （28px 金） │  ┌─信息框─────┐ │
│ 1 介绍    │  ──────────────────────────────────   │  │ 金底标题   │ │
│ 2 立绘    │  引言块（引用样式，左侧金条 4px）       │  │ 12 行字段  │ │
│ 3 语音    │                                       │  │ 300px 固定 │ │
│ 4 考据    │  介绍                    （20px 金）   │  └────────────┘ │
│ (sticky)  │  正文 14px/1.7，行宽 ≤68ch             │                 │
│           │  [Tab 分组：立绘 | 语音 | 数据]         │                 │
│           │  ── 画廊（auto-fill）──                │                 │
├───────────┴──────────────────────────────────────┴─────────────────┤
│ 内容容器 1200px 居中；正文列 937px（1200 − TOC − gap），与真实维基一致 │
└────────────────────────────────────────────────────────────────────┘
```

- **归属**：`WikiEntityPage`（**重做**）、`WikiStoryView`（**重排**，拆为 `components/wiki/WikiStoryBody.vue` 消除 views 互相 import）。
- **要点（对应 §2.4 差距表）**：① 分节**去卡片壳**，只留金色标题 + 分隔线；② 栅格 `180px minmax(0,7fr) minmax(300px,3fr)`（信息框改 `minmax(300px,3fr)` 修溢出）；③ 容器 1200px 居中；④ TOC `sticky`，`max-height` 用 `--lme-topbar-height + --lme-tabbar-height` 计算（现状魔数 `calc(100vh - 32px)`）。

### M6 · 表单 / 向导页

```
┌─────────────────────────────────────────────────────────────────┐
│              内容居中，max-width: 900px                           │
│  ┌─ 分区卡 ─────────────────────────────────────────────────┐   │
│  │ 分区标题（15px/600）                                      │   │
│  │  标签    [控件 32px]        说明文字（12px 次要色）        │   │
│  │  标签    [控件]                                           │   │
│  └───────────────────────────────────────────────────────────┘   │
│  ┌─ 分区卡 ─────────────────────────────────────────────────┐   │
│  └───────────────────────────────────────────────────────────┘   │
│                       [sticky 动作栏]  [取消] [保存]              │
└─────────────────────────────────────────────────────────────────┘
```

- **归属**：`SettingsView`（**重排**：sticky 动作栏、修死样式、Toast 化保存提示）、`ProjectView`（**重排**：补加载态 + M2 入口网格）、`ExportView`（**重做**：向导式，先修"开始导出按钮永不渲染"与报告区永不显示）、`HelpView`（**重排**：M6 + 侧边目录 sticky，去掉 `\u00A0` 撑排版的写法）。

### 13 视图归属总表

| # | 视图 | 模式 | 档位 | 首要动作 |
| --- | --- | --- | --- | --- |
| 1 | AssetsView | M1 + M4 | 保留+重排 | 自适应高度、可拖拽分隔条、补 focus |
| 2 | BankView | M1（三栏）+ M4 | 重排 | 解决 312px/260px 溢出；去掉恒空列 |
| 3 | TextView | M1 | 重做 | 修死分支空态；去 `alert()` |
| 4 | StaticView | M1 + M3 | 重做 | 动态行高；JSON 树拆 SFC |
| 5 | ExportView | M6（向导）+ M3 | 重做 | 修列对齐、按钮/报告区不可见 |
| 6 | ProjectView | M2 + M6 | 重排 | 补加载态；空态组件化 |
| 7 | SettingsView | M6 | 重排 | sticky 动作栏；修悬空令牌 |
| 8 | HelpView | M6（带侧目录） | 重排 | 去空字符排版；目录 sticky |
| 9 | WikiHomeView | M2 | 保留 | 按钮/进度条改金；网格去写死列 |
| 10 | WikiCategoryView | M2 | 重排 | 分页复用 PageBar；图片兜底 |
| 11 | WikiEntityPage | M5 | **重做** | 修 300px 溢出 + 居中 + 分节去卡片 + TOC 高亮 |
| 12 | WikiSearchView | M2（结果列表） | 重排 | 补错误态；结果卡可键盘 |
| 13 | WikiStoryView | M5（内嵌） | 重排 | 下沉为组件；音频改 `resolveAudioUrl` |

---

## 5. 组件与依赖建议

### 5.1 需要的新组件（职责 + props 草案）

> 命名与目录：通用件放 `src/components/ui/`，维基专用件放 `src/components/wiki/`。

| 组件 | 职责 | props 草案 | 备注 |
| --- | --- | --- | --- |
| `AppShell` | 外壳骨架（命令栏 + 工作区 tab + 内容容器 + 通知/模态层），内含 `<slot name="topbar-actions">` | `{ navItems: NavItem[]; activeKey?: string }` | 把 `App.vue` 的骨架抽出来，页面不再写外壳 |
| `CommandBar` | 顶部命令栏（品牌 + 搜索触发 + 右侧动作） | `{ brand: string; placeholder?: string }` emit `open-palette` | 46px |
| `WorkspaceTabs` | 工作区 tab，滚动渐隐 + 键盘左右键 + `aria-selected` | `{ items: NavItem[]; modelValue: string }` | 修现状 `overflow-x:auto` 无提示 |
| `CommandPalette` | Ctrl+K 命令面板（**已存在**，r1 落地） | `{ open: boolean; items: CommandItem[] }` | 后续接 `catalog.query`/`wiki.search` 做真搜索 |
| `Toolbar` | 页面工具栏（左：搜索/筛选，右：视图切换/动作），统一 40px 高与间距 | `{ dense?: boolean }` + slot | 消灭 4 个视图各自手写 `.filter-row` |
| `Card` / `SectionCard` | 卡片壳（标题 + 内容 + 可选底部动作） | `{ title?: string; padded?: boolean; interactive?: boolean }` | `interactive` 时渲染为 `<button/link>` 而非 `div+@click`（修大量键盘不可达） |
| `DataTable` | 表格式数据（sticky 表头、行高令牌、空/加载/错误态） | `{ columns: Column[]; rows: Row[]; rowKey: string; loading?: boolean; empty?: string }` | 内置 `z-index: var(--lme-z-sticky)` |
| `SplitPane` | 可拖拽分隔的主从栅格 | `{ direction: 'horizontal'\|'vertical'; min: number; max: number; modelValue: number }` | 统一现状 5 套分隔条 |
| `EmptyState` | 三段式空态 | `{ icon?: string; title: string; desc?: string }` + slot `action` | 消灭 5 套实现 |
| `Skeleton` | 骨架占位（行/卡两种） | `{ variant: 'row'\|'card'; count?: number }` | 替换 ⏳ emoji |
| `LoadingBar` | 顶部 2px 进度条 | `{ active: boolean }` | 已在 AssetsView 实现，抽公共 |
| `ErrorBanner` | 错误横幅 | `{ message: string; retry?: () => void }` | 消灭 `alert()` |
| `Toast` | 全局通知（自动消失 + 队列） | 经 `useToast()` 调用 | 替代 SettingsView:272 的常驻提示 |
| `Breadcrumb` | 面包屑（渲染 `<RouterLink>`，可键盘） | `{ items: { label: string; to?: string }[] }` | 修 WikiShell:97 的可点 span |
| `SidePanel` | 维基/详情侧栏（sticky + 独立滚动） | `{ width?: number; side: 'left'\|'right' }` | 承载 TOC 与信息框 |
| `Chip` / `Badge` | 单一样式的胶囊/徽章 | `{ tone: 'accent'\|'success'\|'warning'\|'error'\|'muted' }` | 合并 WikiChipList/WikiSourceBadge 两套 pill |
| `SegmentedControl` | 分段控件 | `{ options: {value,label,icon?}[]; modelValue }` | 统一 WikiTabGroup 与 CategoryView:243 两套 |
| `Pagination` | 分页（复用现有 `PageBar` 提升） | 同 `PageBar`，加 `:compact` | 消灭 CategoryView:355 第二套 |
| `Icon` | 图标（见下） | `{ name: string; size?: number }` | 统一 emoji 基线问题 |

**图标策略**：现状用 emoji（导航 9 个、状态 4 个），问题是**基线不齐、跨系统渲染不同、字号不统一**（`.empty-icon` 32 vs 48）。两种选择：① 继续 emoji，但用 `<Icon>` 统一 `font-size/line-height` 与 `aria-hidden`；② 引入 `lucide-vue-next`（tree-shaking，单图标约 0.3–1 KB，量级以实际构建为准）。**建议：先用 ① 保证一致性（零成本），P3 若仍觉得糙再评估 ②。**

### 5.2 依赖取舍

> **本节结论已更新（r1 起）**：需求方明确「**要求引入组件库**」。据此最终**引入 Naive UI**；
> 主题在运行时从 `tokens.css` 派生（已落地 `src/theme/naiveTheme.ts`，**零色值字面量**），
> 并提供 `/lib-check` 自检页（`src/views/LibCheckView.vue`）做库组件在暗色 + tokens 主题下的回归验证。
> 「设计色只能来自 `tokens.css`」铁律**未被违反**：库不携带自有色板，所有色值仍由 tokens 单一供给。

| 方案 | 体积（量级，以本地构建产物为准） | 主题化代价 | 结论 |
| --- | --- | --- | --- |
| **Naive UI（按需引入）** | 组件按需分包（构建产物实际以 `npm run build` 为准） | **低**：`themeOverrides` 在运行时由 tokens 派生，无需引入第二套色板 | ✅ **已采用**（`94cb2e6`）；前提是**只用 `themeOverrides` 映射、禁止出现色值字面量** |
| **纯 CSS 栅格 + 自研组件（CSS 变量主题）** | **0 KB**（全部走 `tokens.css`） | 无（变量即主题） | ⚠️ 仅保留给维基区少量「壳 + 态」型组件（如 `WikiNoticeBox`），不再作为总体路线 |
| `@vueuse/core`（按需引入） | tree-shaking 后通常几 KB～十几 KB | 无 | ✅ **可选**：`useElementSize`（修虚拟列表写死高度）、`onKeyStroke`（命令面板）、`useVirtualList`（替换自研 VirtualList 待评估） |
| headless 组件（`@headlessui/vue` / `radix-vue`） | 每个组件约几 KB，合计数十 KB | 需覆写样式以接入 tokens | ❌ 不再考虑：既然已采用 Naive UI，再叠一层 headless 只会增加适配成本 |
| 图标集 `lucide-vue-next` | 单图标 <1 KB（tree-shaking） | 无 | ⚠️ 延后（P3） |
| CSS 框架（Tailwind / Bootstrap） | Tailwind 产物约 5–15 KB（purge 后）；Bootstrap 约 20–30 KB | 中：Tailwind 需把 tokens 映射到 config；Bootstrap 自带组件观感与维基风格冲突 | ❌ 不建议（Tailwind 会与 tokens.css 双轨并行，违反单一色源铁律） |

**主题映射示例**（`src/theme/naiveTheme.ts` 的实际做法：运行时读 `getComputedStyle(:root)` 的变量计算值，
再喂给 `GlobalThemeOverrides`；库需要可解析的 hex/rgba 才能算派生色，故**不能直接传 `var(...)` 字符串**；
token 缺失时该键跳过、回落库自带暗色，**绝不在 TS 里另起一套色值**）：

```
tokens.css 变量            →  Naive UI 主题字段
--lme-bg-base              →  common.baseColor / common.bodyColor
--lme-bg-panel             →  common.cardColor / Card.color / DataTable.tdColor
--lme-bg-elevated          →  common.modalColor / common.popoverColor / DataTable.thColor
--lme-bg-input             →  common.inputColor / Input.color
--lme-text-primary         →  common.textColorBase / common.textColor1
--lme-text-secondary       →  common.textColor2
--lme-text-muted           →  common.textColor3 / Empty.textColor / placeholderColor
--lme-border               →  common.borderColor / common.dividerColor
--lme-accent（工作台紫）    →  common.primaryColor（维基区传 --wiki-accent 换成金）
--lme-radius-md            →  common.borderRadius
--lme-font-size-md         →  common.fontSize / common.fontSizeMedium
--lme-shadow-md            →  common.boxShadow2
```

**接入红线（三条）**：① 只通过 `themeOverrides` 映射，组件内**不得出现色值字面量**；
② 主色分区域注入（工作台紫 / 维基金），不由库决定；③ 新增 token 时同步补映射，缺失即回落库默认。

---

## 6. 分阶段落地计划

> 每阶段必须同时满足：`npm run type-check` + `npm run build` 通过（既有铁律），且**功能与 IPC 调用零丢失**（每阶段结束做一次 IPC 方法清单比对，见 §7.2）。

### P0 · 外壳 + 令牌（r1 已部分落地：`2515333`、`20c2fdb`）

**范围**：抽 `AppShell`/`CommandBar`/`WorkspaceTabs`；补齐 §1.2 令牌（间距 2/6px、控件高度、行高、内容最大宽、语义文字）；全局 `:focus-visible` 兜底；`--lme-shadow-focus` 全量落地。
**验收标准**：
- 键盘 Tab 可遍历全部导航/工具栏/列表行，且焦点可见（无一处 `outline:none` 无替代）。
- `grep -rnE '#[0-9a-fA-F]{3,8}\b|rgba?\(' src/views src/components` → **0 命中**。
- `grep -rnE 'z-index:\s*\d' src/` → 0 命中（全部走 `--lme-z-*`）。
- 外壳截图：1440/1280/1024 三档下 tab 条无裁切、无横向滚动。
**风险**：全局 `:focus-visible` 可能与现有 `outline:none` 控件冲突 → 先加后逐个核对；令牌改名需一次性全局替换（避免半途两套并存）。

### P1 · 四个浏览/编辑型工作台（Assets / Bank / Text / Static）

**范围**：按 M1/M3 重排；`Toolbar`/`DataTable`/`SplitPane`/`EmptyState`/`Skeleton`/`ErrorBanner` 落地；修 BankView 溢出与恒空列、TextView 死分支、StaticView 动态行高与 JSON 树 SFC 化；AssetsView 自适应高度 + 可拖拽分隔条。
**验收标准**：
- 四个工作台均能在 1280×800 下无横向滚动条（BankView 预览列不再溢出）。
- 每个工作台具备统一的加载/空/错误/悬停/选中/聚焦六态。
- **IPC 比对**：`asset.*`、`catalog.*`、`bank.*`、`lang.*`、`static.*`、`relation.describe` 调用面与 P0 前一致（`git diff` 看 `ipc` 调用点数量不减）。
- 截图：`/assets`、`/bank`、`/text`、`/static` 各 1 张（含空态与错误态各 1 张）。
**风险**：StaticView 虚拟列表动态行高改造可能引入性能回退（万级行）→ 先做分页+固定行高的折中，必要时保留虚拟滚动但按组展开重算（待实测）；TextView/StaticView 现有逻辑 bug 与版式改造混合，建议**先修 bug 再改版式**。

### P2 · 导出 / 项目 / 设置 / 帮助

**范围**：ExportView 改 M6 向导并修三个"永不显示"逻辑；ProjectView M2+M6；SettingsView sticky 动作栏 + Toast；HelpView 去空字符排版 + 目录 sticky。
**验收标准**：
- ExportView：开始导出按钮可见、报告区可见、目标矩阵列与数据对齐（7 列对 7 列）。
- SettingsView：无悬空变量（对比 tokens 定义表）、保存提示 3 秒自动消失。
- 4 个页面在 1280 下无溢出；`alert()` 调用数 = 0。
**风险**：ExportView 的"不可见"是逻辑 bug 而非样式问题，改版式前需先与需求方确认预期交互（避免把 bug 画进新设计）。

### P3 · 维基四视图 + 打磨

**范围**：WikiEntityPage 按 M5 重做（居中 + 栅格 `minmax(300px,3fr)` + 分节去卡片 + TOC sticky 高亮）；WikiCategory/Home/Search 统一 M2；WikiStory 下沉为组件；`Chip`/`SegmentedControl`/`Pagination` 合并；双强调色策略落地（维基页内禁紫）；可选评估图标集。
**验收标准**：
- 1440/1280/1024 三档下维基实体页信息框不溢出、正文列宽 ≤ 68ch、TOC 吸顶且当前节高亮。
- 维基页内 `grep -c 'lme-accent'` = 0（禁紫策略）。
- 对比图：重做后与 `docs/img/wiki-ref/live-persona.png`（真实维基）同视口并排，标题/分节/行宽/信息框四项观感对齐（主观验收，需需求方确认）。
**风险**：去卡片化与"工具感"有张力（需求方可能觉得信息密度低）→ 先出 1 个实体页样板再铺开；TOC 滚动联动需要 IntersectionObserver，注意与虚拟滚动/懒加载的交互。

---

## 7. 附录：观测方法与证据

### 7.1 真实维基观测

> ⚠️ **路径变更（2026-09-19）**：本节及本文其他位置引用的 `docs/img/**` 截图、`spikes/harness-tools/**`
> 工具均已整体移出仓库，归档于仓库外 `_lme-removed-20260919/tree/`。下文保留原始路径以维持记录完整。

- 工具：Playwright（`spikes/harness-tools/node_modules/playwright-core`）+ 本机 Chrome，**不下载 Chromium**。
- 关键：站点有 Cloudflare 人机校验，默认 headless 会被拦（首测 HTTP 403）。需设置常规 Chrome UA、`locale: zh-CN`、`Accept-Language`，并 `addInitScript` 抹掉 `navigator.webdriver`，加 `--disable-blink-features=AutomationControlled`；随后 HTTP 200。
- 探测脚本（临时，未入库，可复现）：
  - `C:\Users\tester\temp\lme-verify\wiki-probe.mjs` —— 取 `:root` CSS 变量与各选择器 `getComputedStyle`（字号/行高/字重/色/边距/栅格）。
  - `C:\Users\tester\temp\lme-verify\wiki-probe2.mjs` —— 取背景层、页头高度、卡片、按钮、栅格、TOC、段落行宽、h2 节奏、信息框行高。
- 运行：
  ```bash
  NODE_PATH="E:/desktop/work/_lme-removed-20260919/tree/spikes/harness-tools/node_modules" \
    node C:/Users/tester/temp/lme-verify/wiki-probe.mjs "https://limbuscompany.huijiwiki.com/wiki/%E6%B5%AE%E5%A3%AB%E5%BE%B7"
  ```
- 新增参考截图（本轮产出）：
  - `docs/img/wiki-ref/live-home.png` —— 真实维基首页（1440×1000 视口）
  - `docs/img/wiki-ref/live-persona.png` —— 真实维基「浮士德」实体页（含信息框、分节、TOC）
  - 既有：`docs/img/wiki-ref/faust-lcb-wiki.png`、`docs/img/wiki-personality-page.png`、`docs/img/wiki-front-page.png`、`docs/img/wiki-category-page.png`、`docs/img/wiki-vs-ours-persona.png`

### 7.2 IPC 保真比对（每阶段执行）

```bash
# 提取前端所有 IPC 方法调用点，阶段前后各跑一次比对数量与集合
grep -rhoE "ipc\.request[<(][^,)]*" src/LimbusModEditor.Web/src --include=*.vue --include=*.ts | sort | uniq -c
```
要求：阶段前后**集合相等**，数量不减。

### 7.3 铁律自检（每阶段执行）

```bash
cd src/LimbusModEditor.Web
npm run type-check && npm run build
# 1) 设计色只能来自 tokens.css
grep -rnE '#[0-9a-fA-F]{3,8}\b|rgba?\(' src/views src/components   # 期望 0
# 2) z-index 必须走令牌
grep -rnE 'z-index:\s*[0-9]' src/                                   # 期望 0
# 3) 悬空变量（引用了 tokens 里不存在的）
#    做法：提取 src/ 内所有 var(--x) 与 tokens.css 内所有定义，做差集
```

### 7.4 待确认项

1. `StaticView.vue:128-182` 的 `JsonTreeNode`（`h()` 定义 + 父级 scoped 样式）样式是否真的失效 —— 静态推断，**需在浏览器实测**。
2. `StaticView.vue:588-592` 可展开分组 + 固定 `item-height=32` 的错位表现 —— 静态推断，**需实测**。
3. 真实维基 TOC `position: relative`（不吸顶）是皮肤默认还是该页特殊 —— 观测到的是 relative，**粘顶为本项目的主动改进**，非照抄。
4. 依赖体积数字为公开资料量级，**未在本项目实测**（未引入），若引入需以本地 `npm run build` 产物为准。
5. `ExportView` 三个"永不显示"逻辑（按钮/报告区/矩阵列）是 bug 还是有意的未完成实现 —— 需与需求方确认后再定版式。
