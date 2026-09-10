# Limbus Mod Editor 后续开发路线图

本文档记录当前工作区之后的开发任务。目标是把现有的 C#/.NET 8 + WPF
骨架继续推进为面向 Limbus Company 的高级 Assets Studio + FMOD Studio
工作流。任务可以按阶段拆给不同 agent，但每个任务都必须保留现有格式边界、
安全检查和回归测试。

## 当前基线

已具备：

- Windows x64、.NET 8、WPF 分层项目结构。
- `.lmeproj` 创建、打开、保存、旧 Schema 迁移和未来版本拒绝。
- `.carra`、`.carra2`、`.rebank`、`.bank`、Lunartique ZIP、普通 ZIP、目录导入。
- Carra XZ/LZMA2 读写、Rebank 创建/差分、Bank RIFF/FSB 索引与重组。
- AssetsTools.NET Unity Bundle/SerializedFile 解析和写回。
- Unity Texture2D RGB24、RGBA32/BGRA32、DXT1、DXT5 PNG 预览/替换。
- Unity Bundle 与独立 `.assets` 的对象索引、对象替换、Sprite 元数据编辑。
- Unity 对象基础字段树读取，以及布尔/整数/浮点/字符串字段修改记录。
- lang 文本模组通道：RFC6902 差分补丁的生成/应用（`TextDiffService`）与
  真实加载器兼容的 `patchs` 文档读写（`LangTextPatchService`），主窗口
  「文本模组（lang 补丁）…」入口。
- .staticmod 静态数据模组通道：staticmod/v1 包读取/写出/补丁应用预览/
  从 JSON 差分生成（`StaticModService`），主窗口「静态数据模组…」入口。
- 官方 catalog（catalog.bin/catalog_S1.bin）只读解析与 vanilla 基线判定：
  导入 bundle 时自动判定相对 vanilla 是否被修改/新增，资源列表展示。
- 图像预览、图集拆分/恢复、文本/JSON 编辑、资源替换记录。
- 用户提供合法 FMOD/FSBank DLL 后的 FSB↔WAV 工作流。
- Debug overlay 备份、应用、失败回滚、冲突保护和游戏启动。
- 领域测试与格式测试；Windows x64 发布流程。

当前验证基线应保持：

```text
dotnet build LimbusModEditor.slnx --no-restore
dotnet test LimbusModEditor.slnx --no-restore
dotnet publish src/LimbusModEditor.App/LimbusModEditor.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish-win-x64 --no-restore
```

## P0：真实样本和稳定性（优先级最高）

### P0.1 Unity/Lunartique 真实样本回归

输入：至少一组真实游戏版本的 Bundle、SerializedFile、Lunartique
Installation/Uninstallation 和 Carra/Carra2 样本。

**状态（读取层已达成，2026-08 经 LCTA 文档化路径在本机发现真实样本）**：
按 LCTA 记录的真实路径（`resource_updater/core.py:78-94`：缓存根
`LocalLow/Unity/ProjectMoon_LimbusCompany`，junction 透传；模组目录
`%APPDATA%\LimbusCompanyMods`）在本机发现 **1459 个真实 bundle** 与一个真实
Carra2 模组（3898 对象），并建立了随测试套件运行的真实样本回归
（`RealSampleTests`，无样本环境自动跳过）：

1. ✅ 真实 bundle 扫描：`ScanBundle` 在 6 个样本上成功（对象数 2–497，
   识别 GameObject/Texture/Sprite/Mesh/Animation/MonoBehaviour）。
2. ✅ 真实 Texture2D：像素存于 `m_StreamData`（`archive:/CAB-xxx.resS`
   容器内资源流）——`ReadTexture` 已支持 .resS 解析（6 个真实纹理像素读取
   成功）；`ReplaceTextureFromPng` 写回内联像素后清空 `m_StreamData`。
   **T1（2026-09-06）端到端写回闭环已达成**：真实流纹理 PNG 替换 → 重打包
   引用校验 → 真实 Carra2 结构导出/重导入 → 已安装进真实模组目录；过程中
   发现并修复了「`Pack` 路径丢弃 Replacer」的严重写回缺陷
   （详见 `docs/REALDATA-VERIFY.md`）。
3. ✅ 真实 Carra2：条目键 `<缓存外层键>/<bundleHash>/<pathId>.<类型表索引>`
   实测（typeIdx 0..82，与全局类 ID 不同语义），逐条目 XZ，含 `carra.json`
   标记文件；往返保持逐字节一致。导入层已把 typeIdx 按 Binary 呈现
   （不再误映射为全局类 ID）。
4. ✅ 真实 bundle 的 SerializedFile 全部内嵌类型树（勘察 12/12，
   `TypeTreeEnabled=true`），Unity 版本字符串被抹为 `0.0.0`（bundle 头为
   `5.x.x`）——对 AssetsTools.NET 的内嵌类型树解析无影响。
5. ✅ 写回链路已在真实数据上闭环（T1，2026-09-06）：真实流纹理 PNG 替换 →
   内联像素 + 清 `m_StreamData` → 重打包（引用校验通过）→ 真实 Carra2 结构
   导出（键=外层/内层/pathId.类型表索引，XZ 逐条目）→ 重新导入逐字节还原 →
   已安装进真实模组目录待用户游戏内确认。过程中发现并修复了 `Pack` 路径
   丢弃 Replacer 的严重写回缺陷（详见 `docs/REALDATA-VERIFY.md`）。
   FMOD 音频链路的游戏内验证依旧待真实需求驱动。

原验收项保留作为后续写回验证清单：

1. 导入后对象数量、Path ID、Type ID 与 AssetsTools.NET/UnityPy 交叉核对。
2. 对 Texture2D、Sprite、MonoBehaviour、ScriptableObject 各选一个对象，完成预览、修改、写回。
3. 用游戏或独立验证器重新加载写回文件，确认 Bundle/SerializedFile 未损坏。
4. 安装/卸载调试 overlay 后，原文件哈希可恢复，重复应用不会累积临时文件。
5. 记录 Unity 版本、压缩方式、失败对象和兼容性诊断，不把未知对象静默丢弃。

真实环境事实（供后续任务引用，来源 LCTA + 本机实测）：

- 游戏 Unity 版本为 **6000.3.12f1**（不是 2021.3）；官方 bundle 的
  SerializedFile 版本被抹为 `0.0.0`。
- Carra2 的 `.typeIdx` 是目标 SerializedFile 的**类型表索引**
  （LimbusModLoader patch.py:287-301），不是全局 Unity 类 ID；第一段是
  Unity 缓存外层键（32 位 hex，跨版本稳定，与玩家账号无关）。
- 真实 Rebank 加载器把 wav 数为 0 的 rebank 判为错误并回滚安装
  （launcher/bankmod.py:189-198）——向导已撤下空白 Rebank 模板。
- 真实 .bank 实测（1531 个）：事件 bank（`<id>.bank`）的 SNDH 合法地为
  size 0（无 FSB 负载）；音频 bank（`<id>.assets.bank`）的 SNDH 表
  (offset,size) 直指 FSB5 payload；FSB codec 实测为 16（Vorbis）——
  `BankParser`/`Fsb5Parser` 已全部经真实数据回归（`RealBankTests`）。
- 真实加载器（LCTA launcher）按 `<缓存根>/<外层键>/<内层键>/__data`
  匹配 Carra 对象，游戏更新更换外层键后旧模组被静默跳过——导出时已
  增加「缓存对齐」诊断（配置缓存目录后逐外层键核对）。
- 模组目录约定：`%APPDATA%\LimbusCompanyMods`，条目为平铺文件或每模组
  一目录，`_disable` 后缀切换禁用——已实现自动获取；曾提供「管理已安装
  模组」窗口，P3.10 按用户要求移除（该窗口不属于「做模组」主流程）。

### P0.2 构建事务和文件锁

- 为 Bundle、SerializedFile、Carra、Bank、Rebank 的构建统一使用事务目录。
- 处理目标文件被游戏进程占用、只读、同名临时文件和进程异常退出。
- 所有临时文件在成功、失败、取消三种路径都能清理或留下可恢复日志。
- 增加并发构建/取消构建测试。

状态（部分完成）：新增 `AtomicOutput` 事务写出助手（唯一临时文件 + 原子移动 +
三种路径清理 + 过期临时清扫 + 目标被占用时的可操作中文错误），已接入
ModExportService 两条导出路径与 DebugApplyService 的复制步骤；导出/应用失败时
原目标保持不变。Bundle/SerializedFile 构建内部写回与并发取消测试仍待后续。

### P0.3 编码和诊断统一化

- 清理现有源文件中的乱码中文字符串，统一 UTF-8 和资源文本编码策略。
- 引入结构化诊断：代码、严重级别、文件、Path ID、建议操作。
- WPF、CLI、日志使用同一诊断模型；导出报告可保存为 JSON/文本。

状态（部分完成）：导出报告已可保存为结构化 JSON（逐资源状态 + 诊断，
经 AtomicOutput 事务写出，P0.3 最后一项）；全库乱码巡检通过（源码/文档/测试
均为 UTF-8 无 BOM 的规范中文）。`FormatDiagnostic`（代码/级别/消息/路径）已存在，
WPF/CLI 共用同一诊断模型的工作仍待后续。

## P1：Unity 资源编辑能力

### P1.1 通用字段编辑器完善

- 字段树支持数组索引、嵌套结构、PPtr 引用、枚举和 byte array 信息。
- 编辑前按字段实际类型校验；显示原值、修改值、差异和恢复按钮。
- 支持仅应用选中字段，避免保存窗口中未修改的值。
- 对 MonoBehaviour/ScriptableObject 显示脚本 GUID、类名、TypeTree 缺失原因。
- 将字段编辑记录从单一 JSON 字典升级为带类型、原值哈希、时间和作者的版本化模型。

状态（本轮）：核心能力已落地并有真实 SerializedFile 样本的回归测试
（`UnityFieldTreeTests`：字段树元数据、脚本信息、校验、应用回写全链路）；
编辑记录为 SchemaVersion 2 版本化模型，旧 JSON 字典自动迁移。

### P1.2 PPtr 引用和依赖检查

- 解析 `m_FileID`/`m_PathID`，显示目标对象和跨 SerializedFile 依赖。
- 修改或删除对象前检查引用者，并提供阻止、保留空引用、重定向三种策略。
- Bundle 重打包后验证引用仍可解析。

状态（本轮）：已落地并有真实样本回归测试（`UnityDependencyTests`）。
- 依赖解析：`ReadObjectDependencies`/`ReadBundleObjectDependencies` 把每个指针解析为
  空引用/同文件/外部文件/悬空四种状态，附目标类型与外部路径、GUID。
- 引用者扫描：`FindReferencers`（同文件）与 `FindBundleReferencers`（bundle 内跨文件，
  支持 `archive:/...` 名称匹配）；UI「查找谁引用了此对象」直接展示。
- 策略落地：保留空引用=允许 m_PathID→0；重定向=指针语义校验（新 InvalidTarget 状态，
  同文件目标必须存在、m_FileID 不得越界，支持同批编辑兄弟字段联动）；阻止=构建前
  预校验 + 重写后 `VerifySerializedReferences`/`VerifyBundleReferences` 引用完整性
  diff，任何“可解析→悬空”回退中止构建。验证读取走独立后端，避免缓存污染。

### P1.3 Texture2D 编解码扩展

- ETC1/ETC2：优先寻找可审计、可再分发的 Windows 库；没有可靠库时只提供只读预览或明确提示。
- ASTC：同上，禁止伪造编码结果。
- RGB565、RGBA4444、ARGB32 等 Unity 格式按真实字节序实现并加入样本测试。
- 为所有格式标记有损/无损、平台限制和 mipmap 行为。
- 支持 mipmap 层选择、保留/重建 mipmap、纹理尺寸和可读性检查。

状态（部分完成）：全部无损/半压缩格式已实现并有测试（`ExtendedTextureFormatTests`，
枚举值经权威工具链核对：ARGB32 为 A,R,G,B 字节序（UnityPy 以 Pillow rawmode
`ARGB` 解码；BGRA32 才是 B,G,R,A）、RGBA4444=13、RG16 为 2 字节双 8 位通道）；
`TextureFormatCatalog` 标注有损/无损与字节序说明并在预览行展示
（`ReadTextureSummary`）；`UnityTextureMipmaps` 提供逐层布局、mip 数推断与
`Slice`（含 DXT 块对齐）。DXT 编解码维持托管实现（有损已标注）。ETC/ASTC 需要可靠
可再分发库，当前不提供预览也不声称支持（符合"不伪造"约束）；mipmap 重建式替换与
可读性检查待后续。独立复审曾发现 ARGB32 字节序与 R8 写通道两处错误，已修复并
以修正后的测试固化。

### P1.4 Sprite、SpriteAtlas 和网格

- 读取 SpriteAtlas 对象、页纹理、SpriteRect、packing tag、mesh 顶点和索引。
- 支持网格/边界编辑与引用重建；未支持的类型必须阻止写回而不是降级。
- 提供 atlas 可视化：原图、SpriteRect、pivot、border、mesh overlay。
- 添加 SpriteAtlas 真实样本端到端测试。

状态（读取侧推进，2026-08）：真实 bundle 样本已可扫描（真实 Sprite/Texture/
Mesh/Animation 对象均被识别），Texture2D 现已支持真实 .resS 流数据的读取与
替换清流；Sprite 元数据编辑此前已落地。SpriteAtlas 页纹理与 atlas mesh 重写
仍需真实 SpriteAtlas 样本支撑（不猜测）。

### P1.5 Mesh、Animation、Font 只读和基础替换

- 先实现安全只读摘要和导出（顶点数、材质、骨骼、曲线、字体信息）。
- 再按真实样本确定可写字段；每种资源单独设计版本化替换器。

状态（第一步完成 + 真实样本验证）：只读摘要已落地（`UnityObjectSummaryBuilder` +
`UnityAssetService.ReadObjectSummary`/`ReadBundleObjectSummary`，合成样本测试
`UnityObjectSummaryTests`）：Mesh 报告子网格数/顶点数/顶点与索引数据大小，
AnimationClip 报告采样率与动画类型（Legacy/Generic/Humanoid 为 Unity 公开常量），
Font 报告字号/字符表条目数/内嵌数据大小/默认材质依赖解析；UI 入口
「查看对象摘要」带复制与 JSON 导出（AtomicOutput）。数值一律来自文件自身类型树，
缺失字段明确标注「未包含」而不是补默认值；真实样本回归确认真实 bundle 中的
Mesh/AnimationClip 摘要可读（`RealSampleTests`）。MeshRenderer 材质引用、
骨骼/曲线细节、以及各类型可写字段需真实样本（P1.5 第二步）后再扩展。
Mesh(43)/AnimationClip(74) 类 ID 映射已并入 `UnityClassId`。

## P2：音频和 Bank/FMOD 工作流

### P2.1 Bank 结构化编辑

- 完善 RIFF/FEV/SNDH 索引显示：事件、总线、FSB、音频条目、偏移和大小。
- 支持 FSB 子声音列表、采样率、声道、长度和编码诊断。
- 任何加密/未知 FSB 使用清晰错误，不尝试猜测或破坏性重写。

状态（部分完成）：SNDH 偏移/大小索引与 FSB 提取/重组此前已落地并有测试；本轮新增
FSB5 只读结构探测（`Fsb5Parser`：按 vgmstream/python-fsb5 双源核对的布局——基头
0x3C/0x40、整库编码字段@0x18、packed-u64 样本条目（采样率索引/声道映射/数据偏移/
样本数）+ 元数据块（CHANNELS/FREQUENCY/LOOP 等）、u32 偏移名称表、相邻偏移推导
数据大小、长度一致性诊断）与「查看 FSB 结构」窗口（`BankInspectorWindow`）。
加密/未知负载给出明确原因，不做猜测或改写；独立复审曾发现初版解析器布局错误，
已按权威布局重写并以合成样本测试固化。WAV→FSB 已在导出管线实现
（`ModExportService.ApplyReplacementsAsync` 检测 RIFF/WAVE 后自动经用户配置的
FSBANK 编码器转 FSB5 再写入 Bank，未配置编码器时给出明确错误）；事件/总线级
FEV 索引展示仍待真实 FEV 样本（不猜测）。**真实数据验证（2026-08）**：本机
1531 个真实 .bank 全部经 `BankParser` 解析（事件 bank 空 SNDH 已支持），6 个
真实 FSB（codec 16=Vorbis）89 个样本经 `Fsb5Parser` 解析成功且未修改重建逐
字节无损——0x3C/0x40 基头布局首获真实数据验证（`RealBankTests`）。

### P2.2 FMOD DLL 管理

- 增加 DLL 版本/位数/导出符号探测报告和兼容性缓存。
- 启动时不加载未知 DLL；用户明确选择目录后才加载。
- 提供 WAV→FSB 选项：格式、采样率、声道、循环元数据，并显示有损编码警告。
- 不分发 FMOD/FSBank 专有 DLL，不逆向或伪造编码器。

状态（部分完成）：`FmodDllInspector` 从原始字节解析 PE 头与导出表（不 LoadLibrary、
不执行），报告位数/版本资源/导出符号快照与缺失的解码、FSBank 编码符号；
`FmodCompatibilityService` 以 DLL 大小+时间戳指纹缓存探测结果（logs/fmod-probe.json）；
「检测兼容性」入口（项目设置窗口）展示就绪状态与 32 位告警。加载仍遵循"用户明确选择目录后才绑定"
的既有原则。WAV→FSB 选项面板待后续。

### P2.3 Bank 调试和回滚

- 将 Bank 构建输出接入 overlay 和游戏启动事务。
- 增加“仅替换一个 FSB”“批量替换”“恢复原 Bank”测试。

状态（部分完成）：Bank 输出经文件级编辑进入 overlay 并随调试事务备份/回滚；
本轮将全部 overlay 复制（含 Bank/Unity 产物）统一接入 `AtomicOutput` 事务写出，
锁定目标不再残留临时文件。"仅替换一个 FSB" 已支持（单资源替换），
批量替换已通过按文件名匹配实现（见 P3.3）；"恢复原 Bank"依赖备份回滚，
专项端到端测试仍待补。

## P3：导入、导出和创作体验

### P3.1 新建模组向导

- 选择目标格式、账号、Bundle、资源类型和目标游戏目录。
- 根据格式生成最小合法 Carra/Rebank/Lunartique 工程模板。
- 模板必须通过对应 handler 的 ValidateAsync，并给出输出路径预览。

状态（完成，2026-08 修订）：「新建模组向导…」已落地（`NewModTemplateService` +
`NewModWizardWindow`，测试 `NewModTemplateServiceTests`）：向导收集模组
名称/版本/作者/描述，选择模板格式后创建项目结构并生成经格式处理器校验的最小
合法模板 —— Carra/Carra2 生成空对象包；模板经 `AtomicOutput` 事务写出，
完成后项目直接在主窗口打开。**Rebank 空白模板已撤下**（真实加载器把 wav 数
为 0 的 rebank 判错并回滚，launcher/bankmod.py:189-198）；Lunartique 无空白
模板（真实模组才能确定其资源目录形态）；两者向导中置灰并说明 —— 符合"不猜测"
原则。`base_bank` 校验为纯文件名（与 bankmod.py:104-108 一致）。向导的
"辅助自动化"部分（自动定位游戏目录/Unity 缓存目录）此前已落地，缓存候选已按
真实布局修正（LocalLow/Unity/ProjectMoon_LimbusCompany，cache-v2 条目计数）。

### P3.2 导出向导和兼容性矩阵

- 在导出前显示源格式→目标格式矩阵。
- 对不支持的跨格式转换提前禁用并解释原因。
- 展示未知文件保留策略、对象数量、替换数量、压缩方式和潜在风险。
- 导出报告包含每个资源的状态：应用、跳过、失败、保留未知。

状态（部分完成）：`ExportMatrix` 静态镜像真实导出管线（同格式/Carra 同族/
Lunartique→对象级 Carra/目录来源支持，其余禁用并给出原因），`ExportWizardWindow`
导出前展示矩阵、项目概况与输出路径建议；`ModExportService` 逐资源记录
应用/跳过/保留未知状态（`ModExportStatus` 列表）并在导出后由 `ExportReportWindow`
展示。失败以异常中止并完整回传原因；压缩方式说明保留在矩阵理由文本中。

### P3.3 资源搜索和工作区

- 按路径、类型、大小、Path ID、Type ID、编辑状态、哈希和诊断过滤。
- 支持收藏、标签、最近编辑、批量选择和撤销/重做。
- 大型游戏目录使用增量索引、取消和后台线程，UI 不阻塞。

状态（部分完成）：`AssetSearchQuery` 扩展了 Unity Path/Type ID、大小区间与
"仅已替换"维度，主窗口新增筛选行（类型/状态下拉、Path/Type ID、KB 大小区间、
仅已替换开关、清除筛选），资源计数在筛选时显示 "筛选后 / 总数" 并有 5 个
新搜索测试。另新增 **从文件夹批量登记替换**：按文件名（不区分大小写）匹配资源、
默认跳过已替换、复用可逆的单个替换管线，完成后给出匹配/未匹配统计。
收藏/标签/撤销重做与大型目录增量索引仍待后续。

### P3.4 预览和比较

- 二进制对象显示十六进制、结构化字段和原始哈希。
- 图片显示原图/修改后对比、缩放、透明棋盘格和颜色通道。
- 音频显示波形、时长和循环标记（需要合法 FMOD DLL 时）。

状态（部分完成）：新增「十六进制预览」窗口（P3.4）：任意资源可查看前 4 KiB 的
经典十六进制转储（偏移/HEX/ASCII 边栏）+ 文件大小 + 原始/当前哈希，优先显示
替换后内容。结构化字段树（P1.1）与图片 PNG 预览此前已有；原图/修改后对比、
波形显示仍待后续。

### P3.5 lang 文本模组通道与 vanilla 基线（T2/T4/T3，2026-09-06 初版达成）

- **lang 文本模组（T2/T4）**：真实加载器（LCTA launcher/changes.py）的
  RFC6902 补丁通道已接入——`TextDiffService`（RFC6902 生成/应用，fail fast
  中文报错）+ `LangTextPatchService`（patchs 文档读写、活动语言读取、目录
  差分生成、应用回 lang 目录，新增/缺失文件明确诊断）。UI 入口「文本模组
  （lang 补丁）…」。真实 lang 目录的「读→改→差分→应用→回读相等」已随测试
  验证。后续：补丁内直接编辑操作的可视化 diff 视图。
- **vanilla 基线（T3）**：官方 catalog 只读解析（名字数与 LCTA 输出完全一致
  1461=1461）+ 缓存 bundle 基线判定（解压块 CRC32 口径与 LCTA
  `bundle_decompressed_crc` 交叉核对 10/10 一致）。实测记录区的 CRC/大小字段
  在格式版本间整体平移（2026-08-22 为 +0x44/+0x48，2026-09-03 为 +0x3C/
  +0x40），解析器按「大小值合理占比」双布局自校准。导入 bundle 时自动判定
  并在资源列表「vanilla 基线」列展示。后续：catalog 依赖图（bundle 间
  依赖关系）展示与失配诊断整合。
- **静态数据模组（.staticmod，2026-09-06）**：`StaticModService` 读取/写出
  staticmod/v1 zip（manifest + patches + fullFiles + 未知文件保留），
  pathset/jsonpatch 双 opType 的应用语义与真实加载器一致；支持从「官方 JSON
  vs 修改 JSON」差异生成 jsonpatch 静态模组。UI「静态数据模组…」。
  两个真实样本（环指回调 / 攻击容量修改）导入验证通过。边界：bundle 打补丁
  与 catalog 双写由加载器完成（带风险开关），编辑器只产出模组包。

### P3.6 傻瓜化改造（2026-09-06，完成）

目标：专一化、傻瓜化 —— 进软件即引导建/开项目，自动扫描后立刻可编辑；
共享设置与缓存放程序目录；FMOD DLL 随包分发。

- **启动引导**：WelcomeDialog（新建/打开/最近项目）、上次项目自动恢复、
  主窗口「下一步」提示条、无项目全屏引导；左栏按「① 获取资源 /
  ② 产出模组」重组。
- **共享配置**：`AppEnvironment`（`config/shared-config.json` + `cache/`，
  均在程序目录）；目录解析「共享 → 旧项目字段 → 自动发现」，手动值不覆盖，
  旧项目值首开迁移；FMOD 动态发现不落盘。
- **扫描通道**：`UnityCacheScanService` 引用模式 + 程序目录增量索引缓存 +
  逐 bundle 容错 + catalog 基线；实测 60 bundle≈1.6s（≈280 资产/bundle），
  全缓存 1459 bundle 首扫 ≈1-2 分钟，重扫近瞬时。
- **一键导出**：`UnityCacheMaterializationService`（编辑实体化，规避全缓存
  同名 `__data` 构建冲突）+ `UnityCacheExportService`（重打包→逐对象读回→
  真实加载器 Carra2 键打包），真实数据端到端验证。
- **FMOD 随包**：`scripts/publish.ps1`（third_party/fmod → 发布输出 fmod/，
  git 忽略二进制）+ `FmodLibraryLocator` 自动发现 + 加载/探测候选扩展。
- 新增测试 13 个（共享配置/发现/扫描/实体化/导出），基线 205 → 218。

### P3.7 交互打磨与撤销能力（2026-09-06，完成）

目标：继续清除「不合理处」—— 补上撤销能力、大项目卡顿、长操作无反馈、
缺少快捷交互四个缺口（对应 REVIEW §4 的便捷性提案落地）。

- **撤销修改**：`AssetEditService.ClearEdits`（清除 replacementPath /
  unityFieldEdits / spriteMetadata 三类编辑标记，还原 originalSize 与
  Unchanged 状态，项目 edits/assets 内暂存文件一并删除；项目外文件不动）。
  UI 为右栏「撤销此资源的修改」按钮 + 右键菜单项，带确认对话框。
  替换时新增 `originalSize` 元数据（首次替换时捕获，重复替换不覆盖）。
- **大项目不卡顿**：搜索 300ms 防抖 + 后台线程过滤排序
  （`AssetSearchService.Search(IEnumerable)` 快照重载，UI 线程只取快照；
  代际守卫丢弃过期结果；选中项按 AssetId 跨刷新保留）；纹理预览解码
  移入后台线程（同样代际守卫）；项目保存去掉 sync-over-async
  （设置窗口回调改 `Func<_, Task<bool>>`，扫描后保存走后台线程）。
- **长操作有反馈**：`ExportProgressWindow`（阶段消息 + 不确定进度条，
  主窗口期间禁用）；`UnityCacheExportService` 增加可选
  `IProgress<string>`（实体化逐资源 / 重打包 / 逐对象读回 / 压缩写出）；
  ScanDialog 显示预计剩余时间（按实测速率估算）；导出报告新增
  「打开输出位置」（explorer /select）。
- **快捷交互**：资源列表右键菜单（替换/字段编辑/hex/撤销/复制路径，
  右键先选中所在行）；双击 = 图像替换、其余字段编辑或 hex 预览；
  窗口级拖放（包/文件夹拖入即导入、逐文件容错；单张图片拖到选中图像
  资源上可一键替换）；Ctrl+F 聚焦搜索、Esc 清空；行悬停显示完整路径；
  大小列人性化（B/KB/MB/GB）；「已修改资源」数与提示条/一键导出同一
  口径（HasEdits），不再显示虚增的 Edits 历史条数；左栏新增
  「打开模组目录」「打开项目文件夹」。
- 新增测试 5 个（ClearEdits×4 + 快照搜索口径一致），基线 218 → 223。

### P3.8 VS Code 式工作台与目录树（2026-09-06，完成）

目标：消除「功能 = 独立弹窗」的割裂感，补上类文件夹的资源浏览方式
（对应原始诉求 3 的「多种编辑工作台 + 参照 VS Code 的创建/切换」）。

- **工作台标签页**：主界面改为 48px 活动栏（📦资源 / 📝文本 / 🧩静态 /
  🗂模组 / 📖教程 / ⚙设置）+ 深色标签区（VS Code 编辑器标签样式，
  选中带顶部高亮条）。「资源工作台」为固定首标签；文本模组 / 静态数据 /
  模组管理由独立窗口改为 `UserControl` 嵌入式标签（可关闭标题 ✕，
  关闭后激活相邻标签；重复点击活动栏图标 = 激活已打开的标签，不重建）。
  快捷键 Ctrl+Tab / Ctrl+Shift+Tab 循环切换。教程与设置保持模态对话框
  （低频、轻量，不值得常驻标签）。
- **目录树资源浏览**：中栏新增「☰ 列表 / 🗂 目录树」切换（两态互斥）。
  `AssetTreeBuilder` 把扁平 LogicalPath 按「/」路径段逐层分组：
  根层一次构建（只是引用分组），每个目录节点展开时才按下一层惰性分组
  （目录持有子树全部资源的引用集合，40 万级索引也只在展开到的分支上
  付出分组成本）；TreeView 虚拟化 + 占位子节点懒展开。目录标题显示
  「名称 (N)」；点选叶子与列表共用同一套选中处理（右侧状态 / 按钮 /
  预览），双击叶子 = 与列表双击相同的最常用动作。树内容跟随当前搜索
  结果（筛选后树里也只有命中项）。
- **顺手的性能清理**：`UpdateDirectoryStatus` 每次刷新都触发的四个
  目录定位器扫描（游戏 / Unity 缓存 / 模组 / FMOD）改为进程级记忆化，
  配置变更时显式失效（设置保存 / 自动获取 / 手动指定模组目录）。
- 新增 `AssetTreeBuilderTests` 5 个（根层分组、逐层展开到叶子、深层
  容器路径、目录计数、真实缓存树遍历不丢不重），基线 223 → 228。
  （后续 P3.10 把树的数据源从 LogicalPath 换成 m_Container 容器路径，
  并移除模组管理标签；本节的逻辑路径口径已被 P3.10 取代。）

### P3.9 性能改造（阶段 C，2026-09-06，完成）

目标：审查不必要的性能开销并优化（对应总目标最后一段「引入数据库加速
索引」）。方法：先加 `PerformanceBaselineTests`（LME_BENCH=1 门控）用真实
缓存量化，再按数据优化。实测规模：1459 bundle、**1,194,061 条资产**
（远超早期「40 万」估计）。

量化发现的三个真正瓶颈与处置：

- **项目文件携带全部引用资产**（缩进 JSON）：保存 12.4s / 文件 1595MB /
  打开 64.9s。处置：`SkipReferenceAssetsConverter` 写入时跳过纯引用资产
  （`reference=true`，100% 可由扫描索引重建；读取不过滤，旧项目向后兼容）；
  打开项目时 `UnityCacheScanService.RehydrateFromIndexAsync` 从索引库后台
  重建（实体化/导入资产按 LogicalPath 优先保留，整体替换集合引用不逐条
  触发 CollectionChanged）。→ 保存 0.4s / 1KB，打开 0.04s + 14s 后台回灌。
- **扫描索引 JSON 全文件重写**（164MB 解析+序列化）：热扫描 102.7s。
  处置：`UnityCacheSqliteIndexStore`（Microsoft.Data.Sqlite）——
  `bundles`+`assets` 两表，新鲜度检查内存字典化、资产行单条流式查询分组
  载入、写回单事务批量（synchronous=NORMAL，WAL；索引可整库重建故安全）。
  → 热扫描 45.8s。实测教训：逐 bundle 开连接查询（1459 次）会把并行
  解析阶段拖慢一个数量级——批量元数据必须一次查询载入。
- **冷扫描 ≈ 4-5 分钟**：主体是 bundle 解析 + catalog 基线 CRC 逐 bundle
  评估（生产路径必选、一次性成本，索引持久后不再发生），索引写回仅 18s。
  保持现状，列为后续候选（基线评估结果随行持久化，已是增量的）。

其余量化结论：全量搜索 4.8s（后台+防抖，可接受）、树根层 0.47s、
树全展开 6.3s（惰性，仅展开过的分支付费）——均无需改动。
新增测试 3 个（瘦身往返一致性×2 + 真实缓存基准门控），基线 228 → 231。

### P3.10 资源工作台 UI 重构（容器视图 + 布局精简，2026-09-10，完成）

问题（用户提出）：资源视图按缓存键路径展示，暴露 `<外层键>/<内层键>/<CAB
名>/<pathId>.<typeId>` 这类实现细节；Path ID / Type ID / vanilla 基线等
技术字段占据筛选行与列表列；左侧「模组管理」等入口与主流程无关。目标是
「方便用户做模组」——打开/创建 .lmeproj → 导入或扫描资源 → 改资源/音频/
文本并预览 → 修改暂存在项目里 → 导出时给出各种思路。

处置：

- **容器视图（m_Container）**：`AssetsToolsBackend` 新增 `ReadContainerMap`，
  按真实 Unity 6 bundle 结构（`m_Container` → `Array` 子节点 → pair
  `first`/`second`，PPtr 在 `second.asset` 下，`m_FileID` 0/-1）读出
  「游戏内资源路径 → PPtr」，写入资源元数据 `containerEntry`；扫描索引
  SQLite 加 `assets.container_entry` 列（`EnsureContainerEntryColumn` 旧库
  自动迁移）。注意：`AssetRecord.ContainerPath`（SerializedFile/CAB 名）
  仍被导出与构建链路使用，容器条目一律走新字段，绝不混用。
- **显示层 `AssetDisplay`**：显示路径 = 容器条目（缺失则归入「未命名资源」，
  叶子名退化为「类型中文 #编号」）；导入的旧式资源用 LogicalPath 并剥掉
  纯编号叶子；新增中文类型/状态标签与自然名称比较（icon2 < icon10）。
- **目录树改版 `AssetTreeBuilder`**：树完全建立在显示路径上（逐段惰性展开），
  默认视图即目录树，列表一键切换；两视图共用同一份右键菜单。
- **搜索/筛选/排序 `AssetSearchService`**：新增 `AssetSortKind`（名称 / 大小
  双向 / 类型 / 修改在前）与 `HasContainerEntry`（默认开启「仅显示容器内
  资源」，隐藏容器外支撑对象，导入资源始终可见）；文本匹配覆盖显示路径、
  原路径与源文件。
- **布局精简**：移除「新建项目」（保留新建模组向导）、活动栏「🗂 模组管理」
  与 `ModManagerControl`（按用户要求舍弃模组管理）、Path ID / Type ID 筛选、
  vanilla 基线列；筛选行收拢为 类型 / 状态 / 排序 / 仅显示容器内资源 +
  「高级筛选」折叠区（大小区间 / 仅已替换）；右栏按「创作状态 / 预览 /
  选中资源（名称/位置/类型/大小/状态）/ 常用 / 高级操作 / 调试」重排。
- 新增/改写测试：`AssetDisplayTests` 12 个、`AssetTreeBuilderTests` 重写为
  容器语义 7 个 + 真实缓存遍历、`AssetSearchServiceTests` +5（排序 ×3、
  容器过滤、名称排序期望）。基线 231 → 256（61 → 62 Format + 170 → 194
  Domain）。
- **导出思路（自动分析）**：新增 `ExportAdvisor`（Application/Build）——
  按项目里实际修改的构成（Unity 资源 / 音频 / 已登记源 / 无修改）产出
  `ExportIdea` 清单（标题、一句话结论、展开说明、是否推荐、是否可用与
  中文原因），UI 侧新增 `ExportAdvisorWindow` 与左栏「导出思路（自动分析）…」
  入口；点「一键导出模组」时若存在多种可行出口会先弹出该清单，纯 Unity
  修改则直接导出。新增 `ExportAdvisorTests` 7 个，基线 256 → 263。
- **三种预览与文本编辑**：新增 `TextPreviewService`（只读 UTF-8/UTF-16 文本
  预览，二进制与非 UTF 编码明确拒绝、超长截断），右栏「预览」区按选中项
  在 图像缩略图 / 文本正文 / 音频试听 之间切换；音频试听用 FSB→WAV（临时
  文件）+ WPF `MediaPlayer`，缺 FMOD DLL 或 Unity 音频对象时说明原因；
  新增 `TextAssetEditorWindow` 把既有 `TextAssetEditService`（原本「留给未来
  代码编辑器」）接到 UI：双击文本资源即改，保存登记为替换，JSON 保存前
  校验并格式化。新增 `TextPreviewServiceTests` 9 个、`TextAssetEditTests` +2。

### P3.11 页面架构重构与工作台扩展（2026-09，完成）

目标（用户提出的 10 项修改）：移除顶部标签页、活动栏直切页面、共享侧边栏、
资源只允许自动加载、预览可拖拽调整、强化多格式预览、修复 Unity bundle 资源
属性/预览、并新增音频 / 文本 / 静态数据三个专用工作台。执行按
`docs/plans/` 的 8 份任务书分四波次完成（每波次独立提交）。

- **页面架构（plan-02/03/04）**：主窗口改三列 `[48 活动栏][220 共享侧边栏]
  [* 页面宿主]`；删除 `TabControl WorkbenchTabs` 与 `WorkbenchTabItem`、
  `OpenWorkbench` 系列与 `BuildClosableHeader`（全仓 0 处）；新增
  `IWorkbenchHost` + `ShowPage` 注册表（6 个 key 常驻不销毁）；设置/教程
  从模态窗口改为页面（`SettingsPage` / `HelpPage`，无项目也可用）；
  `ProjectSettingsWindow.cs` / `HelpWindow.cs` 删除。资源页移除手动导入入口
  （中栏按钮 + 侧边栏两项），「扫描游戏资源」更名「自动加载游戏资源」，
  拖放收窄为「单张图片拖到选中图像资源上替换」；新增 `GridSplitter`
  （预览列默认 360 / 最小 260，浏览列最小 280）+ `UiStateService`
  持久化到 `config/ui-state.json`（双击把手复位）。
- **后端修复（plan-01 第 0-2 步 + plan-08 第 1-2 步）**：`UnityClassId` 补
  class 49（TextAsset → Text）；`AssetsToolsBackend` 新增
  `ReadBundleTextAsset` / `ReadBundleSpriteComposite`（`m_RD.textureRect`
  裁剪，实测 `m_Rect` 只是逻辑尺寸）/ `ReadBundleAudioClipData`
  （`m_Resource` 流式 / 旧版 `m_Source` / 内联三种形态，全部走类型树）；
  新增 `UnitySpriteCrop`（Unity 左下原点 → 图像左上原点）与
  `UnityTextureCodec.ToPngCropped`；新增 `AssetPropertyService`（中文属性行，
  单项失败降级）；新增 `StaticBundleLocator`（catalog 动态解析静态 bundle，
  扫描按内层键 O(1) 打 `staticBundle` 标记并持久化到索引，`AssetSearchQuery`
  新增 `ShowStaticTables` 默认过滤）。前后对照矩阵见
  `docs/plans/REALDATA-VERIFY.md`。
- **预览管线（plan-05）**：`IAssetPreviewProvider` + `AssetPreviewRegistry`
  （Texture → Sprite → Audio → Text → Script → Summary → Hex 兜底），七种形态
  （图像缩放/平移/棋盘格/原图↔替换图、文本行号 + JSON 树、音频波形 + 试听、
  摘要卡、只读字段树、内嵌十六进制、说明）；未知类型不空白。
- **三个工作台（plan-06/07/08）**：`BankWorkbenchPage`（bank 扫描 / 样本表 /
  逐样本试听与 WAV 替换 / 导出整包 .bank 或 .rebank）、`TextWorkbenchPage`
  （活动语言目录浏览 / 键值编辑 / 导出 lang 补丁）、`StaticWorkbenchPage`
  （静态 bundle 定位 / 静态表编辑 / 导出 .staticmod）。三个页面复用资源页
  骨架与 splitter；旧过渡控件 `LangTextModControl` / `StaticModControl` 删除。
- **FMOD 2.x 兼容修复**：游戏自带 `fmodstudio.dll`（2.2.26）的
  `FMOD_System_Create` 需要 `headerversion`，此前单参数调用返回
  `FMOD_ERR_HEADER_MISMATCH(20)`；现按导出符号所属 DLL 的文件版本推导
  `FMOD_VERSION`，并支持按子样本索引解码（Bank 逐样本试听）。
- 测试：基线 296 → 334（74 Format + 260 Domain），新增真实数据门控
  `RealPreviewCoverageTests` / `RealWorkbenchGateTests` / `AssetPreviewRegistryTests`
  / `AssetPropertyServiceTests` / `StaticBundleLocatorTests` / `UiStateServiceTests`，
  本机全部真跑（真实缓存 / 真实 bank / 真实 lang / 真实 catalog）。

## P3.12 工作台 UI 一致化 + 表缓存（2026-09，进行中）

用户反馈：「资源工作台的交互页面设计非常好（树形编辑 + 方便预览），但其他三种模组的编辑器
有很大问题——文本编辑器页面不美观、没有树形查看；bank 页面没有所有音频的视图；
static mod 可以使用资源工作台类似的页面。事实上我们也可以使用表来缓存这些数据。」
执行按 `docs/plans/plan-09 ~ plan-12` 四份任务书：plan-09 为串行前置，10/11/12 依赖它。

- **plan-09（前置）**：`WorkbenchShell`（三列骨架 + 每页列宽持久化 + 列表↔树切换）
  与 `JsonTreeEditor`（树/原文双 Tab、按原类型写回、RFC6902 差异摘要）公共件，
  设计令牌集中到 `Themes/WorkbenchStyles.xaml`（页面代码禁止硬编码设计色）；
  `Application/Caching/` 表缓存底座（SQLite，照抄 `UnityCacheSqliteIndexStore` 的事务/WAL/
  轻量迁移策略），建好 `cache/` 下 `bank-index.db` / `static-tables.db` / `text-index.db` 表结构。
- **plan-10 文本**：🗂 目录→文件→JSON 键层级树 + ☰ 列表，独立搜索结果视图，
  键值树惰性展开（去掉 5000 行截断），`cache/text-index.db` 缓存文件索引与搜索命中。
- **plan-11 音频**：**跨 bank 样本总表**（bank/FSB/样本名/codec/采样率/声道/时长/大小/状态，
  虚拟化 + 筛选排序）+ bank→FSB→样本 树 + 现有 bank 列表，`cache/bank-index.db`
  按 `(size,mtime)` 增量重建；冷建索引带进度且可取消。
- **plan-12 静态**：与资源工作台同款页面（dataClass 树 + 列表 + 筛选/排序 + 修改标记），
  搜索改走 `cache/static-tables.db`（不再同步逐张解码 1392 张表），编辑器复用 `JsonTreeEditor` + diff Tab。

约束：缓存只存 vanilla 事实、编辑集不落缓存；失效规则只有「源签名变化」一条；绝不写游戏目录。

## P4：工程质量和交付

- 把当前代码后置 UI 逐步迁移到 MVVM，但不牺牲现有 Windows-only 可运行性。
- 为每个格式 handler 建立 fuzz/损坏输入测试，确保不会路径穿越、整数溢出或无限分配。
- 添加静态分析、格式化、覆盖率和 Windows CI。
- 建立版本化迁移说明、用户手册、故障排查和样本脱敏规范。
- 发布 zip、校验和、版本信息及第三方许可证清单。

## 分派给其他 agent 的建议顺序

1. P0.1：真实样本端到端验证和兼容性报告。
2. P1.1：通用字段编辑器完善与差异/撤销模型。
3. P1.2：PPtr 依赖图和引用安全检查。
4. P2.1/P2.2：Bank/FMOD 专业工作流。
5. P1.3：ETC/ASTC/RGB565 等纹理格式，必须先确认可用库的许可证。
6. P1.4：SpriteAtlas mesh 和引用重建。
7. P3.1/P3.2：新建模组向导、导出报告和兼容性矩阵。
8. P0.2/P4：事务文件系统、锁冲突、CI 和正式发布。

## 不应做的事情

- 不要继续手写完整 Unity Bundle/SerializedFile 解析器；优先扩展 AssetsTools.NET 适配层。
- 不要伪造或分发 FMOD/FSBank 专有 DLL。
- 不要把未知对象、未知压缩格式或删除操作静默转换成看似成功的输出。
- 不要在没有真实样本验证时宣称格式完全兼容。
- 不要使用宽泛递归删除、覆盖用户游戏目录或跳过备份的调试操作。
