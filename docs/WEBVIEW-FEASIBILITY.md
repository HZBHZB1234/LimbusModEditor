# WebView 重构前端 · 可行性分析

> ⚠️ **2026-09-15 状态更新**：本文档 §5/§6 的「不建议现在做 A（全量 WebView 重写）」结论
> **已被 `docs/ARCH-WEBVIEW2-VUE.md`（ADR）显式取代**——用户已定案全量重构为 WebView2 宿主 + Vue3/TS 前端，
> 波次 W0→W4 与 IPC 契约见 ADR 与 `docs/WEB-IPC-CONTRACT.md`。
> 本文档 §1–§4 的技术账（现状实测、纯数据家底、代价分布、WebView2 技术账）**仍然有效**，继续作为事实基础引用。

- 分析方式：**只读**（不改源码 / 不跑 build / 不跑 test；索引库一律 `mode=ro`）
- 日期：2026-09-15
- 数据源：工作区源码实测（`src/**` 行数与控件统计）、`artifacts/publish-win-x64`、`docs/*`
- 结论一句话：**技术上完全可行，但这个仓库现在做「全量 WebView 重写」是负收益；值得做的是「先把视图状态从 code-behind 里剥出来」，之后按面板渐进嵌入 WebView2。**

---

## 1. 现状实测（不是估算）

| 项 | 实测值 |
| --- | --- |
| UI 技术栈 | WPF `net8.0-windows` + `WPF-UI 4.3.0`（Fluent 样式）；`OutputType=WinExe`，RID 固定 `win-x64` |
| App 层体量 | **13,156 行 C#** + **1,325 行 XAML**（全部 XAML 加起来只有 1,325 行） |
| 窗口/页面数 | **15 个 Window + 7 个 WorkbenchPage**＝22 个顶层 UI 单元 |
| 单文件最大 | `AssetsWorkbenchPage.xaml.cs` **2,501 行 / 137 KB**；其后 `BankWorkbenchPage` 1,483 行、`MainWindow.xaml.cs` 1,177 行、`TextWorkbenchPage` 1,136 行 |
| MVVM | **没有**。`ViewModel` / `ICommand` / `CommunityToolkit` 命中 **0**；`DataContext` 0；只有 5 处 `ObservableCollection`（纯列表容器） |
| 列表实现 | `ListView + GridView` 手开虚拟化（`IsVirtualizing=True`、`VirtualizationMode=Recycling`、`ScrollUnit=Pixel`、`CanContentScroll=True`）+ `TreeView`（同样手开虚拟化） |
| 渲染/动画 | `WriteableBitmap` / `DrawingVisual` / `DoubleAnimation` / `Storyboard` **全部 0 命中**；预览是 `BitmapImage` 解码 PNG 贴 `Image`；Spine 走 `SkiaSharp`（在叶子工程 `SpineRuntime`）出帧，App 只贴帧 |
| 原生交互点 | `Microsoft.Win32`（文件对话框）11 文件 · `SaveFileDialog` 9 · `MessageBox` 10 · `OpenFileDialog` 4 · `Clipboard` 2 · `Process.Start` 5 · `ShowDialog` 调用点 ~30 |
| 线程编组 | `Dispatcher` 13 文件、`BeginInvoke` 2 文件（都在 App 层，UI 线程语义） |
| 发布体积 | `artifacts/publish-win-x64` = **519 MB**（自包含，已含 5 个 SQLite 索引库） |
| 测试工程 | `tests/` 只有 `Domain.Tests` 与 `Format.Tests`。**`App` 项目没有任何测试**（这是项目里的明文铁律） |

---

## 2. 决定可行性的第一条：这个仓库「意外地」很适合桥接

大多数 WPF 项目迁 WebView 会一头撞在「业务逻辑和控件缠在一起」上。**这个仓库不是**——它已经把最关键的三块做成了框架无关的：

| 已有资产 | 为什么这对 WebView 是利好 |
| --- | --- |
| `Application/Catalog/AssetCatalog.cs` | 公共 API 是纯数据契约：`Count/Page/Locate/IndexIn/Resolve/MatchOrder`，**本身就不引用 WPF**。命中集限制成 `AssetCatalogEntry(Rank, ExtraIndex)` **8 字节**——正好是 IPC 能承受的载荷量级。 |
| `AssetCatalogPage(Items, TotalCount, Offset, Take)` | 已经是**分页 DTO**。Web 前端只要做「取一页 → 渲染一行」，不需要一次性拿 127 万条。 |
| `AssetSearchQuery` + 只读 `sqlite ?mode=ro` | 查询参数是可序列化的判据对象；重活（FTS5 trigram、稠密名次）全在 SQL，不依赖 UI 线程。 |
| `RelationDeepLink`（`'\0'` 分段载荷） | 是**纯数据**（明文规定：改它不动 `FormatVersion`）——天生就是可放进 IPC 的载荷。 |
| `IReferenceRevealable.Reveal(payload)` | 已经是**显式契约**，且规定「不得抛、失败退化为关键词过滤」——这就是一个现成的桥接口子。 |
| `PresetWorkbenchService` | 卡片流/详情的**纯逻辑**已单独成类（记忆里明文：纯逻辑在此，视觉在 `RelationDisplayRules`）。 |
| 五个缓存库「只影响速度，不影响正确性」 | WebView 侧拿到脏数据不会污染正确性，最坏是慢——这让渐进迁移的容错空间很大。 |

**一句话：重活已经在 `Application` 里、且是纯数据接口。桥接要写的只是「视图状态 ↔ JSON」那一层。**

---

## 3. 决定可行性的第二条：代价集中在「视图逻辑」，而它恰恰是最难搬的

`App` 层 13,156 行里，绝大部分是 **imperative 的视图逻辑写进 code-behind**，而不是可迁移的 XAML 声明。作证：

- 全部 XAML 只有 **1,325 行**（含 27 KB 的 `WorkbenchStyles.xaml` 与 5 KB 的 `Theme.xaml`）。也就是说**界面是「在 C# 里 new 出来的」**，不是用模板声明的。
- `AssetsWorkbenchPage.xaml.cs` 单文件 **2,501 行**。里面必然混着：列宽/分割条位置、选中态与树展开态同步、深链跳转、缩略图异步解码、右键菜单、写回项目态……这些**没有任何一样能靠"翻译 XAML"自动过桥**。
- 已存在的 UI 状态资产：`TreeExpansionState.cs`（78 行）、`config/ui-state.json`（记忆明文：UI 状态绝不进 `.lmeproj`）。这些语义都要在 Web 侧重新实现一遍。
- 正在进行中的重构（S3c 收窄 `Assets`、S4 预览按尺寸解码/切页释放）**本身就动这四个大文件**。现在插入前端重写 = 两条战线在同一批文件上打架。

**量化判断**：WebView 重写不是「把 1,325 行 XAML 转成 HTML」，而是「把 ~11,000 行 C# 视图逻辑用 TypeScript 重写一遍，再补一层双向 IPC」。按 22 个 UI 单元、~30 个 `ShowDialog` 站点算，这是一个**数量级大于当前 S3c+S4 之和**的工程。

---

## 4. WebView2 的技术账（逐条）

前提：`TargetFramework=net8.0-windows` + `win-x64` ⇒ `Microsoft.Web.WebView2` 与 `WebView2 (WPF)` 控件**开箱可用**，无框架障碍。

### 4.1 有利的一面

| 维度 | 判断 |
| --- | --- |
| 列表性能 | 现状已是「服务端分页 200 条/页」。Chromium 渲染 200 行是白给；127 万行由 `AssetCatalog` 继续挡在 SQL 侧，Web 层只做虚拟滚动 + 预取窗口。**性能不会退化**。 |
| 样式/主题 | CSS 变量替代 `Theme.xaml`(5 KB) + `WorkbenchStyles.xaml`(27 KB) 的 162 个 `<Setter>`。规则「设计色只允许出现在两个样式文件」在 Web 侧变成「只允许出现在 `tokens.css`」——**更好守**。 |
| 预览 | 图片预览 = 把 PNG 字节交给前端（`WebResourceRequested` / `SetVirtualHostNameToFolderMapping` 走本地虚拟主机，比 base64 过 IPC 快得多）。 |
| Spine | **完全不需要动**。现有分层（`Application/Spine` 不含渲染器 → `SpineRuntime` 出帧 → App 贴帧）本来就是「native 渲染 + 贴帧」，换成 WebView 后照旧出 PNG/位图给前端即可，不必引入 spine-ts（省掉一套 vendor + 许可问题）。 |
| 打包体积 | 现有 519 MB。Evergreen 运行时通常已在机（Win11 自带、Win10 经 Edge 推送）；即便走 Fixed Version 分发多 ~180 MB，占比也不算突兀。 |
| 可测性 | **这是最大的长期利好**。现在 `App` 13k 行 0 测试；改成「薄宿主 + Web 前端」后，前端可用 Playwright 驱动（本机已有 `playwright-cli` / `agent-browser` 能力），逻辑仍留在有测试的 `Application`。相当于把「需要测试的逻辑必须下沉」这条铁律**从约束升级成架构事实**。 |

### 4.2 不利/需要额外工程的一面

| 维度 | 判断 | 缓解 |
| --- | --- | --- |
| **运行时依赖** | Evergreen 依赖用户机已装 WebView2 Runtime。模组工具的受众机器不可控，且**可能离线**。 | 分发 Fixed Version（+体积），或启动时检测并引导安装；两者都要写。 |
| **内存** | Chromium 会另起浏览器进程，基线多 ~100–200 MB。项目当前最大的痛点恰恰是「127 万条 → 1,250.9 MiB / 872 ms UI 冻结」。 | 页级分页已把常驻量压到 200 条/页；但要用 `--single-process` 之外的默认多进程模型，内存不能指望下降，只能指望**不叠加**。 |
| **IPC 延迟** | 滚动/筛选/跳转都要一次 `PostWebMessageAsJson` 往返。 | 预取窗口 + 本地虚拟主机提供二进制；把「一次查询回一页」的契约固定下来（`AssetCatalog` 已经就是这个形状）。 |
| **中文 IME** | Chromium 输入法在 WebView2 里可用，但需要正确配置（含 `CoreWebView2EnvironmentOptions` 与 PerMonitorV2 DPI 清单）。 | 属已知可解项，但要专门测（本项目全中文界面 + `JsonTreeEditor` 大量文本输入）。 |
| **文件对话框/剪贴板/`ShowDialog`** | 11 文件用 `Microsoft.Win32`、~30 个 `ShowDialog`、10 个 `MessageBox`。 | 走「原生对话框经 IPC 回调」路线，保留系统外观与路径语义；不要用 Web 的 `<input type=file>` 顶替（拿不到真实路径、拖拽语义也不同）。 |
| **DPI / 多显示器** | Chromium 自行处理 per-monitor DPI，但宿主窗口缩放时有两套缩放体系要对齐。 | 需要一份 PerMonitorV2 清单与统一缩放策略。 |
| **无障碍** | WPF 的自动化树被替换成 Chromium 的 UIA。 | 现状本来也没做无障碍（无相关代码），属中性。 |
| **调试** | DevTools 让前端调试**变好**；但 JS↔.NET 跨边界调试比纯 WPF 复杂。 | 可接受。 |
| **⚠️ 不解决跨平台** | **WebView2 仍然是 Windows-only**。若引入 WebView 的动机是「以后上 Linux/macOS」，这条路**给不了**。 | 真要跨平台，应评估「本地 HTTP 服务 + 浏览器」或 Avalonia/MAUI/Electron-Tauri 外壳——那是另一个决策，与 WebView2 不同。 |
| **工具链** | 仓库现在是纯 .NET（构建入口 `python logs/lme.py build`），**没有 `package.json`、没有 Node 构建**。 | 引入 Vite + TypeScript 意味着新增 Node 依赖、lockfile、构建步骤与 CI 变化——对「PATH 为空、靠 `lme.py` 显式解析 dotnet」的本机环境是实打实的额外基建。 |

---

## 5. 三方案对比

| | A. 全量 WebView 重写 | B. 混合：WPF 外壳 + 面板级 WebView2 | C. 不引入 WebView，先补 MVVM/下沉 |
| --- | --- | --- | --- |
| 范围 | 22 个 UI 单元全部重写 | 先挑 1 个高密度面板（建议资源库的「卡片流/详情」或关联图） | `App` 里的逻辑搬进 `Application` + ViewModel |
| 新增代码 | ~11k 行视图逻辑重写为 TS + IPC 层 | 一个面板的 TS + 一层通用 IPC 契约 | 0 新语言；纯 C# 重构 |
| 风险 | 高：`ShowDialog`/IME/DPI/对话框/树状态全要重做，且与 S3c/S4 撞车 | 中：范围可控，失败可退 | 低 |
| 收益 | UI 现代化 + 前端可测 | 验证「桥接真的可行」用最小代价 | **直接解决当前真痛点**（13k 行 0 测试、单文件 2,501 行） |
| 可回退 | 否 | 是（面板级可摘） | 是 |

---

## 6. 建议路线

**不建议现在做 A。建议 C → B → 视 B 的结果再决定要不要走向 A。**

1. **第一步（C，无论最终是否上 WebView 都该做）**
   把 `AssetsWorkbenchPage` / `BankWorkbenchPage` / `TextWorkbenchPage` 三巨头的**状态与决策**剥成 `Application` 层的 ViewModel/Presenter（无 WPF 依赖），code-behind 只留控件管道。这一步：
   - 严守既有铁律（`App` 无测试 ⇒ 逻辑下沉 `Application`），产出可被 `Domain.Tests` 覆盖；
   - 是 A 与 B 的**共同前置**——不做这步，A 只是把乱麻从 C# 搬进 JS；
   - 与 S3c/S4 同向（S3c 本就在收窄 `Assets`）。
2. **第二步（B，验证性试点）**
   定义一份最小 IPC 契约（建议直接复用 `AssetSearchQuery` / `AssetCatalogPage` / `RelationDeepLink` 的形状），只把一个面板嵌进 WebView2，把「运行时检测/分发、IME、DPI、对话框回调、二进制预览服务」这五个未知量一次性打掉。
3. **第三步（再判断 A）**
   试点通过后再决定是否推平其余 21 个 UI 单元。判断依据应是试点暴露的**真实工时与缺陷密度**，而不是「Web 看起来更现代」。

---

## 7. 若真要上：必须提前定的三件事

1. **IPC 契约归属**：桥接口必须定义在 `Application`（无 WPF），否则铁律「需测试的逻辑下沉 Application」被绕过，且未来换壳要重写两遍。
2. **二进制通道**：图片/Spine 帧走 `SetVirtualHostNameToFolderMapping` 或本地回环，**禁止 base64 过 IPC**（大纹理下会立刻成为新瓶颈）。
3. **统一分页契约**：所有列表一律「查询 → 一页」；`AssetCatalog.Page(query, offset, take)` 已有此形状，Web 侧不要再引入"取全量再前端筛"的写法——那等于放弃 S1–S4 的全部性能成果。

---

## 8. 本次分析的边界

- 全部结论基于**读源码 + 目录统计**；未启动程序、未跑 build/test、未实测 WebView2 性能与内存（那需要先做出 B 的试点才能测）。
- 未评估具体前端框架选型（React/Vue/Svelte/纯 TS）——在 C 步完成前，选型是无意义的优化。
- 未评估许可（WebView2 SDK 随 Edge 分发，通常无额外许可成本；Spine 因保持 native 渲染，**不新增**许可面）。

---

## 9. 一句话总结

**WebView2 与这个项目的技术栈不冲突，且 `AssetCatalog` / `RelationDeepLink` / `IReferenceRevealable` 这套「纯数据 + 显式契约」的家底让它比一般 WPF 项目更适合桥接；但它换不来跨平台，也不会自动修好「2,501 行单文件、13k 行 0 测试」这个真正的问题。所以：先做 C（下沉逻辑），再做 B（单面板试点），A 留到试点数据说话。**
