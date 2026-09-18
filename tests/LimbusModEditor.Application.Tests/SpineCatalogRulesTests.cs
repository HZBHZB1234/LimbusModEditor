using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.SpineData;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 「未被页面绑定的 Spine」这条链路的<b>纯规则</b>回归：判据、归类、名册口径。
///
/// <para>背景（见 <c>docs/SPINE-DATA-REPORT.md</c> 与 <c>artifacts/workbuddy-reports/spine-unbound.md</c>）：
/// Spine 三件套一直都在本地缓存里，但战斗用的那批挂在
/// <c>Assets/Resources_moved/Prefab/SD/**</c>，<b>路径名里没有任何 Spine 字样</b>，
/// 因此被旧判据全部挡在取数之前 —— 实测抽样 24 个「未绑定」候选，0 个进得了引用链。
/// 本文件锁住「这批路径必须被放行」以及「放行 ≠ 认定它是 Spine」这两件事。</para>
/// </summary>
public sealed class SpineCatalogRulesTests
{
    // ── 判据：SD 目录必须放行（本轮补上的那条）────────────────────

    [Theory]
    // 真实路径（跑真实缓存时打印出来的原文）
    [InlineData("Assets/Resources_moved/Prefab/SD/Personality/10103_Yisang_SwordGroupAppearance.prefab")]
    [InlineData("Assets/Resources_moved/Prefab/SD/Enemy/90215_Heishou_Si_mob_AAppearance.prefab")]
    [InlineData("Assets/Resources_moved/Prefab/SD/Abnormality/8002_Aztec_CircleAppearance.prefab")]
    [InlineData("Assets/Resources_moved/Prefab/SD/EGO/ErosionAppearance_2020421.prefab")]
    [InlineData("Assets/Resources_moved/Prefab/SD/400006_VespaAppearance.prefab")]
    [InlineData("Prefab/SD/Enemy/1159_WangChungSanAppearance.prefab")]
    public void Sd_appearance_prefabs_are_admitted_by_the_spine_path_rule(string entry)
        => Assert.True(RelationDisplayRules.IsSpinePath(entry), $"{entry} 应当被放行（否则永远进不了引用链）");

    [Theory]
    // 早已支持的那几处不能被这轮改动弄丢
    [InlineData("Assets/Resources_moved/Prefab/SpineIllustPrefab/10103_gacksung.prefab")]
    [InlineData("Assets/Resources_moved/Story/StandingModel/foo.psb")]
    [InlineData("Assets/Resources_moved/Story/CG/Ep8_3/bar_SkeletonData.asset")]
    [InlineData("Assets/Resources_moved/Story/Spine/baz.json")]
    [InlineData("Assets/Resources_moved/Story/CG/Ep9_3/StorySpine_Sinclair.png")]
    public void Existing_spine_locations_still_pass_the_rule(string entry)
        => Assert.True(RelationDisplayRules.IsSpinePath(entry), $"{entry} 是既有支持的位置，不能被回归掉");

    [Theory]
    // SD 目录里只有 .prefab 才是挂点：同目录的图/音频不是
    [InlineData("Assets/Resources_moved/Prefab/SD/Enemy/90015_GCorp_Infested_Bob.png")]
    [InlineData("Assets/Resources_moved/Prefab/SD/Enemy/some_voice.wav")]
    [InlineData("Assets/Resources_moved/Prefab/SD/Enemy/some.asset")]
    // 别的目录里的 prefab 不受影响（这一轮只放行 SD）
    [InlineData("Assets/Resources_moved/Prefab/Battle/Skill/Template/SomeSkill.prefab")]
    [InlineData("Assets/Prefab/Dungeon/Mirror/Background/MirrorDungeonBackground_MOWE.prefab")]
    [InlineData("Assets/Prefab/Effect/Mon/Boss/FX_Mon_Boss_SDGlass1.prefab")]
    public void Non_sd_prefabs_are_not_admitted_by_this_rounds_rule(string entry)
        => Assert.False(RelationDisplayRules.IsSpinePath(entry), $"{entry} 不该被这一轮放行");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_path_is_never_spine(string? entry)
        => Assert.False(RelationDisplayRules.IsSpinePath(entry));

    /// <summary>
    /// <b>放行 ≠ 认定它是 Spine</b>：判据只决定「值不值得试一次引用链」，
    /// 到底是不是 Spine 一律由 <c>SpinePrefabChainResolver</c> 按<b>正文</b>判定。
    /// 这条锁住「SD 里确实有不是 Spine 的 prefab」，所以名册里必然存在
    /// 取数时报「引用链里没有骨架与图集」的条目 —— 那是正确结果，不是 bug。
    /// </summary>
    [Fact]
    public void Admitting_the_sd_folder_does_not_claim_every_sd_prefab_is_spine()
    {
        // 同一个目录下既有能解出骨架的，也有解不出的（实测 SD/Personality 12 个里 3 个命中）。
        // 判据对两者一视同仁地放行 —— 判定留给正文。
        Assert.True(RelationDisplayRules.IsSpinePath(
            "Assets/Resources_moved/Prefab/SD/Personality/10103_Yisang_SwordGroupAppearance.prefab"));
        Assert.True(RelationDisplayRules.IsSpinePath(
            "Assets/Resources_moved/Prefab/SD/Personality/10102_YiSang_7Appearance.prefab"));
    }

    // ── 归类：名册的中文栏目 ────────────────────────────────────

    [Theory]
    [InlineData("Assets/Resources_moved/Prefab/SpineIllustPrefab/10103_gacksung.prefab", "人格立绘")]
    [InlineData("Assets/Resources_moved/Prefab/SD/Enemy/90215_Heishou_Si_mob_AAppearance.prefab", "敌方单位")]
    [InlineData("Assets/Resources_moved/Prefab/SD/Abnormality/8002_Aztec_CircleAppearance.prefab", "异想体")]
    [InlineData("Assets/Resources_moved/Prefab/SD/Personality/10103_Yisang_SwordGroupAppearance.prefab", "人格(战斗)")]
    [InlineData("Assets/Resources_moved/Prefab/SD/EGO/ErosionAppearance_2020421.prefab", "E.G.O")]
    [InlineData("Assets/Resources_moved/Prefab/SD/400006_VespaAppearance.prefab", "战斗其它")]
    [InlineData("Assets/Resources_moved/Story/Spine/whatever.json", "其它")]
    public void Grouping_puts_each_entry_in_a_readable_chinese_bucket(string entry, string expected)
        => Assert.Equal(expected, SpineCatalogGroups.Of(entry));

    // ── 名册查询形状（不碰真实 bundle）───────────────────────────

    /// <summary>名册查询为空库时给空结果，不抛（前端拿到空名册而不是一个红条）。</summary>
    [Fact]
    public async Task Catalog_query_on_a_missing_database_returns_an_empty_page_instead_of_throwing()
    {
        var gateway = SpineDataGatewayFactory.Create(
            Path.Combine(Path.GetTempPath(), $"lme-no-such-index-{Guid.NewGuid():N}.db"));

        var page = await gateway.BrowseCatalogAsync(new SpineCatalogQuery(null, true, 0, 10));
        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);

        var summary = await gateway.SummarizeCatalogAsync();
        Assert.Equal(0, summary.Total);
        Assert.Equal(0, summary.Unbound);
    }

    /// <summary>不存在的路径按现有口径给中文原因，不抛、不编造数据。</summary>
    [Fact]
    public async Task Unknown_path_reports_a_chinese_reason()
    {
        var gateway = SpineDataGatewayFactory.Create(
            Path.Combine(Path.GetTempPath(), $"lme-no-such-index-{Guid.NewGuid():N}.db"));

        var (data, error) = await gateway.GetSpineDataByPathAsync("有/但/不存在.prefab");
        Assert.Null(data);
        Assert.NotNull(error);
        Assert.Contains("不是 Spine 资源", error);
    }

    /// <summary>空路径给中文原因（既有行为，锁住）。</summary>
    [Fact]
    public async Task Empty_path_reports_a_chinese_reason()
    {
        var gateway = SpineDataGatewayFactory.Create(
            Path.Combine(Path.GetTempPath(), $"lme-no-such-index-{Guid.NewGuid():N}.db"));

        var (data, error) = await gateway.GetSpineDataByPathAsync("   ");
        Assert.Null(data);
        Assert.Contains("无法获取 Spine 数据", error);
    }
}
