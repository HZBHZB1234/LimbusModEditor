using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _searchTimer;
    private int _searchGeneration;
    private int _previewGeneration;
    // 当前选中资源：列表与目录树两个视图共用（右键菜单/双击/按钮都以此为准）。
    private AssetRecord? _selectedAsset;
    // 最近一次搜索结果快照：目录树视图按它重建根层。
    private IReadOnlyList<AssetRecord>? _lastResults;
    private bool _treeMode;

    // ── 目录定位器记忆化：UpdateDirectoryStatus 每次 RefreshProjectState 都会
    //    调用，而定位器要扫盘（候选根列表）；结果按输入键缓存到进程级，
    //    配置变更时通过 ResetLocatorCache 失效 ─────────────────────────────
    private static bool _memoGameDone;
    private static string? _memoGameDirectory;
    private static (string? Game, string? Path) _memoCacheCandidate;
    private static bool _memoModsDone;
    private static string? _memoModsDirectory;
    private static (string? Base, string? Game, string? Dir) _memoFmod;

    private static void ResetLocatorCache()
    {
        _memoGameDone = false;
        _memoGameDirectory = null;
        _memoCacheCandidate = default;
        _memoModsDone = false;
        _memoModsDirectory = null;
        _memoFmod = default;
    }

    /// <summary>Current project (nullable). Exposed for the settings window.</summary>
    public ModProject? Project => _project;
    public string? ProjectFile => _projectFile;

    /// <summary>Re-reads directory fields into the left-panel labels after the
    /// settings window saves.</summary>
    public void RefreshDirectoryLabels()
    {
        ResetLocatorCache();
        RefreshProjectState("设置已保存");
    }

    public MainWindow()
    {
        InitializeComponent();
        // 搜索防抖：全缓存扫描后有数十万资产，逐键即时过滤会卡顿；
        // 停止输入 300ms 后才真正过滤（过滤与排序在后台线程）。
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _ = RunSearchAsync(); };
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

    /// <summary>项目就绪后的无感流程：共享目录自动配置；打开时从扫描索引
    /// 后台回灌纯引用资产（项目文件已瘦身，不再携带它们）；项目仍无资源时
    /// 自动弹出扫描窗口（傻瓜化核心）。</summary>
    private async Task AfterProjectOpenedAsync(string prefix)
    {
        RefreshProjectState(prefix);
        await AutoConfigureAsync();
        await RehydrateAssetsFromIndexAsync();
        RefreshProjectState(prefix);

        // 空项目自动要求扫描（扫描完成后即可直接编辑）。回灌成功过的项目
        // 资产数 > 0，不会误触发。
        if (_project is { Assets.Count: 0 })
        {
            var cacheDirectory = _env.EffectiveUnityCacheDirectory(_project);
            if (UnityCacheScanService.EnumerateCacheEntries(cacheDirectory).Count > 0)
                await PromptScanAsync(autoStart: true);
        }
        UpdateHint();
    }

    /// <summary>阶段 C 项目瘦身配套：项目文件不再携带纯引用资产（真实全缓存
    /// 项目曾把 .lmeproj 撑到 1.6GB），打开时从扫描索引 SQLite 库重建（不解析
    /// 任何 bundle，秒级）。索引库缺失时静默返回 0（随后空项目逻辑会引导扫描）。</summary>
    private async Task<int> RehydrateAssetsFromIndexAsync()
    {
        if (_project is null) return 0;
        try
        {
            StatusText.Text = "正在从扫描索引重建资源列表（不解析 bundle，秒级）…";
            var added = await _cacheScan.RehydrateFromIndexAsync(_project);
            if (added > 0) StatusText.Text = $"资源索引已重建（{added} 条引用资产，后台完成，未解析任何 bundle）。";
            return added;
        }
        catch (Exception ex) { ShowError("重建资源索引失败", ex); return 0; }
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
                var result = await ImportSingleFileAsync(file);
                added += result.AddedAssets;
                updated += result.UpdatedAssets;
            }
            if (_project is not null && _projectFile is not null) await _projects.SaveAsync(_project, _projectFile);
            RefreshProjectState($"导入完成：新增 {added}，更新 {updated}");
        }
        catch (Exception ex) { ShowError("导入失败", ex); }
    }

    /// <summary>单个文件按扩展名分发到对应导入管道（按钮导入与拖放共用）。</summary>
    private Task<ImportResult> ImportSingleFileAsync(string file)
    {
        var extension = Path.GetExtension(file);
        return extension.Equals(".bundle", StringComparison.OrdinalIgnoreCase)
            ? _importer.ImportUnityBundleIntoProjectAsync(file, _project!)
            : extension.Equals(".assets", StringComparison.OrdinalIgnoreCase)
                ? _importer.ImportSerializedFileIntoProjectAsync(file, _project!)
                : _importer.ImportIntoProjectAsync(file, _project!);
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
    /// 手动调整在这里。项目保存走后台线程（全缓存扫描后项目很大）。</summary>
    private void ProjectSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { StatusText.Text = "请先创建或打开项目"; return; }
        new ProjectSettingsWindow(this, _ => SaveProjectInternalAsync()) { Owner = this }.ShowDialog();
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
                ResetLocatorCache();
                UpdateDirectoryStatus();
            }
            var gameDirectory = _env.EffectiveGameDirectory(_project);
            var defaultLangRoot = string.IsNullOrWhiteSpace(gameDirectory)
                ? string.Empty
                : Path.Combine(gameDirectory, "LimbusCompany_Data", "lang");
            // VS Code 式工作台：不再弹独立窗口，作为标签页打开（重复点击 = 回到已打开的标签）。
            OpenWorkbench("lang", "文本模组",
                () => new LangTextModControl(defaultLangRoot, _env.EffectiveModDirectory(_project), this));
            StatusText.Text = "文本模组工作台已打开（补丁文件放进模组目录后由加载器应用）。";
        }
        catch (Exception ex) { ShowError("打开文本模组工作台失败", ex); }
    }

    /// <summary>静态数据模组通道：.staticmod 的读取/预览应用/生成（布局与真实
    /// 加载器 LCTA launcher/staticmod.py 一致）；bundle 打补丁与 catalog 双写
    /// 由加载器完成，编辑器只产出加载器可消费的模组包。</summary>
    private void StaticMod_Click(object sender, RoutedEventArgs e)
    {
        OpenWorkbench("static", "静态数据模组", () => new StaticModControl(this));
        StatusText.Text = "静态数据模组工作台已打开（.staticmod 放进模组目录后由加载器应用）。";
    }

    /// <summary>项目保存的同步入口已被 async 版本取代：全缓存扫描后项目
    /// 含数十万资产，序列化必须在后台线程完成，不能阻塞 UI。</summary>
    private async Task<bool> SaveProjectInternalAsync()
    {
        if (_project is null || _projectFile is null) return false;
        try
        {
            await _projects.SaveAsync(_project, _projectFile);
            return true;
        }
        catch (Exception ex) { ShowError("保存项目失败", ex); return false; }
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
        if (_selectedAsset is not AssetRecord asset) return;
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
        ResetLocatorCache();
        UpdateDirectoryStatus();
        if (report.Any) StatusText.Text = $"自动配置：{report.Describe()}（共享设置已保存到程序目录）";
    }

    /// <summary>扫描游戏资源入口（左栏「① 获取资源」）。autoStart=true 时
    /// 窗口打开即开始（用于新建/打开空项目后的自动引导）。</summary>
    private async void Scan_Click(object sender, RoutedEventArgs e) => await PromptScanAsync(autoStart: false);

    private async Task PromptScanAsync(bool autoStart)
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
            await SaveProjectQuietlyAsync();
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
        var progressWindow = new ExportProgressWindow(
            "一键导出模组",
            "导出完成后会显示逐资源报告；长时间无进展请检查被编辑的 bundle 是否过大。") { Owner = this };
        var progress = new Progress<string>(progressWindow.Report);
        void CloseProgress() { try { progressWindow.Close(); } catch { /* 已关闭 */ } }
        IsEnabled = false;
        progressWindow.Show();
        try
        {
            await MaterializeEditedAssetsAsync(progress);
            await SaveProjectQuietlyAsync();
            var result = await _cacheExporter.ExportCarra2Async(
                _project, Path.GetDirectoryName(_projectFile)!, dialog.FileName,
                _env.EffectiveUnityCacheDirectory(_project), default, progress);
            CloseProgress();
            StatusText.Text = $"导出完成：{result.AppliedReplacements} 个对象 → {result.OutputPath}";
            new ExportReportWindow(result) { Owner = this }.ShowDialog();
            UpdateHint();
        }
        catch (Exception ex) { CloseProgress(); ShowError("一键导出失败", ex); }
        finally { IsEnabled = true; OneClickExportButton.IsEnabled = true; }
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
    private async Task MaterializeEditedAssetsAsync(IProgress<string>? progress = null)
    {
        if (_project is null) return;
        var seeds = _project.Assets.Where(x =>
            x.Metadata.TryGetValue("reference", out var reference) && reference == "true" &&
            (x.Metadata.ContainsKey("replacementPath") || x.Metadata.ContainsKey("unityFieldEdits") || x.Metadata.ContainsKey("spriteMetadata"))).ToList();
        progress?.Report(seeds.Count == 0
            ? "没有需要实体化的缓存引用资源。"
            : $"正在把 {seeds.Count} 个已编辑资源实体化（复制所属 bundle 到项目）…");
        for (var i = 0; i < seeds.Count; i++)
        {
            var seed = seeds[i];
            progress?.Report($"[{i + 1}/{seeds.Count}] 实体化 {seed.LogicalPath}");
            await UnityCacheMaterializationService.MaterializeForEditingAsync(_project, seed, _projectFile is null ? null : Path.GetDirectoryName(_projectFile));
        }
    }

    /// <summary>下一步提示条：按当前状态告诉用户该做什么（傻瓜化指引）。</summary>
    private void UpdateHint()
    {
        NoProjectOverlay.Visibility = _project is null ? Visibility.Visible : Visibility.Collapsed;
        var edits = _project?.Assets.Count(AssetEditService.HasEdits) ?? 0;
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
            ResetLocatorCache();
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
            ResolveGameDirectoryMemoized, "自动发现");
        GameDirectoryStatus.Text = DescribeDirectory("游戏目录", game, gameSource);
        var (cache, cacheSource) = _env.ResolveWithSource(
            _env.Config.UnityCacheDirectory, _project?.UnityCacheDirectory,
            () => FirstCacheCandidateMemoized(game), "自动发现");
        UnityCacheStatus.Text = DescribeDirectory("Unity 缓存", cache, cacheSource);
        var (mods, modsSource) = _env.ResolveWithSource(
            _env.Config.ModDirectory, _project?.ModDirectory,
            FirstModCandidateMemoized, "自动发现");
        ModDirectoryStatus.Text = DescribeDirectory("模组目录", mods, modsSource);
        var (fmod, fmodSource) = _env.ResolveWithSource(
            _env.Config.FmodLibraryDirectory, _project?.FmodLibraryDirectory,
            () => FmodDirectoryMemoized(game), "随包/自动发现");
        FmodDirectoryStatus.Text = DescribeDirectory("FMOD DLL", fmod, fmodSource);
        GameDirectoryStatus.ToolTip = game;
        UnityCacheStatus.ToolTip = cache;
        ModDirectoryStatus.ToolTip = mods;
        FmodDirectoryStatus.ToolTip = fmod;
    }

    private static string? ResolveGameDirectoryMemoized()
    {
        if (!_memoGameDone)
        {
            _memoGameDirectory = LimbusModEditor.Application.Debugging.GameDirectoryLocator
                .Scan(LimbusModEditor.Application.Debugging.GameDirectoryLocator.DefaultCandidateRoots()).GameDirectory;
            _memoGameDone = true;
        }
        return _memoGameDirectory;
    }

    private static string? FirstCacheCandidateMemoized(string? gameDirectory)
    {
        if (_memoCacheCandidate.Game != gameDirectory)
            _memoCacheCandidate = (gameDirectory,
                LimbusModEditor.Application.Debugging.UnityCacheLocator.SuggestCandidates(gameDirectory)
                    .FirstOrDefault()?.Path);
        return _memoCacheCandidate.Path;
    }

    private static string? FirstModCandidateMemoized()
    {
        if (!_memoModsDone)
        {
            _memoModsDirectory = LimbusModEditor.Application.Debugging.ModDirectoryLocator.SuggestCandidates().FirstOrDefault();
            _memoModsDone = true;
        }
        return _memoModsDirectory;
    }

    private string? FmodDirectoryMemoized(string? game)
    {
        var baseDirectory = _env.BaseDirectory;
        if (_memoFmod.Base != baseDirectory || _memoFmod.Game != game)
            _memoFmod = (baseDirectory, game,
                LimbusModEditor.Application.AppConfig.FmodLibraryLocator
                    .Discover(baseDirectory, game)?.Directory);
        return _memoFmod.Dir;
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
                    ResetLocatorCache();
                    UpdateDirectoryStatus();
                    modsDirectory = candidates[0];
                }
            }
            if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
            {
                MessageBox.Show(this, "未能自动获取模组目录（%APPDATA%\\LimbusCompanyMods）。\n请在「设置…」中手动指定。", "管理已安装模组", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenWorkbench("mods", "模组管理", () => new ModManagerControl(modsDirectory));
            StatusText.Text = "模组管理工作台已打开（切换结果以加载器下次扫描为准）。";
        }
        catch (Exception ex) { ShowError("管理已安装模组失败", ex); }
    }

    private async void DebugApply_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        // 目标目录缺失时先无感自动获取一次（只填缺失项，不覆盖手动值）。
        await Task.Run(() => _env.ApplyAutoConfigure(_project));
        await SaveProjectQuietlyAsync();
        ResetLocatorCache();
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
        // 与提示条/一键导出同一口径：按编辑标记统计「已修改资源」数，
        // 而不是 Edits 历史条数（重复替换会让历史虚增）。
        EditCountText.Text = (_project?.Assets.Count(AssetEditService.HasEdits) ?? 0).ToString();
        UpdateDirectoryStatus();
        RefreshAssetList();
        UpdateHint();
        if (status is not null) StatusText.Text = $"{status}：{_project?.Name}";
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    /// <summary>P3.3: populates the type/state filter combos once.</summary>
    private bool _filtersInitialized;
    private void Filter_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_filtersInitialized) return;
        _searchTimer.Stop();
        _ = RunSearchAsync();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _searchTimer.Stop();
        _ = RunSearchAsync();
    }

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
        _searchTimer.Stop();
        _ = RunSearchAsync();
    }

    private static long? ParseLongFilter(System.Windows.Controls.TextBox box)
    {
        var text = box.Text.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return long.TryParse(text, out var value) ? value : long.MinValue;
    }

    private void AssetList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => ApplyAssetSelection(AssetList.SelectedItem as AssetRecord);

    /// <summary>统一的选中处理：列表与目录树两个视图共用（右侧状态、按钮
    /// 可用性、预览都以此为准；右键菜单/双击读取 <see cref="_selectedAsset"/>）。</summary>
    private void ApplyAssetSelection(AssetRecord? asset)
    {
        _selectedAsset = asset;
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
        ClearEditsButton.IsEnabled = asset is not null && AssetEditService.HasEdits(asset);
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

    private void UpdatePreview(AssetRecord? asset) => _ = UpdatePreviewAsync(asset);

    /// <summary>异步预览：纹理解码（可能是 DXT 压缩的大图）放在后台线程，
    /// 代际守卫保证快速切换选中项时旧结果不会覆盖新选中项的预览。</summary>
    private async Task UpdatePreviewAsync(AssetRecord? asset)
    {
        var generation = ++_previewGeneration;
        PreviewImage.Source = null;
        PreviewInfoText.Text = "无图像预览";
        if (asset is null || asset.Type is not (AssetType.Texture or AssetType.Sprite)) return;
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

    private void SetPreviewBitmap(byte[] png)
    {
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(png);
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        PreviewImage.Source = bitmap;
    }

    private async void SplitAtlas_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null || _selectedAsset is not AssetRecord asset) return;
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
        if (_project is null || _projectFile is null || _selectedAsset is not AssetRecord asset) return;
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
        if (_project is null || _projectFile is null || _selectedAsset is not AssetRecord asset) return;
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

    /// <summary>撤销选中资源上的全部修改（替换文件 / Unity 字段 / Sprite
    /// 元数据），资源还原为未修改状态；导出不再包含它。</summary>
    private async void ClearEdits_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null || _selectedAsset is not AssetRecord asset) return;
        if (!AssetEditService.HasEdits(asset)) { StatusText.Text = "此资源没有可撤销的修改"; return; }
        var confirm = MessageBox.Show(this,
            $"撤销资源 {asset.LogicalPath} 上的全部修改？\n\n替换文件、Unity 字段、Sprite 元数据会被清除；导出将不再包含此资源的修改。",
            "撤销修改", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            var cleared = _assetEdits.ClearEdits(_project, asset.AssetId, Path.GetDirectoryName(_projectFile)!);
            await _projects.SaveAsync(_project, _projectFile);
            RefreshProjectState(cleared ? "已撤销此资源的全部修改" : "此资源没有可撤销的修改");
            AssetList.SelectedItem = asset;
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

    /// <summary>按类型做最常用的事：图像 → 替换；其余 → Unity 字段编辑
    /// （可用时），否则十六进制预览。</summary>
    private void ActivateDefaultAction(AssetRecord asset)
    {
        if (asset.Type is AssetType.Texture or AssetType.Sprite && ReplaceAssetButton.IsEnabled)
        {
            ReplaceAsset_Click(this, new RoutedEventArgs());
            return;
        }
        if (UnityFieldsButton.IsEnabled) { EditUnityFields_Click(this, new RoutedEventArgs()); return; }
        if (HexPreviewButton.IsEnabled) HexPreview_Click(this, new RoutedEventArgs());
    }

    /// <summary>右键菜单跟随光标：右键落在某行上时先选中该行。</summary>
    private void AssetList_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TryFindAncestor<System.Windows.Controls.ListViewItem>(e.OriginalSource as System.Windows.DependencyObject) is { } row)
            row.IsSelected = true;
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
        try { Clipboard.SetText(asset.LogicalPath); StatusText.Text = "已复制资源路径"; }
        catch (Exception) { StatusText.Text = "复制失败（剪贴板被其他程序占用）"; }
    }

    // ── 拖放：文件/文件夹拖进窗口即导入；单张图片拖到选中的图像资源上
    //    询问是否直接作为替换图 ──────────────────────────────────────────

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) &&
                    e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] { Length: > 0 }
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        if (_project is null) { StatusText.Text = "请先创建或打开项目，再拖入文件"; return; }
        if (paths.Length == 1 && File.Exists(paths[0]) &&
            ImagePreviewService.IsSupportedExtension(Path.GetExtension(paths[0])) &&
            _selectedAsset is AssetRecord selected &&
            selected.Type is AssetType.Texture or AssetType.Sprite && _projectFile is not null)
        {
            var choice = MessageBox.Show(this,
                $"把 {Path.GetFileName(paths[0])} 用作选中资源的替换图？\n\n资源：{selected.LogicalPath}\n\n（选「否」则按普通资源包导入）",
                "拖放替换", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes)
            {
                try
                {
                    await _assetEdits.ReplaceFromFileAsync(_project, selected.AssetId, paths[0], Path.GetDirectoryName(_projectFile)!);
                    await _projects.SaveAsync(_project, _projectFile);
                    RefreshProjectState("拖放替换已登记");
                    AssetList.SelectedItem = selected;
                }
                catch (Exception ex) { ShowError("拖放替换失败", ex); }
                return;
            }
        }
        await ImportPathsAsync(paths);
    }

    /// <summary>拖放的统一导入：文件夹走目录导入，文件按扩展名分发，与
    /// 「导入资源包」按钮同一管道；逐文件容错（失败的文件不影响其余）。</summary>
    private async Task ImportPathsAsync(string[] paths)
    {
        if (_project is null) return;
        try
        {
            var added = 0;
            var updated = 0;
            var failed = new List<string>();
            foreach (var path in paths)
            {
                try
                {
                    var result = Directory.Exists(path)
                        ? await _importer.ImportDirectoryIntoProjectAsync(path, _project)
                        : await ImportSingleFileAsync(path);
                    added += result.AddedAssets;
                    updated += result.UpdatedAssets;
                }
                catch (Exception ex) { failed.Add($"{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}：{ex.Message}"); }
            }
            if (_projectFile is not null) await _projects.SaveAsync(_project, _projectFile);
            RefreshProjectState($"拖放导入完成：新增 {added}，更新 {updated}" + (failed.Count > 0 ? $"（失败 {failed.Count}）" : string.Empty));
            if (failed.Count > 0)
                MessageBox.Show(this,
                    "以下文件导入失败：\n" + string.Join("\n", failed.Take(10)),
                    "拖放导入", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError("拖放导入失败", ex); }
    }

    /// <summary>快捷键：Ctrl+F 聚焦搜索框；Esc 在搜索框内清空筛选；
    /// Ctrl+Tab / Ctrl+Shift+Tab 在工作台标签页间循环（VS Code 同款）。</summary>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Control && e.Key == System.Windows.Input.Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.KeyboardDevice.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) && e.Key == System.Windows.Input.Key.Tab)
        {
            var shift = e.KeyboardDevice.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);
            var count = WorkbenchTabs.Items.Count;
            if (count > 1)
            {
                var index = WorkbenchTabs.SelectedIndex < 0 ? 0 : WorkbenchTabs.SelectedIndex;
                WorkbenchTabs.SelectedIndex = shift ? (index - 1 + count) % count : (index + 1) % count;
            }
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && SearchBox.IsKeyboardFocusWithin)
        {
            SearchBox.Text = string.Empty;
            e.Handled = true;
        }
    }

    // ── 快捷打开目录：导出后去模组目录确认、找回项目文件 ─────────────

    private void OpenModsDirectory_Click(object sender, RoutedEventArgs e)
    {
        var mods = _env.EffectiveModDirectory(_project);
        if (string.IsNullOrWhiteSpace(mods) || !Directory.Exists(mods))
        {
            MessageBox.Show(this,
                "模组目录尚未配置或不存在（%APPDATA%\\LimbusCompanyMods）。\n可先点「重新自动获取目录」，或在「设置…」中手动指定。",
                "打开模组目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { System.Diagnostics.Process.Start("explorer.exe", mods); }
        catch (Exception ex) { ShowError("打开模组目录失败", ex); }
    }

    private void OpenProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        var root = _projectFile is null ? null : Path.GetDirectoryName(Path.GetFullPath(_projectFile));
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            StatusText.Text = "请先创建或打开项目";
            return;
        }
        try { System.Diagnostics.Process.Start("explorer.exe", root); }
        catch (Exception ex) { ShowError("打开项目文件夹失败", ex); }
    }

    private async void InspectSprite_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAsset is not AssetRecord asset || asset.Type != AssetType.Sprite ||
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
        if (_project is null || _projectFile is null || _selectedAsset is not AssetRecord asset ||
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
            RefreshSelectionButtons(_selectedAsset);
            StatusText.Text = "就绪";
        }
    }

    /// <summary>P2.1: read-only FSB5 structural inspection for the selected
    /// Bank audio entry; unknown/encrypted payloads are explained in-window.</summary>
    private async void InspectFsb_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _selectedAsset is not AssetRecord asset ||
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
        if (_project is null || _selectedAsset is not AssetRecord asset ||
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
            RefreshSelectionButtons(_selectedAsset);
            StatusText.Text = "就绪";
        }
    }

    /// <summary>P1.5: read-only structural summary of the selected Mesh /
    /// AnimationClip / Font object; values come from the type tree, missing
    /// fields are reported instead of guessed.</summary>
    private async void ShowObjectSummary_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _selectedAsset is not AssetRecord asset ||
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
            RefreshSelectionButtons(_selectedAsset);
            StatusText.Text = "就绪";
        }
    }

    private async void DecodeAudio_Click(object sender, RoutedEventArgs e)
    {
        var fmodDirectory = _env.EffectiveFmodLibraryDirectory(_project);
        if (_project is null || _selectedAsset is not AssetRecord asset || asset.Type != AssetType.Audio ||
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

    /// <summary>初始化筛选下拉框（仅一次）并触发一次搜索。真正的过滤在
    /// RunSearchAsync（后台线程 + 防抖）。</summary>
    private void RefreshAssetList()
    {
        if (_project is null) { AssetList.ItemsSource = null; _lastResults = null; AssetTree.ItemsSource = null; return; }
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
        _searchTimer.Stop();
        _ = RunSearchAsync();
    }

    private AssetSearchQuery BuildSearchQuery()
    {
        var selectedType = (TypeFilter.SelectedItem as dynamic)?.Value as LimbusModEditor.Domain.Assets.AssetType?;
        var selectedState = (StateFilter.SelectedItem as dynamic)?.Value as LimbusModEditor.Domain.Assets.AssetEditState?;
        long? minKb = null, maxKb = null;
        if (long.TryParse(MinSizeFilter.Text.Trim(), out var minSize) && minSize > 0) minKb = minSize * 1024;
        if (long.TryParse(MaxSizeFilter.Text.Trim(), out var maxSize) && maxSize > 0) maxKb = maxSize * 1024;
        return new AssetSearchQuery(
            SearchBox?.Text,
            selectedType,
            selectedState,
            null,
            ParseLongFilter(PathIdFilter),
            int.TryParse(TypeIdFilter.Text.Trim(), out var typeId) ? typeId : null,
            minKb,
            maxKb,
            ReplacedOnlyFilter.IsChecked);
    }

    /// <summary>后台线程过滤 + 排序（快照先在 UI 线程取好，避免与集合修改
    /// 竞争），带代际守卫：过期结果直接丢弃；选中项按 AssetId 跨刷新保留。</summary>
    private async Task RunSearchAsync()
    {
        if (_project is null) { AssetList.ItemsSource = null; _lastResults = null; AssetTree.ItemsSource = null; return; }
        var generation = ++_searchGeneration;
        var query = BuildSearchQuery();
        var snapshot = _project.Assets.ToArray();
        IReadOnlyList<AssetRecord> results;
        try { results = await Task.Run(() => _search.Search(snapshot, query)); }
        catch (ArgumentException) { return; }
        if (generation != _searchGeneration) return;
        _lastResults = results;
        var selectedId = (AssetList.SelectedItem as AssetRecord)?.AssetId;
        AssetList.ItemsSource = results;
        if (selectedId is { } id)
        {
            var restored = results.FirstOrDefault(x => x.AssetId == id);
            if (restored is not null) AssetList.SelectedItem = restored;
        }
        AssetCountText.Text = results.Count == _project.Assets.Count
            ? _project.Assets.Count.ToString()
            : $"{results.Count} / {_project.Assets.Count}";
        // 目录树视图：按最新搜索结果重建根层（展开仍是惰性的）。
        if (_treeMode) RebuildTree();
    }

    // ── VS Code 式工作台标签页：固定「资源工作台」+ 按需打开的工具标签。
    //    活动栏图标与左栏按钮都经由 OpenWorkbench 打开；key 相同的标签只保留
    //    一份，重复点击 = 激活已打开的标签（不重复创建）。────────────────

    /// <summary>打开（或激活）一个工作台标签页；内容用工厂惰性创建。</summary>
    private void OpenWorkbench(string key, string title, Func<object> contentFactory)
    {
        if (FindWorkbenchTab(key) is { } existing)
        {
            WorkbenchTabs.SelectedItem = existing;
            return;
        }
        var tab = new System.Windows.Controls.TabItem
        {
            Header = BuildClosableHeader(title, key),
            Tag = key,
            Content = contentFactory(),
            ToolTip = title,
        };
        WorkbenchTabs.Items.Add(tab);
        WorkbenchTabs.SelectedItem = tab;
    }

    private System.Windows.Controls.TabItem? FindWorkbenchTab(string key)
        => WorkbenchTabs.Items.OfType<System.Windows.Controls.TabItem>().FirstOrDefault(t => Equals(t.Tag, key));

    /// <summary>标签头 = 标题 + ✕ 关闭按钮；关闭后激活相邻标签（固定的
    /// 资源工作台不走这里，由 XAML 直接声明）。</summary>
    private System.Windows.Controls.TabItem? CloseWorkbenchTab(string key)
    {
        if (FindWorkbenchTab(key) is not { } tab) return null;
        var index = WorkbenchTabs.Items.IndexOf(tab);
        WorkbenchTabs.Items.Remove(tab);
        if (WorkbenchTabs.SelectedItem is null && WorkbenchTabs.Items.Count > 0)
            WorkbenchTabs.SelectedIndex = Math.Max(0, index - 1);
        return tab;
    }

    private object BuildClosableHeader(string title, string key)
    {
        var panel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var closeButton = new System.Windows.Controls.Button
        {
            Content = "✕",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(4, 0, 4, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9F, 0xB0, 0xBF)),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.Bold,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        closeButton.Click += (_, _) => CloseWorkbenchTab(key);
        panel.Children.Add(closeButton);
        return panel;
    }

    private void ActivateAssetsWorkbench_Click(object sender, RoutedEventArgs e)
        => WorkbenchTabs.SelectedItem = AssetsTab;

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
    private static System.Windows.Controls.TreeViewItem MakeTreeItem(AssetTreeNode node)
    {
        var item = new System.Windows.Controls.TreeViewItem
        {
            Header = node.IsLeaf ? node.Name : $"{node.Name} ({node.Count})",
            Tag = node,
        };
        if (!node.IsLeaf) item.Items.Add(new object());
        return item;
    }

    private void AssetTree_Expanded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.TreeViewItem item ||
            item.Tag is not AssetTreeNode node || node.IsLeaf) return;
        if (item.Items.Count == 1 && item.Items[0] is not AssetTreeNode)
        {
            item.Items.Clear();
            foreach (var child in node.Expand())
                item.Items.Add(MakeTreeItem(child));
        }
    }

    private void AssetTree_SelectedItemChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is System.Windows.Controls.TreeViewItem { Tag: AssetTreeNode { IsLeaf: true } leaf })
            ApplyAssetSelection(leaf.Asset);
    }

    private void AssetTree_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AssetTree.SelectedItem is System.Windows.Controls.TreeViewItem { Tag: AssetTreeNode { IsLeaf: true, Asset: { } asset } })
            ActivateDefaultAction(asset);
    }

    private void ShowError(string title, Exception ex)
    {
        StatusText.Text = $"{title}：{ex.Message}";
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
