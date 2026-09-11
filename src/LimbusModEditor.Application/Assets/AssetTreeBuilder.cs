using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Assets;

/// <summary>文件管理器式资源目录树节点：把资源按「容器路径 + 友好名」
/// （<see cref="AssetDisplay.TreePath"/>，源自 Unity m_Container 的游戏内
/// 真实资源路径）按「/」路径段逐层分组，让用户像浏览文件夹一样展开定位资源，
/// 不暴露缓存键 / Path ID 等实现细节。
/// 目录节点持有子树内全部资源的引用集合，展开时才按下一层路径段分组
/// （惰性），因此 40 万级全量索引也只需在展开到的分支上付出分组成本。</summary>
public sealed class AssetTreeNode
{
    public AssetTreeNode(string name, int depth, IReadOnlyList<AssetRecord> assets, AssetRecord? leaf)
    {
        Name = name;
        Depth = depth;
        Assets = assets;
        Asset = leaf;
    }

    /// <summary>节点显示名：路径段（目录）或资源友好名（叶子）。</summary>
    public string Name { get; }

    /// <summary>路径深度：根节点为 0，其子节点为 1，以此类推。</summary>
    public int Depth { get; }

    /// <summary>目录节点：子树内全部资源（引用，不复制）；叶子节点：仅自身。</summary>
    public IReadOnlyList<AssetRecord> Assets { get; }

    /// <summary>非空 = 文件叶子节点，直接对应一条资源。</summary>
    public AssetRecord? Asset { get; }

    public bool IsLeaf => Asset is not null;

    /// <summary>目录节点的子项总数（供 UI 显示「名称 (N)」）。</summary>
    public int Count => Assets.Count;

    /// <summary>展开一层：当前节点代表第 <see cref="Depth"/> 段，展开 = 把
    /// 子树内资源按第 Depth+1 段分组（根节点由 <see cref="AssetTreeBuilder.BuildRoots"/>
    /// 按第 0 段建好后，第一次展开产出第 1 段的子节点）。路径恰好在当前节点
    /// 之下就结束的资产成为叶子子节点。目录在前、叶子在后，组内按名称
    /// 自然排序（数字按数值比较，与文件管理器一致）。</summary>
    public IReadOnlyList<AssetTreeNode> Expand()
    {
        var segmentIndex = Depth + 1;
        var directories = new Dictionary<string, List<AssetRecord>>(StringComparer.OrdinalIgnoreCase);
        var leaves = new List<(string Name, AssetRecord Asset)>();
        foreach (var asset in Assets)
        {
            var segment = SegmentAt(asset, segmentIndex);
            if (segment is null) continue;
            var remaining = segmentIndex + 1 >= SegmentCount(asset);
            if (remaining) leaves.Add((segment, asset));
            else
            {
                if (!directories.TryGetValue(segment, out var bucket))
                    directories[segment] = bucket = [];
                bucket.Add(asset);
            }
        }
        var children = new List<AssetTreeNode>(directories.Count + leaves.Count);
        foreach (var (name, bucket) in directories.OrderBy(x => x.Key, AssetDisplay.ComparerInstance))
            children.Add(new AssetTreeNode(name, segmentIndex, bucket, null));
        foreach (var (name, asset) in DistinguishLeaves(leaves).OrderBy(x => x.Name, AssetDisplay.ComparerInstance))
            children.Add(new AssetTreeNode(name, segmentIndex, [asset], asset));
        return children;
    }

    /// <summary>同一父节点下的同名叶子消歧：m_Container 允许一个路径挂多个
    /// 对象（实测 92.7% 的容器条目是重名的），若只显示路径末段，用户会看到
    /// 一串完全相同的条目而无法分辨该选哪一条。这里只给<b>真正重名</b>的叶子
    /// 追加「 · 类型 #编号」后缀（见 <see cref="AssetDisplay.LeafDisambiguator"/>）：
    /// 唯一名保持原样，23,326 条唯一路径零后缀、零视觉噪音。
    /// <para>后缀逐级加长，直到组内互相区分：常规是「类型 #编号」，仅当同组里
    /// 出现「类型 #编号」全同的资源（只差 bundle）时才补 bundle 归属 —— 实测该
    /// 升级路径覆盖 1,751 组 / 3,502 条，补 bundle 后仍歧义的组数 = 0。
    /// 判定走后缀计数而非两两比较：真实的重复组可以很大（实测最大 111 个同级
    /// 同名叶子），两两比较是 O(n²)；而比较拼接后的后缀字符串又会在格式调整时
    /// 静默失效。</para></summary>
    internal static List<(string Name, AssetRecord Asset)> DistinguishLeaves(
        List<(string Name, AssetRecord Asset)> leaves)
    {
        if (leaves.Count < 2) return leaves;
        var nameCounts = new Dictionary<string, int>(leaves.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in leaves)
            nameCounts[name] = nameCounts.TryGetValue(name, out var n) ? n + 1 : 1;

        // 只统计重名名字的后缀：唯一名完全不进这里（绝大多数资源走这条路）。
        // 后缀出现次数 > 1 即该后缀不足以区分，需要升级到带 bundle 的形态。
        var coarseCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, asset) in leaves)
        {
            if (nameCounts[name] < 2) continue;
            var coarse = AssetDisplay.LeafDisambiguator(asset, includeTypeLabel: true);
            if (!coarseCounts.TryGetValue(name, out var counts))
                coarseCounts[name] = counts = new Dictionary<string, int>(StringComparer.Ordinal);
            counts[coarse] = counts.TryGetValue(coarse, out var n) ? n + 1 : 1;
        }

        for (var i = 0; i < leaves.Count; i++)
        {
            var (name, asset) = leaves[i];
            if (nameCounts[name] < 2) continue;
            // 「类型 #编号」仍不足以区分 → 升级为「类型 #编号 bundle」。
            var needsBundle = coarseCounts[name][AssetDisplay.LeafDisambiguator(asset, includeTypeLabel: true)] > 1;
            var label = AssetDisplay.LeafDisambiguator(asset, includeTypeLabel: true, includeBundle: needsBundle);
            if (label.Length == 0) continue;
            leaves[i] = (name + AssetDisplay.LeafDisambiguatorSeparator + label, asset);
        }
        return leaves;
    }

    private static int SegmentCount(AssetRecord asset) =>
        AssetDisplay.SplitTreePath(AssetDisplay.TreePath(asset)).Length;

    private static string? SegmentAt(AssetRecord asset, int depth)
    {
        var segments = AssetDisplay.SplitTreePath(AssetDisplay.TreePath(asset));
        return depth < segments.Length ? segments[depth] : null;
    }
}

public static class AssetTreeBuilder
{
    /// <summary>构建根层节点：按容器显示路径的第一段分组（游戏内资源根目录）。
    /// 根层一次构建（只是引用分组，不做完整解析），其余层在展开时惰性构建。</summary>
    public static IReadOnlyList<AssetTreeNode> BuildRoots(IEnumerable<AssetRecord> assets)
    {
        var byRoot = new Dictionary<string, List<AssetRecord>>(StringComparer.OrdinalIgnoreCase);
        var single = new List<(string Name, AssetRecord Asset)>();
        foreach (var asset in assets)
        {
            var path = AssetDisplay.TreePath(asset);
            var index = path.IndexOf('/');
            if (index < 0) { single.Add((path, asset)); continue; }
            var root = path[..index];
            if (!byRoot.TryGetValue(root, out var bucket))
                byRoot[root] = bucket = [];
            bucket.Add(asset);
        }
        var roots = new List<AssetTreeNode>(byRoot.Count + single.Count);
        foreach (var (name, bucket) in byRoot.OrderBy(x => x.Key, AssetDisplay.ComparerInstance))
            roots.Add(new AssetTreeNode(name, 0, bucket, null));
        // 根级叶子（容器条目本身就是单段路径）同样可能重名，走同一条消歧规则。
        foreach (var (name, asset) in AssetTreeNode.DistinguishLeaves(single).OrderBy(x => x.Name, AssetDisplay.ComparerInstance))
            roots.Add(new AssetTreeNode(name, 0, [asset], asset));
        return roots;
    }
}
