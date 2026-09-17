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
/// 命中集里的一条 —— **只记录来源，不持有记录**（这才是分页能省下 1.2 GiB 的原因）。
/// <list type="bullet">
/// <item><see cref="Rank"/> ≥ 0：索引行，名次就是它的身份（目录全序里的位置）。</item>
/// <item><see cref="ExtraIndex"/> ≥ 0：只存在于项目里的资源
/// （见 <see cref="IAssetStateSource.ProjectOnly"/>），在补充集里的下标就是它的身份。</item>
/// </list>
/// <para>两个字段互斥，<see cref="IsIndex"/> 判定。大小只有 8 字节，命中集几万条也只有几百 KB ——
/// 这是「目录树能在不物化全部记录的前提下分层展开」的前提：树节点存的是区间，
/// 区间里装的就是这种 8 字节的条目。</para>
/// </summary>
public readonly record struct AssetCatalogEntry(int Rank, int ExtraIndex)
{
    /// <summary>索引行（名次 ≥ 0，补充集下标 -1）。</summary>
    public static AssetCatalogEntry FromRank(int rank) => new(rank, -1);

    /// <summary>只在项目里的资源（名次 -1，补充集下标 ≥ 0）。</summary>
    public static AssetCatalogEntry FromExtra(int index) => new(-1, index);

    public bool IsIndex => Rank >= 0;
}

/// <summary>
/// 项目态覆盖的取值来源：按 LogicalPath 给出**项目里已经存在的**那条记录。
/// <para>为什么需要它：索引库只有扫描时的事实（类型、大小、容器、bundle 归属、静态结论），
/// 而资源列表要显示的事实里有一半属于**项目态** —— 编辑状态、已实体化的本地副本路径、
/// 替换文件。索引 + 项目态覆盖才是列表看到的记录（静态结论以索引为权威，见
/// <see cref="AssetCatalog.MergeProjectState"/>）。</para>
/// <para>实现要保证「查不到」是廉价且正确的：S3a 阶段 <see cref="ModProject.Assets"/> 仍
/// 全量常驻，所以现成的实现会建一份全量字典；等集合收窄成「只含项目态记录」之后，
/// 同一份实现自然变轻，调用点不用改。</para>
/// </summary>
public interface IAssetStateSource
{
    /// <summary>取项目态记录；项目里没有这条（纯扫描得到的引用）时返回 null。</summary>
    AssetRecord? Find(string logicalPath);

    /// <summary>
    /// <b>只存在于项目里</b>的资源 —— 扫描索引库中没有它们，所以命中集必须由项目态自己贡献。
    ///
    /// <para>为什么不能省：索引行只覆盖「扫描到的缓存引用」，而项目还能自己长出资源 ——
    /// 音频工作台「提取样本到项目」（<c>BankWorkbenchPage</c>）、<c>ModImportService</c> 导入的
    /// 旧式模组（Carra / Lunartique / Rebank）都是往 <see cref="ModProject.Assets"/> 里加一条
    /// 索引里不存在的记录。<b>列表改走索引之后，这些资源不会因为「索引里没有」就消失</b> ——
    /// 它们由这里补进来，与索引命中按同一份排序键混排。</para>
    ///
    /// <para>实现要保证「取一次就够」：<c>ModProject.Assets</c> 超过百万条时逐次枚举太贵，
    /// 所以实现可以缓存结果，调用方（<see cref="AssetCatalog"/>）也只在每次查询时取一次引用。</para>
    /// </summary>
    IReadOnlyList<AssetRecord> ProjectOnly { get; }
}

/// <summary>项目态来源 = 一个资产记录集合（当前即 <see cref="ModProject.Assets"/>）。</summary>
public sealed class ProjectAssetStateSource : IAssetStateSource
{
    private readonly IReadOnlyCollection<AssetRecord> _records;
    private Dictionary<string, AssetRecord>? _byPath;
    private IReadOnlyList<AssetRecord>? _projectOnly;

    public ProjectAssetStateSource(IEnumerable<AssetRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records as IReadOnlyCollection<AssetRecord> ?? records.ToList();
    }

    public AssetRecord? Find(string logicalPath)
        => Index().TryGetValue(logicalPath, out var record) ? record : null;

    public IReadOnlyList<AssetRecord> ProjectOnly
    {
        get
        {
            Index();
            return _projectOnly!;
        }
    }

    /// <summary>按 LogicalPath 建索引，**顺便**把「不是索引引用」的那些摘出来。
    /// <para>两个结果同一趟算完是刻意的：判断「是不是索引引用」只多一次 Metadata 查找，
    /// 而这一趟本来就要遍历全部记录（真实项目百万级）。分开算等于把百万级遍历做两遍。</para>
    /// <para>「先出现的优先」与 <c>RehydrateFromIndexAsync</c> 同一口径 —— 重复的 LogicalPath
    /// 只可能来自损坏的项目文件，此时保留先读到的那条比保留后读到的更接近原意。</para>
    /// <para>补充集的判据用 <see cref="AssetDisplay.IsCacheReference"/>（元数据 <c>reference=true</c>
    /// 或存在 <c>cacheOuter</c>），与「显示路径取容器条目还是取 LogicalPath」是同一个判据 ——
    /// 两者必须同源，否则会出现「按索引口径算路径、却按项目口径决定要不要补」的错位。</para>
    /// </summary>
    private Dictionary<string, AssetRecord> Index()
    {
        if (_byPath is not null) return _byPath;
        var map = new Dictionary<string, AssetRecord>(_records.Count, StringComparer.Ordinal);
        var extras = new List<AssetRecord>();
        foreach (var record in _records)
        {
            if (string.IsNullOrEmpty(record.LogicalPath)) continue;
            if (!AssetDisplay.IsCacheReference(record)) extras.Add(record);
            map.TryAdd(record.LogicalPath, record);
        }
        _projectOnly = extras;
        return _byPath = map;
    }
}

/// <summary>没有任何项目态来源（索引即全部事实）。</summary>
public sealed class EmptyAssetStateSource : IAssetStateSource
{
    public static readonly EmptyAssetStateSource Instance = new();
    public AssetRecord? Find(string logicalPath) => null;
    public IReadOnlyList<AssetRecord> ProjectOnly => [];
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
/// <item>命中集有两个来源，缺一不可：索引库下推出来的行，以及
/// <see cref="IAssetStateSource.ProjectOnly"/>（导入的旧式模组、提取到项目的音频 ——
/// 扫描索引里根本没有它们）。**只查索引会让这些资源从列表里消失**。</item>
/// </list>
///
/// <para><b>返回的是「视图记录」，不是项目态记录</b>：它们是按索引现算出来的副本。
/// 要改资源必须走编辑服务（按 <see cref="AssetRecord.LogicalPath"/> 落到项目态），
/// 直接改这些副本不会有任何效果。唯一被继承过去的身份字段是
/// <see cref="AssetRecord.AssetId"/>（项目态有记录时取它自己的）—— 因为
/// <c>EditOperation.AssetId</c> 是持久化在 <c>.lmeproj</c> 里的，不能因为
/// 「重新查了一次列表」就对不上。补充集里的记录是项目态记录**本身**，不是副本。</para>
///
/// <para><b>目录树</b>不在这里，在 <see cref="AssetCatalogTree"/>：它复用本类的命中序列
/// （<see cref="MatchOrder"/>）与取数入口（<see cref="ResolveEntries"/>），
/// 只是把「一次取几万条记录」换成「按区间分层取」。</para>
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
        var slice = new List<AssetCatalogEntry>(count);
        for (var i = 0; i < count; i++) slice.Add(matches[(int)offset + i].Entry);
        var items = ResolveEntries(slice);
        Log.Debug("资源目录分页：命中 {0:N0} 条，返回第 {1} 页（下标 {2}，{3} 条，排序 {4}）",
            total, take <= 0 ? 0 : offset / take, offset, items.Count, query.Sort);
        return new(items, total, offset, take);
    }

    /// <summary>
    /// 命中集的**顺序本身**（按查询的排序排好），只返回 8 字节的来源标记。
    ///
    /// <para>用途是目录树：它的分层展开要「按目录序连续分组」，而它一秒也不能持有记录
    /// （命中集几万条 × 1,028 字节 = 几十 MB）。拿到这份序列之后，树的每个节点只需要
    /// 记一个区间 <c>[start, end)</c>，展开时才按区间分批物化 —— 见
    /// <see cref="AssetCatalogTree"/>。</para>
    ///
    /// <para>与 <see cref="Page"/> 走**同一个** <c>CollectMatches</c>：树的命中集与列表的
    /// 命中集因此不可能不一致（差别只在取多少个）。</para>
    /// </summary>
    public IReadOnlyList<AssetCatalogEntry> MatchOrder(AssetSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var matches = CollectMatches(query);
        var entries = new List<AssetCatalogEntry>(matches.Count);
        foreach (var match in matches) entries.Add(match.Entry);
        return entries;
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
    /// 按 Unity <b>容器路径</b>（<c>assets.container_entry</c>）取记录，返回<b>全部</b>匹配行。
    ///
    /// <para>与 <see cref="Locate"/> 的分工：后者吃的是 LogicalPath（维基绑定给的不是它），
    /// 而这里吃的是容器路径 —— 维基资源绑定、封面引用都是这个口径。</para>
    ///
    /// <para>同一路径通常回来 <b>两行</b>（Texture2D + Sprite），由调用方挑；
    /// 排序与取舍的口径见 <c>PresetWorkbenchService.Preferable</c>（Texture 必须赢）。</para>
    /// </summary>
    public IReadOnlyList<AssetRecord> FindByContainerEntry(string containerEntry)
    {
        if (string.IsNullOrWhiteSpace(containerEntry)) return [];
        var rows = _store.ReadByContainerEntry(containerEntry);
        if (rows.Count == 0) return [];
        var records = new List<AssetRecord>(rows.Count);
        foreach (var row in rows) records.Add(Materialize(row));
        return records;
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
        // 先问索引要名次：命中集里比较两个 int 比比较两条路径便宜得多。
        // 索引里没有（导入的旧式资源 / 提取到项目的音频）才退化为在补充集里按路径找。
        var rank = _store.FindRank(logicalPath);
        var extras = _state.ProjectOnly;
        var matches = CollectMatches(query);
        for (var i = 0; i < matches.Count; i++)
        {
            var entry = matches[i].Entry;
            if (rank >= 0)
            {
                if (entry.IsIndex && entry.Rank == rank) return i;
            }
            else if (!entry.IsIndex && (uint)entry.ExtraIndex < (uint)extras.Count
                     && string.Equals(extras[entry.ExtraIndex].LogicalPath, logicalPath,
                         StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
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
        var entries = new List<AssetCatalogEntry>(logicalPaths.Count);
        // 补充集**按需再取**：全部路径都在索引里时（导出/构建的常见情形）这一趟
        // 完全不必惊动项目态那百万级的一遍遍历。
        IReadOnlyList<AssetRecord>? extras = null;
        foreach (var path in logicalPaths)
        {
            var rank = _store.FindRank(path);
            if (rank >= 0)
            {
                entries.Add(AssetCatalogEntry.FromRank(rank));
                continue;
            }
            extras ??= _state.ProjectOnly;
            for (var i = 0; i < extras.Count; i++)
            {
                if (!string.Equals(extras[i].LogicalPath, path, StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(AssetCatalogEntry.FromExtra(i));
                break;
            }
        }
        return ResolveEntries(entries);
    }

    /// <summary>
    /// 按**来源标记**批量取记录（保持输入顺序，取不到的跳过；同一来源出现多次时返回同一对象）。
    /// <para>这是列表取一页、目录树取一层共用的取数入口 —— 两者手里的都只有
    /// <see cref="AssetCatalogEntry"/>，没有记录。索引行按名次一次性批量读
    /// （<c>StreamByRanks</c> 分批，压在 SQLite 参数上限之下），项目补充集直接按下标取。</para>
    /// </summary>
    public IReadOnlyList<AssetRecord> ResolveEntries(IReadOnlyList<AssetCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return [];
        var ranks = new List<int>(entries.Count);
        foreach (var entry in entries)
            if (entry.IsIndex) ranks.Add(entry.Rank);
        var byRank = new Dictionary<int, AssetRecord>(ranks.Count);
        if (ranks.Count > 0)
            foreach (var row in _store.StreamByRanks(ranks)) byRank[row.Rank] = Materialize(row);
        var extras = _state.ProjectOnly;
        var result = new List<AssetRecord>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.IsIndex)
            {
                if (byRank.TryGetValue(entry.Rank, out var record)) result.Add(record);
            }
            else if ((uint)entry.ExtraIndex < (uint)extras.Count)
            {
                result.Add(extras[entry.ExtraIndex]);
            }
        }
        return result;
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

    /// <summary>命中集里的一条：来源标记 + 排序键（只在需要排序时才填）。</summary>
    private readonly record struct CatalogMatch(AssetCatalogEntry Entry, AssetSearchService.AssetSortKey Key);

    /// <summary>
    /// 走一遍「下推候选 → 复核判据 → 排序」，返回排好序的命中集。
    /// <para>刻意**不留 AssetRecord**：命中集可能上万条而记录是 1,028 字节/条，
    /// 留记录就等于把「全量常驻」原样搬回来。真正要显示的只有最后一页。</para>
    /// <para>候选集来自两处：索引库下推出来的名次（SQL 判得动的条件全部交给它），
    /// 以及**只存在于项目里的资源**（<see cref="IAssetStateSource.ProjectOnly"/>）。
    /// 后者不必也不能下推 —— 它们不在名次表里，只能走同一份判据
    /// （<see cref="AssetSearchService.Matches"/>）在内存里复核。</para>
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
            Text: text,
            // 勾了「显示静态数据表」= 不加静态条件（全部可见）；没勾 = 只要非静态。
            // 下推与内存判据（AssetSearchService.Matches）用的是同一个结论，
            // 所以 SQL 这边不必留「超集」余量 —— 两边完全同义。
            IsStatic: query.ShowStaticTables ? null : false));
        var extras = _state.ProjectOnly;
        // 「按名称」的排序就是目录全序本身（catalog_rank 的 r 就是按它算出来的），
        // 所以没有补充集时这条路径既不排序、也不算排序键 —— 省掉每条一次的显示路径分配。
        // **有补充集就必须算键**：项目里的资源不在名次表里，只有靠同一份排序键才能与索引行混排。
        var needKeys = query.Sort != AssetSortKind.Name || extras.Count > 0;
        var matches = new List<CatalogMatch>(Math.Min(ranks.Count + extras.Count, 65536));
        foreach (var row in _store.StreamByRanks(ranks))
        {
            var record = Materialize(row);
            if (!AssetSearchService.Matches(record, query, text)) continue;
            matches.Add(new CatalogMatch(AssetCatalogEntry.FromRank(row.Rank),
                needKeys ? AssetSearchService.BuildSortKey(record) : default));
        }
        for (var i = 0; i < extras.Count; i++)
        {
            var record = extras[i];
            if (!AssetSearchService.Matches(record, query, text)) continue;
            matches.Add(new CatalogMatch(AssetCatalogEntry.FromExtra(i), AssetSearchService.BuildSortKey(record)));
        }
        if (needKeys)
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
    /// 而索引里有的那些，项目记录本来就是扫描写进去的同一份。</item>
    /// <item><b>唯一的例外是静态结论</b>（<c>staticBundle</c>）：它由**索引**当权威 ——
    /// 项目记录可能是几个月前保存的 <c>.lmeproj</c>（里面还写着老形态的 <c>"true"</c>，
    /// 或者带着一次性误判），而索引每次扫描/迁移都会重算。整体覆盖会让「列表按索引
    /// 隐藏静态表、预览按记录判静态」这种错位重新出现。</item>
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
        foreach (var pair in state.Metadata)
        {
            if (string.Equals(pair.Key, UnityCacheScanService.StaticBundleMetadataKey, StringComparison.Ordinal))
                continue;
            indexed.Metadata[pair.Key] = pair.Value;
        }
        return indexed;
    }
}
