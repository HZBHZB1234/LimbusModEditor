using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.Spine;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Formats.Unity;
using NLog;

namespace LimbusModEditor.App;

/// <summary>
/// plan-11：人格预设卡片流页面。
///
/// <para><b>它解决什么</b>：游戏里的资源是按「人格 id」互相关联的（lang 语音文件名、
/// static-data 数值表、bank 样本名、Unity 立绘/图标/CG/Spine 路径都带同一个 5 位 id）。
/// 关联图在启动扫描第 6 步从四个索引库派生（<c>cache/relation-index.db</c>），
/// 本页把它变成「一个人格一张卡」的<b>下滑卡片流</b>：卡片画人格立绘，点开看它关联的
/// 数据 / 文本 / 音频 / 图像 / Spine，并能一键跳到对应工作台继续编辑。</para>
///
/// <para><b>封面怎么来</b>：<see cref="RelationDisplayRules.PortraitRank"/> 给出的优先级
/// （<c>Sprite/Unit/Profile</c> → <c>UnitCgThumbnail</c> → <c>Unit/CG</c> …），
/// 取第一个能解码的；解码在后台按有限并发做，且 <c>DecodePixelWidth</c> 限制解码尺寸
/// —— 185 张卡全是原分辨率位图会把内存打爆（立绘单张可达数 MiB）。</para>
///
/// <para><b>不重复实现编辑</b>：本页只做「定位 + 跳转」，编辑仍在四个工作台里做
/// （文本编辑器 / 静态度量器 / bank 试听替换 / 资源替换都各自有完整实现），
/// 点「打开」就是 <c>IWorkbenchHost.ShowWorkbenchSearch</c>。</para>
/// </summary>
public sealed partial class PresetWorkbenchPage : UserControl
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IWorkbenchHost _host;
    private readonly PersonaPresetService _presets;
    private readonly SpinePreviewService _spinePreview;
    private readonly SpineExportService _spineExport;

    private readonly TextBox _search;
    private readonly ScrollViewer _cardScroll;
    private readonly WrapPanel _cardPanel;
    private readonly TextBlock _countText;
    private readonly StackPanel _detailPanel;
    private readonly DispatcherTimer _searchTimer;

    private readonly List<CardVisual> _cards = [];
    private CancellationTokenSource? _portraitCts;
    private bool _loaded;
    private int _buildGeneration;

    /// <summary>卡片常显尺寸（px）。封面正方形，标题两行封顶。</summary>
    private const double CardWidth = 168;
    private const double PortraitSize = 148;

    /// <summary>封面解码宽度上限（px）：卡片只有 148px，按它解码省内存。</summary>
    private const int PortraitDecodeWidth = 176;

    /// <summary>同时解码的封面数（立绘解码是 CPU 密集的，太高反而拖慢 UI 线程的贴图上传）。</summary>
    private const int PortraitConcurrency = 4;

    private sealed record CardVisual(Button Root, Border Frame, Image Portrait, TextBlock Placeholder, string SubjectId);

    public PresetWorkbenchPage(IWorkbenchHost host)
    {
        _host = host;
        InitializeComponent();

        _presets = new PersonaPresetService(new RelationQueryService(new RelationStore(host.Env.CacheDirectory)));
        _spinePreview = new SpinePreviewService(() =>
            (IReadOnlyList<AssetRecord>?)host.Project?.Assets ?? Array.Empty<AssetRecord>());
        _spineExport = new SpineExportService(_spinePreview);

        // 搜索行：关键词过滤卡片（人格 id / 中文名 / 角色 token）。先建节流器再接事件，避免构造期触发空引用。
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RebuildCards(); };
        _search = WorkbenchShell.CreateSearchBox("按人格 id / 名字过滤卡片（如 10201、浮士德、Faust）");
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        Shell.AddSearchItem(_search);
        Shell.AddSearchItem(WorkbenchShell.CreateButton("↻ 重新分析", (_, _) => RebuildCards()));
        _countText = WorkbenchShell.CreateSectionLabel("—");
        Shell.AddSearchItem(_countText);

        // 浏览列：下滑卡片流（WrapPanel 自动换行 + 纵向滚动）。
        _cardPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        _cardScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(4),
            Content = _cardPanel,
        };
        WorkbenchShell.EnableWheelScrolling(_cardScroll);
        Shell.SetBrowseContent(_cardScroll);

        // 详情列：点卡片后填充。
        _detailPanel = new StackPanel();
        Shell.SetEditContent(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _detailPanel,
        });

        Shell.SetStatus("—");
        Shell.SetEmptyHint("点「获取资源」完成启动扫描后，这里会自动列出全部人格卡片。");

        Loaded += (_, _) => { if (!_loaded) { _loaded = true; RebuildCards(); } };
        Log.Info("人格卡片流页面构造完成：关系库={0}", host.Env.CacheDirectory);
    }

    /// <summary>宿主在项目状态刷新时调用（打开/保存/重扫后重建卡片）。</summary>
    public void OnProjectRefreshed()
    {
        if (!_loaded) return;
        RebuildCards();
    }

    // ── 卡片流 ───────────────────────────────────────────────────────

    private void RebuildCards()
    {
        var generation = ++_buildGeneration;
        _portraitCts?.Cancel();
        _portraitCts?.Dispose();
        _portraitCts = null;
        _cardPanel.Children.Clear();
        _cards.Clear();
        ShowDetailPlaceholder("左侧点一张卡，这里显示该人格关联的全部资源。");

        var project = _host.Project;
        if (project is null)
        {
            _countText.Text = "—";
            Shell.SetEmptyHint("还没有打开项目：先「新建 / 打开项目」并扫描资源。");
            Shell.SetStatus("没有项目");
            return;
        }
        if (!_presets.IsReady)
        {
            _countText.Text = "—";
            Shell.SetEmptyHint("关联图还没建立：启动扫描的最后一步会从四个索引库派生它（cache/relation-index.db）。");
            Shell.SetStatus("关联图未就绪");
            return;
        }

        var keyword = _search.Text;
        var assets = (IReadOnlyList<AssetRecord>)project.Assets;
        IReadOnlyList<PresetCard> cards;
        try
        {
            cards = _presets.BuildCards(assets, keyword);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "构建人格卡片失败");
            _countText.Text = "—";
            Shell.SetEmptyHint($"构建卡片失败：{ex.Message}");
            return;
        }
        if (generation != _buildGeneration) return;

        foreach (var card in cards) _cardPanel.Children.Add(BuildCard(card));
        _countText.Text = cards.Count == 0 ? "没有匹配的人格" : $"{cards.Count} 张卡片";
        Shell.SetEmptyHint(cards.Count == 0 ? "没有匹配的人格卡片：换个关键词，或确认关联图已建好。" : null);
        Shell.SetStatus($"共 {cards.Count} 个人格卡片"
            + (string.IsNullOrWhiteSpace(keyword) ? "。" : $"（已按「{keyword}」过滤）。"));

        // 封面异步加载（有限并发 + 可取消：切页/改关键词会取消上一轮）。
        var cts = new CancellationTokenSource();
        _portraitCts = cts;
        var bySubject = cards.ToDictionary(c => c.SubjectId, c => c.PortraitAsset, StringComparer.Ordinal);
        _ = LoadPortraitsAsync(_cards.ToArray(), bySubject, cts.Token);
    }

    private Button BuildCard(PresetCard card)
    {
        var portrait = new Image
        {
            Stretch = Stretch.Uniform,
            Width = PortraitSize,
            Height = PortraitSize,
        };
        var placeholder = new TextBlock
        {
            Text = Initials(card),
            FontSize = 34,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = WbBrush("WbTextMutedBrush"),
        };
        var frame = new Border
        {
            Width = PortraitSize,
            Height = PortraitSize,
            CornerRadius = new CornerRadius(6),
            Background = WbBrush("WbPanelBackgroundBrush"),
            BorderBrush = WbBrush("WbBorderLineBrush"),
            BorderThickness = new Thickness(1),
            Child = new Grid { Children = { portrait, placeholder } },
        };

        var stack = new StackPanel { Width = PortraitSize };
        stack.Children.Add(frame);
        stack.Children.Add(new TextBlock
        {
            Text = card.Title,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        if (!string.IsNullOrWhiteSpace(card.Summary))
            stack.Children.Add(new TextBlock
            {
                Text = card.Summary,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 10.5,
                Opacity = 0.75,
            });

        var button = new Button
        {
            Content = stack,
            Width = CardWidth,
            Margin = new Thickness(6),
            Padding = new Thickness(8),
            ToolTip = $"{card.Title}\n{card.LinkCount} 个关联资源\n{card.Summary}",
        };
        button.Click += (_, _) => ShowDetail(card.SubjectId);
        var visual = new CardVisual(button, frame, portrait, placeholder, card.SubjectId);
        _cards.Add(visual);
        // Placeholder 的可见性由「有没有封面资源」决定：有封面就先藏起来等图片。
        placeholder.Visibility = card.PortraitAsset is null ? Visibility.Visible : Visibility.Hidden;
        return button;
    }

    /// <summary>没有立绘时的占位字：取标题里「· 」之后的第一段（通常是中文角色名）首字。</summary>
    private static string Initials(PresetCard card)
    {
        var name = card.Title;
        var marker = name.IndexOf("· ", StringComparison.Ordinal);
        if (marker >= 0) name = name[(marker + 2)..];
        name = name.Trim();
        return name.Length == 0 ? "?" : name[..1];
    }

    /// <summary>
    /// 后台按有限并发解码封面并贴到卡片上。取消（改关键词 / 切页 / 关窗）后不再贴图。
    /// </summary>
    /// <param name="cards">本轮的卡片视觉对象。</param>
    /// <param name="bySubject">subjectId → 卡片封面资源（由 <see cref="RebuildCards"/> 一并给出，避免二次查库）。</param>
    private async Task LoadPortraitsAsync(
        IReadOnlyList<CardVisual> cards,
        IReadOnlyDictionary<string, AssetRecord?> bySubject,
        CancellationToken cancellationToken)
    {
        using var scope = Log.Scope("加载人格封面");
        var targets = cards.Where(c => c.Placeholder.Visibility == Visibility.Hidden).ToArray();
        if (targets.Length == 0)
        {
            Log.Debug("封面加载跳过：卡片 {0} 张都没有封面候选", cards.Count);
            return;
        }

        using var gate = new SemaphoreSlim(PortraitConcurrency);
        var service = new UnityAssetService();
        var loaded = 0;
        var tasks = targets.Select(async target =>
        {
            if (!bySubject.TryGetValue(target.SubjectId, out var asset) || asset is null) return;
            if (string.IsNullOrWhiteSpace(asset.SourcePath) || asset.UnityPathId is null) return;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var png = await Task.Run(
                    () => SafeReadTexturePng(service, asset, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (png is null || png.Length == 0) return;
                var bitmap = await Task.Run(() => Decode(png), cancellationToken).ConfigureAwait(false);
                if (bitmap is null) return;
                if (cancellationToken.IsCancellationRequested) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    target.Portrait.Source = bitmap;
                    target.Placeholder.Visibility = Visibility.Collapsed;
                }, DispatcherPriority.Background);
                Interlocked.Increment(ref loaded);
            }
            catch (OperationCanceledException) { /* 正常取消 */ }
            catch (Exception ex)
            {
                Log.Warn(ex, "人格封面加载失败：subject={0}", target.SubjectId);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
            Log.Info("人格封面加载完成：成功 {0} / 候选 {1} 张", loaded, targets.Length);
        }
        catch (OperationCanceledException)
        {
            Log.Debug("人格封面加载被取消（改关键词 / 切页）：已完成 {0} 张", loaded);
        }
    }

    private static byte[]? SafeReadTexturePng(UnityAssetService service, AssetRecord asset, CancellationToken cancellationToken)
    {
        try
        {
            return service.ReadTexturePng(asset.SourcePath!, asset.UnityPathId!.Value, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            Log.Debug(ex, "封面贴图解码失败：{0}", AssetDisplay.DisplayPath(asset));
            return null;
        }
    }

    /// <summary>按卡片尺寸解码 PNG（限制解码宽度，避免把原分辨率立绘整张装进内存）。</summary>
    private static BitmapImage? Decode(byte[] png)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = PortraitDecodeWidth;
            bitmap.StreamSource = new MemoryStream(png);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
        {
            return null;
        }
    }

    // ── 详情列 ───────────────────────────────────────────────────────

    private void ShowDetailPlaceholder(string message)
    {
        _detailPanel.Children.Clear();
        _detailPanel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(2, 6, 2, 0),
        });
    }

    private void ShowDetail(string subjectId)
    {
        var project = _host.Project;
        _detailPanel.Children.Clear();
        if (project is null) { ShowDetailPlaceholder("没有打开项目。"); return; }

        var subject = _presets.Subjects().FirstOrDefault(x => string.Equals(x.SubjectId, subjectId, StringComparison.Ordinal));
        var title = subject is null ? subjectId : $"{subject.SubjectId} · {subject.DisplayName}";
        _detailPanel.Children.Add(WorkbenchShell.CreatePanelTitle(title));
        if (subject is not null && !string.IsNullOrWhiteSpace(subject.Character))
            _detailPanel.Children.Add(WorkbenchShell.CreateSectionLabel(
                $"角色 token：{subject.Character}" + (string.IsNullOrWhiteSpace(subject.Subtitle) ? string.Empty : $" · 风格：{subject.Subtitle}"),
                new Thickness(0, 2, 0, 6)));

        var openAll = WorkbenchShell.CreateButton("在资源列表里搜索该人格 id",
            (_, _) => _host.ShowWorkbenchSearch(WorkbenchPageKeys.Assets, subjectId));
        openAll.Margin = new Thickness(0, 4, 0, 10);
        _detailPanel.Children.Add(openAll);

        IReadOnlyList<PresetDetailGroup> groups;
        try
        {
            groups = _presets.BuildDetail(subjectId, (IReadOnlyList<AssetRecord>)project.Assets);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "构建人格详情失败：{0}", subjectId);
            ShowDetailPlaceholder($"构建详情失败：{ex.Message}");
            return;
        }

        if (groups.Count == 0)
        {
            _detailPanel.Children.Add(new TextBlock
            {
                Text = "该人格没有任何已登记的资源（关联图里它只有身份、没有链接）。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
            });
            Shell.SetStatus($"{title}：0 类资源");
            return;
        }

        var total = groups.Sum(g => g.Rows.Count);
        Shell.SetStatus($"{title}：{groups.Count} 类 / {total} 个关联资源");
        foreach (var group in groups) _detailPanel.Children.Add(BuildGroup(group));
    }

    private UIElement BuildGroup(PresetDetailGroup group)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(WorkbenchShell.CreateSectionLabel(
            $"{group.Title}（{group.Rows.Count}）", new Thickness(0, 0, 0, 4)));
        var shown = 0;
        foreach (var row in group.Rows)
        {
            if (shown++ >= 200)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"…还有 {group.Rows.Count - 200} 项（用上面的「打开」到对应工作台看全部）",
                    Opacity = 0.7,
                    FontSize = 10.5,
                });
                break;
            }
            panel.Children.Add(BuildRow(group.Kind, row));
        }
        return panel;
    }

    private UIElement BuildRow(RelationKind kind, PresetDetailRow row)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = row.Display, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(row.Detail))
            text.Children.Add(new TextBlock { Text = row.Detail, Opacity = 0.7, FontSize = 10.5, TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(text);

        var open = WorkbenchShell.CreateButton("打开", (_, _) =>
            _host.ShowWorkbenchSearch(PersonaPresetService.WorkbenchKeyFor(kind), PersonaPresetService.SearchKeywordFor(row)));
        open.Margin = new Thickness(4, 0, 0, 0);
        open.ToolTip = "切到对应工作台并按这个资源名过滤。";
        Grid.SetColumn(open, 1);
        grid.Children.Add(open);

        // Spine 行额外给一个「导出」：把骨架 / 图集 / 贴图取出来交给外部 Spine 工具看。
        if (kind == RelationKind.Spine && row.Asset is not null)
        {
            var export = WorkbenchShell.CreateButton("导出…", (_, _) => ExportSpine(row.Asset!));
            export.Margin = new Thickness(4, 0, 0, 0);
            export.ToolTip = "把该 Spine 资源（骨架 / 图集 / 图集贴图）导出到一个目录，供外部 Spine 工具查看。";
            Grid.SetColumn(export, 2);
            grid.Children.Add(export);
        }
        return grid;
    }

    private void ExportSpine(AssetRecord asset)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            CheckFileExists = false,
            ValidateNames = false,
            FileName = "选择此文件夹",
            Title = "选择 Spine 资源的导出目录（会在其中建以资源目录名命名的子文件夹）",
            InitialDirectory = Directory.Exists(_host.Env.ConfigDirectory)
                ? Directory.GetParent(_host.Env.ConfigDirectory)?.FullName ?? _host.Env.ConfigDirectory
                : null,
        };
        if (dialog.ShowDialog() != true) { Log.Info("Spine 导出：用户在选目录对话框取消"); return; }
        var root = Path.GetDirectoryName(dialog.FileName);
        if (string.IsNullOrWhiteSpace(root)) { Log.Warn("Spine 导出：选中的路径取不到目录（{0}）", dialog.FileName); return; }

        _ = RunSpineExportAsync(asset, root);
    }

    private async Task RunSpineExportAsync(AssetRecord asset, string root)
    {
        Shell.SetStatus($"正在导出 Spine 资源到 {root}…");
        try
        {
            var result = await _spineExport.ExportAsync(asset, root).ConfigureAwait(true);
            Shell.SetStatus(result.Detail);
            MessageBox.Show(Window.GetWindow(this), result.Detail, "Spine 资源导出",
                MessageBoxButton.OK, result.FileCount > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Spine 资源导出失败：{0}", AssetDisplay.DisplayPath(asset));
            Shell.SetStatus($"Spine 导出失败：{ex.Message}");
            MessageBox.Show(Window.GetWindow(this), $"导出失败：{ex.Message}", "Spine 资源导出",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── 小工具 ───────────────────────────────────────────────────────

    /// <summary>取共享色板画笔（取不到退回透明，绝不抛）。</summary>
    private static Brush WbBrush(string key)
        => System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
}
