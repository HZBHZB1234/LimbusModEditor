using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.App;

/// <summary>
/// 文本工作台（plan-07）：浏览 <c>&lt;游戏&gt;/LimbusCompany_Data/lang</c> 下
/// config.json 指向的活动语言目录，键值编辑后导出与真实加载器（LCTA
/// changes.py）语义一致的 RFC6902 lang 补丁。<b>默认不写游戏 lang 目录</b>——
/// 「直接应用」入口有显著警告。
/// </summary>
public sealed class TextWorkbenchPage : UserControl
{
    private readonly IWorkbenchHost _host;
    private readonly LangTextWorkbenchService _service = new();
    private readonly DispatcherTimer _searchTimer;
    private readonly TextBox _search;
    private readonly ListView _fileList;
    private readonly ListView _hitList;
    private readonly TextBlock _status;
    private readonly TextBlock _langInfo;
    private readonly ListView _kvList;
    private readonly TextBox _valueBox;
    private readonly TextBox _jsonBox;
    private readonly Button _applyValue;
    private readonly Button _deleteKey;
    private readonly Button _saveJson;
    private readonly Button _revertFile;
    private readonly Button _exportPatch;
    private readonly Button _applyToGame;

    private IReadOnlyList<LangTextFileInfo> _files = [];
    private List<KvRow> _rows = [];
    private JsonNode? _document;
    private string? _selectedRelativePath;
    private int _loadGeneration;

    private sealed record KvRow(string Path, string Preview, JsonNode? Node, JsonNode? Parent, string? Key, int Index)
    {
        public bool IsLeaf => Node is JsonValue;
    }

    public TextWorkbenchPage(IWorkbenchHost host)
    {
        _host = host;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RunSearchAsync(); };

        // ── 浏览列 ────────────────────────────────────────────────────
        _search = new TextBox { Width = 240, Height = 28, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "搜索文件名 / 键 / 值（防抖 300ms，后台线程）" };
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        var reload = new Button { Content = "重新加载", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        reload.Click += async (_, _) => await RefreshAsync();
        var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        searchRow.Children.Add(_search);
        searchRow.Children.Add(reload);

        _langInfo = new TextBlock { Text = "—", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x93, 0xA4)), Margin = new Thickness(0, 0, 0, 8) };

        _fileList = MakeListView();
        var fileGrid = new GridView();
        fileGrid.Columns.Add(new GridViewColumn { Header = "文件", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(LangTextFileInfo.RelativePath)), Width = 320 });
        fileGrid.Columns.Add(new GridViewColumn { Header = "键数", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(LangTextFileInfo.KeyCount)), Width = 70 });
        fileGrid.Columns.Add(new GridViewColumn { Header = "大小", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(LangTextFileInfo.SizeBytes)), Width = 90 });
        _fileList.View = fileGrid;
        _fileList.SelectionChanged += async (_, _) => await LoadSelectedFileAsync();

        _hitList = MakeListView();
        _hitList.Height = 150;
        _hitList.Margin = new Thickness(0, 8, 0, 0);
        _hitList.SelectionChanged += (_, _) =>
        {
            if (_hitList.SelectedItem is not LangTextSearchHit hit) return;
            var target = _files.FirstOrDefault(x => x.RelativePath == hit.RelativePath);
            if (target is not null) _fileList.SelectedItem = target;
        };

        var browsePanel = new DockPanel();
        DockPanel.SetDock(searchRow, Dock.Top);
        DockPanel.SetDock(_langInfo, Dock.Top);
        browsePanel.Children.Add(searchRow);
        browsePanel.Children.Add(_langInfo);
        var hitHeader = new TextBlock { Text = "搜索结果", Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x93, 0xA4)), Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(hitHeader, Dock.Top);
        browsePanel.Children.Add(hitHeader);
        browsePanel.Children.Add(_hitList);
        browsePanel.Children.Add(_fileList);
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
            Height = 70,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "选中键的完整值；修改后点「应用修改」（数组/对象节点不可直接编辑）。",
        };
        _applyValue = MakeButton("应用修改", async (_, _) => await ApplyValueAsync());
        _deleteKey = MakeButton("删除此键…", async (_, _) => await DeleteKeyAsync());
        _saveJson = MakeButton("保存原始 JSON 修改", async (_, _) => await SaveJsonAsync());
        _revertFile = MakeButton("还原此文件", async (_, _) => await RevertFileAsync());

        _jsonBox = new TextBox
        {
            Height = 160,
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
            ToolTip = "当前文件的完整 JSON（UTF-8 无 BOM）。保存前会校验合法性，失败不改编辑集。",
        };

        _exportPatch = MakeButton("导出 lang 补丁…", async (_, _) => await ExportPatchAsync());
        _applyToGame = MakeButton("直接应用到 lang 目录…", async (_, _) => await ApplyToGameAsync());

        var buttonRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in new[] { _applyValue, _deleteKey, _saveJson, _revertFile, _exportPatch, _applyToGame })
        {
            button.Margin = new Thickness(0, 0, 6, 6);
            buttonRow.Children.Add(button);
        }
        _status = new TextBlock { Text = "—", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x93, 0xA4)), Margin = new Thickness(0, 8, 0, 0) };

        var editPanel = new StackPanel();
        editPanel.Children.Add(new TextBlock { Text = "键值编辑", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        editPanel.Children.Add(_kvList);
        editPanel.Children.Add(_valueBox);
        editPanel.Children.Add(buttonRow);
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

    // ── 加载与搜索 ───────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        var langRoot = _service.ResolveLangRoot(_host.Env.EffectiveGameDirectory(_host.Project));
        if (langRoot is null)
        {
            _langInfo.Text = "没有找到 lang 目录。请在「设置」页填写游戏目录（lang 目录为 LimbusCompany_Data/lang）。";
            _files = [];
            _fileList.ItemsSource = null;
            return;
        }
        _status.Text = "正在枚举 lang 文件…";
        try
        {
            var files = await Task.Run(() => _service.EnumerateFiles(langRoot));
            _files = files;
            var active = _service.ReadActiveLanguage(langRoot);
            _langInfo.Text = $"lang 根：{langRoot}\n活动语言：{active ?? "（config.json 未指定）"} · 共 {files.Count} 个 JSON 文件" +
                             $"（只处理活动语言目录 + config.json）";
            _fileList.ItemsSource = files;
            _status.Text = "已加载。选中文件即可编辑（编辑只进内存编辑集，导出补丁时才写盘）。";
            RefreshActionButtons();
        }
        catch (Exception ex)
        {
            _status.Text = $"枚举 lang 文件失败：{ex.Message}";
        }
    }

    private async Task RunSearchAsync()
    {
        var query = _search.Text.Trim();
        if (query.Length == 0) { _hitList.ItemsSource = null; return; }
        var files = _files;
        try
        {
            var hits = await Task.Run(() => _service.Search(query, files));
            _hitList.ItemsSource = hits;
            _status.Text = hits.Count == 0 ? $"没有匹配「{query}」的文件名/键/值。" : $"匹配 {hits.Count} 条（点击可跳转文件）。";
        }
        catch (Exception ex) { _status.Text = $"搜索失败：{ex.Message}"; }
    }

    // ── 文件编辑 ─────────────────────────────────────────────────────

    private async Task LoadSelectedFileAsync()
    {
        var generation = ++_loadGeneration;
        _rows = [];
        _document = null;
        _kvList.ItemsSource = null;
        _valueBox.Text = string.Empty;
        _jsonBox.Text = string.Empty;
        if (_fileList.SelectedItem is not LangTextFileInfo file)
        {
            _selectedRelativePath = null;
            RefreshActionButtons();
            return;
        }
        _selectedRelativePath = file.RelativePath;
        _status.Text = "正在读取文件…";
        try
        {
            var text = await Task.Run(() => _service.BeginEdit(file.RelativePath));
            if (generation != _loadGeneration) return;
            ApplyDocument(text);
            _status.Text = $"{file.RelativePath}：{_rows.Count} 个可编辑键" + (_service.IsModified(file.RelativePath) ? "（已在编辑集中）" : string.Empty);
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return;
            _status.Text = $"读取失败：{ex.Message}";
        }
        RefreshActionButtons();
    }

    private void ApplyDocument(string jsonText)
    {
        _document = JsonNode.Parse(jsonText);
        _jsonBox.Text = jsonText;
        _rows = [];
        if (_document is not null) Flatten(_document, string.Empty, _document, null, -1, _rows);
        _kvList.ItemsSource = _rows;
    }

    /// <summary>把 JSON 展平为可编辑的键值行（对象键 / 数组下标；只列叶子可编辑）。</summary>
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
                rows.Add(new KvRow(path, Truncate(text, 120), value, parent, key, index));
                break;
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    private void RefreshValueEditor()
    {
        if (_kvList.SelectedItem is not KvRow row) { _valueBox.Text = string.Empty; return; }
        _valueBox.Text = row.Node is JsonValue value
            ? (value.TryGetValue<string>(out var str) ? str : value.ToJsonString())
            : row.Preview;
    }

    private async Task ApplyValueAsync()
    {
        if (_selectedRelativePath is null || _kvList.SelectedItem is not KvRow row) return;
        if (!row.IsLeaf)
        {
            _status.Text = "只能编辑叶子值（字符串/数字/布尔/null）；数组与对象请用「原始 JSON」编辑。";
            return;
        }
        try
        {
            var updated = SetNodeValue(row, _valueBox.Text);
            await PersistAsync(updated);
            _status.Text = $"已修改键 {row.Path}（未写盘，导出补丁时才落地）。";
            ReloadCurrentDocument();
        }
        catch (Exception ex) { _status.Text = $"应用修改失败：{ex.Message}"; }
    }

    private async Task DeleteKeyAsync()
    {
        if (_selectedRelativePath is null || _kvList.SelectedItem is not KvRow row || row.Parent is null) return;
        var confirm = MessageBox.Show(Window.GetWindow(this),
            $"删除键 {row.Path}？\n\n删除会以 RFC6902 remove 操作进入补丁（游戏加载器按补丁回放）。",
            "删除键", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            if (row.Parent is JsonObject obj && row.Key is { } key) obj.Remove(key);
            else if (row.Parent is JsonArray array && row.Index >= 0) array.RemoveAt(row.Index);
            await PersistAsync(_document!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _status.Text = $"已删除键 {row.Path}。";
            ReloadCurrentDocument();
        }
        catch (Exception ex) { _status.Text = $"删除失败：{ex.Message}"; }
    }

    /// <summary>按原值类型写回（数字/布尔/null 保持类型，字符串原样）。</summary>
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
        if (_selectedRelativePath is null) return;
        try
        {
            await PersistAsync(_jsonBox.Text);
            _status.Text = "原始 JSON 修改已进入编辑集（导出补丁时生成 RFC6902 操作）。";
            ReloadCurrentDocument();
        }
        catch (Exception ex) { _status.Text = $"保存失败：{ex.Message}"; }
    }

    private async Task PersistAsync(string jsonText)
    {
        var relativePath = _selectedRelativePath ?? throw new InvalidOperationException("未选中文件。");
        var text = jsonText;
        await Task.Run(() => _service.SetModified(relativePath, text));
    }

    private void ReloadCurrentDocument()
    {
        if (_selectedRelativePath is null) return;
        var text = _service.TryGetModifiedText(_selectedRelativePath);
        if (text is null) return;
        var selectedPath = (_kvList.SelectedItem as KvRow)?.Path;
        ApplyDocument(text);
        if (selectedPath is not null)
            _kvList.SelectedItem = _rows.FirstOrDefault(x => x.Path == selectedPath);
        RefreshActionButtons();
    }

    private async Task RevertFileAsync()
    {
        if (_selectedRelativePath is null) return;
        var reverted = _service.Revert(_selectedRelativePath);
        _status.Text = reverted ? $"已还原 {_selectedRelativePath}（移出编辑集；lang 目录从未被改动）。" : "该文件不在编辑集中。";
        await LoadSelectedFileAsync();
    }

    // ── 导出与直接应用 ───────────────────────────────────────────────

    private async Task ExportPatchAsync()
    {
        if (_service.EditedFiles.Count == 0)
        {
            _status.Text = "编辑集为空：先修改至少一个键再导出。";
            return;
        }
        var project = _host.Project;
        var modDirectory = _host.Env.EffectiveModDirectory(project);
        var defaultDirectory = !string.IsNullOrWhiteSpace(modDirectory) && Directory.Exists(modDirectory)
            ? modDirectory
            : _host.ProjectFile is null ? null : Path.Combine(Path.GetDirectoryName(_host.ProjectFile)!, "builds");
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "lang 补丁 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = (project?.Name is { Length: > 0 } name ? Sanitize(name) : "LME") + "-lang.json",
            InitialDirectory = defaultDirectory,
            Title = "导出 lang 补丁（放进模组目录后由加载器应用）",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var report = await Task.Run(() => _service.ExportPatch(dialog.FileName));
            var detail = string.Join("；", report.Files.Select(x =>
                x.OperationCount > 0 ? $"{x.RelativePath} → {x.OperationCount} 个操作" : $"{x.RelativePath} → {x.Note ?? "无差异"}"));
            _status.Text = $"补丁已导出：{report.OutputPath}（编辑 {report.EditedFileCount} 个文件，{report.PatchedFileCount} 个进入补丁）\n{detail}";
        }
        catch (Exception ex) { _status.Text = $"导出补丁失败：{ex.Message}"; }
    }

    private async Task ApplyToGameAsync()
    {
        var langRoot = _service.CurrentLangRoot;
        if (langRoot is null || _service.EditedFiles.Count == 0)
        {
            _status.Text = "编辑集为空或未定位 lang 根。";
            return;
        }
        var confirm = MessageBox.Show(Window.GetWindow(this),
            "这会直接把编辑集写入游戏 lang 目录（编辑器不负责备份/还原；真实加载器在启动/退出时才会 .bak 备份与还原）。\n\n" +
            "推荐做法是「导出 lang 补丁…」把补丁放进模组目录，由加载器应用。\n\n确定要直接写入游戏目录吗？",
            "直接应用到 lang 目录", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            var patchService = new LangTextPatchService();
            var report = await Task.Run(() =>
            {
                var output = Path.Combine(Path.GetTempPath(), $"lme-lang-apply-{Guid.NewGuid():N}.json");
                try
                {
                    var export = _service.ExportPatch(output);
                    var document = patchService.Read(export.OutputPath);
                    return string.Join("；", patchService.ApplyToDirectory(langRoot, document)
                        .Select(x => $"{x.RelativePath} → {(x.Applied ? $"{x.OperationCount} 个操作已应用" : x.Note ?? "未应用")}"));
                }
                finally { try { File.Delete(output); } catch (Exception) { /* 临时文件 */ } }
            });
            _status.Text = $"已直接应用到游戏 lang 目录：{report}";
        }
        catch (Exception ex) { _status.Text = $"直接应用失败：{ex.Message}"; }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "LME" : cleaned;
    }

    private void RefreshActionButtons()
    {
        var hasFile = _selectedRelativePath is not null;
        var hasLeaf = (_kvList.SelectedItem as KvRow)?.IsLeaf == true;
        _applyValue.IsEnabled = hasFile && hasLeaf;
        _deleteKey.IsEnabled = hasFile && _kvList.SelectedItem is KvRow;
        _saveJson.IsEnabled = hasFile;
        _revertFile.IsEnabled = hasFile && _service.IsModified(_selectedRelativePath!);
        _exportPatch.IsEnabled = _service.EditedFiles.Count > 0;
        _applyToGame.IsEnabled = _service.EditedFiles.Count > 0 && _service.CurrentLangRoot is not null;
    }
}
