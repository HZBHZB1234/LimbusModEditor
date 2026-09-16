using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.Relations.Authority;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// <see cref="WikiPageStore.SaveGeneratedPage"/>（生成落库路径）的语义测试。
///
/// <para>这是「幂等 + 绝不覆盖用户修订」两条硬口径的守护测试：
/// 生成会重跑很多次，用户改过的条目一次都不能丢。</para>
/// </summary>
public sealed class WikiGeneratedPageSaveTests : IDisposable
{
    private readonly string _cacheDir;

    public WikiGeneratedPageSaveTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "lme-wikigen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_cacheDir, true); } catch (Exception) { /* 临时目录 */ }
    }

    private WikiPageStore NewStore() => new(_cacheDir);

    /// <summary>造一棵「1 页 / 1 分节 / 2 条目」的树；条目 id 用稳定 id（与生成服务同一算法）。</summary>
    private static WikiPageDetail Tree(string bodyA = "A 正文", string bodyB = "B 正文")
    {
        var page = new WikiPage("persona:10201", RelationCategories.Persona, "格里高尔", "Gregor", "10201");
        var subPageId = WikiStableIds.Of(page.PageId, "立绘与图集", "0");
        var entries = new List<WikiEntryDetail>();
        foreach (var (refKey, body, order) in new[]
                 {
                     ("Assets/10201_profile.png", bodyA, 0),
                     ("Assets/10201_cg.png", bodyB, 1),
                 })
        {
            var entryId = WikiStableIds.Of(subPageId, refKey);
            var entry = new WikiEntry(entryId, subPageId, refKey, body, order)
            {
                Source = WikiEntrySources.Auto,
                Authority = nameof(AuthoritySource.ContainerPathPrefix),
                Confidence = nameof(ConfidenceLevel.Authoritative),
                WritableSource = nameof(WritableSourceKind.Path),
                WritableSourcePath = refKey,
            };
            var binding = new WikiResourceBinding(WikiStableIds.Of(entryId, refKey), entryId, refKey, "Image", refKey, 0);
            entries.Add(new WikiEntryDetail(entry, [binding]));
        }
        return new WikiPageDetail(page, [new WikiSubPageDetail(new WikiSubPage(subPageId, page.PageId, "立绘与图集", 0), entries)]);
    }

    [Fact]
    public void Stable_ids_are_deterministic_and_content_keyed()
    {
        var first = WikiStableIds.Of("persona:10201", "立绘与图集", "0");
        var second = WikiStableIds.Of("persona:10201", "立绘与图集", "0");
        var other = WikiStableIds.Of("persona:10202", "立绘与图集", "0");

        Assert.Equal(first, second);          // 同内容 → 同 id（幂等的前提）
        Assert.NotEqual(first, other);        // 不同内容 → 不同 id（不串页）
    }

    [Fact]
    public void Generating_twice_does_not_duplicate_rows()
    {
        var store = NewStore();
        var first = store.SaveGeneratedPage(Tree());
        var second = store.SaveGeneratedPage(Tree());

        Assert.Equal(2, first.Entries);
        Assert.Equal(2, second.Entries);

        // 关键：库里的行数不因第二次生成而翻倍
        Assert.Equal(1, store.ReadPageCount());
        Assert.Equal(1, store.ReadSubPageCount());
        Assert.Equal(2, store.ReadEntryCount());
        Assert.Equal(2, store.ReadBindingCount());
    }

    [Fact]
    public void Revised_entries_survive_a_regeneration()
    {
        var store = NewStore();
        store.SaveGeneratedPage(Tree());

        // 用户改了第一条（前端 wiki.saveContent 走的正是这条路）
        var subPageId = WikiStableIds.Of("persona:10201", "立绘与图集", "0");
        var entryId = WikiStableIds.Of(subPageId, "Assets/10201_profile.png");
        var before = store.ReadEntry(entryId);
        Assert.NotNull(before);

        store.SaveEntry(before with { Body = "用户改写过的正文", Source = WikiEntrySources.Revised });
        Assert.Equal(1, store.SourceStatistics().Revised);

        // 再生成一次：auto 的被重写，revised 的那条必须原样留着
        var save = store.SaveGeneratedPage(Tree(bodyA: "生成器重新算出的正文"));

        Assert.Equal(1, save.RevisedPreserved);
        var after = store.ReadEntry(entryId);
        Assert.NotNull(after);
        Assert.Equal("用户改写过的正文", after.Body);
        Assert.Equal(WikiEntrySources.Revised, after.Source);

        // 没被改过的那条照常更新
        var untouched = store.ReadEntry(WikiStableIds.Of(subPageId, "Assets/10201_cg.png"));
        Assert.NotNull(untouched);
        Assert.Equal("B 正文", untouched.Body);
        Assert.Equal(WikiEntrySources.Auto, untouched.Source);
    }

    [Fact]
    public void Empty_sections_are_not_written()
    {
        var store = NewStore();
        var page = new WikiPage("persona:10201", RelationCategories.Persona, "格里高尔", "Gregor", "10201");
        var saved = store.SaveGeneratedPage(new WikiPageDetail(page, []));

        Assert.Equal(0, saved.SubPages);
        Assert.Equal(1, store.ReadPageCount());
        Assert.Equal(0, store.ReadSubPageCount());      // 主页面建了，但没有空分节占位
    }

    [Fact]
    public void Authority_columns_round_trip()
    {
        var store = NewStore();
        store.SaveGeneratedPage(Tree());

        var subPageId = WikiStableIds.Of("persona:10201", "立绘与图集", "0");
        var entry = store.ReadEntry(WikiStableIds.Of(subPageId, "Assets/10201_profile.png"));

        Assert.NotNull(entry);
        Assert.Equal(nameof(AuthoritySource.ContainerPathPrefix), entry.Authority);
        Assert.Equal(nameof(ConfidenceLevel.Authoritative), entry.Confidence);
        Assert.Equal(nameof(WritableSourceKind.Path), entry.WritableSource);
        Assert.True(entry.Editable);                    // 只有 Path 可写
    }

    [Fact]
    public void Entries_without_a_writable_source_are_read_only()
    {
        var entry = new WikiEntry("x", "s", "标题", string.Empty, 0)
        {
            Authority = nameof(AuthoritySource.Unknown),
            Confidence = nameof(ConfidenceLevel.None),
            WritableSource = nameof(WritableSourceKind.Unknown),
        };

        Assert.False(entry.Editable);
        Assert.True(new WikiEntry("x", "s", "标题", string.Empty, 0)
        {
            WritableSource = nameof(WritableSourceKind.Path),
        }.Editable);
    }
}
