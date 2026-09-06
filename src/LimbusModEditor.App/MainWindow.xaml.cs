using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Editing.Images;
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
    private readonly AppEnvironment _env = AppEnvironment.Current;
    private readonly UnityCacheScanService _cacheScan =
        new(Path.Combine(AppEnvironment.Current.CacheDirectory, "unity-cache-index.json"));
    private readonly UnityCacheExportService _cacheExporter = new();
    private DebugApplySession? _debugSession;
    private ModProject? _project;
    private string? _projectFile;

    /// <summary>Current project (nullable). Exposed for the settings window.</summary>
    public ModProject? Project => _project;
    public string? ProjectFile => _projectFile;

    /// <summary>Re-reads directory fields into the left-panel labels after the
    /// settings window saves.</summary>
    public void RefreshDirectoryLabels() => RefreshProjectState("设置已保存");

    public MainWindow()
    {
        InitializeComponent();
        UpdateDirectoryStatus();
        UpdateHint();
        Loaded += async (_, _) => await OnWindowLoadedAsync();
    }

    /// <summary>启动引导（傻瓜化）：有上次项目就自动恢复；否则弹出欢迎窗口
    /// 引导「新建 / 打开 / 最近项目」。</summary>
    private async Task OnWindowLoadedAsync()
    {
        var last = _env.Config.LastProjectFile;
        if (!string.IsNullOrWhiteSpace(last) && File.Exists(last))
        {
            try
            {
                await OpenProjectFileAsync(last, "已恢复上次项目");
                return;
            }
            catch (Exception ex) { ShowError("恢复上次项目失败", ex); }
        }
        ShowWelcomeDialog();
    }

    private void ShowWelcomeDialog()
    {
        var dialog = new WelcomeDialog(_env.Config.RecentProjects) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        switch (dialog.Result.Choice)
        {
            case WelcomeChoice.CreateNew: NewModWizard_Click(this, new RoutedEventArgs()); break;
            case WelcomeChoice.OpenFile: OpenProject_Click(this, new RoutedEventArgs()); break;
            case WelcomeChoice.Recent when dialog.Result.RecentProjectFile is not null:
                _ = OpenRecentAsync(dialog.Result.RecentProjectFile);
                break;
        }
    }

    private async Task OpenRecentAsync(string projectFile)
    {
        try { await OpenProjectFileAsync(projectFile, "已打开最近项目"); }
        catch (Exception ex) { ShowError("打开最近项目失败", ex); }
    }


    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "LME 项目 (*.lmeproj)|*.lmeproj",
            FileName = "MyMod.lmeproj",
            Title = "选择新模组项目位置"
        };
        // 零操作默认位置：程序目录 projects/。
        if (!Directory.Exists(_env.ProjectsDirectory)) Directory.CreateDirectory(_env.ProjectsDirectory);
        dialog.InitialDirectory = _env.ProjectsDirectory;
        if (dialog.ShowDialog() != true) return;
        try
        {
            var directory = Path.GetDirectoryName(dialog.FileName)!;
            _project = await _projects.CreateAsync(directory, Path.GetFileNameWithoutExtension(dialog.FileName));
            _projectFile = dialog.FileName;
            await AfterProjectOpenedAsync("已创建项目");
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
            await AfterProjectOpenedAsync("已通过向导创建项目");
        }
        catch (Exception ex) { ShowError("打开新建项目失败", ex); }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "LME 项目 (*.lmeproj)|*.lmeproj|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(_env.ProjectsDirectory) ? _env.ProjectsDirectory : null
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await OpenProjectFileAsync(dialog.FileName, "已打开项目");
        }
        catch (Exception ex) { ShowError("打开项目失败", ex); }
    }

    /// <summary>打开项目的统一入口：加载 → 记录最近项目 → 无感自动配置 →
    /// 需要时自动要求扫描 → 更新提示。</summary>
    private async Task OpenProjectFileAsync(string projectFile, string? statusPrefix = null)
    {
        _project = await _projects.LoadAsync(projectFile);
        _projectFile = Path.GetFullPath(projectFile);
        _env.RegisterRecentProject(_projectFile, _project.Name);
        await AfterProjectOpenedAsync(statusPrefix ?? "已打开项目");
    }

    /// <summary>项目就绪后的无感流程：共享目录自动配置；项目还没有资源时
    /// 自动弹出扫描窗口（傻瓜化核心）。</summary>
    private async Task AfterProjectOpenedAsync(string prefix)
    {
        RefreshProjectState(prefix);
        await AutoConfigureAsync();
        RefreshProjectState(prefix);

        // 空项目自动要求扫描（扫描完成后即可直接编辑）。
        if (_project is { Assets.Count: 0 })
        {
            var cacheDirectory = _env.EffectiveUnityCacheDirectory(_project);
            if (UnityCacheScanService.EnumerateCacheEntries(cacheDirectory).Count > 0)
                PromptScan(autoStart: true);
        }
        UpdateHint();
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
            var fmodDirectory = _env.EffectiveFmodLibraryDirectory(_project);
            using NativeFmodAudioCodec? nativeCodec = choice.Target == LimbusModEditor.Domain.Formats.ModFormatKind.Bank && !string.IsNullOrWhiteSpace(fmodDirectory) && Directory.Exists(fmodDirectory)
                ? new NativeFmodAudioCodec(fmodDirectory) : null;
            var result = await _exporter.ExportWithEditsAsync(selectedSource, _project, choice.OutputPath, choice.Target, nativeCodec);
            StatusText.Text = $"导出完成：应用 {result.AppliedReplacements} 个替换 → {result.OutputPath}";
            new ExportReportWindow(result) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { ShowError("导出模组失败", ex); }
    }

    /// <summary>导出全部（多格式项目）：a standard project mixes Unity-bundle
    /// edits (Carra2) and audio edits (Bank/Rebank); each registered source is
    /// exported to its own format into the chosen directory.</summary>
    private async void ExportAll_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { StatusText.Text = "请先创建或打开项目"; return; }
        if (!_project.Sources.Any())
        {
            MessageBox.Show(this, "项目还没有登记任何源模组。\n请先「导入资源包」再导出。", "导出全部", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = false,
            ValidateNames = false,
            FileName = "选择此文件夹",
            Title = "选择导出输出目录"
        };
        if (dialog.ShowDialog() != true) return;
        var directory = Path.GetDirectoryName(dialog.FileName);
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            var fmodDirectory = _env.EffectiveFmodLibraryDirectory(_project);
            using NativeFmodAudioCodec? nativeCodec = !string.IsNullOrWhiteSpace(fmodDirectory) && Directory.Exists(fmodDirectory)
                ? new NativeFmodAudioCodec(fmodDirectory) : null;
            var result = await _exporter.ExportAllAsync(_project, directory, nativeCodec);
            StatusText.Text = $"导出全部完成：成功 {result.SucceededCount}，失败 {result.FailedCount} → {directory}";
            new MultiExportReportWindow(result, directory) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { ShowError("导出全部失败", ex); }
    }

    /// <summary>设置（二级窗口）：集中修改目录与元数据；日常自动获取，
    /// 手动调整在这里。</summary>
    private void ProjectSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { StatusText.Text = "请先创建或打开项目"; return; }
        new ProjectSettingsWindow(this, _ => _projectFile is not null && SaveProjectInternal()) { Owner = this }.ShowDialog();
    }

    /// <summary>lang 文本模组通道（T2）：RFC6902 补丁的生成与应用，格式与真实
    /// 加载器（LCTA launcher/changes.py）一致。lang 根目录默认取项目游戏目录；
    /// 游戏目录缺失时先无感自动获取一次。</summary>
    private async void LangTextMod_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_project is not null && _projectFile is not null && string.IsNullOrWhiteSpace(_env.EffectiveGameDirectory(_project)))
            {
                await Task.Run(() => _env.ApplyAutoConfigure(_project));
                await SaveProjectQuietlyAsync();
                UpdateDirectoryStatus();
            }
            var gameDirectory = _env.EffectiveGameDirectory(_project);
            var defaultLangRoot = string.IsNullOrWhiteSpace(gameDirectory)
                ? string.Empty
                : Path.Combine(gameDirectory, "LimbusCompany_Data", "lang");
            new LangTextModWindow(defaultLangRoot, _env.EffectiveModDirectory(_project)) { Owner = this }.ShowDialog();
            StatusText.Text = "文本模组窗口已关闭（补丁文件放进模组目录后由加载器应用）。";
        }
        catch (Exception ex) { ShowError("打开文本模组窗口失败", ex); }
    }

    /// <summary>静态数据模组通道：.staticmod 的读取/预览应用/生成（布局与真实
    /// 加载器 LCTA launcher/staticmod.py 一致）；bundle 打补丁与 catalog 双写
    /// 由加载器完成，编辑器只产出加载器可消费的模组包。</summary>
    private void StaticMod_Click(object sender, RoutedEventArgs e)
    {
        new StaticModWindow() { Owner = this }.ShowDialog();
        StatusText.Text = "静态数据模组窗口已关闭（.staticmod 放进模组目录后由加载器应用）。";
    }

    private bool SaveProjectInternal()
    {
        if (_project is null || _projectFile is null) return false;
        _projects.SaveAsync(_project, _projectFile).GetAwaiter().GetResult();
        return true;
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

    /// <summary>无感自动化（共享配置版）：只填充从未配置过的目录（游戏 /
    /// Unity 缓存 / 模组 / FMOD），旧项目里的目录值自动迁移进共享配置。
    /// 手动值永远不会被覆盖。</summary>
    private async Task AutoConfigureAsync()
    {
        if (_project is null) return;
        var report = await Task.Run(() => _env.ApplyAutoConfigure(_project));
        if (_projectFile is not null) await SaveProjectQuietlyAsync();
        UpdateDirectoryStatus();
        if (report.Any) StatusText.Text = $"自动配置：{report.Describe()}（共享设置已保存到程序目录）";
    }

    /// <summary>扫描游戏资源入口（左栏「① 获取资源」）。autoStart=true 时
    /// 窗口打开即开始（用于新建/打开空项目后的自动引导）。</summary>
    private async void Scan_Click(object sender, RoutedEventArgs e) => PromptScan(autoStart: false);

    private void PromptScan(bool autoStart)
    {
        if (_project is null) { StatusText.Text = "请先创建或打开项目"; return; }
        var cacheDirectory = _env.EffectiveUnityCacheDirectory(_project);
        if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory))
        {
            HintText.Text = "⚠ 尚未找到 Unity 缓存目录 —— 请确认游戏已启动过一次（生成缓存），或在「设置…」中指定缓存目录后重试扫描。";
            MessageBox.Show(this,
                "还没有可扫描的 Unity 缓存目录。\n\n请先启动一次游戏让缓存生成，或打开「设置…」手动指定缓存目录\n（LocalLow/Unity/ProjectMoon_LimbusCompany）。",
                "扫描游戏资源", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new ScanDialog(_cacheScan, _project, cacheDirectory, _env.EffectiveGameDirectory(_project), autoStart) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Result is not null && _projectFile is not null)
        {
            SaveProjectInternal();
            RefreshProjectState($"扫描完成：新增 {dialog.Result.AddedAssets} 个资源，更新 {dialog.Result.UpdatedAssets} 个 —— 现在就可以在列表中搜索并编辑了");
        }
        else
        {
            RefreshProjectState();
        }
        UpdateHint();
    }

    /// <summary>傻瓜化一键导出：扫描资源上的全部修改 → Carra2 → 模组目录。</summary>
    private async void OneClickExport_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        await AutoConfigureAsync();
        var modDirectory = _env.EffectiveModDirectory(_project);
        var defaultDirectory = !string.IsNullOrWhiteSpace(modDirectory) && Directory.Exists(modDirectory)
            ? modDirectory
            : Path.Combine(Path.GetDirectoryName(_projectFile)!, "builds");
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Carra2 模组 (*.carra2)|*.carra2",
            FileName = SanitizeFileName(_project.Name) + ".carra2",
            InitialDirectory = defaultDirectory,
            Title = "选择导出位置（默认放在模组目录，游戏加载器可直接读取）"
        };
        if (dialog.ShowDialog() != true) return;
        OneClickExportButton.IsEnabled = false;
        StatusText.Text = "正在导出（重打包被编辑的 bundle 并生成 Carra2）…";
        try
        {
            await MaterializeEditedAssetsAsync();
            await SaveProjectQuietlyAsync();
            var result = await _cacheExporter.ExportCarra2Async(
                _project, Path.GetDirectoryName(_projectFile)!, dialog.FileName,
                _env.EffectiveUnityCacheDirectory(_project));
            StatusText.Text = $"导出完成：{result.AppliedReplacements} 个对象 → {result.OutputPath}";
            new ExportReportWindow(result) { Owner = this }.ShowDialog();
            UpdateHint();
        }
        catch (Exception ex) { ShowError("一键导出失败", ex); }
        finally { OneClickExportButton.IsEnabled = true; }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "MyMod" : cleaned;
    }

    /// <summary>导出/构建前的统一实体化：把所有「已编辑但仍是缓存引用」的
    /// 资源实体化为项目本地副本（每个 bundle 只复制一次）。缓存里所有
    /// bundle 都叫 __data，不实体化会在构建输出目录互相覆盖。</summary>
    private async Task MaterializeEditedAssetsAsync()
    {
        if (_project is null) return;
        var seeds = _project.Assets.Where(x =>
            x.Metadata.TryGetValue("reference", out var reference) && reference == "true" &&
            (x.Metadata.ContainsKey("replacementPath") || x.Metadata.ContainsKey("unityFieldEdits") || x.Metadata.ContainsKey("spriteMetadata"))).ToList();
        foreach (var seed in seeds)
        {
            await UnityCacheMaterializationService.MaterializeForEditingAsync(_project, seed, _projectFile is null ? null : Path.GetDirectoryName(_projectFile));
        }
    }

    /// <summary>下一步提示条：按当前状态告诉用户该做什么（傻瓜化指引）。</summary>
    private void UpdateHint()
    {
        NoProjectOverlay.Visibility = _project is null ? Visibility.Visible : Visibility.Collapsed;
        var edits = _project?.Assets.Count(x =>
            x.Metadata.ContainsKey("replacementPath") || x.Metadata.ContainsKey("unityFieldEdits") || x.Metadata.ContainsKey("spriteMetadata")) ?? 0;
        if (_project is null)
        {
            HintText.Text = "👋 欢迎使用 Limbus Mod Editor —— 点击「新建模组项目」开始：填一个名字，其余（目录、游戏路径、资源扫描）全部自动完成。";
            return;
        }
        if (_project.Assets.Count == 0)
        {
            HintText.Text = "下一步：点击左栏「扫描游戏资源」把游戏素材加入项目（引用模式，不复制文件）——扫描完成后即可搜索并编辑。";
            return;
        }
        if (edits == 0)
        {
            HintText.Text = $"已索引 {_project.Assets.Count} 个游戏资源。下一步：在列表中搜索你想要的素材（可用类型筛选），选中后用右侧按钮替换图片 / 编辑字段。";
            return;
        }
        HintText.Text = $"已有 {edits} 处修改。下一步：点击左栏「一键导出模组」生成 .carra2 到模组目录（或继续编辑）。";
    }

    private async Task SaveProjectQuietlyAsync()
    {
        if (_project is null || _projectFile is null) return;
        try { await _projects.SaveAsync(_project, _projectFile); }
        catch (Exception ex) { StatusText.Text = $"项目保存失败：{ex.Message}"; }
    }

    /// <summary>左栏「重新自动获取目录」：无感自动化的手动兜底（例如项目
    /// 创建时游戏尚未安装）。共享配置只填空位，手动值不会被覆盖。</summary>
    private async void AutoConfigure_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { StatusText.Text = "请先创建或打开项目"; return; }
        try
        {
            var report = await Task.Run(() => _env.ApplyAutoConfigure(_project));
            UpdateDirectoryStatus();
            StatusText.Text = report.Any
                ? $"已自动获取：{report.Describe()}（共享设置已保存到程序目录）"
                : "目录均已配置，无需自动获取。";
        }
        catch (Exception ex) { ShowError("自动获取目录失败", ex); }
    }

    /// <summary>左栏目录状态一览：显示「生效值（来源）」——来源可为
    /// 共享配置 / 本项目（旧）/ 自动发现。</summary>
    private void UpdateDirectoryStatus()
    {
        var (game, gameSource) = _env.ResolveWithSource(
            _env.Config.GameDirectory, _project?.GameDirectory,
            () => LimbusModEditor.Application.Debugging.GameDirectoryLocator
                .Scan(LimbusModEditor.Application.Debugging.GameDirectoryLocator.DefaultCandidateRoots()).GameDirectory, "自动发现");
        GameDirectoryStatus.Text = DescribeDirectory("游戏目录", game, gameSource);
        var (cache, cacheSource) = _env.ResolveWithSource(
            _env.Config.UnityCacheDirectory, _project?.UnityCacheDirectory,
            () => FirstCacheCandidate(game), "自动发现");
        UnityCacheStatus.Text = DescribeDirectory("Unity 缓存", cache, cacheSource);
        var (mods, modsSource) = _env.ResolveWithSource(
            _env.Config.ModDirectory, _project?.ModDirectory,
            () => LimbusModEditor.Application.Debugging.ModDirectoryLocator.SuggestCandidates().FirstOrDefault(), "自动发现");
        ModDirectoryStatus.Text = DescribeDirectory("模组目录", mods, modsSource);
        var (fmod, fmodSource) = _env.ResolveWithSource(
            _env.Config.FmodLibraryDirectory, _project?.FmodLibraryDirectory,
            () => LimbusModEditor.Application.AppConfig.FmodLibraryLocator
                .Discover(_env.BaseDirectory, game)?.Directory, "随包/自动发现");
        FmodDirectoryStatus.Text = DescribeDirectory("FMOD DLL", fmod, fmodSource);
        GameDirectoryStatus.ToolTip = game;
        UnityCacheStatus.ToolTip = cache;
        ModDirectoryStatus.ToolTip = mods;
        FmodDirectoryStatus.ToolTip = fmod;
    }

    private static string? FirstCacheCandidate(string? gameDirectory)
    {
        var candidates = LimbusModEditor.Application.Debugging.UnityCacheLocator.SuggestCandidates(gameDirectory);
        return candidates.FirstOrDefault()?.Path;
    }

    private static string DescribeDirectory(string label, string? path, string source)
    {
        var suffix = string.IsNullOrWhiteSpace(path) ? string.Empty : $"（{source}）";
        if (string.IsNullOrWhiteSpace(path)) return $"✗ {label}：未配置";
        return Directory.Exists(path)
            ? $"✓ {label}：{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}{suffix}"
            : $"⚠ {label}：目录不存在{suffix}";
    }

    /// <summary>真实加载器约定（LCTA launcher）："_disable" 后缀切换启用/禁用。
    /// 管理窗口只做重命名，不修改文件内容。模组目录缺失时先无感自动获取。</summary>
    private async void ManageMods_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { StatusText.Text = "请先创建或打开项目"; return; }
        try
        {
            var modsDirectory = _env.EffectiveModDirectory(_project);
            if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
            {
                var candidates = LimbusModEditor.Application.Debugging.ModDirectoryLocator.SuggestCandidates();
                if (candidates.Count > 0)
                {
                    _env.Config.ModDirectory = candidates[0];
                    _env.Save();
                    UpdateDirectoryStatus();
                    modsDirectory = candidates[0];
                }
            }
            if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
            {
                MessageBox.Show(this, "未能自动获取模组目录（%APPDATA%\\LimbusCompanyMods）。\n请在「设置…」中手动指定。", "管理已安装模组", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            new ModManagerWindow(modsDirectory) { Owner = this }.ShowDialog();
            StatusText.Text = "模组目录管理已关闭（切换结果以加载器下次扫描为准）。";
        }
        catch (Exception ex) { ShowError("管理已安装模组失败", ex); }
    }

    private async void DebugApply_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        // 目标目录缺失时先无感自动获取一次（只填缺失项，不覆盖手动值）。
        await Task.Run(() => _env.ApplyAutoConfigure(_project));
        await SaveProjectQuietlyAsync();
        UpdateDirectoryStatus();
        var debugTarget = _env.EffectiveModDirectory(_project) is { } mods && Directory.Exists(mods) ? mods
            : _env.EffectiveUnityCacheDirectory(_project) is { } cache && Directory.Exists(cache) ? cache
            : _env.EffectiveGameDirectory(_project);
        if (string.IsNullOrWhiteSpace(debugTarget) || !Directory.Exists(debugTarget))
        {
            StatusText.Text = "请先在设置中配置游戏目录";
            return;
        }
        try
        {
            var root = Path.GetDirectoryName(_projectFile)!;
            var overlay = Path.Combine(root, "builds", "debug-overlay");
            await MaterializeEditedAssetsAsync();
            await SaveProjectQuietlyAsync();
            await _builder.BuildOverlayAsync(_project, root, overlay);
            await AddUnityBundlesToOverlayAsync(_project, root, overlay);
            await AddUnitySerializedFilesToOverlayAsync(_project, root, overlay);
            _debugSession = await _debugApply.ApplyAsync(_project, overlay, debugTarget);
            var launch = _gameLaunch.TryLaunch(_env.EffectiveGameDirectory(_project)!, _project.GameExecutablePath);
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
            await MaterializeEditedAssetsAsync();
            await SaveProjectQuietlyAsync();
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
            var relative = GetSafeDebugRelativePath(_env, project, original, Path.GetFileName(bundle.OutputPath));
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
            var relative = GetSafeDebugRelativePath(_env, project, original, Path.GetFileName(file.OutputPath));
            var target = Path.Combine(overlay, relative);
            await LimbusModEditor.Application.Build.AtomicOutput.CopyAsync(file.OutputPath, target);
        }
        return files.Sum(x => x.AppliedAssets);
    }

    private static string GetSafeDebugRelativePath(AppEnvironment env, ModProject project, string? original, string fallback)
    {
        if (string.IsNullOrWhiteSpace(original)) return fallback;
        var source = Path.GetFullPath(original);
        foreach (var root in new[] { env.EffectiveUnityCacheDirectory(project), env.EffectiveGameDirectory(project), env.EffectiveModDirectory(project) })
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
        UpdateDirectoryStatus();
        RefreshAssetList();
        UpdateHint();
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
        var fmodDirectory = _env.EffectiveFmodLibraryDirectory(_project);
        DecodeAudioButton.IsEnabled = asset?.Type == AssetType.Audio &&
            asset.LogicalPath.StartsWith("fsb/", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(fmodDirectory) &&
            Directory.Exists(fmodDirectory) && _projectFile is not null;
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
        var fmodDirectory = _env.EffectiveFmodLibraryDirectory(_project);
        if (_project is null || AssetList.SelectedItem is not AssetRecord asset || asset.Type != AssetType.Audio ||
            string.IsNullOrWhiteSpace(fmodDirectory) || !Directory.Exists(fmodDirectory)) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "WAV audio (*.wav)|*.wav|All files (*.*)|*.*",
            FileName = Path.GetFileName(asset.LogicalPath) + ".wav",
            Title = "导出 Bank 音频为 WAV"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            using var codec = new NativeFmodAudioCodec(fmodDirectory);
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
