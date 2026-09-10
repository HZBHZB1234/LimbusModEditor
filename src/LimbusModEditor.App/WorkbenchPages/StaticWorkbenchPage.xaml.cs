using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;

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
public partial class StaticWorkbenchPage : UserControl
{
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
    private readonly Dictionary<string, string> _modified = new(StringComparer.OrdinalIgnoreCase);
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

    public StaticWorkbenchPage(IWorkbenchHost host)
    {
        _host = host;
        InitializeComponent();
        Shell.Attach(host, WorkbenchPageKeys.Static);
        _index = new StaticIndexService(new StaticTableIndexStore(host.Env.CacheDirectory));

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
        _viewSwitch = new ComboBox { Width = 130, Height = 28, Margin = new Thickness(0, 0, 6, 0) };
        _viewSwitch.Items.Add("🗂 数据类树");
        _viewSwitch.Items.Add("☰ 表列表");
        _viewSwitch.SelectedIndex = 0;
        _viewSwitch.SelectionChanged += (_, _) => SwitchView((StaticViewMode)Math.Max(0, _viewSwitch.SelectedIndex));
        _viewSwitch.ToolTip = "数据类树 = 按 dataClass 分组（与资源工作台的容器目录同语义）；表列表 = 扁平表";
        Shell.AddFilterItem(_viewSwitch);

        _stateFilter = MakeCombo(130, "按状态筛选", "全部", "仅已修改", "仅已缓存正文", "仅非 UTF-8");
        _stateFilter.SelectionChanged += (_, _) => ApplyFilter();
        Shell.AddFilterItem(_stateFilter);

        _sortFilter = MakeCombo(160, "排序方式",
            "按数据类 + 表名", "按表名", "按大小（大→小）", "按大小（小→大）", "已修改在前");
        _sortFilter.SelectionChanged += (_, _) => ApplyFilter();
        Shell.AddFilterItem(_sortFilter);

        Shell.AddFilterItem(WorkbenchShell.CreateButton("清除筛选", (_, _) => ClearFilters()));

        // ── 视图一：dataClass 树（默认）──────────────────────────────
        _tableTree = WorkbenchShell.CreateTree();
        _tableTree.ToolTip = "按数据类分组；展开时才生成下一层";
        _tableTree.SelectedItemChanged += (_, e) => OnTreeSelectionChanged(e.NewValue as TreeViewItem);
        _tableTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(TreeItem_Expanded));

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

        Loaded += async (_, _) => await RefreshAsync();
    }

    private static Style? FindStyle(string key) => System.Windows.Application.Current?.TryFindResource(key) as Style;

    private static ComboBox MakeCombo(double width, string toolTip, params string[] items)
    {
        var combo = new ComboBox { Width = width, Height = 28, Margin = new Thickness(0, 0, 6, 0), ToolTip = toolTip };
        foreach (var item in items) combo.Items.Add(item);
        combo.SelectedIndex = 0;
        return combo;
    }

    // ── 定位与索引 ───────────────────────────────────────────────────

    private async Task RefreshAsync(bool forceRebuild = false)
    {
        var gameDirectory = _host.Env.EffectiveGameDirectory(_host.Project);
        var cacheRoots = StaticIndexService.CacheRoots(_host.Env.EffectiveUnityCacheDirectory(_host.Project));
        Shell.SetEmptyHint(null);
        Shell.SetStatus("正在从 catalog 定位静态数据 bundle…");
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
                return;
            }
            if (!location.IsCached)
            {
                _locationInfo.Text = location.Describe();
                _tables.Clear();
                ApplyFilter();
                Shell.SetEmptyHint("缓存里还没有这个 bundle：启动一次游戏让它生成缓存后点「重新定位/重建索引」。");
                Shell.SetStatus("静态数据 bundle 已定位，但缓存条目不存在。");
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
                return;
            }

            Shell.SetEmptyHint("正在读取静态数据表…（首次需要枚举 bundle 内的全部 TextAsset）");
            var progress = new Progress<StaticIndexProgress>(p => Shell.SetStatus(p.Describe()));
            var result = await _index.RebuildAsync(location, source, progress);
            var reloaded = _index.Load(source);
            ApplyEntries(reloaded.Entries);
            _locationInfo.Text = location.Describe();
            Shell.SetStatus(result.Describe());
        }
        catch (Exception ex)
        {
            Shell.SetStatus($"定位或读取静态数据表失败：{ex.Message}");
            Shell.SetEmptyHint("读取失败：详见状态栏。");
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
    }

    // ── 筛选 / 排序 / 视图 ───────────────────────────────────────────

    private void ClearFilters()
    {
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
            1 => rows.Where(x => _modified.ContainsKey(x.Key)),
            2 => rows.Where(x => _index.Store.ReadCachedDocumentKeys().Contains(x.Key)),
            3 => rows.Where(x => !x.IsUtf8),
            _ => rows,
        };
        return _sortFilter.SelectedIndex switch
        {
            1 => rows.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
            2 => rows.OrderByDescending(x => x.SizeBytes).ToList(),
            3 => rows.OrderBy(x => x.SizeBytes).ToList(),
            4 => rows.OrderByDescending(x => _modified.ContainsKey(x.Key)).ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => rows.ToList(),
        };
    }

    private void ApplyFilter()
    {
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
                        (_modified.Count > 0 ? $" · 已修改 {_modified.Count} 张" : string.Empty));
    }

    private void SwitchView(StaticViewMode mode)
    {
        _viewMode = mode;
        if (mode == StaticViewMode.List) Shell.SetBrowseContent(_tableList);
        else Shell.SetBrowseContent(_tableTree);
        ApplyFilter();
    }

    // ── 树（dataClass → 表）────────────────────────────────────────

    private void RebuildTree(IReadOnlyList<StaticTableEntry> filtered)
    {
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
            node.Items.Add(new TreeViewItem { Header = PlaceholderText });
            roots.Add(node);
        }
        _tableTree.ItemsSource = roots;
    }

    private const string PlaceholderText = "载入中…";

    private void TreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item) return;
        if (item.Tag is not string dataClass) return;
        item.Items.Clear();
        foreach (var entry in FilteredEntries().Where(x => string.Equals(x.DataClass, dataClass, StringComparison.OrdinalIgnoreCase)))
        {
            var modified = _modified.ContainsKey(entry.Key);
            item.Items.Add(new TreeViewItem
            {
                Header = $"{entry.FileName}（{entry.SizeLabel}{(entry.IsUtf8 ? string.Empty : " · 非 UTF-8")}）" +
                         (modified ? "　✎" : string.Empty),
                Tag = entry,
                Style = WorkbenchShell.CreateTreeItemStyle(),
            });
        }
    }

    private void OnTreeSelectionChanged(TreeViewItem? item)
    {
        if (item?.Tag is not StaticTableEntry entry) return;
        _ = SelectTableAsync(entry);
    }

    // ── 表编辑 ───────────────────────────────────────────────────────

    private async Task SelectTableAsync(StaticTableEntry entry)
    {
        var generation = ++_loadGeneration;
        _selected = entry;
        _editor.Clear();
        _tableInfo.Text = $"{entry.DataClass}/{entry.FileName} · {entry.SizeLabel} · pathId {entry.PathId}";
        if (!entry.IsUtf8)
        {
            _tableInfo.Text += " · 非 UTF-8，无法以文本编辑";
            RefreshActionButtons();
            return;
        }
        if (_location is null) return;
        Shell.SetStatus($"正在读取 {entry.FileName}…");
        try
        {
            var document = _modified.TryGetValue(entry.Key, out var modifiedText)
                ? new StaticTableDocument(entry, modifiedText, false)
                : await _index.LoadDocumentAsync(_location, entry);
            if (generation != _loadGeneration) return;
            var vanilla = _vanilla.TryGetValue(entry.Key, out var savedVanilla) ? savedVanilla : document.Text;
            if (vanilla is not null) _vanilla[entry.Key] = vanilla;
            if (document.Text is null)
            {
                Shell.SetStatus($"{entry.FileName} 不是可读文本（{entry.SizeLabel}）。");
                RefreshActionButtons();
                return;
            }
            _editor.LoadDocument(document.Text, vanilla);
            Shell.SetStatus($"{entry.FileName}：{_editor.DiffSummary}（{(document.FromCache ? "正文来自缓存" : "现场解码")}）");
            UpdateDiffInfo();
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return;
            Shell.SetStatus($"读取静态表失败：{ex.Message}");
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
    }

    private void SaveCurrentEdit()
    {
        if (_selected is null || !_editor.HasDocument) return;
        _modified[_selected.Key] = _editor.CurrentJsonText;
        UpdateDiffInfo();
        RefreshActionButtons();
        ApplyFilter();
        Shell.SetStatus($"{_selected.FileName} 的修改已进入编辑集（导出 .staticmod 时生成 RFC6902 补丁）。");
    }

    private void UpdateDiffInfo()
    {
        if (_selected is null) { _diffInfo.Text = "—"; return; }
        if (!_modified.TryGetValue(_selected.Key, out var modified))
        {
            _diffInfo.Text = "与官方版本无差异。";
            return;
        }
        try
        {
            var baseline = _vanilla.TryGetValue(_selected.Key, out var vanilla) ? vanilla : null;
            if (baseline is null)
            {
                _diffInfo.Text = "（缺少官方基线，无法计算差异）";
                return;
            }
            var before = System.Text.Json.Nodes.JsonNode.Parse(baseline);
            var after = System.Text.Json.Nodes.JsonNode.Parse(modified);
            var operations = _diff.Generate(before, after);
            _diffInfo.Text = operations.Count == 0
                ? "与官方版本无差异。"
                : $"与官方版本差异：{operations.Count} 个 RFC6902 操作（导出时写入 patches/*.json）。";
        }
        catch (System.Text.Json.JsonException)
        {
            _diffInfo.Text = "（差异无法计算：JSON 非法）";
        }
    }

    private async Task RevertTableAsync()
    {
        if (_selected is null) return;
        _modified.Remove(_selected.Key);
        _vanilla.Remove(_selected.Key);
        await SelectTableAsync(_selected);
        ApplyFilter();
        Shell.SetStatus($"{_selected.FileName} 已还原（未写任何文件）。");
    }

    private void RefreshActionButtons()
    {
        var hasTable = _selected is not null;
        _saveEdit.IsEnabled = hasTable && _editor.IsModified;
        _revertTable.IsEnabled = hasTable && _selected is not null && _modified.ContainsKey(_selected.Key);
        _exportStaticMod.IsEnabled = _modified.Count > 0;
        _clearDocumentCache.IsEnabled = _source is not null;
    }

    // ── 正文缓存维护 ─────────────────────────────────────────────────

    private void ClearDocumentCache()
    {
        try
        {
            var before = _index.Store.ReadDocumentCacheUsage();
            _index.Store.ClearDocuments();
            Shell.SetStatus($"已清空正文缓存（{before.Count} 张表 / {before.Bytes / 1024.0 / 1024.0:0.0} MB）。元数据索引保留，" +
                            "再次打开表时会重新读取并按需缓存。");
        }
        catch (Exception ex) { Shell.SetStatus($"清空正文缓存失败：{ex.Message}"); }
    }

    // ── 导出 .staticmod ─────────────────────────────────────────────

    private async Task ExportStaticModAsync()
    {
        if (_modified.Count == 0)
        {
            Shell.SetStatus("没有修改：先编辑至少一张表。");
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
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var work = Path.Combine(Path.GetTempPath(), "lme-staticmod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var entries = new List<(string DataClass, string File, string? Container, string OfficialJsonPath, string ModifiedJsonPath)>();
            foreach (var table in _tables)
            {
                if (!_modified.TryGetValue(table.Key, out var modified)) continue;
                if (!_vanilla.TryGetValue(table.Key, out var vanilla))
                {
                    if (_location is null) continue;
                    var document = await _index.LoadDocumentAsync(_location, table);
                    vanilla = document.Text ?? string.Empty;
                    _vanilla[table.Key] = vanilla;
                }
                var officialPath = Path.Combine(work, $"official-{entries.Count}.json");
                var modifiedPath = Path.Combine(work, $"modified-{entries.Count}.json");
                await File.WriteAllTextAsync(officialPath, vanilla, new UTF8Encoding(false));
                await File.WriteAllTextAsync(modifiedPath, modified, new UTF8Encoding(false));
                entries.Add((table.DataClass, table.FileName,
                    string.IsNullOrWhiteSpace(table.ContainerEntry) ? null : table.ContainerEntry,
                    officialPath, modifiedPath));
            }
            if (entries.Count == 0)
            {
                Shell.SetStatus("没有可导出的差异（修改的表不在当前索引里）。");
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
        }
        catch (Exception ex) { Shell.SetStatus($"导出失败：{ex.Message}"); }
        finally
        {
            try { Directory.Delete(work, true); } catch (Exception) { /* 临时目录 */ }
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "LME" : cleaned;
    }
}
