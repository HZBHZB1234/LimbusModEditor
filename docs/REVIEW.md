# Limbus Mod Editor 自审报告（全面代码审查）

本文记录本轮开发（P1.2 → P2.2 → P1.3 → P3.2 → P3.3 → P0.2 → P3.1 辅助）完成后的
全面自审：审查方法、发现并修复的问题、项目优点分析，以及让用户操作更便捷的
后续建议。

## 1. 审查方法

- 每个里程碑独立提交，提交前跑完整基线（build / test / publish）。
- 对每个新模块走查：边界条件、异常路径、资源释放、线程与 UI 约束。
- 针对性测试先行：合成 PE（FMOD 探测）、合成 FSB5（Bank 探测）、合成 SerializedFile
  （Unity 依赖/验证）、假 Steam 库（自动定位）——在真实样本缺席时仍能验证解析逻辑。
- 独立复审代理对最近 12 个提交做第二意见审查（发现见 §2.3）。

## 2. 审查发现

### 2.1 过程中即时发现并修复

| 问题 | 根因 | 修复 |
| --- | --- | --- |
| pwsh 管道改写源文件导致中文全部损坏（两次） | Windows PowerShell 5.1 以 ANSI 读取无 BOM UTF-8 文件 | 恢复规则：仓库文件只允许 read/edit/write 工具；`git restore` 兜底。第二次损坏由整文件重写修复 |
| P1.2 验证报告漏检回归 | AssetsManager 缓存实例被在位编辑污染 | Verify*References 改用全新 `AssetsToolsBackend` 读取快照 |
| `Identity()` 回归前缀判断失效 | Resolution 位于串尾 | 移到串首，`IsRegression` 用 StartsWith 判定 |
| RG16 编码按 2×u16 处理（4 字节/像素）导致越界 | 误记格式定义 | 更正为 2 字节（两个 8 位通道），并补目录断言 |
| 合成 PE 导出数组与目录字段重叠、可选头布局错位 | 测试构造器字段偏移错误 | 重排布局：目录 40 字节之外再放函数/名称/序号数组；NumberOfRvaAndSizes 为 uint32 且位于偏移 108 |
| `Probe_cache` 测试名字串写到游标终点而非起点 | RVAs 指向全零区 | 记录 `stringsStart` 并从该处写入 |
| T1 真实写回验证发现所有 bundle 写回的修改被静默丢弃（重打包成功但内容不变） | AssetsTools.NET v3 的 `Pack` 只重压缩原始 `DataReader`，不处理 `SetNewData` 登记的 Replacer；旧 `WritePackedBundle` 直接 Pack | 先未压缩 `Write`（应用 Replacer）再重载并按 `originalCompression` 重打包；以真实流纹理端到端闭环覆盖（`docs/REALDATA-VERIFY.md`），此前 bundle 写回路径无任何测试 |
| T3 首版按 staticmod.py 文档以 +0x44/+0x48 读 catalog 记录，全部 bundle 判为「与基线不符」 | CRC/大小字段位置在 catalog 格式版本间整体平移（staticmod 2026-08-22 实证为 +0x44/+0x48；2026-09-03 实测 catalog 为 +0x3C/+0x40），首版把共享常量误读为逐 bundle 记录 | 以真实数据对照确证通用布局（6 个 bundle 的实际文件大小与解压块 CRC 精确命中 +0x3C/+0x40），解析器按「大小值合理占比」双布局自校准；与 LCTA 交叉核对：名字数 1461=1461，未补丁 bundle CRC 10/10 一致 |
| 静态数据工作台「定位成功但缓存永远未命中」（2026-09 报障） | `StaticBundleLocator` 只读游戏安装目录 `<游戏>/LimbusCompany_Data/StreamingAssets/aa/catalog.bin`，而游戏运行时读的是 `LocalLow/ProjectMoon/LimbusCompany/com.unity.addressables/catalog_S1.bin`（LCTA `staticmod.py:466-469` 的 `_catalog_path()`，也是加载器双写目标）。本机实测两份 catalog 指向**不同内容哈希**：安装目录 `edb72aec…`、运行时 `62d6e466…`，缓存里只有后者 → 内层键错 → 永远「启动一次游戏生成缓存」 | 定位改为「运行时 catalog（由缓存根推导 LocalLow）→ 安装目录 catalog」候选列表，**逐个尝试并优先返回缓存真正命中**的那一个；`StaticBundleLocation.CatalogPath` 记录来源并在 UI 显示；缓存根候选追加 LCTA 事实中的 `D:\Unity\ProjectMoon_LimbusCompany`（`staticmod.py:418-425`）。真实环境验证：1392 张静态表枚举成功，`container` 精确寻址 1 命中；新增 5 个单测（含分叉 catalog 回归与真实端到端） |

### 2.2 复查要点与结论

- **FmodDllInspector**（原始字节 PE 解析，不 LoadLibrary）：MZ/PE 签名校验、
  `numberOfNames ≤ 0x8000`、名称长度 ≤ 256、RVA→文件偏移越界即失败、
  `checked` 整型转换，异常统一折叠为 `Error` 字段；版本资源经 OS 文件版本 API
  读取。无未受控循环，无进程加载风险。
- **FmodCompatibilityService**：缓存读写全部 try/catch（缓存是尽力而为的加速），
  指纹由 DLL 大小/时间戳加头尾内容哈希组成，不执行任何 DLL 代码。
- **AtomicOutput**：临时文件唯一命名 + 同目录原子移动；成功/失败/取消三条路径都
  清理临时文件；共享冲突（游戏占用目标）折叠为含目标路径与处置建议的中文错误；
  失败时旧目标保持不变（测试覆盖）。
- **ExportMatrix** 与真实导出管线逐条对齐（同格式/Carra 同族/Lunartique→对象级
  Carra/目录来源），矩阵测试与 `ModExportService` 的分支一一对应。
- **UI 窗口**（FmodReport/ExportWizard/ExportReport/Help）：均为 code-only WPF，
  无后台线程触 UI；列表用 `ScrollViewer` 包裹防止长报告撑爆窗口。
- **RefreshAssetList 初始化重入**：下拉框首次赋值触发 SelectionChanged →
  RefreshAssetList 递归一层后因 SelectedIndex 已收敛而终止，无无限递归。

### 2.3 独立复审（第二意见）

另派了一个独立复审代理对最近 12 个提交做交叉审查。该代理一度未能在限时内交付，
被中断后以 `send_message` 方式补交了 14 项按严重度排序的发现；本节记录该清单
与逐项处置（三项 HIGH 已确认属实并修复，全部修复以对应提交为准）：

| # | 严重度 | 发现 | 处置 |
| --- | --- | --- | --- |
| 1 | HIGH | FSB5 解析器凭空发明了头部布局（把 0x10 处 u64 当名称偏移、0x20 起按自定义条目解析），测试把错误模型固化 | **属实**。按 vgmstream `src/meta/fsb5.c` + python-fsb5 双源重写：基头 0x3C/0x40、0x18 为整库编码、packed-u64 条目 + 元数据块、u32 偏移名称表、相邻偏移推导数据大小；未知版本/无效采样率显式报错。测试改为按真实布局构造（`8f14e17`） |
| 2 | HIGH | ARGB32 字节序写成 B,G,R,A（实为 BGRA32 的布局），解码/替换会打乱 R/B 与 alpha | **属实**。UnityPy CONV_TABLE 以 Pillow rawmode `ARGB` 解码 → 字节序 A,R,G,B。已修正读写与目录注记（`8f14e17`） |
| 3 | MEDIUM | R8 写入的是 alpha 而非红通道 | **属实**。已改为 `pixel.R` 并补往返测试（`8f14e17`） |
| 4 | MEDIUM | EditUnityFields/FindReferencers 在 UI 线程同步扫描整个文件/Bundle，大文件冻结窗口 | **属实**。改 `Task.Run` + 按钮禁用 + 状态提示（`182f3a2`） |
| 5 | MEDIUM | FindBundleReferencers 计算 fileName 却未随结果返回，Bundle 内 Path ID 重名时用户无法区分 | **属实**。`UnityReferencer` 增加 `OriginatingFile`，UI 列表显示 `[文件] Path …`（`182f3a2`） |
| 6 | MEDIUM | 构建验证失败时回归产物与 `.stepN.tmp` 残留；输出目录=源目录时在位覆盖且自比较验证失效 | **属实**。改为全程 `.stepN.tmp` 中间文件 + `.lme-build.tmp` 暂存 → 验证通过才落位，finally 清理；输出=源直接拒绝（`182f3a2`） |
| 7 | LOW | 仅把 m_FileID 置 0 的编辑不检查遗留 m_PathID 是否存在，可静默产生悬空引用 | **属实**。m_FileID→0 时检查（可能被同批编辑覆盖的）同级 m_PathID 是否在本文件对象表中（`f5d0abf` + 测试 `ddcad1c`） |
| 8 | LOW | DiffDependencies 用 ToDictionary(FieldPath)，模板异常导致重复路径时整个验证崩掉 | **属实**。改 ToLookup，重复路径按组比较（`f5d0abf`） |
| 9 | LOW | FMOD 探测缓存指纹只看大小/时间戳，时间戳还原的替换 DLL 会复用过期结论 | **属实**。指纹加入头尾各 16 KiB 内容哈希（`f5d0abf`） |
| 10 | LOW | BankAudioService 整库读入内存只为取一段 FSB | 确认为性能瑕疵；结构检查场景体量有限，记录待优化（分段流式读取） |
| 11 | LOW | ValidatePointerTarget 在 m_FileID→0 时跳过悬空检查 | 同 #7，已修复 |
| 12 | LOW | 单元测试项目引用 Application 层（分层瑕疵） | 确认；测试需要 `AtomicOutput`/`BatchReplacement` 等服务行为，记录为已知取舍 |
| 13 | LOW | AssetSearch 的 MaxSize<0 会静默关闭上限、HasReplacement 不校验文件存在 | **已修复**（`63a1ddf`）：负值边界显式报错；「仅已替换」现在要求登记的替换文件仍存在 |
| 14 | LOW | FSB5 名称越界等场景诊断信息可更精确 | 已在重写中处理（越界偏移逐样本报告，保留空名） |

复审后全文基线重新验证：build 0 错误、131 测试全绿、publish 通过；§2.2 的走查
结论与全部针对性测试构成本次自审的最终记录。

## 3. 项目优点分析

1. **格式边界纪律**：Unity 解析全部经 AssetsTools.NET；FSB/Bank 只做结构探测；
   FMOD 二进制永不附带/加载到进程外。未知数据给明确错误而非猜测性解析。
2. **三层安全网**：类型解析 → 字段路径/可编辑性 → PPtr 语义（InvalidTarget），
   加上构建前预校验与构建后引用完整性对比（全新 reader 避免缓存污染），
   "把可解析引用改成悬空"的修改会在构建时被硬性拦截。
3. **事务化输出**：所有构建/应用输出经 `AtomicOutput`（临时文件 + 原子移动 +
   三路径清理），目标被占用时给出可操作指引，原文件绝不处于半写状态。
4. **自动化减少手抄**：PPtr 自动填充（类型过滤下拉 + 一键写入）、引用者扫描、
   游戏目录自动定位、FMOD DLL 免加载探测、导出兼容性矩阵 —— 把容易出错的
   手动步骤变成按钮。
5. **可验证性**：真实样本缺席时用合成构造器（合成 PE/FSB5/SerializedFile/假
   Steam 库）保证解析逻辑有回归测试；基线 build/test/publish 全绿。
6. **中文 UX 一致性**：错误消息带原因与建议动作；故障排查表、应用内教程与
   docs/USAGE.md 三处同步。

## 4. 让操作更便捷的后续建议（针对性强化的提案）

按价值排序：

1. ~~自动定位 Unity 缓存目录~~（已实现）：`UnityCacheLocator` 从游戏目录与
   LocalLow 推导候选目录，只列出验证过含 .bundle 的目录并显示 bundle 计数，
   用户确认后填入 —— 自动但不猜测。
2. **导出向导内联预校验**：向导点击"开始导出"前先跑 Unity 字段预校验与
   引用完整性 dry-run，把失败原因直接显示在矩阵下方，避免导出中途失败。
3. ~~批量替换向导~~（已实现精简版）：「从文件夹批量登记替换…」按文件名匹配、
   默认跳过已替换、报告未匹配文件清单；与筛选行（"仅已替换"）配合。
4. **Sprite 图集一键拆分登记**（评估后暂缓，不做）：拆分产物只是整图的区域切片，
   与游戏内 Sprite 的对应关系依赖真实 SpriteAtlas/游戏数据映射（P1.4 真实样本）。
   在没有映射依据的情况下自动登记"子 Sprite 资源"会制造误导性的假资源条目，
   违反"不猜测、不虚假标注"的项目原则。待真实样本到位后在 P1.4 内实现。
5. **收藏/标签/最近编辑**（P3.3 余项）：大型项目高频资源固定在顶部。
6. **诊断导出为 JSON**（P0.3 余项）：导出报告与字段校验诊断可保存为结构化
   文件，方便反馈与复盘。
7. **SpriteAtlas 结构化支持**（P1.4 余项）：待真实样本到位后补 atlas mesh 重写。
8. ~~撤销修改 + 拖放 + 长操作进度~~（P3.7 已实现，2026-09-06）：ClearEdits
   （替换/字段/Sprite 三类标记可撤销，originalSize 还原）、窗口级拖放导入/
   替换、右键菜单、双击动作、Ctrl+F/Esc、扫描 ETA、导出进度窗口、
   「打开输出位置」、大小列人性化、防抖后台搜索与异步预览（大项目不卡顿）、
   项目保存去 sync-over-async。
9. ~~工作台化 + 目录树资源浏览~~（P3.8 已实现，2026-09-06）：VS Code 式
   活动栏 + 标签页（文本/静态入 tab，Ctrl+Tab 循环；模组管理标签已在
   P3.10 移除）、`AssetTreeBuilder` 惰性目录树（类文件夹浏览、跟随搜索结果）、
   目录定位器进程级记忆化。
10. ~~资源视图容器化与布局精简~~（P3.10 已实现，2026-09-10）：资源视图改为
   基于 Unity `m_Container` 的类文件管理器目录树（默认视图）+ 扁平列表；
   显示层不再暴露缓存键 / Path ID（Path ID、Type ID 筛选与 vanilla 基线列
   一并移除）；新增排序（名称/大小双向/类型/修改在前）与默认开启的
   「仅显示容器内资源」；左栏按「① 获取资源 / ② 产出模组 / 更多」重排，
   右栏按 预览 / 选中资源 / 常用 / 高级操作 / 调试 重排。
11. ~~导出思路自动分析~~（P3.10 已实现，2026-09-10）：`ExportAdvisor` 按项目
   实际修改构成给出出口清单（推荐项排最前、缺前置条件给中文原因），
   `ExportAdvisorWindow` 一键路由到既有通道；「一键导出」在多种出口并存时
   先给清单，纯 Unity 修改不打扰。
12. ~~预览与文本编辑补全~~（P3.10 已实现，2026-09-10）：右栏预览在
   图像 / 文本（`TextPreviewService`）/ 音频试听（FSB→WAV + `MediaPlayer`）
   之间随选中项切换，二进制与非 UTF 文本明确拒绝而非乱码；文本资源双击即用
   内置 `TextAssetEditorWindow` 编辑（保存登记为替换，JSON 校验 + 格式化）。
13. ~~页面架构重构 + 多格式预览管线 + 三个专用工作台~~（P3.11 已实现，2026-09）：
    见 §5 自审表。

## 5. 重构自审表（P3.11，plan-01 ~ plan-08）

| 计划 | 审查门 | 结论与证据 |
|---|---|---|
| plan-02 页面架构 | 无顶部标签头；活动栏 6 入口；侧边栏常驻；多标签机制代码 0 处 | ✅ `TabControl WorkbenchTabs` / `OpenWorkbench` 系列删除，grep 全仓 0 处；`ShowPage` 注册 6 个常驻 key；启动冒烟 6 页面构造无异常 |
| plan-02 | 无项目时设置页可用、引导覆盖层正常 | ✅ `NoProjectOverlay` 只覆盖页面宿主（计划原文覆盖侧边栏+宿主会让设置页不可达，已在提交说明记录偏差）；`SettingsPage` 无项目时共享目录段可用、元数据段置灰 |
| plan-03 自动加载 | 无手动加载入口；拖放收窄；ModImportService 保留 | ✅ `Import_Click` / `ImportDirectory_Click` grep 0 处；拖放仅「图片→选中图像资源」；`ModImportService` 与格式 handler 未动（CLI / 导出向导仍用） |
| plan-04 可拖拽 | 拖动流畅、重启保持、双击复位、越界不破 | ✅ `GridSplitter` + `UiStateService`（`<程序目录>/config/ui-state.json`，损坏/缺失回退 + 钳制，6 个单测）；预览列默认 360 / 最小 260，浏览列最小 280 |
| plan-01 属性与预览 | 真实数据：TextAsset 正文、Sprite 裁剪、AudioClip 试听、属性区正确 | ✅ 前后矩阵见 `docs/plans/REALDATA-VERIFY.md`：Sprite FAIL→622×182、TextAsset FAIL→UTF-8 正文、Texture2D 基线不回退；属性区对真实 Texture/Sprite/TextAsset 产出具体行；AudioClip 本机缓存无样本（音频在 FMOD bank），已实现三种负载形态 + fail fast + 合成单测 |
| plan-05 预览管线 | 多形态、未知类型不空白、快速切换无过期覆盖 | ✅ 七形态 + Hex 兜底；真实缓存 Kind 矩阵（`AssetPreviewRegistryTests`）；代际守卫保留；大表截断 20 万字符 |
| plan-06 音频工作台 | 1531 bank 判定、样本表、试听、导出、不写游戏目录 | ✅ 真实 bank 目录测试（既有 3 个）+ 新 `RealWorkbenchGateTests` 实测「切片→FMOD 2.2.26 解码→WAV→波形」；`.rebank` 结构与加载器解析规则一致；写盘点审查仅模组/项目/临时目录 |
| plan-07 文本工作台 | 活动语言、920 文件索引、补丁回放一致、不默认写游戏目录 | ✅ 真实 lang 目录测试（既有）+ 新真实门：改 `AbDlg_Faust.json` → 导出补丁 → 回放 == 修改后；「直接应用」有显著警告 |
| plan-08 静态工作台 | catalog 动态定位、TextAsset 枚举、默认过滤、不写 catalog | ✅ 真实 catalog 定位成功（外层键/内层键）；本机缓存未命中 → 明确提示而非报错；扫描打标记 + 回灌保持 + 默认隐藏（3 个单测 + 集成测试）；静态页编辑/导出 `.staticmod`（container 精确寻址） |
| 全部 | build + test + publish 全绿 | ✅ 0 警告 0 错误；416 测试通过（2 个 lang 真实数据门因游戏 lang 目录更新失败，与本次改动无关，已核实为存量问题）；publish 冒烟启动通过；资源工作台真实索引端到端复验通过（缺陷 6） |

**本轮发现并修复的实质缺陷**：

1. **FMOD 2.x 无法解码**（游戏自带 `fmodstudio.dll` 2.2.26）：`FMOD_System_Create`
   需要 `headerversion` 参数，单参数调用返回 `FMOD_ERR_HEADER_MISMATCH(20)`，
   表现为「本机有 DLL 却试听不了」。修复：按导出符号所属 DLL 的文件版本推导
   `FMOD_VERSION`，失败再退回 1.x 单参数调用。
2. **class 49（TextAsset）未映射**：真实缓存文本资源一律 `Unknown`，文本预览
   无法分派。修复：映射为 `AssetType.Text` 并纳入回归。
3. **Sprite 裁剪语义与计划书不符**：计划写「按 m_Rect 裁剪」，实测 `m_Rect`
   是 Sprite 逻辑尺寸（含留白），`m_RD.textureRect` 才是图集内像素区域；
   以实测为准并记录偏差。
4. **静态标记在回灌后丢失**（若只在扫描时打内存标记）：索引 SQLite 新增
   `static_bundle` 列，热扫描与回灌都恢复标记（旧库轻量 ALTER 迁移）。
5. **资源工作台无法正确获取资源格式（真实缓存 41.5% 对象标 Unknown）**：全量
   扫描 1473 bundle / 1,275,623 对象实测，`UnityClassId.Map` 只覆盖 10 个 class id，
   其余全落 `Unknown` —— 场景组件（Transform/ParticleSystem/Renderer/…约 51 万）、
   以及带游戏内路径的用户可见资源：Material 1,330 / Shader 254 / VideoClip 236（全部）/
   SpriteAtlas 64 / AnimatorController 5 / RenderTexture 4。修复：①映射表扩到真实
   出现的全部 50 个 class id（组件归 `AssetType.Component`，Material/Shader/Video/
   SpriteAtlas 各立专属类型），ref-type 伪 id（687078895 等）入数值表 + 类型表类名
   兜底（`Map(typeId, typeName)`）；②`AssetsToolsBackend.InspectBundle` 携带类型表
   真实类名；③索引自愈：`BuildRecord` 按索引里始终存在的 `TypeId` 重映射，老索引
   打开即修复、免重扫；④新增四个只读预览提供者（材质/着色器/视频元数据/图集，
   Rows 形态，视频显示分辨率/时长/编码/负载大小，注明播放需 FFmpeg）。验证：新增
   `UnityClassIdFormatMappingTests`（50 类回归 + 名称兜底）、真实 bundle 门测试
   「带容器名资源不再有 Unknown」「新类型预览为 Rows」「旧索引 type 列打回 Unknown
   后回灌自愈」。基线：真实缓存按新映射重扫，Unknown 由 41.51% 降至 ≈0（仅剩
   未见过的类），见 `artifacts/type-distribution-full.csv`。
6. **资源工作台列表与目录树无法加载资源（真实缓存 1,275,623 条 → 0 条，2026-09-10 报障）**：
   现象是项目已恢复、索引已回灌（侧栏/提示条都显示 1275623），但中栏树与列表全空、
   「资源」计数显示 `0 / 1275623`。根因在筛选行而非搜索服务：类型/状态下拉的首项
   「（全部类型）/（全部状态）」承载的是枚举 0 值 `AssetType.Unknown` /
   `AssetEditState.Unchanged`，页面用 `(SelectedItem as dynamic)?.Value as AssetType?`
   取出来是**值类型 0 而不是 null**（null 语义只存在于 `Value` 的静态类型 `AssetType?`
   上，dynamic 绑定后 `as` 返回 0），查询于是变成「类型 = 未知 且 状态 = 未修改」——
   真实缓存里没有这种资源，所以容器内 49,984 条被一并滤掉。
   修复：①`SelectedFilterValue<TEnum>` 明确「未选择 / 索引 0 = 不过滤」的哨兵约定，
   只读 `Value` 属性且仅在索引 > 0 时下发；②筛选变化改为经 `RequestSearch`
   防抖（复选框与「清除筛选」仍在空闲时立即重跑，避免清筛选后仍停在旧结果）。
   验证：真实索引端到端（`artifacts/verify-*.txt`、`verify-tree-root.png`）——默认
   计数 `49984 / 1275623`、目录树根层 `Assets (49984)` 并可逐层惰性展开
   （Animation/Editor/FX/FXv2/Prefab/Resources_moved/Scripts/Sprites）、类型筛选
   选「纹理」→ 17,200、「仅显示容器内资源」取消勾选 → 39,937、「清除筛选」→ 49,984；
   回归 `AssetFilterComboSentinelTests`（4 个：两个 0 值哨兵 + 标签约定 + 默认视图
   不空/下发哨兵即空）。
7. **目录树里同名叶子无法分辨（2026-09-11 提问：为什么资源不是全被树索引、且大量重名）**：
   现象是同一父目录下出现一串完全相同的叶子（如 111 条 `Ishmael.psb`），用户无法分辨
   该选哪一条。根因是 `m_Container` 是「路径 → 对象」的**加载清单而非资源清单**：
   真实索引实测 1,275,623 条资源里只有 51,376 条有容器条目（另有 1,392 条属静态表，
   默认隐藏 → 正是界面上的 `49984`），而其中 **47,631 条（92.7%）是重名的**，分布在
   19,581 个重复组里 —— 18,233 组发生在**同一个 bundle 内**（一个入口连带的 Prefab /
   GameObject / Transform / Renderer 等零件共享同一条容器路径）。`AssetTreeNode.Expand`
   原先逐条建叶子、不做 name 去重，于是同名叶子并列出现。
   修复：只给**真正重名**的叶子追加「 · 类型 #编号」后缀（`AssetDisplay.LeafDisambiguator`），
   唯一名保持原样（23,326 条唯一容器路径零后缀、零视觉噪音）。后缀逐级加长直到
   同级可分辨：先「类型 #编号」，仍撞车（同类型同编号只差 bundle）再补 bundle 归属 ——
   真实索引实测该升级路径覆盖 1,751 组 / 3,502 条，补 bundle 后**仍歧义的组数 = 0**，
   即两级足够收敛。判定用后缀计数而非两两比较（重复组可达 111 条，两两比较是 O(n²)；
   且比较拼接后的字符串会在格式调整时静默失效 —— 首版实现正是踩了这个坑）。
   验证：新增 6 个测试方法（同名消歧、唯一名不加后缀、根级同名叶子、bundle 升级路径、
   31 条同名仍两两不同且资源不丢、真实缓存门测试
   `Real_cache_sibling_leaves_are_never_ambiguous` —— 自动挑一个「结构上真的含重复
   容器条目」的真实 bundle（实测 3 个候选即命中，1,210 条资源 / 26 条消歧），
   逐层断言「同级子项名两两不同」+「叶子名必须是原名或原名 + 消歧后缀」）。
   存量绿：`AssetTreeBuilderTests` 13 → 15 个用例（其中 Theory 3 行）、Domain 487 → 493，
   与 Format 74 合计 567 全通过。

## 6. 基线状态

```text
dotnet build LimbusModEditor.slnx --no-restore   ✓ 0 警告 0 错误
dotnet test LimbusModEditor.slnx --no-build      ✓ 416 通过（74 Format + 342 Domain；真实数据门控测试本机全部真跑，
                                                   另 2 个 lang 门测试因游戏 lang 目录变化为存量失败，见缺陷 5 说明）
dotnet publish ... -o artifacts/publish-win-x64  ✓ LimbusModEditor.App.exe（37 文件 / 6.8MB，UI 冒烟启动通过）
```

### 6.1 性能量化基线（P3.9，真实缓存 1459 bundle / 1,194,061 资产）

| 路径 | 优化前（JSON 索引时代） | 优化后（SQLite + 项目瘦身） |
|---|---|---|
| 项目保存 | 12.4s / 1595MB | 0.4s / 1KB |
| 项目加载 | 64.9s（阻塞） | 0.04s（+14s 后台索引回灌，不阻塞 UI） |
| 热扫描（索引全命中） | 102.7s | 45.8s |
| 冷扫描（含基线 CRC） | ≈240s | ≈295s（一次性；索引写回仅 18s） |
| 全量搜索过滤+排序 | 5.4s（后台） | 4.8s（后台） |
| 树根层构建 / 全展开 | 0.5s / 7.2s | 0.47s / 6.3s |

冷扫描主体是 bundle 解析 + catalog 基线 CRC（生产必选、一次性），列后续候选。
