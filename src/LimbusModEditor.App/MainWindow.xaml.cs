using System.Diagnostics;
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
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Domain.Projects;
using NLog;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.App;

/// <summary>
/// 主窗口（plan-02 页面架构）：三列布局 = 48 活动栏 / 220 共享侧边栏 / 页面宿主。
/// 顶部不再有标签页——活动栏图标与 Ctrl+Tab 直接切换常驻页面；侧边栏所有页面共用。
/// 本类同时是 <see cref="IWorkbenchHost"/> 实现，页面经它访问项目与共享环境。
/// </summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow, IWorkbenchHost
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IProjectService _projects = new ProjectService();
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
        Log.Info("主窗口构造：InitializeComponent 完成（XAML 已加载）");
        _startupScan = new StartupScanService(_env, _cacheScan);
        // 启动即进入资源工作台（活动栏首个入口）。
        ShowPage("assets");
        StartupTrace.Mark("MainWindow ctor 结束（资源页已就位）");
        // plan-15 修复「双击启动后过一会儿才弹窗」：目录定位（扫描 Steam 库 / LocalLow 候选）
        // 与提示条渲染都排到<b>首次呈现之后</b>再跑，任何一秒级动作都不许挡在窗口出现之前。
        Loaded += async (_, _) =>
        {
            StartupTrace.Mark("MainWindow Loaded");
            Log.Debug("主窗口 Loaded：等待首次呈现排空（Background 优先级）");
            await Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.Background);
            StartupTrace.Mark("首次呈现已排空（Background 优先级到手）");
            Log.Debug("主窗口首次呈现已排空，进入 OnWindowLoadedAsync");
            await OnWindowLoadedAsync();
            Log.Debug("主窗口 Loaded 流程结束");
        };
    }

    // ── 页面切换（plan-02 第 3 步）────────────────────────────────────────

    /// <summary>切换页面：惰性创建并常驻（保住各页面的搜索/选中/预览状态）。</summary>
    public void ShowPage(string key)
    {
        var createdNew = false;
        if (!_pages.TryGetValue(key, out var page))
        {
            page = CreatePage(key);
            if (page is null) return;
            _pages[key] = page;
            createdNew = true;
        }
        PageHost.Content = page;
        _currentPageKey = key;
        SyncActivityBar(key);
        // 设置页每次显示都重载字段（项目可能已切换）。
        if (page is SettingsPage settings) settings.Reload();
        UpdateHint();
        // 窗口尺寸一并记下：排查「窗口变矮后页面内容被裁掉 / 无法上下滑动」时，
        // 需要知道切页当时的窗口实际高度（本窗口没有 SizeChanged 处理器）。
        Log.Debug("切换页面：{0}（本次是否新建页面={1}；已创建 {2} 页；窗口 {3}×{4}）",
            key, createdNew, _pages.Count, Math.Round(ActualWidth), Math.Round(ActualHeight));
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
        // plan-16：调试/覆盖层入口已从资源页删除（统一走侧边栏「导出模组…」与
        // 「使用当前修改启动游戏进行调试」），页面不再需要事件转发。
        var page = new AssetsWorkbenchPage(this);
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

    // ── 侧边栏的 lang 导出入口已在 plan-16 S6 删除 ────────────────────────
    // 旧的「导出 lang 补丁…」「直接应用到 lang 目录…」两个按钮及其转调通道删除：
    // 文本编辑集自 S3 起由宿主持有（IWorkbenchHost.LangEdits），导出统一走「导出模组…」，
    // 直接落盘统一走「使用当前修改启动游戏进行调试」。

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
        Log.Info("启动引导开始：最近项目={0}", last ?? "-");
        if (!string.IsNullOrWhiteSpace(last) && File.Exists(last))
        {
            try
            {
                await OpenProjectFileAsync(last, "已恢复上次项目");
                Log.Info("启动引导结束：已恢复上次项目 {0}", Path.GetFileName(last));
                return;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动引导失败：恢复上次项目 {0}", last);
                ShowError("恢复上次项目失败", ex);
            }
        }
        // 没有可恢复的项目：启动扫描仍要跑（打开即扫，用户不必先点任何按钮）
        // —— 它会把四个索引库准备好，并说明「还没有项目所以跳过资源扫描」。
        Log.Debug("启动引导：无可恢复项目，进入启动扫描（无项目）");
        await RunStartupScanAsync();
        await WarmUpWorkbenchesAsync();
        UpdateDirectoryStatus(); // 目录状态一览放在最后：定位器要扫盘，绝不挡在模态窗口之前
        UpdateHint();
        Log.Debug("启动引导结束：无项目路径走完（启动扫描 + 工作台预热 + 目录状态）");
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
        var scanWatch = Stopwatch.StartNew();
        Log.Info("启动扫描开始：项目={0}，传入 projectMatchesIndex={1}，含前奏={2}",
            _project?.Name ?? "-", projectMatchesIndex?.ToString() ?? "-", prepare is not null);
        try
        {
            StatusText.Text = _project is null
                ? "启动扫描：正在准备四个索引库…"
                : "启动扫描：正在检查全部资源与四张表…";
            StartupTrace.Mark("RunStartupScanAsync: 构造模态窗口前");
            Log.Debug("启动扫描：弹占位窗口前，先起 ProbeCacheTables 后台任务");
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
            Log.Debug("启动扫描：占位窗口已关闭（耗时 {0} ms），模态构造情况={1}",
                scanWatch.ElapsedMilliseconds, dialog is null ? "未构造（视为取消）" : "已构造");

            if (dialog is null) return; // 占位窗口被用户提前关掉：当作取消
            StartupTrace.Mark("RunStartupScanAsync: ShowDialog 前");
            _startupScanCancellation = dialog.Cancellation;
            dialog.Owner = this;
            Log.Debug("启动扫描：进入模态 ShowDialog（此时主窗口 UI 线程开始被模态泵占用）");
            dialog.ShowDialog();
            StartupTrace.Mark("RunStartupScanAsync: ShowDialog 返回（窗口已关闭）");
            report = dialog.Result;
            Log.Debug("启动扫描：模态返回，耗时 {0} ms，报告={1}",
                scanWatch.ElapsedMilliseconds, report is null ? "null（用户关窗取消）" : report.Describe());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动扫描失败（已跑 {0} ms）：项目={1}", scanWatch.ElapsedMilliseconds, _project?.Name ?? "-");
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
        Log.Info("启动扫描结束（总耗时 {0} ms）：扫描 {1} 个资源，状态栏文案=「{2}」",
            scanWatch.ElapsedMilliseconds, report.ScannedCount, report.Describe());
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
        var warmWatch = Stopwatch.StartNew();
        Log.Debug("工作台预热开始：{0} 个页面（{1}）", PageOrder.Length, string.Join("/", PageOrder));
        foreach (var key in PageOrder)
        {
            var pageWatch = Stopwatch.StartNew();
            try
            {
                if (!_pages.ContainsKey(key))
                {
                    var created = CreatePage(key);
                    if (created is null) continue;
                    _pages[key] = created;
                    Log.Debug("工作台预热：按需新建页面 {0}", key);
                }
                switch (_pages[key])
                {
                    case AssetsWorkbenchPage assets: assets.OnProjectRefreshed(); break;
                    case BankWorkbenchPage bank: await bank.ReloadFromIndexAsync(); break;
                    case StaticWorkbenchPage tables: await tables.ReloadFromIndexAsync(); break;
                    case TextWorkbenchPage texts: await texts.ReloadFromIndexAsync(); break;
                    case SettingsPage settings: settings.Reload(); break;
                }
                Log.Debug("工作台预热：{0} 完成，耗时 {1} ms", key, pageWatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"预热「{key}」工作台失败（不影响其它页面）：{ex.Message}";
                Log.Warn(ex, "工作台预热失败：{0}（耗时 {1} ms，继续其它页面）", key, pageWatch.ElapsedMilliseconds);
            }
        }
        Log.Debug("工作台预热结束：总耗时 {0} ms", warmWatch.ElapsedMilliseconds);
    }

    /// <summary>P3.1: new-mod wizard scaffolds a project plus a minimal,
    /// handler-validated template package, then opens the project.</summary>
    private async void NewModWizard_Click(object sender, RoutedEventArgs e)
    {
        Log.Info("用户动作：点击「新建模组项目」（打开向导窗口）");
        var service = new LimbusModEditor.Application.Build.NewModTemplateService(_projects);
        var wizard = new NewModWizardWindow(service) { Owner = this };
        if (wizard.ShowDialog() != true || wizard.Result is null) return;
        Log.Info("新建向导返回：项目文件={0}", wizard.Result.ProjectFile ?? "-");
        try
        {
            _project = await _projects.LoadAsync(wizard.Result.ProjectFile!);
            _projectFile = wizard.Result.ProjectFile;
            await AfterProjectOpenedAsync("已通过向导创建项目");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开新建项目失败：{0}", wizard.Result.ProjectFile ?? "-");
            ShowError("打开新建项目失败", ex);
        }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        Log.Info("用户动作：点击「打开项目」");
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "LME 项目 (*.lmeproj)|*.lmeproj|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(_env.ProjectsDirectory) ? _env.ProjectsDirectory : null
        };
        if (dialog.ShowDialog() != true) return;
        Log.Info("用户选定项目文件：{0}", dialog.FileName);
        try
        {
            await OpenProjectFileAsync(dialog.FileName, "已打开项目");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开项目失败：{0}", dialog.FileName);
            ShowError("打开项目失败", ex);
        }
    }

    /// <summary>打开项目的统一入口：加载 → 记录最近项目 → 无感自动配置 →
    /// 需要时自动要求加载资源 → 更新提示。</summary>
    private async Task OpenProjectFileAsync(string projectFile, string? statusPrefix = null)
    {
        var loadWatch = Stopwatch.StartNew();
        Log.Info("打开项目开始：{0}（状态前缀={1}）", projectFile, statusPrefix ?? "-");
        using (StartupTrace.Span($"OpenProjectFileAsync: LoadAsync({Path.GetFileName(projectFile)})"))
            _project = await _projects.LoadAsync(projectFile);
        _projectFile = Path.GetFullPath(projectFile);
        _env.RegisterRecentProject(_projectFile, _project.Name);
        Log.Info("打开项目：LoadAsync 完成，耗时 {0} ms，项目名={1}，资产 {2} 条",
            loadWatch.ElapsedMilliseconds, _project.Name, _project.Assets.Count);
        await AfterProjectOpenedAsync(statusPrefix ?? "已打开项目");
        Log.Debug("打开项目结束：{0}，总耗时 {1} ms", Path.GetFileName(_projectFile), loadWatch.ElapsedMilliseconds);
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
        var afterWatch = Stopwatch.StartNew();
        Log.Info("项目就绪流程开始：「{0}」，项目={1}，文件={2}", prefix, _project?.Name ?? "-", _projectFile ?? "-");
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
        Log.Info("项目就绪流程结束：总耗时 {0} ms（含模态扫描全程）", afterWatch.ElapsedMilliseconds);
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
        if (_project is not { } project || _projectFile is null)
        {
            Log.Debug("模态前奏跳过：项目={0}，项目文件={1}",
                _project?.Name ?? "-", _projectFile ?? "-");
            return;
        }

        Log.Info("模态前奏开始：项目={0}，文件={1}", project.Name, _projectFile);
        status("正在自动配置目录（游戏 / Unity 缓存 / 模组 / FMOD）…");
        StartupTrace.Mark("前奏: AutoConfigureAsync 前");
        var configureWatch = Stopwatch.StartNew();
        using (StartupTrace.Span("前奏: AutoConfigureAsync"))
            await AutoConfigureAsync();
        Log.Debug("模态前奏：AutoConfigureAsync 完成，耗时 {0} ms", configureWatch.ElapsedMilliseconds);

        cancellationToken.ThrowIfCancellationRequested();
        status("正在从扫描索引重建资源列表（不解析任何 bundle）…");
        StartupTrace.Mark("前奏: 回灌前");
        var rehydrateWatch = Stopwatch.StartNew();
        var added = await RehydrateAssetsFromIndexAsync(project, cancellationToken);
        StartupTrace.Mark("前奏: 回灌后");
        if (added is null)
        {
            Log.Debug("模态前奏：回灌被取消（耗时 {0} ms），前奏中止", rehydrateWatch.ElapsedMilliseconds);
            return; // 已被取消
        }
        Log.Info("模态前奏结束：回灌 {0} 条引用资产，耗时 {1} ms（项目共 {2} 条）",
            added.Value, rehydrateWatch.ElapsedMilliseconds, project.Assets.Count);
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
        var rehydrateWatch = Stopwatch.StartNew();
        try
        {
            var added = await _cacheScan.RehydrateFromIndexAsync(project);
            cancellationToken.ThrowIfCancellationRequested();
            // 回灌成功后，索引里每一行都能在项目里找到对应记录 → 启动扫描可以跳过
            // 「119 万行逐条对账」（实测该步占启动扫描 50 秒里的 ~40 秒）。
            _assetsMatchIndex = true;
            Log.Debug("回灌引用资产成功：新增 {0} 条，耗时 {1} ms，_assetsMatchIndex=true（后续跳过逐条对账）",
                added, rehydrateWatch.ElapsedMilliseconds);
            return added;
        }        catch (OperationCanceledException)
        {
            Log.Debug("回灌引用资产被取消（用户关窗）：已跑 {0} ms，_assetsMatchIndex 保持 false", rehydrateWatch.ElapsedMilliseconds);
            return null;
        }
        catch (Exception ex)
        {
            // 失败只写状态栏 + 诊断：模态窗口还在跑，不能再弹一个模态 MessageBox 把它顶掉。
            StatusText.Text = $"重建资源索引失败（不影响功能，本次跳过逐条对账加速）：{ex.Message}";
            Log.Error(ex, "回灌引用资产失败（已跑 {0} ms，项目={1}）：本次跳过逐条对账加速",
                rehydrateWatch.ElapsedMilliseconds, project.Name);
            return 0;
        }
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        var saveWatch = Stopwatch.StartNew();
        Log.Info("用户动作：点击「保存项目」，目标={0}", _projectFile);
        try
        {
            await _projects.SaveAsync(_project, _projectFile);
            StatusText.Text = "项目已保存";
            Log.Debug("保存项目成功：{0}，耗时 {1} ms", Path.GetFileName(_projectFile), saveWatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存项目失败：{0}（已跑 {1} ms）", _projectFile, saveWatch.ElapsedMilliseconds);
            ShowError("保存项目失败", ex);
        }
    }

    /// <summary>项目保存的同步入口已被 async 版本取代：全缓存扫描后项目
    /// 含数十万资产，序列化必须在后台线程完成，不能阻塞 UI。</summary>
    private async Task<bool> SaveProjectInternalAsync()
    {
        if (_project is null || _projectFile is null) return false;
        var saveWatch = Stopwatch.StartNew();
        try
        {
            await _projects.SaveAsync(_project, _projectFile);
            Log.Debug("保存项目（内部入口）成功：{0}，耗时 {1} ms", Path.GetFileName(_projectFile), saveWatch.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存项目（内部入口）失败：{0}（已跑 {1} ms）", _projectFile, saveWatch.ElapsedMilliseconds);
            ShowError("保存项目失败", ex);
            return false;
        }
    }

    /// <summary>无感自动化（共享配置版）：只填充从未配置过的目录（游戏 /
    /// Unity 缓存 / 模组 / FMOD），旧项目里的目录值自动迁移进共享配置。
    /// 手动值永远不会被覆盖。</summary>
    private async Task AutoConfigureAsync()
    {
        if (_project is null) return;
        var configureWatch = Stopwatch.StartNew();
        var report = await Task.Run(() => _env.ApplyAutoConfigure(_project));
        if (_projectFile is not null) await SaveProjectQuietlyAsync();
        ResetLocatorCache();
        UpdateDirectoryStatus();
        if (report.Any) StatusText.Text = $"自动配置：{report.Describe()}（共享设置已保存到程序目录）";
        Log.Info("自动配置目录完成：项目={0}，有变更={1}，{2}，耗时 {3} ms",
            _project.Name, report.Any, report.Any ? report.Describe() : "无需变更", configureWatch.ElapsedMilliseconds);
    }

    /// <summary>加载游戏资源入口（侧边栏「① 获取资源」）。plan-15：与启动扫描<b>合并</b>——
    /// 打开的是同一个模态（四张表 + 全部资源），不再有只扫资源的两步式窗口。</summary>
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { StatusText.Text = "请先创建或打开项目"; return; }
        Log.Info("用户动作：点击「加载游戏资源」，改走启动扫描模态（_assetsMatchIndex={0}）", _assetsMatchIndex);
        await RunStartupScanAsync();
    }

    // ── plan-16：导出模组 / 调试启动（侧边栏仅这两个入口）────────────────

    /// <summary>本次调试会话的备份目录（关闭时据此逐字节还原）。</summary>
    private string? _debugApplyBackupDirectory;

    /// <summary>
    /// 侧边栏「导出模组…」：选定一个目录 → 分析全部修改 → 按「种类 → 格式」两层写出产物 → 报告。
    /// 分析在写出之前完成（<see cref="ModExportPlanService"/>），因此报告与产物必然一致。
    /// </summary>
    private async void ExportMod_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        using var exportScope = Log.Scope("导出模组");
        var exportWatch = Stopwatch.StartNew();
        Log.Info("用户动作：点击「导出模组…」，项目={0}，文件={1}", _project.Name, _projectFile);
        await AutoConfigureAsync();

        // 选目录：用 SaveFileDialog 的「选择此文件夹」惯例（与本仓库既有一键导出的交互一致）。
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            CheckFileExists = false,
            ValidateNames = false,
            FileName = "选择此文件夹",
            Title = "选择导出目标目录（会在其中建 <项目名>_fmod / _data / _text / _static）",
            InitialDirectory = Directory.Exists(_env.EffectiveModDirectory(_project) ?? string.Empty)
                ? _env.EffectiveModDirectory(_project)
                : Path.GetDirectoryName(_projectFile)!,
        };
        if (dialog.ShowDialog() != true) { Log.Info("导出模组：用户在选目录对话框取消，流程结束"); return; }
        var root = Path.GetDirectoryName(dialog.FileName);
        if (string.IsNullOrWhiteSpace(root))
        {
            Log.Warn("导出模组：选中的路径取不到目录（{0}），流程中止", dialog.FileName);
            return;
        }
        Log.Info("导出模组：目标目录={0}（选目录耗时 {1} ms）", root, exportWatch.ElapsedMilliseconds);

        var progressWindow = new ExportProgressWindow("导出模组",
            "正在分析当前修改并逐槽位写出；跳过的槽位与原因会在报告里列出来。") { Owner = this };
        var progress = new Progress<string>(progressWindow.Report);
        progressWindow.Show();
        IsEnabled = false;
        var cancelled = false;
        Log.Debug("导出模组：进度窗口已 Show（非模态），主窗口 IsEnabled=false，取消按钮尚未启用");
        try
        {
            // 分析与写出整体跑在后台线程：await 一个「同步完成」的 Task 会原地继续执行，
            // 因此导出链上的同步重活（bundle 重打包、逐对象 XZ、bank 重组）以前全部落在
            // UI 线程上 —— 这正是「模态窗口弹出后软件未响应且不恢复」的主因。
            var planWatch = Stopwatch.StartNew();
            Log.Debug("导出模组：PrepareExportPlanAsync 开始（实体化 + 存项目 + 分析计划）");
            var (plan, context) = await PrepareExportPlanAsync(root, progress, progressWindow.Token);
            Log.Debug("导出模组：PrepareExportPlanAsync 返回，耗时 {0} ms，计划槽位 {1} 个",
                planWatch.ElapsedMilliseconds, plan.PlannedSlotCount);
            if (plan.PlannedSlotCount == 0)
            {
                progressWindow.MarkFinished();
                progressWindow.Close();
                StatusText.Text = "没有可导出的修改（先改点东西：替换图片 / 替换音频样本 / 编辑文本表 / 编辑静态表）。";
                Log.Info("导出模组：无可导出修改，计划为空（总耗时 {0} ms），只弹报告窗口", exportWatch.ElapsedMilliseconds);
                new ModExportReportWindow(new ModPackExportResult(root, plan.ModName, [])) { Owner = this }.ShowDialog();
                return;
            }
            progressWindow.EnableCancel();
            Log.Debug("导出模组：EnableCancel 已调用（此后用户可取消），准备 Task.Run 写出 {0} 个槽位", plan.PlannedSlotCount);
            var writeWatch = Stopwatch.StartNew();
            var result = await Task.Run(() => new ModPackExportService().ExportAsync(
                _project!, Path.GetDirectoryName(_projectFile)!, plan, context, progress, progressWindow.Token),
                progressWindow.Token);
            Log.Debug("导出模组：写出完成，Task.Run 耗时 {0} ms；准备 MarkFinished + Close",
                writeWatch.ElapsedMilliseconds);
            progressWindow.MarkFinished();
            progressWindow.Close();
            StatusText.Text = result.Describe();
            Log.Info("导出模组成功：目标={0}，状态栏文案=「{1}」，总耗时 {2} ms",
                root, result.Describe(), exportWatch.ElapsedMilliseconds);
            new ModExportReportWindow(result) { Owner = this }.ShowDialog();
            Log.Debug("导出模组：报告窗口已关闭，流程结束");
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try { progressWindow.MarkFinished(); progressWindow.Close(); } catch { /* 已关闭 */ }
            StatusText.Text = "导出已取消：已写出的产物保留在目标目录，临时文件已清理（可再次导出覆盖）。";
            Log.Info("导出模组被取消：用户取消或关闭进度窗口，总耗时 {0} ms，目标={1}",
                exportWatch.ElapsedMilliseconds, root);
        }
        catch (Exception ex)
        {
            try { progressWindow.MarkFinished(); progressWindow.Close(); } catch { /* 已关闭 */ }
            Log.Error(ex, "导出模组失败：目标={0}，已跑 {1} ms", root, exportWatch.ElapsedMilliseconds);
            ShowError("导出模组失败", ex);
        }
        finally
        {
            IsEnabled = true;
            UpdateHint();
            // 用户在导出过程中关掉了进度窗口（它是 Show() 非模态）：导出被取消，
            // 这里必须明确说明，否则「窗口没了 + 主窗恢复」会让人以为导出成功了。
            if (!cancelled && progressWindow.ClosedByUser)
            {
                StatusText.Text = "导出已取消（进度窗口被关闭）。已写出的产物保留在目标目录。";
                Log.Info("导出模组：进度窗口被用户关闭（ClosedByUser），按取消处理并改写状态栏");
            }
            Log.Debug("导出模组 finally：主窗口 IsEnabled=true，总耗时 {0} ms", exportWatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 侧边栏「使用当前修改启动游戏进行调试」：导出到项目内 <c>builds/debug-pack</c>，
    /// 再按加载器语义铺到游戏 / Unity 缓存（自动备份），然后启动游戏；关闭编辑器时逐字节还原。
    /// </summary>
    private async void DebugMod_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _projectFile is null) { StatusText.Text = "请先创建或打开项目"; return; }
        var gameDirectory = _env.EffectiveGameDirectory(_project);
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            StatusText.Text = "请先在「设置」页配置游戏目录（调试要写游戏目录与 Unity 缓存）。";
            return;
        }
        if (IsGameRunning())
        {
            MessageBox.Show(this, "检测到 LimbusCompany.exe 正在运行。\n调试会改动游戏文件与 Unity 缓存，请先关闭游戏再试。",
                "调试启动", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirm = MessageBox.Show(this,
            "调试会把当前修改<b>直接铺到游戏目录与 Unity 缓存</b>（bank 覆盖、资源对象写回 __data、语言补丁放进 lang 目录）：\n" +
            "• 改动前逐个文件备份到项目的 backups/ 目录，退出编辑器时自动还原；\n" +
            "• 只用于自测，分发请用「导出模组…」；\n" +
            "• 调试期间请不要用别的工具改同一批文件。\n\n确定继续吗？",
            "使用当前修改启动游戏进行调试", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        await AutoConfigureAsync();
        var projectRoot = Path.GetDirectoryName(_projectFile)!;
        var debugPack = Path.Combine(projectRoot, "builds", "debug-pack");
        var progressWindow = new ExportProgressWindow("调试启动", "正在导出调试包并铺到游戏…") { Owner = this };
        var progress = new Progress<string>(progressWindow.Report);
        progressWindow.Show();
        IsEnabled = false;
        try
        {
            var (plan, context) = await PrepareExportPlanAsync(debugPack, progress, progressWindow.Token);
            progressWindow.EnableCancel();
            // 与「导出模组…」同一条线程口径：写出与铺盘整体在后台线程上跑
            // （否则 await 同步完成的 Task 会原地继续，重活全落在 UI 线程）。
            var export = await Task.Run(() => new ModPackExportService().ExportAsync(
                _project!, projectRoot, plan, context, progress, progressWindow.Token), progressWindow.Token);
            progressWindow.Report("正在铺到游戏目录与 Unity 缓存（自动备份）…");
            var report = await Task.Run(() => new ModApplyService().ApplyAsync(_project!, export,
                gameDirectory, _env.EffectiveUnityCacheDirectory(_project),
                Path.Combine(projectRoot, "backups"), context, progress, progressWindow.Token), progressWindow.Token);
            _debugApplyBackupDirectory = report.BackupDirectory;
            progressWindow.MarkFinished();
            progressWindow.Close();

            var launch = _gameLaunch.TryLaunch(gameDirectory, _project.GameExecutablePath);
            var skippedNote = report.Skipped.Count == 0 ? string.Empty : $"；{report.Skipped.Count} 项未应用（见状态栏）";
            StatusText.Text = launch.Started
                ? $"{report.Describe()}{skippedNote}；游戏已启动，退出编辑器时自动还原"
                : $"{report.Describe()}{skippedNote}；{launch.Message}";
            if (launch.Started && report.Skipped.Count > 0)
                MessageBox.Show(this, "以下内容本次调试未应用（原因如下）：\n\n" + string.Join("\n", report.Skipped),
                    "调试启动", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            try { progressWindow.MarkFinished(); progressWindow.Close(); } catch { /* 已关闭 */ }
            StatusText.Text = "调试启动已取消（未铺盘）。已生成的调试包保留在 builds/debug-pack。";
        }
        catch (Exception ex)
        {
            try { progressWindow.MarkFinished(); progressWindow.Close(); } catch { /* 已关闭 */ }
            ShowError("调试启动失败（已回滚本次改动）", ex);
        }
        finally
        {
            IsEnabled = true;
            UpdateHint();
        }
    }

    /// <summary>
    /// 导出/调试的共用前奏：实体化已编辑的缓存资源 → 存项目 → 分析计划。
    /// 两条链路必须走同一份分析与同一份上下文（否则报告与实际产物会不一致）。
    ///
    /// <para><b>线程口径</b>：<c>MaterializeEditedAssetsAsync</c> 会改写
    /// <c>project.Assets</c>（ObservableCollection 绑定在 UI 上），必须留在 UI 线程；
    /// 项目序列化（119 万行，实测 12~14 秒）与计划分析（同步遍历 127 万资产 ×4）
    /// 是纯读的重活，显式放到后台线程 —— 早先它们同步跑在 UI 线程上，是
    /// 「导出模态弹出后软件未响应」的组成部分。</para>
    /// </summary>
    private async Task<(ModExportPlan Plan, ModExportPlanContext Context)> PrepareExportPlanAsync(
        string rootDirectory, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        await MaterializeEditedAssetsAsync(progress);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => SaveProjectQuietlyAsync(), cancellationToken);
        var fmodDirectory = _env.EffectiveFmodLibraryDirectory(_project);
        var context = new ModExportPlanContext(
            UnityCacheDirectory: _env.EffectiveUnityCacheDirectory(_project),
            FmodDirectory: fmodDirectory);
        var project = _project!;
        var langEdits = _langEdits;
        var staticEdits = _staticEdits;
        var plan = await Task.Run(
            () => new ModExportPlanService().Plan(project, rootDirectory, langEdits, staticEdits, context),
            cancellationToken);
        return (plan, context);
    }

    /// <summary>游戏是否在运行（调试会写游戏文件，运行时一律拒绝）。</summary>
    private static bool IsGameRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("LimbusCompany").Length > 0;
        }
        catch (Exception)
        {
            return false; // 查不到就按「没在跑」处理（后续应用仍会失败并回滚，不会静默）
        }
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
        HintText.Text = $"已有 {edits} 处修改。下一步：点击侧边栏「导出模组…」选一个目录，编辑器会按「库种类 → 格式」写出产物；想立刻验收就点「使用当前修改启动游戏进行调试」。";
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

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _startupScanCancellation?.Cancel(); // 启动扫描（后台增量扫描）随关窗取消
        _assetsPage?.PersistUiState(); // plan-04：关窗时持久化布局占比
        WorkbenchShell.PersistAllPreviewWidths(); // plan-10：四个工作台的列宽一次性合并落盘
        if (_project is not null && !_project.RestoreDebugFilesOnClose) return;
        if (string.IsNullOrWhiteSpace(_debugApplyBackupDirectory)) return;
        try
        {
            var conflicts = await new ModApplyService().RestoreAsync(_debugApplyBackupDirectory);
            _debugApplyBackupDirectory = null;
            if (conflicts.Count > 0)
                MessageBox.Show(this, "以下游戏文件在调试期间已被其他程序修改，编辑器未覆盖恢复：" + Environment.NewLine +
                    string.Join(Environment.NewLine, conflicts), "调试恢复冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
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
