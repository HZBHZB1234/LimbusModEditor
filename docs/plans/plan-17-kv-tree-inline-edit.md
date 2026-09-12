# Plan 17 — 用户反馈第六批（3 项）

> 覆盖需求：①lang 页面「键值树未正常加载」；②JSON 源文件预览的文本预览框太矮；
> ③键值树不再用一个小输入框改值，改为**双击叶子行就地编辑**、删除键走右键菜单。
> 依赖 plan-09（`JsonTreeEditor` / `WorkbenchShell`）、plan-10/16（文本工作台口径）。

## 1. 结论速览

| # | 现象（用户口径） | 根因 | 落点 |
|---|---|---|---|
| ① | lang 页面的键值树未正常加载（树区一片空白） | 未选文件 / 空对象时树区**没有任何文案**；默认只展开根一层（界面上只剩「根 { 1 个键 } → dataList [ 14 项 ]」，看着像没加载）；`SelectFileAsync` 入口先 `Clear()`，重复触发或读失败会把已渲染的树抹成空白 | `JsonTreeEditor.RebuildTree` / `TextWorkbenchPage.SelectFileAsync` / `RefreshEditSetState` |
| ② | JSON 源文件预览的文本预览框高度太低 | `BuildNumberedText` 与 `TryBuildJsonTree` 把外层 Border 写死 `Height = 220`，而这两个视图被塞在 `ScrollViewer` + `StackPanel` 里——`ScrollViewer` 以无限高度测量，写死的高度就是最终高度 | `AssetsWorkbenchPage.xaml`（右栏结构）/ `.xaml.cs`（两个构建器） |
| ③ | 键值树要用一个小输入框改值 | 值编辑走「选中行 → 下方值输入框 → 应用修改」三步，且那个输入框常驻占掉树的高度 | `JsonTreeEditor.xaml` / `.xaml.cs` |

## 2. 逐项：现象 → 根因 → 修复 → 证据

### ① 键值树「未正常加载」

**先澄清事实**（静态读 + 离屏实测都做过）：树本身**能**载入，`AbDlg_Faust.json` 上
状态条正常显示「已载入文档（100 个节点）」，渲染图里 `根 → dataList → 0 → id/personalityid/…`
都在。所以用户看到的「一片空白」不是解析失败，而是界面在**三种状态下都没有交代**：

1. **还没选文件**：`JsonTreeEditor` 构造函数只把状态条写成 `—`，树区空白，没有任何提示；
   而页面一进来（或扫描未完成时）就是这个状态。
2. **默认展开太浅**：`RebuildTree` 只 `root.IsExpanded = true`，用户看到的是一行根节点 +
   一行 `dataList [ 14 项 ]`，没有任何**值**——与旧版「展平键值表直接列出所有键值」的观感差距很大。
3. **时序缺陷会真的把树清空**：`SelectFileAsync` 一进门就 `_editor.Clear()`，
   而文件树/列表在编辑集变化时会重建（`RefreshEditSetState` → `RebuildTree` / 重绑列表），
   各自抛一次选中事件；重复触发时后一次的 `Clear()` 落在前一次 `LoadDocument()` 之后，
   终态就是「文档已在内存里、树和状态条却被清空」。读失败（非 UTF-8 / 非法 JSON / 文件被删）
   同样会先抹掉当前已渲染的树。

**改法**：

- `JsonTreeEditor` 新增空态文案（`EmptyHint`）：未载入文档 → 「未载入文档：在左侧选一个文件…」；
  空对象/空数组 → 「这个 JSON 没有可展开的键（空对象 / 空数组）…」。**树区不再出现无解释的空白**。
- 默认展开两层（`DefaultExpandedDepth = 2`）：打开文件就能看到 `根 → dataList → 0 { 6 个键 }`
  这一圈**有内容**的行；展开仍是惰性的，不会全量物化。
- 单层子项**分块追加**（`ChildChunkSize = 200`）：行尾出现「… 还有 N 项（点这行继续显示）」，
  点一下追加下一批。一个 1 万项的数组一次物化会冻住 UI 好几秒，看起来也像「点了没反应」。
- 文件树**去掉「载入中…」占位子项**，改用「子项为空 + `Expanded` 时填充」的惰性口径：
  旧的占位子项会把叶子行也判成「可展开」（行首多一个点了没用的箭头）。
- `TextWorkbenchPage` 时序守卫：
  - 新增 `_lastLoadedPath`，**同一文件重复触发不再重建编辑器**（只补定位）；
  - `_editor.Clear()` 移到 `BeginEdit` **成功之后**：读失败只报错并保留现场；
  - `RefreshEditSetState` 末尾把选中态同步回树/列表（重建后容器是新的，选中高亮会丢，
    但编辑器里的内容还在——两者必须一致）；
  - `SelectPath` 改为「先试，不行则排到 `Loaded` 优先级再试」：`TreeViewItem.IsSelected`
    在容器进可视树之前设置会被 WPF 静默忽略，这正是「搜索命中点了却没定位」的机制；
    页面侧 `ReportKeyLocation` 在 `Loaded` 优先级统一复查并如实报「已定位 / 没找到这个键」。

**边界（本轮未做，如实记录）**：磁盘上确实存在 20 字节的 `{}` 之类文件（如
`Skills_Enemy-a1c9p3.json`），它们显示为空态文案而不是报错；这是产品口径问题（要不要在
浏览列标出来），不在本轮范围。

### ② 文本预览框高度

**根因**：右栏是 `ScrollViewer → StackPanel`，预览是其中一段；`ScrollViewer` 以**无限高度**
测量子元素，所以预览只能吃自己的写死高度（220），下面的「创作状态 / 属性 / 选中资源 / 常用」
越堆越多，预览就被挤成一条窄缝。

**改法**：右栏拆成两行——Row0 = 预览（`*`，占满剩余高度；内部再分「标题 / 内容 / 信息行」），
Row1 = 其余面板（`Auto` + 自带 `ScrollViewer`，内容多时自己滚）。两个构建器里的 `Height = 220`
一并删除，高度由预览列决定。图像预览**不动**：它有自己的固定视口常量（`ImagePreviewViewportHeight`），
「适应窗口」的比例算法依赖确定视口。

### ③ 叶子双击行内编辑

**改法**：

- 行头从「纯字符串」换成模板 + 视图模型 `JsonTreeRowView`（`Display` / `IsEditing` / `EditText` /
  `IsContainer`）。**编辑态靠改视图模型的属性切换，不换 `Header`/`HeaderTemplate`**——
  换模板会让 `ContentPresenter` 重新物化整棵行头，正在编辑的 TextBox（连同事件处理）会被丢掉。
- 双击叶子行 → 行内变 `TextBox` 并全选原值：`Enter` 提交、`Shift+Enter` 换行、`Esc` 取消、
  失焦按提交处理；`MaxHeight = 120` 且可换行（长台词不用横向拖）。
- 校验与写回完全复用 `JsonDocumentEditor.SetLeafText`（字符串/整数/浮点/布尔/null 按原类型写回，
  类型不符给中文错误）——**失败时恢复原值、文档不变**。
- 删除键改为**右键该行 →「删除此键…」**（确认框文案不变），键盘 `Delete` / `Backspace` 同效；
  根节点不可删（不给右键菜单）。
- 删除「选中键的值」标题与输入框、「应用修改」「删除此键…」两个按钮；新增「全部收起」，
  原「展开一层」改名「展开两层」并保持与新默认口径一致。

## 2.5 用户复核后的第二轮修正（plan-17 追加）

用户复核时指出两个仍未解决的问题，都是真缺陷：

### (a) 行内编辑的 `Enter` / 失焦不提交 —— **事件挂在了孤儿实例上**

**根因**：`BeginEditing` 里 `new TextBox { … }` 再给它挂 `KeyDown` / `LostKeyboardFocus`，
**但屏幕上真正在编辑的是行头模板（`JsonTreeRowTemplate`）生成的那个 TextBox**；
代码 new 出来的那个从没进过可视化树，永远不会收到用户的键与焦点事件。
所以编辑态看起来正常（模板实例在显示、在编辑），提交却只有「点别处导致重建」这类间接路径。

**为什么探针没抓到这个**：探针是反射直接调私有 `CommitEditing()`，绕过了键盘/焦点事件，
所以它「验证通过」是**假阳性**——这正是必须由用户实测才能暴露的一类缺陷。

**改法**：`BeginEditing` 里 `item.ApplyTemplate()` + `UpdateLayout()` 之后，
从行容器的可视化树里取出**本行自己的**编辑框（`FindEditBox`，用「最近的 TreeViewItem 就是本行」
过滤，避免误抓已展开子行的编辑框），在它身上挂 `KeyDown` / `LostKeyboardFocus`；
取不到时恢复展示态并明确报错（不静默失败）。
失焦提交同时加「焦点是否真的离开本控件」判定（`IsFocusStillInside`），
避免提交/取消收起编辑框时焦点回落再触发一次提交、误报「修改失败」。

### (b) 编辑列的树还是太矮（只剩三行）

**根因**：编辑列是「标题 / 明细 / 编辑器 `*` / 编辑集状态」四行，键值树是其中唯一的 `*` 行，
余量不足时它被挤扁；而那两段常驻说明文字（编辑集状态 + 「默认不写游戏 lang 目录…」警告）
各占三行左右。

**改法**：编辑行加 `MinHeight = 420`（常量 `EditorMinHeight`，附注释说明为什么需要下限）；
那段长警告收进 `_editSetText` 的 tooltip，常驻文案压到一行。
（**这条后来被 §2.6(b) 推翻并替换**——见下。）

## 2.6 用户复核第二轮（plan-17 追加二）

用户复核后确认上一轮的两个问题**仍然存在**，并追加一个新问题。三个都按「只读源文件做静态分析」
重新定位（本轮不再用任何动态探针）。

### (a) `Enter` / 失焦仍然不提交 —— 接线口径本身太脆

上一轮把事件挂到「从行容器里查出来的 TextBox」上，仍然不可靠：它要求
① 那次 `UpdateLayout()` 之后模板实例确实已生成（否则直接放弃），且
② 查出来的实例始终是屏幕上行内那一个。任一环不成立，编辑框看着在、按键却没人接。

**改法（不再依赖单一时机）**：

- 提交文本**从视图模型取**（编辑框绑定 `UpdateSourceTrigger=PropertyChanged` 一直在维护它），
  视图模型无值时回退读控件——提交因此不再依赖「事件恰好挂在那个实例上」；
- 接线三条路，任一条成功即可：行容器里已能查到 → 立即接线；模板今后生成时由 XAML 里的
  `Loaded="RowEditBox_Loaded"` 接线；再在 `DispatcherPriority.Render` 兜一次并触发 `Loaded`；
- `AttachEditBox` 单点接线（`TextChanged` 同步视图模型 / `KeyDown` / `LostKeyboardFocus`）；
- 收尾改为 `ResetRowView`：**优先改屏幕上那个视图对象**（`item.Header`），再兜底查缓存。
  旧实现若在编辑期间树被重建（`_rowViews` 已清空）会**新建**一个视图，而屏幕上旧视图的
  `IsEditing` 仍是 true——编辑框不消失、按键继续往已被替换的实例上提交。

### (b) lang 键值树高度 —— **完全照搬静态数据工作台的口径**

上一轮加 `MinHeight` 仍是「四行 Grid 里抢空间」的思路，没解决根因。static 页
（`StaticWorkbenchPage.xaml.cs:173`）的口径是 `new ScrollViewer { Content = editPanel }`
包一个 `StackPanel`：编辑器是**内容高度**（树有多少行就多高）、整列纵向滚动，树因此永远不会被挤扁。

**改法**：lang 页改成同一结构（`StackPanel` + 外层 `ScrollViewer`，`Focusable=false`
不占 Tab 焦点链），新增 `_editHost` 承载；`_editPanel` 类型改为 `StackPanel`；
树保留一个可视区下限（`JsonTreeEditor.xaml` 里 TreeView 的 `MinHeight="420"`）。

### (c) 一打开 lang 文件就提示「目标文件已编辑」，重启后又没了

**根因**：`LoadDocument` 结尾必发一次 `DocumentChanged(IsModified=false)`（宿主据此刷新摘要），
而 `TextWorkbenchPage.Editor_DocumentChanged` 只挡了 `_loadingDocument`、**没挡「这次事件不是修改」**。
`_loadingDocument` 在 `LoadDocument` 返回时就被 `finally` 置回 false，而 `SelectPath` 现在是延后执行的，
这次通知因此落在守卫窗口之外，于是原文被 `SetModified` 塞进编辑集 → 徽标「已修改」而且列表/树都标成已改。
编辑集只在内存里，重启即丢，所以「重启后就没了」。

**改法**：`if (!e.IsModified) return;`（`AdoptText` 与「保存原始 JSON」两条真改动路径都带
`IsModified=true`，不受影响）；并把「内容与编辑集已有文本完全一致」的短路条件简化为只比对文本。

## 2.7 用户复核第三轮（plan-17 追加三）

用户复核 §2.6 的产物后确认：**`Enter` 仍不提交**、**打开任何 lang 文件仍立刻判为「已修改」**。
两处都不是时序抖动，而是口径/接线设计本身的错。本轮明确要求：**只做源文件静态阅读定位，
不跑动态探针**。

### (a) `Enter` 不提交 —— 接线仍然建立在「事件恰好挂到了那个实例上」

§2.6(a) 把「取文本」改成了从视图模型取（这一步是对的、保留了），但**按键与焦点的接线**
依旧挂在「从行容器里查出来的那个 TextBox 实例」上：查不到就在 `DispatcherPriority.Render`
再查一次，**再查不到就彻底放弃**——界面上编辑框一切正常（模板实例在显示、能打字，
因为打字走的是 XAML 绑定），但没有任何代码接住 `Enter`。

**根因（设计层）**：把「控件能否提交」押在「某次查询在某个时机能否找到模板实例」上。
只要这条链断一次，用户看到的就是「编辑框在、回车没反应」，而且**没有任何错误提示**——
这类静默失败最该被结构性地排除，而不是靠加一条兜底路径。

**改法（让接线不依赖任何时机假设）**：

1. **按键改在控件这一层用 `Preview`（隧道）**：`Tree_PreviewKeyDown` 在 `_editingRow is not null`
   时处理 `Enter`（提交）与 `Esc`（取消）。隧道事件从根往下走，**只要焦点还在本控件内的任意元素上
   就一定跑得到**，与「TextBox 实例是否被找到」彻底解耦；同时 Preview 先于 TextBox 自身的
   `KeyDown`，不会重复提交。
2. **失焦提交按用户口径重写**：「焦点不再位于本行编辑框内」即视为失焦 → 提交。
   判定的是**去向**（`KeyboardFocusChangedEventArgs.NewFocus`）而不是「编辑框自己丢了焦点」：
   挂在本控件上的 `PreviewLostKeyboardFocus` 会收到整条隧道链上的每一次焦点变化，因此
   行内编辑框、菜单、别行控件都在同一条线里判得清楚。
   仍在「本次编辑会话」内的去向不提交：编辑框自身/其内部部件，以及**本次编辑行的右键菜单**
   （否则菜单一弹出就先提交，菜单项指向的行会被重建掉）。
3. **点别处也走同一条判定**：`PreviewMouseDown` 落在编辑框之外时排一拍提交，
   排到 `DispatcherPriority.Input` 之后（提交会重建树，在鼠标/焦点事件处理过程中重建会让
   输入状态错乱）。
4. **找编辑框这件事本身改成确定性重试**：`UpdateLayout()` 逼模板生成 → 当场查 →
   `LayoutUpdated` / `ItemContainerGenerator.StatusChanged` 两个信号 → 再叠一条「按帧重试」
   兜底链（编辑结束或换行即自停）。任何一步成功即接线并全部解绑。

### (b) 一打开 lang 文件就判「已修改」 —— 界面把「打开过」当成「改过了」

**根因（口径错）**：`LangTextWorkbenchService.BeginEdit` 为了让导出能算 RFC6902 差分，
会把**每一个被打开过的文件**都登记进编辑集（`VanillaText == ModifiedText`）。
而页面的「已修改」徽标 / 列表状态列 / 树上的 `●` / 「编辑集：N 个文件已改」全部用的是
`IsModified`（= `_edits.ContainsKey`）——于是**预览即已修改**，且计数永远 ≥ 1。
编辑集只在内存里，重启后 `_edits` 为空（直到再次打开某个文件），所以「重启后就没了」。
§2.6(c) 修的 `!e.IsModified` 守卫是**必要的**（它挡掉了载入通知误写编辑集），但
**不足以**修掉这个现象——真正的判定源一直是「在不在编辑集里」。

**改法**：

- 服务层新增**「确有改动」口径**：`HasRealEdits(path)` / `RealEditFiles`
  （vanilla 与 modified 逐字节比较）。`IsModified` 保留原义「在编辑集里（已建立导出基线）」，
  两处 XML 注释写明区别，避免下次再被当成同义词。
- 界面全部换成新口径：徽标、列表行 `Modified`/`StateLabel`、「仅已修改」筛选、树上的 `●`、
  `_editSetText` 计数；「还原此文件」按钮的可用性也跟「确有改动」走。
- 明细行文案顺带说清状态：`已修改（未导出）` / `已建立基线（未改动）`，不再一律写「已在编辑集中」。
- `Editor_DocumentChanged` 补上反向处理：**内容被改回与原文逐字节一致时把条目移出编辑集**
  （`Revert`），否则「编辑集：N 个文件已改」会永久挂着一个已经改回去的文件。
- 页面写编辑集改走**宿主会话**（`_host.LangEdits.SetModified/Revert`）而不是直接调服务：
  会话要推进 `Revision` 并广播 `Changed`，直连服务会让 §5 记的「`Revision` 恒为 0」继续成立。
  （§5 的那条待决项因此在本轮落地为「改代码」而不是「改注释」。）

**回归**：新增 `Lang_session_separates_open_baseline_from_real_modification`
（`ModExportPlanTests`）：打开预览 → `IsModified` 真 / `HasRealEdits` 假；改一处 → 真；
改回原文 → 又假。这条钉住的正是「预览 ≠ 已修改」。

## 3. 改动文件

| 文件 | 改动 |
|---|---|
| `src/LimbusModEditor.App/WorkbenchPages/JsonTreeEditor.xaml` | 行头模板（含编辑框 `Loaded` 接线）+ 取反可见性转换器；去掉值输入框与两个按钮；空态文案 / 「全部收起」/ 操作提示；TreeView `MinHeight="420"` |
| `src/LimbusModEditor.App/WorkbenchPages/JsonTreeEditor.xaml.cs` | `JsonTreeRowView` 视图模型；行内编辑状态机（**`Preview` 隧道按键 + 焦点去向判定 + 点别处提交 + 按帧重试接线**，见 §2.7(a)）；右键删除；默认展开两层；分块追加；空态；`SelectPath` 延后一拍 |
| `src/LimbusModEditor.Application/Texts/LangTextWorkbenchService.cs` | 新增「确有改动」口径 `HasRealEdits` / `RealEditFiles`（与「在编辑集里」的 `IsModified` 明确区分） |
| `src/LimbusModEditor.Application/Texts/LangEditSession.cs` | 透出 `HasRealEdits` / `RealEditFiles`；`Revision` 注释改为与实现一致的说明 |
| `src/LimbusModEditor.App/WorkbenchPages/TextWorkbenchPage.xaml.cs` | `_lastLoadedPath` 守卫、清空时机后移、选中态同步、定位结果回报；编辑列改为静态页口径（`StackPanel` + `_editHost` 滚动）；`Editor_DocumentChanged` 挡掉非修改事件 + 「改回原文则移出编辑集」；**界面「已修改」全部改按 `HasRealEdits` 判定**；写编辑集改走宿主会话 |
| `src/LimbusModEditor.App/WorkbenchPages/AssetsWorkbenchPage.xaml` | 右栏拆 `*`（预览）/ `Auto`（其余 + 滚动）两行 |
| `src/LimbusModEditor.App/WorkbenchPages/AssetsWorkbenchPage.xaml.cs` | 删除两处 `Height = 220` |
| `tests/LimbusModEditor.Domain.Tests/ModExportPlanTests.cs` | 新增 `Lang_session_separates_open_baseline_from_real_modification`（预览 ≠ 已修改的回归锚点） |
| `docs/USAGE.md` | 资源页预览形态表 + 文本工作台编辑列两节按新交互重写 |

公共控件 `JsonTreeEditor` 由文本页与静态页共用：**静态数据工作台同样受益**，其调用面
（`LoadDocument` / `Clear` / `SelectPath` / `CurrentJsonText` / `IsModified` / `HasDocument` /
`DiffSummary` / `DocumentChanged` / `StatusMessage`）本轮未变。

## 4. 审查门

- [x] `dotnet build` 全绿（`TreatWarningsAsErrors`，0 警告 0 错误）
- [x] `LimbusModEditor.Domain.Tests` **536 passed / 0 failed**（计划前的基线同为 536：
      ③ 的校验/写回路径复用既有 `JsonDocumentEditor`，其单测已覆盖——
      `Set_leaf_text_writes_back_original_types` / `Set_leaf_text_rejects_wrong_types`
      （含「类型不符时文档保持不变」）/ `Set_leaf_text_rejects_container_rows` /
      `Set_leaf_text_on_root_leaf_without_parent_throws`）
- [x] `WorkbenchPages/` 无设计色字面量（色值只在 `Themes/`）；未新增色值
- [x] 写死预览高度归零：`grep 'Height\s*=\s*"?220'` 在 `src/LimbusModEditor.App` 无命中
- [x] 真实 lang 数据离屏核对：文本页键值树渲染
      （`根 → dataList → 0 → id/personalityid/voicefile/teller/dialog/usage`）、空态文案、
      预览列高度随窗口变化（950px 窗口下预览视口 675px，旧实现恒为 220）
- [x] `docs/USAGE.md` 更新；本文件即计划记录
- [x] 用户复核第二轮修正后重跑：`dotnet build LimbusModEditor.slnx` 0 警告 0 错误；
      `dotnet test` **74 Format + 536 Domain 全绿**
- [x] 用户复核第三轮修正后重跑：`dotnet build LimbusModEditor.slnx` 0 警告 0 错误；
      `dotnet test` **74 Format + 537 Domain 全绿**（+1 = 本轮新增的「预览 ≠ 已修改」回归）
- [ ] 人工复核（用户侧）：双击叶子 → `Enter` 提交 / 点别处提交 / `Esc` 取消；
      键值树高度与滚动是否与静态页一致；打开文件后**不再**自动出现「已修改」徽标
- [ ] `dotnet publish`（Release）——按仓库基线三命令在收尾时执行

## 5. 本轮顺带发现（**未修**，留作决定）

- ~~`LangEditSession.Revision` 恒为 0，且它的 `Changed` 事件永不触发~~
  → **已修**（§2.7(b)）：页面写编辑集改走 `_host.LangEdits.SetModified/Revert`，
  `Bump()` 因此会执行、两类事件订阅者（页面的 `RefreshEditSetState`）也真的被叫到。
  两条回归锚点已在 `ModExportPlanTests.Lang_session_exposes_revision_and_snapshot_with_patch_keys`。
