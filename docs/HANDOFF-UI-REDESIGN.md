# 交接文档：前端 UI 重构（库组件化）剩余工作

> 交接日期：2026-09-18
> 当前 HEAD：`fabfc44`
> 交接原因：执行会话（WorkBuddy 与一个 DSH subagent）先后失效，剩余页面需由新 agent 接手。

---

## 0. 一句话现状（2026-09-18 更新）

**前端 UI 重构已基本完成**：外壳、设计令牌、Naive UI 接入与主题派生、**14/14 个视图的库组件化**均已入库
（最后一批：`e3bbdfd` 维基搜索页、`cada097` 帮助页、`0651085` 维基分类页、`50f40f5` 设置页、
`9f42dfe` 项目页、`eec81dd` 导出页、`fabfc44` 维基首页收尾、`16927a8` 资源工作台）。

**§4 里逐页清单仅作历史记录**：Export/Project 等页已由前序会话完成，若你要再做，请先 `git log --oneline -15` 与
「`grep -L "from 'naive-ui'" src/LimbusModEditor.Web/src/views/*.vue`」核对**实际**剩余页，不要照抄 §4。

**唯一确定剩余的小活**：`docs/UI-REDESIGN-SPEC.md` §5.2 那句「不引入任何 UI 组件库」仍是过时结论（见 §4.8）。

---

## 1. 需求方原话（不可偏离）

> 「**重构前端设计。当前前端设计太丑了，没有发挥 webui 的优势。允许大规模改变页面结构，不必局限于侧边栏菜单设计，可以考虑引入版式组件等库，可以参考维基的设计风格。**」
> 「**要求引入组件库，内存速度限制只针对后端。**」

由此确立：
- **引入组件库是要求**（不是可选）；**前端的体积/启动速度不再是硬约束**（内存/速度约束只针对后端 C# 侧）。
- **允许大规模改页面结构**，但**功能一个都不能丢**（见 §4 保真要求）。
- 可参考维基风格（真实维基截图：`wiki-ref/*.png`、`wiki-vs-ours-persona.png`；截图已于 2026-09-19 移出仓库，归档于 `_lme-removed-20260919/tree/docs-img/`）。

---

## 2. 硬约束（违反即算失败）

| # | 约束 |
|---|---|
| 1 | **设计色只能来自 `src/styles/tokens.css`**。组件里**不得出现色值字面量**（`#xxxxxx`、`rgb()`、命名色）。库主题已在 `naiveTheme.ts` 里**运行时从 tokens 派生**，缺 token 自动回落库暗色默认。 |
| 2 | **IPC 保真**：所有 IPC 方法名、参数、调用时序**一行都不能改**（详见 §4 清单）。 |
| 3 | **深链保真**：路由 query 定位（`?container=` / `?file=` / `?table=` / `?bank=&sample=` / `?q=`）必须保持可用。 |
| 4 | **不要用 PowerShell 重写仓库文本文件**（本项目历史上出过两次中文乱码事故）。用文件编辑工具。 |
| 5 | **禁止** `git checkout/restore/reset/clean`。 |
| 6 | **禁止** `git add -A` / `git add .`；只 `git add <明确路径>`。 |
| 7 | 游戏数据**只读**（`C:\Program Files (x86)\Steam\steamapps\common\Limbus Company` 等）。 |
| 8 | 每次提交前：`type-check` **0 错** 且 `build` **成功**（§6 命令）。 |
| 9 | 提交信息用**中文**，并注明「IPC 与深链零改动」。 |
| 10 | **一次一页**：改完一页 → 跑门禁 → 单独提交 → 再下一页。不要一次铺多页（这是本任务此前失败的主因）。 |

---

## 3. 已完成的部分（不要重做）

### 3.1 外壳与设计体系（已入库）
- `feat`：**Naive UI 接入**（`94cb2e6`）——库主题在 `naiveTheme.ts` **运行时从 tokens 派生、零色值字面量**。
- `refactor`：**新外壳**（`20c2fdb`）——左侧窄活动栏 → **顶部命令栏**（品牌 + `Ctrl+K` 全局命令面板 + 右侧常驻入口）+ **工作区 tab 条**；维基区激活时金色下划线；维基内保留 `WikiShell` 侧栏。
- `style`：**设计令牌升级**（`2515333`）——排版比例/行高/字重/间距刻度/圆角/海拔阴影/动效时长；另有维基区库组件化变量（`75db197`）。
- `feat`：**`/lib-check` 组件库自检页**（`b6c317a`，`src/views/LibCheckView.vue`）——库组件在本项目暗色+tokens 主题下的回归验证，**不进工作区 tab**。改库相关的东西后建议打开它看一眼。
- **设计规格**：`docs/UI-REDESIGN-SPEC.md`（**必读**）——设计语言基线（真实维基观测 → 取值建议）、**现状审计（到文件:行）**、三套外壳候选与**推荐 A+**、**版式模式库 M1–M6**、13 视图归属表、组件与依赖建议、P0–P3 计划、自检脚本（IPC 保真比对 / 悬空 CSS 变量差集）。

### 3.2 已改用库组件的视图（6 个）
| 文件 | 提交 |
|---|---|
| `src/views/AssetsView.vue` + `src/components/SearchFilters.vue` | `16927a8` |
| `src/views/BankView.vue` | `f572a22` |
| `src/views/StaticView.vue` | `05e2033` |
| `src/views/TextView.vue` | `7fb96a2` |
| `src/views/WikiEntityPage.vue` | `8b4a9a2` |
| `src/views/WikiHomeView.vue` | `af2ddbe` + `fabfc44` |
| （自检页 `LibCheckView.vue`） | `b6c317a` |

**它们就是范式**：新页面照抄其写法（`naive-ui` 的具名导入、`NCard` 包裹、三态组件、`NTag` 胶囊、`useMessage` 反馈）。

---

## 4. 剩余工作

> **重要**：本节最初写作时（HEAD `fabfc44`）列出的 8 页，**大部分已被后续提交完成**。
> 开工前请先跑这两条命令核对**真实**剩余，不要照抄本节的清单：
> ```bash
> git log --oneline -15
> grep -L "from 'naive-ui'" src/LimbusModEditor.Web/src/views/*.vue   # 未改用库组件的视图
> ```

**截至 `9e7546b` 的实际状态**：**14/14 个视图均已库组件化**，且 `docs/UI-REDESIGN-SPEC.md` §5.2 已更正为"引入 Naive UI"。
也就是说 §4.1–§4.8 **全部完成**，本节仅作历史记录。

### 4.8 顺手小活（唯一确定剩余项，低风险）
`docs/UI-REDESIGN-SPEC.md` 的 **§5.2**（约 447–465 行）仍写着「**明确建议：不引入任何 UI 组件库**」——**已过时**。改成：
> 引入 Naive UI；主题运行时从 `tokens.css` 派生（已落地 `naiveTheme.ts`，零色值字面量；有 `/lib-check` 自检页）。
并补 5–8 行主题映射示例（tokens 变量 → 库主题字段）。**只改这一节**。

### 4.9 若将来新增页面（套路）
按 §5 的固定套路做；每页单独提交并注明「IPC 与深链零改动」。

---
### 附：本节原始清单（历史记录，多数已完成，仅供对照）


### 4.1 `src/views/ExportView.vue`（**有未提交半成品，优先处理**）
- 现状：工作区里它处于 **modified 未提交**状态（上一个会话改到一半）。
- 处理方式二选一，**先看 diff 再决定**：`git diff src/LimbusModEditor.Web/src/views/ExportView.vue`
  - 若改动方向正确且能通过门禁 → 补完并提交；
  - 若半途且难以判断 → **回退到 HEAD 版本**（`git checkout -- <file>` 只对**这一个文件**允许，或用 `git restore <file>`），然后从头按 §5 改。
- 该页要保留的 IPC：`export.plan`、`export.run`（响应含 `written`/`skipped` 逐条清单与写前校验结果）、`dialog.folderPick`、`process.start`。

### 4.2 `src/views/ProjectView.vue`
- 保留 IPC：`project.create`、`project.recent`、`project.open`、`project.save`、`scan.run`（扫描游戏资源，带 progress）、`dialog.openFile`。
- 建议：`NCard` + `NForm` + `NButton`；最近项目 → `NList`。

### 4.3 `src/views/SettingsView.vue`
- 保留 IPC：`config.read`、`config.write`（**一次一个 `{key,value}`**）、`uiState.read`、`uiState.write`、`config.autoDetect`（自动探测路径）、`dialog.folderPick`。
- 建议：`NForm` 分组 + `NInput`/`NSelect` + 保存按钮 + `useMessage()` 反馈。

### 4.4 `src/views/HelpView.vue`
- 无关键 IPC（纯静态）。建议 `NCollapse` + `NCollapseItem` + `NCard` 做文档式排版。

### 4.5 `src/views/WikiCategoryView.vue`
- 保留：`wiki.categoryIndex` 等分类数据调用、`?category` 路由参数、分页。
- 建议：`NCard` 卡片网格 + `NPagination`。

### 4.6 `src/views/WikiSearchView.vue`
- **更正（2026-09-18，由执行者核实）**：该页**原本就已套 `WikiShell`**，本节原写"没有套"**与仓库实际不符**，特此更正。
- 已完成：库组件化 + 样式收敛（`e3bbdfd`）；**未产生结构改动**。
- 遗留（原有行为，未擅改）：搜索结果仍一次性请求 `limit: 200`、**无分页**。

### 4.7 `src/views/WikiStoryView.vue`
- 保留：剧情页数据来源（`wiki.getPage` 的 story 分节/条目）、章节导航与深链。
- 建议：对话流卡片 + `NCollapse`；保持"说话人/旁白"可区分。

### 4.8 顺手小活（低风险）
`docs/UI-REDESIGN-SPEC.md` 的 **§5.2**（约 447–465 行）仍写着「**明确建议：不引入任何 UI 组件库**」——**已过时**。改成：
> 引入 Naive UI；主题运行时从 `tokens.css` 派生（已落地 `naiveTheme.ts`，零色值字面量；有 `/lib-check` 自检页）。
并补 5–8 行主题映射示例（tokens 变量 → 库主题字段）。**只改这一节**。

---

## 5. 单页改造的固定套路（照做即可）

1. **读**：目标页 + `tokens.css` + 一个已完成范式页（如 `BankView.vue`）。
2. **换容器/控件**（保持逻辑与事件处理函数不变）：
   - 输入 → `NInput`（需要时 `clearable`）、下拉 → `NSelect`、勾选 → `NCheckbox` / `NSwitch`
   - 按钮 → `NButton`（`@click` 处理函数与参数**逐字保留**）
   - 分区/卡片 → `NCard`；表格 → `NDataTable`；列表 → `NList`；树 → `NTree`（懒加载 `on-load` 保留）
   - 三态 → `NSpin`（加载）/ `NEmpty`（空）/ `NAlert`（错误）；反馈 → `useMessage()`；确认 → `useDialog()`
   - 徽标/胶囊 → `NTag`
3. **保留原有 class 名**（便于回归定位），只把容器与控件换成库组件。
4. **门禁**（§6）→ **只 add 本页文件** → 中文提交（注明「IPC 与深链零改动」）。
5. 有余力再截图（见 §7）。

---

## 6. 门禁命令（每次提交前必跑）

```bash
npm --prefix src/LimbusModEditor.Web run type-check     # 必须 0 错（exit 0）
npm --prefix src/LimbusModEditor.Web run build          # 必须成功（exit 0）
```

后端（若你碰了 C# 侧，本任务通常不需要）：
```bash
dotnet build LimbusModEditor.slnx --no-restore --nologo          # 0 错 0 警
dotnet test  LimbusModEditor.slnx --no-build  --nologo           # 当前基线：970 通过 / 0 失败
```

---

## 7. 可选的视觉取证（推荐但非必须）

仓库里原有可复用工具 `spikes/harness-tools/wiki-shot.mjs`（起静态服务 + 注入桩桥 + Playwright 截图 + 导出 DOM 计数）——**该工具已于 2026-09-19 随 `spikes/` 整体移出仓库**，归档于 `_lme-removed-20260919/tree/spikes/harness-tools/`，如需复用请从归档目录取用。
- 静态服务需 **SPA fallback**（深链 `/wiki/page/...` 才不会 404），路由是 **history 模式**。
- 截图输出到 `artifacts/harness-verify-ui/`，命名建议 `ui-v5-<page>.png`。

---

## 8. 已知失败模式（请务必规避）

1. **一次铺多页 → 超时且零产出**。本任务历史上多次出现"会话跑了几十分钟、磁盘零改动"。**必须一次一页、改完即提交**。
2. **长指令可能不投递**（WorkBuddy 侧曾出现"回合完成但 `events_total` 只增几条、无输出"）。若你通过 ACP/WorkBuddy 派活：**指令要短**，长清单写成文档（如本文档），让执行者自己读。
3. **并发改同一文件**：曾有两个会话同时改 `App.vue`/`naiveTheme.ts` 导致互相覆盖。**同一时间只让一个执行者改前端**。
4. **不要为了让门禁变绿而放宽断言/删功能**。宁可如实报告"这一页没做完"。
5. `virtual` 列表与库表格混用时注意定行高（`StaticView.vue` 曾因虚拟列表+分组切换溢出而改为直接渲染 + 上限 200）。

---

## 9. 完成后请回报

- 每页：**commit hash** + `type-check` / `build` 的实际结果
- 未完成页：卡在哪、错误原文
- 若改了 `tokens.css`：新增了哪些变量、为什么
- 报告写到 `artifacts/workbuddy-reports/ui-redesign-r5.md`（追加式）
