using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using NLog;

namespace LimbusModEditor.Application.Catalog;

/// <summary>
/// 目录树的一个节点：**只记「命中序列里的一个区间」**（<c>[Start, End)</c>），不持有记录。
///
/// <para><b>为什么是区间</b>：目录树的分组依据是显示路径，而命中序列（
/// <see cref="AssetCatalog.MatchOrder"/>）恰恰就是**按显示路径的自然序**排好的 ——
/// 于是「同一个目录下的全部资源」必然是序列里**连续的一段**。节点的资源条数 =
/// 区间长度，展开 = 把区间按下一段路径切开，两者都是 O(1)/O(区间长度) 且不复制记录。</para>
///
/// <para>对照旧实现（<see cref="AssetTreeNode"/>）：那里每个节点直接持有子树内
/// **全部 AssetRecord 的引用集合**，于是「建一次树」等于把整份命中集常驻内存 ——
/// 命中 5 万条就是 5 万 × 1,028 字节。这也是资源列表改成按页查询之后，
/// 目录树成为最后一个「把全量拉进内存」的消费者的原因。</para>
/// </summary>
public sealed class AssetCatalogTreeNode
{
    private readonly AssetCatalogTree _tree;
    private IReadOnlyList<AssetCatalogTreeNode>? _children;

    internal AssetCatalogTreeNode(AssetCatalogTree tree, string name, string path, int depth,
        int start, int end, AssetRecord? leaf)
    {
        _tree = tree;
        Name = name;
        Path = path;
        Depth = depth;
        Start = start;
        End = end;
        Asset = leaf;
    }

    /// <summary>节点显示名：目录 = 路径段；资源叶子 = 友好名（重名时含消歧后缀，见
    /// <see cref="AssetTreeNode.DistinguishLeaves"/>）。</summary>
    public string Name { get; }

    /// <summary>
    /// 从根到自己的**原始**路径段（<c>'/'</c> 拼接，不含消歧后缀）—— 展开态的稳定 key。
    /// <para>不含消歧后缀是刻意的：后缀会随「同级是否有重名」而变，拿它当 key 会让
    /// 展开态在一次筛选之后全部失配。旧实现（<c>AssetTreeKeyOf</c>）用「第一条资源的
    /// 显示路径取前 depth+1 段」算出同一个串，这里直接算好，省掉为算 key 而保留一条记录。</para>
    /// </summary>
    public string Path { get; }

    /// <summary>路径深度：根节点 0。</summary>
    public int Depth { get; }

    /// <summary>该节点在命中序列里的区间起点（含）。</summary>
    public int Start { get; }

    /// <summary>该节点在命中序列里的区间终点（不含）。</summary>
    public int End { get; }

    /// <summary>子树内的资源条数（= 区间长度）。目录节点显示为「名称 (N)」。</summary>
    public long Count => End - Start;

    /// <summary>非空 = 资源叶子节点。</summary>
    public AssetRecord? Asset { get; }

    public bool IsLeaf => Asset is not null;

    /// <summary>
    /// 展开一层：把本节点的区间按第 <see cref="Depth"/>+1 段路径切开。
    /// <para>惰性且只在第一次调用时算；叶子返回空。**每一层只物化这一层需要分组的那一段**，
    /// 不是整棵子树。</para>
    /// </summary>
    public IReadOnlyList<AssetCatalogTreeNode> Expand()
        => IsLeaf ? [] : _children ??= _tree.BuildLevel(this);
}

/// <summary>
/// 目录树的**按需分层**数据源：命中集只以 8 字节条目的形式常驻（<see cref="AssetCatalog.MatchOrder"/>），
/// 每展开一层才按区间分批把记录取回来分组、随后丢弃。
///
/// <para><b>口径只有一份</b>：显示路径走 <see cref="AssetDisplay.TreePath"/> 与
/// <see cref="AssetDisplay.SegmentAt"/>，同层叶子的重名消歧直接调
/// <c>AssetTreeNode.DistinguishLeaves</c>（与 <see cref="AssetTreeBuilder"/> 是同一个函数），
/// 同层排序用 <see cref="AssetDisplay.ComparerInstance"/>。所以「树长什么样」这件事
/// 没有第二份实现 —— 差异只可能来自数据来源，而数据来源由
/// <c>AssetCatalogTreeTests</c> 逐节点对账守住。</para>
///
/// <para><b>为什么分组能按连续区间做</b>：树的分组序 = 显示路径的自然序。命中序列
/// <see cref="AssetCatalog.MatchOrder"/> 用的就是这条序（本类强制 <c>Sort = Name</c>），
/// 于是「同层同名目录下的资源」在序列里连续；同层的资源叶子也必然排在**同名目录组之前**
/// （<c>"A/X"</c> 比 <c>"A/X/y"</c> 短，自然序在前），所以叶子的插入不会把任何目录组切断。</para>
///
/// <para><b>与列表排序无关</b>：树的子节点顺序本来就是「目录在前、叶子在后、各自按名称」，
/// 用户的「按大小」排序只影响列表。旧实现把用户排序结果喂进
/// <see cref="AssetTreeBuilder.BuildRoots"/> 也一样 —— 那边的输出顺序也与输入顺序无关。</para>
/// </summary>
public sealed class AssetCatalogTree
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>每层分批物化的批大小。取大一些是为了少开几次连接
    /// （<c>StreamByRanks</c> 每次调用自己开连接，而 WAL 下每次 open 都要读 schema）；
    /// 代价是瞬时多几百 KB 记录，物化完一批就丢。</summary>
    private const int LevelBatchSize = 2000;

    private readonly AssetCatalog _catalog;
    private readonly IReadOnlyList<AssetCatalogEntry> _entries;

    public AssetCatalogTree(AssetCatalog catalog, AssetSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(query);
        _catalog = catalog;
        // 强制按名称序取命中序列：见类注释「为什么分组能按连续区间做」。
        // 这同时是**最便宜**的一档 —— 按名称序时 AssetCatalog 不必为每条算排序键
        // （除非有项目态补充集，那时无论如何都要算）。
        _entries = catalog.MatchOrder(query with { Sort = AssetSortKind.Name });
    }

    /// <summary>命中总数（= 树里的资源总数）。</summary>
    public long Count => _entries.Count;

    /// <summary>根层节点（资源总数级很小时几乎没有开销；大目录只有展开时才付）。</summary>
    public IReadOnlyList<AssetCatalogTreeNode> Roots()
        => _entries.Count == 0 ? [] : BuildLevel(null, 0, _entries.Count, 0, null);

    /// <summary>一个已被收集的同层目录组（原始段名 + 命中序列区间）。</summary>
    private readonly record struct DirectoryGroup(string Name, int Start, int End);

    internal IReadOnlyList<AssetCatalogTreeNode> BuildLevel(AssetCatalogTreeNode? parent)
        => parent is null ? [] : BuildLevel(parent, parent.Start, parent.End, parent.Depth + 1, parent.Path);

    /// <summary>
    /// 把命中序列的 <c>[start, end)</c> 按第 <paramref name="depth"/> 段路径分组，得到一层子节点。
    /// <para>顺序与 <see cref="AssetTreeNode.Expand"/> 逐项一致：目录在前、叶子在后，
    /// 各自按名称自然序；叶子的重名消歧共用同一个函数。</para>
    /// </summary>
    private IReadOnlyList<AssetCatalogTreeNode> BuildLevel(
        AssetCatalogTreeNode? parent, int start, int end, int depth, string? parentPath)
    {
        var directories = new List<DirectoryGroup>();
        var leafNames = new List<(string Name, AssetRecord Asset)>();
        // 与 leafNames 一一对应的两个平行列表：原始段名（拼 Path 用）与命中序列里的下标（区间用）。
        var leafRawNames = new List<string>();
        var leafIndexes = new List<int>();

        for (var offset = start; offset < end; offset += LevelBatchSize)
        {
            var count = Math.Min(LevelBatchSize, end - offset);
            var slice = new List<AssetCatalogEntry>(count);
            for (var i = 0; i < count; i++) slice.Add(_entries[offset + i]);
            var records = _catalog.ResolveEntries(slice);
            if (records.Count != slice.Count)
                Log.Warn("目录树分层：一批 {0} 条只取回 {1} 条（命中序列与索引可能不同步），本批按取回的条数分组",
                    slice.Count, records.Count);
            for (var j = 0; j < records.Count; j++)
            {
                var index = offset + j;
                var record = records[j];
                var segment = AssetDisplay.SegmentAt(AssetDisplay.TreePath(record), depth, out var segmentCount);
                if (segment.IsEmpty) continue;
                var name = segment.ToString();
                // 这一段就是路径最后一段 ⇒ 它是资源叶子（对应旧实现 Expand 里的
                // `remaining = segmentIndex + 1 >= segmentCount`，此处 segmentIndex == depth）。
                if (depth + 1 >= segmentCount)
                {
                    leafNames.Add((name, record));
                    leafRawNames.Add(name);
                    leafIndexes.Add(index);
                    continue;
                }
                // 目录组：同名的连续区间；换名即开新组（叶子被跳过，所以不会把组切断 —— 见类注释）。
                if (directories.Count > 0
                    && string.Equals(directories[^1].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    directories[^1] = directories[^1] with { End = index + 1 };
                }
                else
                {
                    directories.Add(new DirectoryGroup(name, index, index + 1));
                }
            }
        }

        var children = new List<AssetCatalogTreeNode>(directories.Count + leafNames.Count);
        foreach (var group in directories.OrderBy(x => x.Name, AssetDisplay.ComparerInstance))
            children.Add(new AssetCatalogTreeNode(this,
                group.Name, Join(parentPath, group.Name), depth, group.Start, group.End, null));

        // 重名消歧：**同一个函数**，先在 (Name, Asset) 上就地改写名字，再按改写后的名字排序。
        // 排序必须稳定：同名叶子（消歧后仍同名）的顺序沿用命中序列，才不会因为
        // 「换了一次数据来源」而让两棵等价的树出现不同的叶子次序。
        AssetTreeNode.DistinguishLeaves(leafNames);
        var leafOrder = new List<int>(leafNames.Count);
        for (var i = 0; i < leafNames.Count; i++) leafOrder.Add(i);
        leafOrder.Sort((a, b) =>
        {
            var byName = AssetDisplay.CompareNames(leafNames[a].Name, leafNames[b].Name);
            return byName != 0 ? byName : a.CompareTo(b);
        });
        foreach (var i in leafOrder)
        {
            // 叶子的区间是单条：起点用**命中序列里的下标**（不是本层内的偏移），
            // 这样它的 Start/End 与兄弟节点在同一套坐标系里，区间长度恒为 1。
            children.Add(new AssetCatalogTreeNode(this, leafNames[i].Name,
                Join(parentPath, leafRawNames[i]), depth,
                leafIndexes[i], leafIndexes[i] + 1, leafNames[i].Asset));
        }
        return children;
    }

    private static string Join(string? parentPath, string name)
        => string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";
}
