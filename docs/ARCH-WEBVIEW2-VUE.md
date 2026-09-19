# 架构决策记录：WebView2 宿主 + Vue3/TS 前端全量重构（ADR）

- 状态：**冻结**（2026-09-15，W0 冻结波次）
- 取代声明：**本 ADR 显式取代 `docs/WEBVIEW-FEASIBILITY.md` §5/§6 的「不建议现在做 A（全量 WebView 重写）」结论。**
  `docs/WEBVIEW-FEASIBILITY.md` §1–§4 的技术账（现状实测、纯数据家底、代价分布、WebView2 技术账）仍然有效，继续作为本决策的事实基础；§5/§6 的路线建议（C → B → 视 B 结果再决定 A）作废。
- 用户指令（高于仓库现有文档结论）：把整个项目全量重构为 **WebView2 宿主 + Vue3/TS 前端**，前端 GUI 允许大规模更改；**Spine 渲染全部前移前端（官方前端 Spine 运行时库），并移除现有 native Spine 相关逻辑**；范围口径 = **核心闭环优先**（编辑/导出/预览/写回/调试铺盘保真，浏览类界面允许按 Vue 重设计并砍低价值功能）。
- 下游唯一口径来源：本文档 + `docs/WEB-IPC-CONTRACT.md`。host-engineer / frontend-engineer / spine-engineer / relations-engineer 按本文档执行，不另行猜测。

---

## 1. 决策摘要

| 项 | 决策 |
| --- | --- |
| 目标形态 | Windows-only 的 **WebView2 宿主 + Vue3/TS 前端**桌面应用；WPF 仅保留为薄宿主壳（承载 CoreWebView2、原生对话框、剪贴板、Process.Start 回调） |
| 前端工程根 | **固定 `src/LimbusModEditor.Web/`**（Vite + Vue3 + TS + vue-router + Pinia；设计色只允许出现在单一 `src/styles/tokens.css`） |
| 契约 | 最小 IPC 契约见 `docs/WEB-IPC-CONTRACT.md`；契约 DTO 定义在 `Application` 层（无 WPF 依赖，铁律 §3-10） |
| Spine | 渲染/解析/出帧/布局全量前移前端（官方 spine-ts 运行时）；C# 侧只保留「定位并流式喂原始字节」+ 路径判据 + 写盘 |
| 范围口径 | 核心闭环优先：真实游戏数据只读 → 四个工作台编辑 → 导出加载器可消费的包 → 调试铺盘，全部保真；浏览类界面允许重设计；低价值功能允许砍掉 |
| 波次 | W0 冻结/spike → W1 网关+竖切片 → W2 全量迁移 → W3 关联维基化 → W4 终审+集成（§4） |

## 2. 为何现在做 A（取代理由）

`docs/WEBVIEW-FEASIBILITY.md` 建议「先 C（下沉逻辑）→ B（单面板试点）→ 视试点数据再决定 A」。本 ADR 承认该分析的技术判断全部成立，但决策已因以下事实变化而反转：

1. **用户已明确指令做 A，并给出范围口径**（核心闭环优先、浏览类允许重设计、低价值功能允许砍）。原分析中 A 的最大风险面——「22 个 UI 单元全要重做、与 S3c/S4 撞车」——被范围口径直接收窄：四个工作台保真迁移，其余 18 个单元允许重设计或砍掉（§5 分类表）。
2. **纯数据家底已就绪**（WEBVIEW-FEASIBILITY §2）：`AssetCatalog.Page/Count/Locate`、`AssetCatalogPage(Items,TotalCount,Offset,Take)`、`AssetSearchQuery`、`RelationDeepLink`（`'\0'` 分段纯数据）、`IReferenceRevealable.Reveal`（显式布尔契约）全部是框架无关的纯数据形状。桥接层只需做「视图状态 ↔ JSON」，不需要先补 C 步的 ViewModel 下沉——C 步的产物（Application 层服务）**已经存在**，且是全部重活的承载层。
3. **真正的痛点有解**：13k 行 App 代码 0 测试（铁律 §3-10 的根因）在「薄宿主 + Web 前端」下变成架构事实：前端可用 Playwright 驱动测试，逻辑继续留在有测试覆盖的 `Application`。
4. **格式边界零风险**：Unity/Carra/Bank/Rebank/Lunartique/FMOD 全部留在 C#，前端不碰任何格式解析；铁律 §3-1/§3-3/§3-8 的格式边界不受影响。
5. **Spine 前端化有官方路径**：spine-ts（Spine 官方 Web/TS 运行时）与现有 vendored spine-csharp 4.0 同代，游戏数据口径（spine 4.0.64）一致；移除 native 逻辑不损失任何已验证能力（§7）。

**保留的约束（不因做 A 而放松）**：铁律 §3-10（需测逻辑下沉 Application）、§3-11（文档索引同步）、§3-6（文档只用 read/edit/write）、§3-2/§3-15（不猜测、fail fast + 中文错误）、§3-3（游戏数据只读，唯一例外仍是用户显式触发的调试应用）。

## 3. 对 `docs/WEBVIEW-FEASIBILITY.md` §4.2 不利项的逐条回应

| # | 不利项 | 回应（具体措施） |
| --- | --- | --- |
| 1 | **运行时依赖**：Evergreen 依赖用户机已装；模组工具受众机器不可控，且可能离线 | **默认 Fixed Version 分发**（随包携带 WebView2 Runtime，约 +180MB，见 §6.3 体积口径），不依赖用户机状态；**启动时做 Evergreen 探测降级**——用户已装不低于所需版本的 Evergreen 则用系统运行时（省磁盘占用），否则回退随包的 Fixed Version；配置开关预留（`CoreWebView2Environment.CreateAsync(browserExecutableFolder)` 参数 + `shared-config.json` 的 `webview2Distribution` 字段，W1 定案）。缺失则给中文错误与引导（不静默失败）。离线机器因此可用（W0 spike 已实测探测与开关，`docs/SPIKE-WEBVIEW2.md` §2） |
| 2 | **Chromium 内存叠加**：基线多 ~100–200MB；当前痛点是 127 万条 → UI 冻结 | 目标口径写死：**WebView2 进程工作集增量 ≤ 200MB**（相对 WPF 基线，真实数据下采样，§8）。结构性保障：统一分页契约（200 条/页，禁止全量取再前端筛）+ 树/列表窗口化渲染 + 五个 SQLite 索引库继续留在 C# 进程（不搬前端）。 |
| 3 | **IPC 延迟**：滚动/筛选/跳转都要一次往返 | 契约固定为「查询 → 一页」（`AssetCatalogPage` 已有此形状，§2 家底）；前端做预取窗口（当前页 ±1 页）；二进制走本地虚拟主机（§6.2），不过 IPC。 |
| 4 | **中文 IME**：Chromium 输入法需正确配置 | 宿主清单启用 **PerMonitorV2** DPI 感知；`CoreWebView2EnvironmentOptions` 显式配置；W0 spike 必测项：全中文界面 + `JsonTreeEditor` 大量文本输入下的 IME 行为（候选词窗、回车提交、Escape 关闭）。 |
| 5 | **原生文件对话框/剪贴板/`ShowDialog`**：11 文件用 `Microsoft.Win32`、~30 个 `ShowDialog`、10 个 `MessageBox` | 走「原生对话框经 IPC 回调」通道（`docs/WEB-IPC-CONTRACT.md` §6）：`dialog.openFile/saveFile/folderPick/messageBox` 由宿主用 Win32 执行，保留系统外观与路径语义；**禁止**用 Web `<input type=file>` 顶替（拿不到真实路径、拖拽语义不同）。剪贴板与 `Process.Start`（5 处）同样走回调通道。 |
| 6 | **DPI / 多显示器**：两套缩放体系要对齐 | 宿主进程 PerMonitorV2；WebView2 跟随宿主 DPI（Chromium 自行处理 per-monitor）；统一缩放策略：以宿主窗口 DPI 为基准，前端不自行做 DPI 换算。W0 spike 在缩放 100%/125%/150% 与双显示器间移动窗口各测一次。 |
| 7 | 无障碍 | 现状无无障碍代码（中性），不列为目标。 |
| 8 | 调试 | DevTools 改善前端调试；JS↔.NET 跨边界调试复杂度可接受（W0 spike 验证断点与 `postMessage` 日志链路）。 |
| 9 | ⚠️ 不解决跨平台 | **记录在案**：WebView2 仍是 Windows-only；本决策不改变「Windows-only」边界，不承诺跨平台。 |
| 10 | 工具链 | 仓库新增 Node 构建链：`src/LimbusModEditor.Web/` 刻意不进 `.slnx`（与 `SpineRuntime.Tests` 同理）；一键构建命令扩展 `logs/lme.py`（§6.4）。 |

## 4. 波次划分与每波验收口径

质量门：每波 implementation → verification → review 三关；每波独立提交（铁律 §3-7），提交前 build + test 全绿。

### W0 — 冻结 / spike（本波）

- 产出：本 ADR + `docs/WEB-IPC-CONTRACT.md` 冻结；前端工程根骨架（`src/LimbusModEditor.Web/`：Vite + Vue3 + TS + vue-router + Pinia + `tokens.css`）；WebView2 宿主壳 spike（单面板试点，建议资源工作台列表）；spine-ts 选型与许可核对；构建集成骨架（§6.4 一键命令可用）。
- 五个未知量一次性打掉：运行时检测/分发、IME、DPI、对话框回调、二进制预览服务。
- **验收口径**：① 试点面板在本机真实机器跑通五未知量；② 一键构建产出含 `wwwroot/` 的 `artifacts/publish-win-x64`；③ 测试基线按 §7.3 登记为 733；④ ADR/契约经评审冻结。

### W1 — 网关 + 竖切片

- 产出：IPC 宿主网关（请求/响应/事件、二进制通道、对话框/剪贴板/`Process.Start` 回调、Reveal 深链）；资源工作台竖切片端到端（查询 → 分页列表 → 预览 → 编辑 → 保存）；前端路由 + Pinia 骨架；`tokens.css` 设计令牌。
- **验收口径**：① 资源页竖切片可编辑可保存（真实数据）；② 列表 200 条/页、窗口化渲染；③ **二进制链路端到端**：从 SQLite 索引定位缓存 bundle → 读实体化副本 → `lme.data` 喂前端（`asset.preview` 的 `binaryUrl` 走真实缓存资源，非静态测试文件）；④ **预览测试图源替换**为真实缓存 bundle 内实体化大图（≥2MB）验证 Virtual Host 大纹理性能（替换 spike 期 704KB 的 docs/wiki-front-page.png）；⑤ 全量测试不回归（≥733 全绿）。

### W2 — 全量迁移

- 产出：22 个 UI 单元按 §5 分类表迁移；四个工作台保真迁移（行为口径不变）；对话框/工具窗口走回调通道；**Spine 全量前端化**（按 §7 移除/保留清单执行，spine-ts 接入，`Application/Spine` 瘦身）。
- **验收口径**：① 核心闭环端到端保真：编辑/导出/预览/写回/调试铺盘在真实数据下与 WPF 版行为一致（既有回归测试 + 真实数据门控全绿）；② Spine 动画在前端播放（原 `SpineAnimationPreviewWindow` 场景）；③ 测试基线 733 全绿。

### W3 — 关联维基化

- 产出：预设卡片流页维基化彻底重构（人工编纂为唯一事实源、支持多级二级页面、优化内存与运行时速度）；内存与速度目标验收（§8）。
- **验收口径**：① 维基数据源结构落地、多级二级页面渲染；② §8 性能目标达标（同机同数据前后对照）。

### W4 — 终审 + 集成

- 产出：重构后审查（第二轮，对应团队委托②的「重构后」轮）；发布集成（web 产物进 `artifacts/publish-win-x64/wwwroot`）；CI；文档同步收尾。
- **验收口径**：① 一键构建产出完整发布包（含 `wwwroot/` + `fmod/`）；② 测试全绿；③ `docs/PROJECT-INDEX.md` / `docs/CODE-STRUCTURE.md` 同步完成（铁律 §3-11）。

> 团队委托②「审查残留不妥设计」两轮：**重构前轮**在 W0/W1 间执行（审查对象：WPF 时代残留，含 T-D 旧导出通道取舍），**重构后轮**在 W4。

## 5. 22 个 UI 单元迁移分类表

分类依据 = 本工具的核心闭环：**真实游戏数据只读 → 四个工作台编辑 → 导出加载器可消费的包 → 调试铺盘**。
单元清单来自 `docs/WEBVIEW-FEASIBILITY.md` §1 实测（15 个 Window + 7 个 WorkbenchPage = 22 个顶层 UI 单元）。

| # | 单元 | 分类 | 依据与迁移要点 |
| --- | --- | --- | --- |
| 1 | `MainWindow`（外壳：活动栏 7 入口 + 共享侧边栏 ①② + 页面宿主） | **重设计** | 应用外壳，Vue 重写；结构语义保留（活动栏入口、①获取资源/②产出模组、页面宿主、无项目遮罩），视觉全面重设计 |
| 2 | `AssetsWorkbenchPage`（资源工作台） | **保真迁移** | 核心闭环主战场（浏览/编辑/预览/关联资源/替换）；行为口径不变，UI 用 Vue 重实现 |
| 3 | `BankWorkbenchPage`（音频工作台） | **保真迁移** | 核心闭环（bank 树/样本表/试听/FSB 替换/bank 导出） |
| 4 | `TextWorkbenchPage`（文本工作台） | **保真迁移** | 核心闭环（lang 树/键值树就地编辑/搜索） |
| 5 | `StaticWorkbenchPage`（静态数据工作台） | **保真迁移** | 核心闭环（表索引/JSON 编辑/staticmod 产物） |
| 6 | `PresetWorkbenchPage`（预设卡片流） | **重设计** | 浏览类界面，W3 维基化重构主战场；数据源（`PresetWorkbenchService`）不动 |
| 7 | `SettingsPage`（设置页） | **保真迁移** | 配置行为口径不变（目录/项目元数据），UI 重实现 |
| 8 | `HelpPage`（内置教程） | **砍掉** | 低价值浏览；内容并入 `docs/USAGE.md` 与首次运行引导 |
| 9 | `StartupScanDialog`（启动扫描） | **保真迁移** | 每次启动必跑；行为口径不变（打开即扫、不可取消、进度、失败才留痕） |
| 10 | `UnityFieldEditorWindow`（字段树编辑） | **保真迁移** | 核心编辑能力（写回校验、指针目标提示） |
| 11 | `NewModWizardWindow`（新建模组向导） | **保真迁移** | 项目创建是导出闭环入口；只问模组名、目录默认走共享配置 |
| 12 | `UnityCachePickerWindow`（缓存目录选择） | **保真迁移** | 配置类，行为口径不变 |
| 13 | `TextAssetEditorWindow`（文本资源编辑） | **保真迁移** | 核心编辑（JSON 保存前校验并格式化） |
| 14 | `SpriteMetadataWindow`（Sprite 元数据编辑） | **保真迁移** | 核心编辑（rect/pivot/border/ppu） |
| 15 | `HexPreviewWindow`（十六进制预览） | **重设计** | 检查类工具窗口，允许按 Vue 重设计；数据源不变 |
| 16 | `ObjectSummaryWindow`（对象摘要卡） | **重设计** | 检查类工具窗口（Mesh/动画/字体等摘要） |
| 17 | `BankInspectorWindow`（bank 头/块/FSB 表检查） | **重设计** | 检查类工具窗口 |
| 18 | `FmodReportWindow`（FMOD DLL 勘察报告） | **砍掉** | 低频诊断；内容并入设置页运行时诊断区 |
| 19 | `ExportReportWindow`（旧导出报告） | **砍掉** | 旧导出通道专属（见 STATUS.md T-D 决策点：通道退役即删；若 T-D 定案「保留为兼容通道」则降级为隐藏保留，不进 UI） |
| 20 | `ExportProgressWindow`（导出/调试进度） | **保真迁移** | 核心闭环出口；取消令牌 + 关窗即取消语义必须保留 |
| 21 | `ModExportReportWindow`（新导出报告） | **保真迁移** | 核心闭环出口（槽位结果、空槽位说明） |
| 22 | `SpineAnimationPreviewWindow`（Spine 动画播放窗） | **砍掉** | native 播放窗退役；动画改由前端 spine-ts 就地播放（资源页/卡片详情的 Spine 行内） |

**统计**：保真迁移 13 / 重设计 5 / 砍掉 4 = 22。
**砍掉项的替代**：#8 → 文档 + 首次运行引导；#18 → 设置页诊断区；#19 → 随 T-D 决策退役；#22 → 前端 spine-ts 就地播放（W2）。

## 6. 前端工程根与构建集成

### 6.1 工程根与目录布局（固定）

- **前端工程根固定为 `src/LimbusModEditor.Web/`**。技术栈：Vite + Vue3 + TypeScript + vue-router + Pinia。
- 刻意**不进 `LimbusModEditor.slnx`**（与 `tests/LimbusModEditor.SpineRuntime.Tests` 同理：Node 工程与 dotnet 构建体系隔离，验收时单独 `npm run build` / `npm test`）。
- 目录布局（W0 骨架落地）：

```text
src/LimbusModEditor.Web/
├── index.html
├── package.json / package-lock.json / tsconfig.json / vite.config.ts
├── src/
│   ├── main.ts / App.vue
│   ├── styles/tokens.css        ← 设计色唯一允许出处（铁律 §9 的 Web 对应物）
│   ├── ipc/                     ← IPC 契约客户端（请求/响应/事件、二进制通道、对话框回调）
│   ├── stores/                  ← Pinia：catalog / project / ui-state / preview
│   ├── components/              ← 共享构件：TreeList、PreviewPane、PageHost、DialogHost、VirtualList
│   └── views/                   ← 页面：assets / bank / text / static / presets / settings
└── dist/                        ← npm 构建产物（git 忽略）
```

- **设计色铁律（Web 版）**：设计色只允许出现在 `src/styles/tokens.css`；`src/views/` 与 `src/components/` 下硬编码色值必须为 0（取代原 `WorkbenchPages/` 收口验收门，验收方式不变）。

### 6.2 宿主壳与二进制通道

- App 项目瘦身为 WebView2 薄宿主：`MainWindow` 承载 `CoreWebView2`；原生对话框/剪贴板/`Process.Start` 走 IPC 回调（契约 §6）。
- 二进制通道用 `SetVirtualHostNameToFolderMapping`（**禁止 base64 过 IPC**，大纹理下必成新瓶颈）：
  - `lme.app` → `<程序目录>/wwwroot`（前端静态资源，即 `dist/` 发布落点）；
  - `lme.data` → 只读数据根（缓存 bundle 实体化副本、项目 `sources/`、替换文件、Spine 三件套原始字节）。
- 启动导航：`https://lme.app/index.html`。

### 6.3 构建产物布局与发布集成

- npm 构建：`npm --prefix src/LimbusModEditor.Web run build` → `src/LimbusModEditor.Web/dist/`。
- dotnet 构建：`dotnet build LimbusModEditor.slnx`（不含 Web 工程）。
- 发布：`dotnet publish src/LimbusModEditor.App/... -o artifacts/publish-win-x64` 后，**把 `dist/` 复制进 `artifacts/publish-win-x64/wwwroot/`**（扩展 `scripts/publish.ps1` 第 2 步，与 fmod DLL 复制并列；`lme.py publish` 同步扩展）。
- 体积口径：当前发布基线 **519MB**（`artifacts/publish-win-x64`，含 5 个 SQLite 索引库）；Fixed Version WebView2 运行时约 **+180MB**；**目标上限 ≈ 700MB**，验收以 publish 后目录总大小为准（W0 建立基线，W4 终测）。若目标机已装 Evergreen 且探测命中，实际体积可低于该上限（运行时用系统已装版本，不随包重复携带）。

### 6.4 一键构建命令（固定）

```text
python logs/lme.py build     # 扩展为：npm run build（Web）→ dotnet build LimbusModEditor.slnx
python logs/lme.py publish   # 扩展为：npm run build → dotnet publish → 复制 dist/ → artifacts/publish-win-x64/wwwroot/ → 复制 fmod/
python logs/lme.py test domain|format   # 不变（dotnet 侧测试）
```

- `logs/lme.py` 的 dotnet 显式解析（`LME_DOTNET` → 常见路径）与「test 前先 build」纪律保持不变；Web 构建步骤插在 dotnet 之前，失败即中止（不静默跳过）。
- 前端测试：`npm test`（Vitest + Playwright，Playwright 驱动宿主做端到端验收）——W0 骨架落地，W1 起接入竖切片。

## 7. Spine 全量前端化：移除 / 保留清单（逐工程逐文件）

原则：**渲染/解析/出帧/布局全量前移前端（官方 spine-ts 运行时，与 vendored spine-csharp 4.0 同代，游戏数据口径 spine 4.0.64 不变）；C# 侧只保留「定位并流式喂原始字节（骨架二进制 + atlas 文本 + 纹理）」能力、路径判据、写盘。导出目标清单由前端决定、写盘由 C# 执行。**

### 7.1 删除清单

> **✅ 已于 2026-09-19 执行完毕**。其中 `third_party/spine-csharp/` 因 **Spine Runtimes License 属非开源许可**，
> 除从工作区删除外，还从**全部 git 历史**中抹除（`git filter-repo --invert-paths`）。下表为执行时的清单。

| 工程 | 文件 | 说明 |
| --- | --- | --- |
| `src/LimbusModEditor.SpineRuntime/` | `SpineDocument.cs`、`SpineFrameRenderer.cs`、`ISpineTextureSource.cs`、`SkiaTextureLoader.cs`、`SpineResults.cs`、`LimbusModEditor.SpineRuntime.csproj` | **整工程删除**（vendored spine-csharp + SkiaSharp 离线渲染，随工程移除） |
| `third_party/spine-csharp/` | `src/**/*.cs` + `LICENSE` + `README.md` | vendored 源码，随 SpineRuntime 一并清理（其编译入口是该工程 csproj 的 `<Compile Include>`，工程删即失效） |
| `tests/LimbusModEditor.SpineRuntime.Tests/` | `SpineRuntimeTests.cs`（7 例）、`SyntheticData.cs`、`TestTextureSource.cs`、`LimbusModEditor.SpineRuntime.Tests.csproj` | **整工程删除**（7 例随工程消失） |
| `src/LimbusModEditor.App/` | `SpineAnimationPreviewWindow.cs`（含内部 `SourceTextureSource` 适配类） | native 播放窗退役；动画改前端 spine-ts 就地播放 |
| `src/LimbusModEditor.App/` | 相关调用点（code-behind）：`AssetsWorkbenchPage.xaml.cs` 的 `BuildSpineView`/`PreviewSpineAnimation`（约 1601–1664 行）、`PresetWorkbenchPage.xaml.cs` 的 `PreviewSpineAnimation`（约 772–785 行） | 「▶ 动画预览…」按钮改为触发前端就地播放（W2）；「导出…」按钮保留（走 `SpineExportService`） |
| `src/LimbusModEditor.App/` | `LimbusModEditor.App.csproj` 第 22 行 `SpineRuntime` ProjectReference | 随工程删除移除 |
| `src/LimbusModEditor.Application/Spine/` | `SpineModels.cs`（`SpineTextParser` 解析） | 解析前移前端（spine-ts 解析骨架/图集文本）；后端不再解析 |
| `src/LimbusModEditor.Application/Spine/` | `SpinePreviewService.cs`（`Build`/`ParseAsset`/`DrawRegionBoxes` 布局叠加图） | 布局/渲染逻辑前移前端；**其中 `SpineSiblingIndex`（纯定位：`FolderOf`/`FileNameOf`/`Siblings`）保留并迁移为独立文件 `SpineSiblingIndex.cs`**（见 7.2） |
| `src/LimbusModEditor.Application/Spine/` | `SpinePreviewProvider.cs` | native 预览 provider 退役（前端 spine-ts 直接渲染，不再需要后端出图） |
| `src/LimbusModEditor.Application/Assets/Preview/` | `AssetPreview.cs` 中 `CreateDefault` 的 `SpinePreviewProvider` 注册（约 106–116 行可选参数） | 随 provider 删除移除 |
| `tests/LimbusModEditor.Domain.Tests/` | `SpineTests.cs`（13 例：解析/布局/路径粗筛） | 随解析/布局逻辑删除；其中 `SpineSiblingIndex` 的 `FolderOf`/`FileNameOf` 语义由 7.2 迁移落地时补 2 条等价测试 |

### 7.2 保留清单

| 工程 | 文件 | 保留能力 | 改造点 |
| --- | --- | --- | --- |
| `src/LimbusModEditor.Application/Spine/` | `SpineAnimationSourceService.cs` | **定位并流式喂原始字节**：`SpineAnimationSource`（`SkeletonJson` + `AtlasText` + `PageBytes(页名)` + `AvailablePages`） | 构造函数由 `SpinePreviewService` 改为依赖 `SpineSiblingIndex`；语义不变（同目录找齐三件套、失败给中文原因、绝不抛） |
| `src/LimbusModEditor.Application/Spine/` | `SpineExportService.cs` | **写盘**：三件套原样落盘到 `<目标>/<目录名>/`（`FolderNameFor` 目录名净化） | 构造函数改为依赖 `SpineSiblingIndex`；**导出目标清单由前端决定**（前端给资产列表，C# 执行写盘）；不再解析内容 |
| `src/LimbusModEditor.Application/Spine/` | `SpineSiblingIndex.cs`（**新文件**，从 `SpinePreviewService.cs` 迁出） | 纯定位：`FolderOf`/`FileNameOf`/`Siblings`（按容器目录建表，O(1) 取） | 无解析、无渲染；带原索引缓存语义（引用相等 + 数量变化 + 15s 超时） |
| `src/LimbusModEditor.Application/Relations/` | `RelationDisplayRules.cs` 的 `IsSpinePath` | **路径判据**（`Prefab/SpineIllustPrefab/`、`.psb`、`SkeletonData`、`/Story/Spine/`） | 不动；前端经 IPC 使用（资产行携带判据结果或专用轻量判定请求），**不复制判据到前端** |
| `tests/LimbusModEditor.Domain.Tests/` | `SpineAnimationSourceServiceTests.cs`（6 例） | 保留能力测试 | 仅 ctor 适配（`new SpinePreviewService(() => list)` → `SpineSiblingIndex` 等价构造） |

### 7.3 测试基线变化与 docs 同步点

- **测试基线**：当前自述 **746 项全绿**（Domain 672 + Format 74，solution 口径；`SpineRuntime.Tests` 7 例刻意独立、不计入）。
  - 删除 `SpineRuntime.Tests` 7 例（随工程）；
  - 删除 `SpineTests.cs` 13 例（随解析/布局逻辑）；
  - 保留 `SpineAnimationSourceServiceTests.cs` 6 例（ctor 适配）；
  - **新基线 = 733（Domain 659 + Format 74）**，W0 冻结时写入 `docs/STATUS.md` §2。
- **docs 同步点**（铁律 §3-11，W2 执行时逐条改）：
  - `docs/PROJECT-INDEX.md`：§5.2（SpineRuntime 标记删除）、§8.15（Spine 段落瘦身）、§9.3（`SpineAnimationPreviewWindow` 标记删除）、§11.6（SpineRuntime.Tests 标记删除）、新增 §15 登记本 ADR 与 IPC 契约；
  - `docs/CODE-STRUCTURE.md`：§2 项目表、§3 目录地图 Spine 行、§6 不变量 §6-27~29 改写、§10 常用命令；
  - `THIRD-PARTY-NOTICES.md`：§1 spine-csharp 与 §2 SkiaSharp 条目移除，新增 spine-ts（前端运行时）条目；
  - `docs/USAGE.md`：Spine 动画播放章节改为前端就地播放；
  - `docs/STATUS.md`：§2 基线数字 746 → 733。

## 8. 内存与速度目标口径与验收方法

**基线（2026-09 本机实测，直接引用 `docs/STATUS.md` §4）**：

| 项 | 基线值 |
| --- | --- |
| 资源规模 | **1,275,623 条资产** / **1,459 个缓存 bundle**（1,531 bank） |
| 发布体积 | **519MB**（`artifacts/publish-win-x64`，含 5 个 SQLite 索引库） |
| 启动扫描 | 冷 ≈295s（一次性）/ 热 45.8s（索引全命中） |
| 列表查询 | 全量搜索过滤+排序 4.8s（后台线程）；「查询→一页」200 条 |
| UI 响应 | 既有痛点：127 万条下 UI 冻结（S1–S4 已修，见 STATUS.md §6.2.1） |

**目标口径**：

| 维度 | 目标 |
| --- | --- |
| 内存 | WebView2 进程工作集增量 **≤ 200MB**（相对 WPF 基线，真实数据下采样）；不新增 UI 冻结（交互响应 < 2s） |
| 速度 | 启动扫描耗时**不变**（C# 侧不动）；「查询→一页」渲染 ≤ 500ms（200 行）；树构建/全展开不劣于基线（0.47s / 6.3s） |
| 体积 | 发布 ≤ 519MB + Fixed Version WebView2 运行时（约 +180MB），上限 ≈ 700MB |

**验收方法**：

1. W0 建立采样基线：本机真实数据下记录 WPF 版工作集（主进程）与关键场景墙钟（启动扫描/全量搜索/树全展开/导出计划生成）；
2. W3 末同机、同数据、同组场景做 WebView2 版对照采样；
3. 验收门写进 W3：三项指标全部达标才允许进入 W4；任一不达标则回退该优化项并重测（不允许带病发布）。

## 9. 下游分工与口径边界

| 角色 | 职责 | 口径来源 |
| --- | --- | --- |
| host-engineer | WebView2 宿主壳、IPC 网关、二进制通道、对话框/剪贴板/`Process.Start` 回调、构建集成 | 本文档 §6 + `docs/WEB-IPC-CONTRACT.md` |
| frontend-engineer | `src/LimbusModEditor.Web/` 全部前端工程（Vue3/TS/Pinia/vue-router/tokens.css） | 本文档 §5/§6 + IPC 契约 |
| spine-engineer | spine-ts 接入、Spine 行/预览/播放前端化、按 §7 清单执行 C# 侧移除/保留 | 本文档 §7 |
| relations-engineer | W3 关联维基化（人工编纂唯一事实源、多级二级页面、内存与速度目标） | 本文档 §4 W3 + §8 |

**口径边界**：本 ADR 与 IPC 契约是下游唯一口径来源；任何与本文档冲突的既有文档结论（含 `docs/WEBVIEW-FEASIBILITY.md` §5/§6）以本文档为准；既有文档中与本文档不冲突的部分（铁律、格式边界、真实环境事实、性能基线）继续有效。

## 10. 风险与回退

| 风险 | 等级 | 缓解 |
| --- | --- | --- |
| WebView2 运行时在目标机器缺失/损坏 | 中 | Fixed Version 随包分发（§3-1）；启动检测 + 中文错误引导 |
| 内存叠加触发 UI 冻结（历史痛点） | 中 | 统一分页 + 窗口化渲染 + 目标口径 ≤200MB 增量（§8）；W3 验收门 |
| spine-ts 与 spine-csharp 4.0 口径差异（解析/渲染细节） | 中 | W0 许可与口径核对；真实数据样本（`StorySpine_Sinclair/cg_40` 三件套）做前后对照 |
| 中文 IME 在 WebView2 下行为异常 | 中 | W0 spike 必测；PerMonitorV2 清单 |
| 迁移期与既有修复撞车（S3c/S4 同批文件） | 低 | 本决策在 S3c/S4 之后执行；W0 起四个大文件不再有并行战线 |
| 回退 | — | 波次可回退：W1 试点失败则回到「WPF 壳 + 单面板 WebView2」混合形态（原 WEBVIEW-FEASIBILITY 方案 B），契约与二进制通道资产保留可用 |
