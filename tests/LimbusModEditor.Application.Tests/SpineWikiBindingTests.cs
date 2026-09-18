using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.SpineData;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 「未绑定 Spine 自动接入维基」（方案 A）的规则回归。
///
/// <para>本文件锁住三件最容易写错、且错了会静默伤数据的事：</para>
/// <list type="number">
/// <item><b>不许编造关系</b>：归属只能来自关联索引的反查表；查不到就留在「未归类」，
/// 绝不按文件名数字前缀硬塞给某个角色（实测该规则 50/174 与真值冲突）。</item>
/// <item><b>不许吃掉既有分节</b>：接入只往「Spine 与动画」这一节写，
/// 页面上的文本 / 语音 / 立绘 / 剧情必须原样保留。</item>
/// <item><b>幂等</b>：重跑落同一批行，计数不变。</item>
/// </list>
/// </summary>
public sealed class SpineWikiBindingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lme-spine-wiki-" + Guid.NewGuid().ToString("N"));

    public SpineWikiBindingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* 临时目录清理失败不影响断言 */ }
        GC.SuppressFinalize(this);
    }

    private WikiPageStore NewStore() => new(_root);

    // ── 状态判据（名册 / 明细共用一份，避免两处措辞漂移）────────────

    [Fact]
    public void Catalog_status_never_claims_parsed_before_actually_resolving()
    {
        // 名册阶段一个 bundle 都没解过，所以只能说「可解析」/「bundle 不在」/「未归类」，
        // 绝不能说「已解析出三件套」—— 那是真的解出来之后才配说的话。
        Assert.Equal(SpineStatusRules.Likely, SpineStatusRules.CatalogStatus(bundlePresent: true, ownerCount: 3));
        Assert.Equal(SpineStatusRules.BundleMissing, SpineStatusRules.CatalogStatus(bundlePresent: false, ownerCount: 3));
        Assert.Equal(SpineStatusRules.Uncategorized, SpineStatusRules.CatalogStatus(bundlePresent: true, ownerCount: 0));
        Assert.NotEqual(SpineStatusRules.Parsed, SpineStatusRules.CatalogStatus(true, 3));
    }

    [Fact]
    public void Resolve_status_separates_not_a_spine_from_a_real_failure()
    {
        // 「引用链里没有骨架与图集」是**正确结果**（该 prefab 本来就不是 Spine），
        // 不能和「取数失败」混为一谈 —— 前者界面该给中性提示，后者才是错误。
        Assert.Equal(SpineStatusRules.Parsed, SpineStatusRules.ResolveStatus(true, true, null));
        Assert.Equal(SpineStatusRules.BundleMissing,
            SpineStatusRules.ResolveStatus(false, bundlePresent: false, "bundle 文件不在本机。"));
        Assert.Equal(SpineStatusRules.NoSkeleton,
            SpineStatusRules.ResolveStatus(false, true, "同目录里找不到骨架 JSON（*.json），无法获取 Spine 数据。"));
        Assert.Equal(SpineStatusRules.NoSkeleton,
            SpineStatusRules.ResolveStatus(false, true, "路径 \"X\" 不是 Spine 资源。"));
        Assert.Equal(SpineStatusRules.Failed, SpineStatusRules.ResolveStatus(false, true, "磁盘写失败。"));
        Assert.Equal(SpineStatusRules.Failed, SpineStatusRules.ResolveStatus(false, true, null));
    }

    [Theory]
    [InlineData(true, 0, 3, "关联索引自动接入")]
    [InlineData(true, 2, 0, "既有页面绑定")]
    [InlineData(false, 0, 0, "bundle 不在本机")]
    [InlineData(true, 0, 0, "未归类")]
    public void Source_explains_where_a_hook_point_came_from(
        bool bundlePresent, int boundPages, int owners, string expected)
        => Assert.Contains(expected, SpineStatusRules.Source(bundlePresent, boundPages, owners), StringComparison.Ordinal);

    [Fact]
    public void Every_status_has_a_chinese_label()
    {
        foreach (var status in new[]
                 {
                     SpineStatusRules.Parsed, SpineStatusRules.Likely, SpineStatusRules.BundleMissing,
                     SpineStatusRules.NoSkeleton, SpineStatusRules.Uncategorized, SpineStatusRules.Failed,
                 })
        {
            var label = SpineStatusRules.Label(status);
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.NotEqual(status, label);   // 有中文说明，不是原样回显枚举值
        }
    }

    // ── 稳定 id（幂等的根）─────────────────────────────────────────

    [Fact]
    public void Spine_entry_and_binding_ids_are_stable_across_runs()
    {
        // 同一份内容两次算出的 id 必须相同，否则重跑就是堆重复行而不是 UPSERT。
        const string pageId = "persona:10113";
        const string refKey = "Assets/Resources_moved/Prefab/SD/10113_YiSang_DerSchutzeAppearance.prefab";

        var subPageId1 = WikiStableIds.Of(pageId, SpineWikiBindingService.SpineSectionTitle, SpineWikiBindingService.SpineSectionId);
        var subPageId2 = WikiStableIds.Of(pageId, SpineWikiBindingService.SpineSectionTitle, SpineWikiBindingService.SpineSectionId);
        Assert.Equal(subPageId1, subPageId2);

        Assert.Equal(WikiStableIds.Of(subPageId1, refKey), WikiStableIds.Of(subPageId2, refKey));
        // 不同挂点必须落到不同条目（否则会互相覆盖）
        Assert.NotEqual(WikiStableIds.Of(subPageId1, refKey),
            WikiStableIds.Of(subPageId1, refKey + "x"));
    }

    // ── 落库语义：不编造、不覆盖修订、不吃掉别的分节 ────────────────

    [Fact]
    public void Merging_spine_bindings_preserves_other_sections_and_revised_entries()
    {
        // 造一个已有页面：一个含**用户修订**条目的「文本」分节 + 一个普通「语音」分节。
        var store = NewStore();
        var page = new WikiPage("persona:10113", RelationCategories.Persona, "李箱", "YiSang", "010113");
        var textSub = new WikiSubPage("sub-text", page.PageId, "文本", 2);
        var audioSub = new WikiSubPage("sub-audio", page.PageId, "语音", 3);

        store.SaveGeneratedPage(new WikiPageDetail(page, [
            new WikiSubPageDetail(textSub, [
                new WikiEntryDetail(
                    new WikiEntry("entry-revised", textSub.SubPageId, "用户改过的条目", "正文", 0)
                    { Source = WikiEntrySources.Revised },
                    []),
            ]),
            new WikiSubPageDetail(audioSub, [
                new WikiEntryDetail(
                    new WikiEntry("entry-audio", audioSub.SubPageId, "语音", "", 0),
                    [new WikiResourceBinding("bind-audio", "entry-audio", "Audio/x.bnk", "Audio", "x", 0)]),
            ]),
        ]));

        // 接入一批 Spine 挂点：走真实的服务入口（反查表为空 → 全部「未归类」）。
        var service = new SpineWikiBindingService(new AppConfig.AppEnvironment(_root));
        var catalog = new List<SpineCatalogEntry>
        {
            new("Assets/Resources_moved/Prefab/SD/10113_YiSang_DerSchutzeAppearance.prefab",
                "10113_YiSang_DerSchutzeAppearance", "SD", "战斗其它", BundlePresent: true, BoundPageCount: 0),
        };
        var result = service.Attach(store, catalog);

        // 反查表不存在 → 不许硬塞给 persona:10113，全部记「未归类」。
        Assert.Equal(1, result.Uncategorized);
        Assert.Equal(0, result.Attached);

        // 既有内容必须完好：修订条目还在、语音绑定还在、分节还在。
        Assert.Equal("revised", store.ReadEntry("entry-revised")!.Source);
        Assert.Single(store.ReadBindings("entry-audio"));
        var titles = store.ReadSubPages(page.PageId).Select(s => s.Title).ToList();
        Assert.Contains("文本", titles);
        Assert.Contains("语音", titles);
    }

    [Fact]
    public void Attach_reports_uncategorized_instead_of_guessing_by_numeric_prefix()
    {
        // 这条例子的文件名前缀 10802 与页面 id 1081 毫不相干 —— 前缀规则会把它
        // 挂到 abnormality:10802（不存在）或硬塞给 abnormality:1081。
        // 正确行为：反查表里没有它 → 未归类，一个绑定都不落。
        var store = NewStore();
        store.SavePage(new WikiPageDetail(
            new WikiPage("abnormality:1081", RelationCategories.Abnormality, "Septempeccata", "", "001081"),
            [new WikiSubPageDetail(
                new WikiSubPage("sub-1", "abnormality:1081", "概览", 0),
                [new WikiEntryDetail(new WikiEntry("e-1", "sub-1", "摘要", "正文", 0), [])])]));

        var service = new SpineWikiBindingService(new AppConfig.AppEnvironment(_root));
        var catalog = new List<SpineCatalogEntry>
        {
            new("Assets/Resources_moved/Prefab/SD/Abnormality/10802_gacksung_thing.prefab",
                "10802_gacksung_thing", "SD", "异想体", BundlePresent: true, BoundPageCount: 0),
        };

        var result = service.Attach(store, catalog);

        Assert.Equal(1, result.Uncategorized);
        Assert.Equal(0, result.Attached);
        Assert.Empty(store.ReadBindings("e-1"));
        // 反查查不到时绝不能凭 id 造页
        Assert.Single(store.ReadPages());
    }

    [Fact]
    public void Attach_counts_bundle_missing_truthfully_without_faking_success()
    {
        var store = NewStore();
        var service = new SpineWikiBindingService(new AppConfig.AppEnvironment(_root));
        var catalog = new List<SpineCatalogEntry>
        {
            new("Assets/Resources_moved/Prefab/SD/Gone.prefab", "Gone", "SD", "战斗其它",
                BundlePresent: false, BoundPageCount: 0),
        };

        var result = service.Attach(store, catalog);

        Assert.Equal(1, result.BundleMissing);
        Assert.Equal(0, result.Attached);
    }

    [Fact]
    public void Already_bound_hook_points_are_left_untouched()
    {
        var store = NewStore();
        var service = new SpineWikiBindingService(new AppConfig.AppEnvironment(_root));
        var catalog = new List<SpineCatalogEntry>
        {
            new("Assets/Resources_moved/Prefab/SpineIllustPrefab/10103_gacksung.prefab",
                "10103_gacksung", "SpineIllustPrefab", "人格立绘", BundlePresent: true, BoundPageCount: 2),
        };

        var result = service.Attach(store, catalog);

        Assert.Equal(1, result.AlreadyBound);
        Assert.Equal(0, result.Unbound);
        Assert.Equal(0, result.Attached);
        Assert.Equal(0, store.ReadBindingCount());
    }

    [Fact]
    public void Cancellation_stops_cleanly_and_reports_it()
    {
        var store = NewStore();
        var service = new SpineWikiBindingService(new AppConfig.AppEnvironment(_root));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var catalog = new List<SpineCatalogEntry>
        {
            new("Assets/Resources_moved/Prefab/SD/A.prefab", "A", "SD", "战斗其它", true, 0),
        };

        var result = service.Attach(store, catalog, progress: null, cancellationToken: cts.Token);

        // 取消不是异常：结果里如实标出来，已写入的部分保留（重跑幂等续上）。
        Assert.True(result.Cancelled);
        Assert.Contains("已取消", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Result_summary_states_every_honest_boundary()
    {
        var result = new SpineWikiBindingService.SpineBindingResult(
            HookPoints: 941, AlreadyBound: 123, Unbound: 818, Attached: 746, AttachedBindings: 909,
            PagesUpdated: 711, BundleMissing: 111, Uncategorized: 60, TargetMissing: 0,
            RevisedPreserved: 1, Cancelled: false);

        var text = result.Describe();
        Assert.Contains("未归类 60", text, StringComparison.Ordinal);
        Assert.Contains("bundle 不在本机 111", text, StringComparison.Ordinal);
        Assert.Contains("保留修订 1", text, StringComparison.Ordinal);
        Assert.Equal(91.2, Math.Round(result.Coverage, 1));
    }

    [Fact]
    public void Coverage_with_no_unbound_work_is_full_not_a_divide_by_zero()
    {
        var result = new SpineWikiBindingService.SpineBindingResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false);
        Assert.Equal(100d, result.Coverage);
    }
}
