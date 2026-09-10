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
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Editing.Images;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.App;

/// <summary>
/// 资源工作台页面（plan-02 第 1.2 步：原资源标签内容原样搬入，行为不变）。
/// 页面常驻不销毁（切换页面保留搜索/选中/预览状态）；项目状态刷新由宿主经
/// <see cref="OnProjectRefreshed"/> 驱动。plan-03 已移除手动导入入口，plan-04
/// 加入可拖拽的预览列与占比持久化。
/// </summary>
public partial class AssetsWorkbenchPage : UserControl
{
    private readonly IWorkbenchHost _host;
    private readonly AssetSearchService _search = new();
    private readonly AssetEditService _assetEdits = new();
    private readonly ImageAtlasEditService _atlasEdits = new();
    private readonly SpriteMetadataEditService _spriteEdits = new();
    private readonly UnityFieldEditService _unityFieldEdits = new();
    private readonly AssetPreviewRegistry _previewRegistry;
    private readonly DispatcherTimer _searchTimer;
    private readonly UiStateService _uiState;
    private readonly string _uiStateFile;
    private int _searchGeneration;
    private int _previewGeneration;
    // 音频试听：预览管线给出的已解码 WAV + WPF MediaPlayer（切换选中/关窗即停）。
    private System.Windows.Media.MediaPlayer? _previewPlayer;
    private string? _previewAudioFile;
    private AssetPreviewAudio? _currentAudio;
    // 当前选中资源：列表与目录树两个视图共用（右键菜单/双击/按钮都以此为准）。
    private AssetRecord? _selectedAsset;
    // 最近一次搜索结果快照：目录树视图按它重建根层。
    private IReadOnlyList<AssetRecord>? _lastResults;
    // 默认以「容器目录树」呈现：用户看到的是类文件夹结构，而不是扁平技术路径。
    private bool _treeMode = true;
    private bool _filtersInitialized;

    /// <summary>调试操作由宿主（MainWindow）执行：页面只承载按钮与状态显示。</summary>
    public event EventHandler? BuildOverlayRequested;
    public event EventHandler? DebugApplyRequested;

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
        // plan-05：预览提供者管线（FMOD 目录每次现取，设置改动后立即生效）。
        _previewRegistry = AssetPreviewRegistry.CreateDefault(
            () => host.Env.EffectiveFmodLibraryDirectory(host.Project));
    }

    /// <summary>当前选中的资源（宿主拖放判定用）。</summary>
    public AssetRecord? SelectedAsset => _selectedAsset;

    /// <summary>宿主刷新项目状态时调用：重算计数并重跑搜索（选中项按 AssetId 保留）。
    /// 百万级资产下计数在后台线程算，避免刷新瞬间卡死 UI。</summary>
    public void OnProjectRefreshed()
    {
        var project = _host.Project;
        AssetCountText.Text = (project?.Assets.Count ?? 0).ToString();
        EditCountText.Text = "…";
        _ = UpdateEditCountAsync();
        // 与提示条/一键导出同一口径：按编辑标记统计「已修改资源」数，
        // 而不是 Edits 历史条数（重复替换会让历史虚增）。
        RefreshAssetList();
    }

    private async Task UpdateEditCountAsync()
    {
        var project = _host.Project;
        if (project is null) { EditCountText.Text = "0"; return; }
        var count = await Task.Run(() => project.Assets.Count(AssetEditService.HasEdits));
        EditCountText.Text = count.ToString();
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

    /// <summary>调试状态由宿主写入（覆盖层构建 / 应用启动调试的结果）。</summary>
    public void SetDebugState(string text) => DebugStateText.Text = text;

    /// <summary>关窗时持久化布局占比。</summary>
    public void PersistUiState() => SavePreviewWidth();

    // ── plan-04：预览列拖拽与占比持久化 ─────────────────────────────

    private void PreviewSplitter_DragCompleted(object sender, System.Windows.Input.MouseButtonEventArgs e) => SavePreviewWidth();

    /// <summary>双击把手复位默认宽（360）并持久化。</summary>
    private void PreviewSplitter_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        PreviewColumn.Width = new GridLength(360);
        SavePreviewWidth();
    }

    private void SavePreviewWidth()
    {
        _uiState.AssetsPreviewColumnWidth = PreviewColumn.Width.IsAbsolute ? PreviewColumn.Width.Value : 360;
        UiStateService.Save(_uiState, _uiStateFile);
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
        try
        {
            await _assetEdits.ReplaceFromFileAsync(project, selected.AssetId, path, projectDirectory);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState("拖放替换已登记");
            RestoreListSelection(selected);
        }
        catch (Exception ex) { ShowError("拖放替换失败", ex); }
    }

    // ── 搜索 / 筛选 ──────────────────────────────────────────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchTimer is null) return; // XAML 解析期间可能先于构造函数赋值
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_filtersInitialized || _searchTimer is null) return;
        RequestSearch();
    }

    /// <summary>复选框类筛选（「仅显示容器内资源」/「仅已替换」）：XAML 里的
    /// 初始 IsChecked 会在解析期间就触发 Checked，此时 <see cref="_searchTimer"/>
    /// 还没建好（构造函数在 InitializeComponent 之后才赋值），必须跳过。</summary>
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_searchTimer is null || _host.Project is null) return;
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
        SearchBox.Text = string.Empty;
        TypeFilter.SelectedIndex = -1;
        StateFilter.SelectedIndex = -1;
        SortFilter.SelectedIndex = 0;
        ContainerOnlyFilter.IsChecked = true;
        MinSizeFilter.Text = string.Empty;
        MaxSizeFilter.Text = string.Empty;
        ReplacedOnlyFilter.IsChecked = false;
        ShowStaticFilter.IsChecked = false;
        _searchTimer.Stop();
        // 上面这些赋值会经 Filter_Changed 触发防抖搜索；这里要求「立刻」重跑，
        // 避免用户看到清除筛选后仍然空白的旧结果。
        _ = RunSearchAsync();
    }

    /// <summary>初始化筛选下拉框（仅一次）并触发一次搜索。真正的过滤在
    /// RunSearchAsync（后台线程 + 防抖）。</summary>
    private void RefreshAssetList()
    {
        if (_host.Project is null) { AssetList.ItemsSource = null; _lastResults = null; AssetTree.ItemsSource = null; return; }
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
        return new AssetSearchQuery(
            SearchBox?.Text,
            selectedType,
            selectedState,
            null,
            null,
            null,
            minKb,
            maxKb,
            ReplacedOnlyFilter.IsChecked,
            sort,
            ContainerOnlyFilter?.IsChecked == true ? true : null,
            // plan-08：静态数据 bundle 的资源默认隐藏（勾选「显示静态数据表」后可见）。
            ShowStaticTables: ShowStaticFilter?.IsChecked == true);
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

    /// <summary>后台线程过滤 + 排序 + 行视图模型构造（快照先在 UI 线程取好，
    /// 避免与集合修改竞争），带代际守卫：过期结果直接丢弃；选中项按 AssetId
    /// 跨刷新保留。列表承载 <see cref="AssetRow"/>（显示层），树视图仍按底层
    /// 记录构建。</summary>
    private async Task RunSearchAsync()
    {
        var project = _host.Project;
        if (project is null) { AssetList.ItemsSource = null; _lastResults = null; AssetTree.ItemsSource = null; return; }
        var generation = ++_searchGeneration;
        var query = BuildSearchQuery();
        var snapshot = project.Assets.ToArray();
        IReadOnlyList<AssetRecord> results;
        IReadOnlyList<AssetRow> rows;
        try
        {
            // 过滤 + 排序 + 百万级 AssetRow 构造全部在后台线程完成，
            // UI 线程只做最终的 ItemsSource 赋值（ListView 虚拟化按需实例化）。
            (results, rows) = await Task.Run(() =>
            {
                var filtered = _search.Search(snapshot, query);
                var rowList = filtered.Select(a => new AssetRow(a)).ToList();
                return (filtered, (IReadOnlyList<AssetRow>)rowList);
            });
        }
        catch (ArgumentException) { return; }
        if (generation != _searchGeneration) return;
        _lastResults = results;
        var selectedId = (AssetList.SelectedItem as AssetRow)?.AssetId;
        AssetList.ItemsSource = rows;
        if (selectedId is { } id && AssetList.ItemsSource is IEnumerable<AssetRow> rows2)
        {
            var restored = rows2.FirstOrDefault(x => x.AssetId == id);
            if (restored is not null) AssetList.SelectedItem = restored;
        }
        AssetCountText.Text = results.Count == project.Assets.Count
            ? project.Assets.Count.ToString()
            : $"{results.Count} / {project.Assets.Count}";
        // 目录树视图：按最新搜索结果重建根层（展开仍是惰性的）。
        if (_treeMode) RebuildTree();
    }

    // ── 选中与预览 ───────────────────────────────────────────────────

    private void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ApplyAssetSelection((AssetList.SelectedItem as AssetRow)?.Asset);

    /// <summary>统一的选中处理：列表与目录树两个视图共用（右侧状态、按钮
    /// 可用性、预览都以此为准；右键菜单/双击读取 <see cref="_selectedAsset"/>）。</summary>
    private void ApplyAssetSelection(AssetRecord? asset)
    {
        _selectedAsset = asset;
        SelectedAssetNameText.Text = asset is null ? "未选择" : AssetDisplay.DisplayName(asset);
        SelectedAssetPathText.Text = asset is null ? "—" : AssetDisplay.DisplayPath(asset);
        SelectedAssetTypeText.Text = asset is null ? "—" : AssetDisplay.TypeLabel(asset.Type);
        SelectedAssetSizeText.Text = asset is null
            ? "—"
            : new HumanSizeConverter().Convert(asset.Size, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string;
        SelectedStateText.Text = asset is null ? "—" : AssetDisplay.StateLabel(asset.EditState);
        RefreshSelectionButtons(asset);
        UpdatePreview(asset);
    }

    /// <summary>把底层记录还原成列表选中行：列表已按 AssetRow 承载，选中行
    /// 必须找到对应的行对象，否则右侧面板不会跟着变。</summary>
    private void RestoreListSelection(AssetRecord asset)
    {
        if (AssetList.ItemsSource is IEnumerable<AssetRow> rows)
        {
            var row = rows.FirstOrDefault(r => r.AssetId == asset.AssetId);
            if (row is not null)
            {
                AssetList.SelectedItem = row;
                AssetList.ScrollIntoView(row);
                return;
            }
        }
        ApplyAssetSelection(asset);
    }

    /// <summary>Recomputes every per-selection action button from the current
    /// project/selection state; also used after long operations to restore
    /// buttons disabled during the operation.</summary>
    private void RefreshSelectionButtons(AssetRecord? asset)
    {
        var project = _host.Project;
        var hasProjectFile = _host.ProjectFile is not null;
        ReplaceAssetButton.IsEnabled = asset is not null && hasProjectFile;
        EditTextButton.IsEnabled = asset?.Type is AssetType.Text or AssetType.Json && hasProjectFile;
        HexPreviewButton.IsEnabled = asset is not null;
        ClearEditsButton.IsEnabled = asset is not null && AssetEditService.HasEdits(asset);
        SpriteMetadataButton.IsEnabled = asset?.Type == AssetType.Sprite &&
            asset.UnityPathId.HasValue && !string.IsNullOrWhiteSpace(asset.SourcePath) &&
            File.Exists(asset.SourcePath) && hasProjectFile;
        UnityFieldsButton.IsEnabled = asset?.UnityPathId.HasValue == true &&
            (asset.Metadata.ContainsKey("unityBundle") || asset.Metadata.ContainsKey("unitySerializedFile")) &&
            !string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath) && hasProjectFile;
        ReferencersButton.IsEnabled = UnityFieldsButton.IsEnabled;
        ObjectSummaryButton.IsEnabled = asset?.UnityPathId.HasValue == true &&
            asset.Type is AssetType.Mesh or AssetType.Animation or AssetType.Font &&
            (asset.Metadata.ContainsKey("unityBundle") || asset.Metadata.ContainsKey("unitySerializedFile")) &&
            !string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath) && hasProjectFile;
        var fmodDirectory = _host.Env.EffectiveFmodLibraryDirectory(project);
        DecodeAudioButton.IsEnabled = asset?.Type == AssetType.Audio &&
            asset.LogicalPath.StartsWith("fsb/", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(fmodDirectory) &&
            Directory.Exists(fmodDirectory) && hasProjectFile;
        FsbInspectButton.IsEnabled = asset?.Type == AssetType.Audio &&
            asset.LogicalPath.StartsWith("fsb/", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath) && hasProjectFile;
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
        var generation = ++_previewGeneration;
        ResetPreview();
        if (asset is null) return;
        AssetPreview preview;
        try
        {
            preview = await Task.Run(() => _previewRegistry.PreviewAsync(asset, null, CancellationToken.None)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (generation != _previewGeneration) return;
            PreviewInfoText.Text = $"预览失败：{ex.Message}";
            return;
        }
        if (generation != _previewGeneration) return;
        _currentAudio = preview.Audio;
        PreviewInfoText.Text = preview.InfoLine;
        PreviewHost.Content = BuildPreviewView(preview);
        _ = UpdatePropertiesAsync(asset, generation);
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
        }
        catch (Exception ex)
        {
            if (generation == _previewGeneration) PropertyInfoText.Text = $"属性读取失败：{ex.Message}";
        }
    }

    // ── 预览视图构建（按 Kind 切换；全部只读，plan-05）──────────────────

    private UIElement? BuildPreviewView(AssetPreview preview) => preview.Kind switch
    {
        AssetPreviewKind.Image => BuildImageView(preview),
        AssetPreviewKind.Text => BuildTextView(preview.Text, jsonTree: false),
        AssetPreviewKind.Json => BuildTextView(preview.Text, jsonTree: true),
        AssetPreviewKind.Audio => BuildAudioView(preview.Audio),
        AssetPreviewKind.Rows => BuildRowsView(preview.Rows),
        AssetPreviewKind.Hex => BuildHexView(preview.Text),
        AssetPreviewKind.Message => BuildMessageView(preview.Text),
        _ => null,
    };

    /// <summary>图像预览：棋盘格透明底 + 滚轮缩放 + 拖拽平移 + 双击复位；
    /// 有替换时可在「替换图 ↔ 原图」之间切换。</summary>
    private UIElement BuildImageView(AssetPreview preview)
    {
        var container = new DockPanel { LastChildFill = true };
        var image = new Image { Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0.5, 0.5) };
        var scale = new ScaleTransform(1, 1);
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        image.RenderTransform = transforms;
        if (preview.ImagePng is not null) image.Source = LoadBitmap(preview.ImagePng);

        var scroll = new ScrollViewer
        {
            Background = TryFindResource("Checkerboard") as Brush ?? new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1A)),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 180,
            Content = image,
        };
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
        scroll.MouseDoubleClick += (_, _) => { scale.ScaleX = scale.ScaleY = 1; };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
            scale.ScaleX = Math.Clamp(scale.ScaleX * factor, 0.1, 16);
            scale.ScaleY = scale.ScaleX;
            e.Handled = true;
        };

        if (preview.AlternateImagePng is not null)
        {
            var toggle = new ToggleButton
            {
                Content = $"{preview.AlternateLabel ?? "原图"}（勾选切换）",
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "在替换后的图与缓存原图之间切换显示。",
            };
            var primary = preview.ImagePng;
            var alternate = preview.AlternateImagePng;
            toggle.Checked += (_, _) => image.Source = LoadBitmap(alternate);
            toggle.Unchecked += (_, _) => image.Source = primary is null ? null : LoadBitmap(primary);
            DockPanel.SetDock(toggle, Dock.Top);
            container.Children.Add(toggle);
        }
        container.Children.Add(scroll);
        return container;
    }

    /// <summary>文本预览：行号 + 等宽正文；JSON 可解析时提供「文本 / JSON 树」切换。</summary>
    private UIElement BuildTextView(string? text, bool jsonTree)
    {
        text ??= string.Empty;
        var textView = BuildNumberedText(text);
        if (!jsonTree || TryBuildJsonTree(text) is not { } tree) return textView;

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

    /// <summary>行号 + 正文：两列都用同一字体/字号的只读 TextBox（行高天然一致），
    /// 由外层 ScrollViewer 统一滚动。</summary>
    private static UIElement BuildNumberedText(string text)
    {
        var lineCount = 1;
        foreach (var character in text) if (character == '\n') lineCount++;
        var numbers = new StringBuilder();
        for (var i = 1; i <= lineCount; i++) numbers.Append(i).Append('\n');

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var numberBox = new TextBox
        {
            Text = numbers.ToString(),
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5F, 0x72, 0x85)),
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
            Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xE2, 0xEA)),
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
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x40)),
            BorderThickness = new Thickness(1),
            Height = 220,
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
        catch (JsonException) { return null; }

        const int maxNodes = 2000;
        var budget = maxNodes;
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
        if (budget <= 0) root.Items.Add(new TreeViewItem { Header = $"… 节点过多，只显示前 {maxNodes} 个" });
        var tree = new TreeView
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xE2, 0xEA)),
        };
        tree.Items.Add(root);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x40)),
            BorderThickness = new Thickness(1),
            Height = 220,
            Child = new ScrollViewer { Content = tree, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4) },
        };
    }

    /// <summary>音频预览：试听按钮 + 波形包络（解码 WAV 的 PCM 峰值）。</summary>
    private UIElement BuildAudioView(AssetPreviewAudio? audio)
    {
        var panel = new StackPanel();
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
            Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x93, 0xA4)),
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
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1A)),
            ClipToBounds = true,
        };
        var polyline = new System.Windows.Shapes.Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xDA)),
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
                Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x93, 0xA4)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var value = new TextBlock
            {
                Text = row.Value,
                TextWrapping = TextWrapping.Wrap,
                Foreground = row.Highlight
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD3, 0x7A))
                    : new SolidColorBrush(Color.FromRgb(0xD8, 0xE2, 0xEA)),
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

    private static UIElement BuildHexView(string? text) => new Border
    {
        Background = new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1A)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x40)),
        BorderThickness = new Thickness(1),
        Height = 200,
        Child = new TextBox
        {
            Text = text ?? string.Empty,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xE2, 0xEA)),
            FontFamily = MonoFont,
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6),
        },
    };

    private static UIElement BuildMessageView(string? text) => new TextBlock
    {
        Text = text ?? string.Empty,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB0, 0xBF)),
        LineHeight = 18,
    };

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    private static readonly FontFamily MonoFont = new("Consolas");

    /// <summary>清空预览区（切换选中项 / 无选中时调用），并停止正在播放的试听。</summary>
    private void ResetPreview()
    {
        PreviewHost.Content = null;
        PreviewInfoText.Text = "无预览";
        PropertyList.ItemsSource = null;
        PropertyInfoText.Text = "—";
        _currentAudio = null;
        StopAudioPreview();
    }

    /// <summary>试听当前预览的音频：把已解码的 WAV 写成临时文件交给 MediaPlayer；
    /// 再点一次停止；切换选中项或关闭窗口时自动停止并删除临时文件。</summary>
    private async Task PlayCurrentAudioAsync()
    {
        var audio = _currentAudio;
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
            _host.SetStatus($"正在播放（WAV {audio.Wave.Length / 1024} KB）");
        }
        catch (Exception ex)
        {
            StopAudioPreview();
            _host.SetStatus($"试听失败：{ex.Message}");
        }
    }

    private void EndAudioPreview(System.Windows.Media.MediaPlayer player)
    {
        if (!ReferenceEquals(_previewPlayer, player)) return; // 已经停止 / 换成另一个资源了
        StopAudioPreview();
        _host.SetStatus("播放结束");
    }

    private void StopAudioPreview()
    {
        var player = _previewPlayer;
        _previewPlayer = null;
        if (player is not null)
        {
            try { player.Stop(); player.Close(); }
            catch (Exception) { /* 播放器已释放 */ }
        }
        var file = _previewAudioFile;
        _previewAudioFile = null;
        TryDelete(file);
    }

    private static void TryDelete(string? file)
    {
        if (file is null) return;
        try { File.Delete(file); } catch (Exception) { /* 临时文件留给系统清理 */ }
    }

    private static BitmapImage LoadBitmap(byte[] png)
    {
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(png);
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }

    // ── 编辑操作 ─────────────────────────────────────────────────────

    private async void SplitAtlas_Click(object sender, RoutedEventArgs e)
    {
        var project = _host.Project;
        if (project is null || _host.ProjectFile is null || _selectedAsset is not AssetRecord asset) return;
        if (!TryGetImageSource(asset, out var source)) { _host.SetStatus("当前资源没有可读取的本地图像文件"); return; }
        if (!int.TryParse(AtlasColumnsBox.Text, out var columns) || !int.TryParse(AtlasRowsBox.Text, out var rows) || columns <= 0 || rows <= 0)
        { _host.SetStatus("列数和行数必须是正整数"); return; }
        try
        {
            var result = await _atlasEdits.SplitAsync(project, asset.AssetId, source, Path.GetDirectoryName(_host.ProjectFile)!, columns, rows);
            await _host.SaveProjectAsync();
            RepackAtlasButton.IsEnabled = true;
            _host.SetStatus($"图集已拆分：{result.Layout.Regions.Count} 个区域");
        }
        catch (Exception ex) { ShowError("拆分图集失败", ex); }
    }

    private async void RepackAtlas_Click(object sender, RoutedEventArgs e)
    {
        var project = _host.Project;
        if (project is null || _host.ProjectFile is null || _selectedAsset is not AssetRecord asset) return;
        try
        {
            await _atlasEdits.RepackAsync(project, asset.AssetId, Path.GetDirectoryName(_host.ProjectFile)!);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState("图集已恢复并记录替换");
            RestoreListSelection(asset);
        }
        catch (Exception ex) { ShowError("恢复图集失败", ex); }
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
        try
        {
            await _assetEdits.ReplaceFromFileAsync(project, asset.AssetId, dialog.FileName, Path.GetDirectoryName(_host.ProjectFile)!);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState("资源替换已记录");
            RestoreListSelection(asset);
        }
        catch (Exception ex) { ShowError("替换资源失败", ex); }
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
        try
        {
            var cleared = _assetEdits.ClearEdits(project, asset.AssetId, Path.GetDirectoryName(_host.ProjectFile)!);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState(cleared ? "已撤销此资源的全部修改" : "此资源没有可撤销的修改");
            RestoreListSelection(asset);
        }
        catch (Exception ex) { ShowError("撤销修改失败", ex); }
    }

    /// <summary>双击资源 = 按类型做最常用的事（列表与目录树共用：
    /// <see cref="ActivateDefaultAction"/>)。</summary>
    private void AssetList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_selectedAsset is not AssetRecord asset) return;
        ActivateDefaultAction(asset);
    }

    /// <summary>按类型做最常用的事：图像 → 替换；文本/JSON → 内置文本编辑器；
    /// 其余 → Unity 字段编辑（可用时），否则十六进制预览。</summary>
    private void ActivateDefaultAction(AssetRecord asset)
    {
        if (asset.Type is AssetType.Texture or AssetType.Sprite && ReplaceAssetButton.IsEnabled)
        {
            ReplaceAsset_Click(this, new RoutedEventArgs());
            return;
        }
        if (asset.Type is AssetType.Text or AssetType.Json && EditTextButton.IsEnabled)
        {
            EditTextAsset_Click(this, new RoutedEventArgs());
            return;
        }
        if (UnityFieldsButton.IsEnabled) { EditUnityFields_Click(this, new RoutedEventArgs()); return; }
        if (HexPreviewButton.IsEnabled) HexPreview_Click(this, new RoutedEventArgs());
    }

    /// <summary>文本 / JSON 资源的内置编辑器（P3.10）：改完保存即登记为替换，
    /// 走与替换文件同一条可撤销 / 可导出管道；JSON 保存前校验并格式化。</summary>
    private async void EditTextAsset_Click(object sender, RoutedEventArgs e)
    {
        var project = _host.Project;
        if (project is null || _host.ProjectFile is null) { _host.SetStatus("请先创建或打开项目"); return; }
        if (_selectedAsset is not AssetRecord asset) return;
        if (asset.Type is not (AssetType.Text or AssetType.Json))
        {
            _host.SetStatus("当前资源不是文本 / JSON 资源；可以用「替换…」换掉整个文件。");
            return;
        }
        try
        {
            var service = new TextAssetEditService(_assetEdits);
            var document = await service.OpenAsync(project, asset.AssetId);
            var window = new TextAssetEditorWindow(document, AssetDisplay.DisplayPath(asset)) { Owner = OwnerWindow };
            if (window.ShowDialog() != true || window.Result is not { } edited) return;
            await service.SaveAsync(project, edited, Path.GetDirectoryName(_host.ProjectFile)!);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState("文本修改已记录");
            RestoreListSelection(asset);
        }
        catch (Exception ex) { ShowError("编辑文本失败", ex); }
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
        try { Clipboard.SetText(AssetDisplay.DisplayPath(asset)); _host.SetStatus("已复制资源路径"); }
        catch (Exception) { _host.SetStatus("复制失败（剪贴板被其他程序占用）"); }
    }

    private async void BatchReplace_Click(object sender, RoutedEventArgs e)
    {
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
        try
        {
            var report = await _assetEdits.BatchReplaceFromDirectoryAsync(project, folder, Path.GetDirectoryName(_host.ProjectFile)!);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState($"批量替换已登记（{report.Matched} 个）");
            var detail = report.Describe();
            if (report.FilesWithoutAsset.Count > 0)
                detail += "\n\n没有对应资源的文件（前 15 个）：\n" + string.Join("\n", report.FilesWithoutAsset.Take(15));
            MessageBox.Show(OwnerWindow, detail, "批量登记替换", MessageBoxButton.OK,
                report.Matched > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError("批量登记替换失败", ex); }
    }

    private void HexPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAsset is not AssetRecord asset) return;
        try { new HexPreviewWindow(asset) { Owner = OwnerWindow }.ShowDialog(); }
        catch (Exception ex) { ShowError("十六进制预览失败", ex); }
    }

    private async void InspectSprite_Click(object sender, RoutedEventArgs e)
    {
        var project = _host.Project;
        if (_selectedAsset is not AssetRecord asset || asset.Type != AssetType.Sprite ||
            !asset.UnityPathId.HasValue || string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath)) return;
        try
        {
            var sprite = new LimbusModEditor.Formats.Unity.UnityAssetService().ReadSprite(asset.SourcePath, asset.UnityPathId.Value);
            if (sprite is null) { _host.SetStatus("未找到 Sprite 对象。"); return; }
            var initial = _spriteEdits.ReadStored(asset) ?? UnitySpriteMetadata.From(sprite);
            var dialog = new SpriteMetadataWindow(initial) { Owner = OwnerWindow };
            if (dialog.ShowDialog() != true || dialog.Result is null) return;
            _spriteEdits.Set(project!, asset, dialog.Result);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState("Sprite 元数据修改已记录");
            RestoreListSelection(asset);
        }
        catch (Exception ex) { ShowError("读取 Sprite 元数据失败", ex); }
    }

    private async void EditUnityFields_Click(object sender, RoutedEventArgs e)
    {
        var project = _host.Project;
        if (project is null || _host.ProjectFile is null || _selectedAsset is not AssetRecord asset ||
            !asset.UnityPathId.HasValue || string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath)) return;
        UnityFieldsButton.IsEnabled = false;
        _host.SetStatus("正在读取对象字段…");
        try
        {
            // Field tree, script info, dependencies and the in-file object scan
            // all walk the whole serialized file / bundle; run them off the UI
            // thread so large bundles do not freeze the window.
            var service = new LimbusModEditor.Formats.Unity.UnityAssetService();
            var isBundle = asset.Metadata.ContainsKey("unityBundle") && !string.IsNullOrWhiteSpace(asset.ContainerPath);
            var source = asset.SourcePath;
            var container = asset.ContainerPath;
            var pathId = asset.UnityPathId.Value;
            var stored = _unityFieldEdits.ReadStored(asset);
            var (fields, scriptInfo, dependencies, inFileObjects) = await Task.Run(() =>
            {
                var root = isBundle
                    ? service.ReadBundleObjectFields(source, container!, pathId)
                    : service.ReadObjectFields(source, pathId);
                LimbusModEditor.Formats.Unity.UnityScriptInfo? script = null;
                try
                {
                    script = isBundle
                        ? service.ReadBundleObjectScriptInfo(source, container!, pathId)
                        : service.ReadObjectScriptInfo(source, pathId);
                }
                catch (Exception) { /* non-MonoBehaviour objects have no script info */ }
                IReadOnlyList<LimbusModEditor.Formats.Unity.UnityDependency>? deps = null;
                try
                {
                    deps = isBundle
                        ? service.ReadBundleObjectDependencies(source, container!, pathId)
                        : service.ReadObjectDependencies(source, pathId);
                }
                catch (Exception) { /* dependency view is best-effort */ }
                IReadOnlyList<AssetRecord>? objects = null;
                try
                {
                    var scanned = isBundle ? service.ScanBundle(source) : service.ScanSerializedFile(source);
                    objects = scanned
                        .Where(x => x.UnityPathId.HasValue && (!isBundle || string.Equals(x.ContainerPath, container, StringComparison.OrdinalIgnoreCase)))
                        .ToArray();
                }
                catch (Exception) { /* object picker is best-effort */ }
                return (root, script, deps, objects);
            });
            var dialog = new UnityFieldEditorWindow(fields, stored, scriptInfo, dependencies, inFileObjects) { Owner = OwnerWindow };
            if (dialog.ShowDialog() != true || dialog.Result is null) return;
            if (dialog.Result.Count == 0) return;
            _unityFieldEdits.Set(project, asset, fields, dialog.Result);
            await _host.SaveProjectAsync();
            _host.RefreshProjectState("Unity 字段修改已记录");
            RestoreListSelection(asset);
        }
        catch (Exception ex) { ShowError("Unity 字段读取或保存失败", ex); }
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
        try
        {
            var inspection = await new BankAudioService().InspectFsbAsync(asset);
            new BankInspectorWindow(asset, inspection) { Owner = OwnerWindow }.ShowDialog();
        }
        catch (Exception ex) { ShowError("FSB 结构检查失败", ex); }
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
        _host.SetStatus("正在扫描引用者…");
        try
        {
            var service = new LimbusModEditor.Formats.Unity.UnityAssetService();
            var isBundle = asset.Metadata.ContainsKey("unityBundle") && !string.IsNullOrWhiteSpace(asset.ContainerPath);
            var pathId = asset.UnityPathId.Value;
            var source = asset.SourcePath;
            var container = asset.ContainerPath;
            var referencers = await Task.Run(() => isBundle
                ? service.FindBundleReferencers(source, container!, pathId)
                : service.FindReferencers(source, pathId));
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
        catch (Exception ex) { ShowError("引用者检查失败", ex); }
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
        _host.SetStatus("正在生成对象摘要…");
        try
        {
            var summary = await Task.Run(() => isBundle
                ? service.ReadBundleObjectSummary(source, container!, pathId)
                : service.ReadObjectSummary(source, pathId));
            if (summary is null)
            {
                MessageBox.Show(OwnerWindow,
                    "该对象类型暂无摘要支持（当前支持 Mesh / AnimationClip / Font）。字段树可通过「Unity 字段编辑」查看。",
                    "对象摘要", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            new ObjectSummaryWindow(summary) { Owner = OwnerWindow }.ShowDialog();
        }
        catch (Exception ex) { ShowError("对象摘要生成失败", ex); }
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
        try
        {
            using var codec = new NativeFmodAudioCodec(fmodDirectory);
            var data = await new BankAudioService().DecodeToWaveAsync(asset, codec);
            await File.WriteAllBytesAsync(dialog.FileName, data);
            _host.SetStatus($"音频已导出：{dialog.FileName}");
        }
        catch (Exception ex) { ShowError("导出 WAV 失败", ex); }
    }

    // ── 调试覆盖层（实际操作在宿主，这里只转发）───────────────────────

    private void BuildOverlay_Click(object sender, RoutedEventArgs e) => BuildOverlayRequested?.Invoke(this, EventArgs.Empty);

    private void DebugApply_Click(object sender, RoutedEventArgs e) => DebugApplyRequested?.Invoke(this, EventArgs.Empty);

    // ── 列表 / 目录树视图切换 ────────────────────────────────────────────

    /// <summary>列表 / 目录树切换：两个视图共享同一份搜索结果
    /// （<see cref="_lastResults"/>），树视图按容器路径惰性分层。
    /// 用 Click 而不是 Checked：点击已选中的 ToggleButton 会先取消勾选，
    /// 这里在每次点击后强制两态互斥。</summary>
    private void AssetViewMode_Click(object sender, RoutedEventArgs e)
    {
        // XAML 解析期间事件可能先于其他元素就绪：跳过首次触发。
        if (AssetTree is null || AssetViewListToggle is null || AssetViewTreeToggle is null) return;
        var treeSelected = ReferenceEquals(sender, AssetViewTreeToggle);
        AssetViewListToggle.IsChecked = !treeSelected;
        AssetViewTreeToggle.IsChecked = treeSelected;
        _treeMode = treeSelected;
        AssetList.Visibility = treeSelected ? Visibility.Collapsed : Visibility.Visible;
        AssetTree.Visibility = treeSelected ? Visibility.Visible : Visibility.Collapsed;
        if (_treeMode) RebuildTree();
    }

    private void RebuildTree()
    {
        if (_lastResults is null) { AssetTree.ItemsSource = null; return; }
        AssetTree.ItemsSource = AssetTreeBuilder.BuildRoots(_lastResults).Select(MakeTreeItem).ToList();
    }

    /// <summary>包装一个树节点：目录节点先放一个占位子项，真正展开时才
    /// 生成下一层（40 万级索引也不会在构建树时卡顿）。</summary>
    private static TreeViewItem MakeTreeItem(AssetTreeNode node)
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
        if (e.OriginalSource is not TreeViewItem item ||
            item.Tag is not AssetTreeNode node || node.IsLeaf) return;
        if (item.Items.Count == 1 && item.Items[0] is not AssetTreeNode)
        {
            item.Items.Clear();
            foreach (var child in node.Expand())
                item.Items.Add(MakeTreeItem(child));
        }
    }

    private void AssetTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: AssetTreeNode { IsLeaf: true } leaf })
            ApplyAssetSelection(leaf.Asset);
    }

    private void AssetTree_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AssetTree.SelectedItem is TreeViewItem { Tag: AssetTreeNode { IsLeaf: true, Asset: { } asset } })
            ActivateDefaultAction(asset);
    }

    // ── 辅助 ─────────────────────────────────────────────────────────

    /// <summary>对话框/消息框的宿主窗口（页面本身不是 Window）。</summary>
    private Window OwnerWindow => Window.GetWindow(this) ?? System.Windows.Application.Current.MainWindow;

    private void ShowError(string title, Exception ex)
    {
        _host.SetStatus($"{title}：{ex.Message}");
        MessageBox.Show(OwnerWindow, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
