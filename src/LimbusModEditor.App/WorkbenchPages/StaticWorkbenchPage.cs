using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;

namespace LimbusModEditor.App;

/// <summary>
/// 静态数据工作台（plan-08）：定位游戏的特殊静态 bundle
/// （<c>static_s1_0_assets_all_&lt;32hex&gt;.bundle</c>，经 catalog 动态解析），
/// 浏览/搜索其中的 TextAsset 静态表，编辑后用 StaticModService 导出
/// <c>.staticmod</c> 补丁包到<b>模组目录</b>。编辑器绝不写 catalog / 缓存 /
/// 游戏目录——重打包与 catalog 双写由加载器完成。
/// </summary>
public sealed class StaticWorkbenchPage : UserControl
{
    private readonly IWorkbenchHost _host;
    private readonly StaticModService _staticMods = new();
    private readonly TextDiffService _diff = new();
    private readonly DispatcherTimer _searchTimer;
    private readonly TextBox _search;
    private readonly ListView _tableList;
    private readonly TextBlock _status;
    private readonly TextBlock _locationInfo;
    private readonly ListView _kvList;
    private readonly TextBox _valueBox;
    private readonly TextBox _jsonBox;
    private readonly TextBlock _diffInfo;
    private readonly Button _applyValue;
    private readonly Button _saveJson;
    private readonly Button _revertTable;
    private readonly Button _exportStaticMod;

    private StaticBundleLocation? _location;
    private IReadOnlyList<StaticBundleLocator.StaticTextAsset> _tables = [];
    private readonly Dictionary<string, string> _modified = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _vanilla = new(StringComparer.OrdinalIgnoreCase);
    private List<KvRow> _rows = [];
    private JsonNode? _document;
    private StaticBundleLocator.StaticTextAsset? _selected;
    private int _loadGeneration;

    private sealed record KvRow(string Path, string Preview, JsonNode? Node, JsonNode? Parent, string? Key, int Index)
    {
        public bool IsLeaf => Node is JsonValue;
    }

    public StaticWorkbenchPage(IWorkbenchHost host)
    {
        _host = host;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); };

        // ── 浏览列 ────────────────────────────────────────────────────
        _search = new TextBox { Width = 240, Height = 28, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "按表名 / 键 / 值搜索（防抖 300ms）" };
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        var reload = new Button { Content = "重新定位", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        reload.Click += async (_, _) => await RefreshAsync();
        var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        searchRow.Children.Add(_search);
        searchRow.Children.Add(reload);

        _locationInfo = new TextBlock { Text = "—", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x93, 0xA4)), Margin = new Thickness(0, 0, 0, 8) };

        _tableList = MakeListView();
        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "数据类", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(StaticBundleLocator.StaticTextAsset.DataClass)), Width = 120 });
        grid.Columns.Add(new GridViewColumn { Header = "表", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(StaticBundleLocator.StaticTextAsset.FileName)), Width = 240 });
        grid.Columns.Add(new GridViewColumn { Header = "大小", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(StaticBundleLocator.StaticTextAsset.Data)), Width = 90 });
        _tableList.View = grid;
        _tableList.SelectionChanged += async (_, _) => await LoadSelectedTableAsync();

        var browsePanel = new DockPanel();
        DockPanel.SetDock(searchRow, Dock.Top);
        DockPanel.SetDock(_locationInfo, Dock.Top);
        browsePanel.Children.Add(searchRow);
        browsePanel.Children.Add(_locationInfo);
        browsePanel.Children.Add(_tableList);
        var browseBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1D, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x40)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 8, 0),
            Child = browsePanel,
        };

        // ── 编辑列 ────────────────────────────────────────────────────
        _kvList = MakeListView();
        _kvList.Height = 200;
        var kvGrid = new GridView();
        kvGrid.Columns.Add(new GridViewColumn { Header = "键路径", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(KvRow.Path)), Width = 200 });
        kvGrid.Columns.Add(new GridViewColumn { Header = "值", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(KvRow.Preview)), Width = 220 });
        _kvList.View = kvGrid;
        _kvList.SelectionChanged += (_, _) => RefreshValueEditor();

        _valueBox = new TextBox
        {
            Height = 64,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "选中键的完整值；修改后点「应用修改」。",
        };
        _applyValue = MakeButton("应用修改", async (_, _) => await ApplyValueAsync());
        _saveJson = MakeButton("保存原始 JSON 修改", async (_, _) => await SaveJsonAsync());
        _revertTable = MakeButton("还原此表", async (_, _) => await RevertTableAsync());
        _exportStaticMod = MakeButton("导出 .staticmod…", async (_, _) => await ExportStaticModAsync());

        _jsonBox = new TextBox
        {
            Height = 150,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 8, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xE2, 0xEA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x40)),
        };
        _diffInfo = new TextBlock { Text = "—", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x93, 0xA4)), Margin = new Thickness(0, 8, 0, 0) };

        var buttonRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in new[] { _applyValue, _saveJson, _revertTable, _exportStaticMod })
        {
            button.Margin = new Thickness(0, 0, 6, 6);
            buttonRow.Children.Add(button);
        }
        _status = new TextBlock { Text = "—", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x93, 0xA4)), Margin = new Thickness(0, 8, 0, 0) };

        var editPanel = new StackPanel();
        editPanel.Children.Add(new TextBlock { Text = "静态表编辑", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        editPanel.Children.Add(_kvList);
        editPanel.Children.Add(_valueBox);
        editPanel.Children.Add(buttonRow);
        editPanel.Children.Add(_diffInfo);
        editPanel.Children.Add(new TextBlock { Text = "原始 JSON", Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x93, 0xA4)), Margin = new Thickness(0, 6, 0, 0) });
        editPanel.Children.Add(_jsonBox);
        editPanel.Children.Add(_status);
        var editBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1D, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x40)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = new ScrollViewer { Content = editPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };

        // ── 三列骨架 ─────────────────────────────────────────────────
        var layout = new Grid { Margin = new Thickness(12) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        var editColumn = new ColumnDefinition { Width = new GridLength(420), MinWidth = 260 };
        layout.ColumnDefinitions.Add(editColumn);
        Grid.SetColumn(browseBorder, 0);
        layout.Children.Add(browseBorder);
        var splitter = new GridSplitter
        {
            Width = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = GridResizeDirection.Columns,
            ShowsPreview = true,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x40)),
            Cursor = System.Windows.Input.Cursors.SizeWE,
            ToolTip = "拖拽调整编辑区宽度；双击复位默认占比。",
        };
        splitter.MouseDoubleClick += (_, _) => editColumn.Width = new GridLength(420);
        Grid.SetColumn(splitter, 1);
        layout.Children.Add(splitter);
        Grid.SetColumn(editBorder, 2);
        layout.Children.Add(editBorder);

        Content = layout;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private static ListView MakeListView() => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1A)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x40)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF2)),
        SelectionMode = SelectionMode.Single,
    };

    private static Button MakeButton(string text, RoutedEventHandler handler)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), IsEnabled = false };
        button.Click += handler;
        return button;
    }

    // ── 定位与枚举 ───────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        var gameDirectory = _host.Env.EffectiveGameDirectory(_host.Project);
        var cacheRoots = new[] { _host.Env.EffectiveUnityCacheDirectory(_host.Project) };
        _status.Text = "正在从 catalog 定位静态数据 bundle…";
        try
        {
            var location = await Task.Run(() => StaticBundleLocator.Locate(gameDirectory, cacheRoots));
            _location = location;
            if (location is null)
            {
                _locationInfo.Text = gameDirectory is null
                    ? "没有配置游戏目录：请在「设置」页填写游戏目录后再试。"
                    : "catalog 里没有找到 static_s1_0_assets_all_*.bundle 条目（catalog 缺失或版本不符）。";
                _tables = [];
                _tableList.ItemsSource = null;
                _status.Text = "—";
                return;
            }
            _locationInfo.Text = location.Describe();
            if (!location.IsCached)
            {
                _tables = [];
                _tableList.ItemsSource = null;
                _status.Text = "缓存里还没有这个 bundle —— 启动一次游戏让它生成缓存后点「重新定位」。";
                return;
            }
            _status.Text = "正在枚举静态表…";
            var tables = await Task.Run(() => StaticBundleLocator.ReadTextAssetEntries(location));
            _tables = tables;
            _tableList.ItemsSource = tables;
            _status.Text = $"已读取 {tables.Count} 张静态表（只读引用模式，未复制任何文件）。";
        }
        catch (Exception ex)
        {
            _status.Text = $"定位或枚举失败：{ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        var query = _search.Text.Trim();
        if (query.Length == 0) { _tableList.ItemsSource = _tables; return; }
        var filtered = new List<StaticBundleLocator.StaticTextAsset>();
        foreach (var table in _tables)
        {
            if (table.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                table.DataClass.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                table.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                filtered.Add(table);
                continue;
            }
            var text = table.TryDecodeUtf8();
            if (text is not null && text.Contains(query, StringComparison.OrdinalIgnoreCase)) filtered.Add(table);
        }
        _tableList.ItemsSource = filtered;
        _status.Text = $"{filtered.Count} / {_tables.Count} 张表匹配「{query}」";
    }

    // ── 表编辑 ───────────────────────────────────────────────────────

    private string TableKey(StaticBundleLocator.StaticTextAsset table) => $"{table.ContainerEntry}|{table.Name}|{table.PathId}";

    private async Task LoadSelectedTableAsync()
    {
        var generation = ++_loadGeneration;
        _rows = [];
        _document = null;
        _kvList.ItemsSource = null;
        _valueBox.Text = string.Empty;
        _jsonBox.Text = string.Empty;
        _diffInfo.Text = "—";
        if (_tableList.SelectedItem is not StaticBundleLocator.StaticTextAsset table)
        {
            _selected = null;
            RefreshActionButtons();
            return;
        }
        _selected = table;
        var key = TableKey(table);
        var text = _modified.TryGetValue(key, out var modified) ? modified
            : _vanilla.TryGetValue(key, out var vanilla) ? vanilla
            : table.TryDecodeUtf8();
        if (text is null)
        {
            _status.Text = $"{table.Name} 不是可读文本（{table.Data.Length:N0} 字节，非 UTF-8）。";
            RefreshActionButtons();
            return;
        }
        _vanilla.TryAdd(key, table.TryDecodeUtf8() ?? string.Empty);
        try
        {
            ApplyDocument(text);
            _status.Text = $"{table.Name}：{_rows.Count} 个可编辑键";
            UpdateDiffInfo();
        }
        catch (Exception ex) { _status.Text = $"解析 JSON 失败：{ex.Message}"; }
        RefreshActionButtons();
        await Task.CompletedTask;
    }

    private void ApplyDocument(string jsonText)
    {
        _document = JsonNode.Parse(jsonText);
        _jsonBox.Text = jsonText;
        _rows = [];
        if (_document is not null) Flatten(_document, string.Empty, _document, null, -1, _rows);
        _kvList.ItemsSource = _rows;
    }

    private static void Flatten(JsonNode? node, string path, JsonNode? parent, string? key, int index, List<KvRow> rows)
    {
        if (rows.Count > 5000) return;
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    var childPath = path.Length == 0 ? pair.Key : $"{path}/{pair.Key}";
                    Flatten(pair.Value, childPath, obj, pair.Key, -1, rows);
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    Flatten(array[i], $"{path}/{i}", array, null, i, rows);
                break;
            case JsonValue value:
                var text = value.TryGetValue<string>(out var str) ? str : value.ToJsonString();
                rows.Add(new KvRow(path, text.Length <= 120 ? text : text[..120] + "…", value, parent, key, index));
                break;
        }
    }

    private void RefreshValueEditor()
    {
        if (_kvList.SelectedItem is not KvRow row) { _valueBox.Text = string.Empty; return; }
        _valueBox.Text = row.Node is JsonValue value
            ? (value.TryGetValue<string>(out var str) ? str : value.ToJsonString())
            : row.Preview;
    }

    private async Task ApplyValueAsync()
    {
        if (_selected is null || _kvList.SelectedItem is not KvRow row) return;
        if (!row.IsLeaf)
        {
            _status.Text = "只能编辑叶子值；数组与对象请用「原始 JSON」。";
            return;
        }
        try
        {
            var text = SetNodeValue(row, _valueBox.Text);
            _modified[TableKey(_selected)] = text;
            ApplyDocument(text);
            UpdateDiffInfo();
            _status.Text = $"已修改键 {row.Path}（导出 .staticmod 时生成补丁）。";
            await Task.CompletedTask;
        }
        catch (Exception ex) { _status.Text = $"应用修改失败：{ex.Message}"; }
    }

    private string SetNodeValue(KvRow row, string text)
    {
        JsonNode? replacement;
        var current = row.Node as JsonValue;
        if (current is not null && current.TryGetValue<bool>(out _)) replacement = JsonValue.Create(bool.TryParse(text, out var b) && b);
        else if (current is not null && current.TryGetValue<long>(out _)) replacement = JsonValue.Create(long.TryParse(text, out var l) ? l : throw new InvalidDataException("该键原值是整数，请输入整数。"));
        else if (current is not null && current.TryGetValue<double>(out _)) replacement = JsonValue.Create(double.TryParse(text, out var d) ? d : throw new InvalidDataException("该键原值是数字，请输入数字。"));
        else replacement = JsonValue.Create(text);
        if (row.Parent is JsonObject obj && row.Key is { } key) obj[key] = replacement;
        else if (row.Parent is JsonArray array && row.Index >= 0) array[row.Index] = replacement;
        else throw new InvalidOperationException("该节点没有可写的父容器。");
        return (_document ?? row.Parent).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task SaveJsonAsync()
    {
        if (_selected is null) return;
        try
        {
            var text = _jsonBox.Text;
            _ = JsonNode.Parse(text) ?? throw new InvalidDataException("内容为空。");
            _modified[TableKey(_selected)] = text;
            ApplyDocument(text);
            UpdateDiffInfo();
            _status.Text = "原始 JSON 修改已进入编辑集。";
        }
        catch (JsonException ex) { _status.Text = $"JSON 非法，已拒绝（{ex.Message}）。"; }
        catch (Exception ex) { _status.Text = $"保存失败：{ex.Message}"; }
        await Task.CompletedTask;
    }

    private async Task RevertTableAsync()
    {
        if (_selected is null) return;
        var key = TableKey(_selected);
        _modified.Remove(key);
        _vanilla.Remove(key);
        await LoadSelectedTableAsync();
        _status.Text = $"{_selected.Name} 已还原（未写任何文件）。";
    }

    private void UpdateDiffInfo()
    {
        if (_selected is null) { _diffInfo.Text = "—"; return; }
        var key = TableKey(_selected);
        if (!_modified.TryGetValue(key, out var modified) || !_vanilla.TryGetValue(key, out var vanilla))
        {
            _diffInfo.Text = "与官方版本无差异。";
            return;
        }
        try
        {
            var before = JsonNode.Parse(vanilla);
            var after = JsonNode.Parse(modified);
            if (before is null || after is null) { _diffInfo.Text = "—"; return; }
            var ops = _diff.Generate(before, after);
            _diffInfo.Text = ops.Count == 0
                ? "与官方版本无差异。"
                : $"与官方版本差异：{ops.Count} 个 RFC6902 操作（导出时写入 patches/*.json）。";
        }
        catch (JsonException) { _diffInfo.Text = "（差异无法计算：JSON 非法）"; }
    }

    // ── 导出 .staticmod ─────────────────────────────────────────────

    private async Task ExportStaticModAsync()
    {
        if (_modified.Count == 0)
        {
            _status.Text = "没有修改：先编辑至少一张表。";
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
                var key = TableKey(table);
                if (!_modified.TryGetValue(key, out var modified)) continue;
                if (!_vanilla.TryGetValue(key, out var vanilla)) vanilla = table.TryDecodeUtf8() ?? string.Empty;
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
                _status.Text = "没有可导出的差异（修改的表不在当前列表中）。";
                return;
            }
            var package = _staticMods.CreateJsonPatchPackage(
                project?.Name is { Length: > 0 } packageName ? packageName : "LME 静态数据补丁",
                project?.Version is { Length: > 0 } version ? version : "1.0",
                project?.Description ?? "由 Limbus Mod Editor 生成（静态数据表 RFC6902 补丁）",
                entries);
            _staticMods.Write(package, dialog.FileName);
            _status.Text = $"已导出：{dialog.FileName}（{package.Patches.Count} 个补丁条目）\n" +
                           "提示：真实加载器需要开启静态模组开关，并由它负责重打包 bundle 与双写 catalog。";
        }
        catch (Exception ex) { _status.Text = $"导出失败：{ex.Message}"; }
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

    private void RefreshActionButtons()
    {
        var hasTable = _selected is not null;
        _applyValue.IsEnabled = hasTable && (_kvList.SelectedItem as KvRow)?.IsLeaf == true;
        _saveJson.IsEnabled = hasTable;
        _revertTable.IsEnabled = hasTable;
        _exportStaticMod.IsEnabled = _modified.Count > 0;
    }
}
