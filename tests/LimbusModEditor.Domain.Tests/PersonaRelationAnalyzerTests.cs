using LimbusModEditor.Application.Relations;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// 人格关联分析器（纯函数，无 IO）的语义测试。
///
/// <para>覆盖：身份发现（SD 人格预制体 / 语音文件名两处权威来源）、
/// 五类资源命中（文本 / 静态数据 / 音频 / 图像 / 视频 / Spine）、
/// 「连写编号要按窗口匹配」（<c>SkillIcon/1020101.png</c> = 人格 10201 + 技能 01）、
/// 以及<b>绝不凭空造人格</b>（未在身份集合里的 5 位数字不得产出关联）。</para>
/// </summary>
public sealed class PersonaRelationAnalyzerTests
{
    private const string SdPrefab = "Assets/Prefab/SD/Personality/10201_Faust_LCBAppearance.prefab";

    private static RelationInputs Inputs(
        IEnumerable<RelationAssetFact>? assets = null,
        IEnumerable<RelationAudioFact>? audio = null,
        IEnumerable<RelationStaticFact>? statics = null,
        IEnumerable<RelationLangFact>? lang = null)
        => new(
            assets?.ToArray() ?? [],
            audio?.ToArray() ?? [],
            statics?.ToArray() ?? [],
            lang?.ToArray() ?? []);

    [Fact]
    public void Discovers_identity_and_links_all_resource_kinds()
    {
        var inputs = Inputs(
            assets:
            [
                new RelationAssetFact(SdPrefab, AssetType.GameObject, 100),
                new RelationAssetFact("Assets/Sprite/Unit/Profile/10201.png", AssetType.Sprite, 200),
                // 连写：人格 10201 + 技能 01 —— 必须靠「取长度 5 的窗口」命中。
                new RelationAssetFact("Assets/Sprite/SkillIcon/1020101.png", AssetType.Sprite, 300),
                new RelationAssetFact("Assets/PersonalityVideo/10201.mp4", AssetType.Video, 400),
                new RelationAssetFact("Assets/Prefab/SpineIllustPrefab/10201_gacksung.prefab", AssetType.GameObject, 500),
            ],
            audio: [new RelationAudioFact(@"C:\game\Voice_Default_S5.bank", "voice_faust_10201_1", "Vorbis", 999)],
            statics: [new RelationStaticFact("Assets/x/personality/personality-02.json", "personality-02", "personality", "{\"id\":10201}", 10)],
            lang: [new RelationLangFact("PersonalityVoiceDlg/Voice_Faust_LCB_10201.json", 12)]);

        var graph = PersonaRelationAnalyzer.Analyze(inputs);

        var subject = Assert.Single(graph.Subjects);
        Assert.Equal("10201", subject.SubjectId);
        Assert.Equal(RelationCategories.Persona, subject.SubjectKind);
        Assert.Equal("浮士德 · LCB", subject.DisplayName);
        Assert.Equal("LCB", subject.Subtitle);
        Assert.Equal("Faust", subject.Character);
        Assert.Equal("10201", subject.SortKey);

        var kinds = graph.Links.Select(x => x.Kind).ToHashSet();
        Assert.Contains(RelationKind.Text, kinds);
        Assert.Contains(RelationKind.StaticData, kinds);
        Assert.Contains(RelationKind.Audio, kinds);
        Assert.Contains(RelationKind.Image, kinds);
        Assert.Contains(RelationKind.Video, kinds);
        Assert.Contains(RelationKind.Spine, kinds);

        // 音频的定位键是「bank 路径 + \0 + 样本名」（样本名跨 bank 不唯一）。
        var audioLink = Assert.Single(graph.Links, x => x.Kind == RelationKind.Audio);
        Assert.Equal(@"C:\game\Voice_Default_S5.bank" + "\u0000" + "voice_faust_10201_1", audioLink.RefKey);

        // 图像：Profile（rank 0）与 SkillIcon（rank 6）都在，且 Profile 排在前。
        var images = graph.Links.Where(x => x.Kind == RelationKind.Image)
            .OrderBy(x => x.RefKey, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, images.Length);

        // 语音台词文件按「文件名里的 5 位 id」精确命中。
        Assert.Contains(graph.Links, x => x.Kind == RelationKind.Text
            && x.RefKey == "PersonalityVoiceDlg/Voice_Faust_LCB_10201.json");

        // SD 人格预制体本身也是一条关联（prefab，且路径里带 id）。
        Assert.Contains(graph.Links, x => x.RefKey == SdPrefab);
    }

    [Fact]
    public void Never_invents_personas_from_unrelated_five_digit_numbers()
    {
        // 事件 id 10711 落在人格号段内，但**不在身份集合**里 → 不得产出关联。
        var inputs = Inputs(
            assets:
            [
                new RelationAssetFact(SdPrefab, AssetType.GameObject, 100),
                new RelationAssetFact("Assets/x/event/10711_event.json", AssetType.Text, 50),
                new RelationAssetFact("Assets/x/item/99999_unknown.json", AssetType.Text, 50),
            ]);

        var graph = PersonaRelationAnalyzer.Analyze(inputs);

        Assert.Single(graph.Subjects);
        Assert.All(graph.Links, x => Assert.Equal("10201", x.SubjectId));
        Assert.DoesNotContain(graph.Links, x => x.RefKey.Contains("10711", StringComparison.Ordinal));
        Assert.DoesNotContain(graph.Links, x => x.RefKey.Contains("99999", StringComparison.Ordinal));
    }

    [Fact]
    public void Character_dialogue_files_link_to_every_persona_of_that_character()
    {
        var inputs = Inputs(
            assets: [new RelationAssetFact(SdPrefab, AssetType.GameObject, 100)],
            lang:
            [
                // 文件名里没有 5 位 id：按角色挂到该角色的全部人格上。
                new RelationLangFact("AbDlg_Faust.json", 3),
                // 角色不匹配 → 不应挂到 Faust 的人格。
                new RelationLangFact("AbDlg_DonQuixote.json", 3),
            ]);

        var graph = PersonaRelationAnalyzer.Analyze(inputs);

        Assert.Contains(graph.Links, x => x.RefKey == "AbDlg_Faust.json");
        Assert.DoesNotContain(graph.Links, x => x.RefKey == "AbDlg_DonQuixote.json");
    }

    [Fact]
    public void Voice_file_name_supplies_identity_when_no_prefab_exists()
    {
        var inputs = Inputs(lang: [new RelationLangFact("PersonalityVoiceDlg/Voice_Outis_Wuthering_10408.json", 5)]);

        var graph = PersonaRelationAnalyzer.Analyze(inputs);

        var subject = Assert.Single(graph.Subjects);
        Assert.Equal("10408", subject.SubjectId);
        Assert.Equal("奥提斯 · Wuthering", subject.DisplayName);
    }

    [Fact]
    public void No_identity_source_means_empty_graph()
    {
        var inputs = Inputs(assets: [new RelationAssetFact("Assets/Sprite/Unit/Profile/10201.png", AssetType.Sprite, 1)]);

        var graph = PersonaRelationAnalyzer.Analyze(inputs);

        Assert.Empty(graph.Subjects);
        Assert.Empty(graph.Links);
    }

    [Fact]
    public void Duplicate_links_are_collapsed_per_subject_kind_and_ref()
    {
        var inputs = Inputs(
            assets: [new RelationAssetFact(SdPrefab, AssetType.GameObject, 100)],
            lang:
            [
                new RelationLangFact("AbDlg_Faust.json", 3),
                new RelationLangFact("AbDlg_Faust.json", 3),
            ]);

        var graph = PersonaRelationAnalyzer.Analyze(inputs);

        Assert.Single(graph.Links, x => x.RefKey == "AbDlg_Faust.json");
    }

    [Fact]
    public void Game_side_character_typos_are_merged_into_one_character()
    {
        // 真实数据实测：部分 SD 人格预制体的文件名把角色写成 Heathclif（少一个 f），
        // 而语音文件名是 Heathcliff。不归并的话同一个角色会被拆成两组、显示名也退回原始 token。
        var inputs = Inputs(
            assets:
            [
                new RelationAssetFact("Assets/Prefab/SD/Personality/10809_Heathclif_BaseAppearance.prefab", AssetType.GameObject, 1),
                new RelationAssetFact("Assets/Prefab/SD/Personality/10810_Heathcliff_BaseAppearance.prefab", AssetType.GameObject, 1),
            ],
            lang: [new RelationLangFact("AbDlg_Heathcliff.json", 0)]);

        var graph = PersonaRelationAnalyzer.Analyze(inputs);

        Assert.Equal(2, graph.Subjects.Count);
        Assert.All(graph.Subjects, x => Assert.Equal("希斯克利夫", x.DisplayName));
        Assert.All(graph.Subjects, x => Assert.Equal("Heathcliff", x.Character));
        // 角色的对话文本挂到该角色的两个人格上（归并后才算「同一个角色」）。
        Assert.Equal(2, graph.Links.Count(x => x.RefKey == "AbDlg_Heathcliff.json"));
    }

    // ── 纯规则 ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Assets/Sprite/Unit/Profile/10201.png", 0)]
    [InlineData("Assets/Sprite/UnitCgThumbnail/10201.png", 1)]
    [InlineData("Assets/Sprite/Unit/CG/10201_normal.png", 2)]
    [InlineData("Assets/Sprite/SkillIcon/1020101.png", 6)]
    [InlineData("Assets/Other/thing.png", -1)]
    public void Portrait_rank_orders_candidates_by_how_much_they_look_like_a_portrait(string path, int expected)
        => Assert.Equal(expected, RelationDisplayRules.PortraitRank(path, AssetType.Sprite));

    [Theory]
    [InlineData("Assets/Prefab/SpineIllustPrefab/10201_gacksung.prefab", true)]
    [InlineData("Assets/Story/StandingModel/faust.psb", true)]
    [InlineData("Assets/Story/CG/x/Faust_SkeletonData.asset", true)]
    [InlineData("Assets/Story/Spine/x.json", true)]
    [InlineData("Assets/Sprite/Unit/Profile/10201.png", false)]
    public void Spine_paths_are_recognised_without_guessing_the_format(string path, bool expected)
        => Assert.Equal(expected, RelationDisplayRules.IsSpinePath(path));

    [Fact]
    public void Every_relation_kind_has_a_chinese_section_label()
    {
        foreach (var kind in Enum.GetValues<RelationKind>())
            Assert.False(string.IsNullOrWhiteSpace(RelationDisplayRules.KindLabel(kind)));
    }
}
