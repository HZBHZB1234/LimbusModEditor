# Limbus Mod Editor 用户手册

本手册面向直接使用编辑器界面的模组作者，按真实操作顺序组织。所有窗口均为中文界面；
命令行（CLI）只覆盖探测/导入/导出三步，完整编辑流程请使用 GUI。

## 1. 准备工作

1. 启动 `LimbusModEditor.App.exe`（或 `dotnet run --project src/LimbusModEditor.App`）。
2. 点击 **新建模组向导…**（推荐）或 **新建项目**：
   - **新建项目** 只选择 `.lmeproj` 保存位置；
   - **新建模组向导** 额外收集名称/版本/作者/描述并生成空白模板：
     Carra/Carra2 → 空对象包 `.carra2`；Rebank 与 Lunartique **不再提供空白模板**
     ——真实加载器会把 wav 数为 0 的 Rebank 判为错误并回滚安装
     （launcher/bankmod.py:189-198），Lunartique 必须以现有模组为基底；
     两者在向导中置灰并显示原因。模板会先经对应格式处理器校验
     （校验失败不写盘）。项目会在同目录创建：
   - `sources/` — 导入的原始资源（不会再改动）
   - `edits/` — 替换素材与记录
   - `previews/` — 预览缓存
   - `builds/` — 构建产物
   - `backups/` — 应用调试覆盖层前的游戏文件备份
   - `logs/` — 日志（含 FMOD 探测缓存 `fmod-probe.json`）
3. 在右侧设置区完成目录配置：
   - **游戏目录** — `LimbusCompany.exe` 所在目录（应用并启动调试时需要）。
     可手动选择，也可点击 **「自动定位游戏目录」**：编辑器会扫描已知的 Steam 库
     （注册表 Steam 路径 + `libraryfolders.vdf` + 常见安装位置），找到包含
     `LimbusCompany.exe` 的安装目录并自动填入；未找到时请改用手动选择。
   - **Unity 缓存目录** — 游戏的 Addressables 缓存根目录，用于导入 bundle 时补全
     依赖信息。真实布局为 `LocalLow/Unity/ProjectMoon_LimbusCompany`（可能是迁移到
     其它盘的 junction）下的 `<32位hex外层键>/<32位hex内层键>/__data`。可点击
     **「自动建议 Unity 缓存目录」**：编辑器从游戏目录与 LocalLow 推导候选，只列出
     验证过实际包含缓存条目的目录并显示条目数量，由你确认后填入。
   - **模组目录** — 你的模组存放根目录（导出时默认位置）。真实加载器默认使用
     `%APPDATA%\LimbusCompanyMods`，可点击 **「自动建议模组目录」** 一键填入；
     配置后可用 **「管理已安装模组」** 查看已装模组并按加载器自身的
     `_disable` 后缀约定切换启用/禁用（只重命名，不改文件内容）。
   - **FMOD DLL 目录** — 含 `fmod64.dll` / `fsbank64.dll` 的目录。**必须使用你合法获得的
     DLL**，本工具不附带任何 FMOD 二进制文件；没有它时音频只能探测索引，不能解码。
     配置后可用 **「检测 FMOD DLL」** 在不加载任何 DLL 的前提下核对位数、版本与
     导出符号是否齐全。

## 2. 导入资源

- **导入资源包**：支持 `.carra` / `.carra2` / `.rebank` / `.bank` / Lunartique ZIP /
  Unity `.bundle` / 独立 SerializedFile `.assets`，以及普通资源目录。
- 导入会把文件复制进 `sources/` 并在项目文件里登记，原始文件不会被修改。
- 导入后资源出现在合并索引中，可用顶部搜索框与筛选行按名称、类型、Path/Type ID、
  大小、状态与替换状态过滤。
- **从文件夹批量登记替换**：选择一个文件夹，按文件名（不区分大小写）匹配资源并
  登记替换；默认跳过已替换的资源，完成后报告匹配数与未匹配文件清单。

## 3. Unity 对象检查与字段编辑（P1.1 / P1.2）

选中一个 Unity 资源后，右侧面板提供：

### 3.1 Unity 字段编辑
1. 点击 **Unity 字段编辑**。窗口顶部会显示脚本信息（m_Script 解析出的类名/命名空间/
   程序集；指向外部文件时显示其路径与 GUID）。
2. 字段树列出每个字段的真实序列化类型。默认只显示可编辑字段，勾选 **显示全部字段**
   可以看到数组、PPtr、字节数组等结构信息。
3. 编辑值并勾选 **应用** 列。保存前会做三层校验：
   - 类型校验（bool/整数/浮点/字符串按序列化类型解析）
   - 未知路径 / 不可编辑字段拦截
   - **指针语义校验（P1.2）**：`m_FileID` 不得超出外部引用表；同文件指针
     （`m_FileID=0`）的 `m_PathID` 必须指向真实存在的对象，否则报
     **InvalidTarget** 并标红该行
4. 校验失败时窗口保持打开、错误行标红，可修正后重试或取消。
5. 保存后修改以版本化编辑集存储（含原值、原值哈希、时间、作者、每字段修订号），
   构建时才会真正写入。

### 3.2 指针依赖显示与自动填充（P1.2 自动化）
- 窗口底部蓝色信息栏实时显示所有指针依赖的解析结果：
  - `空引用` — file/path 都是 0
  - `同文件 Path N（类型）` — 指向本 SerializedFile 内的对象
  - `外部文件 xxx Path N` — 通过外部引用表指向其他文件（附 GUID）
  - `无法解析` — 悬空指针（目标不存在）
- 选中某个 PPtr 的 `m_PathID` / `m_FileID` 行后，右下角下拉框会列出**同文件中与该
  指针声明类型匹配的对象**（例如 `PPtr<$GameObject>` 只列 GameObject）。选择对象后
  点击 **自动填充所选 PPtr**，即可自动写入 `m_PathID` 并把 `m_FileID` 归零，
  不需要手动查抄 Path ID。

### 3.3 查找引用者（P1.2）
- 资源面板的 **查找谁引用了此对象** 会扫描：
  - 独立 SerializedFile：本文件内所有指向该对象的指针
  - Bundle：包内**所有** SerializedFile，包括通过 `archive:/...` 形式跨文件引用
- 结果按 `Path N（类型）字段 xxx` 列出。修改/删除对象前先看这个列表：
  - 允许：把指向它的指针改成空引用（保留空引用策略）
  - 允许：重定向指针到另一个真实对象（重定向策略）
  - 拒绝：把同文件指针改成不存在的 Path ID（保存时被 InvalidTarget 拦截）

### 3.4 构建时引用完整性验证（P1.2）
- 字段预校验在构建前执行（`ValidateObject/BundleFieldEdits`），任何
  InvalidTarget/UnknownPath/ParseError 都会中止构建并给出完整原因列表。
- 每次重写 SerializedFile / 重打包 Bundle 之后，构建服务会做 **引用完整性 diff**
  （`VerifySerializedReferences` / `VerifyBundleReferences`）：对比重写前后每个对象
  的每个指针解析状态。任何"原本可解析 → 悬空"的回退都会使构建失败并列出具体字段，
  防止打包过程悄悄破坏引用。

## 4. 图片与图集

- 支持 Texture2D PNG 预览与替换（RGB24、RGBA32/ARGB32/BGRA32、DXT1/DXT5，DXT 为有损编码）。
- Sprite 支持 rect/pivot/border 元数据查看与编辑。
- 图集：**拆分图集** 从 SpriteAtlas 输出子图，**恢复图集** 按记录重新打包。
- ETC/ASTC 压缩格式解码与图集网格重写属于后续里程碑（P1.3/P1.4）。

## 4.1 对象摘要（P1.5，Mesh / 动画 / 字体）

选中 Mesh、AnimationClip 或 Font 类型的资源后，点 **「查看对象摘要」**：

- Mesh：子网格数、顶点数、顶点数据大小、索引缓冲大小；
- AnimationClip：旧版模式、采样率、动画类型（Legacy/Generic/Humanoid）；
- Font：字号、字符表条目数、内嵌字体数据大小、默认材质指向（同文件/外部文件）；
- 所有数值只来自该文件自己的类型树；某版本缺字段时在「未包含」清单中明确标注，
  绝不用默认值补齐；窗口内可复制文本或导出 JSON（`logs/` 外任意位置）。

## 5. 音频（Bank / FMOD）

- `.bank` 仅解析 RIFF/FEV/SNDH 索引层，不做专有格式逆向。
- 配置 FMOD DLL 目录后可 **导出当前音频 WAV**（FSB→WAV 预览/导出）。
- WAV→FSB 替换与基于 WAV 的 `.bank` 重建同样依赖你提供的合法 DLL。
- 完整 FSB5 原样替换不需要 DLL。
- **「检测 FMOD DLL」**（P2.2）：无需加载 DLL 即可查看目录中 fmod64.dll /
  fsbank64.dll 的位数（x64/x86）、文件版本、导出符号快照，以及解码与 FSB 编码
  接口是否齐全；结果按 DLL 大小/时间戳/内容指纹缓存（`logs/fmod-probe.json`），
  目录变化后自动重新检测。32 位 DLL 会给出明确警告（本编辑器为 64 位进程）。

## 6. 构建与调试

1. **导出模组（导出向导，P3.2）**：向导先识别源格式，再显示
   源格式→目标格式兼容性矩阵（不可用目标置灰并解释原因，例如 Lunartique→对象级
   Carra 需要有效 Unity SerializedFile）、项目概况（资源数/替换数/字段编辑数）与
   输出路径建议（选择目标后自动切换扩展名）。
2. **导出报告**：导出完成后显示逐资源状态（已应用 / 已跳过 / 保留未知）与诊断；
   「已跳过」的资源不会写进包，可按说明检查替换路径是否匹配。
   导出 Carra/Carra2 时若配置了 Unity 缓存目录，编辑器会逐外层键核对缓存中
   是否仍有对应 bundle（真实加载器按 `<缓存根>/<外层>/<内层>/__data` 匹配；
   游戏更新会更换外层键，失配的模组会被加载器静默跳过）——失配以
   「缓存对齐：…」诊断出现在报告中。
3. **构建调试覆盖层**：在 `builds/` 生成应用文件。
4. **应用并启动调试**：先备份游戏目录中被覆盖的文件到 `backups/`，再应用覆盖层并
   启动 `LimbusCompany.exe`。编辑器关闭时可恢复备份。
5. 所有构建输出统一经过事务写出（P0.2）：内容先写入临时文件再原子替换到目标；
   失败/取消时原文件保持不变、临时文件自动清理；目标被游戏占用时给出明确提示，
   请关闭游戏后重试。
6. **文本模组（lang 补丁，T2）**：主窗口「文本模组（lang 补丁）…」打开
   lang 补丁窗口。lang 根目录可「自动获取」（定位游戏目录后拼接
   `LimbusCompany_Data/lang`）；窗口显示 config.json 中的活动语言。
   「从目录差异生成补丁…」对比 原版目录 vs 修改目录，产出与真实加载器
   （LCTA launcher/changes.py）兼容的 `{"patchs": {...}}` JSON——放进模组
   目录即会被加载器在启动时应用（目标文件自动 .bak、退出还原）；修改目录
   中多出的文件、原版有而修改目录缺失的文件会作为诊断明确报告（补丁通道
   无法承载，绝不静默丢弃）。「应用补丁到 lang 目录…」把补丁就地写回
   lang 目录（目标缺失的条目按真实加载器行为跳过并报告）。
7. **vanilla 基线判定（T3）**：导入 bundle 时，若项目已设置游戏目录且其中
   有官方 catalog（`StreamingAssets/aa/catalog.bin`），编辑器会只读解析
   catalog 并判定该 bundle 相对 vanilla 的状态（vanilla 一致 / 与基线不符 /
   不在 catalog / 基线未知），写入资源列表「vanilla 基线」列并出现在导入
   诊断中。「与基线不符」通常意味着该缓存 bundle 已被模组补丁改写过。

## 6.1 资源筛选（P3.3）

资源列表上方的筛选行支持：自由文本（匹配资源路径）、类型下拉、编辑状态下拉、
Path ID / Type ID 精确匹配、KB 大小区间和「仅已替换」开关。「清除筛选」一键还原；
筛选生效时右上角资源计数显示 "筛选后 / 总数"。

## 7. 故障排查

| 现象 | 原因与处理 |
| --- | --- |
| 字段编辑保存报 InvalidTarget | 同文件指针指向了不存在的 Path ID；用「自动填充」选择真实对象，或把 m_PathID 改回 0 |
| 构建报「重写后引用完整性检查失败」 | 某次重写把原本可解析的指针变成悬空；按提示的字段回退修改 |
| 无法读取 SerializedFile / Bundle | 文件损坏或不是 Unity 序列化文件；先用 CLI `probe` 探测 |
| 音频导出灰显 | 未配置 FMOD DLL 目录，或目录里缺少 fmod64.dll/fsbank64.dll；先用「检测 FMOD DLL」查看缺什么 |
| Sprite 按钮灰显 | 选中对象不是 Sprite 类型，或来源不是 Unity bundle/SerializedFile |
| 导出报「目标被占用 / 无法写出」 | 输出文件被游戏或其他程序锁定；关闭占用程序（或以管理员权限）重试，原文件未受影响 |

## 8. 格式边界与合规

- Unity 解析全部经由 AssetsTools.NET，本工具不手写 Unity 二进制解析器。
- 不附带、不伪造任何 FMOD 二进制；运行时只绑定用户提供的 DLL 的公开 C ABI。
- 未经真实游戏样本验证的格式兼容性不会宣称；未支持的对象类型会明确报告而不是
  静默丢弃。
- FMOD DLL 检测只读取 PE 头与导出表（不加载、不执行任何 DLL 代码）。

## 9. 验证

```text
dotnet build LimbusModEditor.slnx --no-restore
dotnet test LimbusModEditor.slnx --no-restore
```

测试覆盖项目持久化、导入/导出、Carra/Lunartique/Rebank 往返、XZ 压缩往返、
Bank 探测、图像/图集操作、覆盖层备份/恢复、Unity 字段树与编辑校验、依赖解析、
引用者扫描、指针语义校验与重写后引用完整性验证，以及本轮的 FMOD DLL 探测
（合成 PE）、导出兼容性矩阵、资源搜索筛选与事务写出（锁定/取消/失败清理）。

本轮新增：T1 真实写回链路验证（见 9.1）、RFC6902 文本补丁语义
（`TextDiffServiceTests`，生成→应用→回读相等与非法补丁 fail fast）、
lang 补丁文档与真实 lang 目录验证（`LangTextPatchServiceTests`）、
catalog 解析与 vanilla 基线判定（`CatalogBaselineTests`，合成样本按记录区
布局构造 + 真实 catalog 数量级门控；名字数与 LCTA 输出一致 1461=1461，
未补丁 bundle 的解压 CRC 与 catalog 记录交叉核对 10/10 一致）。

### 9.1 写回链路真实数据验证（T1）

`RealWriteBackTests`（`tests/LimbusModEditor.Format.Tests/`）在本机存在真实
Unity 缓存时执行端到端写回验证：真实流纹理 PNG 替换 → 内联像素 + 清
`m_StreamData` → 重打包引用校验 → 真实 Carra2 结构导出（键 =
`<缓存外层>/<内层>/<pathId>.<类型表索引>`）→ 重新导入逐字节核对。
只验证时产物写入 `artifacts/realdata-verify/`；追加环境变量
`LME_REALDATA_INSTALL=1` 会把验证模组复制进 `%APPDATA%\LimbusCompanyMods`
（游戏内确认与卸载步骤见 `docs/REALDATA-VERIFY.md`）。无真实样本的机器上
测试自动跳过。
