# Plan 01 — 修复 Unity bundle 资源属性获取与预览（真实数据驱动）

> 覆盖需求 1：「修复资源页面获取 unity bundle 资源时无法正确获取资源属性以及预览的问题，使真实数据测试。」

## 1. 问题定位（基于当前代码的失效链分析）

资源工作台的预览与属性入口在 `MainWindow.UpdatePreviewAsync`（`MainWindow.xaml.cs:980-1043`），
按类型分派：图像（Texture/Sprite）→ `UnityAssetService.ReadTexturePng`；文本 → `TextPreviewService`；
音频 → 仅 `LogicalPath` 以 `fsb/` 开头的 Bank 音频。经代码走查，**扫描索引（引用模式）得到的
Unity 缓存资源**存在以下可确认/高度疑似的失效链（实施第一步必须逐一复现证实）：

| # | 失效链 | 根因（代码证据） | 置信度 |
|---|---|---|---|
| A | **容器内 TextAsset/JSON 预览必然失败** | `TextPreviewService.LooksTextual/TryPreview` 直接读 `asset.SourcePath`；缓存引用资源的 SourcePath = 缓存条目 `__data`（整个 bundle 二进制）→ 永远走「非可读文本」拒绝 | 高（读 `TextPreviewService` 即可证实） |
| B | **Sprite 预览失败** | Sprite 走 `ReadTexturePng(source, pathId)`，但 pathId 是 Sprite 对象（class 213）不是 Texture2D（class 28）→ 读不到 → 回退到「replacementPath/SourcePath 是否图像扩展名」→ `__data` 不是图像 → 无预览 | 高 |
| C | **Unity AudioClip 无法试听** | `ShowAudioPreview`（`MainWindow.xaml.cs:1071-1084`）只支持 `LogicalPath.StartsWith("fsb/")` 的 Bank 音频；扫描出的 Unity 音频对象一律显示「暂不支持解码试听」 | 高（现 UI 文案自证） |
| D | **资源属性缺失** | 引用模式扫描只记录 类型/大小/容器条目（`UnityCacheScanService.cs:314-370`）；右栏「选中资源」只有 名称/位置/类型/大小/状态 五行，无 m_Name、纹理尺寸/格式、音频采样率/声道、MonoBehaviour 脚本类等「属性」 | 高（设计缺口） |
| E | Sprite 图集区域未裁剪 | `AssetsToolsBackend.ReadSprite`（`AssetsToolsBackend.cs:754`）已能读 UnitySpriteObject（rect），但预览从未用它合成图集子图 | 高 |

## 2. 实施步骤

### 第 0 步：真实数据复现探针（先写测试再修）

1. 新增 `tests/LimbusModEditor.Format.Tests/RealPreviewCoverageTests.cs`（门控：本机真实缓存存在才运行，
   仿照 `RealSampleTests`/`RealSamples.cs` 的定位方式；支持 `LME_SCAN_BUDGET=N` 限定 bundle 数）：
   - 对有界子集的缓存 bundle 逐类型抽样，断言「预览服务能产出结果」：
     - TextAsset（class 49）→ 文本预览非空、UTF-8 解码；
     - Sprite（class 213）→ 能产出裁剪后 PNG（宽高 == m_Rect）；
     - AudioClip（class 83）→ 能取出 FSB 字节（结构探测成功，FMOD DLL 在时解码为 WAV）；
     - Texture2D（class 28）→ 现状基线（应继续通过）。
   - 输出一份「类型 × 成功/失败/原因」矩阵到测试输出，作为修复前基线证据。
2. 跑一遍记录失败清单（预期：A/B/C 全红）。

### 第 1 步：补 `AssetsToolsBackend` / `UnityAssetService` 读取能力（Formats.Unity 层）

1. `ReadBundleTextAsset(bundlePath, serializedFileName, pathId)` → `(name, bytes)`：
   经类型树读 `m_Name` + `m_Script`（TextAsset 正文）。复用既有 `ReadBundleObjectFields`
   的类型树通道，不手写解析（铁律 1）。
2. `ReadBundleSpriteComposite(bundlePath, serializedFileName, pathId)`：
   读 Sprite 的 `m_Rect` + `m_RD.texture` PPtr → 同文件定位 Texture2D → 复用 `ReadTexture`
   （含 .resS 流，`AssetsToolsBackend.cs:646` 已支持）→ 按 rect 裁剪合成 PNG。
   注意 Unity rect 原点在左下；DXT 纹理先解码再裁剪。
3. `ReadBundleAudioClipData(bundlePath, serializedFileName, pathId)` → `(name, format, byte[] fsb)`：
   读 AudioClip 的 `m_Resource.m_Data`（可能走 .resS 外流，与纹理同机制）+ `m_Format`。
   未知格式/空数据 fail fast 中文报错。
4. 每个方法配合成样本或真实样本门控测试；未知类型不猜测（铁律 2）。

### 第 2 步：Application 属性汇总服务

新增 `Application/Assets/AssetPropertyService.cs`：
- 输入 `AssetRecord`，输出属性键值行（中文标签）：
  - 通用：类型、大小、修改状态、容器路径、所在 bundle（外层键前 8 位 + 内层键）；
  - Texture2D：尺寸、格式（`TextureFormatCatalog` 说明）、mipmap、是否流式（.resS）；
  - Sprite：rect 区域、图集纹理、pivot/border（复用 `UnitySpriteMetadata`）；
  - AudioClip：名称、FSB codec（`Fsb5Parser`）、采样率、声道、样本数/时长；
  - Text/JSON：编码、字符数、是否 JSON 可解析；
  - MonoBehaviour：脚本类名/GUID（复用 `ReadBundleObjectScriptInfo`）、字段数；
  - Mesh/Animation/Font：复用 `UnityObjectSummaryBuilder` 摘要。
- 全部后台线程读取；读取失败逐项降级为「（不可读：原因）」，不抛整块异常。

### 第 3 步：UI 接线（`MainWindow`）

1. `UpdatePreviewAsync` 分派修正：
   - Sprite → `ReadBundleSpriteComposite`（不再直呼 ReadTexturePng）；
   - Text/JSON 且 `unityBundle=true` → `ReadBundleTextAsset` → 文本框（`TextPreviewService`
     保留给导入的独立文件）；JSON 再走格式化;
   - AudioClip → 有 FSB 数据时启用「试听」（`NativeFmodAudioCodec`，缺 DLL 时说明原因——文案从
     「Unity 音频对象暂不支持」改为具体缺什么）。
2. 右栏「选中资源」五行下方加「属性」折叠区（`ItemsControl` 绑定 `AssetPropertyService` 结果），
   异步加载，选中切换用既有 `_previewGeneration` 代际守卫模式。
3. 以上接线在 plan-02 页面化时随 `AssetsWorkbenchPage` 迁移，方法保持public/internal 可测试。

### 第 4 步：回归

1. `RealPreviewCoverageTests` 矩阵全绿（无样本机器自动跳过）。
2. 既有 274 测试不回退；`dotnet build/test` 全绿。

## 3. 验收标准（审查门）

- [ ] 探针测试修复前后的失败矩阵已记录（贴入本文档或 REALDATA-VERIFY.md）。
- [ ] 真实缓存样本：TextAsset 预览显示中文正文；Sprite 显示裁剪子图（尺寸==rect）；AudioClip 能试听（本机有随包 FMOD DLL）。
- [ ] 右栏属性区对 Texture/Sprite/AudioClip/MonoBehaviour 至少各 1 个真实对象显示正确属性。
- [ ] 替换过的资源（有 replacementPath）预览仍优先显示替换后内容（现有行为不回退）。
- [ ] build + test 全绿；中文提交；ROADMAP/USAGE 已更新。

## 4. 风险与边界

- `.resS` 流读取已支持纹理，AudioClip 走同一机制但字段路径不同；若遇到 `archive:/CAB-*.resS`
  之外的流引用形态，fail fast 并记录诊断，不猜测。
- Sprite 裁剪只做展示层（PNG 预览），**不**修改原对象；图集重写仍按 ROADMAP P1.4 维持「阻止写回」。
- 属性读取对 40 万级列表只按「选中单个资源」触发，不做全量预读（性能红线，见 P3.9 实测教训）。
