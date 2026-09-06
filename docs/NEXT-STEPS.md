# Limbus Mod Editor — 交接计划与需求（供后续 agent 继续）

> 本文件是给**后续接手的 agent** 的交接文档：说明当前基线、本轮已完成内容、
> 明确的待办任务（含验收标准）、项目铁律、真实环境事实与常用命令。
> 接手前先读 `docs/ROADMAP.md`、`docs/USAGE.md`、`docs/REVIEW.md`，并跑一遍基线命令确认全绿。

## 0. 一句话现状

LimbusModEditor（C#/.NET 8 + WPF，Windows x64，AssetsTools.NET 3.0.5 适配层）已能：
真实游戏数据读取验证（1459 个真实 bundle + 1531 个真实 .bank + 真实 Carra2 模组）、
Carra2/Rebank/Lunartique/Bank 四格式导入导出、真实 Texture2D `.resS` 流读写、
多格式项目导出、目录无感自动化、模组目录管理（`_disable` 约定）。

**当前基线：168 个测试全绿**（60 Format + 108 Domain）。最近一次提交 `367f87a`。

基线命令（每轮开始和结束都必须跑）：

```text
dotnet build LimbusModEditor.slnx --no-restore --nologo
dotnet test LimbusModEditor.slnx --no-build --nologo
dotnet publish src/LimbusModEditor.App/LimbusModEditor.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish-win-x64 --no-restore
```

## 1. 项目铁律（不可违反）

1. **只扩展 AssetsTools.NET 适配层**，不手写完整 Unity Bundle/SerializedFile 解析器。
2. **不猜测**：未知负载/未知压缩/未知字段一律 fail fast 并给中文错误；没有真实样本
   验证就不宣称兼容。
3. **不加载/不分发 FMOD DLL**：FMOD/FSBank 二进制必须由用户合法提供；只能 PE 探测
   （不 LoadLibrary）、按用户明确配置的目录加载。
4. **真实加载器 = LCTA**（`E:/desktop/work/LCTA-Limbus-company-transfer-auto`，GPL-3.0；
   其 `launcher/` 基于 LimbusModLoader v1.8）。LCTA 代码只可读作事实来源，**不得整段
   复制**；格式事实（字段布局/路径/常量）可以引用。
5. 仓库文本文件只通过 read/edit/write 工具修改（发生过两次中文乱码事故）；pwsh 只用于
   build/test/git/publish。提交信息用中文，每个里程碑独立提交，提交前必须 build+test。
6. UI 文案用中文；用户游戏目录在 `C:\Program Files (x86)\Steam\steamapps\common\Limbus Company`。
7. 真实样本测试（`RealSample*`/`RealBank*`/`RealLocator*`）在无样本机器上**自动跳过**，
   本机（tester 账号）有完整真实数据，会真正执行。

## 2. 真实环境事实（已实测，写代码时直接引用）

| 项 | 值 | 证据 |
|---|---|---|
| 游戏目录 | `C:\Program Files (x86)\Steam\steamapps\common\Limbus Company` | 本机存在，含 LimbusCompany.exe |
| Unity 缓存根 | `%LocalAppData%Low\Unity\ProjectMoon_LimbusCompany`（junction → `D:\Unity\ProjectMoon_LimbusCompany`） | 1459 个 `<outer>/<inner>/__data` |
| 模组目录 | `%APPDATA%\LimbusCompanyMods`（真实加载器约定，`_disable` 后缀禁用） | 内含 Sora_Mod_disable（真实 Nexus #92 模组） |
| Bank 目录 | `<游戏>/LimbusCompany_Data/StreamingAssets/Assets/Sound/FMODBuilds/Desktop` | 1531 个 .bank |
| 文本模组目录 | `<游戏>/LimbusCompany_Data/lang`（config.json + JSON + RFC6902 patch） | 存在多个语言目录 |
| 游戏 Unity 版本 | 6000.3.12f1；SerializedFile 版本串被抹为 `0.0.0`（bundle 头 `5.x.x`） | 类型树勘察 12/12 内嵌 |
| 事件 bank | `<id>.bank`：SNDH 合法为 size 0（无 FSB），尾部 STDT/STBL/HASH/DEL 零块 | BankParser 已支持 |
| 音频 bank | `<id>.assets.bank`：SNDH 表 (offset,size) 直指 FSB5；FSB codec 实测 16（Vorbis） | Fsb5Parser 0x3C 布局真实验证 |
| Carra2 键 | `<缓存外层键(32hex)>/<bundleHash>/<pathId>.<类型表索引>`；逐条目 XZ；含 `carra.json` 标记 | 真实模组 3898 对象实测 |
| 纹理格式 | 实测 RGBA32(4)/DXT5(12)/DXT1(10)/RGB24(3)/R8(29)，全部已实现 | 无 BC7/ASTC |
| 纹理像素 | 存在 `m_StreamData`（`archive:/CAB-xxx.resS` 容器内流），内联数组为空 | 已实现 resS 读写 |

## 3. 本轮（最近一轮）已完成

目标原文：「一个项目不一定只有一种格式。一个标准的项目格式应该是包含 bundle 文件修改
以及 bank 文件修改的文件，在导出时在选择不同的格式导出。同时，尝试自动化获取各种目录，
无感的自动化可以自动化的操作，如需修改则在设置的二级窗口中修改。」

- **多格式项目导出**：`ModExportService.ExportAllAsync`（`src/LimbusModEditor.Application/Build/ModExportService.cs`）
  按 `ModProject.Sources` 中每个已登记来源（各自 Format）导出到各自扩展名
  （`ExtensionFor`：.carra2/.rebank/.bank/.zip）；逐来源收集失败（源文件缺失、目录来源
  需交互选择目标格式）；`MultiFormatExportResult` + `MultiExportReportWindow` 报告，
  主窗口「导出全部（多格式）」按钮。
- **设置二级窗口**：`ProjectSettingsWindow`（`src/LimbusModEditor.App/ProjectSettingsWindow.cs`）
  集中修改 游戏目录/Unity 缓存/模组目录/FMOD DLL + 模组元数据（名称/版本/作者/描述）+
  调试行为（RestoreDebugFilesOnClose），每行带「自动获取」按钮；主窗口「项目设置…」。
- **无感自动化**：`ProjectAutoConfigureService.Apply`（`src/LimbusModEditor.Application/Debugging/ProjectAutoConfigureService.cs`）
  打开/新建项目时**只填充从未设置过的**目录（游戏/缓存/模组），用户设置过的值永不覆盖，
  FMOD 目录从不自动填；状态行提示自动配置了什么。
- 测试：`MultiFormatExportTests`（3）、`ProjectAutoConfigureTests`（3，含真实机器门控）。

## 4. 下一步明确任务（按优先级，每项含验收标准）

### T1（P0）写回后在真实游戏中的验证 —— 目前唯一未被真实数据覆盖的链路
- 现状：读取/解析/重建全部有真实样本验证；**写回后由游戏实际加载**从未验证。
- 任务：做一个含真实纹理替换（或 Sprite 元数据编辑）的端到端流程：
  1) 从真实 bundle 取一个 Texture2D（m_StreamData 流纹理），`ReplaceTextureFromPng`
     写回（内联像素 + 清 m_StreamData）→ 产出新 bundle；
  2) 用真实模组结构（carra2 或 Lunartique）承载 → 导出；
  3) 放入 `%APPDATA%\LimbusCompanyMods`，让用户启动游戏验证；
  4) 写一份「写回验证报告」（`docs/REALDATA-VERIFY.md`）。
- 验收：至少一个真实 bundle 对象完成 读→改→写→（用户）游戏内确认；报告记录
  bundle 前后哈希、重打包后 `VerifyBundleReferences` 通过、游戏内可见变化。
- 注意：写回产物不要覆盖游戏缓存原件；先在副本上验证。

### T2（P1）文本模组通道（lang JSON + RFC6902）
- LCTA 事实：`LimbusCompany_Data/lang` 下有 config.json + 语言 JSON + RFC6902 patch
  （`webutils/` 的 changes.py:24-52），是「改文本」的主流通道；我们的编辑器目前完全没有。
- 任务：新增 `lang` 文本差分格式的导入/导出（或并入现有 Directory 流程）：
  - 读取 config.json 确定活动语言；解析 RFC6902 patch 应用/生成；
  - 导出为与 LCTA 兼容的文本模组（放入 mods 目录后加载器可应用）。
- 验收：对真实 lang 目录生成/回读 patch，与 LCTA 输出语义一致；有合成样本测试；
  UI 有入口（可在导入列表加 `.json` 识别或独立「文本模组」按钮）。

### T3（P1）catalog_S1.bin 与「vanilla 基线」支持
- LCTA 事实：`resource_updater` 解析 catalog_S1.bin 获得 vanilla bundle 基线
  （哈希对比判断哪些对象被修改/新增），静态模组替换依赖它。
- 任务：只读解析 catalog_S1.bin（内容哈希表），在「导入 bundle 时显示该对象相对
  vanilla 是否修改/新增」；不猜测字段，只按 LCTA 记录的布局实现并对照真实验证。
- 验收：对真实 catalog_S1.bin 解析出合理条目数，与 LCTA 输出交叉核对（数量级一致）；
  单元测试用合成样本。

### T4（P2）文本/JSON 编辑与 RFC6902 双向（可并入 T2）
- 真实 lang JSON 是普通 JSON；编辑器已有文本编辑能力。将 RFC6902 生成/应用做成
  独立服务（`TextDiffService`），供 T2 使用，并加测试（生成→应用→回读相等）。

### T5（P2）REVIEW.md / ROADMAP 定期同步
- 每完成一项任务，把结论写回 `docs/ROADMAP.md`（状态节）与 `docs/REVIEW.md`
  （自审表）。目前真实样本事实集中在 ROADMAP 的「真实环境事实」节。

## 5. 已知边界（不要试图在本轮解决，除非用户要求）

- SpriteAtlas 网格重写、Mesh/AnimationClip 可写字段、FEV 事件/总线索引：**无真实样本
  支撑**，保持「读取可、写回阻止」的现状（不猜测）。
- FMOD 解码/编码：需要用户提供 fmod64.dll/fsbank64.dll；无 DLL 时 FSB 只能结构探测。
- 加密 bank / 未知 FSB：明确报错，不尝试解码。
- 游戏本体更新后缓存外层键更换：已通过「缓存对齐」导出诊断暴露（不自动修复）。

## 6. 结构速览（改代码前先看这些）

- `src/LimbusModEditor.Formats.Carra/` — Carra/Carra2 容器（键=外层/bundle/pathId.typeIdx，逐条目 XZ）
- `src/LimbusModEditor.Formats.Bank/` — `BankParser`（RIFF/FEV/SNDH，支持空 SNDH）、
  `Fsb5Parser`（0x3C/0x40 基头，vgmstream/python-fsb5 双源）、`BankAssembler`
- `src/LimbusModEditor.Formats.Unity/` — `AssetsToolsBackend`（InspectBundle/ReadTexture
  resS/ReplaceTextureFromPng/SurveyBundle/字段树/PPtr 依赖）、`UnityObjectSummary*`、
  `UnityClassId`（Mesh=43/AnimationClip=74）
- `src/LimbusModEditor.Application/` — `Projects/`（ModProject.Sources 多来源）、
  `Build/`（ModExportService.ExportAllAsync/缓存对齐、NewModTemplateService）、
  `Debugging/`（GameDirectoryLocator/UnityCacheLocator/ModDirectoryLocator/
  ModInstallService/ProjectAutoConfigureService）
- `src/LimbusModEditor.App/` — `MainWindow`（按钮：导入/导出/导出全部/项目设置/管理模组）、
  `ProjectSettingsWindow`、`ModManagerWindow`、`ExportReportWindow`、`MultiExportReportWindow`
- `tests/LimbusModEditor.Format.Tests/` — 真实样本：`RealSamples`（缓存/模组定位）、
  `RealSampleTests`（bundle 扫描/纹理 resS/摘要/类型树勘察/格式分布）、`RealBankTests`
- `tests/LimbusModEditor.Domain.Tests/` — `RealLocatorTests`、`MultiFormatExportTests`、
  `ProjectAutoConfigureTests`、`ModInstallServiceTests`

## 7. 交接给其他 agent 时的建议起点

1. 先跑基线三命令确认全绿；确认 `git log --oneline` 最近提交为 `367f87a` 或更新。
2. 从 T1 开始（写回验证）——它是唯一缺失的验证闭环，且本机有全部真实数据。
3. 每完成一个 T 任务：build + test 全绿 → 独立中文提交 → 更新 ROADMAP/USAGE/REVIEW。
4. 若遇到「某个行为与 LCTA 不一致」，先去 `E:/desktop/work/LCTA-Limbus-company-transfer-auto`
   读对应源码（引用路径+行号），再决定是修我们这边还是记录为 LCTA 的偏差。
