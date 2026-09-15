using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Assets.Preview;
using LimbusModEditor.Application.Caching;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.Spine;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Editing.Images;
using LimbusModEditor.Formats.Bank;
using NLog;

namespace LimbusModEditor.App;

/// <summary>
/// 资源工作台页面（plan-02 第 1.2 步：原资源标签内容原样搬入，行为不变）。
/// 页面常驻不销毁（切换页面保留搜索/选中/预览状态）；项目状态刷新由宿主经
/// <see cref="OnProjectRefreshed"/> 驱动。plan-03 已移除手动导入入口，plan-04
/// 加入可拖拽的预览列与占比持久化。
/// </summary>
public partial class AssetsWorkbenchPage : UserControl, ISearchableWorkbench, IReferenceRevealable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 待定位的资源（精确跳转用）。搜索是<b>异步</b>的（过滤 + 排序在后台线程，
    /// 见 <see cref="RunSearchAsync"/>），所以「先选中」不可能成功——只能把目标记在这里，
    /// 等这一代搜索结果挂上列表后再选中并滚到它。null = 没有待定位目标。
    /// <para>存 <see cref="AssetRecord.LogicalPath"/> 而不是 <c>AssetId</c>：
    /// 后者是每次重建记录都会变的 <c>Guid</c>，一旦列表改成从索引库分页取记录
    /// （见 <c>AssetCatalog</c>）就会全部失配。</para>
    /// </summary>
    private string? _pendingRevealLogicalPath;

    private readonly IWorkbenchHost _host;
    private readonly AssetEditService _assetEdits = new();
    private readonly ImageAtlasEditService _atlasEdits = new();
    private readonly SpriteMetadataEditService _spriteEdits = new();
    private readonly UnityFieldEditService _unityFieldEdits = new();
    private readonly AssetPreviewRegistry _previewRegistry;
    // Spine 动画预览的素材解析（plan-11 深化）：与预览注册表**共用同一个** SpinePreviewService，
    // 于是「同目录索引」只建一份；两个入口（资源页的 Spine 预览 / 卡片详情的 Spine 行）
    // 看到的图集与贴图口径必然一致。
    private readonly SpineAnimationSourceService _spineAnimationSource;
    // 关联资源反查门面（plan-11 派生库）：只读 cache/relation-index.db，缺库时返回空 + 原因，
    // 绝不抛异常 —— 关联是旁路信息，缺了不该把资源预览搞崩。
    private readonly RelationQueryService _relations;
    private readonly DispatcherTimer _searchTimer;
    // ③ 布局实测日志的合并去抖（只观测，不参与布局）。
    private readonly DispatcherTimer _layoutLogTimer;
    private readonly UiStateService _uiState;
    private readonly string _uiStateFile;
    private int _searchGeneration;
    private int _previewGeneration;
    // ── 资源目录：按页查询（AssetCatalog）+ 页码条状态 ────────────────────
    // 目录门面按项目缓存：它内部持有「索引库 + 项目态来源」，两者在项目换掉或
    // 资产条数变化（扫描新增 / 提取音频到项目）时必须重来，见 CatalogFor 的说明。
    private AssetCatalog? _catalog;
    private ModProject? _catalogProject;
    private int _catalogAssetCount = -1;
    // 当前页（0 基）与页大小。页码条与列表**必须来自同一次查询**，
    // 所以这两个值只在 RunSearchAsync 里被读取/回写。
    private long _pageIndex;
    private int _pageSize = DefaultPageSize;
    // 音频试听：预览管线给出的已解码 WAV + WPF MediaPlayer（切换选中/关窗即停）。
    private System.Windows.Media.MediaPlayer? _previewPlayer;
    private string? _previewAudioFile;
    private AssetPreviewAudio? _currentAudio;
    // 当前选中资源：列表与目录树两个视图共用（右键菜单/双击/按钮都以此为准）。
    private AssetRecord? _selectedAsset;
    // 最近一次搜索结果的**目录树数据源**（列表模式下为 null —— 那时不需要树）。
    // 它只常驻「命中序列的 8 字节条目」，每展开一层才物化那一层（见 AssetCatalogTree）。
    private AssetCatalogTree? _tree;
    // 命中总数：页码条与「共 N 条」用，列表与树共用同一个数。
    private long _lastTotal;
    // 目录树已展开节点的稳定 key（跨重建累积）：修复「刷新/同步后树突然折叠回初始形态」。
    private readonly HashSet<string> _expandedTreeKeys = new(StringComparer.OrdinalIgnoreCase);
    // 默认以「容器目录树」呈现：用户看到的是类文件夹结构，而不是扁平技术路径。
    private bool _treeMode = true;
    private bool _filtersInitialized;
    // plan-13：右栏行高下限。整栏高度不够时由外层 ScrollViewer 兜底滚动，
    // 而不是把预览或创作状态裁掉（用户反馈：「窗口较小时预览看不全 / 滑不动」）。
    private const double MinStatusPaneHeight = 240;
    private const double PreviewColumnMinHeight = 240;
    private const double PreviewRowFloor = UiStateService.MinPreviewEditorHeight;
    // 预览块默认占右栏可视高的比例（首次使用；用户拖过之后就以用户值为准）。
    private const double PreviewRowDefaultShare = 0.55;
    // 预览块最大占右栏可视高的比例：再大就把「创作状态」挤到看不见。
    private const double PreviewRowMaxShare = 0.72;
    // plan-13：独立「放大预览」窗口（主窗很小时也能大尺寸查看/编辑）。
    private PreviewWindow? _previewWindow;
    // 用户手动定位过预览高度（拖拽/双击）后就别再自动收敛，免得跟用户抢把手。
    private bool _previewHeightUserSet;

    /// <summary>默认页大小。200 是实测的折中：一页 200 条约 0.3 MB 记录、行视图模型
    /// 200 个（旧实现是一次性 1,275,623 个），滚动一屏也就几十条。</summary>
    private const int DefaultPageSize = 200;

    /// <summary>每页条数可选项（与 XAML 里 ComboBoxItem 的顺序一一对应）。</summary>
    private static readonly int[] PageSizes = [100, 200, 500, 1000];

    public AssetsWorkbenchPage(IWorkbenchHost host)
    {
        _host = host;
        InitializeComponent();
        // 搜索防抖：全缓存扫描后有数十万资产，逐键即时过滤会卡顿；
        // 停止输入 300ms 后才真正过滤（过滤与排序在后台线程）。
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _ = RunSearchAsync(); };
        // plan-04：预览列占比从 <程序目录>/config/ui-state.json 恢复（缺失/损坏回退 360）。
        _uiStateFile = Path.Combine(host.Env.ConfigDirectory, "ui-state.json");
        _uiState = UiStateService.Load(_uiStateFile);
        PreviewColumn.Width = new GridLength(_uiState.AssetsPreviewColumnWidth);
        // plan-13：预览块高度（可拖、可复位、持久化）—— 用户的「预览太小」诉求从这里解。
        PreviewRow.Height = new GridLength(_uiState.GetPreviewHeight(WorkbenchPageKeys.Assets));
        // 下限与 UiStateService 的钳制区间共用一套常量（XAML 里不再写死数字）。
        PreviewRow.MinHeight = PreviewRowFloor;
        PreviewHost.MinHeight = PreviewRowFloor;
        PreviewColumnHost.MinHeight = PreviewColumnMinHeight;
        // plan-05：预览提供者管线（FMOD 目录每次现取，设置改动后立即生效）。
        // plan-11：再挂一个 Spine 预览服务（资源列表每次现取，项目换了自动失效）。
        var spinePreview = new SpinePreviewService(() =>
            (IReadOnlyList<AssetRecord>?)host.Project?.Assets ?? Array.Empty<AssetRecord>());
        _spineAnimationSource = new SpineAnimationSourceService(spinePreview);
        _previewRegistry = AssetPreviewRegistry.CreateDefault(
            () => host.Env.EffectiveFmodLibraryDirectory(host.Project),
            spinePreview);
        // 关联资源板块的数据源：库路径与启动扫描用的同一份（cache/relation-index.db）。
        // 构造 RelationStore 只建目录、不开连接；真正读库在选中资源后按需发生。
        _relations = new RelationQueryService(new RelationStore(host.Env.CacheDirectory));
        // ③ 高度/裁切诊断：页面尺寸变化只做合并观测（不改布局）。
        _layoutLogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _layoutLogTimer.Tick += (_, _) => { _layoutLogTimer.Stop(); LogLayoutMetrics("页面尺寸变化后"); };
        SizeChanged += OnPageSizeChanged;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        Log.Info("资源工作台页面构造完成：默认视图={0}，预览列宽={1}，预览块高={2}，ui-state={3}",
            _treeMode ? "树形" : "列表", PreviewColumn.Width, PreviewRow.Height, _uiStateFile);
    }

    /// <summary>当前选中的资源（宿主拖放判定用）。</summary>
    public AssetRecord? SelectedAsset => _selectedAsset;

    /// <summary>宿主刷新项目状态时调用：重算计数并重跑搜索（选中项按 AssetId 保留）。
    /// 百万级资产下计数在后台线程算，避免刷新瞬间卡死 UI。</summary>
    public void OnProjectRefreshed()
    {
        using var scope = Log.Scope("OnProjectRefreshed");
        var project = _host.Project;
        Log.Info("项目状态刷新入口：资产总数={0}，视图={1}，已展开树键={2}",
            project?.Assets.Count ?? 0, _treeMode ? "树形" : "列表", _expandedTreeKeys.Count);
        AssetCountText.Text = (project?.Assets.Count ?? 0).ToString();
        EditCountText.Text = "…";
        _ = UpdateEditCountAsync();
        // 与提示条/一键导出同一口径：按编辑标记统计「已修改资源」数，
        // 而不是 Edits 历史条数（重复替换会让历史虚增）。
        RefreshAssetList();
    }

    private async Task UpdateEditCountAsync()
    {
        using var scope = Log.Scope("UpdateEditCountAsync");
        var project = _host.Project;
        if (project is null) { EditCountText.Text = "0"; Log.Debug("项目为空，已修改资源数记为 0"); return; }
        var count = await Task.Run(() => project.Assets.Count(AssetEditService.HasEdits));
        EditCountText.Text = count.ToString();
        Log.Debug("已修改资源计数完成：{0} / {1}", count, project.Assets.Count);
    }

    /// <summary>宿主（Ctrl+F）聚焦搜索框。</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>宿主（Esc）在搜索框内清空搜索；未聚焦搜索框时返回 false 交回宿主。</summary>
    public bool TryEscapeSearch()
    {
        if (!SearchBox.IsKeyboardFocusWithin) return false;
        SearchBox.Text = string.Empty;
        return true;
    }

    /// <summary>关窗时持久化布局占比。</summary>
    public void PersistUiState()
    {
        SavePreviewWidth();
        SavePreviewHeight();
    }

    // ── plan-04：预览列拖拽与占比持久化 ─────────────────────────────

    private void PreviewSplitter_DragCompleted(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Log.Info("用户拖拽预览列分隔条完成：预览列宽={0}", PreviewColumn.Width);
        SavePreviewWidth();
        LogLayoutMetrics("拖拽预览列后");
    }

    /// <summary>双击把手复位默认宽（360）并持久化。</summary>
    private void PreviewSplitter_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Log.Info("用户双击预览列分隔条：复位为 360（原 {0}）", PreviewColumn.Width);
        PreviewColumn.Width = new GridLength(360);
        SavePreviewWidth();
        LogLayoutMetrics("复位预览列宽后");
    }

    private void SavePreviewWidth()
    {
        _uiState.AssetsPreviewColumnWidth = PreviewColumn.Width.IsAbsolute ? PreviewColumn.Width.Value : 360;
        UiStateService.Save(_uiState, _uiStateFile);
        Log.Debug("预览列宽持久化：{0} → {1}", _uiState.AssetsPreviewColumnWidth, _uiStateFile);
    }

    // ── plan-13：预览块高度（拖拽 / 复位 / 持久化 / 按可用高度收敛） ──

    /// <summary>用户在右栏拖完水平把手：记下新高度并持久化。</summary>
    private void PreviewHeightSplitter_DragCompleted(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _previewHeightUserSet = true;
        Log.Info("用户拖拽预览高度分隔条完成：预览块高={0}", PreviewRow.ActualHeight);
        SavePreviewHeight();
        LogLayoutMetrics("拖拽预览高度后");
    }

    /// <summary>双击水平把手：复位默认高度并持久化。</summary>
    private void PreviewHeightSplitter_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var before = PreviewRow.ActualHeight;
        PreviewRow.Height = new GridLength(UiStateService.DefaultPreviewEditorHeight);
        _previewHeightUserSet = true;
        ApplyPreviewRowHeight();
        SavePreviewHeight();
        Log.Info("用户双击预览高度分隔条：复位为默认 {0}（原 {1:0}）",
            UiStateService.DefaultPreviewEditorHeight, before);
        LogLayoutMetrics("复位预览高度后");
    }

    private void SavePreviewHeight()
    {
        var height = PreviewRow.Height.IsAbsolute ? PreviewRow.Height.Value : PreviewRow.ActualHeight;
        _uiState.SetPreviewHeight(WorkbenchPageKeys.Assets, height);
        UiStateService.Save(_uiState, _uiStateFile);
        Log.Debug("预览块高度持久化：{0:0} → {1}", height, _uiStateFile);
    }

    /// <summary>
    /// 按右栏可用高度落定预览行高（每次布局变化都跑，不写死比例）。
    ///
    /// <para>规则：用户拖过就完全听用户的（只保证不低于 <see cref="PreviewRowFloor"/>），
    /// 没拖过则取「可用高 × <see cref="PreviewRowDefaultShare"/>」（首次使用时的合理占比）。
    /// 「创作状态」那一行是 <c>*</c>，随剩余高度伸缩；它的自然高由
    /// <see cref="SyncStatusPanelFloor"/> 兜底。整栏内容一旦超过可视高，
    /// 外层 <c>PreviewColumnScroller</c> 就出现滚动条 —— 窗口再矮也能把下面滑出来
    /// （这修的就是用户反馈的「窗口小时滑不动」）。</para>
    /// </summary>
    private void ApplyPreviewRowHeight()
    {
        var available = PreviewColumnScroller.ViewportHeight;
        if (available <= 1) available = ActualHeight - 48; // 首帧尚无视口：按页面高估算
        if (available <= 1) return;
        var splitter = PreviewHeightSplitter.ActualHeight > 1 ? PreviewHeightSplitter.ActualHeight : 6;
        var maxPreview = Math.Max(PreviewRowFloor, available * PreviewRowMaxShare);
        var minHost = Math.Max(PreviewColumnMinHeight, Math.Max(available, PreviewRowFloor + splitter + MinStatusPaneHeight));
        // 已持久化的用户高度优先；没有记录（新安装 / 旧配置）时按可用高取默认占比，
        // 这样 1400 高的窗口不会只给预览 380 的固定值。
        var persisted = _uiState.TryGetPreviewHeight(WorkbenchPageKeys.Assets, out var stored)
            ? stored
            : available * PreviewRowDefaultShare;
        var applied = Math.Clamp(persisted, PreviewRowFloor, maxPreview);

        if (Math.Abs(PreviewColumnHost.MinHeight - minHost) > 0.5) PreviewColumnHost.MinHeight = minHost;
        // 创作状态内容实测高 → 作为它的 MinHeight：行有余量时铺满整行，行被挤小时把整栏
        // 顶高、外层滚动条随即出现（内容永远滑得到；这里不再有第二层滚动器抢滚轮）。
        SyncStatusPanelFloor();
        // 用户已经手动定位过高度就只更新 MinHeight（保证滑得到），不再改行高。
        if (_previewHeightUserSet) return;
        var current = PreviewRow.Height.IsAbsolute ? PreviewRow.Height.Value : -1;
        if (current < 0 || Math.Abs(current - applied) > 0.5)
        {
            PreviewRow.Height = new GridLength(applied);
            if (Log.IsDebugEnabled) Log.Debug(
                "预览行高落定：可用高={0:0}，偏好值={1:0}（有记录={2}），落定值={3:0}，上限={4:0}，右栏 MinHeight={5:0}",
                available, persisted, _uiState.TryGetPreviewHeight(WorkbenchPageKeys.Assets, out _), applied, maxPreview, minHost);
        }
    }

    /// <summary>把「创作状态」内容的实测高写进 ScrollViewer 的 MinHeight（见 ApplyPreviewRowHeight）：
    /// StatusScroll 用 <c>ScrollBarVisibility="Disabled"</c> 量内容自然高（ExtentHeight），
    /// 写回 MinHeight 后行有余量时铺满整行、行被挤小时把整栏顶高，外层滚动条随即出现。</summary>
    private void SyncStatusPanelFloor()
    {
        if (StatusPanel.Children.Count == 0) return;
        var contentHeight = StatusScroll.ExtentHeight;
        if (!double.IsFinite(contentHeight) || contentHeight <= 1) return;
        var target = Math.Max(MinStatusPaneHeight, contentHeight);
        if (Math.Abs(StatusScroll.MinHeight - target) > 0.5) StatusScroll.MinHeight = target;
    }

    // ── ③ 高度/滚动诊断：只读测量，不参与布局决策 ────────────────────
    // 用户报障「预览区（最顶栏『创作状态』整块）在窗口变矮时不能上下滑动、内容被裁」。
    // 这里只把布局实测值记下来（页面总高、右栏两行高度、预览块与「创作状态」滚动区的
    // 实测高度），不改任何布局。

    /// <summary>记录一次布局实测值（Debug）。<paramref name="reason"/> 说明触发场景。</summary>
    private void LogLayoutMetrics(string reason)
    {
        var previewGrid = PreviewHost.Parent as Grid;          // 预览块三行（标题/内容/信息）
        var hostGrid = previewGrid?.Parent as Grid;            // 右栏三行（预览/把手/创作状态）
        Log.Debug(
            "布局实测（{0}）：页面 Actual={1}x{2}；右栏可视高(外层滚动器)={3:0}，可滚高={4:0}，滚动条={5}；"
            + "预览行 Actual={6:0}/{7}，把手={8:0}，状态行 Actual={9:0}/{10}；"
            + "预览块 Actual={11:0}x{12:0}（标题{13:0}/内容{14:0}/信息{15:0}），PreviewHost Actual={16:0}x{17:0}；"
            + "创作状态自然高={18:0}（MinHeight={20:0}）；右栏 MinHeight={19:0}",
            reason, ActualWidth, ActualHeight,
            PreviewColumnScroller.ViewportHeight, PreviewColumnScroller.ExtentHeight,
            PreviewColumnScroller.ComputedVerticalScrollBarVisibility,
            PreviewRow.ActualHeight, PreviewRow.Height,
            PreviewHeightSplitter.ActualHeight,
            StatusRow.ActualHeight, StatusRow.Height,
            previewGrid?.ActualWidth ?? -1, previewGrid?.ActualHeight ?? -1,
            previewGrid is { RowDefinitions.Count: > 0 } ? previewGrid.RowDefinitions[0].ActualHeight : -1,
            previewGrid is { RowDefinitions.Count: > 1 } ? previewGrid.RowDefinitions[1].ActualHeight : -1,
            previewGrid is { RowDefinitions.Count: > 2 } ? previewGrid.RowDefinitions[2].ActualHeight : -1,
            PreviewHost.ActualWidth, PreviewHost.ActualHeight,
            StatusScroll.ExtentHeight,
            hostGrid?.MinHeight ?? -1,
            StatusScroll.MinHeight);
    }

    /// <summary>页面尺寸变化的合并观测（500 ms 去抖，避免拖拽窗口时刷屏）。</summary>
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // plan-13：尺寸变化即按新可用高度收敛预览行高（保证「预览吃满、状态保底」）。
        ApplyPreviewRowHeight();
        _layoutLogTimer.Stop();
        _layoutLogTimer.Start();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Log.Info("资源工作台页面 Loaded：页面尺寸={0}x{1}，视图={2}，预览列宽={3}",
            ActualWidth, ActualHeight, _treeMode ? "树形" : "列表", PreviewColumn.Width);
        ApplyPreviewRowHeight();
        LogLayoutMetrics("页面 Loaded");
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        Log.Info("资源工作台页面 Unloaded：页面尺寸={0}x{1}", ActualWidth, ActualHeight);
        // plan-13：放大预览窗口不是页面的可视化子级，页面卸载时要自己收掉，
        // 否则关项目/关窗后它会孤零零留在桌面上。
        if (_previewWindow is { } window)
        {
            _previewWindow = null;
            try { window.Close(); }
            catch (Exception ex) { Log.Warn(ex, "关闭放大预览窗口失败（忽略）"); }
        }
    }

    // ── plan-03：拖放收窄为「单张图片拖到选中的图像资源上替换」──────────

    /// <summary>是否接受这次拖放（宿主 DragOver 判定光标用）：只有单张图片 +
    /// 已选中图像资源 + 已打开项目才给 Copy 光标，其余一律 None。</summary>
    public bool CanAcceptImageDrop(IDataObject data)
    {
        if (_host.Project is null || _host.ProjectFile is null) return false;
        if (data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths) return false;
        if (!File.Exists(paths[0]) || !ImagePreviewService.IsSupportedExtension(Path.GetExtension(paths[0]))) return false;
        return _selectedAsset is AssetRecord selected && selected.Type is AssetType.Texture or AssetType.Sprite;
    }

    /// <summary>拖放替换：单张图片拖到选中图像资源上 → 询问后登记替换。
    /// 其他拖放内容不再触发导入（plan-03：只允许自动加载资源）。</summary>
    public async Task HandleImageDropAsync(IDataObject data)
    {
        if (!CanAcceptImageDrop(data)) return;
        var path = ((string[])data.GetData(DataFormats.FileDrop))[0];
        var selected = _selectedAsset!;
        var project = _host.Project!;
        var projectDirectory = Path.GetDirectoryName(_host.ProjectFile!)!;
        var choice = MessageBox.Show(OwnerWindow,
            $"把 {Path.GetFileName(path)} 用作选中资源的替换图？\n\n资源：{AssetDisplay.DisplayPath(selected)}",
            "拖放替换", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes) return;
        Log.Info("用户确认拖放替换：资源={0}，来源图片={1}", AssetDisplay.DisplayPath(selected), path);
        try
        {
            await _assetEdits.ReplaceFromFileAsync(project, selected, path, projectDirectory);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState("拖放替换已登记");
            RestoreListSelection(selected);
        }
        catch (Exception ex) { Log.Error(ex, "拖放替换失败：资源={0}，来源图片={1}", AssetDisplay.DisplayPath(selected), path); ShowError("拖放替换失败", ex); }
    }

    // ── 搜索 / 筛选 ──────────────────────────────────────────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchTimer is null) return; // XAML 解析期间可能先于构造函数赋值
        if (Log.IsDebugEnabled) Log.Debug("用户修改搜索框：关键词长度={0}", SearchBox.Text.Length);
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_filtersInitialized || _searchTimer is null) return;
        if (Log.IsDebugEnabled) Log.Debug(
            "用户修改下拉筛选：类型={0}{1}，状态={2}{3}，排序={4}，大小={5}~{6} KB",
            TypeFilter.SelectedIndex,
            TypeFilter.SelectedItem is null ? "(全部)" : "(已选)",
            StateFilter.SelectedIndex,
            StateFilter.SelectedItem is null ? "(全部)" : "(已选)",
            SortFilter?.SelectedIndex ?? -1,
            MinSizeFilter?.Text ?? "-", MaxSizeFilter?.Text ?? "-");
        RequestSearch();
    }

    /// <summary>复选框类筛选（「仅显示容器内资源」/「显示静态数据表」/「仅已替换」）：XAML 里的
    /// 初始 IsChecked 会在解析期间就触发 Checked，此时 <see cref="_searchTimer"/>
    /// 还没建好（构造函数在 InitializeComponent 之后才赋值），必须跳过。</summary>
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_searchTimer is null || _host.Project is null) return;
        if (Log.IsDebugEnabled) Log.Debug("用户勾选复选框筛选：容器内={0}，显示静态表={1}，仅已替换={2}，发送者={3}",
            ContainerOnlyFilter?.IsChecked == true, ShowStaticFilter?.IsChecked == true,
            ReplacedOnlyFilter?.IsChecked == true, (sender as FrameworkElement)?.Name ?? "-");
        RequestSearch();
    }

    /// <summary>筛选变化后的搜索请求：搜索已在跑（<see cref="_searchTimer"/> 启用）
    /// 时不抢跑，只重置防抖时钟——连续改动折叠成一次搜索；空闲时立即重跑
    /// （复选框 / 清除筛选的改动必须马上看到结果）。</summary>
    private void RequestSearch()
    {
        if (_searchTimer is null) return;
        if (_searchTimer.IsEnabled) { _searchTimer.Stop(); _searchTimer.Start(); return; }
        _ = RunSearchAsync();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        Log.Info("用户点击「清除筛选」：重置全部筛选条件。");
        SearchBox.Text = string.Empty;
        TypeFilter.SelectedIndex = -1;
        StateFilter.SelectedIndex = -1;
        SortFilter.SelectedIndex = 0;
        ContainerOnlyFilter.IsChecked = true;
        ShowStaticFilter.IsChecked = false;
        MinSizeFilter.Text = string.Empty;
        MaxSizeFilter.Text = string.Empty;
        ReplacedOnlyFilter.IsChecked = false;
        _searchTimer.Stop();
        // 上面这些赋值会经 Filter_Changed 触发防抖搜索；这里要求「立刻」重跑，
        // 避免用户看到清除筛选后仍然空白的旧结果。
        _ = RunSearchAsync();
    }

    /// <summary>初始化筛选下拉框（仅一次）并触发一次搜索。真正的过滤在
    /// RunSearchAsync（后台线程 + 防抖）。</summary>
    private void RefreshAssetList()
    {
        using var scope = Log.Scope("RefreshAssetList");
        if (_host.Project is null)
        {
            AssetList.ItemsSource = null; AssetTree.ItemsSource = null;
            _tree = null; _lastTotal = 0; _pageIndex = 0;
            UpdatePageBar();
            Log.Debug("刷新资源列表：无项目，已清空列表与目录树");
            return;
        }
        // 回第 0 页：刷新之后「上次停在第 137 页」没有意义（命中集可能完全不同）。
        _pageIndex = 0;
        Log.Debug("刷新资源列表：资产总数={0}，筛选已初始化={1}，视图={2}",
            _host.Project.Assets.Count, _filtersInitialized, _treeMode ? "树形" : "列表");
        if (!_filtersInitialized)
        {
            _filtersInitialized = true;
            TypeFilter.ItemsSource = Enum.GetValues<AssetType>().Select(t => new
            {
                Value = (AssetType?)t,
                Label = t == AssetType.Unknown ? "（全部类型）" : AssetDisplay.TypeLabel(t)
            });
            TypeFilter.DisplayMemberPath = "Label";
            TypeFilter.SelectedIndex = 0;
            StateFilter.ItemsSource = Enum.GetValues<AssetEditState>().Select(s => new
            {
                Value = (AssetEditState?)s,
                Label = s == AssetEditState.Unchanged ? "（全部状态）" : AssetDisplay.StateLabel(s)
            });
            StateFilter.DisplayMemberPath = "Label";
            StateFilter.SelectedIndex = 0;
        }
        _searchTimer.Stop();
        _ = RunSearchAsync();
    }

    private AssetSearchQuery BuildSearchQuery()
    {
        var selectedType = SelectedFilterValue<AssetType>(TypeFilter);
        var selectedState = SelectedFilterValue<AssetEditState>(StateFilter);
        long? minKb = null, maxKb = null;
        if (long.TryParse(MinSizeFilter.Text.Trim(), out var minSize) && minSize > 0) minKb = minSize * 1024;
        if (long.TryParse(MaxSizeFilter.Text.Trim(), out var maxSize) && maxSize > 0) maxKb = maxSize * 1024;
        var sort = (AssetSortKind)Math.Clamp(SortFilter?.SelectedIndex ?? 0, 0, 4);
        Log.Debug("构造搜索条件：关键词长度={0}，类型={1}，状态={2}，最小={3} KB，最大={4} KB，"
            + "仅已替换={5}，排序={6}，仅容器内={7}，显示静态表={8}",
            SearchBox?.Text?.Length ?? 0,
            selectedType?.ToString() ?? "-", selectedState?.ToString() ?? "-",
            minKb?.ToString() ?? "-", maxKb?.ToString() ?? "-",
            ReplacedOnlyFilter?.IsChecked == true, sort,
            ContainerOnlyFilter?.IsChecked == true,
            ShowStaticFilter?.IsChecked == true);
        return new AssetSearchQuery(
            SearchBox?.Text,
            selectedType,
            selectedState,
            null,
            null,
            null,
            minKb,
            maxKb,
            ReplacedOnlyFilter!.IsChecked,
            sort,
            ContainerOnlyFilter?.IsChecked == true ? true : null,
            ShowStaticFilter?.IsChecked == true);
    }

    /// <summary>下拉筛选的当前值：未选择、以及列表首项「（全部…）」一律返回
    /// null（= 该维度不过滤）。首项承载的是枚举的 0 值（<see cref="AssetType.Unknown"/>
    /// / <see cref="AssetEditState.Unchanged"/>），若按值下发就会变成「只要未知类型」
    /// ——那会把全部资源过滤光（真实缓存 1275623 条 → 0 条，列表与目录树同时空白）。</summary>
    private static TEnum? SelectedFilterValue<TEnum>(ComboBox? combo) where TEnum : struct, Enum
    {
        if (combo?.SelectedIndex is not > 0 || combo.SelectedItem is null) return null;
        var value = combo.SelectedItem.GetType().GetProperty("Value")?.GetValue(combo.SelectedItem);
        return value is TEnum typed ? typed : null;
    }

    /// <summary>
    /// 当前项目对应的资源目录门面。**按项目缓存**，但也必须会失效 ——
    /// 它内部持有索引库与项目态来源（后者把项目的资产集合索引成字典并摘出补充集），
    /// 项目换掉、或资产条数变过（扫描新增、音频工作台「提取到项目」）之后都必须重来，
    /// 否则新资源在列表里会看不见。
    /// <para>条数相同但内容被换掉（删一条又加一条）不在此检测范围内：那种情况下记录对象
    /// 本身仍是活的（<c>Find</c> 返回同一条对象，编辑状态/元数据实时可见），只有
    /// 「路径 → 记录」的映射会滞后，属于可接受的极端情形。</para>
    /// </summary>
    private AssetCatalog CatalogFor(ModProject project)
    {
        if (_catalog is not null && ReferenceEquals(_catalogProject, project)
            && _catalogAssetCount == project.Assets.Count) return _catalog;
        // 索引库路径与启动扫描用的是同一个文件（cache/unity-cache-index.db）。
        // 库不存在时 AssetCatalog 返回空命中集，此时列表里剩下的正是项目态补充集
        // （导入的旧式模组）—— 与「扫描结果为空」的语义一致，不需要第二条代码路径。
        var store = new UnityCacheSqliteIndexStore(
            Path.Combine(_host.Env.CacheDirectory, WorkbenchCachePaths.UnityCacheIndexFileName));
        _catalog = new AssetCatalog(store, project);
        _catalogProject = project;
        _catalogAssetCount = project.Assets.Count;
        Log.Debug("资源目录门面已重建：项目资产 {0:N0} 条，索引库存在={1}", project.Assets.Count, store.Exists);
        return _catalog;
    }

    /// <summary>一次搜索的结果：列表页与目录树只会出现一样（另一个视图本来就没显示）。</summary>
    private sealed record SearchOutcome(
        IReadOnlyList<AssetRow>? Rows, long Total, long PageIndex, AssetRecord? Revealed);

    /// <summary>
    /// 后台线程取数：列表**只物化要显示的那一页**，目录树**只拿命中序列**（8 字节一条）。
    /// <para>两个视图各取所需、**不同时取**是刻意的：列表要按用户选的排序取一页，目录树要按
    /// 目录序拿整条序列以便分层展开，同时取会让「取一页」的代价白白翻倍。</para>
    /// </summary>
    private SearchOutcome BuildView(AssetCatalog catalog, AssetSearchQuery query, bool treeMode,
        string? revealPath, long requestedPage, int pageSize)
    {
        // 定位目标（深链跳转）先解析成记录：它不在当前页里也要能把右侧预览切过去。
        var revealed = revealPath is null ? null : catalog.Resolve([revealPath]).FirstOrDefault();
        if (treeMode)
        {
            var tree = new AssetCatalogTree(catalog, query);
            return new(null, tree.Count, 0, revealed);
        }
        // 「跳到某资源」优先于页码：先算出它在命中集里是第几条，再翻到它所在的那一页，
        // 否则用户跳过来了却停在别的页上（那一行根本不在列表里）。
        if (revealPath is { } path && catalog.IndexIn(query, path) is { } index)
            requestedPage = index / pageSize;
        var page = catalog.Page(query, requestedPage * (long)pageSize, pageSize);
        // 筛选变化后用户可能停在一个已经不存在的页码上 —— 夹到最后一页，
        // 否则他看到的是空白页，而「共 N 条」明明不为零。
        var pageCount = page.PageCount(pageSize);
        if (requestedPage >= pageCount)
        {
            requestedPage = pageCount - 1;
            page = catalog.Page(query, requestedPage * (long)pageSize, pageSize);
        }
        var rows = new List<AssetRow>(page.Items.Count);
        foreach (var record in page.Items) rows.Add(new AssetRow(record));
        return new(rows, page.TotalCount, requestedPage, revealed);
    }

    /// <summary>后台线程查询 + 代际守卫：过期结果直接丢弃；选中项按 LogicalPath 跨刷新保留。
    /// <paramref name="keepPage"/> = false 时回到第 0 页（筛选变化 / 刷新 / 切页回来都该回第 0 页，
    /// 只有「翻页」这一个动作保留页码）。</summary>
    private async Task RunSearchAsync(bool keepPage = false)
    {
        using var scope = Log.Scope("RunSearchAsync");
        var project = _host.Project;
        if (project is null)
        {
            AssetList.ItemsSource = null; AssetTree.ItemsSource = null;
            _tree = null; _lastTotal = 0; _catalog = null; _catalogProject = null; _catalogAssetCount = -1;
            Log.Debug("搜索跳过：无项目，已清空列表/目录树/目录门面");
            return;
        }
        var generation = ++_searchGeneration;
        var query = BuildSearchQuery();
        var catalog = CatalogFor(project);
        var treeMode = _treeMode;
        var requestedPage = keepPage ? _pageIndex : 0;
        var pageSize = _pageSize;
        // 待定位目标只受理一次：本次搜索必须消费掉它，否则之后的每一次筛选都会被它劫持。
        var revealPath = _pendingRevealLogicalPath;
        _pendingRevealLogicalPath = null;
        Log.Debug("搜索开始：代际={0}，视图={1}，请求第 {2} 页（页大小 {3}），待定位={4}",
            generation, treeMode ? "树形" : "列表", requestedPage + 1, pageSize, revealPath ?? "(无)");
        var started = Stopwatch.GetTimestamp();
        SearchOutcome outcome;
        try
        {
            outcome = await Task.Run(() => BuildView(catalog, query, treeMode, revealPath, requestedPage, pageSize));
        }
        catch (ArgumentException ex)
        {
            Log.Error(ex, "搜索参数被拒绝（语义原样：结果丢弃）：代际={0}", generation);
            return;
        }
        if (generation != _searchGeneration)
        {
            Log.Debug("搜索结果已过期丢弃：本代际={0}，当前代际={1}，命中={2} 条",
                generation, _searchGeneration, outcome.Total);
            return;
        }
        _lastTotal = outcome.Total;
        _pageIndex = outcome.PageIndex;
        // 列表项源赋值会清掉列表选中（触发一次 SelectionChanged(null)）——先把缓冲的选中记下来，
        // 供「它不在本页」时恢复右侧预览（否则翻页会把预览清空）。
        var keepSelection = _selectedAsset;
        if (outcome.Rows is { } rows) AssetList.ItemsSource = rows;
        AssetCountText.Text = outcome.Total == project.Assets.Count
            ? project.Assets.Count.ToString()
            : $"{outcome.Total} / {project.Assets.Count}";
        UpdatePageBar();
        Log.Debug("搜索完成：代际={0}，命中 {1:N0} / 全量 {2:N0} 条，本页 {3} 条，耗时 {4:0.#} ms",
            generation, outcome.Total, project.Assets.Count, outcome.Rows?.Count ?? 0,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (treeMode)
        {
            RebuildTree();
            // 树是惰性物化的：根层重建后还得把目标叶子选中（并展开路径），否则「跳过来」看不见东西。
            // 目标还很深时树里找不到它，此时至少要让右侧预览切过去。
            if (outcome.Revealed is { } revealed)
            {
                ApplyAssetSelection(revealed);
                RestoreTreeSelection(revealed.LogicalPath);
                _host.SetStatus($"已定位到资源：{AssetDisplay.DisplayPath(revealed)}");
                Log.Info("资源页精确跳转命中：{0}", AssetDisplay.DisplayPath(revealed));
            }
        }
        else if (outcome.Revealed is { } revealedRow)
        {
            RestoreListSelection(revealedRow);
            _host.SetStatus($"已定位到资源：{AssetDisplay.DisplayPath(revealedRow)}");
            Log.Info("资源页精确跳转命中：{0}（第 {1} 页）",
                AssetDisplay.DisplayPath(revealedRow), _pageIndex + 1);
        }
        else if (keepSelection is { } keep)
        {
            // 翻转页之后原选中项多半不在本页：列表里没有那一行，但右侧预览必须保持住。
            RestoreListSelection(keep);
        }
    }

    // ── 页码条 ───────────────────────────────────────────────────────

    private static long PageCountOf(long total, int pageSize)
        => pageSize <= 0 ? 1 : Math.Max(1, (total + pageSize - 1) / pageSize);

    /// <summary>按当前命中数与页码重画页码条（不查询、不改变页码来源）。</summary>
    private void UpdatePageBar()
    {
        var pageCount = PageCountOf(_lastTotal, _pageSize);
        if (_pageIndex >= pageCount) _pageIndex = pageCount - 1;
        PageNumberBox.Text = (_pageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        PageCountText.Text = $"/ {pageCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        PageTotalText.Text = $"共 {_lastTotal:N0} 条";
        PageFirstButton.IsEnabled = PagePrevButton.IsEnabled = _pageIndex > 0;
        PageNextButton.IsEnabled = PageLastButton.IsEnabled = _pageIndex + 1 < pageCount;
        // 页码条只服务列表视图：目录树是惰性分层的，没有「第几页」这回事。
        PageBar.Visibility = _treeMode ? Visibility.Collapsed : Visibility.Visible;
    }

    private void GoToPage(long pageIndex)
    {
        var pageCount = PageCountOf(_lastTotal, _pageSize);
        var clamped = Math.Clamp(pageIndex, 0, pageCount - 1);
        if (clamped == _pageIndex) { UpdatePageBar(); return; }
        Log.Info("用户翻页：第 {0} 页 → 第 {1} 页（共 {2} 页，命中 {3:N0} 条）",
            _pageIndex + 1, clamped + 1, pageCount, _lastTotal);
        _pageIndex = clamped;
        _ = RunSearchAsync(keepPage: true);
    }

    private void PageFirst_Click(object sender, RoutedEventArgs e) => GoToPage(0);

    private void PagePrev_Click(object sender, RoutedEventArgs e) => GoToPage(_pageIndex - 1);

    private void PageNext_Click(object sender, RoutedEventArgs e) => GoToPage(_pageIndex + 1);

    private void PageLast_Click(object sender, RoutedEventArgs e)
        => GoToPage(PageCountOf(_lastTotal, _pageSize) - 1);

    /// <summary>页码框：回车才跳转；输入非法就把显示恢复成当前页（不做任何过滤）。</summary>
    private void PageNumberBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        var text = PageNumberBox.Text.Trim();
        if (!long.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 1)
        {
            Log.Debug("页码框输入非法：「{0}」，恢复为当前页", text);
            UpdatePageBar();
            return;
        }
        GoToPage(number - 1);
    }

    private void PageSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        // XAML 解析期间也会触发一次（SelectedIndex 写在 XAML 里），那时筛选还没初始化。
        if (!_filtersInitialized || _host.Project is null) return;
        var index = PageSizeBox.SelectedIndex;
        if (index < 0 || index >= PageSizes.Length) return;
        var size = PageSizes[index];
        if (size == _pageSize) return;
        Log.Info("用户切换每页条数：{0} → {1}（回到第 1 页）", _pageSize, size);
        _pageSize = size;
        _ = RunSearchAsync();
    }

    // ── 选中与预览 ───────────────────────────────────────────────────

    private void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = AssetList.SelectedItem as AssetRow;
        if (Log.IsDebugEnabled) Log.Debug("列表选中变化：{0}（共 {1} 项被选/取消）",
            row is null ? "(无)" : AssetDisplay.DisplayPath(row.Asset), e.AddedItems.Count);
        ApplyAssetSelection(row?.Asset);
    }

    /// <summary>统一的选中处理：列表与目录树两个视图共用（右侧状态、按钮
    /// 可用性、预览都以此为准；右键菜单/双击读取 <see cref="_selectedAsset"/>）。</summary>
    private void ApplyAssetSelection(AssetRecord? asset)
    {
        Log.Info("选中资源：{0}（类型={1}，大小={2}，状态={3}，UnityPathId={4}）",
            asset is null ? "(无)" : AssetDisplay.DisplayPath(asset),
            asset?.Type.ToString() ?? "-", asset?.Size.ToString() ?? "-",
            asset?.EditState.ToString() ?? "-", asset?.UnityPathId?.ToString() ?? "-");
        _selectedAsset = asset;
        SelectedAssetNameText.Text = asset is null ? "未选择" : AssetDisplay.DisplayName(asset);
        SelectedAssetPathText.Text = asset is null ? "—" : AssetDisplay.DisplayPath(asset);
        SelectedAssetTypeText.Text = asset is null ? "—" : AssetDisplay.TypeLabel(asset.Type);
        SelectedAssetSizeText.Text = asset is null
            ? "—"
            : new HumanSizeConverter().Convert(asset.Size, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string;
        SelectedStateText.Text = asset is null ? "—" : AssetDisplay.StateLabel(asset.EditState);
        RefreshSelectionButtons(asset);
        PreviewExpandButton.IsEnabled = asset is not null;
        UpdatePreview(asset);
    }

    /// <summary>把底层记录还原成列表选中行：列表已按 AssetRow 承载，选中行
    /// 必须找到对应的行对象，否则右侧面板不会跟着变。身份键是
    /// <see cref="AssetRecord.LogicalPath"/>（见 <see cref="AssetRow.LogicalPath"/>）。</summary>
    private void RestoreListSelection(AssetRecord asset)
    {
        if (AssetList.ItemsSource is IEnumerable<AssetRow> rows)
        {
            var row = rows.FirstOrDefault(r => string.Equals(r.LogicalPath, asset.LogicalPath, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
            {
                AssetList.SelectedItem = row;
                AssetList.ScrollIntoView(row);
                Log.Debug("恢复列表选中：{0} → 已定位到行对象", AssetDisplay.DisplayPath(asset));
                return;
            }
        }
        Log.Debug("恢复列表选中：{0} 不在当前结果中，改为直接应用选中", AssetDisplay.DisplayPath(asset));
        ApplyAssetSelection(asset);
    }

    /// <summary>Recomputes every per-selection action button from the current
    /// project/selection state; also used after long operations to restore
    /// buttons disabled during the operation.</summary>
    private void RefreshSelectionButtons(AssetRecord? asset)
    {
        var project = _host.Project;
        var hasProjectFile = _host.ProjectFile is not null;
        // File.Exists 是系统调用：同一个选中项原先最多查 5 次（每个按钮各一次），
        // 统一算一次复用。
        var sourceExists = asset is not null && !string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath);
        ReplaceAssetButton.IsEnabled = asset is not null && hasProjectFile;
        EditTextButton.IsEnabled = asset is not null && TextAssetEditService.CanEditText(asset) && hasProjectFile;
        // bundle 内对象的正文在 AssetBundle 容器里，内置编辑器不支持 —— 置灰并说明原因，
        // 否则双击会把整个容器字节当正文读进 WPF 文本框（实测 2.26 MB 二进制排版
        // 26.5 秒 → 界面被 Windows 判「未响应」）。
        var textAssetSelected = asset?.Type is AssetType.Text or AssetType.Json;
        EditTextButton.ToolTip = asset is not null && textAssetSelected && !TextAssetEditService.CanEditText(asset)
            ? TextAssetEditService.BundleAssetHint
            : "文本 / JSON 资源的内置编辑器：改完保存即登记为替换（可撤销，导出时写入模组）。";
        HexPreviewButton.IsEnabled = asset is not null;
        ClearEditsButton.IsEnabled = asset is not null && AssetEditService.HasEdits(asset);
        SpriteMetadataButton.IsEnabled = asset?.Type == AssetType.Sprite &&
            asset.UnityPathId.HasValue && sourceExists && hasProjectFile;
        var unityBacked = asset is not null &&
            (asset.Metadata.ContainsKey("unityBundle") || asset.Metadata.ContainsKey("unitySerializedFile"));
        UnityFieldsButton.IsEnabled = asset?.UnityPathId.HasValue == true &&
            unityBacked && sourceExists && hasProjectFile;
        ReferencersButton.IsEnabled = UnityFieldsButton.IsEnabled;
        ObjectSummaryButton.IsEnabled = asset?.UnityPathId.HasValue == true &&
            (asset.Type is AssetType.Mesh or AssetType.Animation or AssetType.Font) &&
            unityBacked && sourceExists && hasProjectFile;
        var fmodDirectory = _host.Env.EffectiveFmodLibraryDirectory(project);
        var isFsbAudio = asset?.Type == AssetType.Audio &&
            asset.LogicalPath.StartsWith("fsb/", StringComparison.OrdinalIgnoreCase);
        DecodeAudioButton.IsEnabled = isFsbAudio &&
            !string.IsNullOrWhiteSpace(fmodDirectory) &&
            Directory.Exists(fmodDirectory) && hasProjectFile;
        FsbInspectButton.IsEnabled = isFsbAudio && sourceExists && hasProjectFile;
        var isImage = asset?.Type is AssetType.Texture or AssetType.Sprite;
        AtlasPanel.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        SplitAtlasButton.IsEnabled = isImage && hasProjectFile;
        RepackAtlasButton.IsEnabled = isImage && asset?.Metadata.ContainsKey("atlasLayoutPath") == true && hasProjectFile;
    }

    private void UpdatePreview(AssetRecord? asset) => _ = UpdatePreviewAsync(asset);

    /// <summary>异步预览（plan-05 提供者管线）：注册表按类型分派预览形态，代际守卫
    /// 保证快速切换选中项时旧结果不覆盖新选中项；属性区并行异步加载。</summary>
    private async Task UpdatePreviewAsync(AssetRecord? asset)
    {
        using var scope = Log.Scope("UpdatePreviewAsync");
        var generation = ++_previewGeneration;
        ResetPreview();
        if (asset is null) { Log.Debug("预览跳过：无选中资源（代际={0}）", generation); return; }
        Log.Debug("预览开始：代际={0}，资源={1}，类型={2}，LogicalPath={3}，SourcePath={4}",
            generation, AssetDisplay.DisplayPath(asset), asset.Type,
            asset.LogicalPath ?? "-", asset.SourcePath ?? "-");
        AssetPreview preview;
        try
        {
            preview = await Task.Run(() => _previewRegistry.PreviewAsync(asset, null, CancellationToken.None)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (generation != _previewGeneration) { Log.Error(ex, "预览失败（结果已过期丢弃）：资源={0}", AssetDisplay.DisplayPath(asset)); return; }
            Log.Error(ex, "预览失败：资源={0}，类型={1}，LogicalPath={2}",
                AssetDisplay.DisplayPath(asset), asset.Type, asset.LogicalPath ?? "-");
            ApplyPreview(null, $"预览失败：{ex.Message}");
            ClosePreviewWindowAfterRebuild();
            return;
        }
        if (generation != _previewGeneration)
        {
            Log.Debug("预览结果已过期丢弃：本代际={0}，当前代际={1}，资源={2}",
                generation, _previewGeneration, AssetDisplay.DisplayPath(asset));
            return;
        }
        _currentAudio = preview.Audio;
        LogPreviewResult(generation, asset, preview);
        Log.Debug("预览进入构建阶段：代际={0}，形态={1}，窗口宿主={2}", generation, preview.Kind, _previewWindow is null ? "右栏" : "放大窗口");
        var buildTimer = System.Diagnostics.Stopwatch.StartNew();
        // plan-13：预览构建失败不能只留个空白区（用户看到「预览是空的」却没有任何线索）。
        UIElement? view;
        try
        {
            view = WrapPreview(BuildPreviewView(preview), preview.Kind);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "预览视图构建失败：代际={0}，资源={1}，形态={2}", generation, AssetDisplay.DisplayPath(asset), preview.Kind);
            ApplyPreview(null, $"预览显示失败：{ex.Message}");
            return;
        }
        ApplyPreview(view, preview.InfoLine);
        buildTimer.Stop();
        Log.Debug("预览视图已挂载：代际={0}，形态={1}，构建耗时={2} ms，目标={3}，PreviewHost ActualHeight={4}（可用高={5}）",
            generation, preview.Kind, buildTimer.ElapsedMilliseconds,
            _previewWindow is null ? "右栏" : "放大窗口",
            PreviewHost.ActualHeight, ActualHeight);
        _ = UpdatePropertiesAsync(asset, generation);
        // 关联资源板块：与属性区同样「后台读 + 代际守卫」，但走另一条异步链，
        // 互不阻塞（属性失败不影响关联，反之亦然）。
        _ = UpdateRelatedResourcesAsync(asset, generation);
    }

    /// <summary>预览内容的落点（plan-13）：打开「放大预览」窗口时改投到该窗口，
    /// 关窗后自动回到右栏。这样「同一份预览」有两个宿主，不用维护两套构建逻辑。</summary>
    private ContentControl PreviewSurface => _previewWindow?.Surface ?? PreviewHost;

    /// <summary>把预览内容与信息行投到当前宿主（右栏，或「放大预览」窗口）。</summary>
    private void ApplyPreview(UIElement? view, string infoLine)
    {
        if (_previewWindow is { } window)
        {
            window.Surface.Content = view;
            window.InfoText.Text = infoLine;
            return;
        }
        PreviewHost.Content = view;
        PreviewInfoText.Text = infoLine;
    }

    /// <summary>放大窗口打开期间换了资源：先关掉旧内容的窗口再按右栏形态重建，
    /// 否则会留下上一份内容的幽灵窗口。</summary>
    private void ClosePreviewWindowAfterRebuild()
    {
        if (_previewWindow is { } window) window.Close();
    }

    /// <summary>② 预览链路结果留痕：Kind + 内容规模 + 是否截断（不记内容本身）。</summary>
    private static void LogPreviewResult(int generation, AssetRecord asset, AssetPreview preview)
    {
        var textLength = preview.Text?.Length ?? -1;
        var rows = preview.Rows?.Count ?? -1;
        Log.Debug("预览结果：代际={0}，资源={1}，Kind={2}，InfoLine={3}，文本长度={4}，字符/行数={5}，"
            + "音频={6}，图像字节={7}，替换图字节={8}，替换图标签={9}，是否截断见 InfoLine",
            generation, AssetDisplay.DisplayPath(asset), preview.Kind, preview.InfoLine ?? "-",
            textLength, rows,
            preview.Audio is null ? "无" : "有",
            preview.ImagePng?.Length ?? -1,
            preview.AlternateImagePng?.Length ?? -1,
            preview.AlternateLabel ?? "-");
    }

    /// <summary>属性区（plan-01 第 3 步）：后台读取 + 代际守卫；失败只影响本区。</summary>
    private async Task UpdatePropertiesAsync(AssetRecord asset, int generation)
    {
        PropertyInfoText.Text = "正在读取属性…";
        PropertyList.ItemsSource = null;
        try
        {
            var rows = await Task.Run(() => new AssetPropertyService().Describe(asset));
            if (generation != _previewGeneration) return;
            PropertyList.ItemsSource = rows;
            PropertyInfoText.Text = rows.Count == 0
                ? "（没有可显示的属性）"
                : $"{rows.Count} 项 · {AssetDisplay.DisplayPath(asset)}";
            Log.Debug("属性读取完成：代际={0}，资源={1}，条目数={2}",
                generation, AssetDisplay.DisplayPath(asset), rows.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "属性读取失败：代际={0}，资源={1}", generation, AssetDisplay.DisplayPath(asset));
            if (generation == _previewGeneration) PropertyInfoText.Text = $"属性读取失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 「关联资源」板块（plan-11）：反查「当前资源属于哪些人格」。
    ///
    /// <para>数据全部来自派生库 <c>cache/relation-index.db</c>（启动扫描在四个索引库就绪后
    /// 生成，见 <c>PersonaRelationIndexService</c>）—— 这里只做只读反查，不做分析、不建库。
    /// 库没建好时 <see cref="RelationQueryService.DescribeSubjectsForAsset"/> 返回空 + 中文原因，
    /// 面板照常显示原因（而不是空白或异常）。</para>
    ///
    /// <para>与属性区共用同一个 <c>generation</c>（<see cref="_previewGeneration"/>）：
    /// 快速连点不同资源时，过期结果一律丢弃，避免「A 的关联」贴到 B 的面板上。</para>
    /// </summary>
    private async Task UpdateRelatedResourcesAsync(AssetRecord asset, int generation)
    {
        RelationInfoText.Text = "正在查询关联…";
        RelationList.ItemsSource = null;
        try
        {
            var lookup = await Task.Run(() => _relations.DescribeSubjectsForAsset(asset)).ConfigureAwait(true);
            if (generation != _previewGeneration) return;
            RelationList.ItemsSource = lookup.Rows;
            RelationInfoText.Text = lookup.Info;
            if (Log.IsDebugEnabled) Log.Debug("关联资源查询完成：代际={0}，资源={1}，命中对象={2}，说明={3}",
                generation, AssetDisplay.DisplayPath(asset), lookup.Rows.Count, lookup.Info);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "关联资源查询失败：代际={0}，资源={1}", generation, AssetDisplay.DisplayPath(asset));
            if (generation == _previewGeneration)
                RelationInfoText.Text = $"关联资源查询失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 「关联资源」行里的「查看」：把该人格 id 填进搜索框并立刻过滤资源列表。
    ///
    /// <para>为什么按 id 搜就能定位全部资源：人格相关的资源路径里都带这个 5 位 id
    /// （<c>PersonalityVideo/10201.mp4</c>、<c>Sprite/Unit/CG/10201_normal.png</c>、
    /// 语音样本名 <c>voice_faust_10201_1</c> …），所以「按 id 过滤」是这个人格资源的并集，
    /// 不需要为每条链接单独造一个跳转目标。</para>
    /// </summary>
    private void RelationView_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string subjectId } || string.IsNullOrWhiteSpace(subjectId))
        {
            Log.Debug("关联资源「查看」：按钮未携带对象 id，忽略");
            return;
        }
        Log.Info("关联资源「查看」：按人格 id 过滤资源列表，id={0}", subjectId);
        // 统一走宿主跳转（等价于「切到资源页 + 用该关键词过滤」），避免页面自己拼搜索时序。
        _host.ShowWorkbenchSearch(WorkbenchPageKeys.Assets, subjectId);
    }

    /// <summary>
    /// 宿主跳转过来时的过滤入口（<see cref="ISearchableWorkbench"/>）：填搜索框 + 立刻搜。
    /// 不走 300ms 防抖 —— 用户点了「查看」期望立刻看到结果。
    /// </summary>
    public void ApplySearchKeyword(string keyword)
    {
        var text = keyword ?? string.Empty;
        Log.Debug("资源页接受外部关键词过滤：长度={0}", text.Length);
        SearchBox.Text = text;
        _searchTimer.Stop();
        _ = RunSearchAsync();
    }

    /// <summary>
    /// 精确跳转（<see cref="IReferenceRevealable"/>）：载荷第 0 段是资源的<b>容器路径</b>
    /// ——关联图里资源侧的口径就是容器路径。
    ///
    /// <para>为什么还要先过滤：40 万行资源里目标那一行<b>不在当前结果集</b>就没法选中，
    /// 所以顺序是「按路径过滤 → 搜索结果挂上列表后选中」（见
    /// <see cref="_pendingRevealLogicalPath"/> 与 <see cref="RunSearchAsync"/>）。</para>
    /// </summary>
    public bool Reveal(string payload)
    {
        var container = RelationDeepLink.Part(payload, 0);
        if (string.IsNullOrWhiteSpace(container)) return false;

        var asset = _host.Project?.Assets.FirstOrDefault(a => string.Equals(
            AssetDisplay.ContainerEntryPath(a), container, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            Log.Warn("资源页精确跳转：项目里没有容器路径为「{0}」的资源，退化为关键词过滤", container);
            ApplySearchKeyword(container);
            return false;
        }

        _pendingRevealLogicalPath = asset.LogicalPath;
        ApplySearchKeyword(container);
        Log.Debug("资源页精确跳转已受理：{0} → LogicalPath={1}", container, asset.LogicalPath);
        return true;
    }

    // ── 预览视图构建（按 Kind 切换；全部只读，plan-05）──────────────────

    /// <summary>JSON 树预览的行数上限（超过就只给纯文本，见
    /// <see cref="IsJsonTreeWorthBuilding"/>）。</summary>
    private const int JsonTreeMaxSourceLines = 1500;

    private UIElement? BuildPreviewView(AssetPreview preview) => preview.Kind switch
    {
        AssetPreviewKind.Image => BuildImageView(preview),
        AssetPreviewKind.Text => BuildTextView(preview.Text, jsonTree: false),
        AssetPreviewKind.Json => BuildTextView(preview.Text, jsonTree: true),
        AssetPreviewKind.Audio => BuildAudioView(preview.Audio),
        AssetPreviewKind.Rows => BuildRowsView(preview.Rows),
        AssetPreviewKind.Spine => BuildSpineView(preview),
        AssetPreviewKind.Hex => BuildHexView(preview.Text),
        AssetPreviewKind.Message => BuildMessageView(preview.Text),
        _ => null,
    };

    /// <summary>
    /// 预览内容的包装（plan-13：<b>不再外包第二层滚动器</b>）。
    ///
    /// <para>旧实现把预览包进「<c>ScrollViewer MaxHeight=520</c>」，于是：可滚动窗口比外层
    /// 预览行还矮时，外层行只给内容一小条高度、内层滚动器又按 520 撑高 → 内容被裁、滚动条
    /// 也点不到（用户反馈「窗口小的时候看不全、滑不动」）。现在预览宿主
    /// <see cref="PreviewHost"/> 所在的行高由用户拖拽决定（见
    /// <see cref="ApplyPreviewRowHeight"/>），内容自己撑满这一行；各 Kind 自带的滚动器
    /// （文本 / JSON 树 / 键值行 / 十六进制）因此拿到确定视口，一条滚动条就够用。</para>
    /// </summary>
    private UIElement? WrapPreview(UIElement? content, AssetPreviewKind kind)
    {
        if (content is null) { Log.Debug("预览包装：内容为空（Kind={0}），PreviewHost 将保持空", kind); return null; }
        Log.Debug("预览包装：Kind={0}，内容类型={1}，PreviewHost 可用高={2:0}（内容自行撑满该行，不再外包滚动器）",
            kind, content.GetType().Name, PreviewHost.ActualHeight);
        return content;
    }

    /// <summary>图像预览的视口高度兜底（未接入可视化树时的初值；实测高优先）：
    /// 固定高度才能让 ScrollViewer 有确定的可视区，「适应窗口」比例也才有意义。</summary>
    private const double ImagePreviewViewportHeight = 260;

    /// <summary>图像预览视口的最小高度（行很矮时仍然看得见图像）。</summary>
    private const double ImagePreviewViewportFloor = 160;

    /// <summary>小图默认最多放大到几倍（再大就只是糊；用户仍可滚轮继续放大）。</summary>
    private const double ImagePreviewMaxFitScale = 2.0;

    /// <summary>
    /// 图像预览：棋盘格透明底 + <b>默认缩放到合适大小（适应窗口）</b> + 滚轮缩放 +
    /// 拖拽平移 + 双击复位「适应窗口」；有替换时可在「替换图 ↔ 原图」之间切换。
    ///
    /// <para><b>为什么默认要自己算缩放</b>：<c>ScrollViewer</c> 会以「无限尺寸」测量内容，
    /// 所以 <c>Stretch.Uniform</c> 在这里不起作用——图像会按原始像素铺开，超大纹理一进来
    /// 就只能看到左上角。改成「按图片像素显式定尺寸 + <c>LayoutTransform</c> 缩放」：
    /// 默认缩放 = 视口 / 图像（大图缩到看得全、小图最多放大 2 倍），滚轮再在此基础上乘用户倍数。
    /// 用 <c>LayoutTransform</c> 而非 <c>RenderTransform</c>，放大后布局随之变大，
    /// 滚动条与拖拽平移才会生效。</para>
    /// </summary>
    private UIElement BuildImageView(AssetPreview preview)
    {
        var container = new DockPanel { LastChildFill = true };
        var bitmap = preview.ImagePng is null ? null : LoadBitmap(preview.ImagePng);
        Log.Debug("构建图像预览：PNG 字节={0}，解码像素={1}x{2}，固定视口高={3}，替换图={4}",
            preview.ImagePng?.Length ?? -1, bitmap?.PixelWidth ?? -1, bitmap?.PixelHeight ?? -1,
            ImagePreviewViewportHeight, preview.AlternateImagePng is null ? "无" : "有");
        var image = new Image
        {
            Stretch = Stretch.Fill, // 尺寸完全由「像素 × 缩放」决定，不再依赖 Uniform 的自动适配
            Width = bitmap?.PixelWidth ?? 1,
            Height = bitmap?.PixelHeight ?? 1,
            Source = bitmap,
        };
        var zoom = new ScaleTransform(1, 1);
        image.LayoutTransform = zoom;

        var zoomInfo = new TextBlock
        {
            Foreground = WbBrush("WbTextMutedBrush"),
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var scroll = new ScrollViewer
        {
            Background = TryFindResource("Checkerboard") as Brush ?? WbBrush("WbListBrush"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // plan-13：视口不再写死 260 —— 撑满预览行（行高由用户拖拽决定），
            // 只留一个下限保证行很矮时图像仍看得见。
            MinHeight = ImagePreviewViewportFloor,
            Content = image,
        };

        var fitScale = 1.0;       // 适应窗口的比例（视口 / 图像）
        var userScale = 1.0;      // 用户滚轮倍数（1 = 适应窗口）

        void UpdateScalingMode()
        {
            // 放大到 1.5 倍以上时用最近邻：像素纹理放大后要保持硬边（清晰），
            // 缩小时则用高质量重采样，避免细节糊成噪点。
            RenderOptions.SetBitmapScalingMode(image,
                zoom.ScaleX >= 1.5 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        }

        void UpdateZoomInfo()
        {
            if (bitmap is null) { zoomInfo.Text = "没有可显示的图像。"; return; }
            var percent = zoom.ScaleX * 100;
            zoomInfo.Text = Math.Abs(userScale - 1.0) < 0.001
                ? $"适应窗口：{percent:0}%（{bitmap.PixelWidth}×{bitmap.PixelHeight} 像素）· 滚轮缩放 · 拖拽平移 · 双击复位"
                : $"缩放：{percent:0}%（{bitmap.PixelWidth}×{bitmap.PixelHeight} 像素）· 双击复位为适应窗口";
        }

        void ApplyZoom()
        {
            zoom.ScaleX = zoom.ScaleY = fitScale * userScale;
            UpdateScalingMode();
            UpdateZoomInfo();
        }

        void FitToViewport()
        {
            if (bitmap is null) return;
            var viewportWidth = Math.Max(1, scroll.ActualWidth - 2);
            var viewportHeight = Math.Max(1, scroll.ActualHeight - 2);
            var fit = Math.Min(viewportWidth / bitmap.PixelWidth, viewportHeight / bitmap.PixelHeight);
            fitScale = Math.Clamp(fit, 0.02, ImagePreviewMaxFitScale);
            ApplyZoom();
            if (Log.IsDebugEnabled) Log.Debug(
                "图像适应视口：视口={0}x{1}（Actual={2}x{3}），图像={4}x{5}，fit={6:0.###}，最终比例={7:0.###}",
                viewportWidth, viewportHeight, scroll.ActualWidth, scroll.ActualHeight,
                bitmap.PixelWidth, bitmap.PixelHeight, fit, fitScale);
        }

        // 视口尺寸变化（拖分隔条 / 换选中项）时重新适应：用户手动缩放过就不打扰他。
        scroll.SizeChanged += (_, _) =>
        {
            if (Math.Abs(userScale - 1.0) > 0.001) return;
            FitToViewport();
        };
        scroll.Loaded += (_, _) => FitToViewport();

        Point? dragOrigin = null;
        double originHorizontal = 0, originVertical = 0;
        image.MouseLeftButtonDown += (_, e) =>
        {
            dragOrigin = e.GetPosition(scroll);
            originHorizontal = scroll.HorizontalOffset;
            originVertical = scroll.VerticalOffset;
            image.CaptureMouse();
            e.Handled = true;
        };
        image.MouseMove += (_, e) =>
        {
            if (dragOrigin is not { } origin) return;
            var current = e.GetPosition(scroll);
            scroll.ScrollToHorizontalOffset(originHorizontal - (current.X - origin.X));
            scroll.ScrollToVerticalOffset(originVertical - (current.Y - origin.Y));
        };
        image.MouseLeftButtonUp += (_, _) => { dragOrigin = null; image.ReleaseMouseCapture(); };
        scroll.MouseDoubleClick += (_, _) => { userScale = 1.0; FitToViewport(); };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
            userScale = Math.Clamp(userScale * factor, 0.05, 16);
            ApplyZoom();
            e.Handled = true;
        };

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        if (preview.AlternateImagePng is not null)
        {
            var toggle = new ToggleButton
            {
                Content = $"{preview.AlternateLabel ?? "原图"}（勾选切换）",
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 4),
                ToolTip = "在替换后的图与缓存原图之间切换显示。",
            };
            var primary = preview.ImagePng;
            var alternate = preview.AlternateImagePng;
            toggle.Checked += (_, _) => { image.Source = LoadBitmap(alternate); FitToViewport(); };
            toggle.Unchecked += (_, _) => { image.Source = primary is null ? null : LoadBitmap(primary); FitToViewport(); };
            bar.Children.Add(toggle);
        }
        var fitButton = new Button
        {
            Content = "适应窗口",
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 0, 4),
            ToolTip = "把图像缩放到刚好能在预览区里看全（等同双击预览区）。",
        };
        fitButton.Click += (_, _) => { userScale = 1.0; FitToViewport(); };
        bar.Children.Add(fitButton);
        // DockPanel 的顺序必须「顶栏 → 说明文字 → 预览区」：LastChildFill 会让最后一个
        // 子元素吃掉剩余空间，所以承载图像的 ScrollViewer 必须最后加入。
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(zoomInfo, Dock.Bottom);
        container.Children.Add(bar);
        container.Children.Add(zoomInfo);
        container.Children.Add(scroll);
        return container;
    }

    /// <summary>文本预览：行号 + 等宽正文；JSON 可解析时提供「文本 / JSON 树」切换。</summary>
    private UIElement BuildTextView(string? text, bool jsonTree)
    {
        text ??= string.Empty;
        var textView = BuildNumberedText(text);
        // ② 未响应的关键闸门：JSON 树在 UI 线程一次性建到 2000 节点，
        // 静态数据表这类大 JSON 会被这里挡下（退回纯文本）。
        var worthTree = jsonTree && IsJsonTreeWorthBuilding(text);
        Log.Debug("构建文本预览：请求 JSON 树={0}，值得建树={1}（源行数上限={2}），字符数={3}",
            jsonTree, worthTree, JsonTreeMaxSourceLines, text.Length);
        if (!worthTree)
        {
            if (jsonTree && text.Length > 0)
                Log.Debug("JSON 树闸门拦下：行数超过 {0} 行（字符数={1}），退回纯文本预览以避免 UI 线程卡顿",
                    JsonTreeMaxSourceLines, text.Length);
            return textView;
        }
        if (TryBuildJsonTree(text) is not { } tree)
        {
            Log.Warn("JSON 树构建失败（解析异常或超预算）：字符数={0}，已回退纯文本预览", text.Length);
            return textView;
        }

        var panel = new DockPanel { LastChildFill = true };
        var host = new ContentControl { Content = textView };
        var textToggle = new ToggleButton { Content = "文本", IsChecked = true, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4) };
        var treeToggle = new ToggleButton { Content = "JSON 树", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 0, 4) };
        textToggle.Click += (_, _) => { textToggle.IsChecked = true; treeToggle.IsChecked = false; host.Content = textView; };
        treeToggle.Click += (_, _) => { treeToggle.IsChecked = true; textToggle.IsChecked = false; host.Content = tree; };
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(textToggle);
        bar.Children.Add(treeToggle);
        DockPanel.SetDock(bar, Dock.Top);
        panel.Children.Add(bar);
        panel.Children.Add(host);
        return panel;
    }

    /// <summary>
    /// 是否值得为这段 JSON 建树。
    ///
    /// <para><b>为什么要有这个闸门</b>：JSON 树是在 **UI 线程**上一次性构造的
    /// （<see cref="TryBuildJsonTree"/> 最多 2000 个 <c>TreeViewItem</c>，前两层还
    /// <c>IsExpanded=true</c> 立刻参与布局）。静态数据表这类大 JSON（实测单表 3.4 MB、
    /// 截断仍有上万行）会让「点一下预览」变成秒级冻屏甚至未响应。行数超阈值时
    /// 退回纯文本预览（<see cref="BuildNumberedText"/>）并保持可滚动。</para>
    /// </summary>
    private static bool IsJsonTreeWorthBuilding(string text)
    {
        if (text.Length == 0) return false;
        var lines = 1;
        foreach (var character in text)
        {
            if (character != '\n') continue;
            if (++lines > JsonTreeMaxSourceLines) return false;
        }
        return true;
    }

    /// <summary>行号 + 正文：两列都用同一字体/字号的只读 TextBox（行高天然一致），
    /// 由外层 ScrollViewer 统一滚动。
    ///
    /// <para><b>不写死高度</b>：本视图由预览列的 <c>*</c> 行承载，写死 220 会让正文
    /// 永远只有一条窄缝（用户反馈「文本预览框高度太低」）。现在高度由预览列决定，
    /// 行号列与正文列的 <c>AcceptsReturn</c> TextBox 都随视口拉伸，滚动交给外层
    /// ScrollViewer（它已经拿到确定高度，滚动条才会正常出现）。</para></summary>
    private static UIElement BuildNumberedText(string text)
    {
        var lineCount = 1;
        foreach (var character in text) if (character == '\n') lineCount++;
        var numbers = new StringBuilder();
        for (var i = 1; i <= lineCount; i++) numbers.Append(i).Append('\n');
        Log.Debug("构建行号文本预览：字符数={0}，行数={1}，行号列宽预算=Auto", text.Length, lineCount);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var numberBox = new TextBox
        {
            Text = numbers.ToString(),
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = WbBrush("WbTextFaintBrush"),
            FontFamily = MonoFont,
            FontSize = 11,
            TextAlignment = TextAlignment.Right,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Padding = new Thickness(2, 6, 8, 6),
        };
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = WbBrush("WbCodeForegroundBrush"),
            FontFamily = MonoFont,
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Padding = new Thickness(0, 6, 6, 6),
        };
        Grid.SetColumn(box, 1);
        grid.Children.Add(numberBox);
        grid.Children.Add(box);
        return new Border
        {
            Background = WbBrush("WbListBrush"),
            BorderBrush = WbBrush("WbBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer
            {
                Content = grid,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
        };
    }

    /// <summary>JSON 树（惰性上限 2000 节点；解析失败返回 null，回退纯文本）。</summary>
    private static UIElement? TryBuildJsonTree(string text)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException ex)
        {
            Log.Warn(ex, "JSON 树解析失败：字符数={0}，已回退纯文本预览", text.Length);
            return null;
        }

        const int maxNodes = 1500;
        var budget = maxNodes;
        var treeTimer = System.Diagnostics.Stopwatch.StartNew();
        TreeViewItem BuildNode(string label, JsonElement element, int depth)
        {
            var (typeLabel, valueLabel) = element.ValueKind switch
            {
                JsonValueKind.Object => ($"对象 · {element.EnumerateObject().Count()} 键", string.Empty),
                JsonValueKind.Array => ($"数组 · {element.GetArrayLength()} 项", string.Empty),
                JsonValueKind.String => ("字符串", Truncate(element.GetString() ?? string.Empty, 120)),
                JsonValueKind.Number => ("数字", element.GetRawText()),
                JsonValueKind.True or JsonValueKind.False => ("布尔", element.GetRawText()),
                JsonValueKind.Null => ("空", "null"),
                _ => (element.ValueKind.ToString(), element.GetRawText()),
            };
            var item = new TreeViewItem
            {
                Header = string.IsNullOrEmpty(valueLabel) ? $"{label}  ({typeLabel})" : $"{label}  ({typeLabel})  {valueLabel}",
                IsExpanded = depth < 2,
                Tag = label,
            };
            if (budget-- <= 0) return item;
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (budget <= 0) break;
                    item.Items.Add(BuildNode(property.Name, property.Value, depth + 1));
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var child in element.EnumerateArray())
                {
                    if (budget <= 0) break;
                    item.Items.Add(BuildNode($"[{index++}]", child, depth + 1));
                }
            }
            return item;
        }

        var root = BuildNode("$", document.RootElement, 0);
        document.Dispose();
        treeTimer.Stop();
        // ② 未响应线索：这段构造发生在 UI 线程（BuildTextView 调用链）。
        Log.Debug("JSON 树构建完成：字符数={0}，节点数={1}（预算 {2}），是否触顶={3}，UI 线程耗时={4} ms，线程={5}",
            text.Length, maxNodes - Math.Max(0, budget), maxNodes, budget <= 0,
            treeTimer.ElapsedMilliseconds, Environment.CurrentManagedThreadId);
        if (budget <= 0) root.Items.Add(new TreeViewItem { Header = $"… 节点过多，只显示前 {maxNodes} 个" });
        var tree = new TreeView
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = WbBrush("WbCodeForegroundBrush"),
        };
        tree.Items.Add(root);
        return new Border
        {
            Background = WbBrush("WbListBrush"),
            BorderBrush = WbBrush("WbBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = tree, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4) },
        };
    }

    /// <summary>音频预览：试听按钮 + 波形包络（解码 WAV 的 PCM 峰值）。</summary>
    private UIElement BuildAudioView(AssetPreviewAudio? audio)
    {
        var panel = new StackPanel();
        Log.Debug("构建音频预览：PCM 字节={0}，时长={1:0.##} 秒，采样率={2} Hz，声道={3}，包络点={4}，可播放={5}",
            audio?.Wave?.Length ?? -1, audio?.DurationSeconds ?? -1, audio?.SampleRate ?? -1,
            audio?.Channels ?? -1, audio?.Envelope.Count ?? -1, audio?.CanPlay == true);
        if (audio is null)
        {
            panel.Children.Add(new TextBlock { Text = "（没有音频数据）", Foreground = Brushes.Silver });
            return panel;
        }
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        var play = new Button
        {
            Content = _previewPlayer is null ? "▶ 试听" : "■ 停止",
            Padding = new Thickness(12, 5, 12, 5),
            IsEnabled = audio.CanPlay,
            ToolTip = audio.CanPlay ? "解码成 WAV 在本机播放（不写入项目，切选/关窗即停并删除临时文件）。" : audio.UnavailableReason,
        };
        play.Click += async (_, _) => await PlayCurrentAudioAsync();
        bar.Children.Add(play);
        bar.Children.Add(new TextBlock
        {
            Text = audio.CanPlay
                ? $"约 {audio.DurationSeconds:0.##} 秒 · {audio.SampleRate} Hz · {audio.Channels} 声道"
                : audio.UnavailableReason ?? "无法试听",
            Foreground = WbBrush("WbTextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 0, 0, 0),
        });
        panel.Children.Add(bar);
        if (audio.Envelope.Count > 0) panel.Children.Add(BuildWaveform(audio.Envelope));
        return panel;
    }

    /// <summary>波形：每个桶画一条垂直峰线（Polyline，随宽度重算）。</summary>
    private static UIElement BuildWaveform(IReadOnlyList<float> envelope)
    {
        var canvas = new Canvas
        {
            Height = 72,
            Margin = new Thickness(0, 6, 0, 0),
            Background = WbBrush("WbListBrush"),
            ClipToBounds = true,
        };
        var polyline = new System.Windows.Shapes.Polyline
        {
            Stroke = WbBrush("WbAccentBrush"),
            StrokeThickness = 1,
        };
        canvas.Children.Add(polyline);
        canvas.SizeChanged += (_, _) =>
        {
            var width = canvas.ActualWidth;
            var height = canvas.ActualHeight;
            if (width <= 1 || height <= 1) return;
            var middle = height / 2;
            var points = new PointCollection();
            for (var i = 0; i < envelope.Count; i++)
            {
                var x = envelope.Count == 1 ? 0 : i * width / (envelope.Count - 1);
                var amplitude = envelope[i] * (middle - 1);
                points.Add(new Point(x, middle - amplitude));
                points.Add(new Point(x, middle + amplitude));
            }
            polyline.Points = points;
        };
        return canvas;
    }

    /// <summary>键值行预览（摘要卡 / 只读字段树）。</summary>
    private static UIElement BuildRowsView(IReadOnlyList<AssetPreviewRow>? rows)
    {
        Log.Debug("构建键值行预览：行数={0}，固定 MaxHeight=260（行越多越依赖外层滚动）", rows?.Count ?? -1);
        var panel = new StackPanel();
        if (rows is null || rows.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "（无内容）", Foreground = Brushes.Silver });
            return panel;
        }
        foreach (var row in rows)
        {
            var grid = new Grid { Margin = new Thickness(row.Depth * 12, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock
            {
                Text = row.Label,
                Foreground = WbBrush("WbTextMutedBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var value = new TextBlock
            {
                Text = row.Value,
                TextWrapping = TextWrapping.Wrap,
                Foreground = row.Highlight
                    ? WbBrush("WbModifiedBrush")
                    : WbBrush("WbCodeForegroundBrush"),
            };
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
            panel.Children.Add(grid);
        }
        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 260,
            Padding = new Thickness(0, 2, 0, 2),
        };
    }

    /// <summary>
    /// Spine 预览（plan-11 / plan-11 深化）：上面放「图集页 + 区域框」叠加图（有就放），
    /// 下面放结构行（骨架版本 / 画布 / 动画清单 / 图集页区域 / 骨骼表）。
    ///
    /// <para><b>动画播放去哪了</b>：本视图给的是「能确证的事实 + 图集怎么切」；真的把骨架
    /// 画出来播，走上面的「动画预览…」按钮（<see cref="SpineAnimationPreviewWindow"/>，
    /// 渲染由 <c>LimbusModEditor.SpineRuntime</c> 完成）。分成两处是因为这个视图是
    /// <c>AssetPreview</c> 的**只读快照**（没有资源记录、也不该开始跑 30fps 的循环）。</para>
    ///
    /// <para>导出仍走「Spine 资源导出」（卡片流页 / 关联资源区），交给外部 Spine 工具。</para>
    /// </summary>
    private UIElement BuildSpineView(AssetPreview preview)
    {
        var panel = new StackPanel();

        // 动画预览入口：只在当前确实选中了一个资源时给（预览是异步的，选中项可能已经变了）。
        if (_selectedAsset is { } spineAsset)
        {
            var play = WorkbenchShell.CreateButton("▶ 动画预览…", (_, _) => PreviewSpineAnimation(spineAsset));
            play.ToolTip = "用内置 Spine 运行时渲染并播放这个骨架的动画（可切动画 / 拖时间轴）。";
            play.HorizontalAlignment = HorizontalAlignment.Left;
            play.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(play);
        }

        if (preview.ImagePng is not null)
        {
            var bitmap = LoadBitmap(preview.ImagePng);
            Log.Debug("构建 Spine 预览：叠加图 {0}×{1}，结构行 {2} 条",
                bitmap?.PixelWidth ?? -1, bitmap?.PixelHeight ?? -1, preview.Rows?.Count ?? -1);
            panel.Children.Add(new Border
            {
                Background = TryFindResource("Checkerboard") as Brush ?? WbBrush("WbListBrush"),
                BorderBrush = WbBrush("WbBorderBrush"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    MaxHeight = 360,
                    HorizontalAlignment = HorizontalAlignment.Left,
                },
            });
        }
        panel.Children.Add(BuildRowsView(preview.Rows));
        return panel;
    }

    /// <summary>
    /// 打开 Spine 动画预览窗口。素材解析失败（缺骨架 / 缺图集 / 读取失败）时只弹一条说明性
    /// 消息——不让「点一下预览」变成静默无反应。
    /// </summary>
    private void PreviewSpineAnimation(AssetRecord asset)
    {
        var (source, error) = _spineAnimationSource.Resolve(asset);
        if (source is null)
        {
            Log.Warn("Spine 动画预览不可用：{0}（{1}）", error, AssetDisplay.DisplayPath(asset));
            MessageBox.Show(Window.GetWindow(this), error ?? "无法解析该 Spine 资源的动画素材。",
                "Spine 动画预览", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SpineAnimationPreviewWindow.ShowFor(Window.GetWindow(this), source);
    }

    private static UIElement BuildHexView(string? text)
    {
        Log.Debug("构建十六进制预览：字符数={0}，固定 Height=200", text?.Length ?? -1);
        return new Border
        {
            Background = WbBrush("WbListBrush"),
            BorderBrush = WbBrush("WbBorderBrush"),
            BorderThickness = new Thickness(1),
            Height = 200,
            Child = new TextBox
            {
                Text = text ?? string.Empty,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = WbBrush("WbCodeForegroundBrush"),
                FontFamily = MonoFont,
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(6),
            },
        };
    }

    private static UIElement BuildMessageView(string? text)
    {
        Log.Debug("构建说明文字预览：字符数={0}", text?.Length ?? -1);
        return new TextBlock
        {
            Text = text ?? string.Empty,
            TextWrapping = TextWrapping.Wrap,
            Foreground = WbBrush("WbTextSecondaryBrush"),
            LineHeight = 18,
        };
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// 取共享色板画笔（plan-09 收口：资源页原先在 C# 里硬编码 <c>Color.FromRgb</c> 色值，
    /// 现统一走 <c>Themes/WorkbenchStyles.xaml</c> 的具名画刷，色值只在那一处出现）。
    /// 取不到时退回不透明黑：宁可观感退化也不抛异常（与 <c>WorkbenchShell.TryFindStyle</c> 同策略）。
    /// </summary>
    private static Brush WbBrush(string key)
        => System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Black;

    private static readonly FontFamily MonoFont = new("Consolas");

    /// <summary>清空预览区（切换选中项 / 无选中时调用），并停止正在播放的试听。
    /// plan-13：右栏与「放大预览」窗口两个宿主一起清（否则关窗后会留下上一份内容）。</summary>
    private void ResetPreview()
    {
        if (Log.IsDebugEnabled) Log.Debug("清空预览区：原 PreviewHost 内容={0}，窗口内容={1}，代际={2}",
            PreviewHost.Content?.GetType().Name ?? "(空)",
            _previewWindow?.Surface.Content?.GetType().Name ?? "(无窗口)", _previewGeneration);
        PreviewHost.Content = null;
        if (_previewWindow is not null)
        {
            // 窗口已打开：只在窗口内重建（信息行也在窗口里更新），避免把内容投回右栏。
            ApplyPreview(null, "无预览");
        }
        else
        {
            PreviewInfoText.Text = "无预览";
        }
        PropertyList.ItemsSource = null;
        PropertyInfoText.Text = "—";
        // 关联资源板块也随选中项清空（异步结果按代际守卫丢弃，见 UpdateRelatedResourcesAsync）。
        RelationList.ItemsSource = null;
        RelationInfoText.Text = "—";
        _currentAudio = null;
        StopAudioPreview();
    }

    // ── plan-13：「放大预览」独立窗口（主窗小也能大尺寸查看/编辑） ──────

    /// <summary>「⤢ 放大」：把预览投到大窗口。同一份预览视图，不复制构建逻辑；
    /// 关窗后自动回到右栏。已打开时只激活。</summary>
    private void PreviewExpand_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAsset is null) { Log.Debug("放大预览：当前没有选中资源，忽略"); return; }
        if (_previewWindow is { } existing)
        {
            existing.Activate();
            Log.Debug("放大预览：窗口已存在，仅激活（资源={0}）", AssetDisplay.DisplayPath(_selectedAsset));
            return;
        }
        var window = new PreviewWindow(OwnerWindow, AssetDisplay.DisplayPath(_selectedAsset));
        window.Closed += (_, _) => OnPreviewWindowClosed(window);
        window.Closing += (_, _) =>
        {
            // 窗口自己的预览元素随窗口销毁：声音与控件在这里收干净，再让右栏接管。
            window.Surface.Content = null;
            StopAudioPreview();
        };
        _previewWindow = window;
        Log.Info("打开放大预览窗口：资源={0}", AssetDisplay.DisplayPath(_selectedAsset));
        window.Show();                    // 非模态：主窗仍可继续浏览/选择
        UpdatePreview(_selectedAsset);    // 立刻把当前预览投到窗口
    }

    private void OnPreviewWindowClosed(PreviewWindow window)
    {
        if (!ReferenceEquals(_previewWindow, window)) return;
        _previewWindow = null;
        // 试听的声音跟着窗口走（窗口关了就不该还在响）。
        StopAudioPreview();
        Log.Info("关闭放大预览窗口：预览回到右栏");
        UpdatePreview(_selectedAsset);    // 右栏重新接管预览（原内容随窗口一起销毁了）
    }

    /// <summary>试听当前预览的音频：把已解码的 WAV 写成临时文件交给 MediaPlayer；
    /// 再点一次停止；切换选中项或关闭窗口时自动停止并删除临时文件。</summary>
    private async Task PlayCurrentAudioAsync()
    {
        var audio = _currentAudio;
        Log.Info("用户点击试听：音频数据={0}，PCM 字节={1}，当前播放器={2}",
            audio is null ? "无" : "有", audio?.Wave?.Length ?? -1,
            _previewPlayer is null ? "空闲" : "播放中");
        if (audio?.Wave is not { Length: > 0 })
        {
            _host.SetStatus(audio?.UnavailableReason ?? "当前资源没有可播放的音频数据");
            return;
        }
        if (_previewPlayer is not null)
        {
            StopAudioPreview();
            _host.SetStatus("已停止试听");
            return;
        }
        var generation = _previewGeneration;
        var playTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var file = Path.Combine(Path.GetTempPath(), $"lme-preview-{Guid.NewGuid():N}.wav");
            await File.WriteAllBytesAsync(file, audio.Wave);
            if (generation != _previewGeneration) { TryDelete(file); return; }
            var player = new System.Windows.Media.MediaPlayer();
            player.MediaEnded += (_, _) => EndAudioPreview(player);
            player.MediaFailed += (_, args) =>
            {
                EndAudioPreview(player);
                _host.SetStatus($"播放失败：{args.ErrorException?.Message ?? "未知原因"}");
            };
            player.Open(new Uri(file));
            _previewPlayer = player;
            _previewAudioFile = file;
            player.Play();
            playTimer.Stop();
            Log.Info("试听已开始：临时 WAV={0}，字节={1}，准备耗时={2} ms",
                file, audio.Wave.Length, playTimer.ElapsedMilliseconds);
            _host.SetStatus($"正在播放（WAV {audio.Wave.Length / 1024} KB）");
        }
        catch (Exception ex)
        {
            StopAudioPreview();
            _host.SetStatus($"试听失败：{ex.Message}");
            Log.Error(ex, "试听失败：PCM 字节={0}，代际={1}", audio.Wave?.Length ?? -1, generation);
        }
    }

    private void EndAudioPreview(System.Windows.Media.MediaPlayer player)
    {
        if (!ReferenceEquals(_previewPlayer, player)) return; // 已经停止 / 换成另一个资源了
        StopAudioPreview();
        _host.SetStatus("播放结束");
        Log.Debug("试听自然结束（MediaEnded）");
    }

    private void StopAudioPreview()
    {
        var player = _previewPlayer;
        _previewPlayer = null;
        if (player is not null)
        {
            try { player.Stop(); player.Close(); }
            catch (Exception ex) { Log.Debug(ex, "停止试听时播放器已释放（无害，语义原样忽略）"); }
        }
        var file = _previewAudioFile;
        _previewAudioFile = null;
        if (file is not null) Log.Debug("试听停止：已释放播放器并清理临时 WAV {0}", file);
        TryDelete(file);
    }

    private static void TryDelete(string? file)
    {
        if (file is null) return;
        try { File.Delete(file); }
        catch (Exception ex) { Log.Debug(ex, "删除预览临时文件失败（无害，留给系统清理）：{0}", file); }
    }

    private static BitmapImage LoadBitmap(byte[] png)
    {
        Log.Debug("解码预览位图：PNG 字节={0}", png.Length);
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(png);
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }

    // ── 编辑操作 ─────────────────────────────────────────────────────

    private async void SplitAtlas_Click(object sender, RoutedEventArgs e)
    {
        using var scope = Log.Scope("SplitAtlas_Click");
        var project = _host.Project;
        if (project is null || _host.ProjectFile is null || _selectedAsset is not AssetRecord asset) return;
        if (!TryGetImageSource(asset, out var source)) { _host.SetStatus("当前资源没有可读取的本地图像文件"); Log.Warn("拆分图集中止：资源没有可读取的本地图像文件，资源={0}", AssetDisplay.DisplayPath(asset)); return; }
        if (!int.TryParse(AtlasColumnsBox.Text, out var columns) || !int.TryParse(AtlasRowsBox.Text, out var rows) || columns <= 0 || rows <= 0)
        { _host.SetStatus("列数和行数必须是正整数"); Log.Warn("拆分图集中止：行列输入无效（列={0}，行={1}）", AtlasColumnsBox.Text ?? "-", AtlasRowsBox.Text ?? "-"); return; }
        Log.Info("用户拆分图集：资源={0}，来源={1}，列={2}，行={3}", AssetDisplay.DisplayPath(asset), source, columns, rows);
        try
        {
            var result = await _atlasEdits.SplitAsync(project, asset.AssetId, source, Path.GetDirectoryName(_host.ProjectFile)!, columns, rows);
            await _host.SaveProjectAsync();
            RepackAtlasButton.IsEnabled = true;
            Log.Info("图集拆分完成：区域数={0}", result.Layout.Regions.Count);
            _host.SetStatus($"图集已拆分：{result.Layout.Regions.Count} 个区域");
        }
        catch (Exception ex) { Log.Error(ex, "拆分图集失败：资源={0}", AssetDisplay.DisplayPath(asset)); ShowError("拆分图集失败", ex); }
    }

    private async void RepackAtlas_Click(object sender, RoutedEventArgs e)
    {
        using var scope = Log.Scope("RepackAtlas_Click");
        var project = _host.Project;
        if (project is null || _host.ProjectFile is null || _selectedAsset is not AssetRecord asset) return;
        Log.Info("用户恢复图集：资源={0}", AssetDisplay.DisplayPath(asset));
        try
        {
            await _atlasEdits.RepackAsync(project, asset, Path.GetDirectoryName(_host.ProjectFile)!);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState("图集已恢复并记录替换");
            RestoreListSelection(asset);
        }
        catch (Exception ex) { Log.Error(ex, "恢复图集失败：资源={0}", AssetDisplay.DisplayPath(asset)); ShowError("恢复图集失败", ex); }
    }

    private static bool TryGetImageSource(AssetRecord asset, out string path)
    {
        path = asset.Metadata.TryGetValue("replacementPath", out var replacement) && File.Exists(replacement)
            ? replacement
            : asset.SourcePath ?? string.Empty;
        return File.Exists(path) && ImagePreviewService.IsSupportedExtension(Path.GetExtension(path));
    }

    private async void ReplaceAsset_Click(object sender, RoutedEventArgs e)
    {
        var project = _host.Project;
        if (project is null || _host.ProjectFile is null || _selectedAsset is not AssetRecord asset) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = asset.Type switch
            {
                AssetType.Texture or AssetType.Sprite => "图像文件 (*.png;*.jpg;*.jpeg;*.tga)|*.png;*.jpg;*.jpeg;*.tga|所有文件 (*.*)|*.*",
                AssetType.Audio => "音频文件 (*.wav;*.fsb)|*.wav;*.fsb|所有文件 (*.*)|*.*",
                _ => "所有文件 (*.*)|*.*"
            },
            Title = $"替换资源：{AssetDisplay.DisplayPath(asset)}"
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        Log.Info("用户替换资源：资源={0}，来源文件={1}", AssetDisplay.DisplayPath(asset), dialog.FileName);
        try
        {
            await _assetEdits.ReplaceFromFileAsync(project, asset.AssetId, dialog.FileName, Path.GetDirectoryName(_host.ProjectFile)!);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState("资源替换已记录");
            RestoreListSelection(asset);
        }
        catch (Exception ex) { Log.Error(ex, "替换资源失败：资源={0}，来源文件={1}", AssetDisplay.DisplayPath(asset), dialog.FileName); ShowError("替换资源失败", ex); }
    }

    /// <summary>撤销选中资源上的全部修改（替换文件 / Unity 字段 / Sprite
    /// 元数据），资源还原为未修改状态；导出不再包含它。</summary>
    private async void ClearEdits_Click(object sender, RoutedEventArgs e)
    {
        var project = _host.Project;
        if (project is null || _host.ProjectFile is null || _selectedAsset is not AssetRecord asset) return;
        if (!AssetEditService.HasEdits(asset)) { _host.SetStatus("此资源没有可撤销的修改"); return; }
        var confirm = MessageBox.Show(OwnerWindow,
            $"撤销资源 {AssetDisplay.DisplayPath(asset)} 上的全部修改？\n\n替换文件、Unity 字段、Sprite 元数据会被清除；导出将不再包含此资源的修改。",
            "撤销修改", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        Log.Info("用户撤销资源全部修改：资源={0}", AssetDisplay.DisplayPath(asset));
        try
        {
            var cleared = _assetEdits.ClearEdits(project, asset.AssetId, Path.GetDirectoryName(_host.ProjectFile)!);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState(cleared ? "已撤销此资源的全部修改" : "此资源没有可撤销的修改");
            RestoreListSelection(asset);
            Log.Debug("撤销修改结果：资源={0}，是否实际清除={1}", AssetDisplay.DisplayPath(asset), cleared);
        }
        catch (Exception ex) { Log.Error(ex, "撤销修改失败：资源={0}", AssetDisplay.DisplayPath(asset)); ShowError("撤销修改失败", ex); }
    }

    /// <summary>双击资源 = 按类型做最常用的事（列表与目录树共用：
    /// <see cref="ActivateDefaultAction"/>)。</summary>
    private void AssetList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_selectedAsset is not AssetRecord asset) return;
        Log.Info("用户双击列表行：资源={0}（类型={1}）", AssetDisplay.DisplayPath(asset), asset.Type);
        ActivateDefaultAction(asset);
    }

    /// <summary>按类型做最常用的事：图像 → 替换；文本/JSON → 内置文本编辑器；
    /// 其余 → Unity 字段编辑（可用时），否则十六进制预览。</summary>
    private void ActivateDefaultAction(AssetRecord asset)
    {
        if (asset.Type is AssetType.Texture or AssetType.Sprite && ReplaceAssetButton.IsEnabled)
        {
            Log.Debug("默认动作：图像资源 → 替换（替换按钮可用）");
            ReplaceAsset_Click(this, new RoutedEventArgs());
            return;
        }
        if (asset.Type is AssetType.Text or AssetType.Json && EditTextButton.IsEnabled)
        {
            Log.Debug("默认动作：文本/JSON 资源 → 内置文本编辑器");
            EditTextAsset_Click(this, new RoutedEventArgs());
            return;
        }
        if (UnityFieldsButton.IsEnabled) { Log.Debug("默认动作：Unity 字段编辑（按钮可用）"); EditUnityFields_Click(this, new RoutedEventArgs()); return; }
        if (HexPreviewButton.IsEnabled) { Log.Debug("默认动作：十六进制预览（兜底分支）"); HexPreview_Click(this, new RoutedEventArgs()); return; }
        Log.Warn("默认动作：没有任何可执行动作（资源={0}，类型={1}，替换可用={2}，文本可用={3}，Unity 可用={4}，Hex 可用={5}）",
            AssetDisplay.DisplayPath(asset), asset.Type, ReplaceAssetButton.IsEnabled,
            EditTextButton.IsEnabled, UnityFieldsButton.IsEnabled, HexPreviewButton.IsEnabled);
    }

    /// <summary>文本 / JSON 资源的内置编辑器（P3.10）：改完保存即登记为替换，
    /// 走与替换文件同一条可撤销 / 可导出管道；JSON 保存前校验并格式化。</summary>
    private async void EditTextAsset_Click(object sender, RoutedEventArgs e)
    {
        using var scope = Log.Scope("EditTextAsset_Click");
        var project = _host.Project;
        if (project is null || _host.ProjectFile is null) { _host.SetStatus("请先创建或打开项目"); return; }
        if (_selectedAsset is not AssetRecord asset) return;
        if (asset.Type is not (AssetType.Text or AssetType.Json))
        {
            _host.SetStatus("当前资源不是文本 / JSON 资源；可以用「替换…」换掉整个文件。");
            Log.Warn("编辑文本中止：资源类型不是文本/JSON，资源={0}，类型={1}",
                AssetDisplay.DisplayPath(asset), asset.Type);
            return;
        }
        Log.Info("用户打开文本编辑器：资源={0}，类型={1}", AssetDisplay.DisplayPath(asset), asset.Type);
        try
        {
            var service = new TextAssetEditService(_assetEdits);
            var document = await service.OpenAsync(project, asset);
            var window = new TextAssetEditorWindow(document, AssetDisplay.DisplayPath(asset)) { Owner = OwnerWindow };
            if (window.ShowDialog() != true || window.Result is not { } edited)
            {
                Log.Debug("文本编辑器取消/无结果：资源={0}", AssetDisplay.DisplayPath(asset));
                return;
            }
            await service.SaveAsync(project, edited, Path.GetDirectoryName(_host.ProjectFile)!);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState("文本修改已记录");
            RestoreListSelection(asset);
        }
        catch (NotSupportedException ex)
        {
            // 不是故障，是能力边界：给出中文原因和替代做法，不弹「失败」吓人。
            Log.Warn("文本编辑器拒绝该资源：资源={0}，原因={1}", AssetDisplay.DisplayPath(asset), ex.Message);
            _host.SetStatus(ex.Message);
            MessageBox.Show(OwnerWindow, ex.Message, "此资源不能用内置文本编辑器", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Log.Error(ex, "编辑文本失败：资源={0}", AssetDisplay.DisplayPath(asset)); ShowError("编辑文本失败", ex); }
    }

    /// <summary>右键菜单跟随光标：右键落在某行上时先选中该行。</summary>
    private void AssetList_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TryFindAncestor<ListViewItem>(e.OriginalSource as System.Windows.DependencyObject) is { } row)
            row.IsSelected = true;
    }

    /// <summary>目录树同理：右键落在某个节点上时先选中它（菜单里的操作都以
    /// 当前选中资源为准，不选中就会出现「点了替换却作用在上一个资源」）。</summary>
    private void AssetTree_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TryFindAncestor<TreeViewItem>(e.OriginalSource as System.Windows.DependencyObject) is { } node)
            node.IsSelected = true;
    }

    private static T? TryFindAncestor<T>(System.Windows.DependencyObject? from) where T : class
    {
        while (from is not null)
        {
            if (from is T match) return match;
            from = System.Windows.Media.VisualTreeHelper.GetParent(from);
        }
        return null;
    }

    private void CopyAssetPath_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAsset is not AssetRecord asset) return;
        try { Clipboard.SetText(AssetDisplay.DisplayPath(asset)); _host.SetStatus("已复制资源路径"); Log.Debug("已复制资源路径到剪贴板：{0}", AssetDisplay.DisplayPath(asset)); }
        catch (Exception ex) { Log.Debug(ex, "复制资源路径失败（剪贴板被占用，无害）：{0}", AssetDisplay.DisplayPath(asset)); _host.SetStatus("复制失败（剪贴板被其他程序占用）"); }
    }

    private async void BatchReplace_Click(object sender, RoutedEventArgs e)
    {
        using var scope = Log.Scope("BatchReplace_Click");
        var project = _host.Project;
        if (project is null || _host.ProjectFile is null) { _host.SetStatus("请先创建或打开项目"); return; }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = false,
            ValidateNames = false,
            FileName = "选择此文件夹",
            Title = "选择替换文件所在文件夹（按文件名匹配资源）"
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        var folder = Path.GetDirectoryName(dialog.FileName);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        Log.Info("用户批量登记替换：文件夹={0}", folder);
        try
        {
            var report = await _assetEdits.BatchReplaceFromDirectoryAsync(project, folder, Path.GetDirectoryName(_host.ProjectFile)!);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState($"批量替换已登记（{report.Matched} 个）");
            Log.Info("批量替换完成：匹配={0}，无对应资源的文件={1}", report.Matched, report.FilesWithoutAsset.Count);
            var detail = report.Describe();
            if (report.FilesWithoutAsset.Count > 0)
                detail += "\n\n没有对应资源的文件（前 15 个）：\n" + string.Join("\n", report.FilesWithoutAsset.Take(15));
            MessageBox.Show(OwnerWindow, detail, "批量登记替换", MessageBoxButton.OK,
                report.Matched > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { Log.Error(ex, "批量登记替换失败：文件夹={0}", folder); ShowError("批量登记替换失败", ex); }
    }

    private void HexPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAsset is not AssetRecord asset) return;
        Log.Info("用户打开十六进制预览：资源={0}", AssetDisplay.DisplayPath(asset));
        try { new HexPreviewWindow(asset) { Owner = OwnerWindow }.ShowDialog(); }
        catch (Exception ex) { Log.Error(ex, "十六进制预览失败：资源={0}", AssetDisplay.DisplayPath(asset)); ShowError("十六进制预览失败", ex); }
    }

    private async void InspectSprite_Click(object sender, RoutedEventArgs e)
    {
        var project = _host.Project;
        if (_selectedAsset is not AssetRecord asset || asset.Type != AssetType.Sprite ||
            !asset.UnityPathId.HasValue || string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath)) return;
        Log.Info("用户查看 Sprite 元数据：资源={0}，UnityPathId={1}",
            AssetDisplay.DisplayPath(asset), asset.UnityPathId?.ToString() ?? "-");
        try
        {
            var sprite = new LimbusModEditor.Formats.Unity.UnityAssetService().ReadSprite(asset.SourcePath!, asset.UnityPathId!.Value);
            if (sprite is null) { _host.SetStatus("未找到 Sprite 对象。"); Log.Warn("Sprite 元数据未找到对象：资源={0}", AssetDisplay.DisplayPath(asset)); return; }
            var initial = _spriteEdits.ReadStored(asset) ?? UnitySpriteMetadata.From(sprite);
            var dialog = new SpriteMetadataWindow(initial) { Owner = OwnerWindow };
            if (dialog.ShowDialog() != true || dialog.Result is null) return;
            _spriteEdits.Set(project!, asset, dialog.Result);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState("Sprite 元数据修改已记录");
            RestoreListSelection(asset);
        }
        catch (Exception ex) { Log.Error(ex, "读取 Sprite 元数据失败：资源={0}", AssetDisplay.DisplayPath(asset)); ShowError("读取 Sprite 元数据失败", ex); }
    }

    private async void EditUnityFields_Click(object sender, RoutedEventArgs e)
    {
        var project = _host.Project;
        if (project is null || _host.ProjectFile is null || _selectedAsset is not AssetRecord asset ||
            !asset.UnityPathId.HasValue || string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath)) return;
        UnityFieldsButton.IsEnabled = false;
        Log.Info("用户打开 Unity 字段编辑：资源={0}，PathId={1}，来源={2}，容器={3}",
            AssetDisplay.DisplayPath(asset), asset.UnityPathId?.ToString() ?? "-",
            asset.SourcePath ?? "-", asset.ContainerPath ?? "-");
        _host.SetStatus("正在读取对象字段…");
        var readTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Field tree, script info, dependencies and the in-file object scan
            // all walk the whole serialized file / bundle; run them off the UI
            // thread so large bundles do not freeze the window.
            var service = new LimbusModEditor.Formats.Unity.UnityAssetService();
            var isBundle = asset.Metadata.ContainsKey("unityBundle") && !string.IsNullOrWhiteSpace(asset.ContainerPath);
            var source = asset.SourcePath!;
            var container = asset.ContainerPath;
            var pathId = asset.UnityPathId!.Value;
            var stored = _unityFieldEdits.ReadStored(asset);
            var (fields, scriptInfo, dependencies, inFileObjects) = await Task.Run(() =>
            {
                var root = isBundle
                    ? service.ReadBundleObjectFields(source!, container!, pathId)
                    : service.ReadObjectFields(source!, pathId);
                LimbusModEditor.Formats.Unity.UnityScriptInfo? script = null;
                try
                {
                    script = isBundle
                        ? service.ReadBundleObjectScriptInfo(source, container!, pathId)
                        : service.ReadObjectScriptInfo(source, pathId);
                }
                catch (Exception ex) { Log.Debug(ex, "读取 Unity 脚本信息失败（非 MonoBehaviour 属正常，语义原样忽略）：Path={0}", pathId); }
                IReadOnlyList<LimbusModEditor.Formats.Unity.UnityDependency>? deps = null;
                try
                {
                    deps = isBundle
                        ? service.ReadBundleObjectDependencies(source, container!, pathId)
                        : service.ReadObjectDependencies(source, pathId);
                }
                catch (Exception ex) { Log.Debug(ex, "读取 Unity 依赖失败（尽力而为，语义原样忽略）：Path={0}", pathId); }
                IReadOnlyList<AssetRecord>? objects = null;
                try
                {
                    var scanned = isBundle ? service.ScanBundle(source) : service.ScanSerializedFile(source);
                    objects = scanned
                        .Where(x => x.UnityPathId.HasValue && (!isBundle || string.Equals(x.ContainerPath, container, StringComparison.OrdinalIgnoreCase)))
                        .ToArray();
                }
                catch (Exception ex) { Log.Debug(ex, "扫描文件内对象失败（对象选择器尽力而为，语义原样忽略）：Path={0}", pathId); }
                return (root, script, deps, objects);
            });
            readTimer.Stop();
            Log.Debug("Unity 字段读取完成：PathId={0}，isBundle={1}，耗时={2} ms，依赖={3}，同文件对象={4}，脚本信息={5}",
                pathId, isBundle, readTimer.ElapsedMilliseconds,
                dependencies?.Count ?? -1, inFileObjects?.Count ?? -1, scriptInfo is null ? "无" : "有");
            var dialog = new UnityFieldEditorWindow(fields, stored, scriptInfo, dependencies, inFileObjects) { Owner = OwnerWindow };
            if (dialog.ShowDialog() != true || dialog.Result is null) return;
            if (dialog.Result.Count == 0) return;
            _unityFieldEdits.Set(project, asset, fields, dialog.Result);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState("Unity 字段修改已记录");
            RestoreListSelection(asset);
        }
        catch (Exception ex) { Log.Error(ex, "Unity 字段读取或保存失败：资源={0}，PathId={1}", AssetDisplay.DisplayPath(asset), asset.UnityPathId?.ToString() ?? "-"); ShowError("Unity 字段读取或保存失败", ex); }
        finally
        {
            RefreshSelectionButtons(_selectedAsset);
            _host.SetStatus("就绪");
        }
    }

    /// <summary>P2.1: read-only FSB5 structural inspection for the selected
    /// Bank audio entry; unknown/encrypted payloads are explained in-window.</summary>
    private async void InspectFsb_Click(object sender, RoutedEventArgs e)
    {
        if (_host.Project is null || _selectedAsset is not AssetRecord asset ||
            string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath)) return;
        Log.Info("用户查看 FSB 结构：资源={0}，来源={1}", AssetDisplay.DisplayPath(asset), asset.SourcePath ?? "-");
        try
        {
            var inspection = await new BankAudioService().InspectFsbAsync(asset);
            Log.Debug("FSB 结构读取完成：资源={0}", AssetDisplay.DisplayPath(asset));
            new BankInspectorWindow(asset, inspection) { Owner = OwnerWindow }.ShowDialog();
        }
        catch (Exception ex) { Log.Error(ex, "FSB 结构检查失败：资源={0}", AssetDisplay.DisplayPath(asset)); ShowError("FSB 结构检查失败", ex); }
    }

    /// <summary>Answers "who points at this object" for the selected Unity
    /// object: same-file referencers plus, for bundles, cross-file referencers
    /// from every other SerializedFile inside the same bundle. The scan runs
    /// off the UI thread because it walks every object in the file/bundle.</summary>
    private async void FindReferencers_Click(object sender, RoutedEventArgs e)
    {
        if (_host.Project is null || _selectedAsset is not AssetRecord asset ||
            !asset.UnityPathId.HasValue || string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath)) return;
        ReferencersButton.IsEnabled = false;
        Log.Info("用户查找引用者：资源={0}，PathId={1}，来源={2}，容器={3}",
            AssetDisplay.DisplayPath(asset), asset.UnityPathId?.ToString() ?? "-",
            asset.SourcePath ?? "-", asset.ContainerPath ?? "-");
        _host.SetStatus("正在扫描引用者…");
        var refTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var service = new LimbusModEditor.Formats.Unity.UnityAssetService();
            var isBundle = asset.Metadata.ContainsKey("unityBundle") && !string.IsNullOrWhiteSpace(asset.ContainerPath);
            var pathId = asset.UnityPathId!.Value;
            var source = asset.SourcePath!;
            var container = asset.ContainerPath;
            var referencers = await Task.Run(() => isBundle
                ? service.FindBundleReferencers(source!, container!, pathId)
                : service.FindReferencers(source!, pathId));
            refTimer.Stop();
            Log.Debug("引用者扫描完成：PathId={0}，isBundle={1}，引用者={2}，耗时={3} ms",
                pathId, isBundle, referencers.Count, refTimer.ElapsedMilliseconds);
            if (referencers.Count == 0)
            {
                MessageBox.Show(OwnerWindow,
                    $"没有发现任何对象引用 Path {pathId}（{asset.Type}）。\n修改或替换它不会破坏其他对象。",
                    "引用者检查", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var lines = referencers
                .OrderBy(r => r.SourcePathId)
                .Select(r => (r.OriginatingFile is { } file ? $"[{file}] " : string.Empty) +
                             $"Path {r.SourcePathId}（{r.SourceTypeName ?? "未知类型"}）字段 {r.FieldPath}");
            MessageBox.Show(OwnerWindow,
                $"有 {referencers.Count} 个指针引用 Path {pathId}（{asset.Type}）：\n\n" +
                string.Join(Environment.NewLine, lines) +
                "\n\n修改此对象前请确认这些指针仍然有效；把指针改成空引用是允许的，改成不存在的 Path ID 会在保存时被拒绝。",
                "引用者检查", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { Log.Error(ex, "引用者检查失败：资源={0}", AssetDisplay.DisplayPath(asset)); ShowError("引用者检查失败", ex); }
        finally
        {
            RefreshSelectionButtons(_selectedAsset);
            _host.SetStatus("就绪");
        }
    }

    /// <summary>P1.5: read-only structural summary of the selected Mesh /
    /// AnimationClip / Font object; values come from the type tree, missing
    /// fields are reported instead of guessed.</summary>
    private async void ShowObjectSummary_Click(object sender, RoutedEventArgs e)
    {
        if (_host.Project is null || _selectedAsset is not AssetRecord asset ||
            !asset.UnityPathId.HasValue || string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath)) return;
        var service = new LimbusModEditor.Formats.Unity.UnityAssetService();
        var isBundle = asset.Metadata.ContainsKey("unityBundle") && !string.IsNullOrWhiteSpace(asset.ContainerPath);
        var source = asset.SourcePath;
        var container = asset.ContainerPath;
        var pathId = asset.UnityPathId.Value;
        ObjectSummaryButton.IsEnabled = false;
        Log.Info("用户查看对象摘要：资源={0}，PathId={1}，类型={2}，isBundle={3}",
            AssetDisplay.DisplayPath(asset), pathId, asset.Type, isBundle);
        _host.SetStatus("正在生成对象摘要…");
        var summaryTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var summary = await Task.Run(() => isBundle
                ? service.ReadBundleObjectSummary(source, container!, pathId)
                : service.ReadObjectSummary(source, pathId));
            summaryTimer.Stop();
            Log.Debug("对象摘要完成：PathId={0}，有结果={1}，耗时={2} ms",
                pathId, summary is not null, summaryTimer.ElapsedMilliseconds);
            if (summary is null)
            {
                MessageBox.Show(OwnerWindow,
                    "该对象类型暂无摘要支持（当前支持 Mesh / AnimationClip / Font）。字段树可通过「Unity 字段编辑」查看。",
                    "对象摘要", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            new ObjectSummaryWindow(summary) { Owner = OwnerWindow }.ShowDialog();
        }
        catch (Exception ex) { Log.Error(ex, "对象摘要生成失败：资源={0}，PathId={1}", AssetDisplay.DisplayPath(asset), pathId); ShowError("对象摘要生成失败", ex); }
        finally
        {
            RefreshSelectionButtons(_selectedAsset);
            _host.SetStatus("就绪");
        }
    }

    private async void DecodeAudio_Click(object sender, RoutedEventArgs e)
    {
        var fmodDirectory = _host.Env.EffectiveFmodLibraryDirectory(_host.Project);
        if (_host.Project is null || _selectedAsset is not AssetRecord asset || asset.Type != AssetType.Audio ||
            string.IsNullOrWhiteSpace(fmodDirectory) || !Directory.Exists(fmodDirectory)) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "WAV audio (*.wav)|*.wav|All files (*.*)|*.*",
            FileName = Path.GetFileName(asset.LogicalPath) + ".wav",
            Title = "导出 Bank 音频为 WAV"
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        Log.Info("用户导出音频 WAV：资源={0}，目标={1}，FMOD 目录={2}",
            AssetDisplay.DisplayPath(asset), dialog.FileName, fmodDirectory);
        var decodeTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var codec = new NativeFmodAudioCodec(fmodDirectory);
            var data = await new BankAudioService().DecodeToWaveAsync(asset, codec);
            await File.WriteAllBytesAsync(dialog.FileName, data);
            decodeTimer.Stop();
            Log.Info("音频导出完成：字节={0}，耗时={1} ms，目标={2}", data.Length, decodeTimer.ElapsedMilliseconds, dialog.FileName);
            _host.SetStatus($"音频已导出：{dialog.FileName}");
        }
        catch (Exception ex) { Log.Error(ex, "导出 WAV 失败：资源={0}，目标={1}", AssetDisplay.DisplayPath(asset), dialog.FileName); ShowError("导出 WAV 失败", ex); }
    }

    // ── 列表 / 目录树视图切换 ────────────────────────────────────────────

    /// <summary>列表 / 目录树切换。两个视图的数据来源不同（列表按排序取一页、目录树按目录序
    /// 取整条命中序列），所以切到树视图要重新取一次数 —— 列表模式下根本没取过树的数据源。
    /// 用 Click 而不是 Checked：点击已选中的 ToggleButton 会先取消勾选，
    /// 这里在每次点击后强制两态互斥。</summary>
    private void AssetViewMode_Click(object sender, RoutedEventArgs e)
    {
        // XAML 解析期间事件可能先于其他元素就绪：跳过首次触发。
        if (AssetTree is null || AssetViewListToggle is null || AssetViewTreeToggle is null) return;
        var treeSelected = ReferenceEquals(sender, AssetViewTreeToggle);
        Log.Info("用户切换浏览视图：{0} → {1}", _treeMode ? "目录树" : "列表", treeSelected ? "目录树" : "列表");
        AssetViewListToggle.IsChecked = !treeSelected;
        AssetViewTreeToggle.IsChecked = treeSelected;
        _treeMode = treeSelected;
        AssetList.Visibility = treeSelected ? Visibility.Collapsed : Visibility.Visible;
        AssetTree.Visibility = treeSelected ? Visibility.Visible : Visibility.Collapsed;
        if (_tree is not null && treeSelected) RebuildTree();
        else _ = RunSearchAsync(keepPage: true);
    }

    /// <summary>
    /// 按当前命中序列重建目录树根层。展开仍是惰性的 —— 每个节点只记「命中序列里的一个区间」，
    /// <see cref="MaterializeAssetNode"/> 才把那一层取回来（见 <see cref="AssetCatalogTree"/>）。
    /// <para>根层构建要逐条算显示路径（真实规模 5 万条约 0.4 s），所以它发生在后台的
    /// <c>BuildView</c> 里 —— 本方法只做 <c>TreeViewItem</c> 映射（根节点数量级很小）。</para>
    /// </summary>
    private void RebuildTree()
    {
        using var scope = Log.Scope("RebuildTree");
        if (_tree is null)
        {
            AssetTree.ItemsSource = null;
            _expandedTreeKeys.Clear();
            Log.Debug("重建目录树：没有命中序列（列表模式或未搜索），清空树并清空展开键集合");
            return;
        }
        // 先把「现在哪些节点是展开的」收进累积集合（跨重建保留：否则一次筛选把树清空
        // 就会把展开集合冲掉，清空筛选后无法回放）。
        // ④ 关键线索：捕获前后展开键数量变化能看出「突然折叠回初始形态」。
        var keysBefore = _expandedTreeKeys.Count;
        TreeExpansionState.Capture(AssetTree, AssetTreeKeyOf, _expandedTreeKeys);
        var capturedKeys = _expandedTreeKeys.Count;
        var selectedLogicalPath = (AssetTree.SelectedItem as TreeViewItem)?.Tag is AssetCatalogTreeNode { Asset: { } selected }
            ? selected.LogicalPath
            : null;
        var roots = _tree.Roots().Select(MakeTreeItem).ToList();
        AssetTree.ItemsSource = roots;
        Log.Debug("重建目录树：命中 {0:N0} 条 → 根节点 {1} 个；捕获展开键 {2} → {3}；重建前选中 LogicalPath={4}（④ 若展开键数量骤减即折叠）",
            _tree.Count, roots.Count, keysBefore, capturedKeys, selectedLogicalPath ?? "(无)");
        // 回放展开态（懒加载：按 key 逐层物化 + 展开），并找回树上的选中叶子。
        TreeExpansionState.Restore(AssetTree, AssetTreeKeyOf, _expandedTreeKeys, MaterializeAssetNode);
        Log.Debug("回放展开态完成：应回放 {0} 个键，AssetTree 根层实测 {1} 项",
            _expandedTreeKeys.Count, AssetTree.Items.Count);
        if (selectedLogicalPath is { } selectedPath) RestoreTreeSelection(selectedPath);
    }

    /// <summary>树节点的稳定 key = 节点自己记的**原始**路径段拼接（不含消歧后缀，
    /// 免得「同级出现重名之后后缀变了」把所有展开态冲掉）。</summary>
    private static string? AssetTreeKeyOf(object? tag)
        => tag is AssetCatalogTreeNode { Path.Length: > 0 } node ? node.Path : null;

    /// <summary>物化一个节点的子层（与 <see cref="AssetTree_Expanded"/> 同一逻辑，供回放复用）。
    /// 这一刻才向目录门面要记录 —— 而且要的只是**这一层的那一段区间**。</summary>
    private static void MaterializeAssetNode(TreeViewItem item)
    {
        if (item.Tag is not AssetCatalogTreeNode node || node.IsLeaf) return;
        if (item.Items.Count == 1 && item.Items[0] is not AssetCatalogTreeNode)
        {
            item.Items.Clear();
            foreach (var child in node.Expand())
                item.Items.Add(MakeTreeItem(child));
            if (Log.IsDebugEnabled) Log.Debug("物化树节点子层：节点={0}，子项={1}，累计资源数={2}",
                node.Name, item.Items.Count, node.Count);
        }
    }

    /// <summary>重建后按 LogicalPath 找回树里的选中叶子（重建会丢选中高亮）。
    /// 用 LogicalPath 而非 AssetId：树叶子上的记录会随重建/分页更换实例，
    /// AssetId 是每次重建都会变的 Guid。</summary>
    private void RestoreTreeSelection(string logicalPath)
    {
        var found = FindLeafItem(AssetTree.Items, logicalPath);
        if (found is not null) found.IsSelected = true;
        else Log.Debug("重建目录树后没有找到选中叶子：LogicalPath={0}（目标还在未展开的分支里，属正常）", logicalPath);
    }

    private static TreeViewItem? FindLeafItem(System.Collections.IEnumerable items, string logicalPath)
    {
        foreach (var item in items)
        {
            if (item is not TreeViewItem node) continue;
            if (node.Tag is AssetCatalogTreeNode { IsLeaf: true, Asset: { } asset }
                && string.Equals(asset.LogicalPath, logicalPath, StringComparison.OrdinalIgnoreCase)) return node;
            var nested = FindLeafItem(node.Items, logicalPath);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>包装一个树节点：目录节点先放一个占位子项，真正展开时才
    /// 生成下一层（40 万级索引也不会在构建树时卡顿）。</summary>
    private static TreeViewItem MakeTreeItem(AssetCatalogTreeNode node)
    {
        var item = new TreeViewItem
        {
            Header = node.IsLeaf ? node.Name : $"{node.Name} ({node.Count})",
            Tag = node,
        };
        if (!node.IsLeaf) item.Items.Add(new object());
        return item;
    }

    private void AssetTree_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item) return;
        MaterializeAssetNode(item);
        if (AssetTreeKeyOf(item.Tag) is { Length: > 0 } key) _expandedTreeKeys.Add(key);
        if (Log.IsDebugEnabled) Log.Debug("树节点展开：键={0}，子项={1}，累计展开键={2}",
            AssetTreeKeyOf(item.Tag) ?? "(无)", item.Items.Count, _expandedTreeKeys.Count);
    }

    private void AssetTree_Collapsed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem item && AssetTreeKeyOf(item.Tag) is { Length: > 0 } key)
        {
            _expandedTreeKeys.Remove(key);
            if (Log.IsDebugEnabled) Log.Debug("树节点折叠：键={0}，剩余展开键={1}（④ 若是用户没动就整片折叠，看这里与 RebuildTree 的先后顺序）",
                key, _expandedTreeKeys.Count);
        }
    }

    private void AssetTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: AssetCatalogTreeNode { IsLeaf: true } leaf })
        {
            if (Log.IsDebugEnabled) Log.Debug("树选中叶子：{0}", AssetDisplay.DisplayPath(leaf.Asset!));
            ApplyAssetSelection(leaf.Asset);
        }
    }

    private void AssetTree_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AssetTree.SelectedItem is TreeViewItem { Tag: AssetCatalogTreeNode { IsLeaf: true, Asset: { } asset } })
        {
            Log.Info("用户双击目录树叶子：资源={0}（类型={1}）", AssetDisplay.DisplayPath(asset), asset.Type);
            ActivateDefaultAction(asset);
        }
    }

    // ── 辅助 ─────────────────────────────────────────────────────────

    /// <summary>对话框/消息框的宿主窗口（页面本身不是 Window）。</summary>
    private Window OwnerWindow => Window.GetWindow(this) ?? System.Windows.Application.Current.MainWindow;

    private void ShowError(string title, Exception ex)
    {
        // 调用点已各自 Log.Error 记过完整栈；这里只补「用户实际看到了什么」。
        Log.Warn("弹出错误对话框：{0} —— {1}", title, ex.Message);
        _host.SetStatus($"{title}：{ex.Message}");
        MessageBox.Show(OwnerWindow, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
