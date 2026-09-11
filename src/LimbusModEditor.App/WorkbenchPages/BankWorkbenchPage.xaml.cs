using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.App;

/// <summary>
/// 音频工作台（plan-06 首版 → plan-11 重做）：按 FMOD Bank 浏览 / 试听 / 替换音频样本，
/// 并可导出整包 <c>.bank</c> 或 <c>.rebank</c> 到<b>模组目录</b>（绝不写游戏目录）。
///
/// <para><b>plan-11 的两视图</b>（互斥，右上角标准切换对）：</para>
/// <list type="number">
/// <item><b>☰ 全部音频</b>：跨全部 bank 的样本总表（列 = 样本名 / bank / FSB / codec / 采样率 /
/// 声道 / 时长 / 大小 / 状态）。数据源是 <c>cache/bank-index.db</c> 的 <c>samples</c> 表
/// （本机实测 52826 行），用 <see cref="DataGrid"/> 行虚拟化承载；筛选与排序都在内存行集上做，
/// 不重新读盘。</item>
/// <item><b>🗂 bank 树</b>：bank → FSB → 样本 三层，惰性展开。</item>
/// </list>
///
/// <para>首版的第三视图「☰ bank 列表」（逐个 bank 的类型 / FSB 数 / 大小）已删除：
/// 它和 bank 树的信息完全重合，只是把同一份 <see cref="BankIndexEntry"/> 摊平再排一次；
/// 少一个视图就少一份要跟着筛选/重建同步的状态。</para>
///
/// <para><b>索引是加速旁路</b>：删掉 <c>cache/bank-index.db</c> 后两个视图的数据与筛选结果
/// 完全一致，只是每次进页面要重新解析 1531 个 bank（冷建 ~19s，热读 ~0.9s）。</para>
/// </summary>
public partial class BankWorkbenchPage : UserControl
{
    /// <summary>浏览列的两种呈现形态（与壳的列表/树切换对一一对应）。</summary>
    private enum BankViewMode
    {
        AudioList,
        BankTree,
    }

    /// <summary>
    /// 总表 / 树 / 列表共用的样本行模型。
    /// 字段与 <see cref="BankSampleRecord"/> 一一对应（不新增推断值），
    /// 显示列由只读属性派生；<see cref="State"/> 供共享行样式做「已修改」金色高亮。
    /// </summary>
    private sealed record SampleRow(
        string BankFileName,
        string BankPath,
        int FsbIndex,
        int SampleIndex,
        string Name,
        string CodecName,
        int SampleRate,
        int Channels,
        uint SampleCount,
        long DataSize,
        long DataOffset,
        bool IsModified)
    {
        /// <summary>排序/筛选用的 bank 文件对象（懒创建，避免 5 万次分配）。</summary>
        public BankFileEntry? BankEntry { get; init; }

        /// <summary>时长（秒）；采样率为 0 时无法计算 → null（不猜）。</summary>
        public double? DurationSeconds => SampleRate > 0 ? SampleCount / (double)SampleRate : null;

        /// <summary>时长显示文案。</summary>
        public string DurationLabel => DurationSeconds is { } seconds ? $"{seconds:0.##} 秒" : "—";

        /// <summary>大小显示文案。</summary>
        public string SizeLabel => $"{DataSize:N0}";

        /// <summary>「FSB 3」显示文案。</summary>
        public string FsbLabel => $"FSB {FsbIndex}";

        /// <summary>共享行样式触发条件（Modified → 金色）。</summary>
        public string State => IsModified ? "Modified" : string.Empty;

        /// <summary>状态列文案。</summary>
        public string StateLabel => IsModified ? "已修改" : "—";

        /// <summary>定位文案（试听/替换日志用）。</summary>
        public string LocationLabel => $"{BankFileName} · FSB {FsbIndex} · 样本 {SampleIndex}";
    }

    private readonly IWorkbenchHost _host;
    private readonly BankIndexService _index;
    private readonly ModExportService _exporter = new(BuiltInFormatRegistry.Create());
    private readonly ObservableCollection<SampleRow> _viewSamples = [];
    private readonly List<SampleRow> _allSamples = [];
    private readonly List<BankIndexEntry> _entries = [];
    private readonly List<BankSampleRecord> _sampleRecords = [];
    private readonly Dictionary<string, bool> _modifiedBanks = new(StringComparer.OrdinalIgnoreCase);

    private readonly TextBox _search;
    private readonly ComboBox _kindFilter;
    private readonly ComboBox _codecFilter;
    private readonly ComboBox _durationFilter;
    private readonly CheckBox _modifiedOnly;
    private readonly CheckBox _showEventBanks;
    private readonly ComboBox _sortFilter;
    private readonly DataGrid _audioGrid;
    private readonly TreeView _bankTree;
    private readonly StackPanel _sampleDetail;
    private readonly TextBlock _contextInfo;
    private readonly TextBlock _sampleInfo;
    private readonly Button _auditionButton;
    private readonly Button _exportWavButton;
    private readonly Button _replaceButton;
    private readonly Button _exportBankButton;
    private readonly Button _exportRebankButton;
    private readonly Button _cancelIndexButton;
    private readonly Button _reloadButton;
    private readonly Button _clearCacheButton;
    private readonly ProgressBar _playBar;
    private readonly TextBlock _playTime;
    private readonly DispatcherTimer _playTimer;

    private System.Windows.Media.MediaPlayer? _player;
    private string? _playerFile;
    private string? _bankDirectory;
    private BankViewMode _viewMode = BankViewMode.AudioList;
    private BankIndexEntry? _selectedBank;
    private SampleRow? _selectedSample;
    private bool _isSelecting;
    private bool _indexing;
    private CancellationTokenSource? _indexCancellation;

    public BankWorkbenchPage(IWorkbenchHost host)
    {
        _host = host;
        InitializeComponent();
        Shell.Attach(host, WorkbenchPageKeys.Bank);
        _index = new BankIndexService(new BankIndexStore(host.Env.CacheDirectory));

        // ── 搜索行 ────────────────────────────────────────────────────
        _search = WorkbenchShell.CreateSearchBox("搜索样本名 / bank 文件名（50000+ 样本在内存行集上即时筛选）");
        _search.Width = 260;
        _search.TextChanged += (_, _) => ApplyFilter();
        _reloadButton = WorkbenchShell.CreateButton("重新扫描", async (_, _) => await RefreshAsync(forceReindex: true));
        _cancelIndexButton = WorkbenchShell.CreateButton("取消索引", (_, _) => _indexCancellation?.Cancel(), isEnabled: false);
        _clearCacheButton = WorkbenchShell.CreateButton("清空索引缓存", (_, _) => ClearCache());
        Shell.AddSearchItem(_search);
        Shell.AddSearchItem(_reloadButton);
        Shell.AddSearchItem(_cancelIndexButton);
        Shell.AddSearchItem(_clearCacheButton);

        // ── 筛选行（视图切换已移到右上角的标准切换对）──────────────────
        // 下拉一律走壳的共享工厂：具名样式（MinHeight=32，不写死 28，否则中文被裁）
        // + 按最长候选项估算宽度（不再由调用点拍脑袋传宽度魔数）。
        //
        // 类型下拉的候选项顺序 = BankTreeKindFilter 的取值顺序（见 ToKindFilter）：
        // 两处必须一起改，否则「全部类型」会被映射成错误的档位。
        _kindFilter = WorkbenchShell.CreateFilterCombo("按 bank 类型筛选（只影响 bank 树；事件 bank 没有样本负载）",
            "全部类型", "仅音频 bank", "仅事件 bank", "加密/无法识别");
        _kindFilter.SelectionChanged += (_, _) => ApplyFilter();
        Shell.AddFilterItem(_kindFilter);

        _showEventBanks = new CheckBox
        {
            Content = "显示事件 bank",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
            // 默认不勾：音频 bank 只有 100 多个，事件 bank 有上千个，默认全列会把音频 bank 淹掉
            // （plan-14.2 用户要求）。勾上只影响树，总表本来就只有样本行。
            IsChecked = false,
            ToolTip = "事件 bank（SNDH 表为空）没有任何可试听/替换的样本，默认不在 bank 树里显示；" +
                      "勾选后一并列出。选「仅事件 bank」筛选时会自动显示，无需勾选。",
        };
        _showEventBanks.Checked += (_, _) => ApplyFilter();
        _showEventBanks.Unchecked += (_, _) => ApplyFilter();
        Shell.AddFilterItem(_showEventBanks);

        _codecFilter = WorkbenchShell.CreateFilterCombo("按 codec 筛选（音频 bank 的 FSB 头部字段）", "全部 codec");
        _codecFilter.SelectionChanged += (_, _) => ApplyFilter();
        Shell.AddFilterItem(_codecFilter);

        _durationFilter = WorkbenchShell.CreateFilterCombo("按时长筛选",
            "全部时长", "≤ 1 秒", "1~5 秒", "5~30 秒", "> 30 秒", "无法计算时长");
        _durationFilter.SelectionChanged += (_, _) => ApplyFilter();
        Shell.AddFilterItem(_durationFilter);

        _modifiedOnly = new CheckBox
        {
            Content = "仅已修改",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
            ToolTip = "只显示已在项目里登记过替换的 bank 的样本",
        };
        _modifiedOnly.Checked += (_, _) => ApplyFilter();
        _modifiedOnly.Unchecked += (_, _) => ApplyFilter();
        Shell.AddFilterItem(_modifiedOnly);

        _sortFilter = WorkbenchShell.CreateFilterCombo("排序方式（在内存行集上排序，不重新读盘）",
            "按 bank + 样本", "按名称", "按时长（长→短）", "按大小（大→小）", "已修改在前");
        _sortFilter.SelectionChanged += (_, _) => ApplyFilter();
        Shell.AddFilterItem(_sortFilter);

        var clearFilters = WorkbenchShell.CreateButton("清除筛选", (_, _) => ClearFilters());
        Shell.AddFilterItem(clearFilters);

        // ── 视图一：全部音频（DataGrid，行虚拟化）────────────────────
        _audioGrid = new DataGrid { Style = FindStyle("WorkbenchDataGrid") };
        AddColumn(_audioGrid, "样本名", nameof(SampleRow.Name), 260);
        AddColumn(_audioGrid, "bank", nameof(SampleRow.BankFileName), 220);
        AddColumn(_audioGrid, "FSB", nameof(SampleRow.FsbLabel), 60);
        AddColumn(_audioGrid, "codec", nameof(SampleRow.CodecName), 90);
        AddColumn(_audioGrid, "采样率", nameof(SampleRow.SampleRate), 80);
        AddColumn(_audioGrid, "声道", nameof(SampleRow.Channels), 60);
        AddColumn(_audioGrid, "时长", nameof(SampleRow.DurationLabel), 80);
        AddColumn(_audioGrid, "大小", nameof(SampleRow.SizeLabel), 90);
        AddColumn(_audioGrid, "状态", nameof(SampleRow.StateLabel), 70);
        _audioGrid.ItemsSource = _viewSamples;
        _audioGrid.SelectionChanged += (_, _) =>
        {
            if (_isSelecting) return;
            _isSelecting = true;
            try
            {
                if (_audioGrid.SelectedItem is SampleRow row)
                {
                    _selectedSample = row;
                    SelectBank(row.BankPath, loadEditor: false);
                    LoadEditorForSample(row);
                    RefreshActionButtons();
                }
            }
            finally { _isSelecting = false; }
        };

        // ── 视图二：bank 树 ──────────────────────────────────────────
        _bankTree = WorkbenchShell.CreateTree();
        _bankTree.ToolTip = "bank → FSB → 样本；展开时才生成下一层；双击已在别的视图里选中的样本即在此定位";
        _bankTree.SelectedItemChanged += (_, e) => OnTreeSelectionChanged(e.NewValue as TreeViewItem);
        _bankTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(TreeItem_Expanded));
        // 双击定位：首版的「在 bank 树中定位」按钮已按计划删除，能力移到这里
        // （树/list 交互本身），所以别再为它单开一个按钮位和一份启用状态。
        _bankTree.MouseDoubleClick += (_, _) => LocateInTree();

        Shell.SetBrowseContent(_audioGrid);
        Shell.SetEmptyHint("正在准备音频索引…");

        // 视图切换走壳的标准切换对（plan-09 共享件）；本页只把「列表 / 树」映射到自己的
        // BankViewMode。AddViewToggles 只设置按钮初始状态、不触发事件，所以这里的状态是同步的。
        Shell.AddViewToggles("☰ 全部音频", "🗂 bank 树", treeDefault: false);
        Shell.ViewModeChanged += (_, mode) =>
            SwitchView(mode == WorkbenchViewMode.Tree ? BankViewMode.BankTree : BankViewMode.AudioList);

        // ── 编辑列 ───────────────────────────────────────────────────
        // bank 上下文行用主文字色（plan-14.1）：它下面是「选中样本」小节的次/弱色标签，
        // 若这行也用弱色，「当前在看哪个 bank」和「说明文字」就糊在同一层，看不出主次。
        _contextInfo = new TextBlock
        {
            Text = "未选择 bank",
            Style = FindStyle("WorkbenchSectionLabel"),
            Foreground = TryBrush("WbTextPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        _sampleDetail = BuildSampleDetailPanel();

        // 播放进度：把唯一的「试听」按钮换成进度条 + 播放/停止 + 时间标签。
        _auditionButton = WorkbenchShell.CreateButton("▶ 播放", async (_, _) => await AuditionAsync(), isEnabled: false);
        _auditionButton.ToolTip = "解码当前样本并用系统播放器试听；播放中再点一次即停止";
        _playBar = new ProgressBar
        {
            Height = 6,
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Margin = new Thickness(8, 0, 8, 0),
            IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "播放进度（可在进度条上点击/拖动跳转）",
        };
        _playBar.PreviewMouseLeftButtonDown += PlayBar_PreviewMouseDown;
        _playBar.PreviewMouseMove += PlayBar_PreviewMouseMove;
        _playTime = new TextBlock { Text = "00:00 / —", Foreground = TryBrush("WbTextMutedBrush"), VerticalAlignment = VerticalAlignment.Center };

        // 200ms：进度条视觉上连续（5 帧/秒足够），又不会为了刷一根 6px 的条频繁抢 UI 线程。
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _playTimer.Tick += (_, _) => UpdatePlaybackUi();

        // 动作区只留四个按钮：播放/停止在进度条旁边，「在 bank 树中定位」已被删除
        // （定位能力挂到 bank 树双击上，见 LocateInTree）。
        _exportWavButton = WorkbenchShell.CreateButton("导出样本 WAV…", async (_, _) => await ExportSampleWavAsync(), isEnabled: false);
        _replaceButton = WorkbenchShell.CreateButton("用 WAV 替换…", async (_, _) => await ReplaceSampleAsync(), isEnabled: false);
        _exportBankButton = WorkbenchShell.CreateButton("导出整包 .bank…", async (_, _) => await ExportAsync(ModFormatKind.Bank), isEnabled: false);
        _exportBankButton.ToolTip = "把当前 bank（含已登记的替换）导出到模组目录";
        _exportRebankButton = WorkbenchShell.CreateButton("导出 .rebank…", async (_, _) => await ExportAsync(ModFormatKind.Rebank), isEnabled: false);
        _exportRebankButton.ToolTip = "把当前 bank 导出为 .rebank 补丁包到模组目录";
        var buttonRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in new[] { _replaceButton, _exportWavButton, _exportBankButton, _exportRebankButton })
            buttonRow.Children.Add(button);

        var playRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        playRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        playRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        playRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_playBar, 1);
        Grid.SetColumn(_playTime, 2);
        playRow.Children.Add(_auditionButton);
        playRow.Children.Add(_playBar);
        playRow.Children.Add(_playTime);

        _sampleInfo = new TextBlock { Text = "—", Style = FindStyle("WorkbenchStatusText"), Margin = new Thickness(0, 6, 0, 0) };

        var editPanel = new StackPanel();
        editPanel.Children.Add(WorkbenchShell.CreatePanelTitle("bank / 样本"));
        editPanel.Children.Add(_contextInfo);
        editPanel.Children.Add(WorkbenchShell.CreateSectionLabel("选中样本", new Thickness(0, 10, 0, 0)));
        editPanel.Children.Add(_sampleDetail);
        editPanel.Children.Add(WorkbenchShell.CreateSectionLabel("播放", new Thickness(0, 10, 0, 0)));
        editPanel.Children.Add(playRow);
        editPanel.Children.Add(WorkbenchShell.CreateSectionLabel("操作", new Thickness(0, 10, 0, 0)));
        editPanel.Children.Add(buttonRow);
        editPanel.Children.Add(_sampleInfo);
        Shell.SetEditContent(new ScrollViewer { Content = editPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        RefreshSampleDetail();

        Loaded += async (_, _) => await RefreshAsync();
    }

    // ── 构件助手（色值一律来自共享样式）────────────────────────────

    private static Style? FindStyle(string key) => System.Windows.Application.Current?.TryFindResource(key) as Style;

    private static void AddColumn(DataGrid grid, string header, string path, double width)
        => grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path),
            Width = new DataGridLength(width),
        });

    /// <summary>
    /// 编辑列的键值行：左列 72px 次色标签，右列值（值文本在刷新时改写）。
    ///
    /// <para>plan-14.1：标签列 64 → 72px、标签色 <c>WbTextMutedBrush</c> →
    /// <c>WbTextSecondaryBrush</c>。「采样率 / 已修改」这类标签在 64px 下会被
    /// CharacterEllipsis 截成省略号，同时弱色标签压在面板底上几乎读不出来；
    /// 值与标签同处一屏，标签太弱就只剩值可读、行与行的对应关系丢失。
    /// （两个画刷的具体色值只在 Themes/WorkbenchStyles.xaml 里，页面不写色值。）</para>
    /// </summary>
    private static void AddDetailRow(Panel panel, string label, TextBlock value)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var caption = new TextBlock
        {
            Text = label,
            Foreground = TryBrush("WbTextSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(caption);
        grid.Children.Add(value);
        panel.Children.Add(grid);
    }

    /// <summary>键值行的值文本：默认主文字色（与次色标签拉开层次；刷新时可换成金色）。</summary>
    private static TextBlock DetailValue() => new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = TryBrush("WbTextPrimaryBrush"),
    };

    /// <summary>选中样本的紧凑键值格（左列 64px 弱色标签，右列值）；行序固定，值在刷新时改写。</summary>
    private static StackPanel BuildSampleDetailPanel()
    {
        var panel = new StackPanel();
        foreach (var label in new[] { "名称", "bank", "FSB", "codec", "采样率", "声道", "时长", "大小", "状态" })
            AddDetailRow(panel, label, DetailValue());
        return panel;
    }

    /// <summary>
    /// 写一个键值格的值。<paramref name="brush"/> 为 null 时用<b>主文字色</b>而不是清空 Foreground：
    /// 清成 null 会让文字落回继承色，「未修改的普通值」与「已修改（金色）」两种状态的层次就没了
    /// （plan-14.1：值必须显式主色、标签次色）。
    /// </summary>
    private static void SetDetailValue(Panel panel, int index, string text, Brush? brush = null)
    {
        if (index >= panel.Children.Count || panel.Children[index] is not Grid grid) return;
        if (grid.Children.Count < 2 || grid.Children[1] is not TextBlock value) return;
        value.Text = text;
        value.Foreground = brush ?? TryBrush("WbTextPrimaryBrush");
    }

    // ── 加载与索引 ───────────────────────────────────────────────────

    private async Task RefreshAsync(bool forceReindex = false)
    {
        var gameDirectory = _host.Env.EffectiveGameDirectory(_host.Project);
        _bankDirectory = _index.ResolveBankDirectory(gameDirectory);
        if (_bankDirectory is null)
        {
            Shell.SetStatus("没有找到 FMOD bank 目录。请在「设置」页填写游戏目录（bank 目录为 " +
                            string.Join("/", BankDirectoryService.BankRelativePath) + "）。");
            Shell.SetEmptyHint("没有找到 FMOD bank 目录：请先在「设置」页填写游戏目录。");
            _entries.Clear();
            _sampleRecords.Clear();
            _allSamples.Clear();
            _viewSamples.Clear();
            return;
        }

        var source = BankIndexSource.Describe(_bankDirectory);
        var cache = _index.Load(source);
        if (cache.IsUsable && !forceReindex)
        {
            // 热路径：直接读库（本机实测 1531 文件 / 52826 样本约 0.7 秒）。
            ApplySnapshot(cache.Snapshot.Directory, cache.Snapshot.Entries);
            Shell.SetEmptyHint(null);
            Shell.SetStatus($"{_allSamples.Count} 个样本 · {_entries.Count} 个 bank（读索引 " +
                            $"{cache.Snapshot.ReadElapsed.TotalMilliseconds:F0} ms）· 点「重新扫描」可强制重建索引");
            RefreshActionButtons();
            return;
        }

        await RunIndexAsync(source);
    }

    /// <summary>后台建索引（带进度、可取消），完成后刷新三个视图。</summary>
    private async Task RunIndexAsync(BankIndexSource source)
    {
        if (_indexing) return;
        _indexing = true;
        _indexCancellation = new CancellationTokenSource();
        _cancelIndexButton.IsEnabled = true;
        _reloadButton.IsEnabled = false;
        Shell.SetEmptyHint("正在建立音频索引…\n（首次需要读一遍全部 bank 文件，之后进页面直接读缓存）");
        var watch = Stopwatch.StartNew();
        try
        {
            var progress = new Progress<BankIndexProgress>(p => Shell.SetStatus(p.Describe()));
            var result = await _index.RefreshAsync(source, progress, _indexCancellation.Token);
            watch.Stop();
            var snapshot = _index.Load(source).Snapshot;
            ApplySnapshot(snapshot.Directory, snapshot.Entries);
            Shell.SetEmptyHint(null);
            Shell.SetStatus(result.Describe() + $"（建索引总计 {watch.Elapsed.TotalSeconds:0.0} 秒）");
            if (_index.Store.WasRecreated)
                Shell.SetStatus("索引缓存曾损坏，已自动删除重建。" + result.Describe());
        }
        catch (OperationCanceledException)
        {
            Shell.SetStatus("索引已取消。已完成的文件已入库，下次进页面会继续增量补齐。");
            var partial = _index.Load(source);
            if (partial.IsUsable) ApplySnapshot(partial.Snapshot.Directory, partial.Snapshot.Entries);
        }
        catch (Exception ex)
        {
            Shell.SetStatus($"建立/读取音频索引失败：{ex.Message}");
        }
        finally
        {
            _indexing = false;
            _indexCancellation?.Dispose();
            _indexCancellation = null;
            _cancelIndexButton.IsEnabled = false;
            _reloadButton.IsEnabled = true;
            RefreshActionButtons();
        }
    }

    /// <summary>把索引快照灌进三个视图（总表行集 / bank 列表 / 树）。</summary>
    private void ApplySnapshot(string directory, IReadOnlyList<BankIndexEntry> entries)
    {
        _entries.Clear();
        _entries.AddRange(entries);
        _sampleRecords.Clear();
        _allSamples.Clear();
        _modifiedBanks.Clear();

        // 已修改标记：项目里登记过 replacementPath 的 bank（与首版判定同口径，只读项目数据）。
        if (_host.Project is { } project)
        {
            foreach (var asset in project.Assets)
            {
                if (asset.Metadata.TryGetValue("bankSource", out var bankSource) && !string.IsNullOrWhiteSpace(bankSource))
                    _modifiedBanks[Path.GetFileName(bankSource)] = true;
            }
        }

        foreach (var entry in entries)
        {
            var entryModified = _modifiedBanks.ContainsKey(entry.FileName);
            var fileEntry = entry.ToFileEntry();
            foreach (var record in entry.Samples)
            {
                _sampleRecords.Add(record);
                _allSamples.Add(new SampleRow(
                    record.BankFileName,
                    record.BankPath,
                    record.FsbIndex,
                    record.SampleIndex,
                    record.Name,
                    record.CodecName,
                    record.SampleRate,
                    record.Channels,
                    record.SampleCount,
                    record.DataSize,
                    record.DataOffset,
                    entryModified)
                {
                    BankEntry = fileEntry,
                });
            }
        }

        RebuildCodecFilter();
        ApplyFilter();
        _bankDirectory = directory;
        if (_entries.Count == 0)
            Shell.SetEmptyHint("索引里没有 bank 记录（目录为空？）");
    }

    private void RebuildCodecFilter()
    {
        var current = _codecFilter.SelectedItem as string;
        var codecs = _allSamples.Select(x => x.CodecName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _codecFilter.Items.Clear();
        _codecFilter.Items.Add("全部 codec");
        foreach (var codec in codecs) _codecFilter.Items.Add(codec);
        _codecFilter.SelectedIndex = current is not null && codecs.Contains(current, StringComparer.OrdinalIgnoreCase)
            ? _codecFilter.Items.IndexOf(current)
            : 0;
    }

    // ── 筛选 / 排序 / 视图切换 ───────────────────────────────────────

    private void ClearFilters()
    {
        _search.Text = string.Empty;
        _kindFilter.SelectedIndex = 0;
        _codecFilter.SelectedIndex = 0;
        _durationFilter.SelectedIndex = 0;
        _modifiedOnly.IsChecked = false;
        _showEventBanks.IsChecked = false;   // 回到默认口径：事件 bank 不显示
        _sortFilter.SelectedIndex = 0;
        ApplyFilter();
    }

    /// <summary>类型下拉的选中项 → 树筛选档位。下标与 <c>CreateFilterCombo</c> 的候选项顺序一一对应。</summary>
    private BankTreeKindFilter ToKindFilter() => _kindFilter.SelectedIndex switch
    {
        1 => BankTreeKindFilter.AudioOnly,
        2 => BankTreeKindFilter.EventOnly,
        3 => BankTreeKindFilter.Unrecognized,
        _ => BankTreeKindFilter.All,
    };

    /// <summary>勾选框「显示事件 bank」是否勾上（bank 树专用，总表不受影响）。</summary>
    private bool ShowEventBanksChecked => _showEventBanks.IsChecked == true;

    private void ApplyFilter()
    {
        var query = _search.Text.Trim();
        var kind = _kindFilter.SelectedIndex switch
        {
            1 => (BankKind?)BankKind.Audio,
            2 => BankKind.Event,
            3 => null,
            _ => null,
        };
        var codec = _codecFilter.SelectedIndex <= 0 ? null : _codecFilter.SelectedItem as string;
        var duration = _durationFilter.SelectedIndex;
        var modifiedOnly = _modifiedOnly.IsChecked == true;

        IEnumerable<SampleRow> rows = _allSamples;
        if (query.Length > 0)
        {
            rows = rows.Where(x =>
                x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.BankFileName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        if (codec is not null)
            rows = rows.Where(x => string.Equals(x.CodecName, codec, StringComparison.OrdinalIgnoreCase));
        if (kind is { } expected)
        {
            rows = rows.Where(x => x.BankEntry is not null && x.BankEntry.Kind == expected);
            if (expected == BankKind.Event) rows = [];
        }
        else if (_kindFilter.SelectedIndex == 3)
        {
            rows = rows.Where(x => x.BankEntry is { Kind: BankKind.Encrypted or BankKind.Unknown });
        }
        rows = duration switch
        {
            1 => rows.Where(x => x.DurationSeconds is { } s && s <= 1),
            2 => rows.Where(x => x.DurationSeconds is { } s && s is > 1 and <= 5),
            3 => rows.Where(x => x.DurationSeconds is { } s && s is > 5 and <= 30),
            4 => rows.Where(x => x.DurationSeconds is { } s && s > 30),
            5 => rows.Where(x => x.DurationSeconds is null),
            _ => rows,
        };
        if (modifiedOnly) rows = rows.Where(x => x.IsModified);

        var sorted = _sortFilter.SelectedIndex switch
        {
            1 => rows.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            2 => rows.OrderByDescending(x => x.DurationSeconds ?? -1),
            3 => rows.OrderByDescending(x => x.DataSize),
            4 => rows.OrderByDescending(x => x.IsModified).ThenBy(x => x.BankFileName, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(x => x.BankFileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.FsbIndex).ThenBy(x => x.SampleIndex),
        };

        _viewSamples.Clear();
        foreach (var row in sorted) _viewSamples.Add(row);

        // 事件 bank 的隐藏量在这里算一次：总表与树共用同一份「默认隐藏事件 bank」口径，
        // 两处都由它出文案（不静默丢数据）。
        var hiddenEventBanks = BankTreeRules.CountHiddenEventBanks(_entries, ToKindFilter(), ShowEventBanksChecked);
        var hiddenNote = BankTreeRules.DescribeHiddenEventBanks(hiddenEventBanks);

        if (_viewMode == BankViewMode.BankTree)
        {
            RebuildTree();
            if (hiddenNote is not null) Shell.SetStatus(hiddenNote);
        }

        Shell.SetEmptyHint(_viewSamples.Count == 0 && _viewMode == BankViewMode.AudioList
            ? (_allSamples.Count == 0
                ? "索引里还没有样本。"
                : "没有样本匹配当前筛选条件。")
              + (hiddenNote is null ? string.Empty : $"\n{hiddenNote}")
            : null);
    }

    private void SwitchView(BankViewMode mode)
    {
        if (_viewMode == mode &&
            ReferenceEquals(Shell.Browse.Content, mode == BankViewMode.BankTree ? _bankTree : _audioGrid))
            return;
        _viewMode = mode;
        if (mode == BankViewMode.BankTree)
        {
            Shell.SetBrowseContent(_bankTree);
            RebuildTree();
        }
        else
        {
            Shell.SetBrowseContent(_audioGrid);
        }
        ApplyFilter();
    }

    // ── bank 树（bank → FSB → 样本，惰性展开）───────────────────────

    /// <summary>
    /// FSB 节点的 Tag（plan-14.3）。
    ///
    /// <para>此前 FSB 节点的 Tag 只是一个 <c>int</c>，填充样本时靠
    /// <c>item.Parent</c> 反查所属 bank。自动展开单 FSB 时那个 FSB 节点的容器
    /// <b>往往还没生成</b>（容器要等父节点展开后才由虚拟化面板生产），
    /// <c>Parent</c> 会是 null → 拿不到所属 bank。这里把所属 bank 直接随 Tag 带上：
    /// 与容器是否生成无关，自动展开也能走同一条填充路径。</para>
    /// </summary>
    private sealed record FsbNodeTag(BankIndexEntry Bank, int FsbIndex);

    private void RebuildTree()
    {
        if (_viewMode != BankViewMode.BankTree) return;
        var filter = ToKindFilter();
        var showEventBanks = ShowEventBanksChecked;
        var roots = new List<TreeViewItem>();
        foreach (var entry in _entries.Where(x => BankTreeRules.ShouldShowBank(x.Kind, filter, showEventBanks)))
        {
            var node = new TreeViewItem
            {
                Header = $"{entry.FileName}（{entry.KindLabel} · {entry.FsbCount} FSB · {entry.Samples.Count} 样本）",
                Tag = entry,
                Style = WorkbenchShell.CreateTreeItemStyle(),
            };
            node.Items.Add(CreatePlaceholder()); // 惰性占位
            roots.Add(node);
        }
        _bankTree.ItemsSource = roots;
    }

    private const string PlaceholderText = "载入中…";

    /// <summary>
    /// 惰性占位子项的 Tag（**单例哨兵**，plan-14.3）。
    ///
    /// <para>为什么用一个对象当哨兵、而不是用 <c>Tag == null</c>：事件 bank 展开后，
    /// 占位子项会被换成一行「无样本负载（事件 bank）」说明文字，它也没有真实 Tag；
    /// 用 <c>Tag == null</c> 判定就会把那行说明误当成「还没填充」，每次展开都重新填一遍。
    /// 有了哨兵，「这一层建过没有」就是一次引用比较，且判定只影响该节点本身
    /// （不能用页面级 bool：那会让第一个展开的 bank 之后，别的 bank 永远停在「载入中…」）。</para>
    /// </summary>
    private static readonly object PlaceholderTag = new();

    private static TreeViewItem CreatePlaceholder() => new() { Header = PlaceholderText, Tag = PlaceholderTag };

    /// <summary>该节点是否还挂着惰性占位（= 这一层还没建过）。</summary>
    private static bool IsPlaceholder(TreeViewItem item)
        => item.Items.Count == 1 && item.Items[0] is TreeViewItem { Tag: var tag } && ReferenceEquals(tag, PlaceholderTag);

    /// <summary>
    /// 惰性填充：节点展开时才把下一层建出来（<b>幂等</b>：只在还挂着占位时填充）。
    /// </summary>
    private void TreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item) return;
        switch (item.Tag)
        {
            case BankIndexEntry entry when IsPlaceholder(item):
                BuildFsbNodes(item, entry);
                break;
            case FsbNodeTag fsb when IsPlaceholder(item):
                BuildSampleNodes(item, fsb.Bank, fsb.FsbIndex);
                break;
        }
    }

    /// <summary>把 bank 节点的子项建出来（每个 FSB 一行 + 事件 bank 的说明行），
    /// 单 FSB 时顺带把那个 FSB 展开到样本层（plan-14.3）。</summary>
    private void BuildFsbNodes(TreeViewItem item, BankIndexEntry entry)
    {
        item.Items.Clear();
        var fsbNodes = new List<(TreeViewItem Node, FsbNodeTag Tag)>();
        foreach (var group in entry.Samples.GroupBy(x => x.FsbIndex).OrderBy(x => x.Key))
        {
            var tag = new FsbNodeTag(entry, group.Key);
            var fsbNode = new TreeViewItem
            {
                Header = $"FSB {group.Key}（{group.Count()} 个样本）",
                Tag = tag,
                Style = WorkbenchShell.CreateTreeItemStyle(),
            };
            fsbNode.Items.Add(CreatePlaceholder()); // 惰性占位
            item.Items.Add(fsbNode);
            fsbNodes.Add((fsbNode, tag));
        }
        if (entry.Samples.Count == 0)
            item.Items.Add(new TreeViewItem { Header = entry.KindNote ?? "无样本负载（事件 bank）", Style = WorkbenchShell.CreateTreeItemStyle() });

        // plan-14.3：只有一个 FSB 的 bank 直接把 FSB 也展开（少一层手点）。
        // 走的是与「手点展开 FSB」**同一条**填充路径（BuildSampleNodes），
        // 否则自动展开与手点展开的树会长得不一样。
        // 这里不能只设 IsExpanded 等事件：此刻 FSB 节点的容器往往还没生成，
        // Expanded 的冒泡路由到不了挂在 TreeView 上的处理器（见 FsbNodeTag 的注释）。
        if (BankTreeRules.ShouldAutoExpandSingleFsb(fsbNodes.Count))
        {
            var (node, tag) = fsbNodes[0];
            BuildSampleNodes(node, tag.Bank, tag.FsbIndex);
            node.IsExpanded = true;
        }
    }

    /// <summary>把 FSB 节点的样本行建出来（同 <see cref="BuildFsbNodes"/> 的幂等判据）。</summary>
    private void BuildSampleNodes(TreeViewItem item, BankIndexEntry bank, int fsbIndex)
    {
        if (item.Items.Count > 0) return;

        foreach (var sample in bank.Samples.Where(x => x.FsbIndex == fsbIndex))
        {
            var row = _allSamples.FirstOrDefault(x =>
                string.Equals(x.BankPath, bank.Path, StringComparison.OrdinalIgnoreCase) &&
                x.FsbIndex == sample.FsbIndex && x.SampleIndex == sample.SampleIndex);
            if (row is null) continue;
            item.Items.Add(new TreeViewItem
            {
                Header = $"{row.Name}（{row.CodecName} · {row.DurationLabel} · {row.SizeLabel} 字节）",
                Tag = row,
                // 未选中时一律主文字色（plan-14.1 第 5 条）：只有「已修改」才换金色，
                // 其余交给样式里的主色，不再用默认模板那层偏暗的灰。
                Foreground = row.IsModified ? TryBrush("WbModifiedBrush") : TryBrush("WbTextPrimaryBrush"),
                Style = WorkbenchShell.CreateTreeItemStyle(),
            });
        }
    }

    private void OnTreeSelectionChanged(TreeViewItem? item)
    {
        if (_isSelecting || item is null) return;
        switch (item.Tag)
        {
            case BankIndexEntry entry:
                _selectedSample = null;
                SelectBank(entry.Path, loadEditor: true);
                break;
            case SampleRow row:
                _isSelecting = true;
                try
                {
                    _selectedSample = row;
                    SelectBank(row.BankPath, loadEditor: false);
                    LoadEditorForSample(row);
                }
                finally { _isSelecting = false; }
                break;
            case FsbNodeTag fsb:
                _selectedSample = null;
                SelectBank(fsb.Bank.Path, loadEditor: true);
                Shell.SetStatus($"FSB {fsb.FsbIndex} · {fsb.Bank.FileName}");
                break;
        }
    }

    /// <summary>选中一个 bank（不重入触发选择事件）。</summary>
    private void SelectBank(string bankPath, bool loadEditor)
    {
        _selectedBank = _entries.FirstOrDefault(x => string.Equals(x.Path, bankPath, StringComparison.OrdinalIgnoreCase));
        if (loadEditor) LoadEditorForBank(_selectedBank);
        else RefreshActionButtons();
    }

    /// <summary>
    /// 在 bank 树里定位当前样本所属的 bank（展开并滚到可视区）。
    /// 首版这是「在 bank 树中定位」按钮；按钮已按计划删除，能力改挂在
    /// bank 树的<b>双击</b>上——用户已经在树里点上来的情况下，双击就是「定位到这个 bank」，
    /// 不再占一个按钮位、也不必维护它的启用状态。
    /// </summary>
    private void LocateInTree()
    {
        if (_selectedSample is not { } row) return;
        if (_viewMode != BankViewMode.BankTree)
        {
            Shell.SetViewMode(WorkbenchViewMode.Tree); // 触发 ViewModeChanged → SwitchView(BankTree)
            RebuildTree();
        }
        foreach (var item in _bankTree.Items.OfType<TreeViewItem>())
        {
            if (item.Tag is not BankIndexEntry entry) continue;
            if (!string.Equals(entry.Path, row.BankPath, StringComparison.OrdinalIgnoreCase)) continue;
            item.IsExpanded = true;
            item.IsSelected = true;
            item.BringIntoView();
            Shell.SetStatus($"已在 bank 树中定位：{entry.FileName}（{row.FsbLabel} · 样本 {row.SampleIndex}）");
            return;
        }
        Shell.SetStatus("当前筛选条件下 bank 树里没有这个 bank（先清除筛选再定位）。");
    }

    // ── 编辑列（选中上下文 + 选中样本明细）──────────────────────────

    /// <summary>
    /// 载入编辑列的 bank 上下文。<paramref name="clearSample"/> 表示「换 bank」：
    /// 此时选中样本已经不属于这个 bank，必须清掉——既会清空明细块，
    /// 也会经 <see cref="StopAudio"/> 把播放进度条归零（plan-11）。
    /// 「同一 bank 内继续选样本」的路径传 false，免得每次选样本都自断播放。
    /// </summary>
    private void LoadEditorForBank(BankIndexEntry? entry, bool clearSample = false)
    {
        if (entry is null || clearSample) _selectedSample = null;
        StopAudio();
        if (entry is null)
        {
            _contextInfo.Text = "未选择 bank";
            _sampleInfo.Text = "—";
            RefreshSampleDetail();
            RefreshActionButtons();
            return;
        }
        _contextInfo.Text = $"{entry.FileName} · {entry.KindLabel} · {entry.SizeBytes:N0} 字节 · FSB {entry.FsbCount} 个" +
                            (string.IsNullOrWhiteSpace(entry.KindNote) ? string.Empty : $"\n{entry.KindNote}") +
                            (string.IsNullOrWhiteSpace(entry.Note) ? string.Empty : $"\n{entry.Note}");
        _sampleInfo.Text = entry.Samples.Count == 0
            ? (entry.Kind == BankKind.Event ? "事件 bank：无 FSB 负载（SNDH 表为空），没有可试听/替换的样本。" : "该 bank 没有可解析的样本。")
            : $"{entry.Samples.Count} 个样本（索引快照，未重新解析 bank 文件）";
        RefreshSampleDetail();
        RefreshActionButtons();
    }

    private void LoadEditorForSample(SampleRow row)
    {
        var entry = _entries.FirstOrDefault(x => string.Equals(x.Path, row.BankPath, StringComparison.OrdinalIgnoreCase));
        LoadEditorForBank(entry); // 不清样本：row 就是要在编辑列里展开的那个
        RefreshSampleDetail();
        Shell.SetStatus($"{row.LocationLabel} · {row.CodecName} · {row.DurationLabel} · {row.SizeLabel} 字节");
    }

    /// <summary>
    /// 把选中样本的明细写进「选中样本」键值块。找不到选中项时整块显示「未选择样本」
    /// （九个标签保留，值一律清空），免得残留上一个样本的数据误导人。
    /// </summary>
    private void RefreshSampleDetail()
    {
        var row = _selectedSample;
        var modifiedBrush = TryBrush("WbModifiedBrush");
        if (row is null)
        {
            // 标签列次色、值列主色：整块只剩一行「未选择样本」（其余值置空保留标签，避免残留上个样本的数据）。
            SetDetailValue(_sampleDetail, 0, "未选择样本");
            for (var i = 1; i < _sampleDetail.Children.Count; i++) SetDetailValue(_sampleDetail, i, string.Empty);
            return;
        }
        SetDetailValue(_sampleDetail, 0, row.Name);
        SetDetailValue(_sampleDetail, 1, row.BankFileName);
        SetDetailValue(_sampleDetail, 2, row.FsbLabel);
        SetDetailValue(_sampleDetail, 3, row.CodecName);
        SetDetailValue(_sampleDetail, 4, row.SampleRate > 0 ? $"{row.SampleRate:N0} Hz" : "—");
        SetDetailValue(_sampleDetail, 5, row.Channels > 0 ? $"{row.Channels} 声道" : "—");
        SetDetailValue(_sampleDetail, 6, row.DurationLabel);
        SetDetailValue(_sampleDetail, 7, $"{row.SizeLabel} 字节");
        SetDetailValue(_sampleDetail, 8, row.StateLabel, row.IsModified ? modifiedBrush : null);
    }

    private SampleRow? SelectedSample => _selectedSample;

    private void RefreshActionButtons()
    {
        var sample = SelectedSample;
        var hasSample = sample is not null;
        var hasProject = _host.Project is not null;
        var fmodDirectory = _host.Env.EffectiveFmodLibraryDirectory(_host.Project);
        var hasFmod = !string.IsNullOrWhiteSpace(fmodDirectory) && Directory.Exists(fmodDirectory);
        // 播放按钮在播放中必须保持可点（再点一次就是「停止」），否则用户没有停止的入口。
        _auditionButton.Content = _player is null ? "▶ 播放" : "■ 停止";
        _auditionButton.IsEnabled = hasSample && (hasFmod || _player is not null);
        _exportWavButton.IsEnabled = hasSample && hasFmod;
        _replaceButton.IsEnabled = hasSample && hasProject && _host.ProjectFile is not null;
        _exportBankButton.IsEnabled = _selectedBank is not null && hasProject;
        _exportRebankButton.IsEnabled = _selectedBank is not null && hasProject;
        _clearCacheButton.IsEnabled = !_indexing && _index.Store.Exists;
    }

    // ── 播放进度（plan-11：唯一的试听按钮 → 进度条 + 播放/停止 + 时间）────

    /// <summary>
    /// 停止播放并释放临时 WAV。除了原有的摘钩/删文件，现在还负责停掉进度定时器、
    /// 把进度条与时间标签归零（<see cref="ResetPlaybackUi"/>），并刷新按钮上的
    /// 「▶ 播放 / ■ 停止」文案。所有原有调用点（选 bank、选样本、播放失败、导出前置等）
    /// 都因此自动获得「进度归零」的行为。
    /// </summary>
    private void StopAudio()
    {
        var player = _player;
        _player = null;
        if (player is not null)
        {
            try { player.Stop(); player.Close(); }
            catch (Exception) { /* 已释放 */ }
        }
        var file = _playerFile;
        _playerFile = null;
        if (file is not null)
        {
            try { File.Delete(file); } catch (Exception) { /* 系统清理 */ }
        }
        _playTimer.Stop();
        ResetPlaybackUi();
        RefreshActionButtons();
    }

    /// <summary>进度条与时间标签归零（停止 / 切换选择时调用）。</summary>
    private void ResetPlaybackUi()
    {
        _playBar.Value = 0;
        var total = SelectedSample?.DurationSeconds;
        _playBar.Maximum = total is > 0 ? total.Value : 1;
        // 时长未知：进度条与「总时长」都不可信，直接禁用并显示占位，不编一个假的时长。
        _playBar.IsEnabled = total is > 0 && _player is not null;
        _playTime.Text = $"00:00 / {(total is > 0 ? FormatTime(total.Value) : "—")}";
    }

    /// <summary>启动播放进度刷新（播放开始时调用）。时长未知时进度条保持禁用。</summary>
    private void StartPlaybackUi(double? durationSeconds)
    {
        _playBar.Value = 0;
        _playBar.Maximum = durationSeconds is > 0 ? durationSeconds.Value : 1;
        _playBar.IsEnabled = durationSeconds is > 0;
        _playTime.Text = $"00:00 / {(durationSeconds is > 0 ? FormatTime(durationSeconds.Value) : "—")}";
        _playTimer.Start();
    }

    /// <summary>
    /// 定时器每 200ms 拉一次 <c>MediaPlayer.Position</c> 更新进度条与时间标签。
    /// 位置/时长都以播放器为准（时长取 <c>NaturalDuration</c>，解码出的 WAV 的实际长度），
    /// 不用索引里的 <c>SampleCount / SampleRate</c> 估算——那是「原始样本」的时长，
    /// 和试听用的临时 WAV 未必逐毫秒一致。
    /// </summary>
    private void UpdatePlaybackUi()
    {
        if (_player is not { } player) return;
        var total = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : 0;
        var position = player.Position.TotalSeconds;
        if (total <= 0) return;
        if (_playBar.Maximum != total) _playBar.Maximum = total;
        _playBar.Value = Math.Clamp(position, 0, total);
        _playBar.IsEnabled = true;
        _playTime.Text = $"{FormatTime(position)} / {FormatTime(total)}";
    }

    /// <summary>点击进度条跳转播放位置（纯播放器操作，不碰索引与解码逻辑）。</summary>
    private void PlayBar_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => SeekToPointer();

    /// <summary>按住拖动时跟随跳转（未按下左键不响应，避免只是划过就跳）。</summary>
    private void PlayBar_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        SeekToPointer();
    }

    private void SeekToPointer()
    {
        if (_player is not { } player || _playBar.ActualWidth <= 0) return;
        var total = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : 0;
        if (total <= 0) return;
        var target = Math.Clamp(System.Windows.Input.Mouse.GetPosition(_playBar).X / _playBar.ActualWidth, 0, 1) * total;
        player.Position = TimeSpan.FromSeconds(target);
        _playBar.Value = target;
        _playTime.Text = $"{FormatTime(target)} / {FormatTime(total)}";
    }

    private static string FormatTime(double seconds)
    {
        var total = (int)Math.Round(Math.Max(0, seconds));
        return $"{total / 60:00}:{total % 60:00}";
    }

    // ── 试听 / 导出样本 ──────────────────────────────────────────────

    private async Task AuditionAsync()
    {
        if (_selectedBank is null || SelectedSample is not { } sample) return;
        if (_player is not null) { StopAudio(); Shell.SetStatus("已停止试听。"); return; }
        var fmodDirectory = _host.Env.EffectiveFmodLibraryDirectory(_host.Project);
        if (string.IsNullOrWhiteSpace(fmodDirectory) || !Directory.Exists(fmodDirectory))
        {
            Shell.SetStatus("需要 FMOD DLL 才能解码试听：把 fmod64.dll 放进程序目录 fmod\\，或在「设置」页指定目录。");
            return;
        }
        Shell.SetStatus($"正在解码 {sample.Name}…");
        try
        {
            var wav = await Task.Run(() =>
            {
                var fsb = ExtractFsb(_selectedBank.Path, sample.FsbIndex);
                using var codec = new NativeFmodAudioCodec(fmodDirectory);
                return codec.DecodeFsbToWaveAsync(fsb, sample.SampleIndex).GetAwaiter().GetResult();
            });
            var file = Path.Combine(Path.GetTempPath(), $"lme-bank-{Guid.NewGuid():N}.wav");
            await File.WriteAllBytesAsync(file, wav);
            var player = new System.Windows.Media.MediaPlayer();
            player.MediaEnded += (_, _) => StopAudio(); // 播完即停：停定时器、进度归零、按钮还原
            player.MediaFailed += (_, args) => { StopAudio(); Shell.SetStatus($"播放失败：{args.ErrorException?.Message ?? "未知原因"}"); };
            player.Open(new Uri(file));
            _player = player;
            _playerFile = file;
            player.Play();
            StartPlaybackUi(player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : null);
            RefreshActionButtons(); // 按钮切成「■ 停止」
            Shell.SetStatus($"正在播放 {sample.Name}（WAV {wav.Length / 1024} KB）");
        }
        catch (Exception ex) { StopAudio(); Shell.SetStatus($"试听失败：{ex.Message}"); }
    }

    private async Task ExportSampleWavAsync()
    {
        if (_selectedBank is null || SelectedSample is not { } sample) return;
        var fmodDirectory = _host.Env.EffectiveFmodLibraryDirectory(_host.Project);
        if (string.IsNullOrWhiteSpace(fmodDirectory) || !Directory.Exists(fmodDirectory)) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "WAV 音频 (*.wav)|*.wav|所有文件 (*.*)|*.*",
            FileName = Sanitize(sample.Name) + ".wav",
            Title = "导出样本为 WAV",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var wav = await Task.Run(() =>
            {
                var fsb = ExtractFsb(_selectedBank.Path, sample.FsbIndex);
                using var codec = new NativeFmodAudioCodec(fmodDirectory);
                return codec.DecodeFsbToWaveAsync(fsb, sample.SampleIndex).GetAwaiter().GetResult();
            });
            await File.WriteAllBytesAsync(dialog.FileName, wav);
            Shell.SetStatus($"已导出：{dialog.FileName}");
        }
        catch (Exception ex) { Shell.SetStatus($"导出 WAV 失败：{ex.Message}"); }
    }

    /// <summary>从 bank 文件里切出第 <paramref name="fsbIndex"/> 个 FSB 负载。</summary>
    private static byte[] ExtractFsb(string bankPath, int fsbIndex)
    {
        var data = File.ReadAllBytes(bankPath);
        var info = BankParser.TryParse(data) ?? throw new InvalidDataException("无法解析 bank 文件。");
        if (fsbIndex < 0 || fsbIndex >= info.FsbCount) throw new ArgumentOutOfRangeException(nameof(fsbIndex));
        return data.AsSpan((int)info.FsbOffsets[fsbIndex], (int)info.FsbSizes[fsbIndex]).ToArray();
    }

    // ── 替换样本（实体化进项目）─────────────────────────────────────

    private async Task ReplaceSampleAsync()
    {
        var project = _host.Project;
        if (project is null || _host.ProjectFile is null || _selectedBank is null || SelectedSample is not { } sample)
        {
            Shell.SetStatus("请先打开项目并选中一个样本。");
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "WAV 音频 (*.wav)|*.wav|所有文件 (*.*)|*.*",
            Title = $"选择替换样本 {sample.Name} 的 WAV 文件",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var projectDirectory = Path.GetDirectoryName(_host.ProjectFile)!;
            var bankCopy = await Task.Run(() => MaterializeBank(project, _selectedBank, projectDirectory));
            var replacementDirectory = Path.Combine(projectDirectory, "banks", Path.GetFileNameWithoutExtension(_selectedBank.FileName));
            Directory.CreateDirectory(replacementDirectory);
            var replacement = Path.Combine(replacementDirectory, $"{sample.FsbIndex}.wav");
            File.Copy(dialog.FileName, replacement, overwrite: true);
            var asset = EnsureSampleAsset(project, _selectedBank, sample, bankCopy);
            asset.Metadata["replacementPath"] = replacement;
            asset.EditState = AssetEditState.Modified;
            await _host.SaveProjectAsync();
            _host.RefreshProjectState($"样本替换已登记（{_selectedBank.FileName} · {sample.Name}）");
            _modifiedBanks[_selectedBank.FileName] = true;
            LoadEditorForBank(_selectedBank);
            ApplyFilter();
            Shell.SetStatus("替换已登记到项目。导出时会用 FSBANK 把 WAV 重新编码进 bank；" +
                            "缺少 fsbank64.dll 时导出会明确报错（不会静默跳过）。");
        }
        catch (Exception ex) { Shell.SetStatus($"替换失败：{ex.Message}"); }
    }

    /// <summary>把 bank 复制进项目（引用模式 + 实体化，与 Unity 缓存同策略），
    /// 并登记为项目 Source（Format=Bank），使既有导出管道可直接处理。</summary>
    private static string MaterializeBank(ModProject project, BankIndexEntry bank, string projectDirectory)
    {
        var targetDirectory = Path.Combine(projectDirectory, "sources", "banks");
        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, bank.FileName);
        if (!File.Exists(target) || new FileInfo(target).Length != bank.SizeBytes)
            File.Copy(bank.Path, target, overwrite: true);
        if (!project.Sources.Any(x => string.Equals(x.Path, target, StringComparison.OrdinalIgnoreCase)))
        {
            project.Sources.Add(new ProjectSource
            {
                DisplayName = bank.FileName,
                Path = target,
                Format = ModFormatKind.Bank,
                ImportedAt = DateTimeOffset.UtcNow,
            });
        }
        return target;
    }

    /// <summary>确保该样本在项目里有一条 fsb/&lt;index&gt; 音频资源（与 Bank 导入
    /// 管道同构），返回它。</summary>
    private static AssetRecord EnsureSampleAsset(ModProject project, BankIndexEntry bank, SampleRow sample, string bankCopy)
    {
        var logicalPath = $"fsb/{sample.FsbIndex}";
        var existing = project.Assets.FirstOrDefault(x =>
            string.Equals(x.LogicalPath, logicalPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.SourcePath, bankCopy, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var asset = new AssetRecord
        {
            LogicalPath = logicalPath,
            ContainerPath = logicalPath,
            SourcePath = bankCopy,
            Type = AssetType.Audio,
            Size = sample.DataSize,
            Bundle = bank.FileName,
            Metadata =
            {
                ["bankSource"] = bank.FileName,
                ["sampleName"] = sample.Name,
            },
        };
        project.Assets.Add(asset);
        return asset;
    }

    // ── 导出（只写模组目录）─────────────────────────────────────────

    private async Task ExportAsync(ModFormatKind format)
    {
        var project = _host.Project;
        if (project is null || _host.ProjectFile is null || _selectedBank is null)
        {
            Shell.SetStatus("请先打开项目并选择一个 bank（导出走项目源管道）。");
            return;
        }
        var modDirectory = _host.Env.EffectiveModDirectory(project);
        var defaultDirectory = !string.IsNullOrWhiteSpace(modDirectory) && Directory.Exists(modDirectory)
            ? modDirectory
            : Path.Combine(Path.GetDirectoryName(_host.ProjectFile)!, "builds");
        var extension = ModExportService.ExtensionFor(format);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = $"{extension} 模组 (*{extension})|*{extension}|所有文件 (*.*)|*.*",
            FileName = Sanitize(Path.GetFileNameWithoutExtension(_selectedBank.FileName)) + extension,
            InitialDirectory = defaultDirectory,
            Title = "选择导出位置（默认模组目录，加载器可直接读取；编辑器绝不写游戏目录）",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var projectDirectory = Path.GetDirectoryName(_host.ProjectFile)!;
        var bankCopy = MaterializeBank(project, _selectedBank, projectDirectory);
        await _host.SaveProjectAsync();
        var fmodDirectory = _host.Env.EffectiveFmodLibraryDirectory(_host.Project);
        try
        {
            using NativeFmodAudioCodec? codec = !string.IsNullOrWhiteSpace(fmodDirectory) && Directory.Exists(fmodDirectory)
                ? new NativeFmodAudioCodec(fmodDirectory)
                : null;
            Shell.SetStatus("正在导出…");
            var result = await _exporter.ExportWithEditsAsync(bankCopy, project, dialog.FileName, format, codec);
            Shell.SetStatus($"导出完成：{result.AppliedReplacements} 个替换 → {result.OutputPath}");
            if (result.AssetStatuses.Count > 0)
            {
                var skipped = result.AssetStatuses.Count(x => x.Status != ExportAssetStatus.Applied);
                if (skipped > 0) Shell.SetStatus($"（{skipped} 条未应用，详见导出报告）");
            }
            new ExportReportWindow(result) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex) { Shell.SetStatus($"导出失败：{ex.Message}"); }
    }

    // ── 索引缓存维护 ─────────────────────────────────────────────────

    private void ClearCache()
    {
        try
        {
            _index.Store.DeleteDatabase();
            _entries.Clear();
            _sampleRecords.Clear();
            _allSamples.Clear();
            _viewSamples.Clear();
            _bankTree.ItemsSource = null;
            _selectedBank = null;
            _selectedSample = null;
            LoadEditorForBank(null);
            RefreshSampleDetail();
            RefreshActionButtons();
            Shell.SetEmptyHint("索引缓存已清空：点「重新扫描」重建（不影响游戏目录里的任何文件）。");
            Shell.SetStatus("已删除 cache/bank-index.db。再次进入本页或点「重新扫描」会重新建索引。");
        }
        catch (Exception ex) { Shell.SetStatus($"清空索引缓存失败：{ex.Message}"); }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "sample" : cleaned;
    }

    private static Brush? TryBrush(string key) => System.Windows.Application.Current?.TryFindResource(key) as Brush;
}
