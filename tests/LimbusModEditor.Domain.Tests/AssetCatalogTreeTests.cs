using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// <see cref="AssetCatalogTree"/> 的验收测试：**目录树换了一条取数路径之后，还必须长成同一棵树**。
///
/// <para>判据与 <see cref="AssetCatalogTests"/> 同源、同理由：拿同一份索引造出项目，
/// 两条路径（新 = 按名次区间分层；旧 = <see cref="AssetTreeBuilder"/> 直接吃全部记录）
/// 当场各建一棵树、逐节点比对。手写期望值守不住这种等价 —— 它只会把实现当时的想法抄一遍，
/// 而分组的任何一处口径漂移（同层顺序、叶子判定、区间边界、重名消歧）在用户那里
/// 都只表现为「树里的文件少了/位置变了」。</para>
///
/// <para>数据集刻意铺满树的形状维度（4 个 bundle × 8 行）：三层嵌套、同一目录下**重名叶子**
/// （必须走消歧后缀）、**没有容器条目**的资源（整片落进「未命名资源」）、根级单段路径、
/// 以及「一条路径同时是叶子又是目录前缀」（<c>Assets/Text/表-01.json</c> 与
/// <c>Assets/Text/表/…</c>）—— 最后一条压的是「叶子排在同名目录组之前、且不会把目录组切断」
/// 这个前提。</para>
/// </summary>
public sealed class AssetCatalogTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-tree-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "index.db");

    private sealed record RowSpec(string Container, long PathId, int TypeId, AssetType Type, long Size, string? ContainerEntry);

    private static readonly RowSpec[] Template =
    [
        new("CAB-1", 1, 28, AssetType.Unknown, 100, "Assets/Animation/SD/cg_01.png"),
        new("CAB-1", 2, 28, AssetType.Unknown, 200, "Assets/Animation/SD/cg_10.png"),
        // 与上一行同容器条目、不同对象：同层重名叶子 → 必须走「类型 #编号 (bundle)」消歧
        new("CAB-1", 3, 213, AssetType.Unknown, 201, "Assets/Animation/SD/cg_10.png"),
        new("CAB-1", 4, 28, AssetType.Unknown, 300, "Assets/Animation/SD/Sub/deep.png"),
        // 叶子与目录同名前缀：Assets/Text/表-01.json 会排在 Assets/Text/表/… 之前
        new("CAB-1", 5, 49, AssetType.Text, 400, "Assets/Text/表/表-01.json"),
        new("CAB-1", 6, 49, AssetType.Text, 401, "Assets/Text/表-01.json"),
        // 没有容器条目 → 整片落在「未命名资源」下
        new("CAB-2", 7, 999_999_999, AssetType.Unknown, 500, null),
        // 单段路径 → 根级叶子
        new("CAB-2", 8, 28, AssetType.Unknown, 600, "RootFile.png"),
    ];

    private static readonly (string Outer, string Inner)[] Bundles =
    [
        ("outer1", "inner1"), ("outer2", "inner2"), ("outer3", "inner3"), ("outer4", "inner4"),
    ];

    private static string DataPathOf(string root, string outer, string inner)
        => Path.Combine(root, outer, inner, "__data");

    private UnityCacheSqliteIndexStore NewStore()
    {
        var store = new UnityCacheSqliteIndexStore(Database);
        store.PersistAll(
            [.. Bundles.Select(b => new UnityCacheScanEntry(b.Outer, b.Inner, DataPathOf(_root, b.Outer, b.Inner)))],
            [.. Enumerable.Range(0, Bundles.Length).Select(i =>
            {
                var (outer, inner) = Bundles[i];
                IReadOnlyList<UnityCacheIndexRow> rows = [.. Template.Select((row, index) =>
                    new UnityCacheIndexRow(index, row.Container, row.PathId, row.TypeId, row.Type, row.Size, null, row.ContainerEntry))];
                return (new UnityCacheIndexBundle(DataPathOf(_root, outer, inner), 4096 + i, 900 + i, outer, inner), rows);
            })]);
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

    /// <summary>
    /// **核心用例**：每一种筛选下，catalog 树必须与「直接吃全部记录」的旧树逐节点一致。
    /// <para>对照面的输入顺序取自 <see cref="AssetSearchService.Search"/>（按名称排序的完整结果），
    /// 也就是产品语义里的排列；catalog 树内部走的是目录名次序。两种序若不同，这里的
    /// 叶子次序断言会立刻红掉 —— 这正是要锁住的东西。</para>
    /// </summary>
    [Fact]
    public async Task Catalog_tree_matches_the_record_tree_node_by_node()
    {
        var harness = await NewHarnessAsync();
        var queries = new AssetSearchQuery[]
        {
            new(Sort: AssetSortKind.Name),                                  // 默认视图
            new(HasContainerEntry: true, Sort: AssetSortKind.Name),         // 资源页默认勾选的那个
            new(HasContainerEntry: false, Sort: AssetSortKind.Name),        // 只剩「未命名资源」那片
            new(Text: "cg", Sort: AssetSortKind.Name),                      // 命中显示路径
            new(Text: "表", Sort: AssetSortKind.Name),                      // 1 字符 → 词项取不到，退化扫描
            new(Text: "Assets/Animation", Sort: AssetSortKind.Name),        // 带 '/' 的显示路径
            new(Text: "inner2", Sort: AssetSortKind.Name),                  // 只命中 LogicalPath
            new(Container: "CAB-1", Sort: AssetSortKind.Name),              // 只留一个容器
            new(Type: AssetType.Text, Sort: AssetSortKind.Name),
        };
        foreach (var query in queries)
        {
            // 参照面：旧树直接吃「按名称排序的完整搜索结果」。
            var reference = AssetTreeBuilder.BuildRoots(harness.Search.Search(harness.Project, query));
            var actual = new AssetCatalogTree(harness.Catalog, query).Roots();
            AssertSameTree(reference, actual, Describe(query));
        }
    }

    /// <summary>命中总数与根层区间必须自洽：各根节点区间之和 = 命中总数（区间是划分，不是近似）。</summary>
    [Fact]
    public async Task Root_ranges_partition_the_match_set()
    {
        var harness = await NewHarnessAsync();
        var query = new AssetSearchQuery(Sort: AssetSortKind.Name);
        var tree = new AssetCatalogTree(harness.Catalog, query);
        var roots = tree.Roots();
        Assert.True(tree.Count > 0);
        Assert.Equal(tree.Count, roots.Sum(x => x.Count));
        Assert.Equal(0, roots.Min(x => x.Start));
        Assert.Equal(tree.Count, roots.Max(x => x.End));
        // 区间不重叠、按起点递增：这是「分组靠连续区间」这条设计前提的可验证形态。
        var ordered = roots.OrderBy(x => x.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i - 1].End <= ordered[i].Start,
                $"区间重叠：{ordered[i - 1].Name}[{ordered[i - 1].Start},{ordered[i - 1].End}) 与 {ordered[i].Name}");
    }

    /// <summary>
    /// 只存在于项目里的资源（导入的旧式模组 / 音频工作台「提取到项目」）也必须出现在树里 ——
    /// 它们不在索引名次表里，只能靠同一份排序键插进正确位置。
    /// </summary>
    [Fact]
    public async Task Project_only_assets_sit_in_the_tree_at_their_sorted_place()
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
        var catalog = new AssetCatalog(harness.Store, harness.Project);
        var query = new AssetSearchQuery(Sort: AssetSortKind.Name);

        var reference = AssetTreeBuilder.BuildRoots(harness.Search.Search(harness.Project, query));
        var actual = new AssetCatalogTree(catalog, query).Roots();
        AssertSameTree(reference, actual, "（含项目态补充集）");

        // 「bank」这个根节点只可能来自补充集 —— 索引里根本没有它。
        var bank = Assert.Single(actual, x => x.Name == "bank");
        Assert.False(bank.IsLeaf);
        var bgm = Assert.Single(bank.Expand(), x => x.Name == "bgm");
        var leaf = Assert.Single(bgm.Expand());
        Assert.True(leaf.IsLeaf);
        Assert.Equal("bank/bgm/foo.wav", leaf.Asset!.LogicalPath);
        Assert.Equal("bank/bgm/foo.wav", leaf.Path);
    }

    /// <summary>
    /// 树的分组与用户的**列表排序**无关（树的子节点顺序永远是「目录在前、叶子在后、各自按名称」）。
    /// 旧实现把用户排序结果喂进建树器也一样 —— 这里锁住新路径没有把这条性质弄丢。
    /// </summary>
    [Fact]
    public async Task Tree_shape_is_independent_of_the_list_sort()
    {
        var harness = await NewHarnessAsync();
        var byName = new AssetCatalogTree(harness.Catalog, new AssetSearchQuery(Sort: AssetSortKind.Name)).Roots();
        foreach (var sort in new[]
                 {
                     AssetSortKind.SizeDescending, AssetSortKind.SizeAscending,
                     AssetSortKind.Type, AssetSortKind.ModifiedFirst,
                 })
        {
            var other = new AssetCatalogTree(harness.Catalog, new AssetSearchQuery(Sort: sort)).Roots();
            AssertSameTree(AssetTreeBuilder.BuildRoots(harness.Search.Search(
                    harness.Project, new AssetSearchQuery(Sort: AssetSortKind.Name))),
                other, $"（列表排序 = {sort}）");
            Assert.True(byName.Count == other.Count);
        }
    }

    // ── 逐节点比对 ──────────────────────────────────────────────────────

    private static void AssertSameTree(IReadOnlyList<AssetTreeNode> expected,
        IReadOnlyList<AssetCatalogTreeNode> actual, string because)
    {
        Assert.True(expected.Count == actual.Count, $"{because}：同层节点数 {expected.Count} ≠ {actual.Count}");
        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = actual[i];
            var label = $"{because}｜第 {i} 个节点｜「{e.Name}」对「{a.Name}」";
            Assert.True(e.Name == a.Name, $"{label}：名称不一致");
            Assert.True(e.Depth == a.Depth, $"{label}：深度 {e.Depth} ≠ {a.Depth}");
            Assert.True(e.IsLeaf == a.IsLeaf, $"{label}：叶子判定不一致");
            Assert.True(e.Count == a.Count, $"{label}：资源数 {e.Count} ≠ {a.Count}");
            // 展开键：旧实现是「第一条资源的显示路径取前 depth+1 段」，新实现直接记着同一个串。
            var expectedPath = string.Join('/',
                AssetDisplay.SplitTreePath(AssetDisplay.TreePath(e.Assets[0])).Take(e.Depth + 1));
            Assert.True(expectedPath == a.Path, $"{label}：展开键「{expectedPath}」≠「{a.Path}」");
            if (e.IsLeaf)
            {
                Assert.True(e.Asset!.LogicalPath == a.Asset!.LogicalPath, $"{label}：叶子指向的资源不一致");
            }
            else
            {
                AssertSameTree(e.Expand(), a.Expand(), label);
            }
        }
    }

    private static string Describe(AssetSearchQuery query)
        => $"文本「{query.Text ?? "-"}」· 容器「{query.Container ?? "-"}」· 排序 {query.Sort}";

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
