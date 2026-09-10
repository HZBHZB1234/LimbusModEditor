using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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
/// <para><b>plan-11 的三视图</b>：</para>
/// <list type="number">
/// <item><b>☰ 全部音频</b>：跨全部 bank 的样本总表（列 = 样本名 / bank / FSB / codec / 采样率 /
/// 声道 / 时长 / 大小 / 状态）。数据源是 <c>cache/bank-index.db</c> 的 <c>samples</c> 表
/// （本机实测 52826 行），用 <see cref="DataGrid"/> 行虚拟化承载；筛选与排序都在内存行集上做，
/// 不重新读盘。</item>
/// <item><b>🗂 bank 树</b>：bank → FSB → 样本 三层，惰性展开。</item>
/// <item><b>☰ bank 列表</b>：首个版本的逐 bank 表（类型 / FSB 数 / 大小）。</item>
/// </list>
///
/// <para><b>索引是加速旁路</b>：删掉 <c>cache/bank-index.db</c> 后三个视图的数据与筛选结果
/// 完全一致，只是每次进页面要重新解析 1531 个 bank（冷建 ~19s，热读 ~0.9s）。</para>
/// </summary>
public partial class BankWorkbenchPage : UserControl
{
    /// <summary>浏览列视图形态（与列表/树两个按钮无关，本页有三种）。</summary>
    private enum BankViewMode
    {
        AllAudio,
        BankTree,
        BankList,
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
    private readonly ComboBox _viewSwitch;
    private readonly ComboBox _kindFilter;
    private readonly ComboBox _codecFilter;
    private readonly ComboBox _durationFilter;
    private readonly CheckBox _modifiedOnly;
    private readonly ComboBox _sortFilter;
    private readonly DataGrid _audioGrid;
    private readonly ListView _bankList;
    private readonly TreeView _bankTree;
    private readonly ListView _sampleList;
    private readonly TextBlock _contextInfo;
    private readonly TextBlock _sampleInfo;
    private readonly Button _auditionButton;
    private readonly Button _exportWavButton;
    private readonly Button _replaceButton;
    private readonly Button _exportBankButton;
    private readonly Button _exportRebankButton;
    private readonly Button _locateButton;
    private readonly Button _cancelIndexButton;
    private readonly Button _reloadButton;
    private readonly Button _clearCacheButton;

    private System.Windows.Media.MediaPlayer? _player;
    private string? _playerFile;
    private string? _bankDirectory;
    private BankViewMode _viewMode = BankViewMode.AllAudio;
    private BankIndexEntry? _selectedBank;
    private BankFileEntry? _selectedFileEntry;
    private SampleRow? _selectedSample;
    private List<SampleRow> _editSampleRows = [];
    private int _editGeneration;
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

        // ── 筛选行（含视图切换）──────────────────────────────────────
        _viewSwitch = new ComboBox { Width = 130, Height = 28, Margin = new Thickness(0, 0, 6, 0) };
        _viewSwitch.Items.Add("☰ 全部音频");
        _viewSwitch.Items.Add("🗂 bank 树");
        _viewSwitch.Items.Add("☰ bank 列表");
        _viewSwitch.SelectedIndex = 0;
        _viewSwitch.SelectionChanged += (_, _) => SwitchView((BankViewMode)Math.Max(0, _viewSwitch.SelectedIndex));
        _viewSwitch.ToolTip = "全部音频 = 跨全部 bank 的样本总表；bank 树 = bank → FSB → 样本；bank 列表 = 逐个 bank";
        Shell.AddFilterItem(_viewSwitch);

        _kindFilter = MakeFilterCombo(120, "按 bank 类型筛选（事件 bank 没有样本负载）", "全部类型", "仅音频 bank", "仅事件 bank", "加密/无法识别");
        _kindFilter.SelectionChanged += (_, _) => ApplyFilter();
        Shell.AddFilterItem(_kindFilter);

        _codecFilter = MakeFilterCombo(120, "按 codec 筛选（音频 bank 的 FSB 头部字段）", "全部 codec");
        _codecFilter.SelectionChanged += (_, _) => ApplyFilter();
        Shell.AddFilterItem(_codecFilter);

        _durationFilter = MakeFilterCombo(130, "按时长筛选", "全部时长", "≤ 1 秒", "1~5 秒", "5~30 秒", "> 30 秒", "无法计算时长");
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

        _sortFilter = MakeFilterCombo(150,
            "排序方式（在内存行集上排序，不重新读盘）",
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
                }
            }
            finally { _isSelecting = false; }
        };

        // ── 视图二：bank 树 ──────────────────────────────────────────
        _bankTree = WorkbenchShell.CreateTree();
        _bankTree.ToolTip = "bank → FSB → 样本；展开时才生成下一层";
        _bankTree.SelectedItemChanged += (_, e) => OnTreeSelectionChanged(e.NewValue as TreeViewItem);
        _bankTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(TreeItem_Expanded));

        // ── 视图三：bank 列表 ────────────────────────────────────────
        _bankList = WorkbenchShell.CreateList();
        _bankList.ItemContainerStyle = WorkbenchShell.CreateListItemStyle();
        var bankGrid = new GridView();
        bankGrid.Columns.Add(Column("bank 文件", nameof(BankFileEntry.FileName), 280));
        bankGrid.Columns.Add(Column("类型", nameof(BankFileEntry.KindLabel), 90));
        bankGrid.Columns.Add(Column("FSB 数", nameof(BankFileEntry.FsbCount), 70));
        bankGrid.Columns.Add(Column("大小", nameof(BankFileEntry.FileSizeBytes), 100));
        _bankList.View = bankGrid;
        _bankList.SelectionChanged += (_, _) =>
        {
            if (_isSelecting) return;
            if (_bankList.SelectedItem is not BankFileEntry entry) return;
            _isSelecting = true;
            try
            {
                _selectedFileEntry = entry;
                _selectedSample = null;
                SelectBank(entry.FullPath, loadEditor: true);
            }
            finally { _isSelecting = false; }
        };

        Shell.SetBrowseContent(_audioGrid);
        Shell.SetEmptyHint("正在准备音频索引…");
        Shell.ViewModeChanged += (_, _) => { /* 本页视图由 _viewSwitch 控制，忽略列表/树切换事件 */ };

        // ── 编辑列 ───────────────────────────────────────────────────
        _contextInfo = new TextBlock { Text = "未选择 bank", TextWrapping = TextWrapping.Wrap, Style = FindStyle("WorkbenchSectionLabel") };
        _sampleList = WorkbenchShell.CreateList();
        _sampleList.ItemContainerStyle = WorkbenchShell.CreateListItemStyle();
        _sampleList.Height = 240;
        var sampleGrid = new GridView();
        sampleGrid.Columns.Add(Column("样本", nameof(SampleRow.Name), 170));
        sampleGrid.Columns.Add(Column("FSB", nameof(SampleRow.FsbLabel), 56));
        sampleGrid.Columns.Add(Column("codec", nameof(SampleRow.CodecName), 70));
        sampleGrid.Columns.Add(Column("采样率", nameof(SampleRow.SampleRate), 70));
        sampleGrid.Columns.Add(Column("声道", nameof(SampleRow.Channels), 50));
        sampleGrid.Columns.Add(Column("时长", nameof(SampleRow.DurationLabel), 70));
        sampleGrid.Columns.Add(Column("大小", nameof(SampleRow.SizeLabel), 80));
        sampleGrid.Columns.Add(Column("状态", nameof(SampleRow.StateLabel), 60));
        _sampleList.View = sampleGrid;
        _sampleList.SelectionChanged += (_, _) =>
        {
            if (_isSelecting) return;
            _selectedSample = _sampleList.SelectedItem as SampleRow;
            RefreshActionButtons();
            if (_selectedSample is { } row) Shell.SetStatus($"{row.LocationLabel} · {row.CodecName} · {row.DurationLabel} · {row.SizeLabel} 字节");
        };

        _auditionButton = WorkbenchShell.CreateButton("▶ 试听样本", async (_, _) => await AuditionAsync(), isEnabled: false);
        _exportWavButton = WorkbenchShell.CreateButton("导出样本 WAV…", async (_, _) => await ExportSampleWavAsync(), isEnabled: false);
        _replaceButton = WorkbenchShell.CreateButton("用 WAV 替换…", async (_, _) => await ReplaceSampleAsync(), isEnabled: false);
        _exportBankButton = WorkbenchShell.CreateButton("导出整包 .bank…", async (_, _) => await ExportAsync(ModFormatKind.Bank), isEnabled: false);
        _exportRebankButton = WorkbenchShell.CreateButton("导出 .rebank…", async (_, _) => await ExportAsync(ModFormatKind.Rebank), isEnabled: false);
        _locateButton = WorkbenchShell.CreateButton("在 bank 树中定位", (_, _) => LocateInTree(), isEnabled: false);
        var buttonRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in new[] { _auditionButton, _exportWavButton, _replaceButton, _exportBankButton, _exportRebankButton, _locateButton })
            buttonRow.Children.Add(button);

        _sampleInfo = new TextBlock { Text = "—", Style = FindStyle("WorkbenchStatusText") };
        var editPanel = new StackPanel();
        editPanel.Children.Add(WorkbenchShell.CreatePanelTitle("bank / 样本"));
        editPanel.Children.Add(_contextInfo);
        editPanel.Children.Add(_sampleList);
        editPanel.Children.Add(buttonRow);
        editPanel.Children.Add(_sampleInfo);
        Shell.SetEditContent(new ScrollViewer { Content = editPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        Loaded += async (_, _) => await RefreshAsync();
    }

    // ── 构件助手（色值一律来自共享样式）────────────────────────────

    private static Style? FindStyle(string key) => System.Windows.Application.Current?.TryFindResource(key) as Style;

    private static ComboBox MakeFilterCombo(double width, string toolTip, params string[] items)
    {
        var combo = new ComboBox { Width = width, Height = 28, Margin = new Thickness(0, 0, 6, 0), ToolTip = toolTip };
        foreach (var item in items) combo.Items.Add(item);
        combo.SelectedIndex = 0;
        return combo;
    }

    private static GridViewColumn Column(string header, string path, double width)
        => new() { Header = header, DisplayMemberBinding = new Binding(path), Width = width };

    private static void AddColumn(DataGrid grid, string header, string path, double width)
        => grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path),
            Width = new DataGridLength(width),
        });

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
        _bankList.ItemsSource = null;
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
        _sortFilter.SelectedIndex = 0;
        ApplyFilter();
    }

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

        if (_viewMode == BankViewMode.BankList)
        {
            var filteredEntries = _entries.Where(entry => MatchesKindFilter(entry)).ToList();
            if (query.Length > 0)
                filteredEntries = filteredEntries.Where(x => x.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            _bankList.ItemsSource = filteredEntries.Select(x => x.ToFileEntry()).ToList();
        }
        else if (_viewMode == BankViewMode.BankTree)
        {
            RebuildTree();
        }

        Shell.SetEmptyHint(_viewSamples.Count == 0 && _viewMode == BankViewMode.AllAudio
            ? (_allSamples.Count == 0 ? "索引里还没有样本。" : "没有样本匹配当前筛选条件。")
            : null);
    }

    private bool MatchesKindFilter(BankIndexEntry entry) => _kindFilter.SelectedIndex switch
    {
        1 => entry.Kind == BankKind.Audio,
        2 => entry.Kind == BankKind.Event,
        3 => entry.Kind is BankKind.Encrypted or BankKind.Unknown,
        _ => true,
    };

    private void SwitchView(BankViewMode mode)
    {
        _viewMode = mode;
        switch (mode)
        {
            case BankViewMode.AllAudio:
                Shell.SetBrowseContent(_audioGrid);
                break;
            case BankViewMode.BankTree:
                Shell.SetBrowseContent(_bankTree);
                RebuildTree();
                break;
            default:
                Shell.SetBrowseContent(_bankList);
                _bankList.ItemsSource = _entries.Select(x => x.ToFileEntry()).ToList();
                break;
        }
        ApplyFilter();
    }

    // ── bank 树（bank → FSB → 样本，惰性展开）───────────────────────

    private void RebuildTree()
    {
        if (_viewMode != BankViewMode.BankTree) return;
        var roots = new List<TreeViewItem>();
        foreach (var entry in _entries.Where(MatchesKindFilter))
        {
            var node = new TreeViewItem
            {
                Header = $"{entry.FileName}（{entry.KindLabel} · {entry.FsbCount} FSB · {entry.Samples.Count} 样本）",
                Tag = entry,
                Style = WorkbenchShell.CreateTreeItemStyle(),
            };
            node.Items.Add(new TreeViewItem { Header = PlaceholderText }); // 惰性占位
            roots.Add(node);
        }
        _bankTree.ItemsSource = roots;
    }

    private const string PlaceholderText = "载入中…";

    private void TreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item) return;
        if (item.Tag is BankIndexEntry entry)
        {
            item.Items.Clear();
            var byFsb = entry.Samples.GroupBy(x => x.FsbIndex).OrderBy(x => x.Key);
            foreach (var group in byFsb)
            {
                var fsbNode = new TreeViewItem
                {
                    Header = $"FSB {group.Key}（{group.Count()} 个样本）",
                    Tag = group.Key,
                    Style = WorkbenchShell.CreateTreeItemStyle(),
                };
                fsbNode.Items.Add(new TreeViewItem { Header = PlaceholderText });
                item.Items.Add(fsbNode);
            }
            if (entry.Samples.Count == 0)
                item.Items.Add(new TreeViewItem { Header = entry.KindNote ?? "无样本负载（事件 bank）", Style = WorkbenchShell.CreateTreeItemStyle() });
        }
        else if (item.Tag is int fsbIndex && item.Parent is TreeViewItem parent && parent.Tag is BankIndexEntry bank)
        {
            item.Items.Clear();
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
                    Foreground = row.IsModified ? TryBrush("WbModifiedBrush") : null,
                    Style = WorkbenchShell.CreateTreeItemStyle(),
                });
            }
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
                    _sampleList.SelectedItem = _editSampleRows.FirstOrDefault(x =>
                        x.FsbIndex == row.FsbIndex && x.SampleIndex == row.SampleIndex);
                }
                finally { _isSelecting = false; }
                RefreshActionButtons();
                break;
            case int fsbIndex when item.Parent is TreeViewItem { Tag: BankIndexEntry bank }:
                _selectedSample = null;
                SelectBank(bank.Path, loadEditor: true);
                Shell.SetStatus($"FSB {fsbIndex} · {bank.FileName}");
                break;
        }
    }

    /// <summary>在三个视图里把一个 bank 标为「当前选中」（不重入触发选择事件）。</summary>
    private void SelectBank(string bankPath, bool loadEditor)
    {
        _selectedBank = _entries.FirstOrDefault(x => string.Equals(x.Path, bankPath, StringComparison.OrdinalIgnoreCase));
        _selectedFileEntry = _selectedBank?.ToFileEntry();
        if (loadEditor) LoadEditorForBank(_selectedBank);
        RefreshActionButtons();
    }

    /// <summary>在 bank 树里定位当前样本所属的 bank（切到树视图并展开到该 bank）。</summary>
    private void LocateInTree()
    {
        if (_selectedSample is not { } row) return;
        _viewSwitch.SelectedIndex = 1; // 触发 SwitchView(BankTree)
        RebuildTree();
        foreach (var item in _bankTree.Items.OfType<TreeViewItem>())
        {
            if (item.Tag is not BankIndexEntry entry) continue;
            if (!string.Equals(entry.Path, row.BankPath, StringComparison.OrdinalIgnoreCase)) continue;
            item.IsExpanded = true;
            item.BringIntoView();
            Shell.SetStatus($"已在 bank 树中定位：{entry.FileName}（{row.FsbLabel} · 样本 {row.SampleIndex}）");
            return;
        }
        Shell.SetStatus("当前筛选条件下 bank 树里没有这个 bank（先清除筛选再定位）。");
    }

    // ── 编辑列（样本表 + 选中上下文）────────────────────────────────

    private void LoadEditorForBank(BankIndexEntry? entry)
    {
        var generation = ++_editGeneration;
        StopAudio();
        _editSampleRows = [];
        _sampleList.ItemsSource = null;
        if (entry is null)
        {
            _contextInfo.Text = "未选择 bank";
            _sampleInfo.Text = "—";
            RefreshActionButtons();
            return;
        }
        _contextInfo.Text = $"{entry.FileName} · {entry.KindLabel} · {entry.SizeBytes:N0} 字节 · FSB {entry.FsbCount} 个" +
                            (string.IsNullOrWhiteSpace(entry.KindNote) ? string.Empty : $"\n{entry.KindNote}") +
                            (string.IsNullOrWhiteSpace(entry.Note) ? string.Empty : $"\n{entry.Note}");
        var modified = _modifiedBanks.ContainsKey(entry.FileName);
        _editSampleRows = _allSamples
            .Where(x => string.Equals(x.BankPath, entry.Path, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.FsbIndex).ThenBy(x => x.SampleIndex)
            .Select(x => x with { IsModified = modified })
            .ToList();
        _sampleList.ItemsSource = _editSampleRows;
        _sampleInfo.Text = entry.Samples.Count == 0
            ? (entry.Kind == BankKind.Event ? "事件 bank：无 FSB 负载（SNDH 表为空），没有可试听/替换的样本。" : "该 bank 没有可解析的样本。")
            : $"{_editSampleRows.Count} 个样本（索引快照，未重新解析 bank 文件）";
        if (generation != _editGeneration) return;
        RefreshActionButtons();
    }

    private void LoadEditorForSample(SampleRow row)
    {
        var entry = _entries.FirstOrDefault(x => string.Equals(x.Path, row.BankPath, StringComparison.OrdinalIgnoreCase));
        LoadEditorForBank(entry);
        _sampleList.SelectedItem = _editSampleRows.FirstOrDefault(x =>
            x.FsbIndex == row.FsbIndex && x.SampleIndex == row.SampleIndex);
        Shell.SetStatus($"{row.LocationLabel} · {row.CodecName} · {row.DurationLabel} · {row.SizeLabel} 字节");
    }

    private SampleRow? SelectedSample => _selectedSample;

    private void RefreshActionButtons()
    {
        var sample = SelectedSample;
        var hasSample = sample is not null;
        var hasProject = _host.Project is not null;
        var fmodDirectory = _host.Env.EffectiveFmodLibraryDirectory(_host.Project);
        var hasFmod = !string.IsNullOrWhiteSpace(fmodDirectory) && Directory.Exists(fmodDirectory);
        _auditionButton.IsEnabled = hasSample && hasFmod;
        _exportWavButton.IsEnabled = hasSample && hasFmod;
        _replaceButton.IsEnabled = hasSample && hasProject && _host.ProjectFile is not null;
        _exportBankButton.IsEnabled = _selectedBank is not null && hasProject;
        _exportRebankButton.IsEnabled = _selectedBank is not null && hasProject;
        _locateButton.IsEnabled = hasSample;
        _clearCacheButton.IsEnabled = !_indexing && _index.Store.Exists;
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
            player.MediaEnded += (_, _) => StopAudio();
            player.MediaFailed += (_, args) => { StopAudio(); Shell.SetStatus($"播放失败：{args.ErrorException?.Message ?? "未知原因"}"); };
            player.Open(new Uri(file));
            _player = player;
            _playerFile = file;
            player.Play();
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
            _bankList.ItemsSource = null;
            _bankTree.ItemsSource = null;
            _sampleList.ItemsSource = null;
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
    }
}
