using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Unity;
using NLog;

namespace LimbusModEditor.Application.Scanning;

/// <summary>One cache entry: <c>&lt;缓存根&gt;/&lt;外层键&gt;/&lt;内层键&gt;/__data</c>.</summary>
public sealed record UnityCacheScanEntry(string OuterKey, string InnerKey, string DataPath);

/// <summary>Scan progress snapshot (raised from worker threads).</summary>
public sealed record UnityCacheScanProgress(
    int TotalEntries, int ProcessedEntries, string CurrentPath, int BundlesScanned, int BundlesFromIndex,
    string? Phase = null);

/// <summary>Scan outcome summary.</summary>
public sealed record UnityCacheScanResult(
    int TotalEntries,
    int ScannedBundles,
    int IndexedBundles,
    int AddedAssets,
    int UpdatedAssets,
    IReadOnlyList<string> Diagnostics,
    /// <summary>本次是否跳过了「索引命中 bundle 的逐行对账」（调用方声明项目已与索引一致）。
    /// 为 true 时 <see cref="UpdatedAssets"/> 不包含「已存在但未逐条确认」的资产。</summary>
    bool SkippedRowReconciliation = false,
    /// <summary>本次是否连整个合并段都跳过了（项目与索引一致 + 没有任何 bundle 需要解析/新增）：
    /// 此时合并结果必然为空，跳过的是「建 127 万条路径字典」这类纯 no-op 开销。</summary>
    bool SkippedMerge = false);

/// <summary>
/// 傻瓜化核心：一键扫描游戏 Unity 缓存，把全部 bundle 的对象索引登记进项目。
/// 「引用模式」——不复制任何文件，资源直接指向缓存中的 <c>__data</c>（只读），
/// 首次编辑某个 bundle 时才把该 bundle 复制进项目（见
/// UnityCacheMaterializationService）。
/// 扫描索引持久化为 SQLite（<c>cache/unity-cache-index.db</c>，阶段 C 性能改造：
/// 原单文件 JSON 164MB 全量解析/重写在真实 119 万资产规模下热扫描 ≈100s，
/// SQLite 后新鲜度检查与读回都是按 bundle 的索引查询，写回只增删变化的
/// bundle）。索引损坏只影响速度不影响正确性：删除 .db 重建即可。
/// </summary>
public sealed class UnityCacheScanService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly UnityCacheSqliteIndexStore? _store;

    /// <param name="indexCacheFile">索引文件路径；默认放在程序目录 cache/ 下
    /// （习惯名 unity-cache-index.json，实际存储为同名 .db 的 SQLite 库，
    /// 与旧版 JSON 索引共存但不再读写 JSON）。测试可传入临时目录。</param>
    public UnityCacheScanService(string? indexCacheFile = null)
    {
        _store = indexCacheFile is null
            ? null
            : new UnityCacheSqliteIndexStore(Path.ChangeExtension(indexCacheFile, ".db"));
    }

    /// <summary>Enumerates <c>&lt;root&gt;/&lt;outer&gt;/&lt;inner&gt;/__data</c>
    /// cache entries. Unreadable subdirectories are skipped.</summary>
    public static IReadOnlyList<UnityCacheScanEntry> EnumerateCacheEntries(string? cacheDirectory)
    {
        var results = new List<UnityCacheScanEntry>();
        if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory)) return results;
        foreach (var outer in EnumerateDirectoriesSafe(cacheDirectory))
        {
            foreach (var inner in EnumerateDirectoriesSafe(outer))
            {
                var data = Path.Combine(inner, "__data");
                if (!File.Exists(data)) continue;
                results.Add(new UnityCacheScanEntry(Path.GetFileName(outer), Path.GetFileName(inner), data));
            }
        }
        if (Log.IsDebugEnabled)
            Log.Debug("枚举 Unity 缓存条目：{0} 条 · cache={1}", results.Count, cacheDirectory);
        return results;
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string directory)
    {
        try { return Directory.EnumerateDirectories(directory); }
        catch (Exception ex)
        {
            Log.Warn(ex, "枚举缓存子目录失败（该目录被跳过，扫描继续）：{0}", directory);
            return [];
        }
    }

    /// <summary>
    /// 扫描缓存目录并把对象索引登记进项目（引用模式，不复制任何文件）。
    /// </summary>
    /// <param name="project">目标项目。</param>
    /// <param name="cacheDirectory">Unity 缓存根（<c>&lt;外层&gt;/&lt;内层&gt;/__data</c> 的父目录）。</param>
    /// <param name="gameDirectory">游戏目录（读 catalog 做 vanilla 基线判定；可空）。</param>
    /// <param name="progress">进度回调（后台线程）。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <param name="projectMatchesIndex">
    /// 调用方保证「索引里目前每一行都能在项目里找到对应记录」（打开项目时刚成功跑过一次
    /// <see cref="RehydrateFromIndexAsync"/>）。为 true 时，索引命中的 bundle 不再逐行对账
    /// ——那一步在真实规模下要读 119 万行并做 127 万次字典查找（实测约 40 秒），
    /// 而结果恒为 no-op。**这个保证必须由调用方给出**：传错会让「项目里缺失的记录」
    /// 不被补回，因此只有「刚回灌过、且本次会话没有删过记录」的路径才允许传 true。
    /// </param>
    public async Task<UnityCacheScanResult> ScanIntoProjectAsync(
        ModProject project,
        string cacheDirectory,
        string? gameDirectory = null,
        IProgress<UnityCacheScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool projectMatchesIndex = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        if (!Directory.Exists(cacheDirectory))
            throw new DirectoryNotFoundException($"Unity 缓存目录不存在：{cacheDirectory}");

        using var scope = Log.Scope($"扫描游戏资源（{Path.GetFileName(cacheDirectory.TrimEnd(Path.DirectorySeparatorChar))}）");
        Log.Info("Unity 缓存扫描开始：cache={0} · game={1} · 项目={2}（资产 {3} 条）· 索引与项目一致={4}",
            cacheDirectory, gameDirectory ?? "-", project.Name, project.Assets.Count, projectMatchesIndex);

        // 枚举缓存目录放后台线程：缓存可能上万条目，同步枚举会让调用方
        // （UI 线程）在「开始扫描」时卡住。
        var entries = await Task.Run(() => EnumerateCacheEntries(cacheDirectory), cancellationToken);
        if (entries.Count == 0)
            throw new InvalidDataException($"缓存目录里没有 <外层>/<内层>/__data 缓存条目：{cacheDirectory}");
        Log.Debug("缓存条目枚举完成：{0} 条 · cache={1}", entries.Count, cacheDirectory);

        // 建表 / 读索引在后台线程完成（避免调用方 UI 线程上的同步卡顿）。
        //
        // plan-13：catalog 与「静态 bundle 内层键集合」改成**完全惰性**——只有某个 bundle
        // 真的被解析成功后才需要它们（vanilla 基线 + 静态标记）。真实 catalog.bin（5 MB）
        // 解析一次约 17 秒（本机实测，解析走正则启发式），而启动扫描每次都跑：
        // 缓存里总有几条永远解析不了的条目（本机 2 条），若在解析前就要求 catalog，
        // 每次启动都会白花这 17 秒。惰性后「热启动 / 只有坏条目」两种情况一分钱不花。
        var catalogLazy = new Lazy<CatalogFileService?>(() => LoadCatalog(gameDirectory),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var staticHashesLazy = new Lazy<IReadOnlySet<string>>(() => catalogLazy.Value is { } catalog
                ? StaticBundleLocator.StaticInnerHashes(catalog)
                : (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var (store, bundleIndex) = await Task.Run(() =>
        {
            var s = _store;
            if (s is not null)
            {
                // 建表必须先于并行扫描阶段；建表失败（损坏/无写权限）降级为
                // 纯扫描不持久化，不影响扫描正确性。
                try { s.EnsureSchema(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    Log.Error(ex, "资源索引库建表失败（降级为纯扫描、不持久化索引）：数据库已存在={0}", s.Exists);
                    s = null;
                }
            }
            // 新鲜度检查用一次性载入的内存字典（阶段 C3：实测逐 bundle 开连接
            // 查询会把并行解析阶段拖慢一个数量级；bundle 元数据只有 1459 行级别）。
            var idx = s is not null
                ? s.ReadBundleIndex(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, UnityCacheIndexBundle>(StringComparer.OrdinalIgnoreCase);
            Log.Debug("资源索引载入内存：库可用={0} · 索引内 bundle {1} 行", s is not null, idx.Count);
            return (s, idx);
        }, cancellationToken);

        var diagnostics = new List<string>();

        var total = entries.Count;
        var processed = 0;
        var scanned = 0;
        var indexed = 0;
        var added = 0;
        var updated = 0;

        var results = new ConcurrentBag<(UnityCacheScanEntry Entry, bool FromCache, UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow>? Rows)>();
        var failures = new ConcurrentQueue<string>();
        // 进度节流：热扫描（索引命中）时上报可达每秒上万条，逐条 Post 到 UI
        // 线程会把它 flood 到卡死；按 100ms 窗口聚合，扫描过程界面始终可响应。
        var lastReportTicks = Environment.TickCount64;
        var failureSamples = 0;
        await Task.Run(() =>
        {
            Parallel.ForEach(entries, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 4)
            }, entry =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // 并行阶段零 DB 访问：新鲜度查内存字典；未命中才解析 bundle。
                    var info = new FileInfo(entry.DataPath);
                    if (bundleIndex.TryGetValue(entry.DataPath, out var cached) &&
                        cached.Size == info.Length &&
                        cached.MTimeUtcTicks == info.LastWriteTimeUtc.Ticks)
                    {
                        results.Add((entry, true, cached, null));
                        Interlocked.Increment(ref indexed);
                    }
                    else
                    {
                        var (bundle, rows) = ParseEntry(entry, () => catalogLazy.Value, () => staticHashesLazy.Value);
                        results.Add((entry, false, bundle, rows));
                        Interlocked.Increment(ref scanned);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 个别 bundle 损坏/格式未知不阻断整体扫描；按铁律明确报告。
                    failures.Enqueue($"{entry.OuterKey}/{entry.InnerKey}: {ex.Message}");
                    if (Log.IsWarnEnabled && Interlocked.Increment(ref failureSamples) <= 20)
                        Log.Warn(ex, "解析 bundle 失败（已记录诊断并跳过该 bundle）：{0}", entry.DataPath);
                }
                var done = Interlocked.Increment(ref processed);
                Log.Every(done, 5000, LogLevel.Debug,
                    () => $"资源扫描进度：{done}/{total} 个缓存条目（解析 {Volatile.Read(ref scanned)} · 索引命中 {Volatile.Read(ref indexed)}）");
                var now = Environment.TickCount64;
                if (now - Volatile.Read(ref lastReportTicks) >= 100)
                {
                    Volatile.Write(ref lastReportTicks, now);
                    progress?.Report(new UnityCacheScanProgress(total, done, entry.DataPath, scanned, indexed));
                }
            });
            // 结束补报一次最终进度（最后一条可能因节流被跳过）。
            progress?.Report(new UnityCacheScanProgress(total, processed, string.Empty, scanned, indexed));
        }, cancellationToken).ConfigureAwait(false);
        Log.Info("资源扫描解析阶段结束：{0} 个缓存条目 · 索引命中 {1} · 需解析 {2} · 解析失败 {3}",
            total, indexed, scanned, failures.Count);
        diagnostics.AddRange(failures.OrderBy(x => x, StringComparer.Ordinal));
        // catalog 只在真的用上时才可能为空：惰性被触发过 + 加载失败 + 配了游戏目录。
        if (catalogLazy.IsValueCreated && catalogLazy.Value is null && !string.IsNullOrWhiteSpace(gameDirectory))
            diagnostics.Add("官方 catalog 缺失或无法解析，本次扫描不做 vanilla 基线判定。");
        Log.Debug("catalog 惰性值状态：已触发={0} · 目录权威性（staticHashesLazy 已触发）={1} · 配了游戏目录={2}",
            catalogLazy.IsValueCreated, staticHashesLazy.IsValueCreated, !string.IsNullOrWhiteSpace(gameDirectory));

        // 收尾阶段反馈：合并百万级资产与写索引库可能持续数十秒到数分钟，
        // 让 UI 明确展示当前阶段而不是停在最后一条解析进度上。
        progress?.Report(new UnityCacheScanProgress(total, total, string.Empty, scanned, indexed,
            Phase: "正在合并索引…"));

        // 合并（单线程；MainWindow 的列表绑定的是检索结果快照，可直接改集合）。
        // 全缓存 ≈ 119 万资产：必须用字典索引，逐条 FirstOrDefault 是 O(N²)。
        // 阶段 C2 惰性构记录：未变化的 bundle（索引命中）只产出路径字符串，
        // 路径已存在就不再构造 AssetRecord（项目刚回灌时 119 万条几乎零成本）。
        //
        // plan-13：整段合并只在「真有事要做」时执行 —— 项目已与索引逐条一致（打开项目刚回灌过）
        // 且本次没有任何 bundle 需要解析/新增时，合并结果恒为空，而它要建一张 127 万条的
        // 路径字典（真实规模下数百 MB 的分配 + 一次字典插入风暴）。启动扫描每次都跑，
        // 这段纯 no-op 的开销就是「每次启动都要白等」的来源。
        var reconcileIndexHits = !projectMatchesIndex;
        // 「有没有解析成功过任何 bundle」用 results 判定（解析失败的条目根本不进 results），
        // 而不是「有没有条目看起来需要解析」：真实缓存里那几条永远解析不了的坏条目
        // 不该让整段合并（含 127 万条路径字典）白跑。
        var needMerge = reconcileIndexHits || results.Any(x => !x.FromCache);
        var cacheSourceRegistered = project.Sources.Any(x =>
            string.Equals(x.Path, cacheDirectory, StringComparison.OrdinalIgnoreCase));
        if (!needMerge)
        {
            // 索引命中的 bundle 无需对账；但要保留「索引可能已过期（磁盘上删了 bundle）」
            // 的收缩能力 —— 那由后面的 PersistIndex 完成，与项目资产无关。
            Log.Debug("跳过资产合并段：项目与索引逐条一致={0} · 本次无需解析的 bundle={1}（未对账、未重建资产表）",
                projectMatchesIndex, !results.Any(x => !x.FromCache));
            PersistIndex(store, entries, results, cancellationToken);
            RegisterCacheSource(project, cacheDirectory, ref cacheSourceRegistered);
            Log.Info("资源扫描结束（纯索引命中）：bundle {0} 个（解析 {1} · 索引命中 {2}）· 资产未变",
                total, scanned, indexed);
            return new UnityCacheScanResult(total, scanned, indexed, added, updated, diagnostics,
                SkippedRowReconciliation: true, SkippedMerge: true);
        }

        var existingByPath = new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var existingAsset in project.Assets)
            existingByPath.TryAdd(existingAsset.LogicalPath, existingAsset);
        Log.Debug("资产合并开始：项目现有资产 {0} 条 · 是否逐条对账={1}", existingByPath.Count, reconcileIndexHits);
        // 索引命中且需要对账时按 bundle 流式读取。冷解析结果只保留紧凑行，
        // 一个 bundle 消费完再处理下一个，避免同时保留百万行中间记录与最终资产。
        IEnumerable<(UnityCacheScanEntry Entry, bool FromCache, UnityCacheIndexBundle Bundle,
            IReadOnlyList<UnityCacheIndexRow>? Rows)> MergeBatches()
        {
            if (store is not null && reconcileIndexHits)
            {
                var hits = results.Where(x => x.FromCache).ToDictionary(x => x.Entry.DataPath, StringComparer.OrdinalIgnoreCase);
                if (hits.Count > 0)
                    foreach (var (bundle, rows) in store.ReadAll(hits.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), cancellationToken))
                        yield return (hits[bundle.DataPath].Entry, true, bundle, rows);
            }
            foreach (var result in results.Where(x => !x.FromCache)
                         .OrderBy(x => x.Entry.OuterKey, StringComparer.Ordinal).ThenBy(x => x.Entry.InnerKey, StringComparer.Ordinal))
                yield return result;
        }
        // 静态标记留痕（只在 Debug 打开时计数）：写入 / 保留 / 清除各计一次，
        // 便于回答「标记为什么没了」。不参与任何判定，关闭日志时零改动。
        var staticCounters = Log.IsDebugEnabled
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : null;
        var staticBundleDecisions = 0L;
        foreach (var (entry, fromCache, cachedBundle, cachedRows) in MergeBatches())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 静态标记：以索引里记录的标记为主；catalog 本次已被解析过（有 bundle 解析成功）
            // 时再用内层键集合补一刀 —— 但**绝不在这里主动触发 catalog 解析**
            // （见本方法开头的惰性说明）。旧索引/旧项目（没有 static_bundle 语义）由
            // 项目记录里已有的标记兜底：命中分支里只要记录上已有标记就保留，
            // 免得一次「什么都没变」的扫描把静态标记全抹掉。
            //
            // catalog 不可用（未解析 / 解析失败 / 版本不符）时 staticHashes 集合为空，
            // 此时**没有资格判定「这个 bundle 不是静态的」** —— 早先的写法会据此把
            // 已有标记全部清除，于是资源工作台又把 static-data 全列出来。
            // 因此清除只在「本次真的拿到过 catalog 内层键集合」时发生；
            // 另有 bundle 名兜底（不依赖 catalog）作为第二道防线。
            var catalogAuthoritative = staticHashesLazy.IsValueCreated;
            var nameLooksStatic = StaticBundleLocator.LooksLikeStaticBundle(entry.InnerKey);
            var isStaticBundle = cachedBundle.StaticBundle
                || nameLooksStatic
                || (catalogAuthoritative && staticHashesLazy.Value.Contains(entry.InnerKey));
            var mayClearStatic = catalogAuthoritative && !nameLooksStatic;
            if (Log.IsTraceEnabled)
                Log.Trace("静态标记判定：bundle={0} · 索引标记={1} · 名字像静态={2} · catalog 权威={3} · 判定静态={4} · 允许清除={5} · 来自索引缓存={6} · data={7}",
                    entry.InnerKey, cachedBundle.StaticBundle, nameLooksStatic, catalogAuthoritative,
                    isStaticBundle, mayClearStatic, fromCache, entry.DataPath);
            if (staticCounters is not null && staticBundleDecisions++ % 5000 == 0)
                Log.Debug("静态标记判定进度：已判 {0} 个 bundle · 最近一个 {1}（索引标记={2} · 名字像静态={3} · catalog 权威={4} → 静态={5}）",
                    staticBundleDecisions, entry.InnerKey, cachedBundle.StaticBundle, nameLooksStatic,
                    catalogAuthoritative, isStaticBundle);
            if (fromCache)
            {
                var sourcePackagePath = Path.GetDirectoryName(entry.DataPath) ?? entry.DataPath;
                // bundle 未变化：数据与索引完全同源（含 vanilla 基线），已有记录
                // 无需刷新字段；只有项目里缺失的路径（如从未扫描过的新项目）
                // 才构造记录。项目已与索引一致时（reconcileIndexHits=false）这一趟
                // 整体跳过 —— 注意这里**不能**用 continue：后面还有「登记缓存来源」
                // 的公共代码，跳过整个 bundle 的循环体即可。
                foreach (var row in cachedRows!)
                {
                    var logicalPath = $"{entry.OuterKey}/{entry.InnerKey}/{row.Container}/{row.PathId}.{row.TypeId}";
                    if (existingByPath.TryGetValue(logicalPath, out var existingAsset))
                    {
                        // 已存在：只补/清静态标记（旧项目没有该标记时也能对齐）。
                        // 记录上已有标记就保留：旧索引（static_bundle 列全是 0）在
                        // 「什么都没变」的扫描里不该把标记抹掉。
                        var hadStaticMark = existingAsset.Metadata.ContainsKey(StaticBundleMetadataKey);
                        var staticForAsset = isStaticBundle
                            || existingAsset.Metadata.ContainsKey(StaticBundleMetadataKey);
                        if (staticForAsset) existingAsset.Metadata[StaticBundleMetadataKey] = "true";
                        else if (mayClearStatic) existingAsset.Metadata.Remove(StaticBundleMetadataKey);
                        if (Log.IsDebugEnabled) LogStaticMark(staticCounters, logicalPath, isStaticBundle,
                            staticForAsset && !hadStaticMark,
                            mayClearStatic && !staticForAsset && hadStaticMark,
                            "bundle 未变化（索引命中）");
                        updated++;
                        continue;
                    }
                    var asset = BuildRecord(entry.DataPath, entry.OuterKey, entry.InnerKey, row, isStaticBundle, sourcePackagePath, logicalPath);
                    project.Assets.Add(asset);
                    existingByPath.Add(logicalPath, asset);
                    added++;
                    if (Log.IsDebugEnabled && isStaticBundle)
                        LogStaticMark(staticCounters, logicalPath, true, true, false,
                            "新增引用记录（bundle 未变化，项目里缺失）");
                }
            }
            else
            {
                // 变化过的 bundle（或首轮解析）：整条刷新/新增。
                foreach (var asset in RebuildRecords(entry.DataPath, entry.OuterKey, entry.InnerKey, cachedRows!, isStaticBundle))
                {
                    if (!existingByPath.TryGetValue(asset.LogicalPath, out var existing))
                    {
                        project.Assets.Add(asset);
                        existingByPath.Add(asset.LogicalPath, asset);
                        added++;
                        if (Log.IsDebugEnabled && isStaticBundle)
                            LogStaticMark(staticCounters, asset.LogicalPath, true, true, false,
                                "新增引用记录（bundle 本次重新解析）");
                        continue;
                    }
                    if (existing.Metadata.ContainsKey("reference"))
                    {
                        // 仍是纯引用：整条刷新（含指向缓存的 SourcePath）。
                        existing.SourcePath = asset.SourcePath;
                        existing.ContainerPath = asset.ContainerPath;
                        existing.Account = asset.Account;
                        existing.Bundle = asset.Bundle;
                        existing.UnityPathId = asset.UnityPathId;
                        existing.UnityTypeId = asset.UnityTypeId;
                        existing.Type = asset.Type;
                        existing.Size = asset.Size;
                        foreach (var pair in asset.Metadata) existing.Metadata[pair.Key] = pair.Value;
                    }
                    else
                    {
                        // 已实体化（本地有编辑副本）：保留本地 SourcePath，只刷新描述性字段。
                        existing.Type = asset.Type;
                        existing.Size = asset.Size;
                        if (asset.Metadata.TryGetValue("catalogBaseline", out var baseline))
                            existing.Metadata["catalogBaseline"] = baseline;
                    }
                    // 静态标记随本次判定刷新（补上总是安全的；清除需要 catalog 权威判定，见上）。
                    var existingHadStaticMark = existing.Metadata.ContainsKey(StaticBundleMetadataKey);
                    if (isStaticBundle) existing.Metadata[StaticBundleMetadataKey] = "true";
                    else if (mayClearStatic) existing.Metadata.Remove(StaticBundleMetadataKey);
                    if (Log.IsDebugEnabled) LogStaticMark(staticCounters, asset.LogicalPath, isStaticBundle,
                        isStaticBundle && !existingHadStaticMark,
                        mayClearStatic && !isStaticBundle && existingHadStaticMark,
                        "bundle 本次重新解析");
                    updated++;
                }
            }
        }
        RegisterCacheSource(project, cacheDirectory, ref cacheSourceRegistered);

        if (staticCounters is not null)
        {
            staticCounters.TryGetValue("写入", out var written);
            staticCounters.TryGetValue("保留", out var kept);
            staticCounters.TryGetValue("清除", out var cleared);
            staticCounters.TryGetValue("清除(已无标记)", out var clearedAlready);
            Log.Debug("静态标记汇总：写入 {0} · 已存在保留 {1} · 清除 {2} · 清除但本就无标记 {3} · catalog 权威={4}",
                written, kept, cleared, clearedAlready, staticHashesLazy.IsValueCreated);
            if (cleared > 0)
                Log.Warn("本次扫描清除了 {0} 条 staticBundle 标记（catalog 权威判定为「非静态」）—— 若资源工作台静态口径不对，先看这里的清除是否合理",
                    cleared);
        }

        progress?.Report(new UnityCacheScanProgress(total, total, string.Empty, scanned, indexed,
            Phase: "正在写入索引库…"));
        PersistIndex(store, entries, results, cancellationToken);
        Log.Info("Unity 缓存扫描结束：bundle {0} 个（解析 {1} · 索引命中 {2}）· 新增资产 {3} · 更新资产 {4} · 诊断 {5} 条 · 跳过对账={6}",
            total, scanned, indexed, added, updated, diagnostics.Count, !reconcileIndexHits);
        return new UnityCacheScanResult(total, scanned, indexed, added, updated, diagnostics,
            SkippedRowReconciliation: !reconcileIndexHits);
    }

    /// <summary>
    /// 静态标记（<see cref="StaticBundleMetadataKey"/>）写入 / 保留 / 清除的留痕。
    /// 只在 <c>Debug</c> 打开时被调用（调用点已守卫）：逐条只做计数，
    /// 每 5000 条采样一条明细；「清除」单独成类，因为它正是历史上标记丢失的现场。
    /// 不参与任何判定，也不改动元数据。
    /// </summary>
    private static void LogStaticMark(
        Dictionary<string, int>? counters,
        string logicalPath,
        bool isStaticBundle,
        bool wrote,
        bool cleared,
        string reason)
    {
        // 计数器只在 Debug 级别开启时创建；这里跟着一起降级为「只打日志、不计数」。
        var category = cleared ? "清除" : wrote ? "写入" : isStaticBundle ? "保留" : "清除(已无标记)";
        if (counters is null)
        {
            if (cleared && Log.IsWarnEnabled)
                Log.Warn("清除 staticBundle 标记：{0} · 依据：{1}", logicalPath, reason);
            return;
        }
        counters.TryGetValue(category, out var counter);
        counters[category] = ++counter;
        if (cleared && Log.IsWarnEnabled)
            Log.Warn("清除 staticBundle 标记：{0} · 依据：{1}（累计 {2} 条）", logicalPath, reason, counter);
        else if (counter == 1 || counter % 5000 == 0)
            Log.Debug("{0} staticBundle 标记：{1} · 依据：{2}（累计 {3} 条）", category, logicalPath, reason, counter);
    }

    /// <summary>登记缓存来源（导出全部会跳过 Directory 来源；缓存写回由
    /// <c>UnityCacheExportService</c> 负责）。已登记过则什么都不做。</summary>
    private static void RegisterCacheSource(ModProject project, string cacheDirectory, ref bool registered)
    {
        if (registered) return;
        project.Sources.Add(new ProjectSource
        {
            DisplayName = "游戏资源（Unity 缓存扫描）",
            Path = cacheDirectory,
            Format = Domain.Formats.ModFormatKind.Directory
        });
        registered = true;
        Log.Debug("登记缓存来源到项目：{0}", cacheDirectory);
    }

    /// <summary>打开旧项目时的后台回灌：纯引用资产不再存进项目文件（阶段 C
    /// 项目瘦身，保存 1.6GB→KB 级），打开时从扫描索引 SQLite 库重建。
    /// 实体化过的资产（本地有编辑副本，随项目文件加载）按 LogicalPath 优先保留；
    /// 索引库缺失时返回 0（提示用户重新扫描即可）。整体替换 <c>Assets</c>
    /// 集合引用（不做逐条 CollectionChanged），期间 UI 读到的要么是旧集合
    /// 要么是新集合，两者都一致。</summary>
    public async Task<int> RehydrateFromIndexAsync(
        ModProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var store = _store;
        if (store is null || !store.Exists)
        {
            Log.Warn("从索引回灌资产跳过：资源索引库不存在或未配置（项目={0}）", project.Name);
            return 0;
        }

        using var scope = Log.Scope("从索引回灌资产");
        Log.Info("从索引回灌资产开始：项目={0}（现有资产 {1} 条）", project.Name, project.Assets.Count);
        var rehydrated = await Task.Run(() =>
        {
            // 先快照既有资产（实体化/导入的优先），按 LogicalPath 去重。
            var merged = new List<AssetRecord>(project.Assets.Count);
            var byPath = new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in project.Assets)
            {
                merged.Add(existing);
                byPath.TryAdd(existing.LogicalPath, existing);
            }
            var readBundles = 0;
            foreach (var (bundle, rows) in store.ReadAll(cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBundles++;
                Log.Every(readBundles, 500, LogLevel.Debug,
                    () => $"回灌索引读取进度：已读 {readBundles} 个 bundle · 已合并 {merged.Count} 条资产");
                foreach (var record in RebuildRecords(bundle.DataPath, bundle.Outer, bundle.Inner, rows, bundle.StaticBundle))
                {
                    if (byPath.TryAdd(record.LogicalPath, record)) merged.Add(record);
                }
            }
            var added = merged.Count - project.Assets.Count;
            project.Assets = new System.Collections.ObjectModel.ObservableCollection<AssetRecord>(merged);
            Log.Info("从索引回灌资产结束：读取 {0} 个 bundle · 合并后资产 {1} 条（新增 {2} 条）",
                readBundles, merged.Count, added);
            return added;
        }, cancellationToken).ConfigureAwait(false);
        return rehydrated;
    }

    // ── 单个 bundle：完整解析（仅新鲜度未命中时才走到这里）─────────────

    /// <summary>
    /// 解析一个缓存 bundle，只产出紧凑索引行；资产记录在合并时构造。
    ///
    /// <para><paramref name="catalogProvider"/> / <paramref name="staticHashesProvider"/> 都是
    /// <b>惰性提供者</b>，且刻意在 <c>ScanBundle</c> <b>成功之后</b>才取值
    /// （两者最终都会触发 5 MB catalog.bin 的解析，本机实测约 17 秒）：
    /// 缓存里总有几条永远解析不了的条目（本机 2 条），若在解析前就取，
    /// 每次扫描都会为了它们白花十几秒。解析失败时这两个 provider 一次都不会被调用。</para>
    /// </summary>
    private (UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow> Rows) ParseEntry(
        UnityCacheScanEntry entry,
        Func<CatalogFileService?> catalogProvider,
        Func<IReadOnlySet<string>> staticHashesProvider)
    {
        var info = new FileInfo(entry.DataPath);
        // 先解析：这一步抛异常（损坏 / 未知格式）时下面两个惰性值都不会被触发。
        string? baselineSummary = null;
        using var backend = new AssetsToolsBackend();
        // 直接消费轻量 descriptor，不经 UnityAssetService 创建临时 AssetRecord/元数据字典。
        // 回调仅在对象解析成功后执行，保留损坏 bundle 不触发 catalog 的惰性语义。
        var descriptors = backend.InspectBundle(entry.DataPath, stream =>
        {
            if (catalogProvider() is not { } catalog) return;
            try { baselineSummary = CatalogBaselineService.Evaluate(catalog, entry.DataPath, stream).Summary; }
            catch (Exception ex)
            {
                Log.Debug(ex, "vanilla 基线判定失败（基线留空，扫描继续）：{0}", entry.DataPath);
            }
        });
        var isStaticBundle = staticHashesProvider().Contains(entry.InnerKey);
        var bundle = new UnityCacheIndexBundle(entry.DataPath, info.Length, info.LastWriteTimeUtc.Ticks, entry.OuterKey, entry.InnerKey, isStaticBundle);
        if (Log.IsDebugEnabled)
            Log.Debug("解析 bundle 成功：{0}/{1} · {2:0.0} KB · 对象 {3} 个 · 静态 bundle={4}",
                entry.OuterKey, entry.InnerKey, info.Length / 1024.0, descriptors.Count, isStaticBundle);

        var rows = new List<UnityCacheIndexRow>(descriptors.Count);
        for (var i = 0; i < descriptors.Count; i++)
        {
            var descriptor = descriptors[i];
            rows.Add(new UnityCacheIndexRow(
                i,
                descriptor.ContainerPath,
                descriptor.PathId,
                descriptor.TypeId,
                descriptor.AssetType,
                descriptor.ByteSize,
                baselineSummary is { Length: > 0 } ? baselineSummary : null,
                string.IsNullOrWhiteSpace(descriptor.ContainerEntryPath) ? null : descriptor.ContainerEntryPath));
        }
        return (bundle, rows);
    }

    /// <summary>静态数据 bundle 标记的元数据键（plan-08）。</summary>
    public const string StaticBundleMetadataKey = "staticBundle";

    private static AssetRecord BuildRecord(
        string dataPath, string outerKey, string innerKey, UnityCacheIndexRow item, bool staticBundle,
        string sourcePackagePath, string? logicalPath = null)
    {
        var mappedType = UnityClassId.Map(item.TypeId);
        var record = new AssetRecord
        {
            LogicalPath = logicalPath ?? $"{outerKey}/{innerKey}/{item.Container}/{item.PathId}.{item.TypeId}",
            SourcePath = dataPath,
            ContainerPath = item.Container,
            Account = outerKey,
            Bundle = innerKey,
            UnityPathId = item.PathId,
            UnityTypeId = item.TypeId,
            // 按 TypeId 应用当前映射；类映射扩充无需重新解析 bundle。
            // ref-type 伪 ID 可能只有类型树类名能解析，保留扫描时已识别的类型。
            Type = mappedType == AssetType.Unknown ? item.Type : mappedType,
            Size = item.Size,
            Metadata =
            {
                ["bundleIndex"] = item.BundleIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["unityBundle"] = "true",
                ["cacheOuter"] = outerKey,
                ["cacheInner"] = innerKey,
                ["originalSourcePath"] = dataPath,
                ["sourcePackagePath"] = sourcePackagePath,
                ["reference"] = "true"
            }
        };
        if (!string.IsNullOrEmpty(item.Baseline)) record.Metadata["catalogBaseline"] = item.Baseline!;
        if (!string.IsNullOrEmpty(item.ContainerEntry)) record.Metadata["containerEntry"] = item.ContainerEntry!;
        if (staticBundle) record.Metadata[StaticBundleMetadataKey] = "true";
        return record;
    }

    private static IEnumerable<AssetRecord> RebuildRecords(
        string dataPath, string outerKey, string innerKey, IReadOnlyList<UnityCacheIndexRow> rows, bool staticBundle = false)
    {
        var sourcePackagePath = Path.GetDirectoryName(dataPath) ?? dataPath;
        foreach (var item in rows) yield return BuildRecord(dataPath, outerKey, innerKey, item, staticBundle, sourcePackagePath);
    }

    // ── 索引持久化（SQLite；单事务批量写，不再整文件/逐 bundle 重写）──

    private static void PersistIndex(
        UnityCacheSqliteIndexStore? store,
        IReadOnlyList<UnityCacheScanEntry> entries,
        ConcurrentBag<(UnityCacheScanEntry Entry, bool FromCache, UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow>? Rows)> results,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            Log.Debug("跳过索引持久化：未配置索引库路径（只扫描不持久化）");
            return; // 未配置索引路径：只扫描不持久化（与旧内存模式一致）
        }
        try
        {
            // 只写变化过的 bundle（索引命中的未变化 bundle 无需重写）；
            // 收缩（游戏更新换键后旧索引自然淘汰）与 upsert 在同一事务完成。
            var toWrite = results
                .Where(x => !x.FromCache && x.Rows is not null)
                .Select(x => (x.Bundle, x.Rows!))
                .ToList();
            var watch = Stopwatch.StartNew();
            store.PersistAll(entries, toWrite, cancellationToken);
            watch.Stop();
            Log.Debug("索引库持久化完成：磁盘条目 {0} 个 · 重写 bundle {1} 个 · 用时 {2:0.0} 秒",
                entries.Count, toWrite.Count, watch.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 缓存写失败不影响扫描结果（下次冷扫描重建）。
            Log.Error(ex, "索引库持久化失败（本次扫描结果不受影响，下次冷扫描会重建）：磁盘条目 {0} 个", entries.Count);
        }
    }

    private static CatalogFileService? LoadCatalog(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            Log.Debug("不加载 catalog：未配置游戏目录（vanilla 基线与静态内层键集合都不可用）");
            return null;
        }
        var catalogPath = Path.Combine(gameDirectory, "LimbusCompany_Data", "StreamingAssets", "aa", "catalog.bin");
        if (!File.Exists(catalogPath))
        {
            Log.Warn("catalog 不存在（vanilla 基线与静态内层键集合都不可用）：{0}", catalogPath);
            return null;
        }
        try
        {
            var watch = Stopwatch.StartNew();
            var catalog = CatalogFileService.Load(catalogPath);
            watch.Stop();
            Log.Debug("加载 catalog 成功：{0} · 用时 {1:0.0} 秒（首次解析较慢，约十余秒）",
                catalogPath, watch.Elapsed.TotalSeconds);
            return catalog;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Log.Error(ex, "catalog 解析失败（本次不做 vanilla 基线，静态内层键集合为空）：{0}", catalogPath);
            return null;
        }
    }

    /// <summary>该缓存条目是否需要重新解析：索引里没有，或 (size, mtime) 与磁盘不符。
    /// 读不到元数据（被占用 / 权限）时按「需要解析」处理（宁可多解析一次，不假装新鲜）。
    /// 这是 catalog 是否必须解析的唯一判据（见 <see cref="ScanIntoProjectAsync"/> 的说明）。</summary>
    private static bool IsMissingOrChanged(UnityCacheScanEntry entry, IReadOnlyDictionary<string, UnityCacheIndexBundle> index)
    {
        try
        {
            var info = new FileInfo(entry.DataPath);
            return !index.TryGetValue(entry.DataPath, out var cached) ||
                   cached.Size != info.Length ||
                   cached.MTimeUtcTicks != info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "读缓存条目元数据失败（按「需要重新解析」处理，不假装新鲜）：{0}", entry.DataPath);
            return true;
        }
    }
}
