using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Relations.Authority;
using NLog;

namespace LimbusModEditor.Application.Relations;

/// <summary>
/// 「未绑定 Spine → 维基页面」的自动接入服务（<b>方案 A：挂到既有页面</b>）。
///
/// <para><b>为什么是方案 A 而不是新建独立页面</b>：未绑定的 Spine 挂点有 <b>798</b> 个，
/// 其中绝大多数的宿主（人格 / 敌人 / 异想体 / E.G.O 饰品页）<b>本来就已经存在</b> ——
/// 给它们各建一个 <c>spine:&lt;骨架名&gt;</c> 独立页会凭空造出几百个只有一条绑定的零散页
/// （导航爆炸、没有正文、无法维护）；而挂到既有页则是「这几百条素材本来就属于这些角色」的
/// 事实补登记。匹配不上的少数（实测 52 条）<b>不硬塞</b>给某个角色，
/// 由 <see cref="SpineCatalogGroups"/> 的目录归类如实呈现为「未归类」。</para>
///
/// <para><b>归属怎么定（这是本服务唯一关键的设计决定）</b>：一律走
/// <c>relation-index.db</c> 的 <c>subjects_by_ref</c> 反查索引 —— 那是既有分析器
/// （<see cref="RelationStore.PersistGraph"/>）从 <c>links</c> 派生的<b>权威</b>归属，
/// 不是本服务现推的。</para>
///
/// <para><b>为什么不用「文件名数字前缀 = 页面 id」这条看着很顺的规则</b>：
/// 拿库里已有的 174 条 ground-truth Spine 绑定交叉验证，该规则
/// <b>50/174 与真值冲突</b>（例：<c>abnormality:1080</c> 实际持有 <c>10802_gacksung</c>，
/// 前缀 10802 ≠ 1080；<c>ego_gift:1011</c> 持有 6 个不同的 <c>10xxx_gacksung</c>），
/// 且 <c>11108</c>/<c>11209</c> 同时命中 <c>enemy:*</c> 与 <c>persona:*</c>（歧义）。
/// 覆盖只有 564/798 且会<b>编造关系</b>；反查表覆盖 746/798 且与真值零冲突。</para>
///
/// <para><b>幂等</b>：条目 / 绑定 id 由 <see cref="WikiStableIds"/> 按内容键算成稳定 id，
/// 重跑命中同一行 → UPSERT，不产生重复。</para>
///
/// <para><b>绝不覆盖用户修订</b>：写入走 <see cref="WikiPageStore.SaveGeneratedPage"/>，
/// 它把 <c>source = 'revised'</c> 的条目整条跳过（连绑定一起）。</para>
///
/// <para><b>限流 / 可中断 / 有进度</b>：按 <b>页面</b> 分批，批间让出并检查取消；
/// 每批报一次 <see cref="WikiGenerationProgress"/>。整个流程<b>不解任何 bundle</b>
/// （只登记绑定键），所以它是秒级而不是上轮报告里担心的几十分钟。</para>
/// </summary>
public sealed class SpineWikiBindingService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>分节 id：复用既有分节表里的「Spine 与动画」。</summary>
    public const string SpineSectionId = WikiSectionTables.SpineAndAnimation;

    /// <summary>分节标题：与既有分节表一致（<see cref="WikiSectionTables.SpineAndAnimation"/> 的中文名）。</summary>
    public const string SpineSectionTitle = "Spine 与动画";

    /// <summary>绑定类别：<see cref="RelationKind.Spine"/> 的名字，与既有 134 条口径一致。</summary>
    public const string SpineKind = nameof(RelationKind.Spine);

    /// <summary>每批处理的页面数（限流粒度：批间让出，避免长时间占满）。</summary>
    public const int PageBatchSize = 25;

    private readonly AppEnvironment _env;

    public SpineWikiBindingService(AppEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        _env = env;
    }

    /// <summary>一次接入的结果计数。</summary>
    /// <param name="HookPoints">全部 Spine 挂点数（已绑定 + 未绑定）。</param>
    /// <param name="AlreadyBound">已被既有页面绑定的挂点数（本轮不动它们）。</param>
    /// <param name="Unbound">未被任何页面绑定的挂点数（本轮要接的那批）。</param>
    /// <param name="Attached">成功挂到既有页面的挂点数（去重后）。</param>
    /// <param name="AttachedBindings">实际写入的绑定行数（一个挂点可被多个页面持有）。</param>
    /// <param name="PagesUpdated">被更新的页面数。</param>
    /// <param name="BundleMissing">bundle 文件此刻不在本机的挂点数（Unity 缓存被清，如实跳过）。</param>
    /// <param name="Uncategorized">在反查索引里没有归属、因而<b>不硬塞</b>给任何角色的挂点数。</param>
    /// <param name="TargetMissing">反查命中的对象在维基里没有页面、因而不落库的挂点数。</param>
    /// <param name="RevisedPreserved">因条目被用户修订而整条跳过的绑定数。</param>
    /// <param name="Cancelled">是否因取消而提前结束（已写入的部分<b>保留</b>，重跑幂等续上）。</param>
    public sealed record SpineBindingResult(
        int HookPoints,
        int AlreadyBound,
        int Unbound,
        int Attached,
        int AttachedBindings,
        int PagesUpdated,
        int BundleMissing,
        int Uncategorized,
        int TargetMissing,
        int RevisedPreserved,
        bool Cancelled)
    {
        /// <summary>已接入比例（对「未绑定」口径；分母为 0 时给 100）。</summary>
        public double Coverage => Unbound <= 0 ? 100d : Attached * 100d / Unbound;

        /// <summary>中文摘要（进度 / 日志 / 结果面板用）。</summary>
        public string Describe()
            => $"未绑定 Spine {Unbound} 个：接入 {Attached} 个（{Coverage:0.0}%，绑定行 {AttachedBindings}，页面 {PagesUpdated}）· " +
               $"未归类 {Uncategorized} · 目标页缺失 {TargetMissing} · bundle 不在本机 {BundleMissing}" +
               (RevisedPreserved > 0 ? $" · 保留修订 {RevisedPreserved}" : string.Empty) +
               (Cancelled ? " · 已取消（已写入部分保留）" : string.Empty);
    }

    /// <summary>
    /// 把可归属的未绑定 Spine 挂到既有页面的「Spine 与动画」分节。
    ///
    /// <para>只 <b>登记绑定键</b>（<c>ref_key</c> = 容器路径），<b>不解素材、不落地址</b>：
    /// 三件套地址由前端经 <c>spine.resolve</c> 按需取（后端有结果缓存）。
    /// 这正是「818 条要解十几分钟」这个性能问题的答案 —— 生成侧一次都不解。</para>
    /// </summary>
    public SpineBindingResult Attach(
        WikiPageStore store,
        IReadOnlyList<SpineData.SpineCatalogEntry> catalog,
        IProgress<WikiGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);

        // ── ① 既有绑定：哪些挂点已经被页面持有（这些一个都不动） ──────────
        var alreadyBound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unbound = new List<SpineData.SpineCatalogEntry>(catalog.Count);
        foreach (var entry in catalog)
        {
            if (entry.BoundPageCount > 0) alreadyBound.Add(entry.RefKey);
            else unbound.Add(entry);
        }

        // ── ② 归属：一次读完整张反查索引，内存里查（不逐键往返） ──────────
        var byRef = new RelationStore(_env.CacheDirectory).ReadSubjectsByRefMap();

        // 既有页面 id 集合：反查命中的对象只有在维基里真有页时才落绑定
        // （不凭 id 造页）。一次读出来，避免逐条 ReadPage 往返。
        var existingPages = new HashSet<string>(
            store.ReadPages().Select(p => p.PageId), StringComparer.Ordinal);

        // ── ③ 按目标页面归组 ────────────────────────────────────────────
        var plan = new Dictionary<string, List<SpineData.SpineCatalogEntry>>(StringComparer.Ordinal);
        var bundleMissing = 0;
        var uncategorized = 0;
        var targetMissing = 0;

        foreach (var entry in unbound)
        {
            if (!entry.BundlePresent) bundleMissing++;

            if (!byRef.TryGetValue(entry.RefKey, out var owners) || owners.Count == 0)
            {
                uncategorized++;   // 反查索引里没有归属 → 不硬塞给任何角色（见类型注释）
                continue;
            }

            var placed = false;
            var missed = 0;
            foreach (var owner in owners)
            {
                if (!existingPages.Contains(owner))
                {
                    missed++;          // 反查命中但维基里没有这个页面 → 不凭 id 造页
                    continue;
                }
                if (!plan.TryGetValue(owner, out var list))
                {
                    list = [];
                    plan[owner] = list;
                }
                list.Add(entry);
                placed = true;
            }

            if (placed) continue;

            // 一个页面都没挂上：区分「索引里没有归属」与「归属指向的页不存在」——
            // 前者是数据事实，后者只是维基还没生成那一页，两种如实分开计数。
            if (missed > 0) targetMissing++;
            else uncategorized++;
        }

        // ── ④ 逐页写入（按批限流 + 可中断 + 进度） ──────────────────────
        var pages = plan.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var attached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attachedBindings = 0;
        var pagesUpdated = 0;
        var revisedPreserved = 0;
        var cancelled = false;

        progress?.Report(new WikiGenerationProgress("spine", 0, pages.Count,
            $"待接入的 Spine 挂点 {unbound.Count} 个，分布在 {pages.Count} 个既有页面…"));

        // 进循环之前先看一次：已取消时（哪怕一个页面都没有）也要如实报「已取消」，
        // 不能因为 pages 为空就报成「跑完了、0 接入」。
        if (cancellationToken.IsCancellationRequested) cancelled = true;

        for (var i = 0; i < pages.Count && !cancelled; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var pageId = pages[i];
            var entries = plan[pageId];
            var detail = MergeIntoPage(store, pageId, entries);
            if (detail is not null)
            {
                var save = store.SaveGeneratedPage(detail);
                // 只计「本服务写的 Spine 绑定」，不能拿 save.Bindings ——
                // 那是该页全部绑定的重写总数（含既有语音/图像等），会把它当成新增量。
                attachedBindings += save.SpineBindings;
                revisedPreserved += save.RevisedPreserved;
                pagesUpdated++;
                foreach (var entry in entries) attached.Add(entry.RefKey);
            }

            if ((i + 1) % PageBatchSize == 0 || i + 1 == pages.Count)
            {
                progress?.Report(new WikiGenerationProgress("spine", i + 1, pages.Count,
                    $"正在接入 Spine 绑定：{i + 1}/{pages.Count} 个页面（已接入 {attached.Count} 个挂点）"));
                // 批间让出一次，长任务不至于把消息循环堵死（也让取消能被及时看到）。
                Thread.Yield();
            }
        }

        var result = new SpineBindingResult(
            catalog.Count, alreadyBound.Count, unbound.Count, attached.Count, attachedBindings,
            pagesUpdated, bundleMissing, uncategorized, targetMissing, revisedPreserved, cancelled);

        Log.Info("Spine 自动接入：{0}", result.Describe());
        if (uncategorized > 0)
            Log.Info("其中 {0} 个挂点在关联索引里没有归属，未硬塞给任何角色（保留为「未归类」）。", uncategorized);
        if (bundleMissing > 0)
            Log.Info("其中 {0} 个挂点的 bundle 已被 Unity 缓存清掉，如实跳过（非代码可补）。", bundleMissing);

        progress?.Report(new WikiGenerationProgress("spine", pages.Count, pages.Count, result.Describe()));
        return result;
    }

    /// <summary>
    /// 把一批 Spine 挂点合并成「该页面的一次生成结果」：只带「Spine 与动画」这一个分节。
    ///
    /// <para><b>不会吃掉页面上的其它分节</b>：<see cref="WikiPageStore.SaveGeneratedPage"/>
    /// 只会删「本轮不再生成的二级页面」，所以这里<b>必须</b>把该页已有的其它分节原样带回来
    /// （否则一次接入就会把文本 / 语音 / 立绘全删掉）。这是本方法存在的唯一理由，
    /// 也是本服务最容易写错的地方。</para>
    /// </summary>
    private static WikiPageDetail? MergeIntoPage(
        WikiPageStore store, string pageId, IReadOnlyList<SpineData.SpineCatalogEntry> entries)
    {
        var page = store.ReadPage(pageId);
        if (page is null) return null;

        // ① 该页已有的分节整棵读回来（原样带过，保住其它内容）
        var subPages = new List<WikiSubPageDetail>();
        foreach (var sub in store.ReadSubPages(pageId))
        {
            // 「Spine 与动画」这一节由下面重建；其余分节原样保留。
            // （必须带过，否则 SaveGeneratedPage 会把它们当「本轮不再生成」删掉。）
            if (string.Equals(sub.Title, SpineSectionTitle, StringComparison.Ordinal)) continue;

            var existingEntries = store.ReadEntries(sub.SubPageId)
                .Select(e => new WikiEntryDetail(e, store.ReadBindings(e.EntryId)))
                .ToList();
            subPages.Add(new WikiSubPageDetail(sub, existingEntries));
        }

        // ② 「Spine 与动画」分节：按内容键算稳定 id（幂等）
        var spineSubPageId = WikiStableIds.Of(pageId, SpineSectionTitle, SpineSectionId);
        var spineEntries = new List<WikiEntryDetail>(entries.Count);

        var sorted = entries
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.RefKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var index = 0; index < sorted.Count; index++)
        {
            var entry = sorted[index];
            var entryId = WikiStableIds.Of(spineSubPageId, entry.RefKey);
            var status = entry.BundlePresent ? "已解析" : "bundle 不在本机";
            var wikiEntry = new WikiEntry(
                entryId, spineSubPageId, entry.Name,
                $"{entry.Group} · {status}（三件套在打开时按需取）", index)
            {
                Source = WikiEntrySources.Auto,
                // 归属来自关联索引的反查表（既有分析器产出），不是本服务现推的。
                Authority = nameof(AuthoritySource.Unknown),
                Confidence = nameof(ConfidenceLevel.None),
                WritableSource = nameof(WritableSourceKind.Unknown),
                SourceDetail = $"relation-index.db subjects_by_ref（Spine 挂点 {entry.RefKey}）",
            };

            var binding = new WikiResourceBinding(
                WikiStableIds.Of(entryId, entry.RefKey),
                entryId,
                entry.RefKey,
                SpineKind,
                entry.Name,
                0)
            {
                // 深链走既有口径（与 relation 侧一致），前端据此能跳回资源。
                DeepLink = entry.RefKey,
                PreviewText = entry.BundlePresent ? null : "bundle 文件已被 Unity 缓存清掉，三件套取不到。",
                MediaKind = "spine",
            };

            spineEntries.Add(new WikiEntryDetail(wikiEntry, [binding]));
        }

        var sections = new List<WikiSubPageDetail>(subPages.Count + 1);
        if (spineEntries.Count > 0)
        {
            sections.Add(new WikiSubPageDetail(
                new WikiSubPage(spineSubPageId, pageId, SpineSectionTitle, 0),
                spineEntries));
        }
        sections.AddRange(subPages);

        // 重排 sort_order：按分节表定义的顺序（不猜），未定义的沿用其原有相对次序。
        var ordered = sections
            .Select((section, index) => (Section: section, Order: OrderOf(page.Category, section.SubPage.Title), Index: index))
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Index)
            .ToList();

        var rebuilt = new List<WikiSubPageDetail>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var sub = ordered[i].Section.SubPage;
            rebuilt.Add(new WikiSubPageDetail(
                new WikiSubPage(sub.SubPageId, pageId, sub.Title, i),
                ordered[i].Section.Entries));
        }

        return new WikiPageDetail(page, rebuilt);
    }

    /// <summary>
    /// 分节排序：按该类别分节表里定义的顺序；表里没有的排到最后（沿用原相对次序，不猜）。
    /// </summary>
    private static int OrderOf(string category, string sectionTitle)
    {
        foreach (var section in WikiSectionTables.ForCategory(category))
        {
            if (string.Equals(section.Title, sectionTitle, StringComparison.Ordinal))
                return section.Order;
        }
        return int.MaxValue;
    }
}
