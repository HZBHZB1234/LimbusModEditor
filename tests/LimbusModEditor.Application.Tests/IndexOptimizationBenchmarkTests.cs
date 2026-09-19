using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LimbusModEditor.Application.Scanning;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace LimbusModEditor.Application.Tests;

public sealed class IndexOptimizationBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public void Measure_wide_query_on_existing_index()
    {
        var directory = Environment.GetEnvironmentVariable("LME_WIDE_BENCH_OUTPUT");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var store = new UnityCacheSqliteIndexStore(Environment.GetEnvironmentVariable("LME_WIDE_BENCH_INDEX")!);
        Assert.True(store.IsDerivedReady());
        var timings = new List<double>();
        IReadOnlyList<int> ranks = [];
        var allocated = GC.GetTotalAllocatedBytes(true);
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            ranks = store.SearchRanks("CAB-");
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }
        Assert.Equal(store.Count(), ranks.Count);
        var json = JsonSerializer.Serialize(new { MedianMs = timings.Order().ElementAt(1), Count = ranks.Count,
            AllocatedMiB = (GC.GetTotalAllocatedBytes(true) - allocated) / 1048576.0 / 3,
            Hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ranks))) },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "wide-query.json"), json);
        output.WriteLine(json);
    }

    [Fact]
    public void Measure_real_derived_index_and_queries()
    {
        var directory = Environment.GetEnvironmentVariable("LME_DERIVED_BENCH_OUTPUT");
        if (string.IsNullOrEmpty(directory)) return;
        var sourcePath = Environment.GetEnvironmentVariable("LME_DERIVED_BENCH_SOURCE")!;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "index.db");
        Assert.False(File.Exists(path));
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        using (var destination = new SqliteConnection($"Data Source={path}"))
        {
            source.Open(); destination.Open(); source.BackupDatabase(destination);
            using var clear = destination.CreateCommand();
            clear.CommandText = "DELETE FROM derived_state";
            clear.ExecuteNonQuery();
        }
        var store = new UnityCacheSqliteIndexStore(path);
        var metrics = new Dictionary<string, object>();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        var built = store.EnsureDerived();
        metrics["BuildMs"] = watch.Elapsed.TotalMilliseconds;
        metrics["BuildAllocatedMiB"] = (GC.GetTotalAllocatedBytes(true) - allocated) / 1048576.0;
        metrics["Rows"] = built.Rows;
        foreach (var query in new[] { "Faust", "Assets/Animation", "Texture2D", "12345", "CAB-", "不存在的资源" })
        {
            store.SearchRanks(query);
            var times = new List<double>();
            IReadOnlyList<int> ranks = [];
            for (var i = 0; i < 3; i++)
            {
                watch.Restart(); ranks = store.SearchRanks(query); times.Add(watch.Elapsed.TotalMilliseconds);
            }
            metrics[query] = new { Ms = times.Order().ElementAt(1), Count = ranks.Count,
                Hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ranks))) };
        }
        watch.Restart();
        for (var i = 0; i < 100; i++) Assert.Equal(200, store.ReadPage(i * 1000, 200).Count);
        metrics["PageMeanMs"] = watch.Elapsed.TotalMilliseconds / 100;
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            command.ExecuteNonQuery();
            metrics["DatabaseMiB"] = new FileInfo(path).Length / 1048576.0;
            command.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", command.ExecuteScalar());
        }
        var json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "metrics.json"), json);
        output.WriteLine(json);
    }
}
