using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
        PreviewColumn.Width = new GridLength(UiStateService.ClampPreviewWidth(width));
        if (persist) SavePreviewWidth();
    }

    /// <summary>复位到默认列宽并持久化（splitter 双击）。</summary>
    public void ResetPreviewColumnWidth()
    {
        PreviewColumn.Width = new GridLength(UiStateService.DefaultPreviewColumnWidth);
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

    /// <summary>工作台列表（统一底色/边框/文字色，单选）。</summary>
    public static ListView CreateList() => new() { Style = TryFindStyle("WorkbenchList") };

    /// <summary>列表行容器样式（<c>State=Modified</c> 时金色，用于 ItemContainerStyle）。</summary>
    public static Style? CreateListItemStyle() => TryFindStyle("WorkbenchListItem");

    /// <summary>工作台树（开启虚拟化）。</summary>
    public static TreeView CreateTree() => new() { Style = TryFindStyle("WorkbenchTree") };

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
    public static TextBox CreateSearchBox(string toolTip)
        => new() { Style = TryFindStyle("WorkbenchSearchBox"), ToolTip = toolTip };

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

    // ── 列宽持久化 ───────────────────────────────────────────────────

    private void PreviewSplitter_DragCompleted(object sender, MouseButtonEventArgs e) => SavePreviewWidth();

    private void PreviewSplitter_DoubleClick(object sender, MouseButtonEventArgs e) => ResetPreviewColumnWidth();

    private void SavePreviewWidth()
    {
        if (_uiState is null) return;
        _uiState.SetPreviewWidth(PageKey, PreviewColumnWidth);
        UiStateService.Save(_uiState, _uiStateFile);
    }
}
