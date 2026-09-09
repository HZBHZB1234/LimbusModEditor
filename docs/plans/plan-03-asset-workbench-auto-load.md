# Plan 03 — 资源工作台移除手动加载入口，只保留自动加载

> 覆盖需求 4：「对于资源工作台页面，移除顶端的加载文件/加载文件夹的输入框以及按钮，
> 只允许自动加载资源文件。」

## 1. 现状（证据）

资源工作台有两处手动「加载文件/文件夹」入口：

1. **中栏搜索行**（`MainWindow.xaml:184-188`）：搜索框旁的 `导入资源` 按钮（`Import_Click`）
   与 `文件夹` 按钮（`ImportDirectory_Click`）——这就是「顶端的输入框以及按钮」。
2. **左栏 ① 获取资源 区**（`MainWindow.xaml:151-157`）：`扫描游戏资源（推荐）`（自动）、
   `导入资源包…`（`Import_Click`）、`导入文件夹…`（`ImportDirectory_Click`）。

相关代码：`MainWindow.xaml.cs:227-284`（`Import_Click`/`ImportSingleFileAsync`/
`ImportDirectory_Click`）、拖放导入 `Window_Drop` → `ImportPathsAsync`（`1371-1430`）。

「自动加载」已有骨架：打开项目后 `AfterProjectOpenedAsync`（`181-197`）自动配置目录，
空项目且有缓存时自动弹扫描（`autoStart: true`）；`UnityCacheScanService` 引用模式全量索引。

## 2. 实施步骤

1. **删除中栏搜索行的两个按钮**（`MainWindow.xaml:186-187`），搜索框独占一行。
2. **删除左栏「导入资源包…」「导入文件夹…」按钮**（`MainWindow.xaml:154-157`）；
   「① 获取资源」区只剩自动加载入口。
3. **自动加载强化**（保证「只允许自动加载」仍能用）：
   - `扫描游戏资源（推荐）` 更名为 `自动加载游戏资源`（Tooltip 说明：引用模式索引全部缓存，
     不复制文件，重扫近瞬时）；
   - 打开项目时的自动扫描逻辑保持；若缓存目录缺失，提示条给出具体原因（现状已做，保留）。
4. **拖放行为收窄**：`Window_Drop` 只保留「单张图片拖到选中的图像资源上 → 询问替换」
   （`MainWindow.xaml.cs:1375-1396`）；删除 `ImportPathsAsync` 及「按普通资源包导入」分支
   与对应的 `DragOver` 文案。替换拖放是编辑行为，不属于「加载资源」，保留。
5. **保留（不删）的底层能力**，避免破坏其他链路：
   - `ModImportService` 及格式 handler（CLI `import`、导出向导「项目没有源时选择源模组」
     `MainWindow.xaml.cs:290-299`、`ExportAll_Click` 的源检查都依赖项目 Sources 概念）；
   - `ImportSingleFileAsync` 若仅被删除的 UI 引用则一并删除（grep 确认后决定）。
6. **plan-02 落地后**：以上删除在 `AssetsWorkbenchPage` 内完成；侧边栏按钮删除同步到
   `ProjectSidebarControl`。

## 3. 验收标准（审查门）

- [ ] 资源工作台可见范围内不存在任何「选择文件/文件夹加载资源」的按钮或输入框。
- [ ] 新建/打开空项目（缓存存在）→ 自动弹出扫描并完成加载 → 可搜索可预览（真实缓存冒烟）。
- [ ] 图片拖到选中纹理上仍会询问替换；拖入 .carra2/文件夹不再触发导入（无反应或提示走自动加载）。
- [ ] `dotnet build/test` 全绿（删除 UI 入口不破坏 CLI 与导出向导测试）。
- [ ] USAGE/ROADMAP 已同步「自动加载」口径。

## 4. 风险与边界

- 导入管线保留但无 UI 入口后，`ImportResult` 等类型可能变成仅测试引用——不要顺手删格式层
  代码（Carra/Lunartique/Rebank/Bank handler 仍是导出目标）。
- 拖放收窄后 `Window_DragOver` 的判定要同步简化，避免「可拖入但无响应」的误导光标。
