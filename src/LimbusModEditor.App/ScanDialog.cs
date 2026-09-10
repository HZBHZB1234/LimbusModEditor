using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.App;

/// <summary>
/// 游戏资源扫描窗口（傻瓜化第二步）：显示将要扫描的缓存条目数，带进度与
/// 取消；完成后主窗口立即可以编辑。扫描为「引用模式」——不复制文件，
/// 只把 bundle 对象索引登记进项目；已扫描过的条目走程序目录索引缓存，
/// 二次扫描接近瞬时。
/// 缓存目录枚举（可能上万条目）在后台线程执行，打开窗口与点击开始时
/// UI 始终保持响应；进度上报在扫描服务侧做了 100ms 节流。
/// </summary>
public sealed class ScanDialog : Window
{
    private readonly UnityCacheScanService _service;
    private readonly ModProject _project;
    private readonly string _cacheDirectory;
    private readonly string? _gameDirectory;
    private readonly CancellationTokenSource _cancellation = new();

    private readonly ProgressBar _progress;
    private readonly TextBlock _state;
    private readonly TextBlock _summary;
    private readonly Button _startButton;
    private readonly Button _closeButton;
    private bool _initialized;
    private bool _started;

    public UnityCacheScanResult? Result { get; private set; }

    public ScanDialog(UnityCacheScanService service, ModProject project,
        string cacheDirectory, string? gameDirectory, bool autoStart)
    {
        _service = service;
        _project = project;
        _cacheDirectory = cacheDirectory;
        _gameDirectory = gameDirectory;

        Title = "扫描游戏资源";
        Width = 560;
        Height = 320;
        MinWidth = 480;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.WindowBackground;

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "扫描游戏资源（Unity 缓存）",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        });

        // 枚举缓存目录放后台线程：先显示占位文案，枚举完再更新计数。
        _summary = new TextBlock
        {
            Text = "正在枚举缓存目录…",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.WhiteSmoke,
            Margin = new Thickness(0, 10, 0, 14)
        };
        panel.Children.Add(_summary);

        _progress = new ProgressBar { Height = 10, Minimum = 0, Maximum = 1, Value = 0 };
        panel.Children.Add(_progress);
        _state = new TextBlock
        {
            Text = "正在枚举缓存…",
            Margin = new Thickness(0, 8, 0, 4),
            Foreground = Brushes.Silver,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        panel.Children.Add(_state);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        _startButton = new Button
        {
            Content = "开始扫描",
            Padding = new Thickness(18, 7, 18, 7),
            FontWeight = FontWeights.SemiBold,
            IsEnabled = false
        };
        _startButton.Click += async (_, _) => await RunScanAsync();
        buttons.Children.Add(_startButton);
        _closeButton = new Button { Content = "关闭", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        _closeButton.Click += (_, _) => { _cancellation.Cancel(); Close(); };
        buttons.Children.Add(_closeButton);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += async (_, _) => await InitializeAsync(autoStart);
    }

    /// <summary>后台枚举缓存条目，更新摘要与进度上限；autoStart 时直接开扫。</summary>
    private async Task InitializeAsync(bool autoStart)
    {
        if (_initialized) return;
        _initialized = true;
        IReadOnlyList<UnityCacheScanEntry> entries;
        try
        {
            entries = await Task.Run(() => UnityCacheScanService.EnumerateCacheEntries(_cacheDirectory));
        }
        catch (Exception ex)
        {
            _state.Text = $"枚举缓存失败：{ex.Message}";
            _state.Foreground = Brushes.IndianRed;
            return;
        }
        _progress.Maximum = Math.Max(1, entries.Count);
        _summary.Text = entries.Count > 0
            ? $"发现 {entries.Count} 个游戏资源包（缓存条目）。扫描为引用模式：不复制文件，只建立可搜索的索引。\n已扫描过的资源走索引缓存，再次扫描接近瞬时。"
            : "未在缓存目录发现 <外层>/<内层>/__data 缓存条目。请检查「项目设置」中的 Unity 缓存目录。";
        _state.Text = entries.Count > 0
            ? (autoStart ? "正在准备扫描…" : "点击「开始扫描」。")
            : "—";
        _startButton.IsEnabled = entries.Count > 0;
        if (autoStart && entries.Count > 0) await RunScanAsync();
    }

    private async Task RunScanAsync()
    {
        if (_started) return;
        _started = true;
        _startButton.IsEnabled = false;
        _state.Text = "正在扫描…";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var progress = new Progress<UnityCacheScanProgress>(p =>
        {
            _progress.Maximum = Math.Max(1, p.TotalEntries);
            _progress.Value = p.ProcessedEntries;
            // 收尾阶段（合并 / 写索引库）显示阶段名，避免界面像卡住。
            if (p.Phase is { Length: > 0 } phase)
            {
                _state.Text = $"[{p.ProcessedEntries}/{p.TotalEntries}] {phase}";
                return;
            }
            var eta = string.Empty;
            if (p.ProcessedEntries > 4 && watch.Elapsed.TotalSeconds > 3)
            {
                var rate = p.ProcessedEntries / watch.Elapsed.TotalSeconds;
                var remaining = TimeSpan.FromSeconds((p.TotalEntries - p.ProcessedEntries) / Math.Max(0.1, rate));
                eta = remaining.TotalSeconds >= 90
                    ? $"，预计剩余约 {Math.Ceiling(remaining.TotalMinutes)} 分钟"
                    : $"，预计剩余约 {Math.Max(1, (int)Math.Round(remaining.TotalSeconds))} 秒";
            }
            var current = string.IsNullOrWhiteSpace(p.CurrentPath)
                ? string.Empty
                : Path.GetFileName(Path.GetDirectoryName(p.CurrentPath));
            _state.Text = $"[{p.ProcessedEntries}/{p.TotalEntries}] {current}{eta}" +
                          $"（新建索引 {p.BundlesScanned}，缓存命中 {p.BundlesFromIndex}）";
        });
        try
        {
            Result = await _service.ScanIntoProjectAsync(
                _project, _cacheDirectory, _gameDirectory, progress, _cancellation.Token);
            _progress.Value = _progress.Maximum;
            _state.Text = $"扫描完成：{Result.AddedAssets} 个新资源，{Result.UpdatedAssets} 个已有资源已刷新。";
            foreach (var diagnostic in Result.Diagnostics)
                _state.Text += $"\n{diagnostic}";
            _closeButton.Content = "完成";
            _closeButton.Focus();
        }
        catch (OperationCanceledException)
        {
            _state.Text = "扫描已取消（已解析的条目已保留）。";
        }
        catch (Exception ex)
        {
            _state.Text = $"扫描失败：{ex.Message}";
            _state.Foreground = Brushes.IndianRed;
        }
        finally
        {
            _startButton.IsEnabled = false;
        }
    }
}
