# Plan 02 — 页面架构重构：移除顶部标签页、活动栏直切、全页面共享侧边栏

> 覆盖需求 2、3、5：
> 2. 移除顶端的多标签页 tab，只保留左侧的 VS Code 风格 tab（活动栏）进行页面切换。
> 3. 移除除了资源工作台、设置、教程以外的所有页面以及相关代码。
> 5. 资源工作台左侧的侧边栏改为所有页面共用、不消失。

本计划是 plan-03/04/05/06/07/08 的**布局基础**，建议最先执行。

## 1. 现状（证据）

- `MainWindow.xaml:110-135`：`Grid` 两列（48px 活动栏 + `*`），活动栏按钮 📦/📝/🧩/📖/⚙；
  第 2 列是 `TabControl x:Name="WorkbenchTabs"`，其**顶部标签头**即要移除的「多标签页」，
  固定首个 `TabItem x:Name="AssetsTab"`（资源工作台），其余标签由 `OpenWorkbench` 运行时加入。
- `MainWindow.xaml.cs:1774-1832`：`OpenWorkbench(key,title,factory)` / `FindWorkbenchTab` /
  `CloseWorkbenchTab` / `BuildClosableHeader`（✕ 可关闭头）/ `ActivateAssetsWorkbench_Click`。
- `MainWindow.xaml.cs:1442-1452`：Ctrl+Tab 循环 `WorkbenchTabs`。
- `MainWindow.xaml.cs:358-388`：`LangTextMod_Click` / `StaticMod_Click` 经 `OpenWorkbench` 嵌入
  `LangTextModControl` / `StaticModControl`。
- `MainWindow.xaml.cs:349-353` `ProjectSettings_Click` → 模态 `ProjectSettingsWindow(owner, saveProject)`；
  `MainWindow.xaml.cs:1586-1587` `Help_Click` → 模态 `HelpWindow`。
- 资源工作台左栏 220px「项目工作区」（`MainWindow.xaml:136-180`）目前**只在资源标签内存在**。
- `MainWindow.xaml:340-352`：`NoProjectOverlay` 覆盖资源标签的三列区域。

## 2. 目标页面清单（最终态）

| 页面 key | 图标 | 名称 | 来源 |
|---|---|---|---|
| `assets` | 📦 | 资源工作台 | 现有 AssetsTab 内容抽出为 `AssetsWorkbenchPage` |
| `bank` | 🏦 | 音频工作台 | plan-06 新建（落地前占位页显示「开发中」） |
| `text` | 📝 | 文本工作台 | plan-07 新建；过渡期直接承载现有 `LangTextModControl` |
| `static` | 🧩 | 静态数据工作台 | plan-08 新建；过渡期直接承载现有 `StaticModControl` |
| `help` | 📖 | 使用教程 | `HelpWindow` 内容改为 `HelpPage`（页面化，不再是模态窗） |
| `settings` | ⚙ | 设置 | `ProjectSettingsWindow` 内容改为 `SettingsPage` |

除此之外没有其他页面；导出向导/导出思路/扫描等**保持模态对话框**（它们是操作流，不是页面）。

## 3. 实施步骤

### 第 1 步：引入页面宿主与页面基座

1. 新增 `App/WorkbenchPages/IWorkbenchHost.cs`（或放在 App 根，遵循现有平铺风格亦可）：

   ```csharp
   public interface IWorkbenchHost
   {
       ModProject? Project { get; }
       string? ProjectFile { get; }
       AppEnvironment Env { get; }
       void SetStatus(string message);
       void RefreshProjectState(string? status = null);
       Task<bool> SaveProjectAsync();          // 封装 SaveProjectInternalAsync
       void ShowPage(string key);              // 页面间跳转（如「去扫描」）
   }
   ```

   `MainWindow` 实现之；页面通过构造函数注入，避免继续膨胀 MainWindow（现 1901 行）。
2. 新增 `AssetsWorkbenchPage : UserControl`：把 `MainWindow.xaml:133-354` 的资源标签内容
   原样搬入（含事件处理迁至 page 代码后置）。`MainWindow` 上被搬走的事件处理
   （搜索/筛选/树/列表/预览/替换/导出…约 900 行）随迁；`RefreshProjectState`、
   `RefreshSelectionButtons`、`UpdateHint` 等跨页逻辑改经 `IWorkbenchHost` 调用。
   一次性纯搬家，不改行为（降低审查难度）。
3. 新增 `HelpPage : UserControl`（内容取自 `HelpWindow`）与 `SettingsPage : UserControl`
   （内容取自 `ProjectSettingsWindow` 的两段布局：共享目录 + 项目元数据）。

### 第 2 步：MainWindow 布局改造（XAML）

1. `MainWindow.xaml:110-135` 的 `Grid` 改三列：

   ```
   [48 活动栏][220 共享侧边栏][* 页面宿主 ContentControl x:Name="PageHost"]
   ```

2. **侧边栏**：把资源标签内 220px 左栏（`MainWindow.xaml:137-180`）整块搬出为共享列
   （提取为 `ProjectSidebarControl : UserControl` 或直接内联），所有页面可见、不随页面切换消失。
   - 按钮路由调整：`文本模组（lang 补丁）…` / `静态数据模组…` 改为 `ShowPage("text"/"static")`；
     plan-06 落地后加「音频工作台」按钮；`扫描游戏资源` 保留在侧边栏（plan-03 将其改名为
     自动加载并去掉同区的手动导入按钮）。
   - 侧边栏内容对无项目状态保持可用（未打开项目时显示引导文案而不是报错）。
3. **页面宿主**：`TabControl WorkbenchTabs` → `ContentControl x:Name="PageHost"`；
   删除 `WorkbenchTabItem` 样式（`MainWindow.xaml:23-47`）。
4. `NoProjectOverlay` 移到 MainWindow 层，覆盖侧边栏 + 页面宿主（Grid.ColumnSpan=2）。

### 第 3 步：切换逻辑与快捷键（代码侧）

1. 新增 `Dictionary<string, UserControl> _pages`（惰性创建、**常驻不销毁**，保住各页面状态）
   + `ShowPage(string key)`；活动栏按钮 Click 全部改为 `ShowPage(...)`。
2. 删除 plan-README 盘点中列出的多标签机制代码：`OpenWorkbench` / `FindWorkbenchTab` /
   `CloseWorkbenchTab` / `BuildClosableHeader` / `WorkbenchTabItem` 样式引用；
   `LangTextMod_Click`/`StaticMod_Click` 改为 `ShowPage`（过渡期内容仍为旧 UserControl，
   plan-07/08 再替换内部实现）。
3. Ctrl+Tab / Ctrl+Shift+Tab（`MainWindow.xaml.cs:1442-1452`）改为按固定页面顺序循环
   `ShowPage`（顺序 = 活动栏图标顺序）。
4. `SettingsPage` 行为修正：不再要求「已打开项目」才能打开（共享目录是全局的）；
   项目元数据段在无项目时置灰并说明原因。保存回调仍走 `IWorkbenchHost.SaveProjectAsync()`。

### 第 4 步：清理（需求 3「相关代码」）

- 删除 `ProjectSettingsWindow.cs` / `HelpWindow.cs` 的窗口壳（内容已迁页面）；若仍有
  `Owner`/`ShowDialog` 残留引用一并清理。
- 全仓 grep `WorkbenchTabs`、`OpenWorkbench`、`AssetsTab` 应为 0 处。
- `LangTextModControl` / `StaticModControl` 的文件保留（plan-07/08 会重写其内容），
  但其「作为标签页打开」的旧入口注释标明过渡态。

## 4. 验收标准（审查门）

- [ ] 顶部不再有任何标签头；页面切换只通过左侧活动栏（鼠标）与 Ctrl+Tab（键盘）。
- [ ] 切换到设置/教程/文本/静态页面时，左侧 220px 侧边栏始终可见且内容一致。
- [ ] 活动栏 6 个入口全部可用；无「请先创建或打开项目」死路（设置页无项目也能改共享目录）。
- [ ] 无项目时 `NoProjectOverlay` 覆盖工作区并正常引导；打开项目后消失。
- [ ] 资源工作台功能无回退：搜索/筛选/树/列表/预览/右键菜单/拖放/双击/Ctrl+F/Esc 全部照旧。
- [ ] grep 证明多标签机制代码已删除；build + test 全绿；发布包可启动冒烟（打开→扫描→预览）。
- [ ] ROADMAP 新节 + USAGE §1 重写为页面模型。

## 5. 风险与边界

- 纯搬家与重构混做会难以审查：第 1.2 步**只搬家不改行为**，行为修正（设置页无项目可用）
  单独提交。
- 页面常驻不销毁 → 注意各页面订阅的事件在项目切换时的刷新（统一走 `RefreshProjectState`）。
- `HumanSizeConverter` 等资源在 `Window.Resources` 中定义，页面化后需保证页面内可用
  （移到 `App.xaml` 或页面资源）。
