using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.App;

/// <summary>浏览列的一个文件行（列表视图；<c>State=Modified</c> 触发金色高亮）。</summary>
/// <param name="RelativePath">相对 lang 根的内部路径（'/' 分隔；= 补丁键，含语言目录前缀）。</param>
/// <param name="DisplayPath">显示路径（去掉语言目录前缀，与树里的位置一致）。</param>
/// <param name="KeyCount">顶层键数。</param>
/// <param name="SizeBytes">文件大小。</param>
/// <param name="IsUtf8">是否合法 UTF-8（非 UTF-8 明确标记，不做编码猜测）。</param>
/// <param name="Modified"><b>文本是否真的被改过</b>（与官方原文不同）——不是「打开过」。</param>
public sealed record TextFileRow(string RelativePath, string DisplayPath, int KeyCount, long SizeBytes, bool IsUtf8, bool Modified)
{
    /// <summary>行状态绑定（<c>WorkbenchListItem</c> 样式的 DataTrigger 读它）。</summary>
    public string State => Modified ? "Modified" : "Unchanged";

    /// <summary>状态列文案（中文，不用枚举名）。</summary>
    public string StateLabel => Modified ? "已修改" : "原始";

    /// <summary>键数列文案（非 UTF-8 明确标注，不显示 0 键假装正常）。</summary>
    public string KeyCountLabel => IsUtf8 ? KeyCount.ToString() : "非 UTF-8";

    /// <summary>大小列文案。</summary>
    public string SizeLabel => FormatSize(SizeBytes);

    /// <summary>人类可读大小（与资源工作台 HumanSize 转换器同口径）。</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1024.0 / 1024.0:F2} MB",
    };
}

/// <summary>浏览列的一个搜索结果行（文件名 / 键 / 值三类命中）。</summary>
/// <param name="Hit">命中原样（内部键口径；点击后据此跳转并定位键）。</param>
/// <param name="DisplayPath">命中的显示路径（去掉语言目录前缀，只用于显示）。</param>
public sealed record TextHitRow(LangTextSearchHit Hit, string DisplayPath)
{
    /// <summary>命中类别标签。</summary>
    public string KindLabel => Hit.Kind switch
    {
        LangTextSearchKind.FileName => "文件名",
        LangTextSearchKind.Key => "键",
        _ => "值",
    };

    /// <summary>命中位置（文件名命中就是文件本身，其余是展平键路径）。</summary>
    public string Location => Hit.KeyPath ?? DisplayPath;

    /// <summary>命中片段（值命中是匹配窗口，其余与位置同）。</summary>
    public string Snippet => Hit.Snippet ?? DisplayPath;

    /// <summary>所属文件（多文件命中时区分用；显示口径）。</summary>
    public string RelativePath => DisplayPath;
}

/// <summary>
/// 文本工作台（plan-07 建立、plan-10 重做、plan-14 调整数据源口径）：浏览
/// <c>&lt;游戏&gt;/LimbusCompany_Data/lang/config.json</c> 指向的<b>活动语言目录内部</b>，
/// 树形逐层浏览 → 选中文件 → 键值编辑 → 导出与真实加载器（LCTA changes.py）语义一致的
/// RFC6902 lang 补丁。<b>默认不写游戏 lang 目录</b>——「直接应用」入口有显著警告。
///
/// <para><b>plan-10 改了什么</b>：页面改用公共骨架 <see cref="WorkbenchShell"/>
/// （与资源工作台同一套版面语言）；浏览列加 🗂 树（目录层级 → 文件，默认）与 ☰ 扁平列表；
/// 搜索结果成为<b>独立视图</b>（命中时才出现，不再挤常驻高度）；编辑列换成共享
/// <see cref="JsonTreeEditor"/>（按层惰性展开，<b>取消旧的 5000 行截断</b>）；
/// 进页面改走 <c>cache/text-index.db</c> 表缓存（二次进入不再全目录重读）。</para>
///
/// <para><b>plan-14 改了什么</b>：① 数据源显示口径从「lang 根」下移到「活动语言目录内部」
/// ——树顶层直接是 <c>AbDlg_Faust.json</c> / <c>StoryData</c>，不再有 <c>config.json</c>；
/// ② 修掉「点了文件预览一片空白」：索引命中路径也要把 lang 根交给服务
/// （<see cref="LangTextWorkbenchService.AttachLangRoot"/>），否则编辑集没有基线；
/// ③ 「导出 lang 补丁 / 直接应用到 lang 目录」两个按钮移出编辑列，改到侧边栏「② 产出模组」
/// 板块（本页仍持有编辑集，导出通道由 <see cref="ExportPatchInteractive"/> /
/// <see cref="ApplyToGameInteractive"/> 暴露给宿主）。</para>
///
/// <para><b>plan-16 改了什么</b>：① <b>条目口径去掉根文件夹</b>——树 / 列表 / 索引库 /
/// 编辑集 / 搜索命中的路径一律「相对活动语言目录」（<c>StoryData/S1.json</c>、
/// <c>AbDlg_Faust.json</c>），不再带 <c>LLc-CN-LCTA/</c> 那一层；加载器口径的补丁键
/// 由 <see cref="LangTextWorkbenchService.ToPatchKey"/> 在导出时补回；② 树顶层
/// <b>没有根节点</b>（文件夹在前 + 根级 json 平铺）；③ 导出与调试入口合并到侧边栏两个按钮，
/// 本页的导出/直接应用通道待 S6 一并删除。</para>
///
/// <para><b>缓存只是加速旁路</b>：删掉 <c>cache/text-index.db</c> 功能完全不受影响，
/// 只是每次进页面要重新读全部 lang 文件。</para>
/// </summary>
public sealed partial class TextWorkbenchPage : UserControl
{
    private readonly IWorkbenchHost _host;
    /// <summary>
    /// lang 服务实例：**宿主共享的那一份**（plan-16 S3）。
    /// 编辑集随之变成宿主可见的状态（导出 / 调试都从 <see cref="IWorkbenchHost.LangEdits"/> 读），
    /// 而枚举 / 搜索 / 差分仍由同一实例完成——不允许出现两份互不可见的服务状态。
    /// </summary>
    private readonly LangTextWorkbenchService _service;
    private readonly TextIndexStore _store;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _filterTimer;

    // 浏览列构件
    private readonly TextBox _search;
    private readonly Button _clearCache;
    private readonly ComboBox _stateFilter;
    private readonly TextBox _keyCountFilter;
    private readonly TextBlock _langInfo;
    private readonly ListView _fileList;
    private readonly TreeView _fileTree;
    private readonly ListView _hitList;
    private readonly TextBlock _hitHeader;
    private readonly StackPanel _hitPanel;

    // 编辑列构件
    private readonly StackPanel _editPanel;
    private readonly ScrollViewer _editHost;
    private readonly TextBlock _fileTitle;
    private readonly Border _modifiedBadge;
    private readonly Button _revertFile;
    private readonly TextBlock _fileDetail;
    private readonly JsonTreeEditor _editor;
    private readonly TextBlock _editSetText;

    // 状态
    private LangTextTreeNode? _treeRoot;
    private IReadOnlyList<LangTextFileInfo> _files = [];
    private List<TextFileRow> _rows = [];
    private LangTextFileInfo? _selectedFile;
    // 条目口径（plan-16 §5）：相对**活动语言目录**（如 StoryData/S1.json、AbDlg_Faust.json），
    // 不带语言目录那一层。界面、索引库、编辑集键、搜索结果全部用它；
    // 补丁键（加载器口径）由 service.ToPatchKey(...) 在导出时补回语言目录那层。
    private Dictionary<string, LangTextFileInfo> _byDisplay = new(StringComparer.OrdinalIgnoreCase);
    private int _searchGeneration;
    private int _loadGeneration;
    private bool _loadingDocument;
    private bool _loaded;
    private bool _suppressSelection;

    /// <summary>最近一次**成功载入编辑器**的条目路径（相对活动语言目录）。
    ///
    /// <para>用于抵抗「同一文件被重复触发载入」：文件树/列表在刷新（编辑集变化 →
    /// <c>RefreshEditSetState</c> → 重建树 / 重绑列表）时会各自抛出选中事件，
    /// 而 <see cref="SelectFileAsync"/> 一进去就 <c>_editor.Clear()</c>——重复调用会把
    /// 已经渲染好的键值树清空（现象：树区空白、状态条只剩 <c>—</c>）。
    /// 有了这个字段，重复触发退化成「重新定位到上次的键」，不再重建编辑器。</para></summary>
    private string? _lastLoadedPath;

    public TextWorkbenchPage(IWorkbenchHost host)
    {
        _host = host;
        InitializeComponent();
        // 用宿主共享的会话（编辑集因此对导出 / 调试可见）；页面的常驻性保证只创建一次。
        _service = host.LangEdits.Service;
        _host.LangEdits.Changed += (_, _) => Dispatcher.InvokeAsync(RefreshEditSetState);

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RunSearchAsync(); };
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); ApplyFileFilter(); };

        _store = new TextIndexStore(host.Env.CacheDirectory);

        // ── 编辑列（与浏览列一样在代码里构建：见 TextWorkbenchPage.xaml 的说明）──
        _editor = new JsonTreeEditor();
        _fileTitle = WorkbenchShell.CreatePanelTitle("未选择文件");
        _fileTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _fileTitle.TextWrapping = TextWrapping.NoWrap;
        _modifiedBadge = new Border
        {
            Background = System.Windows.Application.Current?.TryFindResource("WbModifiedBrush") as System.Windows.Media.Brush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock { Text = "已修改", FontSize = 11, FontWeight = FontWeights.SemiBold },
        };
        _revertFile = WorkbenchShell.CreateButton("还原此文件", RevertFile_Click, isEnabled: false);
        _revertFile.ToolTip = "把该文件移出编辑集（lang 目录从未被改动，无需写盘）";
        _fileDetail = WorkbenchShell.CreateSectionLabel("—");
        _fileDetail.TextTrimming = TextTrimming.CharacterEllipsis;
        _fileDetail.TextWrapping = TextWrapping.NoWrap;
        _fileDetail.Margin = new Thickness(0, 0, 0, 6);

        _editSetText = new TextBlock
        {
            Text = "编辑集：空",
            Style = System.Windows.Application.Current?.TryFindResource("WorkbenchStatusText") as Style,
        };
        // 这段说明曾经是一整段常驻文字，把键值树挤成三行（用户反馈「预览框太小」）。
        // 内容重要但不该常驻吃高度，收进 tooltip；常驻只留「编辑集：N 个文件已改」一行。
        _editSetText.ToolTip =
            "默认不写游戏 lang 目录：导出补丁 / 直接应用两个入口都在左侧「② 产出模组」板块；" +
            "「直接应用」才会写入游戏目录，且编辑器不负责备份/还原（真实加载器在启动/退出时做 .bak）。";
        _editSetText.TextWrapping = TextWrapping.Wrap;

        var titleRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_fileTitle, 0);
        Grid.SetColumn(_modifiedBadge, 1);
        Grid.SetColumn(_revertFile, 2);
        titleRow.Children.Add(_fileTitle);
        titleRow.Children.Add(_modifiedBadge);
        titleRow.Children.Add(_revertFile);

        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(_editSetText);

        // 编辑列：**照抄静态数据工作台的口径**（StaticWorkbenchPage：`new ScrollViewer
        // { Content = editPanel }` 包一个 StackPanel）——编辑器是**内容高度**（树有多少行就多高），
        // 整列纵向滚动。
        //
        // 为什么不再用「标题 / 明细 / 编辑器 * / 编辑集状态」四行 Grid：编辑器是其中唯一的 * 行，
        // 余量不足时被上面的标题/明细与下面的状态文字挤扁，用户实测只剩三行（「预览框太小」）。
        // 内容高度口径下树永远不会被挤，空间不够时是整列滚动（与静态页手感一致）；
        // 树自身的可视区下限写在 JsonTreeEditor.xaml（TreeView 的 MinHeight）。
        var editPanel = new StackPanel();
        editPanel.Children.Add(titleRow);
        editPanel.Children.Add(_fileDetail);
        editPanel.Children.Add(_editor);
        editPanel.Children.Add(footer);

        _editPanel = editPanel;
        _editHost = new ScrollViewer
        {
            Content = editPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // 外层滚动器不参与 Tab 焦点链：否则 Tab 会停在滚动器上，
            // 用户从行内编辑框按 Tab 会「焦点消失」。
            Focusable = false,
            Visibility = Visibility.Collapsed,
        };

        // ── 浏览列：搜索行 ────────────────────────────────────────────
        _search = WorkbenchShell.CreateSearchBox("搜索文件名 / 键路径 / 值（防抖 300ms；走索引库，后台线程）");
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        var reload = WorkbenchShell.CreateButton("重新加载", (_, _) => _ = RefreshAsync());
        _clearCache = WorkbenchShell.CreateButton("清空缓存", (_, _) => ClearCache());
        _clearCache.ToolTip = "删除 cache/text-index.db（下次进页面重建索引；缓存只影响速度，删掉功能不受影响）";
        Shell.AddSearchItem(_search);
        Shell.AddSearchItem(reload);
        Shell.AddSearchItem(_clearCache);

        // ── 浏览列：筛选行（说明 + 状态 + 键数）───────────────────────
        _langInfo = WorkbenchShell.CreateSectionLabel("—");
        _langInfo.TextWrapping = TextWrapping.Wrap;
        // 下拉一律用共享工厂：具名样式 WorkbenchFilterCombo（MinHeight=32，不写死 28，
        // 否则 Fluent 模板的内容盒被压扁、中文被裁）+ 按最长候选项估算宽度。
        _stateFilter = WorkbenchShell.CreateFilterCombo("按状态筛选文件",
            "全部文件", "仅已修改", "仅非 UTF-8");
        _stateFilter.SelectionChanged += (_, _) => { _filterTimer.Stop(); _filterTimer.Start(); };
        _keyCountFilter = new TextBox
        {
            Width = 80, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "只显示键数不少于该值的文件（留空 = 不限）",
        };
        _keyCountFilter.TextChanged += (_, _) => { _filterTimer.Stop(); _filterTimer.Start(); };
        Shell.AddFilterItem(_langInfo);
        Shell.AddFilterItem(WorkbenchShell.CreateSectionLabel("文件："));
        Shell.AddFilterItem(_stateFilter);
        Shell.AddFilterItem(WorkbenchShell.CreateSectionLabel("键数 ≥"));
        Shell.AddFilterItem(_keyCountFilter);

        // ── 浏览列：列表 / 树切换（默认树）────────────────────────────
        Shell.AddViewToggles();
        Shell.ViewModeChanged += (_, _) => ApplyViewMode();

        // ── 浏览列内容：树 + 列表 + 独立搜索结果视图 ──────────────────
        _fileList = WorkbenchShell.CreateList();
        _fileList.ItemContainerStyle = WorkbenchShell.CreateListItemStyle();
        _fileList.Visibility = Visibility.Collapsed;
        var grid = new GridView();
        // 列显示的是「显示路径」（语言目录内部口径）；行模型同时带内部键（= 补丁键），
        // 导航一律走内部键，见 SelectFileAsync 的入参约定。
        grid.Columns.Add(new GridViewColumn { Header = "文件", DisplayMemberBinding = new Binding(nameof(TextFileRow.DisplayPath)), Width = 300 });
        grid.Columns.Add(new GridViewColumn { Header = "键数", DisplayMemberBinding = new Binding(nameof(TextFileRow.KeyCountLabel)), Width = 80 });
        grid.Columns.Add(new GridViewColumn { Header = "大小", DisplayMemberBinding = new Binding(nameof(TextFileRow.SizeLabel)), Width = 90 });
        grid.Columns.Add(new GridViewColumn { Header = "状态", DisplayMemberBinding = new Binding(nameof(TextFileRow.StateLabel)), Width = 80 });
        _fileList.View = grid;
        _fileList.SelectionChanged += (_, _) =>
        {
            if (!_suppressSelection && _fileList.SelectedItem is TextFileRow row) _ = SelectFileAsync(row.DisplayPath, null);
        };

        _fileTree = WorkbenchShell.CreateTree();
        _fileTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(TreeItem_Expanded));
        _fileTree.SelectedItemChanged += (_, e) =>
        {
            if (!_suppressSelection && e.NewValue is TreeViewItem { Tag: LangTextTreeNode { IsFile: true } node })
                _ = SelectFileAsync(node.RelativePath, null);
        };
        _fileTree.MouseDoubleClick += FileTree_DoubleClick;

        _hitHeader = WorkbenchShell.CreateSectionLabel("搜索结果");
        var closeHits = WorkbenchShell.CreateButton("返回浏览", (_, _) => ExitSearchMode());
        _hitList = WorkbenchShell.CreateList();
        _hitList.ItemContainerStyle = WorkbenchShell.CreateListItemStyle();
        var hitGrid = new GridView();
        hitGrid.Columns.Add(new GridViewColumn { Header = "类别", DisplayMemberBinding = new Binding(nameof(TextHitRow.KindLabel)), Width = 60 });
        hitGrid.Columns.Add(new GridViewColumn { Header = "命中位置", DisplayMemberBinding = new Binding(nameof(TextHitRow.Location)), Width = 240 });
        hitGrid.Columns.Add(new GridViewColumn { Header = "片段", DisplayMemberBinding = new Binding(nameof(TextHitRow.Snippet)), Width = 240 });
        hitGrid.Columns.Add(new GridViewColumn { Header = "文件", DisplayMemberBinding = new Binding(nameof(TextHitRow.RelativePath)), Width = 240 });
        _hitList.View = hitGrid;
        _hitList.SelectionChanged += (_, _) =>
        {
            if (_hitList.SelectedItem is TextHitRow row) _ = NavigateToHitAsync(row);
        };

        var hitBar = new StackPanel { Orientation = Orientation.Horizontal };
        hitBar.Children.Add(_hitHeader);
        hitBar.Children.Add(closeHits);
        _hitPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
        _hitPanel.Children.Add(hitBar);
        _hitPanel.Children.Add(_hitList);

        var content = new DockPanel();
        DockPanel.SetDock(_hitPanel, Dock.Top);
        content.Children.Add(_hitPanel);
        var views = new Grid();
        views.Children.Add(_fileTree);
        views.Children.Add(_fileList);
        content.Children.Add(views);
        Shell.SetBrowseContent(content);

        // ── 编辑列接线 ───────────────────────────────────────────────
        Shell.SetEditContent(_editHost);
        _editor.DocumentChanged += Editor_DocumentChanged;
        _editor.StatusMessage += (_, message) => Shell.SetStatus(message);
        Shell.SetStatus("正在准备文本工作台…");

        Loaded += async (_, _) =>
        {
            if (_loaded) return; // 页面常驻：只在首次显示时载入
            _loaded = true;
            await RefreshAsync();
        };
    }

    /// <summary>当前编辑集里的相对路径（lang 根口径 = 补丁键）。</summary>
    public IReadOnlyList<string> EditedFiles => _service.EditedFiles;

    /// <summary>编辑集里的文件数（宿主与导出计划据此判断「有没有文本改动」）。</summary>
    public int EditedFileCount => _service.EditedFiles.Count;

    /// <summary>索引库文件路径（诊断用）。</summary>
    public string IndexDatabasePath => _store.DatabasePath;

    /// <summary>关窗时持久化本页列宽（宿主 MainWindow 调用；与资源页同口径）。</summary>
    public void PersistUiState() => Shell.PersistPreviewWidth();

    /// <summary>宿主（Ctrl+F）聚焦搜索框。</summary>
    public void FocusSearch()
    {
        _search.Focus();
        _search.SelectAll();
    }

    /// <summary>宿主（Esc）在搜索框内清空搜索；未聚焦搜索框时返回 false 交回宿主。</summary>
    public bool TryEscapeSearch()
    {
        if (!_search.IsKeyboardFocusWithin) return false;
        _search.Text = string.Empty;
        ExitSearchMode();
        return true;
    }

    // ── 加载与索引 ───────────────────────────────────────────────────

    /// <summary>
    /// 宿主在启动扫描完成后调用（plan-15）：**主动**刷新本页数据到最新索引。
    /// 用户口径是「打开软件时刷新所有表单，而不是切页懒加载」，因此这里越过
    /// <c>_loaded</c> 首次显示守卫；只读索引库，不重新解析任何文件。
    /// </summary>
    public async Task ReloadFromIndexAsync()
    {
        _loaded = true;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var langRoot = _service.ResolveLangRoot(_host.Env.EffectiveGameDirectory(_host.Project));
        if (langRoot is null)
        {
            _files = [];
            _rows = [];
            _treeRoot = null;
            _selectedFile = null;
            _editor.Clear();
            SetBrowseItems();
            _langInfo.Text = "未定位 lang 目录（数据源 = config.json 指向的活动语言目录内部）。";
            Shell.SetEmptyHint("没有找到 lang 目录。请在「设置」页填写游戏目录（lang 目录为 LimbusCompany_Data/lang）。");
            Shell.SetStatus("没有找到 lang 目录。请在「设置」页填写游戏目录。");
            RefreshEditSetState();
            return;
        }

        // 无条件把 lang 根交给服务：索引命中路径不经过 EnumerateFiles，
        // 而编辑集方法（BeginEdit/SetModified/…）都要求服务已定位 lang 根与活动语言目录
        // （条目口径 = 相对活动语言目录，plan-16 §5）；漏掉这一步的现象就是
        // 「点文件后预览区空白、明细只显示 —」。
        _service.AttachLangRoot(langRoot);
        var languageDirectory = _service.ResolveLanguageDirectory(langRoot);
        var languageName = languageDirectory is null
            ? null
            : Path.GetFileName(languageDirectory.TrimEnd(Path.DirectorySeparatorChar));

        var generation = ++_loadGeneration;
        Shell.SetStatus("正在检查文本索引…");
        try
        {
            var source = TextIndexStore.DescribeSource(langRoot, languageName);
            var fresh = await Task.Run(() => _store.IsFresh(source));
            IReadOnlyList<LangTextFileInfo> files;
            long elapsed = 0;
            if (fresh)
            {
                files = await Task.Run(() => _store.ReadFiles(languageDirectory));
            }
            else
            {
                // 索引未建 / 源变过 / 库被删：重新枚举（有缓存时只重解析签名变过的文件）。
                Shell.SetStatus("正在建立文本索引（首次会扫描全部 lang 文件）…");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                files = await Task.Run(() =>
                {
                    var cached = _store.ReadFileMap(languageDirectory);
                    var enumerated = _service.EnumerateFiles(langRoot, cached);
                    _store.PersistFiles(source, enumerated);
                    return enumerated;
                });
                watch.Stop();
                elapsed = watch.ElapsedMilliseconds;
            }
            if (generation != _loadGeneration) return;

            _files = files;
            // 树按条目口径建（相对活动语言目录）：顶层就是语言目录的直接子项
            // （文件夹 StoryData / BattleAnnouncerDlg… 与根级 AbDlg_*.json），
            // **没有任何根节点、也没有语言目录那一层包裹**（plan-16 §4/§5）。
            // rootName 只是构造器的内部容器名，不会渲染。
            _treeRoot = LangTextTreeBuilder.Build(files.Select(x => x.RelativePath), "lang");
            _langInfo.Text = languageDirectory is null
                ? $"lang 根：{langRoot}\n⚠ config.json 未指定活动语言（或目录不存在），没有可浏览的文本表。" +
                  $"{files.Count} 个 JSON 文件"
                : $"数据源：{languageName}（{files.Count} 个 JSON 文件 · 条目相对该语言目录" +
                  (fresh ? " · 索引命中" : $" · 索引{(_store.WasRecreated ? "已重建（原库损坏）" : "已更新")} {elapsed}ms") +
                  $"）\nlang 根：{langRoot}　补丁键：{languageName}/…（导出补丁时自动补回这一层）";
            Shell.SetEmptyHint(languageDirectory is null
                ? "config.json 里没有可用的活动语言目录：请检查 <游戏>/LimbusCompany_Data/lang/config.json 的 lang 字段。"
                : null);
            SetBrowseItems();
            Shell.SetStatus(fresh
                ? "已从索引载入（未重读文件）。选中文件即可编辑；修改只进内存编辑集，导出补丁时才写盘。"
                : "索引已更新。选中文件即可编辑；修改只进内存编辑集，导出补丁时才写盘。");
            RefreshEditSetState();
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return;
            Shell.SetStatus($"载入 lang 文件失败：{ex.Message}");
        }
    }

    /// <summary>把当前文件清单分发到「树 / 列表」两个视图（两视图共用同一份筛选结果），
    /// 并建立「条目路径 → 文件条目」映射表（树 / 列表 / 搜索命中的导航都走它）。
    /// 本轮起条目路径 = 显示路径（相对活动语言目录），两者不再需要互相映射。</summary>
    private void SetBrowseItems()
    {
        _byDisplay = new Dictionary<string, LangTextFileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _files) _byDisplay[file.RelativePath] = file;
        _rows = _files
            .Select(x => new TextFileRow(
                x.RelativePath,
                x.RelativePath,
                x.KeyCount,
                x.SizeBytes,
                x.IsUtf8,
                _service.HasRealEdits(x.RelativePath)))
            .ToList();
        ApplyFileFilter();
        RebuildTree();
    }

    /// <summary>筛选（状态 / 键数）后绑定扁平列表（两千行级，防抖 300ms）。</summary>
    private void ApplyFileFilter()
    {
        var state = _stateFilter?.SelectedIndex ?? 0;
        var minKeys = int.TryParse(_keyCountFilter?.Text?.Trim(), out var parsed) ? parsed : -1;
        var rows = _rows.Where(row => state switch
        {
            1 => row.Modified,
            2 => !row.IsUtf8,
            _ => true,
        }).Where(row => minKeys < 0 || (row.IsUtf8 && row.KeyCount >= minKeys)).ToList();
        _suppressSelection = true;
        try
        {
            _fileList.ItemsSource = rows;
            if (_selectedFile is { } selected)
            {
                var match = rows.FirstOrDefault(x => x.RelativePath == selected.RelativePath);
                if (match is not null) _fileList.SelectedItem = match;
            }
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private void ApplyViewMode()
    {
        var tree = Shell.IsTreeMode;
        _fileTree.Visibility = tree ? Visibility.Visible : Visibility.Collapsed;
        _fileList.Visibility = tree ? Visibility.Collapsed : Visibility.Visible;
        if (tree) SyncTreeSelection();
    }

    private void ClearCache()
    {
        try
        {
            _store.DeleteDatabase();
            Shell.SetStatus("缓存已删除（cache/text-index.db）。点「重新加载」会重建索引——缓存只影响速度，删除不影响功能。");
        }
        catch (Exception ex)
        {
            Shell.SetStatus($"删除缓存失败：{ex.Message}");
        }
    }

    // ── 树（目录层级 → 文件，惰性展开）───────────────────────────────

    private void RebuildTree()
    {
        _suppressSelection = true;
        try
        {
            // 树顶层 = **活动语言目录的直接子项**：文件夹（StoryData / BattleAnnouncerDlg…）
            // 排在前面、根级 json 文件平铺在后。**没有根节点**（也从来没有语言目录那一层
            // 包裹节点）——根容器只是 LangTextTreeBuilder 的内部结构，不渲染。
            _fileTree.ItemsSource = _treeRoot is null
                ? null
                : _treeRoot.Children.Select(CreateTreeItem).ToList();
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private TreeViewItem CreateTreeItem(LangTextTreeNode node)
    {
        var item = new TreeViewItem
        {
            Header = BuildTreeHeader(node),
            Tag = node,
            Style = WorkbenchShell.CreateTreeItemStyle(),
            // 条目路径（相对活动语言目录）+ 加载器口径的补丁键（导出时自动补回语言目录那层）。
            ToolTip = node.IsFile
                ? $"{node.RelativePath}\n补丁键：{_service.ToPatchKey(node.RelativePath)}"
                : $"{node.RelativePath}/　（{node.Children.Count} 项）",
        };
        // 目录先放一个占位子项，真正展开时才生成下一层（与资源工作台同款惰性策略）。
        if (node.IsFolder && node.Children.Count > 0) item.Items.Add(new TreeViewItem { Header = "载入中…" });
        return item;
    }

    private string BuildTreeHeader(LangTextTreeNode node)
    {
        if (node.IsFile)
        {
            var info = FindFileByPath(node.RelativePath);
            var suffix = info is null
                ? string.Empty
                : info.IsUtf8 ? $"（{info.KeyCount} 键 · {TextFileRow.FormatSize(info.SizeBytes)}）" : "（非 UTF-8）";
            return info is not null && _service.HasRealEdits(info.RelativePath)
                ? $"● {node.Name}{suffix}"
                : $"{node.Name}{suffix}";
        }
        return node.Kind == LangTextTreeNodeKind.Root
            ? $"{node.Name}（{_files.Count} 个文件）"
            : $"{node.Name}（{node.Children.Count}）";
    }

    /// <summary>条目路径 → 文件条目（树 / 列表 / 搜索命中都用同一口径，不再需要映射）。</summary>
    private LangTextFileInfo? FindFileByPath(string relativePath)
        => _byDisplay.TryGetValue(relativePath, out var file) ? file : null;

    private void TreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item || item.Tag is not LangTextTreeNode node || !node.IsFolder) return;
        MaterializeChildren(item, node);
    }

    /// <summary>把占位子项替换成真实子项（只在展开时生成这一层）。</summary>
    private void MaterializeChildren(TreeViewItem item, LangTextTreeNode node)
    {
        if (item.Items.Count != 1 || item.Items[0] is not TreeViewItem { Tag: null }) return;
        item.Items.Clear();
        foreach (var child in LangTextTreeBuilder.Expand(node)) item.Items.Add(CreateTreeItem(child));
    }

    private void FileTree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_fileTree.SelectedItem is TreeViewItem { Tag: LangTextTreeNode { IsFile: true } node })
            _ = SelectFileAsync(node.RelativePath, null);
    }

    /// <summary>把当前选中文件同步到树（切到树视图时沿途展开，不丢选中）。</summary>
    private void SyncTreeSelection()
    {
        if (_selectedFile is not { } file || _fileTree.Items.Count == 0) return;
        var segments = file.RelativePath.Split('/');
        if (segments.Length == 0) return;

        _suppressSelection = true;
        try
        {
            // 顶层项是语言目录的直接子项（没有包裹节点），所以从 Items 里按第一段找起。
            var container = _fileTree.Items.OfType<TreeViewItem>().FirstOrDefault(x =>
                x.Tag is LangTextTreeNode node && node.RelativePath == segments[0]);
            if (container is null) return;
            for (var i = 1; i < segments.Length; i++)
            {
                if (container.Tag is LangTextTreeNode current) MaterializeChildren(container, current);
                var accumulated = string.Join('/', segments.Take(i + 1));
                var next = container.Items.OfType<TreeViewItem>().FirstOrDefault(x =>
                    x.Tag is LangTextTreeNode node && node.RelativePath == accumulated);
                if (next is null) return;
                container = next;
            }
            container.IsSelected = true;
            container.BringIntoView();
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    // ── 搜索（独立结果视图 + 索引库）─────────────────────────────────

    private async Task RunSearchAsync()
    {
        var query = _search.Text.Trim();
        if (query.Length == 0)
        {
            ExitSearchMode();
            return;
        }
        var generation = ++_searchGeneration;
        var files = _files;
        Shell.SetStatus($"正在搜索「{query}」…");
        try
        {
            var hits = await Task.Run(() => _service.Search(query, files, cached: _store.ReadHits()));
            if (generation != _searchGeneration) return;
            // 命中的 RelativePath 就是条目口径（相对活动语言目录），直接当显示路径用。
            var rows = hits
                .Select(x => new TextHitRow(x, x.RelativePath))
                .ToList();
            _hitList.ItemsSource = rows;
            _hitHeader.Text = rows.Count == 0
                ? $"没有匹配「{query}」的文件名 / 键 / 值"
                : $"搜索结果：{rows.Count} 条（点击可跳转到文件并定位到键）";
            _hitPanel.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Shell.SetStatus(rows.Count == 0
                ? $"没有匹配「{query}」的文件名/键/值。"
                : $"匹配 {rows.Count} 条（点击命中即可跳转文件并定位键；「返回浏览」收起结果）。");
        }
        catch (Exception ex)
        {
            if (generation != _searchGeneration) return;
            Shell.SetStatus($"搜索失败：{ex.Message}");
        }
    }

    private void ExitSearchMode()
    {
        _hitPanel.Visibility = Visibility.Collapsed;
        _hitList.ItemsSource = null;
    }

    /// <summary>点击命中：跳到文件；键/值命中再定位到键（<see cref="JsonTreeEditor.SelectPath"/>）。
    /// 命中的 RelativePath 就是条目口径，直接交给 <see cref="SelectFileAsync"/>。</summary>
    private async Task NavigateToHitAsync(TextHitRow row)
    {
        if (!_byDisplay.ContainsKey(row.Hit.RelativePath)) return;
        await SelectFileAsync(row.Hit.RelativePath,
            row.Hit.Kind == LangTextSearchKind.FileName ? null : row.Hit.KeyPath);
    }

    // ── 选中文件与编辑集 ─────────────────────────────────────────────

    /// <summary>
    /// 选中一个文件并载入编辑器。
    /// <paramref name="relativePath"/> 是**条目口径**（相对活动语言目录，树 / 列表 / 搜索结果都带它），
    /// 服务层的编辑集与索引库也是同一口径；加载器口径的补丁键只在导出时由
    /// <c>LangTextWorkbenchService.ToPatchKey</c> 补回。
    /// </summary>
    private async Task SelectFileAsync(string relativePath, string? keyPath)
    {
        var file = FindFileByPath(relativePath);
        if (file is null) return;

        // 同一文件被重复触发（树/列表刷新各自抛一次选中事件）：不重建编辑器，
        // 只把定位补上。见 _lastLoadedPath 的说明。
        if (!_loadingDocument &&
            string.Equals(_lastLoadedPath, file.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(keyPath)) _editor.SelectPath(keyPath);
            return;
        }

        var generation = ++_loadGeneration;
        _selectedFile = file;
        _loadingDocument = true;
        try
        {
            _fileTitle.Text = Path.GetFileName(relativePath);
            _editHost.Visibility = Visibility.Visible;
            SelectRowInViews(file.RelativePath);
            Shell.SetStatus($"正在读取 {relativePath}…");

            // 先读、后清：读失败（非 UTF-8 / 非法 JSON / 文件被删）时不再抹掉
            // 当前已经渲染好的键值树，只报错并保留现场。
            var text = await Task.Run(() => _service.BeginEdit(file.RelativePath));
            if (generation != _loadGeneration) return;

            _editor.Clear();
            _editor.LoadDocument(text, _service.TryGetVanillaText(file.RelativePath));
            _lastLoadedPath = file.RelativePath;
            if (!string.IsNullOrWhiteSpace(keyPath)) _editor.SelectPath(keyPath);
            _fileDetail.Text = $"{relativePath} · {TextFileRow.FormatSize(file.SizeBytes)} · " +
                                  (file.IsUtf8 ? $"{file.KeyCount} 个顶层键" : "非 UTF-8（不做编码猜测：键值搜索与编辑均跳过）") +
                                  (_service.HasRealEdits(file.RelativePath)
                                      ? " · 已修改（未导出）"
                                      : _service.IsModified(file.RelativePath)
                                          ? " · 已建立基线（未改动）"
                                          : string.Empty) +
                                  $"\n补丁键：{_service.ToPatchKey(file.RelativePath)}";
            Shell.SetStatus(keyPath is { Length: > 0 }
                ? $"已载入 {relativePath}，正在定位键 {keyPath}（树按需展开）…"
                : $"已载入 {relativePath}（修改只进内存编辑集）。");
            ReportKeyLocation(file.RelativePath, keyPath);
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return;
            _fileDetail.Text = "—";
            Shell.SetStatus($"读取失败：{ex.Message}");
        }
        finally
        {
            _loadingDocument = false;
            RefreshEditSetState();
        }
    }

    /// <summary>
    /// 定位结果的最终回报：<c>JsonTreeEditor.SelectPath</c> 需要等树走完一轮布局
    /// （首层容器还没进可视树时设置 <c>IsSelected</c> 会被 WPF 忽略），所以刚载入时
    /// 它可能返回 false 但随后自行完成。这里在 Loaded 优先级统一复查一次，
    /// 成功就报「已定位」，失败就明说没找到——不再出现「点了搜索命中却毫无反应」。
    /// </summary>
    private void ReportKeyLocation(string relativePath, string? keyPath)
    {
        if (keyPath is not { Length: > 0 }) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (!string.Equals(_selectedFile?.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)) return;
            Shell.SetStatus(_editor.SelectedRow?.Path == keyPath
                ? $"已定位到键 {keyPath}（{relativePath}）。"
                : $"已载入 {relativePath}，但没有找到键 {keyPath}（可能已改名或被删除）。");
        }));
    }

    /// <summary>把选中项同步到两个视图（程序化选中不触发重新打开文件）。</summary>
    private void SelectRowInViews(string relativePath)
    {
        _suppressSelection = true;
        try
        {
            if (_fileList.ItemsSource is IEnumerable<TextFileRow> rows)
            {
                var match = rows.FirstOrDefault(x => x.RelativePath == relativePath);
                if (match is not null) _fileList.SelectedItem = match;
            }
        }
        finally
        {
            _suppressSelection = false;
        }
        if (Shell.IsTreeMode) SyncTreeSelection();
    }

    /// <summary>编辑器的文档变更：把当前文本写进编辑集（失败时明确报错，编辑集保持原状态）。
    ///
    /// <para><b>先把「不是修改」的事件挡掉</b>：<c>LoadDocument</c> 结尾必发一次
    /// <c>DocumentChanged(IsModified=false)</c>（宿主据此刷新摘要），它不是用户改动。
    /// 只靠 <c>_loadingDocument</c> 守卫是不够的——那次事件可能在守卫窗口之外到达
    /// （<c>SelectPath</c> 现在是延后执行的），于是原文被 <c>SetModified</c> 塞进编辑集，
    /// 现象就是用户反馈的「一打开文件就提示目标文件已编辑，重启后又没了」
    /// （编辑集只在内存里，退出即丢）。</para></summary>
    private void Editor_DocumentChanged(object? sender, JsonDocumentChangedEventArgs e)
    {
        if (_loadingDocument || _selectedFile is null) return;
        if (!e.IsModified) return;   // 载入/切页签触发的通知：不是修改，绝不写编辑集
        var relativePath = _selectedFile.RelativePath;
        try
        {
            // 先判定「真的有改动」再进编辑集：载入时登记的那条基线（vanilla == modified）
            // 只有等这里第一次写入后才会变成真正的修改；反过来，恢复成原文的写入要
            // 把条目移出编辑集，否则「编辑集：N 个文件已改」会一直挂着这个文件（用户反馈的
            // 「一打开文件就提示已修改」有一半出在这种只进不出的账上）。
            if (string.Equals(_service.TryGetModifiedText(relativePath), e.JsonText, StringComparison.Ordinal))
            {
                RefreshEditSetState();
                return;
            }
            if (string.Equals(_service.TryGetVanillaText(relativePath), e.JsonText, StringComparison.Ordinal))
                _host.LangEdits.Revert(relativePath);
            else
                _host.LangEdits.SetModified(relativePath, e.JsonText);
        }
        catch (Exception ex)
        {
            Shell.SetStatus($"写入编辑集失败：{ex.Message}");
            return;
        }
        RefreshEditSetState();
    }

    private async void RevertFile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFile is null) return;
        var relativePath = _selectedFile.RelativePath;
        // 走宿主会话（而不是直接调服务）：会话要推进版本号并广播 Changed，
        // 否则 Revision 恒为 0、依赖它的导出报告对不上账。
        var reverted = _host.LangEdits.Revert(relativePath);
        Shell.SetStatus(reverted
            ? $"已还原 {relativePath}（移出编辑集；lang 目录从未被改动）。"
            : "该文件不在编辑集中。");
        await SelectFileAsync(relativePath, null);
    }

    /// <summary>刷新编辑集相关的 UI 状态（徽标 / 状态文案 / 列表与树上的「已修改」标记）。
    /// 导出与直接应用两个按钮已移到侧边栏（plan-14 14.5），这里不再维护它们的可用性。
    ///
    /// <para><b>「已修改」一律按「文本与官方原文不同」判定</b>（<see cref="LangTextWorkbenchService.HasRealEdits"/>），
    /// 不是「在编辑集里」：<c>BeginEdit</c> 为了让导出拿到差分基线会把**打开过的每个文件**
    /// 都登记进编辑集，用 <c>IsModified</c> 当「已修改」会一边看预览一边把它标成已改，
    /// 且计数永远 ≥ 1（用户反馈的「一打开文件就提示已修改」）。</para></summary>
    private void RefreshEditSetState()
    {
        var edited = _service.RealEditFiles;
        var hasEdits = edited.Count > 0;
        var selectedModified = _selectedFile is not null && _service.HasRealEdits(_selectedFile.RelativePath);

        _revertFile.IsEnabled = selectedModified;
        _modifiedBadge.Visibility = selectedModified ? Visibility.Visible : Visibility.Collapsed;
        _fileTitle.Text = _selectedFile is null
            ? "未选择文件"
            : (_selectedFile.IsUtf8 ? string.Empty : "⚠ ") + Path.GetFileName(_selectedFile.RelativePath);
        _editSetText.Text = hasEdits
            ? $"编辑集：{edited.Count} 个文件已改（只在内存里；用左侧「② 产出模组 → 导出模组…」落地）"
            : "编辑集：空（改完文本后用左侧「② 产出模组 → 导出模组…」落地）";

        // 「已修改」金色标记要跟着编辑集走（列表行 + 树里的 ● 前缀）。
        var changed = false;
        for (var i = 0; i < _rows.Count; i++)
        {
            var modified = _service.HasRealEdits(_rows[i].RelativePath);
            if (_rows[i].Modified == modified) continue;
            _rows[i] = _rows[i] with { Modified = modified };
            changed = true;
        }
        if (changed) ApplyFileFilter();
        if (_treeRoot is not null) RebuildTree();

        // 树 / 列表在重建后都会丢掉「选中高亮」（容器是新建的），但编辑器里的内容还在。
        // 这里把选中同步回去，让用户看到的选中行与编辑列里的文件始终一致
        // （同步走的是程序化路径，不会触发重新打开文件，见 _lastLoadedPath）。
        if (_selectedFile is not null) SelectRowInViews(_selectedFile.RelativePath);
    }

    // ── 导出与直接应用（plan-16 S6 起已移除）──────────────────────────
    // 旧的「导出 lang 补丁…」「直接应用到 lang 目录…」两个入口与实现一并删除：
    // ① 文本改动统一由侧边栏「导出模组…」写成 <项目名>_text/{bus,patch,pathset}/… 多套格式；
    // ② 「直接写游戏目录」这件事由「使用当前修改启动游戏进行调试」承担（带备份与关闭还原），
    //    一条没有备份的裸写路径不该再留在界面上。
}
