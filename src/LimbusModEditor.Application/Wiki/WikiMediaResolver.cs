using System.Security.Cryptography;
using System.Text;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Formats.Bank;
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

    /// <summary>
    /// 一次页面加载<b>每种形态</b>各解多少条。配额按形态分开：共用一份会让图片先把
    /// 预算吃光，同页的音频永远排不上号；图片解码实测约 5 秒一条，所以额度必须紧。
    /// </summary>
    private const int ImageQuotaPerPage = 16;

    /// <summary>音频形态配额（bank 读的代价由 FSB 缓存摊掉，一条 = 一次解码）。</summary>
    private const int AudioQuotaPerPage = 16;

    /// <summary>Spine 形态配额（骨架 + 图集 + 多页纹理，三件套最贵，额度最小）。</summary>
    private const int SpineQuotaPerPage = 4;

    /// <summary>最多在内存里留几个 FSB 分片（一个 big bank 的 FSB 就上百 MB，必须封顶）。</summary>
    private const int MaxCachedFsbs = 2;

    /// <summary>FSB 缓存的字节上限（256 MB）。</summary>
    private const long MaxCachedFsbBytes = 256L * 1024 * 1024;

    /// <summary>最多缓存几个 bank 的样本表（每张几千行，封顶 8 张）。</summary>
    private const int MaxCachedSampleTables = 8;

    private const string ImageKind = "Image";
    private const string AudioKind = "Audio";
    private const string SpineKind = "Spine";

    /// <summary>落地文件对外的地址前缀（<c>lme.data</c> 虚拟主机的根）。</summary>
    private const string DataHost = "https://lme.data/";

    private readonly AssetCatalog _catalog;
    private readonly UnityAssetService _unity = new();
    private readonly string? _fmodDirectory;

    /// <summary>FSB 分片缓存（<c>bankPath|fsbIndex</c> → FSB 字节）与样本表缓存的锁。</summary>
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, byte[]> _fsbCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _fsbOrder = [];
    private long _fsbCacheBytes;
    private readonly Dictionary<string, IReadOnlyList<BankSampleRecord>> _sampleCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _sampleOrder = [];

    public WikiMediaResolver(
        AssetCatalog catalog,
        BankIndexService bankIndex,
        ISpineDataGateway spineData,
        BankAudioService? bankAudio = null,
        string? fmodDirectory = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        BankIndex = bankIndex;
        SpineData = spineData;
        BankAudio = bankAudio;
        // 显式目录优先（测试用它给 DLL 路径）；未指定时走「共享配置 → 项目 → 自动发现」。
        _fmodDirectory = string.IsNullOrWhiteSpace(fmodDirectory) ? null : fmodDirectory;
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
        => ResolveCoreAsync(binding?.Kind, binding?.RefKey, binding?.DeepLink, cancellationToken);

    /// <summary>解析封面（封面引用同样是容器路径）。</summary>
    public Task<WikiMediaResolution> ResolveImageAsync(string? refKey, CancellationToken cancellationToken = default)
        => ResolveCoreAsync(ImageKind, refKey, null, cancellationToken);

    /// <summary>
    /// 解析一批绑定（保持输入顺序）。
    ///
    /// <para><b>按 ref_key 去重</b>：同一页里同一张图出现十次也只解码一次；
    /// <b>配额按形态分开</b>（<see cref="ImageQuotaPerPage"/> / <see cref="AudioQuotaPerPage"/> /
    /// <see cref="SpineQuotaPerPage"/>）：超出的给 <see cref="WikiMediaResolution.None"/>，
    /// 病态页面拖不住加载，某一种形态也吃不掉别人的预算。</para>
    /// </summary>
    public async Task<IReadOnlyList<WikiMediaResolution>> ResolveManyAsync(
        IReadOnlyList<WikiResourceBinding> bindings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count == 0) return [];
        var cache = new Dictionary<string, WikiMediaResolution>(StringComparer.OrdinalIgnoreCase);
        var budgets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [ImageKind] = ImageQuotaPerPage,
            [AudioKind] = AudioQuotaPerPage,
            [SpineKind] = SpineQuotaPerPage,
        };
        var results = new List<WikiMediaResolution>(bindings.Count);
        var resolved = 0;
        foreach (var binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = binding.RefKey;
            if (!string.IsNullOrEmpty(key) && cache.TryGetValue(key, out var cached))
            {
                results.Add(cached);
                continue;
            }
            if (!budgets.TryGetValue(binding.Kind, out var left) || left <= 0)
            {
                // 未支持的形态（除 Image / Audio / Spine 之外的 kind）与超配额一样给空结果。
                results.Add(WikiMediaResolution.None);
                continue;
            }
            budgets[binding.Kind] = left - 1;
            resolved++;
            var resolution = await ResolveCoreAsync(binding.Kind, key, binding.DeepLink, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(key)) cache[key] = resolution;
            results.Add(resolution);
        }
        Log.Debug("维基资源解析：绑定 {0} 条，实际解码 {1} 条，命中地址 {2} 条",
            bindings.Count, resolved, results.Count(r => r.MediaUrl is not null || r.AudioUrl is not null
                || r.SkeletonUrl is not null));
        return results;
    }

    /// <summary>
    /// 解析一条绑定。<b>拿不到真实地址一律给 null，绝不编造 URL</b>；失败也不抛 ——
    /// 一条资源解不出来不能让整页加载失败。
    /// </summary>
    private Task<WikiMediaResolution> ResolveCoreAsync(
        string? kind, string? refKey, CancellationToken cancellationToken)
        => ResolveCoreAsync(kind, refKey, null, cancellationToken);

    /// <summary>
    /// 解析一条绑定（带 <c>deep_link</c> —— 音频用它定位 bank 里的那一条样本）。
    ///
    /// <para>Image：<c>Unity 容器路径 → 纹理/精灵 → PNG</c>；
    /// Audio：<c>bank 路径 + '\0' + 样本名 → 索引行 → FSB → FMOD 解 WAV</c>；
    /// Spine：<c>Prefab 容器路径 → ISpineDataGateway 三件套</c>。</para>
    /// </summary>
    private async Task<WikiMediaResolution> ResolveCoreAsync(
        string? kind, string? refKey, string? deepLink, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refKey)) return WikiMediaResolution.None;
        try
        {
            // Audio：<c>bank 路径 + '\0' + 样本名 → 索引行 → FSB → FMOD 解 WAV</c>。
            if (string.Equals(kind, AudioKind, StringComparison.OrdinalIgnoreCase))
                return await ResolveAudioAsync(deepLink, refKey!, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(kind, ImageKind, StringComparison.OrdinalIgnoreCase))
                return WikiMediaResolution.None;
            // 解码是 CPU/IO 密集的同步活，丢到线程池：IPC 跑在 WebView2 的消息线程上，
            // 同步解码十几张图 / 十几条语音会直接冻住界面。
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

    // ── 音频：bank 路径 + '\0' + 样本名 → WAV ────────────────────────

    /// <summary>
    /// 把一条音频绑定解成 <c>wwwroot/data/wiki/{hash}.wav</c>（文件名按
    /// 「bank 路径 + 样本名」哈希，稳定可复用）并填进 <see cref="WikiMediaResolution.AudioUrl"/>。
    /// </summary>
    private async Task<WikiMediaResolution> ResolveAudioAsync(
        string? deepLink, string refKey, CancellationToken cancellationToken)
    {
        var (bankPath, eventName) = SplitAudioPayload(deepLink, refKey);
        if (bankPath is null || eventName is null)
        {
            Log.Debug("维基音频绑定的载荷不是「bank 路径 + NUL + 事件名」，无法定位：ref={0}", refKey);
            return WikiMediaResolution.None;
        }

        var fileName = $"{Hash($"{AudioKind}|{bankPath}|{eventName}")}.wav";
        if (ExistingWikiUrl(fileName) is { } hit) return new WikiMediaResolution(null, AudioUrl: hit);

        var sample = FindBankSample(bankPath, eventName);
        if (sample is null)
        {
            Log.Debug("维基音频未在 bank 索引里命中样本：{0}（bank {1}）", eventName, Path.GetFileName(bankPath));
            return WikiMediaResolution.None;
        }

        if (BankAudio is null) return WikiMediaResolution.None;
        var fsb = await ReadFsbAsync(bankPath, sample.FsbIndex, cancellationToken).ConfigureAwait(false);
        if (fsb is not { Length: > 0 })
        {
            Log.Debug("维基音频取不到 FSB 分片：bank={0} fsbIndex={1}", Path.GetFileName(bankPath), sample.FsbIndex);
            return WikiMediaResolution.None;
        }

        var directory = ResolveFmodDirectory();
        if (string.IsNullOrWhiteSpace(directory))
        {
            // FMOD 不在，就诚实地说「没有音频地址」——不猜、不造 URL。
            Log.Warn("维基音频降级为无地址：FMOD DLL 目录不可用（样本 {0}）。", eventName);
            return WikiMediaResolution.None;
        }

        byte[] wave;
        try
        {
            using var codec = new NativeFmodAudioCodec(directory);
            if (!codec.IsAvailable)
            {
                Log.Warn("维基音频降级为无地址：FMOD 解码器不可用（目录 {0}）。", directory);
                return WikiMediaResolution.None;
            }
            wave = await codec.DecodeFsbToWaveAsync(fsb, sample.SampleIndex, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DllNotFoundException ex)
        {
            Log.Warn(ex, "维基音频降级为无地址：FMOD DLL 加载失败（目录 {0}）。", directory);
            return WikiMediaResolution.None;
        }
        if (wave is not { Length: > 0 }) return WikiMediaResolution.None;

        var url = WriteWikiFile(fileName, wave);
        return url is null ? WikiMediaResolution.None : new WikiMediaResolution(null, AudioUrl: url);
    }

    /// <summary>
    /// 拆开音频绑定：<c>deep_link</c> 与 <c>ref_key</c> 都是
    /// <c>bank 路径 + '\0' + 样本名</c> 的口径（<see cref="RelationDeepLink"/>），
    /// 两个都试一遍，取第一个「首段是磁盘上存在的 .bank 且有第二段」的载荷。
    /// </summary>
    private static (string? BankPath, string? EventName) SplitAudioPayload(string? deepLink, string refKey)
    {
        foreach (var payload in new[] { deepLink, refKey })
        {
            if (string.IsNullOrEmpty(payload)) continue;
            var parts = RelationDeepLink.Decode(payload);
            if (parts.Count < 2) continue;
            var bankPath = parts[0];
            var eventName = parts[1];
            if (string.IsNullOrWhiteSpace(bankPath) || string.IsNullOrWhiteSpace(eventName)) continue;
            if (!bankPath.EndsWith(".bank", StringComparison.OrdinalIgnoreCase) || !File.Exists(bankPath)) continue;
            return (bankPath, eventName);
        }
        return (null, null);
    }

    /// <summary>在 bank 索引里按 <c>(bank_path, name)</c> 精确命中样本行。</summary>
    private BankSampleRecord? FindBankSample(string bankPath, string eventName)
    {
        foreach (var sample in SamplesOf(bankPath))
        {
            if (string.Equals(sample.Name, eventName, StringComparison.Ordinal)) return sample;
        }
        return null;
    }

    /// <summary>
    /// 读某个 bank 的样本行（带小容量缓存：一页十几条语音可能全在同一个 bank 里，
    /// 每次都查库没必要）。索引库不存在时返回空集合 —— 不建库、不猜样本。
    /// </summary>
    private IReadOnlyList<BankSampleRecord> SamplesOf(string bankPath)
    {
        lock (_cacheGate)
        {
            if (_sampleCache.TryGetValue(bankPath, out var cached)) return cached;
        }
        if (BankIndex?.Store is not { Exists: true }) return [];

        var rows = BankIndex.Store.ReadSamples(bankPath);
        lock (_cacheGate)
        {
            _sampleCache[bankPath] = rows;
            if (!_sampleOrder.Contains(bankPath)) _sampleOrder.Add(bankPath);
            while (_sampleOrder.Count > MaxCachedSampleTables)
                _sampleCache.Remove(_sampleOrder[0]);
            _sampleOrder.RemoveAt(0);
        }
        return rows;
    }

    /// <summary>
    /// 取某个 FSB 分片（走 <see cref="BankAudioService"/>，不写第二套 bank 解析）。
    /// 一个 voice bank 上百 MB，每条语音读一遍文件系统是页面加载变慢的主因，
    /// 因此这里按 <c>bankPath|fsbIndex</c> 缓存（<see cref="MaxCachedFsbs"/> 条 / 256 MB 封顶）。
    /// </summary>
    private async Task<byte[]?> ReadFsbAsync(string bankPath, int fsbIndex, CancellationToken cancellationToken)
    {
        var key = $"{bankPath}|{fsbIndex}";
        lock (_cacheGate)
        {
            if (_fsbCache.TryGetValue(key, out var cached)) return cached;
        }
        if (BankAudio is null) return null;

        var asset = new AssetRecord { SourcePath = bankPath, LogicalPath = $"fsb/{fsbIndex}" };
        cancellationToken.ThrowIfCancellationRequested();
        var fsb = await BankAudio.ReadFsbAsync(asset, cancellationToken).ConfigureAwait(false);
        if (fsb is not { Length: > 0 }) return null;

        lock (_cacheGate)
        {
            _fsbCache[key] = fsb;
            _fsbCacheBytes += fsb.Length;
            _fsbOrder.Add(key);
            while (_fsbOrder.Count > MaxCachedFsbs || _fsbCacheBytes > MaxCachedFsbBytes)
            {
                if (_fsbOrder.Count == 0) break;
                var oldest = _fsbOrder[0];
                _fsbOrder.RemoveAt(0);
                if (_fsbCache.Remove(oldest, out var dropped)) _fsbCacheBytes -= dropped.Length;
            }
        }
        return fsb;
    }

    /// <summary>FMOD DLL 目录：显式指定 → 「共享配置 → 项目 → 自动发现」。</summary>
    private string? ResolveFmodDirectory()
        => _fmodDirectory ?? AppEnvironment.Current.EffectiveFmodLibraryDirectory(null);

    // ── 落地：{BaseDirectory}/wwwroot/data/wiki ──────────────────────

    /// <summary>落地文件的绝对路径。</summary>
    private static string WikiFilePath(string fileName)
        => Path.Combine(AppEnvironment.Current.BaseDirectory, "wwwroot", "data", "wiki", fileName);

    /// <summary>文件已存在且非空就给出地址（跳过解码 / 二次编码）。</summary>
    private static string? ExistingWikiUrl(string fileName)
    {
        var info = new FileInfo(WikiFilePath(fileName));
        return info is { Exists: true, Length: > 0 } ? DataHost + "wiki/" + fileName : null;
    }

    /// <summary>把字节写进 <c>wwwroot/data/wiki</c> 并给出它的对外地址。</summary>
    private static string? WriteWikiFile(string fileName, byte[] bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        var file = WikiFilePath(fileName);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes);
            return DataHost + "wiki/" + fileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "维基资源落地失败：{0}", file);
            return null;
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
