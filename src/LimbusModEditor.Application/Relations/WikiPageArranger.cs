namespace LimbusModEditor.Application.Relations;

/// <summary>
/// 维基页面自动编排引擎：把本地数据分析产物（relation-index.db + 四源事实）
/// 按分节表与二级页拆分规则，组织成维基式页面结构。
///
/// <para><b>输入</b>：既有 <see cref="RelationGraph"/>（来自 relation-index.db），
/// 包含 subjects / links / xrefs。<b>不新造分析</b>。</para>
///
/// <para><b>输出</b>：<see cref="WikiPageDetail"/>，可直接落库（t12 的 wiki-pages.db）或前端展示。</para>
///
/// <para><b>禁止 id 规则猜测</b>：资源归属由权威来源决定（资源目录前缀、lang 的 Egos.json），
/// 不用「扫文件名里的 5 位数字窗口」之类脆弱推断。</para>
///
/// <para><b>幂等</b>：重复生成同一对象必须产生相同的页面结构（深链、排序、分节），
/// 不覆盖用户已修订内容（由 <see cref="WikiAutoGenerationService"/> 负责优先级判断）。</para>
///
/// <para><b>性能</b>：按需生成单个对象的页面结构，不一次性读入全量数据。
/// 真实规模（1,313 对象 / 62,970 链接）下，单对象生成只涉及该对象的 links。</para>
/// </summary>
public sealed class WikiPageArranger
{
    private readonly RelationQueryService _relations;

    public WikiPageArranger(RelationQueryService relations)
    {
        ArgumentNullException.ThrowIfNull(relations);
        _relations = relations;
    }

    /// <summary>
    /// 为指定对象生成维基式页面结构。
    /// <param name="subjectId">对象 id（格式 <c>&lt;category&gt;:&lt;key&gt;</c>）。</param>
    /// <param name="assets">项目资源索引（用于封面挑选与资源定位）。</param>
    /// <param name="assetIndex">
    /// 预先建好的「容器路径 → 资源」索引；<b>批量生成时必须传</b>——不传就按
    /// <paramref name="assets"/> 重建一次，在真实规模（127 万条资源行）下
    /// 每个对象重建一遍是不可接受的。单个对象按需生成时可以不传。
    /// </param>
    /// <returns>生成的页面结构。如果对象不存在或关联图为空，返回 null。</returns>
    /// </summary>
    public WikiPageDetail? Arrange(
        string subjectId,
        IReadOnlyList<Domain.Assets.AssetRecord> assets,
        IReadOnlyDictionary<string, Domain.Assets.AssetRecord>? assetIndex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        if (!_relations.IsReady) return null;

        var subject = _relations.Subjects().FirstOrDefault(s => s.SubjectId == subjectId);
        if (subject is null) return null;

        var links = _relations.Links(subjectId);
        if (links.Count == 0) return null;

        var category = SubjectIds.CategoryOf(subjectId);
        var sections = WikiSectionTables.ForCategory(category);
        assetIndex ??= PresetWorkbenchService.IndexByContainerEntry(assets);

        // 按分节归类资源
        var sectionGroups = new Dictionary<string, List<RelationLink>>();
        foreach (var section in sections)
            sectionGroups[section.SectionId] = new List<RelationLink>();

        foreach (var link in links)
        {
            var section = FindSectionForKind(sections, link.Kind.ToString());
            if (section is not null)
                sectionGroups[section.SectionId].Add(link);
        }

        // 构建二级页
        var subPages = new List<WikiSubPageDetail>();
        var sortOrder = 0;
        foreach (var section in sections)
        {
            var items = sectionGroups.GetValueOrDefault(section.SectionId, []);
            if (items.Count == 0 && !IsRequiredSection(section.SectionId))
                continue;

            var sectionSubPages = BuildSectionSubPages(subjectId, section, items, assetIndex, sortOrder);
            subPages.AddRange(sectionSubPages);
            sortOrder += sectionSubPages.Count;
        }

        // 构建主页面
        var page = BuildPage(subject, links, assetIndex);

        return new WikiPageDetail(page, subPages);
    }

    /// <summary>
    /// 批量为多个对象生成页面结构（用于后台生成）。</summary>
    public IReadOnlyDictionary<string, WikiPageDetail> ArrangeBatch(
        IEnumerable<string> subjectIds, IReadOnlyList<Domain.Assets.AssetRecord> assets)
    {
        var results = new Dictionary<string, WikiPageDetail>(StringComparer.Ordinal);
        foreach (var id in subjectIds)
        {
            var detail = Arrange(id, assets);
            if (detail is not null)
                results[id] = detail;
        }
        return results;
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    private static WikiSectionDefinition? FindSectionForKind(IReadOnlyList<WikiSectionDefinition> sections, string kind)
    {
        foreach (var section in sections)
            if (section.ContainsKind(kind))
                return section;
        return null;
    }

    private static bool IsRequiredSection(string sectionId) => sectionId == WikiSectionTables.Overview;

    private static WikiPage BuildPage(
        RelationSubject subject, IReadOnlyList<RelationLink> links,
        IReadOnlyDictionary<string, Domain.Assets.AssetRecord> assetIndex)
    {
        // 封面：图像链接里按「像不像立绘」排序，优先能定位到资源的
        string? coverRef = null;
        RelationLink? best = null;
        var bestRank = 0;
        var bestResolved = false;
        foreach (var link in links.Where(x => x.Kind == RelationKind.Image))
        {
            assetIndex.TryGetValue(link.RefKey, out var asset);
            var rank = RelationDisplayRules.CoverCandidateRank(link.RefKey, asset?.Type ?? Domain.Assets.AssetType.Sprite);
            if (rank < 0) continue;
            var resolved = asset is not null && asset.UnityPathId.HasValue && !string.IsNullOrWhiteSpace(asset.SourcePath);
            var better = best is null
                || (resolved != bestResolved && resolved)
                || (resolved == bestResolved && rank != bestRank && rank < bestRank)
                || (resolved == bestResolved && rank == bestRank
                    && string.CompareOrdinal(link.RefKey, best.RefKey) < 0);
            if (!better) continue;
            best = link;
            bestRank = rank;
            bestResolved = resolved;
        }
        coverRef = best?.RefKey;

        return new WikiPage(
            subject.SubjectId,
            subject.SubjectKind,
            subject.DisplayName,
            subject.Subtitle,
            subject.SortKey)
        {
            CoverRef = coverRef,
        };
    }

    private IReadOnlyList<WikiSubPageDetail> BuildSectionSubPages(
        string subjectId,
        WikiSectionDefinition section,
        IReadOnlyList<RelationLink> items,
        IReadOnlyDictionary<string, Domain.Assets.AssetRecord> assetIndex,
        int baseSortOrder)
    {
        var result = new List<WikiSubPageDetail>();

        // 检查是否需要拆分
        if (WikiSecondaryPageRules.NeedsSplit(section.SectionId, items.Count))
        {
            var groups = SplitItems(section.SectionId, items);
            var index = 0;
            foreach (var group in groups)
            {
                var pageName = WikiSecondaryPageRules.GetSplitPageName(section.SectionId, index);
                var subPage = BuildSubPage(subjectId, pageName, baseSortOrder + index, group, assetIndex);
                result.Add(subPage);
                index++;
            }
        }
        else
        {
            var subPage = BuildSubPage(subjectId, section.Title, baseSortOrder, items, assetIndex);
            result.Add(subPage);
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<RelationLink>> SplitItems(string sectionId, IReadOnlyList<RelationLink> items)
    {
        return sectionId switch
        {
            WikiSectionTables.Images => SplitImages(items),
            WikiSectionTables.Audio => SplitAudio(items),
            _ => SplitByCount(items, sectionId == WikiSectionTables.Text ? WikiSecondaryPageRules.TextSplitThreshold : WikiSecondaryPageRules.OtherSplitThreshold),
        };
    }

    private static IReadOnlyList<IReadOnlyList<RelationLink>> SplitImages(IReadOnlyList<RelationLink> items)
    {
        return items
            .GroupBy(x => WikiSecondaryPageRules.GetImageGroupKey(x.RefKey))
            .OrderBy(GetImageGroupOrder)
            .Select(g => (IReadOnlyList<RelationLink>)g.ToList())
            .ToArray();
    }

    private static int GetImageGroupOrder(IGrouping<string, RelationLink> g) => g.Key switch
    {
        "profile" => 0,
        "cg" => 1,
        "thumbnail" => 2,
        "info" => 3,
        "skin_preview" => 4,
        "support_portrait" => 5,
        "sprite" => 6,
        "texture" => 7,
        _ => 8,
    };

    private static IReadOnlyList<IReadOnlyList<RelationLink>> SplitAudio(IReadOnlyList<RelationLink> items)
    {
        return items
            .GroupBy(x => WikiSecondaryPageRules.GetAudioGroupKey(x.RefKey))
            .OrderBy(GetAudioGroupOrder)
            .Select(g => (IReadOnlyList<RelationLink>)g.ToList())
            .ToArray();
    }

    private static int GetAudioGroupOrder(IGrouping<string, RelationLink> g) => g.Key switch
    {
        "battle" => 0,
        "skill" => 1,
        "move" => 2,
        "lobby" => 3,
        "event" => 4,
        "death" => 5,
        _ => 6,
    };

    private static IReadOnlyList<IReadOnlyList<RelationLink>> SplitByCount(IReadOnlyList<RelationLink> items, int threshold)
    {
        return items
            .Select((item, index) => (item, index))
            .GroupBy(x => x.index / threshold)
            .Select(g => (IReadOnlyList<RelationLink>)g.Select(x => x.item).ToList())
            .ToArray();
    }

    private WikiSubPageDetail BuildSubPage(
        string subjectId, string title, int sortOrder, IReadOnlyList<RelationLink> items,
        IReadOnlyDictionary<string, Domain.Assets.AssetRecord> assetIndex)
    {
        var subPageId = Guid.NewGuid().ToString("N");
        var entries = new List<WikiEntryDetail>();

        foreach (var link in items)
        {
            assetIndex.TryGetValue(link.RefKey, out var asset);
            var entry = BuildEntry(subPageId, link, asset);
            entries.Add(entry);
        }

        return new WikiSubPageDetail(
            new WikiSubPage(subPageId, subjectId, title, sortOrder),
            entries);
    }

    private static WikiEntryDetail BuildEntry(
        string subPageId, RelationLink link, Domain.Assets.AssetRecord? asset)
    {
        var entryId = Guid.NewGuid().ToString("N");
        var entry = new WikiEntry(entryId, subPageId, link.Display, string.Empty, 0)
        {
            Source = WikiEntrySources.Auto,
        };
        var binding = new WikiResourceBinding(
            Guid.NewGuid().ToString("N"),
            entryId,
            link.RefKey,
            link.Kind.ToString(),
            link.Display,
            0)
        {
            DeepLink = link.DeepLink,
            PreviewText = link.PreviewText,
            MediaKind = link.MediaKind,
            DurationSec = link.DurationSec,
        };
        return new WikiEntryDetail(entry, new[] { binding });
    }
}
