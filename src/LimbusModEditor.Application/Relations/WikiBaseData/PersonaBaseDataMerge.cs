using LimbusModEditor.Application.Relations.Authority;

namespace LimbusModEditor.Application.Relations.WikiBaseData;

/// <summary>
/// 把 <see cref="PersonaBaseDataSections"/> 产出的三节合并进人格页面树。
///
/// <para><b>合并语义</b>：</para>
/// <list type="bullet">
/// <item>「数据」节：已有该节（静态表链接编出来的）就用基础数值条目<b>替换</b>其内容
/// （原内容只是表文件元数据噪声），并补一条「来源表」条目保留出处；没有该节就新建。</item>
/// <item>「技能」「被动」节：<b>追加在页面末尾</b>——绝不插入中间。
/// 分节/条目 id 由「页面 id + 标题 + 顺序」派生（<see cref="WikiStableIds"/>），
/// 中插会让既有分节全体顺移 → id 全变 → 含用户修订的分节被陈旧保护逻辑整节留底，
/// 页面上出现重复分节（实测 persona:10201 的「语音/3」重复过一次）。
/// 展示顺序不受影响：前端按分节标题语义分组出 Tab（技能/被动紧挨数据），
/// 不按 sortOrder 平铺。</item>
/// <item>某节没有条目就不建（不占位）；同内容两次生成幂等。</item>
/// </list>
/// </summary>
public static class PersonaBaseDataMerge
{
    private const string DataTitle = "数据";
    private const string SkillsTitle = "技能";
    private const string PassivesTitle = "被动";

    /// <summary>合并后的页面树。三节全空时原样返回。</summary>
    public static WikiPageDetail MergeInto(WikiPageDetail detail, PersonaBaseDataSections.Result baseData)
    {
        if (baseData.BaseData.Count == 0 && baseData.Skills.Count == 0 && baseData.Passives.Count == 0)
            return detail;

        var pageId = detail.Page.PageId;

        // 1) 重排分节序列：「数据」就地替换（id 不变；没有基础数值可补就保留原节），
        //    技能/被动追加在末尾（不动既有顺序）。
        var ordered = new List<SectionSpec>();
        var dataPlaced = false;
        foreach (var sub in detail.SubPages)
        {
            if (string.Equals(sub.SubPage.Title, DataTitle, StringComparison.Ordinal) &&
                baseData.BaseData.Count > 0)
            {
                ordered.Add(new SectionSpec(DataTitle, Existing: [], Prepared: PreparedWithSources(baseData)));
                dataPlaced = true;
                continue; // 原数据节（表文件元数据条目）被替换，不再保留
            }
            ordered.Add(new SectionSpec(sub.SubPage.Title, sub.Entries));
        }

        if (!dataPlaced && baseData.BaseData.Count > 0)
            ordered.Add(new SectionSpec(DataTitle, Existing: [], Prepared: PreparedWithSources(baseData)));
        if (baseData.Skills.Count > 0)
            ordered.Add(new SectionSpec(SkillsTitle, Existing: [], Prepared: baseData.Skills));
        if (baseData.Passives.Count > 0)
            ordered.Add(new SectionSpec(PassivesTitle, Existing: [], Prepared: baseData.Passives));

        // 2) 统一分配 sortOrder 与稳定 id；新建分节就地物化条目，沿用分节 id 漂移时重算条目/绑定 id。
        var subPages = new List<WikiSubPageDetail>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            var spec = ordered[index];
            var subPageId = WikiStableIds.Of(pageId, spec.Title, index.ToString());
            IReadOnlyList<WikiEntryDetail> entries;
            if (spec.Prepared is not null)
            {
                var built = new List<WikiEntryDetail>(spec.Prepared.Count);
                for (var i = 0; i < spec.Prepared.Count; i++)
                    built.Add(MakeEntry(subPageId, spec.Prepared[i], i));
                entries = built;
            }
            else
            {
                entries = spec.Existing.Select(e => ReId(e, subPageId)).ToList();
            }
            subPages.Add(new WikiSubPageDetail(new WikiSubPage(subPageId, pageId, spec.Title, index), entries));
        }

        return new WikiPageDetail(detail.Page, subPages);
    }

    private static List<PersonaBaseDataSections.PreparedEntry> PreparedWithSources(
        PersonaBaseDataSections.Result baseData)
    {
        var entries = new List<PersonaBaseDataSections.PreparedEntry>(baseData.BaseData);
        entries.Add(new PersonaBaseDataSections.PreparedEntry(
            "base:source-tables", "来源表",
            "静态表（行级匹配 id）：" + baseData.SourceTables,
            "静态表索引 static-tables.db（正文按表名惰性读取并落 documents 缓存）",
            nameof(AuthoritySource.StaticTableForeignKey)));
        return entries;
    }

    private sealed record SectionSpec(
        string Title,
        IReadOnlyList<WikiEntryDetail> Existing,
        IReadOnlyList<PersonaBaseDataSections.PreparedEntry>? Prepared = null);

    private static WikiEntryDetail MakeEntry(
        string subPageId, PersonaBaseDataSections.PreparedEntry prepared, int sortOrder)
    {
        var entryId = WikiStableIds.Of(subPageId, prepared.Key);
        var entry = new WikiEntry(entryId, subPageId, prepared.Title, prepared.Body, sortOrder)
        {
            Source = WikiEntrySources.Auto,
            // 权威：行定位来自静态表主键精确匹配 / Lang 权威 id 清单；置信度：主键精确匹配 → Authoritative。
            // 正文是跨源格式化摘要（不是某个可写字段的原文）→ 只读，不开编辑。
            Authority = prepared.Authority,
            Confidence = nameof(ConfidenceLevel.Authoritative),
            WritableSource = nameof(WritableSourceKind.None),
            SourceDetail = prepared.SourceDetail,
        };
        return new WikiEntryDetail(entry, []);
    }

    /// <summary>沿用分节的条目在 subPageId 漂移时重算稳定 id（标题/正文不变，只换 id 与归属）。
    /// 正常路径下追加式合并不产生漂移，这里只是防御（数据节被删导致后续索引前移等）。</summary>
    private static WikiEntryDetail ReId(WikiEntryDetail detail, string subPageId)
    {
        var entry = detail.Entry;
        if (string.Equals(entry.SubPageId, subPageId, StringComparison.Ordinal)) return detail;

        var entryId = WikiStableIds.Of(subPageId, entry.EntryId);
        var moved = entry with { EntryId = entryId, SubPageId = subPageId };
        var bindings = detail.Bindings
            .Select(binding => binding with
            {
                BindingId = WikiStableIds.Of(entryId, binding.RefKey),
                EntryId = entryId,
            })
            .ToList();
        return new WikiEntryDetail(moved, bindings);
    }
}
