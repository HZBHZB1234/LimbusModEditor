namespace LimbusModEditor.Application.Relations.Authority;

/// <summary>
/// 维基页面编排器：把权威引擎产出的事实组织成维基式页面结构。
///
/// <para><b>设计原则</b>：
/// - 只产出 <see cref="WritableSourceKind.Path"/> 的事实（可编辑）
/// - <see cref="WritableSourceKind.None"/> 和 <see cref="WritableSourceKind.Unknown"/> 不进页面结构
/// - 分节按 <see cref="WikiSectionTables"/> 集中定义，可测
/// - 生成幂等：相同输入产出相同结构
/// - 用户修订优先：只生成默认结构，不覆盖用户已编辑内容</para>
/// </summary>
public sealed class WikiPageArranger
{
    private readonly WikiPageAuthorityEngine _engine;

    public WikiPageArranger(WikiPageAuthorityEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <summary>
    /// 为指定对象编排维基页面结构。
    /// <param name="subjectId">对象 id。</param>
    /// <param name="context">抽取上下文。</param>
    /// <returns>页面结构。如果无可用事实，返回 null。</returns>
    /// </summary>
    public WikiPageDetail? Arrange(string subjectId, AuthorityExtractionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        var facts = _engine.Extract(subjectId, context);
        if (facts.Facts.Count == 0) return null;

        var category = SubjectIds.CategoryOf(subjectId);
        var sections = WikiSectionTables.ForCategory(category);

        // 只保留可写的事实（WritableSourceKind.Path）
        var writableFacts = facts.Facts
            .Where(f => f.WritableSource == WritableSourceKind.Path)
            .ToList();

        if (writableFacts.Count == 0) return null;

        // 按分节归类
        var subPages = BuildSubPages(subjectId, sections, writableFacts);
        if (subPages.Count == 0) return null;

        // 构建主页面
        var page = BuildPage(subjectId, category, writableFacts);

        return new WikiPageDetail(page, subPages);
    }

    private static WikiPage BuildPage(string subjectId, string category, IReadOnlyList<AuthorityFact> facts)
    {
        var coverRef = facts
            .Where(f => f.ContentType == "image" || f.ContentType == "spine")
            .OrderBy(f => f.Source == AuthoritySource.SpriteBaseNameMatch ? 0 : 1)
            .Select(f => f.RefKey)
            .FirstOrDefault();

        return new WikiPage(subjectId, category, subjectId.Split(':')[1], string.Empty, subjectId)
        {
            CoverRef = coverRef,
        };
    }

    private static IReadOnlyList<WikiSubPageDetail> BuildSubPages(
        string subjectId, IReadOnlyList<WikiSectionDefinition> sections, IReadOnlyList<AuthorityFact> facts)
    {
        var result = new List<WikiSubPageDetail>();
        var sortOrder = 0;

        foreach (var section in sections)
        {
            var sectionFacts = facts.Where(f => section.ContainsKind(f.ContentType)).ToList();
            if (sectionFacts.Count == 0) continue;

            var entries = sectionFacts.Select((f, i) => new WikiEntryDetail(
                new WikiEntry(Guid.NewGuid().ToString("N"), $"temp-{i}", f.Display ?? f.RefKey, string.Empty, i)
                {
                    Source = WikiEntrySources.Auto,
                },
                new[]
                {
                    new WikiResourceBinding(Guid.NewGuid().ToString("N"), $"temp-{i}", f.RefKey, f.ContentType, f.Display ?? f.RefKey, 0)
                    {
                        DeepLink = f.DeepLink,
                        PreviewText = f.PreviewText,
                        MediaKind = f.MediaKind,
                        DurationSec = f.DurationSec,
                    }
                })).ToList();

            result.Add(new WikiSubPageDetail(
                new WikiSubPage(Guid.NewGuid().ToString("N"), subjectId, section.Title, sortOrder),
                entries));

            sortOrder++;
        }

        return result;
    }
}
