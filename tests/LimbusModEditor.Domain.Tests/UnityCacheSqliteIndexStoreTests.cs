using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using Microsoft.Data.Sqlite;

namespace LimbusModEditor.Domain.Tests;

public sealed class UnityCacheSqliteIndexStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-index-v2-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "index.db");
    private static UnityCacheIndexBundle Bundle(string name) => new(name, 123, 456, "outer", name, true);
    private static UnityCacheScanEntry Entry(string name) => new("outer", name, name);
    private static UnityCacheIndexRow Row(int index, string? baseline = "基线") =>
        new(index, "共享容器", long.MaxValue - index, 49, AssetType.Text, 9876543210, baseline, index == 0 ? "assets/中文.json" : null);

    [Fact]
    public void Roundtrip_preserves_empty_bundles_nulls_large_ids_and_shared_strings()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a"), Entry("b")], [(Bundle("a"), new[] { Row(1, null), Row(0) }), (Bundle("b"), Array.Empty<UnityCacheIndexRow>())]);
        var batches = store.ReadAll().ToArray();
        Assert.Equal(2, batches.Length);
        Assert.Equal(Bundle("a"), batches[0].Bundle);
        Assert.Equal(new[] { Row(0), Row(1, null) }, batches[0].Rows);
        Assert.Same(batches[0].Rows[0].Container, batches[0].Rows[1].Container);
        Assert.Empty(batches[1].Rows);
        Assert.Single(store.ReadContainerRows());
        Assert.Equal("b", Assert.Single(store.ReadAll(new HashSet<string> { "b" })).Bundle.DataPath);
    }

    [Fact]
    public void Failed_replacement_rolls_back_rows_metadata_and_pruning()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a"), Entry("b")], [(Bundle("a"), new[] { Row(0) }), (Bundle("b"), new[] { Row(1) })]);
        Assert.Throws<SqliteException>(() => store.PersistAll([Entry("a")],
            [(Bundle("a") with { Size = 999 }, new[] { Row(0), Row(0) })]));
        var all = store.ReadAll().ToArray();
        Assert.Equal(2, all.Length);
        Assert.Equal(123, all[0].Bundle.Size);
        Assert.Equal(Row(0), Assert.Single(all[0].Rows));
        store.PersistAll([Entry("A")], [(Bundle("A"), new[] { Row(2) })]);
        Assert.Equal(Row(2), Assert.Single(Assert.Single(store.ReadAll()).Rows));
    }

    [Fact]
    public void Streaming_reader_keeps_one_snapshot_while_another_store_updates()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a"), Entry("b")], [(Bundle("a"), new[] { Row(0) }), (Bundle("b"), new[] { Row(1) })]);
        using (var iterator = store.ReadAll().GetEnumerator())
        {
            Assert.True(iterator.MoveNext());
            new UnityCacheSqliteIndexStore(Database).PersistAll([Entry("a"), Entry("b")], [(Bundle("b"), new[] { Row(2) })]);
            Assert.True(iterator.MoveNext());
            Assert.Equal(Row(1), Assert.Single(iterator.Current.Rows));
        }
        Assert.Equal(Row(2), Assert.Single(store.ReadAll().Last().Rows));
    }

    [Fact]
    public async Task Rehydrate_remaps_known_ids_but_preserves_type_tree_classifications()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a")], [(Bundle("a"), new[]
        {
            Row(0) with { TypeId = 28, Type = AssetType.Unknown },
            Row(1) with { TypeId = int.MaxValue, Type = AssetType.SpriteAtlas }
        })]);
        var project = new ModProject();
        Assert.Equal(2, await new UnityCacheScanService(Database).RehydrateFromIndexAsync(project));
        Assert.Equal(new[] { AssetType.Texture, AssetType.SpriteAtlas }, project.Assets.Select(x => x.Type));
    }

    [Fact]
    public void Cancellation_rolls_back_pruning_and_updates()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a"), Entry("b")], [(Bundle("a"), new[] { Row(0) }), (Bundle("b"), new[] { Row(1) })]);
        using var cancellation = new CancellationTokenSource();
        IEnumerable<(UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow> Rows)> Changes()
        {
            yield return (Bundle("a"), new[] { Row(2) });
            cancellation.Cancel();
        }
        Assert.Throws<OperationCanceledException>(() => store.PersistAll([Entry("a")], Changes(), cancellation.Token));
        Assert.Equal(2, store.ReadAll().Count());
        Assert.Equal(Row(0), Assert.Single(store.ReadAll().First().Rows));
    }

    [Fact]
    public void Missing_asset_table_invalidates_bundle_freshness()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a")], [(Bundle("a"), new[] { Row(0) })]);
        using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE assets";
            command.ExecuteNonQuery();
        }
        Assert.Empty(new UnityCacheSqliteIndexStore(Database).ReadBundleIndex(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Old_schema_is_invalidated_and_recreated()
    {
        Directory.CreateDirectory(_root);
        using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE bundles(data_path TEXT); CREATE TABLE assets(data_path TEXT); INSERT INTO bundles VALUES('old');";
            command.ExecuteNonQuery();
        }
        var store = new UnityCacheSqliteIndexStore(Database);
        Assert.Empty(store.ReadAll());
        store.PersistAll([Entry("a")], [(Bundle("a"), new[] { Row(0) })]);
        Assert.Single(store.ReadAll());
    }

    // ── 派生层（分页名次 + 子串检索）────────────────────────────────────
    //
    // 这一组测试的判据不是「手写期望值」，而是**拿同一个公共口径当场重算一遍**：
    //   · 全序   → AssetDisplay.CompareCatalogOrder（派生层名次的定义）
    //   · 显示路径 → AssetDisplay.CacheRowDisplayPath（列表显示 / 派生层回填 / 复核 三处共用）
    //   · 检索   → 对显示路径做 OrdinalIgnoreCase 子串暴力扫描
    // 这样一旦派生层与「内存里那条路径」分家（本次重构最大的风险），测试立刻红。

    private static UnityCacheIndexRow CatalogRow(int index, string? containerEntry, long pathId,
        int typeId = 49, AssetType type = AssetType.Text)
        => new(index, "共享容器", pathId, typeId, type, 9876543210, null, containerEntry);

    /// <summary>刻意造出三种「SQL 自己排不出来」的形态：
    /// ① 数字段自然序（icon2 在 icon10 前，BINARY 序恰好相反）；
    /// ② 显示路径完全并列（只差大小写 / 只差编号），要靠 LogicalPath 才分先后；
    /// ③ 无容器条目 → 落到「未命名资源/类型 #编号」兜底名。</summary>
    private static (UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow> Rows)[] CatalogBatches() =>
    [
        (Bundle("a"), new[]
        {
            CatalogRow(0, "assets/anim/icon2.png", 700),
            CatalogRow(1, "assets/anim/icon10.png", 701),
            CatalogRow(2, "assets/anim/icon2.png", 702),
            CatalogRow(3, null, 703),
        }),
        (Bundle("b"), new[]
        {
            CatalogRow(0, "assets/anim/icon2.png", 702),
            CatalogRow(1, null, 703),
            CatalogRow(2, "Assets/anim/ICON2.PNG", 704),
            // 这一条是「FTS 超集」的反例：它同时含 'ico'、'con'、'on2' 三个 trigram，
            // 却没有连续子串 'icon2'（中间隔了一个 '/'）。复核必须把它滤掉。
            CatalogRow(3, "assets/icon/on2/x.png", 705),
        }),
    ];

    private static string DisplayPathOf(UnityCacheIndexBundle bundle, UnityCacheIndexRow row)
        => AssetDisplay.CacheRowDisplayPath(row.TypeId, row.Type, row.PathId, row.ContainerEntry);

    private static string LogicalPathOf(UnityCacheIndexBundle bundle, UnityCacheIndexRow row)
        => $"{bundle.Outer}/{bundle.Inner}/共享容器/{row.PathId}.{row.TypeId}";

    private static List<(UnityCacheIndexBundle Bundle, UnityCacheIndexRow Row)> ExpectedCatalogOrder(
        (UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow> Rows)[] batches)
    {
        var all = batches
            .SelectMany(b => b.Rows.Select(r => (Bundle: b.Bundle, Row: r)))
            .ToList();
        // LogicalPath 全库唯一 → 这是严格全序，排序结果与稳定性无关。
        all.Sort((x, y) => AssetDisplay.CompareCatalogOrder(
            DisplayPathOf(x.Bundle, x.Row), LogicalPathOf(x.Bundle, x.Row),
            DisplayPathOf(y.Bundle, y.Row), LogicalPathOf(y.Bundle, y.Row)));
        return all;
    }

    private static List<(UnityCacheIndexBundle Bundle, UnityCacheIndexRow Row)> Page(
        UnityCacheSqliteIndexStore store, long from, int take)
        => store.ReadPage(from, take).Select(p => (p.Bundle, p.Row)).ToList();

    [Fact]
    public void Derived_layer_orders_rows_exactly_like_the_documented_catalog_order()
    {
        var batches = CatalogBatches();
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a"), Entry("b")], batches);
        store.EnsureDerived();

        Assert.Equal(8, store.Count());
        var expected = ExpectedCatalogOrder(batches);

        // 先证明这套样本不是恒真的：自然序与 BINARY（Ordinal）序确实不同，
        // 而且确实存在并列 —— 这两条正是「SQL 排不出来、必须物化名次」的原因。
        var natural = expected.Select(x => DisplayPathOf(x.Bundle, x.Row)).ToArray();
        Assert.NotEqual(natural.OrderBy(x => x, StringComparer.Ordinal).ToArray(), natural);
        Assert.Contains(Enumerable.Range(1, natural.Length - 1),
            i => AssetDisplay.ComparePaths(natural[i - 1], natural[i]) == 0);

        Assert.Equal(expected, Page(store, 0, expected.Count));
    }

    [Fact]
    public void Paging_by_rank_is_depth_independent_and_clamps_at_the_end()
    {
        var batches = CatalogBatches();
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a"), Entry("b")], batches);
        store.EnsureDerived();
        var expected = ExpectedCatalogOrder(batches);

        // 每页 3 条拼起来 == 整段全序（步长与页大小刻意不成整数倍，跨页边界也覆盖到）。
        var stitched = new List<(UnityCacheIndexBundle Bundle, UnityCacheIndexRow Row)>();
        for (long from = 0; from < expected.Count; from += 3) stitched.AddRange(Page(store, from, 3));
        Assert.Equal(expected, stitched);

        // 最后一页不足一页时只返回剩余部分；越界返回空而不是抛异常。
        var tail = Page(store, expected.Count - 1, 3);
        Assert.Equal(expected[^1], Assert.Single(tail));
        Assert.Empty(store.ReadPage(expected.Count, 3));
        Assert.Empty(store.ReadPage(expected.Count + 100, 3));
    }

    [Fact]
    public void ReadByRanks_returns_the_rows_those_ranks_name_in_ascending_order()
    {
        var batches = CatalogBatches();
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a"), Entry("b")], batches);
        store.EnsureDerived();
        var expected = ExpectedCatalogOrder(batches);

        var ranks = new[] { 5, 0, 3, expected.Count - 1 };
        Assert.Equal(
            ranks.OrderBy(x => x).Select(i => expected[i]).ToArray(),
            store.ReadByRanks(ranks).Select(p => (p.Bundle, p.Row)).ToArray());
        Assert.Empty(store.ReadByRanks([]));
    }

    [Theory]
    [InlineData("icon2")]        // 常规 trigram 复合查询
    [InlineData("ICON2")]        // 大小写不敏感
    [InlineData("anim/icon2")]   // 跨 '/' 的连续子串
    [InlineData("未命名")]        // 中文三字（正好一个 trigram）
    [InlineData("文本 #7")]       // 含空格：trigram 跨空白建词项，见 logs/probe_trigram_space.py
    [InlineData("icon2.png")]    // 含 '.'
    [InlineData("i")]            // < 3 字符：trigram 取不到词项 → 退化全表扫描
    [InlineData("n2")]           // 同上
    [InlineData("_")]            // LIKE 的通配符：这里必须是字面量，且谁都不该命中
    [InlineData("")]             // 空串：不该扫全表
    [InlineData("   ")]          // 纯空白：同上
    public void Search_matches_a_brute_force_substring_scan_of_the_display_paths(string needle)
    {
        var batches = CatalogBatches();
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a"), Entry("b")], batches);
        store.EnsureDerived();

        var expected = ExpectedCatalogOrder(batches);
        // 复核口径必须与 SearchRanks 的契约一致：先 Trim，空/纯空白一律「没有结果」。
        // （不能直接拿原始串做 Contains —— `"".Contains` 恒真，会把每一行都算成命中。）
        var probe = needle.Trim();
        var brute = probe.Length == 0
            ? Array.Empty<int>()
            : expected
                .Select((x, rank) => (x, rank))
                .Where(t => DisplayPathOf(t.x.Bundle, t.x.Row).Contains(probe, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.rank)
                .ToArray();

        Assert.Equal(brute, store.SearchRanks(needle).ToArray());
    }

    [Fact]
    public void Search_verifies_fts_candidates_instead_of_trusting_them()
    {
        var batches = CatalogBatches();
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a"), Entry("b")], batches);
        store.EnsureDerived();

        // trigram 的 AND 只保证候选集是超集：bundle b 的 'assets/icon/on2/x.png'
        // 同时含 'ico'/'con'/'on2' 三个词项，却并没有连续的 'icon2' —— 复核要滤掉它。
        Assert.Single(store.SearchRanks("icon/on2"));          // 它本身确实在库里、搜得到
        Assert.Equal(4, store.SearchRanks("icon2").Count);     // 但没被算成 'icon2' 的命中
        // 反过来，真正存在的连续子串必须命中 —— 复核不能把对的滤掉。
        Assert.Single(store.SearchRanks("assets/icon"));
    }

    [Fact]
    public void PersistAll_invalidates_the_derived_layer_and_EnsureDerived_restores_it()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a")], [(Bundle("a"), new[] { Row(0) })]);
        Assert.True(store.NeedsDerived());
        Assert.False(store.IsDerivedReady());

        var built = store.EnsureDerived();
        Assert.True(built.Rebuilt);
        Assert.Equal(1, built.Rows);
        Assert.True(store.IsDerivedReady());
        Assert.False(store.NeedsDerived());

        // 第二次调用是廉价的空操作 —— 扫描流程每次都能放心调用。
        Assert.False(store.EnsureDerived().Rebuilt);

        // 只要没有真正写入，失效标记就不该出现。
        store.PersistAll([Entry("a")], []);
        Assert.True(store.IsDerivedReady());
        Assert.False(store.EnsureDerived().Rebuilt);

        // 写入了就必须再建一次。
        store.PersistAll([Entry("a")], [(Bundle("a"), new[] { Row(0), Row(1) })]);
        Assert.True(store.NeedsDerived());
        Assert.True(store.EnsureDerived().Rebuilt);
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void Queries_self_heal_a_derived_layer_that_was_never_built()
    {
        // 模拟「索引写完就被杀掉」：PersistAll 之后没有 EnsureDerived。
        // 此时名次表是空的，查询路径必须自己补建，而不是返回空页/零命中。
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a")], [(Bundle("a"), new[] { Row(0) })]);
        Assert.False(store.IsDerivedReady());

        Assert.Single(store.ReadPage(0, 10));
        Assert.Single(store.ReadByRanks([0]));

        store.PersistAll([Entry("a")], [(Bundle("a"), new[] { Row(0), Row(1) })]);
        // Row(0) 的容器路径是 assets/中文.json，Row(1) 没有容器条目 → 落到「未命名资源/…」。
        Assert.Single(store.SearchRanks("assets"));
        Assert.Single(store.SearchRanks("未命名"));

        // 补建之后状态是干净的。
        Assert.True(store.IsDerivedReady());
        Assert.False(store.NeedsDerived());
    }

    [Fact]
    public void Derived_tables_are_added_to_an_existing_v2_database_without_a_rescan()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a")], [(Bundle("a"), new[] { Row(0) })]);
        store.EnsureDerived();

        // 把派生层三张表删掉，模拟「老版本程序写下的 v2 库」——
        // 升级路径必须只补建它们，**不能**动 assets（SchemaVersion 不符 = 逼用户重扫 1471 个 bundle）。
        using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE catalog_rank; DROP TABLE asset_fts; DROP TABLE derived_state;";
            command.ExecuteNonQuery();
        }

        var upgraded = new UnityCacheSqliteIndexStore(Database);
        Assert.Equal(Row(0), Assert.Single(Assert.Single(upgraded.ReadAll()).Rows));  // 资产还在，没重扫
        Assert.True(upgraded.NeedsDerived());                                        // 缺派生层
        Assert.True(upgraded.EnsureDerived().Rebuilt);
        Assert.False(upgraded.NeedsDerived());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
