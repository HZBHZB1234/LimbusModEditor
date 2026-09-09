using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
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
    private readonly ImagePreviewService _imagePreview = new();
    private readonly ImageAtlasEditService _atlasEdits = new();
    private readonly SpriteMetadataEditService _spriteEdits = new();
    private readonly UnityFieldEditService _unityFieldEdits = new();
    private readonly DispatcherTimer _searchTimer;
    private readonly UiStateService _uiState;
    private readonly string _uiStateFile;
    private int _searchGeneration;
    private int _previewGeneration;
    // 音频试听：解码出的 WAV 临时文件 + WPF MediaPlayer（切换选中/关窗即停）。
    private System.Windows.Media.MediaPlayer? _previewPlayer;
    private string? _previewAudioFile;
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
    }

    /// <summary>当前选中的资源（宿主拖放判定用）。</summary>
    public AssetRecord? SelectedAsset => _selectedAsset;

    /// <summary>宿主刷新项目状态时调用：重算计数并重跑搜索（选中项按 AssetId 保留）。</summary>
    public void OnProjectRefreshed()
    {
        var project = _host.Project;
        AssetCountText.Text = (project?.Assets.Count ?? 0).ToString();
        // 与提示条/一键导出同一口径：按编辑标记统计「已修改资源」数，
        // 而不是 Edits 历史条数（重复替换会让历史虚增）。
        EditCountText.Text = (project?.Assets.Count(AssetEditService.HasEdits) ?? 0).ToString();
        RefreshAssetList();
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
        _searchTimer.Stop();
        _ = RunSearchAsync();
    }

    /// <summary>复选框类筛选（「仅显示容器内资源」/「仅已替换」）：XAML 里的
    /// 初始 IsChecked 会在解析期间就触发 Checked，此时 <see cref="_searchTimer"/>
    /// 还没建好（构造函数在 InitializeComponent 之后才赋值），必须跳过。</summary>
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_searchTimer is null || _host.Project is null) return;
        _searchTimer.Stop();
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
        _searchTimer.Stop();
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
        var selectedType = (TypeFilter.SelectedItem as dynamic)?.Value as AssetType?;
        var selectedState = (StateFilter.SelectedItem as dynamic)?.Value as AssetEditState?;
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
            ContainerOnlyFilter?.IsChecked == true ? true : null);
    }

    /// <summary>后台线程过滤 + 排序（快照先在 UI 线程取好，避免与集合修改
    /// 竞争），带代际守卫：过期结果直接丢弃；选中项按 AssetId 跨刷新保留。
    /// 列表承载 <see cref="AssetRow"/>（显示层），树视图仍按底层记录构建。</summary>
    private async Task RunSearchAsync()
    {
        var project = _host.Project;
        if (project is null) { AssetList.ItemsSource = null; _lastResults = null; AssetTree.ItemsSource = null; return; }
        var generation = ++_searchGeneration;
        var query = BuildSearchQuery();
        var snapshot = project.Assets.ToArray();
        IReadOnlyList<AssetRecord> results;
        try { results = await Task.Run(() => _search.Search(snapshot, query)); }
        catch (ArgumentException) { return; }
        if (generation != _searchGeneration) return;
        _lastResults = results;
        var selectedId = (AssetList.SelectedItem as AssetRow)?.AssetId;
        AssetList.ItemsSource = results.Select(a => new AssetRow(a)).ToList();
        if (selectedId is { } id && AssetList.ItemsSource is IEnumerable<AssetRow> rows)
        {
            var restored = rows.FirstOrDefault(x => x.AssetId == id);
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

    /// <summary>异步预览：纹理解码（可能是 DXT 压缩的大图）放在后台线程，
    /// 代际守卫保证快速切换选中项时旧结果不会覆盖新选中项的预览。
    /// 三种形态：图像（Texture/Sprite）、文本（Text/JSON）、音频（可试听）。</summary>
    private async Task UpdatePreviewAsync(AssetRecord? asset)
    {
        var generation = ++_previewGeneration;
        ResetPreview();
        if (asset is null) return;
        if (asset.Type is not (AssetType.Texture or AssetType.Sprite))
        {
            if (TextPreviewService.LooksTextual(asset)) { await ShowTextPreviewAsync(asset, generation); return; }
            if (asset.Type == AssetType.Audio) { ShowAudioPreview(asset); return; }
            return;
        }
        if (asset.UnityPathId.HasValue && asset.Metadata.TryGetValue("unityBundle", out var isBundle) && isBundle == "true" &&
            !(asset.Metadata.TryGetValue("replacementPath", out var existingReplacement) && File.Exists(existingReplacement)) &&
            !string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath))
        {
            var source = asset.SourcePath;
            var pathId = asset.UnityPathId.Value;
            try
            {
                var (png, summary) = await Task.Run(() =>
                {
                    var unityService = new LimbusModEditor.Formats.Unity.UnityAssetService();
                    var decoded = unityService.ReadTexturePng(source, pathId);
                    string? described = null;
                    if (decoded is not null)
                    {
                        try { described = unityService.ReadTextureSummary(source, pathId)?.Describe(); }
                        catch (Exception) { described = null; }
                    }
                    return (decoded, described);
                });
                if (generation != _previewGeneration) return;
                if (png is not null)
                {
                    SetPreviewBitmap(png);
                    PreviewInfoText.Text = summary ?? "Unity Texture2D PNG preview";
                    return;
                }
            }
            catch (Exception ex)
            {
                if (generation != _previewGeneration) return;
                PreviewInfoText.Text = $"Unity texture preview failed: {ex.Message}";
                return;
            }
            if (generation != _previewGeneration) return;
        }
        var replacement = asset.Metadata.TryGetValue("replacementPath", out var path) ? path : null;
        if (string.IsNullOrWhiteSpace(replacement) || !File.Exists(replacement))
            replacement = asset.SourcePath;
        if (string.IsNullOrWhiteSpace(replacement) || !File.Exists(replacement) || !ImagePreviewService.IsSupportedExtension(Path.GetExtension(replacement))) return;
        try
        {
            var preview = await Task.Run(() => _imagePreview.CreatePreviewFromFile(replacement));
            if (generation != _previewGeneration) return;
            SetPreviewBitmap(preview.ThumbnailPng);
            PreviewInfoText.Text = $"{preview.Width} × {preview.Height} · {preview.Format}" + (preview.HasAlpha ? " · Alpha" : string.Empty);
        }
        catch (Exception ex)
        {
            if (generation != _previewGeneration) return;
            PreviewInfoText.Text = $"预览失败：{ex.Message}";
        }
    }

    /// <summary>文本 / JSON 预览：右栏直接显示可读正文（不是十六进制转储）。
    /// 非 UTF-8/UTF-16 或二进制负载明确说明原因，并指向十六进制预览。</summary>
    private async Task ShowTextPreviewAsync(AssetRecord asset, int generation)
    {
        TextPreview? preview;
        try { preview = await Task.Run(() => TextPreviewService.TryPreview(asset)); }
        catch (Exception ex)
        {
            if (generation != _previewGeneration) return;
            PreviewInfoText.Text = $"文本预览失败：{ex.Message}";
            return;
        }
        if (generation != _previewGeneration) return;
        if (preview is null)
        {
            PreviewInfoText.Text = "这个文件不是可读文本（非 UTF-8 / UTF-16，或含二进制内容）；可用「十六进制预览」查看原始字节。";
            return;
        }
        PreviewTextBox.Text = preview.Text;
        PreviewTextPanel.Visibility = Visibility.Visible;
        var size = new HumanSizeConverter().Convert(preview.TotalBytes, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string;
        PreviewInfoText.Text = $"{preview.EncodingName} · {size}" + (preview.Truncated ? " · 仅显示开头部分" : string.Empty);
    }

    /// <summary>音频预览面板：能解码的（Bank FSB + 本机 FMOD DLL）给「试听」按钮，
    /// 不能解码的说明缺什么，不装作能播。</summary>
    private void ShowAudioPreview(AssetRecord asset)
    {
        PreviewAudioPanel.Visibility = Visibility.Visible;
        var isBankFsb = asset.LogicalPath.StartsWith("fsb/", StringComparison.OrdinalIgnoreCase);
        var fmodDirectory = _host.Env.EffectiveFmodLibraryDirectory(_host.Project);
        var canDecode = isBankFsb && !string.IsNullOrWhiteSpace(fmodDirectory) && Directory.Exists(fmodDirectory);
        PreviewAudioButton.IsEnabled = canDecode;
        PreviewAudioText.Text = canDecode
            ? "解码成 WAV 在本机播放（不会写入项目）"
            : isBankFsb
                ? "需要 FMOD DLL 才能解码试听：把 fmod64.dll / fsbank64.dll 放进程序目录的 fmod\\ 后重试。"
                : "Unity 音频对象暂不支持解码试听；Bank 音频可在「高级操作」里导出 WAV。";
        PreviewInfoText.Text = "音频预览";
    }

    /// <summary>清空右栏预览区（切换选中项 / 无选中时调用），并停止正在播放的试听。</summary>
    private void ResetPreview()
    {
        PreviewImage.Source = null;
        PreviewImagePanel.Visibility = Visibility.Collapsed;
        PreviewTextPanel.Visibility = Visibility.Collapsed;
        PreviewTextBox.Text = string.Empty;
        PreviewAudioPanel.Visibility = Visibility.Collapsed;
        PreviewAudioButton.IsEnabled = false;
        PreviewAudioButton.Content = "▶ 试听";
        PreviewAudioText.Text = string.Empty;
        PreviewInfoText.Text = "无预览";
        StopAudioPreview();
    }

    /// <summary>试听 Bank 音频：FSB → WAV（临时文件）→ WPF MediaPlayer 播放。
    /// 再点一次停止；切换选中项或关闭窗口时自动停止并删除临时文件。</summary>
    private async void PreviewAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAsset is not AssetRecord asset || asset.Type != AssetType.Audio) return;
        if (_previewPlayer is not null)
        {
            StopAudioPreview();
            PreviewAudioButton.Content = "▶ 试听";
            PreviewAudioText.Text = "已停止";
            return;
        }
        var fmodDirectory = _host.Env.EffectiveFmodLibraryDirectory(_host.Project);
        if (string.IsNullOrWhiteSpace(fmodDirectory) || !Directory.Exists(fmodDirectory))
        {
            PreviewAudioText.Text = "需要 FMOD DLL 才能解码试听：把 fmod64.dll / fsbank64.dll 放进程序目录的 fmod\\ 后重试。";
            return;
        }
        var generation = _previewGeneration;
        PreviewAudioButton.IsEnabled = false;
        PreviewAudioText.Text = "正在解码…";
        try
        {
            var wav = await Task.Run(async () =>
            {
                using var codec = new NativeFmodAudioCodec(fmodDirectory);
                return await new BankAudioService().DecodeToWaveAsync(asset, codec);
            });
            if (generation != _previewGeneration) return;
            var file = Path.Combine(Path.GetTempPath(), $"lme-preview-{Guid.NewGuid():N}.wav");
            await File.WriteAllBytesAsync(file, wav);
            if (generation != _previewGeneration) { TryDelete(file); return; }
            var player = new System.Windows.Media.MediaPlayer();
            player.MediaEnded += (_, _) => EndAudioPreview(player);
            player.MediaFailed += (_, args) =>
            {
                EndAudioPreview(player);
                PreviewAudioText.Text = $"播放失败：{args.ErrorException?.Message ?? "未知原因"}";
            };
            player.Open(new Uri(file));
            _previewPlayer = player;
            _previewAudioFile = file;
            player.Play();
            PreviewAudioButton.Content = "■ 停止";
            PreviewAudioText.Text = $"WAV {wav.Length / 1024} KB · 正在播放";
        }
        catch (Exception ex)
        {
            StopAudioPreview();
            PreviewAudioText.Text = $"试听失败：{ex.Message}";
        }
        finally
        {
            PreviewAudioButton.IsEnabled = _selectedAsset?.Type == AssetType.Audio;
        }
    }

    private void EndAudioPreview(System.Windows.Media.MediaPlayer player)
    {
        if (!ReferenceEquals(_previewPlayer, player)) return; // 已经停止 / 换成另一个资源了
        StopAudioPreview();
        PreviewAudioButton.Content = "▶ 试听";
        PreviewAudioText.Text = "播放结束";
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

    private void SetPreviewBitmap(byte[] png)
    {
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(png);
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        PreviewImage.Source = bitmap;
        PreviewImagePanel.Visibility = Visibility.Visible;
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
