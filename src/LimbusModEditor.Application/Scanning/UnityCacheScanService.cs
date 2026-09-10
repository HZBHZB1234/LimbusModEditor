using System.Collections.Concurrent;
using System.IO;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Unity;

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
    IReadOnlyList<string> Diagnostics);

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
        return results;
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string directory)
    {
        try { return Directory.EnumerateDirectories(directory); }
        catch (Exception) { return []; }
    }

    public async Task<UnityCacheScanResult> ScanIntoProjectAsync(
        ModProject project,
        string cacheDirectory,
        string? gameDirectory = null,
        IProgress<UnityCacheScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        if (!Directory.Exists(cacheDirectory))
            throw new DirectoryNotFoundException($"Unity 缓存目录不存在：{cacheDirectory}");

        // 枚举缓存目录放后台线程：缓存可能上万条目，同步枚举会让调用方
        // （UI 线程）在「开始扫描」时卡住。
        var entries = await Task.Run(() => EnumerateCacheEntries(cacheDirectory), cancellationToken);
        if (entries.Count == 0)
            throw new InvalidDataException($"缓存目录里没有 <外层>/<内层>/__data 缓存条目：{cacheDirectory}");

        // 建表 / 读索引 / 解析 catalog 也全部在后台线程完成（避免调用方 UI
        // 线程上的同步卡顿），一次性载入内存字典与静态 bundle 键集合。
        var (store, bundleIndex, catalog, staticInnerHashes) = await Task.Run(() =>
        {
            var s = _store;
            if (s is not null)
            {
                // 建表必须先于并行扫描阶段；建表失败（损坏/无写权限）降级为
                // 纯扫描不持久化，不影响扫描正确性。
                try { s.EnsureSchema(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                { s = null; }
            }
            // 新鲜度检查用一次性载入的内存字典（阶段 C3：实测逐 bundle 开连接
            // 查询会把并行解析阶段拖慢一个数量级；bundle 元数据只有 1459 行级别）。
            var idx = s is not null
                ? s.ReadBundleIndex(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, UnityCacheIndexBundle>(StringComparer.OrdinalIgnoreCase);
            var cat = LoadCatalog(gameDirectory);
            // plan-08：静态数据 bundle 的内层键集合（catalog 一次性解析，之后按
            // 内层键 O(1) 判定；官方热修会换 hash，所以每次扫描都重新解析）。
            var staticHashes = StaticBundleLocator.StaticInnerHashes(cat);
            return (s, idx, cat, staticHashes);
        }, cancellationToken);

        var diagnostics = new List<string>();
        if (catalog is null && !string.IsNullOrWhiteSpace(gameDirectory))
            diagnostics.Add("官方 catalog 缺失或无法解析，本次扫描不做 vanilla 基线判定。");

        var total = entries.Count;
        var processed = 0;
        var scanned = 0;
        var indexed = 0;
        var added = 0;
        var updated = 0;

        var results = new ConcurrentBag<(UnityCacheScanEntry Entry, bool FromCache, UnityCacheIndexBundle Bundle, IReadOnlyList<AssetRecord>? Records, IReadOnlyList<UnityCacheIndexRow>? Rows)>();
        var failures = new ConcurrentQueue<string>();
        // 进度节流：热扫描（索引命中）时上报可达每秒上万条，逐条 Post 到 UI
        // 线程会把它 flood 到卡死；按 100ms 窗口聚合，扫描过程界面始终可响应。
        var lastReportTicks = Environment.TickCount64;
        await Task.Run(() =>
        {
            Parallel.ForEach(entries, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
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
                        results.Add((entry, true, cached, null, null));
                        Interlocked.Increment(ref indexed);
                    }
                    else
                    {
                        var (records, bundle, rows) = ParseEntry(entry, catalog, staticInnerHashes);
                        results.Add((entry, false, bundle, records, rows));
                        Interlocked.Increment(ref scanned);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 个别 bundle 损坏/格式未知不阻断整体扫描；按铁律明确报告。
                    failures.Enqueue($"{entry.OuterKey}/{entry.InnerKey}: {ex.Message}");
                }
                var done = Interlocked.Increment(ref processed);
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
        diagnostics.AddRange(failures.OrderBy(x => x, StringComparer.Ordinal));

        // 收尾阶段反馈：合并百万级资产与写索引库可能持续数十秒到数分钟，
        // 让 UI 明确展示当前阶段而不是停在最后一条解析进度上。
        progress?.Report(new UnityCacheScanProgress(total, total, string.Empty, scanned, indexed,
            Phase: "正在合并索引…"));

        // 合并（单线程；MainWindow 的列表绑定的是检索结果快照，可直接改集合）。
        // 全缓存 ≈ 119 万资产：必须用字典索引，逐条 FirstOrDefault 是 O(N²)。
        // 阶段 C2 惰性构记录：未变化的 bundle（索引命中）只产出路径字符串，
        // 路径已存在就不再构造 AssetRecord（项目刚回灌时 119 万条几乎零成本）。
        var existingByPath = new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var existingAsset in project.Assets)
            existingByPath.TryAdd(existingAsset.LogicalPath, existingAsset);
        var cacheSourceRegistered = project.Sources.Any(x =>
            string.Equals(x.Path, cacheDirectory, StringComparison.OrdinalIgnoreCase));
        // 索引命中的 bundle 需要行数据参与合并：单条流式查询分组载入
        // （冷扫描没有命中项，完全跳过这一步）。
        var rowsByBundle = store is not null && results.Any(x => x.FromCache)
            ? store.ReadAllRowsGrouped(StringComparer.OrdinalIgnoreCase)
            : null;
        foreach (var (entry, fromCache, cachedBundle, records, _) in results.OrderBy(x => x.Entry.OuterKey, StringComparer.Ordinal)
                     .ThenBy(x => x.Entry.InnerKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 静态标记：索引里记录的标记 + 本次 catalog 判定（任一命中即视为静态）。
            var isStaticBundle = cachedBundle.StaticBundle || staticInnerHashes.Contains(entry.InnerKey);
            if (fromCache)
            {
                // bundle 未变化：数据与索引完全同源（含 vanilla 基线），已有记录
                // 无需刷新字段；只有项目里缺失的路径（如从未扫描过的新项目）
                // 才构造记录。
                var cachedRows = rowsByBundle is not null && rowsByBundle.TryGetValue(entry.DataPath, out var rows)
                    ? rows
                    : (IReadOnlyList<UnityCacheIndexRow>)Array.Empty<UnityCacheIndexRow>();
                foreach (var row in cachedRows)
                {
                    var logicalPath = $"{entry.OuterKey}/{entry.InnerKey}/{row.Container}/{row.PathId}.{row.TypeId}";
                    if (existingByPath.TryGetValue(logicalPath, out var existingAsset))
                    {
                        // 已存在：只补/清静态标记（旧项目没有该标记时也能对齐）。
                        if (isStaticBundle) existingAsset.Metadata[StaticBundleMetadataKey] = "true";
                        else existingAsset.Metadata.Remove(StaticBundleMetadataKey);
                        updated++;
                        continue;
                    }
                    var asset = BuildRecord(entry.DataPath, entry.OuterKey, entry.InnerKey, row, isStaticBundle);
                    project.Assets.Add(asset);
                    existingByPath.Add(logicalPath, asset);
                    added++;
                }
            }
            else
            {
                // 变化过的 bundle（或首轮解析）：整条刷新/新增。
                foreach (var asset in records!)
                {
                    if (!existingByPath.TryGetValue(asset.LogicalPath, out var existing))
                    {
                        project.Assets.Add(asset);
                        existingByPath.Add(asset.LogicalPath, asset);
                        added++;
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
                    // 静态标记随本次 catalog 判定刷新（旧项目也据此补上/清除）。
                    if (isStaticBundle) existing.Metadata[StaticBundleMetadataKey] = "true";
                    else existing.Metadata.Remove(StaticBundleMetadataKey);
                    updated++;
                }
            }
            // 登记缓存来源（导出全部会跳过 Directory 来源；缓存写回由
            // UnityCacheExportService 负责）。
            if (!cacheSourceRegistered)
            {
                project.Sources.Add(new ProjectSource
                {
                    DisplayName = "游戏资源（Unity 缓存扫描）",
                    Path = cacheDirectory,
                    Format = Domain.Formats.ModFormatKind.Directory
                });
                cacheSourceRegistered = true;
            }
        }

        progress?.Report(new UnityCacheScanProgress(total, total, string.Empty, scanned, indexed,
            Phase: "正在写入索引库…"));
        PersistIndex(store, entries, results, cancellationToken);
        return new UnityCacheScanResult(total, scanned, indexed, added, updated, diagnostics);
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
        if (store is null || !store.Exists) return 0;

        var rehydrated = await Task.Run(() =>
        {
            // 先快照既有资产（实体化/导入的优先），按 LogicalPath 去重。
            var merged = new List<AssetRecord>(project.Assets.Count + 1_200_000);
            var byPath = new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in project.Assets)
            {
                merged.Add(existing);
                byPath.TryAdd(existing.LogicalPath, existing);
            }
            foreach (var (bundle, rows) in store.ReadAll())
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var record in RebuildRecords(bundle.DataPath, bundle.Outer, bundle.Inner, rows, bundle.StaticBundle))
                {
                    if (byPath.TryAdd(record.LogicalPath, record)) merged.Add(record);
                }
            }
            var added = merged.Count - project.Assets.Count;
            project.Assets = new System.Collections.ObjectModel.ObservableCollection<AssetRecord>(merged);
            return added;
        }, cancellationToken).ConfigureAwait(false);
        return rehydrated;
    }

    // ── 单个 bundle：完整解析（仅新鲜度未命中时才走到这里）─────────────

    private (IReadOnlyList<AssetRecord> Records, UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow> Rows) ParseEntry(
        UnityCacheScanEntry entry,
        CatalogFileService? catalog,
        IReadOnlySet<string>? staticInnerHashes = null)
    {
        var info = new FileInfo(entry.DataPath);
        var isStaticBundle = staticInnerHashes?.Contains(entry.InnerKey) == true;
        var bundle = new UnityCacheIndexBundle(entry.DataPath, info.Length, info.LastWriteTimeUtc.Ticks, entry.OuterKey, entry.InnerKey, isStaticBundle);

        var descriptors = new UnityAssetService().ScanBundle(entry.DataPath);
        var records = new List<AssetRecord>(descriptors.Count);
        string? baselineSummary = null;
        if (catalog is not null)
        {
            try { baselineSummary = CatalogBaselineService.Evaluate(catalog, entry.DataPath).Summary; }
            catch (Exception) { baselineSummary = null; }
        }
        var rows = new List<UnityCacheIndexRow>(descriptors.Count);
        for (var i = 0; i < descriptors.Count; i++)
        {
            var descriptor = descriptors[i];
            rows.Add(new UnityCacheIndexRow(
                i,
                descriptor.ContainerPath ?? string.Empty,
                descriptor.UnityPathId ?? 0,
                descriptor.UnityTypeId ?? 0,
                descriptor.Type,
                descriptor.Size,
                baselineSummary is { Length: > 0 } ? baselineSummary : null,
                descriptor.Metadata.TryGetValue("containerEntry", out var entryPath) ? entryPath : null));
            records.Add(new AssetRecord
            {
                LogicalPath = $"{entry.OuterKey}/{entry.InnerKey}/{descriptor.ContainerPath}/{descriptor.UnityPathId}.{descriptor.UnityTypeId}",
                SourcePath = entry.DataPath,
                ContainerPath = descriptor.ContainerPath,
                Account = entry.OuterKey,
                Bundle = entry.InnerKey,
                UnityPathId = descriptor.UnityPathId,
                UnityTypeId = descriptor.UnityTypeId,
                Type = descriptor.Type,
                Size = descriptor.Size,
                Metadata =
                {
                    ["bundleIndex"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["unityBundle"] = "true",
                    ["cacheOuter"] = entry.OuterKey,
                    ["cacheInner"] = entry.InnerKey,
                    ["originalSourcePath"] = entry.DataPath,
                    ["sourcePackagePath"] = Path.GetDirectoryName(entry.DataPath) ?? entry.DataPath,
                    ["reference"] = "true"
                }
            });
            if (descriptor.Metadata.TryGetValue("containerEntry", out var containerEntry))
                records[^1].Metadata["containerEntry"] = containerEntry;
            if (baselineSummary is { Length: > 0 }) records[^1].Metadata["catalogBaseline"] = baselineSummary;
            // plan-08：静态数据 bundle 的资源打标记，资源工作台默认视图据此过滤。
            if (isStaticBundle) records[^1].Metadata[StaticBundleMetadataKey] = "true";
        }
        return (records, bundle, rows);
    }

    /// <summary>静态数据 bundle 标记的元数据键（plan-08）。</summary>
    public const string StaticBundleMetadataKey = "staticBundle";

    private static AssetRecord BuildRecord(
        string dataPath, string outerKey, string innerKey, UnityCacheIndexRow item, bool staticBundle = false)
    {
        var record = new AssetRecord
        {
            LogicalPath = $"{outerKey}/{innerKey}/{item.Container}/{item.PathId}.{item.TypeId}",
            SourcePath = dataPath,
            ContainerPath = item.Container,
            Account = outerKey,
            Bundle = innerKey,
            UnityPathId = item.PathId,
            UnityTypeId = item.TypeId,
            // 按 TypeId 重映射而不是信任索引里持久化的 Type：老索引（映射表扩容前）
            // 可能把真实类存成 Unknown，TypeId 始终在，打开项目即可自愈无需重扫。
            Type = UnityClassId.Map(item.TypeId),
            Size = item.Size,
            Metadata =
            {
                ["bundleIndex"] = item.BundleIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["unityBundle"] = "true",
                ["cacheOuter"] = outerKey,
                ["cacheInner"] = innerKey,
                ["originalSourcePath"] = dataPath,
                ["sourcePackagePath"] = Path.GetDirectoryName(dataPath) ?? dataPath,
                ["reference"] = "true"
            }
        };
        if (!string.IsNullOrEmpty(item.Baseline)) record.Metadata["catalogBaseline"] = item.Baseline!;
        if (!string.IsNullOrEmpty(item.ContainerEntry)) record.Metadata["containerEntry"] = item.ContainerEntry!;
        if (staticBundle) record.Metadata[StaticBundleMetadataKey] = "true";
        return record;
    }

    private static IReadOnlyList<AssetRecord> RebuildRecords(
        string dataPath, string outerKey, string innerKey, IReadOnlyList<UnityCacheIndexRow> rows, bool staticBundle = false)
    {
        var records = new List<AssetRecord>(rows.Count);
        foreach (var item in rows) records.Add(BuildRecord(dataPath, outerKey, innerKey, item, staticBundle));
        return records;
    }

    // ── 索引持久化（SQLite；单事务批量写，不再整文件/逐 bundle 重写）──

    private static void PersistIndex(
        UnityCacheSqliteIndexStore? store,
        IReadOnlyList<UnityCacheScanEntry> entries,
        ConcurrentBag<(UnityCacheScanEntry Entry, bool FromCache, UnityCacheIndexBundle Bundle, IReadOnlyList<AssetRecord>? Records, IReadOnlyList<UnityCacheIndexRow>? Rows)> results,
        CancellationToken cancellationToken)
    {
        if (store is null) return; // 未配置索引路径：只扫描不持久化（与旧内存模式一致）
        try
        {
            // 只写变化过的 bundle（索引命中的未变化 bundle 无需重写）；
            // 收缩（游戏更新换键后旧索引自然淘汰）与 upsert 在同一事务完成。
            store.PersistAll(entries, results
                .Where(x => !x.FromCache && x.Rows is not null)
                .Select(x => (x.Bundle, x.Rows!)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 缓存写失败不影响扫描结果（下次冷扫描重建）。
        }
    }

    private static CatalogFileService? LoadCatalog(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return null;
        var catalogPath = Path.Combine(gameDirectory, "LimbusCompany_Data", "StreamingAssets", "aa", "catalog.bin");
        if (!File.Exists(catalogPath)) return null;
        try { return CatalogFileService.Load(catalogPath); }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return null; }
    }
}
