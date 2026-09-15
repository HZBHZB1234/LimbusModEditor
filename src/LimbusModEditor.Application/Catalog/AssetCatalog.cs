using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using NLog;
using UnityCachePageRow = LimbusModEditor.Application.Scanning.UnityCacheSqliteIndexStore.UnityCachePageRow;

namespace LimbusModEditor.Application.Catalog;

/// <summary>按页查询的一页结果。</summary>
/// <param name="Items">本页记录（顺序 = 当前排序）。</param>
/// <param name="TotalCount">**命中总数**（不是总数、也不只是本页数）—— 页码条与「共 N 条」要它。</param>
/// <param name="Offset">本页起始下标。</param>
/// <param name="Take">请求的页大小。</param>
public sealed record AssetCatalogPage(IReadOnlyList<AssetRecord> Items, long TotalCount, long Offset, int Take)
{
    /// <summary>总页数（<paramref name="Take"/> 为 0 时是 1）。</summary>
    public long PageCount(int take) => take <= 0 ? 1 : Math.Max(1, (TotalCount + take - 1) / take);
}

/// <summary>某个 <see cref="AssetRecord.LogicalPath"/> 在**目录全序**里的位置。
/// <para><paramref name="Rank"/> 是全局名次（0 基），**只有在「无任何筛选」的视图里**
/// 才等于列表的页下标（<c>Rank / 页大小</c>）—— 默认视图会隐藏静态数据表，两个数就不一样了。
/// 要「翻到某一页并选中」请用 <see cref="AssetCatalog.IndexIn"/>。</para>
/// <para>用它而不是把记录本身塞回去：定位的用途是「翻页 + 选中」，而选中靠 LogicalPath
/// （<c>AssetRecord.AssetId</c> 每次回灌都会变）。</para></summary>
public sealed record AssetCatalogLocation(int Rank, string LogicalPath);

/// <summary>
/// 项目态覆盖的取值来源：按 LogicalPath 给出**项目里已经存在的**那条记录。
/// <para>为什么需要它：索引库只有扫描时的事实（类型、大小、容器、bundle 归属），
/// 而资源列表要显示的事实里有一半属于**项目态** —— 编辑状态、已实体化的本地副本路径、
/// 替换文件、静态标记的最终结论。索引 + 项目态覆盖才是列表看到的记录。</para>
/// <para>实现要保证「查不到」是廉价且正确的：S3a 阶段 <see cref="ModProject.Assets"/> 仍
/// 全量常驻，所以现成的实现会建一份全量字典；等集合收窄成「只含项目态记录」之后，
/// 同一份实现自然变轻，调用点不用改。</para>
/// </summary>
public interface IAssetStateSource
{
    /// <summary>取项目态记录；项目里没有这条（纯扫描得到的引用）时返回 null。</summary>
    AssetRecord? Find(string logicalPath);
}

/// <summary>项目态来源 = 一个资产记录集合（当前即 <see cref="ModProject.Assets"/>）。</summary>
public sealed class ProjectAssetStateSource : IAssetStateSource
{
    private readonly IReadOnlyCollection<AssetRecord> _records;
    private Dictionary<string, AssetRecord>? _byPath;

    public ProjectAssetStateSource(IEnumerable<AssetRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records as IReadOnlyCollection<AssetRecord> ?? records.ToList();
    }

    public AssetRecord? Find(string logicalPath)
        => Index().TryGetValue(logicalPath, out var record) ? record : null;

    /// <summary>按 LogicalPath 建索引。**先出现的优先**（与
    /// <c>RehydrateFromIndexAsync</c> 的「既有记录优先」同一口径）—— 重复的 LogicalPath
    /// 只可能来自损坏的项目文件，此时保留先读到的那条比保留后读到的更接近原意。</summary>
    private Dictionary<string, AssetRecord> Index()
    {
        if (_byPath is not null) return _byPath;
        var map = new Dictionary<string, AssetRecord>(_records.Count, StringComparer.Ordinal);
        foreach (var record in _records)
        {
            if (string.IsNullOrEmpty(record.LogicalPath)) continue;
            map.TryAdd(record.LogicalPath, record);
        }
        return _byPath = map;
    }
}

/// <summary>没有任何项目态来源（索引即全部事实）。</summary>
public sealed class EmptyAssetStateSource : IAssetStateSource
{
    public static readonly EmptyAssetStateSource Instance = new();
    public AssetRecord? Find(string logicalPath) => null;
}

/// <summary>
/// 资源目录的**按页查询门面**（Application 层，无 WPF 依赖）。
///
/// <para><b>要解决的问题</b>：此前列表 = 「把 1,275,623 条 <see cref="AssetRecord"/> 全量常驻
/// 并全量过滤排序」。托管堆 1,028 字节/条 → 1,250.9 MiB，一次强制 gen2 就是 872 ms 界面冻结。
/// 这里把它换成「下推筛选 → 只对候选集排序 → 只物化要显示的那一页」。</para>
///
/// <para><b>口径</b>（这是本类存在的全部意义，改动前请先看这三条）：</para>
/// <list type="number">
/// <item>判据只有一份：<see cref="AssetSearchService.Matches"/>。分页列表与旧的全量搜索、
/// 导出/构建消费者对同一条资源必须给出同一个结论。</item>
/// <item>排序只有一份：<see cref="AssetSearchService.BuildSortKey"/> /
/// <see cref="AssetSearchService.CompareSortKeys"/>。其中「按名称」直接使用目录名次
/// （<c>catalog_rank</c> 的 <c>r</c>）—— 名次本身就是目录全序，所以**不必排序、也不分配排序键**，
/// 而且与页深无关。</item>
/// <item>重建记录只有一份：<see cref="UnityCacheScanService.BuildReferenceRecord"/>，
/// 与扫描写入项目时是同一个函数。</item>
/// </list>
///
/// <para><b>返回的是「视图记录」，不是项目态记录</b>：它们是按索引现算出来的副本。
/// 要改资源必须走编辑服务（按 <see cref="AssetRecord.LogicalPath"/> 落到项目态），
/// 直接改这些副本不会有任何效果。唯一被继承过去的身份字段是
/// <see cref="AssetRecord.AssetId"/>（项目态有记录时取它自己的）—— 因为
/// <c>EditOperation.AssetId</c> 是持久化在 <c>.lmeproj</c> 里的，不能因为
/// 「重新查了一次列表」就对不上。</para>
/// </summary>
public sealed class AssetCatalog
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly UnityCacheSqliteIndexStore _store;
    private readonly IAssetStateSource _state;

    public AssetCatalog(UnityCacheSqliteIndexStore store, IAssetStateSource state)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>项目态来源 = 项目里的资产集合（见 <see cref="ProjectAssetStateSource"/>）。</summary>
    public AssetCatalog(UnityCacheSqliteIndexStore store, ModProject project)
        : this(store, new ProjectAssetStateSource(
            (project ?? throw new ArgumentNullException(nameof(project))).Assets))
    {
    }

    /// <summary>命中总数（不取条目）。「共 N 条」与页码条用。</summary>
    public long Count(AssetSearchQuery query) => Page(query, 0, 0).TotalCount;

    /// <summary>
    /// 取一页。<paramref name="offset"/> 是**命中集内**的下标（不是名次，也不是跳过条数）。
    /// <para>每次都从候选集头部重新走一遍判据 —— 这是「页深无关」的代价换来的：
    /// 不排序全表、不用 <c>OFFSET</c> 翻页（末页实测 12.2 s），而候选集由 SQL 下推而来
    /// （默认视图只 5.1 万行）。</para>
    /// </summary>
    public AssetCatalogPage Page(AssetSearchQuery query, long offset = 0, int take = 200)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        var matches = CollectMatches(query);
        var total = matches.Count;
        if (take == 0 || offset >= total) return new([], total, offset, take);
        var count = (int)Math.Min(take, total - offset);
        var ranks = new int[count];
        for (var i = 0; i < count; i++) ranks[i] = matches[(int)offset + i].Rank;
        var items = ResolveRanks(ranks);
        Log.Debug("资源目录分页：命中 {0:N0} 条，返回第 {1} 页（下标 {2}，{3} 条，排序 {4}）",
            total, take <= 0 ? 0 : offset / take, offset, items.Count, query.Sort);
        return new(items, total, offset, take);
    }

    /// <summary>按 LogicalPath 定位（跑不进索引的路径 —— 导入的旧式资源 —— 返回 null）。
    /// <para>给的是**全局名次**。要在列表里翻到它，用 <see cref="IndexIn"/> —— 只要查询带
    /// 任何筛选（默认视图也算，它隐藏静态数据表），全局名次就**不等于**命中集里的下标。</para>
    /// </summary>
    public AssetCatalogLocation? Locate(string logicalPath)
    {
        if (string.IsNullOrWhiteSpace(logicalPath)) return null;
        var rank = _store.FindRank(logicalPath);
        return rank < 0 ? null : new AssetCatalogLocation(rank, logicalPath);
    }

    /// <summary>
    /// 在**给定筛选与排序下**它是命中的第几条（0 基）；没命中返回 null。
    /// <para>这是「翻到那一页并选中」真正要的那个数：列表下标是**命中集内**的下标，
    /// 带筛选时与全局名次不是一个数。开销 = 一次完整判据扫描，与取页同级。</para>
    /// </summary>
    public long? IndexIn(AssetSearchQuery query, string logicalPath)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(logicalPath)) return null;
        var rank = _store.FindRank(logicalPath);
        if (rank < 0) return null;
        var matches = CollectMatches(query);
        for (var i = 0; i < matches.Count; i++)
            if (matches[i].Rank == rank) return i;
        return null;
    }

    /// <summary>
    /// 按 LogicalPath 批量取记录（**保持输入顺序**，查不到的跳过）。
    /// <para>用途：列表/树选中、深链跳转、导出前把已编辑子集换回完整记录 —— 这些场景手里
    /// 只有 LogicalPath（跨「重新物化」保鲜的唯一稳定键），需要把记录拿回来。</para>
    /// </summary>
    public IReadOnlyList<AssetRecord> Resolve(IReadOnlyList<string> logicalPaths)
    {
        ArgumentNullException.ThrowIfNull(logicalPaths);
        var ranks = new List<int>(logicalPaths.Count);
        foreach (var path in logicalPaths)
        {
            var rank = _store.FindRank(path);
            if (rank >= 0) ranks.Add(rank);
        }
        return ResolveRanks(ranks);
    }

    /// <summary>按名次取记录（输入顺序保持；同一名次出现多次时返回同一对象）。</summary>
    public IReadOnlyList<AssetRecord> ResolveRanks(IReadOnlyList<int> ranks)
    {
        ArgumentNullException.ThrowIfNull(ranks);
        if (ranks.Count == 0) return [];
        var byRank = new Dictionary<int, AssetRecord>(ranks.Count);
        var distinct = new List<int>(ranks.Count);
        var seen = new HashSet<int>();
        foreach (var rank in ranks)
            if (seen.Add(rank)) distinct.Add(rank);
        foreach (var row in _store.StreamByRanks(distinct)) byRank[row.Rank] = Materialize(row);
        var result = new List<AssetRecord>(ranks.Count);
        foreach (var rank in ranks)
            if (byRank.TryGetValue(rank, out var record)) result.Add(record);
        return result;
    }

    /// <summary>命中集（**已按当前排序排好**，只留名次与排序键）。</summary>
    private readonly record struct CatalogMatch(int Rank, AssetSearchService.AssetSortKey Key);

    /// <summary>
    /// 走一遍「下推候选 → 复核判据 → 排序」，返回排好序的命中名次。
    /// <para>刻意**不留 AssetRecord**：命中集可能上万条而记录是 1,028 字节/条，
    /// 留记录就等于把「全量常驻」原样搬回来。真正要显示的只有最后一页。</para>
    /// </summary>
    private List<CatalogMatch> CollectMatches(AssetSearchQuery query)
    {
        if (query.MinSize is < 0) throw new ArgumentException("MinSize 不能为负。", nameof(query));
        if (query.MaxSize is < 0) throw new ArgumentException("MaxSize 不能为负（负值曾经静默关闭上限）。", nameof(query));
        var text = query.Text?.Trim();
        var ranks = _store.ReadCandidateRanks(new UnityCacheIndexFilter(
            Type: query.Type,
            PathId: query.UnityPathId,
            TypeId: query.UnityTypeId,
            MinSize: query.MinSize,
            MaxSize: query.MaxSize,
            Container: query.Container,
            HasContainerEntry: query.HasContainerEntry,
            Text: text));
        var verdicts = query.ShowStaticTables ? null : new AssetSearchService.StaticVerdictCache();
        // 「按名称」的排序就是目录全序本身（catalog_rank 的 r 就是按它算出来的），
        // 所以这条路径既不排序、也不算排序键 —— 省掉每条一次的显示路径分配。
        var byName = query.Sort == AssetSortKind.Name;
        var matches = new List<CatalogMatch>(Math.Min(ranks.Count, 65536));
        foreach (var row in _store.StreamByRanks(ranks))
        {
            var record = Materialize(row);
            if (!AssetSearchService.Matches(record, query, text, verdicts)) continue;
            matches.Add(new CatalogMatch(row.Rank, byName ? default : AssetSearchService.BuildSortKey(record)));
        }
        if (!byName)
        {
            var sort = query.Sort;
            matches.Sort((a, b) => AssetSearchService.CompareSortKeys(a.Key, b.Key, sort));
        }
        return matches;
    }

    /// <summary>
    /// 一条索引行 → 列表看到的记录：**索引事实 + 项目态覆盖**。
    /// <para>LogicalPath 只用一份（先拼出来既能当覆盖查找的键，又直接交给构造器），
    /// 因为它既是稳定身份键、也是显示路径与检索的一部分，拼错一次就是三条记录对不上。</para>
    /// </summary>
    private AssetRecord Materialize(UnityCachePageRow row)
    {
        var indexed = row.Row;
        var logicalPath = AssetDisplay.CacheRowLogicalPath(
            row.Bundle.Outer, row.Bundle.Inner, indexed.Container, indexed.PathId, indexed.TypeId);
        var overlay = _state.Find(logicalPath);
        var record = UnityCacheScanService.BuildReferenceRecord(row.Bundle, indexed, logicalPath, overlay?.AssetId);
        return overlay is null ? record : MergeProjectState(record, overlay);
    }

    /// <summary>
    /// 把项目态叠到索引事实上。规则与 <c>UnityCacheScanService</c> 扫描合并的
    /// 「已实体化」分支同向：
    /// <list type="bullet">
    /// <item>编辑状态与改动哈希是纯项目态 → 直接取项目的。</item>
    /// <item>已实体化（元数据里没有 <c>reference=true</c>）时，<c>SourcePath</c> 指本地副本，
    /// 项目才是权威；同时摘掉 <c>reference</c> 标记（与实体化时的写法一致）。</item>
    /// <item>元数据整体以项目记录为准：索引里没有的（<c>replacementPath</c> 等）要带过来，
    /// 而索引里有的那些，项目记录本来就是扫描写进去的同一份 —— 唯一可能不同的是
    /// 扫描后来**清除**过的静态标记，那种情况下保留项目的才是对的。</item>
    /// </list>
    /// </summary>
    public static AssetRecord MergeProjectState(AssetRecord indexed, AssetRecord state)
    {
        ArgumentNullException.ThrowIfNull(indexed);
        ArgumentNullException.ThrowIfNull(state);
        indexed.EditState = state.EditState;
        indexed.OriginalHash = state.OriginalHash;
        indexed.ModifiedHash = state.ModifiedHash;
        var materialized = !(state.Metadata.TryGetValue("reference", out var reference)
            && string.Equals(reference, "true", StringComparison.OrdinalIgnoreCase));
        if (materialized)
        {
            indexed.SourcePath = state.SourcePath;
            indexed.Metadata.Remove("reference");
        }
        foreach (var pair in state.Metadata) indexed.Metadata[pair.Key] = pair.Value;
        return indexed;
    }
}
