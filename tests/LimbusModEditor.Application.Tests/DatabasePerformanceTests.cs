using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.Loader;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Unity;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace LimbusModEditor.Application.Tests;

public class DatabasePerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void Real_index_and_catalog_benchmark()
    {
        var root = Environment.GetEnvironmentVariable("LME_PERF_OUTPUT");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        var source = Environment.GetEnvironmentVariable("LME_PERF_INDEX")!;
        Assert.True(File.Exists(source), "LME_PERF_INDEX 必须指向真实索引副本。");
        if (Environment.GetEnvironmentVariable("LME_PERF_LEGACY") == "1")
        {
            var normalized = Path.Combine(root, "normalized-input.db");
            var target = new UnityCacheSqliteIndexStore(normalized);
            var entriesToCopy = ReadLegacy(source).Select(x =>
                new UnityCacheScanEntry(x.Bundle.Outer, x.Bundle.Inner, x.Bundle.DataPath)).ToArray();
            target.PersistAll(entriesToCopy, ReadLegacy(source));
            // 测试夹具转换不参与计时；生产代码仅支持 v2，不带旧库迁移路径。
            using var expected = ReadLegacy(source).GetEnumerator();
            foreach (var (bundle, rows) in target.ReadAll())
            {
                Assert.True(expected.MoveNext());
                Assert.Equal(expected.Current.Bundle, bundle);
                Assert.Equal(expected.Current.Rows, rows);
            }
            Assert.False(expected.MoveNext());
            source = normalized;
        }
        var store = new UnityCacheSqliteIndexStore(source);
        var metrics = new Dictionary<string, double>();
        void Measure(string key, Action action)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var watch = Stopwatch.StartNew();
            action();
            metrics[key + "Ms"] = watch.Elapsed.TotalMilliseconds;
            metrics[key + "AllocatedMB"] = (GC.GetTotalAllocatedBytes(true) - allocated) / 1048576.0;
        }
        var count = 0;
        Measure("Read", () => { foreach (var (_, rows) in store.ReadAll()) count += rows.Count; });
        Assert.True(count > 100_000);
        metrics["Rows"] = count;
        Measure("Relations", () => metrics["NamedRows"] = store.ReadContainerRows().Count);
        var entries = store.ReadBundleIndex(StringComparer.OrdinalIgnoreCase).Values
            .Select(b => new UnityCacheScanEntry(b.Outer, b.Inner, b.DataPath)).ToArray();
        var destination = Path.Combine(root, "rebuilt.db");
        Assert.False(File.Exists(destination), "请为每次基准使用新的输出目录。");
        var rebuilt = new UnityCacheSqliteIndexStore(destination);
        rebuilt.EnsureSchema();
        Measure("Persist", () => rebuilt.PersistAll(entries, store.ReadAll()));
        using (var connection = new SqliteConnection($"Data Source={destination}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            command.ExecuteNonQuery();
        }
        metrics["DatabaseMB"] = new FileInfo(destination).Length / 1048576.0;
        var catalogPath = Path.Combine(Environment.GetEnvironmentVariable("LME_GAME_DIR") ??
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company",
            "LimbusCompany_Data", "StreamingAssets", "aa", "catalog.bin");
        Measure("Catalog", () =>
        {
            var catalog = CatalogFileService.Load(catalogPath);
            Assert.NotEmpty(catalog.RecordsByInnerHash);
            File.WriteAllText(Path.Combine(root, "catalog.json"), JsonSerializer.Serialize(catalog.RecordsByInnerHash));
        });
        var json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(root, "metrics.json"), json);
        output.WriteLine(json);
    }

    private static IEnumerable<(UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow> Rows)> ReadLegacy(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data_path,size,mtime_ticks,outer_key,inner_key,static_bundle FROM bundles ORDER BY data_path";
        var bundles = new List<UnityCacheIndexBundle>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) bundles.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetString(3), reader.GetString(4), (StaticKind)reader.GetInt32(5)));
        command.CommandText = "SELECT bundle_index,container,path_id,type_id,type,size,baseline,container_entry FROM assets WHERE data_path=$p ORDER BY bundle_index";
        command.Parameters.Add("$p", SqliteType.Text);
        foreach (var bundle in bundles)
        {
            command.Parameters[0].Value = bundle.DataPath;
            var rows = new List<UnityCacheIndexRow>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3),
                    (AssetType)reader.GetInt32(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            yield return (bundle, rows);
        }
    }

    [Fact]
    public void Real_export_object_reads_are_identical_and_load_each_bundle_once()
    {
        var root = Environment.GetEnvironmentVariable("LME_PERF_OUTPUT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var cache = Environment.GetEnvironmentVariable("LME_UNITY_CACHE_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany");
        var entry = UnityCacheScanService.EnumerateCacheEntries(cache)
            .Where(x => new FileInfo(x.DataPath).Length is > 1_000_000 and < 10_000_000).First();
        using var backend = new AssetsToolsBackend();
        var objects = backend.InspectBundle(entry.DataPath).Take(64).ToArray();
        Assert.True(objects.Length > 1);
        var baselineAssembly = Environment.GetEnvironmentVariable("LME_PERF_BASELINE_UNITY");
        var legacyContext = new AssemblyLoadContext("performance-baseline", isCollectible: true);
        var legacyType = string.IsNullOrWhiteSpace(baselineAssembly) ? null :
            legacyContext.LoadFromAssemblyPath(baselineAssembly).GetType(typeof(AssetsToolsBackend).FullName!, throwOnError: true);
        using var legacyBackend = legacyType is null ? null : (IDisposable)Activator.CreateInstance(legacyType)!;
        var legacyRead = legacyType?.GetMethod(nameof(AssetsToolsBackend.ReadBundleSerializedObject));
        var allocated = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        var expected = objects.Select(x =>
        {
            if (legacyRead is null)
            {
                var raw = backend.ReadBundleSerializedObject(entry.DataPath, x.ContainerPath, x.PathId);
                return (raw.Data, raw.TypeTableIndex);
            }
            var old = legacyRead.Invoke(legacyBackend, [entry.DataPath, x.ContainerPath, x.PathId])!;
            return (Data: (byte[])old.GetType().GetProperty("Data")!.GetValue(old)!,
                TypeTableIndex: (int)old.GetType().GetProperty("TypeTableIndex")!.GetValue(old)!);
        }).ToArray();
        var singleMs = watch.Elapsed.TotalMilliseconds;
        var singleMB = (GC.GetTotalAllocatedBytes(true) - allocated) / 1048576.0;
        allocated = GC.GetTotalAllocatedBytes(true);
        watch.Restart();
        using var reader = new AssetsToolsBackend.BundleObjectReader(entry.DataPath);
        for (var i = 0; i < objects.Length; i++)
        {
            var actual = reader.Read(objects[i].ContainerPath, objects[i].PathId);
            Assert.Equal(expected[i].Data, actual.Data);
            Assert.Equal(expected[i].TypeTableIndex, actual.TypeTableIndex);
        }
        Assert.Equal(1, reader.BundleLoadCount);
        var json = JsonSerializer.Serialize(new { Baseline = baselineAssembly ?? "isolated-single-object-sessions", Objects = objects.Length, SingleMs = singleMs, SingleAllocatedMB = singleMB,
            BatchMs = watch.Elapsed.TotalMilliseconds, BatchAllocatedMB = (GC.GetTotalAllocatedBytes(true) - allocated) / 1048576.0 },
            new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "export-read.json"), json);
        output.WriteLine(json);
        legacyContext.Unload();
    }

    [Fact]
    public async Task Real_cold_scan_rehydrate_and_hot_scan()
    {
        if (Environment.GetEnvironmentVariable("LME_PERF_FULL_SCAN") != "1") return;
        var root = Environment.GetEnvironmentVariable("LME_PERF_OUTPUT")!;
        Assert.False(string.IsNullOrWhiteSpace(root));
        Directory.CreateDirectory(root);
        var cache = Environment.GetEnvironmentVariable("LME_UNITY_CACHE_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany");
        var game = Environment.GetEnvironmentVariable("LME_GAME_DIR") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company";
        var db = Path.Combine(root, "cold-index.db");
        Assert.False(File.Exists(db));
        var service = new UnityCacheScanService(db);
        var project = new ModProject { Name = "真实全量扫描" };
        var watch = Stopwatch.StartNew();
        var cold = await service.ScanIntoProjectAsync(project, cache, game);
        var coldMs = watch.Elapsed.TotalMilliseconds;
        var count = project.Assets.Count;
        Assert.True(count > 1_000_000);
        Assert.Equal(0, cold.IndexedBundles);
        Assert.Equal(count, new UnityCacheSqliteIndexStore(db).ReadAll().Sum(x => x.Rows.Count));
        project = new ModProject { Name = "真实热启动" };
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        watch.Restart();
        Assert.Equal(count, await service.RehydrateFromIndexAsync(project));
        var rehydrateMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var hot = await service.ScanIntoProjectAsync(project, cache, game, projectMatchesIndex: true);
        var hotMs = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(0, hot.ScannedBundles);
        Assert.Equal(cold.ScannedBundles, hot.IndexedBundles);
        Assert.True(hot.SkippedMerge);
        Assert.Equal(count, project.Assets.Count);
        if (Environment.GetEnvironmentVariable("LME_PERF_COMPARE_INDEX") is { Length: > 0 } previous)
        {
            // 比较包括每行基线与静态标记的全部事实，忽略并行解析导致的 bundle 插入次序。
            var before = new UnityCacheSqliteIndexStore(previous).ReadAll().ToDictionary(x => x.Bundle.DataPath,
                x => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { x.Bundle, x.Rows })), StringComparer.OrdinalIgnoreCase);
            foreach (var batch in new UnityCacheSqliteIndexStore(db).ReadAll())
            {
                Assert.True(before.Remove(batch.Bundle.DataPath, out var expected));
                Assert.Equal(expected, SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { batch.Bundle, batch.Rows })));
            }
            Assert.Empty(before);
        }
        var json = JsonSerializer.Serialize(new { Rows = count, cold.TotalEntries, cold.ScannedBundles,
            ColdMs = coldMs, RehydrateMs = rehydrateMs, HotScanMs = hotMs, cold.Diagnostics,
            PeakWorkingSetMB = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0 },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(root, "full-scan.json"), json);
        output.WriteLine(json);
    }
}
