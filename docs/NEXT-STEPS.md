# Limbus Mod Editor — 交接计划与需求（供后续 agent 继续）

> 本文件是给**后续接手的 agent** 的交接文档：说明当前基线、本轮已完成内容、
> 明确的待办任务（含验收标准）、项目铁律、真实环境事实与常用命令。
> 接手前先读 `docs/ROADMAP.md`、`docs/USAGE.md`、`docs/REVIEW.md`，并跑一遍基线命令确认全绿。

## 0. 一句话现状

LimbusModEditor（C#/.NET 8 + WPF，Windows x64，AssetsTools.NET 3.0.5 适配层）已能：
真实游戏数据读取验证（1459 个真实 bundle + 1531 个真实 .bank + 真实 Carra2 模组）、
Carra2/Rebank/Lunartique/Bank 四格式导入导出、真实 Texture2D `.resS` 流读写、
多格式项目导出、目录无感自动化、模组目录管理（`_disable` 约定）、
**真实写回闭环（T1：纹理替换→Carra2 导出→已装模组目录，含重大写回缺陷修复）**、
**lang 文本模组通道（T2/T4：RFC6902 差分，与 LCTA changes.py 兼容）**、
**官方 catalog 只读解析 + vanilla 基线判定（T3，CRC 口径与 LCTA 交叉验证一致）**、
**.staticmod 静态数据模组通道（读取/预览应用/生成，两个真实样本导入验证通过）**。

**当前基线：231 个测试全绿**（61 Format + 170 Domain，含 1 个 LME_BENCH 门控基准）。最近提交见 git log。

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
3. **FMOD DLL 随包分发（2026-09-06 用户修订）**：FMOD/FSBank 二进制由发布
   脚本随包分发（`scripts/publish.ps1` 把 `third_party/fmod/` 下合法获得的
   fmod64/fsbank64/libfsbvorbis64 复制进发布输出 `fmod/`；该目录不入 git）。
   编辑器按「程序目录\fmod → 程序目录 → 游戏自带运行库」自动发现，手动指定
   优先；`NativeFmodAudioCodec` 仍只绑定公开 C ABI。
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

## 3. 本轮（2026-09-06）已完成

本轮按上一节交接任务顺序完成了 T1/T2/T3/T4（T5 持续）：

- **T1 写回闭环**：`RealWriteBackTests`（真实样本驱动）+ 新增后端 API
  `ReadBundleSerializedObject`（bundle 内对象原始字节 + 类型表索引，Carra2
  键所需）；修复 `WritePackedBundle` 重大缺陷；报告 `docs/REALDATA-VERIFY.md`。
- **T2/T4 lang 文本模组**：`src/LimbusModEditor.Application/Texts/`
  （`TextDiffService` + `LangTextPatchService`）、UI `LangTextModWindow`、
  测试 22 个。
- **T3 catalog 基线**：`src/LimbusModEditor.Application/Catalog/`
  （`CatalogFileService` + `CatalogBaselineService`）、导入自动判定 +
  资源列表「vanilla 基线」列、测试 6 个。
- **.staticmod 静态数据模组通道**：`src/LimbusModEditor.Application/StaticMods/`
  （`StaticModService`）、UI `StaticModWindow`、测试 8 个（含两个真实样本）。
- 测试基线 168 → 205（61 Format + 144 Domain）。

## 3.5 本轮二（2026-09-06）：傻瓜化改造（启动引导 / 共享配置 / 扫描 / 随包 FMOD）

用户指出三大问题：不够傻瓜化、.lmeproj 共享设置重复配置、缺少操作指引，
并要求 FMOD DLL 随包分发。本轮交付：

- **共享配置（程序目录）**：`Application/AppConfig/`（`AppEnvironment` +
  `SharedAppConfigService`）。游戏/缓存/模组/FMOD 目录与最近项目存
  `<程序目录>/config/shared-config.json`，所有项目共用；旧项目目录值首开
  自动迁移（只填空位）；缓存统一放 `<程序目录>/cache/`（FMOD 探测、
  扫描索引）。设置窗口改为「共享目录 + 项目元数据」两段。
- **启动引导**：`WelcomeDialog`（新建/打开/最近项目）+ 上次项目自动恢复 +
  主窗口「下一步」提示条 + 无项目全屏引导覆盖层 + 左栏按「① 获取资源 /
  ② 产出模组」分组。新建向导默认项目目录 = 程序目录 `projects/<名>/`。
- **游戏资源扫描**：`Application/Scanning/UnityCacheScanService` —— 引用
  模式索引全部缓存 bundle（不复制文件），程序目录索引缓存增量复用，
  catalog 基线单次加载，逐 bundle 容错（失败进诊断不中断）。
  **性能事实（实测）**：60 bundle≈1.6s（约 280 资产/bundle）→ 全缓存
  1459 bundle ≈ 1-2 分钟；索引重建近瞬时。曾有的 O(N²) 合并已修复
  （字典索引）。
- **编辑实体化**：`UnityCacheMaterializationService` —— 首次导出/构建前把
  「已编辑的引用资源」所属 bundle 复制进项目 `sources/cache/<外>_<内>.bundle`
  （全缓存都叫 `__data`，不实体化会在构建输出互相覆盖）。
- **一键导出**：`Application/Build/UnityCacheExportService` —— 编辑过的
  bundle 经 `UnityBundleBuildService` 重打包（含引用完整性验证）后逐对象
  读回原始字节，按真实加载器 Carra2 键打包（口径=T1），一键产出
  `<名>.carra2` 到模组目录。真实数据端到端测试
  `UnityCacheExportServiceTests` 验证 导出→探针→重导入→键一致。
- **FMOD 随包**：`third_party/fmod/`（git 忽略）+ `scripts/publish.ps1`
  （纯 ASCII，PS5.1 兼容）复制进发布输出 `fmod/`；`FmodLibraryLocator`
  自动发现；`FmodCodecLibrary`/`FmodDllInspector` 候选扩展
  （fmod.dll/fmodstudio.dll 也可解码）。本机实测 3/3 DLL 齐套。
- **新测试 13 个**：SharedConfigTests(9)、UnityCacheScanServiceTests(3,
  真实样本门控)、UnityCacheExportServiceTests(1, 真实样本门控)；
  `RealFullScanSmokeTests` 保留为 `LME_FULL_SCAN_SMOKE=1` 手动冒烟
  （`LME_SCAN_BUDGET=N` 有界子集测吞吐）。
- 基线 205 → 218。发布产物：`artifacts/publish-win-x64`（含 fmod/ 三 DLL）。

## 3.6 本轮三（2026-09-06）：交互打磨与撤销能力（P3.7）

继续清除「不合理处」，本轮交付（详见 ROADMAP P3.7 / USAGE §1.2）：

- **撤销修改**：`AssetEditService.ClearEdits` + UI 按钮/右键菜单
  （替换/字段/Sprite 三类编辑标记可撤销；`originalSize` 还原显示大小；
  项目暂存文件一并清理）。
- **大项目不卡顿**：搜索 300ms 防抖 + 后台线程过滤排序（快照重载 +
  代际守卫 + 选中项保留）、异步纹理预览、项目保存去 sync-over-async。
- **长操作反馈**：ExportProgressWindow（一键导出阶段进度）、ScanDialog
  预计剩余时间、导出报告「打开输出位置」。
- **快捷交互**：窗口级拖放导入/替换、右键菜单、双击动作、Ctrl+F/Esc、
  大小列人性化、已修改行高亮、「打开模组目录/项目文件夹」、「已修改资源」
  计数与提示条口径统一。
- 新测试 5 个（ClearEdits×4 + 快照搜索口径一致），基线 218 → 223。

## 3.7 本轮四（2026-09-06）：VS Code 式工作台与目录树（P3.8）

继续清除「不合理处」——工作台化与类文件夹资源浏览（详见 ROADMAP P3.8 /
USAGE §1.3）：

- **工作台标签页**：48px 活动栏（📦/📝/🧩/🗂/📖/⚙）+ 深色标签区；
  「资源工作台」固定首标签，文本模组 / 静态数据 / 模组管理由独立窗口改为
  `UserControl` 嵌入式标签（可关闭、重复点击激活不重建）；
  Ctrl+Tab / Ctrl+Shift+Tab 循环。
- **目录树浏览**：`AssetTreeBuilder`（Application/Assets）把 LogicalPath
  按路径段惰性分层（根层一次构建、展开才分组下一层），中栏「☰ 列表 /
  🗂 目录树」切换，TreeView 虚拟化 + 占位懒展开；树跟随搜索结果，
  叶子选中/双击与列表共用 `ApplyAssetSelection` / `ActivateDefaultAction`。
- **性能清理**：`UpdateDirectoryStatus` 的四个目录定位器扫描进程级记忆化
  （配置变更显式失效）。
- 新增 `AssetTreeBuilderTests` 5 个（含真实缓存树遍历不丢不重），
  基线 223 → 228。

## 3.8 本轮五（2026-09-06）：性能改造（阶段 C，P3.9）

「先量化、再优化」：新增 `PerformanceBaselineTests`（`LME_BENCH=1` 门控，
真实缓存 1459 bundle / **1,194,061 资产**）量化热点路径后逐项处置
（详见 ROADMAP P3.9 / REVIEW §5.1 的量化对照表）：

- **项目文件瘦身**：`SkipReferenceAssetsConverter` 写入时跳过纯引用资产
  （可由索引重建；读取向后兼容旧项目文件）——保存 12.4s/1595MB →
  0.4s/1KB，打开 64.9s → 0.04s。
- **打开即后台回灌**：`RehydrateFromIndexAsync` 从索引库重建引用资产
  （14s，不解析 bundle、不阻塞 UI；实体化资产按 LogicalPath 优先）。
- **扫描索引 SQLite 化**：`UnityCacheSqliteIndexStore`（Microsoft.Data.Sqlite，
  `cache/unity-cache-index.db`）取代 164MB JSON 全文件重写——新鲜度检查
  内存字典化（实测逐 bundle 开连接查询会拖慢一个数量级，必须一次载入）、
  资产行单条流式查询分组、写回单事务批量（WAL + synchronous=NORMAL，
  索引可整库重建故安全）。热扫描 102.7s → 45.8s。
- **量化结论**：冷扫描 ≈4-5 分钟主体是 bundle 解析 + catalog 基线 CRC
  （生产必选、一次性），索引写回仅 18s；搜索/树构建本就良好，未动。
- 新增测试 3 个（瘦身往返×2 + 门控基准），基线 228 → 231。

## 4. 下一轮明确任务（按优先级，每项含验收标准）

### T6（P1）傻瓜化后续打磨（候选）
- ~~首扫体验：扫描窗口显示预计剩余时间~~（P3.7 已完成 ETA；「稍后再扫」入口
  保留为候选）。
- ~~工作台化（VS Code 式标签页）与目录树资源浏览~~（P3.8 已完成）。
- 全缓存 40 万级资产下的检索性能（必要时给 AssetList 加虚拟化/分页）。
- 欢迎窗口与提示条的用户实测反馈回收。

### T1（P0）✅ 已完成（2026-09-06）——写回后在真实游戏中的验证
- 端到端闭环已达成：真实流纹理 `Fx_T_Shape_LineFlash_01`（128×128 DXT1）
  PNG 替换 → 内联像素 + 清 m_StreamData → 重打包 `VerifyBundleReferences`
  通过 → 真实 Carra2 结构导出（键=外层/内层/pathId.类型表索引）→ 重新导入
  逐字节还原 → 已安装 `%APPDATA%\LimbusCompanyMods\LME-写回验证-5e6bda62.carra2`。
- **唯一待办：用户启动游戏做游戏内确认**（战斗特效中的线条闪光应变纯白）；
  报告与卸载步骤见 `docs/REALDATA-VERIFY.md`。
- 验证中修复重大缺陷：`Pack` 路径丢弃 Replacer（此前所有 bundle 写回的修改
  都会被静默丢弃），详见 `docs/REVIEW.md` 与 REALDATA-VERIFY §4。

### T2/T4（P1）✅ 已完成（2026-09-06）——lang 文本模组通道
- `TextDiffService`（RFC6902 生成/应用）+ `LangTextPatchService`
  （patchs 文档读写/目录差分/应用），UI「文本模组（lang 补丁）…」。
- 真实 lang 目录「读→改→差分→应用→回读相等」随测试验证。
- 后续可选：补丁操作的可视化 diff 视图。

### T3（P1）✅ 已完成（2026-09-06）——catalog 解析与 vanilla 基线
- `CatalogFileService`（只读解析，名字数与 LCTA 完全一致 1461=1461）+
  `CatalogBaselineService`（vanilla/修改/不在 catalog/未知 四态判定，
  CRC 口径与 LCTA 交叉验证 10/10 一致）。
- 重要事实：记录区 CRC/大小字段在 catalog 格式版本间整体平移
  （2026-08-22 +0x44/+0x48 → 2026-09-03 +0x3C/+0x40），解析器双布局自校准。
- 导入 bundle 自动判定，资源列表「vanilla 基线」列展示。

### T5（P2）✅ 持续执行——文档同步
- 每轮把结论写回 `docs/ROADMAP.md`（P3.5 新节）、`docs/REVIEW.md`（自审表）、
  `docs/USAGE.md`（§6/§9）。

### 下一轮候选任务（按价值排序）
1. **用户游戏内确认 T1 写回模组**（等待用户；唯一未闭环动作）。
2. catalog 依赖图展示与「缓存对齐 + vanilla 基线」诊断合并报告。
3. ✅ .staticmod 静态数据模组通道已于本轮完成（读取/预览应用/生成，
   真实样本导入验证通过）；后续可加：静态表编辑后从项目导出 .staticmod
   （当前生成入口在「静态数据模组」窗口，按 JSON 对逐条生成）。
4. ROADMAP P1.3 纹理格式扩展（BC7/ASTC 仍无真实样本，保持不猜测）。
5. ROADMAP P1.4 SpriteAtlas mesh 重写（无真实样本支撑，保持阻止写回现状）。

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
- `src/LimbusModEditor.App/` — `MainWindow`（活动栏 + 工作台标签页：资源/文本/
  静态/模组管理；资源列表与目录树切换）、`LangTextModControl`、`StaticModControl`、
  `ModManagerControl`（均作为工作台 UserControl 嵌入）、`ProjectSettingsWindow`、
  `ExportReportWindow`、`MultiExportReportWindow`
- `tests/LimbusModEditor.Format.Tests/` — 真实样本：`RealSamples`（缓存/模组定位）、
  `RealSampleTests`（bundle 扫描/纹理 resS/摘要/类型树勘察/格式分布）、`RealBankTests`
- `tests/LimbusModEditor.Domain.Tests/` — `RealLocatorTests`、`MultiFormatExportTests`、
  `ProjectAutoConfigureTests`、`ModInstallServiceTests`

## 7. 交接给其他 agent 时的建议起点

1. 先跑基线三命令确认全绿；确认 `git log --oneline` 最近提交为 `3c04b08` 或更新。
2. 优先跟进「下一轮候选任务」（§4 末尾）：T1-T4 与 .staticmod 通道已完成，
   唯一待办的用户动作是启动游戏确认 T1 写回模组的效果（见 `docs/REALDATA-VERIFY.md` §5）。
3. 建议从「catalog 依赖图与诊断合并报告」开始（T3 的解析器已就绪可复用）。
4. 每完成一个任务：build + test 全绿 → 独立中文提交 → 更新 ROADMAP/USAGE/REVIEW。
5. 若遇到「某个行为与 LCTA 不一致」，先去 `E:/desktop/work/LCTA-Limbus-company-transfer-auto`
   读对应源码（引用路径+行号），再决定是修我们这边还是记录为 LCTA 的偏差。
   注意：catalog 记录布局会随游戏版本整体平移（T3 的双布局自校准就是为此）。
