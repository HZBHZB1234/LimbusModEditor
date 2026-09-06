using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Assets;

/// <summary>VS Code 式资源目录树节点：把扁平的 LogicalPath
/// （<c>&lt;缓存外层键&gt;/&lt;内层键&gt;/&lt;容器路径&gt;/&lt;pathId&gt;.&lt;typeId&gt;</c>）
/// 按「/」路径段逐层分组，让用户像浏览文件夹一样展开定位资源。
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

    /// <summary>节点显示名：路径段（目录）或 <c>pathId.typeId</c>（文件叶子）。</summary>
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
    /// 之下就结束的资产成为叶子子节点。目录在前、叶子在后，组内按名称排序
    /// （与 VS Code 的资源管理器一致）。</summary>
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
        foreach (var (name, bucket) in directories.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            children.Add(new AssetTreeNode(name, segmentIndex, bucket, null));
        foreach (var (name, asset) in leaves.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            children.Add(new AssetTreeNode(name, segmentIndex, [asset], asset));
        return children;
    }

    private static int SegmentCount(AssetRecord asset)
    {
        var path = asset.LogicalPath;
        var count = 1;
        for (var i = 0; i < path.Length; i++)
            if (path[i] == '/') count++;
        return count;
    }

    private static string? SegmentAt(AssetRecord asset, int depth)
    {
        var path = asset.LogicalPath;
        var start = 0;
        for (var i = 0; i <= depth; i++)
        {
            if (start > path.Length) return null;
            var index = path.IndexOf('/', start);
            if (i == depth)
                return index < 0 ? path[start..] : path[start..index];
            if (index < 0) return null;
            start = index + 1;
        }
        return null;
    }
}

public static class AssetTreeBuilder
{
    /// <summary>构建根层节点：按第一段路径分组（缓存外层键 / 来源包名）。
    /// 根层一次构建（只是引用分组，不做完整解析），其余层在展开时惰性构建。</summary>
    public static IReadOnlyList<AssetTreeNode> BuildRoots(IEnumerable<AssetRecord> assets)
    {
        var byRoot = new Dictionary<string, List<AssetRecord>>(StringComparer.OrdinalIgnoreCase);
        var single = new List<(string Name, AssetRecord Asset)>();
        foreach (var asset in assets)
        {
            var path = asset.LogicalPath;
            var index = path.IndexOf('/');
            if (index < 0) { single.Add((path, asset)); continue; }
            var root = path[..index];
            if (!byRoot.TryGetValue(root, out var bucket))
                byRoot[root] = bucket = [];
            bucket.Add(asset);
        }
        var roots = new List<AssetTreeNode>(byRoot.Count + single.Count);
        foreach (var (name, bucket) in byRoot.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            roots.Add(new AssetTreeNode(name, 0, bucket, null));
        foreach (var (name, asset) in single.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            roots.Add(new AssetTreeNode(name, 0, [asset], asset));
        return roots;
    }
}
