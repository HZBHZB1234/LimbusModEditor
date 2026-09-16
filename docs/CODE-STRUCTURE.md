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
> - **WebView2+Vue 全量重构 ADR（取代 WEBVIEW-FEASIBILITY §5/§6）**：`docs/ARCH-WEBVIEW2-VUE.md`
> - **最小 IPC 契约（请求/响应/事件、分页、二进制通道、对话框回调）**：`docs/WEB-IPC-CONTRACT.md`
> - **W0 spike 实测结论（五个未知量、运行时/二进制通道证据）**：`docs/SPIKE-WEBVIEW2.md`
> - 架构反模式与仓库卫生审计（R1）：`docs/AUDIT-2026-R1.md`
>
> 本文所有结论来自源码阅读（read/grep），不依赖运行程序、探针或截图。
> 文中行号为撰写时的真实行号，改代码后请以符号名检索为准。

> ⚠️ **章节新鲜度声明（2026-09-17 交付轮加注，务必先读）**
>
> 本文书写于 **WPF 时代**，其中**描述旧 WPF 界面外壳与页面层的章节已不再是当前实现**，
> 保留仅为存档决策上下文。**已按重构后事实同步**：§1（总览）、§2（分层/依赖表/新增服务）、
> §3（目录地图的界面与 Spine 两行）、§6（不变量与新增两条）、§10（命令与基线）。
> **仍为 WPF 时代原文、不可按字面执行**：§4（三列页面模型 / `MainWindow.xaml` / `WorkbenchPages/` 五页）、
> §5（启动与导出流程中的 `MainWindow.*` 调用点）、§7（部分落盘说明）、§8（症状定位表里的 `App/WorkbenchPages/*`
> 与 `Application/Spine/*` 路径）、§9（含 `SpineRuntime` 的边界条目）。
> 这些章节指向的文件**已被删除**（22 个 UI 单元 + Spine native 工程，见 `docs/ARCH-WEBVIEW2-VUE.md` §7）。
> 当前的界面外壳与运行流程请读：`docs/ARCH-WEBVIEW2-VUE.md` + `docs/WEB-IPC-CONTRACT.md` +
> `src/LimbusModEditor.Web/src/`（前端）与 `src/LimbusModEditor.App/`（8 文件薄宿主）；
> 逐文件索引见 `docs/PROJECT-INDEX.md`（其中 §5.2/§9/§11.6 同样是 WPF 时代遗留，见该文同步标记）。
> **债务登记**：上述章节的逐段重写尚未完成（已登记在 `docs/FINAL-DELIVERY.md` §已知缺口 G-07）。

---

## 1. 一句话总览

Limbus Mod Editor 是一个 **Windows-only 的 C#/.NET 8 工作台（WebView2 薄宿主 + Vue3/TS 前端）**，
用于给《Limbus Company》做模组：读取真实游戏数据（Unity 缓存 bundle / FMOD bank /
lang 文本 / 静态数据表）→ 在 **9 个工作台视图**里浏览与编辑（资源 / 音频 / 文本 / 静态数据 /
预设 / 项目 / 导出 / 设置 / 帮助）+ **维基化关联页**（4 个维基视图）
→ 一键导出加载器可消费的
模组包（`.carra` / `.bank` / `.rebank` / `.staticmod` / lang 补丁）→ 或直接铺到游戏
目录做调试（带逐文件备份与还原）。

界面全部在前端（`src/LimbusModEditor.Web/`，由 WebView2 承载），**Spine 渲染也在前端**；
C# 侧只剩「薄宿主 + 服务编排」。分层与依赖见 §2，架构决策见 `docs/ARCH-WEBVIEW2-VUE.md`。

它不是启动器替代品：**游戏目录只读、Unity 缓存只读、catalog 只读**（唯一例外是用户
显式触发的调试应用，见 §6）。

---

## 2. 项目分层与依赖方向

`LimbusModEditor.slnx` 声明 **12 个 src 项目 + 3 个测试项目**
（另有 `src/LimbusModEditor.Web/`，**Node 工程、刻意不进 `.slnx`**，见下）。
依赖方向严格单向（下层永不引用上层）：

```
                 ┌──────────────────────────────────────┐
                 │  App（WebView2 薄宿主, net8.0-windows）│ ← 只承载 CoreWebView2
                 │  + NativeBridgeService（对话框/剪贴板/ │    原生对话框/剪贴板/
                 │    Process.Start/日志）                │    Process.Start 回调
                 └──┬──────┬───────┬────────┬────────────┘
                    │      │       │        │
                    ▼      ▼       ▼        ▼
             Application Infra  Editing Formats.Unity
            （编排/服务）(路径) （图像） （Unity 后端）
                    │      │       │        │
   ┌────────────────┼──────┴───────┴────────┴──────────────────────┐
   ▼            ▼              ▼              ▼                    ▼
Formats.Carra Formats.Bank Formats.Rebank Formats.Lunartique Formats.Abstractions
                                                                      │
                                                                      ▼
                                     Domain（共享基础：模型 + 日志约定）

  ✗ 已删除（W2，2026-09-15）：LimbusModEditor.SpineRuntime（native Spine 渲染）、
    Application/Spine/ 的解析与渲染、App 的 WPF 页面层（WorkbenchPages/ 等 22 个 UI 单元）。
  ✓ 前端（Node 工程）：src/LimbusModEditor.Web/ —— Vite + Vue3 + TS，承载全部界面与
    Spine 渲染（spine-webgl@4.0.26）；由 App 的 PublishFrontend Target 复制进 wwwroot/。
```

| 项目 | TFM | 关键 NuGet | 角色 |
|---|---|---|---|
| `LimbusModEditor.Domain` | net8.0 | NLog 6.2.0 | 全仓库共享数据模型与纯数据规则（资产、编辑、格式枚举、导出布局、项目文档）+ 日志调用约定（`Diagnostics/LoggerExtensions.cs`，见 §5.4） |
| `LimbusModEditor.Formats.Abstractions` | net8.0 | 无 | 格式插件唯一接口契约 `IModFormatHandler` |
| `LimbusModEditor.Infrastructure` | net8.0 | 无 | 基础实现（当前只有 `SafePathService`，且**未被调用**，见 §9） |
| `LimbusModEditor.Editing` | net8.0 | ImageSharp 3.1.11 + NLog | Unity 纹理 ↔ PNG 编解码、图集切分/回填、缩略图 |
| `LimbusModEditor.Formats.Unity` | net8.0 | AssetsTools.NET 3.0.5 + NLog | Unity bundle/SerializedFile 唯一后端（不手写解析器） |
| `LimbusModEditor.Formats.Carra` | net8.0 | Joveler.Compression.XZ 5.0.2 + SharpCompress 0.38.0 + NLog | Carra/Carra2 容器 |
| `LimbusModEditor.Formats.Bank` | net8.0 | NLog（P/Invoke 运行时绑 FMOD C ABI） | FMOD bank/FSB5 索引层 + 音频编解码抽象 |
| `LimbusModEditor.Formats.Rebank` | net8.0 | NLog | .rebank 差分包 |
| `LimbusModEditor.Formats.Lunartique` | net8.0 | SharpCompress 0.38.0 + NLog | Lunartique 安装/卸载配对包 |
| `LimbusModEditor.Application` | net8.0 | Microsoft.Data.Sqlite 8.0.11 + System.IO.Hashing 8.0.0 + ImageSharp + NLog | WPF 无关的服务编排层；Hashing 用于 IEEE CRC32 流式校验（**能被直接单测**）。除既有分组外，**W1–W3 新增三组**：`Ipc/`（IPC 网关与契约 DTO，**49 个派发方法名 / 47 个独立处理器**，见下）、`Relations/Authority/`（16 种权威来源的推断引擎）、`SpineData/`（Spine 原始字节提取网关） |
| `LimbusModEditor.App` | **net8.0-windows** | **Microsoft.Web.WebView2** + NLog + **WPF-UI 4.3.0（仍在，未被移除）** | **WebView2 薄宿主：8 个 `.cs` 源文件**（`WebView2MainWindow` / `NativeBridgeService` / `TextService` / `LogHost` / `StartupTrace` / `UiHeartbeat` / `App.xaml.cs` / `AssemblyInfo.cs`）+ 2 个在用 XAML（`App.xaml`、`WebView2MainWindow.xaml`）+ `app.manifest`。界面全在前端工程；宿主只提供 CoreWebView2 承载 + 原生对话框/剪贴板/`Process.Start` 回调 + `PublishFrontend` Target（**没有测试工程**，逻辑要下沉到 Application 才能测）。⚠️ **2026-09-17 交付轮实测更正**：`WPF-UI 4.3.0` **仍是 PackageReference**（`App.xaml` 的 `ui:ThemesDictionary`/`ui:ControlsDictionary` 与 `WebView2MainWindow.xaml` 的 `ui:FluentWindow`/`ui:TitleBar` 在用，宿主外壳依赖它）；`Themes/Theme.xaml`(76 行) 与 `Themes/WorkbenchStyles.xaml`(443 行) **文件仍在**，被 `App.xaml` 合并进 `Application.Resources`，但**已无任何消费点**（旧 WPF 页面层删除后成为死资源，`WebView2MainWindow.xaml` 未引用任何 `Wb*` 键）→ 见 `docs/FINAL-DELIVERY.md` 已知缺口 G-08 |
| `LimbusModEditor.Cli` | net8.0 | NLog | 三个子命令的命令行入口 |
| `src/LimbusModEditor.Web/`（**2026-09-15 起，刻意不进 `.slnx`**） | Node（npm） | Vite 6 + Vue 3.5 + TS 5.6 + vue-router 4 + Pinia 2 + **`@esotericsoftware/spine-webgl@4.0.26`**（Spine Runtimes License，权威文本见官网 https://esotericsoftware.com/spine-runtimes-license；许可副本随发布包进 `wwwroot/licenses/`） | **前端工程根（固定）**：WebView2 承载的全部前端（9 工作台视图 + 4 个维基视图）；设计色只允许出现在 `src/styles/tokens.css`；契约见 `docs/WEB-IPC-CONTRACT.md`，架构见 `docs/ARCH-WEBVIEW2-VUE.md` |
| `tests/LimbusModEditor.Domain.Tests` | net8.0 | xunit 2.9.3 | 行为测试（94 例，2026-09-17 实测）。**只引用 Domain / Editing / Formats.Unity —— 对 Application 的反向引用已解除（审计 F-02 真正关闭，t48）** |
| `tests/LimbusModEditor.Format.Tests` | net8.0 | xunit 2.9.3 + AssetsTools.NET | 格式层与真实样本测试（74 例） |
| `tests/LimbusModEditor.Application.Tests` | net8.0 | xunit 2.9.3 | **新建（t42）**：服务层测试（IPC 网关、维基页面树/查询、权威引擎、关系分析等），696 例 |

### 2.1 Application 层重构后新增/变更的服务与第三方（2026-09-17）

| 位置 | 服务 / 类型 | 职责 | 接线状态 |
|---|---|---|---|
| `Application/Ipc/` | `IpcGateway.cs` | 单一 dispatch 入口（**49 个派发方法名**，2026-09-17 交付轮实测：`grep -oE '"[a-zA-Z]+\.[a-zA-Z.]+" =>'` = 49；其中 `wiki.*` 10 个，`wiki.categoryIndex`/`wiki.category.load` 与 `wiki.page.load`/`wiki.getPage` 是两组别名 → **47 个独立处理器**），请求/响应/事件三向信封、统一分页、错误码、协作式取消 | ✅ 宿主 `NativeBridgeService` ↔ 前端 `ipc/client.ts` |
| `Application/Ipc/` | `IpcDtos.cs` / `IpcEnvelope.cs` / `ProjectState.cs` | 契约 DTO（含 wiki 段）、信封与错误码、组合根（服务装配） | ✅ |
| `Application/Relations/` | `WikiPageStore` / `WikiPageQueryService` / `WikiEditService` / `WikiPageModels` | 维基页面库（`cache/wiki-pages.db`，4 层树 + CASCADE 删父）、分页查询门面、内容编辑 | ✅ 被 IPC 处理器使用 |
| `Application/Relations/` | `WikiPageArranger`（t12）/ `WikiSectionDefinition` / `WikiSecondaryPageRules` | 页面编排、分节表、二级页拆分判据（支持一对象多二级页） | ⚠️ **无生产调用点** |
| `Application/Relations/Authority/` | `WikiPageAuthorityEngine` + 7 个 `*AuthorityProvider` + `AuthorityFact`/`AuthoritySource`/`ConfidenceLevel`/`IAuthorityProvider` | 16 种权威来源 → 事实；三档置信度；`WritableSourceKind`（`Unknown`/`None`/`Path`）判可写性；**禁止 id 数字窗口猜测** | ⚠️ **仅被测试覆盖，未接入 IPC/UI** |
| `Application/SpineData/` | `ISpineDataGateway` / `SpineDataGateway` / `SpineAssetLocator` / `SpineAssetEnumerator` / `SpinePathRules` / `SpineRawData` | Spine 原始字节提取（三件套定位 + 索引枚举 + 流式喂字节），替代已删除的 `Application/Spine/` 解析职责 | ✅ 被 `spine.locate` / `spine.export` 使用 |
| 前端 | `spine-webgl@4.0.26`（第三方） | 骨骼动画渲染全部在前端（JSON 主路径 + 二进制兜底 + `pma:true` 预乘 alpha） | ✅ `SpineRenderer.vue` + `spine-runtime.ts` |

**依赖原则：优先用成熟第三方库，分层不构成拒绝引库的理由**（铁律 §3-12）。通用能力
（日志、压缩、图像、Unity 解析、SQLite）一律用库；自研只留「本工具特有编排」。
`Domain` 因此不再是「零 NuGet」：它现在是**唯一被全仓库引用的叶子程序集**，
日志调用约定（NLog 的 `Logger` 扩展）放在这里，Formats.*/Editing 这些底层项目才能直接用；
这条依赖是**刻意登记**的，不是漏网。

构建约定（`Directory.Build.props`）：`net8.0`（WPF 项目覆盖为 `net8.0-windows`）、
`Nullable=enable`、`TreatWarningsAsErrors=true`（**任何警告即失败**）、
Windows 下默认 `RuntimeIdentifier=win-x64`、`CopyLocalLockFileAssemblies=true`。

---

## 3. 目录地图（先记这 10 条）

| 想看什么 | 去哪里 |
|---|---|
| 数据模型 / 枚举 / 导出目录布局 | `src/LimbusModEditor.Domain/`（`Assets/` `Edits/` `Formats/` `Projects/`） |
| Unity bundle 读写、容器路径、纹理/Sprite/文本/音频对象 | `src/LimbusModEditor.Formats.Unity/AssetsToolsBackend.cs`（2053 行，唯一后端） |
| 业务编排（扫描、索引、搜索、预览、编辑、导出、调试） | `src/LimbusModEditor.Application/`（按功能分子目录） |
| 界面与页面（全部在 WebView2 内） | `src/LimbusModEditor.Web/src/`（`views/` 9 工作台 + 4 维基视图、`components/`、`stores/`、`ipc/`、`router/`）；宿主只提供 CoreWebView2：`src/LimbusModEditor.App/WebView2MainWindow.xaml.cs` |
| 格式包（Carra/Bank/Rebank/Lunartique） | `src/LimbusModEditor.Formats.*/` |
| 缓存库（五个 SQLite：四源 + 一派生） | `src/LimbusModEditor.Application/Caching/` + `Scanning/` + `Texts/` + `StaticMods/` + `Assets/BankIndex*` + `Relations/` |
| 跨资源关联（6 类别对象 ↔ 资源） | `src/LimbusModEditor.Application/Relations/`（`RelationCategories` 六类别 / `SubjectRelationAnalyzer` / 派生缓存 `RelationStore` / 查询门面 / 卡片流展示层 `PresetWorkbenchService` / 精确跳转载荷 `RelationDeepLink`） |
| Spine：解析 / 静态预览 / 导出 / 找素材 | **W2 起已改为前端承担解析与静态预览**（`src/LimbusModEditor.Web/src/components/spine-runtime.ts`）；C# 侧只剩 `src/LimbusModEditor.Application/SpineData/`（`SpineAssetLocator` 定位 + `SpineAssetEnumerator` 枚举 + `SpinePathRules` 判据 + `SpineDataGateway` 流式喂字节）与导出写盘 `Application/Spine/SpineExportService.cs`。**`Application/Spine/` 的 `SpineModels`/`SpinePreviewService`/`SpineAnimationSourceService` 已删除**（逐文件清单见 `docs/ARCH-WEBVIEW2-VUE.md` §7） |
| Spine：骨骼动画**真播放** | `src/LimbusModEditor.Web/src/components/SpineRenderer.vue`（**spine-webgl@4.0.26**，JSON 主路径 + 二进制兜底 + `pma:true`）。**native 渲染工程 `LimbusModEditor.SpineRuntime`、`App/SpineAnimationPreviewWindow` 已于 W2 删除，全仓 0 命中** |
| Spine：原始字节提取网关（W2 新增） | `src/LimbusModEditor.Application/SpineData/`（`SpineRawData.cs` 载荷 + `ISpineDataGateway.cs`/`SpineDataGateway.cs` 网关（无 WPF）+ `SpinePathRules.cs` 路径判据 + `SpineAssetLocator.cs` 索引定位 + 网关工厂）→ 替换 `SpineAnimationSourceService` 的解析职责，前端 spine-ts 直接消费字节 |
| 导出流水线 | `Domain/Formats/ExportLayout.cs` + `Application/Build/ModExportPlanService.cs` + `ModPackExportService.cs` |
| 调试应用（写游戏目录） | `Application/Debugging/ModApplyService.cs` + `StaticModApplyService.cs` + `DebugApplyService.cs` |

---

## 4. 界面外壳：三列页面模型（plan-02）

`App/MainWindow.xaml` 是唯一的窗口骨架，三列布局：

```
[48px 活动栏] [220px 共享侧边栏] [* 页面宿主 PageHost]
   7 个 RadioButton         项目工作区 + ① 获取资源 / ② 产出模组 / 更多      ContentControl
```

- **页面宿主与注册表**：`MainWindow.xaml.cs:48` 的
  `PageOrder = ["assets","bank","text","static","presets","help","settings"]`；
  `ShowPage(string key):133` 惰性创建并**常驻**（切换不销毁，保住各页的搜索/选中/预览状态）；
  工厂是 `CreatePage(key):174` 的 switch。新增页面的标准做法 =
  在 `CreatePage` 注册 key + 在 `MainWindow.xaml` 活动栏加一个 `RadioButton` + `SyncActivityBar:196` 补映射
  + `NeedsProject:213` 决定是否要项目。
- **跨页搜索跳转**：`ShowWorkbenchSearch(pageKey, keyword):159` 切页后，若目标页实现
  `ISearchableWorkbench` 就把关键词送进去自动过滤（目标页不支持时只在状态栏说明，不报错）。
- **跨页精确跳转（plan-11 深化）**：`RevealReference(pageKey, payload, fallbackKeyword)` 优先让目标页
  按**精确载荷**（`RelationDeepLink` 口径）选中并滚到那一行；目标页没实现 `IReferenceRevealable`
  或 `Reveal` 返回 false 时，**退化为** `ShowWorkbenchSearch(pageKey, fallbackKeyword ?? KeywordOf(payload))`
  （至少把关键词填好）。载荷 → `App/WorkbenchPages/IReferenceRevealable.cs`；四页实现：
  `AssetsWorkbenchPage`（容器路径）/ `BankWorkbenchPage`（bank+样本名）/ `TextWorkbenchPage`（相对路径+键路径）/
  `StaticWorkbenchPage`（容器路径+记录键）。**`Reveal` 不得抛异常**（关联图是旁路）。
- **宿主契约**：`App/WorkbenchPages/IWorkbenchHost.cs`。页面通过构造函数拿 `IWorkbenchHost`
  （项目、项目文件、`AppEnvironment`、`LangEdits`/`StaticEdits` 编辑集会话、状态栏、
  刷新、保存、`ShowPage`/`ShowWorkbenchSearch`/`RevealReference`），**不允许反向依赖 `MainWindow` 具体类型**。
- **无项目遮罩**：`NoProjectOverlay`（`MainWindow.xaml:152`）只遮页面宿主；
  `NeedsProject(key)`（`MainWindow.xaml.cs:213`）决定哪些页面需要项目，设置/教程页始终可用。
- **启动顺序**（性能敏感）：构造函数只做 `InitializeComponent` + 建资源页；
  所有磁盘动作（目录定位、启动扫描、模态窗）排在 `Loaded` 且
  `DispatcherPriority.Background` 之后（`MainWindow.xaml.cs:117-126`），
  并有 `StartupTrace`（`App/StartupTrace.cs`）埋点。**不要把秒级工作提前到窗口出现之前**。
- **共享侧边栏**是设计约束：`① 获取资源`（自动加载游戏资源 = 启动扫描入口）与
  `② 产出模组`（**只有两个按钮**：`导出模组…`、`使用当前修改启动游戏进行调试`）在
  `MainWindow.xaml:109-146`。lang 的独立导出按钮已删除，导出统一走「导出模组…」。
- **UI 状态持久化**：`Application/AppConfig/UiStateService.cs` 把每页的预览列宽写
  `<程序目录>/config/ui-state.json`（页面 key 见 `WorkbenchPageKeys`），
  与 `shared-config.json` 同风格（原子写 + 损坏回退）。**UI 状态绝不写进 `.lmeproj`**。

### 五个工作台页与公共骨架

| 页面 | 文件 | 职责 |
|---|---|---|
| 资源 | `App/WorkbenchPages/AssetsWorkbenchPage.xaml(.cs)` | 容器路径树/列表 + 搜索筛选排序 + 八形态预览（含 Spine）+ 属性编辑 + **关联资源区** + 替换/导入 |
| 音频 | `WorkbenchPages/BankWorkbenchPage.xaml(.cs)` | bank 树 + 跨 bank 样本总表 + 试听 + FSB 替换 + bank 导出 |
| 文本 | `WorkbenchPages/TextWorkbenchPage.xaml(.cs)` | lang 文件树 + 键值树（就地编辑）+ 源文本预览 + 搜索 |
| 静态 | `WorkbenchPages/StaticWorkbenchPage.xaml(.cs)` | 静态数据表索引/搜索 + JSON 文档编辑 + staticmod 产物 |
| 卡片流 | `WorkbenchPages/PresetWorkbenchPage.xaml(.cs)` | **预设下滑卡片流**（**6 类别切换**：人格 / 敌人 / 异想体 / 播报员 / E.G.O 装备 / E.G.O 饰品）：卡片（封面 + 一句话预览 + 强度标签）+ 点进详情按类别看全部关联资源、**每行内联预览**、逐行「打开」走 `RevealReference` **精确跳转**、Spine 行可「▶ 动画预览…」（`SpineAnimationPreviewWindow`）与「导出…」。挑选/排序/行载荷全在 `Application/Relations/PresetWorkbenchService.cs` |

公共骨架：`WorkbenchPages/WorkbenchShell.xaml(.cs)`（列宽/视图模式/分节/滚轮接线）、
`WorkbenchPages/TreeExpansionState.cs`（**树展开态回放**，四页共用）、
`WorkbenchPages/JsonTreeEditor.xaml(.cs)`（键值树 + 行内编辑 + 右键菜单）、
`Themes/Theme.xaml` 与 `Themes/WorkbenchStyles.xaml`（**设计色只允许出现在这两个文件**：
`WorkbenchPages/` 下不得出现硬编码 `#RRGGBB` 或 `Color.FromRgb(0x`，这是收口验收门）。

**改页面前必读第五章后的不变量 §6-17 ~ §6-29**：页面的线程口径、预览规模闸门、
树展开态保持、`Loaded` 守卫、刷新去重、Spine 的边界（不播动画 / 靠容器目录定位 / 导出不是槽位）都在这几条里。

---

## 5. 核心运行流程

### 5.1 启动 → 扫描（每次启动都跑）

**2026-09-14 资源库 v2**：`unity-cache-index.db` 的 `user_version=2`；`bundles.id`
为整数键，`strings` 去重容器名/基线文字，`assets` 以 `(bundle_id,bundle_index)` 为
`WITHOUT ROWID` 主键。路径只在 bundle 表保存；有名称资源使用部分覆盖索引。
旧版本或缺少业务表会整体失效，不能把缺失对象行的 bundle 当作命中。更新、淘汰与取消
在同一事务中完成；读回使用同一只读快照，逐 bundle 消费，不再聚合整库中间行。
冷解析最多并行 4 个 bundle，只产出紧凑索引行，合并时才构造资产；目录字符串按 bundle 共享。
直接消费后端 descriptor，基线 CRC 借用该次解包流并使用 System.IO.Hashing；不再二次解包。
基准与复现步骤见 `PERFORMANCE-REFACTOR.md`，本节后面的历史耗时以该报告为准。

```
MainWindow ctor
  └─ ShowPage("assets")  ← 只建页面，不碰磁盘
Loaded → OnWindowLoadedAsync()
  ├─ UpdateHint() / 恢复上次项目（AppEnvironment.Config.LastProjectFile）
  ├─ RunStartupScanAsync()   ← 统一模态 StartupScanDialog（打开即扫、不可取消）
  │    └─ StartupScanService.RunAsync()           Application/Scanning/StartupScanService.cs
  │         ├─ 步骤 ①：建库/校表五个 SQLite（cache/，幂等；库在表不在也会补建）
  │         ├─ 步骤 ②：Unity 缓存增量扫描 → UnityCacheScanService（530 行）
  │         │     └─ UnityCacheSqliteIndexStore 读写 cache/unity-cache-index.db
  │         ├─ 步骤 ③：音频索引 → BankIndexService → BankIndexStore（cache/bank-index.db）
  │         ├─ 步骤 ④：静态表索引 → StaticIndexService → StaticTableIndexStore（cache/static-tables.db）
  │         ├─ 步骤 ⑤：文本索引 → LangTextWorkbenchService + TextIndexStore（cache/text-index.db）
  │         └─ 步骤 ⑥：关联图（派生）→ PersonaRelationIndexService → RelationStore（cache/relation-index.db）
  │               └─ 读上面四库的**语义签名**判新鲜度；四个库都就绪后分析「人格 ↔ 资源」并落库
  └─ 预热各工作台（CreatePage 全部 key：资源/音频/文本/静态数据/卡片流），不依赖切页懒加载
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
资源页数据流（S3b 起列表与目录树走 `AssetCatalog`；只有编辑/导出仍读 ModProject.Assets）：
  AssetCatalog(UnityCacheSqliteIndexStore, IAssetStateSource)   Application/Catalog/AssetCatalog.cs
    ├─ 命中集 = 索引下推名次 ∪ IAssetStateSource.ProjectOnly（导入的旧式模组 / 提取到项目的音频）
    ├─ 判据只有一份：AssetSearchService.Matches（分页列表 / 旧全量搜索 / 导出与构建同结论）
    ├─ 排序只有一份：AssetSearchService.BuildSortKey / CompareSortKeys
    │                （「按名称」直接用目录名次，免算排序键；**有补充集时必须算**才能混排）
    ├─ Page(query, offset, take)             只物化这一页 → 列表 + 页码条
    └─ AssetCatalogTree(catalog, query)       节点只记命中序列的一个区间，展开才物化那一层 → 目录树
  AssetDisplay                               显示路径/名称/中文类型与状态标签（容器视图口径）
    → AssetPreviewRegistry → IAssetPreviewProvider  多形态预览（图像/Sprite 合成/音频/文本/JSON行/对象字段树/摘要/材质/着色器/视频/图集/Spine/十六进制）
    → RelationQueryService.DescribeSubjectsForAsset  关联资源板块（反查「这资源属于哪些对象」，6 类别，走 relation-index.db 反向索引）
    → IReferenceRevealable.Reveal(payload)          别的页 / 卡片详情点「打开」时，按精确载荷选中并滚到那一行
```

**关键口径区分**（曾出错，务必分清）：
`AssetRecord.ContainerPath` = CAB/SerializedFile 名（导出/构建链路消费）；
`AssetRecord.Metadata["containerEntry"]` = Unity `m_Container` 给出的**游戏内资源路径**
（显示层消费）。两者不可混用。

### 5.3 编辑 → 导出（plan-16 的流水线）

Carra2 对象读回按源 bundle 分组，每组持有一个 `AssetsToolsBackend.BundleObjectReader`，
只解包一次并复用 SerializedFile，结束立即释放。会话只读、不可并发、不跨重打包写入复用；
单对象接口保持按 backend 生命周期复用内部缓存。对象字节、类型表索引及逐对象失败报告的契约不变。
catalog 用固定 16 字节键集合做一次线性扫描；Carra 差异比较直接比较字节，补丁按键查找。

```
编辑动作四类：
 ① 纹理/Sprite/字段替换  → AssetEditService / SpriteMetadataEditService / UnityFieldEditService
 ② 文本（lang）           → LangEditSession（宿主持有）→ LangTextWorkbenchService
 ③ 静态数据表             → StaticEditSession → StaticIndexService
 ④ 音频（bank/FSB）       → BankAudioService / BankIndexService

导出：
  ExportMod_Click（侧边栏「导出模组…」，先选目标目录；SaveFileDialog 的「选择此文件夹」惯例）
  → ExportProgressWindow（Show 非模态 + 取消令牌；主窗在导出期间禁用输入）
  → MainWindow.PrepareExportPlanAsync()   导出/调试**共用前奏**（两条链路必须同一份分析与上下文）
      ├─ MaterializeEditedAssetsAsync    已编辑的缓存资源实体化进项目（UI 线程：会改写 ObservableCollection）
      ├─ SaveProjectQuietlyAsync         存项目（Task.Run：序列化是 119 万行级重活）
      └─ ModExportPlanService.Plan(...)  分析修改 → 点亮哪些槽位（Task.Run）
          └─ ExportLayout（Domain）      <目标>/<项目名>_<种类>/<格式>/<产物>
  → Task.Run(ModPackExportService.ExportAsync)               按槽位执行（整体后台线程）
      ├─ Bank/Rebank   → Formats.Bank / Formats.Rebank
      ├─ Carra         → UnityBundleBuildService + UnitySerializedFileBuildService
      │                  + UnityCacheMaterializationService（先把引用 bundle 复制进项目）
      │                  + Formats.Carra
      ├─ Lunartique    → LunartiqueCarraConversionService + Formats.Lunartique
      │                  （复用 Carra 槽位已生成的对象包，不重跑流水线）
      ├─ Lang bus/patch/pathset → LangExportFormatter（带「可表达性门」：表达不了则整份不产出）
      └─ StaticMod     → StaticModService
  → ModExportReportWindow                                    报告（含空槽位说明）
```

- `plan.PlannedSlotCount == 0` 时直接给「没有可导出的修改」引导，不写任何文件。

- **线程口径（不变量 §6-17）**：`await` 一个「同步完成」的 Task 不会切线程 —— 所以导出链上
  所有同步重活必须自己 `Task.Run`（`UnityBundleBuildService.BuildAsync`）或由调用方整体包一层
  （`ExportMod_Click` / `PrepareExportPlanAsync`），两者都做是刻意的双保险；
  逐资源/逐对象上报必须经 `ThrottledProgress` 节流，否则 UI 消息队列会被淹掉。
- **取消（不变量 §6-18）**：`ExportProgressWindow.Token` 一路传到各槽位与逐对象循环；
  写盘走 `AtomicOutput`（临时文件 + 原子替换），取消只停在检查点，不留下半成品；
  用户关掉进度窗口即视为取消，宿主必须恢复主窗并说明，不能出现「窗口没了、主窗还禁用」的观感。
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
| 8 | 五个缓存库位置固定 `<程序目录>/cache/`，绝不写游戏目录/catalog | `Application/Caching/WorkbenchCachePaths.cs` | 污染用户游戏数据 |
| 9 | 导出目录布局由 `ExportLayout` 唯一决定；`Sanitize` 同时影响目录名与文件名 | `Domain/Formats/ExportLayout.cs` | 输出树与加载器匹配口径不一致 |
| 10 | `AssetRecord.Metadata` 字典是**大小写不敏感**，且是扩展槽 | `Domain/Assets/AssetModels.cs` | 已有工程读不到数据 |
| 11 | `AssetType`/`AssetEditState` 枚举**隐式数值被 UI 当筛选值下发** | `Domain/Assets/AssetModels.cs` + `AssetsWorkbenchPage` | 筛选语义整体偏移；新成员只能追加末尾 |
| 12 | 项目文件版本 `ModProject.CurrentSchemaVersion`；未知更高版本拒绝打开 | `Domain/Projects/ModProject.cs` + `Application/Projects/ProjectService.cs` | 旧版本编辑器静默丢数据 |
| 13 | ~~设计色只允许在 `Themes/*.xaml`；`WorkbenchPages/` 不得硬编码色值~~ **（验收对象已消失：`App/WorkbenchPages/` 22 个 UI 单元已于 W2 删除；但 `App/Themes/Theme.xaml` + `WorkbenchStyles.xaml` 两个文件 2026-09-17 实测仍在且无消费点，见 §2 App 行与 FINAL-DELIVERY G-08）**；Web 对应物见 §6-34 | —（历史条目，保留以解释旧验收门） | 按本条去 `App/WorkbenchPages/` 找文件会落空；按「已删除」理解 `App/Themes/` 会与磁盘不符 |
| 14 | 磁盘写入统一走 `AtomicOutput`（临时文件 + 替换） | `Application/Build/AtomicOutput.cs` | 中断留下半个文件 |
| 15 | 未知负载/压缩/字段一律 **fail fast + 中文错误**，不猜测、不静默降级 | 全仓库（各 handler 的 `ValidateAsync`/异常路径） | 产物「看起来成功」实则无效 |
| 16 | **缓存只影响速度，不影响正确性**：任何损坏一律删库重建，且「删库」不得改变功能结果 | 各 Store 的类注释 + `SqliteTableCache.RecreateOrThrow`；由「有缓存 vs 删库」对照测试钉死 | 缓存变成事实来源，删库即出错 |
| 17 | **`await` 一个「同步完成」的 Task 会原地继续执行，不切线程**：声明 `async` 但体内没有真异步点的服务，其重活跑在调用线程上（从 WPF 调就是 UI 线程） | `Application/Build/UnityBundleBuildService.BuildAsync`（自己 `Task.Run`）、`App/MainWindow.ExportMod_Click` / `PrepareExportPlanAsync`（整体 `Task.Run`） | 模态窗口弹出后软件「未响应且不恢复」（历史报障）；导出链上新加同步重活必须同时确认线程口径 |
| 18 | 导出链必须可**协作式取消**：写盘走 `AtomicOutput`、槽位/对象之间检查令牌、进度窗口关窗即取消 | `App/ExportProgressWindow.cs` + `ModPackExportService` / `UnityCacheExportService` / `UnityBundleBuildService` 的 `CancellationToken` 形参 | 用户无法中断长导出；只能杀进程，产物半途而废 |
| 19 | 进度上报必须**节流**（逐资源/逐对象可达十万级，UI 侧每条都 Post 到消息队列） | `Application/Build/ThrottledProgress.cs`；各服务的逐条上报走它 | UI 队列被淹，导出期间界面假死 |
| 20 | **静态数据表默认隐藏（2026-09-15 恢复），且判据只能有一份**：结论 = `AssetStaticClassifier` 的位掩码（位1 catalog 内层键命中 / 位2 bundle 名前缀 / 位4 容器路径前缀 `Assets/Resources_moved/StaticData/static-data/`），**扫描期算完落库**（`assets.static_kind` + 记录元数据 `staticBundle`），查询期只做一次 `static_kind = 0` 的索引比较。catalog 不可用时**没有资格判定「不是静态」** ⇒ 不得清除已有位（只影响位1）。索引对静态结论**是权威** —— 项目态不得覆盖它 | `Application/StaticMods/AssetStaticClassifier.cs`（唯一出处）、`Scanning/UnityCacheSqliteIndexStore.cs`（列 + 两个索引 + 就地迁移/回填）、`Scanning/UnityCacheScanService.cs`（扫描期算位）、`Assets/AssetSearchService.cs`（`Matches` 末条判据）、`Catalog/AssetCatalog.cs`（`IsStatic:` 下推 + `MergeProjectState` 跳过静态键） | 判据被抄成第二份（历史病根：列表运行时重算、预览读标记 ⇒ 列表藏了预览不认）；结论被降级成 `bool`（丢掉「为什么是静态」的依据，catalog 换键后无法恢复）；把「没变化过的 bundle」当成「结论没变」（`PersistAll` 只 upsert 变化过的 bundle ⇒ 老功能「时灵时不灵」） |
| 21 | **预览规模必须先在 provider 侧截断**，且预览内容要包在「有最大高度 + 可滚动」的容器里 | `Application/Assets/Preview/AssetPreviewProviders.cs`（文本预览字符上限）、`App/WorkbenchPages/AssetsWorkbenchPage.xaml.cs`（`WrapPreview`） | UI 线程为超长内容造几千个控件 → 点预览即未响应；窗口变矮时内容被裁且无法滚动 |
| 22 | **树重建必须回放展开态**（节点模型是只读纯数据，承载不了展开态）；集合跨重建累积 | `App/WorkbenchPages/TreeExpansionState.cs` + 四页的 `_expandedTreeKeys` | 搜索/筛选/编辑/切页后树突然折叠回初始形态（历史报障） |
| 23 | 常驻页面的 `Loaded` **会重复触发**（宿主反复换内容），载入逻辑必须有「已载入」守卫 | `App/WorkbenchPages/`（Static/Bank/Text 的 `_loaded`；`MainWindow.ShowPage`） | 「离开再回来」重跑索引并重建树 |
| 24 | 编辑集自身的 `Changed` 已驱动刷新，调用方**不得再手动刷新一次** | 四个页面 + `LangEditSession`/`StaticEditSession` | 同一次保存重建两遍树（历史缺陷） |
| 25 | **全表谓词里不许把 `File.Exists` 排在廉价判据之前**，且惰性 `GroupBy` 链**必须先物化再计数** | `Application/Build/UnityCacheExportService.IsEditedCacheAsset`、`UnityBundleBuildService.Build`、`UnitySerializedFileBuildService.BuildAsync` | 回灌后项目有 127 万条资产：一次全表 `File.Exists` ≈ 44 s，同一条谓词被跑 4 遍 → 导出 7 分钟（实测，见 `STATUS.md` §6.2.1） |
| 26 | **内置编辑器只吃松散文件**：bundle 内对象的 `SourcePath` 是整个 AssetBundle 容器，不能当正文读 | `Application/Assets/AssetEditService.ReadCurrentBytesAsync`、`TextAssetEditService.CanEditText`（App 侧按钮置灰 + 中文说明） | 双击 bundle 内 TextAsset → 2.26 MB 二进制进 `TextBox`，排版 26.5 秒 → 界面「未响应」被强杀（历史报障，且**没有** crash 日志） |
| 27 | **（2026-09-17 更新）Spine 的「解析/静态预览」与「动画播放」都在前端**：C# 侧**只**负责「定位三件套 + 流式喂原始字节」（`Application/SpineData/`）+ 导出写盘（`Application/Spine/SpineExportService.cs`）；解析、静态结构/布局图与动画播放由前端 `spine-runtime.ts` + `SpineRenderer.vue`（spine-webgl@4.0.26）承担。**native 渲染工程 `LimbusModEditor.SpineRuntime` 与 `App/SpineAnimationPreviewWindow` 已删除（全仓 0 命中）**；逐文件清单见 `docs/ARCH-WEBVIEW2-VUE.md` §7 | `Application/SpineData/`（定位/枚举/路径判据/网关）、`Application/Spine/SpineExportService.cs`、前端 `components/spine-runtime.ts` | 以为还能在 C# 里加个预览 provider 播动画；或把渲染逻辑漏回 Application（破坏分层，且与「渲染全在前端」的决策冲突） |
| 28 | **Spine 三件套靠「容器路径的目录」定位**（骨架 + `.atlas.txt` + 页贴图同目录）；索引只收**带容器路径**的资源 | `Application/Spine/SpinePreviewService.cs`（`SpineSiblingIndex`） | 拼不出同目录就既看不到图集布局图、也导出不齐三件套 |
| 29 | **Spine 导出不是 `ExportSlot`**：模组导出只装「被修改过的资源」，给外部工具查看属独立动作 | `Application/Spine/SpineExportService.cs`、`Build/ModExportPlanService.cs`（槽位定义） | 把「导出查看」混进模组产物，破坏「只装改动」的语义 |
| 30 | **关联对象 id 必须带类别前缀** `<category>:<key>`（走 `SubjectIds.Make/CategoryOf/KeyOf`），且**类别一律由资源目录前缀判定**，绝不「扫 5 位数字窗口」 | `Application/Relations/RelationCategories.cs`、`SubjectRelationAnalyzer.cs` | 敌人 4 位段与异常/事件 id 真的撞车（实测含 `90005`）；E.G.O 资源侧 20xxx 数字会造「幽灵 EGO」（只认 lang `Egos.json`）；E.G.O 饰品要 `id % 10000` 归一化 |
| 31 | **`IReferenceRevealable.Reveal` 允许失败（返回 false），但不得抛异常**；宿主必须退化为关键词过滤 | `App/WorkbenchPages/IReferenceRevealable.cs` + `MainWindow.RevealReference` | 关联图是旁路：定位不到就崩页面、或静默什么都不做（用户以为按钮坏了） |
| 32 | **精确跳转载荷（`RelationDeepLink`）是纯数据**：不参与判重、不影响关联图正确性 → 改它**不需要** `FormatVersion` +1 | `Application/Relations/RelationDeepLink.cs` | 误以为必须动派生库版本；或把载荷塞进判重键导致同一关联重复/丢失 |
| 33 | **改「抽哪些事实 / 怎么算关联 / 类别 id 归一化 / 新增并填充列 / 跨链关系名」必须把 `RelationIndexSource.FormatVersion` +1**；仅改 UI 怎么用现有列、或 SQLite 加列本身（轻量迁移）**不**需要。**当前值 = `"v4"`**（2026-09-17 实测：`Application/Relations/RelationModels.cs` 的 `public const string FormatVersion = "v4";`）；v4 的语义增量 = **StorySpine（剧情骨架）纳入 `IsSpinePath` 判据**（t24 越界产出，经复核保留，见 `docs/AUDIT-2026-R2.md` §3.2） | `Application/Relations/RelationModels.cs`（`FormatVersion`）、`Caching/WorkbenchCacheSchema.cs` | 旧派生库被当成新鲜的，卡片流 / 关联资源显示旧数据且不重建 |
| 34 | **（2026-09-15 新增）设计色只允许出现在 `src/LimbusModEditor.Web/src/styles/tokens.css`**；`src/views/` 与 `src/components/` 下硬编码色值必须为 0（铁律 §9 的 Web 对应物，取代原 `WorkbenchPages/` 收口验收门） | 前端工程 + 收口验收门 | 界面颜色/样式不一致 |
| 35 | **（2026-09-15 新增）所有列表一律「查询 → 一页」**：IPC 契约为 `AssetCatalogPage(Items,TotalCount,Offset,Take)` 形状；**禁止取全量再前端筛**（=WPF 时代 S1–S4 性能成果）；禁止 base64 二进制过 IPC，一律走 `lme.app`/`lme.data` 本地虚拟主机 | `docs/WEB-IPC-CONTRACT.md` §2/§5 | 列表卡死、大纹理传输成为新瓶颈 |
| 36 | **（2026-09-15 新增）IPC 契约 DTO 与宿主网关逻辑必须定义在 `Application` 层（无 WPF 依赖）**；`CoreWebView2.PostWebMessageAsJson` 传输层是唯一的 WPF 依赖点；契约逻辑必须可被 `Domain.Tests` 覆盖（铁律 §3-10 不被桥接绕过） | `src/LimbusModEditor.Application/Ipc/`（W1 起）、宿主 App | 换壳要重写两遍；契约无测试 |
| 37 | **（2026-09-15 新增）`relation.reveal` 的 `'\0'` 分段载荷（`RelationDeepLink`）原样透传、前端不解析**；响应 `located:false` 时前端退化为关键词过滤（与 `IReferenceRevealable` 布尔语义一致）；改载荷**不需要** `FormatVersion` +1 | `Application/Relations/RelationDeepLink.cs` + IPC 契约 §2.4/§3 | 误升派生库版本；或载荷被前端解析后语义漂移 |
| 38 | **（2026-09-17 新增）权威来源优先，禁止 id 窗口猜测**：对象→资源/类别的归属**只能**由权威来源判定（资源目录前缀、lang 的 `Egos.json`、static 表记录键等，共 16 种，见 `docs/RELATIONS-AUTHORITY-MATRIX.md`）；**绝不**用「扫文件名/键里的 N 位数字窗口」推断类别；每条事实必须带 `AuthoritySource` 与 `ConfidenceLevel`（`Authoritative`/`Derived`/`Ambiguous`），并判 `WritableSourceKind`（`Unknown`/`None`/`Path`）——**只有 `Path` 允许出现在可编辑页面结构里**；**推不出来的字段一律留空，不编造、不用占位文案** | `Application/Relations/Authority/`（`WikiPageAuthorityEngine` + `*AuthorityProvider` + `AuthorityFact`/`ConfidenceLevel`/`WritableSourceKind`）、`Application/Relations/RelationCategories.cs`（前缀判类，配合 §6-30） | 敌人 4 位段与异常/事件 id 撞车（实测含 `90005`）、20xxx 造「幽灵 EGO」——**这正是旧 id 规则推断的错配来源**；页面显示看似合理实则错误的数据 |

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
| `<程序目录>/cache/relation-index.db` | **派生**关联图（**6 类别**对象 ↔ 跨资源链接 + 反向索引 `subjects_by_ref` + 显式跨资源边 `xref`） | `Application/Relations/RelationStore.cs`（由 `PersonaRelationIndexService` 从上面四个库派生） |
| `<程序目录>/projects/<名>/` | 新建项目的默认位置 | `Application/Build/NewModTemplateService.cs` |
| `<项目>/<名>.lmeproj` | 项目文档（`ModProject` JSON） | `Application/Projects/ProjectService.cs` |
| `<项目>/sources/cache/<外>_<内>.bundle` | 编辑过的缓存 bundle 实体化副本 | `Application/Scanning/UnityCacheMaterializationService.cs` |
| `<目标>/<项目名>_<种类>/<格式>/` | 导出产物树 | `Application/Build/ModPackExportService.cs` |
| `%APPDATA%\LimbusCompanyMods` | 真实加载器的模组目录（默认导出目标） | 用户配置 / `Debugging/ModDirectoryLocator.cs` |

**游戏目录、Unity 缓存、catalog 一律只读**，唯一例外是 §5.4 的调试应用。

### 7.1 五个缓存库的失效规则与「口径版本」

| 库 | 源签名 | 失效粒度 | 口径/结构版本 |
|---|---|---|---|
| `unity-cache-index.db` | **不用 `index_meta`**：新鲜度直接落在 `bundles(size, mtime_ticks)` 行上 | 逐 bundle | 层内 `EnsureContainerEntryColumn` / `EnsureStaticBundleColumn` 轻量补列（旧库自动迁移） |
| `bank-index.db` | 目录签名 `0:mtimeTicks`（源键 = 目录全路径小写） | 逐 bank 文件 `(size_bytes, mtime_ticks)`，只删该 bank 的 `samples` 行；行集对账删已消失文件 | `WorkbenchCacheSchema` 建表脚本 |
| `static-tables.db` | **内层内容哈希**（小写） | 源变即整库重建；正文缓存 LRU 淘汰（上限 64MB，淘汰到 80%） | 同上 |
| `text-index.db` | 内容口径版本 + 活动语言目录签名 + **`config.json` 内容哈希**（切语言必须用内容哈希，size/mtime 不可靠） | 整库 / 单文件 `(size,mtime_ticks)` / 行集不一致整表重建 | `TextIndexStore.IndexFormatVersion = "v4"`（v1 首版 → v2 不收录根级 `config.json` → v3 口径去根文件夹 → v4 并入活动语言目录名 + `language_prefix`） |
| `relation-index.db`（**派生**） | 上面**四个库的语义签名拼接**（`RelationIndexSource.From`）：unity=bundles 计数+最大 mtime+总字节；bank=目录签名；static=内层内容哈希；text=内容口径版本+目录签名+config 哈希+语言目录 | 任一上游签名变 → **整库重建**（关联图整体由四库决定） | `RelationIndexSource.FormatVersion = "v4"`（v3 → v4：扩展 `RelationDisplayRules.IsSpinePath` 覆盖 `StorySpine_*` 目录，修正既有判据盲区；**改「抽哪些事实 / 怎么算关联 / 键口径 / 新增并填充列 / 跨链关系名」时必须 +1**，否则旧派生库会被当成新鲜的）。业务表：`subjects` / `links` / `subjects_by_ref` / `xref` |

> **派生库的源签名不用库文件 mtime**：WAL 下写事务未必改主库 mtime（只写 `-wal`），拿它判「源变没变」会漏判；
> 四个上游的语义签名才是各自服务真实使用的判据。

各库共用的底座能力（`Caching/SqliteTableCache.cs`）：幂等建表、`EnsureColumn` 轻量迁移、
`Write` 单事务批量写（WAL + `synchronous=NORMAL`）、`EnsureSource(sourceKey, signature, languagePrefix)`
（源/签名/前缀任一不一致 → 清空业务表 + `index_meta` 并返回 true）、
损坏判定只认 SQLITE_CORRUPT(11)/SQLITE_NOTADB(26) → 删库重建。
**「文件在但表不在」（0 字节库 / 上次建库被打断）是 `no such table: index_meta` 的直接来源**，
各库的读路径都内置缺表自愈，启动扫描的 `EnsureCacheDatabases` 也会补建。

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
| 搜索/筛选/排序行为 | `Application/Assets/AssetSearchService.cs`（`AssetSearchQuery` + `Matches`） | 分页列表走 `Application/Catalog/AssetCatalog.cs`、`AssetsWorkbenchPage` 筛选栏绑定、`AssetSortKind` |
| 分页/页码/「翻到某资源第几页」 | `Application/Catalog/AssetCatalog.cs`（`Page` / `IndexIn` / `MatchOrder` / `ResolveEntries`） | `AssetsWorkbenchPage.RunSearchAsync` + 页码条；`AssetCatalogTests`（与旧搜索逐行等价） |
| 目录树层级/懒展开/重名消歧 | `Application/Catalog/AssetCatalogTree.cs`（生产路径） | `AssetDisplay.TreePath`、`AssetTreeNode.DistinguishLeaves`（共用）、`AssetCatalogTreeTests`（与 `AssetTreeBuilder` 逐节点等价） |
| 列表里少了导入的模组 / 提取到项目的音频 | `Application/Catalog/AssetCatalog.cs` 的 `IAssetStateSource.ProjectOnly` | `ProjectAssetStateSource`（判据 = `AssetDisplay.IsCacheReference`） |
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
| **导出/调试卡住界面、无法中断** | `App/MainWindow.xaml.cs`（`ExportMod_Click`/`DebugMod_Click`/`PrepareExportPlanAsync` 的 `Task.Run` 与令牌） | `Build/UnityBundleBuildService.cs`（必须自己切线程池）、`Build/ThrottledProgress.cs`、`App/ExportProgressWindow.cs`、不变量 §6-17~19 |
| **预览卡死 / 预览看不到下面** | `Application/Assets/Preview/AssetPreviewProviders.cs`（截断闸门） | `App/WorkbenchPages/AssetsWorkbenchPage.xaml.cs`（JSON 树闸门、`WrapPreview`）、`Assets/AssetPropertyService.cs`（会再读一次正文）、不变量 §6-21 |
| **静态表没走专门预览通道** | `Application/Assets/Preview/AssetPreviewProviders.cs`（`IsStaticTableAsset`：先读 `AssetStaticClassifier.Of(asset)`，再退到 bundle 名 / 文件名 / 容器路径前缀） | `Application/Scanning/UnityCacheScanService.cs`（标记补写/清除规则）、`StaticMods/StaticBundleLocator.cs`（`LooksLikeStaticBundle` + `LooksLikeStaticTablePath`）、不变量 §6-20 |
| **资源工作台默认视图里又出现 static-data（没藏住）** | `Application/StaticMods/AssetStaticClassifier.cs`（位掩码合成，唯一判据）、`Scanning/UnityCacheSqliteIndexStore.cs`（`static_kind` 落库 + 就地迁移/回填 + `IX` 列序） | `Assets/AssetSearchService.cs`（`Matches` 末条判据）、`Catalog/AssetCatalog.cs`（`IsStatic:` 下推）、`App/WorkbenchPages/AssetsWorkbenchPage.xaml(.cs)`（「显示静态数据表」复选框）、`Scanning/UnityCacheScanService.cs`（扫描期算位 / 不得清位）、不变量 §6-20 |
| **树突然折叠回初始形态** | `App/WorkbenchPages/TreeExpansionState.cs`（key 回放） | 四页的 `RebuildTree` 与 `_expandedTreeKeys`、`Loaded` 守卫、不变量 §6-22~24 |
| **改「资源之间怎么关联 / 新增预设类别（除 6 类之外）」** | `Application/Relations/RelationCategories.cs`（加类别 + 中文标签 + `All`）、`SubjectRelationAnalyzer.cs`（抽取事实 + 建链接 + id 归一化） | 改完把 `RelationIndexSource.FormatVersion` +1（不变量 §6-33）；`RelationQueryService`（查询门面）、`Relations/RelationStore.cs`（表结构）、`Scanning/StartupScanService`（第 6 步） |
| **改卡片流页的卡片/详情展示（封面挑哪张、分组顺序、行预览、「打开」跳哪）** | `Application/Relations/PresetWorkbenchService.cs`（**纯逻辑，改这里而不是页面**） | `App/WorkbenchPages/PresetWorkbenchPage.xaml.cs`（只做装配与并发/代际守卫）、`PresetWorkbenchServiceTests.cs` |
| **卡片详情 / 关联资源点「打开」后没跳到那一行** | `Application/Relations/RelationDeepLink.cs`（载荷口径）、目标页的 `IReferenceRevealable.Reveal` | `App/MainWindow.xaml.cs` 的 `RevealReference`（失败会退化为关键词）、不变量 §6-31~32 |
| **Spine 预览/导出异常（不显示预览、没有布局图、导不出文件）** | `Application/Spine/SpinePreviewService.cs`、`SpineExportService.cs` | `SpineModels.cs`（内容判定）、`RelationDisplayRules.IsSpinePath`（路径粗筛）、`AssetPreviewRegistry.CreateDefault`（是否注册了 provider）、不变量 §6-27~29 |
| **Spine 动画播放不工作（白图 / 报错 / 缺页）** | `Application/SpineData/SpineAssetLocator.cs` + `SpinePathRules.cs`（找三件套，中文错误）、`SpineDataGateway`（喂字节） | 前端 `components/spine-runtime.ts` / `SpineRenderer.vue`（JSON 主路径 + `pma:true`）、页贴图解码走 `ReadBundleSpriteComposite`（Sprite）而非 `ReadTexturePng`、不变量 §6-27 |
| **新增/修改预览形态** | `Application/Assets/Preview/AssetPreview.cs`（`AssetPreviewKind` + `AssetPreviewRegistry.CreateDefault` 注册顺序） | `AssetsWorkbenchPage.BuildPreviewView`（App 侧 switch 也要加分支）、不变量 §6-21 |
| **某种资源预览退化成十六进制** | `AssetPreviewProviders.ScriptPreviewProvider.CanPreview`（现覆盖 MonoBehaviour / MonoScript **+ Component / GameObject / ScriptableObject**） | 该类型是否已有 provider、`AssetPreviewRegistry.CreateDefault` 注册顺序 |
| **图片（尤其 Sprite）预览不出图** | 解码入口：Sprite 走 `ReadBundleSpriteComposite`，Texture2D 才走 `ReadTexturePng`（`TryDecodeImagePng`） | `asset.Type == AssetType.Sprite && ContainerPath` 是否存在、`UnityPathId` 是否为空 |

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
- **`App` 项目没有测试工程**：任何需要被测试覆盖的逻辑都应下沉到 `Application`
  （IPC 网关、维基页面库/查询、权威引擎因此都在 `Application`，由 `tests/LimbusModEditor.Application.Tests` 覆盖）。
- **（2026-09-17 更新）渲染不在 C# 侧**：Spine 解析、静态结构预览与骨骼动画播放**全在前端**
  （`src/LimbusModEditor.Web/src/components/spine-runtime.ts` + `SpineRenderer.vue`，spine-webgl@4.0.26）。
  C# 侧只保留 `Application/SpineData/`（定位三件套 + 流式喂原始字节）与 `Application/Spine/SpineExportService.cs`（写盘）。
  **`LimbusModEditor.SpineRuntime` 工程、`App/SpineAnimationPreviewWindow`、`Application/Spine/SpineModels|SpinePreviewService|SpineAnimationSourceService` 均已删除**
  （全仓 grep `SpineRuntime|SpineFrameRenderer|SpineAnimationPreviewWindow` = **0 命中**）。
  **不得把渲染依赖引回 C#**（分层与「渲染全在前端」的决策都会破）。
- **`.slnx` 的唯一刻意例外是前端工程**：`src/LimbusModEditor.Web/` 是 Node 工程，不进 `.slnx`
  （dotnet 构建与 npm 构建彼此隔离）；发布时由 App 的 `PublishFrontend` Target 把 `dist/**` 复制进 `wwwroot/`。
- **不要**：手写完整 Unity 解析器、伪造/分发 FMOD 专有 DLL、把未知对象静默转成「成功输出」、
  在无真实样本时宣称格式兼容、用宽泛递归删除或跳过备份去写用户游戏目录。

---

## 10. 常用命令

```text
# 后端（.slnx = 12 个 src 工程 + 3 个测试工程）
dotnet build LimbusModEditor.slnx --no-restore --nologo
dotnet test  LimbusModEditor.slnx --no-build --nologo      # 864 例（Domain 94 + Format 74 + Application 696）
# 建议按工程分开跑（并行时互相抢磁盘，墙钟断言最先受影响）
dotnet test tests/LimbusModEditor.Domain.Tests      -c Debug --no-build --nologo   # 94 例
dotnet test tests/LimbusModEditor.Format.Tests      -c Debug --no-build --nologo   # 74 例
dotnet test tests/LimbusModEditor.Application.Tests -c Debug --no-build --nologo   # 696 例

# 前端（Node 工程，刻意不在 .slnx 内）
npm --prefix src/LimbusModEditor.Web run build       # vite build + 复制两份 LICENSE 到 dist/licenses/
npm --prefix src/LimbusModEditor.Web run type-check  # vue-tsc --noEmit（当前无 vitest/Playwright 工程，见下）

# 发布（一步到位：必须先构建前端，否则 PublishFrontend Target 直接报错中止）
npm --prefix src/LimbusModEditor.Web run build
dotnet publish src/LimbusModEditor.App/LimbusModEditor.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish-win-x64 --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1   # 额外把 third_party/fmod/*.dll 复制进输出 fmod/
```

**一键构建/发布的真实情况（2026-09-17 实测核对）**：

- `src/LimbusModEditor.Web/` 的 `package.json` 脚本只有 `dev` / `build` / `copy-licenses` / `type-check` / `preview`；
  **没有 `test` 脚本**（前端单测仍是新债 N-02，见 `docs/AUDIT-2026-R2.md` §4），所以
  `npm --prefix src/LimbusModEditor.Web test` **会失败**，不要再照抄。
- `logs/lme.py`（`build` / `test <name>` / `publish`）**只跑 dotnet，不含 npm 步骤**（实测 `lme.py` 源码：
  三条分支分别调 `dotnet build` / `dotnet test` / `dotnet publish`）。因此「前端 + 后端」一键发布 =
  上面那两行（先 `npm run build`，再 `dotnet publish`）。App.csproj 的 `PublishFrontend` Target 对
  **缺失的 `dist/`** 是 `<Error>` 硬失败并给出中文提示，不会静默产出无前端界面的包。
- 前端产物复制是**增量复制（`SkipUnchangedFiles=true`）且不清理目标目录**：向**已存在旧产物**的目录
  重复 publish 会残留上一版哈希文件名的 `assets/*`（2026-09-17 交付轮实测：主产物目录 69 个文件 vs
  `dist/` 52 个，多出 17 个孤儿）。发到干净目录不会有该现象（实测 52 = 52，逐文件 SHA256 一致）。
- 发布体积基线：`artifacts/publish-win-x64` = **519MB**（含 5 个 SQLite 索引库）；Fixed Version WebView2 运行时约 +180MB，目标上限 ≈ 700MB（见 `docs/ARCH-WEBVIEW2-VUE.md` §6.3）。
- 测试基线：**864**（Domain 94 + Format 74 + Application 696；2026-09-17 交付轮实测，命令与原始输出见
  `docs/FINAL-DELIVERY.md`）。历史轨迹：746（WPF 时代 Domain 672 + Format 74）→ 733 → **864**。
