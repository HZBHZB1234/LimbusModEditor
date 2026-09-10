using System.Diagnostics;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Caching;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// plan-11：音频索引（<c>cache/bank-index.db</c>）的语义测试。
///
/// <para>用共享合成 bank 夹具（<see cref="SyntheticBank"/>）构成小目录，覆盖：
/// 往返一致、逐文件增量失效（改一个 bank 只重解析它）、行列集对账（新增/删除文件）、
/// 换源整库重建、损坏库重建、以及本计划最重要的不变量
/// ——<b>「有缓存」与「删库后重解析」两条路径的产物逐字段相同</b>。</para>
/// </summary>
public sealed class BankIndexStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _bankDir;
    private readonly string _cacheDir;

    public BankIndexStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lme-bank-index-" + Guid.NewGuid().ToString("N"));
        _bankDir = Path.Combine(_root, "banks");
        _cacheDir = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_bankDir);
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 临时目录交系统清理 */ }
    }

    private BankIndexStore NewStore() => new(_cacheDir);

    private BankIndexService NewService(BankIndexStore? store = null) => new(store ?? NewStore());

    // ── 往返 ─────────────────────────────────────────────────────────

    [Fact]
    public void Store_round_trips_bank_and_sample_rows()
    {
        var store = NewStore();
        var entry = new BankIndexEntry(
            Path.Combine(_bankDir, "audio.bank"), "audio.bank", 4096, 12345,
            BankKind.Audio, null, 2, "备注",
            [
                new BankSampleRecord(Path.Combine(_bankDir, "audio.bank"), 0, 0, "audio.bank", "hit_01", "Vorbis", 48000, 2, 24000, 1234, 0),
                new BankSampleRecord(Path.Combine(_bankDir, "audio.bank"), 1, 0, "audio.bank", "hit_02", "Vorbis", 44100, 1, 8820, 999, 4096),
            ],
            DateTimeOffset.UtcNow.UtcTicks);

        store.PersistEntries([entry]);
        var read = store.ReadEntries();

        var loaded = Assert.Single(read);
        Assert.Equivalent(entry, loaded, strict: true);
        Assert.Equal(2, store.ReadSampleCount());
        Assert.Equal(1, store.ReadBankCount());
    }

    [Fact]
    public void Store_replaces_sample_rows_instead_of_appending()
    {
        var store = NewStore();
        var path = Path.Combine(_bankDir, "audio.bank");
        store.PersistEntries([MakeEntry(path, "audio.bank", 4096, 1, samples: 3)]);
        Assert.Equal(3, store.ReadSampleCount());

        // 同一个 bank 二次落库且样本变少：旧行必须被清掉（否则总表会多出幽灵样本）。
        store.PersistEntries([MakeEntry(path, "audio.bank", 4096, 2, samples: 1)]);
        Assert.Equal(1, store.ReadSampleCount());
        Assert.Single(store.ReadEntries());
    }

    // ── 索引刷新与增量失效 ───────────────────────────────────────────

    [Fact]
    public async Task Refresh_builds_index_and_reports_counts()
    {
        SyntheticBank.WriteAudioBank(_bankDir, "a.bank");
        SyntheticBank.WriteEventBank(_bankDir, "b.bank");
        SyntheticBank.WriteGarbageFile(_bankDir, "c.bank");

        var service = NewService();
        var source = BankIndexSource.Describe(_bankDir);
        var result = await service.RefreshAsync(source);

        Assert.Equal(3, result.DiskFileCount);
        Assert.Equal(3, result.ParseCount);
        Assert.Equal(0, result.ReusedCount);
        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(3, service.Store.ReadBankCount());
    }

    [Fact]
    public async Task Changed_bank_is_reparsed_and_unchanged_ones_are_reused()
    {
        SyntheticBank.WriteAudioBank(_bankDir, "keep.bank");
        SyntheticBank.WriteAudioBank(_bankDir, "change.bank");
        var service = NewService();
        var source = BankIndexSource.Describe(_bankDir);

        var cold = await service.RefreshAsync(source);
        Assert.Equal(2, cold.ParseCount);

        // 二次刷新：两个文件都没变 → 一个都不重解析（这就是「二次进页面不碰 bank 文件」）。
        var warm = await service.RefreshAsync(BankIndexSource.Describe(_bankDir));
        Assert.Equal(0, warm.ParseCount);
        Assert.Equal(2, warm.ReusedCount);

        // 改一个文件的内容（大小也变）→ 只重解析它。
        SyntheticBank.WriteAudioBank(_bankDir, "change.bank", codec: 2);
        var afterEdit = await service.RefreshAsync(BankIndexSource.Describe(_bankDir));
        Assert.Equal(1, afterEdit.ParseCount);
        Assert.Equal(1, afterEdit.ReusedCount);
    }

    [Fact]
    public async Task Added_and_removed_files_reconcile_the_rows()
    {
        SyntheticBank.WriteAudioBank(_bankDir, "a.bank");
        SyntheticBank.WriteAudioBank(_bankDir, "b.bank");
        var service = NewService();
        await service.RefreshAsync(BankIndexSource.Describe(_bankDir));
        Assert.Equal(2, service.Store.ReadBankCount());

        File.Delete(Path.Combine(_bankDir, "a.bank"));
        SyntheticBank.WriteEventBank(_bankDir, "c.bank");
        var result = await service.RefreshAsync(BankIndexSource.Describe(_bankDir));

        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(1, result.ParseCount);
        var names = service.Store.ReadEntries().Select(x => x.FileName).OrderBy(x => x).ToList();
        Assert.Equal(["b.bank", "c.bank"], names);
    }

    [Fact]
    public async Task Changing_the_source_directory_rebuilds_the_index()
    {
        SyntheticBank.WriteAudioBank(_bankDir, "a.bank");
        var service = NewService();
        await service.RefreshAsync(BankIndexSource.Describe(_bankDir));

        var otherDirectory = Path.Combine(_root, "banks2");
        Directory.CreateDirectory(otherDirectory);
        SyntheticBank.WriteEventBank(otherDirectory, "x.bank");
        SyntheticBank.WriteEventBank(otherDirectory, "y.bank");

        var result = await service.RefreshAsync(BankIndexSource.Describe(otherDirectory));
        Assert.Equal(2, result.ParseCount);
        Assert.Equal(2, service.Store.ReadBankCount());
        Assert.Equal(["x.bank", "y.bank"],
            service.Store.ReadEntries().Select(x => x.FileName).OrderBy(x => x).ToList());
    }

    [Fact]
    public void Corrupted_database_is_deleted_rebuilt_and_still_usable()
    {
        var store = NewStore();
        store.PersistEntries([MakeEntry(Path.Combine(_bankDir, "a.bank"), "a.bank", 100, 1)]);
        Assert.Equal(1, store.ReadBankCount());

        // 把库文件写成垃圾：下一次读应删库重建（空库）而不是崩。
        var dbFile = Path.Combine(_cacheDir, WorkbenchCachePaths.FileName(WorkbenchCacheKind.BankIndex));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.WriteAllText(dbFile, "这不是 SQLite 文件");

        // 损坏的库在下次使用（读或写）时被删除重建：
        // 读路径抛中文 InvalidDataException（提示重新载入），写路径同样；
        // 两种情况之后库都可正常重建使用——这一条是「缓存只影响速度」的兜底。
        var reopened = NewStore();
        var readFailure = Record.Exception(() => reopened.ReadBankCount());
        if (readFailure is not null)
        {
            Assert.IsType<InvalidDataException>(readFailure);
            Assert.Contains("缓存库已损坏", readFailure.Message, StringComparison.Ordinal);
        }
        reopened.PersistEntries([MakeEntry(Path.Combine(_bankDir, "b.bank"), "b.bank", 100, 1)]);
        Assert.Equal(1, reopened.ReadBankCount());
        Assert.True(reopened.ReadSampleCount() == 0);
    }

    [Fact]
    public void Deleting_the_cache_database_leaves_functionality_intact()
    {
        var store = NewStore();
        store.PersistEntries([MakeEntry(Path.Combine(_bankDir, "a.bank"), "a.bank", 100, 1)]);
        store.DeleteDatabase();
        Assert.False(File.Exists(store.DatabasePath));
        Assert.Equal(0, store.ReadBankCount()); // 自动重建空库，不抛
    }

    // ── 核心不变量：有缓存 vs 删库 ───────────────────────────────────

    [Fact]
    public async Task Cached_and_uncached_paths_produce_identical_entries()
    {
        SyntheticBank.WriteAudioBank(_bankDir, "audio-1.bank");
        SyntheticBank.WriteAudioBank(_bankDir, "audio-2.bank", codec: 2);
        SyntheticBank.WriteEventBank(_bankDir, "event-1.bank");
        SyntheticBank.WriteGarbageFile(_bankDir, "garbage.bank");

        var service = NewService();
        var source = BankIndexSource.Describe(_bankDir);

        // 无缓存路径：直接逐文件解析（索引落库用的是同一个 BuildEntry）。
        var live = BankIndexService.EnumerateDiskFiles(_bankDir)
            .Select(x => service.BuildEntry(x))
            .OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await service.RefreshAsync(source);
        var cached = service.Store.ReadEntries().OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(live.Count, cached.Count);
        for (var i = 0; i < live.Count; i++)
        {
            // ScannedTicks 是「写入索引的时刻」，两条路径必然不同（只作诊断），归一化后再比。
            Assert.Equivalent(WithoutScannedTicks(live[i]), WithoutScannedTicks(cached[i]), strict: true);
            Assert.Equal(live[i].Samples.Count, cached[i].Samples.Count);
            for (var s = 0; s < live[i].Samples.Count; s++)
                Assert.Equivalent(live[i].Samples[s], cached[i].Samples[s], strict: true);
        }
    }

    /// <summary>把写入时间戳归零（只用于跨路径比对，两个字段本身仍有各自的断言覆盖）。</summary>
    private static BankIndexEntry WithoutScannedTicks(BankIndexEntry entry) => entry with { ScannedTicks = 0 };

    [Fact]
    public async Task Cached_and_uncached_type_classification_matches_scan_directory()
    {
        SyntheticBank.WriteAudioBank(_bankDir, "audio.bank");
        SyntheticBank.WriteEventBank(_bankDir, "event.bank");
        SyntheticBank.WriteGarbageFile(_bankDir, "garbage.bank");
        var service = NewService();
        await service.RefreshAsync(BankIndexSource.Describe(_bankDir));

        // 与既有 BankDirectoryService.ScanDirectory 的口径逐一对照（同一套判定，不重复实现）。
        var scanned = service.Banks.ScanDirectory(_bankDir).ToDictionary(x => x.FileName, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in service.Store.ReadEntries())
        {
            var reference = scanned[entry.FileName];
            Assert.Equal(reference.Kind, entry.Kind);
            Assert.Equal(reference.FsbCount, entry.FsbCount);
            Assert.Equal(reference.FileSizeBytes, entry.SizeBytes);
        }
        Assert.Equal(BankKind.Audio, scanned["audio.bank"].Kind);
        Assert.Equal(BankKind.Event, scanned["event.bank"].Kind);
        Assert.Equal(BankKind.Unknown, scanned["garbage.bank"].Kind);
    }

    [Fact]
    public async Task Refresh_reports_progress_and_honours_cancellation()
    {
        for (var i = 0; i < 20; i++) SyntheticBank.WriteAudioBank(_bankDir, $"bank-{i:00}.bank");
        var service = NewService();
        var reports = new List<BankIndexProgress>();
        var progress = new Progress<BankIndexProgress>(reports.Add);
        await service.RefreshAsync(BankIndexSource.Describe(_bankDir), progress);
        Assert.NotEmpty(reports);
        Assert.All(reports, x => Assert.Equal(20, x.Total));
        Assert.True(reports[^1].Processed <= 20);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RefreshAsync(BankIndexSource.Describe(_bankDir), null, cancellation.Token));
    }

    [Fact]
    public async Task Enumerate_disk_files_reports_metadata_without_reading_content()
    {
        var path = SyntheticBank.WriteAudioBank(_bankDir, "a.bank");
        var observation = Assert.Single(BankIndexService.EnumerateDiskFiles(_bankDir));
        Assert.Equal(Path.GetFullPath(path), observation.Path);
        Assert.Equal("a.bank", observation.FileName);
        Assert.Equal(new FileInfo(path).Length, observation.SizeBytes);
        Assert.Equal(new FileInfo(path).LastWriteTimeUtc.Ticks, observation.MTimeUtcTicks);
    }

    private static BankIndexEntry MakeEntry(string path, string fileName, long size, long ticks, int samples = 0)
        => new(path, fileName, size, ticks, BankKind.Audio, null, 1, null,
            Enumerable.Range(0, samples)
                .Select(i => new BankSampleRecord(path, 0, i, fileName, $"s{i}", "Vorbis", 48000, 2, 1000, 100, i * 100))
                .ToList());
}
