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
    //   · 检索   → 对 dp ∪ lp ∪ src 做 OrdinalIgnoreCase 子串暴力扫描
    // 这样一旦派生层与「内存里那条路径」分家（本次重构最大的风险），测试立刻红。
    //
    // 检索的匹配面 = 旧 AssetSearchService.MatchesText：
    //   DisplayPath ∪ LogicalPath ∪ SourcePath
    // 默认夹具（Bundle(name)）的 data_path 就是 bundle 名，只有 1 个字符，天然覆盖不到
    // SourcePath；真正测 SourcePath 要用 SourceBundle，它给出接近真实的
    // `<缓存根>\<名字>\__data`，于是公共前后缀 = `<缓存根>\` 与 `\__data`。

    private static UnityCacheIndexRow CatalogRow(int index, string? containerEntry, long pathId,
        int typeId = 49, AssetType type = AssetType.Text)
        => new(index, "共享容器", pathId, typeId, type, 9876543210, null, containerEntry);

    /// <summary>data_path 取接近真实的形态 —— 否则造不出 SourcePath 的公共前后缀，
    /// 检索的常量规则（命中它 = 所有行）就没法被覆盖。</summary>
    private static UnityCacheIndexBundle SourceBundle(string name)
        => new($@"C:\缓存根\{name}\__data", 123, 456, "outer", name, true);

    private static UnityCacheScanEntry SourceEntry(string name)
        => new("outer", name, $@"C:\缓存根\{name}\__data");

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

    /// <summary>检索的复核口径（= 旧 <c>AssetSearchService.MatchesText</c>）：
    /// 显示路径 ∪ LogicalPath ∪ SourcePath，大小写不敏感。
    /// <para>lp 的拼法**故意手写**，而不是调 <c>AssetDisplay.CacheRowLogicalPath</c> ——
    /// 这样它同时是对派生层 lp 格式的一层独立校验：派生层若把 lp 拼成别的样子，
    /// 检索结果立刻与这个暴力扫描对不上。</para></summary>
    private static bool MatchesAnyText(UnityCacheIndexBundle bundle, UnityCacheIndexRow row, string needle)
        => DisplayPathOf(bundle, row).Contains(needle, StringComparison.OrdinalIgnoreCase)
        || LogicalPathOf(bundle, row).Contains(needle, StringComparison.OrdinalIgnoreCase)
        || bundle.DataPath.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>三字段子串暴力扫描 → 名次（检索的独立判据，恒升序）。</summary>
    private static int[] BruteForce(
        (UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow> Rows)[] batches, string needle)
    {
        var probe = needle.Trim();
        if (probe.Length == 0) return [];
        return ExpectedCatalogOrder(batches)
            .Select((x, rank) => (x, rank))
            .Where(t => MatchesAnyText(t.x.Bundle, t.x.Row, probe))
            .Select(t => t.rank)
            .ToArray();
    }

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
    [InlineData("共享容器")]      // 只出现在 lp 里（容器名）—— d1 只索引 dp 时会漏
    [InlineData("i")]            // < 3 字符：trigram 取不到词项 → 退化全表扫描
    [InlineData("n2")]           // 同上
    [InlineData("a")]            // < 3 字符且只命中 src（data_path == bundle 名）→ 全表扫描 + 三字段复核
    [InlineData("_")]            // LIKE 的通配符：这里必须是字面量，且谁都不该命中
    [InlineData("")]             // 空串：不该扫全表
    [InlineData("   ")]          // 纯空白：同上
    public void Search_matches_a_brute_force_substring_scan_of_dp_lp_and_src(string needle)
    {
        var batches = CatalogBatches();
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a"), Entry("b")], batches);
        store.EnsureDerived();

        // 判据先 Trim、空/纯空白一律「没有结果」—— 与 SearchRanks 的契约一致。
        // （不能直接拿原始串做 Contains —— `"".Contains` 恒真，会把每一行都算成命中。）
        Assert.Equal(BruteForce(batches, needle), store.SearchRanks(needle).ToArray());
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
    public void Search_covers_logical_path_and_source_path_like_the_old_matcher()
    {
        var batches = new (UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow> Rows)[]
        {
            (SourceBundle("a"), new[] { CatalogRow(0, "assets/anim/icon2.png", 700) }),
            (SourceBundle("b"), new[] { CatalogRow(0, "assets/anim/icon3.png", 800) }),
        };
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([SourceEntry("a"), SourceEntry("b")], batches);
        store.EnsureDerived();
        var expected = ExpectedCatalogOrder(batches);

        // ① 只有 lp 会命中：容器名是 lp 的一段，任何一个显示路径里都没有它。
        //    先钉住「dp 确实搜不到」这一点，否则这条测试可能只是碰巧通过。
        Assert.DoesNotContain(expected, x =>
            DisplayPathOf(x.Bundle, x.Row).Contains("共享容器", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, store.SearchRanks("共享容器").Count);
        Assert.Equal(BruteForce(batches, "共享容器"), store.SearchRanks("共享容器").ToArray());

        // ② pathId 同样只出现在 lp 里（dp 用的是容器条目名）。
        Assert.Equal(BruteForce(batches, "800"), store.SearchRanks("800").ToArray());
        Assert.Single(store.SearchRanks("800"));

        // ③ src 的公共前后缀「每一行都相同」⇒ 命中它就等于没在过滤。
        //    旧实现在这种情况下同样返回全部（SourcePath 人人含它），所以派生层返回全部名次。
        Assert.Equal(2, store.SearchRanks("缓存根").Count);
        Assert.Equal(2, store.SearchRanks("__data").Count);
        Assert.Equal(BruteForce(batches, "缓存根"), store.SearchRanks("缓存根").ToArray());
        Assert.Equal(BruteForce(batches, "__data"), store.SearchRanks("__data").ToArray());

        // ④ 含反斜杠 → 可能跨过 outer/inner 的分隔符只在 src 里命中：
        //    FTS 给不出候选，必须退化全表扫描，而结果仍要与暴力扫描逐条相等。
        Assert.Equal(BruteForce(batches, @"根\a\"), store.SearchRanks(@"根\a\").ToArray());
        Assert.Single(store.SearchRanks(@"根\a\"));
    }

    [Fact]
    public void An_old_search_index_shape_is_dropped_and_rebuilt_without_touching_assets()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a")], [(Bundle("a"), new[] { Row(0) })]);
        store.EnsureDerived();

        // 把 asset_fts 换回 d1 的形状（只有 dp 一列）、状态标成 d1 —— 模拟升级前用户手上的库。
        // `CREATE VIRTUAL TABLE IF NOT EXISTS` 对已存在的表什么都不做，所以这必须由
        // 形状自愈兜住，否则写入端按 (rowid,dp,lp) 插会直接报「no column named lp」。
        using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE asset_fts;
                CREATE VIRTUAL TABLE asset_fts USING fts5(dp, content='', detail=none, columnsize=0, tokenize='trigram');
                DELETE FROM derived_state;
                INSERT INTO derived_state(k,v) VALUES('revision','d1'),('rows','1');
                """;
            command.ExecuteNonQuery();
        }

        var upgraded = new UnityCacheSqliteIndexStore(Database);
        Assert.True(upgraded.NeedsDerived());
        Assert.True(upgraded.EnsureDerived().Rebuilt);

        // 资产没被重扫：SchemaVersion 仍是 2，assets 原样在位。
        Assert.Equal(Row(0), Assert.Single(Assert.Single(upgraded.ReadAll()).Rows));
        Assert.False(upgraded.NeedsDerived());

        // 检索索引确实换成了两列的形状，而且 lp 立刻可搜（d1 会返回空）。
        using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info('asset_fts') ORDER BY cid";
            var columns = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(0));
            Assert.Equal(new[] { "dp", "lp" }, columns);
        }
        Assert.Single(upgraded.SearchRanks("共享容器"));
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

    /// <summary>
    /// <c>path_id</c> 是 Unity 的对象 id（内部是**带符号**的 64 位哈希），真实缓存里有大量负值。
    /// 解析 LogicalPath 尾段时曾经用 <c>NumberStyles.None</c> —— 它拒绝前导负号，于是这些资源的
    /// <see cref="UnityCacheSqliteIndexStore.FindRank"/> 永远返回 -1。
    /// <para>症状极隐蔽：列表里明明看得到那条资源，「定位到它 / 精确跳转」却静默失效，
    /// 日志只有一句「不在本代际的结果中」。真实数据实测踩到（探针里对页码 0 的第一条
    /// 资源调 <c>Locate</c> 直接拿到 null，而它的 path_id 正是
    /// <c>-4060527305021521791</c>）。</para>
    /// </summary>
    [Fact]
    public void FindRank_accepts_the_negative_path_ids_that_real_caches_contain()
    {
        const long realNegativePathId = -4060527305021521791L;
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll([Entry("a")], [(Bundle("a"), new[]
        {
            Row(0) with { PathId = realNegativePathId },
            Row(1) with { PathId = 42L }
        })]);
        Assert.True(store.EnsureDerived().Rebuilt);

        var negative = store.FindRank(AssetDisplay.CacheRowLogicalPath("outer", "a", "共享容器", realNegativePathId, 49));
        var positive = store.FindRank(AssetDisplay.CacheRowLogicalPath("outer", "a", "共享容器", 42L, 49));
        Assert.True(negative >= 0, "负的 path_id 必须能定位（NumberStyles.None 会把它判成非法）。");
        Assert.True(positive >= 0);
        Assert.NotEqual(negative, positive);

        // 仍然拒绝真的不像索引路径的东西 —— 放开符号位不等于变成模糊匹配。
        Assert.Equal(-1, store.FindRank("outer/a/共享容器/abc.49"));
        Assert.Equal(-1, store.FindRank("outer/a/共享容器/- 4.49"));
        Assert.Equal(-1, store.FindRank("outer/a/共享容器/4.49.0"));
        Assert.Equal(-1, store.FindRank("outer/a/共享容器/4"));
        Assert.Equal(-1, store.FindRank("outer/a/b/c/共享容器/4.49"));
        Assert.Equal(-1, store.FindRank(""));
        Assert.Equal(-1, store.FindRank(null!));
    }

    /// <summary>
    /// 候选集的两个不变量：**名次升序**、以及**反查索引存在**。
    /// <para>① 升序：SQL 里刻意不写 <c>ORDER BY k.r</c>（一写 planner 就改成全扫名次表，
    /// 实测 3.9–6.0 s vs 168 ms），所以升序必须由 <see cref="UnityCacheSqliteIndexStore.ReadCandidateRanks"/>
    /// 自己补 —— 调用方（分页/目录树）按「名次升序 = 目录全序」使用结果，返回乱序会静默给出乱序的页。</para>
    /// <para>② 反查索引：没有它「assets 驱动 → 反查明次」无索引可用，筛选下推整个失效。
    /// 它由建表脚本与派生层重建两处 <c>CREATE INDEX IF NOT EXISTS</c> 共同保证，
    /// 而重建时会先 DROP 再整批建（逐行维护 127 万行比整批建贵得多）。</para>
    /// </summary>
    [Fact]
    public void Candidate_ranks_are_ascending_and_the_reverse_lookup_index_is_in_place()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll(
            [.. Enumerable.Range(0, 4).Select(i => Entry($"b{i}"))],
            [.. Enumerable.Range(0, 4).Select(i =>
                (Bundle($"b{i}"), (IReadOnlyList<UnityCacheIndexRow>)new[] { Row(0), Row(1) }))]);
        Assert.True(store.EnsureDerived().Rebuilt);

        // 只有一个容器条目的那批（每个 bundle 的 Row(0)）。
        var withEntry = store.ReadCandidateRanks(new UnityCacheIndexFilter(HasContainerEntry: true));
        Assert.Equal(4, withEntry.Count);
        Assert.Equal(withEntry.OrderBy(x => x), withEntry);

        // 无任何条件：连 assets 都不碰，直接扫名次表。
        var all = store.ReadCandidateRanks(new UnityCacheIndexFilter());
        Assert.Equal(8, all.Count);
        Assert.Equal(all.OrderBy(x => x), all);

        // 重建之后索引仍在（WriteDerivedLayer 是先 DROP 再建）。
        Assert.True(store.EnsureDerived().Rebuilt || store.IsDerivedReady());
        using var connection = new SqliteConnection($"Data Source={Database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='ix_catalog_rank_bundle'";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
