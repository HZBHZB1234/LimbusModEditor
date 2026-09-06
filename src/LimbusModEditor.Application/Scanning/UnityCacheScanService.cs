using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Application.Scanning;

/// <summary>One cache entry: <c>&lt;缓存根&gt;/&lt;外层键&gt;/&lt;内层键&gt;/__data</c>.</summary>
public sealed record UnityCacheScanEntry(string OuterKey, string InnerKey, string DataPath);

/// <summary>Scan progress snapshot (raised from worker threads).</summary>
public sealed record UnityCacheScanProgress(
    int TotalEntries, int ProcessedEntries, string CurrentPath, int BundlesScanned, int BundlesFromIndex);

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
/// UnityCacheMaterializationService）。扫描结果增量缓存在程序目录
/// <c>cache/unity-cache-index.json</c>，二次扫描只处理变化过的 bundle。
/// </summary>
public sealed class UnityCacheScanService
{
    private readonly string? _indexCacheFile;

    /// <param name="indexCacheFile">索引缓存文件；默认放在程序目录 cache/ 下，
    /// 测试可传入临时目录。</param>
    public UnityCacheScanService(string? indexCacheFile = null)
    {
        _indexCacheFile = indexCacheFile;
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

        var entries = EnumerateCacheEntries(cacheDirectory);
        if (entries.Count == 0)
            throw new InvalidDataException($"缓存目录里没有 <外层>/<内层>/__data 缓存条目：{cacheDirectory}");

        var index = LoadIndex();
        var catalog = LoadCatalog(gameDirectory);
        var diagnostics = new List<string>();
        if (catalog is null && !string.IsNullOrWhiteSpace(gameDirectory))
            diagnostics.Add("官方 catalog 缺失或无法解析，本次扫描不做 vanilla 基线判定。");

        var total = entries.Count;
        var processed = 0;
        var scanned = 0;
        var indexed = 0;
        var added = 0;
        var updated = 0;

        var results = new ConcurrentBag<(UnityCacheScanEntry Entry, IReadOnlyList<AssetRecord> Assets, bool FromCache)>();
        var failures = new ConcurrentQueue<string>();
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
                    var (assets, fromCache) = ScanEntry(entry, index, catalog);
                    results.Add((entry, assets, fromCache));
                    if (fromCache) Interlocked.Increment(ref indexed);
                    else Interlocked.Increment(ref scanned);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 个别 bundle 损坏/格式未知不阻断整体扫描；按铁律明确报告。
                    failures.Enqueue($"{entry.OuterKey}/{entry.InnerKey}: {ex.Message}");
                }
                var done = Interlocked.Increment(ref processed);
                progress?.Report(new UnityCacheScanProgress(total, done, entry.DataPath, scanned, indexed));
            });
        }, cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(failures.OrderBy(x => x, StringComparer.Ordinal));

        // 合并（单线程；MainWindow 的列表绑定的是检索结果快照，可直接改集合）。
        // 全缓存 ≈ 10 万+ 资产：必须用字典索引，逐条 FirstOrDefault 是 O(N²)。
        var existingByPath = new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var existingAsset in project.Assets)
            existingByPath.TryAdd(existingAsset.LogicalPath, existingAsset);
        var cacheSourceRegistered = project.Sources.Any(x =>
            string.Equals(x.Path, cacheDirectory, StringComparison.OrdinalIgnoreCase));
        foreach (var (entry, assets, _) in results.OrderBy(x => x.Entry.OuterKey, StringComparer.Ordinal)
                     .ThenBy(x => x.Entry.InnerKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var asset in assets)
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
                updated++;
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

        SaveIndex(index, entries, results);
        return new UnityCacheScanResult(total, scanned, indexed, added, updated, diagnostics);
    }

    // ── 单个 bundle：命中索引缓存则直接重建记录，否则完整解析 ──────────

    private (IReadOnlyList<AssetRecord> Assets, bool FromCache) ScanEntry(
        UnityCacheScanEntry entry,
        UnityCacheIndexFile index,
        CatalogFileService? catalog)
    {
        var info = new FileInfo(entry.DataPath);
        if (index.Entries.TryGetValue(entry.DataPath, out var cached) &&
            cached.Size == info.Length &&
            cached.MTimeUtcTicks == info.LastWriteTimeUtc.Ticks)
        {
            return (RebuildFromIndex(entry, cached), true);
        }

        var descriptors = new UnityAssetService().ScanBundle(entry.DataPath);
        var records = new List<AssetRecord>(descriptors.Count);
        string? baselineSummary = null;
        if (catalog is not null)
        {
            try { baselineSummary = CatalogBaselineService.Evaluate(catalog, entry.DataPath).Summary; }
            catch (Exception) { baselineSummary = null; }
        }
        for (var i = 0; i < descriptors.Count; i++)
        {
            var descriptor = descriptors[i];
            var record = new AssetRecord
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
            };
            if (baselineSummary is { Length: > 0 }) record.Metadata["catalogBaseline"] = baselineSummary;
            records.Add(record);
        }
        // 回写索引缓存（线程安全：按 DataPath 独立键）。
        lock (index.Entries)
        {
            index.Entries[entry.DataPath] = new UnityCacheIndexEntry
            {
                Size = info.Length,
                MTimeUtcTicks = info.LastWriteTimeUtc.Ticks,
                Outer = entry.OuterKey,
                Inner = entry.InnerKey,
                Assets = records.Select(r => new UnityCacheIndexAsset
                {
                    Container = r.ContainerPath ?? string.Empty,
                    PathId = r.UnityPathId ?? 0,
                    TypeId = r.UnityTypeId ?? 0,
                    Type = r.Type,
                    Size = r.Size,
                    Baseline = r.Metadata.TryGetValue("catalogBaseline", out var b) ? b : null
                }).ToList()
            };
        }
        return (records, false);
    }

    private static IReadOnlyList<AssetRecord> RebuildFromIndex(UnityCacheScanEntry entry, UnityCacheIndexEntry cached)
    {
        var records = new List<AssetRecord>(cached.Assets.Count);
        for (var i = 0; i < cached.Assets.Count; i++)
        {
            var item = cached.Assets[i];
            var record = new AssetRecord
            {
                LogicalPath = $"{entry.OuterKey}/{entry.InnerKey}/{item.Container}/{item.PathId}.{item.TypeId}",
                SourcePath = entry.DataPath,
                ContainerPath = item.Container,
                Account = entry.OuterKey,
                Bundle = entry.InnerKey,
                UnityPathId = item.PathId,
                UnityTypeId = item.TypeId,
                Type = item.Type,
                Size = item.Size,
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
            };
            if (!string.IsNullOrEmpty(item.Baseline)) record.Metadata["catalogBaseline"] = item.Baseline!;
            records.Add(record);
        }
        return records;
    }

    // ── 索引缓存（程序目录）────────────────────────────────────────

    internal UnityCacheIndexFile LoadIndex()
    {
        try
        {
            if (_indexCacheFile is not null && File.Exists(_indexCacheFile))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_indexCacheFile));
                var file = document.RootElement.Deserialize<UnityCacheIndexFile>(IndexOptions);
                if (file is not null && file.Version == UnityCacheIndexFile.CurrentVersion) return file;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 索引缓存损坏只影响速度，不影响正确性：直接重建。
        }
        return new UnityCacheIndexFile();
    }

    private void SaveIndex(UnityCacheIndexFile index, IReadOnlyList<UnityCacheScanEntry> entries,
        ConcurrentBag<(UnityCacheScanEntry Entry, IReadOnlyList<AssetRecord> Assets, bool FromCache)> results)
    {
        if (_indexCacheFile is null) return;
        try
        {
            // 只保留本次枚举到的条目（游戏更新换键后旧索引自然淘汰）。
            var currentPaths = entries.Select(x => x.DataPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pruned = index.Entries.Where(kv => currentPaths.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            index.Entries = new ConcurrentDictionary<string, UnityCacheIndexEntry>(pruned, StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(_indexCacheFile)!);
            var temp = _indexCacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(index, IndexOptions));
            File.Move(temp, _indexCacheFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写失败不影响扫描结果。
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

    private static readonly JsonSerializerOptions IndexOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>Persisted scan index (one JSON file in the program cache).</summary>
public sealed class UnityCacheIndexFile
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;

    /// <summary>ConcurrentDictionary：扫描并行阶段按 DataPath 独立读写。</summary>
    public ConcurrentDictionary<string, UnityCacheIndexEntry> Entries { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UnityCacheIndexEntry
{
    public long Size { get; set; }
    public long MTimeUtcTicks { get; set; }
    public string Outer { get; set; } = string.Empty;
    public string Inner { get; set; } = string.Empty;
    public List<UnityCacheIndexAsset> Assets { get; set; } = [];
}

public sealed class UnityCacheIndexAsset
{
    public string Container { get; set; } = string.Empty;
    public long PathId { get; set; }
    public int TypeId { get; set; }
    public AssetType Type { get; set; } = AssetType.Unknown;
    public long Size { get; set; }
    public string? Baseline { get; set; }
}
