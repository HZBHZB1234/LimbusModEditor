using System.IO;
using System.Windows;
using System.Windows.Controls;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Debugging;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.App;

/// <summary>
/// 主窗口（plan-02 页面架构）：三列布局 = 48 活动栏 / 220 共享侧边栏 / 页面宿主。
/// 顶部不再有标签页——活动栏图标与 Ctrl+Tab 直接切换常驻页面；侧边栏所有页面共用。
/// 本类同时是 <see cref="IWorkbenchHost"/> 实现，页面经它访问项目与共享环境。
/// </summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow, IWorkbenchHost
{
    private readonly IProjectService _projects = new ProjectService();
    private readonly ProjectBuildService _builder = new();
    private readonly UnityBundleBuildService _unityBuilder = new();
    private readonly UnitySerializedFileBuildService _serializedBuilder = new();
    private readonly ModExportService _exporter = new(BuiltInFormatRegistry.Create());
    private readonly DebugApplyService _debugApply = new();
    private readonly GameLaunchService _gameLaunch = new();
    private readonly AppEnvironment _env = AppEnvironment.Current;
    private readonly UnityCacheScanService _cacheScan =
        new(Path.Combine(AppEnvironment.Current.CacheDirectory, "unity-cache-index.json"));
    /// <summary>启动扫描（每次启动/打开项目都跑一遍全部资源 + 四个缓存库）。
    /// 与 <see cref="_cacheScan"/> 共用同一实例，避免两个扫描器同时写同一个索引库。</summary>
    private readonly StartupScanService _startupScan;
    private CancellationTokenSource? _startupScanCancellation;

    /// <summary>项目里的引用资产是否已与扫描索引逐条一致（打开项目时成功回灌过即为 true）。
    /// 启动扫描据此跳过「119 万行逐条对账」——真实规模下那一步要几十秒且结果恒为 no-op。
    /// 保守起见：只有回灌成功才置 true，任何「可能删掉记录」的路径都不会把它留在 true。</summary>
    private bool _assetsMatchIndex;
    private readonly UnityCacheExportService _cacheExporter = new();
    private DebugApplySession? _debugSession;
    private ModProject? _project;
    private string? _projectFile;

    // ── 页面宿主（plan-02）：6 个 key 的常驻页面，惰性创建、切换不销毁 ──────
    private static readonly string[] PageOrder = ["assets", "bank", "text", "static", "help", "settings"];
    private readonly Dictionary<string, UserControl> _pages = [];
    private AssetsWorkbenchPage? _assetsPage;
    private string _currentPageKey = "assets";

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

    /// <summary>Current project (nullable). Exposed for pages.</summary>
    public ModProject? Project => _project;
    public string? ProjectFile => _projectFile;

    /// <summary>全局共享环境（IWorkbenchHost）。</summary>
    public AppEnvironment Env => _env;

    /// <summary>文本（lang）编辑集会话（plan-16 S3）：宿主持有，页面注入使用。
    /// 导出模组 / 调试两个入口从它读取当前全部文本修改。</summary>
    private readonly LimbusModEditor.Application.Texts.LangEditSession _langEdits = new();

    /// <summary>静态数据表编辑集会话（plan-16 S3）。</summary>
    private readonly LimbusModEditor.Application.Texts.StaticEditSession _staticEdits = new();

    /// <summary>文本编辑集会话（IWorkbenchHost）。</summary>
    public LimbusModEditor.Application.Texts.LangEditSession LangEdits => _langEdits;

    /// <summary>静态编辑集会话（IWorkbenchHost）。</summary>
    public LimbusModEditor.Application.Texts.StaticEditSession StaticEdits => _staticEdits;

    /// <summary>状态栏写入（IWorkbenchHost）。</summary>
    public void SetStatus(string message) => StatusText.Text = message;

    /// <summary>共享目录设置变更后的刷新（IWorkbenchHost）：重算定位缓存并更新侧边栏。</summary>
    public void RefreshDirectorySettings()
    {
        ResetLocatorCache();
        RefreshProjectState("设置已保存");
    }

    /// <summary>保存当前项目（IWorkbenchHost）；无项目时返回 false。</summary>
    public Task<bool> SaveProjectAsync() => SaveProjectInternalAsync();

    public MainWindow()
    {
        StartupTrace.Mark("MainWindow ctor 开始");
        InitializeComponent();
        _startupScan = new StartupScanService(_env, _cacheScan);
        // 启动即进入资源工作台（活动栏首个入口）。
        ShowPage("assets");
        StartupTrace.Mark("MainWindow ctor 结束（资源页已就位）");
        // plan-15 修复「双击启动后过一会儿才弹窗」：目录定位（扫描 Steam 库 / LocalLow 候选）
        // 与提示条渲染都排到<b>首次呈现之后</b>再跑，任何一秒级动作都不许挡在窗口出现之前。
        Loaded += async (_, _) =>
        {
            StartupTrace.Mark("MainWindow Loaded");
            await Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.Background);
            StartupTrace.Mark("首次呈现已排空（Background 优先级到手）");
            await OnWindowLoadedAsync();
        };
    }

    // ── 页面切换（plan-02 第 3 步）────────────────────────────────────────

    /// <summary>切换页面：惰性创建并常驻（保住各页面的搜索/选中/预览状态）。</summary>
    public void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = CreatePage(key);
            if (page is null) return;
            _pages[key] = page;
        }
        PageHost.Content = page;
        _currentPageKey = key;
        SyncActivityBar(key);
        // 设置页每次显示都重载字段（项目可能已切换）。
        if (page is SettingsPage settings) settings.Reload();
        UpdateHint();
    }

    private UserControl? CreatePage(string key) => key switch
    {
        "assets" => CreateAssetsPage(),
        "bank" => new BankWorkbenchPage(this),
        "text" => new TextWorkbenchPage(this),
        "static" => new StaticWorkbenchPage(this),
        "help" => new HelpPage(),
        "settings" => new SettingsPage(this),
        _ => null,
    };

    private AssetsWorkbenchPage CreateAssetsPage()
    {
        var page = new AssetsWorkbenchPage(this);
        // 调试操作属于宿主职责（构建覆盖层 / 应用并启动游戏）。
        page.BuildOverlayRequested += async (_, _) => await BuildOverlayAsync();
        page.DebugApplyRequested += async (_, _) => await DebugApplyAsync();
        _assetsPage = page;
        return page;
    }

    /// <summary>活动栏选中态与当前页面同步（侧边栏按钮/快捷键切页时也要对上）。</summary>
    private void SyncActivityBar(string key)
    {
        var button = key switch
        {
            "assets" => ActivityAssets,
            "bank" => ActivityBank,
            "text" => ActivityText,
            "static" => ActivityStatic,
            "help" => ActivityHelp,
            "settings" => ActivitySettings,
            _ => null,
        };
        if (button is not null) button.IsChecked = true;
    }

    /// <summary>需要项目的页面（无项目时被引导覆盖层遮住）；设置/教程始终可用。</summary>
    private static bool NeedsProject(string key) => key is "assets" or "bank" or "text" or "static";

    private void ActivityAssets_Click(object sender, RoutedEventArgs e) => ShowPage("assets");
    private void ActivityBank_Click(object sender, RoutedEventArgs e) => ShowPage("bank");
    private void ActivityText_Click(object sender, RoutedEventArgs e) => ShowPage("text");
    private void ActivityStatic_Click(object sender, RoutedEventArgs e) => ShowPage("static");
    private void ActivityHelp_Click(object sender, RoutedEventArgs e) => ShowPage("help");
    private void ActivitySettings_Click(object sender, RoutedEventArgs e) => ShowPage("settings");

    // ── 侧边栏的 lang 导出入口（plan-14 14.5）─────────────────────────────
    // 文本编辑集是文本工作台页面对象里的内存状态（服务实例属于页面），
    // 所以侧边栏这两个按钮只做「转调」：页面没打开过 → 编辑集一定是空的 → 引导先改文本。
    // 不在这里另建一套编辑集/导出实现（那会出现两份互相看不见的状态）。

    private void ExportLangPatch_Click(object sender, RoutedEventArgs e)
        => RunLangWorkbenchChannel(page => page.ExportPatchInteractive(),
            "还没有文本编辑集：点「文本工作台（lang 补丁）…」改完文本后，再回来点「导出 lang 补丁…」。");

    private void ApplyLangToGame_Click(object sender, RoutedEventArgs e)
        => RunLangWorkbenchChannel(page => page.ApplyToGameInteractive(),
            "还没有文本编辑集：点「文本工作台（lang 补丁）…」改完文本后，再回来点「直接应用到 lang 目录…」。");

    /// <summary>把侧边栏的 lang 入口路由到文本工作台的既有通道（没有编辑集时打开页面并说明）。</summary>
    private void RunLangWorkbenchChannel(Action<TextWorkbenchPage> channel, string emptyHint)
    {
        if (_pages.TryGetValue("text", out var page) && page is TextWorkbenchPage workbench && workbench.EditedFileCount > 0)
        {
            channel(workbench);
            return;
        }
        ShowPage("text");
        StatusText.Text = emptyHint;
    }

    /// <summary>启动引导（傻瓜化）：有上次项目就自动恢复；否则主窗口的
    /// 「无项目遮罩」引导「新建 / 打开 / 最近项目」（不再弹独立欢迎窗口）。
    /// plan-15：四个缓存库不再在这里单独建 —— 统一交给启动模态（见
    /// <see cref="RunStartupScanAsync"/>），无论有无项目都跑一遍。</summary>
    private async Task OnWindowLoadedAsync()
    {
        StartupTrace.Mark("OnWindowLoaded: UpdateHint 前");
        UpdateHint();
        var last = _env.Config.LastProjectFile;
        StartupTrace.Mark($"OnWindowLoaded: UpdateHint 后，最近项目={last}");
        if (!string.IsNullOrWhiteSpace(last) && File.Exists(last))
        {
            try
            {
                await OpenProjectFileAsync(last, "已恢复上次项目");
                return;
            }
            catch (Exception ex) { ShowError("恢复上次项目失败", ex); }
        }
        // 没有可恢复的项目：启动扫描仍要跑（打开即扫，用户不必先点任何按钮）
        // —— 它会把四个索引库准备好，并说明「还没有项目所以跳过资源扫描」。
        await RunStartupScanAsync();
        await WarmUpWorkbenchesAsync();
        UpdateDirectoryStatus(); // 目录状态一览放在最后：定位器要扫盘，绝不挡在模态窗口之前
        UpdateHint();
    }

    /// <summary>
    /// 启动扫描的唯一入口（plan-15）：**一个模态窗口跑完四张表 + 全部资源**，扫完窗口自动关闭，
    /// 随后保存项目、刷新界面，并把四个工作台的数据全部预热。
    ///
    /// <para>它同时是侧边栏「① 获取资源 → 自动加载游戏资源…」的实现 —— 按钮与启动走同一条路，
    /// 原来的两步式只扫资源的 <c>ScanDialog</c> 已删除。</para>
    ///
    /// <para><b>为什么每次启动都真跑一遍</b>：扫描是增量的（每步真实枚举磁盘、只重解析签名变过的
    /// 文件），热启动秒级；自动跑比让用户自己判断「要不要点扫描」更符合傻瓜化目标，也避免各工作台
    /// 首次打开才发现索引没建。</para>
    /// </summary>
    /// <param name="projectMatchesIndex">打开项目时刚回灌成功才传 true：索引里每一行都能在项目里
    /// 找到对应记录，于是资源扫描可以跳过「119 万行逐条对账」（实测那一步占 50 秒里的 ~40 秒）。
    /// 手动点侧边栏按钮时按 <see cref="_assetsMatchIndex"/> 的当前值判断（回灌成功后它就是 true）。</param>
    /// <param name="prepare">可选的前奏（plan-15 修复「双击启动后过一会儿才弹窗」）：
    /// 自动配置目录 + 从扫描索引回灌引用资产这类<b>几十秒级</b>的打开项目动作，以前跑在
    /// <b>窗口弹出之前</b>，用户看到的是一段没有任何反馈的空白等待（真实项目回灌 119 万行约 14 秒）。
    /// 现在它们作为第一步进模态窗口内执行并显示进度，窗口因此<b>立即</b>弹出。
    /// 前奏拿到的两个回调：<c>status</c> 写窗口里的进度行，<c>matchesIndex</c> 告诉宿主
    /// 「索引与项目已一致」（回灌成功）→ 资源扫描可跳逐条对账。</param>
    private async Task RunStartupScanAsync(
        bool? projectMatchesIndex = null,
        Func<Action<string>, Action, CancellationToken, Task>? prepare = null)
    {
        _startupScanCancellation?.Cancel();
        _startupScanCancellation = null;
        StartupScanReport? report;
        try
        {
            StatusText.Text = _project is null
                ? "启动扫描：正在准备四个索引库…"
                : "启动扫描：正在检查全部资源与四张表…";
            StartupTrace.Mark("RunStartupScanAsync: 构造模态窗口前");
            // 先开一个最小「正在准备…」窗口占位（构造几乎零成本），模态在它的 Loaded 里再构造 ——
            // 构造模态本身要探四张表（SQLite count，本机实测 ~430ms），不能让用户多等这一下。
            // 探测任务<b>在占位窗口显示前就起跑</b>（后台线程），与占位显示并行，不占弹出延迟。
            var countsTask = Task.Run(() => _startupScan.ProbeCacheTables());
            var placeholderText = new TextBlock
            {
                Text = _project is null
                    ? "正在准备四个索引库…"
                    : "正在准备四张表与全部资源，请稍候…",
                Margin = new Thickness(20, 16, 20, 16),
                Foreground = AppTheme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
            };
            var placeholder = new Window
            {
                Title = "正在扫描并加载资源",
                Width = 420,
                Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = AppTheme.WindowBackground,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Owner = this,
                Content = placeholderText,
            };
            StartupScanDialog? dialog = null;
            placeholder.Loaded += (_, _) =>
            {
                // 占位窗口已经在屏幕上了：**不等**探测结果，立刻把模态顶上（用户看到的是
                // 「主窗口 → 等待窗口 → 扫描窗口」连续三下，没有一段空白停顿）。
                // 四张表的行数由模态自己在探测任务完成后填进去（通常几十毫秒后）。
                StartupTrace.Mark("占位窗口已显示，立刻构造模态");
                dialog = new StartupScanDialog(
                    _startupScan, _cacheScan, _project,
                    projectMatchesIndex ?? _assetsMatchIndex,
                    prepare,
                    countsTask);
                placeholderText.Text = string.Empty;
                StartupTrace.Mark("模态已构造，关掉占位窗口");
                placeholder.Close();
            };
            placeholder.ShowDialog();

            if (dialog is null) return; // 占位窗口被用户提前关掉：当作取消
            StartupTrace.Mark("RunStartupScanAsync: ShowDialog 前");
            _startupScanCancellation = dialog.Cancellation;
            dialog.Owner = this;
            dialog.ShowDialog();
            StartupTrace.Mark("RunStartupScanAsync: ShowDialog 返回（窗口已关闭）");
            report = dialog.Result;
        }
        catch (Exception ex)
        {
            ShowError("启动扫描失败", ex);
            return;
        }
        finally
        {
            _startupScanCancellation = null;
        }

        if (report is null) { UpdateHint(); return; } // 用户关窗取消：不保存、不刷新

        // 扫描写了项目资产表 → 落盘（静默失败，不打断流程）。
        if (report.ScannedCount > 0 && _project is not null && _projectFile is not null)
            await SaveProjectQuietlyAsync();

        // 「加载到前端」：先把四个工作台的数据全部预热，再刷新界面状态。
        await WarmUpWorkbenchesAsync();
        RefreshProjectState();
        StatusText.Text = report.Describe();
        if (_project is null || _project.Assets.Count == 0) UpdateHint();
    }

    /// <summary>
    /// 把四个工作台的数据一次性预热（plan-15，用户口径：**打开软件时刷新所有表单，而不是切页懒加载**）。
    ///
    /// <para>页面 <b>主动创建</b>（未创建过的一并建出来），每个页面自己去读各自的表缓存 —— 扫描已经
    /// 把这些库写好，所以这一步只是读现成数据（本机实测：bank 917ms / 静态表 11ms / text 608ms），
    /// 不解析任何文件。逐页 try/catch 隔离：某一页读失败只写状态栏，不影响其它页，更不影响扫描结论。</para>
    /// </summary>
    private async Task WarmUpWorkbenchesAsync()
    {
        foreach (var key in PageOrder)
        {
            try
            {
                if (!_pages.ContainsKey(key))
                {
                    var created = CreatePage(key);
                    if (created is null) continue;
                    _pages[key] = created;
                }
                switch (_pages[key])
                {
                    case AssetsWorkbenchPage assets: assets.OnProjectRefreshed(); break;
                    case BankWorkbenchPage bank: await bank.ReloadFromIndexAsync(); break;
                    case StaticWorkbenchPage tables: await tables.ReloadFromIndexAsync(); break;
                    case TextWorkbenchPage texts: await texts.ReloadFromIndexAsync(); break;
                    case SettingsPage settings: settings.Reload(); break;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"预热「{key}」工作台失败（不影响其它页面）：{ex.Message}";
            }
        }
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
    /// 需要时自动要求加载资源 → 更新提示。</summary>
    private async Task OpenProjectFileAsync(string projectFile, string? statusPrefix = null)
    {
        using (StartupTrace.Span($"OpenProjectFileAsync: LoadAsync({Path.GetFileName(projectFile)})"))
            _project = await _projects.LoadAsync(projectFile);
        _projectFile = Path.GetFullPath(projectFile);
        _env.RegisterRecentProject(_projectFile, _project.Name);
        await AfterProjectOpenedAsync(statusPrefix ?? "已打开项目");
    }

    /// <summary>项目就绪后的无感流程（plan-15 修复启动时序）：这里只做<b>秒级</b>的事
    /// （加载项目文件、记录最近项目），把<b>几十秒级</b>的「自动配置目录 + 从索引回灌引用资产」
    /// 作为模态窗口的第一段前奏交给 <see cref="RunStartupScanAsync"/>。
    ///
    /// <para>为什么必须这么改：这两个动作原先在这里先跑完，窗口才弹出来 —— 真实项目的回灌
    /// （119 万行）约 14 秒，用户双击图标后只看到一段空白等待。现在模态窗口立即弹出，
    /// 回灌进度就在窗口里显示。</para>
    /// </summary>
    private async Task AfterProjectOpenedAsync(string prefix)
    {
        StartupTrace.Mark("AfterProjectOpened: RefreshProjectState");
        RefreshProjectState(prefix);
        StartupTrace.Mark("AfterProjectOpened: 进入 RunStartupScanAsync");
        using (StartupTrace.Span("AfterProjectOpened: RunStartupScanAsync（含模态全程）"))
            await RunStartupScanAsync(projectMatchesIndex: false, prepare: PrepareProjectInsideModalAsync);
        StartupTrace.Mark("AfterProjectOpened: 预热工作台");
        using (StartupTrace.Span("AfterProjectOpened: WarmUpWorkbenchesAsync"))
            await WarmUpWorkbenchesAsync();
        UpdateDirectoryStatus(); // 目录状态一览放在最后：定位器要扫盘，绝不挡在模态窗口之前
        UpdateHint();
        StartupTrace.Mark("AfterProjectOpened: 完成");
    }

    /// <summary>
    /// 模态窗口内的第一段前奏：无感自动配置共享目录（只填空位，从不覆盖手动值）→
    /// 从扫描索引回灌纯引用资产（项目文件已瘦身，不再携带它们）。
    /// <paramref name="status"/> 写窗口里的进度行；<paramref name="matchesIndex"/> 报告
    /// 「索引与项目已一致」，让后续资源扫描跳过逐条对账（真实规模下省几十秒）。
    /// </summary>
    private async Task PrepareProjectInsideModalAsync(
        Action<string> status, Action matchesIndex, CancellationToken cancellationToken)
    {
        if (_project is not { } project || _projectFile is null) return;

        status("正在自动配置目录（游戏 / Unity 缓存 / 模组 / FMOD）…");
        StartupTrace.Mark("前奏: AutoConfigureAsync 前");
        using (StartupTrace.Span("前奏: AutoConfigureAsync"))
            await AutoConfigureAsync();

        cancellationToken.ThrowIfCancellationRequested();
        status("正在从扫描索引重建资源列表（不解析任何 bundle）…");
        StartupTrace.Mark("前奏: 回灌前");
        var added = await RehydrateAssetsFromIndexAsync(project, cancellationToken);
        StartupTrace.Mark("前奏: 回灌后");
        if (added is null) return; // 已被取消
        status(added > 0
            ? $"资源索引已重建（{added:N0} 条引用资产，未解析任何 bundle）"
            : "资源索引已是最新（项目里的引用资产无需重建）");
    }

    /// <summary>阶段 C 项目瘦身配套：项目文件不再携带纯引用资产（真实全缓存
    /// 项目曾把 .lmeproj 撑到 1.6GB），打开时从扫描索引 SQLite 库重建（不解析
    /// 任何 bundle，秒级）。索引库缺失时静默返回 0（随后空项目逻辑会引导扫描）。
    /// 返回 null 表示被取消（关窗）。</summary>
    /// <param name="project">要回灌的项目（显式传入：模态前奏里 <c>_project</c> 字段可能已被替换）。</param>
    /// <param name="cancellationToken">取消（关窗）：回灌途中取消就立刻收手，不当作失败报告。</param>
    private async Task<int?> RehydrateAssetsFromIndexAsync(ModProject project, CancellationToken cancellationToken)
    {
        _assetsMatchIndex = false; // 先保守置否：只有真的回灌成功才敢声称「项目与索引一致」
        try
        {
            var added = await _cacheScan.RehydrateFromIndexAsync(project);
            cancellationToken.ThrowIfCancellationRequested();
            // 回灌成功后，索引里每一行都能在项目里找到对应记录 → 启动扫描可以跳过
            // 「119 万行逐条对账」（实测该步占启动扫描 50 秒里的 ~40 秒）。
            _assetsMatchIndex = true;
            return added;
        }        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // 失败只写状态栏 + 诊断：模态窗口还在跑，不能再弹一个模态 MessageBox 把它顶掉。
            StatusText.Text = $"重建资源索引失败（不影响功能，本次跳过逐条对账加速）：{ex.Message}";
            return 0;
        }
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
            MessageBox.Show(this,
                "项目还没有登记任何源模组。\n请用「导出模组（向导）…」，在向导里选择源模组文件后再导出。",
                "导出全部", MessageBoxButton.OK, MessageBoxImage.Information);
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

    /// <summary>加载游戏资源入口（侧边栏「① 获取资源」）。plan-15：与启动扫描<b>合并</b>——
    /// 打开的是同一个模态（四张表 + 全部资源），不再有只扫资源的两步式窗口。</summary>
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { StatusText.Text = "请先创建或打开项目"; return; }
        await RunStartupScanAsync();
    }

    /// <summary>傻瓜化一键导出：扫描资源上的全部修改 → Carra2 → 模组目录。</summary>
    /// <summary>导出思路（P3.10）：不要求用户先搞清 Carra2 / Bank / lang 补丁 /
    /// 多格式导出的区别——先分析项目里改了什么，再让用户挑出口。</summary>
    private void ExportIdeas_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        if (PickExportIdea() is not { } idea) return;
        RunExportIdea(idea.Kind);
    }

    private ExportIdea? PickExportIdea()
        => _project is null ? null : ShowExportIdeas(new ExportAdvisor().Analyze(_project, BuildAdvisorContext()));

    private ExportIdea? ShowExportIdeas(IReadOnlyList<ExportIdea> ideas)
    {
        var window = new ExportAdvisorWindow(ideas) { Owner = this };
        return window.ShowDialog() == true ? window.Result : null;
    }

    private ExportAdvisorContext BuildAdvisorContext()
    {
        var fmodDirectory = _env.EffectiveFmodLibraryDirectory(_project);
        return new ExportAdvisorContext(
            _env.EffectiveGameDirectory(_project),
            _env.EffectiveModDirectory(_project),
            !string.IsNullOrWhiteSpace(fmodDirectory) && Directory.Exists(fmodDirectory));
    }

    /// <summary>把选中的导出思路路由到既有通道（不新增导出实现）。</summary>
    private void RunExportIdea(ExportIdeaKind kind)
    {
        switch (kind)
        {
            case ExportIdeaKind.ScanFirst: Scan_Click(this, new RoutedEventArgs()); break;
            case ExportIdeaKind.EditFirst:
                ShowPage("assets");
                _assetsPage?.FocusSearch();
                StatusText.Text = "在资源视图里选中资源后替换或编辑，改完再来导出";
                break;
            case ExportIdeaKind.OneClickCarra2: OneClickExport_Click(this, new RoutedEventArgs()); break;
            case ExportIdeaKind.ExportWizard: Export_Click(this, new RoutedEventArgs()); break;
            case ExportIdeaKind.MultiFormat or ExportIdeaKind.AudioBank: ExportAll_Click(this, new RoutedEventArgs()); break;
            case ExportIdeaKind.LangText: ShowPage("text"); break;
            case ExportIdeaKind.DebugOverlay: _ = DebugApplyAsync(); break;
        }
    }

    /// <summary>一键导出前自动给出导出思路：只有一条可行通道（纯 Unity 资源修改）
    /// 时不打扰用户，直接导出；同时存在音频 / 已登记源模组等多种出口时先让用户挑。</summary>
    private bool ShouldOfferExportIdeas(out IReadOnlyList<ExportIdea> ideas)
    {
        ideas = [];
        if (_project is null) return false;
        ideas = new ExportAdvisor().Analyze(_project, BuildAdvisorContext());
        var channels = ideas.Count(x => x.Enabled &&
            x.Kind is not (ExportIdeaKind.ScanFirst or ExportIdeaKind.EditFirst
                or ExportIdeaKind.LangText or ExportIdeaKind.DebugOverlay));
        return channels > 1;
    }

    private async void OneClickExport_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        if (ShouldOfferExportIdeas(out var ideas))
        {
            if (ShowExportIdeas(ideas) is not { } idea) return;
            if (idea.Kind != ExportIdeaKind.OneClickCarra2) { RunExportIdea(idea.Kind); return; }
        }
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
        // 无项目时只遮住需要项目的页面（设置/教程页始终可用）。
        NoProjectOverlay.Visibility = _project is null && NeedsProject(_currentPageKey)
            ? Visibility.Visible
            : Visibility.Collapsed;
        var edits = _project?.Assets.Count(AssetEditService.HasEdits) ?? 0;
        if (_project is null)
        {
            HintText.Text = "👋 欢迎使用 Limbus Mod Editor —— 点击「新建模组项目」开始：填一个名字，其余（目录、游戏路径、资源加载）全部自动完成。";
            return;
        }
        if (_project.Assets.Count == 0)
        {
            HintText.Text = "编辑器每次启动都会自动扫描全部游戏资源（引用模式，不复制文件）。这里仍是 0，"
                          + "通常是还没找到 Unity 缓存：请确认「设置」页的缓存目录并先启动一次游戏生成缓存，"
                          + "再点侧边栏「自动加载游戏资源」重试。";
            return;
        }
        if (edits == 0)
        {
            HintText.Text = $"已索引 {_project.Assets.Count} 个游戏资源。下一步：在资源视图里按容器目录浏览或直接搜索（可切换排序与筛选），选中后用右侧按钮替换图片 / 编辑字段。";
            return;
        }
        HintText.Text = $"已有 {edits} 处修改。下一步：点击侧边栏「一键导出模组」生成 .carra2 到模组目录（有多种出口时先给导出思路，也可点「导出思路（自动分析）…」主动查看）。";
    }

    private async Task SaveProjectQuietlyAsync()
    {
        if (_project is null || _projectFile is null) return;
        try { await _projects.SaveAsync(_project, _projectFile); }
        catch (Exception ex) { StatusText.Text = $"项目保存失败：{ex.Message}"; }
    }

    /// <summary>侧边栏「重新自动获取目录」：无感自动化的手动兜底（例如项目
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

    /// <summary>侧边栏目录状态一览：显示「生效值（来源）」——来源可为
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

    // ── 调试覆盖层（页面按钮经事件转发到这里）────────────────────────────

    private async Task DebugApplyAsync()
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
            StatusText.Text = "请先在设置页配置游戏目录";
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
            _assetsPage?.SetDebugState(launch.Started
                ? $"已应用 {_debugSession.Changes.Count} 个文件，游戏已启动，退出前可恢复"
                : $"已应用 {_debugSession.Changes.Count} 个文件；{launch.Message}");
            StatusText.Text = launch.Started ? "调试覆盖层已应用，游戏已启动" : launch.Message;
        }
        catch (Exception ex) { ShowError("应用调试覆盖层失败", ex); }
    }

    private async Task BuildOverlayAsync()
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
            _assetsPage?.SetDebugState($"覆盖层已构建：{result.AppliedEdits} 个文件编辑，{unity + serialized} 个 Unity 对象编辑");
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
        _startupScanCancellation?.Cancel(); // 启动扫描（后台增量扫描）随关窗取消
        _assetsPage?.PersistUiState(); // plan-04：关窗时持久化布局占比
        WorkbenchShell.PersistAllPreviewWidths(); // plan-10：四个工作台的列宽一次性合并落盘
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

    public void RefreshProjectState(string? status = null)
    {
        ProjectNameText.Text = _project?.Name ?? "未打开项目";
        _assetsPage?.OnProjectRefreshed();
        UpdateHint();
        if (status is not null) StatusText.Text = $"{status}：{_project?.Name}";
    }

    // ── plan-03：拖放收窄为「单张图片拖到选中的图像资源上替换」──────────
    //    其他内容（.carra2 / 文件夹 / 多文件）不再触发导入，也不给可拖入光标。

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = _assetsPage?.CanAcceptImageDrop(e.Data) == true
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (_assetsPage is not { } page) return;
        await page.HandleImageDropAsync(e.Data);
    }

    /// <summary>快捷键：Ctrl+F 聚焦资源搜索框；Esc 在搜索框内清空筛选；
    /// Ctrl+Tab / Ctrl+Shift+Tab 按活动栏顺序循环切页（VS Code 同款）。</summary>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Control && e.Key == System.Windows.Input.Key.F)
        {
            if (_currentPageKey == "assets") _assetsPage?.FocusSearch();
            e.Handled = true;
        }
        else if (e.KeyboardDevice.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) && e.Key == System.Windows.Input.Key.Tab)
        {
            var shift = e.KeyboardDevice.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);
            var index = Math.Max(0, Array.IndexOf(PageOrder, _currentPageKey));
            var next = shift ? (index - 1 + PageOrder.Length) % PageOrder.Length : (index + 1) % PageOrder.Length;
            ShowPage(PageOrder[next]);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && _assetsPage?.TryEscapeSearch() == true)
        {
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
                "模组目录尚未配置或不存在（%APPDATA%\\LimbusCompanyMods）。\n可先点「重新自动获取目录」，或在「设置」页中手动指定。",
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

    private void ShowError(string title, Exception ex)
    {
        StatusText.Text = $"{title}：{ex.Message}";
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
