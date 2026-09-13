using System.Windows.Controls;

namespace LimbusModEditor.App;

/// <summary>
/// 树形浏览的展开状态保持（修复「树形视图突然折叠回初始形态」）。
///
/// <para><b>为什么需要它</b>：四个工作台的浏览树都是「惰性展开 + 整体重建」：
/// 数据刷新（搜索 / 筛选 / 数据库同步 / 编辑集变更 / 切页重进）时页面会
/// <c>ItemsSource = 全新 TreeViewItem 列表</c>，而节点模型
/// （<c>AssetTreeNode</c> / <c>LangTextTreeNode</c> / bank 的 <c>Tag</c>）
/// 是只读纯数据，<b>无法承载展开态</b>，于是整棵树瞬间塌回初始形态、
/// 用户展开到一半的位置全丢。本类在重建前把「哪些路径是展开的」按
/// <b>稳定 key</b>（各页用自己已有的路径口径）收下来，重建后按 key 回放：
/// 该展开的节点先物化子层（复用各页已有的惰性填充逻辑），再置
/// <see cref="TreeViewItem.IsExpanded"/>，逐层向下。</para>
///
/// <para><b>key 由页面提供</b>：<c>keyOf</c> 把 <see cref="TreeViewItem.Tag"/>
/// 映射成稳定字符串（资源页 = 路径段拼 <c>/</c>；文本页 = 条目相对路径；
/// 音频页 = bank 路径 / <c>bankPath|FSB序号</c>；静态页 = dataClass）。
/// 页面切换视图或数据源变化时，key 不匹配的旧条目自然失效，不会误展开。</para>
///
/// <para><b>累积语义</b>：页面持有一个 <see cref="HashSet{T}"/> 跨重建累积
/// （而不是每次重建前现抓）—— 否则一次筛选把树清空就会把「展开集合」冲掉，
/// 清空筛选后无法回放。</para>
/// </summary>
public static class TreeExpansionState
{
    /// <summary>把当前已展开的节点 key 收进 <paramref name="into"/>（只遍历已物化的层）。</summary>
    public static void Capture(ItemsControl? tree, Func<object?, string?> keyOf, ISet<string> into)
    {
        if (tree is null) return;
        foreach (var item in tree.Items)
        {
            if (Resolve(item) is not { } node) continue;
            var key = keyOf(node.Tag);
            if (string.IsNullOrEmpty(key)) continue;
            if (node.IsExpanded) into.Add(key);
            else into.Remove(key);
            Capture(node, keyOf, into);
        }
    }

    /// <summary>
    /// 按 key 集合回放展开态。
    /// <paramref name="materialize"/> 在把某个节点置为展开**之前**调用
    /// （页面在这里生成它的子层，也就是各自 <c>TreeItem_Expanded</c> 里的那段逻辑）。
    /// </summary>
    public static void Restore(ItemsControl? tree, Func<object?, string?> keyOf,
        IReadOnlySet<string> keys, Action<TreeViewItem>? materialize = null, int maxDepth = 16)
        => Restore(tree, keyOf, keys, materialize, depth: 0, maxDepth);

    private static void Restore(ItemsControl? tree, Func<object?, string?> keyOf,
        IReadOnlySet<string> keys, Action<TreeViewItem>? materialize, int depth, int maxDepth)
    {
        if (tree is null || depth > maxDepth || keys.Count == 0) return;
        foreach (var item in tree.Items)
        {
            if (Resolve(item) is not { } node) continue;
            var key = keyOf(node.Tag);
            if (string.IsNullOrEmpty(key) || !keys.Contains(key)) continue;
            materialize?.Invoke(node);
            node.IsExpanded = true;
            Restore(node, keyOf, keys, materialize, depth + 1, maxDepth);
        }
    }

    /// <summary>
    /// 从 <c>Items</c> 里的条目取出容器：页面可能直接放 <see cref="TreeViewItem"/>
    /// （惰性树，本项目四个页面都是），也可能放数据项（由 ItemTemplate 承载）。
    /// </summary>
    private static TreeViewItem? Resolve(object? item) => item switch
    {
        TreeViewItem container => container,
        null => null,
        _ => null,
    };
}
