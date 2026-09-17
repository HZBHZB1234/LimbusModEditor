using System.Diagnostics;
using LimbusModEditor.Application.Caching;
using LimbusModEditor.Formats.Unity;
using NLog;

namespace LimbusModEditor.Application.StaticMods;

/// <summary>
/// plan-12：静态数据表索引的编排——「定位 bundle → 枚举 TextAsset → 只留下元数据落库」。
///
/// <para><b>为什么要丢弃正文</b>：<c>StaticBundleLocator.ReadTextAssetEntries</c> 会把 1392 张表的
/// 正文<b>全部</b>读进内存（它返回的每条 <c>StaticTextAsset</c> 都带 <c>byte[] Data</c>），
/// 这是静态工作台「进页面慢、内存高」的根因。本服务在枚举过程中<b>逐张取元数据后立即丢弃正文</b>
/// （不持有 <c>Data</c>），因此索引阶段的峰值内存与表数无关，只与单张最大表有关。
/// 正文只在用户真正打开某张表时按需读取（<c>documents</c> 有界缓存）。</para>
///
/// <para><b>缓存只影响速度</b>：删掉 <c>cache/static-tables.db</c> 后功能完全不受影响，
/// 只是每次进页面要重新枚举一遍 bundle（本机约 8 秒）。</para>
/// </summary>
public sealed class StaticIndexService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly StaticTableIndexStore _store;

    /// <param name="store">索引库（页面传 <c>new StaticTableIndexStore(host.Env.CacheDirectory)</c>）。</param>
    public StaticIndexService(StaticTableIndexStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>索引库（页面诊断 / 清缓存用）。</summary>
    public StaticTableIndexStore Store => _store;

    /// <summary>从 catalog 动态定位静态数据 bundle（每次调用都重新解析，绝不缓存 hash）。</summary>
    public static StaticBundleLocation? Locate(string? gameDirectory, IEnumerable<string?> cacheRoots)
        => StaticBundleLocator.Locate(gameDirectory, cacheRoots);

    /// <summary>缓存根候选（共享配置里的 Unity 缓存目录 + LCTA 事实中的迁移盘缓存）。</summary>
    public static IReadOnlyList<string> CacheRoots(string? unityCacheDirectory)
        => StaticBundleLocator.WithMigratedCacheRoot([unityCacheDirectory]);

    /// <summary>
    /// 给「只读派生链路」（维基生成 / 基础数据分节）定位静态 bundle 的两级定位：
    /// 先按索引里存的源键在缓存根下探 <c>&lt;外层键&gt;/&lt;源键&gt;/__data</c>（快路径，不解析 catalog）；
    /// 探不到 → 回落 catalog 全路径重新定位。
    ///
    /// <para><b>为什么要回落</b>：官方热修会更换静态 bundle 的内层内容哈希，并把旧条目从 Unity 缓存里
    /// 清掉 —— 此时索引里的 <c>source_key</c> 还指着旧哈希，只走探针会「静默丢掉静态表这路输入」
    /// （实测 2026-09-17：<c>fa6984a9…</c> 已从缓存消失，catalog 已换成 <c>fadb2926…</c>，
    /// 于是人格页的「数据/技能/被动」三节全部不生成）。语义与
    /// <see cref="Scanning.StartupScanService"/> 步骤④「探针不成立 → catalog 全路径」一致。</para>
    /// </summary>
    public static StaticBundleLocation? LocateForReads(
        StaticTableIndexStore store, string? gameDirectory, IReadOnlyList<string> cacheRoots)
    {
        ArgumentNullException.ThrowIfNull(store);
        var sourceKey = store.ReadSourceKey();
        if (!string.IsNullOrWhiteSpace(sourceKey))
        {
            var probe = new StaticBundleLocation(
                $"{StaticBundleLocator.BundleNamePrefix}{sourceKey}.bundle", sourceKey, OuterKey: null, Record: null);
            var cached = StaticBundleLocator.LocateInCache(probe, cacheRoots);
            if (cached.IsCached) return cached;
            Log.Info("静态表索引的源键 {0} 已不在缓存根下（游戏热修换了静态 bundle）：回落 catalog 重新定位", sourceKey);
        }
        return Locate(gameDirectory, cacheRoots);
    }

    /// <summary>
    /// 读索引（不碰 bundle）。<see cref="StaticIndexLoad.IsUsable"/> 为 false 时
    /// 页面应走一次 <see cref="RebuildAsync"/>。
    /// </summary>
    public StaticIndexLoad Load(StaticIndexSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var storedKey = _store.ReadSourceKey();
        var sameSource = storedKey is not null &&
                         string.Equals(storedKey, source.SourceKey, StringComparison.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew();
        var entries = _store.ReadEntries();
        watch.Stop();
        return new StaticIndexLoad(source, entries, sameSource && entries.Count > 0, _store.Exists, sameSource, watch.Elapsed);
    }

    /// <summary>
    /// 重建索引：枚举 bundle 内的全部 TextAsset，逐张<b>只取元数据</b>后丢弃正文，单事务落库。
    /// 源（内层内容哈希）不一致时整库重建；一致时不重写（避免每次进页面都白跑一遍 8 秒）。
    /// </summary>
    /// <param name="location">已定位的 bundle（须 <c>IsCached</c>）。</param>
    /// <param name="source">源标识。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="cancellationToken">取消。</param>
    public Task<StaticIndexBuildResult> RebuildAsync(
        StaticBundleLocation location,
        StaticIndexSource source,
        IProgress<StaticIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(source);
        if (!location.IsCached)
            return Task.FromResult(new StaticIndexBuildResult(location.BundleName, 0, 0, TimeSpan.Zero, false));

        var watch = Stopwatch.StartNew();
        // 在后台线程做（读 1392 张表是 IO + 解析密集），并把进度回报给 UI 线程。
        return Task.Run(() =>
        {
            var entries = new List<StaticTableEntry>(2048);
            long totalBytes = 0;
            var ticks = DateTimeOffset.UtcNow.UtcTicks;
            foreach (var asset in StaticBundleLocator.ReadTextAssetEntries(location, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 关键：只取元数据，正文（asset.Data）在此之后不再被引用 → 可被回收。
                var size = asset.Data.LongLength;
                var isUtf8 = asset.TryDecodeUtf8() is not null;
                entries.Add(new StaticTableEntry(
                    asset.ContainerEntry,
                    asset.Name,
                    asset.DataClass,
                    asset.FileName,
                    asset.SerializedFile,
                    asset.PathId,
                    size,
                    isUtf8,
                    ticks));
                totalBytes += size;
                if (entries.Count % 64 == 0 || entries.Count == 1)
                    progress?.Report(new StaticIndexProgress(entries.Count, 0, asset.FileName, watch.Elapsed));
            }

            cancellationToken.ThrowIfCancellationRequested();
            _store.PersistEntries(source, entries);
            watch.Stop();
            return new StaticIndexBuildResult(location.BundleName, entries.Count, totalBytes, watch.Elapsed, true);
        }, cancellationToken);
    }

    /// <summary>
    /// 取一张表的正文：优先 <c>documents</c> 缓存；未命中则从 bundle 现读（并把结果按需写入缓存）。
    /// 非 UTF-8 返回 null（不猜编码，也不进正文缓存）。
    /// </summary>
    /// <param name="location">已定位的 bundle。</param>
    /// <param name="entry">目标表（来自索引）。</param>
    /// <param name="cacheDocument">是否把读到的正文写入 <c>documents</c>（默认写）。</param>
    /// <param name="cancellationToken">取消。</param>
    public Task<StaticTableDocument> LoadDocumentAsync(
        StaticBundleLocation location,
        StaticTableEntry entry,
        bool cacheDocument = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(entry);

        var cached = _store.TryReadDocument(entry.Key);
        if (cached is not null) return Task.FromResult(new StaticTableDocument(entry, cached, true));

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var service = new UnityAssetService();
            var asset = service.ReadBundleTextAsset(location.DataPath!, entry.SerializedFile, entry.PathId, cancellationToken);
            var text = asset.TryDecodeUtf8();
            if (text is not null && cacheDocument) _store.PersistDocument(entry.Key, text);
            return new StaticTableDocument(entry, text, false);
        }, cancellationToken);
    }
}
