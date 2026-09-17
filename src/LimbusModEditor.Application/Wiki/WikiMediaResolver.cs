using System.Security.Cryptography;
using System.Text;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Formats.Unity;
using NLog;

namespace LimbusModEditor.Application.Wiki;

/// <summary>
/// 一条资源绑定的解析结果。<c>MediaUrl</c> 是「图片/预览地址」，其余三个是
/// 音频/Spine 三种形态各自的地址 —— 拿不到真实地址的一律为 null（<b>不编造</b>）。
/// </summary>
public sealed record WikiMediaResolution(
    string? MediaUrl,
    string? AudioUrl = null,
    string? SkeletonUrl = null,
    string? AtlasUrl = null,
    IReadOnlyDictionary<string, string>? TextureUrls = null)
{
    /// <summary>「什么都没有」的结果（解析失败、未支持的形态、超出配额都用它）。</summary>
    public static WikiMediaResolution None { get; } = new(MediaUrl: null);
}

/// <summary>
/// 维基资源绑定 → 可访问地址的解析器。
///
/// <para><b>要解决的问题</b>：绑定里的 <c>ref_key</c> / <c>deep_link</c> 是 Unity
/// <b>容器路径</b>（<c>Assets/Resources_moved/Sprite/SkillIcon/1000101.png</c>），不是 URL。
/// 直接把它当地址给前端，前端就只能显示「无可用地址」。这里做的是
/// 「容器路径 → 索引行 → 解出字节 → 落到 <c>wwwroot/data</c> → 给出
/// <c>https://lme.data/…</c>」—— <c>lme.data</c> 虚拟主机映射到
/// <c>{AppContext.BaseDirectory}/wwwroot/data</c>，所以写进那里就真的能取到。</para>
///
/// <para><b>只做本轮需要的形态</b>：Image 已通；Audio / Spine 的入口留了 TODO，
/// 依赖（<see cref="BankIndex"/> / <see cref="BankAudio"/> / <see cref="SpineData"/>）已就位，
/// 后续轮次补。</para>
/// </summary>
public sealed class WikiMediaResolver
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>一次页面加载最多解析多少条绑定（病态页面不能拖住加载）。超出的给 null。</summary>
    private const int MaxResolutionsPerPage = 64;

    private const string ImageKind = "Image";

    /// <summary>落地文件对外的地址前缀（<c>lme.data</c> 虚拟主机的根）。</summary>
    private const string DataHost = "https://lme.data/";

    private readonly AssetCatalog _catalog;
    private readonly UnityAssetService _unity = new();

    public WikiMediaResolver(
        AssetCatalog catalog,
        BankIndexService bankIndex,
        ISpineDataGateway spineData,
        BankAudioService? bankAudio = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        BankIndex = bankIndex;
        SpineData = spineData;
        BankAudio = bankAudio;
    }

    /// <summary>音频解析要用的 bank 索引（本轮未用，后续轮次走它）。</summary>
    public BankIndexService BankIndex { get; }

    /// <summary>Spine 解析要用的数据网关（本轮未用，后续轮次走它）。</summary>
    public ISpineDataGateway SpineData { get; }

    /// <summary>音频解码服务（本轮未用，后续轮次走它）。</summary>
    public BankAudioService? BankAudio { get; }

    /// <summary>
    /// 解析一条绑定。除 Image 外的形态本轮一律返回全 null 的结果，失败也不抛 ——
    /// 一条资源解不出来不能让整页加载失败。
    /// </summary>
    public Task<WikiMediaResolution> ResolveAsync(WikiResourceBinding binding, CancellationToken cancellationToken = default)
        => ResolveCoreAsync(binding?.Kind, binding?.RefKey, cancellationToken);

    /// <summary>解析封面（封面引用同样是容器路径）。</summary>
    public Task<WikiMediaResolution> ResolveImageAsync(string? refKey, CancellationToken cancellationToken = default)
        => ResolveCoreAsync(ImageKind, refKey, cancellationToken);

    /// <summary>
    /// 解析一批绑定（保持输入顺序）。
    ///
    /// <para><b>按 ref_key 去重</b>：同一页里同一张图出现十次也只解码一次；
    /// <b>带配额</b>（<see cref="MaxResolutionsPerPage"/>）：超出的给
    /// <see cref="WikiMediaResolution.None"/>，病态页面拖不住加载。</para>
    /// </summary>
    public async Task<IReadOnlyList<WikiMediaResolution>> ResolveManyAsync(
        IReadOnlyList<WikiResourceBinding> bindings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count == 0) return [];
        var cache = new Dictionary<string, WikiMediaResolution>(StringComparer.OrdinalIgnoreCase);
        var results = new List<WikiMediaResolution>(bindings.Count);
        var budget = MaxResolutionsPerPage;
        foreach (var binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = binding.RefKey;
            if (!string.IsNullOrEmpty(key) && cache.TryGetValue(key, out var cached))
            {
                results.Add(cached);
                continue;
            }
            if (budget <= 0)
            {
                results.Add(WikiMediaResolution.None);
                continue;
            }
            budget--;
            var resolution = await ResolveCoreAsync(binding.Kind, key, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(key)) cache[key] = resolution;
            results.Add(resolution);
        }
        Log.Debug("维基资源解析：绑定 {0} 条，实际解码 {1} 条，命中地址 {2} 条",
            bindings.Count, MaxResolutionsPerPage - budget, results.Count(r => r.MediaUrl is not null));
        return results;
    }

    private async Task<WikiMediaResolution> ResolveCoreAsync(
        string? kind, string? refKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refKey)) return WikiMediaResolution.None;
        // TODO(后续轮次)：Audio → BankIndex/BankAudio 解 bank 音频；Spine → SpineData 出
        // skeleton/atlas/textures。本轮只走 Image，其它形态一律给空结果 —— 拿不到真实地址
        // 就返回 null，绝不编造 URL。
        if (!string.Equals(kind, ImageKind, StringComparison.OrdinalIgnoreCase))
            return WikiMediaResolution.None;
        try
        {
            // 解码是 CPU/IO 密集的同步活，丢到线程池：IPC 跑在 WebView2 的消息线程上，
            // 同步解码 64 张图会直接冻住界面。
            var url = await Task.Run(() => MaterializeImage(refKey!, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return url is null ? WikiMediaResolution.None : new WikiMediaResolution(url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "维基资源解析失败（降级为无地址）：kind={0} ref={1}", kind, refKey);
            return WikiMediaResolution.None;
        }
    }

    /// <summary>
    /// 把一张图解成 <c>wwwroot/data/wiki/{hash}.png</c> 并给出它的地址。
    /// 文件已存在（且非空）就直接复用，跳过解码 —— 第二次打开同一页不再付解码钱。
    /// </summary>
    private string? MaterializeImage(string refKey, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(AppEnvironment.Current.BaseDirectory, "wwwroot", "data", "wiki");
        var fileName = $"{Hash($"{ImageKind}|{refKey}")}.png";
        var file = Path.Combine(directory, fileName);
        var existing = new FileInfo(file);
        if (existing is { Exists: true, Length: > 0 }) return DataHost + "wiki/" + fileName;

        var png = DecodeImage(refKey, cancellationToken);
        if (png is not { Length: > 0 }) return null;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(file, png);
        return DataHost + "wiki/" + fileName;
    }

    /// <summary>
    /// 容器路径 → PNG 字节。<b>Texture2D 必须优先</b>：同一个容器路径在索引里有
    /// Texture（type_id 28）与 Sprite（type_id 213）两行，而
    /// <c>ReadTexturePng</c> 只能按 Texture2D 的 pathId 解码，选中 Sprite 行必然返回 null
    /// （口径与 <c>PresetWorkbenchService.Preferable</c> 一致）。
    /// Texture 解不出来时再退到 Sprite 合成。
    /// </summary>
    private byte[]? DecodeImage(string refKey, CancellationToken cancellationToken)
    {
        var records = _catalog.FindByContainerEntry(refKey);
        if (records.Count == 0)
        {
            Log.Debug("维基图片未在索引里命中容器条目：{0}", refKey);
            return null;
        }

        AssetRecord? texture = null;
        AssetRecord? sprite = null;
        foreach (var record in records)
        {
            if (record.UnityPathId is null || string.IsNullOrWhiteSpace(record.SourcePath)) continue;
            if (record.UnityTypeId == UnityClassId.Texture2D || record.Type == AssetType.Texture)
                texture ??= record;
            else if (record.UnityTypeId == UnityClassId.Sprite || record.Type == AssetType.Sprite)
                sprite ??= record;
        }
        if (texture is null && sprite is null)
        {
            Log.Debug("维基图片命中的 {0} 行里没有可解码的 Texture/Sprite：{1}", records.Count, refKey);
            return null;
        }

        if (texture is not null)
        {
            var png = _unity.ReadTexturePng(texture.SourcePath!, texture.UnityPathId!.Value, cancellationToken);
            if (png is { Length: > 0 }) return png;
        }
        if (sprite is not null && !string.IsNullOrWhiteSpace(sprite.ContainerPath))
        {
            var composite = _unity.ReadBundleSpriteComposite(
                sprite.SourcePath!, sprite.ContainerPath!, sprite.UnityPathId!.Value, cancellationToken);
            if (composite?.Png is { Length: > 0 } png) return png;
        }
        Log.Debug("维基图片解码失败（Texture 与 Sprite 都解不出来）：{0}", refKey);
        return null;
    }

    /// <summary>源文件键 → 稳定文件名（SHA256 截断 32 位十六进制）。</summary>
    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32];
}
