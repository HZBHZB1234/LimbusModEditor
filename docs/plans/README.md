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

建议执行顺序：**plan-02 →（plan-01 并行）→ plan-03、plan-04 → plan-05 → plan-06/07/08（三者可并行）**。
plan-01 不依赖布局改造，可随时开工；plan-03/04 很小，也可在 plan-02 前先在现有 TabItem 内完成再随布局迁移。

## 共同执行规则（每个计划都必须遵守）

1. **基线三命令**（每轮开始与结束）：

   ```text
   dotnet build LimbusModEditor.slnx --no-restore --nologo
   dotnet test LimbusModEditor.slnx --no-build --nologo
   dotnet publish src/LimbusModEditor.App/LimbusModEditor.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish-win-x64 --no-restore
   ```

   当前基线：274 个测试全绿（62 Format + 212 Domain）。
2. **仓库文本文件只通过 read/edit/write 工具修改**（历史上有中文乱码事故）；pwsh 只用于 build/test/git/publish。
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
| lang 目录 | `<游戏>/LimbusCompany_Data/lang` | `config.json` 内容 `{"lang":"LLC_zh-CN",...}`；活动语言目录 `LLC_zh-CN` 含根级 JSON 数百个 + 子目录（StoryData 920 文件、PersonalityVoiceDlg 187、BattleAnnouncerDlg 61、BgmLyrics 15、EGOVoiceDig 14、Info 2、Font 0），本机已确认 |
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
