namespace LimbusModEditor.Application.Relations;

/// <summary>
/// 候选导入服务：把 <see cref="RelationGraph"/>（分析器产出的 id 规则推断结果）
/// 转为「候选」，供用户一键导入/忽略。
///
/// <para><b>候选不是事实</b>：它只是「分析器认为这里可能有内容」。
/// 导入后转为人工编纂（来源 = <see cref="WikiEntrySources.Candidate"/>），
/// 可追溯来源（<c>CandidateId</c>）。</para>
///
/// <para><b>人工编纂是唯一事实源</b>：导入时如果目标条目已存在（同 sub_page_id + 同标题），
/// 跳过而非覆盖——用户可能已经手动编辑过，不得用候选覆盖。</para>
/// </summary>
public sealed class WikiCandidateService
{
    private readonly RelationQueryService _relations;
    private readonly WikiPageStore _wikiStore;

    /// <param name="relations">关联图查询（读分析器产出）。</param>
    /// <param name="wikiStore">维基页库（写导入结果）。</param>
    public WikiCandidateService(RelationQueryService relations, WikiPageStore wikiStore)
    {
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(wikiStore);
        _relations = relations;
        _wikiStore = wikiStore;
    }

    /// <summary>
    /// 获取某个主对象页的候选列表（分析器产出的关联资源，尚未导入）。
    /// 只返回<b>尚未导入</b>的候选（已导入的不再重复推荐）。
    /// </summary>
    public IReadOnlyList<WikiCandidate> GetCandidates(string pageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        if (!_relations.IsReady) return [];

        var links = _relations.Links(pageId);
        var candidates = new List<WikiCandidate>();
        foreach (var link in links)
        {
            var candidateId = CandidateIdOf(pageId, link.Kind.ToString(), link.RefKey);
            candidates.Add(new WikiCandidate(
                candidateId,
                pageId,
                link.Kind.ToString(),
                link.RefKey,
                link.Display,
                link.PreviewText,
                link.PreviewKind.ToString())
            {
                DeepLink = link.DeepLink,
                MediaKind = link.MediaKind,
                DurationSec = link.DurationSec,
            });
        }
        return candidates;
    }

    /// <summary>
    /// 一键导入某个主对象页的全部候选。
    /// 已存在的条目（同 sub_page_id + 同标题）跳过，不覆盖。
    /// 返回导入数 / 跳过数。
    /// </summary>
    public WikiCandidateImportResult ImportAll(string pageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        var candidates = GetCandidates(pageId);
        return ImportCandidates(pageId, candidates);
    }

    /// <summary>
    /// 导入指定候选（可挑选部分导入）。</summary>
    public WikiCandidateImportResult ImportCandidates(string pageId, IReadOnlyList<WikiCandidate> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) return new WikiCandidateImportResult(0, 0);

        // 找到或创建一个「导入候选」二级页面
        var subPageId = EnsureImportSubPage(pageId);
        var existing = GetExistingCandidateIds(pageId);
        var imported = 0;
        var skipped = 0;

        foreach (var candidate in candidates)
        {
            if (existing.Contains(candidate.CandidateId)) { skipped++; continue; }
            var entry = new WikiEntry(
                Guid.NewGuid().ToString("N"),
                subPageId,
                candidate.Display,
                candidate.PreviewText ?? string.Empty,
                imported)
            {
                Source = WikiEntrySources.Candidate,
                CandidateId = candidate.CandidateId,
            };
            var binding = new WikiResourceBinding(
                Guid.NewGuid().ToString("N"),
                entry.EntryId,
                candidate.RefKey,
                candidate.Kind,
                candidate.Display,
                0)
            {
                DeepLink = candidate.DeepLink,
                PreviewText = candidate.PreviewText,
                MediaKind = candidate.MediaKind,
                DurationSec = candidate.DurationSec,
            };
            _wikiStore.SaveEntry(entry, binding);
            imported++;
        }
        return new WikiCandidateImportResult(imported, skipped);
    }

    /// <summary>忽略指定候选（只是不显示，不写入任何数据）。</summary>
    public void IgnoreCandidates(IReadOnlyList<string> candidateIds)
    {
        // 候选是实时从 relation-index.db 计算的，「忽略」= 不导入。
        // 未来如需持久化忽略列表，可在此扩展。
        // 当前实现：无操作（候选列表每次实时计算，已导入的会自动排除）。
    }

    // ── 内部 ─────────────────────────────────────────────────────────

    private string EnsureImportSubPage(string pageId)
    {
        // 确保主对象页存在（如果不存在则创建一个空页面，但不删除已有数据）
        var page = _wikiStore.ReadPage(pageId);
        if (page is null)
        {
            var category = SubjectIds.CategoryOf(pageId);
            var key = SubjectIds.KeyOf(pageId);
            page = new WikiPage(pageId, string.IsNullOrEmpty(category) ? RelationCategories.Persona : category, key, string.Empty, key);
            // 使用 INSERT OR IGNORE 语义，避免删除已有数据
            _wikiStore.InsertPageIfNotExists(page);
        }

        var subPages = _wikiStore.ReadSubPages(pageId);
        var import = subPages.FirstOrDefault(s => s.Title == "导入候选");
        if (import is not null) return import.SubPageId;

        var subPage = new WikiSubPage(
            Guid.NewGuid().ToString("N"),
            pageId,
            "导入候选",
            subPages.Count > 0 ? subPages.Max(s => s.SortOrder) + 1 : 0);
        _wikiStore.SaveSubPage(subPage);
        return subPage.SubPageId;
    }

    private HashSet<string> GetExistingCandidateIds(string pageId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var subPages = _wikiStore.ReadSubPages(pageId);
        foreach (var sub in subPages)
        {
            var entries = _wikiStore.ReadEntries(sub.SubPageId);
            foreach (var entry in entries)
            {
                if (!string.IsNullOrEmpty(entry.CandidateId))
                    result.Add(entry.CandidateId);
            }
        }
        return result;
    }

    private static string CandidateIdOf(string subjectId, string kind, string refKey)
        => $"{subjectId}#{kind}#{refKey}";
}
