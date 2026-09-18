# 前端设计体系（UI 重构 R6）

> 生效日期：2026-09-18
> 适用范围：`src/LimbusModEditor.Web/`（Vue 3 + TypeScript + Naive UI + lucide 图标）
> 本文档是**改造页面时的唯一规范**。新增页面、修改样式前请先读完第 2–5 节。

---

## 1. 这次改了什么（背景）

旧界面被评价为「极度地差」，具体问题：

| 问题 | 表现 |
|---|---|
| emoji 当图标 | 📦🎵📝📊📤📁⚙️❓🔍 等 100+ 处散落在 23 个文件里，字号/基线/配色都不受控 |
| 配色割裂 | 工作台用紫色 `#7c6ff7`、维基自带一套金色，两套主色并存；与 WPF 原生外壳的蓝灰 `#111C24` 又是第三套 |
| 层级不清 | 顶部命令栏 + 工作区 Tab 条 + 各页自己画的工具栏，三层横向条抢注意力 |
| 状态不可见 | 长操作只在页面局部给一行灰字；没有全局「当前在做什么 / 刚才发生了什么」 |
| 指引缺失 | 空态只写「没有命中的资源」，不告诉用户下一步该做什么 |

对应改造（本次）：

1. **图标体系**：全部 emoji 换成 lucide 矢量图标，统一走 `AppIcon`。
2. **主题系统**：三套配色合并成一套令牌；支持**明暗双主题**与**5 套强调色**，可在设置/工具条切换。
3. **外壳**：改为 VS Code 式「工具条 + 左侧活动栏 + 页面 + 底部状态栏」，去掉工作区 Tab 条。
4. **状态反馈**：新增全局状态 store 与底部状态栏；长操作统一登记为「活动」，进度、通知、连接状态实时可见。
5. **指引**：新增 `PageHeader`（标题 + 一句话说明 + 可关闭的操作指引）与 `StateBlock`（加载/空/错误三态 + 下一步建议）。

---

## 2. 硬性约束（违反即算失败）

| # | 约束 |
|---|---|
| 1 | **禁止色值字面量**。视图与组件里不得出现 `#xxxxxx` / `rgb()` / 命名色；一律用 `var(--lme-*)`。色值只在 `src/styles/tokens.css` 定义。 |
| 2 | **禁止 emoji**。需要图形一律用 `<AppIcon name="…" />`；缺图标时先在 `src/components/icons.ts` 里加语义名。 |
| 3 | **IPC 零改动**。所有 `ipc.request('x.y', …)` 的方法名、载荷字段、调用时序**一行都不能改**（`tests/.../IpcMethodContractTests.cs` 会扫源码核对）。 |
| 4 | **深链零改动**。路由 query 参数（`?container=` / `?file=` / `?table=` / `?bank=&sample=` / `?q=` / `?category=`）必须保持可用。 |
| 5 | **保留既有 class 名**（便于回归定位），只改容器/控件与样式。 |
| 6 | 不要用 PowerShell 重写仓库文本文件（本项目出过两次中文乱码事故），用文件编辑工具。 |

---

## 3. 设计令牌（`src/styles/tokens.css`）

令牌分四层，按顺序生效：

```
:root                     → 结构令牌（间距/圆角/字体/动效/层级/尺寸）
:root, [data-theme=dark]  → 深色基色（默认）
[data-theme=light]        → 浅色基色
:root, [data-accent=amber]→ 强调色包（amber / azure / crimson / emerald / violet）
:root                     → 派生令牌（外壳、维基、进度条…由上面三层算出）
```

切换主题 = 改 `<html>` 上的 `data-theme` / `data-accent`（`src/theme/preferences.ts`）。

### 3.1 常用令牌速查

| 用途 | 令牌 |
|---|---|
| 页面底 / 面板 / 浮层 | `--lme-bg-base` / `--lme-bg-panel` / `--lme-bg-elevated` |
| 输入框底 / 内嵌底 | `--lme-bg-input` / `--lme-bg-inset` |
| 悬停 / 按下 / 选中 | `--lme-bg-hover` / `--lme-bg-active` / `--lme-bg-selected` |
| 正文 / 次要 / 弱化 / 禁用 | `--lme-text-primary` / `--lme-text-secondary` / `--lme-text-muted` / `--lme-text-disabled` |
| 边框 / 强边框 / 细分隔 | `--lme-border` / `--lme-border-strong` / `--lme-border-subtle` |
| 主色四件套 | `--lme-accent` / `--lme-accent-hover` / `--lme-accent-muted` / `--lme-accent-contrast` |
| 主色淡底 / 淡边 / 聚焦光晕 | `--lme-accent-subtle` / `--lme-accent-border` / `--lme-accent-glow` |
| 语义色 + 淡底 + 淡边 | `--lme-success` `--lme-warning` `--lme-error` `--lme-info` / `--lme-{语义}-subtle` / `--lme-{语义}-border` |
| 资产类型色 | `--lme-type-texture` `--lme-type-sprite` `--lme-type-audio` `--lme-text-text` `--lme-type-json` `--lme-type-mono` … |
| 编辑状态色 | `--lme-state-modified` `--lme-state-added` `--lme-state-deleted` `--lme-state-conflict` `--lme-state-unchanged`（+ `-bg` 淡底） |
| 间距 | `--lme-gap-2xs|xs|sm|md|lg|xl|2xl|3xl` = 2/4/8/12/16/24/32/48px |
| 圆角 | `--lme-radius-xs|sm|md|lg|xl|full` |
| 字号 | `--lme-font-size-2xs|xs|sm|md|lg|xl|2xl|3xl` = 10/11/12/13/15/18/22/28px |
| 行高 / 字重 | `--lme-line-height-tight|normal|relaxed` / `--lme-font-weight-regular|medium|semibold|bold` |
| 阴影 | `--lme-shadow-sm|md|lg|xl` / 聚焦环 `--lme-shadow-focus` |
| 动效 | `--lme-dur-instant|fast|base|slow` + `--lme-ease-standard|emphasized` |
| 层级 | `--lme-z-base|raised|sticky|dropdown|overlay|modal|toast` |
| 外壳尺寸 | `--lme-toolbar-height`(40) `--lme-activitybar-width`(48) `--lme-statusbar-height`(24) `--lme-row-height`(28) |
| 媒体画布 | `--lme-canvas-bg`（明暗都保持深色） |

### 3.2 工具类（`tokens.css` 末尾）

- `.lme-mono` 等宽字体 · `.lme-ellipsis` 单行省略 · `.lme-caps` 小号全大写标签
- `.lme-kbd` 键帽 · `.lme-loadingbar` 顶部细进度条（配 `@keyframes lme-loading-slide`）
- `@keyframes lme-spin` 旋转（配 `AppIcon name="loader"`）

---

## 4. 公共组件

### 4.1 `AppIcon` —— 唯一的图标出口

```vue
<AppIcon name="assets" :size="16" :stroke="1.75" />
<AppIcon name="warning" :size="14" label="警告" />   <!-- 需要语义时才给 label -->
```

图标名见 `src/components/icons.ts`（语义命名，如 `assets` / `audio` / `wikiPersona` / `success` / `chevronRight`）。
颜色继承 `currentColor`，所以**由外层 CSS 决定颜色**，组件本身不写色。
缺图标 → 在该文件里加一行（`语义名: Lucide 组件`），不要在页面里内联 SVG。

### 4.2 `PageHeader` —— 每个工作台顶部的标题区

```vue
<PageHeader
  icon="assets"
  title="资源工作台"
  description="浏览、预览与替换游戏资源"
  hint="先在上面搜索资源；选中一行后右侧可以预览或替换"
  hint-key="assets"
>
  <template #meta><!-- 计数、状态胶囊等 --></template>
  <template #actions><!-- 右侧主操作按钮 --></template>
</PageHeader>
```

- `description` 必写：一句话说清**这页能做什么**。
- `hint` 是**可关闭**的操作指引；给了 `hint-key` 才会记住关闭状态（localStorage）。
- `compact` 用于卡片内的次级标题。

### 4.3 `StateBlock` —— 加载 / 空 / 错误三态

```vue
<StateBlock state="loading" title="正在查询资源目录…" />
<StateBlock
  state="empty"
  icon="database"
  title="没有命中的资源"
  description="换个关键词，或点上方「清除筛选」恢复默认视图"
/>
<StateBlock state="error" :title="catalog.error" description="检查游戏目录设置后重试">
  <template #actions><NButton size="small" @click="retry">重试</NButton></template>
</StateBlock>
```

`description` 是**下一步建议**，不是重复报错。加 `.fill` class 可撑满容器高度。

### 4.4 `StatusBar` / `stores/status.ts` —— 全局状态

`App.vue` 已挂载状态栏并调用 `status.attach()`。页面**不要**再自己画全局进度条，
改为把长操作登记进 store：

```ts
import { useStatusStore } from '@/stores/status'
const status = useStatusStore()

// 方式一：包住一段异步操作（自动登记 + 成功/失败通知 + 结束后移除）
await status.track('export-run', '正在导出模组', () => ipc.request('export.run', { targetDirectory }))

// 方式二：手动登记（需要中途更新进度时）
status.beginActivity('scan', '正在扫描游戏资源')
status.updateActivity('scan', { detail: '第 3/12 个 bundle', current: 3, total: 12 })
status.endActivity('scan')

// 只记一条通知（不进活动列表）
status.notify('warning', '游戏目录未设置，本次扫描已跳过')
```

`status` 提供的响应式字段：`isBusy` `activeActivity` `activities` `notices` `unreadCount` `connected` `pendingRequests` `projectName` `projectPath` `hasProject` `indexStale` `healthSummary`。

宿主事件（`progress` / `toast` / `state.scanDone` / `state.projectChanged` / `state.indexInvalid` / `session.hello`）由 store 统一订阅，页面无需重复订阅。

### 4.5 其他共享组件

| 组件 | 用途 |
|---|---|
| `SearchFilters.vue` | 资源搜索 + 类型/排序/范围筛选（TextView / BankView / StaticView 共用） |
| `PageBar.vue` | 服务端分页条 |
| `ContainerTree.vue` / `TreeNode.vue` | 资源容器目录树 |
| `PreviewPane.vue` | 右侧预览面板 |
| `VirtualList.vue` | 定行高虚拟滚动 |
| `WikiShell.vue` | 维基区侧栏 + 面包屑外壳 |
| `AppearanceControl.vue` | 工具条上的外观快速切换（明暗 + 强调色） |
| `CommandPalette.vue` | Ctrl+K 全局命令面板 |

---

## 5. 单页改造套路

1. **读**：目标页 + `tokens.css` + 一个已改造的范例页（`AssetsView.vue` / `ProjectView.vue`）。
2. **加页头**：页面最外层第一个子元素放 `PageHeader`，写清 `description` 与 `hint`。
3. **换图标**：grep 页面里的 emoji（`[\x{1F300}-\x{1FAFF}\x{2600}-\x{27BF}]`），逐个换成 `<AppIcon>`。
4. **换三态**：手写的「加载中/空/错误」块换成 `StateBlock`；顶部细进度条统一用 `.lme-loadingbar`。
5. **接状态**：把 `busy` 类 ref + 局部结果字符串的反馈，改为 `status.track(...)` 或 `status.notify(...)`（局部内联结果可以保留，但不要只有它）。
6. **收色值**：把任何硬编码色值换成令牌；旧令牌名已全部保留，可放心沿用。
7. **门禁**：`npm --prefix src/LimbusModEditor.Web run type-check` 必须 0 错、`run build` 必须成功。
8. **提交**：中文提交信息，注明「IPC 与深链零改动」。

---

## 6. 已知遗留（本次未做，别当成规范）

- `src/LimbusModEditor.App/Themes/WorkbenchStyles.xaml`（443 行）是已删除的 WPF 工作台页面遗留的样式字典，当前无任何引用，仅在 `App.xaml` 里被合并加载。属于死代码，清理需单独评估。
- `docs/UI-REDESIGN-SPEC.md` 与 `docs/HANDOFF-UI-REDESIGN.md` 是本次改造**之前**的规格与交接记录，其中的外壳方案（顶部命令栏 + 工作区 Tab）已被本次取代，仅作历史参考。
- 后端构建前须补 `OS` 环境变量（`$env:OS = "Windows_NT"`），否则 `Directory.Build.props` 推导不出
  `RuntimeIdentifier`，`dotnet build` 会报 `NETSDK1060 … Value cannot be null (Parameter 'path1')`。
  根因与解法见 `docs/STATUS.md` §6.4。**2026-09-19 复跑：`dotnet build` 0 警告 0 错误、
  `dotnet test` 1045 项全绿、`IpcMethodContractTests` 单独通过。**
