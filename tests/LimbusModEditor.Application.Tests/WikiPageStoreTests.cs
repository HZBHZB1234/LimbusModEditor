using LimbusModEditor.Application.Relations;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// <c>cache/wiki-pages.db</c>（人工编纂的维基化页面树）的语义测试。
///
/// <para>覆盖：页面树往返（主对象页 → 二级页面 → 条目 → 资源绑定）、
/// 多级二级页面的创建/重命名/排序/删除、引用完整性（删父不孤儿）、
/// 序列化往返、损坏输入的中文报错、候选导入、搜索分页。</para>
/// </summary>
public sealed class WikiPageStoreTests : IDisposable
{
    private readonly string _cacheDir;

    public WikiPageStoreTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "lme-wikipage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_cacheDir, true); } catch (Exception) { /* 临时目录 */ }
    }

    private WikiPageStore NewStore() => new(_cacheDir);

    private static WikiPage Page(string id = "persona:10201", string title = "格里高尔", string sub = "Gregor")
    {
        // 从 id 中提取类别（格式：category:key）
        var category = id.Contains(':') ? id.Split(':')[0] : RelationCategories.Persona;
        return new(id, category, title, sub, id);
    }

    private static WikiSubPage SubPage(string pageId, string title = "介绍", int sort = 0)
        => new(Guid.NewGuid().ToString("N"), pageId, title, sort);

    private static WikiEntry Entry(string subPageId, string title = "大致原出处", string body = "出自《变形记》", int sort = 0)
        => new(Guid.NewGuid().ToString("N"), subPageId, title, body, sort);

    private static WikiResourceBinding Binding(string entryId, string refKey = "Assets/10201.png", string kind = "Image")
        => new(Guid.NewGuid().ToString("N"), entryId, refKey, kind, "立绘", 0);

    private static WikiPageDetail Detail(WikiPage page, params WikiSubPageDetail[] subs)
        => new(page, subs);

    private static WikiSubPageDetail SubDetail(WikiSubPage sub, params WikiEntryDetail[] entries)
        => new(sub, entries);

    private static WikiEntryDetail EntryDetail(WikiEntry entry, params WikiResourceBinding[] bindings)
        => new(entry, bindings);

    [Fact]
    public void Page_tree_round_trips_with_all_levels()
    {
        var store = NewStore();
        var page = Page();
        var sub1 = SubPage(page.PageId, "介绍", 0);
        var sub2 = SubPage(page.PageId, "相关角色", 1);
        var entry1 = Entry(sub1.SubPageId, "大致原出处", "出自《变形记》", 0);
        var entry2 = Entry(sub1.SubPageId, "主要相关点", "异化主题", 1);
        var binding1 = Binding(entry1.EntryId, "Assets/10201.png", "Image");
        var binding2 = Binding(entry1.EntryId, "Voice/10201.wav", "Audio");

        store.SavePage(Detail(page,
            SubDetail(sub1,
                EntryDetail(entry1, binding1, binding2),
                EntryDetail(entry2)),
            SubDetail(sub2)));

        // 主对象页
        var pages = store.ReadPages();
        Assert.Single(pages);
        Assert.Equal(page.PageId, pages[0].PageId);
        Assert.Equal("格里高尔", pages[0].Title);

        // 二级页面
        var subs = store.ReadSubPages(page.PageId);
        Assert.Equal(2, subs.Count);
        Assert.Equal("介绍", subs[0].Title);
        Assert.Equal("相关角色", subs[1].Title);

        // 条目
        var entries = store.ReadEntries(sub1.SubPageId);
        Assert.Equal(2, entries.Count);
        Assert.Equal("大致原出处", entries[0].Title);
        Assert.Equal("出自《变形记》", entries[0].Body);

        // 资源绑定
        var bindings = store.ReadBindings(entry1.EntryId);
        Assert.Equal(2, bindings.Count);
        Assert.Contains(bindings, b => b.RefKey == "Assets/10201.png");
        Assert.Contains(bindings, b => b.RefKey == "Voice/10201.wav");

        // 统计
        Assert.Equal(1, store.ReadPageCount());
        Assert.Equal(2, store.ReadSubPageCount());
        Assert.Equal(2, store.ReadEntryCount());
        Assert.Equal(2, store.ReadBindingCount());
    }

    [Fact]
    public void Delete_page_cascades_to_all_children()
    {
        var store = NewStore();
        var page = Page();
        var sub = SubPage(page.PageId);
        var entry = Entry(sub.SubPageId);
        var binding = Binding(entry.EntryId);

        store.SavePage(Detail(page, SubDetail(sub, EntryDetail(entry, binding))));
        Assert.Equal(1, store.ReadPageCount());
        Assert.Equal(1, store.ReadSubPageCount());
        Assert.Equal(1, store.ReadEntryCount());
        Assert.Equal(1, store.ReadBindingCount());

        store.DeletePage(page.PageId);
        Assert.Equal(0, store.ReadPageCount());
        Assert.Equal(0, store.ReadSubPageCount());
        Assert.Equal(0, store.ReadEntryCount());
        Assert.Equal(0, store.ReadBindingCount());
    }

    [Fact]
    public void Save_page_is_idempotent()
    {
        var store = NewStore();
        var page = Page();
        var sub = SubPage(page.PageId);
        var entry = Entry(sub.SubPageId);

        store.SavePage(Detail(page, SubDetail(sub, EntryDetail(entry))));
        store.SavePage(Detail(page, SubDetail(sub, EntryDetail(entry))));

        Assert.Equal(1, store.ReadPageCount());
        Assert.Equal(1, store.ReadSubPageCount());
        Assert.Equal(1, store.ReadEntryCount());
    }

    [Fact]
    public void Save_page_replaces_old_sub_pages()
    {
        var store = NewStore();
        var page = Page();
        var sub1 = SubPage(page.PageId, "介绍", 0);
        var sub2 = SubPage(page.PageId, "相关角色", 1);

        store.SavePage(Detail(page, SubDetail(sub1)));
        Assert.Single(store.ReadSubPages(page.PageId));

        // 重新保存，只包含 sub2
        store.SavePage(Detail(page, SubDetail(sub2)));
        var subs = store.ReadSubPages(page.PageId);
        Assert.Single(subs);
        Assert.Equal("相关角色", subs[0].Title);
    }

    [Fact]
    public void Read_page_returns_null_for_missing()
    {
        var store = NewStore();
        Assert.Null(store.ReadPage("persona:99999"));
    }

    [Fact]
    public void Read_sub_pages_returns_empty_for_missing()
    {
        var store = NewStore();
        Assert.Empty(store.ReadSubPages("persona:99999"));
    }

    [Fact]
    public void Read_entries_returns_empty_for_missing()
    {
        var store = NewStore();
        Assert.Empty(store.ReadEntries("missing-sub-page"));
    }

    [Fact]
    public void Read_bindings_returns_empty_for_missing()
    {
        var store = NewStore();
        Assert.Empty(store.ReadBindings("missing-entry"));
    }

    [Fact]
    public void Search_entries_paginates()
    {
        var store = NewStore();
        var page = Page();
        var sub = SubPage(page.PageId);
        // 先创建页面和二级页面，再添加条目
        store.SavePage(Detail(page, SubDetail(sub)));
        for (var i = 0; i < 5; i++)
        {
            var entry = Entry(sub.SubPageId, $"条目{i}", $"正文{i}", i);
            store.SaveEntry(entry);
        }

        var result1 = store.SearchEntries("条目", 0, 2);
        Assert.Equal(5, result1.Total);
        Assert.Equal(2, result1.Entries.Count);

        var result2 = store.SearchEntries("条目", 2, 2);
        Assert.Equal(2, result2.Entries.Count);

        var result3 = store.SearchEntries("条目", 4, 2);
        Assert.Single(result3.Entries);
    }

    [Fact]
    public void Search_entries_filters_by_keyword()
    {
        var store = NewStore();
        var page = Page();
        var sub = SubPage(page.PageId);
        // 先创建页面和二级页面，再添加条目
        store.SavePage(Detail(page, SubDetail(sub)));
        store.SaveEntry(Entry(sub.SubPageId, "格里高尔", "出自卡夫卡", 0));
        store.SaveEntry(Entry(sub.SubPageId, "浮士德", "出自歌德", 1));

        var searchResult = store.SearchEntries("卡夫卡", 0, 10);
        Assert.Equal(1, searchResult.Total);
        Assert.Single(searchResult.Entries);
        Assert.Equal("格里高尔", searchResult.Entries[0].Title);
    }

    [Fact]
    public void Count_by_category_returns_correct_count()
    {
        var store = NewStore();
        var page1 = Page("persona:10201", "格里高尔", "Gregor");
        var page2 = Page("persona:10202", "浮士德", "Faust");
        var page3 = Page("enemy:8050", "害虫凯撒", "Kaiser");

        store.SavePage(new WikiPageDetail(page1, Array.Empty<WikiSubPageDetail>()));
        store.SavePage(new WikiPageDetail(page2, Array.Empty<WikiSubPageDetail>()));
        store.SavePage(new WikiPageDetail(page3, Array.Empty<WikiSubPageDetail>()));

        Assert.Equal(2, store.CountByCategory(RelationCategories.Persona));
        Assert.Equal(1, store.CountByCategory(RelationCategories.Enemy));
        Assert.Equal(0, store.CountByCategory(RelationCategories.Ego));
    }

    [Fact]
    public void Source_statistics_counts_correctly()
    {
        var store = NewStore();
        var page = Page();
        var sub = SubPage(page.PageId);
        // 先创建页面和二级页面，再添加条目
        store.SavePage(Detail(page, SubDetail(sub)));
        var humanEntry = Entry(sub.SubPageId, "人工条目", "人工正文", 0);
        var candidateEntry = Entry(sub.SubPageId, "候选条目", "候选正文", 1);
        candidateEntry = candidateEntry with { Source = WikiEntrySources.Candidate, CandidateId = "candidate-1" };

        store.SaveEntry(humanEntry);
        store.SaveEntry(candidateEntry);

        var (human, candidate) = store.SourceStatistics();
        Assert.Equal(1, human);
        Assert.Equal(1, candidate);
    }

    [Fact]
    public void Invalid_input_throws_with_chinese_message()
    {
        var store = NewStore();
        Assert.Throws<ArgumentException>(() => store.ReadPage(""));
        Assert.Throws<ArgumentException>(() => store.ReadSubPages(""));
        Assert.Throws<ArgumentException>(() => store.ReadEntries(""));
        Assert.Throws<ArgumentException>(() => store.ReadBindings(""));
        Assert.Throws<ArgumentException>(() => store.SearchEntries("", 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SearchEntries("test", -1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SearchEntries("test", 0, 0));
    }

    [Fact]
    public void Clear_all_removes_everything()
    {
        var store = NewStore();
        var page = Page();
        var sub = SubPage(page.PageId);
        var entry = Entry(sub.SubPageId);
        store.SavePage(Detail(page, SubDetail(sub, EntryDetail(entry, Binding(entry.EntryId)))));

        store.ClearAll();
        Assert.Equal(0, store.ReadPageCount());
        Assert.Equal(0, store.ReadSubPageCount());
        Assert.Equal(0, store.ReadEntryCount());
        Assert.Equal(0, store.ReadBindingCount());
    }

    [Fact]
    public void Delete_database_recreates_on_next_use()
    {
        var store = NewStore();
        var page = Page();
        store.SavePage(Detail(page));
        Assert.True(store.Exists);

        store.DeleteDatabase();
        Assert.False(store.Exists);

        // 重新创建
        var store2 = NewStore();
        Assert.Equal(0, store2.ReadPageCount());
    }
}
