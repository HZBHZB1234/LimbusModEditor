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
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#11151A"));

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "扫描游戏资源（Unity 缓存）",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        });

        var entryCount = UnityCacheScanService.EnumerateCacheEntries(cacheDirectory).Count;
        _summary = new TextBlock
        {
            Text = entryCount > 0
                ? $"发现 {entryCount} 个游戏资源包（缓存条目）。扫描为引用模式：不复制文件，只建立可搜索的索引。\n已扫描过的资源走索引缓存，再次扫描接近瞬时。"
                : "未在缓存目录发现 <外层>/<内层>/__data 缓存条目。请检查「项目设置」中的 Unity 缓存目录。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.LightGray,
            Margin = new Thickness(0, 10, 0, 14)
        };
        panel.Children.Add(_summary);

        _progress = new ProgressBar { Height = 10, Minimum = 0, Maximum = Math.Max(1, entryCount), Value = 0 };
        panel.Children.Add(_progress);
        _state = new TextBlock
        {
            Text = entryCount > 0 ? (autoStart ? "正在准备扫描…" : "点击「开始扫描」。") : "—",
            Margin = new Thickness(0, 8, 0, 4),
            Foreground = Brushes.Gray,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        panel.Children.Add(_state);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        _startButton = new Button
        {
            Content = "开始扫描",
            Padding = new Thickness(18, 7, 18, 7),
            FontWeight = FontWeights.SemiBold,
            IsEnabled = entryCount > 0
        };
        _startButton.Click += async (_, _) => await RunScanAsync();
        buttons.Children.Add(_startButton);
        _closeButton = new Button { Content = "关闭", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        _closeButton.Click += (_, _) => { _cancellation.Cancel(); Close(); };
        buttons.Children.Add(_closeButton);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += async (_, _) =>
        {
            if (autoStart && !_started) await RunScanAsync();
        };
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
            var eta = string.Empty;
            if (p.ProcessedEntries > 4 && watch.Elapsed.TotalSeconds > 3)
            {
                var rate = p.ProcessedEntries / watch.Elapsed.TotalSeconds;
                var remaining = TimeSpan.FromSeconds((p.TotalEntries - p.ProcessedEntries) / Math.Max(0.1, rate));
                eta = remaining.TotalSeconds >= 90
                    ? $"，预计剩余约 {Math.Ceiling(remaining.TotalMinutes)} 分钟"
                    : $"，预计剩余约 {Math.Max(1, (int)Math.Round(remaining.TotalSeconds))} 秒";
            }
            _state.Text = $"[{p.ProcessedEntries}/{p.TotalEntries}] {Path.GetFileName(Path.GetDirectoryName(p.CurrentPath))}{eta}" +
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
