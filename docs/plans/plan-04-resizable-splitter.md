# Plan 04 — 资源列表/树与预览区拖拽调整占比

> 覆盖需求 6：「对于资源工作台页面，允许用户拖拽调整资源列表/资源树与资源预览页面的大小占比。」

## 1. 现状（证据）

- 资源工作台三列定宽比例布局：`MainWindow.xaml:135`
  `<ColumnDefinition Width="220"/><ColumnDefinition Width="*"/><ColumnDefinition Width="260"/>`
  —— 中栏（列表/目录树）与右栏（预览+操作）之间**没有 GridSplitter**，260px 预览区过窄且不可调。
- 无任何 UI 状态持久化机制（窗口尺寸为 XAML 硬编码 `Height=720 Width=1280`）。

## 2. 实施步骤

1. **列定义改造**（plan-02 后在 `AssetsWorkbenchPage.xaml`）：

   ```
   [220 侧边栏(共享列，不在本页)][* 浏览列][4 GridSplitter][320~* 预览列]
   ```

   - 预览列改为带宽度的自适应列（`Width` 由 splitter 控制），默认 360px；
   - `GridSplitter`：`HorizontalAlignment="Stretch"`、`VerticalAlignment="Stretch"`、
     `ResizeBehavior="PreviousAndNext"`、`ResizeDirection="Columns"`、`ShowsPreview="True"`；
   - 拖拽把手视觉：1px 深色分隔 + hover 高亮（沿用现有 #2A3540/#4C8DDA 配色）。
2. **最小宽度约束**：浏览列 `MinWidth=280`（搜索行 + 树可用），预览列 `MinWidth=260`
   （属性区/预览图可用），窗口 `MinWidth=960`（已有）不变。
3. **占比持久化**：新增 `<程序目录>/config/ui-state.json`（`AppEnvironment` 增加
   `LoadUiState()/SaveUiState()`，与 shared-config 同目录同风格），保存：
   - 资源页 splitter 位置（预览列宽）；
   - plan-02 中若侧边栏也加 splitter，则一并保存侧边栏宽（可选增强）。
   - 读取失败/文件不存在 → 默认值；写入时机： splitter 拖动完成
     （`Splitter` 无完成事件，用 `PreviewMouseLeftButtonUp` 或 `LostMouseCapture`）+ 窗口关闭。
4. **双击把手复位默认宽**（参考 dsh-aionui-panel 的交互约定）：splitter 上双击 →
   恢复默认列宽并持久化。
5. plan-06/07/08 的新页面复用同一 `GridSplitter` 模式（抽出共享 Style/行为），保持全应用
   布局手感一致。

## 3. 验收标准（审查门）

- [ ] 拖拽中栏与预览区之间的把手可平滑调整两者宽度，松手后布局稳定（树/列表虚拟化不抖动）。
- [ ] 重启应用后保持上次占比；删除 ui-state.json 后恢复默认；双击把手复位。
- [ ] 拖到极限（MinWidth）不再压缩，布局不破（右键菜单/预览图/属性区正常）。
- [ ] plan-01 的属性区、plan-05 的多形态预览在 ≥260px 宽度下可用。
- [ ] build + test 全绿（`AppEnvironment` 新增读写有单测：缺失/损坏 JSON 回退默认）。

## 4. 风险与边界

- TreeView/ListView 虚拟化在列宽变化时会重新布局：40 万级索引下拖动应仍流畅
  （只改列宽，不触发搜索重跑）；验证真实缓存下拖动手感。
- 不要把占比存进 `.lmeproj`（项目文件已瘦身，UI 状态不属于项目数据）。
