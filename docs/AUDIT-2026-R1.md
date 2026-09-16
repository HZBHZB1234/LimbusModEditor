# 架构级反模式审查与仓库卫生审计（AUDIT-2026-R1）

> 审查日期：2026-09-15
> 审查范围：`src/`（13 工程）、`tests/`（3 工程）、仓库根、`artifacts/`、`logs/`、`.gitignore`、`.slnx`、`Directory.Build.props`
> 已排除项：`docs/REVIEW.md` 中已记录的 14 项复审发现（见 §0 排除清单）
> 口径：全量改为 WebView2 + Vue3/TS 前端；Spine 渲染全部前移前端并删除现有 native Spine 逻辑

---

## 0. 与 docs/REVIEW.md 的边界（已排除项）

以下 14 项已在 `docs/REVIEW.md` §2.3 独立复审中记录，本轮**不再重复**：

1. FSB5 解析器头部布局发明（HIGH，已修复）
2. ARGB32 字节序 B,G,R,A（HIGH，已修复）
3. R8 写入 alpha 而非红通道（MEDIUM，已修复）
4. EditUnityFields/FindReferencers 同步 UI 线程（MEDIUM，已修复）
5. FindBundleReferencers 缺 OriginatingFile（MEDIUM，已修复）
6. 构建验证 `.stepN.tmp` 残留（MEDIUM，已修复）
7. m_FileID→0 不检查悬空 m_PathID（LOW，已修复）
8. DiffDependencies ToDictionary 崩（LOW，已修复）
9. FMOD 缓存指纹仅大小/时间戳（LOW，已修复）
10. BankAudioService 整库读入内存（LOW，记录待优化）
11. ValidatePointerTarget 跳过悬空检查（LOW，已修复）
12. 单元测试引用 Application（LOW，已知取舍）— *本轮从架构角度重新审视，见 F-02*
13. AssetSearch MaxSize<0 静默关闭上限（LOW，已修复）
14. FSB5 名称越界诊断不精确（LOW，已在重写中处理）

此外，`REVIEW.md` §6.1 提及的「保留未删的旧代码（有意）」中关于 ModExportService 的**处置决策**已被本轮继承并**从仓库卫生角度重新评估**（见 F-06）。

---

## 1. 测量总览

### 1.1 src/ 各工程规模

| 工程 | .cs 文件数 | 总行数 | 说明 |
|---|---:|---:|
| LimbusModEditor.App | 15 | 12,428 | WPF 宿主，含 9 个 code-behind |
| LimbusModEditor.Application | 92 | 23,754 | 服务层（最大） |
| LimbusModEditor.Domain | 8 | 508 | 纯模型 |
| LimbusModEditor.Editing | 4 | 506 | 图像编辑 |
| LimbusModEditor.Infrastructure | 1 | 15 | 基础结构 |
| LimbusModEditor.Formats.Abstractions | 1 | 41 | 格式抽象 |
| LimbusModEditor.Formats.Unity | 7 | 3,342 | Unity 格式 |
| LimbusModEditor.Formats.Bank | 6 | 933 | Bank 格式 |
| LimbusModEditor.Formats.Carra | 5 | 522 | Carra 格式 |
| LimbusModEditor.Formats.Lunartique | 2 | 173 | Lunartique 格式 |
| LimbusModEditor.Formats.Rebank | 1 | 142 | Rebank 格式 |
| LimbusModEditor.SpineRuntime | 5 | 533 | Spine 离线渲染 |
| **src/ 合计** | **147** | **42,797** | — |

> 注：统计排除 `obj/`、`bin/` 目录。Application 层行数占 src/ 总量的 **55.5%**。

### 1.2 tests/ 各工程规模

| 工程 | .cs 文件数 | 总行数 | 在 .slnx 中 |
|---|---:|---:|:---:|
| LimbusModEditor.Domain.Tests | 87 | 14,696 | ✅ |
| LimbusModEditor.Format.Tests | 22 | 2,569 | ✅ |
| LimbusModEditor.SpineRuntime.Tests | 3 | 320 | ❌ 刻意排除 |
| **tests/ 合计** | **112** | **17,585** | — |

### 1.3 仓库卫生

| 路径 | 文件数 | 体积 | Git 跟踪 |
|---|---:|---:|:---:|
| `artifacts/` | 212 | 2,214.1 MB | ❌ 已忽略 |
| `logs/` | 316 | 2.5 MB | ❌ 已忽略 |
| `src/.../wpftmp.csproj` | 1 | — | ❌ 已忽略 |

---

## 2. 发现清单

### F-01 · 零 MVVM 模式 — 全量 Code-Behind 命令式架构

| 项 | 内容 |
|---|---|
| **严重度** | **high** |
| **类别** | 架构反模式 |
| **证据** | `grep -r "ViewModel\|ICommand\|INotifyPropertyChanged\|BindableBase\|SetProperty" src/ --include="*.cs"` 返回 **0 命中**。App 层 9 个 code-behind 文件合计 12,428 行，最大的 `AssetsWorkbenchPage.xaml.cs` 2,340 行含 54 个 `private void` 方法。 |
| **根因** | 项目自始至终未引入任何 MVVM 框架（Prism、CommunityToolkit.Mvvm、ReactiveUI 等）。所有视图逻辑、状态管理、事件处理、异步编排全部置于 `.xaml.cs` code-behind 中。 |
| **建议修复** | 本轮重构直接替换为 Vue3/TS 响应式框架，无需在 WPF 侧补 MVVM。**不需要修**。 |
| **重构交互** | ✅ **重构后会自然消失** — Vue3/TS 前端将完全取代 WPF code-behind。 |
| **影响面** | 全部 9 个 code-behind 文件（AssetsWorkbenchPage 2340、BankWorkbenchPage 1396、MainWindow 1091、TextWorkbenchPage 1049、JsonTreeEditor 859、StaticWorkbenchPage 846、PresetWorkbenchPage 835、WorkbenchShell 544、App 45 行）。 |

---

### F-02 · 测试工程反向引用 Application 层（分层违规）

| 项 | 内容 |
|---|---|
| **严重度** | **medium** |
| **类别** | 架构反模式 / 分层瑕疵 |
| **证据** | `tests/LimbusModEditor.Domain.Tests/LimbusModEditor.Domain.Tests.csproj` 含 `<ProjectReference Include="..\..\src\LimbusModEditor.Application\LimbusModEditor.Application.csproj" />`。Domain 层测试直接依赖 Application 服务层，违反依赖方向（Domain 应独立于 Application）。 |
| **根因** | `REVIEW.md` #12 已记录为「已知取舍」——测试需要 `AtomicOutput`/`BatchReplacement` 等服务。但该决策使 Domain.Tests 实际上变成了 Application.Tests，污染了分层边界。 |
| **建议修复** | 短期：将需要 Application 服务的测试用例拆入独立的 `LimbusModEditor.Application.Tests` 工程（当前不存在）。长期：重构后 Application 层服务将由 WebView2 前端通过 IPC 调用，测试策略需整体重做。 |
| **重构交互** | ⚠️ **与本轮重构无关但应修** — 测试工程的分层问题不会因前端迁移自动解决；需在 W2/W3 阶段重建测试策略。 |
| **影响面** | 1 个 csproj 文件，87 个测试文件，14,696 行测试代码。 |

---

### F-03 · Application 层 Spine 渲染逻辑未隔离 — 跨三层散布

| 项 | 内容 |
|---|---|
| **严重度** | **high** |
| **类别** | 架构反模式 / 紧耦合 |
| **证据** | Spine 相关代码散布于三层：① `LimbusModEditor.SpineRuntime`（5 文件 / 533 行）：`SpineFrameRenderer.cs` 231、`SpineDocument.cs` 155；② `LimbusModEditor.Application/Spine/`（5 文件 / 1,342 行）：`SpineModels.cs` 577、`SpinePreviewService.cs` 399、`SpineExportService.cs` 143、`SpineAnimationSourceService.cs` 177、`SpinePreviewProvider.cs` 46；③ `LimbusModEditor.App/SpineAnimationPreviewWindow.cs` 303 行 + `PresetWorkbenchPage.xaml.cs` 含 34 处 Spine 引用。**合计 3,178 行跨 3 工程。** |
| **根因** | Spine 渲染管线（解析 → 纹理加载 → 帧渲染 → 导出）被拆分为 Application 服务层 + SpineRuntime 类库 + App 预览窗口，且 `PresetWorkbenchPage` 直接在构造函数中 `new SpinePreviewService()`、`new SpineExportService()`、`new SpineAnimationSourceService()`，无任何接口抽象或服务注入。 |
| **建议修复** | 本轮重构后 Spine 渲染全部前移前端，这些代码将整体删除。**不需要修**。 |
| **重构交互** | ✅ **重构后会自然消失** — Spine 渲染全部前移至 Vue3 前端，三层代码全部删除。 |
| **影响面** | 11 个 .cs 文件，3,178 行代码。删除时需同步清理 `LimbusModEditor.SpineRuntime.csproj`、`LimbusModEditor.Formats.Unity/UnityClassId.cs` 中的 Spine 相关映射。 |

---

### F-04 · Relations 模块与 WPF 紧耦合 — 阻塞维基化重构

| 项 | 内容 |
|---|---|
| **严重度** | **high** |
| **类别** | 架构反模式 / 耦合 |
| **证据** | `LimbusModEditor.Application/Relations/` 含 10 文件 / ~2,848 行：`SubjectRelationAnalyzer.cs` 811、`PresetWorkbenchService.cs` 310、`RelationModels.cs` 314、`RelationStore.cs` 434、`PersonaRelationIndexService.cs` 274、`LangAnchorReader.cs` 161、`RelationQueryService.cs` 135、`RelationCategories.cs` 94、`RelationDisplayRules.cs` 91、`RelationDeepLink.cs` 64。`PresetWorkbenchPage.xaml.cs`（835 行）在构造函数中直接 `new PresetWorkbenchService(new RelationQueryService(new RelationStore(...)))`，UI 页面同时承担数据编排职责。 |
| **根因** | Relations 数据层（RelationStore、RelationQueryService）与展示层（PresetWorkbenchPage）未分离。Relations 既是被 W1 维基化重构的目标模块，又与 WPF UI 深度缠绕。 |
| **建议修复** | W3 维基化重构时需：①将 Relations 数据层（RelationStore、RelationQueryService、SubjectRelationAnalyzer）提取为纯后端服务，通过 WebView2 IPC 暴露；②删除 PresetWorkbenchPage.xaml.cs 中的 Spine/Preview 等展示逻辑；③维基化 UI 用 Vue3 重写。 |
| **重构交互** | ⚠️ **必须保留并迁到新架构** — Relations 数据层是维基化的核心资产，必须完整迁移到 WebView2 前端 + 后端 IPC 架构。 |
| **影响面** | 10 个 Application 文件 + 1 个 App code-behind，合计 ~3,683 行。 |

---

### F-05 · Application 层体量膨胀 — 服务层承担过多职责

| 项 | 内容 |
|---|---|
| **严重度** | **high** |
| **类别** | 架构反模式 / 职责过载 |
| **证据** | Application 层 92 文件 / 23,754 行，占 src/ 总量的 55.5%。含 6 个功能分组：Assets（预览/索引）、Build（导出/打包）、Caching（SQLite 表缓存）、Catalog（资产目录）、Debugging（Mod 应用）、Documents（JSON 编辑）、Relations（关联分析）、Scanning（启动/缓存扫描）、Spine（预览/导出/动画源）、StaticMods（静态表定位）、Texts（文本索引）。44 个文件使用 `NLog.LogManager`。最大文件：`UnityCacheSqliteIndexStore.cs` 1,381 行、`AssetPreviewProviders.cs` 937 行、`StartupScanService.cs` 890 行。 |
| **根因** | 项目没有明确的 Application 层边界；所有非 Domain、非 UI 的逻辑都堆积在 Application 层。导出管线、缓存扫描、Spine 渲染、Relations 分析等异质功能共处同一工程。 |
| **建议修复** | 本轮重构不需立即拆分 Application 层，但应在 W2 迁移时逐步将：①Spine 相关服务标记待删除（F-03）；②Relations 服务提取为独立模块（F-04）；③导出/打包/扫描服务保留并通过 IPC 暴露给 WebView2。 |
| **重构交互** | ⚠️ **必须保留并迁到新架构** — 核心服务（扫描、缓存、导出、Relations 数据层）需保留并暴露为 WebView2 后端 API。 |
| **影响面** | 92 个 .cs 文件，23,754 行。 |

---

### F-06 · ModExportService 死代码保留 — 仓库卫生

| 项 | 内容 |
|---|---|
| **严重度** | **medium** |
| **类别** | 死代码 / 仓库卫生 |
| **证据** | `ModExportService.cs`（497 行）位于 `src/LimbusModEditor.Application/Build/`。`grep -r "ModExportService" src/ --include="*.cs"` 命中 **4 个文件**（`ExportMatrix.cs`、`ModExportService.cs` 自身、`UnityCacheExportService.cs`、`Cli/Program.cs`）。`REVIEW.md` §6.1 确认另有 5 个测试文件引用（`BankExportTests`、`MultiFormatExportTests`、`ModExportServiceTests`、`SourceImportTests`、`LunartiqueConversionTests`），总计 ~17 处引用。该服务被标记为「有意保留」，但不再是界面入口。 |
| **根因** | `REVIEW.md` §6.1 决策「不做界面外的破坏性清理」。但 ModExportService 与将迁移到前端的导出功能重叠，属于死代码。 |
| **建议修复** | 在 W1 网关阶段前删除 ModExportService 及其 17 处引用。CLI 若仍需要导出功能，应在迁移后通过新的 WebView2 导出通道替代。 |
| **重构交互** | ⚠️ **与本轮重构无关但应修** — 导出功能将迁移到前端，旧 C# 导出服务应在重构前清理。 |
| **影响面** | 1 文件（497 行）+ 3 个引用文件 + 5 个测试文件。 |

---

### F-07 · SpineRuntime.Tests 被排除在解决方案外

| 项 | 内容 |
|---|---|
| **严重度** | **medium** |
| **类别** | 测试缺口 / 仓库卫生 |
| **证据** | `tests/LimbusModEditor.SpineRuntime.Tests/`（3 文件 / 320 行，含 `SpineRuntimeTests.cs` 170 行、`SyntheticData.cs` 115 行）**未包含在 `LimbusModEditor.slnx`** 中。`.slnx` 文件仅列出 Domain.Tests 和 Format.Tests。SpineRuntime.Tests.csproj 自身注释声明「本工程刻意保持独立，不并入主仓库 .slnx」。 |
| **根因** | 历史决策（见 csproj 注释），可能因为 SpineRuntime 是独立类库，测试需单独运行。 |
| **建议修复** | 本轮重构将删除 SpineRuntime 全部代码，因此该测试工程也应一并删除。**不需要修**。 |
| **重构交互** | ✅ **重构后会自然消失** — SpineRuntime 整体删除，测试随之删除。 |
| **影响面** | 1 个测试工程，3 文件，320 行。 |

---

### F-08 · WPF 临时文件残留工作目录

| 项 | 内容 |
|---|---|
| **严重度** | **low** |
| **类别** | 仓库卫生 |
| **证据** | `src/LimbusModEditor.App/LimbusModEditor.App_qlsqmiaq_wpftmp.csproj` 存在于工作目录。`.gitignore` 第 54-55 行已配置 `*_wpftmp.csproj` 和 `*_wpftmp.*` 忽略规则，`git ls-files` 确认该文件**未跟踪**。 |
| **根因** | WPF XAML 编译过程偶发残留临时工程文件。`.gitignore` 已正确配置，但工作目录中仍有残留。 |
| **建议修复** | 删除该文件（`git clean -fdx` 或手动删除）。本轮重构后 WPF 不再存在，问题自然消失。 |
| **重构交互** | ✅ **重构后会自然消失** — WPF 项目整体删除。 |
| **影响面** | 1 个文件。 |

---

### F-09 · Domain 层过薄 — 贫血模型

| 项 | 内容 |
|---|---|
| **严重度** | **medium** |
| **类别** | 架构反模式 / 设计 |
| **证据** | Domain 层仅 8 文件 / 508 行（平均 63.5 行/文件）。对比 Application 层 92 文件 / 23,754 行（平均 258.2 行/文件），Domain 层体量仅为 Application 层的 **2.1%**。Domain 层零项目引用（不依赖任何其他工程），所有业务逻辑集中在 Application 层。 |
| **根因** | 项目采用「大服务层 + 瘦领域模型」模式，Domain 层仅作数据容器（DTO/Record），无领域行为。 |
| **建议修复** | 本轮重构不需修改 Domain 层。但 W2 迁移时应评估：①将部分 Application 层逻辑下沉到 Domain（如值对象验证）；②Domain 层作为 WebView2 前后端共享的契约层，可考虑提取为独立的 `.Shared` 工程供 TypeScript 类型生成。 |
| **重构交互** | ⚠️ **与本轮重构无关但应修** — 不影响前端迁移，但影响长期架构健康。 |
| **影响面** | 8 个文件，508 行。 |

---

### F-10 · Code-Behind 中 Dispatcher 直接调用 — 线程模型混用

| 项 | 内容 |
|---|---|
| **严重度** | **medium** |
| **类别** | 架构反模式 / 线程安全 |
| **证据** | App 层 13 个文件使用 `Dispatcher.Invoke`/`BeginInvoke`/`InvokeAsync`。`JsonTreeEditor.xaml.cs` 7 处、`PresetWorkbenchPage.xaml.cs` 8 处、`TextWorkbenchPage.xaml.cs` 6 处、`AssetsWorkbenchPage.xaml.cs` 4 处。`MainWindow.xaml.cs` 含 53 处 `async Task`/`await` 调用。无统一的线程调度抽象。 |
| **根因** | 异步操作（扫描、解码、导出）直接在 code-behind 中启动，需手动 Dispatcher 回到 UI 线程更新控件。无 `SynchronizationContext` 封装或 `IProgress<T>` 统一抽象。 |
| **建议修复** | 本轮重构后 Vue3 前端使用 `async/await` + 响应式状态管理，无需 Dispatcher。**不需要修**。 |
| **重构交互** | ✅ **重构后会自然消失** — WPF Dispatcher 机制被 Vue3 响应式系统取代。 |
| **影响面** | 13 个文件，共 ~50 处 Dispatcher 调用。 |

---

### F-11 · artifacts/ 目录体积过大

| 项 | 内容 |
|---|---|
| **严重度** | **low** |
| **类别** | 仓库卫生 |
| **证据** | `artifacts/` 目录含 212 个文件，合计 **2,214.1 MB**。`.gitignore` 第 12 行已配置 `artifacts/` 忽略规则，`git ls-files` 确认**未跟踪**。 |
| **根因** | `dotnet publish` 输出目录，含 win-x64 自包含发布产物。 |
| **建议修复** | 定期清理（`git clean -fdx artifacts/` 或 CI 中配置 retention policy）。不影响重构。 |
| **重构交互** | ⚠️ **与本轮重构无关但应修** — 发布产物目录，不影响代码迁移。 |
| **影响面** | 212 文件，2.2 GB。 |

---

### F-12 · 测试工程无 Application.Tests — 服务层测试归属缺失

| 项 | 内容 |
|---|---|
| **严重度** | **medium** |
| **类别** | 架构反模式 / 测试缺口 |
| **证据** | `tests/` 下仅有 3 个测试工程：Domain.Tests（87 文件 / 14,696 行）、Format.Tests（22 / 2,569）、SpineRuntime.Tests（3 / 320）。**无 Application.Tests 工程**。Application 层 23,754 行代码中，仅被 Domain.Tests 间接覆盖（通过反向引用）。Build 组（ExportMatrix、ModExportService、ModPackExportService、UnityCacheExportService 等 5+ 文件）无直接测试覆盖。 |
| **根因** | 测试按「被测工程的依赖者」而非「被测工程本身」组织。Domain.Tests 引用 Application 层来测试 Domain 服务，但 Application 层自身的导出/打包/扫描服务无专属测试。 |
| **建议修复** | W2 迁移前创建 `LimbusModEditor.Application.Tests` 工程，至少覆盖：①导出管线（ModExportService、ModPackExportService、ExportMatrix）；②扫描服务（UnityCacheScanService、StartupScanService）；③Relations 数据层（RelationStore、RelationQueryService）。 |
| **重构交互** | ⚠️ **与本轮重构无关但应修** — 测试缺口不会自动修复，且 WebView2 迁移需要后端服务有测试保障。 |
| **影响面** | Application 层 92 文件 / 23,754 行，当前无专属测试工程。 |

---

## 3. 重构交互矩阵

| 编号 | 发现 | 重构后自然消失 | 必须保留并迁到新架构 | 与本轮重构无关但应修 |
|---|:---:|:---:|:---:|:---:|
| F-01 | 零 MVVM / 全量 Code-Behind | ✅ | | |
| F-02 | 测试反向引用 Application | | | ✅ |
| F-03 | Spine 跨三层散布 | ✅ | | |
| F-04 | Relations 与 WPF 紧耦合 | | ✅ | |
| F-05 | Application 层体量膨胀 | | ✅ | |
| F-06 | ModExportService 死代码 | | | ✅ |
| F-07 | SpineRuntime.Tests 排除 | ✅ | | |
| F-08 | WPF 临时文件残留 | ✅ | | |
| F-09 | Domain 层过薄 | | | ✅ |
| F-10 | Dispatcher 线程混用 | ✅ | | |
| F-11 | artifacts/ 体积过大 | | | ✅ |
| F-12 | 无 Application.Tests | | | ✅ |

**统计**：重构后自然消失 6 项、必须保留并迁 2 项、无关但应修 4 项。

---

## 4. 与后续 Wave 的输入映射

| Wave | 输入发现 | 行动 |
|---|---|---|
| W0 冻结/spike | F-06、F-12 | 冻结前清理 ModExportService；创建 Application.Tests 工程覆盖核心服务 |
| W1 网关+竖切片 | F-04、F-05 | Relations 数据层提取为独立模块；Application 层服务通过 IPC 暴露 |
| W2 全量迁移 | F-01、F-03、F-07、F-08、F-10 | 删除全部 WPF code-behind、SpineRuntime、Spine 服务、wpftmp 文件 |
| W3 关联维基化 | F-04、F-05 | Relations 模块维基化重写（Vue3 前端 + 多级二级页面） |
| W4 终审+集成 | F-02、F-09、F-11、F-12 | 测试分层整改、Domain 层补强、artifacts 清理策略 |

---

## 5. 未发现项（已验证）

以下审查点经测量确认**不存在问题**：

1. **SQLite 误提交**：`git ls-files *.db` 返回 0 结果，无 .db 文件被跟踪。
2. **logs/ 入库**：`git ls-files logs/` 返回 0 结果，已正确忽略。
3. **artifacts/ 入库**：`git ls-files artifacts/` 返回 0 结果，已正确忽略。
4. **wpftmp 入库**：`git ls-files *wpftmp*` 返回 0 结果，已正确忽略。
5. **根目录 .cs 文件**：无散落的根目录 C# 文件。
6. **Domain 层反向引用**：Domain 层零项目引用，分层方向正确。
7. **Formats 层反向引用**：所有 Formats 工程仅引用 Domain + Formats.Abstractions，方向正确。
8. **Infrastructure 层膨胀**：仅 1 文件 / 15 行，无膨胀问题。

---

## 6. 附录：原始测量命令

```powershell
# src/ 各工程 .cs 文件数与行数
Get-ChildItem -Path "src" -Recurse -Include *.cs | Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } | Group-Object { ($_.FullName -split '\\')[-3] } | Sort-Object Count -Descending

# 全仓 MVVM 模式搜索（0 命中）
Get-ChildItem -Path "src" -Recurse -Include *.cs | Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } | Where-Object { (Get-Content $_.FullName | Select-String -Pattern 'ViewModel|ICommand|INotifyPropertyChanged|BindableBase|SetProperty').Count -gt 0 }

# ModExportService 引用计数
Get-ChildItem -Path "src" -Recurse -Include *.cs | Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } | Where-Object { (Get-Content $_.FullName | Select-String -Pattern 'ModExportService' -SimpleMatch).Count -gt 0 }

# Spine 跨三层引用
Get-ChildItem -Path "src" -Recurse -Include *.cs | Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } | Where-Object { (Get-Content $_.FullName | Select-String -Pattern 'Spine').Count -gt 0 }

# artifacts/ 体积
Get-ChildItem -Path "artifacts" -Recurse | Measure-Object -Property Length -Sum

# 仓库跟踪验证
git ls-files artifacts/ logs/ *.db *wpftmp* | Measure-Object
```
