using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.Relations.WikiBaseData;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 人格页「数据/技能/被动」三节合并（<see cref="PersonaBaseDataMerge"/>）语义测试。
/// 覆盖：数据节就地替换、技能/被动<b>追加在末尾</b>（不移动既有分节 id，否则含修订的
/// 旧分节会被陈旧保护整节留底造成重复）、稳定 id 幂等、空三节原样返回。
/// </summary>
public sealed class WikiPersonaBaseDataMergeTests
{
    private const string PageId = "persona:10201";

    /// <summary>手造一棵与 Materialize 同 id 规则（Of(页面,标题,序号) / Of(分节,refKey)）的页面树。</summary>
    private static WikiPageDetail ArrangeDetail(bool withData = true)
    {
        var page = new WikiPage(PageId, "persona", "浮士德", "Base", "10201");
        var subPages = new List<WikiSubPageDetail>();

        var overviewId = WikiStableIds.Of(PageId, "概览", "0");
        subPages.Add(new WikiSubPageDetail(
            new WikiSubPage(overviewId, PageId, "概览", 0),
            [new WikiEntryDetail(
                new WikiEntry(WikiStableIds.Of(overviewId, "overview:summary"), overviewId, "摘要", "正文", 0),
                [])]));

        if (withData)
        {
            var dataId = WikiStableIds.Of(PageId, "数据", "1");
            subPages.Add(new WikiSubPageDetail(
                new WikiSubPage(dataId, PageId, "数据", 1),
                [new WikiEntryDetail(
                    new WikiEntry(WikiStableIds.Of(dataId, "static-ref"), dataId, "personality-02", "TextAsset · 33 KB", 0),
                    [])]));
        }

        var textId = WikiStableIds.Of(PageId, "文本", withData ? "2" : "1");
        var textEntryId = WikiStableIds.Of(textId, "Voice_10201_1.json");
        subPages.Add(new WikiSubPageDetail(
            new WikiSubPage(textId, PageId, "文本", withData ? 2 : 1),
            [new WikiEntryDetail(
                new WikiEntry(textEntryId, textId, "Voice_10201_1.json", string.Empty, 0),
                [new WikiResourceBinding(
                    WikiStableIds.Of(textEntryId, "Voice_10201_1.json"), textEntryId,
                    "Voice_10201_1.json", "Text", "语音", 0)])]));

        return new WikiPageDetail(page, subPages);
    }

    private static PersonaBaseDataSections.Result BaseData(int skills, int passives) => new(
        [new PersonaBaseDataSections.PreparedEntry(
            "base:hp", "体力", "初始 84", "personality-02.json 行 id=10201", "StaticTableForeignKey")],
        Enumerable.Range(0, skills)
            .Select(i => new PersonaBaseDataSections.PreparedEntry(
                $"skill:{1020101 + i}", $"技能{i}", "威力 2", "personality-skill-02.json", "StaticTableForeignKey"))
            .ToList(),
        Enumerable.Range(0, passives)
            .Select(i => new PersonaBaseDataSections.PreparedEntry(
                $"passive:{1020121 + i}", $"被动{i}", "伤害+10%", "personality-passive-02.json", "StaticTableForeignKey"))
            .ToList(),
        "personality-02.json · personality-skill-02.json");

    [Fact]
    public void Merge_replaces_data_in_place_and_appends_skills_then_passives()
    {
        var detail = ArrangeDetail();
        var merged = PersonaBaseDataMerge.MergeInto(detail, BaseData(skills: 2, passives: 1));

        // 顺序：概览 → 数据（就地替换）→ 文本（不动）→ 技能 → 被动（末尾追加）。
        // 追加而非中插：中插会让既有分节 id 全体漂移，含用户修订的旧分节被陈旧
        // 保护整节留底 → 页面上出现重复分节。
        Assert.Equal(["概览", "数据", "文本", "技能", "被动"],
            merged.SubPages.Select(s => s.SubPage.Title).ToArray());
        Assert.Equal([0, 1, 2, 3, 4], merged.SubPages.Select(s => s.SubPage.SortOrder).ToArray());

        // 数据节被替换：旧「表文件元数据」条目没了，变成基础数值 + 来源表。
        var data = merged.SubPages[1];
        Assert.Equal(["体力", "来源表"], data.Entries.Select(e => e.Entry.Title).ToArray());
        Assert.DoesNotContain(data.Entries, e => e.Entry.Title == "personality-02");

        // 技能/被动追加在末尾，条目带权威标注与来源详情。
        Assert.Equal(["技能0", "技能1"], merged.SubPages[3].Entries.Select(e => e.Entry.Title).ToArray());
        var skillEntry = merged.SubPages[3].Entries[0].Entry;
        Assert.Equal("StaticTableForeignKey", skillEntry.Authority);
        Assert.Equal("Authoritative", skillEntry.Confidence);
        Assert.Equal("None", skillEntry.WritableSource);
        Assert.False(skillEntry.Editable);
        Assert.Contains("personality-skill-02.json", skillEntry.SourceDetail);
        Assert.Equal(["被动0"], merged.SubPages[4].Entries.Select(e => e.Entry.Title).ToArray());
    }

    [Fact]
    public void Merge_keeps_existing_section_and_entry_ids_untouched()
    {
        var detail = ArrangeDetail();
        var merged = PersonaBaseDataMerge.MergeInto(detail, BaseData(skills: 1, passives: 1));

        // 追加式合并不移动既有分节 → 既有分节/条目/绑定的 id 与 Materialize 算出的完全一致
        // ——含用户修订的分节必须还能被 SaveGeneratedPage 的「修订优先」按 entry id 命中，
        // 否则修订会被陈旧留底、页面上出现重复分节。
        var text = merged.SubPages.Single(s => s.SubPage.Title == "文本");
        Assert.Equal(WikiStableIds.Of(PageId, "文本", "2"), text.SubPage.SubPageId);
        var entry = text.Entries.Single();
        Assert.Equal(WikiStableIds.Of(text.SubPage.SubPageId, "Voice_10201_1.json"), entry.Entry.EntryId);
        Assert.Equal(text.SubPage.SubPageId, entry.Entry.SubPageId);
        var binding = entry.Bindings.Single();
        Assert.Equal(WikiStableIds.Of(entry.Entry.EntryId, binding.RefKey), binding.BindingId);
        Assert.Equal(entry.Entry.EntryId, binding.EntryId);
    }

    [Fact]
    public void Merge_is_idempotent_on_repeated_generation()
    {
        var first = PersonaBaseDataMerge.MergeInto(ArrangeDetail(), BaseData(skills: 1, passives: 1));
        var second = PersonaBaseDataMerge.MergeInto(ArrangeDetail(), BaseData(skills: 1, passives: 1));

        Assert.Equal(
            first.SubPages.Select(s => s.SubPage.SubPageId),
            second.SubPages.Select(s => s.SubPage.SubPageId));
        Assert.Equal(
            first.SubPages.SelectMany(s => s.Entries.Select(e => e.Entry.EntryId)),
            second.SubPages.SelectMany(s => s.Entries.Select(e => e.Entry.EntryId)));
    }

    [Fact]
    public void Merge_keeps_original_data_section_when_base_data_is_empty()
    {
        // 防御路径：人格行找到了、但一行字段都没解析出来（基础数值空）→
        // 原「数据」节（表文件元数据）必须原样保留，不许被空替换掉。
        var detail = ArrangeDetail();
        var result = new PersonaBaseDataSections.Result(
            [], BaseData(skills: 1, passives: 0).Skills, [], "personality-02.json");
        var merged = PersonaBaseDataMerge.MergeInto(detail, result);

        Assert.Equal(["概览", "数据", "文本", "技能"], merged.SubPages.Select(s => s.SubPage.Title).ToArray());
        var data = merged.SubPages[1];
        Assert.Equal(["personality-02"], data.Entries.Select(e => e.Entry.Title).ToArray());
    }

    [Fact]
    public void Merge_without_sections_returns_detail_untouched()
    {
        var detail = ArrangeDetail();
        var empty = new PersonaBaseDataSections.Result([], [], [], "-");
        Assert.Same(detail, PersonaBaseDataMerge.MergeInto(detail, empty));
    }

    [Fact]
    public void Merge_creates_data_section_when_arranger_had_none()
    {
        var detail = ArrangeDetail();
        var withoutData = new WikiPageDetail(detail.Page,
            detail.SubPages.Where(s => s.SubPage.Title != "数据").ToList());
        var merged = PersonaBaseDataMerge.MergeInto(withoutData, BaseData(skills: 0, passives: 0));

        // 数据节缺则补建（追加在末尾）；技能/被动空则不建（不占位）。
        Assert.Equal(["概览", "文本", "数据"], merged.SubPages.Select(s => s.SubPage.Title).ToArray());
    }
}
