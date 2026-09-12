using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LimbusModEditor.Application.Documents;
namespace LimbusModEditor.App;

/// <summary>一次文档变更的快照（宿主据此决定怎么存编辑集、怎么导出）。</summary>
/// <param name="JsonText">当前（含修改的）完整 JSON 文本。</param>
/// <param name="IsModified">相对本次载入是否已有修改。</param>
/// <param name="DiffOperationCount">与基线（官方版本；未提供时为载入文本）的 RFC6902 op 数。</param>
public sealed record JsonDocumentChangedEventArgs(string JsonText, bool IsModified, int DiffOperationCount);

/// <summary>
/// 键值树一行的视图模型（行头模板的绑定源）。
///
/// <para><b>为什么要有它</b>：本控件的行头必须能在「展示」与「就地编辑」之间切换
/// （双击叶子改值），而绑定是这条路最省心的做法——把「显示什么 / 是否在编辑 / 编辑框里是什么」
/// 三个状态放在视图模型上，模板只做 Visibility 切换。</para>
///
/// <para><b>为什么必须可变且可通知</b>：编辑态靠改这个对象的属性来切换，
/// 而不是换 <c>Header</c>/<c>HeaderTemplate</c>——换模板会让 ContentPresenter 重新物化
/// 整棵行头，正在编辑的 TextBox（连同它的事件处理）会被丢掉，用户敲一个字就失焦。</para>
/// </summary>
public sealed class JsonTreeRowView : System.ComponentModel.INotifyPropertyChanged
{
    private string _display = string.Empty;
    private bool _isEditing;
    private string _editText = string.Empty;

    /// <summary>行的展示文本（容器 = <c>名字 { N 个键 }</c>；叶子 = <c>键: 值</c>）。</summary>
    public string Display
    {
        get => _display;
        set => Set(ref _display, value, nameof(Display));
    }

    /// <summary>是否正处在就地编辑态（模板据此切换 TextBlock / TextBox）。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => Set(ref _isEditing, value, nameof(IsEditing));
    }

    /// <summary>就地编辑框的文本（双向绑定；未编辑时为空串）。</summary>
    public string EditText
    {
        get => _editText;
        set => Set(ref _editText, value, nameof(EditText));
    }

    /// <summary>是否是可展开的容器行（叶子行与占位行不显示展开箭头）。</summary>
    public bool IsContainer { get; init; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}

/// <summary>取反的布尔 → 可见性（行头里「展示文本」在编辑时收起）。</summary>
public sealed class InverseBoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is Visibility.Collapsed;
}

/// <summary>
/// plan-09：共享 JSON 编辑器控件（plan-10 文本工作台 / plan-12 静态工作台共用，
/// 杜绝第二份实现）。
///
/// <para>能力：对象/数组/叶子树（<b>按层惰性展开</b>，单层项数过多时<b>分块追加</b>）、
/// <b>双击叶子行就地改值</b>（按原值类型写回：字符串/整数/浮点/布尔/null；类型不匹配给中文错误）、
/// 右键菜单 / Delete 键删除键（确认框）、原始 JSON 双 Tab（树 / 原文，非法 JSON 拒绝且不改编辑集）、
/// 与官方版本的差异摘要（<see cref="LimbusModEditor.Application.Texts.TextDiffService"/>
/// 生成的 RFC6902 op 数，供 plan-12 的 diff Tab 复用）。</para>
///
/// <para><b>本控件不写任何文件、不管导出</b>：它通过 <see cref="DocumentChanged"/>
/// 把「当前修改后的 JSON 文本」「是否有修改」交给宿主页面，宿主自己决定怎么存编辑集与导出。
/// 一切判定委托给纯逻辑内核 <see cref="JsonDocumentEditor"/>（无 WPF 依赖，可单测）。</para>
/// </summary>
public partial class JsonTreeEditor : UserControl
{
    /// <summary>默认自动展开的层数：根（0）与它下面一层容器（1）——
    /// 打开文件立刻能看到 <c>根 → dataList → 0</c> 这一圈「有内容的行」，
    /// 而不是只有一行根节点（用户反馈「树没加载」的一种真实观感）。
    /// 展开仍是惰性的：只有被展开的那一层会被生成。</summary>
    private const int DefaultExpandedDepth = 2;

    /// <summary>单层子项的分块大小：一次最多生成这么多行，其余靠行尾的「继续显示」占位项追加。
    /// 一个 1 万项的数组若一次物化，UI 线程会冻住好几秒（看起来就像「点了没反应/树是空的」）。</summary>
    private const int ChildChunkSize = 200;

    /// <summary>自动计算差异摘要的文档体量上限（两侧文本字符数之和）。超过就改成按需计算，
    /// 避免 MB 级静态表在每次编辑时全量走一遍 <c>TextDiffService</c> 冻住 UI。</summary>
    private const int DiffAutoComputeMaxChars = 1_000_000;

    /// <summary>「刚提交完」的静默窗口（毫秒）：提交会重建树，紧接着到达的双击事件
    /// 若还去开编辑，用户看到的就是「回车后又被打开了编辑框」。</summary>
    private const int DoubleClickGuardMs = 250;

    /// <summary>「还有更多子项」占位行的 Tag（与真实行的 <see cref="JsonEditRow"/> 区分）。</summary>
    private sealed record OverflowMarker(JsonEditRow Parent, int Shown);

    private JsonNode? _document;
    private JsonEditRow? _selected;
    private string _loadedText = string.Empty;
    private string? _vanillaText;
    private bool _modified;

    /// <summary>行为树行头模板的绑定源（路径 → 视图模型），行内编辑时替换整行。</summary>
    private readonly Dictionary<string, JsonTreeRowView> _rowViews = new(StringComparer.Ordinal);

    // 就地编辑会话状态（同一时刻只允许一行在编辑）。
    private TreeViewItem? _editingItem;
    private TextBox? _editingBox;
    private JsonEditRow? _editingRow;
    private string _editingOriginal = string.Empty;
    private bool _editClosing;
    /// <summary>「提交」已排队（失焦与鼠标按下可能在同一拍里各报一次，靠它去重）。</summary>
    private bool _commitPending;
    /// <summary>最近一次提交的时刻：提交会重建树，紧接着到达的双击不该再开一次编辑。</summary>
    private long _lastCommitTicks;

    public JsonTreeEditor()
    {
        InitializeComponent();
        // 这棵树在 XAML 里声明（不经过 WorkbenchShell.CreateTree），因此滚轮支持要在这里显式接上：
        // 否则指针悬停在树行上时滚轮事件被行/模板 ScrollViewer 吃掉，只有悬停空白处才滚得动。
        WorkbenchShell.EnableWheelScrolling(Tree);
        SetEmptyState("未载入文档：在左侧选一个文件即可查看键值树。");
    }

    /// <summary>文档变更（值写回 / 删除键 / 保存原文）。载入文档时也会触发一次（IsModified=false）。</summary>
    public event EventHandler<JsonDocumentChangedEventArgs>? DocumentChanged;

    /// <summary>控件内部状态文案（宿主可转发到工作台状态条）。</summary>
    public event EventHandler<string>? StatusMessage;

    /// <summary>当前（含修改的）完整 JSON 文本；无文档时为空串。</summary>
    public string CurrentJsonText { get; private set; } = string.Empty;

    /// <summary>相对本次载入是否已有修改。</summary>
    public bool IsModified => _modified;

    /// <summary>是否已载入文档。</summary>
    public bool HasDocument => _document is not null;

    /// <summary>与基线（官方版本；未提供时为载入文本）的 RFC6902 op 数。</summary>
    public int DiffOperationCount { get; private set; }

    /// <summary>差异摘要的中文文案（plan-12 的 diff Tab 可直接显示）。</summary>
    public string DiffSummary => DiffInfo.Text;

    /// <summary>当前选中的树行。</summary>
    public JsonEditRow? SelectedRow => _selected;

    /// <summary>载入时的原文（宿主可用来做「还原」）。</summary>
    public string LoadedJsonText => _loadedText;

    /// <summary>与官方版本比较的基线文本（未提供时为 null）。</summary>
    public string? VanillaJsonText => _vanillaText;

    /// <summary>载入文档。<paramref name="jsonText"/> 必须是合法 JSON 且根不是 null，
    /// 否则抛 <see cref="InvalidDataException"/>（中文原因）且控件保持原状态。
    /// <paramref name="vanillaJsonText"/> 给「与官方版本差异」用的基线（plan-12 传官方原文）。</summary>
    public void LoadDocument(string jsonText, string? vanillaJsonText = null)
    {
        var document = JsonDocumentEditor.ParseStrict(jsonText);
        _document = document;
        _loadedText = jsonText;
        CurrentJsonText = jsonText;
        _vanillaText = vanillaJsonText;
        _modified = false;

        RawBox.Text = jsonText;
        Tabs.SelectedIndex = 0;
        RebuildTree();
        UpdateDiffSummary();
        var nodes = JsonDocumentEditor.CountNodes(document);
        var suffix = nodes >= 20000 ? "（至少这么多）" : string.Empty;
        SetStatus($"已载入文档（{nodes}{suffix} 个节点；树按需展开，不做全量物化）。");
        RaiseDocumentChanged();
    }

    /// <summary>清空（切换选中项 / 关闭页面时用）。</summary>
    public void Clear()
    {
        _document = null;
        _selected = null;
        _loadedText = string.Empty;
        _vanillaText = null;
        CurrentJsonText = string.Empty;
        _modified = false;
        DiffOperationCount = 0;
        EndEditing();
        Tree.Items.Clear();
        _rowViews.Clear();
        RawBox.Text = string.Empty;
        DiffInfo.Text = "—";
        SetStatus("—");
        RefreshButtons();
    }

    /// <summary>定位并选中某个键路径（展平口径，如 <c>dataList/0/dialog</c>），
    /// 沿途逐级展开。路径不存在时返回 false。
    ///
    /// <para><b>为什么选择动作要延后一拍</b>：<c>TreeViewItem.IsSelected</c> 只有在容器
    /// 真正进入可视树并完成布局之后才生效（在 <c>LoadDocument</c> 里紧接着调用会被 WPF
    /// 静默忽略，表现为「搜索点了命中但树没有定位」）。这里在 <c>Loaded</c> 优先级再试。</para></summary>
    public bool SelectPath(string path)
    {
        if (_document is null) return false;
        if (TrySelectPath(path)) return true;
        // 首次加载时树刚建好、还没走完布局：排队到布局之后再定位一次。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => TrySelectPath(path)));
        return false;
    }

    private bool TrySelectPath(string path)
    {
        if (_document is null) return false;
        var item = ExpandChain(path);
        if (item?.Tag is not JsonEditRow row || row.Path != path) return false;
        item.IsSelected = true;
        item.BringIntoView();
        return true;
    }

    // ── 树构建（惰性展开 + 分块）─────────────────────────────────────

    private void RebuildTree()
    {
        EndEditing();
        Tree.Items.Clear();
        _rowViews.Clear();
        _selected = null;
        if (_document is null)
        {
            SetEmptyState("未载入文档：在左侧选一个文件即可查看键值树。");
            RefreshButtons();
            return;
        }
        var rootRow = JsonDocumentEditor.CreateRoot(_document);
        var hasContent = JsonDocumentEditor.CountChildren(_document) > 0;
        if (!hasContent)
        {
            // 空对象 / 空数组：摆出根行，并明确说明「这里确实没有键」，
            // 免得用户以为界面坏了（旧实现同样会显示一个孤零零的根节点）。
            SetEmptyState("这个 JSON 没有可展开的键（空对象 / 空数组）——请用「原文 JSON」页查看或编辑。");
        }
        else
        {
            SetEmptyState(null);
        }
        var root = CreateItem(rootRow);
        Tree.Items.Add(root);
        root.IsExpanded = true; // 默认展开第一层（与资源工作台/旧键值表的默认口径一致）
        RefreshButtons();
    }

    private TreeViewItem CreateItem(JsonEditRow row)
    {
        var item = new TreeViewItem
        {
            Header = ViewFor(row),
            HeaderTemplate = HeaderTemplate,
            Tag = row,
            Style = WorkbenchShell.CreateTreeItemStyle(),
        };
        // 每一层放一个占位子项，真正展开时才生成这一层（见 MaterializeChildren）。
        // 占位项 Collapsed：它只是「还有子项」的标记，不该在界面上露出来。
        if (row.IsContainer && row.ChildCount > 0) item.Items.Add(CreatePlaceholder());
        item.Expanded += TreeItem_Expanded;
        AttachRowContextMenu(item, row);
        return item;
    }

    /// <summary>行头模板的绑定源（同路径复用<b>同一个</b>视图模型实例：行内编辑改的是它的属性，
    /// 换实例等于换掉正在编辑的行头）。</summary>
    private JsonTreeRowView ViewFor(JsonEditRow row)
    {
        if (_rowViews.TryGetValue(row.Path, out var existing)) return existing;
        var view = new JsonTreeRowView { Display = BuildHeader(row), IsContainer = row.IsContainer };
        _rowViews[row.Path] = view;
        return view;
    }

    private DataTemplate? HeaderTemplate => TryFindResource("JsonTreeRowTemplate") as DataTemplate;

    private static string BuildHeader(JsonEditRow row)
        => row.IsContainer ? $"{row.Name}   {row.Preview}" : $"{row.Name}: {row.Preview}";

    private void TreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        // 只处理真正被展开的那一项：Expanded 是冒泡事件，父项展开时子项也会收到。
        if (e.OriginalSource is TreeViewItem item) MaterializeChildren(item);
    }

    /// <summary>把占位项换成真实子项（只在展开时生成这一层，超过分块大小则追加「继续显示」行）。</summary>
    private void MaterializeChildren(TreeViewItem item)
    {
        if (item.Tag is not JsonEditRow row) return;
        if (item.Items.Count != 1 || item.Items[0] is not TreeViewItem { Tag: null }) return;
        item.Items.Clear();
        if (row.IsContainer && row.ChildCount > 0) AppendChildren(item, row, 0);
    }

    /// <summary>从 <paramref name="skip"/> 开始追加一批子项（分块加载的核心）。</summary>
    private void AppendChildren(TreeViewItem item, JsonEditRow row, int skip)
    {
        if (row.IsContainer && row.ChildCount > 0)
        {
            var children = JsonDocumentEditor.EnumerateChildren(row);
            var end = Math.Min(children.Count, skip + ChildChunkSize);
            for (var i = skip; i < end; i++) item.Items.Add(CreateItem(children[i]));
            if (end < children.Count) item.Items.Add(CreateOverflowItem(item, row, end));
        }
    }

    /// <summary>「还有 N 项」占位行：点它或按右键都会追加下一批。</summary>
    private TreeViewItem CreateOverflowItem(TreeViewItem parentItem, JsonEditRow row, int shown)
    {
        var remaining = row.ChildCount - shown;
        var marker = new OverflowMarker(row, shown);
        var header = new TextBlock
        {
            Text = $"… 还有 {remaining} 项（点这行继续显示）",
            Style = TryFindResource("WorkbenchStatusText") as Style,
            Margin = new Thickness(0),
        };
        var overflow = new TreeViewItem
        {
            Header = header,
            Tag = marker,
            Style = WorkbenchShell.CreateTreeItemStyle(),
        };
        overflow.MouseLeftButtonUp += (_, e) =>
        {
            AppendChildren(parentItem, row, marker.Shown);
            // 追加之后原来的占位行自己让位（AppendChildren 会再放一个新的在末尾）。
            parentItem.Items.Remove(overflow);
            e.Handled = true;
        };
        return overflow;
    }

    private static TreeViewItem CreatePlaceholder()
        => new() { Header = string.Empty, Tag = null, Visibility = Visibility.Collapsed };

    private static bool IsPlaceholder(TreeViewItem item)
        => item.Items.Count == 1 && item.Items[0] is TreeViewItem { Tag: null };

    /// <summary>逐级展开到指定路径并返回该项（用于编辑后保持定位）。</summary>
    private TreeViewItem? ExpandChain(string path)
    {
        if (Tree.Items.Count == 0 || Tree.Items[0] is not TreeViewItem root) return null;
        if (path.Length == 0) return root;

        var current = root;
        var accumulated = string.Empty;
        foreach (var token in path.Split('/'))
        {
            accumulated = accumulated.Length == 0 ? token : $"{accumulated}/{token}";
            if (accumulated.Length == 0) continue; // 根节点（路径为空）
            MaterializeChildren(current);
            var next = FindChild(current, accumulated);
            if (next is null) return current;
            next.IsExpanded = true; // 触发 Expanded → 惰性生成下一层
            current = next;
        }
        return current;
    }

    private static TreeViewItem? FindChild(TreeViewItem parent, string path)
    {
        foreach (var entry in parent.Items)
        {
            if (entry is TreeViewItem item && item.Tag is JsonEditRow row && row.Path == path) return item;
        }
        return null;
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _selected = (e.NewValue as TreeViewItem)?.Tag as JsonEditRow;

    private void ExpandRoot_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.Items.Count > 0 && Tree.Items[0] is TreeViewItem root) root.IsExpanded = true;
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.Items.Count > 0 && Tree.Items[0] is TreeViewItem root) root.IsExpanded = false;
    }

    /// <summary>空态文案（null / 空串 = 隐藏）。<b>一定要有</b>：树区一片空白时用户
    /// 无法区分「这个文件没有键」「还没选文件」与「界面坏了」。</summary>
    private void SetEmptyState(string? message)
    {
        var visible = !string.IsNullOrWhiteSpace(message);
        EmptyHint.Text = visible ? message! : string.Empty;
        EmptyHint.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 行内编辑（双击叶子）──────────────────────────────────────────

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_editingBox is not null) return;                     // 已在编辑：让编辑框自己处理
        // 提交会重建整棵树，双击的第二下（在提交之后到达）不该顺手再开一次编辑。
        if (Environment.TickCount64 - _lastCommitTicks < DoubleClickGuardMs) return;
        if (RowItemAt(e.OriginalSource as DependencyObject) is not { Tag: JsonEditRow row } item) return;
        if (!row.IsLeaf || row.Parent is null) return;           // 容器 / 根：双击仍然只是展开收起
        BeginEditing(item, row);
        e.Handled = true;
    }

    /// <summary>从命中的可视化元素向上找它所属的树行（点的是行头文本还是空白行区域都算）。</summary>
    private TreeViewItem? RowItemAt(DependencyObject? source)
    {
        var node = source;
        while (node is not null)
        {
            if (node is TreeViewItem item && item.Tag is JsonEditRow) return item;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void BeginEditing(TreeViewItem item, JsonEditRow row)
    {
        EndEditing();
        var original = JsonDocumentEditor.LeafText(row.Node);
        var view = ViewFor(row);
        view.EditText = original;
        view.IsEditing = true;   // 顺序要紧：先把文本放好，再翻开关（模板立刻绑定到它）

        _editingItem = item;
        _editingRow = row;
        _editingOriginal = original;
        _editClosing = false;
        _commitPending = false;
        SetStatus($"编辑 {row.Path}：Enter 提交，Esc 取消（原值类型会照原样写回）。");
        AttachEditBoxWhenReady(item);
    }

    /// <summary>
    /// 等行头模板里的编辑框真正生成后接线（挂焦点钩子 + 把焦点放进去）。
    ///
    /// <para><b>为什么不能只「查一次 / 排一拍再查」</b>：编辑框是 <c>Visibility</c> 绑定翻成
    /// Visible 之后才进入布局、再进入可视树的。查早了一定查不到，而查不到的历史后果是
    /// 整个就地编辑没有键盘接线（Enter 不提交、点了别处也不提交）——界面看起来却完全正常。
    /// 这里用<b>确定性</b>的多重兜底：立刻 <c>UpdateLayout()</c> 逼模板生成并当场查一次；
    /// 查不到就挂 <c>LayoutUpdated</c> 与 <c>ItemContainerGenerator.StatusChanged</c>
    /// （模板重新物化、容器生成时都会再来一次），再叠一条按帧重试的兜底链。</para>
    /// </summary>
    private void AttachEditBoxWhenReady(TreeViewItem item)
    {
        item.UpdateLayout();
        if (TryAttachEditBox(item)) return;

        void OnLayoutUpdated(object? sender, EventArgs e) => TryAttachEditBox(item);
        void OnStatusChanged(object? sender, EventArgs e) => TryAttachEditBox(item);

        void Detach()
        {
            LayoutUpdated -= OnLayoutUpdated;
            item.ItemContainerGenerator.StatusChanged -= OnStatusChanged;
        }

        bool TryAttachEditBox(TreeViewItem owner)
        {
            if (_editingRow is null || !ReferenceEquals(_editingItem, owner)) { Detach(); return true; }
            if (_editingBox is not null) { Detach(); return true; }
            if (FindEditBox(owner) is not { } box) return false;
            AttachEditBox(box);
            Detach();
            return true;
        }

        LayoutUpdated += OnLayoutUpdated;
        item.ItemContainerGenerator.StatusChanged += OnStatusChanged;
        RetryAfterFrames(item, 60, TryAttachEditBox);
    }

    /// <summary>按帧重试（最多 <paramref name="frames"/> 帧，约 1 秒）：条件满足、或编辑已经
    /// 结束/换行时立刻停手。给「模板要等一次布局才生成」这类时序兜底，不留下常驻定时器。</summary>
    private void RetryAfterFrames(TreeViewItem item, int frames, Func<TreeViewItem, bool> attempt)
    {
        if (frames <= 0) return;
        if (_editingRow is null || !ReferenceEquals(_editingItem, item)) return;   // 编辑已结束/换行
        if (attempt(item)) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => RetryAfterFrames(item, frames - 1, attempt)));
    }

    /// <summary>把事件接到**屏幕上那个**编辑框上（只接一次），并把焦点放进去。</summary>
    private void AttachEditBox(TextBox box)
    {
        if (_editingBox is not null || _editingRow is null) return;
        _editingBox = box;
        box.TextChanged += EditBox_TextChanged;
        // 焦点钩子挂在这里（控件自己）而不只挂在编辑框上：行头模板是 Collapsed/展开
        // 动态生成的，编辑框可能在极端时序下还没进可视树；而本控件一定在树上，
        // 编辑框的焦点事件一定会冒泡经过本控件——这条线不会断。
        PreviewLostKeyboardFocus += Editor_PreviewLostKeyboardFocus;
        PreviewMouseDown += Editor_PreviewMouseDown;
        FocusEditBox(box);
        var path = _editingRow.Path;
        SetStatus($"编辑 {path}：Enter 提交，Esc 取消（原值类型会照原样写回）。");
    }

    /// <summary>本行编辑框当前应有的文本：优先问屏幕上那个 TextBox（用户敲的字就在它里面），
    /// 取不到再退回视图模型（绑定是 <c>UpdateSourceTrigger=PropertyChanged</c>，
    /// 值经绑定同步，因此不依赖任何事件是否挂上）。</summary>
    private string? CurrentEditText()
        => _editingBox?.Text ?? (_editingItem?.Header as JsonTreeRowView)?.EditText;

    /// <summary>
    /// 用户在编辑框里改字。文本经绑定（<c>UpdateSourceTrigger=PropertyChanged</c>）同步到
    /// 视图模型，这里再兜一次（控件未接上事件时也保证值在视图模型里）。
    ///
    /// <para><b>为什么提交要从视图模型取文本</b>：这样提交就不再依赖「事件恰好挂在屏幕上那个
    /// 实例上」——只要 TextBox 还连着绑定，最新值就一定在视图模型里。</para>
    /// </summary>
    private void EditBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (_editingBox is not null && !ReferenceEquals(box, _editingBox)) return;
        if (_editingItem?.Header is JsonTreeRowView view) view.EditText = box.Text;
    }

    /// <summary>行头模板里那个编辑框加载完成：只认当前正在编辑的那一行，补一次接线。
    /// 这是 <see cref="AttachEditBoxWhenReady"/> 之外的第四条兜底（XAML 里挂的 Loaded），
    /// 因此它必须自身安全：任何不匹配的情况都直接返回，绝不改变状态。</summary>
    private void RowEditBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (_editingRow is null || _editingBox is not null) return;
        if (_editingItem is null || NearestItem(box) != _editingItem) return;
        AttachEditBox(box);
    }

    /// <summary>从行容器里取出本行自己的行内编辑框（只认最近的 TreeViewItem 就是本行的那个，
    /// 避免误抓到已展开子行的编辑框）。</summary>
    private static TextBox? FindEditBox(TreeViewItem item) => FindEditBoxIn(item, item);

    private static TextBox? FindEditBoxIn(DependencyObject root, TreeViewItem owner)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox box && NearestItem(box) == owner) return box;
            if (FindEditBoxIn(child, owner) is { } nested) return nested;
        }
        return null;
    }

    private static TreeViewItem? NearestItem(DependencyObject node)
    {
        var current = node;
        while (current is not null)
        {
            if (current is TreeViewItem item) return item;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>把焦点放进刚出现的编辑框并全选原值。可能在布局之前被调用，
    /// 所以再排一拍重试（否则用户双击后要自己再点一下才能打字）。</summary>
    private void FocusEditBox(TextBox box)
    {
        if (box.Focus()) box.SelectAll();
        else Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!ReferenceEquals(_editingBox, box)) return; // 编辑已经结束/换行
            if (box.Focus()) box.SelectAll();
        }));
    }

    /// <summary>
    /// 就地编辑期间的按键（Enter 提交 / Esc 取消）。
    ///
    /// <para><b>为什么在控件这一层用 Preview（而不是把 KeyDown 挂在编辑框上）</b>：
    /// Preview 从根往下隧道，<b>谁拿到焦点都跑得到</b>——只要还在编辑一行。
    /// 之前挂在 TextBox 实例上的写法有一个致命前提：「事件一定挂到了屏幕上那个实例」，
    /// 一旦挂接时机没赶上（模板刚生成 / 实例被重建），用户敲 Enter 就石沉大海，
    /// 而界面上看起来一切正常（这正是用户反馈的「Enter 不提交」）。挂在这一层，
    /// 接线不再依赖任何时序假设；同时因为 Preview 先于 TextBox 自己的 KeyDown，
    /// 也不会出现「提交两次」。</para></summary>
    private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // ① 编辑中：Enter 提交 / Esc 取消（此时 Delete/Back 是删字符，交给编辑框）
        if (_editingRow is not null && !_editClosing)
        {
            if (e.Key == Key.Escape)
            {
                CancelEditing();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                CommitNow();
                e.Handled = true;
                return;
            }
            return;
        }

        // ② 非编辑态：Delete / Back 删除选中键
        if (e.Key is not (Key.Delete or Key.Back)) return;
        if (_editingBox is not null) return;                 // 编辑态里 Delete 是删字符
        if (e.OriginalSource is TextBox) return;
        if (_selected is null) return;
        DeleteRow(_selected);
        e.Handled = true;
    }

    /// <summary>
    /// 键盘焦点离开编辑框 = 提交（用户口径：「不在叶子节点的输入框中」就算失焦）。
    ///
    /// <para>判定的是<b>去向</b>，不是「编辑框自己丢了焦点」：点上下文菜单、点窗口外、
    /// 切页签都会让编辑框丢焦点，但只有「焦点落到别处」才算用户走开了。</para></summary>
    private void Editor_PreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_editClosing || _editingRow is null) return;
        // 只在「离开的那个」就是本行编辑框时判定：其它行（或别的控件）的焦点变化与我们无关。
        if (_editingBox is not null && !ReferenceEquals(e.OldFocus, _editingBox)) return;
        if (e.NewFocus is null) return;                     // 无新焦点：窗口失活等，先不动
        if (IsInsideEditSession(e.NewFocus)) return;        // 仍在编辑框/菜单里：不算失焦
        DispatchCommit();
    }

    /// <summary>点击树里的别处（不是本行编辑框）= 提交。鼠标按下比焦点变化更早，
    /// 这条兜底让「点一下别处在编辑框失焦判定之外」的情况也不会漏提交。</summary>
    private void Editor_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editClosing || _editingRow is null || _editingBox is null) return;
        if (e.OriginalSource is DependencyObject source && IsDescendantOf(source, _editingBox)) return;
        DispatchCommit();
    }

    /// <summary>延迟一拍提交：焦点/鼠标事件处理过程中重建树（提交会重建）会让 WPF 的
    /// 输入状态错乱，因此统一排到输入优先级之后再做。</summary>
    private void DispatchCommit()
    {
        if (_commitPending) return;
        _commitPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            _commitPending = false;
            CommitEditing();
        }));
    }

    /// <summary>同步提交（Enter 走这条：按键事件里没有「等一拍」的必要）。</summary>
    private void CommitNow()
    {
        _commitPending = false;
        CommitEditing();
    }

    /// <summary>新的焦点位置是否还属于「本次编辑会话」：编辑框本身、编辑框内部的部件，
    /// 或本次编辑行的右键菜单（菜单弹出期间先提交会把菜单项的目标行重建掉）。</summary>
    private bool IsInsideEditSession(IInputElement? newFocus)
    {
        if (newFocus is not DependencyObject target) return false;
        if (_editingBox is not null && IsDescendantOf(target, _editingBox)) return true;
        // 上下文菜单：MenuItem 是 ContextMenu 的子孙（Popup 里的另一棵可视化树），
        // 因此「锚点是不是本次编辑行」要单独判一次。
        if (_editingItem?.ContextMenu is not { } menu) return false;
        for (var current = target; current is not null;
             current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, menu)) return true;
        }
        return false;
    }

    private static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor)
    {
        if (ReferenceEquals(node, ancestor)) return true;
        var current = node;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    /// <summary>提交：按原值类型写回（校验失败给中文原因、文档不变、恢复原值）。
    ///
    /// <para>文本从**屏幕上那个编辑框 / 视图模型**取（见 <see cref="CurrentEditText"/>），
    /// 因此不依赖任何事件是否挂到了正确的实例上——这正是「Enter 不提交」的历史根因所在。</para></summary>
    private void CommitEditing()
    {
        if (_editClosing || _editingRow is null || _document is null) return;
        var row = _editingRow;
        var text = CurrentEditText();
        if (text is null) return;
        var path = row.Path;
        try
        {
            var json = JsonDocumentEditor.SetLeafText(row, text, _document);
            _lastCommitTicks = Environment.TickCount64;
            EndEditing(restoreDisplay: false);
            AdoptText(json, isModified: true, focusPath: path);
            SetStatus($"已修改键 {path}（未写盘；宿主导出时才落地）。");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            SetStatus($"修改失败（{path}）：{ex.Message} 原值已恢复。");
            EndEditing();   // 校验失败：行头切回展示态并放回原值
        }
    }

    /// <summary>取消：还原原始文本（<c>Esc</c>）。</summary>
    private void CancelEditing()
    {
        if (_editClosing) return;
        if (_editingRow is null) return;
        var path = _editingRow.Path;
        EndEditing(restoreDisplay: true);
        SetStatus($"已取消编辑 {path}（未改动文档）。");
    }

    /// <summary>结束行内编辑态：把行头切回展示态；<paramref name="restoreDisplay"/> 为 true 时
    /// 再把原始文本放回视图模型（取消路径）。</summary>
    private void EndEditing(bool restoreDisplay = true)
    {
        if (_editingItem is null || _editingRow is null)
        {
            _editingItem = null;
            _editingBox = null;
            _editingRow = null;
            _editClosing = false;
            return;
        }
        _editClosing = true;
        try
        {
            ResetRowView(_editingItem, _editingRow, restoreDisplay ? _editingOriginal : null);
        }
        finally
        {
            _editingItem = null;
            _editingBox = null;
            _editingRow = null;
            _editingOriginal = string.Empty;
            _commitPending = false;
            // 复位必须放在最后：_editClosing 留在 true 会让**下一次**编辑的所有
            // 焦点/按键判定直接被挡掉（表现为「只能编辑一次」）。
            _editClosing = false;
        }
    }

    /// <summary>
    /// 把某一行的行头切回展示态。
    ///
    /// <para><b>为什么优先改「屏幕上那个视图对象」</b>：行头模板是绑在视图模型上的，
    /// 若编辑期间树被重建（<c>_rowViews</c> 已清空），直接 <c>ViewFor(row)</c> 会**新建**一个
    /// 视图对象，而屏幕上旧视图对象的 <c>IsEditing</c> 仍是 true——编辑框不消失、
    /// 后续按键继续往已被替换的实例上提交。先改容器上那个实例，再兜底查缓存。</para>
    /// </summary>
    private void ResetRowView(TreeViewItem? item, JsonEditRow row, string? restoreText)
    {
        var view = item?.Header as JsonTreeRowView ?? ViewFor(row);
        view.IsEditing = false;
        if (restoreText is not null) view.EditText = restoreText;
    }

    // ── 删除键（右键菜单 / Delete 键）────────────────────────────────

    private void AttachRowContextMenu(TreeViewItem item, JsonEditRow row)
    {
        if (row.Parent is null) return; // 根节点不能删
        var menu = new ContextMenu();
        var delete = new MenuItem { Header = "删除此键…" };
        delete.Click += (_, _) =>
        {
            item.IsSelected = true;
            DeleteRow(row);
        };
        menu.Items.Add(delete);
        item.ContextMenu = menu;
    }

    private void DeleteRow(JsonEditRow row)    {
        if (_document is null || row.Parent is null) return;
        var path = row.Path;
        var confirm = MessageBox.Show(Window.GetWindow(this),
            $"删除键 {path}？\n\n删除会以 RFC6902 remove 操作进入补丁（游戏加载器按补丁回放）。",
            "删除键", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        EndEditing();
        if (!JsonDocumentEditor.RemoveNode(row, out var error))
        {
            SetStatus($"删除失败：{error}");
            return;
        }
        var parentPath = ParentPathOf(path);
        AdoptText(JsonDocumentEditor.Serialize(_document), isModified: true, focusPath: parentPath);
        SetStatus($"已删除键 {path}（未写盘；宿主导出时才落地）。");
    }

    /// <summary>删除后要重新定位到的父路径（把最后一段去掉）。</summary>
    private static string ParentPathOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    // ── 原文 JSON ───────────────────────────────────────────────────

    private void SaveRaw_Click(object sender, RoutedEventArgs e)
    {
        if (!JsonDocumentEditor.TryParse(RawBox.Text, out var document, out var error))
        {
            SetStatus($"原始 JSON 非法，已拒绝（{error}）；当前文档与宿主编辑集都未改动。");
            return;
        }
        // 与旧实现一致：进宿主的文本是用户输入的那份原文（不重新排版）。
        var text = RawBox.Text;
        _document = document;
        CurrentJsonText = text;
        _modified = true;
        RebuildTree();
        UpdateDiffSummary();
        SetStatus("原始 JSON 修改已进入宿主编辑集。");
        RaiseDocumentChanged();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 切到「原文 JSON」时把当前文本同步过去（树侧改动立刻可见）；切回树视图不覆盖用户输入。
        if (e.Source is TabControl && Tabs.SelectedIndex == 1 && _document is not null && RawBox.Text != CurrentJsonText)
            RawBox.Text = CurrentJsonText;
    }

    // ── 内部状态同步 ─────────────────────────────────────────────────

    /// <summary>用新的 JSON 文本替换当前文档（改动来自树侧），重建树并把选中定位回
    /// <paramref name="focusPath"/>（逐级展开）。</summary>
    private void AdoptText(string jsonText, bool isModified, string focusPath)
    {
        var document = JsonDocumentEditor.ParseStrict(jsonText);
        _document = document;
        CurrentJsonText = jsonText;
        _modified = isModified;
        RawBox.Text = jsonText;
        RebuildTree();
        UpdateDiffSummary();
        if (focusPath.Length > 0) SelectPath(focusPath);
        else if (Tree.Items.Count > 0 && Tree.Items[0] is TreeViewItem root) root.IsSelected = true;
        RaiseDocumentChanged();
    }

    private void UpdateDiffSummary(bool force = false)
    {
        var baseline = _vanillaText ?? _loadedText;
        if (string.IsNullOrWhiteSpace(baseline) || string.IsNullOrWhiteSpace(CurrentJsonText))
        {
            DiffOperationCount = 0;
            DiffInfo.Text = "—";
            return;
        }
        // 超大文档不在每次编辑时全量算差异（TextDiffService 会走完整棵树，MB 级表会明显掉帧）：
        // 只有小文档自动算，大文档由宿主/页面按需调用 RefreshDiffSummary(force: true)。
        if (!force && (long)baseline.Length + CurrentJsonText.Length > DiffAutoComputeMaxChars)
        {
            DiffOperationCount = 0;
            DiffInfo.Text = $"（文档较大：差异摘要按需计算，可点「计算差异」或在本页 diff 视图查看）";
            return;
        }
        try
        {
            DiffOperationCount = JsonDocumentEditor.CountDiffOperations(baseline, CurrentJsonText);
        }
        catch (InvalidDataException)
        {
            DiffOperationCount = 0;
            DiffInfo.Text = "（差异无法计算：JSON 非法）";
            return;
        }
        DiffInfo.Text = _vanillaText is null
            ? DiffOperationCount == 0 ? "与载入时无差异。" : $"本次修改：{DiffOperationCount} 个 RFC6902 操作。"
            : JsonDocumentEditor.DescribeDiff(baseline, CurrentJsonText);
    }

    /// <summary>按需（重新）计算差异摘要：超大文档由页面/宿主显式触发（plan-12 的 diff Tab）。</summary>
    public void RefreshDiffSummary(bool force = true) => UpdateDiffSummary(force);

    private void RefreshButtons()
    {
        ExpandAllButton.IsEnabled = _document is not null;
        CollapseButton.IsEnabled = _document is not null;
        SaveRawButton.IsEnabled = _document is not null;
    }

    private void SetStatus(string message)
    {
        StatusInfo.Text = string.IsNullOrWhiteSpace(message) ? "—" : message;
        StatusMessage?.Invoke(this, StatusInfo.Text);
    }

    private void RaiseDocumentChanged()
        => DocumentChanged?.Invoke(this, new JsonDocumentChangedEventArgs(CurrentJsonText, _modified, DiffOperationCount));
}
