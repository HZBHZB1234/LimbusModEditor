# 文件功能索引（PROJECT-INDEX）

> 用途：**按文件**快速了解「这个项目有哪些文件、各自负责什么」，以及「要改的东西在哪个文件」。
> 索引以读代码为准（不依赖运行程序、探针或截图）。
>
> 配套：
> - 分层架构、运行流程、跨文件不变量：`docs/CODE-STRUCTURE.md`
> - 待办 / 铁律 / 真实环境事实：`docs/STATUS.md`
> - 用户可见行为：`docs/USAGE.md`
>
> **本索引的写法（2026-09-12 起）**：每张表只有「命名空间 / 文件 / 功能」三列 ——
> **不写行号、不写类名与方法签名**（那些改一次代码就过期，维护成本大于收益）。
> 需要精确到方法时**直接 grep 文件**。带 **★** 的文件另有一节「要点」说明改动红线。
>
> 命名空间列的 `…` 表示该项目/目录的公共前缀，例如 `LME.Application.Build` 对应
> `namespace LimbusModEditor.Application.Build;`。

## 目录

| 编号 | 范围 |
|---|---|
| §1 | 仓库根配置与脚本 |
| §2 | `src/LimbusModEditor.Domain`（纯模型） |
| §3 | `src/LimbusModEditor.Formats.Abstractions` |
| §4 | `src/LimbusModEditor.Infrastructure` |
| §5 | `src/LimbusModEditor.Editing`（图像） |
| §6 | `src/LimbusModEditor.Formats.Unity` ★ |
| §7 | `src/LimbusModEditor.Formats.Carra` / `.Bank` / `.Rebank` / `.Lunartique` |
| §8 | `src/LimbusModEditor.Application` ★（服务层，按功能分组） |
| §9 | `src/LimbusModEditor.App`（WPF）★ |
| §10 | `src/LimbusModEditor.Cli` |
| §11 | `tests/` |
| §12 | `artifacts/` 与 `third_party/` |
| §13 | 元数据（`AssetRecord.Metadata`）键字典 |
| §14 | 按「症状」定位文件的速查表 |

---

## §1 仓库根配置与脚本

| 命名空间 | 文件 | 功能 |
|---|---|---|
| — | `LimbusModEditor.slnx` | 解决方案清单（13 个 src 项目 + 2 个测试项目）；**新项目必须在此登记**，否则本地构建都不编译。**刻意独立的例外**：`tests/LimbusModEditor.SpineRuntime.Tests`（W2 随工程删除）与 `src/LimbusModEditor.Web/`（Node 工程，见 §1），登记在各自小节 |
| — | `Directory.Build.props` | 全局编译约定：`net8.0`、`Nullable=enable`、`TreatWarningsAsErrors=true`（**警告即失败**）、`RuntimeIdentifier=win-x64` |
| — | `nlog.config` ★ | **日志配置的唯一来源**（NLog 6.2）：`current.log` 全量 + `errors.log` 只收 Error/Fatal（跨会话追加）、按大小轮转归档、AsyncWrapper 丢弃式背压（**绝不阻塞 UI 线程**）、`memory` 环形缓冲（UI/崩溃快照）、`autoReload="true"` 运行中改级别即生效；同时内嵌进 `Application` 以便被删时自动还原。埋点约定见 `docs/LOGGING.md` |
| — | `global.json` | 固定构建 SDK 版本（`rollForward: latestPatch`） |
| — | `README.md` | 项目门面：三步工作流、格式边界、CLI、打包、文档导航与验证命令 |
| — | `scripts/publish.ps1` | Release win-x64 发布，并把 `third_party/fmod/*.dll` 复制进输出 `fmod/`（纯 ASCII，兼容 PS 5.1） |
| — | `.gitignore` | 仓库卫生（忽略 `bin/obj/artifacts/third_party/fmod` 等） |
| — | `src/LimbusModEditor.Web/` ★ | **前端工程根（固定，2026-09-15 起）**：Vite + Vue3 + TS + vue-router + Pinia；**刻意不进 `.slnx`**（Node 工程与 dotnet 构建隔离，与 `SpineRuntime.Tests` 同理）；设计色只允许出现在 `src/styles/tokens.css`；构建产物 `dist/` 发布时复制进 `artifacts/publish-win-x64/wwwroot/`。架构与 IPC 契约见 `docs/ARCH-WEBVIEW2-VUE.md` / `docs/WEB-IPC-CONTRACT.md` |

---

## §2 `src/LimbusModEditor.Domain`（共享基础层：数据模型 + 纯数据规则 + 日志调用约定）

命名空间前缀：`LimbusModEditor.Domain.*`

| 命名空间 | 文件 | 功能 |
|---|---|---|
| `…Assets` ★ | `Assets/AssetModels.cs` | 全仓库最核心的资产模型：资产类型/编辑状态枚举 + 资产记录；`Metadata` 是大小写不敏感的扩展槽 |
| `…Edits` | `Edits/EditModels.cs` | 「一次编辑操作」的不可变审计记录（项目 `Edits` 集合元素） |
| `…Edits` ★ | `Edits/UnityFieldEditModels.cs` | Unity 字段编辑的版本化记录 + JSON 编解码（保留 v1 扁平字典回退分支） |
| `…Formats` | `Formats/FormatModels.cs` | 格式标识与诊断模型（校验报告 = 不含 Error 即有效） |
| `…Formats` ★ | `Formats/ExportLayout.cs` | **导出目录布局的唯一事实来源**（纯数据、无 IO）：槽位清单与目录名 |
| `…Projects` ★ | `Projects/ModProject.cs` | `.lmeproj` 文档根对象（schema 版本 + 三个 ObservableCollection） |
| `…Projects` | `Projects/ProjectSource.cs` | 导入到项目里的包/目录来源记录 |
| `…Diagnostics` ★ | `Diagnostics/LoggerExtensions.cs` | 日志调用约定（NLog 的 `Logger` 扩展）：`Log.Scope`（计时作用域，慢操作升级 Warn）、`Log.Guard/GuardAsync`（拦截异常 + 完整栈，**用于替换既有静默 catch**）、`Log.Every`（十万级循环采样）。全仓库唯一的日志"自研"部分，其余（轮转/异步/缓冲）交给 NLog |

### ★ 要点

- **`AssetModels.cs`**：新增资产类型必须**同时**改 `Formats.Unity/UnityClassId`（映射）与
  `Application/Assets/AssetDisplay`（中文标签）；枚举只能**追加末尾**（数值被 UI 当筛选值下发）。
  `Metadata` 比较器一旦改成大小写敏感，所有既有工程都读不到扩展数据。
- **`ExportLayout.cs`**：三个消费方必须同步 —— `Application/Build/ModExportPlanService`（规划）、
  `Application/Build/ModPackExportService`（执行）、`Application/Debugging/ModApplyService`（调试按槽位分派）。
  槽位目录字面量属于**加载器口径**，改名等于改 mod 加载行为；对未登记槽位抛异常是保护网，别绕过。
- **`UnityFieldEditModels.cs`**：路径→值映射是「后者覆盖」（最新编辑胜）；哈希用小写 hex；
  改 JSON 形状必须保留 v1 回退。

---

## §3 `src/LimbusModEditor.Formats.Abstractions`

命名空间：`LimbusModEditor.Formats.Abstractions`

| 文件 | 功能 |
|---|---|
| `FormatContracts.cs` | 格式插件的唯一接口契约：探测/导入/校验/导出 + 上下文 DTO（`ModPackage`、`ExportContext` 等） |

- **新增格式的做法**：一般不用改本文件 → 新建 `Formats.Xxx` 项目实现格式接口
  → 在 `Application/Formats/FormatRegistry` / `BuiltInFormatRegistry` 注册
  → 需要新上下文参数时才加 DTO 字段（**必须给默认值**：多处调用点只传部分参数）。

---

## §4 `src/LimbusModEditor.Infrastructure`

命名空间：`LimbusModEditor.Infrastructure.*`

| 命名空间 | 文件 | 功能 |
|---|---|---|
| `…FileSystem` | `FileSystem/SafePathService.cs` | 相对路径 → 根目录之下的越界防护（防 zip-slip） |

> **重要事实**：`SafePathService` **没有任何调用点**（未接线代码）；等价校验目前以内联私有方法
> 散布在 `Application/Build/ProjectBuildService`、`Application/Debugging/DebugApplyService`、
> `Application/Assets/ModImportService` 等处。想统一防护 = 把那些内联实现改为调用本服务；
> **只改本文件不改变任何运行时行为**。

---

## §5 `src/LimbusModEditor.Editing`（图像编解码）与 `src/LimbusModEditor.SpineRuntime`（Spine 离线渲染）

命名空间前缀：`LimbusModEditor.Editing.Images`

| 文件 | 功能 |
|---|---|
| `Images/UnityTextureCodec.cs` ★ | Unity Texture2D 负载 ↔ PNG 双向编解码（非压缩格式 + 托管 DXT1/DXT5）、纹理格式能力目录、mipmap 布局数学 |
| `Images/ImageAtlasService.cs` | 图集按网格/区域切分与回填 |
| `Images/ImagePreviewService.cs` | 任意图像 → 等比缩略 PNG（含原尺寸/格式/是否含 alpha） |
| `Images/ImageRegionModels.cs` | 图集区域/布局纯模型（布局会序列化为 `layout.json`） |

### ★ 要点（`UnityTextureCodec`）

- **第一原则是行序**：Unity 负载**左下原点**（第 0 行 = 图像最下面一行）；解码翻正一次、
  编码翻回一次，DXT 块行号同样自下而上。**「贴图上下颠倒」几乎总是这条约定被绕过**
  （例如调用方又翻了一次）。
- **新增一种纹理格式的同步点**：枚举值 → 每像素字节数 → 读像素 → 写像素 → 格式能力目录。
  漏改会在更深层抛「不支持」异常。
- **通道坑**：`Argb32` 是 A,R,G,B（与 `Bgra32` 不同）；`R8` 写红通道而 `Alpha8` 才是 alpha；
  `Rg16` 是「两个 8 位通道」。
- **已知边界**：`ToImage`/`FromPng` 只处理 level 0（mipmap 只支持布局计算与切片）。

### §5.2 `src/LimbusModEditor.SpineRuntime`（Spine 4.0 骨骼动画离线渲染）— **2026-09-15 起计划删除（W2）**

命名空间：`LimbusModEditor.SpineRuntime`

> **⚠️ 已定案删除**（`docs/ARCH-WEBVIEW2-VUE.md` §7）：Spine 渲染全量前移前端（官方 spine-ts 运行时），
> 本工程与 `third_party/spine-csharp/`、`tests/LimbusModEditor.SpineRuntime.Tests/` 一并移除。
> 保留能力仅「定位并流式喂原始字节」（留在 `Application/Spine/SpineAnimationSourceService.cs`）
> 与纯定位逻辑（`SpineSiblingIndex` 迁为独立文件）。删除/保留逐文件清单见 ADR §7；
> 测试基线 746 → **733**（Domain 672→659）。下方为**删除前**的现状记录。

| 文件 | 功能 | 处置（W2） |
|---|---|---|
| `SpineDocument.cs` ★ | 加载入口：`TryCreate(骨架 JSON, 图集文本, `ISpineTextureSource`)` → 可查询/可渲染的文档（动画清单、时长）。失败给中文原因，不抛 | **删除**（渲染前移前端） |
| `SpineFrameRenderer.cs` ★ | 渲染入口：`RenderFrame(time, 宽, 高)` → PNG 字节；立绘等比缩进画布 | **删除** |
| `ISpineTextureSource.cs` | 「图集页名 → PNG 字节」的解耦接口（App 侧由 `SpineAnimationSource.PageBytes` 适配） | **删除** |
| `SkiaTextureLoader.cs` | 用 SkiaSharp 把 PNG 字节解码成 Spine 运行时需要的纹理 | **删除** |
| `SpineResults.cs` | 失败结果模型（中文原因） | **删除** |
| `LimbusModEditor.SpineRuntime.csproj` | 工程文件；刻意覆盖根 `Directory.Build.props`（`Nullable=disable` + `TreatWarningsAsErrors=false`，vendored 源码非 nullable 标注） | **删除** |

### ★ 要点（`SpineRuntime`，删除前现状）

- **本工程刻意覆盖根 `Directory.Build.props`**：`Nullable=disable` + `TreatWarningsAsErrors=false`
  （vendored 官方源码不是 nullable 标注的；不为消警告大改官方源码）。`NoWarn=CS8632` 只抑制
  vendored 源码的历史警告，**本工程自身新增代码保持零警告**。
- **不要在 App 层直接引用 spine-csharp 的命名空间**：App 只碰 `SpineDocument` / `SpineFrameRenderer`
  与 `Application.Spine.SpineAnimationSource`；渲染细节留在本类库，保持可在无桌面环境复用。

---

## §6 `src/LimbusModEditor.Formats.Unity`（Unity 后端）

命名空间：`LimbusModEditor.Formats.Unity`

| 文件 | 功能 |
|---|---|
| `AssetsToolsBackend.cs` ★ | **AssetsTools.NET 的唯一适配层**：bundle/SerializedFile 读取、对象枚举、容器映射、纹理/Sprite/文本/音频对象读取、字段树与字段编辑、PPtr 依赖与引用者、重打包写回 |
| `UnityAssetService.cs` | 对外门面：编辑器侧的记录模型 + 每次调用新建一次性 backend；**不把 AssetsTools 类型泄漏给上层** |
| `UnityBundleInspector.cs` | UnityFS 头快速校验（签名/格式版本/Unity 版本/声明大小） |
| `UnityClassId.cs` | Unity class id 常量 + class id/名称 → 资产类型映射 |
| `UnityFieldValueParser.cs` | 字段值字符串 ↔ 类型解析（字段编辑的输入校验） |
| `UnityObjectSummary.cs` | Mesh/动画/字体/材质/着色器等对象的摘要卡 |
| `UnitySpriteCrop.cs` | Unity 左下原点 → 图像左上原点的裁剪换算（与纹理行序约定**互为配套**） |

### ★ 要点（`AssetsToolsBackend`）

按主题定位（用 grep 找方法名即可，本文**不再记行号**）：

| 主题 | 关键方法（名字） | 用途 |
|---|---|---|
| 字段树读取 | `ReadObjectFields` / `ReadBundleObjectFields` | 类型树驱动的字段树（enum 折叠、数组 `[i]` 命名、byteArray 不展开） |
| 脚本溯源 | `ReadScriptInfo` / `ReadBundleScriptInfo` | MonoBehaviour 的脚本信息 |
| 字段编辑校验 | `ValidateObjectFieldEdits` / `ValidateBundleObjectFieldEdits` | 不写盘的预校验（未知路径/不可编辑/解析失败/指针目标非法） |
| 引用与依赖 | `ReadObjectReferences` / `ReadBundleObjectReferences` / `ReadObjectDependencies` / `FindReferencers` / `FindBundleReferencers` | PPtr 解析四态（Null/SameFile/ExternalFile/Missing）与反向引用查找 |
| 重打包验证 | `VerifySerializedReferences` / `VerifyBundleReferences` | 修改前后依赖逐项对照（内部新建独立 backend 读快照，避免缓存污染） |
| 勘察 | `BundleSerializedFileNames` / `InspectBundle` / `SurveyBundle` / `TryLoadBundleForCrc` | 枚举对象/容器路径/类型名；CRC 口径与加载器一致的全块解压流 |
| 裸对象 | `ReadSerializedObjects` / `ReadBundleSerializedObject` / `BundleObjectReader` | 对象原始字节 + **类型表索引**；只读会话在同一 bundle 内复用解包和 SerializedFile，结束立即释放 |
| 纹理 | `ReadTexture` / `ReadTexturePixelData` / `ReadStreamData` / `ClearStreamData` | 内联像素优先、否则 `.resS` 流；写回内联后**必须**清 `m_StreamData` |
| Sprite | `ReadSprite` / `ReplaceSpriteMetadata` / `ReplaceBundleSpriteMetadata` | 元数据可读写；**不压平 SpriteAtlas、不改写图集网格** |
| TextAsset | `ReadBundleTextAsset` | `m_Script` 支持 byteArray / string，其他形态 fail fast |
| Sprite 合成 | `ReadBundleSpriteComposite` | `m_RD.textureRect` 优先、缺失回退 `m_Rect` |
| AudioClip | `ReadBundleAudioClipData` / `TryReadStreamingInfo` | 四种负载形态，都不命中则抛中文异常 |
| SerializedFile 写回 | `ReplaceSerializedAsset(s)` / `ReplaceObjectFields` / `ReplaceTextureFromPng` | 临时文件 + 原子替换；纹理格式白名单 3/4/5/10/12/14 |
| Bundle 写回 | `ReplaceBundleObjectFields` / `ReplaceBundleFile` / `ReplaceBundleSerializedAsset(s)` / `ReplaceBundleTextureFromPng` | 全部经写盘核心 `WritePackedBundle` |
| **写盘核心** | `WritePackedBundle` / `WriteSerializedFile` / `MoveWithRetry` | **先未压缩写入（应用替换器）→ 重载 → 按原压缩重打包**；文件占用最多重试 5 次 |
| 容器映射 | `ReadContainerMap` | 读 class 142 `m_Container` → 游戏内资源路径（兼容 Unity 6） |
| 字段编辑实现 | `BuildFieldNode` / `LoadTemplate` / `ValidateFieldEdits` / `ValidatePointerTarget` / `NavigateField` / `ApplyFieldEdits` / `ThrowForUnknownFieldEdits` | 类型树缺失会给中文原因；路径容忍 `Base.` 前缀；未命中路径抛异常 |
| 生命周期 | `Dispose` | 先清缓存再卸载 bundle |

**改动定位提示**

- 「字段编辑报未找到路径」→ `ApplyFieldEdits` / `ThrowForUnknownFieldEdits`，先核对 `Base.` 前缀与 `[i]` 下标。
- 「字段树全 unknown / 不可编辑」→ `LoadTemplate`（类型树是否被剥离）。
- 「纹理替换后游戏仍显示旧图」→ `ClearStreamData` 及其在纹理写回尾部的调用。
- 「bundle 重打包后修改被丢弃」→ `WritePackedBundle`，**不要**改回直接重打包
  （历史严重缺陷，见 `docs/REALDATA-VERIFY.md`）。
- 「导出偶发文件占用」→ `MoveWithRetry`。
- **必须保持的边界**：不要把 AssetsTools 类型暴露给上层（`UnityAssetService` 的存在意义）。

---

## §7 其他格式项目

命名空间前缀：`LimbusModEditor.Formats.*`

### §7.1 `Formats.Carra`（Carra / Carra2 容器）

| 文件 | 功能 |
|---|---|
| `CarraModels.cs` | Carra 数据模型与条目读写（键 = 外层键/内层键/pathId/类型表索引；逐条目 XZ） |
| `CarraArchive.cs` | 探测/读取/写出（zip 容器 + `carra.json` 标记） |
| `CarraFormatHandler.cs` | 接入格式注册表的探测/导入/校验/导出；导出时从上下文取 XZ 编码器 |
| `CarraDiffService.cs` | 与基线包直接比对字节产出变更清单；通过字典选择补丁对象 |
| `XzCodec.cs` | XZ 编解码抽象：解码用 SharpCompress、编码用 Joveler liblzma；缺原生库时回落「不可用」 |

- **Carra2 键口径**：`<缓存外层键(32hex)>/<内层bundle哈希>/<pathId>.<类型表索引>`，逐条目 XZ。
  **类型表索引**（不是 class id）才是键的一部分。
- **XZ 报错排查**：编码器注入点（`CarraFormatHandler` 的导出分支）+ `XzCodec` 的 liblzma 探测与「能否编码」。

### §7.2 `Formats.Bank`（FMOD bank / FSB5）

| 文件 | 功能 |
|---|---|
| `BankModels.cs` | RIFF/FEV/SNDH 索引层解析（**支持事件 bank 的 size 0 SNDH**，加密 bank 拒绝重组）、探测、整体重组装（Bank 侧**唯一**的写回路径） |
| `Fsb5Models.cs` | FSB5 头/块/子样本表解析（含 codec 与**真实样本名**） |
| `FmodCodec.cs` | FMOD 运行库发现与编解码器选择（无 DLL 时明确不可用） |
| `NativeFmodAudioCodec.cs` | P/Invoke 绑定**公开** FMOD/FSBank C ABI：FSB→WAV 预览、WAV→FSB 替换（含 FMOD 2.x headerversion 兼容） |
| `FmodDllInspector.cs` | DLL 导出符号/版本勘察（判定能否解码/编码） |
| `FmodCompatibilityService.cs` | 探测结果缓存与兼容性汇总 |

- **两类 bank 的分流**：解析分支区分事件 bank（空 SNDH）与音频 bank（SNDH → FSB5）；加密 bank 重组时明确拒绝。
- **DLL 候选名单在两处**（`FmodCodecLibrary.TryLoad` 与 `FmodDllInspector.InspectDirectory`）不一致，
  要统一必须**同改两处**。
- **不支持**：加密 bank、未知 FSB 变体 → 明确报错，不尝试解码。

### §7.3 `Formats.Rebank` 与 `Formats.Lunartique`

| 文件 | 功能 |
|---|---|
| `Formats.Rebank/RebankPackage.cs` | `.rebank` 差分音频包：`rebank.json` + `{fsbIndex}/{样本名}.wav`，`base_bank` 为纯文件名 |
| `Formats.Lunartique/LunartiqueArchive.cs` | 安装/卸载配对包读写（`Installation`/`Uninstallation` 双目录，条目为 `__data`） |
| `Formats.Lunartique/LunartiqueFormatHandler.cs` | 格式注册表接入 |

- **`.rebank` 条目名必须是真实样本名**（历史缺陷：写成 `{i}.fsb` 时加载器一条都匹配不上并回滚）。

---

## §8 `src/LimbusModEditor.Application`（服务层，WPF 无关、可单测）

命名空间前缀：`LimbusModEditor.Application.*`

### §8.1 `AppConfig/`（全局配置与环境）

| 文件 | 功能 |
|---|---|
| `AppEnvironment.cs` ★ | 进程级环境单例：程序目录/配置目录/缓存目录/项目目录；有效目录解析（共享 → 项目 → 自动发现）；旧项目值迁移；最近项目；FMOD DLL 发现 |
| `SharedAppConfig.cs` | `config/shared-config.json` 读写（原子写 + 损坏时备份回退） |
| `UiStateService.cs` | `config/ui-state.json`：每页预览列宽（钳制 260–2000）；旧字段双向兼容。`WorkbenchPageKeys` 是**页面 key 的唯一来源**（`assets`/`bank`/`text`/`static`/`presets`，新增页面必须加进 `All`，`UiStateServiceTests` 会断言唯一性） |

- **目录解析优先级**：共享配置（手动值）→ 项目旧字段 → 自动发现。自动配置**只填空位**，永不覆盖手动值。
- **FMOD DLL 发现顺序**：`<程序目录>/fmod` → 程序目录 → 游戏 `LimbusCompany_Data/Plugins/x86_64` → 游戏目录；
  **带编码 DLL 的候选优先返回**。

### §8.2 `Projects/`

| 文件 | 功能 |
|---|---|
| `ProjectService.cs` ★ | `.lmeproj` 加载/保存、schema 版本校验、纯引用资产不落盘（项目瘦身）、打开时从索引后台回灌 |

### §8.3 `Assets/`（资源视图与编辑服务）

| 文件 | 功能 |
|---|---|
| `AssetDisplay.cs` ★ | **显示层口径**：容器条目路径、叶子名消歧、中文类型/状态标签、自然名称比较 |
| `AssetSearchService.cs` | **判据与排序的唯一出处**（`Matches` / `BuildSortKey` / `CompareSortKeys`）+ 全量搜索（吃 `IEnumerable<AssetRecord>` 的旧路径，导出/构建与测试仍用）。`Matches` 末条判据 = 默认隐藏静态数据表（`AssetSearchQuery.ShowStaticTables` 为假时按 `AssetStaticClassifier` 判，2026-09-15 恢复，见 `CODE-STRUCTURE` §6-20） |
| `AssetTreeBuilder.cs` | 按显示路径逐段**惰性**建树。**已不是资源页的生产路径**（改为 `Catalog/AssetCatalogTree.cs`）；保留作分组语义的参考实现 + `AssetTreeNode.DistinguishLeaves` 供两边共用 |
| `AssetPropertyService.cs` | 选中资源的属性行（中文标签；单项失败降级，不整体失败） |
| `AssetEditService.cs` | 替换/批量替换/撤销、暂存到项目 `edits/assets/`、`HasEdits` 口径；`ReadCurrentBytesAsync` **拒绝把 AssetBundle 容器当正文读** |
| `TextAssetEditService.cs` | 文本资源（TextAsset/.json）编辑，保存注册为普通可逆替换；`CanEditText` 只放行松散文件，二进制/超长正文（>40 万字符）拒绝打开 |
| `TextPreviewService.cs` | 只读文本预览：编码识别（UTF-8/UTF-16）、二进制与非 UTF 拒绝、超长截断 |
| `SpriteMetadataEditService.cs` | Sprite 元数据（rect/pivot/border/ppu）编辑记录 |
| `ImageAtlasEditService.cs` | 纹理 → 图集拆分落盘 + 回填（`layout.json` + 区域 PNG） |
| `UnityFieldEditService.cs` | Unity 字段编辑的读写与校验（写进资产元数据） |
| `ModImportService.cs` | 导入包/目录/bundle/zip 到项目（含 catalog 基线判定与来源登记） |
| `BankTreeRules.cs` | bank 树纯规则（默认隐藏事件 bank、单 FSB 自动展开） |
| `BankAudioService.cs` | FSB 样本试听/替换（临时 WAV + 写回） |
| `BankDirectoryService.cs` | bank 目录解析/分类、RIFF 块与 FSB 表读取 |
| `BankIndexService.cs` | bank 索引刷新（并行度 ≤4、进度、可取消、按 `(size,mtime)` 增量） |
| `BankIndexStore.cs` | `cache/bank-index.db` 读写 + 删库/清表 |
| `BankIndexModels.cs` | bank 索引的数据模型与源签名口径 |

### §8.4 `Assets/Preview/`（预览管线）

| 文件 | 功能 |
|---|---|
| `AssetPreview.cs` ★ | 预览服务定位：按资产类型选择 provider，未知类型兜底十六进制；预览产物模型 |
| `AssetPreviewProviders.cs` | 各形态预览实现（图像 / Sprite 合成 / 音频 / 文本 / 脚本字段 / 摘要 / 材质 / 着色器 / 视频 / 图集 / 十六进制）。**预览规模硬闸门**在这里：文本预览有字符上限，静态数据表只给「摘要 + 前若干行」并指路静态数据工作台 |
| `HexDumpService.cs` | 十六进制转储（截断 + ASCII 边栏）与波形包络计算（只接 16-bit PCM WAV） |

- **预览是 UI 线程的成控点**：内容多长，UI 就要建多少控件。任何「按内容规模无上限」的新预览形态
  都必须先在 provider 侧截断（参考文本预览的字符上限），否则会重现「点一下预览就未响应」。

### §8.5 `Scanning/`（扫描与索引）

| 文件 | 功能 |
|---|---|
| `StartupScanService.cs` ★ | **启动扫描总编排**：五库建库校表 → 游戏资源 → 音频 → 静态表 → 文本 → **关联图（派生，六步）**（逐步容错、进度、报告）；缓存清单与诊断的唯一来源。`ScanWorkbenchIndexesAsync` 可只跑后四步（bank/static/text/relations）而不扫资源 |
| `UnityCacheScanService.cs` ★ | Unity 缓存**引用模式**增量扫描（不复制文件）；容器条目与 catalog 基线写进索引；**静态标记的补写/清除规则在这里**（见要点） |
| `UnityCacheSqliteIndexStore.cs` | v2 规范化资源库：整数 bundle 主键、共享字符串表、对象聚簇主键、有名称资源的覆盖索引；同一快照按 bundle 流式读取；事务更新/取消回滚；旧版本缓存整库重建 |
| `UnityCacheMaterializationService.cs` | 编辑过的引用资源所属 bundle 实体化到项目 `sources/cache/<外>_<内>.bundle` |

- **静态标记的四条规矩**（`UnityCacheScanService`）：① catalog 可用且内层键命中 → 写**位1**；
  ② **catalog 不可用时没有资格判定「不是静态」** → 不得清除已有位（只影响位1）；
  ③ bundle 名兜底是**位2**（`StaticBundleLocator.LooksLikeStaticBundle`）；
  ④ 容器路径前缀是**位4**（`LooksLikeStaticTablePath`）—— 游戏更新换键后缓存里会留着
  旧版本静态 bundle（裸哈希目录名，前两道都认不出），只有这条不依赖 catalog 的路径事实认得出。
  **2026-09-15 起**：这三位合成一个整数 `assets.static_kind`（`AssetStaticClassifier` 是唯一出处），
  **扫描期算完落库**，查询期只做一次 `static_kind = 0` 的索引比较 —— 资源列表默认隐藏静态表
  由它判，预览通道（`AssetPreviewProviders.IsStaticTableAsset`）读同一份结论。违反 ② 的现象
  就是「资源工作台又列出 static-data」，或者更隐蔽的「时灵时不灵」（catalog 换键后结论没刷新）。

### §8.6 `Caching/`（表缓存底座）

| 文件 | 功能 |
|---|---|
| `WorkbenchCachePaths.cs` | 工作台缓存库路径集中定义（unity 索引库 + bank/static/text 三库 + **relation 派生库**） |
| `WorkbenchCacheSchema.cs` ★ | 各库表结构唯一定义（`index_meta` 共同契约 + bank/static/text 业务表 + relation 的 `subjects`/`links`/`subjects_by_ref`） |
| `SqliteTableCache.cs` | SQLite 通用底座：事务/WAL/源签名比对即整库重建/加列轻量迁移/缺表自愈/损坏删库重建 |
| `CacheSignature.cs` | 源签名 `"length:mtimeTicks"` 口径（目录签名长度恒 0） |

- **共同契约**：`index_meta(source_key, signature, language_prefix)`；只存 **vanilla 事实**，
  编辑集绝不落缓存；失效只有「源签名变化」一条；加列走轻量迁移，不要改已发布的建表脚本。

### §8.7 `Texts/`（lang 文本工作台）

| 文件 | 功能 |
|---|---|
| `LangTextWorkbenchService.cs` ★ | lang 根与活动语言目录解析、文件枚举/搜索、读取与编辑集、条目路径 → 补丁键口径换算 |
| `LangEditSession.cs` ★ | **宿主持有**的文本编辑集会话（修订号、真实改动判定、快照） |
| `TextIndexStore.cs` ★ | `cache/text-index.db` 读写；条目口径 = 相对活动语言目录，`index_meta.language_prefix` 拼回磁盘路径；有口径版本常量 |
| `LangTextTreeBuilder.cs` | 文件清单 → 键值/目录树（惰性；UI 不渲染根节点） |
| `LangTextDisplay.cs` | 键路径/文本的显示与截断纯映射 |
| `LangTextPatchService.cs` | 补丁文档读写、目录差分、应用到 lang 目录 |
| `LangExportFormatter.cs` ★ | 三套语言格式（`bus` / `patch` / `pathset`）生成 + **可表达性门**（表达不了整份不产出） |
| `TextDiffService.cs` | RFC6902 差异生成/应用（与加载器侧兼容；含 JSON null 语义修正） |
| `StaticEditSession.cs` | 静态数据表编辑集会话（与文本编辑集同构） |

### §8.8 `StaticMods/`（静态数据表）

| 文件 | 功能 |
|---|---|
| `StaticIndexService.cs` | 静态 bundle 定位 + 表索引构建（只取元数据、逐张丢弃正文） |
| `StaticIndexModels.cs` | 静态索引模型与源签名（用内层内容哈希，**不用外层键**：热修会重分配外层键） |
| `StaticTableIndexStore.cs` | `cache/static-tables.db`（元数据 + **按需有界**正文缓存，LRU 淘汰） |
| `StaticBundleLocator.cs` ★ | 由 catalog 动态解析静态 bundle（内层/外层键，**不硬编码**）、读静态表正文；`LooksLikeStaticBundle` 是不依赖 catalog 的 bundle 名兜底判定，`LooksLikeStaticTablePath` 是容器路径判据（认得缓存里的旧版本静态 bundle） |
| `StaticModService.cs` | staticmod 包读取/写出/补丁预览/从 JSON 差分生成（**只支持 `jsonpatch`**，遇到整文件替换条目明确报错） |

### §8.9 `Build/`（导出与构建流水线）

| 文件 | 功能 |
|---|---|
| `ModExportPlanService.cs` ★ | 分析项目实际修改 → 点亮哪些槽位（导出计划 + 逐槽位跳过原因） |
| `ModPackExportService.cs` ★ | 按槽位执行导出（bank/rebank/carra/lunartique/lang×3/staticmod）；音频语义与调试共用；**槽位之间检查取消** |
| `ThrottledProgress.cs` | 进度上报节流装饰器（首条与末条一定放行）：逐资源/逐对象上报可达十万级，未节流会把 WPF 消息队列淹掉 |
| `ExportAdvisor.cs` | 给用户的出口建议文案（含中文理由与前置条件） |
| `ExportMatrix.cs` | 资产 × 格式的兼容性矩阵（向导启用/禁用目标） |
| `UnityBundleBuildService.cs` ★ | 把替换/字段编辑/Sprite 元数据应用到 bundle 并重打包（含引用完整性验证）；候选谓词**编辑标记先行 + `.ToArray()` 物化**（见 CODE-STRUCTURE §6-25，改回去会让导出慢 40 秒级） |
| `UnitySerializedFileBuildService.cs` | 独立 `.assets` 文件版本 |
| `UnityCacheExportService.cs` ★ | 缓存 bundle → Carra2 一键导出；按源 bundle 分组共享对象读取会话，递增统计进度；中间产物落在项目 `builds/unity-bundles/` |
| `LunartiqueCarraConversionService.cs` | 由「改后 bundle + 缓存原版 bundle」派生 Lunartique 两侧条目（缺缓存整份跳过并说明） |
| `ModExportService.cs` | 旧的多格式导出通道（CLI 与若干测试仍用；界面不再暴露，**别顺手删**） |
| `ProjectBuildService.cs` | 项目级构建（按格式分发 + 导出前校验 + 旧式 overlay） |
| `NewModTemplateService.cs` | 新建模组脚手架（默认项目目录 = `<程序目录>/projects/<名>/`） |
| `AtomicOutput.cs` | 原子写盘（临时文件 + 原子替换）——**所有产物落盘统一走它** |

### ★ 导出链的线程与取消口径（2026-09-12 修复后固定下来）

- **`await` 一个「同步完成」的 Task 会原地继续执行，不切线程。** 因此声明 `async Task`
  但方法体里没有真正异步点的服务，其重活会落在**调用线程**上 —— 从 WPF 调就是 UI 线程，
  直接表现为「模态窗口弹出后软件未响应」。
- **规矩**：导出链上任何 CPU/IO 密集的同步主体，必须自己用 `Task.Run` 切到线程池
  （`UnityBundleBuildService.BuildAsync` 是范例），或由调用方整体包 `Task.Run`
  （`MainWindow.ExportMod_Click` / `PrepareExportPlanAsync` 是范例）。两者都做是刻意的双保险。
- **取消**：`ExportProgressWindow` 提供 `Token`；写盘走 `AtomicOutput`（临时文件 + 原子替换），
  取消只停在检查点，不会留下半成品；临时中间文件在 `finally` 清理。
- **中间产物复用**：Carra 与 Lunartique 两个槽位共用「改后对象」这一份产物，
  同一目标路径在一次导出内复用（`UnityCacheExportService` 的复用缓存），不要重跑整条流水线。

### §8.10 `Catalog/`（资源目录查询门面 + 官方 catalog 只读解析）

| 文件 | 功能 |
|---|---|
| `AssetCatalog.cs` ★ | **资源目录的按页查询门面**（无 WPF 依赖）：命中集 = 索引下推名次 ∪ 项目态补充集；`Page` / `Count` / `Locate` / `IndexIn` / `Resolve` / `ResolveEntries` / `MatchOrder`；判据与排序都复用 `AssetSearchService` 那一份 |
| `AssetCatalogTree.cs` ★ | 目录树的**按区间分层**数据源：节点只记命中序列的 `[Start, End)`，展开才物化那一层。显示路径 / 重名消歧与 `AssetTreeBuilder` 共用同一批函数 |
| `CatalogFileService.cs` | catalog 只读解析：一次线性扫描定位全部 Hash128，复用名称偏移；保留首次命中、双布局 CRC/size 自校准 |
| `CatalogBaselineService.cs` | vanilla / 修改过 / 不在 catalog / 未知 四态判定（供导入与扫描写基线） |

### §8.11 `Debugging/`（目录定位、启动、调试应用）

| 文件 | 功能 |
|---|---|
| `GameDirectoryLocator.cs` | 扫 Steam 库等候选根定位游戏目录 |
| `UnityCacheLocator.cs` | 定位 Unity 缓存根（按缓存条目数择优） |
| `ModDirectoryLocator.cs` | 定位 `%APPDATA%\LimbusCompanyMods`（含 `_disable` 约定） |
| `ProjectAutoConfigureService.cs` | 项目级自动配置报告 |
| `ModInstallService.cs` | 已安装模组枚举/安装 |
| `GameLaunchService.cs` | 启动游戏（含可执行文件校验、运行中判定） |
| `ModApplyService.cs` ★ | **调试应用**：按槽位语义铺到游戏目录/缓存（逐文件备份 + 步骤清单 + 关闭还原 + 冲突不覆盖 + 中途失败回滚） |
| `StaticModApplyService.cs` | 静态模组调试应用（catalog 双写，写前用 size 合理性自校准布局） |
| `DebugApplyService.cs` | 旧的覆盖层调试应用（备份/还原基础设施） |

### §8.12 `Documents/` 与 `Formats/`

| 文件 | 功能 |
|---|---|
| `Documents/JsonDocumentEditor.cs` | JSON 文档的键值行模型 + 校验/写回（键值树就地编辑与静态表编辑共用；不截断大文档） |
| `Formats/FormatRegistry.cs` | 多处理器探测择优 + 按格式解析处理器 |
| `Formats/BuiltInFormatRegistry.cs` | 内置处理器装配（Bank/Carra/Rebank/Lunartique 等） |

### §8.13 `Diagnostics/`（日志装配）

| 文件 | 功能 |
|---|---|
| `Diagnostics/NLogBootstrap.cs` ★ | NLog 装配（宿主在**最早时机**调一次）：日志目录判定（`LME_LOG_DIR` → `<程序目录>/logs` → `%TEMP%/LimbusModEditor-logs`，逐级探测可写）、`nlog.config` 加载（缺失时从内嵌副本还原到程序目录）、`LME_LOG_LEVEL`/`LME_LOG_OFF`/`LME_LOG_CONSOLE` 环境变量覆盖、会话头部（版本/运行时/进程/目录/级别）、`RecentLines/RecentText`（读 `memory` target 的内存快照）、`Flush/Shutdown`。**任何失败都降级为内存日志，绝不阻断宿主** |

### §8.14 `Relations/`（跨资源关联：派生索引 + 查询门面 + 卡片流展示层）

> 一句话：**游戏里的资源是按「预设对象 id」互相关联的**——lang 里的语音文件名、static-data 里的
> 数值表、bank 里的语音样本名、Unity 里的立绘/技能图标/CG/Spine/视频路径，都带同一个 id。
> 本目录把这张关系图**从四个索引库派生**成 `cache/relation-index.db`（**契约 v3，6 个类别**），
> 供资源预览的「关联资源」板块、卡片流页面，以及跨工作台的**精确跳转**查询。
>
> **六个类别**（`RelationCategories`，顺序即 UI 切换顺序）：人格 `persona` / 敌人单位 `enemy` /
> 异想体 `abnormality` / 播报员 `announcer` / E.G.O 装备 `ego` / E.G.O 饰品 `ego_gift`。
> 关联键统一编码成 `<category>:<key>`（`SubjectIds.Make/CategoryOf/KeyOf`）——**因为不同类别的 id 会真的撞车**
> （实测敌人段含 `90005`，播报员段从 `90001` 起，异常与敌人的 4 位段相邻）。

| 文件 | 功能 |
|---|---|
| `RelationCategories.cs` ★ | 6 个类别的常量、中文标签（`Label`）、`SubjectIds` 编解码。**类别一律由资源目录前缀判定**，**绝不复用「扫 5 位数字窗口」的通用匹配**（人格固定 5 位，但敌人变长 4–5 位，4 位值会与事件/异常 id 撞） |
| `RelationModels.cs` ★ | 关联图模型：`RelationKind`（文本/静态数据/音频/图像/视频/Spine/动画/Prefab/网格/其它）、`RelationSubject`（预设对象，带「能预览、能跳转」的载荷列 `preview_text`/`link_count`）、`RelationLink`（对象→资源，带 `preview_text`/`preview_kind`/`media_kind`/`duration_sec`/`ref_path`/`deep_link`/`target_subject_id`）、`RelationXref`（**显式跨资源边，多对多**）、`RelationInputs`（四份事实输入）、`RelationGraph`（含 `LinksOf`）、`RelationIndexSource`（**派生源签名 = `FormatVersion` + 四个上游源签名拼接**，当前 `FormatVersion = "v4"`，v3→v4 扩展 `IsSpinePath` 覆盖 `StorySpine_*`）。另含 `RelationXrefKinds`（跨链关系名，落库 → 改名要 +1）、`RelationAnchorTables`（来源表锚点）、`RelationEntityKeys`（id 归一化，E.G.O 饰品 `id % 10000`） |
| `SubjectRelationAnalyzer.cs` ★ | 纯函数分析器：四份事实 → 关联图（6 类别 + 跨资源 `xref` 边）。**角色名拼写归一**（游戏文件名里的 `Heathclif`/`Meursalut`/`Ishmeal` 合并到规范拼写，否则同角色被拆成多组）；lang 无 5 位 id 命中时回落到 `AbDlg_<角色>.json` 的**角色归属**规则；**E.G.O 只认 lang 的 `Egos.json`**（资源侧 20xxx 数字会造「幽灵 EGO」）；E.G.O 饰品 id 归一化到 4 位基准 |
| `RelationDisplayRules.cs` | 展示层纯规则：`KindLabel`（关联类别中文名）、`PortraitRank`（立绘排序）、`IsSpinePath`（Spine 路径粗筛） |
| `LangAnchorReader.cs` | 读 lang 文件、抽「键 → 文本」锚点（不依赖 Unity 读取管线） |
| `RelationDeepLink.cs` ★ | **精确跳转载荷编解码**（`'\0'` 分隔段）：`ForAudio`（bank 路径 + 样本名）/ `ForText`（相对路径 + 键路径）/ `ForStatic`（容器路径 + 记录键）/ `ForAsset`（只有容器路径）；`Part`/`Decode` 拆段、`KeywordOf` 取**最后一段**当兜底搜索关键词。载荷是**纯数据**，不参与判重、不影响关联图正确性 —— **升级它不需要动 `FormatVersion`** |
| `PersonaRelationIndexService.cs` ★ | 编排：算四个上游源签名 → 决定是否重建 → 读四份事实（**只读有容器路径的 Unity 行**，1.27M 行里只有约 5.1 万行有容器路径）→ 分析 → 落库。**失败隔离**：任一上游缺失就少一份输入，不抛异常 |
| `RelationStore.cs` ★ | `cache/relation-index.db` 读写（`SqliteTableCache` 底座）：`subjects` / `links` / `subjects_by_ref`（**反向索引**：资源定位键 → 对象）/ `xref`（**显式跨资源边，多对多**，一个键可挂多条其它数据）+ `index_meta`；`EnsureSource`/`PersistGraph`（单事务）/`ReadSubjects`/`ReadLinks`/`ReadSubjectIdsByRef`/`ReadXrefs` |
| `RelationQueryService.cs` ★ | 查询门面（UI 直接用，**绝不抛异常**）：资源→对象（`DescribeSubjectsForAsset`，走反向索引 O(1)）/ 对象→资源（`Links`/`CountByKind`）；库没建好返回空 + 中文原因 |
| `PresetWorkbenchService.cs` ★ | 卡片流 / 详情的**展示层**服务（纯逻辑、无 IO）：`Categories()`（**类别切换器数据源**：`PresetCategory` = 类别 + 中文名 + 计数）、`BuildCards`（封面挑选：`PortraitRank` 升序 + **能渲染的胜过不能渲染的** + 定位键稳定排序；卡片带一句话 `PreviewText` 与强度 `RelationPreviewKind`）、`BuildDetail`（分组按用户视角排序：静态数据→文本→音频→图像→视频→Spine→动画→Prefab→网格→其它；每行带 `PreviewText`/`PreviewKind`/`MediaKind`/`DurationSec`/`DeepLink`）、`TargetFor`（每行「打开」跳哪页 / 用什么载荷）。**放在 Application 而不是页面**：App 层没有测试工程，而封面挑选与分组顺序最容易写错 |
| `PersonaPresetService.cs`（**已删除**） | 旧版只支持人格；重构为上面的 `PresetWorkbenchService`（6 类别 + 预览载荷）——该文件已不存在，不要按旧名找 |

- **缓存铁律同样适用**：派生库只是加速，损坏就删库重建；源变没变由**四个上游的语义签名**判定，
  **不拿库文件 mtime**（WAL 下写事务未必改主库 mtime，会漏判）。
- **改动分析器产出的「图内容」时必须把 `RelationIndexSource.FormatVersion` +1**：包括「抽哪些事实 / 怎么算关联 /
  类别 id 归一化 / 新增列并填充 / 跨链关系名改名」。否则旧派生库会被当成新鲜的。
  仅改 UI 侧**如何使用**现有列、或 SQLite 加列本身（`WorkbenchCacheSchema` 轻量迁移）**不**需要 +1 —— 两者是两件事。

### §8.15 `Spine/`（Spine 文本解析 / 静态预览 / 导出 / 找动画素材）— **2026-09-15 起瘦身（W2）**

> 一句话：**解析与渲染分离**。游戏里的 Spine 数据就是可读文本
> （骨架 `<name>.json`、图集 `<name>.atlas.txt`、页贴图 `.png`），本目录自己解析这三样，
> 给出「结构 + 图集布局」预览，并把三件套**导出**给外部 Spine 工具查看。
>
> **⚠️ 已定案瘦身**（`docs/ARCH-WEBVIEW2-VUE.md` §7）：解析（`SpineModels.cs`）与「结构 + 图集布局」
> 预览（`SpinePreviewService.cs` 的 `Build`/`DrawRegionBoxes`）**前移前端**（官方 spine-ts 运行时），
> native 预览 provider（`SpinePreviewProvider.cs`）退役；**保留**：
> ① `SpineAnimationSourceService.cs`——「定位并流式喂原始字节」（骨架 JSON + atlas 文本 + 页名→PNG 字节）；
> ② `SpineExportService.cs`——三件套写盘（**导出目标清单由前端决定、写盘由 C# 执行**）；
> ③ `SpineSiblingIndex.cs`（新文件，从 `SpinePreviewService.cs` 迁出的纯定位逻辑）。
> 路径判据 `RelationDisplayRules.IsSpinePath` 不动（前端经 IPC 使用，不复制）。
> 下方为**瘦身前**的现状记录。
>
> **骨骼动画真播放另有一环**：渲染由独立叶子项目 `LimbusModEditor.SpineRuntime`
> （vendored spine-csharp 4.0 + SkiaSharp，见 §5.2）承担，本目录的 `SpineAnimationSourceService`
> 负责「找齐素材」，App 的 `SpineAnimationPreviewWindow` 负责「驱动时间 + 贴帧」。

| 文件 | 功能 |
|---|---|
| `SpineModels.cs` ★ | 数据模型 + `SpineTextParser`（**纯函数、无 IO**）：`SpineBone`/`SpineSlot`/`SpineRegionAttachment`/`SpineKey`/`SpineAnimation`/`SpineSkeleton`/`SpineAtlas(Page/Region)`/`SpineParseResult`。骨架按 **Spine 4.0 口径**解析：`skins` 是数组（3.8 是对象，两种都认）、动画按 `bones`/`slots` 分组、**第一条关键帧没有 `time` 即 0**。关键帧是「按目标类型取用字段」的联合记录：骨骼时间线用 `rotate`/`translate`/`scale` 按时间合并（旋转/缩放落在单值位），槽位时间线用 `attachment`（换装 / 显隐）+ `rgba`（只取 alpha，淡入淡出）。**只收 `region` 附件**，网格 / 包围盒 / 路径 / 裁剪 / 点附件计入 `UnsupportedAttachmentCount` 并跳过。图集支持多页 + `scale:` / `filter:` / `pma:` / `repeat:` 头 + `bounds`/`xy`/`orig`/`offsets`/`rotate:90`。结构不符返回 `SpineParseResult.None`（**不猜格式**，交回普通文本预览）；容忍 BOM；有病态文件闸门（动画数 / 单时间线键数 / 附件数） | **W2 删除**（解析前移前端 spine-ts） |
| `SpinePreviewService.cs` ★ | 预览构建 + `SpineSiblingIndex`：**Spine 三件套靠「容器路径的目录」定位**（真实数据 `Assets/Resources_moved/Story/CG/Ep9_3/StorySpine_Sinclair/` 下的 `cg_40.json` / `cg_40.atlas.txt` / `cg_40.png`），因此只索引**带容器路径**的资源（127 万行里约 5.1 万行），按目录一次建表 O(1) 取。索引缓存三道失效条件（引用相等 + 数量变化 + 15 s 超时，因为资源集合会被就地增删）。`Build`：骨架资源找同目录 `.atlas.txt` 与页贴图 → 结构行 + 布局叠加图；`DrawRegionBoxes`（ImageSharp：**长边超 `MaxLayoutDimension=1024` 等比缩小**，区域内半透明蓝填充 + 红框，区域外一个像素都不动）。`LooksLikeSpineText` 只按路径 / 类型粗筛（不读盘） | **W2 删除**（布局/渲染前移前端）；**其中 `SpineSiblingIndex` 保留并迁为独立文件 `SpineSiblingIndex.cs`** |
| `SpinePreviewProvider.cs` | `IAssetPreviewProvider`（`Name = "Spine 预览"`）：`CanPreview` = 路径粗筛；`PreviewAsync` 交给 `SpinePreviewService.Build`，**不像 Spine 就返回 null**，交回后面的文本预览 provider | **W2 删除**（前端 spine-ts 直接渲染，不再需要后端出图）；`AssetPreview.CreateDefault` 的注册同步移除 |
| `SpineExportService.cs` | 把三件套导出到 `<目标>/<目录名>/`（目录名取容器路径的末级目录，取不到用 `spine`）；**复用 `SpinePreviewService` 的同目录索引**，不重复扫。**刻意不是 `ExportSlot`**：模组导出只装「被修改过的资源」，把「给外部工具查看」混进去会破坏这个语义，因此做成独立动作。绝不抛异常 | **保留**（写盘由 C# 执行）；构造函数改依赖 `SpineSiblingIndex`；导出目标清单由前端决定（ADR §7） |
| `SpineAnimationSourceService.cs` ★ | **动画预览的素材解析**：从项目资源里按**容器目录**找齐「骨架 JSON（排除 `*.atlas.json`）+ `.atlas.txt` + 同名页贴图 PNG」，产出 `SpineAnimationSource`（骨架/图集文本 + `PageBytes(页名)` 回调 + 可用页清单）。用户从骨架/图集/贴图任一个点进来结果都一致（一律以所在目录为范围）。**失败返回中文原因，绝不抛**（Spine 是旁路预览，缺件只是「这次看不了」）；页贴图解码按 Sprite/Texture 分别走 `ReadBundleSpriteComposite` / `ReadTexturePng` | **保留**（「定位并流式喂原始字节」能力）；构造函数改依赖 `SpineSiblingIndex` |
| `SpineSiblingIndex.cs`（**W2 新增**） | 纯定位：从 `SpinePreviewService.cs` 迁出的 `FolderOf`/`FileNameOf`/`Siblings`（按容器目录建表 O(1) 取；无解析、无渲染；保留原索引缓存语义：引用相等 + 数量变化 + 15 s 超时） | **W2 新增**（承接保留能力） |

- **真实数据口径（2026-09-12 实测）**：骨架 `spine: 4.0.64`；图集页头 `scale:0.333`；
  关系图里 Spine 类 122 条、动画类 25 条。
- **新增判定规则不要只改 `LooksLikeSpineText`**：真正的「是不是 Spine」由 `SpineTextParser` 按内容判定，
  路径粗筛只是省一次读盘的门。（W2 后：内容判定在前端 spine-ts；路径判据 `RelationDisplayRules.IsSpinePath` 保留在 C#。）
- **动画播放链路（2026-09-13 接入，W2 起改为前端播放）**：`SpineAnimationSourceService.Resolve` 找素材 →
  `SpineDocument.TryCreate` 建文档（含动画清单/时长）→ `SpineFrameRenderer.RenderFrame(time, 440, 600)`
  出 PNG → App 贴到 `Image`。任一环失败都由该环给出中文原因，**不出现白图**。
  （W2 后：前端 spine-ts 加载 `spine.locate` 返回的字节 URL 就地播放；C# 侧不再出帧。）

### §8.16 `SpineData/`（Spine 原始字节提取网关，W2 新增）

> 一句话：**C# 侧只负责定位、读取与纹理预解码，前端负责解析与 WebGL 渲染**。
> 替换 `SpineAnimationSourceService` 的解析职责（解析前移前端 spine-ts），
> 保留 ADR §7 的「定位并流式喂原始字节」能力。无 WPF 引用（铁律 §3-10），可单测。

| 文件 | 功能 |
|---|---|
| `SpineRawData.cs` | 载荷模型：`SkeletonBytes`（UTF-8 JSON 或二进制）+ `SkeletonFormat`（`json`/`binary`）+ `AtlasText` + `PageBytes`（页名→PNG 字节）+ `AvailablePages` + `Label` |
| `ISpineDataGateway.cs` | 网关接口：`GetSpineDataAsync`（按资产 ID）/ `GetSpineDataByPathAsync`（按容器路径）/ `GetSpineDataBatchAsync`（批量预取）；**绝不抛异常，失败返回中文原因** |
| `SpineDataGateway.cs` | 网关实现：从 `unity-cache-index.db` 定位同目录资源 → 读骨架/图集字节 → 纹理预解码为 PNG（DXT 解码 / 直通格式透传）；含 `SpineDataGatewayFactory` 静态工厂（默认索引库路径） |
| `SpinePathRules.cs` | 路径判据纯函数：`IsSpinePath` / `IsSkeletonJsonFileName` / `IsAtlasFileName` / `FolderOf` / `FileNameOf`；`TextureFormatRules`（DXT/直通格式判定 + 中文格式名） |
| `SpineAssetLocator.cs` | 索引定位：`FindSiblings(containerEntry)` 从 `unity-cache-index.db` 按目录查同目录资源（`container_entry LIKE` + 内存过滤同目录） |

- **✅ 已修正（2026-09-16，spine-engineer t24）**：`SpinePathRules.IsSpinePath` 删除全子串匹配，改为**委托 `RelationDisplayRules.IsSpinePath`（单一判据来源）**；`RelationDisplayRules.IsSpinePath` 扩展显式目录模式 `Contains("StorySpine")` 覆盖 `StorySpine_*`（如 `StorySpine_Sinclair`，修正既有 4 条判据盲区）；按不变量 §6-33 `RelationIndexSource.FormatVersion` v3 → **v4**（测试 `Relation_format_version_is_v4` 已更新）。
- **真实数据验证（spine-engineer t24）**：cg_40 完整三件套读取成功——骨架 202,276 bytes（`spine:4.0.64`）、图集 2,057 chars（含 `pma:true`）、纹理 5 页（1 RGBA32 + 4 DXT5）全部解码为有效 PNG。

---

## §9 `src/LimbusModEditor.App`（WPF 界面层）

> 本层**没有测试工程**：任何值得测的逻辑都要下沉到 `Application`。

### §9.1 外壳与主题

| 文件 | 功能 |
|---|---|
| `App.xaml` / `App.xaml.cs` | 应用启动**与日志装配入口**（构造函数里 `LogHost.Initialize`，早于主窗与任何磁盘动作）、`OnExit` 冲刷日志、全局异常与主题初始化 |
| `MainWindow.xaml` ★ | 三列布局：活动栏 7 入口（资源 / 音频 / 文本 / 静态数据 / **预设卡片流** / 教程 / 设置）、共享侧边栏（① 获取资源 / ② 产出模组 / 更多）、页面宿主、无项目遮罩 |
| `MainWindow.xaml.cs` ★ | 页面注册表/切换、启动编排、项目打开保存、扫描、导出、调试、目录状态、提示条；宿主契约实现 |
| `Logging/LogHost.cs` ★ | 日志装配 + **报错收集**：`DispatcherUnhandledException`/`AppDomain.UnhandledException`/`TaskScheduler.UnobservedTaskException` 三个钩子 + 崩溃快照 `logs/crash-<时间>.log`（异常 + 最近 400 条日志）；`DumpRecentTo` 导出最近日志。**默认不设 `Handled`**（保持改动前的崩溃行为，只是现在一定留证据） |
| `Logging/UiHeartbeat.cs` ★ | UI 线程心跳：每秒 1 跳，**阻塞 ≥2 s 立刻记 Warn**（定位「界面未响应」：最后一条心跳之后只剩工作线程日志即为卡死点） |
| `AppTheme.cs` | 主题/画刷装配辅助 |
| `StartupTrace.cs` | 启动阶段埋点（排查「窗口出现太慢」） |
| `AssemblyInfo.cs` | 程序集属性 |
| `HumanSizeConverter.cs` | 字节数 → 人类可读（列表大小列） |
| `AssetRow.cs` | 资源列表行视图模型（名称/类型标签/状态/大小/显示路径） |
| `Themes/Theme.xaml` | 设计令牌：主题色与基础样式 |
| `Themes/WorkbenchStyles.xaml` | 工作台共享样式与画刷（树模板、筛选下拉、行 hover/selected 等） |

### ★ 要点（`MainWindow`）

| 关注点 | 说明 |
|---|---|
| 页面注册表与切换 | 页面按 key 惰性创建并**常驻**（保住搜索/选中/预览状态）；切页只换宿主内容 |
| 启动编排 | 构造函数只建页面；所有秒级磁盘动作排到首次呈现之后（`Loaded` + Background 优先级） |
| **导出/调试共用前奏** | `PrepareExportPlanAsync`：实体化已编辑资产（UI 线程）→ 存项目（后台）→ 建导出计划与上下文（后台）；**两条链路必须同一份分析与上下文** |
| 导出与调试 | 都在后台线程读；`ExportProgressWindow` 提供取消令牌；主窗在导出期间禁用输入 |
| 目录状态 | 目录定位器有**进程级记忆化**：改设置后必须失效，否则状态栏显示旧值 |
| 宿主契约实现 | 项目/项目文件/环境/两个编辑集会话/状态/刷新/保存/跳页 |

- **改界面文案/布局**：先看 `MainWindow.xaml`（活动栏、侧边栏），再按页面 key 找 `WorkbenchPages/` 下对应页面。
- **不要把页面逻辑写回 MainWindow**：新增页面 = `CreatePage` 加分支 + 活动栏按钮 + 活动栏选中态映射。

### §9.2 `WorkbenchPages/`（页面与共享骨架）

| 文件 | 功能 |
|---|---|
| `IWorkbenchHost.cs` ★ | 页面宿主契约：页面能拿到的东西全在这里，越界即设计问题；含 `ShowWorkbenchSearch(pageKey, keyword)`（跨页跳转 + 自动过滤）与 `RevealReference(pageKey, payload, fallbackKeyword)`（**精确跳转**：目标页实现 `IReferenceRevealable` 就按载荷定位并选中，否则退化为关键词过滤） |
| `ISearchableWorkbench.cs` | 页面可被跨页搜索跳转的契约（`ApplySearchKeyword`）：资源 / 音频 / 文本 / 静态数据四页实现，卡片流点「打开」靠它把关键词送进目标页 |
| `IReferenceRevealable.cs` ★ | **精确跳转**契约（`bool Reveal(payload)`）：载荷是 `RelationDeepLink` 口径的 `'\0'` 分隔段（「哪个文件的哪个键」「哪张表的哪条记录」「哪个 bank 的哪个样本」）。定位不到返回 false（宿主退化到关键词），**实现者不得抛异常**（关联图是旁路）。资源 / 音频 / 文本 / 静态数据四页实现 |
| `WorkbenchShell.xaml(.cs)` | 三列页面骨架：列宽持久化、列表↔树切换、搜索/筛选/状态/空态构件工厂、滚轮滚动接线 |
| `TreeExpansionState.cs` ★ | **树展开态保持**：重建前按稳定 key 收下已展开节点，重建后逐层物化并回放；四个工作台的树共用 |
| `JsonTreeEditor.xaml(.cs)` | 键值树编辑器：树/原文双 Tab、**行内就地改值**、右键菜单与删除键、RFC6902 差异摘要 |
| `AssetsWorkbenchPage.xaml(.cs)` ★ | 资源工作台：筛选栏 + 列表/容器树 + 预览列 + 属性区 + **关联资源区**（反查「这个资源属于哪些对象」，6 类别，数据来自 `cache/relation-index.db`）+ 替换/批处理/撤销；实现 `IReferenceRevealable`（按容器路径定位并选中该资源）、Spine 行的「▶ 动画预览…」走 `SpineAnimationPreviewWindow` |
| `BankWorkbenchPage.xaml(.cs)` ★ | 音频工作台：全部音频总表 / bank 树双视图、试听与进度条、FSB 替换、bank 导出 |
| `TextWorkbenchPage.xaml(.cs)` ★ | 文本工作台：lang 文件树 + 键值树 + 源文本预览 + 搜索命中跳转 |
| `StaticWorkbenchPage.xaml(.cs)` ★ | 静态数据工作台：dataClass 树 / 表列表 + JSON 编辑 + 差异视图 |
| `PresetWorkbenchPage.xaml(.cs)` ★ | **预设卡片流页**（**6 类别**，非仅人格）：顶部**类别切换器**（`ComboBox`，数据源 `PresetWorkbenchService.Categories()`；用**具名** `OnCategorySelectionChanged` 处理器，lambda 无法 `-=` 退订）+ 搜索框（250 ms 防抖）+「↻ 重新分析」+「关联库尚未就绪」空态。左栏 `WrapPanel` + `ScrollViewer` 下滑卡片流（每张卡 = 立绘封面 + 标题 + 类别小标签 + 关联条数 + 一句话预览与强度标签「精确/推导/关卡级/歧义」；封面走 `PresetWorkbenchService` 挑图规则，解码宽度固定 166、**并发闸门 4**、带 generation 守卫，解码失败给「无图」占位），点卡片进右栏详情：按类别分组的全部关联资源，**每行内联预览**（图像缩略图 / 音频 `[音频 m:ss]` 前缀 / 文本正文 / 静态记录名）。每行「打开」→ `host.RevealReference(...)` **精确跳转**；Spine 行多「▶ 动画预览…」（`SpineAnimationPreviewWindow`）与「导出…」（`SpineExportService`，`SaveFileDialog` 选目标目录）。**卡片与详情的挑选 / 排序 / 行载荷全在 `PresetWorkbenchService` 里**，页面只做装配 |
| `SettingsPage.cs` | 设置页（共享目录 + 项目元数据），无项目也可用 |
| `HelpPage.cs` | 内置教程（`docs/USAGE.md` 的精简版），无项目也可用 |

### ★ 页面层的五条硬约束（都是踩过的坑，改页面前先读）

1. **`Loaded` 会重复触发**：页面在宿主里被反复换进换出，WPF 每次重新挂载都再抛一次 `Loaded`。
   做数据载入的页面**必须有「已载入」守卫**，否则「离开再回来」= 重跑索引 + 重建树（展开态全丢）。
2. **树重建必须回放展开态**：四个浏览树都是「惰性展开 + 整体重建」，节点模型是只读纯数据、
   **无法承载展开态**。重建前用 `TreeExpansionState.Capture` 收 key、重建后 `Restore` 回放；
   集合要**跨重建累积**（页面字段 `_expandedTreeKeys`），不要每次重建前现抓。
3. **能就地刷新就不要重建**：编辑集变化（「已修改」标记）不改变树的拓扑，就地改节点标题即可
   （文本页的 `RefreshTreeHeaders` 是范例）。整棵重建是「树突然折叠」的根源。
4. **预览内容是 UI 线程的成控点**：内容多长就要建多少控件。新增预览形态必须**先在 provider 侧截断**
   （字符上限 / 行数闸门），且预览内容要包在「有最大高度 + 可滚动」的容器里 ——
   否则窗口一变矮，内容既被裁掉又没法上下滑动。
5. **一次动作只触发一次刷新**：编辑集自身的 `Changed` 事件已经会驱动刷新，
   调用方**不要再手动刷新一次**（历史上同一次保存会重建两遍树）。

### §9.3 对话框与工具窗口

| 文件 | 功能 |
|---|---|
| `StartupScanDialog.cs` ★ | 启动扫描统一模态（打开即扫、**不可取消**、完成自动消失、有跳过/失败才留下），含四个缓存库行视图 |
| `UnityFieldEditorWindow.cs` | Unity 字段树编辑窗口（写回校验、指针目标提示） |
| `NewModWizardWindow.cs` | 新建模组向导（只问模组名，目录默认走共享配置） |
| `UnityCachePickerWindow.cs` | Unity 缓存目录选择 |
| `TextAssetEditorWindow.cs` | 文本资源编辑（JSON 保存前校验并格式化） |
| `SpriteMetadataWindow.cs` | Sprite 元数据编辑 |
| `HexPreviewWindow.cs` | 十六进制预览（优先显示替换文件） |
| `ObjectSummaryWindow.cs` | 对象摘要卡窗口（Mesh/动画/字体等） |
| `BankInspectorWindow.cs` | bank 头/块/FSB 表检查 |
| `FmodReportWindow.cs` | FMOD DLL 勘察报告 |
| `ExportReportWindow.cs` | 旧导出报告（旧导出通道） |
| `ExportProgressWindow.cs` ★ | 导出/调试的阶段进度窗口：**取消令牌** + 取消按钮 + 关窗即取消 + 「是否被用户中途关闭」标记 |
| `ModExportReportWindow.cs` | **新导出报告**（槽位结果、空槽位说明） |
| `SpineAnimationPreviewWindow.cs` ★ | **Spine 骨骼动画播放窗**（非模态 `ShowFor`）：用 `SpineDocument.TryCreate` + `SpineFrameRenderer.RenderFrame(time, 440, 600)` 真渲染（~30 fps `DispatcherTimer`），含播放/暂停、时间轴 `Slider`、动画 `ComboBox`。素材由 `SpineAnimationSourceService.Resolve` 提供（`PageBytes` 适配成 `ISpineTextureSource`）。任一件缺失/解析失败都在信息行给**中文原因**，绝不白图崩溃 | **W2 删除**（native 播放窗退役，动画改前端 spine-ts 就地播放；调用点 `AssetsWorkbenchPage`/`PresetWorkbenchPage` 的「▶ 动画预览…」按钮同步改） |

---

## §10 `src/LimbusModEditor.Cli`

命名空间：`LimbusModEditor.Cli`

| 文件 | 功能 |
|---|---|
| `Program.cs` | 子命令入口：`probe <package>` / `import <项目文件> <文件或目录>` / `export <项目文件> <输出> [来源]` / `export-mod <项目文件> <目标目录>` / `logs`；启动即装配日志；顶层 try/catch 输出中文错误并以退出码 1/2 结束 |
| `ExportModCommand.cs` ★ | **无界面「导出模组」**：与侧边栏「导出模组…」走同一条链路（`ModExportPlanService` 分析 → `ModPackExportService` 按槽位写出），但没有 WPF 消息循环 —— 用于在**真实数据**下精确测量每一段耗时、并单独验证「取消要多久才生效」（`LME_BASE=<程序目录>` 指定共享配置目录，`LME_EXPORT_TIMEOUT_S=<秒>` 到点自动取消）。退出码：0 成功 / 1 失败 / 3 已取消 |

---

## §11 `tests/`

三个测试工程：`LimbusModEditor.Domain.Tests`（**661 例**，引用 Domain/Editing/Application/Formats.Unity）、
`LimbusModEditor.Format.Tests`（74 例，引用各 Formats.*）、
`LimbusModEditor.SpineRuntime.Tests`（**7 例**，引用 `LimbusModEditor.SpineRuntime`，**刻意不并入 .slnx**）。
**合计 742 例全绿、0 警告 0 错误**（2026-09-14 实测；建议**分开跑**，见 §11.7）。

### §11.1 真实数据门控（重要：门控失败是「静默 return」，不是 Skip）

| 环境变量 | 语义 | 主要出现位置 |
|---|---|---|
| `LME_GAME_DIR` | 覆盖游戏安装目录（默认探测 `C:\Program Files (x86)\Steam\...` 等） | 多数 `Real*` 测试（如 `RealWorkbenchGateTests`、`RealBankIndexSmokeTests`、`CatalogBaselineTests`） |
| `LME_UNITY_CACHE_DIR` | 覆盖 Unity Addressables 缓存根 | `Format.Tests/RealSamples.cs`、`AssetPreviewRegistryTests`、`StaticBundleLocatorTests` 等 |
| `LME_BENCH=1` | **必须显式设置**才跑性能基线 | `PerformanceBaselineTests.cs` |
| `LME_FULL_SCAN_SMOKE=1` | 必须显式设置才跑真实全缓存首扫冒烟 | `RealFullScanSmokeTests.cs` |
| `LME_STARTUP_SCAN_SMOKE=1` + `LME_APP_DIR` | 必须显式设置才跑真实启动扫描冒烟（`LME_APP_DIR` 下需有 `MyMod.lmeproj`） | `RealStartupScanSmokeTests.cs` |
| `LME_SCAN_BUDGET` | 限定真实扫描的 bundle 数量（预览覆盖默认 12；全扫冒烟复制前 N 个真实 bundle 到 `%TEMP%`） | `RealPreviewCoverageTests.cs`、`RealFullScanSmokeTests.cs` |
| `LME_REALDATA_INSTALL=1` | **唯一会写真实模组目录的开关**（把写回验证产物装进 `%APPDATA%\LimbusCompanyMods`） | `Format.Tests/RealWriteBackTests.cs` |

**关键推论**：测试报告里的「通过」可能意味着「门控直接 return，什么都没测」。
要确认真实数据确实被覆盖，看输出里的实测文本（耗时/规模）或显式设置上述环境变量。

**反例（无真实样本会失败，而不是跳过，且是作者刻意的硬断言，不要「修」成静默跳过）**：
`Domain.Tests/StaticModServiceTests.cs` 的两条 `Real_staticmod_*`（`Assert.True(RealStaticMods.Count >= 1, ...)`）
与 `Format.Tests/RealBankTests.cs` 的三条（`BankRoot` 为 null 时撞 `Assert.NotEmpty(banks)`）。

**共享夹具（不是测试，无 `[Fact]`，改动会连带打红一批测试）**：
- `Format.Tests/RealSamples.cs`：真实样本定位（`UnityCacheRoot` 还要求存在 `<outer32hex>/<inner32hex>/__data` 结构）。
- `Format.Tests/UnityTestAssetBuilder.cs`：**合成但真实**的 Unity 文件（Version 21、内嵌类型树；MonoBehaviour/PPtr/枚举/数组、Mesh/AnimationClip/Font 摘要），
  使 `UnityDependencyTests`/`UnityFieldTreeTests`/`UnityObjectSummaryTests` 与是否装游戏无关。
- `Domain.Tests/SyntheticBank.cs`：合成 FMOD bank（`MinimalFsb5` / 事件 bank / 音频 bank / 垃圾文件），
  被 `BankDirectoryServiceTests`、`BankIndexStoreTests`、`ModPackExportTests`、`ModApplyServiceTests` 依赖。

### §11.2 会写盘 / 钉死数字 / 计时断言

- **写盘**：`RealWriteBackTests` 写 `<repo>/artifacts/realdata-verify/`（只有 `LME_REALDATA_INSTALL=1` 才动真实模组目录）；
  三个 `Real*IndexSmokeTests` / `RealWorkbenchGateTests` 只读游戏目录、缓存写 `%TEMP%`；
  `RealFullScanSmokeTests` 在 `LME_SCAN_BUDGET` 模式会**复制真实 bundle** 到 `%TEMP%`。
- **钉死的精确数字（数据/游戏更新后打红属预期）**：真实 bank 数 `1531`（`BankDirectoryServiceTests`）、
  `TextureFormatCatalog.All().Count == 14`、JsonDocumentEditor 6000 行/5003 节点、Carra2 键正则、
  40 个 class id 清单（`UnityCacheScanServiceTests`）、51 条 class id 映射（`UnityClassIdFormatMappingTests`）、
  9 条官方表值（`Format.Tests/UnityClassIdMappingTests`）、默认列宽 360 / 钳制 260–2000、
  三库表列名清单（`SqliteTableCacheTests`、`TextIndexStoreTests` 直接断言 `PRAGMA table_info`）。
- **墙钟断言（慢机易 flake）**：`RealBankIndexSmokeTests` 读索引 **< 冷建索引耗时的 1/5**（原先是硬编码 1.5 s，
  慢盘 / 并行时实测 1.6–1.9 s 会误报；实测比值约 32×，用相对量才稳）、
  `RealStaticIndexSmokeTests` 热读 < 1.5s 且 < 直接枚举耗时/2、`BankDirectoryServiceTests` 扫 1531 bank < 60s、
  `PerformanceBaselineTests` 断言资产数 > 10 万（缩小缓存场景会失败）。

### §11.3 不要顺手改的测试约定

- **三个测试工程都不引用 `LimbusModEditor.App`**：页面行为只被「镜像约定」测试间接守住——
  `AssetFilterComboSentinelTests` ↔ `AssetsWorkbenchPage` 的筛选哨兵（防下拉空白）、
  `BankTreeRulesTests` ↔ `BankWorkbenchPage.RebuildTree`（防空树）、
  `UiStateServiceTests` 的列宽合并 ↔ `WorkbenchShell.SavePreviewWidth`。
  要改行为就改页面的约定，**不要改这些测试**。
- `Domain.Tests/UnitTest1.cs`（空 `Test1`，无断言）与 `Format.Tests/UnitTest1.cs`（类名 `FormatTests`，**4 条真测试**）
  **含义完全不同**，删「看起来是模板」的文件会删掉真实覆盖。
- 口径类断言（`LangEntryCaliberTests`、`LangTextDisplayTests`）是**双向契约**：
  「顺手统一路径口径」会同时打红 `TextIndexStoreTests`、`RealTextIndexSmokeTests`、`ModExportPlanTests`、`LangExportFormatterTests`。
- SQLite 表/列名断言在测试里，schema 迁移必须同步改测试，不能只改产品代码。
- 全仓测试**没有一处 xunit `Skip`**：所有门控都是测试体内 `return`（见 §11.1）。

### §11.4 `LimbusModEditor.Domain.Tests/`（按被测对象分组，661 例）

| 覆盖对象（源码） | 测试文件 |
|---|---|
| `AssetDisplay` / 显示口径 | `AssetDisplayTests.cs`、`AssetFilterComboSentinelTests.cs` |
| `AssetSearchService` | `AssetSearchServiceTests.cs` |
| `AssetCatalog`（分页/定位/补充集，**与旧搜索逐行等价**） | `AssetCatalogTests.cs` |
| `AssetCatalogTree`（**与 `AssetTreeBuilder` 逐节点等价**） | `AssetCatalogTreeTests.cs` |
| `AssetTreeBuilder`（参考实现，仍由测试驱动） | `AssetTreeBuilderTests.cs` |
| `AssetPropertyService` | `AssetPropertyServiceTests.cs` |
| `AssetPreviewRegistry` / providers | `AssetPreviewRegistryTests.cs`（含「大静态表预览必须有界」的真实数据回归） |
| `AssetEditService` / 批量替换 | `AssetEditServiceTests.cs`、`BatchReplacementTests.cs` |
| `UnityFieldEditService` / 字段编辑 | `UnityFieldEditServiceTests.cs` |
| `SpriteMetadataEditService` | `SpriteMetadataEditTests.cs` |
| `ImageAtlasEditService` / `ImageAtlasService` | `ImageAtlasEditServiceTests.cs`、`ImageAtlasTests.cs` |
| `ImagePreviewService` | `ImagePreviewServiceTests.cs` |
| `UnityTextureCodec` | `UnityTextureCodecTests.cs`、`ExtendedTextureFormatTests.cs` |
| `TextPreviewService` / `TextAssetEditService` | `TextPreviewServiceTests.cs`、`TextAssetEditTests.cs` |
| `JsonDocumentEditor` | `JsonDocumentEditorTests.cs` |
| lang：工作台/编辑集/树/显示/补丁/格式 | `LangTextWorkbenchServiceTests.cs`、`LangEntryCaliberTests.cs`、`LangTextTreeBuilderTests.cs`、`LangTextDisplayTests.cs`、`LangTextPatchServiceTests.cs`、`LangExportFormatterTests.cs`、`TextDiffServiceTests.cs` |
| `TextIndexStore` | `TextIndexStoreTests.cs`（+ 真实数据 `RealTextIndexSmokeTests.cs`） |
| `StaticIndexService`/`StaticModService`/`StaticBundleLocator`/`StaticTableIndexStore` | `StaticModServiceTests.cs`、`StaticBundleLocatorTests.cs`、`StaticTableIndexStoreTests.cs`、`RealStaticIndexSmokeTests.cs` |
| `BankDirectoryService`/`BankAudioService`/`BankIndexStore`/`BankTreeRules` | `BankDirectoryServiceTests.cs`、`BankAudioServiceTests.cs`、`BankIndexStoreTests.cs`、`BankTreeRulesTests.cs`、`SyntheticBank.cs`（合成夹具）、`RealBankIndexSmokeTests.cs` |
| 导出流水线 | `ModExportPlanTests.cs`、`ModPackExportTests.cs`（含进度节流）、`ModExportServiceTests.cs`、`MultiFormatExportTests.cs`、`ExportMatrixTests.cs`、`ExportAdvisorTests.cs`、`BankExportTests.cs`、`LunartiqueConversionTests.cs`、`UnityCacheExportServiceTests.cs` |
| 导入 | `ModImportServiceTests.cs`、`SourceImportTests.cs`、`GenericZipImportTests.cs` |
| 项目持久化 | `ProjectPersistenceTests.cs`、`ProjectFileSlimmingTests.cs`、`ProjectBuildServiceTests.cs`、`ProjectAutoConfigureTests.cs`、`NewModTemplateServiceTests.cs` |
| 扫描与索引 | `StartupScanServiceTests.cs`、`UnityCacheScanServiceTests.cs`、`UnityCacheSqliteIndexStoreTests.cs`（规范化往返、快照、取消/异常回滚、旧版/缺表重建）、`CacheSignatureTests.cs`、`SqliteTableCacheTests.cs`、`RealFullScanSmokeTests.cs`、`RealStartupScanSmokeTests.cs`、`PerformanceBaselineTests.cs`、`DatabasePerformanceTests.cs`（真实库逐行等价、耗时/分配、导出读取会话、全量冷/热扫描） |
| 关联图（派生，6 类别） | `SubjectRelationAnalyzerTests.cs`（含「游戏侧角色名拼写错误合并为一组」、6 类别判定、跨资源 `xref` 边、E.G.O 饰品 id 归一化）、`RelationStoreTests.cs`（持久化 + 反向索引 + `xref` 多对多 + `LinksOf`）、`RealRelationIndexSmokeTests.cs`（真实数据门控 `LME_RELATION_SMOKE=1`） |
| 预设卡片流（展示层） | `PresetWorkbenchServiceTests.cs`（**类别切换器计数**、封面挑立绘优先级 + **能渲染的胜过不能渲染的**、定位键→`AssetRecord` 还原、详情分组用户视角顺序、每行 `TargetFor` 跳哪页 / 用什么载荷） |
| Spine 解析 / 预览 / 导出 / 素材解析 | `SpineTests.cs`（骨架 4.0 口径：`skins` 数组、动画分组、**首帧无 `time` = 0**、`rotate`/`translate`/`scale` 合帧、槽位 `attachment`+`rgba` 淡入淡出、只收 region 附件且 mesh 计入不支持；图集多页 + `scale`/`orig`/`offsets`/`rotate`；BOM 容忍；同目录索引；布局叠加图**区域外像素不动** + 超长边等比缩小；导出目录名取容器目录末级）——**W2 删除**（13 例，随解析/布局逻辑）；`SpineAnimationSourceServiceTests.cs`（同目录找齐骨架/图集/页贴图、从图集进入结果一致、缺骨架/缺图集给中文错、不跨目录串、未知页返回空字节）——**保留**（6 例，ctor 适配） |
| 目录定位与配置 | `GameDirectoryLocatorTests.cs`、`UnityCacheLocatorTests.cs`、`RealLocatorTests.cs`、`SharedConfigTests.cs`、`UiStateServiceTests.cs` |
| 调试与启动 | `ModApplyServiceTests.cs`、`DebugApplyTests.cs`、`ModInstallServiceTests.cs`、`GameLaunchServiceTests.cs` |
| catalog | `CatalogBaselineTests.cs` |
| Unity class 映射 | `UnityClassIdFormatMappingTests.cs` |
| 输出原子性 | `AtomicOutputTests.cs` |
| 真实工作台门 | `RealWorkbenchGateTests.cs`（预览 Kind 矩阵、FMOD 解码、rebank 结构、lang 回放等） |
| 日志调用约定（`LoggerExtensions`） | `LoggingTests.cs`（`Scope` 计时/慢操作升级、`Guard` 拦截并记录完整栈、`GuardAsync` 取消记 Info、`Every` 采样与级别过滤下的惰性构造；用**独立 `LogFactory` + `MemoryTarget`**，不碰全局 `LogManager`、不写任何文件） |
| 占位 | `UnitTest1.cs`（空的 `[Fact] Test1`，无断言） |

### §11.5 `LimbusModEditor.Format.Tests/`（74 例）

| 测试文件 | 覆盖 |
|---|---|
| `RealSamples.cs` | 真实样本定位辅助（缓存/模组/bank 路径发现，供其他 `Real*` 测试用） |
| `RealSampleTests.cs` | 真实 bundle 扫描、纹理 resS、摘要、类型树勘察、格式分布 |
| `RealWriteBackTests.cs` | **写回闭环**：真实流纹理 PNG 替换 → 重打包 → 引用验证 → Carra2 导出/重导入逐字节还原 |
| `RealBankTests.cs` | 真实 bank：事件 bank 空 SNDH、SNDH→FSB5 切片、样本表（无样本机器上硬断言失败，刻意） |
| `RealPreviewCoverageTests.cs` | 预览覆盖率矩阵（`LME_SCAN_BUDGET` 控制抽样 bundle 数），矩阵写入 `%TEMP%/lme-preview-coverage.txt` |
| `UnityTestAssetBuilder.cs` | 合成 Unity 资产构造器（夹具） |
| `UnityBundleTests.cs` / `AssetsToolsBackendTests.cs` / `UnityDependencyTests.cs` / `UnityFieldTreeTests.cs` / `UnityObjectSummaryTests.cs` / `UnitySpriteCompositeTests.cs` / `UnityClassIdMappingTests.cs` | bundle 读写、依赖与引用者、字段树、对象摘要、Sprite 合成、class id 映射 |
| `BankParserTests.cs` / `BankAssemblerTests.cs` / `Fsb5ParserTests.cs` / `FmodCodecTests.cs` / `FmodDllInspectorTests.cs` / `RebankDiffTests.cs` | bank/FSB/FMOD/rebank 格式层 |
| `CarraDiffTests.cs` / `XzCodecTests.cs` | Carra 差分与 XZ 编解码 |
| `UnitTest1.cs` | 类名 `FormatTests`：Carra 探测/导入导出往返、Lunartique 探测与往返（4 例，非空占位） |

### §11.6 `LimbusModEditor.SpineRuntime.Tests/`（7 例，刻意独立）— **2026-09-15 起计划删除（W2）**

| 测试文件 | 覆盖 |
|---|---|
| `SpineRuntimeTests.cs` / `SyntheticData.cs` / `TestTextureSource.cs` | 用**合成**的最小 Spine 4.0 数据（骨架 JSON + 图集 + 由 SkiaSharp 现画的 PNG），覆盖：解析、元信息（动画清单/时长）、region/mesh 渲染非空、不同时间像素不同、缺页/解析失败给出**中文失败结果**、PMA 与 `rotate:90` 不越界 |

- **⚠️ 已定案删除**（`docs/ARCH-WEBVIEW2-VUE.md` §7）：随 `LimbusModEditor.SpineRuntime` 工程一并移除（7 例；
  其中渲染类覆盖由前端 spine-ts 测试 + Playwright 端到端替代）。**测试基线 746 → 733**。
- **为什么不并入 `.slnx`**（删除前现状）：本工程刻意 `TreatWarningsAsErrors=false` 且 `Nullable=disable`（与
  `SpineRuntime` 类库对齐），并入主清单会让「跑整个 solution 测试」把 vendored 源码的历史警告卷进来。

### §11.7 测试维护注意事项

- **墙钟预算断言是既有 flake**：`RealBankIndexSmokeTests` 的读库耗时断言在多测试工程并行时偶发超时
  （慢机上单跑也见过 1.9 s）。**已改成相对判据**（`< 冷建耗时/5`，实测比值约 32×），
  不要再改回硬编码秒数（详见 §11.2）。
- **三个测试工程建议分开跑**：并行时互相抢磁盘，墙钟断言最先受影响；分开跑也更利于排错
  （`dotnet test <单个 csproj> -c Debug --no-build`）。
- **不要写死活动语言**：真实 lang 门控测试必须跟随 `lang/config.json`（本机为汉化组目录 `LLc-CN-LCTA`）。
- 需要真实数据的测试用环境变量或路径存在性门控，新增同类测试请沿用同一风格（§11.1）。

---

## §12 `artifacts/` 与 `third_party/`

| 路径 | 内容 | 说明 |
|---|---|---|
| `artifacts/publish-win-x64/` | 自包含发布产物（`LimbusModEditor.App.exe` + 依赖 + `fmod/` 三 DLL + `cache/` + `config/` + `projects/`） | `scripts/publish.ps1` 输出；**不是源码** |
| `artifacts/LME/` | 旧发布目录（历史遗留） | 同上 |
| `artifacts/realdata-verify/` | 写回验证产物：`source.bundle`、`modified.bundle`、`replacement.png`、`LME-写回验证.carra2`、`verify-report.json` | 由 `RealWriteBackTests` 生成，报告见 `docs/REALDATA-VERIFY.md` |
| `artifacts/**/_probe.cs` | 历史探针脚本残留 | 不参与构建，与源码无关 |
| `third_party/fmod/` | `fmod64.dll` / `fsbank64.dll` / `libfsbvorbis64.dll` | **git 忽略**，由用户合法放置；发布脚本会复制进输出 `fmod/` |

---

## §13 `AssetRecord.Metadata` 键字典（扩展槽的真实键名）

| 键 | 写入方 | 含义 |
|---|---|---|
| `reference` | `Scanning/UnityCacheScanService` | `"true"` = 纯引用资产（可由索引重建，保存项目时被跳过） |
| `cacheOuter` / `cacheInner` | `UnityCacheScanService` / `UnityCacheMaterializationService` | 缓存外层键 / 内层键（Carra2 键与实体化路径） |
| `containerEntry` | `UnityCacheScanService`、`Formats.Unity/UnityAssetService` | Unity `m_Container` 的游戏内资源路径（**显示层口径**） |
| `catalogBaseline` | `UnityCacheScanService`、`Assets/ModImportService` | vanilla 基线判定摘要（列表显示用） |
| `staticBundle` | `UnityCacheScanService`（常量 `StaticBundleMetadataKey`）+ `StaticMods/AssetStaticClassifier` | **位掩码的十进制串**（位1 catalog 内层键 / 位2 bundle 名 / 位4 容器路径）；`"true"` 是老形态的兼容读法（= 位1）。默认在资源工作台**隐藏**（2026-09-15 恢复），预览通道与静态工作台也读它；清除只在 catalog 权威判定时发生，且索引对它有最终解释权（`AssetCatalog.MergeProjectState` 不覆盖它） |
| `unityBundle` / `unitySerializedFile` | `Assets/ModImportService`、`Scanning` | `"true"` = 资产来自 bundle / 独立 `.assets` |
| `replacementPath` | `Assets/AssetEditService`、UI | 替换文件路径（预览、导出、构建都读它） |
| `originalSize` | `AssetEditService` | 撤销替换时还原显示大小 |
| `unityFieldEdits` | `Assets/UnityFieldEditService` | 字段编辑集 JSON（`UnityFieldEditSetCodec`） |
| `spriteMetadata` | `Assets/SpriteMetadataEditService` | Sprite 元数据编辑 JSON |
| `atlasDirectory` / `atlasLayoutPath` / `atlasRegionCount` | `Assets/ImageAtlasEditService` | 图集拆分产物位置与区域数 |
| `originalSourcePath` / `sourcePackagePath` / `sourceFormat` | `Assets/ModImportService` | 来源包/源文件路径与格式（去重与导出择优） |
| `materialized` | `Scanning/UnityCacheMaterializationService` | `"true"` = 已实体化进项目 |
| `bankSource` | `Assets/`（bank 导入/替换） | 样本/替换所属 bank 来源（导出计划按它分组） |
| `base_bank`（Rebank 包侧） | `Formats.Rebank/RebankPackage` | `.rebank` 的目标 bank 文件名（**无路径分隔符**） |

> 键名一律用字符串字面量读写，字典比较器是 `OrdinalIgnoreCase`。
> 新增键名请同时在此登记，避免后来者误判「无用字段」。

---

## §14 按症状定位文件（速查）

| 症状 / 需求 | 先看 | 再看 |
|---|---|---|
| 资源列表名字/路径/类型标签不对 | `Application/Assets/AssetDisplay.cs` | `Formats.Unity/UnityClassId.cs`、`Domain/Assets/AssetModels.cs` |
| 资源列表出现大量「未知」类型 | `Formats.Unity/UnityClassId.cs`（映射） | `AssetDisplay.TypeLabel`（中文文案） |
| 搜索/筛选/排序不对 | `Application/Assets/AssetSearchService.cs`（`Matches`） | 分页列表走 `Application/Catalog/AssetCatalog.cs`、`AssetsWorkbenchPage` 筛选栏、`AssetSortKind` |
| 翻页/页码条/「打开某资源没翻到那一页」 | `Application/Catalog/AssetCatalog.cs`（`Page` / `IndexIn`） | `AssetsWorkbenchPage.RunSearchAsync`、`BuildView`、`UpdatePageBar` |
| 目录树层级/重名/懒展开 | `Application/Catalog/AssetCatalogTree.cs` | `AssetDisplay.TreePath/LeafDisambiguator`、`AssetTreeNode.DistinguishLeaves` |
| 资源列表里导入的模组 / 提取到项目的音频不见了 | `Application/Catalog/AssetCatalog.cs` 的 `IAssetStateSource.ProjectOnly` | `ProjectAssetStateSource`（判据 = `AssetDisplay.IsCacheReference`）、`CatalogFor` 的失效条件 |
| 预览空白或形态不对 | `Application/Assets/Preview/AssetPreview.cs` + `AssetPreviewProviders.cs` | `TextPreviewService.cs`、`HexDumpService.cs` |
| **某种资源预览退化成十六进制转储** | `AssetPreviewProviders.ScriptPreviewProvider.CanPreview`（现覆盖 MonoBehaviour / MonoScript **+ Component / GameObject / ScriptableObject**，字段树标题为「对象字段树」） | 该类型是否还有别的 provider；`AssetPreviewRegistry.CreateDefault` 的注册顺序；真实数据实测 Component（type 14）在缓存里最多 |
| **图片资源预览不出图（Sprite 类）** | 解码要走 `ReadBundleSpriteComposite`（**Sprite**）而不是 `ReadTexturePng`（只认 Texture2D pathId）；参考 `AssetsWorkbenchPage` / `PresetWorkbenchPage` 的 `TryDecodeImagePng` | `asset.Type == AssetType.Sprite && ContainerPath` 是否存在；`UnityPathId` 是否为空 |
| **点预览就未响应/卡死** | `AssetPreviewProviders.TextPreviewProvider.PreviewCharLimit`（预览规模闸门） | `AssetsWorkbenchPage.WrapPreview`/`IsJsonTreeWorthBuilding`（JSON 树是 UI 线程一次性构造）、`AssetPropertyService`（属性区会再读一次正文） |
| **双击文本资源后界面卡死（且无 crash 日志）** | `Application/Assets/AssetEditService.ReadCurrentBytesAsync`（**必须**拒绝 bundle 容器）/ `TextAssetEditService.CanEditText`（二进制、超长闸门） | `AssetsWorkbenchPage.EditTextAsset_Click` + `TextAssetEditorWindow`（`TextBox` 装载规模）、`PreviewRead.IsBundleAsset`（判「正文在容器里」） |
| 预览看不到下面、窗口缩小后没法上下滑动 | `App/WorkbenchPages/AssetsWorkbenchPage.xaml.cs`(`WrapPreview`, `PreviewMaxHeight`) | `WorkbenchShell.xaml` 的 `EditHost`（各页自带的 `ScrollViewer` 才是滚动入口）、`AssetsWorkbenchPage.xaml` 右栏两行的 `Height`（**必须是星号行**：`Auto` 行会让 `ScrollViewer` 永不滚动、并把预览行挤成 0） |
| **资源工作台默认视图里又出现 static-data** | `Application/StaticMods/AssetStaticClassifier.cs`（位掩码合成，唯一判据）、`Scanning/UnityCacheSqliteIndexStore.cs`（`assets.static_kind` 落库 + 就地迁移回填 + `ix_assets_named` 列序 / `ix_assets_static`） | `AssetSearchService.Matches` 末条判据、`AssetCatalog.CollectMatches` 的 `IsStatic:` 下推、`AssetsWorkbenchPage` 的「显示静态数据表」复选框、`UnityCacheScanService`（扫描期算位与「不得清位」） |
| **资源预览「关联资源」为空 / 显示「关联图还没建立」** | `Application/Relations/PersonaRelationIndexService.cs`（四源签名比对是否触发重建）、`RelationQueryService.DescribeSubjectsForAsset`（库没建好时返回中文原因，正常不是 bug） | `Scanning/StartupScanService`（第 6 步 `relations` 是否跑过）、`cache/relation-index.db` 是否存在且非空；资源没有 `containerEntry` 时无法定位（属预期） |
| **同一个人格在关联图里被拆成多组** | `Relations/SubjectRelationAnalyzer.cs` 的 `CharacterAliases`/`NormalizeCharacter`（游戏文件名有拼写错误） | 新增角色别名时同改 `SubjectRelationAnalyzerTests` 的拼写合并用例 |
| **卡片流页空白 / 点卡片没有内容 / 类别切换里某类为 0** | `Relations/PresetWorkbenchService.cs`（`Categories()` 计数是否全为 0 = 关联库没建好）、`App/WorkbenchPages/PresetWorkbenchPage.xaml.cs` 的 `RebuildCards`/generation 守卫（换项目后旧结果被丢弃是**预期**） | `MainWindow.RefreshProjectState` 是否调了 `OnProjectRefreshed()`、`cache/relation-index.db` 是否存在且非空 |
| **卡片有标题但没有立绘封面** | `PresetWorkbenchService.BuildCards`（封面要「能定位到资源」且资源可渲染：源文件存在 + `UnityPathId`） | 该对象的 image 链接是否命中 `containerEntry`；目标类型是否 Sprite/Texture/SpriteAtlas |
| **卡片流想加新类别（EGO 之外）** | `Relations/RelationCategories.cs`（`All` + `Label`）+ `SubjectRelationAnalyzer`（该类别怎么抽事实、id 怎么归一化） | 改完把 `RelationIndexSource.FormatVersion` +1；`WorkbenchCacheSchema` 是否要加列；`PresetWorkbenchServiceTests` 补类别用例 |
| **卡片详情点「打开」跳过去还要自己翻一遍** | `Relations/RelationDeepLink.cs`（载荷口径）+ 目标页的 `IReferenceRevealable.Reveal`（真要选中那一行） | `MainWindow.RevealReference`（定位失败会退化为关键词）；四页是否都实现了 `IReferenceRevealable` |
| **Spine 资源不显示 Spine 预览、退回普通文本** | `Relations/RelationDisplayRules.IsSpinePath`（路径粗筛，真实口径 `Prefab/SpineIllustPrefab/`、`.psb`、`SkeletonData`、`/Story/Spine/`） | 内容判定的真正入口是 `Spine/SpineTextParser`（**不像骨架/图集就返回 `None`**，属预期）；`AssetPreviewRegistry.CreateDefault` 是否传了 `SpinePreviewService` | **W2 后**：内容判定在前端 spine-ts；路径判据保留 C#（前端经 IPC 使用）；`SpinePreviewProvider` 注册移除 |
| **Spine 只能看结构、想放动画** | `Application/Spine/SpineAnimationSourceService.cs`（找齐素材）→ `LimbusModEditor.SpineRuntime`（渲染）→ `App/SpineAnimationPreviewWindow.cs`（播帧） | 素材是否同目录齐三件套、`PageBytes` 是否能解出页贴图（Sprite 走 `ReadBundleSpriteComposite`）、`SpineDocument.TryCreate` 的中文错误 | **W2 起改为**：`SpineAnimationSourceService`（定位+流式喂字节）→ 前端 spine-ts 就地播放（`spine.locate` IPC，ADR §7） |
| **Spine 预览有结构但看不到图集布局图** | `Spine/SpinePreviewService.TryBuildLayoutImage`（按图集页名在同目录找同名贴图） | 页贴图是否与 `.atlas.txt` **同一目录**且文件名与页名一致、是否有 `UnityPathId` | **W2 后**：布局图由前端 spine-ts 渲染替代；此症状转为前端渲染问题（按 spine-ts 加载失败原因排查） |
| **Spine「导出…」写不出文件** | `Spine/SpineExportService.ExportAsync`（写 `<目标>/<目录名>/`，只写骨架/图集文本 + PNG 页贴图） | 目标目录是否可选；该资源是否有容器路径（取不到末级目录时落到 `spine/`） |
| **导出几分钟不出产物** | `Build/UnityCacheExportService.IsEditedCacheAsset` 与 `Build/UnityBundleBuildService.Build` 的候选谓词（`File.Exists` 是否前置、惰性链是否物化） | `ModExportPlanService.Plan`、`Application/Scanning/UnityCacheScanService.RehydrateFromIndexAsync`（项目为何有 127 万条资产） |
| **树形视图突然折叠回初始形态** | `App/WorkbenchPages/TreeExpansionState.cs`（key 回放） | 各页 `RebuildTree` + `_expandedTreeKeys`；`Loaded` 是否有 `_loaded` 守卫（Static/Bank 早先没有） |
| **导出模态弹出后软件未响应、无产物** | `App/MainWindow.xaml.cs`(`ExportMod_Click`/`PrepareExportPlanAsync` 的 `Task.Run`) | `Build/UnityBundleBuildService.BuildAsync`（必须整体在后台线程）、`ModPackExportService` 各槽位、`CarraArchive`/`XzCodec`（同步 XZ 全在调用线程上跑） |
| 导出中途取消/进度窗口 | `App/ExportProgressWindow.cs`（`Token`/`EnableCancel`/`ClosedByUser`） | `ThrottledProgress.cs`（进度节流）、各服务已带 `CancellationToken` 参数 |
| 图像预览/贴图上下颠倒、通道错乱 | `Editing/Images/UnityTextureCodec.cs` | 调用方是否重复翻转；`UnitySpriteCrop.cs` |
| Sprite 预览裁错区域 | `Formats.Unity/AssetsToolsBackend.cs`(`ReadBundleSpriteComposite`) | `UnitySpriteCrop.cs` |
| 字段编辑器打不开/字段不可编辑 | `Formats.Unity/UnityAssetService.cs`、`AssetsToolsBackend.LoadTemplate` | `App/UnityFieldEditorWindow.cs` |
| 字段编辑写回报错 | `AssetsToolsBackend.ApplyFieldEdits/ValidatePointerTarget` | `Application/Assets/UnityFieldEditService.cs` |
| 导出产出的 carra/carra2 加载器不认 | `Build/UnityCacheExportService.cs`、`Formats.Carra/CarraModels.cs` | Carra2 键口径（`<外>/<内>/<pathId>.<类型表索引>`） |
| 导出产物目录结构不对 | `Domain/Formats/ExportLayout.cs` | `Build/ModExportPlanService.cs`、`ModPackExportService.cs` |
| 某个槽位没有产出 | `Build/ModExportPlanService.cs`（槽位点亮条件） | 该槽位 `ExportXxxAsync` 的前置条件（缺缓存/缺样本会整份跳过并说明） |
| 导出报告缺说明 | `App/ModExportReportWindow.cs` | `ModPackExportResult` |
| `.rebank` 加载器不生效 | `Formats.Bank/Fsb5Models.cs`（样本名） | `ModPackExportService` rebank 槽位 |
| lang 树空白 / 打开即「已修改」 | `App/WorkbenchPages/TextWorkbenchPage.xaml.cs` | `LangEditSession.HasRealEdits`、`TextIndexStore` 口径 |
| lang 补丁键错位 | `Application/Texts/LangTextWorkbenchService.ToPatchKey` | `TextIndexStore`（`language_prefix`） |
| lang 导出缺某个格式 | `Texts/LangExportFormatter.cs`（**可表达性门**） | 该格式的说明文案 |
| 静态表打不开/慢 | `StaticMods/StaticTableIndexStore.cs`（正文有界缓存） | `StaticIndexService`、`StaticBundleLocator` |
| 音频 bank 树异常/样本缺失 | `Assets/BankTreeRules.cs`、`BankDirectoryService.cs` | `Formats.Bank/BankModels.cs`、`Fsb5Models.cs` |
| 试听/替换无声或报 DLL 错 | `AppConfig/AppEnvironment.cs`（FMOD 发现） | `Formats.Bank/FmodCodec.cs`、`NativeFmodAudioCodec.cs`、`FmodDllInspector.cs` |
| 启动慢 / 弹窗时机不对 | `App/MainWindow.xaml.cs`（`Loaded` 路径） | `App/StartupTrace.cs`、`Scanning/StartupScanService.cs`、`App/StartupScanDialog.cs` |
| 缓存库报 `no such table` | `Caching/SqliteTableCache.cs`（读路径缺表自愈） | `Caching/WorkbenchCacheSchema.cs` |
| 缓存没生效/读旧数据 | `Caching/CacheSignature.cs` + 对应 Store 的 `EnsureSource` | 索引口径版本常量 |
| 项目文件巨大/打开慢 | `Projects/ProjectService.cs`（瘦身 + 回灌） | `Domain/Projects/ModProject.cs` |
| 打开项目报版本不支持 | `Domain/Projects/ModProject.cs`（`CurrentSchemaVersion`） | `ProjectService` 加载校验 |
| 设置改了但界面没更新 | `App/MainWindow.xaml.cs`（`ResetLocatorCache`） | `AppConfig/AppEnvironment.cs` |
| 调试写盘/还原失败 | `Debugging/ModApplyService.cs` | `DebugFileChange`/`steps.tsv`、`GameLaunchService.cs` |
| 静态模组调试写 catalog 失败 | `Debugging/StaticModApplyService.cs` | catalog 布局自校准（size 合理性） |
| 目录定位到错误位置 | `Debugging/*Locator.cs` | `AppConfig/AppEnvironment.cs`（优先级与迁移） |
| CLI 行为 | `Cli/Program.cs` | `Build/ModExportService.cs`、`Assets/ModImportService.cs` |
| 界面颜色/样式不一致 | `App/Themes/Theme.xaml`、`WorkbenchStyles.xaml` | 页面内是否有硬编码色值（应清零） |

---

## §15 文档登记（2026-09-15 新增，铁律 §3-11）

| 文档 | 内容 |
|---|---|
| `docs/ARCH-WEBVIEW2-VUE.md` ★ | **架构决策记录（ADR）**：显式取代 `docs/WEBVIEW-FEASIBILITY.md` §5/§6 的「不做全量重写」结论；为何现在做 A；§4.2 不利项逐条回应（运行时依赖/内存/IPC/中文 IME/对话框/DPI）；W0→W4 波次与验收口径；22 个 UI 单元迁移分类表（保真 13 / 重设计 5 / 砍掉 4）；前端工程根 `src/LimbusModEditor.Web/` 与构建集成；Spine 全量前端化移除/保留清单（逐工程逐文件）；内存与速度目标口径（基线 1,275,623 条资源 / 1,459 bundle / 发布 519MB） |
| `docs/WEB-IPC-CONTRACT.md` ★ | **最小 IPC 契约**：请求/响应/事件三类消息帧；统一分页契约（`AssetCatalogPage`，禁止全量）；错误码与取消语义；二进制通道（`lme.app`/`lme.data` 本地虚拟主机，禁止 base64）；对话框/剪贴板/`Process.Start` 回调；`RelationDeepLink` 的 `'\0'` 分段载荷原样透传与 Reveal 布尔语义；契约 DTO 归属 `Application/Ipc`（无 WPF，铁律 §3-10） |
| `docs/SPIKE-WEBVIEW2.md` ★ | **W0 WebView2 宿主 spike 实测结论**：五个未知量（运行时检测/IME/DPI/对话框回调/二进制通道）逐项实测；Evergreen 运行时探测（本机 153.0.4234.32）；虚拟主机映射（`lme.app`/`lme.data`）加载静态页成功；PerMonitorV2 DPI 清单；中文 IME/文件对话框/剪贴板/`Process.Start` 回调通道代码落地；二进制通道 Virtual Host vs Base64 对比结论；Fixed Version 分发开关预留与体积代价；契约需修正点 |

### 同步标记汇总（2026-09-15）

- `docs/WEBVIEW-FEASIBILITY.md`：§5/§6 结论**被 ADR 取代**（顶部有横幅）；§1–§4 技术账继续有效。
- `docs/CODE-STRUCTURE.md`：§2/§3/§6/§10 与本文档联动更新（见该文件对应小节）。
- `docs/STATUS.md`：测试基线 746 → **733**（W2 删除 SpineRuntime.Tests 7 例 + SpineTests 13 例时生效）。
- `THIRD-PARTY-NOTICES.md`：§1 spine-csharp / §2 SkiaSharp 条目 W2 移除，新增 spine-ts（前端运行时）条目。
