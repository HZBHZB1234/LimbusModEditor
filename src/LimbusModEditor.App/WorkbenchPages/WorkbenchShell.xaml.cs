using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LimbusModEditor.Application.AppConfig;

namespace LimbusModEditor.App;

/// <summary>浏览列呈现形态（列表 / 树）。</summary>
public enum WorkbenchViewMode
{
    /// <summary>扁平列表。</summary>
    List,
    /// <summary>树形浏览（默认；与资源工作台一致）。</summary>
    Tree,
}

/// <summary>页面拿到的视图切换按钮对（页面可自行改标题/初始状态）。</summary>
public sealed record WorkbenchViewToggles(ToggleButton List, ToggleButton Tree);

/// <summary>
/// plan-09：工作台公共骨架（三列：浏览列 / 6px GridSplitter / 预览编辑列）。
///
/// <para>把资源工作台（<c>AssetsWorkbenchPage.xaml</c>）那套设计语言抽成可复用件：
/// 三列布局、与资源页同款的 splitter（1px 竖线、hover 主色、双击复位、拖动结束
/// 持久化）、搜索行/筛选行/视图切换对/状态条/空态提示的统一构件。
/// 后续 plan-10/11/12 的页面只负责「往里塞内容」，不再各自重造骨架与色值。</para>
///
/// <para>列宽按 <c>pageKey</c>（<see cref="WorkbenchPageKeys"/>）持久化到
/// <c>&lt;程序目录&gt;/config/ui-state.json</c>；四个工作台互不影响。</para>
///
/// <para><b>本控件不解析任何数据、不写任何缓存</b>：它只做布局与状态文案，
/// 数据读写仍由各页面自己的服务负责。</para>
/// </summary>
public partial class WorkbenchShell : UserControl
{
    private IWorkbenchHost? _host;
    private UiStateService? _uiState;
    private string _uiStateFile = string.Empty;
    private WorkbenchViewToggles? _toggles;
    private bool _treeMode = true;

    /// <summary>设计器 / 自检用的空构造：不绑定宿主，列宽用默认值且不持久化。</summary>
    public WorkbenchShell()
    {
        InitializeComponent();
    }

    /// <summary>页面标准构造：绑定宿主与自己页面 key，并恢复上次的列宽。</summary>
    public WorkbenchShell(IWorkbenchHost host, string pageKey) : this()
    {
        Attach(host, pageKey);
    }

    /// <summary>绑定宿主与页面 key（可在构造后调用）。<paramref name="host"/> 为 null 时
    /// 列宽不持久化（自检/设计器场景）。</summary>
    public void Attach(IWorkbenchHost? host, string? pageKey)
    {
        _host = host;
        PageKey = string.IsNullOrWhiteSpace(pageKey) ? WorkbenchPageKeys.Assets : pageKey.Trim();
        if (host is null)
        {
            SetPreviewColumnWidth(UiStateService.DefaultPreviewColumnWidth);
            return;
        }

        _uiStateFile = Path.Combine(host.Env.ConfigDirectory, "ui-state.json");
        _uiState = UiStateService.Load(_uiStateFile);
        SetPreviewColumnWidth(_uiState.GetPreviewWidth(PageKey));
        RegisterLive();
    }

    /// <summary>本页面 key（<see cref="WorkbenchPageKeys"/>），列宽持久化以它为键。</summary>
    public string PageKey { get; private set; } = WorkbenchPageKeys.Assets;

    /// <summary>绑定的宿主（页面间跳转/项目状态查询用）。</summary>
    public IWorkbenchHost? Host => _host;

    /// <summary>当前浏览列形态（默认树视图）。</summary>
    public WorkbenchViewMode ViewMode => _treeMode ? WorkbenchViewMode.Tree : WorkbenchViewMode.List;

    /// <summary>是否树视图（默认 true）。</summary>
    public bool IsTreeMode => _treeMode;

    /// <summary>视图切换时触发（列表 / 树）。</summary>
    public event EventHandler<WorkbenchViewMode>? ViewModeChanged;

    /// <summary>浏览列内容宿主（页面塞列表或树）。</summary>
    public ContentControl Browse => BrowseHost;

    /// <summary>预览/编辑列内容宿主。</summary>
    public ContentControl Edit => EditHost;

    /// <summary>当前预览列宽（非绝对值时回默认值）。</summary>
    public double PreviewColumnWidth => PreviewColumn.Width.IsAbsolute
        ? PreviewColumn.Width.Value
        : UiStateService.DefaultPreviewColumnWidth;

    /// <summary>设置预览列宽（钳制到 [260, 2000]）；<paramref name="persist"/> 为 true 时落盘。</summary>
    public void SetPreviewColumnWidth(double width, bool persist = false)
    {
        var clamped = UiStateService.ClampPreviewWidth(width);
        PreviewColumn.Width = new GridLength(clamped);
        _uiState?.SetPreviewWidth(PageKey, clamped);
        if (persist) SavePreviewWidth();
    }

    /// <summary>复位到默认列宽并持久化（splitter 双击）。</summary>
    public void ResetPreviewColumnWidth()
    {
        PreviewColumn.Width = new GridLength(UiStateService.DefaultPreviewColumnWidth);
        _uiState?.SetPreviewWidth(PageKey, UiStateService.DefaultPreviewColumnWidth);
        SavePreviewWidth();
    }

    /// <summary>关窗时持久化列宽（宿主 MainWindow 调用，与资源页 <c>PersistUiState</c> 同口径）。</summary>
    public void PersistPreviewWidth() => SavePreviewWidth();

    // ── 内容接线 ─────────────────────────────────────────────────────

    /// <summary>放入浏览列内容（列表 / 树；切换视图时页面自行替换）。</summary>
    public void SetBrowseContent(object? content) => BrowseHost.Content = content;

    /// <summary>放入预览/编辑列内容。</summary>
    public void SetEditContent(object? content) => EditHost.Content = content;

    /// <summary>往搜索行加一个控件（搜索框、重新加载按钮…）。加了第一个才显示该行。</summary>
    public void AddSearchItem(UIElement element)
    {
        SearchRowHost.Children.Add(element);
        SearchRowHost.Visibility = Visibility.Visible;
    }

    /// <summary>往筛选行加一个控件（下拉筛选、复选框、说明文字…）。加了第一个才显示该行。</summary>
    public void AddFilterItem(UIElement element)
    {
        FilterRowHost.Children.Add(element);
        FilterRowHost.Visibility = Visibility.Visible;
    }

    /// <summary>往视图切换行加一个控件（一般用 <see cref="AddViewToggles"/>）。</summary>
    public void AddViewToggleItem(UIElement element)
    {
        ViewToggleRowHost.Children.Add(element);
        ViewToggleRowHost.Visibility = Visibility.Visible;
    }

    /// <summary>追加「☰ 列表 / 🗂 树」切换按钮对（互斥、默认树视图），并返回按钮以便页面改标题。
    /// 切换时触发 <see cref="ViewModeChanged"/>。</summary>
    public WorkbenchViewToggles AddViewToggles(
        string listText = "☰ 列表", string treeText = "🗂 树", bool treeDefault = true)
    {
        var list = new ToggleButton
        {
            Content = listText,
            Style = TryFindStyle("WorkbenchViewToggle"),
            IsChecked = !treeDefault,
            ToolTip = "扁平列表视图",
        };
        var tree = new ToggleButton
        {
            Content = treeText,
            Style = TryFindStyle("WorkbenchViewToggle"),
            IsChecked = treeDefault,
            ToolTip = "树形浏览（逐层展开，大表不冻结）",
        };
        list.Click += (_, _) => ApplyViewMode(false);
        tree.Click += (_, _) => ApplyViewMode(true);
        _toggles = new WorkbenchViewToggles(list, tree);
        _treeMode = treeDefault;
        AddViewToggleItem(list);
        AddViewToggleItem(tree);
        return _toggles;
    }

    /// <summary>以代码方式切换视图（互斥状态同步更新并触发事件）。</summary>
    public void SetViewMode(WorkbenchViewMode mode) => ApplyViewMode(mode == WorkbenchViewMode.Tree);

    private void ApplyViewMode(bool tree)
    {
        var changed = _treeMode != tree;
        _treeMode = tree;
        if (_toggles is not null)
        {
            _toggles.List.IsChecked = !tree;
            _toggles.Tree.IsChecked = tree;
        }
        if (changed) ViewModeChanged?.Invoke(this, tree ? WorkbenchViewMode.Tree : WorkbenchViewMode.List);
    }

    /// <summary>写状态条文案（编辑列底部；传 null 显示占位「—」）。</summary>
    public void SetStatus(string? text) => StatusText.Text = string.IsNullOrWhiteSpace(text) ? "—" : text;

    /// <summary>设置浏览列空态提示（null/空白 = 隐藏）。</summary>
    public void SetEmptyHint(string? text)
    {
        var visible = !string.IsNullOrWhiteSpace(text);
        EmptyHint.Text = visible ? text! : string.Empty;
        EmptyHint.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>宿主（键盘快捷键等）用：当前状态条文案。</summary>
    public string Status => StatusText.Text;

    // ── 共用构件工厂（页面代码不再出现色值/字号字面量）────────────────

    /// <summary>工作台列表（统一底色/边框/文字色，单选）。滚轮悬停任一行都能滚动（见
    /// <see cref="EnableWheelScrolling"/>）。</summary>
    public static ListView CreateList()
    {
        var list = new ListView { Style = TryFindStyle("WorkbenchList") };
        EnableWheelScrolling(list);
        return list;
    }

    /// <summary>列表行容器样式（<c>State=Modified</c> 时金色，用于 ItemContainerStyle）。</summary>
    public static Style? CreateListItemStyle() => TryFindStyle("WorkbenchListItem");

    /// <summary>工作台树（开启虚拟化）。滚轮悬停任一节点都能滚动（见
    /// <see cref="EnableWheelScrolling"/>；XAML 里自建的树需自行调用）。</summary>
    public static TreeView CreateTree()
    {
        var tree = new TreeView { Style = TryFindStyle("WorkbenchTree") };
        EnableWheelScrolling(tree);
        return tree;
    }

    /// <summary>树节点样式（手工创建 TreeViewItem 时逐项赋值；ItemContainerStyle 对
    /// 直接加入 Items 的容器不生效）。</summary>
    public static Style? CreateTreeItemStyle() => TryFindStyle("WorkbenchTreeItem");

    /// <summary>工具按钮（统一内边距）。</summary>
    public static Button CreateButton(string text, RoutedEventHandler? handler = null, bool isEnabled = true)
    {
        var button = new Button
        {
            Content = text,
            Style = TryFindStyle("WorkbenchToolButton"),
            IsEnabled = isEnabled,
        };
        if (handler is not null) button.Click += handler;
        return button;
    }

    /// <summary>搜索框（统一尺寸；ToolTip 说明搜索范围）。</summary>
    public static TextBox CreateSearchBox(string? toolTip = null)
        => new() { Style = TryFindStyle("WorkbenchSearchBox"), ToolTip = toolTip };

    /// <summary>
    /// 筛选/切换下拉（统一 <c>WorkbenchFilterCombo</c> 样式 + 外边距 + 选中首项）。
    ///
    /// <para><b>宽度为什么要在代码里估算</b>：Fluent 模板的 ComboBox 有固定 MinHeight(32)，
    /// 页面再写死一个偏小的 Width（110/120 这类）时，最长候选项加上左右内边距和右侧箭头就放不下，
    /// 选中文字被箭头那一侧裁掉（中文尤其明显）。与其让每个页面各写一个魔数，不如按候选项
    /// 直接算一个够用的宽度：全角/CJK 字 ≈13px、半角 ≈7px（默认字号下的经验值），
    /// 再加约 46px 给左内边距 + 右侧箭头，向上取整到 10 的倍数，下限 140。</para>
    /// </summary>
    public static ComboBox CreateFilterCombo(string toolTip, params string[] items)
    {
        var combo = new ComboBox
        {
            Style = TryFindStyle("WorkbenchFilterCombo"),
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = toolTip,
            Width = EstimateComboWidth(items),
        };
        if (items.Length > 0)
        {
            foreach (var item in items) combo.Items.Add(item);
            combo.SelectedIndex = 0;
        }
        return combo;
    }

    /// <summary>下拉最宽需要的宽度（经验估算，宁可宽一点也不让中文被箭头裁掉）。</summary>
    private static double EstimateComboWidth(string[] items)
    {
        const double cjkWidth = 13;      // 全角 / CJK 字经验宽度
        const double halfWidth = 7;      // 半角字经验宽度
        const double chromeWidth = 46;   // 左内边距 + 右侧箭头预留
        const double minWidth = 140;

        double widest = 0;
        foreach (var item in items)
        {
            double textWidth = 0;
            foreach (var ch in item ?? string.Empty)
                textWidth += ch >= 0x1100 ? cjkWidth : halfWidth;
            if (textWidth > widest) widest = textWidth;
        }
        return Math.Max(minWidth, Math.Ceiling((widest + chromeWidth) / 10) * 10);
    }

    /// <summary>小节标签（统一次要文字色）。</summary>
    public static TextBlock CreateSectionLabel(string text, Thickness? margin = null)
    {
        var label = new TextBlock { Text = text, Style = TryFindStyle("WorkbenchSectionLabel") };
        if (margin is { } value) label.Margin = value;
        return label;
    }

    /// <summary>面板标题（15px 半粗）。</summary>
    public static TextBlock CreatePanelTitle(string text) => new() { Text = text, Style = TryFindStyle("WorkbenchPanelTitle") };

    /// <summary>多行代码框（原始 JSON 等；统一等宽字体与深色底）。</summary>
    public static TextBox CreateCodeBox(string? toolTip = null)
        => new() { Style = TryFindStyle("WorkbenchCodeBox"), ToolTip = toolTip };

    /// <summary>值编辑框（多行换行）。</summary>
    public static TextBox CreateValueBox(string? toolTip = null)
        => new() { Style = TryFindStyle("WorkbenchValueBox"), ToolTip = toolTip };

    /// <summary>从应用资源取具名样式（资源缺失时返回 null，控件退回默认外观，不抛）。
    /// 注意必须写全 <c>System.Windows.Application</c>：裸 <c>Application</c> 会被解析成
    /// 命名空间 <c>LimbusModEditor.Application</c>。</summary>
    private static Style? TryFindStyle(string key)
        => System.Windows.Application.Current?.TryFindResource(key) as Style;

    // ── 滚轮滚动（通用）──────────────────────────────────────────────

    /// <summary>每个已接线元素的内部滚动条缓存（树/列表重建后按「不在可视树里」判定失效）。</summary>
    private static readonly ConditionalWeakTable<UIElement, ScrollViewer> WheelScrollViewers = new();

    /// <summary>
    /// 已注册过滚轮处理的元素（<see cref="EnableWheelScrolling"/> 幂等用）。
    /// 用 <see cref="ConditionalWeakTable{TKey,TValue}"/> 而非 <c>HashSet</c>：后者是**强引用**，
    /// 会把每个建过的树/列表（含已从界面摘除的）永久钉住，等于泄漏；弱表随控件回收自动清条目。
    /// </summary>
    private static readonly ConditionalWeakTable<UIElement, object> WheelWiredElements = new();

    /// <summary><see cref="WheelWiredElements"/> 的占位值（该表只当集合用）。</summary>
    private static readonly object WheelWiredMarker = new();

    /// <summary>已挂过「记录派发起始偏移」预览处理器的滚动条（避免缓存失效后重复挂）。
    /// 值类型用 object：ConditionalWeakTable 的值必须是引用类型。</summary>
    private static readonly ConditionalWeakTable<ScrollViewer, object> WheelPreviewWired = new();

    /// <summary><see cref="WheelPreviewWired"/> 的占位值（该表只当集合用）。</summary>
    private static readonly object WheelPreviewMarker = new();

    /// <summary>
    /// 本次滚轮派发开始时，目标滚动条的偏移（用于区分「滚动条已经滚过」与「内层吃掉了事件但没滚」）。
    /// <see cref="WeakReference"/> 是为了不在静态字段里死引用控件；每次滚轮都会覆盖。
    /// </summary>
    [ThreadStatic]
    private static (WeakReference Viewer, double Offset)? _wheelViewerBaseline;

    /// <summary>一个滚轮刻度滚动的行数（配合 <c>CanContentScroll=True</c> 的逻辑滚动，约 3 行/格）。</summary>
    private const int WheelLinesPerNotch = 3;
    /// <summary>
    /// 让 <paramref name="target"/> 的滚轮滚动「无论指针悬停在哪个后代上」都生效。
    ///
    /// <para><b>踩过的坑</b>：TreeView / ListView 的滚动条在模板内部的 <c>ScrollViewer</c> 里，
    /// 而滚轮事件是从指针下的<b>最深元素</b>开始冒泡的。悬停到 TreeViewItem / TextBox / 提示弹层
    /// 这些后代上时，事件先被它们（或模板里的 ScrollViewer）处理掉甚至标记 <c>Handled</c>，
    /// 于是就出现「只有悬停在空白处才滚得动」。</para>
    ///
    /// <para><b>做法</b>：在 <paramref name="target"/> 上用
    /// <c>AddHandler(MouseWheelEvent, ..., handledEventsToo: true)</c> 收已处理过的冒泡滚轮事件，
    /// 拿到后自己找到内部 <c>ScrollViewer</c> 显式滚动并置 <c>Handled</c>，不再向外冒泡
    /// （否则会连外层容器一起滚）。内部 ScrollViewer 惰性解析 + 缓存（模板在 <c>Loaded</c> 之后
    /// 才生成，树重建时旧引用会失效——所以每次都会校验缓存是否仍是本元素的可视祖先，不是就重新解析）。</para>
    ///
    /// <para><b>为什么只挂冒泡相位</b>：<c>ScrollViewer</c> 自己的滚轮滚动是<b>冒泡</b>类处理器。
    /// 若同时挂 <c>PreviewMouseWheel</c> 与冒泡，兜底分支重新派发的合成事件会再走一遍类处理器，
    /// 于是在「内层没余量、外层还能滚」的嵌套场景里，同一次滚轮会被内外滚两遍。
    /// 只挂冒泡相位时，"事件是否已被标记 Handled" 不影响分支判定（两边都收到），语义唯一。</para>
    ///
    /// <para>对同一元素重复调用是安全的；此刻还没有 ScrollViewer（尚未 <c>Loaded</c>）也不抛，
    /// 会在首次滚轮或 <c>Loaded</c> 时再解析。</para>
    /// </summary>
    public static void EnableWheelScrolling(UIElement target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // 幂等：本方法可能被反复调用（工厂 + 页面构造 + 重复接线），不能重复挂处理器。
        if (!WheelWiredElements.TryAdd(target, WheelWiredMarker)) return;

        target.AddHandler(UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnWheelScroll), true);
        if (target is FrameworkElement element)
            element.Loaded += (_, _) => ResolveScrollViewer(element);
    }

    private static void OnWheelScroll(object sender, MouseWheelEventArgs e)
    {
        if (sender is not UIElement target || e.Delta == 0) return;

        // 本处理器自己派发出去的合成事件必须直接放行，否则会与下面的兜底分支无限递归——
        // 「没余量 → 合成 → 又进本处理器 → 再合成」会以 StackOverflowException 崩掉进程
        // （该异常在 .NET 里不可捕获，实测在内容不满一屏的树上必现）。
        // 判据：合成事件就是在本元素上 RaiseEvent 的，其 Source 必然等于本元素；
        // 而真实滚轮事件的 Source 永远是指针下**最深**的那个元素（行/单元格/文本），不会是树本身，
        // 所以这个判断对真实事件永远为假，不会误吞正常滚动。
        if (ReferenceEquals(e.Source, target)) return;

        // 两个前提检查，都必须"零副作用"：
        //  ① 事件在到达这里之前就已被处理（滚动条/内部控件已经滚过）→ 一次滚轮只能滚一次距离，
        //     这里直接放行，不再叠加；但仍然标记 Handled，防止继续冒泡把外层也一起滚了。
        //  ② 内部没有纵向滚动余量 → 不拦截，交给外层（合成事件让它照常冒泡）。
        var scrollViewer = ResolveScrollViewer(target);
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0)
        {
            // 兜底：把本元素当作事件源再派发一个滚轮事件并放行冒泡，外层 ScrollViewer / 宿主页面
            // 会照常处理它。本例**不要**标记 Handled——标记了等于在这里把它掐断。
            // 这次重派发不会再回到本处理器：合成事件的 Source 就是 target，被上面的闸门挡掉。
            // 注：WPF 只有 MouseWheel 这一对路由事件（Preview 与冒泡），并没有 MouseWheelUp/DownEvent。
            target.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = Mouse.MouseWheelEvent,
                Source = target,
            });
            return;
        }

        // 事件在到达这里之前就已被处理，说明内层控件（行/单元格）把滚轮吃掉了或已自行滚动。
        // 两种情形必须区分，否则要么修不好缺陷，要么一次滚轮滚两倍距离：
        //   ① 滚动条已经滚过（它会把事件标记 Handled，偏移已变）→ 不能重复滚；
        //   ② 内层把事件标记 Handled 但滚动条没滚 → 正是本缺陷，必须由我们补滚。
        // 判据：事件派发开始时记录的偏移（由滚动条的 PreviewMouseWheel 记录，见 EnableWheelScrollingForViewer）。
        if (e.Handled)
        {
            var alreadyScrolled = _wheelViewerBaseline is { } baseline &&
                                  ReferenceEquals(baseline.Viewer.Target, scrollViewer) &&
                                  Math.Abs(scrollViewer.VerticalOffset - baseline.Offset) > 0.5;
            if (alreadyScrolled)
            {
                e.Handled = true;   // 已滚过：到此为止，别再冒泡让外层也滚
                return;
            }
            // 没滚过 → 继续往下走，由我们显式滚动。
        }

        // 实测（虚拟化 TreeView，ScrollViewer.CanContentScroll=True）：
        //   VerticalOffset / ScrollableHeight 是**像素**（200 项 → 5143px，行高约 27px），
        //   而 LineUp()/LineDown() 走逻辑滚动单位，一次约一行。
        // 所以既不能用 ScrollToVerticalOffset(±3)（3 像素根本看不出来，早期版本就是这么失效的），
        // 也不该用像素常量（DPI/字号/行高变化后就不准）。按刻度重复调用 Line* 才是唯一
        // 自适应且不抖动的做法：一格 3 行，符合 Windows 默认滚轮步进。
        var up = e.Delta > 0;
        for (var i = 0; i < WheelLinesPerNotch; i++)
        {
            if (up) scrollViewer.LineUp();
            else scrollViewer.LineDown();
        }
        e.Handled = true;
    }

    /// <summary>
    /// 解析（并缓存）<paramref name="target"/> 内部的滚动条。缓存命中条件是「它仍是本元素的
    /// 可视祖先」：树/列表重建或模板重生成后旧引用就不再是祖先，此时重新自顶向下查找。
    /// </summary>
    private static ScrollViewer? ResolveScrollViewer(UIElement target)
    {
        if (WheelScrollViewers.TryGetValue(target, out var cached))
        {
            if (cached.ScrollableHeight > 0 || IsVisualAncestorOf(cached, target)) return cached;
            WheelScrollViewers.Remove(target);
        }

        var found = FindScrollViewer(target);
        if (found is not null)
        {
            WheelScrollViewers.AddOrUpdate(target, found);
            // 预览相位先于所有内层控件执行，正好用来记下「派发开始时」的偏移：
            // 等到我们收到已 Handled 的冒泡事件时，就能判断这次滚轮到底滚没滚过。
            // 只在首次发现这个滚动条时挂一次（缓存失效可能重新解析到同一个实例）。
            if (WheelPreviewWired.TryAdd(found, WheelPreviewMarker))
            {
                found.PreviewMouseWheel += (sender, _) =>
                {
                    if (sender is ScrollViewer viewer)
                        _wheelViewerBaseline = (new WeakReference(viewer), viewer.VerticalOffset);
                };
            }
        }
        return found;
    }

    /// <summary>
    /// 自顶向下（宽度优先）找目标内部<b>能纵向滚动</b>的那个 ScrollViewer。
    /// 不直接返回遇到的第一个：DataGrid 之类的模板里除了纵向滚动条还嵌着一个横向/单元格用的
    /// ScrollViewer，取错对象会导致滚轮「接了线却没反应」。因此优先挑 <c>ScrollableHeight &gt; 0</c>
    /// 的，实在都没有（内容尚未铺满/尚未布局）再退回第一个。
    /// </summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        ScrollViewer? first = null;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(current, i);
                if (child is ScrollViewer viewer)
                {
                    if (viewer.ScrollableHeight > 0) return viewer;
                    first ??= viewer;
                }
                queue.Enqueue(child);
            }
        }
        return first;
    }

    /// <summary><paramref name="ancestor"/> 是否位于 <paramref name="descendant"/> 的可视祖先链上
    /// （弹层/弹出式 ToolTip 不在可视树里，因此不会被误判为有效缓存）。</summary>
    private static bool IsVisualAncestorOf(DependencyObject ancestor, DependencyObject descendant)
    {
        var current = VisualTreeHelper.GetParent(descendant);
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    // ── 列宽持久化 ───────────────────────────────────────────────────

    private void PreviewSplitter_DragCompleted(object sender, MouseButtonEventArgs e) => SavePreviewWidth();

    private void PreviewSplitter_DoubleClick(object sender, MouseButtonEventArgs e) => ResetPreviewColumnWidth();

    /// <summary>
    /// 落盘列宽。<b>先把磁盘上的最新状态读回来再合并自己那一项</b>，避免「后写的覆盖先写的」：
    /// 四个工作台各自持有启动时读到的那份「全页列宽」快照，若直接整份写回，
    /// 先改过宽度的页面会被后改的页面用旧值覆盖（plan-09 留下的隐患，plan-10 修复）。
    /// 这样即便某个页面没实现 <see cref="PersistPreviewWidth"/>，也不会丢别的页面的列宽。
    /// </summary>
    private void SavePreviewWidth()
    {
        if (_uiState is null) return;
        var latest = UiStateService.Load(_uiStateFile);
        latest.SetPreviewWidth(PageKey, PreviewColumnWidth);
        _uiState = latest;
        UiStateService.Save(_uiState, _uiStateFile);
    }

    /// <summary>
    /// 把当前存活的全部工作台实例的列宽一次性合并落盘（宿主关窗时调用）。
    /// 与 <see cref="PersistPreviewWidth"/> 的区别：后者写单项并读回磁盘合并，
    /// 本方法在内存里合并所有存活页再写一次，避免「最后一个写的赢」。
    /// </summary>
    public static void PersistAllPreviewWidths()
    {
        lock (LiveShells)
        {
            var byFile = new Dictionary<string, List<WorkbenchShell>>(StringComparer.OrdinalIgnoreCase);
            for (var i = LiveShells.Count - 1; i >= 0; i--)
            {
                if (LiveShells[i].Target is not WorkbenchShell shell || shell._uiState is null)
                {
                    LiveShells.RemoveAt(i);
                    continue;
                }
                if (!byFile.TryGetValue(shell._uiStateFile, out var list))
                {
                    list = [];
                    byFile[shell._uiStateFile] = list;
                }
                list.Add(shell);
            }
            foreach (var (file, shells) in byFile)
            {
                if (string.IsNullOrWhiteSpace(file)) continue;
                var state = UiStateService.Load(file);
                foreach (var shell in shells) state.SetPreviewWidth(shell.PageKey, shell.PreviewColumnWidth);
                UiStateService.Save(state, file);
                foreach (var shell in shells) shell._uiState = state;
            }
        }
    }

    /// <summary>当前存活的工作台骨架实例（<see cref="PersistAllPreviewWidths"/> 用）。
    /// 弱引用：页面常驻直到关窗，但骨架本身不该因为这张表而无法回收。</summary>
    private static readonly List<WeakReference> LiveShells = [];

    private void RegisterLive()
    {
        lock (LiveShells)
        {
            LiveShells.RemoveAll(x => !ReferenceEquals(x.Target, this) && x.Target is null);
            if (!LiveShells.Any(x => ReferenceEquals(x.Target, this)))
                LiveShells.Add(new WeakReference(this));
        }
    }
}
