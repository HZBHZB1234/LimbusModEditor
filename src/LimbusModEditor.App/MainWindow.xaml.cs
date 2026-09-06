using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Editing.Images;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Debugging;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.App;

public partial class MainWindow : Window
{
    private readonly IProjectService _projects = new ProjectService();
    private readonly ModImportService _importer = new(BuiltInFormatRegistry.Create());
    private readonly AssetSearchService _search = new();
    private readonly AssetEditService _assetEdits = new();
    private readonly ImagePreviewService _imagePreview = new();
    private readonly ImageAtlasEditService _atlasEdits = new();
    private readonly SpriteMetadataEditService _spriteEdits = new();
    private readonly UnityFieldEditService _unityFieldEdits = new();
    private readonly ProjectBuildService _builder = new();
    private readonly UnityBundleBuildService _unityBuilder = new();
    private readonly UnitySerializedFileBuildService _serializedBuilder = new();
    private readonly ModExportService _exporter = new(BuiltInFormatRegistry.Create());
    private readonly DebugApplyService _debugApply = new();
    private readonly GameLaunchService _gameLaunch = new();
    private DebugApplySession? _debugSession;
    private ModProject? _project;
    private string? _projectFile;

    public MainWindow() => InitializeComponent();

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "LME 项目 (*.lmeproj)|*.lmeproj",
            FileName = "MyMod.lmeproj",
            Title = "选择新模组项目位置"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var directory = Path.GetDirectoryName(dialog.FileName)!;
            _project = await _projects.CreateAsync(directory, Path.GetFileNameWithoutExtension(dialog.FileName));
            _projectFile = dialog.FileName;
            RefreshProjectState("已创建项目");
        }
        catch (Exception ex) { ShowError("创建项目失败", ex); }
    }

    /// <summary>P3.1: new-mod wizard scaffolds a project plus a minimal,
    /// handler-validated template package, then opens the project.</summary>
    private async void NewModWizard_Click(object sender, RoutedEventArgs e)
    {
        var service = new LimbusModEditor.Application.Build.NewModTemplateService(_projects);
        var wizard = new NewModWizardWindow(service) { Owner = this };
        if (wizard.ShowDialog() != true || wizard.Result is null) return;
        try
        {
            _project = await _projects.LoadAsync(wizard.Result.ProjectFile);
            _projectFile = wizard.Result.ProjectFile;
            RefreshProjectState("已通过向导创建项目");
        }
        catch (Exception ex) { ShowError("打开新建项目失败", ex); }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "LME 项目 (*.lmeproj)|*.lmeproj|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _project = await _projects.LoadAsync(dialog.FileName);
            _projectFile = dialog.FileName;
            RefreshProjectState("已打开项目");
        }
        catch (Exception ex) { ShowError("打开项目失败", ex); }
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        try
        {
            await _projects.SaveAsync(_project, _projectFile);
            StatusText.Text = "项目已保存";
        }
        catch (Exception ex) { ShowError("保存项目失败", ex); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { StatusText.Text = "请先创建或打开项目"; return; }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "支持的模组/Unity资源 (*.carra;*.carra2;*.rebank;*.bank;*.zip;*.bundle)|*.carra;*.carra2;*.rebank;*.bank;*.zip;*.bundle|所有文件 (*.*)|*.*",
            Multiselect = true,
            Title = "选择要导入的模组或资源包"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var added = 0;
            var updated = 0;
            foreach (var file in dialog.FileNames)
            {
                var extension = Path.GetExtension(file);
                var result = extension.Equals(".bundle", StringComparison.OrdinalIgnoreCase)
                    ? await _importer.ImportUnityBundleIntoProjectAsync(file, _project)
                    : extension.Equals(".assets", StringComparison.OrdinalIgnoreCase)
                        ? await _importer.ImportSerializedFileIntoProjectAsync(file, _project)
                        : await _importer.ImportIntoProjectAsync(file, _project);
                added += result.AddedAssets;
                updated += result.UpdatedAssets;
            }
            if (_project is not null && _projectFile is not null) await _projects.SaveAsync(_project, _projectFile);
            RefreshProjectState($"导入完成：新增 {added}，更新 {updated}");
        }
        catch (Exception ex) { ShowError("导入失败", ex); }
    }

    private async void ImportDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { StatusText.Text = "请先创建或打开项目"; return; }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = false,
            ValidateNames = false,
            FileName = "选择此文件夹",
            Title = "选择要纳入项目的资源文件夹"
        };
        if (dialog.ShowDialog() != true) return;
        var directory = Path.GetDirectoryName(dialog.FileName);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        try
        {
            var result = await _importer.ImportDirectoryIntoProjectAsync(directory, _project);
            if (_projectFile is not null) await _projects.SaveAsync(_project, _projectFile);
            RefreshProjectState($"文件夹导入完成：新增 {result.AddedAssets}，更新 {result.UpdatedAssets}");
        }
        catch (Exception ex) { ShowError("导入文件夹失败", ex); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { StatusText.Text = "请先创建或打开项目"; return; }
        string? selectedSource = null;
        if (!_project.Sources.Any())
        {
            var sourceDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "支持的源模组 (*.carra;*.carra2;*.rebank;*.bank;*.zip)|*.carra;*.carra2;*.rebank;*.bank;*.zip|所有文件 (*.*)|*.*",
                Title = "选择源模组（项目还没有源时必选）"
            };
            if (sourceDialog.ShowDialog() != true) return;
            selectedSource = sourceDialog.FileName;
        }
        var wizard = new ExportWizardWindow(_project, selectedSource) { Owner = this };
        if (wizard.ShowDialog() != true || wizard.Result is not { } choice) return;
        try
        {
            using NativeFmodAudioCodec? nativeCodec = choice.Target == LimbusModEditor.Domain.Formats.ModFormatKind.Bank && !string.IsNullOrWhiteSpace(_project.FmodLibraryDirectory) && Directory.Exists(_project.FmodLibraryDirectory)
                ? new NativeFmodAudioCodec(_project.FmodLibraryDirectory) : null;
            var result = await _exporter.ExportWithEditsAsync(selectedSource, _project, choice.OutputPath, choice.Target, nativeCodec);
            StatusText.Text = $"导出完成：应用 {result.AppliedReplacements} 个替换 → {result.OutputPath}";
            new ExportReportWindow(result) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { ShowError("导出模组失败", ex); }
    }

    /// <summary>P3.3 工作流: batch-register replacements from a folder, matching
    /// files to assets by file name.</summary>
    private async void BatchReplace_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = false,
            ValidateNames = false,
            FileName = "选择此文件夹",
            Title = "选择替换文件所在文件夹（按文件名匹配资源）"
        };
        if (dialog.ShowDialog() != true) return;
        var folder = Path.GetDirectoryName(dialog.FileName);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try
        {
            var report = await _assetEdits.BatchReplaceFromDirectoryAsync(_project, folder, Path.GetDirectoryName(_projectFile)!);
            await _projects.SaveAsync(_project, _projectFile);
            RefreshProjectState($"批量替换已登记（{report.Matched} 个）");
            var detail = report.Describe();
            if (report.FilesWithoutAsset.Count > 0)
                detail += "\n\n没有对应资源的文件（前 15 个）：\n" + string.Join("\n", report.FilesWithoutAsset.Take(15));
            MessageBox.Show(this, detail, "批量登记替换", MessageBoxButton.OK,
                report.Matched > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError("批量登记替换失败", ex); }
    }

    /// <summary>P3.4: hex dump of the selected asset's payload for
    /// identifying unknown data before replacing it.</summary>
    private void HexPreview_Click(object sender, RoutedEventArgs e)
    {
        if (AssetList.SelectedItem is not AssetRecord asset) return;
        try { new HexPreviewWindow(asset) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { ShowError("十六进制预览失败", ex); }
    }

    /// <summary>自动获取资源地址（续）：list verified Unity-cache candidates
    /// (directories that actually contain .bundle files) and let the user pick.</summary>
    private async void SuggestUnityCache_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        try
        {
            var candidates = LimbusModEditor.Application.Debugging.UnityCacheLocator.SuggestCandidates(_project.GameDirectory);
            if (candidates.Count == 0)
            {
                MessageBox.Show(this, "未在游戏目录与 LocalLow 中找到包含 .bundle 的候选目录。\n请手动选择 Unity 缓存目录。", "自动建议失败", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var picker = new UnityCachePickerWindow(candidates) { Owner = this };
            if (picker.ShowDialog() != true || picker.Selected is null) return;
            _project.UnityCacheDirectory = picker.Selected.Path;
            await _projects.SaveAsync(_project, _projectFile);
            StatusText.Text = $"已设置 Unity 缓存目录：{picker.Selected.Path}（含 {picker.Selected.EntryCount} 个缓存条目）";
        }
        catch (Exception ex) { ShowError("自动建议 Unity 缓存目录失败", ex); }
    }

    /// <summary>自动获取资源地址：scan known Steam libraries for the Limbus
    /// Company install and fill the game directory automatically.</summary>
    private async void AutoLocateGame_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        try
        {
            var lookup = LimbusModEditor.Application.Debugging.GameDirectoryLocator.Scan(
                LimbusModEditor.Application.Debugging.GameDirectoryLocator.DefaultCandidateRoots());
            if (!lookup.Found)
            {
                MessageBox.Show(this, "未能在已知的 Steam 库中找到 LimbusCompany.exe。\n请手动选择游戏目录。", "自动定位失败", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _project.GameDirectory = lookup.GameDirectory;
            await _projects.SaveAsync(_project, _projectFile);
            StatusText.Text = $"已自动定位游戏目录：{lookup.GameDirectory}（{lookup.Method}）";
        }
        catch (Exception ex) { ShowError("自动定位游戏目录失败", ex); }
    }

    private async void SetGameDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = false,
            ValidateNames = false,
            FileName = "选择此文件夹",
            Title = "选择 Limbus Company 游戏目录"
        };
        if (dialog.ShowDialog() != true) return;
        var directory = Path.GetDirectoryName(dialog.FileName);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        _project.GameDirectory = directory;
        await _projects.SaveAsync(_project, _projectFile);
        StatusText.Text = $"游戏目录已设置：{directory}";
    }

    private async void SetUnityCacheDirectory_Click(object sender, RoutedEventArgs e)
    {
        await SetProjectDirectoryAsync(value => _project!.UnityCacheDirectory = value, "选择 Unity 缓存目录");
    }

    private async void SetModDirectory_Click(object sender, RoutedEventArgs e)
    {
        await SetProjectDirectoryAsync(value => _project!.ModDirectory = value, "选择模组安装目录");
    }

    private async void SetFmodDirectory_Click(object sender, RoutedEventArgs e)
    {
        await SetProjectDirectoryAsync(value => _project!.FmodLibraryDirectory = value, "选择 FMOD/FSBANK DLL 目录");
    }

    /// <summary>P2.2: probes the configured FMOD DLL directory (cached by file
    /// fingerprint) and shows the compatibility report without loading DLLs.</summary>
    private void ProbeFmod_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        var directory = _project.FmodLibraryDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            MessageBox.Show(this, "请先在右侧设置 FMOD DLL 目录（含 fmod64.dll / fsbank64.dll）。", "尚未配置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var cacheFile = Path.Combine(Path.GetDirectoryName(_projectFile)!, "logs", "fmod-probe.json");
            var cache = new LimbusModEditor.Formats.Bank.FmodCompatibilityService().Probe(directory, cacheFile);
            new FmodReportWindow(directory, cache) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { ShowError("FMOD DLL 检测失败", ex); }
    }

    private async Task SetProjectDirectoryAsync(Action<string> assign, string title)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = false,
            ValidateNames = false,
            FileName = "选择此文件夹",
            Title = title
        };
        if (dialog.ShowDialog() != true) return;
        var directory = Path.GetDirectoryName(dialog.FileName);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        assign(directory);
        await _projects.SaveAsync(_project, _projectFile);
        StatusText.Text = $"目录已设置：{directory}";
    }

    private async void DebugApply_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        var debugTarget = !string.IsNullOrWhiteSpace(_project.ModDirectory) && Directory.Exists(_project.ModDirectory)
            ? _project.ModDirectory
            : !string.IsNullOrWhiteSpace(_project.UnityCacheDirectory) && Directory.Exists(_project.UnityCacheDirectory)
                ? _project.UnityCacheDirectory : _project.GameDirectory;
        if (string.IsNullOrWhiteSpace(debugTarget) || !Directory.Exists(debugTarget))
        {
            StatusText.Text = "请先在项目配置中设置游戏目录";
            return;
        }
        try
        {
            var root = Path.GetDirectoryName(_projectFile)!;
            var overlay = Path.Combine(root, "builds", "debug-overlay");
            await _builder.BuildOverlayAsync(_project, root, overlay);
            await AddUnityBundlesToOverlayAsync(_project, root, overlay);
            await AddUnitySerializedFilesToOverlayAsync(_project, root, overlay);
            _debugSession = await _debugApply.ApplyAsync(_project, overlay, debugTarget);
            var launch = _gameLaunch.TryLaunch(_project.GameDirectory!, _project.GameExecutablePath);
            DebugStateText.Text = launch.Started
                ? $"已应用 {_debugSession.Changes.Count} 个文件，游戏已启动，退出前可恢复"
                : $"已应用 {_debugSession.Changes.Count} 个文件；{launch.Message}";
            StatusText.Text = launch.Started ? "调试覆盖层已应用，游戏已启动" : launch.Message;
        }
        catch (Exception ex) { ShowError("应用调试覆盖层失败", ex); }
    }

    private async void BuildOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        try
        {
            var root = Path.GetDirectoryName(_projectFile)!;
            var output = Path.Combine(root, "builds", "debug-overlay");
            var result = await _builder.BuildOverlayAsync(_project, root, output);
            var unity = await AddUnityBundlesToOverlayAsync(_project, root, output);
            var serialized = await AddUnitySerializedFilesToOverlayAsync(_project, root, output);
            DebugStateText.Text = $"覆盖层已构建：{result.AppliedEdits} 个文件编辑，{unity + serialized} 个 Unity 对象编辑";
            StatusText.Text = $"构建完成：{output}";
        }
        catch (Exception ex) { ShowError("构建覆盖层失败", ex); }
    }

    private async Task<int> AddUnityBundlesToOverlayAsync(ModProject project, string root, string overlay)
    {
        var unityOutput = Path.Combine(root, "builds", "unity-bundles");
        var bundles = await _unityBuilder.BuildAsync(project, unityOutput);
        foreach (var bundle in bundles)
        {
            var original = project.Assets.FirstOrDefault(x =>
                string.Equals(Path.GetFullPath(x.SourcePath ?? string.Empty), Path.GetFullPath(bundle.SourcePath), StringComparison.OrdinalIgnoreCase))
                ?.Metadata.GetValueOrDefault("originalSourcePath");
            var relative = GetSafeDebugRelativePath(project, original, Path.GetFileName(bundle.OutputPath));
            var target = Path.Combine(overlay, relative);
            await LimbusModEditor.Application.Build.AtomicOutput.CopyAsync(bundle.OutputPath, target);
        }
        return bundles.Sum(x => x.AppliedAssets);
    }

    private async Task<int> AddUnitySerializedFilesToOverlayAsync(ModProject project, string root, string overlay)
    {
        var outputRoot = Path.Combine(root, "builds", "unity-serialized");
        var files = await _serializedBuilder.BuildAsync(project, outputRoot);
        foreach (var file in files)
        {
            var original = project.Assets.FirstOrDefault(x =>
                string.Equals(Path.GetFullPath(x.SourcePath ?? string.Empty), Path.GetFullPath(file.SourcePath), StringComparison.OrdinalIgnoreCase))
                ?.Metadata.GetValueOrDefault("originalSourcePath");
            var relative = GetSafeDebugRelativePath(project, original, Path.GetFileName(file.OutputPath));
            var target = Path.Combine(overlay, relative);
            await LimbusModEditor.Application.Build.AtomicOutput.CopyAsync(file.OutputPath, target);
        }
        return files.Sum(x => x.AppliedAssets);
    }

    private static string GetSafeDebugRelativePath(ModProject project, string? original, string fallback)
    {
        if (string.IsNullOrWhiteSpace(original)) return fallback;
        var source = Path.GetFullPath(original);
        foreach (var root in new[] { project.UnityCacheDirectory, project.GameDirectory, project.ModDirectory })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!source.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(fullRoot, source);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative)) return relative;
        }
        return fallback;
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_project is not null && !_project.RestoreDebugFilesOnClose) return;
        if (_debugSession is null || !_debugSession.IsApplied) return;
        try
        {
            await _debugApply.RestoreAsync(_debugSession);
            if (_debugSession.RestoreConflicts.Count > 0)
                MessageBox.Show(this, "以下游戏文件在调试期间已被其他程序修改，编辑器未覆盖恢复：\n" + string.Join("\n", _debugSession.RestoreConflicts), "调试恢复冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { MessageBox.Show(this, $"恢复游戏文件失败：{ex.Message}", "调试恢复失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RefreshProjectState(string? status = null)
    {
        ProjectNameText.Text = _project?.Name ?? "未打开项目";
        AssetCountText.Text = (_project?.Assets.Count ?? 0).ToString();
        EditCountText.Text = (_project?.Edits.Count ?? 0).ToString();
        RefreshAssetList();
        if (status is not null) StatusText.Text = $"{status}：{_project?.Name}";
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshAssetList();

    /// <summary>P3.3: populates the type/state filter combos once.</summary>
    private bool _filtersInitialized;
    private void Filter_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_filtersInitialized) return;
        RefreshAssetList();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => RefreshAssetList();

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        TypeFilter.SelectedIndex = -1;
        StateFilter.SelectedIndex = -1;
        PathIdFilter.Text = string.Empty;
        TypeIdFilter.Text = string.Empty;
        MinSizeFilter.Text = string.Empty;
        MaxSizeFilter.Text = string.Empty;
        ReplacedOnlyFilter.IsChecked = false;
        RefreshAssetList();
    }

    private static long? ParseLongFilter(System.Windows.Controls.TextBox box)
    {
        var text = box.Text.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return long.TryParse(text, out var value) ? value : long.MinValue;
    }

    private void AssetList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var asset = AssetList.SelectedItem as AssetRecord;
        SelectedAssetPathText.Text = asset?.LogicalPath ?? "未选择";
        SelectedAssetTypeText.Text = asset?.Type.ToString() ?? string.Empty;
        SelectedPathIdText.Text = asset?.UnityPathId?.ToString() ?? "—";
        SelectedContainerText.Text = asset?.ContainerPath ?? "—";
        SelectedStateText.Text = asset?.EditState.ToString() ?? string.Empty;
        RefreshSelectionButtons(asset);
        UpdatePreview(asset);
    }

    /// <summary>Recomputes every per-selection action button from the current
    /// project/selection state; also used after long operations to restore
    /// buttons disabled during the operation.</summary>
    private void RefreshSelectionButtons(AssetRecord? asset)
    {
        ReplaceAssetButton.IsEnabled = asset is not null && _projectFile is not null;
        HexPreviewButton.IsEnabled = asset is not null;
        SpriteMetadataButton.IsEnabled = asset?.Type == AssetType.Sprite &&
            asset.UnityPathId.HasValue && !string.IsNullOrWhiteSpace(asset.SourcePath) &&
            File.Exists(asset.SourcePath) && _projectFile is not null;
        UnityFieldsButton.IsEnabled = asset?.UnityPathId.HasValue == true &&
            (asset.Metadata.ContainsKey("unityBundle") || asset.Metadata.ContainsKey("unitySerializedFile")) &&
            !string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath) && _projectFile is not null;
        ReferencersButton.IsEnabled = UnityFieldsButton.IsEnabled;
        ObjectSummaryButton.IsEnabled = asset?.UnityPathId.HasValue == true &&
            asset.Type is AssetType.Mesh or AssetType.Animation or AssetType.Font &&
            (asset.Metadata.ContainsKey("unityBundle") || asset.Metadata.ContainsKey("unitySerializedFile")) &&
            !string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath) && _projectFile is not null;
        DecodeAudioButton.IsEnabled = asset?.Type == AssetType.Audio &&
            asset.LogicalPath.StartsWith("fsb/", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(_project?.FmodLibraryDirectory) &&
            Directory.Exists(_project.FmodLibraryDirectory) && _projectFile is not null;
        FsbInspectButton.IsEnabled = asset?.Type == AssetType.Audio &&
            asset.LogicalPath.StartsWith("fsb/", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath) && _projectFile is not null;
        var isImage = asset?.Type is AssetType.Texture or AssetType.Sprite;
        AtlasPanel.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        SplitAtlasButton.IsEnabled = isImage && _projectFile is not null;
        RepackAtlasButton.IsEnabled = isImage && asset?.Metadata.ContainsKey("atlasLayoutPath") == true && _projectFile is not null;
    }

    private void UpdatePreview(AssetRecord? asset)
    {
        PreviewImage.Source = null;
        PreviewInfoText.Text = "无图像预览";
        if (asset is null || asset.Type is not (AssetType.Texture or AssetType.Sprite)) return;
        if (asset.UnityPathId.HasValue && asset.Metadata.TryGetValue("unityBundle", out var isBundle) && isBundle == "true" &&
            !(asset.Metadata.TryGetValue("replacementPath", out var existingReplacement) && File.Exists(existingReplacement)) &&
            !string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath))
        {
            try
            {
                var unityService = new LimbusModEditor.Formats.Unity.UnityAssetService();
                var png = unityService.ReadTexturePng(asset.SourcePath, asset.UnityPathId.Value);
                if (png is not null)
                {
                    SetPreviewBitmap(png);
                    try { PreviewInfoText.Text = unityService.ReadTextureSummary(asset.SourcePath, asset.UnityPathId.Value)?.Describe() ?? "Unity Texture2D PNG preview"; }
                    catch (Exception) { PreviewInfoText.Text = "Unity Texture2D PNG preview"; }
                    return;
                }
            }
            catch (Exception ex) { PreviewInfoText.Text = $"Unity texture preview failed: {ex.Message}"; return; }
        }
        var replacement = asset.Metadata.TryGetValue("replacementPath", out var path) ? path : null;
        if (string.IsNullOrWhiteSpace(replacement) || !File.Exists(replacement))
            replacement = asset.SourcePath;
        if (string.IsNullOrWhiteSpace(replacement) || !File.Exists(replacement) || !ImagePreviewService.IsSupportedExtension(Path.GetExtension(replacement))) return;
        try
        {
            var preview = _imagePreview.CreatePreviewFromFile(replacement);
            SetPreviewBitmap(preview.ThumbnailPng);
            PreviewInfoText.Text = $"{preview.Width} × {preview.Height} · {preview.Format}" + (preview.HasAlpha ? " · Alpha" : string.Empty);
        }
        catch (Exception ex) { PreviewInfoText.Text = $"预览失败：{ex.Message}"; }
    }

    private void SetPreviewBitmap(byte[] png)
    {
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(png);
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        PreviewImage.Source = bitmap;
    }

    private async void SplitAtlas_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null || AssetList.SelectedItem is not AssetRecord asset) return;
        if (!TryGetImageSource(asset, out var source)) { StatusText.Text = "当前资源没有可读取的本地图像文件"; return; }
        if (!int.TryParse(AtlasColumnsBox.Text, out var columns) || !int.TryParse(AtlasRowsBox.Text, out var rows) || columns <= 0 || rows <= 0)
        { StatusText.Text = "列数和行数必须是正整数"; return; }
        try
        {
            var result = await _atlasEdits.SplitAsync(_project, asset.AssetId, source, Path.GetDirectoryName(_projectFile)!, columns, rows);
            await _projects.SaveAsync(_project, _projectFile);
            RepackAtlasButton.IsEnabled = true;
            StatusText.Text = $"图集已拆分：{result.Layout.Regions.Count} 个区域";
        }
        catch (Exception ex) { ShowError("拆分图集失败", ex); }
    }

    private async void RepackAtlas_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null || AssetList.SelectedItem is not AssetRecord asset) return;
        try
        {
            await _atlasEdits.RepackAsync(_project, asset.AssetId, Path.GetDirectoryName(_projectFile)!);
            await _projects.SaveAsync(_project, _projectFile);
            RefreshProjectState("图集已恢复并记录替换");
            AssetList.SelectedItem = asset;
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
        if (_project is null || _projectFile is null || AssetList.SelectedItem is not AssetRecord asset) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = asset.Type switch
            {
                AssetType.Texture or AssetType.Sprite => "图像文件 (*.png;*.jpg;*.jpeg;*.tga)|*.png;*.jpg;*.jpeg;*.tga|所有文件 (*.*)|*.*",
                AssetType.Audio => "音频文件 (*.wav;*.fsb)|*.wav;*.fsb|所有文件 (*.*)|*.*",
                _ => "所有文件 (*.*)|*.*"
            },
            Title = $"替换资源：{asset.LogicalPath}"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _assetEdits.ReplaceFromFileAsync(_project, asset.AssetId, dialog.FileName, Path.GetDirectoryName(_projectFile)!);
            await _projects.SaveAsync(_project, _projectFile);
            RefreshProjectState("资源替换已记录");
            AssetList.SelectedItem = asset;
        }
        catch (Exception ex) { ShowError("替换资源失败", ex); }
    }

    private async void InspectSprite_Click(object sender, RoutedEventArgs e)
    {
        if (AssetList.SelectedItem is not AssetRecord asset || asset.Type != AssetType.Sprite ||
            !asset.UnityPathId.HasValue || string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath)) return;
        try
        {
            var sprite = new LimbusModEditor.Formats.Unity.UnityAssetService().ReadSprite(asset.SourcePath, asset.UnityPathId.Value);
            if (sprite is null) { StatusText.Text = "未找到 Sprite 对象。"; return; }
            var initial = _spriteEdits.ReadStored(asset) ?? UnitySpriteMetadata.From(sprite);
            var dialog = new SpriteMetadataWindow(initial) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is null) return;
            _spriteEdits.Set(_project!, asset, dialog.Result);
            if (_project is not null && _projectFile is not null) await _projects.SaveAsync(_project, _projectFile);
            RefreshProjectState("Sprite 元数据修改已记录");
            AssetList.SelectedItem = asset;
        }
        catch (Exception ex) { ShowError("读取 Sprite 元数据失败", ex); }
    }

    private async void EditUnityFields_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null || AssetList.SelectedItem is not AssetRecord asset ||
            !asset.UnityPathId.HasValue || string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath)) return;
        UnityFieldsButton.IsEnabled = false;
        StatusText.Text = "正在读取对象字段…";
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
            var dialog = new UnityFieldEditorWindow(fields, stored, scriptInfo, dependencies, inFileObjects) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is null) return;
            if (dialog.Result.Count == 0) return;
            _unityFieldEdits.Set(_project, asset, fields, dialog.Result);
            await _projects.SaveAsync(_project, _projectFile);
            RefreshProjectState("Unity 字段修改已记录");
            AssetList.SelectedItem = asset;
        }
        catch (Exception ex) { ShowError("Unity 字段读取或保存失败", ex); }
        finally
        {
            RefreshSelectionButtons(AssetList.SelectedItem as AssetRecord);
            StatusText.Text = "就绪";
        }
    }

    /// <summary>P2.1: read-only FSB5 structural inspection for the selected
    /// Bank audio entry; unknown/encrypted payloads are explained in-window.</summary>
    private async void InspectFsb_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || AssetList.SelectedItem is not AssetRecord asset ||
            string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath)) return;
        try
        {
            var inspection = await new BankAudioService().InspectFsbAsync(asset);
            new BankInspectorWindow(asset, inspection) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { ShowError("FSB 结构检查失败", ex); }
    }

    private void Help_Click(object sender, RoutedEventArgs e)
        => new HelpWindow { Owner = this }.ShowDialog();

    /// <summary>Answers "who points at this object" for the selected Unity
    /// object: same-file referencers plus, for bundles, cross-file referencers
    /// from every other SerializedFile inside the same bundle. The scan runs
    /// off the UI thread because it walks every object in the file/bundle.</summary>
    private async void FindReferencers_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || AssetList.SelectedItem is not AssetRecord asset ||
            !asset.UnityPathId.HasValue || string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath)) return;
        ReferencersButton.IsEnabled = false;
        StatusText.Text = "正在扫描引用者…";
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
                MessageBox.Show(this,
                    $"没有发现任何对象引用 Path {pathId}（{asset.Type}）。\n修改或替换它不会破坏其他对象。",
                    "引用者检查", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var lines = referencers
                .OrderBy(r => r.SourcePathId)
                .Select(r => (r.OriginatingFile is { } file ? $"[{file}] " : string.Empty) +
                             $"Path {r.SourcePathId}（{r.SourceTypeName ?? "未知类型"}）字段 {r.FieldPath}");
            MessageBox.Show(this,
                $"有 {referencers.Count} 个指针引用 Path {pathId}（{asset.Type}）：\n\n" +
                string.Join(Environment.NewLine, lines) +
                "\n\n修改此对象前请确认这些指针仍然有效；把指针改成空引用是允许的，改成不存在的 Path ID 会在保存时被拒绝。",
                "引用者检查", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError("引用者检查失败", ex); }
        finally
        {
            RefreshSelectionButtons(AssetList.SelectedItem as AssetRecord);
            StatusText.Text = "就绪";
        }
    }

    /// <summary>P1.5: read-only structural summary of the selected Mesh /
    /// AnimationClip / Font object; values come from the type tree, missing
    /// fields are reported instead of guessed.</summary>
    private async void ShowObjectSummary_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || AssetList.SelectedItem is not AssetRecord asset ||
            !asset.UnityPathId.HasValue || string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath)) return;
        var service = new LimbusModEditor.Formats.Unity.UnityAssetService();
        var isBundle = asset.Metadata.ContainsKey("unityBundle") && !string.IsNullOrWhiteSpace(asset.ContainerPath);
        var source = asset.SourcePath;
        var container = asset.ContainerPath;
        var pathId = asset.UnityPathId.Value;
        ObjectSummaryButton.IsEnabled = false;
        StatusText.Text = "正在生成对象摘要…";
        try
        {
            var summary = await Task.Run(() => isBundle
                ? service.ReadBundleObjectSummary(source, container!, pathId)
                : service.ReadObjectSummary(source, pathId));
            if (summary is null)
            {
                MessageBox.Show(this,
                    "该对象类型暂无摘要支持（当前支持 Mesh / AnimationClip / Font）。字段树可通过「Unity 字段编辑」查看。",
                    "对象摘要", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            new ObjectSummaryWindow(summary) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { ShowError("对象摘要生成失败", ex); }
        finally
        {
            RefreshSelectionButtons(AssetList.SelectedItem as AssetRecord);
            StatusText.Text = "就绪";
        }
    }

    private async void DecodeAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || AssetList.SelectedItem is not AssetRecord asset || asset.Type != AssetType.Audio ||
            string.IsNullOrWhiteSpace(_project.FmodLibraryDirectory) || !Directory.Exists(_project.FmodLibraryDirectory)) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "WAV audio (*.wav)|*.wav|All files (*.*)|*.*",
            FileName = Path.GetFileName(asset.LogicalPath) + ".wav",
            Title = "导出 Bank 音频为 WAV"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            using var codec = new NativeFmodAudioCodec(_project.FmodLibraryDirectory);
            var data = await new BankAudioService().DecodeToWaveAsync(asset, codec);
            await File.WriteAllBytesAsync(dialog.FileName, data);
            StatusText.Text = $"音频已导出：{dialog.FileName}";
        }
        catch (Exception ex) { ShowError("导出 WAV 失败", ex); }
    }

    private void RefreshAssetList()
    {
        if (_project is null) { AssetList.ItemsSource = null; return; }
        if (!_filtersInitialized)
        {
            _filtersInitialized = true;
            TypeFilter.ItemsSource = Enum.GetValues<LimbusModEditor.Domain.Assets.AssetType>().Select(t => new
            {
                Value = (LimbusModEditor.Domain.Assets.AssetType?)t,
                Label = t == LimbusModEditor.Domain.Assets.AssetType.Unknown ? "（全部类型）" : t.ToString()
            });
            TypeFilter.DisplayMemberPath = "Label";
            TypeFilter.SelectedIndex = 0;
            StateFilter.ItemsSource = Enum.GetValues<LimbusModEditor.Domain.Assets.AssetEditState>().Select(s => new
            {
                Value = (LimbusModEditor.Domain.Assets.AssetEditState?)s,
                Label = s == LimbusModEditor.Domain.Assets.AssetEditState.Unchanged ? "（全部状态）" : s.ToString()
            });
            StateFilter.DisplayMemberPath = "Label";
            StateFilter.SelectedIndex = 0;
        }
        var selectedType = (TypeFilter.SelectedItem as dynamic)?.Value as LimbusModEditor.Domain.Assets.AssetType?;
        var selectedState = (StateFilter.SelectedItem as dynamic)?.Value as LimbusModEditor.Domain.Assets.AssetEditState?;
        long? minKb = null, maxKb = null;
        if (long.TryParse(MinSizeFilter.Text.Trim(), out var minSize) && minSize > 0) minKb = minSize * 1024;
        if (long.TryParse(MaxSizeFilter.Text.Trim(), out var maxSize) && maxSize > 0) maxKb = maxSize * 1024;
        AssetList.ItemsSource = _search.Search(_project, new AssetSearchQuery(
            SearchBox?.Text,
            selectedType,
            selectedState,
            null,
            ParseLongFilter(PathIdFilter),
            int.TryParse(TypeIdFilter.Text.Trim(), out var typeId) ? typeId : null,
            minKb,
            maxKb,
            ReplacedOnlyFilter.IsChecked));
        if (AssetList.Items.Count == _project.Assets.Count)
            AssetCountText.Text = _project.Assets.Count.ToString();
        else
            AssetCountText.Text = $"{AssetList.Items.Count} / {_project.Assets.Count}";
    }

    private void ShowError(string title, Exception ex)
    {
        StatusText.Text = $"{title}：{ex.Message}";
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
