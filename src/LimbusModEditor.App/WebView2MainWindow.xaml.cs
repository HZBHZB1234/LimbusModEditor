using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Caching;
using LimbusModEditor.Application.Ipc;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NLog;

namespace LimbusModEditor.App;

/// <summary>
/// WebView2 正式版主窗口：承载 CoreWebView2 + IPC 网关 + 原生桥。
/// 取代旧 MainWindow 作为 W1 竖切片的宿主壳（旧页面不删，W2 独立任务清理）。
/// </summary>
public partial class WebView2MainWindow : Wpf.Ui.Controls.FluentWindow, IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private WebView2 _webView = null!;
    private IpcGateway? _gateway;
    private UnityCacheSqliteIndexStore? _store;
    private string _wwwrootDir = null!;
    private string _dataDir = null!;
    private CancellationTokenSource? _cts;
    // 跨线程读：事件出口（SendEvent）会被后台工作线程调用，见 PostToPage。
    private volatile bool _disposed;

    public WebView2MainWindow()
    {
        InitializeComponent();
        Log.Info("WebView2MainWindow 构造");
        Loaded += OnLoaded;
        Closed += OnClosed; // 窗口关闭时取消异步操作 + 取消事件订阅
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        try
        {
            StatusText.Text = "正在检测 WebView2 运行时…";

            // ── 1. 运行时检测（Fixed Version + Evergreen 双轨）──
            string? fixedVersionPath = GetFixedVersionPath();
            try
            {
                var runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
                Log.Info("WebView2 Evergreen 运行时已检测到: {0}", runtimeVersion);
                RuntimeVersionText.Text = $"运行时: {runtimeVersion} (Evergreen)";
            }
            catch (WebView2RuntimeNotFoundException)
            {
                if (fixedVersionPath is not null && Directory.Exists(fixedVersionPath))
                {
                    Log.Info("Evergreen 缺失，回退 Fixed Version: {0}", fixedVersionPath);
                    RuntimeVersionText.Text = "运行时: Fixed Version（随包分发）";
                }
                else
                {
                    Log.Error("WebView2 运行时未安装，且 Fixed Version 也不可用");
                    ShowRuntimeMissingGuide();
                    return;
                }
            }

            // ── 2. 准备目录 ──
            var baseDir = AppContext.BaseDirectory;
            _wwwrootDir = Path.Combine(baseDir, "wwwroot");
            _dataDir = Path.Combine(baseDir, "wwwroot", "data");

            var indexHtml = Path.Combine(_wwwrootDir, "index.html");
            if (!File.Exists(indexHtml))
            {
                Log.Warn("前端产物不存在: {0}", indexHtml);
                ShowFrontendMissingPlaceholder();
                return;
            }

            Directory.CreateDirectory(_dataDir);
            StatusText.Text = "正在初始化 WebView2 控件…";

            // ── 3. 创建 WebView2 环境（可取消）──
            _cts.Token.ThrowIfCancellationRequested();
            var envOptions = new CoreWebView2EnvironmentOptions();
            var envTask = CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: fixedVersionPath,
                userDataFolder: Path.Combine(baseDir, "WebView2Data"),
                options: envOptions);
            var env = await envTask.WaitAsync(_cts.Token);

            _webView = new WebView2 { MinWidth = 100, MinHeight = 100 };
            WebViewHost.Child = _webView;

            // EnsureCoreWebView2Async 不直接支持 CancellationToken，但可通过 _cts 在完成后检查
            await _webView.EnsureCoreWebView2Async(env);
            _cts.Token.ThrowIfCancellationRequested();

            Log.Info("WebView2 初始化完成: {0}", _webView.CoreWebView2.Environment.BrowserVersionString);

            // ── 4. 配置虚拟主机映射（禁 base64）──
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "lme.app", _wwwrootDir, CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "lme.data", _dataDir, CoreWebView2HostResourceAccessKind.Allow);

            Log.Info("虚拟主机映射: lme.app → {0}, lme.data → {1}", _wwwrootDir, _dataDir);

            // ── 5. 初始化 IPC 网关（使用 t34 组合根）──
            _store = InitializeIndexStore();
            var catalog = new AssetCatalog(_store, EmptyAssetStateSource.Instance);
            var projectState = new ProjectState();
            // SpineDataGateway 查的是 assets/bundles/strings 三张表（见 SpineAssetLocator），
            // 库名必须是 unity-cache-index.db；写成 spine-data.db 会得到一个没有表的空库，
            // 于是任何 Spine 资源都查不到（维基 Spine 绑定长期 0 覆盖的真实原因之一）。
            var spineData = new SpineDataGateway(Path.Combine(
                AppEnvironment.Current.CacheDirectory, WorkbenchCachePaths.UnityCacheIndexFileName));
            var bankStore = new BankIndexStore(AppEnvironment.Current.CacheDirectory);
            var bankIndex = new BankIndexService(bankStore);
            var langText = new LangTextWorkbenchService();
            var staticStore = new StaticTableIndexStore(AppEnvironment.Current.CacheDirectory);
            var staticIndex = new StaticIndexService(staticStore);
            _gateway = new IpcGateway(catalog, projectState, spineData, bankIndex, langText, staticIndex);

            // 事件出口（WEB-IPC-CONTRACT §1/§4）：网关推的 progress / toast 事件经此发给页面
            _gateway.EventSink = SendEvent;

            // ── 6. 注册消息处理器 ──
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // 前端首帧就绪后：折叠启动期状态栏（此后状态栏由前端自己绘制），
            // 并按契约 §7 推一条 session.hello 让前端拿到版本信息。
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            // ── 7. 导航到前端 ──
            StatusText.Text = "正在加载前端…";
            _webView.CoreWebView2.Navigate("https://lme.app/index.html");

            StatusText.Text = $"WebView2 就绪 | {_webView.CoreWebView2.Environment.BrowserVersionString}";
        }
        catch (OperationCanceledException)
        {
            Log.Info("WebView2 初始化已取消（窗口关闭）");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WebView2 初始化失败");
            StatusText.Text = $"初始化失败: {ex.Message}";
            MessageBox.Show(this, $"WebView2 初始化失败:\n\n{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 前端首帧就绪：收起启动期状态栏，并推 session.hello（契约 §7）。
    /// 之后界面里的全局状态栏由前端 StatusBar.vue 绘制，两条不并存。
    /// </summary>
    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            Log.Warn("前端导航失败: {0}", e.WebErrorStatus);
            StatusText.Text = $"前端加载失败: {e.WebErrorStatus}";
            return;
        }

        // 启动期信息已无意义，交给前端状态栏
        BootStatusBar.Visibility = Visibility.Collapsed;

        var runtimeVersion = _webView?.CoreWebView2?.Environment?.BrowserVersionString ?? "";
        var appVersion = typeof(WebView2MainWindow).Assembly.GetName().Version?.ToString() ?? "";
        try
        {
            SendEvent(IpcEvent.Create("session.hello", IpcJson.SerializePayload(
                new SessionHelloPayload(IpcContractVersion.V1, appVersion, runtimeVersion))));
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "推送 session.hello 失败（不影响功能）");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // 取消所有进行中的异步操作
        try { _cts?.Cancel(); } catch { }

        // 取消事件订阅（避免内存泄漏）
        if (_webView?.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        }

        // 释放资源
        Dispose();
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            Log.Debug("收到页面消息: {0}", json[..Math.Min(200, json.Length)]);

            var request = IpcRequest.FromJson(json);

            // 原生桥方法（需要 WPF 宿主执行）
            if (request.Method.StartsWith("dialog."))
            {
                var response = NativeBridgeService.HandleDialog(request, this);
                SendToPage(response);
                return;
            }
            if (request.Method.StartsWith("clipboard."))
            {
                var response = NativeBridgeService.HandleClipboard(request);
                SendToPage(response);
                return;
            }
            if (request.Method == "process.start")
            {
                var response = NativeBridgeService.HandleProcessStart(request);
                SendToPage(response);
                return;
            }

            // 数据查询走 IPC 网关（Application 层，无 WPF 依赖）
            if (_gateway is not null)
            {
                var responseJson = await _gateway.HandleRequestAsync(json);
                // 走 PostToPage 而不是直接碰 CoreWebView2：处理链里只要有一处
                // ConfigureAwait(false)，这里就不再是 UI 线程，直连会抛跨线程异常。
                PostToPage(responseJson);
            }
            else
            {
                SendToPage(IpcResponse.Failure(request.Id, IpcErrorCode.Internal, "网关未初始化"));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理页面消息失败");
        }
    }

    private void SendToPage(IpcResponse response) => PostToPage(response.ToJson());

    /// <summary>
    /// 推一条宿主事件给页面（无 id，可多次；契约 §1/§4）。
    ///
    /// <para><b>调用方可能是任意工作线程</b>：扫描 / 音频索引 / 静态表 / 关联分析 / 导出
    /// 都在后台跑，网关的 <c>EventSink</c> 就从那里回调进来。而 <c>CoreWebView2</c> 是
    /// WPF 依赖对象、只有 UI 线程能碰 —— 直接访问会抛
    /// 「调用线程无法访问此对象，因为另一个线程拥有该对象」。
    /// 日志里 <c>scan.run</c> 扫了 412 秒、六步全部 Scanned，最后仍回失败，就是这条异常
    /// 从 <c>Emit</c> 冒到 <c>HandleScanRunAsync</c> 的 catch 里造成的假失败。
    /// 所以统一在这里切回 UI 线程，调用方不必关心自己在哪个线程。</para>
    ///
    /// <para>序列化留在调用线程做（后台做更便宜），只有投递切线程。</para>
    /// </summary>
    private void SendEvent(IpcEvent evt) => PostToPage(evt.ToJson());

    /// <summary>把一段已序列化的消息投给页面；非 UI 线程调用时切回 UI 线程。</summary>
    private void PostToPage(string json)
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess())
        {
            // BeginInvoke 同优先级 FIFO ⇒ 事件顺序与产生顺序一致；
            // 用 Invoke 会让工作线程等 UI 线程，扫描期反而拖慢整体。
            try
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal,
                    new Action(() => PostToPage(json)));
            }
            catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
            {
                // 窗口正在关闭 / Dispatcher 已停：事件丢弃即可，不是错误。
            }
            return;
        }

        if (_webView?.CoreWebView2 is null) return;
        _webView.CoreWebView2.PostWebMessageAsString(json);
    }

    private UnityCacheSqliteIndexStore InitializeIndexStore()
    {
        // 必须是 .db 扩展名（WorkbenchCachePaths.UnityCacheIndexFileName）：
        // UnityCacheSqliteIndexStore 原样使用传入路径、不做 ChangeExtension，
        // 传 "unity-cache-index.json" 会被 ReadWriteCreate 建成 0 行的空库，
        // 于是资源索引恒查不到、立绘/图片解析全为空。
        return UnityCacheSqliteIndexStore.ForCacheDirectory(AppEnvironment.Current.CacheDirectory);
    }

    private string? GetFixedVersionPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var fixedPath = Path.Combine(baseDir, "fixed-runtime");
        return Directory.Exists(fixedPath) ? fixedPath : null;
    }

    private void ShowRuntimeMissingGuide()
    {
        var text = TextService.GetRuntimeMissingMessage();
        var placeholder = new TextBlock
        {
            Text = text,
            Foreground = System.Windows.Media.Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        WebViewHost.Child = placeholder;
        StatusText.Text = "WebView2 运行时未安装";
    }

    private void ShowFrontendMissingPlaceholder()
    {
        var text = TextService.GetFrontendMissingMessage();
        var placeholder = new TextBlock
        {
            Text = text,
            Foreground = System.Windows.Media.Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        WebViewHost.Child = placeholder;
        StatusText.Text = "前端产物不存在（请先构建 src/LimbusModEditor.Web/）";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        // _store is UnityCacheSqliteIndexStore which opens connections on demand; no persistent resource to dispose
        Log.Info("WebView2MainWindow 已释放");
    }
}
