using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Formats.Unity;
using NLog;

namespace LimbusModEditor.Application.Spine;

/// <summary>
/// 一次 Spine 动画预览所需的<b>原始素材</b>：骨架 JSON + 图集文本 + 「页名 → 图集页 PNG 字节」。
///
/// <para><b>为什么把「找素材」和「渲染」分开</b>：渲染要用 vendored Spine 运行时
/// （<c>LimbusModEditor.SpineRuntime</c>，net8.0 无 WPF 依赖），而找素材依赖
/// 项目资源索引与 Unity 读取管线（<see cref="UnityAssetService"/>）。
/// 中间用这个纯数据对象隔开，Application 层就能被单测覆盖，App 层只做「把帧贴到 Image」。</para>
/// </summary>
public sealed class SpineAnimationSource
{
    /// <summary>骨架 JSON 文本（Spine 4.0 <c>.json</c>）。</summary>
    public required string SkeletonJson { get; init; }

    /// <summary>图集文本（<c>*.atlas.txt</c>）。</summary>
    public required string AtlasText { get; init; }

    /// <summary>显示用标签（资源所在目录名，如 <c>StorySpine_Sinclair</c>）。</summary>
    public required string Label { get; init; }

    /// <summary>
    /// 按图集页名取该页的 PNG 字节（页名即贴图文件名）。
    /// <b>拿不到时返回空数组</b>——渲染器据此给出「图集页缺失」的中文原因，而不是抛异常。
    /// </summary>
    public required Func<string, byte[]> PageBytes { get; init; }

    /// <summary>同目录里能找到的图集页贴图名（诊断 / 提示用）。</summary>
    public IReadOnlyList<string> AvailablePages { get; init; } = [];
}

/// <summary>
/// 为一个 Spine 资源解析出可交给 Spine 运行时加载的素材。
///
/// <para><b>口径来自真实数据</b>：Limbus 的 Spine 三件套在同一个容器目录里——
/// <c>cg_40.json</c>（骨架）+ <c>cg_40.atlas.txt</c>（图集）+ <c>cg_40.png</c>（图集页）。
/// 用户可能从三者中任意一个点进来（资源页的 Spine 预览、卡片详情的 Spine 行），
/// 所以入口不同、解析结果必须一致：这里一律以<b>所在目录</b>为范围去找齐三件套。</para>
///
/// <para><b>不猜</b>：找不到骨架就明说「同目录里找不到骨架 JSON」，不去拿
/// <c>SkeletonData.asset</c> 之类的二进制对象硬解（那只会给一个更费解的失败）。</para>
/// </summary>
public sealed class SpineAnimationSourceService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly SpinePreviewService _preview;

    /// <param name="preview">Spine 预览服务（复用它的同目录资源索引，不再建一份）。</param>
    public SpineAnimationSourceService(SpinePreviewService preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        _preview = preview;
    }

    /// <summary>
    /// 解析素材。失败返回中文原因（<b>绝不抛</b>）：Spine 是旁路预览，缺件只是「这次看不了」。
    /// </summary>
    public (SpineAnimationSource? Source, string? Error) Resolve(
        AssetRecord asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var selfEntry = AssetDisplay.ContainerEntryPath(asset) ?? asset.LogicalPath ?? string.Empty;
        var siblings = _preview.Index().Siblings(selfEntry);

        var skeleton = PickSkeleton(asset, siblings);
        if (skeleton is null) return (null, "同目录里找不到骨架 JSON（*.json），无法播放动画。");
        var atlas = PickAtlas(asset, siblings);
        if (atlas is null) return (null, "同目录里找不到图集文本（*.atlas.txt），无法播放动画。");

        var skeletonJson = ReadText(skeleton, cancellationToken);
        if (string.IsNullOrWhiteSpace(skeletonJson))
            return (null, $"骨架 JSON 读取失败（{NameOf(skeleton)}）：可能是空文件或不在可读容器里。");
        var atlasText = ReadText(atlas, cancellationToken);
        if (string.IsNullOrWhiteSpace(atlasText))
            return (null, $"图集文本读取失败（{NameOf(atlas)}）：可能是空文件或不在可读容器里。");

        // 图集页贴图：页名 == 贴图文件名。解出一次就缓存（atlas 加载会对每页各取一次）。
        var textures = siblings
            .Where(x => x.Type is AssetType.Texture or AssetType.Sprite)
            .Where(x => NameOf(x).EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var cache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        byte[] PageBytes(string pageName)
        {
            if (string.IsNullOrWhiteSpace(pageName)) return [];
            if (cache.TryGetValue(pageName, out var cached)) return cached;
            var bytes = ReadPageBytes(textures, pageName, cancellationToken);
            cache[pageName] = bytes;
            return bytes;
        }

        var folder = SpineSiblingIndex.FolderOf(selfEntry);
        var label = folder.Length == 0 ? NameOf(skeleton) : folder[(folder.LastIndexOf('/') + 1)..];
        Log.Info("Spine 动画素材解析完成：目录 {0}，骨架 {1}，图集 {2}，可用图集页 {3} 个",
            label, NameOf(skeleton), NameOf(atlas), textures.Length);
        return (new SpineAnimationSource
        {
            SkeletonJson = skeletonJson,
            AtlasText = atlasText,
            Label = string.IsNullOrWhiteSpace(label) ? "spine" : label,
            PageBytes = PageBytes,
            AvailablePages = textures.Select(NameOf).ToArray(),
        }, null);
    }

    /// <summary>骨架 = 自身（若是 .json）或同目录第一个 <c>.json</c>（排除 <c>.atlas.txt</c>）。</summary>
    private static AssetRecord? PickSkeleton(AssetRecord self, IReadOnlyList<AssetRecord> siblings)
    {
        if (IsSkeletonName(NameOf(self))) return self;
        return siblings.FirstOrDefault(x =>
            x.Type is AssetType.Text or AssetType.Json && IsSkeletonName(NameOf(x)));
    }

    /// <summary>图集 = 自身（若是 <c>.atlas.txt</c>）或同目录第一个 <c>.atlas.txt</c>。</summary>
    private static AssetRecord? PickAtlas(AssetRecord self, IReadOnlyList<AssetRecord> siblings)
    {
        if (IsAtlasName(NameOf(self))) return self;
        return siblings.FirstOrDefault(x =>
            x.Type is AssetType.Text or AssetType.Json && IsAtlasName(NameOf(x)));
    }

    private static bool IsSkeletonName(string name)
        => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
           && !name.EndsWith(".atlas.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsAtlasName(string name)
        => name.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".atlas.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>按页名找贴图并解码为 PNG；找不到 / 解码失败返回空数组（由渲染器报「缺页」）。</summary>
    private static byte[] ReadPageBytes(
        IReadOnlyList<AssetRecord> textures, string pageName, CancellationToken cancellationToken)
    {
        var texture = textures.FirstOrDefault(x =>
            string.Equals(NameOf(x), pageName, StringComparison.OrdinalIgnoreCase));
        if (texture is null)
        {
            Log.Warn("Spine 动画预览：同目录没有图集页贴图 {0}", pageName);
            return [];
        }
        if (string.IsNullOrWhiteSpace(texture.SourcePath) || texture.UnityPathId is null)
        {
            Log.Warn("Spine 动画预览：图集页贴图 {0} 没有可用字节（SourcePath/PathId 缺失）", pageName);
            return [];
        }
        try
        {
            return new UnityAssetService()
                .ReadTexturePng(texture.SourcePath!, texture.UnityPathId!.Value, cancellationToken) ?? [];
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            Log.Warn(ex, "Spine 动画预览：图集页贴图解码失败：{0}", pageName);
            return [];
        }
    }

    /// <summary>容器路径里的文件名（关联键 / 图集页名都是它）。</summary>
    private static string NameOf(AssetRecord asset)
        => SpineSiblingIndex.FileNameOf(AssetDisplay.ContainerEntryPath(asset) ?? asset.LogicalPath ?? string.Empty);

    /// <summary>读 Spine 文本（走 bundle 解包通道；失败返回 null）。</summary>
    private static string? ReadText(AssetRecord asset, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(asset.SourcePath) || asset.UnityPathId is null) return null;
        try
        {
            if (asset.Metadata.TryGetValue("unityBundle", out var isBundle) && isBundle == "true")
            {
                var textAsset = new UnityAssetService().ReadBundleTextAsset(
                    asset.SourcePath!, asset.ContainerPath ?? string.Empty, asset.UnityPathId.Value, cancellationToken);
                return textAsset.TryDecodeUtf8();
            }
            return File.Exists(asset.SourcePath) ? File.ReadAllText(asset.SourcePath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            Log.Warn(ex, "Spine 文本读取失败：{0}", AssetDisplay.DisplayPath(asset));
            return null;
        }
    }
}
