# Plan 06 — 音频 Bank 工作台页面（新页面）

> 覆盖需求 8：「音频资源是在 bank 目录，是一种有别于 unity 资源的特殊资源。我们应该新建一种页面，
> 使用与资源工作台页面类似的布局，用于编辑 bank。」

## 1. 事实依据（真实加载器 + 本机实测）

- Bank 目录：`<游戏>/LimbusCompany_Data/StreamingAssets/Assets/Sound/FMODBuilds/Desktop`，
  本机 1531 个 `.bank`（LCTA `launcher/sound.py:67-68`）。
- 两类 bank（`docs/NEXT-STEPS.md` §2 已实测）：
  - **事件 bank** `<id>.bank`：SNDH 合法为 size 0（无 FSB 负载），尾部 STDT/STBL/HASH/DEL 零块；
  - **音频 bank** `<id>.assets.bank`：SNDH 表 (offset,size) 直指 FSB5 payload，codec 16=Vorbis。
- 加载器通道（编辑器只产出模组包，绝不直写游戏目录）：
  1. **整包 .bank**：模组目录中的 `.bank` 同名覆盖到 bank 目录（`sound.py:104-113`），
     启动校验后替换、退出 `.bak` 还原；
  2. **.rebank**：zip 内 `rebank.json`（`base_bank` 必须纯文件名，`bankmod.py:104-108`）+ wav 条目；
     加载器解包原版 bank → 按 (index, fname) 替换 wav → FMOD 重打包 → 就地补丁（`bankmod.py:170-206`）；
     wav 数为 0 的 rebank 会被判错回滚（`bankmod.py:189-198`）。
- 编辑器既有能力：`BankParser`（RIFF/FEV/SNDH，支持空 SNDH）、`Fsb5Parser`（0x3C/0x40 基头、
  样本表）、`BankAssembler`/`BankModels.Rebuild`、`NativeFmodAudioCodec.Decode/Encode`、
  `BankAudioService`（FSB 读取/结构检查/解码 WAV）、Rebank 格式 handler、
  `AssetType.Audio` + `LogicalPath "fsb/..."` 的项目内表示、导出思路 `ExportIdeaKind.AudioBank`。

## 2. 页面设计（复用资源工作台布局骨架）

```
[活动栏 48][共享侧边栏 220][bank 浏览列 *][splitter][预览/编辑列]
```

- 活动栏新增 🏦「音频工作台」（plan-02 的占位页转正）。
- **浏览列**（BankDirectoryService，新）：
  - 自动定位 bank 目录（共享配置游戏目录 → 固定相对路径；缺失时提示条引导设置）；
  - 引用模式扫描（不复制文件）：文件名、大小、类型判定（事件/音频/加密——经 `BankParser.TryParse`）、
    FSB 数、（音频 bank）总时长估算；SQLite/JSON 轻量索引缓存可选（1531 个文件秒级，先不做）；
  - 搜索框 + 类型筛选（事件/音频/已修改）+ 列表视图（1531 行 ListView 虚拟化足够，暂不做树；
    树视图按 id 前缀分组列为后续增强）。
- **预览/编辑列**：
  - 事件 bank：显示「事件 bank（无 FSB 负载）」+ RIFF 块清单（STDT/STBL/HASH/DEL）+ 说明；
  - 音频 bank：FSB 样本表（名称/codec/采样率/声道/样本数/偏移+大小，`Fsb5Parser`），
    选中样本 → 「▶ 试听」（`NativeFmodAudioCodec` → 临时 WAV → MediaPlayer，沿用 MainWindow
    既有停止/清理模式）+ 「导出 WAV」；
  - 编辑：选中样本 → 「用 WAV 替换」（`EncodeWaveToFsbAsync` → `BankAssembler` 重组写出到
    项目内副本）。首次编辑把该 bank **实体化**进项目（复制到 `sources/banks/<name>`，
    记为项目 Source + 每样本一条 `AssetType.Audio` 记录，`LogicalPath="fsb/<bank>/<idx>/<fname>"`，
    与现有 Bank 导入管道同构），之后修改都落在项目副本上（引用模式+实体化，与 Unity 缓存同策略）。
- **导出**（沿用既有两通道，页面内出口按钮）：
  1. 整包：把修改后的 bank 副本输出为 `<原名>.bank` 到模组目录（加载器整包替换）；
  2. .rebank：从项目副本相对原版的差异生成 rebank 包（`rebank.json` + wav；`base_bank`=原文件名）。
     两者都经 `ModExportService` 既有 Bank/Rebank 路径，不新写格式代码。
- 提示条/引导：无 bank 目录 → 引导设置游戏目录；无 FMOD DLL → 试听/编码禁用并说明
  （`fmod64.dll/fsbank64.dll` 随包机制已有）。

## 3. 实施步骤

1. `Application/Assets/BankDirectoryService.cs`：目录解析 + 扫描 + 类型判定（单测用合成 bank + 真实门控）。
2. `App/BankWorkbenchPage.xaml(.cs)`：布局骨架（列表+预览+splitter）、扫描/搜索/筛选、
   样本表、试听、替换（实体化→记录替换）。
3. MainWindow/活动栏接线（plan-02 的 `ShowPage("bank")`）；导出按钮接 `ModExportService`
   （Bank 整包 / Rebank），产物路径默认模组目录。
4. 剔除联动：资源工作台侧边栏/导出思路里涉及 Bank 的入口文案同步（音频出口指向本页面）。

## 4. 验收标准（审查门）

- [ ] 真实 bank 目录冒烟：扫描 1531 个 bank 全部判定类型；事件 bank 显示「无 FSB」；音频 bank 样本表正确
      （抽 3 个与 `RealBankTests` 口径一致）。
- [ ] 任选一个音频 bank 样本试听成功（本机有 FMOD DLL）；导出 WAV 可播放。
- [ ] 替换一个样本（WAV→FSB）→ 整包导出到模组目录 → 文件可被 `BankParser` 重新解析且差异仅在目标样本；
      （可选游戏内验证）。
- [ ] .rebank 导出包经 `StaticMod`同级的真实加载器语义检查：`base_bank` 纯文件名、wav 数 > 0、
      zip 结构与 `bankmod.py` 预期一致（新增单测固化）。
- [ ] 编辑永不写入游戏目录（grep + 代码审查确认）；加密 bank 给出明确中文错误。
- [ ] build + test 全绿；USAGE 新增「音频工作台」节。

## 5. 风险与边界

- FSB 重编码有损（Vorbis）：替换前提示；不对未修改样本做无谓重编码（逐字节保留原 FSB 块，
  重组只替换目标样本——沿用 `BankAssembler` 行为，必要时补「未改动样本原样保留」测试）。
- 事件 bank 的 FEV 事件索引展示维持只读（真实 FEV 样本不足，ROADMAP P2.1 现状）。
- 时长估算依赖 FMOD 解码；无 DLL 时只显示结构信息，不显示时长（不猜）。
