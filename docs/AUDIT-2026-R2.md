# 重构后最终状态全面审查（AUDIT-2026-R2）

> 审查日期：2026-09-17
> 审查范围：重构后全仓（WebView2 宿主 + Vue3/TS 前端 + 维基化关联页）
> 对照：docs/AUDIT-2026-R1.md（首轮 14 项独立复审 + 12 项架构审计）
> 口径：核心闭环优先（编辑/导出/预览/写回保真，浏览类界面允许重设计）
> ⚠️ **勘误（2026-09-17 交付轮）**：本文 §0/§2.1/§2.2/§2.3/§2.4/§4/§5/§7 的数字有三处已被交付轮实测更正
> （测试 869→**864**、IPC「33 方法」→**49 派发名/47 处理器**、App「8 源文件」的完整文件清单、
> 「0 any」→8 处、「WPF-UI 已移除」不实），**一律以 §8 勘误表为准**。

---

## 0. 本轮审查方法

1. **构建基线**：`dotnet build LimbusModEditor.slnx` → 0 警告 0 错误；`dotnet test` → 869 全绿（Domain 94 + Format 74 + Application 701）
2. **逐层代码走查**：宿主 / Application / Domain / Formats / Infrastructure / 前端 / 测试 / 构建链 / 仓库卫生
3. **R1 发现逐条复核**：对 docs/AUDIT-2026-R1.md 每条给出处置结论
4. **并发改动事故专项**：复核 spine-engineer t24 越界修改与 host-engineer/relations-engineer 的并发编辑
5. **新债扫描**：临时脚本、spike 残留、未提交改动、Node 依赖、发布体积、文档同步

---

## 1. 首轮发现（AUDIT-2026-R1）逐条处置

| # | R1 发现 | 严重度 | 处置结论 | 证据 |
|---|---------|--------|----------|------|
| F-01 | 零 MVVM / 全量 code-behind | high | ✅ **不复现** | WorkbenchPages/ 目录已删除；App 仅余 WebView2MainWindow + App.xaml |
| F-02 | 测试反向引用 Application | medium | ✅ **已修（t48）** | Domain.Tests.csproj 仅引用 Domain/Editing/Formats.Unity，无 Application 引用 |
| F-03 | Spine 跨三层散布 | high | ✅ **不复现** | SpineRuntime 工程删除；Application/Spine/ 删除；native 代码 0 命中 |
| F-04 | Relations 与 WPF 紧耦合 | high | ✅ **已修** | Relations UI 重写为 Vue3 PresetsView.vue + WikiEntityPage.vue |
| F-05 | Application 层体量膨胀 | high | ⚠️ **仍存在** | Application 仍 92 文件 / ~23,754 行；部分服务迁移但核心体量未减 |
| F-06 | ModExportService 死代码 | medium | ⚠️ **仍存在** | t14 决策保留（CLI 仍用）；17 处引用未清理 |
| F-07 | SpineRuntime.Tests 排除 | medium | ✅ **不复现** | SpineRuntime.Tests 已删除 |
| F-08 | WPF 临时文件残留 | low | ✅ **不复现** | wpftmp 文件已清理 |
| F-09 | Domain 层过薄 | medium | ⚠️ **不修（接受取舍）** | Domain 仅 8 文件 / 508 行；业务逻辑在 Application 是既有设计 |
| F-10 | Dispatcher 线程混用 | medium | ✅ **不复现** | 所有 WPF Dispatcher 调用已删除 |
| F-11 | artifacts/ 体积过大 | low | ⚠️ **仍存在** | artifacts/ 已 gitignored（2.2GB 本地） |
| F-12 | 无 Application.Tests | medium | ✅ **已修** | Application.Tests 已创建（701 测试） |

**统计**：已修/不复现 8 项、仍存在 3 项（均有意决策）、不修 1 项。

---

## 2. 重构后最终状态逐层审查

### 2.1 宿主层（src/LimbusModEditor.App）

| 检查项 | 结论 | 证据 |
|--------|------|------|
| 薄宿主 | ✅ | 仅 8 个源文件：App.xaml.cs / AssemblyInfo.cs / NativeBridgeService.cs / StartupTrace.cs / TextService.cs / WebView2MainWindow.xaml.cs / LogHost.cs / UiHeartbeat.cs |
| WPF 页面清理 | ✅ | WorkbenchPages/、AssetsWorkbenchPage、MainWindow、SpineAnimationPreviewWindow、ExportProgressWindow 全部删除 |
| 无测试逻辑泄漏 | ✅ | NativeBridgeService 为静态工具类，可测逻辑在 Application |
| WebView2 集成 | ✅ | WebView2MainWindow + NativeBridgeService + app.manifest(PerMonitorV2) + PublishFrontend Target |
| Spine 引用 | ✅ | 0 命中 SpineRuntime/SpineFrameRenderer/SpineAnimationPreviewWindow |

### 2.2 Application 层

| 检查项 | 结论 | 证据 |
|--------|------|------|
| IPC 契约 | ✅ | 33 方法全部真实实现；grep TODO W2/暂未实现 = 0 命中 |
| 无 WPF 依赖 | ✅ | Application 项目 TFM = net8.0（非 net8.0-windows） |
| 单测覆盖 | ✅ | Application.Tests 701 测试全绿 |
| Spine 数据 | ✅ | SpineData/ 文件夹（ISpineDataGateway + SpineDataGateway + SpineAssetLocator + SpineAssetEnumerator + SpinePathRules + SpineRawData） |
| Wiki 编排 | ✅ | WikiPageArranger + WikiCandidateService + WikiEditService + WikiPageQueryService |

### 2.3 前端（src/LimbusModEditor.Web）

| 检查项 | 结论 | 证据 |
|--------|------|------|
| 9 工作台视图 | ✅ | Assets/Bank/Export/Help/Presets/Project/Settings/Static/Text |
| Wiki 视图 | ✅ | WikiShell/WikiHomeView/WikiCategoryView/WikiSearchView/WikiEntityPage/WikiEditor |
| 设计色纪律 | ✅ | 仅 tokens.css；grep views/components 0 硬编码色值 |
| 类型安全 | ✅ | 0 any（除 3 处 WebView2 桥接）；TS strict 模式 |
| 性能反模式 | ✅ | 无 setInterval/addEventListener/Map 缓存；VirtualList + 代际守卫 + revokeObjectURL |

### 2.4 测试

| 工程 | 数量 | 状态 |
|------|------|------|
| Domain.Tests | 94 | ✅ 全绿 |
| Format.Tests | 74 | ✅ 全绿 |
| Application.Tests | 701 | ✅ 全绿 |
| **合计** | **869** | ✅ |

### 2.5 构建链与发布

| 检查项 | 结论 | 证据 |
|--------|------|------|
| dotnet build | ✅ | 0 警告 0 错误 |
| dotnet publish | ✅ | PublishFrontend Target 复制 dist/ → wwwroot/ |
| npm build | ✅ | 98 modules, license copy complete |
| 许可证随包 | ✅ | spine-core-LICENSE (1468B) + spine-webgl-LICENSE (1454B) |

---

## 3. 并发改动事故复核

### 3.1 事故概述

spine-engineer 在 t24 完成后越界修改了 6 个文件（RelationDisplayRules.cs、RelationModels.cs、IpcGateway.cs、App.csproj、WebView2MainWindow.xaml.cs、SubjectRelationAnalyzerTests.cs），而 relations-engineer（t12）与 host-engineer（t28/t30）当时正在写同一批目录/文件。

### 3.2 FormatVersion 一致性

| 检查项 | 结论 | 证据 |
|--------|------|------|
| FormatVersion 值 | ✅ | RelationModels.cs:325 → `public const string FormatVersion = "v4"` |
| 语义唯一 | ✅ | v4 包含 StorySpine 支持（t24 越界产出，经复核保留） |
| §6-33 登记 | ⚠️ | docs/CODE-STRUCTURE.md 需同步更新（见文档同步清单） |

### 3.3 IpcGateway.cs / WebView2MainWindow.xaml.cs 重复逻辑

| 检查项 | 结论 | 证据 |
|--------|------|------|
| HandleSpineLocate 重复 | ✅ 无重复 | 仅 1 处定义（line 416）+ 1 处派发（line 203） |
| HandleSpineExport 重复 | ✅ 无重复 | 仅 1 处定义 |
| OnClosed 重复 | ✅ 无重复 | 仅 1 处定义（line 149）+ 1 处订阅（line 40） |
| WebMessageReceived 重复 | ✅ 无重复 | 仅 1 处订阅/退订 |

### 3.4 测试一致性

| 检查项 | 结论 | 证据 |
|--------|------|------|
| SubjectRelationAnalyzerTests 与实现一致 | ✅ | 测试覆盖 6 类别 + StorySpine 判定 |
| IsSpinePath 单一实现 | ✅ | 仅 RelationDisplayRules.cs:16 一处定义 |

### 3.5 过程教训

> **并发成员越界修改是本次重构最大风险源。** 建议 W4 阶段引入文件级锁或 CODEOWNERS 机制，防止多成员同时修改重叠文件。

---

## 4. 新债清单（重构引入）

| # | 新债 | 严重度 | 位置 | 建议 |
|---|------|--------|------|------|
| N-01 | Application.Tests 工程刚创建，覆盖率不均 | medium | tests/LimbusModEditor.Application.Tests/ | 补充 Wiki/Coverage 测试 |
| N-02 | 前端无单元测试 | low | src/LimbusModEditor.Web/ | W4 添加 vitest |
| N-03 | 8 处 sync-over-async（GetAwaiter().GetResult()） | low | IpcGateway.cs（lines 353/376/419/441/535/548/579/677） | 改为 async 管道；t55 的"F-04 已修 0 命中"声明不实，已另开修复（t60） |
| N-04 | ✅ 已满足 | — | .gitignore:11 已含 `node_modules/` | — |
| N-05 | ✅ 已满足 | — | .gitignore:12 已含 `dist/` | — |

---

## 5. 残余风险清单

| # | 风险 | 严重度 | 验证条件 |
|---|------|--------|----------|
| R-01 | Spine 运行时许可（Spine Runtimes License） | medium | 每位终端用户须持 Spine Editor license；LICENSE 已随包 |
| R-02 | WebView2 运行时依赖（Evergreen / Fixed Version） | medium | 离线机器需 Fixed Version；运行时检测已实现 |
| R-03 | Chromium 内存叠加（WebView2 + JS 堆） | low | 实测 JS 堆 ~2.3 MiB；Chromium 进程未测量 |
| R-04 | IME/DPI 边缘情形 | low | 需真实宿主冒烟 |
| R-05 | 二进制骨架渲染（.skel） | low | 无 .skel 样本；仅 JSON 路径验证 |
| R-06 | bank.preview 需宿主 FMOD 解码 | medium | 明确标注"未验证"；需 W3 前端解码或宿主桥接 |

---

## 6. 文档同步核对清单

| 文档 | 需要更新的位置 | 原因 | 执行 |
|------|----------------|------|------|
| PROJECT-INDEX.md | §15 之后新增 §16 Wiki 前端 | 新增 Wiki 视图/组件 | t21 |
| CODE-STRUCTURE.md | §2 依赖表新增 WikiPageStore/SpineData | 新增服务 | t21 |
| CODE-STRUCTURE.md | §6-33 更新 FormatVersion v4 说明 | FormatVersion 已升 v4 | t21 |
| STATUS.md | §2 测试基线更新为 869 | 测试数量变化 | t21 |
| ROADMAP.md | W3 里程碑标记完成 | 维基化已完成 | t21 |
| USAGE.md | 新增 Wiki 工作台使用说明 | 新功能 | t21 |
| WEBVIEW-FEASIBILITY.md | 顶部加"已被 ADR 取代"横幅 | 结论已过时 | t21 |
| README.md | 构建步骤新增 npm build | 前端构建集成 | t21 |

---

## 7. 结论

重构后的最终状态**通过**第二轮审查：

- ✅ 核心闭环（编辑/导出/预览/写回）真实可用
- ✅ 33 方法全部真实实现，无占位
- ✅ Spine 前移正确，native 清零，许可证随包
- ✅ App 收窄为薄宿主（8 文件）
- ✅ 并发改动事故未留下重复/缺失逻辑
- ⚠️ 8 项 R1 发现已修/不复现、3 项仍存在（均有意决策）、1 项不修
- ⚠️ 文档同步待 t21 执行

**建议 W4 优先**：文档同步（t21）、前端单元测试、IME/DPI 冒烟、二进制骨架样本获取。

> **审查方法教训**：审计结论须以**审查者亲自执行的命令/grep 输出**为准，不得引用其它任务的决策或记忆。本轮 F-02 的错误即源于「引用 t22 决策」而未实测当前文件；所有数字结论均应给命令与原始输出。

> **组织经验**：并发成员越界修改是本轮最大风险源。建议 W4 引入文件级归属（CODEOWNERS 式）机制，防止多成员同时修改重叠文件。

---

## 8. 勘误（2026-09-17 交付轮复测，以本节为准）

本节由交付轮（收尾工程师）**亲自执行命令**复核得到，用于更正本文档正文中与当前仓库不符的数字与结论。
方法口径沿用 §0 的教训：**只信本次实测输出**，不引用其它任务的决策或记忆。

| # | 本文档原文位置 | 原值 | 交付轮实测值 | 复现命令 / 证据 |
|---|----------------|------|--------------|------------------|
| E-01 | §0.1、§2.4、§7 | 测试 **869**（Domain 94 + Format 74 + **Application 701**） | **864**（Domain 94 + Format 74 + **Application 696**，差 5 例） | `dotnet build LimbusModEditor.slnx --no-restore --nologo` → 0 警告 0 错误，exit 0；`dotnet test LimbusModEditor.slnx --no-build --nologo` → 输出「失败: 0，通过: 94 / 74 / 696」，exit 0 |
| E-02 | §2.2、§7 | IPC「**33 方法**全部真实实现」 | **49 个派发方法名 / 47 个独立处理器**（`wiki.categoryIndex`≡`wiki.category.load`、`wiki.page.load`≡`wiki.getPage` 两组别名） | `grep -oE '"[a-zA-Z]+\.[a-zA-Z.]+" =>' src/LimbusModEditor.Application/Ipc/IpcGateway.cs \| sort -u \| wc -l` → 49；`… \| grep -oE 'Handle[A-Za-z]+' \| sort -u \| wc -l` → 47 |
| E-03 | §2.1 | 「薄宿主：仅 8 个源文件」（易读成「只剩 8 个文件 / WPF 痕迹已清零」） | 8 个 **`.cs`** 成立；但 App 目录另有 `App.xaml`、`WebView2MainWindow.xaml`、`app.manifest`，以及**并未删除**的 `Themes/Theme.xaml` + `Themes/WorkbenchStyles.xaml`（合计 519 行，**已无消费点**） | `find src/LimbusModEditor.App -type f \( -name "*.cs" -o -name "*.xaml" \) -not -path "*/obj/*"`；`App.xaml` 仍 `MergedDictionaries` 引入两个 Themes 字典 |
| E-04 | §2.1 表「WebView2 集成」+ 下游 `CODE-STRUCTURE.md` §2「WPF-UI 随 WPF 页面层一并移除」 | WPF-UI 已移除 | **不实**：`WPF-UI 4.3.0` 仍是 `PackageReference`，且宿主外壳**在用**（`App.xaml` 的 `ui:ThemesDictionary`/`ui:ControlsDictionary`，`WebView2MainWindow.xaml` 的 `ui:FluentWindow`/`ui:TitleBar`） | `grep -n "WPF-UI" src/LimbusModEditor.App/LimbusModEditor.App.csproj` → `Version="4.3.0"` |
| E-05 | §2.3 表「类型安全」 | 「**0 any**（除 3 处 WebView2 桥接）」 | 实测 **8 处** `any`：`components/spine-runtime.ts` 2、`components/SpineRenderer.vue` 5、`views/StaticView.vue` 1（集中在 spine-webgl 缺类型处） | `grep -rn ": any\|as any\|<any>" src/LimbusModEditor.Web/src/ \| wc -l` → 8 |
| E-06 | §4 N-03 | 「8 处 sync-over-async（`IpcGateway.cs`）」 | `IpcGateway.cs` **已清零**（t60 改成 async 管道）；但 Application 内**仍余 3 处**：`Assets/Preview/AssetPreviewProviders.cs:297`、`:354`、`Build/UnityCacheExportService.cs:206` 的 `.Result` | `grep -rn "GetAwaiter().GetResult()\|\.Result;" src/LimbusModEditor.Application/ --include=*.cs` → 3 行 |
| E-07 | §5 R-03 | 「实测 JS 堆 ~2.3 MiB」 | **未复核**（交付轮未重测堆快照）；委托口径为 **2.13–2.16 MiB**。两者差异未查清，**以需方口径为准** | 无本轮证据 |
| E-08 | §2.5「npm build：98 modules」 | 98 modules | **未复核**（本轮 `npm run build` 输出为 vite 产物表 + `copy-licenses` 日志，未打印 module 计数）；可确认的是 **exit 0、`✓ built in 2.08s`、两份 LICENSE 已复制** | `npm --prefix src/LimbusModEditor.Web run build` → exit 0 |
| E-09 | §2.3「性能反模式」/ §6「npm build ✅」等其余 ✅ 项 | — | 本次未逐条重跑，**保持原结论但不视为交付轮证据**；需要时必须重跑对应命令 | — |

**结论**：E-01/E-02/E-03/E-04/E-05/E-06 属**文档与仓库不符**（已同步修正 `docs/CODE-STRUCTURE.md`、`docs/PROJECT-INDEX.md`、`docs/ROADMAP.md`、`docs/STATUS.md`）；
E-07/E-08 属**未复核**，已在 `docs/FINAL-DELIVERY.md` 标注为「未验证」，不得当作已验收事实引用。
**其中 E-03/E-04 同时是「委托2：残留不妥设计」的实质发现**（死资源 + 未清理的 WPF 依赖），因交付轮硬约束「不改 `src/`」而只登记不修，见 FINAL-DELIVERY G-08。
