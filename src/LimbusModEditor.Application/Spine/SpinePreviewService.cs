using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Assets.Preview;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Formats.Unity;
using NLog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LimbusModEditor.Application.Spine;

/// <summary>
/// 同目录资源的快速索引（Spine 骨架 / 图集 / 图集贴图 三件套在同一个文件夹里）。
///
/// <para><b>为什么按「文件夹」索引</b>：Spine 三件套的定位键就是容器路径的目录
/// （真实数据：<c>Assets/Resources_moved/Story/CG/Ep9_3/StorySpine_Sinclair/</c> 下有
/// <c>cg_40.json</c> / <c>cg_40.atlas.txt</c> / <c>cg_40.png</c>），按目录一次建表即可 O(1) 取。</para>
///
/// <para>只索引<b>带容器路径</b>的资源（真实数据 127 万行里只有约 5 万行有），
/// 因此建表很快、内存占用也可控。</para>
/// </summary>
public sealed class SpineSiblingIndex
{
    private readonly Dictionary<string, List<AssetRecord>> _byFolder = new(StringComparer.OrdinalIgnoreCase);

    public SpineSiblingIndex(IReadOnlyList<AssetRecord> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var withPath = 0;
        foreach (var asset in assets)
        {
            var entry = AssetDisplay.ContainerEntryPath(asset);
            if (string.IsNullOrWhiteSpace(entry)) continue;
            withPath++;
            var folder = FolderOf(entry);
            if (!_byFolder.TryGetValue(folder, out var list)) _byFolder[folder] = list = [];
            list.Add(asset);
        }
        IndexedAssetCount = withPath;
        FolderCount = _byFolder.Count;
    }

    /// <summary>被索引的资源数（带容器路径的那些）。</summary>
    public int IndexedAssetCount { get; }

    /// <summary>索引到的目录数。</summary>
    public int FolderCount { get; }

    /// <summary>某容器路径所在目录下的全部资源。</summary>
    public IReadOnlyList<AssetRecord> Siblings(string containerEntry)
    {
        if (string.IsNullOrWhiteSpace(containerEntry)) return [];
        return _byFolder.TryGetValue(FolderOf(containerEntry), out var list) ? list : [];
    }

    /// <summary>容器路径的目录部分（统一 <c>/</c>，不含末尾斜杠）。</summary>
    public static string FolderOf(string containerEntry)
    {
        var normalized = containerEntry.Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        return index <= 0 ? string.Empty : normalized[..index];
    }

    /// <summary>容器路径的文件名部分。</summary>
    public static string FileNameOf(string containerEntry)
    {
        var normalized = containerEntry.Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }
}

/// <summary>Spine 预览的构建结果（供 <see cref="SpinePreviewProvider"/> 打包成 <c>AssetPreview</c>）。</summary>
/// <param name="Skeleton">解析出的骨架（图集文本资源为 null）。</param>
/// <param name="Atlas">解析出的图集（骨架资源为 null）。</param>
/// <param name="LayoutPng">图集页 + 区域框叠加图（定位不到贴图时为 null）。</param>
/// <param name="Rows">结构信息行（骨架 / 图集结构 + 动画清单）。</param>
/// <param name="InfoLine">一行中文摘要。</param>
public sealed record SpinePreviewData(
    SpineSkeleton? Skeleton,
    SpineAtlas? Atlas,
    byte[]? LayoutPng,
    IReadOnlyList<AssetPreviewRow> Rows,
    string InfoLine);

/// <summary>
/// Spine 资源的解析与「结构 + 图集布局」预览。
///
/// <para><b>本工具不做骨骼动画播放</b>：游戏里 Spine 数据是可读文本（<c>.json</c> / <c>.atlas.txt</c>），
/// 但仓库里没有任何 Spine 运行时；要真正播放需要实现完整蒙皮 / 网格变形 / 动画混合 / 约束求值，
/// 那不是「加个预览 provider」的量级。因此这里给出的是<b>能确证的事实</b>：
/// 骨架版本与画布、骨骼 / 槽位 / 附件数量、全部动画名与时长、图集页与区域清单，
/// 外加一张「图集页 + 区域框」的叠加图（看得见贴图怎么切的）。</para>
///
/// <para><b>导出</b>走 <c>ExportSlot.Spine</c>（<c>&lt;项目名&gt;_data/spine/&lt;目录名&gt;/</c>），
/// 把骨架 / 图集 / 图集贴图原样取出，交给外部 Spine 工具查看。</para>
/// </summary>
public sealed class SpinePreviewService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>叠加图的输出上限（长边像素）：超过就等比缩小，避免建超大位图。</summary>
    public const int MaxLayoutDimension = 1024;

    private readonly Func<IReadOnlyList<AssetRecord>> _assetsProvider;
    private IReadOnlyList<AssetRecord>? _cachedFor;
    private SpineSiblingIndex? _cachedIndex;
    private int _cachedCount = -1;
    private long _cachedAt;

    /// <summary>索引缓存有效期（毫秒）：项目资源集合会被就地增删（回灌 / 刷新），
    /// 光靠引用相等会拿到过期索引，因此再加「数量变化」与「超时」两道失效条件。</summary>
    private const long IndexCacheLifetimeMs = 15_000;

    /// <param name="assetsProvider">返回当前项目的资源列表（每次调用都应返回最新快照）。</param>
    public SpinePreviewService(Func<IReadOnlyList<AssetRecord>> assetsProvider)
    {
        ArgumentNullException.ThrowIfNull(assetsProvider);
        _assetsProvider = assetsProvider;
    }

    /// <summary>取（带失效判断的）同目录索引。</summary>
    public SpineSiblingIndex Index()
    {
        var assets = _assetsProvider();
        var now = Environment.TickCount64;
        if (ReferenceEquals(assets, _cachedFor) && _cachedIndex is not null
            && _cachedCount == assets.Count && now - _cachedAt < IndexCacheLifetimeMs)
            return _cachedIndex;
        var index = new SpineSiblingIndex(assets);
        _cachedFor = assets;
        _cachedIndex = index;
        _cachedCount = assets.Count;
        _cachedAt = now;
        Log.Debug("Spine 同目录索引重建：带容器路径的资源 {0} 个，目录 {1} 个", index.IndexedAssetCount, index.FolderCount);
        return index;
    }

    /// <summary>该资源是否是可能的 Spine 文本资源（不读盘，只按路径 / 类型判定）。</summary>
    public static bool LooksLikeSpineText(AssetRecord asset)
    {
        if (asset.Type is not (AssetType.Text or AssetType.Json)) return false;
        var entry = AssetDisplay.ContainerEntryPath(asset) ?? asset.LogicalPath ?? string.Empty;
        if (entry.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase)) return true;
        return Relations.RelationDisplayRules.IsSpinePath(entry);
    }

    /// <summary>读 Spine 文本并解析（失败返回 <see cref="SpineParseResult.None"/>）。</summary>
    public SpineParseResult ParseAsset(AssetRecord asset, CancellationToken cancellationToken)
    {
        var text = ReadText(asset, cancellationToken);
        if (text is null) return SpineParseResult.None;
        return SpineTextParser.Parse(text);
    }

    private static string? ReadText(AssetRecord asset, CancellationToken cancellationToken)
    {
        var readable = !string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath)
            ? asset.SourcePath
            : null;
        if (readable is null || asset.UnityPathId is null) return null;
        try
        {
            if (asset.Metadata.TryGetValue("unityBundle", out var isBundle) && isBundle == "true")
            {
                var textAsset = new UnityAssetService().ReadBundleTextAsset(
                    readable, asset.ContainerPath ?? string.Empty, asset.UnityPathId.Value, cancellationToken);
                return textAsset.TryDecodeUtf8();
            }
            return File.ReadAllText(readable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            Log.Warn(ex, "Spine 文本读取失败：{0}", AssetDisplay.DisplayPath(asset));
            return null;
        }
    }

    /// <summary>
    /// 构建预览数据：解析 Spine 文本 → 定位同目录的图集 / 贴图 → 生成结构行与布局叠加图。
    /// 任何一步失败都只少一块信息，不抛异常。
    /// </summary>
    public SpinePreviewData? Build(AssetRecord asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        using var scope = Log.Scope("构建 Spine 预览");
        var parsed = ParseAsset(asset, cancellationToken);
        if (!parsed.Any) return null;

        var siblings = Index().Siblings(AssetDisplay.ContainerEntryPath(asset) ?? string.Empty);
        var rows = new List<AssetPreviewRow>();
        byte[]? layoutPng = null;
        string infoLine;

        if (parsed.Skeleton is { } skeleton)
        {
            // 骨架资源：同目录里找 .atlas.txt 与图集贴图。
            var atlasAsset = siblings.FirstOrDefault(x =>
                (AssetDisplay.ContainerEntryPath(x) ?? string.Empty).EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase));
            var atlas = atlasAsset is null ? null : ParseAsset(atlasAsset, cancellationToken).Atlas;
            var pagePng = atlas is null ? null : TryBuildLayoutImage(atlas, siblings, cancellationToken, out _);
            layoutPng = pagePng;

            rows.AddRange(BuildSkeletonRows(skeleton, atlas, atlasAsset is not null, pagePng is not null));
            var unsupported = skeleton.UnsupportedAttachmentCount > 0
                ? $"；{skeleton.UnsupportedAttachmentCount} 个非区域附件（网格等）不支持渲染"
                : string.Empty;
            infoLine = $"Spine {Describe(skeleton.Version)}骨架 · {skeleton.Animations.Count} 个动画 · "
                + $"{skeleton.Bones.Count} 根骨骼 · {skeleton.Slots.Count} 个槽位 · "
                + $"{skeleton.Attachments.Count} 个区域附件{unsupported}";
        }
        else
        {
            var atlas = parsed.Atlas!;
            layoutPng = TryBuildLayoutImage(atlas, siblings, cancellationToken, out var pageNote);
            rows.AddRange(BuildAtlasRows(atlas, layoutPng is not null, pageNote));
            infoLine = $"Spine {Describe(string.Empty)}图集 · {atlas.Pages.Count} 页 · {atlas.RegionCount} 个区域"
                + (layoutPng is null ? "（未找到页贴图，只列结构）" : "（叠加图已画出区域框）");
        }

        return new SpinePreviewData(parsed.Skeleton, parsed.Atlas, layoutPng, rows, infoLine);
    }

    private static string Describe(string version)
        => string.IsNullOrWhiteSpace(version) ? string.Empty : version + " ";

    private static IEnumerable<AssetPreviewRow> BuildSkeletonRows(
        SpineSkeleton skeleton, SpineAtlas? atlas, bool atlasFound, bool layoutDrawn)
    {
        var rows = new List<AssetPreviewRow>
        {
            new("骨架版本", string.IsNullOrWhiteSpace(skeleton.Version) ? "（未声明）" : skeleton.Version, 0, true),
            new("画布", $"{skeleton.Width:0.##} × {skeleton.Height:0.##}（原点 {skeleton.X:0.##}, {skeleton.Y:0.##}）"),
            new("骨骼 / 槽位 / 附件", $"{skeleton.Bones.Count} / {skeleton.Slots.Count} / {skeleton.Attachments.Count}"),
        };
        if (skeleton.AttachmentKindCounts.Count > 0)
            rows.Add(new AssetPreviewRow("附件类型分布",
                string.Join(" · ", skeleton.AttachmentKindCounts.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}"))));
        if (skeleton.SimplifiedInheritCount > 0)
            rows.Add(new AssetPreviewRow("继承模式简化", $"{skeleton.SimplifiedInheritCount} 根骨骼不是 normal/onlyTranslation（预览按 normal 近似）"));

        rows.Add(new AssetPreviewRow("— 动画 —", $"共 {skeleton.Animations.Count} 个", 0, true));
        foreach (var animation in skeleton.Animations.Take(200))
            rows.Add(new AssetPreviewRow(animation.Name,
                $"{animation.Duration:0.###} 秒 · 骨骼时间线 {animation.Bones.Count} 条 · 槽位时间线 {animation.Slots.Count} 条",
                1));
        if (skeleton.Animations.Count > 200)
            rows.Add(new AssetPreviewRow("…", $"还有 {skeleton.Animations.Count - 200} 个动画未列出", 1));

        rows.Add(new AssetPreviewRow("— 图集 —",
            atlas is null
                ? (atlasFound ? "同目录的 .atlas.txt 解析失败" : "同目录没找到 .atlas.txt")
                : $"{atlas.Pages.Count} 页 · {atlas.RegionCount} 个区域" + (layoutDrawn ? " · 已画区域框" : " · 未找到页贴图"),
            0, true));
        if (atlas is not null)
        {
            foreach (var page in atlas.Pages)
            {
                rows.Add(new AssetPreviewRow(page.Name,
                    $"{page.Width} × {page.Height} · 打包缩放 {page.Scale:0.###} · {page.Regions.Count} 个区域", 1));
                foreach (var region in page.Regions.Take(40))
                    rows.Add(new AssetPreviewRow(region.Name,
                        $"({region.X},{region.Y}) {region.Width}×{region.Height}"
                        + (region.Rotation != 0 ? $" 旋转 {region.Rotation}°" : string.Empty), 2));
                if (page.Regions.Count > 40)
                    rows.Add(new AssetPreviewRow("…", $"该页还有 {page.Regions.Count - 40} 个区域未列出", 2));
            }
        }

        rows.Add(new AssetPreviewRow("— 骨骼（前 60）—", $"{skeleton.Bones.Count} 根", 0, true));
        foreach (var bone in skeleton.Bones.Take(60))
            rows.Add(new AssetPreviewRow(bone.Name,
                bone.Parent is null ? "（根）" : $"父 {bone.Parent}", 1));
        if (skeleton.Bones.Count > 60)
            rows.Add(new AssetPreviewRow("…", $"还有 {skeleton.Bones.Count - 60} 根骨骼未列出", 1));
        return rows;
    }

    private static IEnumerable<AssetPreviewRow> BuildAtlasRows(SpineAtlas atlas, bool layoutDrawn, string? pageNote)
    {
        var rows = new List<AssetPreviewRow>
        {
            new("页 / 区域", $"{atlas.Pages.Count} 页 · {atlas.RegionCount} 个区域", 0, true),
            new("页贴图", layoutDrawn ? "已找到并画出区域框" : (pageNote ?? "没找到")),
        };
        foreach (var page in atlas.Pages)
        {
            rows.Add(new AssetPreviewRow(page.Name, $"{page.Width} × {page.Height} · 缩放 {page.Scale:0.###} · {page.Regions.Count} 个区域", 1));
            foreach (var region in page.Regions)
                rows.Add(new AssetPreviewRow(region.Name,
                    $"({region.X},{region.Y}) {region.Width}×{region.Height}"
                    + (region.Rotation != 0 ? $" 旋转 {region.Rotation}°" : string.Empty), 2));
        }
        return rows;
    }

    /// <summary>
    /// 找图集页贴图并画上区域框。<paramref name="note"/> 给出「为什么没画出来」。
    /// </summary>
    private static byte[]? TryBuildLayoutImage(
        SpineAtlas atlas, IReadOnlyList<AssetRecord> siblings, CancellationToken cancellationToken, out string? note)
    {
        note = null;
        var page = atlas.Pages.FirstOrDefault();
        if (page is null) { note = "图集没有页"; return null; }

        var texture = siblings.FirstOrDefault(x =>
            x.Type is AssetType.Texture or AssetType.Sprite
            && string.Equals(SpineSiblingIndex.FileNameOf(AssetDisplay.ContainerEntryPath(x) ?? string.Empty),
                page.Name, StringComparison.OrdinalIgnoreCase));
        if (texture is null)
        {
            note = $"同目录没找到页贴图 {page.Name}";
            return null;
        }
        if (string.IsNullOrWhiteSpace(texture.SourcePath) || texture.UnityPathId is null) { note = "页贴图没有可用字节"; return null; }

        try
        {
            var png = new UnityAssetService().ReadTexturePng(texture.SourcePath!, texture.UnityPathId.Value, cancellationToken);
            if (png is null || png.Length == 0) { note = "页贴图解码失败"; return null; }
            return DrawRegionBoxes(png, page);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnknownImageFormatException)
        {
            Log.Warn(ex, "图集页贴图读取/叠加失败：{0}", page.Name);
            note = "页贴图读取失败";
            return null;
        }
    }

    /// <summary>
    /// 在图集页上画出每个区域的边框（红框）+ 半透明填充（蓝）。输出长边不超过
    /// <see cref="MaxLayoutDimension"/>，等比缩小。
    /// </summary>
    public static byte[] DrawRegionBoxes(byte[] png, SpineAtlasPage page)
    {
        ArgumentNullException.ThrowIfNull(png);
        ArgumentNullException.ThrowIfNull(page);
        using var source = Image.Load<Rgba32>(png);
        var scale = Math.Min(1.0, (double)MaxLayoutDimension / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var canvas = new Image<Rgba32>(width, height);
        source.ProcessPixelRows(canvas, (src, dst) =>
        {
            for (var y = 0; y < height; y++)
            {
                var sy = Math.Min(src.Height - 1, (int)(y / scale));
                var srcRow = src.GetRowSpan(sy);
                var dstRow = dst.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var sx = Math.Min(src.Width - 1, (int)(x / scale));
                    dstRow[x] = srcRow[sx];
                }
            }
        });

        var outline = new Rgba32(255, 64, 64, 255);
        var fill = new Rgba32(64, 128, 255, 40);
        canvas.ProcessPixelRows(accessor =>
        {
            foreach (var region in page.Regions)
            {
                var x0 = (int)Math.Round(region.X * scale);
                var y0 = (int)Math.Round(region.Y * scale);
                var x1 = (int)Math.Round((region.X + region.Width) * scale);
                var y1 = (int)Math.Round((region.Y + region.Height) * scale);
                x0 = Math.Clamp(x0, 0, width - 1);
                y0 = Math.Clamp(y0, 0, height - 1);
                x1 = Math.Clamp(x1, 0, width);
                y1 = Math.Clamp(y1, 0, height);
                for (var y = y0; y < y1; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = x0; x < x1; x++)
                        row[x] = Blend(row[x], fill);
                    if (x0 < x1) row[x0] = outline;
                    if (x1 - 1 >= x0) row[x1 - 1] = outline;
                }
                if (y0 < y1)
                {
                    var top = accessor.GetRowSpan(y0);
                    var bottom = accessor.GetRowSpan(y1 - 1);
                    for (var x = x0; x < x1; x++) { top[x] = outline; bottom[x] = outline; }
                }
            }
        });

        using var stream = new MemoryStream();
        canvas.SaveAsPng(stream);
        return stream.ToArray();
    }

    /// <summary>把半透明颜色叠到目标像素上（源为直通 alpha）。</summary>
    private static Rgba32 Blend(Rgba32 destination, Rgba32 source)
    {
        var a = source.A / 255f;
        return new Rgba32(
            (byte)(source.R * a + destination.R * (1 - a)),
            (byte)(source.G * a + destination.G * (1 - a)),
            (byte)(source.B * a + destination.B * (1 - a)),
            destination.A);
    }
}
