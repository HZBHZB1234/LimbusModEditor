# 重构执行计划索引（docs/plans）

本目录是「LimbusModEditor 重构」的**执行操作计划**集合，覆盖 2026-09 用户提出的 10 项修改。
本目录只做计划与事实依据记录；动手实施时按各计划逐步执行并勾选验收项。

## 计划与需求对照表

| 计划 | 覆盖需求 | 主题 | 依赖 |
|---|---|---|---|
| [plan-01](plan-01-unity-properties-preview-realdata.md) | 需求 1 | 修复 Unity bundle 资源属性获取与预览（真实数据验证） | 无 |
| [plan-02](plan-02-page-shell-shared-sidebar.md) | 需求 2、3、5 | 页面架构重构：移除顶部标签页、活动栏直切页面、共享侧边栏、设置/教程页面化 | 无（其余计划的布局基础） |
| [plan-03](plan-03-asset-workbench-auto-load.md) | 需求 4 | 资源工作台移除手动加载入口，只保留自动加载 | plan-02（或先做后并入新布局） |
| [plan-04](plan-04-resizable-splitter.md) | 需求 6 | 资源列表/树与预览区拖拽调整占比 | plan-02 |
| [plan-05](plan-05-preview-enhancement.md) | 需求 7 | 预览强化（多格式预览管线） | plan-01（复用其属性读取层） |
| [plan-06](plan-06-bank-workbench.md) | 需求 8 | 音频 Bank 工作台页面（新页面） | plan-02 |
| [plan-07](plan-07-text-workbench.md) | 需求 9 | 文本（lang）工作台页面（新页面） | plan-02 |
| [plan-08](plan-08-static-workbench.md) | 需求 10 | 静态数据工作台页面（新页面）+ 资源工作台剔除相关 bundle | plan-02 |
| [plan-09](plan-09-workbench-shell-and-table-cache.md) | 第二批需求（工作台 UI 一致 + 表缓存） | 工作台公共骨架（WorkbenchShell / JsonTreeEditor）+ 表缓存底座 | 无（plan-10/11/12 的前置） |
| [plan-10](plan-10-text-workbench-ui.md) | 第二批需求 | 文本工作台重做：树形浏览 + 共享编辑器 + `cache/text-index.db` | plan-09 |
| [plan-11](plan-11-bank-workbench-all-audio-view.md) | 第二批需求 | 音频工作台重做：跨 bank 样本总表 + bank 树 + `cache/bank-index.db` | plan-09 |
| [plan-12](plan-12-static-workbench-shell-ui.md) | 第二批需求 | 静态数据工作台重做：资源工作台同款页面 + `cache/static-tables.db` | plan-09 |
| [plan-13](plan-13-user-feedback-fixes.md) | 第三批需求（8 项，含截图反馈） | 用户反馈修复批次：tooltip 字体 / 图像行序 / 启动自动扫描四库 / lang 缺表自愈 / JSON 树滚轮 / 音频工作台重做 / 图像适应窗口 / 下拉裁字 | plan-09~12 |
| [plan-14](plan-14-feedback-round2.md) | 用户反馈第二轮（5 项，含截图） | bank 行对比度 / 事件 bank 默认隐藏 / 单 FSB 自动展开 / lang 数据源改为语言目录内部 + 预览失败修复 / lang 导出入口移到侧边栏 | plan-13 |

**第二批执行状态（2026-09）**：plan-09 ✅ / plan-10 ✅ / plan-11 ✅ / plan-12 ✅ 均已实施并入库。
公共骨架与表缓存底座已就位（`WorkbenchShell`、`JsonTreeEditor`、`Application/Caching/`、
`Themes/WorkbenchStyles.xaml`）；四个工作台均为 XAML + 共享骨架/色板
（资源页在收口阶段完成色值迁移，结构未动）。
**验收门全部闭合**：`WorkbenchPages/` 下硬编码设计色为 0（`grep #RRGGBB`/`Color.FromRgb(0x` 无命中），
色值只存在于 `Themes/Theme.xaml` 与 `Themes/WorkbenchStyles.xaml`。

**第三批执行状态（2026-09）**：plan-13 ✅ 已实施并入库（8 项全部修复；
四个缓存库改为**每次启动自动建库/校表 + 增量扫描**，新入口 `StartupScanService`）。

**第四批执行状态（2026-09）**：plan-14 ✅ 已实施（5 项：bank 行对比度改为工作台自带树模板
——离屏实测选中行底色从「与列表底相同」变为 `#20304A`（亮度差 +26）；bank 树默认隐藏事件 bank
（勾选框 / 「仅事件 bank」强制显示）；单 FSB 的 bank 展开即到样本层；文本工作台数据源改为
**活动语言目录内部**（不再显示 `config.json` 与语言目录包裹层，补丁键仍是 lang 根相对路径 =
加载器兼容门）+ 修掉「索引命中路径没把 lang 根交给服务 → 点文件预览一片空白」；
lang 导出/直接应用两个按钮移到侧边栏「② 产出模组」）。
测试 549 → 562；新增 `BankTreeRules` / `LangTextDisplay` 两个纯逻辑类、`AttachLangRoot` 契约，
以及索引内容口径版本（`TextIndexStore.IndexFormatVersion`，旧库自动重建）。

**实测收益（本机真实数据）**：
`text-index.db` 二次进页面 608ms、搜索 105ms（vs 逐文件现读 1962ms）；
`bank-index.db` 1531 文件 / 52826 样本，冷建 19.3s → 热读 917ms（0 解析）；
`static-tables.db` 1392 张表 / 43.6 MB 正文，冷建 6.5s → 热读 11ms（枚举 bundle 需 4.3s）。

建议执行顺序：**plan-02 →（plan-01 并行）→ plan-03、plan-04 → plan-05 → plan-06/07/08（三者可并行）**。
plan-01 不依赖布局改造，可随时开工；plan-03/04 很小，也可在 plan-02 前先在现有 TabItem 内完成再随布局迁移。

第二批（2026-09 用户反馈：资源工作台好用，其余三个编辑器不美观 / 无树 / 无全量视图）：
**plan-09（串行前置）→ plan-10/11/12 三者并行**。第二批的公共约束：
表缓存固定放**程序目录 `cache/`**（与 `unity-cache-index.db` 同处），
缓存只存 vanilla 事实、编辑集不落缓存，失效规则只有「源签名变化」一条
（`(size,mtime)` 或 catalog 内层内容哈希），绝不缓存游戏版本常量。

## 共同执行规则（每个计划都必须遵守）

1. **基线三命令**（每轮开始与结束）：

   ```text
   dotnet build LimbusModEditor.slnx --no-restore --nologo
   dotnet test LimbusModEditor.slnx --no-build --nologo
   dotnet publish src/LimbusModEditor.App/LimbusModEditor.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish-win-x64 --no-restore
   ```

   当前基线（2026-09，plan-14 完成后实测）：**562 个测试全绿（74 Format + 488 Domain）**。
   历史：plan-02 期为 274（62+212），plan-08 期为 334（74+260），plan-09 期为 476（74+402），
   plan-09~12 完成后为 528（74+454），plan-13 完成后为 549（74+475），plan-14 完成后为 562（74+488）。
   注 1：2026-09 本机曾出现 2 个真实 lang 门控测试失败，根因是测试把活动语言写死为 `LLC_zh-CN`，
   而本机 `config.json` 指向汉化组目录 `LLc-CN-LCTA`——已改为**跟随 config.json**，不得再写死。
   注 2：`RealBankIndexSmokeTests` 的「读索引 < 1.5s」是**墙钟预算**断言，两个测试项目并行跑时
   偶发超时（单独跑 2/2 通过）；它是既有 flake，与 plan-13/14 的改动无关，待定是否放宽预算。
2. **仓库文本文件只通过 read/edit/write 工具修改**（历史上有中文乱码事故）；pwsh 只用于 build/test/git/publish。
   （plan-14 期间用 pwsh 的 `Get-Content -Raw | Set-Content` 改过一次本文件，直接把 UTF-8 写成了 ANSI，
   本文件按 HEAD 版本 + 该轮内容重建。这条规则不是形式主义。）
3. **提交纪律**：提交信息用中文；每个里程碑独立提交；提交前必须 build+test 全绿；完成后更新 `docs/ROADMAP.md`（新节）、`docs/USAGE.md`（界面变化）、必要时 `docs/REVIEW.md`。
4. **不猜测原则**：未知负载/未知压缩/未知字段一律 fail fast 并给中文错误；没有真实样本验证不宣称兼容。真实样本门控测试（`Real*` 前缀）在无样本机器上自动跳过。
5. **真实加载器 = LCTA**（`E:\desktop\work\LCTA-Limbus-company-transfer-auto\launcher`，GPL-3.0）：代码可读作事实来源，不得整段复制；格式事实（路径/布局/常量）可以引用。
6. **UI 文案用中文**；格式边界不破坏：Unity 走 AssetsTools.NET 适配层、图像走 ImageSharp、XZ 走 Joveler、FMOD 只绑公开 C ABI。
7. **每个计划末尾有「审查门」**：自查清单 + 真实数据验证步骤；完成一条勾一条，全部通过才算该计划完成。

## 真实环境事实（本机已实测，写计划/代码时直接引用）

| 项 | 值 | 证据 |
|---|---|---|
| 游戏目录 | `C:\Program Files (x86)\Steam\steamapps\common\Limbus Company` | 本机存在，含 LimbusCompany.exe（`docs/NEXT-STEPS.md` §2） |
| Unity 缓存根 | `%LocalAppData%Low\Unity\ProjectMoon_LimbusCompany`（junction → `D:\Unity\...`） | 1459 个 `<outer>/<inner>/__data`，本机已确认 |
| Bank 目录 | `<游戏>/LimbusCompany_Data/StreamingAssets/Assets/Sound/FMODBuilds/Desktop` | 1531 个 .bank，本机已确认（LCTA `launcher/sound.py:67-68`） |
| lang 目录 | `<游戏>/LimbusCompany_Data/lang` | 活动语言目录由 `config.json` 的 `lang` 字段决定（本机曾为 `LLC_zh-CN`，后被汉化组改为 `LLc-CN-LCTA`——**代码一律跟随 config.json，不得写死**）；活动语言目录含根级 JSON 数百个 + 子目录（StoryData 920 文件、PersonalityVoiceDlg 187、BattleAnnouncerDlg 61、BgmLyrics 15、EGOVoiceDig 14、Info 2、Font 0），本机已确认；plan-14 起 `config.json` 本身不再是工作台文件 |
| 静态 bundle | `static_s1_0_assets_all_<32hex>.bundle`，经 catalog 定位（catalog_S1.bin 在 `LimbusCompany_Data/StreamingAssets/aa/`） | LCTA `launcher/staticmod.py:65,108-156` |
| 事件 bank / 音频 bank | `<id>.bank` SNDH 合法为 size 0；`<id>.assets.bank` SNDH (offset,size) 直指 FSB5（codec 16=Vorbis） | `docs/NEXT-STEPS.md` §2、`RealBankTests` |
| 游戏 Unity 版本 | 6000.3.12f1；SerializedFile 版本串抹为 `0.0.0`；类型树全部内嵌 | `docs/ROADMAP.md` P0.1 |

## 现有页面/入口盘点（plan-02 的删除对象清单）

- `MainWindow.xaml:110-135`：48px 活动栏（📦资源/📝文本/🧩静态/📖教程/⚙设置）+ `TabControl WorkbenchTabs`（顶部标签头，固定 `AssetsTab` 资源工作台）。
- `MainWindow.xaml.cs:1774-1832`：`OpenWorkbench`/`FindWorkbenchTab`/`CloseWorkbenchTab`/`BuildClosableHeader`/`ActivateAssetsWorkbench_Click`（顶部多标签机制的代码侧）。
- `MainWindow.xaml.cs:1442-1452`：Ctrl+Tab 循环 `WorkbenchTabs`。
- `LangTextModControl.cs`（177 行，UserControl）、`StaticModControl.cs`（219 行，UserControl）：作为标签页嵌入。
- `ProjectSettingsWindow.cs`（模态窗口，构造 `(MainWindow owner, Func<string?,Task<bool>> saveProject)`）、`HelpWindow.cs`（模态窗口）。
- 资源工作台左栏（`MainWindow.xaml:136-180`，220px「项目工作区」面板）= plan-02 要变成全页面共享的侧边栏。
