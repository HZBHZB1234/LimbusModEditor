using System.Diagnostics;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.StaticMods;
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
    bool? HasContainerEntry = null,
    /// <summary>plan-08：是否显示静态数据 bundle（static_s1_0_assets_all_*）里的
    /// 资源。默认 false —— 资源工作台默认视图不出现这些资源，由静态数据工作台
    /// 专门编辑；旧调用不传该参数即保持「默认隐藏」。</summary>
    bool ShowStaticTables = false);

public sealed class AssetSearchService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public IReadOnlyList<AssetRecord> Search(ModProject project, AssetSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(project);
        Log.Debug("资源搜索：对项目快照执行（项目内 {0:N0} 条），文本「{1}」，类型 {2}，容器「{3}」，显示静态数据表={4}。",
            project.Assets.Count, query.Text ?? "-", query.Type?.ToString() ?? "-", query.Container ?? "-", query.ShowStaticTables);
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
        Log.Info("搜索开始：文本「{0}」，类型 {1}，状态 {2}，容器「{3}」，UnityPathId {4}，UnityTypeId {5}，大小 [{6}..{7}]，有替换 {8}，有容器条目 {9}，排序 {10}，显示静态数据表 {11}",
            text ?? "-", query.Type?.ToString() ?? "-", query.State?.ToString() ?? "-", query.Container ?? "-",
            query.UnityPathId?.ToString() ?? "-", query.UnityTypeId?.ToString() ?? "-",
            query.MinSize?.ToString() ?? "-", query.MaxSize?.ToString() ?? "-",
            query.HasReplacement?.ToString() ?? "-", query.HasContainerEntry?.ToString() ?? "-",
            query.Sort, query.ShowStaticTables);
        var startTimestamp = Stopwatch.GetTimestamp();
        // 单趟过滤（不再用 LINQ 谓词链：40 万级下每个委托调用都要付出闭包
        // 取值与迭代器状态机开销），命中集先物化再排序。
        var matched = new List<AssetRecord>(Math.Min(1024, assets is ICollection<AssetRecord> collection ? collection.Count : 1024));
        var staticVerdicts = query.ShowStaticTables ? null : new StaticVerdictCache();
        foreach (var asset in assets)
        {
            if (Matches(asset, query, text, staticVerdicts)) matched.Add(asset);
        }

        var ordered = SortByKeys(matched, query.Sort);
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        Log.Info("搜索完成：命中 {0:N0} 条，耗时 {1:0.#} ms（排序 {2}，显示静态数据表={3}）。",
            ordered.Length, elapsed.TotalMilliseconds, query.Sort, query.ShowStaticTables);
        return ordered;
    }

    /// <summary>逐条判据（顺序与原 LINQ 谓词链一致：廉价判据在前、IO 判据在后）。
    /// <para><b>公开是刻意的</b>：资源列表改成「按页从索引查询」之后，页里的记录由
    /// <c>AssetCatalog</c> 构造，但判据**必须还是这一份** —— 复制一份就等于
    /// 「分页列表」与「导出/构建/旧搜索」对同一条资源给出不同结论，而这种漂移
    /// 只会在用户那里以「少了一条/多了一条」的形式出现。<paramref name="text"/> 由调用方
    /// 预先 Trim（<see cref="Search(IEnumerable{AssetRecord}, AssetSearchQuery)"/> 就是这么做的）。</para></summary>
    public static bool Matches(AssetRecord asset, AssetSearchQuery query, string? text, StaticVerdictCache? staticVerdicts)
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
        // plan-08：静态数据 bundle 的资源默认不出现在资源工作台（由静态数据
        // 工作台专门编辑）；勾选「显示静态数据表」后照常出现。
        if (staticVerdicts is not null && IsStaticBundleAsset(asset, staticVerdicts)) return false;
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

    /// <summary>静态判定的记忆化容器：静态性主要是 **bundle 级**事实
    /// （元数据标记 / bundle 名 / 内层键），只有第三道判据与资源自身相关。
    /// 40 万条资产只对应上千个 bundle，逐条重算等于白烧。
    /// <para>按页查询（<c>AssetCatalog</c>）与全量搜索共用同一个缓存实例类型，
    /// 但**各自持有一个实例**：两者跑在不同的时间点，共享反而会把「上一批候选」
    /// 的判断带进这一批。</para></summary>
    public sealed class StaticVerdictCache
    {
        internal Dictionary<(string Bundle, string InnerKey), bool> BundleLevel { get; } = new();
        internal Dictionary<string, bool> ContainerLevel { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>是否为静态数据 bundle（static_s1_0_assets_all_*）内的资源。
    ///
    /// <para><b>两道判据</b>：① 扫描时按 catalog 写入的元数据标记；
    /// ② 兜底看 bundle 文件名 / 内层键（<see cref="StaticBundleLocator.LooksLikeStaticBundle"/>）。
    /// 只用 ① 是不够的 —— 标记是「catalog 可用且内层键命中」的产物，catalog 缺失 /
    /// 版本不符 / 旧索引缺列时就为假，于是资源工作台又把 static-data 的表全列出来。
    /// bundle 名是不依赖 catalog 的权威事实，因此兜底不会误判。</para>
    ///
    /// <para>前两道判据只取决于 bundle（+ 内层键），第三道判据取决于资源自身的
    /// 容器路径；两段都按 <paramref name="cache"/> 记忆化 —— 这是搜索热路径上
    /// 每资源一次的字典查找 + 文件名分配 + 前缀匹配，40 万条资产下不可忽略。</para></summary>
    private static bool IsStaticBundleAsset(AssetRecord asset, StaticVerdictCache cache)
    {
        // ① 元数据标记是**每个资源自己的**事实（不能按 bundle 记忆化：同一 bundle 里
        //    只要有一个资源带标记就把整包判成静态，会把普通资源一起藏掉）。
        if (asset.Metadata.TryGetValue(UnityCacheScanService.StaticBundleMetadataKey, out var flag)
            && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
            return true;

        // ② bundle 名 / 内层键兜底：同一 bundle（= 同一内层键）的结论必然相同，按 bundle 记忆化。
        var innerKey = asset.Metadata.TryGetValue("cacheInner", out var inner) ? inner : string.Empty;
        var bundle = asset.Bundle ?? string.Empty;
        if (!cache.BundleLevel.TryGetValue((bundle, innerKey), out var bundleStatic))
        {
            bundleStatic = StaticBundleLocator.LooksLikeStaticBundle(asset.Bundle)
                || StaticBundleLocator.LooksLikeStaticBundle(innerKey);
            cache.BundleLevel[(bundle, innerKey)] = bundleStatic;
            // 兜底生效（元数据标记未命中）意味着扫描时的 catalog 判定缺失：这条必须留痕
            // （按 bundle 记一次即可，逐资源重复同一条告警对排查没有增量信息）。
            if (bundleStatic)
                Log.Warn("静态判定：元数据标记未命中，由 bundle 名/内层键兜底判定为静态数据（bundle「{0}」，cacheInner「{1}」）——catalog 可能缺失/版本不符/旧索引缺列。",
                    asset.Bundle ?? "-", innerKey);
        }
        if (bundleStatic) return true;

        // ③ 资源自身的游戏内容器路径（不依赖 catalog / bundle 名）：缓存目录名只是内层
        //    内容哈希，游戏更新换键后旧静态 bundle 会以裸哈希目录留在缓存里，前两道判据
        //    同时失效（真实数据：62d6e466… 旧版本残留）。判定只取决于容器路径字符串，
        //    用元数据原始值当记忆化键，避免为查缓存先分配一次 Trim 后的字符串。
        if (!asset.Metadata.TryGetValue("containerEntry", out var rawEntry) || string.IsNullOrEmpty(rawEntry)) return false;
        if (!cache.ContainerLevel.TryGetValue(rawEntry, out var byContainerPath))
        {
            var containerEntry = AssetDisplay.ContainerEntryPath(asset);
            byContainerPath = StaticBundleLocator.LooksLikeStaticTablePath(containerEntry);
            cache.ContainerLevel[rawEntry] = byContainerPath;
            if (byContainerPath)
                Log.Warn("静态判定：元数据与 bundle 名均未命中，由资源路径兜底判定为静态数据（容器路径「{0}」，bundle「{1}」）——缓存里存在旧版本静态 bundle。",
                    containerEntry, asset.Bundle ?? "-");
            else if (Log.IsTraceEnabled)
                Log.Trace("静态判定结论：非静态（bundle 名「{0}」、内层键「{1}」、容器路径「{2}」均不匹配）。",
                    asset.Bundle ?? "-", innerKey, containerEntry);
        }
        return byContainerPath;
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
