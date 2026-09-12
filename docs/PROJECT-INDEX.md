# 文件功能索引（PROJECT-INDEX）

> 用途：**按文件**快速了解「这个项目有哪些文件、各自负责什么」，以及「要改的东西在哪个文件」。
> 索引以读代码为准（不依赖运行程序、探针或截图）。
>
> 配套：
> - 分层架构、运行流程、跨文件不变量：`docs/CODE-STRUCTURE.md`
> - 待办 / 铁律 / 真实环境事实：`docs/STATUS.md`
> - 用户可见行为：`docs/USAGE.md`
>
> 行数为**物理行数**（含空行，与 read 工具显示的总行数一致），仅作规模参考。
> 「关键类型」列是该文件对外暴露的主要类型；带 **★** 的文件有「详细索引卡」，见对应小节。

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

| 文件 | 规模 | 关键内容 | 职责 |
|---|---|---|---|
| `LimbusModEditor.slnx` | 20 行 | 12 个 src 项目 + 2 个测试项目 | 解决方案清单（新项目必须在此登记，否则本地构建都不编译） |
| `Directory.Build.props` | 16 行 | `net8.0`、`Nullable=enable`、`TreatWarningsAsErrors=true`、`RuntimeIdentifier=win-x64`、`CopyLocalLockFileAssemblies=true` | 全局编译约定。**警告即失败** |
| `global.json` | 7 行 | 固定 SDK 版本 | 构建 SDK 选择 |
| `README.md` | 156 行 | 三步工作流、格式边界、CLI、打包、文档导航与验证命令 | 项目门面文档 |
| `scripts/publish.ps1` | 58 行 | Release win-x64 发布 + 把 `third_party/fmod/*.dll` 复制进发布输出 `fmod/` | 发布脚本（纯 ASCII，兼容 PS 5.1） |
| `.gitignore` | 57 行 | 忽略 `bin/obj/artifacts/third_party/fmod` 等 | 仓库卫生 |
| `docs/*.md` | 见下 | 9 份文档 | 见 `docs/README.md` 的文档地图 |

---

## §2 `src/LimbusModEditor.Domain`（无 NuGet 依赖的纯模型层）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `Domain/Assets/AssetModels.cs` ★ | 67 | `AssetType`(20 值)、`AssetEditState`(6 值)、`AssetRecord` | 全仓库最核心的资产模型；`Metadata` 是大小写不敏感的扩展槽 |
| `Domain/Edits/EditModels.cs` | 23 | `EditOperationKind`、`EditOperation` | 「一次编辑操作」的不可变审计记录（项目 `Edits` 集合元素） |
| `Domain/Edits/UnityFieldEditModels.cs` ★ | 84 | `UnityFieldEdit`、`UnityFieldEditSet`、`UnityFieldEditSetCodec` | Unity 字段编辑的版本化记录 + JSON 编解码（v1 扁平字典兼容） |
| `Domain/Formats/FormatModels.cs` | 38 | `ModFormatKind`、`FormatDescriptor`、`FormatDiagnostic`、`DiagnosticSeverity`、`ValidationReport` | 格式标识与诊断模型；`IsValid` = 不含 Error |
| `Domain/Formats/ExportLayout.cs` ★ | 121 | `ExportGroup`、`ExportSlot`、`ExportSlotDescriptor`、`ExportLayout` | **导出目录布局唯一事实来源**（纯数据无 IO） |
| `Domain/Projects/ModProject.cs` ★ | 29 | `ModProject` | `.lmeproj` 文档根对象（`CurrentSchemaVersion=2`，三个 `ObservableCollection`） |
| `Domain/Projects/ProjectSource.cs` | 13 | `ProjectSource` | 导入到项目里的包/目录来源记录 |

### ★ `Domain/Assets/AssetModels.cs` 索引卡

- **关键 API**：`AssetRecord`(47) —— `AssetId`(49)、`LogicalPath`(50)、`SourcePath`(51)、
  `ContainerPath`(52)、`Account`/`Bundle`(53-54)、`UnityPathId`(55)、`UnityTypeId`(56)、
  `Type`(57)、`OriginalHash`/`ModifiedHash`(58-59)、`EditState`(60)、`Size`(61)、
  `Metadata`(62)、只读派生 `CatalogBaseline`(66)。
- **相关文件**：类型赋值 `Formats.Unity/UnityClassId.cs`；显示文案 `Application/Assets/AssetDisplay.cs`；
  持久化 `Application/Projects/ProjectService.cs`。
- **改动提示**：新增 `AssetType` 必须**同时**改 `UnityClassId`（映射）与 `AssetDisplay`（中文标签）；
  枚举只能**追加末尾**（数值被 UI 当筛选值下发）。`Metadata` 比较器一旦改成大小写敏感，
  所有既有工程读不到扩展数据。

### ★ `Domain/Formats/ExportLayout.cs` 索引卡

- **关键 API**：`ExportGroup`(4) / `ExportSlot`(17) / `ExportSlotDescriptor`(49) /
  `All`(76，唯一槽位清单，顺序=执行顺序) / `For`(89) / `GroupFolder`(94) /
  `FolderName`(104) / `RelativeOutputPath`(113) / `Sanitize`(117)。
- **三条消费方**（改槽位必须同步）：`Application/Build/ModExportPlanService.cs`（规划）、
  `Application/Build/ModPackExportService.cs`（执行）、
  `Application/Debugging/ModApplyService.cs`（调试应用按 `ExportSlot` 分派）。
- **改动提示**：`For`/`FolderName` 对未登记值**抛异常**是保护网，别绕过；目录字面量属于加载器口径，
  改名等于改 mod 加载行为。

### ★ `Domain/Edits/UnityFieldEditModels.cs` 索引卡

- **关键 API**：`UnityFieldEdit`(10)、`UnityFieldEditSet`(21)、
  `UnityFieldEditSetCodec.Serialize`(33)/`Deserialize`(41)/`ToPathValueMap`(64)/`HashValue`(76)，
  `CurrentSchemaVersion=2`(30)。
- **相关文件**：`Application/Assets/UnityFieldEditService.cs`（读写 `Metadata["unityFieldEdits"]`）、
  `Application/Build/UnityBundleBuildService.cs`、`UnitySerializedFileBuildService.cs`（构建期扁平化应用）。
- **改动提示**：`ToPathValueMap` 是「后者覆盖」（最新编辑胜）；`HashValue` 是小写 hex（测试按此断言）；
  改 JSON 形状必须保留 v1 扁平字典回退分支。

---

## §3 `src/LimbusModEditor.Formats.Abstractions`

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `FormatContracts.cs` | 41 | `FormatProbeResult`、`ImportContext`、`ExportContext`、`ModPackage`、`IModFormatHandler` | 格式插件的唯一接口契约（探测/导入/校验/导出 + 上下文 DTO） |

- **新增格式的做法**：一般不用改本文件 → 新建 `Formats.Xxx` 项目实现 `IModFormatHandler`
  → 在 `Application/Formats/FormatRegistry.cs` / `BuiltInFormatRegistry.cs` 注册
  → 需要新上下文参数时才加 DTO 字段（**必须给默认值**，多处调用点只传部分参数）。
- **不要破坏**：`ProbeAsync` 返回 `ValueTask`；`ModPackage.Project` 默认 `new ModProject()`；
  `PreservedFiles` 是 get-only 集合。

---

## §4 `src/LimbusModEditor.Infrastructure`

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `FileSystem/SafePathService.cs` | 15 | `SafePathService.ResolveUnder` | 相对路径 → 根目录之下的越界防护（防 zip-slip） |

> **重要事实**：全仓库 grep 显示 `SafePathService` **没有任何调用点**（未接线代码）。
> 等价校验以内联私有方法散布在 `Application/Build/ProjectBuildService.cs`、
> `Application/Debugging/DebugApplyService.cs`、`Application/Assets/ModImportService.cs` 等处。
> 想统一防护 = 把那些内联实现改为调用本服务；**只改本文件不改变任何运行时行为**。

---

## §5 `src/LimbusModEditor.Editing`（图像编解码）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `Images/UnityTextureCodec.cs` ★ | 371 | `UnityTexturePixelFormat`、`UnityTextureInfo`、`TextureFormatCapability`、`UnityTextureCodec`、`TextureFormatCatalog`、`UnityTextureMipmaps` | Unity Texture2D 负载 ↔ PNG 双向编解码（非压缩格式 + 托管 DXT1/DXT5），格式能力目录与 mipmap 布局数学 |
| `Images/ImageAtlasService.cs` | 67 | `ImageAtlasService` | 图集按网格/区域切分与回填（`SplitGrid`/`Split`/`Repack`） |
| `Images/ImagePreviewService.cs` | 56 | `ImagePreviewResult`、`ImagePreviewService` | 任意图像 → 等比缩略 PNG（含原尺寸/格式/是否含 alpha） |
| `Images/ImageRegionModels.cs` | 12 | `ImageRegion`、`ImageLayout`、`ImageSplitResult` | 图集区域/布局纯模型（`ImageLayout` 会序列化为 `layout.json`） |

### ★ `Editing/Images/UnityTextureCodec.cs` 索引卡

- **第一原则是行序**：Unity 负载**左下原点**（第 0 行 = 图像最下面一行）；
  解码翻正一次、编码翻回一次，DXT 块行号同样自下而上。
  **「预览/游戏里贴图上下颠倒」几乎总是这条约定被绕过（例如调用方又翻了一次）。**
- **关键 API**：`ToPng`、`ToPngCropped`（入参以**图像左上**为原点，调用方负责换算）、
  `ToImage`、`FromPng`、`BytesPerPixel`、`IsEncodeSupported`、
  `TextureFormatCatalog.Describe/All`、`UnityTextureMipmaps.CalculateLevels/Slice`。
- **新增一种纹理格式的五处同步点**：枚举值 → `BytesPerPixel` → `ReadPixel` → `WritePixel` →
  `TextureFormatCatalog.Describe/All`。漏改会在更深层抛 `NotSupportedException`。
- **通道坑**：`Argb32` 是 A,R,G,B（与 `Bgra32` 不同）；`R8` 写红通道而 `Alpha8` 才是 alpha；
  `Rg16` 是「两个 8 位通道」。
- **已知边界**：`ToImage`/`FromPng` 只处理 level 0（mipmap 只支持布局计算与切片）。

---

## §6 `src/LimbusModEditor.Formats.Unity`（Unity 后端）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `AssetsToolsBackend.cs` ★ | 2175 | `UnityAssetDescriptor`、`UnitySerializedObject`、`UnityBundleSerializedObject`、`UnityTextureObject`、`UnitySpriteObject`、`UnityTextAsset`、`UnityAudioClipObject`、`UnitySpriteComposite`、`AssetsToolsBackend` | **AssetsTools.NET 的唯一适配层**：bundle/SerializedFile 读取、对象枚举、容器映射、纹理/Sprite/文本/音频对象读取、字段树与字段编辑、PPtr 依赖与引用者、重打包写回 |
| `UnityAssetService.cs` | 393 | `UnityFieldNode`、`UnityObjectReference`、`UnityDependency*`、`UnityFieldEditStatus/Diagnostic`、`UnityScriptInfo`、`UnityAssetService` | 对外门面：编辑器侧记录模型 + 每次调用新建一次性 backend（**不把 AssetsTools 类型泄漏给上层**） |
| `UnityBundleInspector.cs` | 47 | `UnityBundleInfo`、`UnityBundleInspector` | UnityFS 头快速校验（签名/格式版本/Unity 版本/声明大小） |
| `UnityClassId.cs` | 157 | `UnityClassId` | Unity class id 常量 + class id/名称 → `AssetType` 映射（第 87/126 行附近是映射入口） |
| `UnityFieldValueParser.cs` | 72 | `UnityFieldValueParser` | 字段值字符串 ↔ 类型解析（字段编辑的输入校验） |
| `UnityObjectSummary.cs` | 124 | `UnityObjectSummaryField`、`UnityObjectSummary`、`UnityObjectSummaryBuilder` | Mesh/动画/字体/材质/着色器等的对象摘要卡 |
| `UnitySpriteCrop.cs` | 40 | `UnityCropRect`、`UnitySpriteCrop` | Unity 左下原点 → 图像左上原点的裁剪换算（与纹理行序约定**互为配套**） |

### ★ `Formats.Unity/AssetsToolsBackend.cs` 索引卡（2175 行，按主题定位）

| 主题 | 方法（行号） | 用途 |
|---|---|---|
| 字段树读取 | `ReadObjectFields`(106)、`ReadBundleObjectFields`(119) | 类型树驱动的字段树（含 enum 折叠、数组 `[i]` 命名、byteArray 不展开） |
| 脚本溯源 | `ReadScriptInfo`(141)、`ReadBundleScriptInfo`(153) | MonoBehaviour 的脚本信息 |
| 字段编辑校验 | `ValidateObjectFieldEdits`(171)、`ValidateBundleObjectFieldEdits`(188) | 不写盘的预校验（未知路径/不可编辑/解析失败/指针目标非法） |
| 引用与依赖 | `ReadObjectReferences`(205)、`ReadBundleObjectReferences`(219)、`ReadObjectDependencies`(240)、`FindReferencers`(270)、`FindBundleReferencers`(282) | PPtr 解析四态（Null/SameFile/ExternalFile/Missing）与反向引用查找 |
| 重打包验证 | `VerifySerializedReferences`(327)、`VerifyBundleReferences`(340) | 修改前后依赖逐项对照（内部新建独立 backend 读快照，避免缓存污染） |
| 勘察 | `BundleSerializedFileNames`(530)、`InspectBundle`(548)、`SurveyBundle`(641)、`TryLoadBundleForCrc`(690) | 枚举对象/容器路径/类型名；CRC 口径与 LCTA 一致的全块解压流 |
| 裸对象 | `ReadSerializedObjects`(659)、`ReadBundleSerializedObject`(711) | 对象原始字节 + **类型表索引**（Carra2 键所需事实） |
| 纹理 | `ReadTexture`(743)、`ReadTexturePixelData`(773)、`ReadStreamData`(793)、`ClearStreamData`(843) | 内联像素优先、否则 `.resS` 流；写回内联后**必须**清 `m_StreamData` |
| Sprite | `ReadSprite`(858)、`ReplaceSpriteMetadata`(1028)、`ReplaceBundleSpriteMetadata`(1044) | 元数据可读写；**不压平 SpriteAtlas、不改写图集网格** |
| TextAsset | `ReadBundleTextAsset`(911) | `m_Script` 支持 `ByteArray`/`String`，其他形态 fail fast |
| Sprite 合成 | `ReadBundleSpriteComposite`(936) | `m_RD.textureRect` 优先、缺失回退 `m_Rect`（真实样本实测 `m_Rect` 只是逻辑尺寸） |
| AudioClip | `ReadBundleAudioClipData`(981)、`TryReadStreamingInfo`(1016) | 四种负载形态，都不命中则抛中文异常 |
| SerializedFile 写回 | `ReplaceSerializedAsset`(1071)、`ReplaceSerializedAssets`(1095)、`ReplaceObjectFields`(1111)、`ReplaceTextureFromPng`(1156) | 临时文件 + 原子替换；纹理格式白名单 3/4/5/10/12/14 |
| Bundle 写回 | `ReplaceBundleObjectFields`(1129)、`ReplaceBundleFile`(1194)、`ReplaceBundleSerializedAsset`(1215)、`ReplaceBundleSerializedAssets`(1241)、`ReplaceBundleTextureFromPng`(1269) | 全部经 `WritePackedBundle` |
| **写盘核心** | `WritePackedBundle`(1313)、`WriteSerializedFile`(1345)、`MoveWithRetry`(1367) | **先未压缩 `Write`（应用 Replacer）→ 重载 → 按原压缩 `Pack`**；文件占用最多重试 5 次 |
| 容器映射 | `ReadContainerMap`(599) | 读 class 142 `m_Container` → 游戏内资源路径（兼容 Unity 6 `AssetInfo.asset`） |
| 字段编辑实现 | `BuildFieldNode`(1548)、`LoadTemplate`(1679)、`ValidateFieldEdits`(1800)、`ValidatePointerTarget`(1852)、`NavigateField`(1925)、`ApplyFieldEdits`(2073)、`ThrowForUnknownFieldEdits`(2117) | 类型树缺失会给中文原因；路径容忍 `Base.` 前缀；未命中路径抛 `KeyNotFoundException` |
| 生命周期 | `Dispose`(1401) | 先清缓存再卸载 bundle |

**改动定位提示**
- 「字段编辑报未找到路径」→ `ApplyFieldEdits`/`ThrowForUnknownFieldEdits`，先核对 `Base.` 前缀与 `[i]` 下标。
- 「字段树全 unknown / 不可编辑」→ `LoadTemplate`（`TypeTreeEnabled` 与类型树是否被剥离）。
- 「纹理替换后游戏仍显示旧图」→ `ClearStreamData`(843) 与其在 `ReplaceTextureFromPng` 尾部的调用。
- 「bundle 重打包后修改被丢弃」→ `WritePackedBundle`(1313) 的注释，**不要**改回直接 `Pack`
  （历史严重缺陷，见 `docs/REALDATA-VERIFY.md` §4）。
- 「导出偶发文件占用」→ `MoveWithRetry`(1367)。
- **必须保持的边界**：不要把 AssetsTools 类型暴露给上层（`UnityAssetService` 的存在意义）。

---

## §7 其他格式项目

### §7.1 `Formats.Carra`（Carra / Carra2 容器）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `CarraModels.cs` | 76 | `CarraObjectKey`(Account/Bundle/PathId/TypeId)、`CarraEntry`、`CarraPackage` | Carra 数据模型与条目读写（逐条目 XZ） |
| `CarraArchive.cs` | 91 | `CarraArchive` | 探测/读取/写出（zip 容器 + `carra.json` 标记） |
| `CarraFormatHandler.cs` | 66 | `CarraFormatHandler : IModFormatHandler` | 接入格式注册表的探测/导入/校验/导出；导出时从 `ExportContext.Codec as IXzCodec` 取编码器（第 61 行） |
| `CarraDiffService.cs` | 42 | `CarraChange`、`CarraDiffService` | 与基线包比对产出变更清单 |
| `XzCodec.cs` | 110 | `IXzCodec`、`JovelerXzCodec`、`UnavailableXzCodec`、`CarraCodec` | XZ 编解码抽象：解码用 SharpCompress，编码用 Joveler liblzma；缺原生库时回落 `Unavailable` |

- **Carra2 键口径**：`<缓存外层键(32hex)>/<内层bundle哈希>/<pathId>.<类型表索引>`，逐条目 XZ。
  `TypeTableIndex`（不是 class id）才是键的一部分。
- **XZ 报错排查**：`CarraFormatHandler.cs:61`（Codec 注入）+ `XzCodec.cs` 的 liblzma 探测与 `CanEncode`。

### §7.2 `Formats.Bank`（FMOD bank / FSB5）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `BankModels.cs` | 152 | `BankInfo`、`BankPackage`、`BankParser`、`BankFormatHandler`、`BankAssembler` | RIFF/FEV/SNDH 索引层解析（**支持事件 bank 的 size 0 SNDH**，加密 bank 拒绝重组）、探测、整体重组装（`BankAssembler.Rebuild`，第 112 行——Bank 侧唯一的写回路径） |
| `Fsb5Models.cs` | 258 | `Fsb5Chunk`、`Fsb5SampleInfo`、`Fsb5Info`、`Fsb5Parser` | FSB5 头/块/子样本表解析（0x3C / 0x40 基头，含 codec 与**真实样本名**） |
| `FmodCodec.cs` | 121 | `FmodLibraryStatus`、`FmodCodecLibrary`、`IFmodAudioCodec`、`UnavailableFmodAudioCodec` | FMOD 运行库发现与编解码器选择（无 DLL 时明确不可用） |
| `NativeFmodAudioCodec.cs` | 128 | `NativeFmodAudioCodec` | P/Invoke 绑定**公开** FMOD/FSBank C ABI：FSB→WAV 预览、WAV→FSB 替换（含 FMOD 2.x `headerversion` 兼容） |
| `FmodDllInspector.cs` | 174 | `FmodDllReport`、`FmodDllInspector` | DLL 导出符号/版本勘察（判定能否解码/编码） |
| `FmodCompatibilityService.cs` | 100 | `FmodProbeCache`、`FmodCompatibilityService` | 探测结果缓存与兼容性汇总 |

- **两类 bank 的分流**：`BankModels.cs` 的解析分支区分事件 bank（空 SNDH）与音频 bank（SNDH → FSB5）；
  加密 bank 在重组时明确拒绝。
- **DLL 候选名单不一致**：`FmodCodecLibrary.TryLoad`（`FmodCodec.cs:38-40`）与
  `FmodDllInspector.InspectDirectory`（`FmodDllInspector.cs:53`）的候选文件名不同，
  若要统一必须**同改两处**。
- **不支持**：加密 bank、未知 FSB 变体 → 明确报错，不尝试解码。

### §7.3 `Formats.Rebank` 与 `Formats.Lunartique`

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `Formats.Rebank/RebankPackage.cs` | 142 | `RebankFile`、`RebankPackage`、`RebankDiffService`、`RebankArchive`、`RebankFormatHandler` | `.rebank` 差分音频包：`rebank.json` + `{fsbIndex}/{样本名}.wav`，`base_bank` 为纯文件名 |
| `Formats.Lunartique/LunartiqueArchive.cs` | 112 | `LunartiqueResource`、`LunartiquePackage`、`LunartiqueArchive` | 安装/卸载配对包读写（`Installation`/`Uninstallation` 双目录，条目为 `__data`） |
| `Formats.Lunartique/LunartiqueFormatHandler.cs` | 59 | `LunartiqueFormatHandler : IModFormatHandler` | 格式注册表接入 |

- **`.rebank` 条目名必须是真实样本名**（历史缺陷：写 `{i}.fsb` 时加载器一条都匹配不上并回滚）。

---

## §8 `src/LimbusModEditor.Application`（服务层，WPF 无关、可单测）

### §8.1 `AppConfig/`（全局配置与环境）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `AppEnvironment.cs` ★ | 225 | `FmodDiscovery`、`FmodLibraryLocator`、`SharedAutoConfigureReport`、`AppEnvironment` | 进程级环境单例：程序目录/配置目录/缓存目录/项目目录；有效目录解析（共享→项目→自动发现）；旧项目值迁移；最近项目 |
| `SharedAppConfig.cs` | 77 | `RecentProject`、`SharedAppConfig`、`SharedConfigService` | `config/shared-config.json` 读写（原子写 + 损坏备份回退） |
| `UiStateService.cs` | 141 | `WorkbenchPageKeys`、`UiStateService` | `config/ui-state.json`：每页预览列宽（钳制 260–2000）；旧字段双向兼容 |

- **目录解析优先级**：共享配置（手动值）→ 项目旧字段 → 自动发现。
  自动配置**只填空位**，永久不覆盖手动值。
- **`FmodLibraryLocator.Discover`(25)** 顺序：`<程序目录>/fmod` → 程序目录 →
  游戏 `LimbusCompany_Data/Plugins/x86_64` → 游戏目录；**带编码 DLL 的候选优先返回**。

### §8.2 `Projects/`

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `ProjectService.cs` ★ | 110 | `IProjectService`、`ProjectService`、`SkipReferenceAssetsConverter` | `.lmeproj` 加载/保存、SchemaVersion 校验、纯引用资产不落盘（项目瘦身，写入时跳过 `Metadata["reference"]=="true"`）、打开时从索引后台回灌 |

### §8.3 `Assets/`（资源视图与编辑服务）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `AssetDisplay.cs` ★ | 248 | `AssetSortKind`、`AssetDisplay` | **显示层口径**：容器条目路径、叶子名消歧、中文类型/状态标签、自然名称比较 |
| `AssetSearchService.cs` ★ | 120 | `AssetSearchQuery`、`AssetSearchService` | 合并搜索（文本匹配显示路径/原路径/源文件）+ 类型/状态/大小/容器过滤 + 排序 |
| `AssetTreeBuilder.cs` | 131 | `AssetTreeNode`、`AssetTreeBuilder` | 按显示路径逐段**惰性**建树（根层一次构建，展开才分组下一层） |
| `AssetPropertyService.cs` | 342 | `AssetPropertyRow`、`AssetPropertyService` | 选中资源的属性行（中文标签，单项失败降级不整体失败） |
| `AssetEditService.cs` | 236 | `AssetReplacementResult`、`BatchReplacementReport`、`AssetEditService` | 替换/批量替换/撤销（`ClearEdits`）、暂存到项目 `edits/assets/`、`HasEdits` 口径 |
| `TextAssetEditService.cs` | 53 | `TextAssetDocument`、`TextAssetEditService` | 文本资源（TextAsset/.json）编辑，保存注册为普通可逆替换 |
| `TextPreviewService.cs` | 118 | `TextPreview`、`TextPreviewService` | 只读文本预览：编码识别（UTF-8/UTF-16）、二进制与非 UTF 拒绝、超长截断 |
| `SpriteMetadataEditService.cs` | 74 | `SpriteMetadataEditService`、`UnitySpriteMetadata` | Sprite 元数据（rect/pivot/border/ppu）编辑记录 |
| `ImageAtlasEditService.cs` | 62 | `AtlasSplitResult`、`ImageAtlasEditService` | 纹理→图集拆分落盘 + 回填（`layout.json` + `<regionId>.png`） |
| `UnityFieldEditService.cs` | 104 | `UnityFieldEditDraft`、`UnityFieldEditService` | Unity 字段编辑的读写与校验（写 `Metadata["unityFieldEdits"]`） |
| `ModImportService.cs` | 352 | `ImportResult`、`ModImportService` | 导入包/目录/bundle/zip 到项目（含 catalog 基线判定与来源登记） |
| `BankTreeRules.cs` | 72 | `BankTreeKindFilter`、`BankTreeRules` | bank 树纯规则（默认隐藏事件 bank、单 FSB 自动展开） |
| `BankAudioService.cs` | 58 | `BankFsbInspection`、`BankAudioService` | FSB 样本试听/替换（临时 WAV + 写回） |
| `BankDirectoryService.cs` | 371 | `BankKind`、`BankFileEntry`、`RiffChunkInfo`、`BankFsbTable`、`BankSampleTable`、`BankDirectoryService` | bank 目录解析/分类/RIFF 块与 FSB 表读取 |
| `BankIndexService.cs` | 271 | `BankIndexService`、`BankIndexRefreshSnapshot` | bank 索引刷新（并行度 ≤4、进度、可取消、按 `(size,mtime)` 增量） |
| `BankIndexStore.cs` | 373 | `BankIndexStore` | `cache/bank-index.db` 读写（banks/samples 表）+ `DeleteDatabase`/`ClearTables` |
| `BankIndexModels.cs` | 154 | `BankIndexSource`、`BankSampleRecord`、`BankIndexEntry`、`BankFileObservation`、`BankIndexSnapshot` | bank 索引的数据模型与源签名口径 |

### §8.4 `Assets/Preview/`（预览管线）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `AssetPreview.cs` ★ | 138 | `AssetPreviewKind`、`AssetPreviewRow`、`AssetPreviewAudio`、`AssetPreview`、`IAssetPreviewProvider`、`AssetPreviewRegistry` | 预览服务定位：按资产类型选择 provider，未知类型兜底十六进制 |
| `AssetPreviewProviders.cs` | 602 | `PreviewRead`、Texture/Sprite/Audio/Text/Script/Summary/Material/Shader/VideoClip/SpriteAtlas/Hex 各 Provider | 七形态预览的具体实现（图像、文本行号 + JSON 树、音频波形、摘要卡、只读字段树、十六进制…） |
| `HexDumpService.cs` | 162 | `HexDumpService`、`HexDump`、`AudioWaveform` | 十六进制转储（截断 + ASCII 边栏）与波形包络计算（只接 16-bit PCM WAV） |

### §8.5 `Scanning/`（扫描与索引）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `StartupScanService.cs` ★ | 747 | `StartupScanStepStatus/Result`、`StartupScanProgress/Report`、`CacheTableRowCount/View`、`StartupScanService` | **启动扫描总编排**：四库建库校表 → 游戏资源 → 音频 → 静态表 → 文本（逐步容错、进度、报告）；`CacheTableCatalog`/`CacheTableRows` 是缓存清单与诊断的唯一来源 |
| `UnityCacheScanService.cs` | 561 | `UnityCacheScanEntry`、`UnityCacheScanProgress/Result`、`UnityCacheScanService` | Unity 缓存**引用模式**增量扫描（不复制文件），容器条目与 catalog 基线写进索引，逐 bundle 容错 |
| `UnityCacheSqliteIndexStore.cs` | 297 | `UnityCacheIndexRow`、`UnityCacheIndexBundle`、`UnityCacheSqliteIndexStore` | `cache/unity-cache-index.db` 读写（**不用 `index_meta`**，新鲜度落在 `bundles(size, mtime_ticks)`；容器条目/静态标记按列轻量迁移） |
| `UnityCacheMaterializationService.cs` | 64 | `UnityCacheMaterializationService` | 编辑过的引用资源所属 bundle 实体化到项目 `sources/cache/<外>_<内>.bundle` |

### §8.6 `Caching/`（表缓存底座）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `WorkbenchCachePaths.cs` | 63 | `WorkbenchCacheKind`、`WorkbenchCachePaths` | 三个工作台缓存库路径集中定义（+ 既有 unity 索引库名） |
| `WorkbenchCacheSchema.cs` ★ | 127 | `WorkbenchCacheSchema` | 三库表结构唯一定义（`index_meta` 共同契约 + bank/static/text 业务表） |
| `SqliteTableCache.cs` | 387 | `SqliteTableCache` | SQLite 通用底座：事务/WAL/`EnsureSource`（源或签名变即整库重建）/`EnsureColumn` 轻量迁移/缺表自愈/损坏删库重建 |
| `CacheSignature.cs` | 79 | `CacheSignature` | 源签名 `"length:mtimeTicks"` 口径（目录签名长度恒 0） |

- **共同契约**：`index_meta(source_key, signature, language_prefix)`；
  只存 **vanilla 事实**，编辑集绝不落缓存；失效只有「源签名变化」一条；
  加列走 `EnsureColumn`，不要改已发布建表脚本。

### §8.7 `Texts/`（lang 文本工作台）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `LangTextWorkbenchService.cs` ★ | 611 | `LangTextFileInfo`、`LangTextSearchKind/Hit`、`LangTextExportFileStatus/Report`、`LangTextWorkbenchService` | lang 根与活动语言目录解析、文件枚举/搜索、读取与编辑集、`ToPatchKey` 口径换算 |
| `LangEditSession.cs` ★ | 119 | `LangEditSession`、`LangEditEntry` | **宿主持有**的文本编辑集会话（`Revision`、`HasRealEdits`、`Snapshot()`） |
| `TextIndexStore.cs` ★ | 471 | `TextIndexHitKind`、`TextIndexStore`、`TextIndexSource` | `cache/text-index.db` 读写；条目口径 = 相对活动语言目录，`index_meta.language_prefix` 拼回磁盘路径；口径版本 `IndexFormatVersion = "v4"` |
| `LangTextTreeBuilder.cs` | 140 | `LangTextTreeNode`、`LangTextTreeNodeKind`、`LangTextTreeBuilder` | JSON → 键值树（默认展开两层、单层超 200 项分块追加；UI 不渲染根节点） |
| `LangTextDisplay.cs` | 40 | `LangTextDisplay` | 键路径/文本的显示与截断纯映射 |
| `LangTextPatchService.cs` | 190 | `LangPatchEntryStatus`、`LangPatchDocument`、`LangTextPatchService` | `patchs` 文档读写、目录差分、应用到 lang 目录 |
| `LangExportFormatter.cs` | 346 | `LangExportFormat`、`LangFormatResult`、`LangExportFormatter` | 三套语言格式（`bus` / `patch` / `pathset`）生成 + **可表达性门**（表达不了整份不产出） |
| `TextDiffService.cs` | 294 | `TextDiffService` | RFC6902 差异生成/应用（与 LCTA `changes.py` 兼容；含 JSON null 语义修正） |
| `StaticEditSession.cs` | 91 | `StaticEditSession`、`StaticEditEntry` | 静态数据表编辑集会话（与 `LangEditSession` 同构） |

### §8.8 `StaticMods/`（静态数据表）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `StaticIndexService.cs` | 130 | `StaticIndexService` | 静态 bundle 定位（`Locate`）+ 表索引构建（`RebuildAsync`、`CacheRoots`） |
| `StaticIndexModels.cs` | 132 | `StaticIndexSource`、`StaticTableEntry`、`StaticTableDocument`、`StaticIndexLoad`、`StaticIndexBuildResult` | 静态索引模型与源签名（内层内容哈希） |
| `StaticTableIndexStore.cs` | 379 | `StaticTableIndexStore` | `cache/static-tables.db`（元数据 + **按需有界**正文缓存，LRU 淘汰上限 64MB） |
| `StaticBundleLocator.cs` | 331 | `StaticBundleLocation`、`StaticBundleLocator`、`StaticTextAsset` | 由 catalog 动态解析静态 bundle（内层/外层键，**不硬编码**），读静态表正文 |
| `StaticModService.cs` | 272 | `StaticModPatchEntry`、`StaticModFullFileEntry`、`StaticModPackage`、`StaticModService` | staticmod/v1 包读取/写出/补丁预览/从 JSON 差分生成（**只支持 `opType=jsonpatch`**） |

### §8.9 `Build/`（导出与构建流水线）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `ModExportPlanService.cs` ★ | 247 | `BankEdit`、`ModExportPlanItem`、`ModExportPlan`、`ModExportPlanContext`、`ModExportPlanService` | 分析项目实际修改 → 点亮哪些槽位（导出计划 + 逐槽位 `SkipReason`） |
| `ModPackExportService.cs` ★ | 546 | `ModPackSlotResult`、`ModPackExportResult`、`ModPackExportService` | 按槽位执行导出（bank/rebank/carra/lunartique/lang×3/staticmod）；`LoadBank`/`ApplyAudioEditsAsync` 是**导出与调试共用的音频语义** |
| `ExportAdvisor.cs` | 154 | `ExportIdeaKind`、`ExportIdea`、`ExportAdvisorContext`、`ExportAdvisor` | 给用户的出口建议文案（含中文理由与前置条件） |
| `ExportMatrix.cs` | 75 | `ExportCompatibility`、`ExportMatrix`、`ExportAssetStatus` | 资产 × 格式的兼容性矩阵（向导启用/禁用目标） |
| `UnityBundleBuildService.cs` | 149 | `UnityBundleBuildResult`、`UnityBundleBuildService` | 把替换/字段编辑/Sprite 元数据应用到 bundle 并重打包（含引用完整性验证） |
| `UnitySerializedFileBuildService.cs` | 133 | `UnitySerializedFileBuildResult`、`UnitySerializedFileBuildService` | 独立 `.assets` 文件版本 |
| `UnityCacheExportService.cs` | 128 | `UnityCacheExportService` | 缓存 bundle → Carra2 一键导出（真实加载器键口径；中间产物 `<项目>/builds/unity-bundles/`） |
| `LunartiqueCarraConversionService.cs` | 83 | `LunartiqueConversionDiagnostic`、`LunartiqueCarraConversionResult`、`LunartiqueCarraConversionService` | 改后 bundle + 缓存原版 bundle 派生 Lunartique 两侧条目（缺缓存整份跳过并说明） |
| `ModExportService.cs` | 372 | `ModExportResult`、`MultiFormatExportItem`、`MultiFormatExportResult`、`ModExportService` | 旧的多格式导出通道（CLI 与 5 个测试文件仍用；界面不再暴露，**别顺手删**） |
| `ProjectBuildService.cs` | 69 | `BuildResult`、`ProjectBuildService` | 项目级构建（按格式分发 + 导出前校验 + 旧式 overlay） |
| `NewModTemplateService.cs` | 114 | `NewModTemplateRequest`、`NewModTemplateResult`、`NewModTemplateService` | 新建模组脚手架（默认项目目录 = `<程序目录>/projects/<名>/`） |
| `AtomicOutput.cs` | 98 | `AtomicOutput` | 原子写盘（临时文件 + 替换）——所有产物落盘统一走它 |

### §8.10 `Catalog/`（官方 catalog 只读解析）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `CatalogFileService.cs` | 194 | `CatalogVerdict`、`CatalogBundleRecord`、`CatalogBaselineResult`、`CatalogFileService` | `catalog.bin`/`catalog_S1.bin` 只读解析（**双布局自校准**：记录区 CRC/size 字段随格式版本整体平移） |
| `CatalogBaselineService.cs` | 93 | `CatalogBaselineService` | vanilla/修改/不在 catalog/未知 四态判定（供导入与扫描打 `catalogBaseline`） |

### §8.11 `Debugging/`（目录定位、启动、调试应用）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `GameDirectoryLocator.cs` | 81 | `GameDirectoryLookup`、`GameDirectoryLocator` | 扫 Steam 库等候选根定位游戏目录 |
| `UnityCacheLocator.cs` | 75 | `UnityCacheCandidate`、`UnityCacheLocator` | 定位 Unity 缓存根（按 `__data` 条目计数择优） |
| `ModDirectoryLocator.cs` | 33 | `ModDirectoryLocator` | 定位 `%APPDATA%\LimbusCompanyMods`（`_disable` 约定） |
| `ProjectAutoConfigureService.cs` | 66 | `AutoConfigureReport`、`ProjectAutoConfigureService` | 项目级自动配置报告 |
| `ModInstallService.cs` | 53 | `InstalledModEntry`、`ModInstallService` | 已安装模组枚举/安装 |
| `GameLaunchService.cs` | 44 | `GameLaunchResult`、`GameLaunchService` | 启动游戏（含可执行文件校验、运行中判定） |
| `ModApplyService.cs` ★ | 540 | `ModApplyStep`、`ModApplyKind`、`ModApplyReport`、`ModApplyService` | **调试应用**：按槽位语义铺到游戏目录/缓存（逐文件备份 + `steps.tsv` + 关闭还原 + 冲突不覆盖 + 中途失败回滚） |
| `StaticModApplyService.cs` | 238 | `StaticModApplyService`、`StaticCatalogSlot` | 静态模组调试应用（catalog 双写，写前用 size 合理性自校准布局） |
| `DebugApplyService.cs` | 93 | `DebugFileChange`、`DebugApplySession`、`DebugApplyService` | 旧的覆盖层调试应用（备份/还原基础设施，`DebugApplyTests` 覆盖） |

### §8.12 `Documents/` 与 `Formats/`

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `Documents/JsonDocumentEditor.cs` | 392 | `JsonValueKind`、`JsonEditRow`、`JsonDocumentEditor` | JSON 文档的键值行模型 + 校验/写回（键值树就地编辑与静态表编辑共用；不截断 6000 行） |
| `Formats/FormatRegistry.cs` | 21 | `FormatRegistry` | 多处理器探测择优（按 `Confidence`）+ 按格式解析处理器 |
| `Formats/BuiltInFormatRegistry.cs` | 17 | `BuiltInFormatRegistry` | 内置处理器装配（Bank/Carra/Rebank/Lunartique 等） |

---

## §9 `src/LimbusModEditor.App`（WPF 界面层）

> 本层**没有测试工程**：任何值得测的逻辑都要下沉到 `Application`。

### §9.1 外壳与主题

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `App.xaml` / `App.xaml.cs` | 24（.cs） | `App` | 应用启动、全局异常与主题初始化 |
| `MainWindow.xaml` ★ | 158 | 三列布局 XAML | 活动栏 6 入口、共享侧边栏（① 获取资源 / ② 产出模组 / 更多）、页面宿主 `PageHost`、无项目遮罩 |
| `MainWindow.xaml.cs` ★ | 870 | `MainWindow : FluentWindow, IWorkbenchHost` | 页面注册表/切换、启动编排、项目打开保存、扫描、导出、调试、目录状态、提示条 |
| `AppTheme.cs` | 31 | `AppTheme` | 主题/画刷装配辅助 |
| `StartupTrace.cs` | 39 | `StartupTrace` | 启动阶段埋点（排查「窗口出现太慢」） |
| `AssemblyInfo.cs` | 10 | — | 程序集属性 |
| `HumanSizeConverter.cs` | 24 | `HumanSizeConverter` | 字节数 → 人类可读（列表大小列） |
| `AssetRow.cs` | 29 | `AssetRow` | 资源列表行视图模型（`Name`/`TypeLabel`/`StateLabel`/`Size`/`DisplayPath`） |
| `Themes/Theme.xaml` | 74 行 / 4945 B | 设计令牌 | 主题色与基础样式（**设计色只允许在此与 WorkbenchStyles 出现**） |
| `Themes/WorkbenchStyles.xaml` | 407 行 / 27893 B | `Workbench*` 样式/画刷 | 工作台共享样式与画刷（树模板、筛选下拉、行 hover/selected 等） |

### ★ `App/MainWindow.xaml.cs` 索引卡

| 关注点 | 位置 |
|---|---|
| 页面注册表与切换 | `PageOrder`(43)、`_pages`(44)、`ShowPage`(124)、`CreatePage`(140)、`SyncActivityBar`(161)、`NeedsProject`(177) |
| 活动栏事件 | `ActivityAssets_Click`(179) … `ActivitySettings_Click`(184) |
| 启动编排 | 构造函数（只建页面）(101-119)、`Loaded` → `OnWindowLoadedAsync`(195)、启动扫描 |
| 项目 | `NewModWizard_Click`(362)、`OpenProject_Click`(376)、`SaveProject_Click`(481) |
| 资源与产出 | `Scan_Click`(520)、`ExportMod_Click`(535)、`DebugMod_Click`(592) |
| **导出/调试共用前奏** | `PrepareExportPlanAsync(rootDirectory, progress)`(658)：实体化已编辑资产 → 存项目 → 建导出计划与上下文（两条链路必须同一份分析与上下文） |
| 目录 | `AutoConfigure_Click`(748)、`OpenModsDirectory_Click`(906)、`OpenProjectFolder_Click`(920)、`UpdateDirectoryStatus` |
| 宿主契约实现 | `Project`/`ProjectFile`/`Env`/`LangEdits`/`StaticEdits`/`SetStatus`/`RefreshProjectState`/`RefreshDirectorySettings`/`SaveProjectAsync` |

- **改界面文案/布局**：先看 `MainWindow.xaml`（活动栏、侧边栏），再按页面 key 找 `WorkbenchPages/` 下对应页面。
- **不要把页面逻辑写回 MainWindow**：新增页面 = `CreatePage` 加分支 + 活动栏按钮 + `SyncActivityBar` 映射。
- **目录定位器有进程级记忆化**（`_memo*` 字段 + `ResetLocatorCache`(58)）：改设置后必须失效，否则状态栏显示旧值。
- **启动顺序有性能约束**：任何秒级磁盘动作都排到 `Loaded` + `DispatcherPriority.Background` 之后。

### §9.2 `WorkbenchPages/`（页面与共享骨架）

| 文件 | 行 | 关键类型 | 职责 |
|---|---|---|---|
| `IWorkbenchHost.cs` ★ | 42 | `IWorkbenchHost` | 页面宿主契约（项目、环境、编辑集会话、状态、刷新、保存、跳页） |
| `WorkbenchShell.xaml(.cs)` | 533（.cs） | `WorkbenchViewMode`、`WorkbenchViewToggles`、`WorkbenchShell` | 三列页面骨架：列宽持久化（`UiStateService`）、列表↔树切换、滚轮/分节辅助 |
| `JsonTreeEditor.xaml(.cs)` | 859（.cs） | `JsonDocumentChangedEventArgs`、`JsonTreeRowView`、`InverseBoolToVisibilityConverter`、`JsonTreeEditor` | 键值树编辑器：树/原文双 Tab、**行内就地改值**（Enter 提交 / Shift+Enter 换行 / Esc 取消 / 失焦提交）、右键菜单与 `Delete` 删除键、RFC6902 差异摘要 |
| `AssetsWorkbenchPage.xaml(.cs)` ★ | 1435（.cs） | `AssetsWorkbenchPage` | 资源工作台：筛选栏 + 列表/容器树 + 预览列 + 属性区 + 替换/导入/撤销 |
| `BankWorkbenchPage.xaml(.cs)` ★ | 1146（.cs） | `BankWorkbenchPage` | 音频工作台：全部音频总表 / bank 树双视图、试听与进度条、FSB 替换、bank 导出 |
| `TextWorkbenchPage.xaml(.cs)` ★ | 823（.cs） | `TextFileRow`、`TextHitRow`、`TextWorkbenchPage` | 文本工作台：lang 文件树 + 键值树 + 源文本预览 + 搜索命中跳转 |
| `StaticWorkbenchPage.xaml(.cs)` ★ | 575（.cs） | `StaticWorkbenchPage` | 静态数据工作台：dataClass 树 / 表列表 + JSON 编辑 + 差异视图 |
| `SettingsPage.cs` | 252 | `SettingsPage` | 设置页（共享目录 + 项目元数据），无项目也可用 |
| `HelpPage.cs` | 154 | `HelpPage` | 内置教程（`docs/USAGE.md` 的精简版），无项目也可用 |

### ★ `App/WorkbenchPages/IWorkbenchHost.cs` 索引卡

页面能拿到的东西（越界即设计问题）：`Project`、`ProjectFile`、`Env`（`AppEnvironment`）、
`LangEdits`/`StaticEdits`（宿主持有的编辑集会话）、`SetStatus`、`RefreshProjectState`、
`RefreshDirectorySettings`、`SaveProjectAsync`、`ShowPage`。
**页面之间不互相引用**，跳转统一走 `ShowPage(key)`。

### ★ 四个页面 + 骨架的成员定位速查（按功能）

**`WorkbenchShell.xaml.cs`（共享骨架，533 行）**
`Attach(host, pageKey)`(60) 绑定宿主与页 key；`SetPreviewColumnWidth`(103)/`ResetPreviewColumnWidth`(112)/
`PersistPreviewWidth`(120)/`PersistAllPreviewWidths`(550) 列宽持久化；
`SetBrowseContent`(125)/`SetEditContent`(128)；`AddSearchItem`(131)/`AddFilterItem`(138)/
`AddViewToggleItem`(145)/`AddViewToggles`(153) 组装筛选行；`SetViewMode`(180)/`ApplyViewMode`(182)；
`SetStatus`(195)/`SetEmptyHint`(198)；共享控件工厂 `CreateList`(212)/`CreateTree`(224)/
`CreateButton`(236)/`CreateSearchBox`(249)/`CreateFilterCombo`(261)/`CreateSectionLabel`(298)/
`CreatePanelTitle`(306)/`CreateCodeBox`(309)/`CreateValueBox`(313)；
**`EnableWheelScrolling`(375)**（修「悬停 JSON 树时滚轮失效」：收已处理事件 + 显式滚动 + 合成事件重入保护）。

**`JsonTreeEditor.xaml.cs`（键值树，859 行）**
`LoadDocument`(178)/`Clear`(198)/`SelectPath`(222) 载入与定位；`RebuildTree`(243)/
`CreateItem`(273)/`MaterializeChildren`(312)/`AppendChildren`(321)/`CreateOverflowItem`(333)
分块追加（避免一次物化上万行）；`SetEmptyState`(410) 空态文案；
**行内编辑**：`Tree_MouseDoubleClick`(419) → `BeginEditing`(444) → `AttachEditBoxWhenReady`(471) +
`RetryAfterFrames`(502)（确定性重试链，**不要**改回「查一次查不到就放弃」）→ `CommitEditing`(713)/
`CancelEditing`(736)/`EndEditing`(747)；按键与焦点：`Tree_PreviewKeyDown`(607)（Enter/Esc 隧道）、
`Editor_PreviewLostKeyboardFocus`(641)、`IsInsideEditSession`(682)、`DispatchCommit`(662)；
删除键：`AttachRowContextMenu`(792)/`DeleteRow`(806)；原文 Tab：`SaveRaw_Click`(834)/`AdoptText`(863)；
差异：`UpdateDiffSummary`(877)/`RefreshDiffSummary`(910)。

**`AssetsWorkbenchPage.xaml.cs`（1435 行）**
生命周期/宿主：`OnProjectRefreshed`(73)、`PersistUiState`(108)；
搜索：`SearchBox_TextChanged`(164)/`Filter_Changed`(171/180)/`RequestSearch`(189)（300ms 防抖）、
`BuildSearchQuery`(240)、`RunSearchAsync`(279)（后台线程 + 代际守卫）；
选择与预览：`ApplyAssetSelection`(323)、`RestoreListSelection`(339)、`RefreshSelectionButtons`(357)、
`UpdatePreviewAsync`(394)（异步纹理预览）、`UpdatePropertiesAsync`(418)、`BuildPreviewView`(439) 及
`BuildImageView`(469)/`BuildTextView`(607)/`BuildAudioView`(761)/`BuildRowsView`(830)/`BuildHexView`(871)；
音频试听：`PlayCurrentAudioAsync`(927)/`StopAudioPreview`(974)；编辑动作：
`SplitAtlas_Click`(1004)/`RepackAtlas_Click`(1021)/`ReplaceAsset_Click`(1043)/`ClearEdits_Click`(1070)/
`BatchReplace_Click`(1173)/`EditTextAsset_Click`(1117)/`EditUnityFields_Click`(1228)/
`InspectSprite_Click`(1208)/`InspectFsb_Click`(1296)/`FindReferencers_Click`(1312)/
`ShowObjectSummary_Click`(1356)/`DecodeAudio_Click`(1389)；
视图：`AssetViewMode_Click`(1417)/`RebuildTree`(1430)/`MakeTreeItem`(1438)/`AssetTree_Expanded`(1449)；
拖放：`CanAcceptImageDrop`(131)、`HandleImageDropAsync`(141)。

**`BankWorkbenchPage.xaml.cs`（1146 行）**
刷新与索引：`ReloadFromIndexAsync`(399)、`RefreshAsync`(401)、`RunIndexAsync`(434)、`ApplySnapshot`(477)；
筛选：`RebuildCodecFilter`(528)/`ClearFilters`(546)/`ToKindFilter`(559)/`ApplyFilter`(570)；
视图与树：`SwitchView`(645)（全部音频 ⇄ bank 树）、`RebuildTree`(676)、`TreeItem_Expanded`(718)（惰性）、
`BuildFsbNodes`(734)、`BuildSampleNodes`(768)（占位哨兵判定「这层建过没有」）；
选择与详情：`OnTreeSelectionChanged`(790)/`SelectBank`(818)/`LocateInTree`(831)/
`LoadEditorForBank`(860)/`LoadEditorForSample`(882)/`RefreshSampleDetail`(894)；
播放：`StartPlaybackUi`(973)/`UpdatePlaybackUi`(988)/`PlayBar_PreviewMouseDown`(1001)/`SeekToPointer`(1010)；
音频动作：`AuditionAsync`(1029)/`ExportSampleWavAsync`(1064)/`ExtractFsb`(1091)/`ReplaceSampleAsync`(1101)；
实体化与资产：`MaterializeBank`(1139)/`EnsureSampleAsset`(1161)；
缓存：`ClearCache`(1193)。

**`TextWorkbenchPage.xaml.cs`（823 行）**
`ReloadFromIndexAsync`(382)/`RefreshAsync`(388)；浏览：`SetBrowseItems`(476)/`ApplyFileFilter`(494)/
`ApplyViewMode`(520)/`RebuildTree`(543)/`CreateTreeItem`(561)/`BuildTreeHeader`(578)/
`TreeItem_Expanded`(599)/`MaterializeChildren`(606)；搜索：`RunSearchAsync`(653)/`ExitSearchMode`(688)/
`NavigateToHitAsync`(696)；**载入时序**：`SelectFileAsync`(711)（同一文件不重复重建编辑器、
`Clear()` 移到读取成功之后、重建后同步选中态、`SelectPath` 延后到布局完成并如实回报定位结果）、
`ReportKeyLocation`(776)/`SelectRowInViews`(789)；
编辑集：`Editor_DocumentChanged`(815)/`RevertFile_Click`(844)/`RefreshEditSetState`(864)
（「已修改」标记一律走 `HasRealEdits`）；`ClearCache`(528)。

**`StaticWorkbenchPage.xaml.cs`（575 行）**
`ReloadFromIndexAsync`(190)/`RefreshAsync`(192)（含 `DescribeLocateFailure`(251) 的中文失败指引）；
筛选：`ApplyEntries`(259)/`ClearFilters`(268)/`FilteredEntries`(276)/`ApplyFilter`(305)；
视图：`SwitchView`(323)/`RebuildTree`(333)/`TreeItem_Expanded`(354)/`OnTreeSelectionChanged`(372)；
文档：`SelectTableAsync`(380)/`OnDocumentChanged`(423)/`SaveCurrentEdit`(432)/`UpdateDiffInfo`(449)/
`RevertTableAsync`(480)/`RefreshActionButtons`(490)/`ClearDocumentCache`(501)；
导出：`ExportStaticModAsync`(520)。

### §9.3 对话框与工具窗口

| 文件 | 行 | 职责 |
|---|---|---|
| `StartupScanDialog.cs` ★ | 437 | 启动扫描统一模态（打开即扫、**不可取消**、完成自动消失、有跳过/失败才留下），含四个缓存库行视图 |
| `UnityFieldEditorWindow.cs` | 442 | Unity 字段树编辑窗口（写回校验、指针目标提示） |
| `NewModWizardWindow.cs` | 191 | 新建模组向导（只问模组名，目录默认走共享配置） |
| `UnityCachePickerWindow.cs` | 57 | Unity 缓存目录选择 |
| `TextAssetEditorWindow.cs` | 95 | 文本资源编辑（JSON 保存前校验并格式化） |
| `SpriteMetadataWindow.cs` | 57 | Sprite 元数据编辑 |
| `HexPreviewWindow.cs` | 90 | 十六进制预览（优先显示替换文件） |
| `ObjectSummaryWindow.cs` | 75 | 对象摘要卡窗口（Mesh/动画/字体等） |
| `BankInspectorWindow.cs` | 140 | bank 头/块/FSB 表检查 |
| `FmodReportWindow.cs` | 93 | FMOD DLL 勘察报告 |
| `ExportReportWindow.cs` | 114 | 旧导出报告（旧导出通道） |
| `ExportProgressWindow.cs` | 50 | 导出阶段进度（新导出/调试也复用） |
| `ModExportReportWindow.cs` | 151 | **新导出报告**（槽位结果、空槽位说明） |

---

## §10 `src/LimbusModEditor.Cli`

| 文件 | 行 | 职责 |
|---|---|---|
| `Program.cs` | 67 | 三个子命令：`probe <package>` / `import <project.lmeproj> <file-or-dir>` / `export <project.lmeproj> <output> [source]`；顶层 try/catch 输出中文错误并以退出码 1/2 结束 |

---

## §11 `tests/`

两个测试工程：`LimbusModEditor.Domain.Tests`（537 例，引用 Domain/Editing/Application/Formats.Unity）
与 `LimbusModEditor.Format.Tests`（74 例，引用各 Formats.*）。
**611 例全绿**（2026-09-12 实测，`dotnet test LimbusModEditor.slnx`）。

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
- **墙钟断言（慢机易 flake）**：`RealBankIndexSmokeTests` 读索引 < 1.5s（已知 flake，整包并行偶发，单独跑通过）、
  `RealStaticIndexSmokeTests` 热读 < 1.5s 且 < 直接枚举耗时/2、`BankDirectoryServiceTests` 扫 1531 bank < 60s、
  `PerformanceBaselineTests` 断言资产数 > 10 万（缩小缓存场景会失败）。

### §11.3 不要顺手改的测试约定

- **两个测试工程都不引用 `LimbusModEditor.App`**：页面行为只被「镜像约定」测试间接守住——
  `AssetFilterComboSentinelTests` ↔ `AssetsWorkbenchPage` 的筛选哨兵（防下拉空白）、
  `BankTreeRulesTests` ↔ `BankWorkbenchPage.RebuildTree`（防空树）、
  `UiStateServiceTests` 的四页列宽合并 ↔ `WorkbenchShell.SavePreviewWidth`。
  要改行为就改页面的约定，**不要改这些测试**。
- `Domain.Tests/UnitTest1.cs`（空 `Test1`，无断言）与 `Format.Tests/UnitTest1.cs`（类名 `FormatTests`，**4 条真测试**）
  **含义完全不同**，删「看起来是模板」的文件会删掉真实覆盖。
- 口径类断言（`LangEntryCaliberTests`、`LangTextDisplayTests`）是**双向契约**：
  「顺手统一路径口径」会同时打红 `TextIndexStoreTests`、`RealTextIndexSmokeTests`、`ModExportPlanTests`、`LangExportFormatterTests`。
- SQLite 表/列名断言在测试里，schema 迁移必须同步改测试，不能只改产品代码。
- 全仓测试**没有一处 xunit `Skip`**：所有门控都是测试体内 `return`（见 §11.1）。

### §11.4 `LimbusModEditor.Domain.Tests/`（按被测对象分组，537 例）

| 覆盖对象（源码） | 测试文件 |
|---|---|
| `AssetDisplay` / 显示口径 | `AssetDisplayTests.cs`、`AssetFilterComboSentinelTests.cs` |
| `AssetSearchService` | `AssetSearchServiceTests.cs` |
| `AssetTreeBuilder` | `AssetTreeBuilderTests.cs` |
| `AssetPropertyService` | `AssetPropertyServiceTests.cs` |
| `AssetPreviewRegistry` / providers | `AssetPreviewRegistryTests.cs` |
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
| 导出流水线 | `ModExportPlanTests.cs`、`ModPackExportTests.cs`、`ModExportServiceTests.cs`、`MultiFormatExportTests.cs`、`ExportMatrixTests.cs`、`ExportAdvisorTests.cs`、`BankExportTests.cs`、`LunartiqueConversionTests.cs`、`UnityCacheExportServiceTests.cs` |
| 导入 | `ModImportServiceTests.cs`、`SourceImportTests.cs`、`GenericZipImportTests.cs` |
| 项目持久化 | `ProjectPersistenceTests.cs`、`ProjectFileSlimmingTests.cs`、`ProjectBuildServiceTests.cs`、`ProjectAutoConfigureTests.cs`、`NewModTemplateServiceTests.cs` |
| 扫描与索引 | `StartupScanServiceTests.cs`、`UnityCacheScanServiceTests.cs`、`CacheSignatureTests.cs`、`SqliteTableCacheTests.cs`、`RealFullScanSmokeTests.cs`、`RealStartupScanSmokeTests.cs`、`PerformanceBaselineTests.cs` |
| 目录定位与配置 | `GameDirectoryLocatorTests.cs`、`UnityCacheLocatorTests.cs`、`RealLocatorTests.cs`、`SharedConfigTests.cs`、`UiStateServiceTests.cs` |
| 调试与启动 | `ModApplyServiceTests.cs`、`DebugApplyTests.cs`、`ModInstallServiceTests.cs`、`GameLaunchServiceTests.cs` |
| catalog | `CatalogBaselineTests.cs` |
| Unity class 映射 | `UnityClassIdFormatMappingTests.cs` |
| 输出原子性 | `AtomicOutputTests.cs` |
| 真实工作台门 | `RealWorkbenchGateTests.cs`（预览 Kind 矩阵、FMOD 解码、rebank 结构、lang 回放等） |
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

### §11.6 测试维护注意事项

- **墙钟预算断言是既有 flake**：`RealBankIndexSmokeTests` 的「读索引 < 1.5s」在两测试工程并行时偶发超时，
  单独跑通过；与业务改动无关（详见 §11.2）。
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

| 键 | 写入方（行号示例） | 含义 |
|---|---|---|
| `reference` | `Scanning/UnityCacheScanService` | `"true"` = 纯引用资产（可由索引重建，保存项目时被跳过） |
| `cacheOuter` / `cacheInner` | `UnityCacheScanService` / `UnityCacheMaterializationService`(62-63) | 缓存外层键 / 内层键（Carra2 键与实体化路径） |
| `containerEntry` | `UnityCacheScanService`(460/499)、`Formats.Unity/UnityAssetService`(389) | Unity `m_Container` 的游戏内资源路径（**显示层口径**） |
| `catalogBaseline` | `UnityCacheScanService`(461/498)、`Assets/ModImportService`(70) | vanilla 基线判定摘要（列表显示用） |
| `staticBundle` | `UnityCacheScanService.StaticBundleMetadataKey`(469) | `"true"` = 静态数据 bundle 内资源（默认在资源工作台隐藏） |
| `unityBundle` / `unitySerializedFile` | `Assets/ModImportService`、`Scanning` | `"true"` = 资产来自 bundle / 独立 `.assets` |
| `replacementPath` | `Assets/AssetEditService`(135/178)、UI | 替换文件路径（预览、导出、构建都读它） |
| `originalSize` | `AssetEditService`(187) | 撤销替换时还原显示大小 |
| `unityFieldEdits` | `Assets/UnityFieldEditService` | 字段编辑集 JSON（`UnityFieldEditSetCodec`） |
| `spriteMetadata` | `Assets/SpriteMetadataEditService` | Sprite 元数据编辑 JSON |
| `atlasDirectory` / `atlasLayoutPath` / `atlasRegionCount` | `Assets/ImageAtlasEditService`(28-30) | 图集拆分产物位置与区域数 |
| `originalSourcePath` / `sourcePackagePath` / `sourceFormat` | `Assets/ModImportService` | 来源包/源文件路径与格式（去重与导出择优） |
| `materialized` | `Scanning/UnityCacheMaterializationService`(60) | `"true"` = 已实体化进项目 |
| `bankSource` | `Assets/`（bank 导入/替换） | 样本/替换所属 bank 来源（`ModExportPlanService`(169-170) 用它分组） |
| `base_bank`（Rebank 包侧） | `Formats.Rebank/RebankPackage`(48/134) | `.rebank` 的目标 bank 文件名（**无路径分隔符**） |

> 键名一律用字符串字面量读写，字典比较器是 `OrdinalIgnoreCase`。
> 新增键名请同时在此登记，避免后来者误判「无用字段」。

---

## §14 按症状定位文件（速查）

| 症状 / 需求 | 先看 | 再看 |
|---|---|---|
| 资源列表名字/路径/类型标签不对 | `Application/Assets/AssetDisplay.cs` | `Formats.Unity/UnityClassId.cs`、`Domain/Assets/AssetModels.cs` |
| 资源列表出现大量「未知」类型 | `Formats.Unity/UnityClassId.cs`（映射） | `AssetDisplay.TypeLabel`（中文文案） |
| 搜索/筛选/排序不对 | `Application/Assets/AssetSearchService.cs` | `AssetsWorkbenchPage` 筛选栏、`AssetSortKind` |
| 目录树层级/重名/懒展开 | `Application/Assets/AssetTreeBuilder.cs` | `AssetDisplay.TreePath/LeafDisambiguator` |
| 预览空白或形态不对 | `Application/Assets/Preview/AssetPreview.cs` + `AssetPreviewProviders.cs` | `TextPreviewService.cs`、`HexDumpService.cs` |
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
