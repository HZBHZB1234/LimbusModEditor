namespace LimbusModEditor.Application.Texts;

/// <summary>文本工作台目录树的一个节点（懒加载：一次只给一层）。</summary>
/// <param name="Name">节点显示名（目录名或文件名）。</param>
/// <param name="Path">相对语言目录的路径（'/' 分隔）；根节点下的直接子节点没有前缀。</param>
/// <param name="IsLeaf">true = 文件（点开即读键值）；false = 目录（再请求一层）。</param>
/// <param name="ChildCount">目录的直接子节点数（文件是 0）。</param>
public sealed record LangFileTreeNode(string Name, string Path, bool IsLeaf, int ChildCount);

/// <summary>
/// lang 文件列表 → 目录树。数据源就是 <see cref="LangTextWorkbenchService.EnumerateFiles"/>
/// 的那套相对路径（相对活动语言目录），不另走一遍磁盘、不另造枚举口径。
/// </summary>
public static class LangFileTree
{
    /// <summary>
    /// 取出 <paramref name="parentPath"/> 下的一层子节点（parentPath 为空 = 根）。
    /// 目录在前、文件在后，各自按名称排序（不区分大小写）。
    /// </summary>
    public static IReadOnlyList<LangFileTreeNode> ChildrenOf(
        IEnumerable<string> relativePaths, string? parentPath)
    {
        var parent = Normalize(parentPath);
        var prefix = parent.Length == 0 ? string.Empty : parent + "/";
        // 每个目录下的直接子节点名（用于 childCount）；key = 目录路径（根为 ""）
        var children = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // 目录路径 → 目录名
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // 文件路径 → 文件名

        foreach (var raw in relativePaths)
        {
            var path = Normalize(raw);
            if (path.Length == 0) continue;
            var parts = path.Split('/');
            // 先登记「每个目录的直接子节点」（含文件本身），childCount 由此得来
            for (var i = 0; i < parts.Length; i++)
            {
                var key = string.Join('/', parts.Take(i));
                if (!children.TryGetValue(key, out var set)) children[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(parts[i]);
            }

            if (prefix.Length > 0 &&
                !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = prefix.Length == 0 ? path : path[prefix.Length..];
            if (rest.Length == 0) continue;
            var restParts = rest.Split('/');
            var childPath = prefix.Length == 0 ? restParts[0] : prefix + restParts[0];
            if (restParts.Length == 1)
                files.TryAdd(childPath, restParts[0]);
            else
                dirs.TryAdd(childPath, restParts[0]);
        }

        var nodes = dirs
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new LangFileTreeNode(x.Value, x.Key, false,
                children.TryGetValue(x.Key, out var set) ? set.Count : 0))
            .Concat(files
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new LangFileTreeNode(x.Value, x.Key, true, 0)))
            .ToList();
        return nodes;
    }

    private static string Normalize(string? path)
        => (path ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
}
