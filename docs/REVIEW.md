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
| 13 | LOW | AssetSearch 的 MaxSize<0 会静默关闭上限、HasReplacement 不校验文件存在 | 确认为交互瑕疵；筛选 UI 已限定输入范围，后续在筛选面板收紧 |
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

## 5. 基线状态

```text
dotnet build LimbusModEditor.slnx --no-restore   ✓ 0 错误
dotnet test LimbusModEditor.slnx --no-restore    ✓ 131 通过（45 Format + 86 Domain）
dotnet publish ... -o artifacts/publish-win-x64  ✓ LimbusModEditor.App.exe
```
