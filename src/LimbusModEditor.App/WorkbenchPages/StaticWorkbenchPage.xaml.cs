using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Diagnostics;
using NLog;

namespace LimbusModEditor.App;

/// <summary>
/// 静态数据工作台（plan-08 首版 → plan-12 重做）：定位游戏的特殊静态 bundle
/// （<c>static_s1_0_assets_all_&lt;32hex&gt;.bundle</c>，经 catalog 动态解析），
/// 浏览/搜索其中的 TextAsset 静态表，编辑后用 <see cref="StaticModService"/> 导出
/// <c>.staticmod</c> 补丁包到<b>模组目录</b>。编辑器绝不写 catalog / 缓存 / 游戏目录
/// ——重打包与 catalog 双写由加载器完成。
///
/// <para><b>plan-12 的变化</b>：</para>
/// <list type="bullet">
/// <item><b>浏览列与资源工作台同款</b>：🗂 dataClass 树（默认）/ ☰ 表列表 + 搜索 + 筛选 + 排序
/// + 已修改标记（金色）。</item>
/// <item><b>表索引缓存</b>：进页面读 <c>cache/static-tables.db</c>（本机 1392 张表的元数据），
/// 不再每次重新枚举 bundle 的全部正文（那是旧版「进页面慢、搜索卡」的根因）。</item>
/// <item><b>编辑器换成共享 <c>JsonTreeEditor</c></b>（惰性展开的树 / 原文双 Tab），
/// 不再有 5000 行截断；另有「与官方版本差异」页显示 RFC6902 摘要。</item>
/// </list>
/// </summary>
public partial class StaticWorkbenchPage : UserControl, ISearchableWorkbench, IReferenceRevealable
{
    /// <summary>宿主跳转过来的关键词过滤（<see cref="ISearchableWorkbench"/>）。</summary>
    public void ApplySearchKeyword(string keyword)
    {
        var text = keyword ?? string.Empty;
        _search.Text = text;
        _search.CaretIndex = text.Length;
    }

    /// <summary>
    /// 精确跳转（<see cref="IReferenceRevealable"/>）：载荷第 0 段是静态表的<b>容器路径</b>
    /// ——它正是 <see cref="StaticTableEntry.Key"/> 的口径（空容器时退回 <c>name|pathId</c>）。
    ///
    /// <para><b>为什么优先走列表视图</b>：dataClass 树的子层是惰性物化的，直接去树里找叶子
    /// 常常什么都找不到（这正是 <see cref="RestoreTreeSelection"/> 只在「重建后仍展开」时可靠的原因）。
    /// 所以先试列表（扁平全量，一定有那一行），列表挂不上再物化树、最后才退化为直接载入。</para>
    /// </summary>
    public bool Reveal(string payload)
    {
        var container = RelationDeepLink.Part(payload, 0);
        if (string.IsNullOrWhiteSpace(container))
        {
            ApplySearchKeyword(RelationDeepLink.KeywordOf(payload));
            return false;
        }

        var entry = _tables.FirstOrDefault(x => string.Equals(x.Key, container, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            Log.Warn("静态页精确跳转：索引里没有容器路径为「{0}」的表，退化为关键词过滤", container);
            ApplySearchKeyword(RelationDeepLink.KeywordOf(payload));
            return false;
        }

        var selected = false;
        if (_viewMode != StaticViewMode.List
            && _tableList.ItemsSource is IEnumerable<TableRow> rows
            && rows.FirstOrDefault(r => string.Equals(r.Entry.Key, entry.Key, StringComparison.OrdinalIgnoreCase)) is { } row)
        {
            // 列表选中会经 SelectionChanged → SelectTableAsync，与用户手点完全同一条路径。
            _tableList.SelectedItem = row;
            _tableList.ScrollIntoView(row);
            selected = true;
        }
        if (!selected && _viewMode == StaticViewMode.Tree) selected = TrySelectTableInTree(entry);
        if (!selected)
        {
            _ = SelectTableAsync(entry);
            selected = true;
        }

        Shell.SetStatus($"已定位到静态表：{entry.DataClass}/{entry.FileName}（{entry.SizeLabel}）");
        Log.Info("静态页精确跳转命中：{0}", entry.Key);
        return selected;
    }

    /// <summary>树视图里物化 dataClass 子层后选中目标表；找不到返回 false（不是异常）。</summary>
    private bool TrySelectTableInTree(StaticTableEntry entry)
    {
        foreach (var root in _tableTree.Items)
        {
            if (root is not TreeViewItem node) continue;
            if (!string.Equals(DataClassKeyOf(node.Tag), entry.DataClass, StringComparison.Ordinal)) continue;
            MaterializeDataClass(node, null);
            node.IsExpanded = true;
            foreach (var child in node.Items.OfType<TreeViewItem>())
            {
                if (child.Tag is not StaticTableEntry leaf) continue;
                if (!string.Equals(leaf.Key, entry.Key, StringComparison.OrdinalIgnoreCase)) continue;
                child.IsSelected = true;
                child.BringIntoView();
                return true;
            }
        }
        Log.Debug("静态页精确跳转：树里没找到目标表（dataClass={0}），改用列表 / 直接载入", entry.DataClass);
        return false;
    }

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>浏览列视图（树 / 列表）。</summary>
    private enum StaticViewMode
    {
        Tree,
        List,
    }

    /// <summary>表列表视图的行模型（<c>State=Modified</c> 触发共享样式的金色高亮）。</summary>
    private sealed record TableRow(StaticTableEntry Entry)
    {
        public string DataClass => Entry.DataClass;
        public string FileName => Entry.FileName;
        public string SizeLabel => Entry.SizeLabel;
        public string Utf8Label => Entry.IsUtf8 ? "UTF-8" : "非 UTF-8";
        public string Location => $"{Entry.DataClass}/{Entry.FileName}";
    }

    private readonly IWorkbenchHost _host;
    private readonly StaticModService _staticMods = new();
    private readonly StaticIndexService _index;
    private readonly DispatcherTimer _searchTimer;
    private readonly TextDiffService _diff = new();
    private readonly List<StaticTableEntry> _tables = [];
    /// <summary>静态表编辑集：**宿主共享的会话**（plan-16 S3），导出 / 调试从它读全部修改。</summary>
    private readonly StaticEditSession _edits;
    /// <summary>官方基线正文的页面缓存（打开过的表才有；导出用的基线存在会话里）。</summary>
    private readonly Dictionary<string, string> _vanilla = new(StringComparer.OrdinalIgnoreCase);

    private readonly TextBox _search;
    private readonly ComboBox _viewSwitch;
    private readonly ComboBox _sortFilter;
    private readonly ComboBox _stateFilter;
    private readonly ListView _tableList;
    private readonly TreeView _tableTree;
    private readonly TextBlock _locationInfo;
    private readonly TextBlock _tableInfo;
    private readonly TextBlock _diffInfo;
    private readonly JsonTreeEditor _editor;
    private readonly Button _saveEdit;
    private readonly Button _revertTable;
    private readonly Button _exportStaticMod;
    private readonly Button _clearDocumentCache;
    private readonly Button _reloadButton;

    private StaticBundleLocation? _location;
    private StaticIndexSource? _source;
    private StaticTableEntry? _selected;
    private StaticViewMode _viewMode = StaticViewMode.Tree;
    private int _loadGeneration;
    /// <summary>页面已载入过（<c>Loaded</c> 守卫）：本页常驻，切页回来不该重跑定位/索引。</summary>
    private bool _loaded;
    /// <summary>dataClass 树已展开节点的稳定 key（跨重建累积）。</summary>
    private readonly HashSet<string> _expandedTreeKeys = new(StringComparer.OrdinalIgnoreCase);

    public StaticWorkbenchPage(IWorkbenchHost host)
    {
        _host = host;
        InitializeComponent();
        Shell.Attach(host, WorkbenchPageKeys.Static);
        _index = new StaticIndexService(new StaticTableIndexStore(host.Env.CacheDirectory));
        // 宿主共享的编辑集会话（页面常驻，只创建一次）：导出 / 调试看的就是它。
        _edits = host.StaticEdits;
        // 编辑集变更 → 刷新按钮与视图。注意 ApplyFilter 会重建树（展开态由
        // TreeExpansionState 回放），因此**调用方不要再手动 ApplyFilter 一次**：
        // 早先 SaveCurrentEdit / RevertTableAsync 各自又调了一次，同一次保存要重建两遍。
        _edits.Changed += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            Log.Debug("静态页编辑集变更：触发者=宿主会话 Changed，刷新按钮并重建筛选视图");
            RefreshActionButtons();
            ApplyFilter();
        });

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); };

        // ── 搜索行 ────────────────────────────────────────────────────
        _search = WorkbenchShell.CreateSearchBox("搜索数据类 / 表名（索引里即时命中，不读 bundle）");
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _reloadButton = WorkbenchShell.CreateButton("重新定位/重建索引", async (_, _) => await RefreshAsync(forceRebuild: true));
        _clearDocumentCache = WorkbenchShell.CreateButton("清空正文缓存", (_, _) => ClearDocumentCache());
        Shell.AddSearchItem(_search);
        Shell.AddSearchItem(_reloadButton);
        Shell.AddSearchItem(_clearDocumentCache);

        // ── 筛选行（含视图切换）──────────────────────────────────────
        // 视图切换也是同一个下拉构件（原先 Height=28 同样会把中文裁掉），只是提示语不同。
        _viewSwitch = WorkbenchShell.CreateFilterCombo(
            "数据类树 = 按 dataClass 分组（与资源工作台的容器目录同语义）；表列表 = 扁平表",
            "🗂 数据类树", "☰ 表列表");
        _viewSwitch.SelectionChanged += (_, _) => SwitchView((StaticViewMode)Math.Max(0, _viewSwitch.SelectedIndex));
        Shell.AddFilterItem(_viewSwitch);

        _stateFilter = WorkbenchShell.CreateFilterCombo("按状态筛选", "全部", "仅已修改", "仅已缓存正文", "仅非 UTF-8");
        _stateFilter.SelectionChanged += (_, _) => ApplyFilter();
        Shell.AddFilterItem(_stateFilter);

        _sortFilter = WorkbenchShell.CreateFilterCombo("排序方式",
            "按数据类 + 表名", "按表名", "按大小（大→小）", "按大小（小→大）", "已修改在前");
        _sortFilter.SelectionChanged += (_, _) => ApplyFilter();
        Shell.AddFilterItem(_sortFilter);

        Shell.AddFilterItem(WorkbenchShell.CreateButton("清除筛选", (_, _) => ClearFilters()));

        // ── 视图一：dataClass 树（默认）──────────────────────────────
        _tableTree = WorkbenchShell.CreateTree();
        _tableTree.ToolTip = "按数据类分组；展开时才生成下一层";
        _tableTree.SelectedItemChanged += (_, e) => OnTreeSelectionChanged(e.NewValue as TreeViewItem);
        _tableTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(TreeItem_Expanded));
        _tableTree.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler(TreeItem_Collapsed));

        // ── 视图二：表列表 ───────────────────────────────────────────
        _tableList = WorkbenchShell.CreateList();
        _tableList.ItemContainerStyle = WorkbenchShell.CreateListItemStyle();
        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "数据类", DisplayMemberBinding = new Binding(nameof(TableRow.DataClass)), Width = 130 });
        grid.Columns.Add(new GridViewColumn { Header = "表名", DisplayMemberBinding = new Binding(nameof(TableRow.FileName)), Width = 260 });
        grid.Columns.Add(new GridViewColumn { Header = "大小", DisplayMemberBinding = new Binding(nameof(TableRow.SizeLabel)), Width = 90 });
        grid.Columns.Add(new GridViewColumn { Header = "编码", DisplayMemberBinding = new Binding(nameof(TableRow.Utf8Label)), Width = 80 });
        _tableList.View = grid;
        _tableList.SelectionChanged += async (_, _) =>
        {
            if (_tableList.SelectedItem is TableRow row) await SelectTableAsync(row.Entry);
        };

        Shell.SetBrowseContent(_tableTree);
        Shell.SetEmptyHint("正在从 catalog 定位静态数据 bundle…");

        // ── 编辑列 ───────────────────────────────────────────────────
        _locationInfo = new TextBlock { Text = "—", Style = FindStyle("WorkbenchSectionLabel"), Margin = new Thickness(0, 0, 0, 8) };
        _tableInfo = new TextBlock { Text = "—", Style = FindStyle("WorkbenchStatusText") };
        _diffInfo = new TextBlock { Text = "—", Style = FindStyle("WorkbenchStatusText") };
        _editor = new JsonTreeEditor();
        _editor.StatusMessage += (_, text) => Shell.SetStatus(text);
        _editor.DocumentChanged += (_, e) => OnDocumentChanged(e);

        _saveEdit = WorkbenchShell.CreateButton("保存修改到编辑集", (_, _) => SaveCurrentEdit(), isEnabled: false);
        _revertTable = WorkbenchShell.CreateButton("还原此表", async (_, _) => await RevertTableAsync(), isEnabled: false);
        _exportStaticMod = WorkbenchShell.CreateButton("导出 .staticmod…", async (_, _) => await ExportStaticModAsync(), isEnabled: false);

        var buttonRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in new[] { _saveEdit, _revertTable, _exportStaticMod })
            buttonRow.Children.Add(button);

        var editPanel = new StackPanel();
        editPanel.Children.Add(WorkbenchShell.CreatePanelTitle("静态表编辑"));
        editPanel.Children.Add(_locationInfo);
        editPanel.Children.Add(_tableInfo);
        editPanel.Children.Add(_editor);
        editPanel.Children.Add(buttonRow);
        editPanel.Children.Add(_diffInfo);
        Shell.SetEditContent(new ScrollViewer { Content = editPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        // 页面常驻（MainWindow 的 PageHost 只换 Contents）：WPF 在每次重新挂载时都会
        // 再抛一次 Loaded，没有守卫就会「切页回来就重跑定位/索引 + 把树重建回初始形态」
        // ——用户反馈的「树突然折叠」最隐蔽的一条触发路径。
        Loaded += async (_, _) =>
        {
            if (_loaded)
            {
                Log.Debug("静态页 Loaded：守卫命中（_loaded=true），跳过重新定位/索引；展开键 {0} 个，选中表={1}",
                    _expandedTreeKeys.Count, _selected?.Key ?? "-");
                return;
            }
            _loaded = true;
            Log.Debug("静态页 Loaded：首次载入，触发者=Loaded 事件，开始首次刷新");
            await RefreshAsync();
        };
    }

    private static Style? FindStyle(string key) => System.Windows.Application.Current?.TryFindResource(key) as Style;

    // 原先这里有个 MakeCombo(width, toolTip, items)：宽度由调用方拍脑袋写死，且 Height=28 小于
    // Fluent 模板的 MinHeight(32)，中文会被裁剪。现统一改用 WorkbenchShell.CreateFilterCombo
    // （具名样式 + 按最长候选项估算宽度），故本方法删除。

    // ── 定位与索引 ───────────────────────────────────────────────────

    /// <summary>
    /// 宿主在启动扫描完成后调用（plan-15）：**主动**刷新本页数据到最新索引。
    /// 不强制重建索引（扫描刚写完库，这里是热读路径：本机实测 1392 张表 11ms）。
    /// 同时置 <c>_loaded</c>：宿主已经刷过一次，首次 <c>Loaded</c> 不必再跑一遍。
    /// </summary>
    public Task ReloadFromIndexAsync()
    {
        Log.Debug("静态页 ReloadFromIndexAsync：触发者=宿主（启动扫描完成后主动刷新），forceRebuild=false，_loaded 由 {0} 置为 true", _loaded);
        _loaded = true;
        return RefreshAsync();
    }

    private async Task RefreshAsync(bool forceRebuild = false)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var trigger = forceRebuild ? "重新定位/重建索引按钮" : "首次加载/宿主刷新";
        var gameDirectory = _host.Env.EffectiveGameDirectory(_host.Project);
        var cacheRoots = StaticIndexService.CacheRoots(_host.Env.EffectiveUnityCacheDirectory(_host.Project));
        Shell.SetEmptyHint(null);
        Shell.SetStatus("正在从 catalog 定位静态数据 bundle…");
        Log.Debug("静态页刷新开始：触发者={0}，游戏目录={1}，缓存根 {2} 个",
            trigger, gameDirectory ?? "-", cacheRoots.Count);
        try
        {
            var location = await Task.Run(() => StaticIndexService.Locate(gameDirectory, cacheRoots));
            _location = location;
            if (location is null)
            {
                _locationInfo.Text = DescribeLocateFailure(gameDirectory, cacheRoots);
                _tables.Clear();
                ApplyFilter();
                Shell.SetEmptyHint("没有定位到静态数据 bundle。请确认「设置」页的游戏目录 / Unity 缓存目录，并启动一次游戏生成缓存。");
                Log.Warn("静态页刷新：未定位到静态数据 bundle，表清单已清空（触发者={0}，缓存根 {1} 个，耗时 {2} ms）",
                    trigger, cacheRoots.Count, watch.Elapsed.TotalMilliseconds);
                return;
            }
            if (!location.IsCached)
            {
                _locationInfo.Text = location.Describe();
                _tables.Clear();
                ApplyFilter();
                Shell.SetEmptyHint("缓存里还没有这个 bundle：启动一次游戏让它生成缓存后点「重新定位/重建索引」。");
                Shell.SetStatus("静态数据 bundle 已定位，但缓存条目不存在。");
                Log.Warn("静态页刷新：bundle 已定位但不在 Unity 缓存里，表清单已清空（触发者={0}，bundle={1}，耗时 {2} ms）",
                    trigger, location.Describe(), watch.Elapsed.TotalMilliseconds);
                return;
            }

            var source = StaticIndexSource.From(location);
            _source = source;
            var load = _index.Load(source);
            if (load.IsUsable && !forceRebuild)
            {
                ApplyEntries(load.Entries);
                _locationInfo.Text = location.Describe();
                Shell.SetStatus($"{_tables.Count} 张静态表（读索引 {load.ReadElapsed.TotalMilliseconds:F0} ms）· 点「重新定位/重建索引」可强制重建");
                RefreshActionButtons();
                Log.Debug("静态页刷新完成（热读索引）：触发者={0}，{1} 张表，读索引 {2:F0} ms，总耗时 {3} ms",
                    trigger, _tables.Count, load.ReadElapsed.TotalMilliseconds, watch.Elapsed.TotalMilliseconds);
                return;
            }

            Shell.SetEmptyHint("正在读取静态数据表…（首次需要枚举 bundle 内的全部 TextAsset）");
            var progress = new Progress<StaticIndexProgress>(p => Shell.SetStatus(p.Describe()));
            Log.Debug("静态页刷新：走重建索引路径（触发者={0}，缓存可用={1}）", trigger, load.IsUsable);
            var result = await _index.RebuildAsync(location, source, progress);
            var reloaded = _index.Load(source);
            ApplyEntries(reloaded.Entries);
            _locationInfo.Text = location.Describe();
            Shell.SetStatus(result.Describe());
            Log.Info("静态页重建索引完成：触发者={0}，{1} 张表，耗时 {2} ms",
                trigger, _tables.Count, watch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Shell.SetStatus($"定位或读取静态数据表失败：{ex.Message}");
            Shell.SetEmptyHint("读取失败：详见状态栏。");
            Log.Error(ex, "静态页定位或读取静态数据表失败：触发者={0}，游戏目录={1}，耗时 {2} ms",
                trigger, gameDirectory ?? "-", watch.Elapsed.TotalMilliseconds);
        }
        finally
        {
            RefreshActionButtons();
        }
    }

    private static string DescribeLocateFailure(string? gameDirectory, IReadOnlyList<string> cacheRoots)
    {
        var candidates = StaticBundleLocator.FindCatalogCandidates(gameDirectory, cacheRoots);
        return candidates.Count == 0
            ? "没有找到 catalog：请确认「设置」页的 Unity 缓存目录（运行时 catalog 所在）或游戏目录。"
            : $"已找到 catalog（{string.Join("；", candidates)}）但没有 {StaticBundleLocator.BundleNamePrefix}*.bundle 条目（catalog 版本不符？）。";
    }

    private void ApplyEntries(IReadOnlyList<StaticTableEntry> entries)
    {
        _tables.Clear();
        _tables.AddRange(entries);
        ApplyFilter();
        Log.Debug("静态页应用表清单：{0} 张表", _tables.Count);
    }

    // ── 筛选 / 排序 / 视图 ───────────────────────────────────────────

    private void ClearFilters()
    {
        Log.Debug("静态页清除筛选：重置搜索/状态/排序 3 项");
        _search.Text = string.Empty;
        _stateFilter.SelectedIndex = 0;
        _sortFilter.SelectedIndex = 0;
        ApplyFilter();
    }

    private IReadOnlyList<StaticTableEntry> FilteredEntries()
    {
        var query = _search.Text.Trim();
        IEnumerable<StaticTableEntry> rows = _tables;
        if (query.Length > 0)
        {
            rows = rows.Where(x =>
                x.DataClass.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.ContainerEntry.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        rows = _stateFilter.SelectedIndex switch
        {
            1 => rows.Where(x => _edits.IsModified(x.Key)),
            2 => rows.Where(x => _index.Store.ReadCachedDocumentKeys().Contains(x.Key)),
            3 => rows.Where(x => !x.IsUtf8),
            _ => rows,
        };
        return _sortFilter.SelectedIndex switch
        {
            1 => rows.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
            2 => rows.OrderByDescending(x => x.SizeBytes).ToList(),
            3 => rows.OrderBy(x => x.SizeBytes).ToList(),
            4 => rows.OrderByDescending(x => _edits.IsModified(x.Key)).ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => rows.ToList(),
        };
    }

    private void ApplyFilter()
    {
        var filterWatch = System.Diagnostics.Stopwatch.StartNew();
        var filtered = FilteredEntries();
        if (_viewMode == StaticViewMode.List)
        {
            _tableList.ItemsSource = filtered.Select(x => new TableRow(x)).ToList();
        }
        else
        {
            RebuildTree(filtered);
        }
        if (_tables.Count == 0) Shell.SetEmptyHint("没有静态数据表（索引为空？）");
        else if (filtered.Count == 0) Shell.SetEmptyHint("没有表匹配当前筛选条件。");
        else Shell.SetEmptyHint(null);
        Shell.SetStatus($"{filtered.Count} / {_tables.Count} 张表" +
                        (_edits.EntryCount > 0 ? $" · 已修改 {_edits.EntryCount} 张" : string.Empty));
        Log.Debug("静态页筛选完成：{0} / {1} 张表（搜索=\"{2}\"，状态档={3}，排序档={4}，视图={5}），耗时 {6} ms",
            filtered.Count, _tables.Count, _search.Text.Trim(), _stateFilter.SelectedIndex, _sortFilter.SelectedIndex,
            _viewMode, filterWatch.Elapsed.TotalMilliseconds);
    }

    private void SwitchView(StaticViewMode mode)
    {
        Log.Debug("静态页视图切换：{0} → {1}", _viewMode, mode);
        _viewMode = mode;
        if (mode == StaticViewMode.List) Shell.SetBrowseContent(_tableList);
        else Shell.SetBrowseContent(_tableTree);
        ApplyFilter();
    }

    // ── 树（dataClass → 表）────────────────────────────────────────

    private void RebuildTree(IReadOnlyList<StaticTableEntry> filtered)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // 先收下当前展开态（累积集合跨重建保留），重建后按 dataClass key 回放。
        TreeExpansionState.Capture(_tableTree, DataClassKeyOf, _expandedTreeKeys);
        var capturedKeys = _expandedTreeKeys.Count;
        var filteredKeys = filtered.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = filtered.GroupBy(x => x.DataClass, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var roots = new List<TreeViewItem>();
        foreach (var group in groups)
        {
            var node = new TreeViewItem
            {
                Header = $"{group.Key}（{group.Count()}）",
                Tag = group.Key,
                Style = WorkbenchShell.CreateTreeItemStyle(),
            };
            node.Items.Add(new TreeViewItem { Header = PlaceholderText, Tag = StaticPlaceholderTag });
            roots.Add(node);
        }
        _tableTree.ItemsSource = roots;
        Log.Debug("静态页重建 dataClass 树：根节点 {0} 个（{1} 张表参与分组），捕获展开键 {2} 个",
            roots.Count, filtered.Count, capturedKeys);
        TreeExpansionState.Restore(_tableTree, DataClassKeyOf, _expandedTreeKeys,
            item => MaterializeDataClass(item, filteredKeys));
        // 重建会丢树上的选中高亮：按 key 找回选中的表（编辑列内容本来就不受影响）。
        if (_selected is { } selected) RestoreTreeSelection(selected.Key);
        Log.Debug("静态页重建 dataClass 树完成：展开键 {0} 个，总耗时 {1} ms", _expandedTreeKeys.Count, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>重建后按表 key 找回树里的选中叶子。</summary>
    private void RestoreTreeSelection(string tableKey)
    {
        foreach (var root in _tableTree.Items)
        {
            if (root is not TreeViewItem node) continue;
            foreach (var child in node.Items)
            {
                if (child is TreeViewItem { Tag: StaticTableEntry entry } leaf &&
                    string.Equals(entry.Key, tableKey, StringComparison.OrdinalIgnoreCase))
                {
                    leaf.IsSelected = true;
                    leaf.BringIntoView();
                    Log.Debug("静态页恢复树选中成功：{0}", tableKey);
                    return;
                }
            }
        }
        Log.Warn("静态页恢复树选中失败：重建后的树里找不到该表（可能在当前筛选外或所属 dataClass 未展开），选中={0}", tableKey);
    }

    private const string PlaceholderText = "载入中…";

    /// <summary>占位子项的哨兵 Tag：判定「这一层是否已物化过」（参照其余三页的做法）。</summary>
    private static readonly object StaticPlaceholderTag = new();

    /// <summary>dataClass 节点的稳定 key（同分组只可能有一个节点）。</summary>
    private static string? DataClassKeyOf(object? tag) => tag as string;

    /// <summary>物化一个 dataClass 的子层（与展开事件同一逻辑，供回放复用）。
    /// <paramref name="filteredKeys"/> 为 null 时按当前筛选即时重算（用户在展开）。</summary>
    private void MaterializeDataClass(TreeViewItem item, IReadOnlySet<string>? filteredKeys)
    {
        if (item.Tag is not string dataClass)
        {
            Log.Trace("静态页物化 dataClass：Tag 不是字符串（{0}），跳过", item.Tag?.GetType().Name ?? "-");
            return;
        }
        // 幂等：只有「还是占位子项」时才物化（占位项 Tag 是哨兵对象，真实子项 Tag 是 StaticTableEntry）。
        if (item.Items.Count != 1 || !ReferenceEquals((item.Items[0] as TreeViewItem)?.Tag, StaticPlaceholderTag))
        {
            Log.Trace("静态页物化 dataClass：{0} 已物化（子项 {1} 个），跳过", dataClass, item.Items.Count);
            return;
        }
        var entries = filteredKeys is null
            ? FilteredEntries().Where(x => string.Equals(x.DataClass, dataClass, StringComparison.OrdinalIgnoreCase))
            : FilteredEntries().Where(x => string.Equals(x.DataClass, dataClass, StringComparison.OrdinalIgnoreCase) && filteredKeys.Contains(x.Key));
        item.Items.Clear();
        var added = 0;
        foreach (var entry in entries)
        {
            var modified = _edits.IsModified(entry.Key);
            item.Items.Add(new TreeViewItem
            {
                Header = $"{entry.FileName}（{entry.SizeLabel}{(entry.IsUtf8 ? string.Empty : " · 非 UTF-8")}）" +
                         (modified ? "　✎" : string.Empty),
                Tag = entry,
                Style = WorkbenchShell.CreateTreeItemStyle(),
            });
            added++;
        }
        Log.Debug("静态页物化 dataClass：{0} 建出 {1} 个子项（按筛选键限定={2}）", dataClass, added, filteredKeys is not null);
    }

    private void TreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item)
        {
            Log.Debug("静态页树展开事件：原发者不是 TreeViewItem（{0}），忽略", e.OriginalSource?.GetType().Name ?? "-");
            return;
        }
        MaterializeDataClass(item, null);
        if (DataClassKeyOf(item.Tag) is { Length: > 0 } key)
        {
            _expandedTreeKeys.Add(key);
            Log.Debug("静态页树节点展开：key={0}，累积展开键 {1} 个", key, _expandedTreeKeys.Count);
        }
        else
        {
            Log.Debug("静态页树节点展开：该节点不是 dataClass 节点（Tag 类型={0}），展开键保持 {1} 个",
                item.Tag?.GetType().Name ?? "-", _expandedTreeKeys.Count);
        }
    }

    private void TreeItem_Collapsed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem item && DataClassKeyOf(item.Tag) is { Length: > 0 } key)
        {
            _expandedTreeKeys.Remove(key);
            Log.Debug("静态页树节点折叠：key={0}，剩余展开键 {1} 个", key, _expandedTreeKeys.Count);
        }
    }

    private void OnTreeSelectionChanged(TreeViewItem? item)
    {
        if (item?.Tag is not StaticTableEntry entry)
        {
            Log.Trace("静态页树选择变化：选中的不是表叶子（Tag 类型={0}）", item?.Tag?.GetType().Name ?? "-");
            return;
        }
        Log.Debug("静态页树选择变化：{0}", entry.Key);
        _ = SelectTableAsync(entry);
    }

    // ── 表编辑 ───────────────────────────────────────────────────────

    private async Task SelectTableAsync(StaticTableEntry entry)
    {
        var generation = ++_loadGeneration;
        var loadWatch = System.Diagnostics.Stopwatch.StartNew();
        _selected = entry;
        _editor.Clear();
        _tableInfo.Text = $"{entry.DataClass}/{entry.FileName} · {entry.SizeLabel} · pathId {entry.PathId}";
        Log.Debug("静态页选中表：{0}（{1} 字节，UTF-8={2}，代际 {3}）", entry.Key, entry.SizeBytes, entry.IsUtf8, generation);
        if (!entry.IsUtf8)
        {
            _tableInfo.Text += " · 非 UTF-8，无法以文本编辑";
            RefreshActionButtons();
            Log.Warn("静态页选中表：非 UTF-8，无法以文本编辑（{0}，{1} 字节）", entry.Key, entry.SizeBytes);
            return;
        }
        if (_location is null)
        {
            Log.Warn("静态页选中表：静态数据 bundle 未定位（_location=null），不载入正文（{0}）", entry.Key);
            return;
        }
        Shell.SetStatus($"正在读取 {entry.FileName}…");
        try
        {
            var document = _edits.TryGetModifiedText(entry.Key) is { } modifiedText
                ? new StaticTableDocument(entry, modifiedText, false)
                : await _index.LoadDocumentAsync(_location, entry);
            if (generation != _loadGeneration)
            {
                Log.Debug("静态页选中表：代际已过期（本次 {0}，当前 {1}），丢弃正文（{2}）", generation, _loadGeneration, entry.Key);
                return;
            }
            var vanilla = _vanilla.TryGetValue(entry.Key, out var savedVanilla)
                ? savedVanilla
                : _edits.TryGetOfficialText(entry.Key) ?? document.Text;
            if (vanilla is not null) _vanilla[entry.Key] = vanilla;
            if (document.Text is null)
            {
                Shell.SetStatus($"{entry.FileName} 不是可读文本（{entry.SizeLabel}）。");
                RefreshActionButtons();
                Log.Warn("静态页选中表：正文为空（不可读文本），未载入编辑器（{0}，{1} 字节）", entry.Key, entry.SizeBytes);
                return;
            }
            _editor.LoadDocument(document.Text, vanilla);
            Shell.SetStatus($"{entry.FileName}：{_editor.DiffSummary}（{(document.FromCache ? "正文来自缓存" : "现场解码")}）");
            UpdateDiffInfo();
            Log.Debug("静态页载入表正文完成：{0}，{1} 个字符，正文来自缓存={2}，有官方基线={3}，耗时 {4} ms",
                entry.Key, document.Text.Length, document.FromCache, vanilla is not null, loadWatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration)
            {
                Log.Debug(ex, "静态页读取静态表异常：代际已过期（本次 {0}，当前 {1}），按原有语义忽略（{2}）",
                    generation, _loadGeneration, entry.Key);
                return;
            }
            Shell.SetStatus($"读取静态表失败：{ex.Message}");
            Log.Error(ex, "静态页读取静态表失败：{0}，耗时 {1} ms", entry.Key, loadWatch.Elapsed.TotalMilliseconds);
        }
        RefreshActionButtons();
    }

    /// <summary>编辑器的改动 → 编辑集（内存；导出 .staticmod 时才落盘）。</summary>
    private void OnDocumentChanged(JsonDocumentChangedEventArgs e)
    {
        if (_selected is null) return;
        _saveEdit.IsEnabled = e.IsModified;
        _diffInfo.Text = e.IsModified
            ? $"编辑器中已有未保存的修改（与官方版本 {e.DiffOperationCount} 处差异）。点「保存修改到编辑集」登记。"
            : "与官方版本无差异。";
        Log.Debug("静态页编辑器文档变更：{0}，已修改={1}，差异操作 {2} 处",
            _selected.Key, e.IsModified, e.DiffOperationCount);
    }

    private void SaveCurrentEdit()
    {
        if (_selected is null || !_editor.HasDocument)
        {
            Log.Debug("静态页保存修改：前置条件不足，忽略（选中表={0}，编辑器有文档={1}）",
                _selected?.Key ?? "-", _editor.HasDocument);
            return;
        }
        var baseline = _vanilla.TryGetValue(_selected.Key, out var vanilla) ? vanilla : null;
        if (baseline is null)
        {
            // 导出要生成 RFC6902 差分，必须有官方基线；没有就明确拒绝，不写一个差不出东西的条目。
            Shell.SetStatus($"{_selected.FileName}：缺少官方基线，无法登记（重新打开该表即可拿到基线）。");
            Log.Warn("静态页保存修改被拒绝：缺少官方基线（{0}）", _selected.Key);
            return;
        }
        _edits.Set(_selected.Key, _selected, baseline, _editor.CurrentJsonText);
        UpdateDiffInfo();
        RefreshActionButtons();
        // 不再手动 ApplyFilter()：_edits.Set 会触发 Changed → ApplyFilter（重建一次即可）。
        Shell.SetStatus($"{_selected.FileName} 的修改已进入编辑集（导出模组时生成 .staticmod 的 RFC6902 补丁）。");
        Log.Info("静态页保存修改到编辑集：{0}（基线 {1} 字符 → 当前 {2} 字符，编辑集共 {3} 张）",
            _selected.Key, baseline.Length, _editor.CurrentJsonText.Length, _edits.EntryCount);
    }

    private void UpdateDiffInfo()
    {
        if (_selected is null) { _diffInfo.Text = "—"; return; }
        if (_edits.TryGetModifiedText(_selected.Key) is not { } modified)
        {
            _diffInfo.Text = "与官方版本无差异。";
            return;
        }
        try
        {
            var baseline = _vanilla.TryGetValue(_selected.Key, out var vanilla)
                ? vanilla
                : _edits.TryGetOfficialText(_selected.Key);
            if (baseline is null)
            {
                _diffInfo.Text = "（缺少官方基线，无法计算差异）";
                Log.Warn("静态页差异信息：缺少官方基线，无法计算差异（{0}）", _selected.Key);
                return;
            }
            var before = System.Text.Json.Nodes.JsonNode.Parse(baseline);
            var after = System.Text.Json.Nodes.JsonNode.Parse(modified);
            var operations = _diff.Generate(before, after);
            _diffInfo.Text = operations.Count == 0
                ? "与官方版本无差异。"
                : $"与官方版本差异：{operations.Count} 个 RFC6902 操作（导出时写入 patches/*.json）。";
            Log.Trace("静态页差异信息：{0}，{1} 个 RFC6902 操作", _selected.Key, operations.Count);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _diffInfo.Text = "（差异无法计算：JSON 非法）";
            Log.Warn(ex, "静态页差异计算失败：JSON 非法，界面降级为提示文案（{0}，当前文本 {1} 字符）",
                _selected.Key, modified.Length);
        }
    }

    private async Task RevertTableAsync()
    {
        if (_selected is null)
        {
            Log.Debug("静态页还原表：未选中表，忽略");
            return;
        }
        _edits.Remove(_selected.Key);
        _vanilla.Remove(_selected.Key);
        Log.Info("静态页还原表：{0} 已移出编辑集（未写任何文件，编辑集剩 {1} 张）", _selected.Key, _edits.EntryCount);
        await SelectTableAsync(_selected);
        // 不再手动 ApplyFilter()：_edits.Remove 会触发 Changed → ApplyFilter（重建一次即可）。
        Shell.SetStatus($"{_selected.FileName} 已还原（未写任何文件）。");
    }

    private void RefreshActionButtons()
    {
        var hasTable = _selected is not null;
        _saveEdit.IsEnabled = hasTable && _editor.IsModified;
        _revertTable.IsEnabled = hasTable && _selected is not null && _edits.IsModified(_selected.Key);
        _exportStaticMod.IsEnabled = _edits.EntryCount > 0;
        _clearDocumentCache.IsEnabled = _source is not null;
        Log.Trace("静态页刷新按钮状态：选中表={0}，保存={1}，还原={2}，导出={3}，清缓存={4}（编辑集 {5} 张）",
            _selected?.Key ?? "-", _saveEdit.IsEnabled, _revertTable.IsEnabled, _exportStaticMod.IsEnabled,
            _clearDocumentCache.IsEnabled, _edits.EntryCount);
    }

    // ── 正文缓存维护 ─────────────────────────────────────────────────

    private void ClearDocumentCache()
    {
        try
        {
            var before = _index.Store.ReadDocumentCacheUsage();
            Log.Info("静态页清空正文缓存：开始（{0} 张表 / {1} 字节）", before.Count, before.Bytes);
            _index.Store.ClearDocuments();
            Shell.SetStatus($"已清空正文缓存（{before.Count} 张表 / {before.Bytes / 1024.0 / 1024.0:0.0} MB）。元数据索引保留，" +
                            "再次打开表时会重新读取并按需缓存。");
            Log.Info("静态页清空正文缓存：完成（{0} 张表 / {1} 字节已释放，元数据索引保留）", before.Count, before.Bytes);
        }
        catch (Exception ex)
        {
            Shell.SetStatus($"清空正文缓存失败：{ex.Message}");
            Log.Error(ex, "静态页清空正文缓存失败：{0}", _index.Store.DatabasePath);
        }
    }

    // ── 导出 .staticmod ─────────────────────────────────────────────

    /// <summary>
    /// 导出 .staticmod（S6 之前保留的手动入口；S6 起由侧边栏「导出模组」统一产出
    /// <c>&lt;项目名&gt;_static/staticmod/</c>，本入口与按钮一并删除）。
    /// 编辑集自 plan-16 S3 起由宿主会话持有，这里只读快照。
    /// </summary>
    private async Task ExportStaticModAsync()
    {
        var snapshot = _edits.Snapshot();
        if (snapshot.Count == 0)
        {
            Shell.SetStatus("没有修改：先编辑至少一张表并「保存修改到编辑集」。");
            Log.Debug("静态页导出 .staticmod：编辑集为空，忽略");
            return;
        }
        var project = _host.Project;
        var modDirectory = _host.Env.EffectiveModDirectory(project);
        var defaultDirectory = !string.IsNullOrWhiteSpace(modDirectory) && Directory.Exists(modDirectory)
            ? modDirectory
            : _host.ProjectFile is null ? null : Path.Combine(Path.GetDirectoryName(_host.ProjectFile)!, "builds");
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "静态数据模组 (*.staticmod)|*.staticmod|所有文件 (*.*)|*.*",
            FileName = (project?.Name is { Length: > 0 } name ? Sanitize(name) : "LME") + ".staticmod",
            InitialDirectory = defaultDirectory,
            Title = "导出 .staticmod（放进模组目录后由加载器重打包并双写 catalog）",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            Log.Debug("静态页导出 .staticmod：用户取消保存对话框（{0} 张表待导出）", snapshot.Count);
            return;
        }

        var work = Path.Combine(Path.GetTempPath(), "lme-staticmod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var entries = new List<(string DataClass, string File, string? Container, string OfficialJsonPath, string ModifiedJsonPath)>();
            for (var index = 0; index < snapshot.Count; index++)
            {
                var edit = snapshot[index];
                var officialPath = Path.Combine(work, $"official-{index}.json");
                var modifiedPath = Path.Combine(work, $"modified-{index}.json");
                await File.WriteAllTextAsync(officialPath, edit.OfficialText, new UTF8Encoding(false));
                await File.WriteAllTextAsync(modifiedPath, edit.ModifiedText, new UTF8Encoding(false));
                entries.Add((edit.Entry.DataClass, edit.Entry.FileName,
                    string.IsNullOrWhiteSpace(edit.Entry.ContainerEntry) ? null : edit.Entry.ContainerEntry,
                    officialPath, modifiedPath));
                Log.Every(index + 1, 200, LogLevel.Debug, () => $"静态页导出 .staticmod：已写出 {index + 1} / {snapshot.Count} 张表的临时 JSON");
            }
            if (entries.Count == 0)
            {
                Shell.SetStatus("没有可导出的差异（编辑集为空）。");
                Log.Warn("静态页导出 .staticmod：编辑集快照 {0} 条但临时条目为 0，已中止（{1}）", snapshot.Count, dialog.FileName);
                return;
            }
            var package = _staticMods.CreateJsonPatchPackage(
                project?.Name is { Length: > 0 } packageName ? packageName : "LME 静态数据补丁",
                project?.Version is { Length: > 0 } version ? version : "1.0",
                project?.Description ?? "由 Limbus Mod Editor 生成（静态数据表 RFC6902 补丁）",
                entries);
            _staticMods.Write(package, dialog.FileName);
            Shell.SetStatus($"已导出：{dialog.FileName}（{package.Patches.Count} 个补丁条目）\n" +
                            "提示：真实加载器需要开启静态模组开关，并由它负责重打包 bundle 与双写 catalog。");
            Log.Info("静态页导出 .staticmod 完成：{0} 个补丁条目 → {1}，耗时 {2} ms",
                package.Patches.Count, dialog.FileName, watch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Shell.SetStatus($"导出失败：{ex.Message}");
            Log.Error(ex, "静态页导出 .staticmod 失败：{0}（{1} 张表，耗时 {2} ms）",
                dialog.FileName, snapshot.Count, watch.Elapsed.TotalMilliseconds);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (Exception ex) { /* 临时目录 */ Log.Debug(ex, "静态页导出 .staticmod：清理临时目录失败，已忽略（{0}）", work); }
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "LME" : cleaned;
    }
}
