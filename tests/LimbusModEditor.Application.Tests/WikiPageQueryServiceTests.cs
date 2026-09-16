using LimbusModEditor.Application.Relations;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 维基化页面树查询门面（<see cref="WikiPageQueryService"/>）语义测试。
///
/// <para>覆盖：页面树惰性加载、详情一次性加载、搜索分页、来源统计、空库不抛异常。</para>
/// </summary>
public sealed class WikiPageQueryServiceTests : IDisposable
{
    private readonly string _cacheDir;

    public WikiPageQueryServiceTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "lme-wikipage-qs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_cacheDir, true); } catch (Exception) { /* 临时目录 */ }
    }

    private WikiPageQueryService Service()
    {
        var store = new WikiPageStore(_cacheDir);
        return new WikiPageQueryService(store);
    }

    private static WikiPage Page(string id = "persona:10201", string title = "格里高尔")
        => new(id, RelationCategories.Persona, title, "Gregor", id);

    private static WikiSubPage SubPage(string pageId, string title = "介绍", int sort = 0)
        => new(Guid.NewGuid().ToString("N"), pageId, title, sort);

    private static WikiEntry Entry(string subPageId, string title = "条目", string body = "正文", int sort = 0)
        => new(Guid.NewGuid().ToString("N"), subPageId, title, body, sort);

    private static WikiResourceBinding Binding(string entryId, string refKey = "Assets/10201.png", string kind = "Image")
        => new(Guid.NewGuid().ToString("N"), entryId, refKey, kind, "立绘", 0);

    [Fact]
    public void Is_ready_returns_false_when_empty()
    {
        var svc = Service();
        Assert.False(svc.IsReady);
    }

    [Fact]
    public void Is_ready_returns_true_when_pages_exist()
    {
        var svc = Service();
        var page = Page();
        var sub = SubPage(page.PageId);
        var entry = Entry(sub.SubPageId);
        var store = new WikiPageStore(_cacheDir);
        // 先创建页面和二级页面，再添加条目
        store.SavePage(new WikiPageDetail(page,
            new[] { new WikiSubPageDetail(sub, new[] { new WikiEntryDetail(entry, Array.Empty<WikiResourceBinding>()) }) }));

        Assert.True(svc.IsReady);
    }

    [Fact]
    public void Pages_returns_all_pages_sorted()
    {
        var svc = Service();
        var store = new WikiPageStore(_cacheDir);
        store.SavePage(new WikiPageDetail(Page("persona:10202", "浮士德"), Array.Empty<WikiSubPageDetail>()));
        store.SavePage(new WikiPageDetail(Page("persona:10201", "格里高尔"), Array.Empty<WikiSubPageDetail>()));

        var pages = svc.Pages();
        Assert.Equal(2, pages.Count);
        Assert.Equal("persona:10201", pages[0].PageId); // sort_key 序
        Assert.Equal("persona:10202", pages[1].PageId);
    }

    [Fact]
    public void Get_page_returns_null_for_missing()
    {
        var svc = Service();
        Assert.Null(svc.GetPage("persona:99999"));
    }

    [Fact]
    public void Get_page_detail_returns_full_tree()
    {
        var svc = Service();
        var store = new WikiPageStore(_cacheDir);
        var page = Page();
        var sub1 = SubPage(page.PageId, "介绍", 0);
        var sub2 = SubPage(page.PageId, "相关角色", 1);
        var entry1 = Entry(sub1.SubPageId, "大致原出处", "出自《变形记》", 0);
        var binding1 = Binding(entry1.EntryId, "Assets/10201.png", "Image");

        store.SavePage(new WikiPageDetail(page,
            new[]
            {
                new WikiSubPageDetail(sub1, new[] { new WikiEntryDetail(entry1, new[] { binding1 }) }),
                new WikiSubPageDetail(sub2, Array.Empty<WikiEntryDetail>()),
            }));

        var detail = svc.GetPageDetail(page.PageId);
        Assert.NotNull(detail);
        Assert.Equal(page.PageId, detail!.Page.PageId);
        Assert.Equal(2, detail.SubPages.Count);
        Assert.Single(detail.SubPages[0].Entries);
        Assert.Single(detail.SubPages[0].Entries[0].Bindings);
    }

    [Fact]
    public void Get_page_detail_returns_null_for_missing()
    {
        var svc = Service();
        Assert.Null(svc.GetPageDetail("persona:99999"));
    }

    [Fact]
    public void Search_entries_paginates()
    {
        var svc = Service();
        var store = new WikiPageStore(_cacheDir);
        var page = Page();
        var sub = SubPage(page.PageId);
        // 先创建页面和二级页面，再添加条目
        store.SavePage(new WikiPageDetail(page,
            new[] { new WikiSubPageDetail(sub, Array.Empty<WikiEntryDetail>()) }));
        for (var i = 0; i < 5; i++)
        {
            var entry = Entry(sub.SubPageId, $"条目{i}", $"正文{i}", i);
            store.SaveEntry(entry);
        }

        var result1 = svc.SearchEntries("条目", 0, 2);
        Assert.Equal(5, result1.Total);
        Assert.Equal(2, result1.Entries.Count);
    }

    [Fact]
    public void Count_by_category_returns_correct_count()
    {
        var svc = Service();
        var store = new WikiPageStore(_cacheDir);
        store.SavePage(new WikiPageDetail(Page("persona:10201", "格里高尔"), Array.Empty<WikiSubPageDetail>()));
        store.SavePage(new WikiPageDetail(Page("persona:10202", "浮士德"), Array.Empty<WikiSubPageDetail>()));
        store.SavePage(new WikiPageDetail(
            new WikiPage("enemy:8050", RelationCategories.Enemy, "害虫凯撒", "Kaiser", "enemy:8050"),
            Array.Empty<WikiSubPageDetail>()));

        Assert.Equal(2, svc.CountByCategory(RelationCategories.Persona));
        Assert.Equal(1, svc.CountByCategory(RelationCategories.Enemy));
        Assert.Equal(0, svc.CountByCategory(RelationCategories.Ego));
    }

    [Fact]
    public void Source_statistics_counts_correctly()
    {
        var svc = Service();
        var store = new WikiPageStore(_cacheDir);
        var page = Page();
        var sub = SubPage(page.PageId);
        // 先创建页面和二级页面，再添加条目
        store.SavePage(new WikiPageDetail(page,
            new[] { new WikiSubPageDetail(sub, Array.Empty<WikiEntryDetail>()) }));
        var humanEntry = Entry(sub.SubPageId, "人工条目", "人工正文", 0);
        var candidateEntry = new WikiEntry(Guid.NewGuid().ToString("N"), sub.SubPageId, "候选条目", "候选正文", 1)
        {
            Source = WikiEntrySources.Candidate,
            CandidateId = "candidate-1",
        };
        store.SaveEntry(humanEntry);
        store.SaveEntry(candidateEntry);

        var (human, candidate) = svc.SourceStatistics();
        Assert.Equal(1, human);
        Assert.Equal(1, candidate);
    }

    [Fact]
    public void Empty_store_does_not_throw()
    {
        var svc = Service();
        Assert.False(svc.IsReady);
        Assert.Empty(svc.Pages());
        Assert.Null(svc.GetPage("any"));
        Assert.Null(svc.GetPageDetail("any"));
        Assert.Empty(svc.SubPages("any"));
        Assert.Empty(svc.Entries("any"));
        Assert.Empty(svc.Bindings("any"));
        var searchResult = svc.SearchEntries("any", 0, 10);
        Assert.Equal(0, searchResult.Total);
        Assert.Empty(searchResult.Entries);
    }
}
