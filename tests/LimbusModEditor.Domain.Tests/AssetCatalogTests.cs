using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// <see cref="AssetCatalog"/> 的验收测试：**分页结果必须与旧
/// <see cref="AssetSearchService.Search(ModProject, AssetSearchQuery)"/> 逐行等价**。
///
/// <para>判据刻意是「拿同一份索引造出项目，两条路径当场各算一遍再逐字段比对」，
/// 而不是手写期望值 —— 手写期望值守不住这种等价：它只会把实现当时的想法抄一遍，
/// 而这些条件组合（类型映射回退、文本匹配面、容器条目、五种排序）里
/// 任何一处口径漂移，在用户那里都只表现为「少了一条 / 多了一条」。</para>
///
/// <para>数据集刻意造得小但维度齐：4 个 bundle × 6 行，其中
/// ① 三种「静态形态」的输入各占一份（bundle 名以 <c>static_s1_0_assets_all_</c> 开头、
/// 索引 <c>static_bundle</c> 标记为真、容器路径落在 static-data 前缀里）—— 资源列表
/// 不再按静态数据筛选之后（2026-09-15），它们必须与普通资源**一样可见**，所以这三个
/// 维度留在这里是**反误伤**用：任何「顺手把静态表藏起来」的改动都会让逐行等价当场红掉；
/// ② 类型覆盖 class-id 映射、ref-type 伪 id、以及**映射不到所以保留类型树结论**三种情形；
/// ③ 不同 bundle 的显示路径**故意重名**，用来压住「显示路径打平时靠 LogicalPath 兜底」那一段。</para>
/// </summary>
public sealed class AssetCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-catalog-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "index.db");

    /// <summary>索引根目录名里的一段，必然落在所有 <c>data_path</c> 的公共前缀里
    /// （用来压「命中 SourcePath 公共前后缀 ⇒ 每一行都命中」那条规则）。</summary>
    private const string RootMarker = "lme-catalog-";

    private sealed record RowSpec(
        string Container, long PathId, int TypeId, AssetType Type, long Size, string? Baseline, string? ContainerEntry);

    private static readonly RowSpec[] Template =
    [
        // 第一条刻意用**负的 path_id**：真实缓存的 path_id 是带符号的 64 位对象 id，
        // 负值很常见（这个值就是从真实库 r=0 那行抄来的）。它压住 LogicalPath 反解、
        // 名次定位、以及「lp 写进检索索引再复核」三处对负号的假设。
        new("CAB-1", -4060527305021521791, 28, AssetType.Unknown, 100, "基线", "Assets/Animation/SD/cg_01.png"),
        new("CAB-1", 2, 213, AssetType.Unknown, 2_000, null, "Assets/Animation/SD/cg_10.png"),
        new("CAB-1", 3, 49, AssetType.Text, 30, "基线", "Assets/Text/表/表-01.json"),
        new("CAB-2", 10, 999_999_999, AssetType.Unknown, 55_000, null, null),
        new("CAB-2", 11, 687078895, AssetType.Unknown, 7, null,
            "Assets/Resources_moved/StaticData/static-data/item/item-02.json"),
        new("CAB-2", 12, 1, AssetType.Unknown, 42, "基线", "Assets/Prefab/SD/Enemy/10201_xAppearance.prefab"),
    ];

    private static readonly (string Outer, string Inner, bool Static)[] Bundles =
    [
        ("outer1", "inner1", false),
        ("outer2", "inner2", false),
        ("outer3", "static_s1_0_assets_all_0123456789abcdef0123456789abcdef", false),
        ("outer4", "inner4", true),
    ];

    private static string DataPathOf(string root, string outer, string inner)
        => Path.Combine(root, outer, inner, "__data");

    private UnityCacheIndexBundle BundleOf(int index)
    {
        var (outer, inner, isStatic) = Bundles[index];
        return new(DataPathOf(_root, outer, inner), 4096 + index, 900 + index, outer, inner, isStatic);
    }

    private static IReadOnlyList<UnityCacheIndexRow> RowsOf()
        => [.. Template.Select((row, index) =>
            new UnityCacheIndexRow(index, row.Container, row.PathId, row.TypeId, row.Type, row.Size,
                row.Baseline, row.ContainerEntry))];

    private UnityCacheSqliteIndexStore NewStore()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll(
            [.. Bundles.Select(b => new UnityCacheScanEntry(b.Outer, b.Inner, DataPathOf(_root, b.Outer, b.Inner)))],
            [.. Enumerable.Range(0, Bundles.Length).Select(i => (BundleOf(i), RowsOf()))]);
        Assert.True(store.EnsureDerived().Rebuilt);
        return store;
    }

    private sealed record Harness(UnityCacheSqliteIndexStore Store, ModProject Project, AssetCatalog Catalog)
    {
        public AssetSearchService Search { get; } = new();
    }

    private async Task<Harness> NewHarnessAsync()
    {
        var store = NewStore();
        var project = new ModProject();
        await new UnityCacheScanService(Database).RehydrateFromIndexAsync(project);
        Assert.Equal(Bundles.Length * Template.Length, project.Assets.Count);
        return new(store, project, new AssetCatalog(store, project));
    }

    // ── 逐行等价 ────────────────────────────────────────────────────────

    /// <summary>每一种筛选 × 每一种排序都必须与旧搜索逐行一致。</summary>
    [Fact]
    public async Task Paged_queries_match_legacy_search_row_by_row()
    {
        var harness = await NewHarnessAsync();
        foreach (var query in AllQueries())
        {
            var expected = harness.Search.Search(harness.Project, query);
            var actual = harness.Catalog.Page(query, 0, int.MaxValue);
            Assert.Equal((long)expected.Count, actual.TotalCount);
            AssertSameRows(expected, actual.Items, Describe(query));
        }
    }

    /// <summary>分页不改变顺序：按任意页大小拼接必须等于整份结果。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    public async Task Pages_concatenate_to_the_full_result(int pageSize)
    {
        var harness = await NewHarnessAsync();
        foreach (var query in AllQueries())
        {
            var expected = harness.Search.Search(harness.Project, query);
            var collected = new List<AssetRecord>();
            for (long offset = 0; ; offset += pageSize)
            {
                var page = harness.Catalog.Page(query, offset, pageSize);
                Assert.Equal((long)expected.Count, page.TotalCount);
                if (page.Items.Count == 0) break;
                Assert.True(page.Items.Count <= pageSize, $"{Describe(query)}：一页超过了页大小");
                collected.AddRange(page.Items);
            }
            AssertSameRows(expected, collected, $"{Describe(query)} · 页大小 {pageSize}");
        }
    }

    /// <summary>越界与空页：不抛，且 TotalCount 仍是命中总数。</summary>
    [Fact]
    public async Task Offset_beyond_the_end_is_an_empty_page()
    {
        var harness = await NewHarnessAsync();
        var query = new AssetSearchQuery();
        var total = harness.Catalog.Count(query);
        Assert.True(total > 0);
        Assert.Equal(total, harness.Catalog.Page(query, 0, int.MaxValue).TotalCount);
        Assert.Empty(harness.Catalog.Page(query, total, 10).Items);
        Assert.Equal(total, harness.Catalog.Page(query, total, 10).TotalCount);
        Assert.Empty(harness.Catalog.Page(query, 0, 0).Items);
        Assert.Equal(total, harness.Catalog.Page(query, 0, 0).TotalCount);
    }

    /// <summary>
    /// 索引本身已经装得下全部描述性事实：项目态来源为空时，除身份
    /// （<c>AssetId</c> 每次回灌都变）之外的字段必须与回灌出来的记录逐字段一致。
    /// </summary>
    [Fact]
    public async Task Index_alone_reproduces_every_field_except_identity()
    {
        var harness = await NewHarnessAsync();
        var fromIndex = new AssetCatalog(harness.Store, EmptyAssetStateSource.Instance)
            .Page(new AssetSearchQuery(), 0, int.MaxValue);
        var rehydrated = harness.Search.Search(harness.Project, new AssetSearchQuery());
        Assert.Equal((long)rehydrated.Count, fromIndex.TotalCount);
        for (var i = 0; i < rehydrated.Count; i++)
            AssertSameFieldsExceptIdentity(rehydrated[i], fromIndex.Items[i]);
    }

    /// <summary>项目态（编辑状态 / 替换文件 / 已实体化）参与判据与排序，两条路径同样一致。</summary>
    [Fact]
    public async Task Project_state_takes_part_in_matching_and_sorting()
    {
        var harness = await NewHarnessAsync();
        var replacement = Path.Combine(_root, "replacement.dds");
        File.WriteAllText(replacement, "x");
        var assets = harness.Project.Assets;
        assets[0].EditState = AssetEditState.Modified;
        assets[0].Metadata["replacementPath"] = replacement;
        assets[0].ModifiedHash = "hash-0";
        assets[3].EditState = AssetEditState.Added;
        assets[6].EditState = AssetEditState.Conflict;
        // 已实体化：摘掉 reference 标记 + SourcePath 指向本地编辑副本
        // （与 UnityCacheMaterializationService 同形）。
        assets[9].Metadata.Remove("reference");
        assets[9].SourcePath = Path.Combine(_root, "materialized", "inner1", "__data");
        assets[9].EditState = AssetEditState.Modified;

        foreach (var query in AllQueries().Where(q => q.HasReplacement is not null || q.State is not null
                     || q.Type is null && q.Text is null))
        {
            var expected = harness.Search.Search(harness.Project, query);
            var actual = harness.Catalog.Page(query, 0, int.MaxValue);
            Assert.Equal((long)expected.Count, actual.TotalCount);
            AssertSameRows(expected, actual.Items, $"（带项目态）{Describe(query)}");
        }

        // 身份保鲜：项目态改过的那条，从页里拿回来仍是同一个 AssetId 与同一份本地字段。
        var located = Assert.Single(harness.Catalog.Resolve([assets[0].LogicalPath]));
        Assert.Equal(assets[0].AssetId, located.AssetId);
        Assert.Equal(assets[0].SourcePath, located.SourcePath);
        Assert.Equal(AssetEditState.Modified, located.EditState);
        Assert.Equal(replacement, located.Metadata["replacementPath"]);

        // 已实体化的那条同样：SourcePath 取项目的、且 reference 标记已摘。
        var materialized = Assert.Single(harness.Catalog.Resolve([assets[9].LogicalPath]));
        Assert.Equal(assets[9].SourcePath, materialized.SourcePath);
        Assert.False(materialized.Metadata.ContainsKey("reference"));
    }

    /// <summary>
    /// **只存在于项目里的资源**必须与索引命中一起分页、排序、定位。
    ///
    /// <para>它们不在名次表里（扫描索引只覆盖缓存引用），而项目会自己长出资源：音频工作台
    /// 「提取样本到项目」、<c>ModImportService</c> 导入的旧式模组（Carra / Lunartique / Rebank）
    /// 都是往 <see cref="ModProject.Assets"/> 里加一条索引里没有的记录。列表改走索引之后，
    /// **这些资源不会因为「索引里没有」就消失** —— 本用例就是钉住这一点的：任何「只查索引」
    /// 的写法都会让这份逐行等价当场红掉。</para>
    /// </summary>
    [Fact]
    public async Task Project_only_assets_take_part_in_paging_sorting_and_locating()
    {
        var harness = await NewHarnessAsync();
        harness.Project.Assets.Add(new AssetRecord
        {
            LogicalPath = "bank/bgm/foo.wav",
            ContainerPath = "bank/bgm/foo.wav",
            Type = AssetType.Audio,
            Size = 1_234,
            Metadata = { ["bankSource"] = "foo.bank", ["sampleName"] = "foo" },
        });
        harness.Project.Assets.Add(new AssetRecord
        {
            LogicalPath = "导入的旧式资源/abc.png",
            Type = AssetType.Texture,
            Size = 42,
        });
        // 目录门面按项目构建（与资源页一样：资产条数变过就重建）。
        var catalog = new AssetCatalog(harness.Store, harness.Project);

        foreach (var query in AllQueries())
        {
            var expected = harness.Search.Search(harness.Project, query);
            var actual = catalog.Page(query, 0, int.MaxValue);
            Assert.True((long)expected.Count == actual.TotalCount,
                $"命中数不一致（含补充集）：{expected.Count} vs {actual.TotalCount}｜{Describe(query)}");
            AssertSameRows(expected, actual.Items, $"（含项目态补充集）{Describe(query)}");
        }

        // 补充集同样要能「翻到它所在的那一页并选中」：下标 → 页 → 取回同一条。
        var unfiltered = new AssetSearchQuery();
        foreach (var path in new[] { "bank/bgm/foo.wav", "导入的旧式资源/abc.png" })
        {
            var index = catalog.IndexIn(unfiltered, path);
            Assert.True(index is not null, $"补充集里的「{path}」在无筛选视图里应当有下标");
            Assert.Equal(path, Assert.Single(catalog.Page(unfiltered, index!.Value, 1).Items).LogicalPath);
            var resolved = Assert.Single(catalog.Resolve([path]));
            Assert.Equal(path, resolved.LogicalPath);
        }

        // 上一行与下一行也必须是**混排后**的邻居，而不是「补充集全被甩到末尾」。
        var ordered = catalog.Page(unfiltered, 0, int.MaxValue).Items;
        var positions = ordered.Select((x, i) => (x.LogicalPath, i)).ToDictionary(x => x.LogicalPath, x => x.i);
        Assert.True(positions["bank/bgm/foo.wav"] < ordered.Count - 1,
            "补充集资源不该总是排在最后一行 —— 那说明它没有参与同一份排序");
    }

    // ── 定位与解析 ──────────────────────────────────────────────────────

    [Fact]
    public async Task Locate_finds_the_same_row_that_paging_returns()
    {
        var harness = await NewHarnessAsync();
        var unfiltered = new AssetSearchQuery();
        foreach (var asset in harness.Project.Assets)
        {
            var location = harness.Catalog.Locate(asset.LogicalPath);
            Assert.NotNull(location);
            // 无筛选视图里「全局名次」就是「命中集下标」。
            var index = harness.Catalog.IndexIn(unfiltered, asset.LogicalPath);
            Assert.Equal((long?)location!.Rank, index);
            var page = harness.Catalog.Page(unfiltered, index!.Value, 1);
            Assert.Equal(asset.LogicalPath, Assert.Single(page.Items).LogicalPath);
        }

        // 有筛选的视图里，看不到的记录没有下标 —— 但全局名次仍然拿得到。
        // 原本用「默认视图隐藏静态数据表」演示这一点，那条筛选已移除（2026-09-15）；
        // 改用资源页默认的「仅容器内」这条稳定维度（支撑对象这个维度不会随产品决定消失）。
        var containerOnly = new AssetSearchQuery { HasContainerEntry = true };
        var support = harness.Project.Assets.First(x => x.UnityPathId == 10);
        Assert.NotNull(harness.Catalog.Locate(support.LogicalPath));
        Assert.Null(harness.Catalog.IndexIn(containerOnly, support.LogicalPath));
        Assert.Equal((long?)null, harness.Catalog.IndexIn(unfiltered, "nope"));

        Assert.Null(harness.Catalog.Locate("导入的旧式资源/名字.id"));
        Assert.Null(harness.Catalog.Locate("outer1/inner1/只有三段.28"));
        Assert.Null(harness.Catalog.Locate("outer1/inner1/CAB-1/不是数字.28"));
        Assert.Null(harness.Catalog.Locate(string.Empty));
    }

    [Fact]
    public async Task Resolve_keeps_input_order_and_skips_unknown_paths()
    {
        var harness = await NewHarnessAsync();
        var assets = harness.Project.Assets;
        var resolved = harness.Catalog.Resolve(
            [assets[5].LogicalPath, "nope", assets[1].LogicalPath, assets[5].LogicalPath]);
        Assert.Equal(
            new[] { assets[5].LogicalPath, assets[1].LogicalPath, assets[5].LogicalPath },
            resolved.Select(x => x.LogicalPath).ToArray());
        Assert.Equal(assets[5].AssetId, resolved[0].AssetId);
        Assert.Same(resolved[0], resolved[2]);
        Assert.Empty(harness.Catalog.Resolve([]));
    }

    // ── 下推用的反向映射 ────────────────────────────────────────────────

    /// <summary>反向映射必须与正向映射自洽，否则「按类型筛选」下推会多筛或漏筛。</summary>
    [Fact]
    public void ClassIdsOf_is_consistent_with_Map()
    {
        // 正向 → 反向：每个已知 class id 都要能被它映射到的那个类型找回来。
        foreach (var typeId in UnityClassId.KnownTypeIds)
        {
            var type = UnityClassId.Map(typeId);
            Assert.NotEqual(AssetType.Unknown, type);
            Assert.Contains(typeId, UnityClassId.ClassIdsOf(type));
        }
        // 反向 → 正向：反向表里的 id 必须都映射回同一个类型。
        foreach (var type in Enum.GetValues<AssetType>())
            foreach (var typeId in UnityClassId.ClassIdsOf(type))
                Assert.Equal(type, UnityClassId.Map(typeId));

        // Unknown 是「映射不到时的落点」，不能有任何 class id 指向它 ——
        // 否则按 Unknown 筛选会把别的类型一起捞出来。
        Assert.Empty(UnityClassId.ClassIdsOf(AssetType.Unknown));

        // 少数类型本来就没有 class id（Json：真实缓存 0 行；ScriptableObject：Unity 把
        // ScriptableObject 序列化成 MonoBehaviour 114）。空反向表是**正确**的：
        // SQL 会退化成 `assets.type = $t`，而那一列正是这类类型唯一可能的出处。
        Assert.Empty(UnityClassId.ClassIdsOf(AssetType.Json));
        Assert.Empty(UnityClassId.ClassIdsOf(AssetType.ScriptableObject));
        Assert.NotEmpty(UnityClassId.ClassIdsOf(AssetType.Texture));
        Assert.NotEmpty(UnityClassId.ClassIdsOf(AssetType.SpriteAtlas));
    }

    // ── 查询集合与断言 ──────────────────────────────────────────────────

    private static IEnumerable<AssetSearchQuery> AllQueries()
    {
        var filters = new AssetSearchQuery[]
        {
            new(),                                              // 默认视图
            new() { HasContainerEntry = true },                 // 资源页默认勾选的那个
            new() { HasContainerEntry = false },
            new() { Text = "cg_0" },                            // 命中显示路径
            new() { Text = "CG_10" },                           // 大小写不敏感
            new() { Text = "表" },                              // 1 字符 → 词项取不到，退化扫描
            new() { Text = "表-01" },                           // 非 ASCII 进 trigram
            new() { Text = "CAB-1" },                           // 只命中 LogicalPath
            new() { Text = "Assets/Text" },                     // 带 '/' 的显示路径
            new() { Text = ".json" },
            new() { Text = "outer4\\inner4" },                  // 含反斜杠 → 退化扫描
            new() { Text = RootMarker },                        // 命中 SourcePath 的公共前缀
            new() { Text = "2" },                               // 极短，几乎命中所有路径
            new() { Type = AssetType.Texture },
            new() { Type = AssetType.Sprite },
            new() { Type = AssetType.Text },
            new() { Type = AssetType.SpriteAtlas },             // 走 class id 反向映射
            new() { Type = AssetType.GameObject },
            new() { Type = AssetType.Unknown },                 // 映射不到、保留类型树结论
            new() { Type = AssetType.Component },               // 类型表里有、数据里没有
            new() { Container = "CAB-2" },
            new() { Container = "cab-1" },                      // 大小写不敏感
            new() { UnityPathId = 10 },
            new() { UnityTypeId = 49 },
            new() { MinSize = 40, MaxSize = 2_000 },
            new() { MinSize = 55_000 },
            new() { MaxSize = 7 },
            new() { HasReplacement = true },
            new() { HasReplacement = false },
            new() { State = AssetEditState.Unchanged },
            new() { Type = AssetType.Sprite, MinSize = 1_000 },
            new() { Text = "cg", Type = AssetType.Sprite },
            new() { Text = "inner1", Container = "CAB-1", HasContainerEntry = true },
        };
        var sorts = new[]
        {
            AssetSortKind.Name, AssetSortKind.SizeDescending, AssetSortKind.SizeAscending,
            AssetSortKind.Type, AssetSortKind.ModifiedFirst,
        };
        foreach (var filter in filters)
        foreach (var sort in sorts)
            yield return filter with { Sort = sort };
    }

    private static string Describe(AssetSearchQuery query)
        => $"文本「{query.Text ?? "-"}」· 类型 {query.Type?.ToString() ?? "-"} · 容器「{query.Container ?? "-"}」" +
           $" · 路径 {query.UnityPathId?.ToString() ?? "-"} · 类 {query.UnityTypeId?.ToString() ?? "-"}" +
           $" · 大小 [{query.MinSize?.ToString() ?? "-"}..{query.MaxSize?.ToString() ?? "-"}]" +
           $" · 有替换 {query.HasReplacement?.ToString() ?? "-"} · 有条目 {query.HasContainerEntry?.ToString() ?? "-"}" +
           $" · 状态 {query.State?.ToString() ?? "-"} · 排序 {query.Sort}";

    private static void AssertSameRows(
        IReadOnlyList<AssetRecord> expected, IReadOnlyList<AssetRecord> actual, string because)
    {
        Assert.True(expected.Count == actual.Count,
            $"命中数不一致：{expected.Count} vs {actual.Count}｜{because}");
        Assert.Equal(expected.Select(x => x.LogicalPath).ToArray(), actual.Select(x => x.LogicalPath).ToArray());
        for (var i = 0; i < expected.Count; i++)
        {
            var label = $"{because}｜第 {i} 行｜{expected[i].LogicalPath}";
            Assert.True(expected[i].AssetId == actual[i].AssetId, $"{label}：AssetId 不一致（身份没保住）");
            AssertSameFieldsExceptIdentity(expected[i], actual[i], label);
        }
    }

    private static void AssertSameFieldsExceptIdentity(AssetRecord expected, AssetRecord actual, string? label = null)
    {
        var differences = new List<string>();
        void Same(string name, object? left, object? right)
        {
            if (!Equals(left, right)) differences.Add($"{name}：「{left}」≠「{right}」");
        }

        Same(nameof(AssetRecord.LogicalPath), expected.LogicalPath, actual.LogicalPath);
        Same(nameof(AssetRecord.SourcePath), expected.SourcePath, actual.SourcePath);
        Same(nameof(AssetRecord.ContainerPath), expected.ContainerPath, actual.ContainerPath);
        Same(nameof(AssetRecord.Account), expected.Account, actual.Account);
        Same(nameof(AssetRecord.Bundle), expected.Bundle, actual.Bundle);
        Same(nameof(AssetRecord.UnityPathId), expected.UnityPathId, actual.UnityPathId);
        Same(nameof(AssetRecord.UnityTypeId), expected.UnityTypeId, actual.UnityTypeId);
        Same(nameof(AssetRecord.Type), expected.Type, actual.Type);
        Same(nameof(AssetRecord.Size), expected.Size, actual.Size);
        Same(nameof(AssetRecord.EditState), expected.EditState, actual.EditState);
        Same(nameof(AssetRecord.OriginalHash), expected.OriginalHash, actual.OriginalHash);
        Same(nameof(AssetRecord.ModifiedHash), expected.ModifiedHash, actual.ModifiedHash);
        Same(nameof(AssetRecord.CatalogBaseline), expected.CatalogBaseline, actual.CatalogBaseline);
        Same("Metadata 键集",
            string.Join('|', expected.Metadata.Keys.OrderBy(k => k, StringComparer.Ordinal)),
            string.Join('|', actual.Metadata.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        foreach (var key in expected.Metadata.Keys)
            Same($"Metadata[{key}]",
                expected.Metadata[key],
                actual.Metadata.TryGetValue(key, out var value) ? value : null);
        Assert.True(differences.Count == 0, $"{label}｜" + string.Join("；", differences));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // SQLite 的 WAL 文件可能还被最后一个连接短暂占着；临时目录留着不影响其它用例。
        }
    }
}
