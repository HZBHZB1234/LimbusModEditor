using System.Diagnostics;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Domain.Projects;
using NLog;

namespace LimbusModEditor.Application.Assets;

public sealed record AssetSearchQuery(
    string? Text = null,
    AssetType? Type = null,
    AssetEditState? State = null,
    string? Container = null,
    long? UnityPathId = null,
    int? UnityTypeId = null,
    long? MinSize = null,
    long? MaxSize = null,
    bool? HasReplacement = null,
    AssetSortKind Sort = AssetSortKind.Name,
    bool? HasContainerEntry = null);

public sealed class AssetSearchService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public IReadOnlyList<AssetRecord> Search(ModProject project, AssetSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(project);
        Log.Debug("资源搜索：对项目快照执行（项目内 {0:N0} 条），文本「{1}」，类型 {2}，容器「{3}」。",
            project.Assets.Count, query.Text ?? "-", query.Type?.ToString() ?? "-", query.Container ?? "-");
        // 快照后交给重载：调用方可以先在 UI 线程取快照，再把过滤排序放到
        // 后台线程，避免大项目（全缓存扫描 40 万级）冻结界面。
        return Search(project.Assets.ToArray(), query);
    }

    public IReadOnlyList<AssetRecord> Search(IEnumerable<AssetRecord> assets, AssetSearchQuery query)
    {
        if (query.MinSize is < 0) throw new ArgumentException("MinSize 不能为负。", nameof(query));
        if (query.MaxSize is < 0) throw new ArgumentException("MaxSize 不能为负（负值曾经静默关闭上限）。", nameof(query));
        var text = query.Text?.Trim();
        using var scope = Log.Scope("资源搜索");
        Log.Info("搜索开始：文本「{0}」，类型 {1}，状态 {2}，容器「{3}」，UnityPathId {4}，UnityTypeId {5}，大小 [{6}..{7}]，有替换 {8}，有容器条目 {9}，排序 {10}",
            text ?? "-", query.Type?.ToString() ?? "-", query.State?.ToString() ?? "-", query.Container ?? "-",
            query.UnityPathId?.ToString() ?? "-", query.UnityTypeId?.ToString() ?? "-",
            query.MinSize?.ToString() ?? "-", query.MaxSize?.ToString() ?? "-",
            query.HasReplacement?.ToString() ?? "-", query.HasContainerEntry?.ToString() ?? "-",
            query.Sort);
        var startTimestamp = Stopwatch.GetTimestamp();
        // 单趟过滤（不再用 LINQ 谓词链：40 万级下每个委托调用都要付出闭包
        // 取值与迭代器状态机开销），命中集先物化再排序。
        var matched = new List<AssetRecord>(Math.Min(1024, assets is ICollection<AssetRecord> collection ? collection.Count : 1024));
        foreach (var asset in assets)
        {
            if (Matches(asset, query, text)) matched.Add(asset);
        }

        var ordered = SortByKeys(matched, query.Sort);
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        Log.Info("搜索完成：命中 {0:N0} 条，耗时 {1:0.#} ms（排序 {2}）。",
            ordered.Length, elapsed.TotalMilliseconds, query.Sort);
        return ordered;
    }

    /// <summary>逐条判据（顺序与原 LINQ 谓词链一致：廉价判据在前、IO 判据在后）。
    /// <para><b>公开是刻意的</b>：资源列表改成「按页从索引查询」之后，页里的记录由
    /// <c>AssetCatalog</c> 构造，但判据**必须还是这一份** —— 复制一份就等于
    /// 「分页列表」与「导出/构建/旧搜索」对同一条资源给出不同结论，而这种漂移
    /// 只会在用户那里以「少了一条/多了一条」的形式出现。<paramref name="text"/> 由调用方
    /// 预先 Trim（<see cref="Search(IEnumerable{AssetRecord}, AssetSearchQuery)"/> 就是这么做的）。</para></summary>
    public static bool Matches(AssetRecord asset, AssetSearchQuery query, string? text)
    {
        if (!string.IsNullOrEmpty(text) && !MatchesText(asset, text)) return false;
        if (query.Type is { } type && asset.Type != type) return false;
        if (query.State is { } state && asset.EditState != state) return false;
        if (!string.IsNullOrEmpty(query.Container)
            && asset.ContainerPath?.Contains(query.Container, StringComparison.OrdinalIgnoreCase) != true) return false;
        if (query.UnityPathId is { } pathId && asset.UnityPathId != pathId) return false;
        if (query.UnityTypeId is { } typeId && asset.UnityTypeId != typeId) return false;
        if (query.MinSize is { } minSize && asset.Size < minSize) return false;
        if (query.MaxSize is { } maxSize && asset.Size > maxSize) return false;
        if (query.HasReplacement is { } hasReplacement && HasUsableReplacement(asset) != hasReplacement) return false;
        if (query.HasContainerEntry is { } hasContainerEntry && HasContainerEntry(asset) != hasContainerEntry) return false;
        return true;
    }

    /// <summary>
    /// 列表排序的**预计算键**：排序只需要这几个字段，而 <see cref="AssetRecord"/>
    /// 每条 1,028 字节（127 万条就是 1.2 GiB）。按页查询要在「不物化整条记录」的前提下
    /// 用同一份排序语义排候选集，所以排序依据必须能从记录上摘下来。
    /// </summary>
    public readonly record struct AssetSortKey(
        long Size, string TypeLabel, bool Modified, string DisplayPath, string LogicalPath);

    /// <summary>摘排序键。显示路径只算一次（Schwartzian 变换的显式形态）。</summary>
    public static AssetSortKey BuildSortKey(AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return new(asset.Size, AssetDisplay.TypeLabel(asset.Type),
            asset.EditState is not AssetEditState.Unchanged,
            AssetDisplay.DisplayPath(asset), asset.LogicalPath);
    }

    /// <summary>
    /// 与旧 <c>OrderBy(...).ThenBy(...)</c> 链**逐项同序**的比较。
    /// <para>尾键 <c>LogicalPath</c> 是全库唯一的（1,275,623 行 = 1,275,623 个 DISTINCT），
    /// 所以这是一个**严格全序、没有并列** —— 因此「按需排序」与 LINQ 稳定排序的结果一致，
    /// 可以放心用不稳定排序（<c>Array.Sort</c> / <c>List.Sort</c>）。</para>
    /// <para>类型的二级比较刻意用 <see cref="StringComparison.CurrentCulture"/>：类型标签是
    /// 中文，旧实现的 <c>OrderBy(x =&gt; x.Label, StringComparer.CurrentCulture)</c> 就是它。</para>
    /// </summary>
    public static int CompareSortKeys(in AssetSortKey a, in AssetSortKey b, AssetSortKind sort)
    {
        switch (sort)
        {
            case AssetSortKind.SizeDescending:
            {
                var bySize = b.Size.CompareTo(a.Size);
                if (bySize != 0) return bySize;
                break;
            }
            case AssetSortKind.SizeAscending:
            {
                var bySize = a.Size.CompareTo(b.Size);
                if (bySize != 0) return bySize;
                break;
            }
            case AssetSortKind.Type:
            {
                var byLabel = string.Compare(a.TypeLabel, b.TypeLabel, StringComparison.CurrentCulture);
                if (byLabel != 0) return byLabel;
                break;
            }
            case AssetSortKind.ModifiedFirst:
            {
                // 「已修改在前」= 旧实现的 OrderBy(EditState == Unchanged ? 1 : 0)。
                var byModified = (a.Modified ? 0 : 1).CompareTo(b.Modified ? 0 : 1);
                if (byModified != 0) return byModified;
                break;
            }
        }
        var byPath = AssetDisplay.ComparePaths(a.DisplayPath, b.DisplayPath);
        return byPath != 0
            ? byPath
            : string.Compare(a.LogicalPath, b.LogicalPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 排序键的**预计算**（Schwartzian 变换）：显示路径解析（
    /// <see cref="AssetDisplay.DisplayPath"/> → <see cref="AssetDisplay.TreePath"/>
    /// 的字典查找 + 裁剪 + 拼接 + 分段）原先发生在比较器内部，每次比较都要
    /// 重算两侧 —— 40 万条资产约 2×10⁷ 次比较，单次搜索上亿次分配。
    /// 现在每条只算一次，之后比较器只走 <see cref="CompareSortKeys"/>
    /// （零分配）。语义与原「先比主键、再比显示路径、再比 LogicalPath」完全一致 ——
    /// 排序键与比较器都是 <see cref="BuildSortKey"/> / <see cref="CompareSortKeys"/>
    /// 这一份实现，按页查询用的是同一份。
    /// </summary>
    private static AssetRecord[] SortByKeys(List<AssetRecord> matched, AssetSortKind sort)
    {
        if (matched.Count == 0) return [];
        var keys = new AssetSortKey[matched.Count];
        for (var i = 0; i < keys.Length; i++) keys[i] = BuildSortKey(matched[i]);
        var order = new int[matched.Count];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (x, y) => CompareSortKeys(keys[x], keys[y], sort));
        var sorted = new AssetRecord[matched.Count];
        for (var i = 0; i < sorted.Length; i++) sorted[i] = matched[order[i]];
        return sorted;
    }

    /// <summary>对象是否在 m_Container 表里有游戏内资源路径（文件管理器视图
    /// 的「看得见的文件」；容器外的支撑对象默认隐藏以减少技术噪音）。
    /// 导入的旧式资源不算支撑对象（LogicalPath 就是它的名字），始终视为可见。</summary>
    private static bool HasContainerEntry(AssetRecord asset)
        => !AssetDisplay.IsCacheReference(asset) || !string.IsNullOrWhiteSpace(AssetDisplay.ContainerEntryPath(asset));

    /// <summary>文本匹配覆盖：显示路径（容器路径 + 友好名）、LogicalPath、
    /// 源文件路径 —— 用户按游戏内资源名搜索时不需要知道缓存键。</summary>
    private static bool MatchesText(AssetRecord asset, string text)
        => AssetDisplay.DisplayPath(asset).Contains(text, StringComparison.OrdinalIgnoreCase) ||
           asset.LogicalPath.Contains(text, StringComparison.OrdinalIgnoreCase) ||
           asset.SourcePath?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>An asset counts as "replaced" only when its registered
    /// replacement file still exists; a vanished replacement cannot be
    /// materialized by a build and must not hide behind the filter.</summary>
    private static bool HasUsableReplacement(AssetRecord asset)
        => asset.Metadata.TryGetValue("replacementPath", out var path) && File.Exists(path);
}
