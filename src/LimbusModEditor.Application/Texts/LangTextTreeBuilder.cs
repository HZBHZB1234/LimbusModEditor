namespace LimbusModEditor.Application.Texts;

/// <summary>
/// 文本工作台浏览树的一个节点（<b>纯数据</b>，无 WPF 依赖，可单测）。
///
/// <para>树形口径（与资料页同骨架）：lang 根 → <c>config.json</c> 与活动语言目录 →
/// 子目录（StoryData 等）→ 文件。文件节点之下是 JSON 键层级，那一层由
/// <c>JsonTreeEditor</c> 按需惰性展开（本类不物化键，避免大文件在构建树时被全量解析）。</para>
/// </summary>
public sealed class LangTextTreeNode
{
    private LangTextTreeNode(string name, string relativePath, LangTextTreeNodeKind kind)
    {
        Name = name;
        RelativePath = relativePath;
        Kind = kind;
    }

    /// <summary>显示名：目录取目录名，文件取文件名（<c>config.json</c> 等根级文件同）。</summary>
    public string Name { get; }

    /// <summary>相对 lang 根的路径（'/' 分隔；根为空串）。</summary>
    public string RelativePath { get; }

    /// <summary>节点种类。</summary>
    public LangTextTreeNodeKind Kind { get; }

    /// <summary>子节点（目录与根才有；文件节点恒为空——键层级由编辑器负责）。</summary>
    public List<LangTextTreeNode> Children { get; } = [];

    /// <summary>是否是目录（可继续展开）。</summary>
    public bool IsFolder => Kind == LangTextTreeNodeKind.Root || Kind == LangTextTreeNodeKind.Folder;

    /// <summary>是否是文件（选中即进编辑集）。</summary>
    public bool IsFile => Kind == LangTextTreeNodeKind.File;

    /// <summary>根节点（路径为空串，展开后是 <c>config.json</c> 与活动语言目录）。</summary>
    public static LangTextTreeNode CreateRoot(string name) => new(name, string.Empty, LangTextTreeNodeKind.Root);

    /// <summary>一个目录节点。</summary>
    public static LangTextTreeNode CreateFolder(string name, string relativePath)
        => new(name, relativePath, LangTextTreeNodeKind.Folder);

    /// <summary>一个文件节点。</summary>
    public static LangTextTreeNode CreateFile(string name, string relativePath)
        => new(name, relativePath, LangTextTreeNodeKind.File);
}

/// <summary>树节点种类。</summary>
public enum LangTextTreeNodeKind
{
    /// <summary>lang 根。</summary>
    Root,
    /// <summary>目录（活动语言目录本身也是目录）。</summary>
    Folder,
    /// <summary>JSON 文件。</summary>
    File,
}

/// <summary>
/// plan-10：lang 文件清单 → 浏览树下层的纯函数构造器。
///
/// <para>为什么单独一个纯类：树形是「目录层级」的纯映射，把它从页面里拆出来就能
/// 单测（不需要 WPF）。页面只负责把节点包成 <c>TreeViewItem</c> 并做惰性展开。</para>
///
/// <para>排序口径与列表一致：目录在前、文件在后，各自按 <see cref="StringComparer.Ordinal"/>
/// 字典序——与 <see cref="LangTextWorkbenchService.EnumerateFiles"/> 的「先 config.json 后字典序」
/// 同源，用户在树里看到的位置与列表里一致。</para>
/// </summary>
public static class LangTextTreeBuilder
{
    /// <summary>从文件清单构造根节点（<paramref name="rootName"/> 一般传 lang 根目录名）。</summary>
    public static LangTextTreeNode Build(IEnumerable<string> relativePaths, string rootName = "lang")
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        var root = LangTextTreeNode.CreateRoot(rootName);
        foreach (var path in relativePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) continue;
            Insert(root, segments, 0, path.Replace('\\', '/'));
        }
        Sort(root);
        return root;
    }

    /// <summary>某目录节点的直接子节点（惰性展开用；文件节点返回空列表，不抛）。</summary>
    public static IReadOnlyList<LangTextTreeNode> Expand(LangTextTreeNode folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return folder.IsFolder ? folder.Children : [];
    }

    /// <summary>按目录相对路径取直接子节点（<paramref name="folderRelativePath"/> 为空串 = 根）。
    /// 页面已经持有根节点时直接读 <see cref="LangTextTreeNode.Children"/>；
    /// 本方法是给「只有路径清单」的调用方（含单测）用的便捷入口。</summary>
    public static IReadOnlyList<LangTextTreeNode> Expand(string folderRelativePath, IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        var current = Build(relativePaths, "lang");
        foreach (var segment in Split(folderRelativePath))
        {
            current = current.Children.FirstOrDefault(x =>
                x.IsFolder && string.Equals(x.Name, segment, StringComparison.Ordinal));
            if (current is null) return [];
        }
        return current.Children;
    }

    private static void Insert(LangTextTreeNode folder, string[] segments, int index, string fullPath)
    {
        var isLeaf = index == segments.Length - 1;
        var name = segments[index];
        var relative = string.Join('/', segments.Take(index + 1));

        if (isLeaf)
        {
            if (!folder.Children.Any(x => x.IsFile && string.Equals(x.Name, name, StringComparison.Ordinal)))
                folder.Children.Add(LangTextTreeNode.CreateFile(name, fullPath));
            return;
        }

        var child = folder.Children.FirstOrDefault(x => x.IsFolder && string.Equals(x.Name, name, StringComparison.Ordinal));
        if (child is null)
        {
            child = LangTextTreeNode.CreateFolder(name, relative);
            folder.Children.Add(child);
        }
        Insert(child, segments, index + 1, fullPath);
    }

    private static void Sort(LangTextTreeNode folder)
    {
        folder.Children.Sort(static (left, right) =>
        {
            if (left.IsFolder != right.IsFolder) return left.IsFolder ? -1 : 1;
            return string.CompareOrdinal(left.Name, right.Name);
        });
        foreach (var child in folder.Children) Sort(child);
    }

    private static string[] Split(string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath)
            ? []
            : relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
}
