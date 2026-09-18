using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Editing.Images;
using LimbusModEditor.Formats.Unity;
using NLog;

namespace LimbusModEditor.Application.SpineData;

/// <summary>
/// Spine 数据网关实现：从 Unity bundle 定位、读取并预解码 Spine 三件套。
/// 所有方法绝不抛异常；失败返回中文原因。
/// 不引用 WPF（铁律 §3-10）；使用既有 AssetsTools.NET 适配层与 ImageSharp 纹理解码。
/// </summary>
internal sealed class SpineDataGateway : ISpineDataGateway, IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly SpineAssetLocator _locator;
    private readonly SpineAssetEnumerator _enumerator;
    private readonly SpineCatalogEnumerator _catalog;
    private readonly string _dbPath;
    private readonly AssetsToolsBackend _backend = new();

    /// <summary>按 refKey 的结果缓存（见 <see cref="SpineResultCache"/> 里的取舍说明）。</summary>
    private readonly SpineResultCache _cache = new();

    public SpineDataGateway(string dbPath)
    {
        _dbPath = dbPath;
        _locator = new SpineAssetLocator(dbPath);
        _enumerator = new SpineAssetEnumerator(dbPath);
        _catalog = new SpineCatalogEnumerator(dbPath);
    }

    /// <summary>缓存诊断（日志/报告用）。</summary>
    public string DescribeCache() => _cache.Describe();

    public void InvalidateCache() => _cache.Clear();

    public Task<SpineCatalogPage> BrowseCatalogAsync(
        SpineCatalogQuery query, CancellationToken cancellationToken = default)
        => Task.Run(() => _catalog.Query(query, RelationDbPath()), cancellationToken);

    public Task<SpineCatalogResult> BrowseCatalogWithSummaryAsync(
        SpineCatalogQuery query, CancellationToken cancellationToken = default)
        => Task.Run(() => _catalog.QueryWithSummary(query, RelationDbPath()), cancellationToken);

    public Task<SpineCatalogSummary> SummarizeCatalogAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => _catalog.Summarize(RelationDbPath()), cancellationToken);

    /// <summary>
    /// 绑定表所在库：与索引库同目录的 <c>relation-index.db</c>。索引库不在默认位置
    /// （测试/CLI 显式传路径）时按同目录推，推不到就让名册按「全部未绑定」处理（降级不抛）。
    /// </summary>
    private string? RelationDbPath()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (string.IsNullOrWhiteSpace(directory)) return null;
        return Path.Combine(directory, Caching.WorkbenchCachePaths.RelationIndexFileName);
    }

    public async Task<(SpineRawData? Data, string? Error)> GetSpineDataAsync(
        string assetId, CancellationToken cancellationToken = default)
    {
        // assetId 格式：containerEntry（纯数据定位，不猜测 id）
        return await GetSpineDataByPathAsync(assetId, cancellationToken);
    }

    public async Task<(SpineRawData? Data, string? Error)> GetSpineDataByPathAsync(
        string containerEntry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(containerEntry))
            return (null, "容器路径为空，无法获取 Spine 数据。");

        if (!SpinePathRules.IsSpinePath(containerEntry))
            return (null, $"路径 \"{containerEntry}\" 不是 Spine 资源。");

        // 缓存命中就走人 —— **在这之前不能做任何索引查询**：
        // FindSiblings 是按目录前缀扫 assets（同目录行数可能上千），实测约 1s，
        // 放在缓存查询之前等于「缓存只省下解包钱、把索引钱照付」，命中也就不快了。
        if (_cache.TryGet(containerEntry, out var cachedData, out var cachedError))
            return (cachedData, cachedError);

        // 未命中才查一次「这条路径落在哪个 bundle」，作为失败记录「bundle 之后出现了
        // 就作废重试」的凭据（见 SpineResultCache.TryGet）。
        var probe = ProbeBundlePath(containerEntry);
        try
        {
            var result = await Task.Run(() => LoadSpineData(containerEntry, cancellationToken), cancellationToken);
            _cache.Set(containerEntry, probe, result.Data, result.Error);
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            Log.Warn(ex, "获取 Spine 数据失败：{0}", containerEntry);
            var error = $"获取 Spine 数据失败：{ex.Message}";
            _cache.Set(containerEntry, probe, null, error);
            return (null, error);
        }
    }

    /// <summary>
    /// 这条容器路径落在哪个 bundle 文件上（只做一次轻量索引查询，不解包）。
    /// 用作缓存的「失败记录是否该作废」的凭据：bundle 之前不在、现在在了就重试。
    /// </summary>
    private string? ProbeBundlePath(string containerEntry)
    {
        try
        {
            var siblings = _locator.FindSiblings(containerEntry);
            var self = SelfRows(containerEntry, siblings).FirstOrDefault() ?? siblings.FirstOrDefault();
            return self?.BundleDataPath;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "查 bundle 路径失败（不影响取数，只是缓存凭据缺失）：{0}", containerEntry);
            return null;
        }
    }

    public async Task<IReadOnlyList<SpineRawData>> GetSpineDataBatchAsync(
        IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default)
    {
        var results = new List<SpineRawData>();
        foreach (var id in assetIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (data, error) = await GetSpineDataAsync(id, cancellationToken);
            if (data is not null) results.Add(data);
        }
        return results;
    }

    private (SpineRawData? Data, string? Error) LoadSpineData(string containerEntry, CancellationToken cancellationToken)
    {
        // 1. 定位同目录资源
        var siblings = _locator.FindSiblings(containerEntry);
        if (siblings.Count == 0)
            return (null, $"找不到 \"{containerEntry}\" 所在的 Spine 资源目录。");

        // 2. 分类：骨架 JSON、图集文本、纹理
        var skeletonEntry = FindSkeletonEntry(containerEntry, siblings);
        var atlasEntry = FindAtlasEntry(siblings);
        var textureEntries = FindTextureEntries(siblings);

        // SpinalIllustPrefab 这类 prefab：同目录什么都没有，三件套藏在它的引用链里
        // —— 先看引用链，失败再走「同目录三件套」的老路（两条路都不成立才报错）。
        var selfRows = SelfRows(containerEntry, siblings);
        if (selfRows.Count > 0)
        {
            var viaChain = FollowPrefabChain(selfRows);
            if (viaChain.Data is not null) return viaChain;
        }

        if (skeletonEntry is null)
        {
            var viaChain = siblings.Count > 0
                ? $"；也没有从 \"{containerEntry}\" 的引用链里找到骨架与图集"
                : string.Empty;
            return (null, $"同目录里找不到骨架 JSON（*.json），无法获取 Spine 数据。{viaChain}");
        }

        if (atlasEntry is null)
            return (null, $"同目录里找不到图集文本（*.atlas.txt），无法获取 Spine 数据。");

        // 3. 读取骨架字节
        var skeletonBytes = ReadTextAssetBytes(skeletonEntry, cancellationToken);
        if (skeletonBytes.Length == 0)
            return (null, $"骨架 JSON 读取失败（{skeletonEntry.ContainerEntry}）：可能是空文件或不在可读容器里。");

        var skeletonFormat = SpinePathRules.IsSkeletonJsonFileName(SpinePathRules.FileNameOf(skeletonEntry.ContainerEntry))
            ? "json" : "binary";

        // 4. 读取图集文本
        var atlasText = ReadTextAssetText(atlasEntry, cancellationToken);
        if (string.IsNullOrWhiteSpace(atlasText))
            return (null, $"图集文本读取失败（{atlasEntry.ContainerEntry}）：可能是空文件或不在可读容器里。");

        // 5. 读取并解码纹理
        var pageBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var texEntry in textureEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageName = SpinePathRules.FileNameOf(texEntry.ContainerEntry);
            try
            {
                var png = ReadTexturePng(texEntry, cancellationToken);
                if (png.Length > 0)
                    pageBytes[pageName] = png;
                else
                    warnings.Add($"{pageName}：解码结果为空");
            }
            catch (Exception ex)
            {
                warnings.Add($"{pageName}：{ex.Message}");
                Log.Warn(ex, "Spine 纹理解码失败：{0}", texEntry.ContainerEntry);
            }
        }

        var folder = SpinePathRules.FolderOf(containerEntry);
        var label = folder.Length == 0 ? SpinePathRules.FileNameOf(containerEntry)
            : folder[(folder.LastIndexOf('/') + 1)..];

        var data = new SpineRawData(
            skeletonBytes,
            skeletonFormat,
            atlasText,
            pageBytes,
            textureEntries.Select(s => SpinePathRules.FileNameOf(s.ContainerEntry)).ToList(),
            string.IsNullOrWhiteSpace(label) ? "spine" : label
        );

        if (warnings.Count > 0)
            Log.Warn("Spine 数据获取完成，有 {0} 个纹理警告：{1}", warnings.Count, string.Join("；", warnings.Take(3)));
        else
            Log.Info("Spine 数据获取完成：骨架 {0} 字节，图集 {1} 字节，{2} 页纹理",
                skeletonBytes.Length, atlasText.Length, pageBytes.Count);

        return (data, null);
    }

    /// <summary>请求的这一条自己在索引库里的行（同目录查出来时它就在里面）。</summary>
    private static SpineAssetInfo? SelfEntry(string containerEntry, IReadOnlyList<SpineAssetInfo> siblings)
        => siblings.FirstOrDefault(s =>
            string.Equals(s.ContainerEntry, containerEntry, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 请求的这一条在索引库里的<b>全部</b>行，按「最像 prefab 根节点」的顺序排好。
    ///
    /// <para><b>为什么必须取全部行</b>：同一个容器路径在真实索引库里会出现<b>两行</b>——
    /// 一行 <c>type_id=1</c>（Unity <c>GameObject</c>，序列化正文很小，是 prefab 的真根节点），
    /// 一行 <c>type_id=43</c>（<c>PrefabImporter</c>，正文里存的是导入设置，<b>不是</b> prefab 的
    /// 对象图）。实测：<c>Prefab/SD/**</c> 下 818 个 type-1 行对应 696 个 type-43 行，
    /// 其中 <b>682 个路径同时有两行</b>（分布大致各半：339 + 338 + 5）。
    /// 老实现只取「第一行」，于是大约一半的 SD prefab 会先在 type-43 那一行上走引用链、
    /// 走不通就报「引用链里没有骨架与图集」——<b>而它旁边的 type-1 行本来是能解开的</b>
    /// （实测 <c>10103_Yisang_SwordGroupAppearance.prefab</c>：type-43 行解不出、type-1 行能解出
    /// 骨架 <c>Yisang_blade_idle</c> 24348 字节 + 图集 592 字节 + 1 页纹理）。</para>
    ///
    /// <para><c>SpineIllustPrefab</c> 那批（123 行）<b>只有 type-1</b>，所以从来没暴露这个问题 ——
    /// 这也解释了为什么「页面绑定口径」看起来是通的，而「全库口径」大面积报解不出。</para>
    ///
    /// <para>排序：<c>type_id=1</c> 优先（真根节点），其余按索引顺序兜底；
    /// 逐个试，任一个解开就算成功 —— 不猜测，只多用一次机会。</para>
    /// </summary>
    private static IReadOnlyList<SpineAssetInfo> SelfRows(
        string containerEntry, IReadOnlyList<SpineAssetInfo> siblings)
    {
        var rows = siblings
            .Where(s => string.Equals(s.ContainerEntry, containerEntry, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.Count <= 1) return rows;
        return rows.OrderByDescending(r => r.TypeId == 1).ToList();
    }

    /// <summary>
    /// 第二条取数路径：<b>prefab 引用链</b>。
    ///
    /// <para>为什么需要它：维基里 134 条 Spine 绑定指向的是
    /// <c>Assets/Resources_moved/Prefab/SpineIllustPrefab/*.prefab</c>，而这个目录里<b>只有 prefab</b>
    /// —— 骨架 / 图集 / 纹理都<b>没有登记在 bundle 的容器表</b>里（索引库里查不到它们的行），
    /// 只能从 prefab 的 GameObject 逐跳跟着 PPtr 找到 <c>SkeletonGraphic</c> →
    /// <c>SkeletonDataAsset</c> → 骨架 TextAsset / 图集 TextAsset / 纹理。</para>
    ///
    /// <para>取出来的一律按<b>正文</b>判定是不是真的骨架 / 图集（判据见
    /// <see cref="SpinePrefabChainResolver"/>），纹不出来就返回 null，交给同目录那条路继续报错。</para>
    ///
    /// <para><b>逐个候选行试</b>：同一路径在索引库里可能有多行（见 <see cref="SelfRows"/>），
    /// 其中只有 <c>type_id=1</c>（GameObject）那一行是 prefab 的真根节点。全部试一遍，
    /// 任一个解开就用它 —— 不猜测谁是根，只是不放弃本来就能解开的那一行。</para>
    /// </summary>
    private (SpineRawData? Data, string? Error) FollowPrefabChain(IReadOnlyList<SpineAssetInfo> prefabRows)
    {
        foreach (var prefab in prefabRows)
        {
            var (data, error) = FollowPrefabChain(prefab);
            if (data is not null) return (data, null);
            if (error is not null) return (null, error);
        }
        return (null, null); // 都不是能解的 prefab：交给同目录那条路径的原报错
    }

    private (SpineRawData? Data, string? Error) FollowPrefabChain(SpineAssetInfo prefab)
    {
        var entry = prefab.ContainerEntry;
        var isPrefab = entry.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || prefab.TypeId == 1;
        if (!isPrefab || string.IsNullOrWhiteSpace(prefab.BundleDataPath) || string.IsNullOrWhiteSpace(prefab.ContainerName))
            return (null, null);

        SpinePrefabChain chain;
        try
        {
            using var reader = _backend.CreateBundleSession(prefab.BundleDataPath);
            var resolver = new SpinePrefabChainResolver(reader, prefab.ContainerName);
            chain = resolver.Resolve(prefab.PathId)
                ?? throw new InvalidDataException("引用链里没有同时找到「像骨架的 TextAsset」与「像图集的 TextAsset」。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or KeyNotFoundException)
        {
            Log.Debug("prefab 引用链没能解开：{0} —— {1}", entry, ex.Message);
            return (null, null); // 不是错误：交给同目录那条路径的原报错
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn(ex, "prefab 引用链解析失败：{0}", entry);
            return (null, null);
        }

        // 纹理页：按图集里页面的名字给键（前端按名字/裸名两种口径取）。
        var pageNames = SpineAtlasPageNames.Read(chain.AtlasText);
        var warnings = new List<string>();
        if (pageNames.Count != chain.TexturePathIds.Count && pageNames.Count > 0)
            warnings.Add($"图集 {pageNames.Count} 页与 {chain.TexturePathIds.Count} 张纹理数量不一致，按页名前 {Math.Min(pageNames.Count, chain.TexturePathIds.Count)} 个对齐");

        var pageBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < chain.TexturePathIds.Count; i++)
        {
            var pageName = i < pageNames.Count ? pageNames[i] : $"page{i}.png";
            try
            {
                var png = ReadTexturePng(prefab.BundleDataPath, chain.TexturePathIds[i]);
                if (png.Length > 0) pageBytes[pageName] = png;
                else warnings.Add($"{pageName}：纹理解码结果为空");
            }
            catch (Exception ex)
            {
                warnings.Add($"{pageName}：{ex.Message}");
                Log.Warn(ex, "Spine 纹理解码失败（prefab 引用链）：{0}", entry);
            }
        }

        var label = SpinePathRules.FileNameOf(entry);
        if (label.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            label = label[..^".prefab".Length];

        Log.Info("prefab 引用链取到 Spine 三件套：{0} · 骨架 {1} 字节 · 图集 {2} 字节 · 纹理 {3} 页",
            entry, chain.SkeletonBytes.Length, chain.AtlasText.Length, pageBytes.Count);
        if (warnings.Count > 0) Log.Warn("prefab 引用链的纹理告警：{0}", string.Join("；", warnings.Take(3)));

        return (new SpineRawData(
            chain.SkeletonBytes,
            chain.Binary ? "binary" : "json",
            chain.AtlasText,
            pageBytes,
            pageBytes.Keys.ToList(),
            string.IsNullOrWhiteSpace(label) ? "spine" : label), null);
    }

    private SpineAssetInfo? FindSkeletonEntry(string selfEntry, IReadOnlyList<SpineAssetInfo> siblings)
    {
        // 自身是 .json 且不是 .atlas.json → 直接用
        if (SpinePathRules.IsSkeletonJsonFileName(SpinePathRules.FileNameOf(selfEntry)))
            return siblings.FirstOrDefault(s => string.Equals(s.ContainerEntry, selfEntry, StringComparison.OrdinalIgnoreCase));

        // 否则找同目录第一个 .json（排除 .atlas.json）
        return siblings.FirstOrDefault(s =>
        {
            var name = SpinePathRules.FileNameOf(s.ContainerEntry);
            return (s.TypeId == 49 /* TextAsset */ || s.TypeId == 4) && SpinePathRules.IsSkeletonJsonFileName(name);
        });
    }

    private SpineAssetInfo? FindAtlasEntry(IReadOnlyList<SpineAssetInfo> siblings)
    {
        return siblings.FirstOrDefault(s =>
        {
            var name = SpinePathRules.FileNameOf(s.ContainerEntry);
            return (s.TypeId == 49 /* TextAsset */ || s.TypeId == 4) && SpinePathRules.IsAtlasFileName(name);
        });
    }

    private IReadOnlyList<SpineAssetInfo> FindTextureEntries(IReadOnlyList<SpineAssetInfo> siblings)
    {
        return siblings.Where(s =>
        {
            var name = SpinePathRules.FileNameOf(s.ContainerEntry);
            return s.TypeId == 28 /* Texture2D */ || s.TypeId == 213 || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        }).ToArray();
    }

    private byte[] ReadTextAssetBytes(SpineAssetInfo entry, CancellationToken cancellationToken)
    {
        try
        {
            var text = _backend.ReadBundleTextAsset(entry.BundleDataPath, entry.ContainerName, entry.PathId, cancellationToken);
            return text.Data;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "读取文本失败：{0}", entry.ContainerEntry);
            return [];
        }
    }

    private string ReadTextAssetText(SpineAssetInfo entry, CancellationToken cancellationToken)
    {
        var bytes = ReadTextAssetBytes(entry, cancellationToken);
        if (bytes.Length == 0) return string.Empty;
        try
        {
            return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return string.Empty;
        }
    }

    private byte[] ReadTexturePng(SpineAssetInfo entry, CancellationToken cancellationToken)
        => ReadTexturePng(entry.BundleDataPath, entry.PathId, cancellationToken);

    /// <summary>解码一张纹理为 PNG（prefab 引用链那条路只有 path_id，没有容器路径）。</summary>
    private byte[] ReadTexturePng(string bundleDataPath, long pathId, CancellationToken cancellationToken = default)
    {
        var texture = _backend.ReadTexture(bundleDataPath, pathId, cancellationToken)
            ?? throw new InvalidDataException($"纹理读取失败（bundle={Path.GetFileName(bundleDataPath)}，path_id={pathId}）。");

        // DXT5/DXT1 → 预解码；RGBA32/RGB24/BGRA32/ARGB32 → 直接编码；
        // 其余格式也交给同一个解码器尝试（不支持的由它给出中文原因）。
        var info = new UnityTextureInfo(texture.Width, texture.Height,
            (UnityTexturePixelFormat)texture.TextureFormat, texture.PixelData);
        return new UnityTextureCodec().ToPng(info);
    }

    public void Dispose()
    {
        _backend.Dispose();
    }

    public Task<IReadOnlyList<SpineSetInfo>> EnumerateCompleteSetsAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => _enumerator.EnumerateCompleteSets(cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<SpineSetInfo>> FindByFolderPrefixAsync(
        string folderPrefix, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPrefix))
            return Task.FromResult<IReadOnlyList<SpineSetInfo>>([]);
        return Task.Run(() => _enumerator.FindByFolderPrefix(folderPrefix, cancellationToken), cancellationToken);
    }
}

/// <summary>
/// 网关工厂：创建配置好的 ISpineDataGateway 实例。
/// </summary>
public static class SpineDataGatewayFactory
{
    /// <summary>创建网关实例（使用默认索引库路径）。</summary>
    public static ISpineDataGateway Create(string? dbPath = null)
    {
        dbPath ??= System.IO.Path.Combine(
            AppContext.BaseDirectory, "cache", "unity-cache-index.db");
        return new SpineDataGateway(dbPath);
    }
}
