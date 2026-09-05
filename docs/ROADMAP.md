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

验收：

1. 导入后对象数量、Path ID、Type ID 与 AssetsTools.NET/UnityPy 交叉核对。
2. 对 Texture2D、Sprite、MonoBehaviour、ScriptableObject 各选一个对象，完成预览、修改、写回。
3. 用游戏或独立验证器重新加载写回文件，确认 Bundle/SerializedFile 未损坏。
4. 安装/卸载调试 overlay 后，原文件哈希可恢复，重复应用不会累积临时文件。
5. 记录 Unity 版本、压缩方式、失败对象和兼容性诊断，不把未知对象静默丢弃。

### P0.2 构建事务和文件锁

- 为 Bundle、SerializedFile、Carra、Bank、Rebank 的构建统一使用事务目录。
- 处理目标文件被游戏进程占用、只读、同名临时文件和进程异常退出。
- 所有临时文件在成功、失败、取消三种路径都能清理或留下可恢复日志。
- 增加并发构建/取消构建测试。

### P0.3 编码和诊断统一化

- 清理现有源文件中的乱码中文字符串，统一 UTF-8 和资源文本编码策略。
- 引入结构化诊断：代码、严重级别、文件、Path ID、建议操作。
- WPF、CLI、日志使用同一诊断模型；导出报告可保存为 JSON/文本。

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
枚举值经权威工具链核对：ARGB32 为 B,G,R,A 字节序、RGBA4444=13、RG16 为 2 字节双 8 位
通道）；`TextureFormatCatalog` 标注有损/无损与字节序说明并在预览行展示
（`ReadTextureSummary`）；`UnityTextureMipmaps` 提供逐层布局、mip 数推断与
`Slice`（含 DXT 块对齐）。DXT 编解码维持托管实现（有损已标注）。ETC/ASTC 需要可靠
可再分发库，当前不提供预览也不声称支持（符合"不伪造"约束）；mipmap 重建式替换与
可读性检查待后续。

### P1.4 Sprite、SpriteAtlas 和网格

- 读取 SpriteAtlas 对象、页纹理、SpriteRect、packing tag、mesh 顶点和索引。
- 支持网格/边界编辑与引用重建；未支持的类型必须阻止写回而不是降级。
- 提供 atlas 可视化：原图、SpriteRect、pivot、border、mesh overlay。
- 添加 SpriteAtlas 真实样本端到端测试。

### P1.5 Mesh、Animation、Font 只读和基础替换

- 先实现安全只读摘要和导出（顶点数、材质、骨骼、曲线、字体信息）。
- 再按真实样本确定可写字段；每种资源单独设计版本化替换器。

## P2：音频和 Bank/FMOD 工作流

### P2.1 Bank 结构化编辑

- 完善 RIFF/FEV/SNDH 索引显示：事件、总线、FSB、音频条目、偏移和大小。
- 支持 FSB 子声音列表、采样率、声道、长度和编码诊断。
- 任何加密/未知 FSB 使用清晰错误，不尝试猜测或破坏性重写。

状态（部分完成）：SNDH 偏移/大小索引与 FSB 提取/重组此前已落地并有测试；本轮新增
FSB5 只读结构探测（`Fsb5Parser`：版本、样本条目（数据大小/采样率/声道标记/编码）、
名称表、数据起始偏移、诊断列表）与「查看 FSB 结构」窗口（`BankInspectorWindow`）。
加密/未知负载给出明确原因，不做猜测或改写。事件/总线级 FEV 索引展示与 WAV→FSB
替换选项（P2.2）仍待后续轮次。

### P2.2 FMOD DLL 管理

- 增加 DLL 版本/位数/导出符号探测报告和兼容性缓存。
- 启动时不加载未知 DLL；用户明确选择目录后才加载。
- 提供 WAV→FSB 选项：格式、采样率、声道、循环元数据，并显示有损编码警告。
- 不分发 FMOD/FSBank 专有 DLL，不逆向或伪造编码器。

状态（部分完成）：`FmodDllInspector` 从原始字节解析 PE 头与导出表（不 LoadLibrary、
不执行），报告位数/版本资源/导出符号快照与缺失的解码、FSBank 编码符号；
`FmodCompatibilityService` 以 DLL 大小+时间戳指纹缓存探测结果（logs/fmod-probe.json）；
「检测 FMOD DLL」按钮展示就绪状态与 32 位告警。加载仍遵循"用户明确选择目录后才绑定"
的既有原则。WAV→FSB 选项面板待后续。

### P2.3 Bank 调试和回滚

- 将 Bank 构建输出接入 overlay 和游戏启动事务。
- 增加“仅替换一个 FSB”“批量替换”“恢复原 Bank”测试。

## P3：导入、导出和创作体验

### P3.1 新建模组向导

- 选择目标格式、账号、Bundle、资源类型和目标游戏目录。
- 根据格式生成最小合法 Carra/Rebank/Lunartique 工程模板。
- 模板必须通过对应 handler 的 ValidateAsync，并给出输出路径预览。

### P3.2 导出向导和兼容性矩阵

- 在导出前显示源格式→目标格式矩阵。
- 对不支持的跨格式转换提前禁用并解释原因。
- 展示未知文件保留策略、对象数量、替换数量、压缩方式和潜在风险。
- 导出报告包含每个资源的状态：应用、跳过、失败、保留未知。

### P3.3 资源搜索和工作区

- 按路径、类型、大小、Path ID、Type ID、编辑状态、哈希和诊断过滤。
- 支持收藏、标签、最近编辑、批量选择和撤销/重做。
- 大型游戏目录使用增量索引、取消和后台线程，UI 不阻塞。

### P3.4 预览和比较

- 二进制对象显示十六进制、结构化字段和原始哈希。
- 图片显示原图/修改后对比、缩放、透明棋盘格和颜色通道。
- 音频显示波形、时长和循环标记（需要合法 FMOD DLL 时）。

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
