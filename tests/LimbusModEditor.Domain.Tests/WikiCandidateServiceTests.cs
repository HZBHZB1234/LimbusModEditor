using LimbusModEditor.Application.Relations;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// 候选导入服务（<see cref="WikiCandidateService"/>）语义测试。
///
/// <para>覆盖：候选获取（只返回未导入的）、一键导入、部分导入、跳过已存在、
/// 候选与已编纂内容可区分（来源字段）、可追溯来源（CandidateId）。</para>
/// </summary>
public sealed class WikiCandidateServiceTests : IDisposable
{
    private readonly string _cacheDir;
    private readonly string _relCacheDir;

    public WikiCandidateServiceTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "lme-wikipage-cand-" + Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Combine(root, "wiki");
        _relCacheDir = Path.Combine(root, "rel");
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_relCacheDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(_cacheDir)!, true); } catch (Exception) { /* 临时目录 */ }
    }

    private WikiCandidateService Service(RelationGraph? graph = null)
    {
        // 创建 relation-index.db（分析器产出）
        var relStore = new RelationStore(_relCacheDir);
        var source = RelationIndexSource.From("u", "b", "s", "t");
        relStore.EnsureSource(source);
        relStore.PersistGraph(source, graph ?? new RelationGraph(
            [new RelationSubject("persona:10201", RelationCategories.Persona, "格里高尔", "Gregor", "Gregor", "10201")],
            [
                new RelationLink("persona:10201", RelationCategories.Persona, RelationKind.Image,
                    "Assets/10201.png", "立绘", null, 100),
                new RelationLink("persona:10201", RelationCategories.Persona, RelationKind.Audio,
                    @"C:\game\Voice.bank" + "\u0000" + "voice_10201_1", "语音", null, 200),
            ]));

        var relQuery = new RelationQueryService(relStore);
        var wikiStore = new WikiPageStore(_cacheDir);
        return new WikiCandidateService(relQuery, wikiStore);
    }

    [Fact]
    public void Get_candidates_returns_only_unimported()
    {
        var svc = Service();
        var candidates = svc.GetCandidates("persona:10201");
        Assert.Equal(2, candidates.Count); // Image + Audio
    }

    [Fact]
    public void Get_candidates_returns_empty_for_unknown_subject()
    {
        var svc = Service();
        var candidates = svc.GetCandidates("persona:99999");
        Assert.Empty(candidates);
    }

    [Fact]
    public void Import_all_creates_candidate_entries()
    {
        var svc = Service();
        var result = svc.ImportAll("persona:10201");
        Assert.Equal(2, result.Imported);
        Assert.Equal(0, result.Skipped);

        // 验证导入的条目来源为 Candidate
        var wikiStore = new WikiPageStore(_cacheDir);
        var subPages = wikiStore.ReadSubPages("persona:10201");
        Assert.Single(subPages);
        Assert.Equal("导入候选", subPages[0].Title);

        var entries = wikiStore.ReadEntries(subPages[0].SubPageId);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(WikiEntrySources.Candidate, e.Source));
        Assert.All(entries, e => Assert.NotNull(e.CandidateId));
    }

    [Fact]
    public void Import_all_skips_already_imported()
    {
        var svc = Service();
        svc.ImportAll("persona:10201");

        // 再次导入
        var result = svc.ImportAll("persona:10201");
        Assert.Equal(0, result.Imported);
        Assert.Equal(2, result.Skipped);
    }

    [Fact]
    public void Import_candidates_creates_sub_page_if_missing()
    {
        var svc = Service();
        var candidates = svc.GetCandidates("persona:10201");
        Assert.NotEmpty(candidates);

        svc.ImportCandidates("persona:10201", candidates);

        var wikiStore = new WikiPageStore(_cacheDir);
        var subPages = wikiStore.ReadSubPages("persona:10201");
        Assert.Single(subPages);
        Assert.Equal("导入候选", subPages[0].Title);
    }

    [Fact]
    public void Import_candidates_with_empty_list_does_nothing()
    {
        var svc = Service();
        var result = svc.ImportCandidates("persona:10201", Array.Empty<WikiCandidate>());
        Assert.Equal(0, result.Imported);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Candidate_entries_are_traceable_to_source()
    {
        var svc = Service();
        svc.ImportAll("persona:10201");

        var wikiStore = new WikiPageStore(_cacheDir);
        var subPages = wikiStore.ReadSubPages("persona:10201");
        var entries = wikiStore.ReadEntries(subPages[0].SubPageId);

        // 每个候选都有 CandidateId，格式为 subjectId#kind#refKey
        foreach (var entry in entries)
        {
            Assert.NotNull(entry.CandidateId);
            Assert.Contains("persona:10201#", entry.CandidateId);
        }
    }

    [Fact]
    public void Human_edited_entries_are_not_overwritten_by_candidates()
    {
        var svc = Service();
        var wikiStore = new WikiPageStore(_cacheDir);

        // 先创建页面和二级页面，再添加人工编纂条目
        var page = new WikiPage("persona:10201", RelationCategories.Persona, "格里高尔", "Gregor", "persona:10201");
        var subPage = new WikiSubPage(Guid.NewGuid().ToString("N"), "persona:10201", "介绍", 0);
        wikiStore.SavePage(new WikiPageDetail(page,
            new[] { new WikiSubPageDetail(subPage, Array.Empty<WikiEntryDetail>()) }));
        var humanEntry = new WikiEntry(Guid.NewGuid().ToString("N"), subPage.SubPageId, "立绘", "人工正文", 0)
        {
            Source = WikiEntrySources.HumanEdited,
        };
        wikiStore.SaveEntry(humanEntry);

        // 导入候选（候选的 Display 也是 "立绘"）
        var candidates = svc.GetCandidates("persona:10201");
        var imageCandidate = candidates.First(c => c.Kind == RelationKind.Image.ToString());
        svc.ImportCandidates("persona:10201", new[] { imageCandidate });

        // 验证：人工编纂条目仍在原二级页面中
        var entries = wikiStore.ReadEntries(subPage.SubPageId);
        Assert.Single(entries); // 人工条目仍在原处
        Assert.All(entries, e => Assert.Equal(WikiEntrySources.HumanEdited, e.Source));
    }

    [Fact]
    public void Candidates_distinct_from_human_edited_by_source_field()
    {
        var svc = Service();
        svc.ImportAll("persona:10201");

        var wikiStore = new WikiPageStore(_cacheDir);
        var subPages = wikiStore.ReadSubPages("persona:10201");
        var entries = wikiStore.ReadEntries(subPages[0].SubPageId);

        // 所有导入的候选来源都是 Candidate
        Assert.All(entries, e => Assert.Equal(WikiEntrySources.Candidate, e.Source));
        Assert.All(entries, e => Assert.NotNull(e.CandidateId));
    }
}
