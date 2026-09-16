# 最终交付报告（FINAL-DELIVERY）

> 交付轮日期：**2026-09-17**（收尾交付工程师执行）
> 交付对象：Limbus Mod Editor（C#/.NET 8 桌面工具）——WebView2 薄宿主 + Vue3/TS 前端全量重构 + 维基化关联页
> 本报告口径：**所有数字与结论都来自本轮本机亲自执行的命令或对文件的实测**（命令与原始输出见 §2）；
> 凡未由本轮实测者一律标注「**未验证**」，不引用其它任务的决策或记忆，不编造。
> 交付轮硬约束（遵守情况见 §3）：**未修改 `src/`、`tests/` 下任何代码**；游戏数据只读；
> 仓库文本文件只用 read/edit/write 工具修改（铁律 §3-6）。

---

## 1. 三项委托的完成度与验收证据

### 委托 1：重构为 WebView2 + Vue3/TS，Spine 渲染全部前移到前端，并自测

| 验收项 | 结论 | 证据（本轮实测） |
|---|---|---|
| 宿主收窄为薄宿主 | ✅ | `src/LimbusModEditor.App/` = **8 个 `.cs`**（`WebView2MainWindow` / `NativeBridgeService` / `TextService` / `LogHost` / `UiHeartbeat` / `StartupTrace` / `App.xaml.cs` / `AssemblyInfo.cs`）+ `App.xaml` + `WebView2MainWindow.xaml` + `app.manifest`。⚠️ 另有两个**未删除且已无消费点**的主题 XAML（见 G-08） |
| 前端承担全部界面 | ✅ | `src/LimbusModEditor.Web/`（Vite + Vue3 + TS + vue-router + Pinia）：9 个工作台视图 + 维基外壳/首页/类目/搜索/实体页/编辑器；`npm run build` exit 0（`✓ built in 2.08s`） |
| Spine 渲染全在前端 | ✅ | 前端 `spine-webgl@4.0.26`（`components/spine-runtime.ts` + `SpineRenderer.vue`，JSON 主路径 + 二进制兜底 + `pma:true` 预乘 alpha）；**C# 侧只剩定位/喂字节与导出写盘**（`Application/SpineData/` + `Application/Spine/SpineExportService.cs`） |
| native Spine 彻底移除 | ✅ | `grep -rniE "SpineRuntime\|SpineFrameRenderer\|SpineAnimationPreviewWindow" src/ --include=*.cs --include=*.ts --include=*.vue --include=*.csproj` → **0 行**；`Application/Spine/` 的解析/预览层、`App/SpineAnimationPreviewWindow` 已不存在 |
| 许可合规 | ✅ | `THIRD-PARTY-NOTICES.md` 登记；发布产物 `wwwroot/licenses/` 内含 `spine-webgl-LICENSE`(1454B) 与 `spine-core-LICENSE`(1468B)，各带 SHA256（§2.2） |
| 发布集成可用 | ✅ | `PublishFrontend` Target 把 `dist/**` 复制进 `wwwroot/`；本轮两次 publish 均 exit 0，日志「发布前端产物：52 个文件」；`wwwroot/` 与 `dist/` **逐文件 SHA256 完全一致** |
| 自测（三件套基线） | ✅ | build exit 0（0 警告 0 错误）、test exit 0（**864 全绿** = 94 + 74 + 696）、`npm run build` exit 0；逐条输出见 §2 |

**⚠️ 未能复核的一项**：委托 1 转述的「真实骨架 `cg_40` 渲染成功（截图 `…\temp\t40-test\screenshot_cg40_final.png`）」——
截图文件**存在**（前序报告写的 `temp\40-test\` 是路径笔误），但**该截图的 canvas 区域只有 3 种颜色（纯背景）**，
按像素统计**不能证明渲染成功**（详见 §5 G-06）。因此「Spine 动画在真实数据上渲染成功」在**交付轮未获证据支持**，
只作为前序结论引用，列入人工冒烟清单 M-13 复测。

### 委托 2：审查项目，分析是否有残留的不妥设计需要修正

| 验收项 | 结论 | 证据 |
|---|---|---|
| 重构后终审已产出 | ✅ | `docs/AUDIT-2026-R2.md`（R1 逐条复核：8 已修/不复现、3 仍存在有意决策、1 不修；并发事故一致性复核；新债 3 项；残余风险 6 项） |
| R1 首轮审计 | ✅ | `docs/AUDIT-2026-R1.md`（12 项架构审计 + 14 项独立复审） |
| 交付轮复核发现的**报告与实测不符** | ⚠️ 6 项 | 见 `docs/AUDIT-2026-R2.md` §8 勘误 E-01~E-09（测试数、IPC 方法数、any 数、sync-over-async、WPF-UI/主题残留、未复核项） |
| 交付轮**新发现**的残留设计（本轮登记，未修） | ⚠️ 2 组 | **G-08 宿主 WPF 残留**（死主题资源 519 行 + `WPF-UI 4.3.0` 未移除 + 宿主 XAML 6 处内联色值）；**G-09 Application 内 3 处 sync-over-async** |

**本轮无法修的原因**：交付轮硬约束「不改 `src/`」。两组发现均已给出精确定位与处置建议（G-08/G-09），
并同步进 `docs/STATUS.md` §5 T-J7/T-J8 与 §6.2 已知问题表。

### 委托 3：以 limbuscompany.huijiwiki.com 为目标彻底重构关联页面

| 验收项 | 结论 | 证据（本轮实测） |
|---|---|---|
| 一一对等 14 类页面（规格） | ✅ | `docs/WIKI-PARITY-SPEC.md`：14 类站点地图 + 每类结构 Schema + 仿制清单（照搬 17 / 适配 8 / 放弃 11）+ 逐栏数据映射 |
| 不依赖 id 规则自动推断 | ✅ | 权威引擎 `Application/Relations/Authority/`：16 种权威来源 + 三档置信度 + `WritableSourceKind`（`Unknown`/`None`/`Path`）；不变量与矩阵见 `docs/CODE-STRUCTURE.md` §6-38、`docs/RELATIONS-AUTHORITY-MATRIX.md` |
| 允许多个二级页面 | ✅ | `WikiSecondaryPageRules.cs`（拆分判据）+ `WikiPageStore`（4 层页面树，CASCADE 删父不留孤儿） |
| 只显示本地可推导数据、不编造 | ✅（设计层） | 编排器只产出 `WritableSourceKind == Path` 的事实，`None`/`Unknown` 不进页面结构；推不出来留空 |
| 页面只「方便查看与编辑」/ 只改内容不改结构 / 只导出模组 | ✅（设计层） | 前端 `WikiEditor.vue` 只做字段级编辑；结构由页面库决定；无网页导出通道，导出只有「导出模组…」 |
| 内存与运行时速度 | ⚠️ 部分 | 前端 JS 堆 **2.13–2.16 MiB**（委托口径，**本轮未复测**）；二进制通道比 base64 省 33.3%（前序结论，未复测）；宿主进程内存**未测量**（见 §5 未验证项） |
| **页面可实际看到/编辑** | ❌ **未达成** | `wiki-pages.db` 在仓库与发布产物中**均不存在**（`find . -name wiki-pages.db` → 无输出）；「自动分析 → 生成页面 → 落库」链路未接通（权威引擎/编排器**无生产调用点**）→ **本机启动后 14 类页面全为空**（G-01） |

**委托 3 的诚实结论**：**结构、契约、查询面、编辑面、前端 4 视图是实的且有测试**（`wiki.*` 十个 IPC 处理器
经 `grep` 实测存在：`wiki.home` / `wiki.categoryIndex` / `wiki.category.load` / `wiki.page.load` / `wiki.getPage` /
`wiki.search` / `wiki.page.save` / `wiki.saveContent` / `wiki.getEditPlan` / `wiki.applyEdit`），
但**「谁来生成页面」这一步缺失**，因此用户口径中的「一一对等 14 类页面」目前只是**规格 + 空壳**，
**不是可运行结果**。该项列为本轮最高优先待办（`docs/STATUS.md` T-J1 / 本报告 G-01）。

---

## 2. 本轮命令与原始结果（交付验证基线）

### 2.1 四条命令与退出码

| # | 命令 | 退出码 | 关键原始输出 |
|---|---|---|---|
| 1 | `dotnet build LimbusModEditor.slnx --no-restore --nologo` | **0** | `已成功生成。 0 个警告 0 个错误`（15 个工程全部编译，含 3 个测试工程） |
| 2 | `dotnet test LimbusModEditor.slnx --no-build --nologo` | **0** | `已通过! - 失败: 0，通过: 94`（Domain，440 ms）／`失败: 0，通过: 74`（Format，40 s）／`失败: 0，通过: 696`（Application，42 s）→ **合计 864** |
| 3 | `npm --prefix src/LimbusModEditor.Web run build` | **0** | vite 产物表 + `✓ built in 2.08s`；`copy-licenses`：`Copied: spine-webgl-LICENSE` / `Copied: spine-core-LICENSE` / `License copy complete.` |
| 4 | `dotnet publish src/LimbusModEditor.App/LimbusModEditor.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish-win-x64 --no-restore` | **0** | `发布前端产物：52 个文件从 …\src\LimbusModEditor.Web\dist → wwwroot/`；`LimbusModEditor.App -> …\artifacts\publish-win-x64\` |

> 执行顺序说明：先跑 publish，核对一次一致性；再跑 `npm run build` → 再次 publish，
> 保证**发布产物确实来自最新一次前端构建**（本轮两次 publish 都 exit 0，且两次产物一致）。

### 2.2 发布一致性核对（wwwroot ↔ dist）

```text
sha256(src/LimbusModEditor.Web/dist/index.html)      = 712e1cfed4d1a0b59974563fdfe34eba054dcdc4a7b60164f7006fff64145d39
sha256(artifacts/publish-win-x64/wwwroot/index.html) = 712e1cfed4d1a0b59974563fdfe34eba054dcdc4a7b60164f7006fff64145d39
                                                       ↑ 完全一致（size 404B，mtime 同为 2026-09-17 00:20:30）
diff -rq src/LimbusModEditor.Web/dist artifacts/publish-win-x64/wwwroot   → 无输出（差异 0）
逐文件 SHA256 全量对比（find -exec sha256sum | sort → diff）              → 无输出
文件数：dist = 52，wwwroot = 52                                          → 相等
```

`wwwroot/licenses/`（两个 LICENSE 均在）：

```text
spine-core-LICENSE   1468 B   sha256 435774fb793b0f67892899fc934f98009e64fd90ad3ab964117274e279a0f50e
spine-webgl-LICENSE  1454 B   sha256 6142ee6cc2c03d3a918793e4750ae772bd3755c534d4a35e559e301acf51ec39
```

**发布目录现状（实测，注意与文档旧数字的差异）**：`artifacts/publish-win-x64` = **635 MB / 135 个文件**。
其中约 **433 MB 是本地 `cache/*.db` 索引库残留**（`unity-cache-index.db` 283,561,984 B、
`relation-index.db` 80,519,168 B、`text-index.db` 45,015,040 B、`bank-index.db` 22,650,880 B，
另有 `-shm`/`-wal` 侧文件），`fmod/` 3.7 MB（`fmod64.dll` + `fsbank64.dll`）。
`docs/CODE-STRUCTURE.md` §10 的「发布体积基线 519MB」与本机当前值不符——**差异来自 cache 残留**，
不能作为干净发布的体积基线（见 G-05）。

> 「增量复制残留 17 个孤儿」的旧结论**本轮未复现**：两次构建的前端产物哈希文件名一致，
> `wwwroot/` 与 `dist/` 完全相等。**口径更正**：该现象只在「两次构建的前端内容不同」时发生（G-05）。

---

## 3. 文档同步落地（逐份）

> 全部改动**只用 read/edit/write 工具**完成（铁律 §3-6，历史两次中文乱码事故的纪律）；改动清单见 §7 的 git 提交。

| 文档 | 本轮改了什么 | 依据 |
|---|---|---|
| `docs/PROJECT-INDEX.md` | ① §16「Wiki：前端视图/组件 + 服务层 + IPC 处理器」登记（前端 10 个文件、`Relations/` 与 `Relations/Authority/` 13 个文件、`wiki.*` 10 个处理器逐行表、`wiki-pages.db` 落盘与「不存在」实测）；② **新增 §8.17 `Ipc/`**（网关四文件，原文缺该小节）；③ §9 顶部新增**章节新鲜度声明**（WPF 时代原文指向已删文件，沿用 CODE-STRUCTURE 的做法）；④ §15 登记新文档（WIKI-PARITY-SPEC / AUDIT-R2 / RELATIONS-AUTHORITY-MATRIX / t50-api-signature-table / t43-step2-analysis / FINAL-DELIVERY）；⑤ 新增「2026-09-17 交付轮实测复校」表；⑥ IPC 派发数更正为 49/47 | 本轮 `grep`/`find`/`ls` 实测；`docs/AUDIT-2026-R2.md` §6 清单 |
| `docs/CODE-STRUCTURE.md` | ① §2 依赖表 Application 行登记 `Ipc/`（**49 派发名 / 47 处理器**）、`Relations/Authority/`（16 权威来源）、`SpineData/`；前端行登记 **`spine-webgl@4.0.26`** 与许可随包；② §2 App 行**更正两处不实表述**（`WPF-UI` 未移除；`App/Themes/*.xaml` 未删除、519 行且无消费点）；③ §6 不变量 §6-33（`FormatVersion = "v4"`，实测 `RelationModels.cs`）与 §6-38（**权威来源优先、禁止 id 窗口猜测**）；④ §6-13 条目更正（旧验收门对象 `App/WorkbenchPages/` 已删，但 `App/Themes/` 仍在）；⑤ §10 常用命令补前端 `npm run build` 与两行式「前端 + 后端」一键发布、测试基线 864、发布目录残留口径 | 本轮命令输出；本轮文件实测 |
| `docs/STATUS.md` | ① §1 一句话现状改为「WebView2 薄宿主 + Vue3/TS 前端」并加**维基页现状口径警告**（页面为空）；② §2 验证基线改 **864** 并写明四条命令退出码 0；③ §3-9 设计色铁律改为「前端 `tokens.css` 唯一 + 宿主残留说明」；④ §5 新增 **T-J1~T-J8**（维基生成链路、`wiki.applyEdit` 回执、`bank.preview`、前端无单测、WPF 时代章节重写、发布残留孤儿口径更正、**T-J7 宿主残留**、**T-J8 sync-over-async 余量**）；⑤ §6.2 已知问题表新增 3 行（宿主残留、sync-over-async、维基页面为空）；⑥ §7 文档地图登记 FINAL-DELIVERY | 按 `docs/AUDIT-2026-R2.md` §6 清单逐条落地 |
| `docs/ROADMAP.md` | 新增「W 波次（W0–W4）」小节：**W3 关联维基化标记 ✅（结构面）** 并明确「生成链路未接通」的澄清；W1 派发数更正 49/47；W2 的 cg_40 渲染证据复核结论更正；W4 的发布残留口径更正；构建命令补 `npm run build` | 本轮实测 |
| `docs/USAGE.md` | **新增 §10「Wiki 工作台（维基化关联页）」**：是什么（14 类一一对等）、三条硬口径（只显示可推导数据 / 只改内容不改结构 / 只导出模组）、四个视图与路由、多二级页说明、`cache/wiki-pages.db` 落盘、**§10.3 已知现状**（本机页面为空的原因、`wiki.applyEdit` 是回执、不伪造数据） | 按清单；内容全部对应源码/实测 |
| `docs/WEBVIEW-FEASIBILITY.md` | 顶部**新增「🚫 结论已被取代」横幅**（交付轮复核版）：结论作废、以 `ARCH-WEBVIEW2-VUE.md` 为准、重构已执行交付、指向 FINAL-DELIVERY；原 §1–§4 技术账继续有效 | 按清单 |
| `README.md` | ① 首段改为「WebView2 薄宿主 + Vue3/TS 前端」并指向 ADR/IPC 契约；② **新增「Building from source」**：npm 安装 → build → dotnet build/test → publish 的顺序，说明前端工程**刻意不在 `.slnx`**、`PublishFrontend` 要求先构建前端（缺失即中文报错）、预期 `wwwroot/` 与 `dist/` 逐字节一致（52 文件）、`lme.py` 不含 npm、`scripts/publish.ps1` 另复制 FMOD DLL；③ 「Verification」块补 npm 构建并把基线 **611 → 864** | 按清单；本轮实测 |
| `docs/AUDIT-2026-R2.md` | ① 文件顶部加**勘误指引**；② **新增 §8 勘误表 E-01~E-09**（869→864；IPC 33→49/47；App「8 源文件」的完整文件清单；`any` 0→8；「WPF-UI 已移除」不实；N-03 在 IpcGateway 已清零但 Application 仍有 3 处；JS 堆与 npm modules **未复核**） | 本轮 `dotnet test`/`grep` 实测输出 |
| `docs/FINAL-DELIVERY.md` | 本文（新增） | — |

> **未改的文档**：`docs/ARCH-WEBVIEW2-VUE.md`、`docs/WEB-IPC-CONTRACT.md`、`docs/WIKI-PARITY-SPEC.md`、
> `docs/RELATIONS-AUTHORITY-MATRIX.md`、`docs/SPIKE-WEBVIEW2.md`、`docs/AUDIT-2026-R1.md`、`docs/LOGGING.md`、
> `docs/REALDATA-VERIFY.md`、`docs/PERFORMANCE-REFACTOR.md`、`docs/REVIEW.md`、`docs/ARCHIVE-DESIGN-NOTES.md`、
> `docs/WIKI-PAGE-LAYOUT.md`、`docs/SPINE-INTEGRATION-ANALYSIS.md`、`docs/t43-step2-analysis.md`、
> `docs/t50-api-signature-table.md`、`docs/README.md`（AUDIT-R2 §6 清单未要求，且内容未与本轮实测冲突）。

---

## 4. 与用户原话逐条对照

| # | 用户原话要点 | 落地情况 | 证据 / 缺口 |
|---|---|---|---|
| 1 | 重构为基于 **WebView2 + Vue** 的项目，前端 GUI 可大规模更改 | ✅ 已落地 | 宿主 8 `.cs` + 前端 Vite/Vue3/TS、9 工作台视图；`docs/ARCH-WEBVIEW2-VUE.md` |
| 2 | **Spine 渲染逻辑全部转移到前端** | ✅ 已落地 | 前端 `spine-runtime.ts` + `SpineRenderer.vue`（spine-webgl@4.0.26）；C# 侧仅 `SpineData/` + `SpineExportService`；native grep 0 命中 |
| 3 | 完成后**自行测试** | ✅ 已做，但**不完整** | build/test（864 全绿）/npm build/publish 四条命令 exit 0；**宿主内 IME、多 DPI、原生对话框、剪贴板等无法自动化 → 见 §7 人工冒烟清单（明确标注未自动验证）**；Spine 真实渲染证据未复核（G-06） |
| 4 | **审查项目**，分析是否有残留的不妥设计需要修正 | ✅ 已审查，2 组新发现未修 | R1/R2 两份审计 + 本轮 §8 勘误；新发现 G-08（宿主 WPF 残留）/G-09（sync-over-async 余量）；**受「不改 src/」约束只登记不修** |
| 5 | 维基**一一对等（14 类页面）** | ⚠️ **规格与数据结构完成，运行期为空** | `docs/WIKI-PARITY-SPEC.md` 14 类 + Schema；`WikiPageStore` 4 层树；**无生成链路 → 页面全空**（G-01） |
| 6 | **不应依赖 id 规则自动推断**（错配来源） | ✅ 已落地 | 权威引擎 16 来源 + 三档置信度 + `WritableSourceKind`；不变量 §6-38 明文禁止 id 数字窗口猜测；矩阵见 `docs/RELATIONS-AUTHORITY-MATRIX.md` |
| 7 | **彻底重构关联页面、允许创建多个二级页面** | ✅ 结构面落地 | `WikiSecondaryPageRules` + `WikiPageArranger`（t43 版）允许一对象多二级页；4 层树 + CASCADE；⚠️ 编排器**无生产调用点**（G-01） |
| 8 | 注重**内存与运行时速度** | ⚠️ 部分达成 / 部分未复核 | 委托口径：前端 JS 堆 2.13–2.16 MiB（旧 WPF 峰值 1,250.9 MiB）、二进制通道省 33.3% 体积——**本轮未复测**；宿主进程内存**未测量**；文档 JS 堆「~2.3 MiB」与本口径不一致（勘误 E-07） |
| 9 | **只显示本地可推导数据**（推不出来的不显示、不编造） | ✅ 设计层落地 | 编排器只产 `Path` 事实；ADT 契约含 `WritableSourceKind`；空值不填占位文案（`docs/USAGE.md` §10.1 明文） |
| 10 | 页面只是「方便查看与编辑」；**只改内容、不改结构** | ✅ 设计层落地 | `WikiEditor.vue` 字段级编辑；无自由正文编辑器；`WikiEditService` 只写字段内容 |
| 11 | **只导出模组** | ✅ 已落地 | 前端无维基/网页导出入口；导出通道只有「导出模组…」（`ExportView` + 五种格式槽位） |
| 12 | 三个工作台（音频/文本/静态）**设计不动** | ✅ 已遵守 | 本轮**未改任何 `src/` 代码**；`BankView.vue`/`TextView.vue`/`StaticView.vue` 结构未动 |

---

## 5. 已知缺口与残余风险

### 5.1 已知缺口（G-01 ~ G-13）

| ID | 缺口 | 现状（本轮实测） | 影响 | 建议处置 |
|---|---|---|---|---|
| **G-01** | **维基页面生成链路缺失（最高优先）** | 权威引擎 `Relations/Authority/`（13 文件）与两个 `WikiPageArranger` **无生产调用点**（仅被测试覆盖）；`WikiPageArranger` 注释引用的 `WikiAutoGenerationService` **在本仓库不存在**；`wiki-pages.db` 在仓库与发布产物中**均不存在**（`find` 无输出） | 本机启动后 **14 类页面全为空**——维基化对用户**不可见**；委托 3 的核心价值未交付 | 实现「一次启动/一条命令：读 `relation-index.db` + lang + static 事实 → 按 `WIKI-PARITY-SPEC` 生成页面 → 落 `wiki-pages.db`」，并保证 `WritableSourceKind != Path` 的字段留空 |
| **G-02** | `wiki.getEditPlan` / `wiki.applyEdit` 是**原样回执** | `HandleWikiApplyEdit` 直接 `IpcResponse.Success(..., new WikiApplyEditResponse(true, req.Id, "wiki.saveContent", null))`（`IpcGateway.cs:893`，不落库）；`HandleWikiGetEditPlan` 对所有编辑回填常量方法名 `"wiki.saveContent"`（`:885`） | 前端若走 `applyEdit`，用户会看到「保存成功」但**数据没变**（比报错更危险） | 要么让 `applyEdit` 真正落库（复用 `WikiEditService`），要么让前端只走 `wiki.saveContent` 并在协议注释标注 `applyEdit` 未实现 |
| **G-03** | `bank.preview`（音频试听）未端到端可用 | 仅做到「验证 Bank + FMOD 可用 + 标注需宿主原生播放」 | 音频工作台在**前端壳里无法试听**（旧 WPF 可试听）——功能回退 | W3：前端 Web Audio 解码（FMOD DLL 解码出 WAV 字节后经二进制通道喂前端）或宿主桥接原生播放 |
| **G-04** | `catalog.containerRoots` / `containerChildren` 的规模降级 | 对 >10 万条走「守卫 + 中文错误」降级 | 超大目录（真实库 1,392 表 / 1459 bundle 级别的极端容器）**取不到子节点**，用户看到中文报错 | 改成服务端分页/懒加载（前端已支持分页契约），而不是整段拒绝 |
| **G-05** | 发布目录**不清理**（增量复制） | `PublishFrontend` 用 `SkipUnchangedFiles=true` 且不清理目标目录。**本轮实测**：两次构建产物哈希名相同 → `wwwroot/` 与 `dist/` 52 = 52 完全一致，**未复现**旧报告的「17 个孤儿」；另实测 `artifacts/publish-win-x64` = **635 MB**，其中 **433 MB 是本地 `cache/*.db` 残留**（含 `-shm`/`-wal`），与文档旧值 519MB 不符 | 升级/改前端后重新发布到旧目录会残留旧 `assets/*`（白占体积、可能误加载）；发布目录体积不可控 | `PublishFrontend` 前清理 `$(PublishDir)wwwroot`；发布产物不打包 `cache/*.db`（或显式说明为本地索引） |
| **G-06** | **Spine 证据不足** | ① 真实三件套样本**只有 1 套完整**（`cg_40`；29 个目录有骨架无 atlas；未做全库文本内容嗅探）；② 交付轮复核 `cg_40` 截图：文件存在，但 **canvas 区域仅 3 种颜色（(10,10,26)/(22,33,62)/(68,68,68)），无任何纹理像素** → 截图**不能证明渲染成功**（可能原因：WebGL 无 `preserveDrawingBuffer` 导致全页截图重合成后画布丢失，或渲染确无输出；两者本轮都未区分）；③ 二进制骨架（`.skel`）**无样本、未验证** | 「Spine 渲染全前端」的**运行时正确性缺少可复核证据**；样本覆盖极窄，真实使用中大概率遇到只有骨架/只有 atlas 的目录 | 冒烟清单 M-13 复测：抓**页面内 Log 行文本**（`✓ 渲染成功 \| 动画: … \| 耗时: …ms`）或控制台日志，而不是只看画布截图；补充 `.skel` 与「骨架无 atlas」的失败路径断言 |
| **G-07** | WPF 时代文档章节未重写 | `CODE-STRUCTURE.md` §4/§5/§7/§8/§9 与 `PROJECT-INDEX.md` §5.2/§9/§11.6 仍为 WPF 时代原文（指向已删除文件，如 `MainWindow.xaml`、`WorkbenchPages/*`）。本轮只加了**新鲜度声明横幅**（两文顶部 + §9 顶部），未逐段重写 | 接手者按字面执行会找不到文件、误判架构 | 按重构后事实逐段重写（工作量中等，可作为独立任务） |
| **G-08** | **宿主 WPF 残留（委托 2 本轮新发现，未修）** | ① `Themes/Theme.xaml`(76 行) + `Themes/WorkbenchStyles.xaml`(443 行) **仍在**且被 `App.xaml` 并入 `Application.Resources`，但 `WebView2MainWindow.xaml` **未引用任何 `Wb*`/主题键** → **死资源**（启动期仍解析 519 行 XAML）；② `WPF-UI 4.3.0` **仍是 PackageReference**（`ui:FluentWindow`/`ui:TitleBar` 在用）——文档曾声称「已随页面层移除」**不实**；③ `WebView2MainWindow.xaml` 含 **6 处内联色值**（`#E8EDF2` `#1B222B` `#9FB0BF` `#5CC97A` `#11151A`），与「设计色只在 `tokens.css`」的 Web 收口门口径不一致（宿主是 XAML 域，属历史遗留） | 启动多解析 519 行资源；依赖多一个包（含其运行时体积/许可面）；色值纪律在宿主上未收口 | 删除两个主题字典 + `App.xaml` 中对应 `MergedDictionaries`；评估是否可用原生 `Window` 替掉 `ui:FluentWindow` 以去掉 `WPF-UI`；宿主内联色值收敛到 `App.xaml` 的少量资源键 |
| **G-09** | Application 内 **3 处 sync-over-async**（R2 新债 N-03 的余量） | R2 所述 `IpcGateway.cs` 8 处**已清零**（实测 0 命中，t60 async 贯通）；但仍有 `Assets/Preview/AssetPreviewProviders.cs:297`、`:354`（FMOD 解码 `.GetAwaiter().GetResult()`）与 `Build/UnityCacheExportService.cs:206`（`.Result`） | 预览/导出路径存在线程阻塞与死锁的理论风险（本机未复现） | 改为 async 贯通（与 t60 同法），或至少加注释说明为何必须同步 |
| **G-10** | 前端**无单元测试工程** | `package.json` 只有 `dev`/`build`/`copy-licenses`/`type-check`/`preview`，**无 `test` 脚本**（`npm run test` 会失败）；R2 新债 N-02 未关闭 | 前端（含 Spine 渲染与维基视图）只能靠人工冒烟回归 | 引入 vitest（组件/契约 DTO/stub 桥已有 `harness.ts` 可复用） |
| **G-11** | 宿主原生能力**无法自动化验证** | 中文 IME、PerMonitorV2 多 DPI、原生文件/保存/文件夹对话框（尤其**能否选中空文件夹**）、剪贴板、`Process.Start` 打开输出位置，均需真实窗口交互 | 这些是用户日常高频动作，回归风险只能靠人工 | 见 §7 人工冒烟清单 M-02~M-08（每项都写了预期与排查看点） |
| **G-12** | `artifacts/` 体积与仓库卫生 | 本地 `artifacts/` ≈ 2.2 GB（已 gitignore）；发布目录 635 MB（含 433 MB cache 残留） | 磁盘占用；发布包体积不可控 | 定期清理；发布脚本可选排除 `cache/*.db` |
| **G-13** | 继承自 R1、仍存在的三项（有意决策，未改） | ① `Application` 层体量大（F-05，仍 ~23.7k 行）；② `ModExportService` 死代码保留（F-06，CLI/测试仍在用）；③ `Domain` 层过薄（F-09，业务逻辑在 Application 属既有设计） | 可维护性代价，非缺陷 | 保持现状（R2 已判定为有意取舍），后续按需拆分 |

### 5.2 残余风险

| ID | 风险 | 严重度 | 现状与本轮证据 |
|---|---|---|---|
| R-01 | Spine 运行时许可（Spine Runtimes License） | medium | 每位终端用户须持 Spine Editor 许可；`wwwroot/licenses/` 两份 LICENSE 实测在包 |
| R-02 | WebView2 运行时依赖（Evergreen / Fixed Version） | medium | 离线机器需 Fixed Version；运行时检测已实现（`docs/SPIKE-WEBVIEW2.md`），本轮**未复测** |
| R-03 | Chromium 内存叠加 | low | 前端 JS 堆口径 2.13–2.16 MiB；**Chromium 进程内存本轮未测量**（未验证） |
| R-04 | IME / 多 DPI 边缘情形 | low | 需真实宿主人工冒烟（M-02/M-03） |
| R-05 | 二进制骨架（`.skel`） | low | 无样本，仅 JSON 路径验证（G-06） |
| R-06 | `bank.preview` 需宿主 FMOD 解码 | medium | 明确标注未验证（G-03） |
| R-07 | 维基页面为空导致「功能看起来不存在」 | **high（体验）** | 用户打开 Wiki 工作台看到空页，容易判断为崩溃/未完成（G-01） |
| R-08 | `wiki.applyEdit` 假成功 | **high（数据安全）** | 成功回执不落库，用户以为已保存（G-02） |

### 5.3 本轮明确「未验证」的清单（不得当作已验收事实引用）

1. 前端 JS 堆 **2.13–2.16 MiB** 与「二进制通道省 33.3%」——本轮未复测（勘误 E-07）。
2. `npm run build` 的 **98 modules** —— 本轮输出未含 module 计数（勘误 E-08）。
3. **`cg_40` 真实骨架渲染成功** —— 截图像素证据不成立（G-06）。
4. 二进制骨架（`.skel`）渲染路径 —— 无样本。
5. 宿主内 **中文 IME / PerMonitorV2 多 DPI / 原生对话框（含空文件夹选择）/ 剪贴板 / 打开输出位置** —— 无法自动化（G-11，见 §7）。
6. **Chromium/宿主进程内存峰值** —— 未测量。
7. R2 §2.3/§2.5 中标 ✅ 但本轮未重跑的项（如「0 setInterval/无 Map 缓存」等）——保持原结论，**不作为交付轮证据**（勘误 E-09）。
8. Spine 之外的真实数据闭环（导出五种格式 / 调试铺盘）——本轮**只跑了命令级与单测级验证**，**未在发布产物里人工走一遍**（见 §7 M-09~M-11）。

---

## 6. 过程风险与应对规则

### 6.1 本轮/本项目已发生的三类过程事故

| # | 事故形态 | 实例（可查） | 后果 |
|---|---|---|---|
| P-1 | **改接口不带调用点 → 全仓编译中断** | 重构期多次出现（接口/签名变更后调用点未同步），导致 `dotnet build` 整体变红，其他成员的验证全部阻塞 | 交付节奏被打断；易诱发「为了让 build 变绿而临时注释/绕过」的错误处置 |
| P-2 | **报告与实测不符** | ① R2 记 869 例、实测 **864**；② R2 记 IPC「33 方法」、实测 **49 派发名 / 47 处理器**；③ R2 记「0 any」、实测 **8 处**；④ 文档记「WPF-UI 已移除」，实测**仍在**；⑤ 文档记「`App/Themes/` 已删除」，实测**文件还在**；⑥ 前序报告写截图路径 `temp\40-test\…`（真实路径是 `temp\t40-test\…`），且**截图像素不能证明渲染成功**；⑦ 文档记发布体积 519MB、实测 635MB（含 cache 残留） | 审计结论失真；接手者按错误数字做判断（本项目已明文要求「审计结论须以审查者亲自执行的命令输出为准」） |
| P-3 | **并发成员越界修改重叠文件** | spine-engineer t24 越界改 6 个文件，与 relations/host 成员的编辑撞车（R2 §3 专项复核：本次未造成重复/缺失逻辑，属侥幸） | 逻辑重复/丢失风险极高；复核成本大 |

### 6.2 对应规则（建议写入团队约定 / 项目铁律）

1. **改接口必须同时改全部调用点**，并在同一次提交内完成；提交前**必须**跑全仓 `dotnet build LimbusModEditor.slnx --no-restore`。
2. **落盘即跑全仓 build**：任何源码/工程文件落盘后立即构建（不攒改动），避免「红区」跨越多个成员的操作窗口。
3. **变红先行声明**：一旦发现 build/test 变红，**第一动作是在团队频道声明**（哪个工程、哪条命令、原始报错），
   其他人暂停基于该树的判断；**禁止**为使 build 变绿而注释代码、跳过测试或删调用点。
4. **小步提交**：一个主题一次提交，提交信息带主题与任务号；提交前 build + test 必须全绿（本轮 §2 即为该规则执行结果）。
5. **报告只能引用自己跑出来的输出**：任何数字（测试数、文件数、体积、内存、方法数）必须附**命令与原始输出**；
   无法复现的写成「未验证」，不得转述他人口径（本轮 §8 勘误与 §5.3 清单即按此执行）。
6. **引入文件级归属（CODEOWNERS 式）**：为 `src/`、`tests/`、`docs/` 关键目录指定唯一 owner；
   跨 owner 的文件修改必须先在任务里声明，避免 P-3 类撞车（R2 §3.5 与 §7 已两次建议）。
7. **禁止用脚本批量重写中文文档**（铁律 §3-6）：本次两次中文乱码事故均源于 `pwsh` 按 ANSI 读 UTF-8 重写文件；
   pwsh 只用于 build / test / git / publish 与只读查询。
8. **交付前人工冒烟不可省**：无法自动化的宿主能力（IME/DPI/对话框/剪贴板）必须按 §7 清单走一遍并留证据。
9. **绝不使用破坏性 git 操作**（本轮遵守）：不 `checkout/restore/reset/clean`，不 `--force` 推送，不改写已发布提交。

---

## 7. 交付前人工冒烟清单（**未自动验证**）

> 使用说明：全部在**发布产物** `artifacts/publish-win-x64/`（`LimbusModEditor.App.exe`）里执行，
> 不是 `dotnet run`。每条都给了**预期**与**失败时先看哪里**。
> 日志位置：`<程序目录>/logs/current.log`（全量）、`errors.log`（Error/Fatal）、`crash-<时间>.log`（崩溃快照）；
> 排查约定见 `docs/LOGGING.md`；界面卡死先看 `UiHeartbeat` 的 Warn 行。

| # | 项目 | 步骤 | 预期 | 失败时看哪里 |
|---|---|---|---|---|
| **M-01** | 发布产物能否启动 | 双击 `artifacts/publish-win-x64/LimbusModEditor.App.exe` | 3 秒内出现窗口，标题栏「Limbus Mod Editor」，底部状态栏显示 WebView2 运行时版本；随后加载出工作台界面（非白屏） | 启动即弹错误框：`logs/errors.log` + `StartupTrace` 行；白屏：`wwwroot/index.html` 是否存在（§2.2 应已一致）；缺运行时：状态栏/错误文案会指路 WebView2 安装 |
| **M-02** | 宿主内**中文 IME** | 进入 文本工作台 → 选中任一 lang 条目 → 双击进入编辑 → 用系统中文输入法输入一段中文（含候选词选择、退格、回车确认） | 候选框位置跟随光标且**不被裁切**；确认后文本正确落进输入框，无丢字/乱序/重复 | `logs/current.log` 中 IME 相关 Warn；若候选框偏移：宿主 DPI 清单（`app.manifest` PerMonitorV2）与窗口缩放比例 |
| **M-03** | **PerMonitorV2 多 DPI** | 把窗口从主屏（100%/125%）拖到第二块不同缩放比的屏；再在系统设置里改缩放后不重启观察 | 窗口内容清晰不糊、UI 不重叠、状态栏与列表列宽正常；WebView2 内容同步缩放 | 窗口模糊 → `app.manifest` 的 dpiAwareness；内容不缩放 → `WebView2MainWindow` 的 DPI 处理；记录两块屏的缩放比 |
| **M-04** | 原生**打开文件**对话框 | 资源工作台 → 选中一张 Texture2D → 「替换图片…」 | 弹出系统文件对话框（中文标题），可选中 `.png` 后完成替换；列表状态变为「已修改」 | 对话框不弹：`NativeBridgeService` 的对话框回调 + `logs/current.log`；选中后无反应：宿主→前端回调是否回到 `asset.edit.replacePayload` |
| **M-05** | 原生**保存文件/目录**对话框 | 导出工作台 →「导出模组…」 → 选择一个输出目录并导出 | 弹出系统目录/保存对话框；确认后开始导出并最终出现报告 | 同 M-04；导出无产物 → `logs/current.log` 的导出计划/槽位行 |
| **M-06** | 文件夹选择器能否选中**空文件夹** | 新建一个**完全空的**目录（例如 `D:\lme-smoke-empty`），在「导出模组…」/「打开项目」里尝试选中它 | 能选中空文件夹并返回路径（不应因为「里面没有文件」而被拒绝） | 若被拒绝：记录对话框类型（现代 IFileOpenDialog vs 旧 SHBrowseForFolder）与返回值；`logs/current.log` 的原生回调返回值。**此项是历史疑点，必须实测留证** |
| **M-07** | **剪贴板** | ① 资源列表右键「复制路径/名称」；② 文本工作台复制条目键名；③ 粘贴到外部编辑器（如记事本） | 剪贴板内容与界面显示一致（中文不乱码、路径分隔符为 `\`） | 剪贴板为空/乱码：`NativeBridgeService` 剪贴板实现（WPF Clipboard 需 STA 线程）；`logs/current.log` |
| **M-08** | **打开输出位置** | 导出完成后点报告里的「打开输出位置」（或资源右键同类入口） | 资源管理器打开到正确目录（且选中相关文件） | 无反应：`Process.Start` 回调（`NativeBridgeService`）；目录不存在：导出报告里的路径 |
| **M-09** | **四工作台编辑核心闭环** | 资源：替换一张纹理 → 音频：改一个 FSB/bank（能试听则试听，见 G-03）→ 文本：改一条 lang 文本 → 静态：改一条静态表记录 | 四处都能改、都能看到「已修改」标记，且**切换工作台/重启后项目里仍保留编辑**（保存项目后） | 标记不出现 → 各工作台 `HasRealEdits` 口径；重启丢失 → 项目保存/加载（`project.save`/`project.open`） |
| **M-10** | **导出五种格式** | 用 M-09 的改动执行「导出模组…」，检查输出树 | 输出 `<目标>/<项目名>_fmod|_data|_text/...` 下生成 `bank` / `rebank` / `carra` / `lunartique` / `lang(bus·patch·pathset)` / `staticmod` 中**适用的槽位**；报告写明空槽位原因 | 报告里的槽位说明；`logs/current.log` 的槽位执行行；产物结构对照 `Domain/Formats/ExportLayout.cs` |
| **M-11** | **调试铺盘 + 还原** | 关掉游戏 → 「使用当前修改启动游戏进行调试」→ 观察游戏目录被写入并生成备份 → **关闭编辑器** | 写入前有逐文件备份 + `steps.tsv`；关闭编辑器时**逐字节还原**；若期间文件被外部改过则记冲突**不覆盖** | 备份目录 `<项目>/backups/<时间戳>/`；冲突提示原文；`logs/errors.log` 的还原阶段 |
| **M-12** | **维基页** | 打开 Wiki 工作台（首页/类目/实体/搜索） | **当前预期：页面为空**（G-01）——需要确认的是**空态是友好中文提示而不是报错/白屏**；`wiki.search` 返回空结果不报错 | 报错/白屏 → `wiki-pages.db` 是否存在、`logs/current.log` 中 `wiki.*` 响应；空态文案是否中文 |
| **M-13** | **Spine 渲染**（**必须留可复核证据**） | 资源工作台选 `StorySpine_Sinclair/cg_40`（或任一完整三件套）→ Spine 预览/动画播放；**同时打开 DevTools 控制台或抓页面 Log 行文本** | 页面出现骨骼动画；**关键证据 = `✓ 渲染成功 \| 动画: … \| 耗时: …ms` 这类文本行**（或控制台无错误 + 帧在动）。**不要只用画布截图**（G-06 已证明截图会误导） | 失败：先看 `spine.locate` 的返回（`not-found` 中文原因 / 缺 atlas / 缺页贴图）；再看 `SpineRenderer.vue` 抛错文本；样本覆盖不足见 G-06 |
| **M-14** | **大列表与内存观察** | 资源工作台搜索空串翻页到底部；用任务管理器观察宿主 + WebView2 子进程内存；连续切换 9 个工作台 20 次 | 列表滚动流畅、翻页无卡顿；内存无持续单调上涨（切换不泄漏）；无「未响应」 | 卡顿/未响应：`UiHeartbeat` Warn + `logs/current.log` 尾行；内存上涨：DevTools Memory 快照（本次未建立基线，只做趋势观察） |
| **M-15** | **WebView2 运行时缺失路径**（可选） | 在**未安装** Evergreen 运行时的机器/或临时移走运行时目录后启动 | 给出**中文**提示说明如何安装（或使用 Fixed Version），不闪退、不白屏 | 错误文案是否中文、是否指路；`logs/errors.log` |

> **清单规模**：共 **15 条**（M-01 ~ M-15），全部标注**未自动验证**，需人工执行后回填结果。
> 其中 **M-02 / M-03 / M-06 / M-13 为最高优先**（历史留白与唯一证据缺失点）。

---

## 8. 交付结论（一句话）

重构的**结构面已完成且可验证**（宿主 8 `.cs`、前端 9+维基视图、Spine 全前端且 native 清零、
864 测试全绿、四条命令 exit 0、发布产物与前端产物逐文件一致、许可随包），
**但三处未闭环**：① 维基**没有页面生成链路**（14 类页面为空，G-01）；
② `wiki.applyEdit` **假成功**（G-02）；③ Spine 真实渲染**缺可复核证据**（G-06）。
此外委托 2 的审查在交付轮**新发现两组残留**（宿主 WPF 死资源/依赖/内联色值 G-08、sync-over-async 余量 G-09），
因「不改 `src/`」约束只登记未修。**剩余人工冒烟 15 条（§7）全部未自动验证**。
