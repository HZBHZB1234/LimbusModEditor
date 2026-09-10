using LimbusModEditor.Application.Caching;
using LimbusModEditor.Application.StaticMods;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// plan-12：静态表索引（<c>cache/static-tables.db</c>）的语义测试。
///
/// <para>覆盖：元数据往返、内层哈希换源即整库重建、<b>正文缓存有界（LRU 淘汰）</b>、
/// 正文缓存清空、损坏库删重建，以及「外层键变化不得触发重建」这条关键设计约束
/// （外层键是缓存目录名，热修后会重新分配，拿它当缓存键会误判换源）。</para>
/// </summary>
public sealed class StaticTableIndexStoreTests : IDisposable
{
    private readonly string _cacheDir;

    public StaticTableIndexStoreTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "lme-static-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_cacheDir, true); } catch (Exception) { /* 临时目录 */ }
    }

    private StaticTableIndexStore NewStore(long documentLimit = StaticTableIndexStore.DefaultDocumentCacheLimitBytes)
        => new(_cacheDir, documentLimit);

    private static StaticIndexSource Source(string innerHash = "62d6e466111122223333444455556666",
        string? outerKey = "64bd0105aaaa", string signature = "100:200")
        => new($"static_s1_0_assets_all_{innerHash}.bundle", innerHash, outerKey, @"C:\cache\__data", signature);

    private static StaticTableEntry Entry(string dataClass, string fileName, long size = 1024, bool isUtf8 = true)
        => new($@"Assets/Resources_moved/StaticData/static-data/{dataClass}/{fileName}.json",
            fileName, dataClass, fileName, "CAB-abc", Math.Abs(fileName.GetHashCode()), size, isUtf8);

    // ── 元数据 ───────────────────────────────────────────────────────

    [Fact]
    public void Entries_round_trip_with_grouping_and_derived_file_name()
    {
        var store = NewStore();
        var source = Source();
        Assert.True(store.EnsureSource(source)); // 首次 = 需要重建
        store.PersistEntries(source, [Entry("personality", "1001"), Entry("personality", "1002"), Entry("skill", "s1")]);

        var entries = store.ReadEntries();
        Assert.Equal(3, entries.Count);
        Assert.Equal(["personality", "personality", "skill"], entries.Select(x => x.DataClass).ToArray());
        Assert.All(entries, x => Assert.Equal(x.FileName, Path.GetFileNameWithoutExtension(x.ContainerEntry)));
        Assert.Equal(3, store.ReadTableCount());

        // 二次使用：源与签名都没变 → 不需要重建。
        Assert.False(store.EnsureSource(source));
        Assert.Equal(3, store.ReadEntries().Count);
    }

    [Fact]
    public void Changing_inner_hash_rebuilds_the_whole_index()
    {
        var store = NewStore();
        var first = Source();
        store.EnsureSource(first);
        store.PersistEntries(first, [Entry("personality", "old")]);

        // 内层内容哈希变（= 官方热修换了 bundle 内容）→ 整库重建。
        var second = Source(innerHash: "aaaaaaaa111122223333444455556666");
        Assert.True(store.EnsureSource(second));
        Assert.Equal(0, store.ReadTableCount());

        store.PersistEntries(second, [Entry("skill", "new")]);
        var entries = store.ReadEntries();
        Assert.Equal("new", Assert.Single(entries).FileName);
    }

    [Fact]
    public void Changing_only_the_outer_key_keeps_the_index()
    {
        var store = NewStore();
        var first = Source(outerKey: "64bd0105aaaa");
        store.EnsureSource(first);
        store.PersistEntries(first, [Entry("personality", "1001")]);

        // 外层键是 Unity 缓存目录名，热修后会重新分配；内容（内层哈希）没变就不该重建。
        var rotated = Source(outerKey: "ffffffffbbbb");
        Assert.False(store.EnsureSource(rotated));
        Assert.Equal(1, store.ReadTableCount());
    }

    [Fact]
    public void Same_source_with_new_file_mtime_only_updates_the_signature()
    {
        var store = NewStore();
        var first = Source(signature: "100:200");
        store.EnsureSource(first);
        store.PersistEntries(first, [Entry("personality", "1001")]);

        var touched = Source(signature: "100:999");
        Assert.False(store.EnsureSource(touched)); // 不整体重建（表的集合多半没变）
        Assert.Equal(1, store.ReadTableCount());
    }

    // ── 正文缓存（有界 + LRU）────────────────────────────────────────

    [Fact]
    public void Document_cache_round_trips_and_reports_usage()
    {
        var store = NewStore();
        var key = Entry("personality", "1001").Key;
        Assert.Null(store.TryReadDocument(key));

        store.PersistDocument(key, """{"a":1}""");
        Assert.Equal("""{"a":1}""", store.TryReadDocument(key));
        var usage = store.ReadDocumentCacheUsage();
        Assert.Equal(1, usage.Count);
        Assert.Equal(7, usage.Bytes);
        Assert.Contains(key, store.ReadCachedDocumentKeys());

        // null（非 UTF-8 表）不写缓存。
        store.PersistDocument(Entry("skill", "s1").Key, null);
        Assert.Equal(1, store.ReadDocumentCacheUsage().Count);
    }

    [Fact]
    public void Document_cache_is_bounded_and_evicts_least_recently_opened()
    {
        // 上限 1 KB：写 3 张各 400 字节的表，必须被淘汰到上限以内。
        var store = NewStore(documentLimit: 1024);
        var keys = new[] { "a", "b", "c" };
        var text = new string('x', 400);
        store.PersistDocument(keys[0], text);
        Thread.Sleep(5);
        store.PersistDocument(keys[1], text);
        Thread.Sleep(5);
        store.PersistDocument(keys[2], text);

        var usage = store.ReadDocumentCacheUsage();
        Assert.True(usage.Bytes <= 1024, $"正文缓存超限：{usage.Bytes} 字节");
        // 最近打开的那张必须还在（LRU：淘汰最久未打开）。
        Assert.Equal(text, store.TryReadDocument(keys[2]));
        Assert.True(store.TryReadDocument(keys[0]) is null || store.TryReadDocument(keys[1]) is null,
            "应至少淘汰最早写入的一行");
    }

    [Fact]
    public void Clearing_documents_keeps_the_metadata_index()
    {
        var store = NewStore();
        var source = Source();
        store.EnsureSource(source);
        store.PersistEntries(source, [Entry("personality", "1001")]);
        store.PersistDocument(Entry("personality", "1001").Key, "{}");

        store.ClearDocuments();
        Assert.Equal(0, store.ReadDocumentCacheUsage().Count);
        Assert.Equal(1, store.ReadTableCount()); // 元数据仍在
    }

    [Fact]
    public void Deleting_the_database_rebuilds_empty_and_stays_usable()
    {
        var store = NewStore();
        var source = Source();
        store.EnsureSource(source);
        store.PersistEntries(source, [Entry("personality", "1001")]);

        store.DeleteDatabase();
        Assert.False(File.Exists(store.DatabasePath));
        Assert.Equal(0, store.ReadTableCount()); // 自动重建空库，不抛
        store.PersistEntries(Source(), [Entry("skill", "s1")]);
        Assert.Equal(1, store.ReadTableCount());
    }

    [Fact]
    public void Corrupted_database_is_deleted_rebuilt_and_still_usable()
    {
        var store = NewStore();
        var source = Source();
        store.PersistEntries(source, [Entry("personality", "1001")]);

        var dbFile = Path.Combine(_cacheDir, WorkbenchCachePaths.FileName(WorkbenchCacheKind.StaticTables));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.WriteAllText(dbFile, "这不是 SQLite 文件");

        var reopened = NewStore();
        var failure = Record.Exception(() => reopened.ReadTableCount());
        if (failure is not null)
        {
            Assert.IsType<InvalidDataException>(failure);
            Assert.Contains("缓存库已损坏", failure.Message, StringComparison.Ordinal);
        }
        reopened.PersistEntries(Source(), [Entry("skill", "s1")]);
        Assert.Equal(1, reopened.ReadTableCount());
    }

    [Fact]
    public void Grouped_by_data_class_orders_classes_and_tables()
    {
        var store = NewStore();
        var source = Source();
        store.PersistEntries(source,
            [Entry("skill", "b"), Entry("personality", "1002"), Entry("personality", "1001")]);
        var load = new StaticIndexLoad(source, store.ReadEntries(), true, true, true, TimeSpan.Zero);
        var groups = load.GroupedByDataClass();
        Assert.Equal(["personality", "skill"], groups.Select(x => x.DataClass).ToArray());
        Assert.Equal(["1001", "1002"], groups[0].Tables.Select(x => x.FileName).ToArray());
    }
}
