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
/// plan-11：<b>预设对象</b>卡片流页面（人格 / 敌人单位 / 异想体 / 播报员 / E.G.O 装备 / E.G.O 饰品）。
///
/// <para><b>它解决什么</b>：游戏里的资源是按对象 id 互相关联的（lang 语音文件名、
/// static-data 数值表、bank 样本名、Unity 立绘/图标/CG/Spine 路径都带同一个 id）。
/// 关联图在启动扫描第 6 步从四个索引库派生（<c>cache/relation-index.db</c>），
/// 本页把它变成「一个对象一张卡」的<b>下滑卡片流</b>：卡片画封面 + 一句代表内容，
/// 点开看它关联的数据 / 文本 / 音频 / 图像 / Spine，并能<b>精确跳到那一行</b>继续处理。</para>
///
/// <para><b>为什么不是「只有人格」</b>：六个类别在数据层就是同一张 <c>subjects</c> 表，
/// 只是 <c>subject_kind</c> 不同——所以共用同一条卡片流，靠顶部的类别下拉切换
/// （只列真的有内容的类别，见 <see cref="PresetWorkbenchService.Categories"/>），
/// 不另开页面、也不各自重造一套卡片。</para>
///
/// <para><b>封面怎么来</b>：<see cref="RelationDisplayRules.CoverCandidateRank"/> 给出的优先级
/// （<c>Sprite/Unit/Profile</c> → <c>UnitCgThumbnail</c> → <c>Unit/CG</c> …，
/// 且同路径优先 <c>Texture</c> 记录）取第一个能解码的；解码在后台按有限并发做，
/// 且 <c>DecodePixelWidth</c> 限制解码尺寸——上千张卡全是原分辨率位图会把内存打爆。</para>
///
/// <para><b>不重复实现编辑</b>：本页只做「定位 + 跳转」与「就地预览」，编辑仍在四个工作台里做。
/// 「打开」走 <see cref="IWorkbenchHost.RevealReference"/>：目标页能精确选中那一行就选中，
/// 不能就退化为关键词过滤（见 <see cref="IReferenceRevealable"/>）。</para>
/// </summary>
public sealed partial class PresetWorkbenchPage : UserControl
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IWorkbenchHost _host;
    private readonly PresetWorkbenchService _presets;
    private readonly SpinePreviewService _spinePreview;
    private readonly SpineExportService _spineExport;
    private readonly SpineAnimationSourceService _spineAnimationSource;

    private readonly TextBox _search;
    private readonly ComboBox _categoryBox;
    private readonly ScrollViewer _cardScroll;
    private readonly WrapPanel _cardPanel;
    private readonly TextBlock _countText;
    private readonly StackPanel _detailPanel;
    private readonly DispatcherTimer _searchTimer;

    private readonly List<CardVisual> _cards = [];
    private CancellationTokenSource? _portraitCts;
    private CancellationTokenSource? _thumbnailCts;
    private bool _loaded;
    private int _buildGeneration;

    /// <summary>当前类别筛选（null = 全部类别）；与 <see cref="_categoryBox"/> 的候选项下标一一对应。</summary>
    private string? _category;

    /// <summary>类别下拉每个下标对应的类别键（下标 0 固定是「全部类别」→ null）。</summary>
    private string?[] _categoryKeys = [null];

    /// <summary>卡片常显尺寸（px）。封面正方形，标题两行封顶。</summary>
    private const double CardWidth = 186;
    private const double PortraitSize = 166;

    /// <summary>详情里一行缩略图的边长（px）。</summary>
    private const double ThumbnailSize = 56;

    /// <summary>封面解码宽度上限（px）：卡片只有 166px，按它解码省内存。</summary>
    private const int PortraitDecodeWidth = 196;

    /// <summary>详情缩略图解码宽度上限（px）。</summary>
    private const int ThumbnailDecodeWidth = 64;

    /// <summary>同时解码的封面数（立绘解码是 CPU 密集的，太高反而拖慢 UI 线程的贴图上传）。</summary>
    private const int PortraitConcurrency = 4;

    /// <summary>详情里每组的行数上限：语音组动辄上千条，全量建控件会把 UI 吊死。</summary>
    private const int MaxRowsPerGroup = 200;

    private sealed record CardVisual(Button Root, Border Frame, Image Portrait, TextBlock Placeholder, string SubjectId);

    /// <summary>详情里一张要异步加载的缩略图：解码成功后贴到 <see cref="Image"/>，
    /// 解码失败则把 <see cref="Placeholder"/> 显出来（显示「无图」），而不是留一个空框。</summary>
    private sealed record ThumbRequest(Image Target, TextBlock Placeholder, AssetRecord Asset);

    public PresetWorkbenchPage(IWorkbenchHost host)
    {
        _host = host;
        InitializeComponent();

        _presets = new PresetWorkbenchService(new RelationQueryService(new RelationStore(host.Env.CacheDirectory)));
        _spinePreview = new SpinePreviewService(() =>
            (IReadOnlyList<AssetRecord>?)host.Project?.Assets ?? Array.Empty<AssetRecord>());
        _spineExport = new SpineExportService(_spinePreview);
        _spineAnimationSource = new SpineAnimationSourceService(_spinePreview);

        // 搜索行：关键词过滤卡片（对象键 / 中文名 / 角色 token）。先建节流器再接事件，避免构造期触发空引用。
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RebuildCards(); };
        _search = WorkbenchShell.CreateSearchBox("按对象 id / 名字过滤卡片（如 10201、浮士德、Faust）");
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        Shell.AddSearchItem(_search);
        Shell.AddSearchItem(WorkbenchShell.CreateButton("↻ 重新分析", (_, _) => RebuildCards()));
        _countText = WorkbenchShell.CreateSectionLabel("—");
        Shell.AddSearchItem(_countText);

        // 筛选行：类别切换（只列真的有内容的类别）。
        _categoryBox = WorkbenchShell.CreateFilterCombo("按类别筛选卡片", "全部类别");
        _categoryBox.SelectionChanged += OnCategorySelectionChanged;
        Shell.AddFilterItem(_categoryBox);

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
        Shell.SetEmptyHint("点「获取资源」完成启动扫描后，这里会自动列出全部预设对象卡片。");

        Loaded += (_, _) => { if (!_loaded) { _loaded = true; RebuildCards(); } };
        Log.Info("预设卡片流页面构造完成：关系库={0}", host.Env.CacheDirectory);
    }

    /// <summary>宿主在项目状态刷新时调用（打开/保存/重扫后重建卡片）。</summary>
    public void OnProjectRefreshed()
    {
        if (!_loaded) return;
        RebuildCards();
    }

    // ── 类别切换 ─────────────────────────────────────────────────────

    /// <summary>按当前关联图重填类别下拉（保留用户已选的类别；类别消失则回到「全部」）。</summary>
    private bool RefreshCategoryChoices()
    {
        IReadOnlyList<PresetCategory> categories;
        try
        {
            categories = _presets.Categories();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取类别清单失败");
            categories = [];
        }

        var keys = new string?[categories.Count + 1];
        var labels = new string[categories.Count + 1];
        keys[0] = null;
        labels[0] = "全部类别";
        for (var i = 0; i < categories.Count; i++)
        {
            keys[i + 1] = categories[i].Category;
            labels[i + 1] = $"{categories[i].Label}（{categories[i].Count}）";
        }

        // 内容没变就不动控件：SelectionChanged 会触发重建，无谓的 Items.Clear 会打断用户选择。
        var same = keys.Length == _categoryKeys.Length;
        if (same)
            for (var i = 0; i < keys.Length; i++)
                if (!string.Equals(keys[i], _categoryKeys[i], StringComparison.Ordinal)) { same = false; break; }
        if (same && _categoryBox.Items.Count == labels.Length) return false;

        _categoryKeys = keys;
        var previous = _category;
        // 先摘掉处理器再重建候选项：Items.Clear 会把 SelectedIndex 打回 -1 并触发一次
        // 「空类别」重建。处理器必须用**具名方法**挂在字段上才能摘得掉——早先写成
        // lambda 时 `-=` 摘的是另一个实例，处理器越挂越多（每次刷新叠一个）。
        _categoryBox.SelectionChanged -= OnCategorySelectionChanged;
        _categoryBox.Items.Clear();
        foreach (var label in labels) _categoryBox.Items.Add(label);
        var index = Array.IndexOf(keys, previous);
        _categoryBox.SelectedIndex = index >= 0 ? index : 0;
        _categoryBox.SelectionChanged += OnCategorySelectionChanged;
        _category = keys[_categoryBox.SelectedIndex];
        return true;
    }

    private void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs e) => OnCategoryChanged();

    private void OnCategoryChanged()
    {
        var index = _categoryBox.SelectedIndex;
        _category = index >= 0 && index < _categoryKeys.Length ? _categoryKeys[index] : null;
        Log.Debug("预设卡片流切换类别：{0}", _category ?? "(全部)");
        RebuildCards();
    }

    // ── 卡片流 ───────────────────────────────────────────────────────

    private void RebuildCards()
    {
        var generation = ++_buildGeneration;
        _portraitCts?.Cancel();
        _portraitCts?.Dispose();
        _portraitCts = null;
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
        _cardPanel.Children.Clear();
        _cards.Clear();
        ShowDetailPlaceholder("左侧点一张卡，这里显示该对象关联的全部资源与就地预览。");

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
        RefreshCategoryChoices();

        var keyword = _search.Text;
        var assets = (IReadOnlyList<AssetRecord>)project.Assets;
        IReadOnlyList<PresetCard> cards;
        try
        {
            cards = _presets.BuildCards(assets, _category, keyword);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "构建预设卡片失败");
            _countText.Text = "—";
            Shell.SetEmptyHint($"构建卡片失败：{ex.Message}");
            return;
        }
        if (generation != _buildGeneration) return;

        foreach (var card in cards) _cardPanel.Children.Add(BuildCard(card));
        var scope = _category is null ? "全部类别" : RelationCategories.Label(_category);
        _countText.Text = cards.Count == 0 ? "没有匹配的对象" : $"{cards.Count} 张卡片";
        Shell.SetEmptyHint(cards.Count == 0
            ? "没有匹配的对象卡片：换个关键词 / 类别，或确认关联图已建好。"
            : null);
        Shell.SetStatus($"共 {cards.Count} 张卡片（{scope}）"
            + (string.IsNullOrWhiteSpace(keyword) ? "。" : $"（已按「{keyword}」过滤）。"));

        // 封面异步加载（有限并发 + 可取消：切页/改关键词/改类别会取消上一轮）。
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
            FontSize = 36,
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

        // 类别小标签：六类别共用一条卡片流，卡片上必须能看出「这是哪一类」。
        var header = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = card.Title,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var chip = BuildChip(card.CategoryLabel);
        Grid.SetColumn(chip, 1);
        header.Children.Add(chip);
        stack.Children.Add(frame);
        stack.Children.Add(header);

        // 卡片正面预览：一句代表内容（台词 / 名称）。强度不精确时明说，避免把推导当精确事实。
        if (!string.IsNullOrWhiteSpace(card.PreviewText))
        {
            var previewRow = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            var strength = StrengthLabel(card.PreviewKind);
            previewRow.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(strength) ? card.PreviewText : $"[{strength}] {card.PreviewText}",
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 34,
                FontSize = 11,
                Opacity = 0.9,
                ToolTip = card.PreviewText,
            });
            stack.Children.Add(previewRow);
        }

        if (!string.IsNullOrWhiteSpace(card.Summary))
            stack.Children.Add(new TextBlock
            {
                Text = card.Summary,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 10.5,
                Opacity = 0.7,
                Margin = new Thickness(0, 2, 0, 0),
            });

        var button = new Button
        {
            Content = stack,
            Width = CardWidth,
            Margin = new Thickness(6),
            Padding = new Thickness(8),
            ToolTip = $"{card.CategoryLabel} · {card.Title}\n{card.LinkCount} 个关联资源\n{card.Summary}"
                + (string.IsNullOrWhiteSpace(card.PreviewText) ? string.Empty : $"\n{card.PreviewText}"),
        };
        button.Click += (_, _) => ShowDetail(card.SubjectId);
        var visual = new CardVisual(button, frame, portrait, placeholder, card.SubjectId);
        _cards.Add(visual);
        // Placeholder 的可见性由「有没有封面资源」决定：有封面就先藏起来等图片。
        placeholder.Visibility = card.PortraitAsset is null ? Visibility.Visible : Visibility.Hidden;
        return button;
    }

    /// <summary>一个小圆角标签（类别 / 预览强度）。色板走共享资源，取不到退回透明。</summary>
    private static Border BuildChip(string text)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = WbBrush("WbPanelBackgroundBrush"),
            BorderBrush = WbBrush("WbBorderLineBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Opacity = 0.8,
                Foreground = WbBrush("WbTextMutedBrush"),
            },
        };
    }

    /// <summary>预览强度的中文标签（空串 = 不标注）。</summary>
    private static string StrengthLabel(RelationPreviewKind kind) => kind switch
    {
        RelationPreviewKind.Exact => "精确",
        RelationPreviewKind.Derived => "推导",
        RelationPreviewKind.ChapterLevel => "关卡级",
        RelationPreviewKind.Ambiguous => "歧义",
        _ => string.Empty,
    };

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
    /// 后台按有限并发解码封面并贴到卡片上。取消（改关键词 / 改类别 / 切页 / 关窗）后不再贴图。
    /// </summary>
    /// <param name="cards">本轮的卡片视觉对象。</param>
    /// <param name="bySubject">subjectId → 卡片封面资源（由 <see cref="RebuildCards"/> 一并给出，避免二次查库）。</param>
    private async Task LoadPortraitsAsync(
        IReadOnlyList<CardVisual> cards,
        IReadOnlyDictionary<string, AssetRecord?> bySubject,
        CancellationToken cancellationToken)
    {
        using var scope = Log.Scope("加载卡片封面");
        var targets = cards.Where(c => c.Placeholder.Visibility == Visibility.Hidden).ToArray();
        if (targets.Length == 0)
        {
            Log.Debug("封面加载跳过：卡片 {0} 张都没有封面候选", cards.Count);
            return;
        }

        using var gate = new SemaphoreSlim(PortraitConcurrency);
        // 显式建请求列表（不在 Select 里写三元）：`(t, asset)` 与 `(t, null)` 的类型推断
        // 在 C# 里会得到一个 `(CardVisual, AssetRecord?)` 之外的候选，写清楚最省事。
        var requests = new List<(CardVisual, AssetRecord?)>(targets.Length);
        foreach (var target in targets)
        {
            bySubject.TryGetValue(target.SubjectId, out var asset);
            requests.Add((target, asset));
        }
        var decoded = await DecodeManyAsync(
            requests,
            gate,
            PortraitDecodeWidth,
            cancellationToken).ConfigureAwait(false);

        foreach (var (target, bitmap) in decoded)
        {
            if (cancellationToken.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                target.Portrait.Source = bitmap;
                target.Placeholder.Visibility = Visibility.Collapsed;
            }, DispatcherPriority.Background);
        }

        // 解码失败的封面不能留白：把首字母占位显出来。用户看到「这张确实没有封面」
        // 远好过一个空框（空框看起来像界面坏了，也没法区分「没封面」和「解码失败」）。
        var loaded = decoded.Select(x => x.Owner).ToHashSet();
        var failed = targets.Where(t => !loaded.Contains(t)).ToArray();
        if (failed.Length > 0)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                foreach (var target in failed) target.Placeholder.Visibility = Visibility.Visible;
            }, DispatcherPriority.Background);
        }
        Log.Info("卡片封面加载完成：成功 {0} / 候选 {1} 张（{2} 张退回首字母占位）",
            decoded.Count, targets.Length, failed.Length);
    }

    /// <summary>
    /// 有限并发地把一批资源解码成位图（封面与详情缩略图共用）。
    /// 解码失败只记日志、不产出结果——UI 保持原样（那格留白），绝不抛。
    /// </summary>
    private async Task<List<(T Owner, BitmapImage Bitmap)>> DecodeManyAsync<T>(
        IEnumerable<(T Owner, AssetRecord? Asset)> requests,
        SemaphoreSlim gate,
        int decodePixelWidth,
        CancellationToken cancellationToken)
        where T : class
    {
        var service = new UnityAssetService();
        var results = new List<(T, BitmapImage)>();
        var tasks = requests.Select(async request =>
        {
            var (owner, asset) = request;
            if (asset is null) return;
            if (string.IsNullOrWhiteSpace(asset.SourcePath) || asset.UnityPathId is null) return;
            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var png = await Task.Run(
                        () => TryDecodeImagePng(service, asset, cancellationToken), cancellationToken).ConfigureAwait(false);
                    if (png is null || png.Length == 0) return;
                    var bitmap = await Task.Run(() => Decode(png, decodePixelWidth), cancellationToken).ConfigureAwait(false);
                    if (bitmap is null) return;
                    lock (results) results.Add((owner, bitmap));
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { /* 正常取消 */ }
            catch (Exception ex)
            {
                Log.Debug(ex, "资源位图解码失败：{0}", AssetDisplay.DisplayPath(asset));
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* 正常取消：返回已完成的部分 */ }
        return results;
    }

    /// <summary>
    /// 把一条「图像」关联的资源解码成 PNG 字节；解不出来返回 null（调用方据此显示「无图」）。
    ///
    /// <para><b>为什么 Sprite 要单独走一条路</b>：<c>ReadTexturePng</c> 只能按 <b>Texture2D</b> 的
    /// pathId 解码。而关联图里的图像链接两种都可能：图标 / 立绘原图是 Texture2D，
    /// 图集里切出来的一格是 <b>Sprite</b>。对 Sprite 直接调 <c>ReadTexturePng</c> 必然返回 null
    /// —— 表现成「这张图加载不出来」，正是「部分图片没有成功加载」的成因之一。
    /// Sprite 走 <c>ReadBundleSpriteComposite</c>：它会解出被引用的 Texture2D 再按裁剪区域合成。</para>
    /// </summary>
    private static byte[]? TryDecodeImagePng(UnityAssetService service, AssetRecord asset, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(asset.SourcePath) || asset.UnityPathId is not { } pathId) return null;
        try
        {
            if (asset.Type == AssetType.Sprite && !string.IsNullOrWhiteSpace(asset.ContainerPath))
            {
                var composite = service.ReadBundleSpriteComposite(
                    asset.SourcePath, asset.ContainerPath, pathId, cancellationToken);
                if (composite.Png is { Length: > 0 }) return composite.Png;
            }
            return service.ReadTexturePng(asset.SourcePath, pathId, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            Log.Debug(ex, "图像解码失败：{0}", AssetDisplay.DisplayPath(asset));
            return null;
        }
    }

    /// <summary>按目标宽度解码 PNG（限制解码宽度，避免把原分辨率贴图整张装进内存）。</summary>
    private static BitmapImage? Decode(byte[] png, int decodePixelWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodePixelWidth;
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

        var category = subject is null ? RelationCategories.Label(SubjectIds.KeyOf(subjectId)) : CategoryLabelOf(subject);
        var meta = new List<string> { category };
        if (subject is not null && !string.IsNullOrWhiteSpace(subject.Character)) meta.Add($"角色 {subject.Character}");
        if (subject is not null && !string.IsNullOrWhiteSpace(subject.Subtitle)) meta.Add($"风格 {subject.Subtitle}");
        _detailPanel.Children.Add(WorkbenchShell.CreateSectionLabel(string.Join(" · ", meta), new Thickness(0, 2, 0, 6)));

        // 对象面的代表内容（卡片正面那句）在详情里给全：卡片只有两行高度，会被截断。
        if (subject is not null && !string.IsNullOrWhiteSpace(subject.PreviewText))
        {
            _detailPanel.Children.Add(WorkbenchShell.CreateSectionLabel("代表内容", new Thickness(0, 0, 0, 2)));
            _detailPanel.Children.Add(new TextBlock
            {
                Text = subject.PreviewText,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 0, 2, 8),
            });
        }

        var key = SubjectIds.KeyOf(subjectId);
        var openAll = WorkbenchShell.CreateButton($"在资源列表里搜索该对象的 id / 名字（{key}）",
            (_, _) => _host.ShowWorkbenchSearch(WorkbenchPageKeys.Assets, key));
        openAll.Margin = new Thickness(0, 4, 0, 10);
        _detailPanel.Children.Add(openAll);

        IReadOnlyList<PresetDetailGroup> groups;
        try
        {
            groups = _presets.BuildDetail(subjectId, (IReadOnlyList<AssetRecord>)project.Assets);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "构建对象详情失败：{0}", subjectId);
            ShowDetailPlaceholder($"构建详情失败：{ex.Message}");
            return;
        }

        if (groups.Count == 0)
        {
            _detailPanel.Children.Add(new TextBlock
            {
                Text = "该对象没有任何已登记的资源（关联图里它只有身份、没有链接）。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
            });
            Shell.SetStatus($"{title}：0 类资源");
            return;
        }

        var total = groups.Sum(g => g.Rows.Count);
        Shell.SetStatus($"{title}：{groups.Count} 类 / {total} 个关联资源");

        // 详情里要异步贴缩略图：先收集、最后一并发起（避免每行各起一轮后台任务）。
        var thumbRequests = new List<ThumbRequest>();
        foreach (var group in groups) _detailPanel.Children.Add(BuildGroup(group, thumbRequests));
        if (thumbRequests.Count > 0)
        {
            var cts = new CancellationTokenSource();
            _thumbnailCts = cts;
            _ = LoadThumbnailsAsync(thumbRequests, cts.Token);
        }
    }

    private static string CategoryLabelOf(RelationSubject subject)
        => string.IsNullOrWhiteSpace(subject.CategoryLabel)
            ? RelationCategories.Label(subject.SubjectKind)
            : subject.CategoryLabel;

    private async Task LoadThumbnailsAsync(IReadOnlyList<ThumbRequest> requests, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(PortraitConcurrency);
        var pairs = new List<(ThumbRequest, AssetRecord?)>(requests.Count);
        foreach (var request in requests) pairs.Add((request, request.Asset));
        var decoded = await DecodeManyAsync(pairs, gate, ThumbnailDecodeWidth, cancellationToken).ConfigureAwait(false);
        foreach (var (request, bitmap) in decoded)
        {
            if (cancellationToken.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                request.Target.Source = bitmap;
                request.Placeholder.Visibility = Visibility.Collapsed;
            }, DispatcherPriority.Background);
        }
        Log.Debug("详情缩略图加载完成：成功 {0} / 候选 {1} 张（其余框内显示「无图」）", decoded.Count, requests.Count);
    }

    private UIElement BuildGroup(PresetDetailGroup group, List<ThumbRequest> thumbRequests)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(WorkbenchShell.CreateSectionLabel(
            $"{group.Title}（{group.Rows.Count}）", new Thickness(0, 0, 0, 4)));
        var shown = 0;
        foreach (var row in group.Rows)
        {
            if (shown++ >= MaxRowsPerGroup)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"…还有 {group.Rows.Count - MaxRowsPerGroup} 项（用「打开」到对应工作台看全部）",
                    Opacity = 0.7,
                    FontSize = 10.5,
                });
                break;
            }
            panel.Children.Add(BuildRow(group.Kind, row, thumbRequests));
        }
        return panel;
    }

    private UIElement BuildRow(RelationKind kind, PresetDetailRow row, List<ThumbRequest> thumbRequests)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 图像行给一张就地缩略图（需求「关联数据应在卡片预览部分展示 + 预览功能」）。
        // 贴图行与 Spine 行没有可直接解码的 Texture2D，就不放图（不假装有预览）。
        if (kind == RelationKind.Image && row.Asset is not null)
        {
            var image = new Image { Width = ThumbnailSize, Height = ThumbnailSize, Stretch = Stretch.Uniform };
            var noImage = new TextBlock
            {
                Text = "无图",
                FontSize = 10,
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = WbBrush("WbTextMutedBrush"),
            };
            var frame = new Border
            {
                Width = ThumbnailSize,
                Height = ThumbnailSize,
                CornerRadius = new CornerRadius(4),
                Background = WbBrush("WbPanelBackgroundBrush"),
                BorderBrush = WbBrush("WbBorderLineBrush"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new Grid { Children = { noImage, image } },
            };
            grid.Children.Add(frame);
            thumbRequests.Add(new ThumbRequest(image, noImage, row.Asset));
        }

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = row.Display, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(row.Detail))
            text.Children.Add(new TextBlock { Text = row.Detail, Opacity = 0.7, FontSize = 10.5, TextWrapping = TextWrapping.Wrap });
        AppendInlinePreview(text, row);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var open = WorkbenchShell.CreateButton("打开", (_, _) => OpenRow(kind, row));
        open.Margin = new Thickness(4, 0, 0, 0);
        open.ToolTip = "切到对应工作台并定位到这一条（定位不到会退化为按名称过滤）。";
        Grid.SetColumn(open, 2);
        grid.Children.Add(open);

        // Spine 行额外给两个动作：① 就地播动画（vendored Spine 运行时离屏渲染）；
        // ② 导出三件套交给外部 Spine 工具（需要看细节 / 改素材时用）。
        if (kind == RelationKind.Spine && row.Asset is not null)
        {
            var play = WorkbenchShell.CreateButton("动画预览…", (_, _) => PreviewSpineAnimation(row.Asset!));
            play.Margin = new Thickness(4, 0, 0, 0);
            play.ToolTip = "用内置 Spine 运行时渲染并播放这个骨架的动画（可切动画 / 拖时间轴）。";
            Grid.SetColumn(play, 3);
            grid.Children.Add(play);

            var export = WorkbenchShell.CreateButton("导出…", (_, _) => ExportSpine(row.Asset!));
            export.Margin = new Thickness(4, 0, 0, 0);
            export.ToolTip = "把该 Spine 资源（骨架 / 图集 / 图集贴图）导出到一个目录，供外部 Spine 工具查看。";
            Grid.SetColumn(export, 4);
            grid.Children.Add(export);
        }
        return grid;
    }

    /// <summary>
    /// 打开 Spine 动画预览窗口。素材解析失败（缺骨架 / 缺图集 / 读取失败）时只弹一条
    /// 说明性消息——不让「点一下预览」变成静默无反应。
    /// </summary>
    private void PreviewSpineAnimation(AssetRecord asset)
    {
        var (source, error) = _spineAnimationSource.Resolve(asset);
        if (source is null)
        {
            Log.Warn("Spine 动画预览不可用：{0}（{1}）", error, AssetDisplay.DisplayPath(asset));
            MessageBox.Show(Window.GetWindow(this), error ?? "无法解析该 Spine 资源的动画素材。",
                "Spine 动画预览", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SpineAnimationPreviewWindow.ShowFor(Window.GetWindow(this), source);
    }

    /// <summary>
    /// 行内预览：把 <see cref="PresetDetailRow.PreviewText"/> 就地画出来
    /// （音频附时长、文本给片段），并标注强度——推导出来的内容不能冒充精确命中。
    /// </summary>
    private static void AppendInlinePreview(StackPanel text, PresetDetailRow row)
    {
        if (string.IsNullOrWhiteSpace(row.PreviewText)) return;
        var strength = StrengthLabel(row.PreviewKind);
        var prefix = row.MediaKind switch
        {
            "audio" when row.DurationSec is { } seconds => $"[{DescribeStrength(strength)}音频 {FormatDuration(seconds)}] ",
            _ => string.IsNullOrEmpty(strength) ? string.Empty : $"[{strength}] ",
        };
        text.Children.Add(new TextBlock
        {
            Text = prefix + row.PreviewText,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.85,
            Margin = new Thickness(0, 1, 0, 0),
        });
    }

    private static string DescribeStrength(string strength)
        => string.IsNullOrEmpty(strength) ? string.Empty : strength + " ";

    /// <summary>秒 → <c>m:ss</c>（音频时长）。</summary>
    private static string FormatDuration(double seconds)
    {
        var total = (int)Math.Round(seconds);
        if (total < 0) total = 0;
        return $"{total / 60}:{total % 60:00}";
    }

    /// <summary>「打开」：优先精确跳转到那一行；目标页定位不到时退化为按显示名过滤。</summary>
    private void OpenRow(RelationKind kind, PresetDetailRow row)
    {
        var target = PresetWorkbenchService.TargetFor(kind, row);
        Log.Debug("预设详情打开资源：kind={0}，页={1}，载荷段数={2}",
            kind, target.PageKey, RelationDeepLink.Decode(target.Payload).Count);
        var revealed = _host.RevealReference(target.PageKey, target.Payload, row.Display);
        if (!revealed) Shell.SetStatus($"已切到 {target.PageKey} 工作台：精确定位未命中，已按「{row.Display}」过滤。");
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
