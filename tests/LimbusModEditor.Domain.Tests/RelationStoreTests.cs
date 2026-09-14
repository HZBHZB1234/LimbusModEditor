using LimbusModEditor.Application.Relations;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// <c>cache/relation-index.db</c>（派生关联图）的语义测试。
///
/// <para>覆盖：图往返（对象 / 关联 / 反向索引）、<b>四个上游源任一变化即整库重建</b>、
/// 源未变时不重建，以及「派生缓存只影响速度」的前提——删库即重算。</para>
/// </summary>
public sealed class RelationStoreTests : IDisposable
{
    private readonly string _cacheDir;

    public RelationStoreTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "lme-relation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_cacheDir, true); } catch (Exception) { /* 临时目录 */ }
    }

    private RelationStore NewStore() => new(_cacheDir);

    private static RelationIndexSource Source(
        string unity = "1459:100:999", string bank = "0:200", string statics = "10:20", string text = "v4|1:2|hash|dir")
        => RelationIndexSource.From(unity, bank, statics, text);

    private static RelationSubject Subject(string id, string name = "浮士德 · LCB", string style = "LCB")
        => new(id, RelationCategories.Persona, name, style, "Faust", id);

    private static RelationLink Link(string id, RelationKind kind, string reference, long size = 10, string? detail = null)
        => new(id, RelationCategories.Persona, kind, reference, reference, detail, size);

    private static RelationGraph Graph() => new(
        [Subject("10201"), Subject("10202")],
        [
            Link("10201", RelationKind.Text, "PersonalityVoiceDlg/Voice_Faust_LCB_10201.json"),
            Link("10201", RelationKind.Image, "Assets/Sprite/Unit/Profile/10201.png", detail: "精灵图 · 200 B"),
            Link("10201", RelationKind.Audio, @"C:\game\Voice_Default_S5.bank" + "\u0000" + "voice_faust_10201_1"),
            Link("10202", RelationKind.Text, "PersonalityVoiceDlg/Voice_Faust_LCB_10202.json"),
            Link("10202", RelationKind.Spine, "Assets/Prefab/SpineIllustPrefab/10202_gacksung.prefab"),
        ]);

    [Fact]
    public void Graph_round_trips_with_links_and_reverse_index()
    {
        var store = NewStore();
        var source = Source();
        Assert.True(store.EnsureSource(source)); // 首次 = 需要重算
        store.PersistGraph(source, Graph());

        var subjects = store.ReadSubjects();
        Assert.Equal(2, subjects.Count);
        Assert.Equal("10201", subjects[0].SubjectId); // sort_key 序
        Assert.Equal("LCB", subjects[0].Subtitle);
        Assert.Equal(2, store.ReadSubjectCount());
        Assert.Equal(5, store.ReadLinkCount());

        var links = store.ReadLinks("10201");
        Assert.Equal(3, links.Count);
        Assert.Contains(links, x => x.Kind == RelationKind.Image && x.Detail == "精灵图 · 200 B");

        var textOnly = store.ReadLinks("10201", RelationKind.Text);
        Assert.Single(textOnly);
        Assert.Equal(RelationKind.Text, textOnly[0].Kind);

        // 反向索引：给定一个正在预览的资源，立刻知道它属于哪些人格。
        Assert.Equal(["10201"], store.ReadSubjectIdsByRef("PersonalityVoiceDlg/Voice_Faust_LCB_10201.json"));
        Assert.Empty(store.ReadSubjectIdsByRef("not-related.json"));

        // 二次使用：源与签名都没变 → 不需要重算，图还在。
        Assert.False(store.EnsureSource(source));
        Assert.Equal(5, store.ReadLinkCount());
        Assert.Equal("relations", store.ReadSourceKey());
    }

    [Fact]
    public void Any_upstream_change_rebuilds_the_whole_graph()
    {
        var store = NewStore();
        var first = Source();
        store.EnsureSource(first);
        store.PersistGraph(first, Graph());
        Assert.Equal(5, store.ReadLinkCount());

        // 四个上游里任意一个换源（换游戏目录 / 换活动语言 / 热修换内容哈希 / 重扫 bundle）
        // 都必须整库重建——否则会拿旧图当新鲜。
        Assert.True(store.EnsureSource(Source(bank: "0:999")));
        Assert.Equal(0, store.ReadLinkCount());
        Assert.Equal(0, store.ReadSubjectCount());
    }

    [Fact]
    public void Persisting_the_same_graph_twice_does_not_duplicate_rows()
    {
        var store = NewStore();
        var source = Source();
        store.EnsureSource(source);
        store.PersistGraph(source, Graph());
        store.PersistGraph(source, Graph());

        Assert.Equal(5, store.ReadLinkCount());
        Assert.Equal(2, store.ReadSubjectCount());
    }

    [Fact]
    public void Deleting_the_database_only_costs_speed()
    {
        var store = NewStore();
        var source = Source();
        store.EnsureSource(source);
        store.PersistGraph(source, Graph());
        Assert.True(store.Exists);

        store.DeleteDatabase();

        // 删掉只是下次要重算：源一致判定失效 → 调用方重新分析。
        Assert.True(store.EnsureSource(source));
        Assert.Equal(0, store.ReadLinkCount());
    }

    [Fact]
    public void Index_source_signature_depends_on_every_upstream()
    {
        var baseline = Source();
        Assert.Equal(baseline.Signature, Source().Signature);
        Assert.NotEqual(baseline.Signature, Source(unity: "1:2:3").Signature);
        Assert.NotEqual(baseline.Signature, Source(bank: "x").Signature);
        Assert.NotEqual(baseline.Signature, Source(statics: "x").Signature);
        Assert.NotEqual(baseline.Signature, Source(text: "x").Signature);
    }

    /// <summary>
    /// **每一列都要往返**。这条测试的存在理由：v2 加了 10 个列 + 一张 xref 表，
    /// 而「建表有列、写入语句忘了带上」不会报错——只会静默存成 NULL，
    /// 表现成「卡片没有预览、跳不过去、跨资源边一条都没有」。
    /// 所以这里逐列断言，而不是只数行数。
    /// </summary>
    [Fact]
    public void Persist_round_trips_every_v2_column_and_the_xref_table()
    {
        var store = NewStore();
        var source = Source();
        store.EnsureSource(source);

        var subject = new RelationSubject(
            "ego:20101", RelationCategories.Ego, "乌瞰刀", "李箱的基础E.G.O装备", string.Empty, "020101")
        {
            CategoryLabel = "E.G.O 装备",
            CoverRef = "Assets/Sprite/Unit/Profile/Ego/20101.png",
            PreviewText = "乌瞰刀",
            LinkCount = 2,
        };
        const string bank = @"C:\game\Voice_Default_S5.bank";
        const string sample = "get_20101_1";
        var text = new RelationLink(
            "ego:20101", RelationCategories.Ego, RelationKind.Text, "Egos.json", "Egos.json", "E.G.O 装备定义", 5)
        {
            PreviewText = "乌瞰刀",
            PreviewKind = RelationPreviewKind.Exact,
            MediaKind = "text",
            RefPath = "Egos.json",
            DeepLink = RelationDeepLink.ForText("Egos.json", "20101"),
            TargetSubjectId = "ego:20101",
        };
        var audio = new RelationLink(
            "ego:20101", RelationCategories.Ego, RelationKind.Audio, bank + "\u0000" + sample, sample, "Vorbis · 100 B", 100)
        {
            PreviewKind = RelationPreviewKind.Derived,
            MediaKind = "audio",
            DurationSec = 2.5,
            RefPath = bank,
            DeepLink = RelationDeepLink.ForAudio(bank, sample),
        };
        var xref = new RelationXref("Egos.json", "ego:20101", RelationXrefKinds.TextToSubject,
            "text", "subject", nameof(RelationPreviewKind.Exact), "乌瞰刀");

        store.PersistGraph(source, new RelationGraph([subject], [text, audio]) { Xrefs = [xref] });

        var readSubject = Assert.Single(store.ReadSubjects());
        Assert.Equal("ego:20101", readSubject.SubjectId);
        Assert.Equal(RelationCategories.Ego, readSubject.SubjectKind);
        Assert.Equal("E.G.O 装备", readSubject.CategoryLabel);
        Assert.Equal("乌瞰刀", readSubject.DisplayName);
        Assert.Equal("李箱的基础E.G.O装备", readSubject.Subtitle);
        Assert.Equal("Assets/Sprite/Unit/Profile/Ego/20101.png", readSubject.CoverRef);
        Assert.Equal("乌瞰刀", readSubject.PreviewText);
        Assert.Equal(2, readSubject.LinkCount);

        var links = store.ReadLinks("ego:20101");
        var readText = Assert.Single(links, x => x.Kind == RelationKind.Text);
        Assert.Equal(RelationPreviewKind.Exact, readText.PreviewKind);
        Assert.Equal("乌瞰刀", readText.PreviewText);
        Assert.Equal("text", readText.MediaKind);
        Assert.Equal("Egos.json", readText.RefPath);
        Assert.Equal("20101", RelationDeepLink.Part(readText.DeepLink, 1));
        Assert.Equal("ego:20101", readText.TargetSubjectId);
        Assert.Null(readText.DurationSec);

        var readAudio = Assert.Single(links, x => x.Kind == RelationKind.Audio);
        Assert.Equal(RelationPreviewKind.Derived, readAudio.PreviewKind);
        Assert.Equal("audio", readAudio.MediaKind);
        Assert.Equal(2.5, readAudio.DurationSec!.Value);
        Assert.Equal(bank, readAudio.RefPath);

        // 跨资源边：落库、可双向查、可按关系过滤。
        Assert.Equal(1, store.ReadXrefCount());
        var edge = Assert.Single(store.ReadXrefs("Egos.json"));
        Assert.Equal("ego:20101", edge.ToRef);
        Assert.Equal(RelationXrefKinds.TextToSubject, edge.Relation);
        Assert.Equal("乌瞰刀", edge.Detail);
        Assert.Equal(nameof(RelationPreviewKind.Exact), edge.Confidence);
        Assert.Single(store.ReadXrefsTo("ego:20101"));
        Assert.Empty(store.ReadXrefs("Egos.json", RelationXrefKinds.AudioToSubject));
    }

    [Fact]
    public void LinksOf_filters_and_orders_by_kind_then_ref()
    {
        var graph = Graph();
        var links = graph.LinksOf("10201");
        Assert.Equal(3, links.Count);
        // 排序口径：先按 RelationKind 的枚举序（Text < Audio < Image），再按定位键。
        Assert.Equal([RelationKind.Text, RelationKind.Audio, RelationKind.Image], links.Select(x => x.Kind).ToArray());
    }
}
