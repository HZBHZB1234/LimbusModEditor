using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.App;

/// <summary>
/// 音频工作台（plan-06）：按 FMOD Bank 浏览/试听/替换音频样本，并导出整包
/// .bank 或 .rebank 到<b>模组目录</b>（绝不写游戏目录）。布局与资源工作台同骨架
/// （浏览列 + GridSplitter + 预览/编辑列）。
/// </summary>
public sealed class BankWorkbenchPage : UserControl
{
    private readonly IWorkbenchHost _host;
    private readonly BankDirectoryService _banks = new();
    private readonly ModExportService _exporter = new(BuiltInFormatRegistry.Create());
    private readonly List<BankFileEntry> _all = [];
    private readonly List<SampleRow> _samples = [];

    private readonly TextBox _search;
    private readonly ComboBox _kindFilter;
    private readonly ListView _bankList;
    private readonly ListView _sampleList;
    private readonly TextBlock _bankInfo;
    private readonly TextBlock _sampleInfo;
    private readonly TextBlock _status;
    private readonly Button _auditionButton;
    private readonly Button _exportWavButton;
    private readonly Button _replaceButton;
    private readonly Button _exportBankButton;
    private readonly Button _exportRebankButton;

    private System.Windows.Media.MediaPlayer? _player;
    private string? _playerFile;
    private string? _bankDirectory;
    private BankFileEntry? _selectedBank;
    private BankSampleTable? _table;
    private int _loadGeneration;

    private sealed record SampleRow(int FsbIndex, int SampleIndex, string Name, string Codec, int SampleRate, int Channels, uint SampleCount, long? Size)
    {
        public string SizeLabel => Size is { } size ? $"{size:N0}" : "—";
        public string DurationLabel => SampleRate > 0 ? $"{SampleCount / (double)SampleRate:0.##} 秒" : "—";
    }

    public BankWorkbenchPage(IWorkbenchHost host)
    {
        _host = host;

        // ── 浏览列 ────────────────────────────────────────────────────
        _search = new TextBox { Width = 220, Height = 28, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "按 bank 文件名搜索" };
        _search.TextChanged += (_, _) => ApplyFilter();
        _kindFilter = new ComboBox { Width = 120, Height = 28, Margin = new Thickness(8, 0, 0, 0) };
        _kindFilter.Items.Add("全部类型");
        _kindFilter.Items.Add("事件 bank");
        _kindFilter.Items.Add("音频 bank");
        _kindFilter.Items.Add("加密 bank");
        _kindFilter.Items.Add("无法识别");
        _kindFilter.SelectedIndex = 0;
        _kindFilter.SelectionChanged += (_, _) => ApplyFilter();
        var rescan = new Button { Content = "重新扫描", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        rescan.Click += async (_, _) => await RefreshAsync();

        var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        searchRow.Children.Add(_search);
        searchRow.Children.Add(_kindFilter);
        searchRow.Children.Add(rescan);

        _bankList = new ListView
        {
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x40)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF2)),
            SelectionMode = SelectionMode.Single,
        };
        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "bank 文件", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(BankFileEntry.FileName)), Width = 300 });
        grid.Columns.Add(new GridViewColumn { Header = "类型", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(BankFileEntry.KindLabel)), Width = 80 });
        grid.Columns.Add(new GridViewColumn { Header = "FSB 数", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(BankFileEntry.FsbCount)), Width = 60 });
        grid.Columns.Add(new GridViewColumn { Header = "大小", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(BankFileEntry.FileSizeBytes)), Width = 100 });
        _bankList.View = grid;
        _bankList.SelectionChanged += async (_, _) => await LoadSelectedBankAsync();

        var browsePanel = new DockPanel();
        DockPanel.SetDock(searchRow, Dock.Top);
        browsePanel.Children.Add(searchRow);
        browsePanel.Children.Add(_bankList);

        var browseBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1D, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x40)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 8, 0),
            Child = browsePanel,
        };

        // ── 预览/编辑列 ───────────────────────────────────────────────
        _bankInfo = new TextBlock { Text = "未选择 bank", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        _sampleList = new ListView
        {
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x40)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF2)),
            Height = 220,
            SelectionMode = SelectionMode.Single,
        };
        var sampleGrid = new GridView();
        sampleGrid.Columns.Add(new GridViewColumn { Header = "样本", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(SampleRow.Name)), Width = 180 });
        sampleGrid.Columns.Add(new GridViewColumn { Header = "codec", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(SampleRow.Codec)), Width = 70 });
        sampleGrid.Columns.Add(new GridViewColumn { Header = "采样率", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(SampleRow.SampleRate)), Width = 70 });
        sampleGrid.Columns.Add(new GridViewColumn { Header = "声道", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(SampleRow.Channels)), Width = 50 });
        sampleGrid.Columns.Add(new GridViewColumn { Header = "时长", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(SampleRow.DurationLabel)), Width = 70 });
        sampleGrid.Columns.Add(new GridViewColumn { Header = "大小", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(SampleRow.SizeLabel)), Width = 90 });
        _sampleList.View = sampleGrid;
        _sampleList.SelectionChanged += (_, _) => RefreshSampleButtons();

        _auditionButton = MakeButton("▶ 试听样本", async (_, _) => await AuditionAsync());
        _exportWavButton = MakeButton("导出样本 WAV…", async (_, _) => await ExportSampleWavAsync());
        _replaceButton = MakeButton("用 WAV 替换…", async (_, _) => await ReplaceSampleAsync());
        _exportBankButton = MakeButton("导出整包 .bank…", async (_, _) => await ExportAsync(ModFormatKind.Bank));
        _exportRebankButton = MakeButton("导出 .rebank…", async (_, _) => await ExportAsync(ModFormatKind.Rebank));
        var buttonRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in new[] { _auditionButton, _exportWavButton, _replaceButton, _exportBankButton, _exportRebankButton })
        {
            button.Margin = new Thickness(0, 0, 6, 6);
            buttonRow.Children.Add(button);
        }
        _sampleInfo = new TextBlock { Text = "—", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x93, 0xA4)), Margin = new Thickness(0, 6, 0, 0) };
        _status = new TextBlock { Text = "—", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x93, 0xA4)), Margin = new Thickness(0, 8, 0, 0) };

        var editPanel = new StackPanel();
        editPanel.Children.Add(new TextBlock { Text = "bank / 样本", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        editPanel.Children.Add(_bankInfo);
        editPanel.Children.Add(_sampleList);
        editPanel.Children.Add(buttonRow);
        editPanel.Children.Add(_sampleInfo);
        editPanel.Children.Add(_status);
        var editBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1D, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x40)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = new ScrollViewer { Content = editPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };

        // ── 三列骨架（与资源工作台同款 splitter）──────────────────────
        var layout = new Grid { Margin = new Thickness(12) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        var previewColumn = new ColumnDefinition { Width = new GridLength(360), MinWidth = 260 };
        layout.ColumnDefinitions.Add(previewColumn);
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
            ToolTip = "拖拽调整预览区宽度；双击复位默认占比。",
        };
        splitter.MouseDoubleClick += (_, _) => previewColumn.Width = new GridLength(360);
        Grid.SetColumn(splitter, 1);
        layout.Children.Add(splitter);
        Grid.SetColumn(editBorder, 2);
        layout.Children.Add(editBorder);

        Content = layout;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private static Button MakeButton(string text, RoutedEventHandler handler)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), IsEnabled = false };
        button.Click += handler;
        return button;
    }

    // ── 扫描与筛选 ───────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        var gameDirectory = _host.Env.EffectiveGameDirectory(_host.Project);
        _bankDirectory = _banks.ResolveBankDirectory(gameDirectory);
        if (_bankDirectory is null)
        {
            _status.Text = "没有找到 FMOD bank 目录。请在「设置」页填写游戏目录（bank 目录为 " +
                           string.Join("/", BankDirectoryService.BankRelativePath) + "）。";
            _all.Clear();
            ApplyFilter();
            return;
        }
        _status.Text = "正在扫描 bank 目录…";
        try
        {
            var entries = await Task.Run(() => _banks.ScanDirectory(_bankDirectory!));
            _all.Clear();
            _all.AddRange(entries);
            ApplyFilter();
            var audio = entries.Count(x => x.Kind == BankKind.Audio);
            var events = entries.Count(x => x.Kind == BankKind.Event);
            var other = entries.Count - audio - events;
            _status.Text = $"已扫描 {entries.Count} 个 bank：音频 {audio} · 事件 {events} · 其他 {other}（引用模式，未复制任何文件）";
        }
        catch (Exception ex)
        {
            _status.Text = $"扫描 bank 目录失败：{ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        var text = _search.Text.Trim();
        var kind = _kindFilter.SelectedIndex switch
        {
            1 => (BankKind?)BankKind.Event,
            2 => BankKind.Audio,
            3 => BankKind.Encrypted,
            4 => BankKind.Unknown,
            _ => null,
        };
        var filtered = _all.Where(x =>
            (text.Length == 0 || x.FileName.Contains(text, StringComparison.OrdinalIgnoreCase)) &&
            (kind is null || x.Kind == kind)).ToList();
        _bankList.ItemsSource = filtered;
        _bankInfo.Text = filtered.Count == _all.Count
            ? $"共 {_all.Count} 个 bank"
            : $"{filtered.Count} / {_all.Count} 个 bank";
    }

    // ── 样本表 ───────────────────────────────────────────────────────

    private async Task LoadSelectedBankAsync()
    {
        var generation = ++_loadGeneration;
        StopAudio();
        _samples.Clear();
        _sampleList.ItemsSource = null;
        _table = null;
        RefreshSampleButtons();
        if (_bankList.SelectedItem is not BankFileEntry entry)
        {
            _bankInfo.Text = "未选择 bank";
            _sampleInfo.Text = "—";
            return;
        }
        _selectedBank = entry;
        _bankInfo.Text = $"{entry.FileName} · {entry.KindLabel} · {entry.FileSizeBytes:N0} 字节 · FSB {entry.FsbCount} 个" +
                         (entry.Detail is null ? string.Empty : $"\n{entry.Detail}");
        _sampleInfo.Text = "正在读取样本表…";
        try
        {
            var table = await Task.Run(() => _banks.ReadSampleTable(entry.FullPath));
            if (generation != _loadGeneration) return;
            _table = table;
            foreach (var fsbTable in table.FsbTables)
            {
                if (fsbTable.Fsb is null)
                {
                    _samples.Add(new SampleRow(fsbTable.FsbIndex, -1,
                        $"（FSB {fsbTable.FsbIndex} 无法解析）", "—", 0, 0, 0, fsbTable.Size));
                    continue;
                }
                var sampleIndex = 0;
                foreach (var sample in fsbTable.Fsb.Samples)
                {
                    _samples.Add(new SampleRow(fsbTable.FsbIndex, sampleIndex++,
                        string.IsNullOrWhiteSpace(sample.Name) ? $"FSB {fsbTable.FsbIndex} 样本 {sampleIndex - 1}" : sample.Name!,
                        fsbTable.Fsb.CodecName, sample.SampleRate, sample.Channels, sample.SampleCount, sample.DataSize));
                }
            }
            _sampleList.ItemsSource = _samples;
            _sampleInfo.Text = table.Kind switch
            {
                BankKind.Event => $"事件 bank：无 FSB 负载（SNDH 表为空）。RIFF 顶层块：{string.Join("、", table.RiffChunks.Select(x => x.FourCc))}",
                BankKind.Audio => $"音频 bank：{table.FsbTables.Count} 个 FSB · {_samples.Count} 个样本（codec 为 bank 级字段）",
                _ => table.Note ?? "（未识别）",
            };
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return;
            _sampleInfo.Text = $"读取样本表失败：{ex.Message}";
        }
        RefreshSampleButtons();
    }

    private SampleRow? SelectedSample => _sampleList.SelectedItem as SampleRow;

    private void RefreshSampleButtons()
    {
        var sample = SelectedSample;
        var hasSample = sample is { SampleIndex: >= 0 };
        var hasFmod = _host.Env.EffectiveFmodLibraryDirectory(_host.Project) is { } dir && Directory.Exists(dir);
        _auditionButton.IsEnabled = hasSample && hasFmod;
        _exportWavButton.IsEnabled = hasSample && hasFmod;
        _replaceButton.IsEnabled = hasSample && _host.Project is not null && _host.ProjectFile is not null;
        _exportBankButton.IsEnabled = _selectedBank is not null && _host.Project is not null;
        _exportRebankButton.IsEnabled = _selectedBank is not null && _host.Project is not null;
    }

    // ── 试听 / 导出样本 ──────────────────────────────────────────────

    private async Task AuditionAsync()
    {
        if (_selectedBank is null || SelectedSample is not { SampleIndex: >= 0 } sample) return;
        if (_player is not null) { StopAudio(); _status.Text = "已停止试听。"; return; }
        var fmodDirectory = _host.Env.EffectiveFmodLibraryDirectory(_host.Project);
        if (string.IsNullOrWhiteSpace(fmodDirectory) || !Directory.Exists(fmodDirectory))
        {
            _status.Text = "需要 FMOD DLL 才能解码试听：把 fmod64.dll 放进程序目录 fmod\\，或在「设置」页指定目录。";
            return;
        }
        _status.Text = "正在解码…";
        try
        {
            var wav = await Task.Run(() =>
            {
                var fsb = ExtractFsb(_selectedBank.FullPath, sample.FsbIndex);
                using var codec = new NativeFmodAudioCodec(fmodDirectory);
                return codec.DecodeFsbToWaveAsync(fsb, sample.SampleIndex).GetAwaiter().GetResult();
            });
            var file = Path.Combine(Path.GetTempPath(), $"lme-bank-{Guid.NewGuid():N}.wav");
            await File.WriteAllBytesAsync(file, wav);
            var player = new System.Windows.Media.MediaPlayer();
            player.MediaEnded += (_, _) => StopAudio();
            player.MediaFailed += (_, args) => { StopAudio(); _status.Text = $"播放失败：{args.ErrorException?.Message ?? "未知原因"}"; };
            player.Open(new Uri(file));
            _player = player;
            _playerFile = file;
            player.Play();
            _status.Text = $"正在播放 {sample.Name}（WAV {wav.Length / 1024} KB）";
        }
        catch (Exception ex) { StopAudio(); _status.Text = $"试听失败：{ex.Message}"; }
    }

    private async Task ExportSampleWavAsync()
    {
        if (_selectedBank is null || SelectedSample is not { SampleIndex: >= 0 } sample) return;
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
                var fsb = ExtractFsb(_selectedBank.FullPath, sample.FsbIndex);
                using var codec = new NativeFmodAudioCodec(fmodDirectory);
                return codec.DecodeFsbToWaveAsync(fsb, sample.SampleIndex).GetAwaiter().GetResult();
            });
            await File.WriteAllBytesAsync(dialog.FileName, wav);
            _status.Text = $"已导出：{dialog.FileName}";
        }
        catch (Exception ex) { _status.Text = $"导出 WAV 失败：{ex.Message}"; }
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
        if (project is null || _host.ProjectFile is null || _selectedBank is null || SelectedSample is not { SampleIndex: >= 0 } sample)
        {
            _status.Text = "请先打开项目并选中一个样本。";
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
            _status.Text = "替换已登记到项目。导出时会用 FSBANK 把 WAV 重新编码进 bank；" +
                           "缺少 fsbank64.dll 时导出会明确报错（不会静默跳过）。";
        }
        catch (Exception ex) { _status.Text = $"替换失败：{ex.Message}"; }
    }

    /// <summary>把 bank 复制进项目（引用模式 + 实体化，与 Unity 缓存同策略），
    /// 并登记为项目 Source（Format=Bank），使既有导出管道可直接处理。</summary>
    private static string MaterializeBank(ModProject project, BankFileEntry bank, string projectDirectory)
    {
        var targetDirectory = Path.Combine(projectDirectory, "sources", "banks");
        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, bank.FileName);
        if (!File.Exists(target) || new FileInfo(target).Length != bank.FileSizeBytes)
            File.Copy(bank.FullPath, target, overwrite: true);
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
    private static AssetRecord EnsureSampleAsset(ModProject project, BankFileEntry bank, SampleRow sample, string bankCopy)
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
            Size = sample.Size ?? 0,
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
            _status.Text = "请先打开项目并选择一个 bank（导出走项目源管道）。";
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
        var fmodDirectory = _host.Env.EffectiveFmodLibraryDirectory(project);
        try
        {
            using NativeFmodAudioCodec? codec = !string.IsNullOrWhiteSpace(fmodDirectory) && Directory.Exists(fmodDirectory)
                ? new NativeFmodAudioCodec(fmodDirectory)
                : null;
            _status.Text = "正在导出…";
            var result = await _exporter.ExportWithEditsAsync(bankCopy, project, dialog.FileName, format, codec);
            _status.Text = $"导出完成：{result.AppliedReplacements} 个替换 → {result.OutputPath}";
            if (result.AssetStatuses.Count > 0)
            {
                var skipped = result.AssetStatuses.Count(x => x.Status != ExportAssetStatus.Applied);
                if (skipped > 0) _status.Text += $"（{skipped} 条未应用，详见导出报告）";
            }
            new ExportReportWindow(result) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex) { _status.Text = $"导出失败：{ex.Message}"; }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "sample" : cleaned;
    }

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
