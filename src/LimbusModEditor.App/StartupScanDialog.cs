using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.App;

/// <summary>
/// 启动扫描的统一模态窗口（plan-15）：<b>打开即跑</b>，把「四张表 + 全部资源」这一遍扫描
/// 全程显示给用户，扫完<b>自动消失</b>。原来侧边栏「自动加载游戏资源…」只扫资源的
/// 两步式窗口（<c>ScanDialog</c>）已并入本窗口。
///
/// <para><b>为什么不可取消（用户确认）</b>：扫描写的是四个索引库，中途停下会留下
/// 「库在但只写了一半」的状态，用户下次还得再等一遍。窗口只提供关窗 —— 关窗即取消
/// （<see cref="Window.Closed"/> 触发 <see cref="CancellationTokenSource"/>），
/// 与关主窗口时的取消语义一致。</para>
///
/// <para><b>为什么失败/跳过时不自动关</b>：那种情况（没配游戏目录、缓存条目还没生成）
/// 需要用户看一眼中文原因才知道下一步做什么；静默消失等于把问题吞掉。</para>
///
/// <para>本窗口只做呈现：扫描逻辑全在 <see cref="StartupScanService"/> 里（WPF 无关、可单测）。
/// 四张表的行、表名、状态文案都来自 Application 层（<see cref="CacheTableRowView"/>），
/// 窗口侧不另写一份映射。</para>
/// </summary>
public sealed class StartupScanDialog : Window
{
    private readonly StartupScanService _service;
    private readonly UnityCacheScanService _cacheScan;
    private readonly ModProject? _project;
    private readonly bool _projectMatchesIndex;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    private readonly TextBlock _headline;
    private readonly TextBlock _state;
    private readonly ProgressBar _progress;
    private readonly TextBlock _hint;
    private readonly TableRow[] _rows = new TableRow[4];
    private readonly Button _closeButton;

    private bool _started;

    /// <summary>一次扫描的结果；取消 / 关窗时为 null（宿主据此决定不保存不刷新）。</summary>
    public StartupScanReport? Result { get; private set; }

    /// <summary>扫描落定后四张表的现场读况（模态四行的数据来源）。</summary>
    public IReadOnlyList<CacheTableRowCount>? TableCounts { get; private set; }

    /// <summary>扫描过程中给用户看的一行中文进度（宿主在关窗后还会用它写状态栏以外的场景）。</summary>
    public string LastStatus { get; private set; } = string.Empty;

    /// <summary>本窗口的取消源（宿主在关主窗口时一并取消，与既有 <c>_startupScanCancellation</c> 同一口径）。</summary>
    public CancellationTokenSource Cancellation => _cancellation;

    /// <param name="service">启动扫描服务（与宿主共用同一实例，避免两个扫描器同时写同一个库）。</param>
    /// <param name="cacheScan">游戏资源扫描服务（与侧边栏入口共用同一实例）。</param>
    /// <param name="project">当前项目；<b>允许 null</b>（启动阶段还没打开项目：只建库 + 探针，不扫资源）。</param>
    /// <param name="projectMatchesIndex">宿主保证「索引里每一行都能在项目里找到对应记录」时为 true
    /// （回灌刚成功）：资源扫描跳过逐条对账，真实规模下省几十秒。不确定传 false。</param>
    public StartupScanDialog(
        StartupScanService service,
        UnityCacheScanService cacheScan,
        ModProject? project,
        bool projectMatchesIndex = false)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(cacheScan);
        _service = service;
        _cacheScan = cacheScan;
        _project = project;
        _projectMatchesIndex = projectMatchesIndex;

        Title = "正在扫描并加载资源";
        Width = 760;
        Height = 560;
        MinWidth = 620;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.WindowBackground;
        ResizeMode = ResizeMode.CanResize;

        var panel = new StackPanel { Margin = new Thickness(24) };

        _headline = new TextBlock
        {
            Text = "正在扫描并加载资源",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.TextPrimary,
        };
        panel.Children.Add(_headline);

        // 扫描期间四张表的行数会在补齐缓存库之后再探一次，所以这里先说明口径。
        panel.Children.Add(new TextBlock
        {
            Text = "四张表 = 程序目录 cache\\ 下的四个索引库（各含两张业务表）。扫描是增量的："
                 + "每次都真实枚举磁盘，只重解析签名变过的文件，所以第二次启动通常是秒级。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Foreground = AppTheme.TextMuted,
            Margin = new Thickness(0, 6, 0, 14),
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });

        AddHeader(grid, 0, "表", 0);
        AddHeader(grid, 1, "业务表（行数）", 1);
        AddHeader(grid, 2, "状态", 2);
        AddHeader(grid, 3, "库写入", 3);

        // 打开窗口先探一次现场：用户一眼看到四张表现在各有多少行（库没建也显示「—」而不是空白）。
        // 初始状态 = 「等待」（Pending），行数据/状态文案都由 Application 层的投影给出，窗口不另写映射。
        var initialCounts = _service.ProbeCacheTables();
        TableCounts = initialCounts;
        var initialViews = StartupScanReport.CreateCacheTableRows(initialCounts, null);
        for (var index = 0; index < initialViews.Count && index < _rows.Length; index++)
        {
            var view = initialViews[index];
            var bounds = new Border
            {
                BorderBrush = AppTheme.BorderLine,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 6, 0, 6),
            };
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });

            var name = Cell(view.Label, AppTheme.TextPrimary);
            var counts = Cell(DescribeCounts(view), AppTheme.TextSecondary);
            var status = Cell(view.StatusText, AppTheme.TextSecondary);
            var stamp = Cell(DescribeStamp(view), AppTheme.TextMuted);
            Grid.SetColumn(name, 0);
            Grid.SetColumn(counts, 1);
            Grid.SetColumn(status, 2);
            Grid.SetColumn(stamp, 3);
            line.Children.Add(name);
            line.Children.Add(counts);
            line.Children.Add(status);
            line.Children.Add(stamp);
            bounds.Child = line;

            var row = new TableRow(status, counts, stamp);
            _rows[index] = row;
            Grid.SetRow(bounds, index + 1);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(bounds);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(grid);

        _progress = new ProgressBar { Height = 8, Minimum = 0, Maximum = 1, Value = 0, Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(_progress);

        _state = new TextBlock
        {
            Text = "正在准备…",
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = AppTheme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(_state);

        _hint = new TextBlock
        {
            Text = "这一步不需要操作：扫描完成后窗口会自动关闭，四个工作台的数据随即就绪。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppTheme.TextMuted,
            Margin = new Thickness(0, 8, 0, 0),
        };
        panel.Children.Add(_hint);

        _closeButton = new Button
        {
            Content = "关闭",
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed,
        };
        _closeButton.Click += (_, _) => Close();
        panel.Children.Add(_closeButton);

        Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => { try { _cancellation.Cancel(); } catch (ObjectDisposedException) { /* 已收尾 */ } };
    }

    private static void AddHeader(Grid grid, int column, string text, int row)
    {
        var header = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = AppTheme.TextMuted,
            Margin = new Thickness(0, 0, 0, 4),
        };
        Grid.SetColumn(header, column);
        Grid.SetRow(header, row);
        grid.Children.Add(header);
    }

    private static TextBlock Cell(string text, Brush brush) => new()
    {
        Text = text,
        Foreground = brush,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>模态四行里的可变单元格。</summary>
    private sealed record TableRow(TextBlock Status, TextBlock Counts, TextBlock Stamp);

    // ── 扫描 ─────────────────────────────────────────────────────────

    private async Task RunAsync()
    {
        if (_started) return;
        _started = true;

        // 打开窗口先探一次现场：用户一眼看到「现在库里有什么」（库没建也会显示 0 行而不是空白）。
        ApplyCounts(_service.ProbeCacheTables());

        var progress = new Progress<StartupScanProgress>(p =>
        {
            LastStatus = $"{p.Label}：{p.Detail}";
            _state.Text = LastStatus;
            _state.Foreground = AppTheme.TextSecondary;
        });

        try
        {
            var project = _project;
            var steps = new List<StartupScanStepResult>();

            // ① 四个缓存库建库/校表（没有项目也照跑：这是「四张表」能读出行的前提）。
            MarkRow(0, "检查中", AppTheme.TextSecondary);
            var prepared = await _service.PrepareDatabaseAsync(progress, _cancellation.Token);
            steps.Add(prepared);
            ApplyCounts(_service.ProbeCacheTablesAfterScan());
            ApplyStep(prepared);

            if (project is null)
            {
                _headline.Text = "四张表已就绪（未打开项目，跳过资源扫描）";
                _hint.Text = "还没有打开模组项目：本次只把四个索引库准备好。"
                           + "打开或新建项目后，会再跑一遍完整的资源扫描。";
                _state.Text = "完成";
                _progress.IsIndeterminate = false;
                _progress.Value = 1;
                Result = new StartupScanReport(steps, _watch.Elapsed);
                CloseAfterSuccess();
                return;
            }

            // ② 游戏资源：Unity 缓存里的全部 bundle（引用模式，只登记对象索引）。
            MarkRow(0, "扫描中", AppTheme.Accent);
            _state.Text = "扫描游戏资源：正在枚举 Unity 缓存…";
            var assets = await _service.ScanUnityAssetsStepAsync(project, progress, _cancellation.Token, _projectMatchesIndex);
            steps.Add(assets);
            ApplyStep(assets);

            // ③④⑤ 音频 / 静态表 / lang 文本三个工作台索引。
            var indexSteps = await _service.ScanWorkbenchIndexesAsync(project, progress, _cancellation.Token);
            steps.AddRange(indexSteps);
            foreach (var step in indexSteps) ApplyStep(step);

            TableCounts = _service.ProbeCacheTablesAfterScan();
            ApplyCounts(TableCounts);

            var report = new StartupScanReport(steps, _watch.Elapsed);
            Result = report;
            _progress.IsIndeterminate = false;
            _progress.Value = 1;
            ApplyCompletionText(report);
            if (NeedsUserAttention(steps))
            {
                // 有跳过 / 失败：留住窗口 + 中文原因，不自动关闭。
                _closeButton.Visibility = Visibility.Visible;
                _closeButton.Focus();
            }
            else
            {
                CloseAfterSuccess();
            }
        }
        catch (OperationCanceledException)
        {
            _state.Text = "扫描已取消（已写入的索引内容保留，下次启动会继续增量补齐）。";
            _state.Foreground = AppTheme.TextSecondary;
            _hint.Text = "窗口被关闭 / 主窗口退出，本次扫描已取消。";
        }
        catch (Exception ex)
        {
            _state.Text = $"扫描失败：{ex.Message}";
            _state.Foreground = Brushes.IndianRed;
            _hint.Text = "扫描只影响速度不影响功能：可先关闭本窗口，稍后在侧边栏「自动加载游戏资源…」重试。";
            _closeButton.Visibility = Visibility.Visible;
        }
    }

    /// <summary>成功路径：显示摘要 → 停一下让用户看清 → 自动关闭。</summary>
    private void CloseAfterSuccess()
    {
        _hint.Text = "窗口即将自动关闭，四个工作台的数据已就绪。";
        _closeButton.Visibility = Visibility.Collapsed;
        Dispatcher.InvokeAsync(async () =>
        {
            try { await Task.Delay(800); } catch (TaskCanceledException) { /* 关窗竞态 */ }
            if (IsVisible) Close();
        });
    }

    private static bool NeedsUserAttention(IReadOnlyList<StartupScanStepResult> steps)
        => steps.Any(x => x.Status is StartupScanStepStatus.Failed or StartupScanStepStatus.Skipped);

    private void ApplyCompletionText(StartupScanReport report)
    {
        var scanned = report.Steps.Where(x => x.Status == StartupScanStepStatus.Scanned).Select(x => x.Label).ToList();
        _headline.Text = NeedsUserAttention(report.Steps)
            ? "扫描完成（有项目未完成）"
            : scanned.Count == 0
                ? "完成：四张表与全部资源都已是最新"
                : $"完成：{string.Join("、", scanned)} 已更新";
        _state.Text = report.Describe() + "\n" + report.DescribeSteps();
        _state.Foreground = AppTheme.TextSecondary;
    }

    /// <summary>把现场行数灌进四行（读不到就显示「—」，绝不编造数字）。</summary>
    private void ApplyCounts(IReadOnlyList<CacheTableRowCount> counts)
    {
        TableCounts = counts;
        var views = StartupScanReport.CreateCacheTableRows(counts, null);
        for (var index = 0; index < views.Count && index < _rows.Length; index++)
        {
            _rows[index].Counts.Text = DescribeCounts(views[index]);
            _rows[index].Stamp.Text = DescribeStamp(views[index]);
        }
    }

    /// <summary>「bundles 1,194,061 行 · assets 2,388,122 行」（读不到 → 「—」）。</summary>
    private static string DescribeCounts(CacheTableRowView view)
        => view.Counts is null
            ? "—"
            : $"{view.Counts.PrimaryTable} {view.PrimaryCountText} · {view.Counts.SecondaryTable} {view.SecondaryCountText}";

    private static string DescribeStamp(CacheTableRowView view) => view.LastWriteText;

    private static int IndexOfTable(string fileName)
    {
        var rows = StartupScanService.CacheTableRows;
        for (var index = 0; index < rows.Count; index++)
            if (string.Equals(rows[index].FileName, fileName, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    /// <summary>把某一步的结果写到它对应的那一行（步骤没有归属库时写第 1 行 = 资源索引）。</summary>
    private void ApplyStep(StartupScanStepResult step)
    {
        var index = step.CacheDatabase is { } kind
            ? IndexOfTable(LimbusModEditor.Application.Caching.WorkbenchCachePaths.FileName(kind))
            : 0;
        if (index < 0 || index >= _rows.Length) return;

        var (fileName, _, label) = StartupScanService.CacheTableRows[index];
        // 状态文案只有一处来源（Application 层的 CacheTableRowView），窗口不另写一份映射。
        var statusText = new CacheTableRowView(label, fileName, null, step).StatusText;
        MarkRow(index, statusText, step.Status switch
        {
            StartupScanStepStatus.Failed => Brushes.IndianRed,
            StartupScanStepStatus.Scanned => AppTheme.TextPrimary,
            _ => AppTheme.TextSecondary,
        });
        _state.Text = step.Detail;
    }

    private void MarkRow(int index, string status, Brush brush)
    {
        if (index < 0 || index >= _rows.Length) return;
        _rows[index].Status.Text = status;
        _rows[index].Status.Foreground = brush;
    }
}
