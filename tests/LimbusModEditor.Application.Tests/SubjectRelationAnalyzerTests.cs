using LimbusModEditor.Application.Relations;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 多类别关联分析器（纯函数、无 IO）的语义测试。
///
/// <para><b>这一组测试在钉死什么</b>：</para>
/// <list type="number">
/// <item><b>类别由权威来源判定</b>——人格/敌人/异想体/播报员各自的「创造身份」的路径，
/// 以及「绝不靠 4–5 位数字串反推类别」（敌人 4 位 id 与事件 id、异常 id 同号段）。</item>
/// <item><b>一对多</b>——同一个数字 id 在多个类别里都存在时不得静默二选一；
/// 同一个音频样本可以同时挂到多个对象上（这是需求，不是例外）。</item>
/// <item><b>强度必须显式</b>——精确命中（样本名 == 台词 id）与推导（属于某人格）在
/// <see cref="RelationPreviewKind"/> 上分得开，UI 才不会把推导当事实展示。</item>
/// <item><b>跨资源边</b>——音频 ⇄ 台词、静态表 ⇄ lang 的 <c>xref</c> 真的产出，且多对多。</item>
/// </list>
/// </summary>
public sealed class SubjectRelationAnalyzerTests
{
    private const string SdPrefab = "Assets/Prefab/SD/Personality/10201_Faust_LCBAppearance.prefab";

    private static RelationInputs Inputs(
        IEnumerable<RelationAssetFact>? assets = null,
        IEnumerable<RelationAudioFact>? audio = null,
        IEnumerable<RelationStaticFact>? statics = null,
        IEnumerable<RelationLangFact>? lang = null,
        IEnumerable<RelationTextAnchor>? anchors = null)
        => new(
            assets?.ToArray() ?? [],
            audio?.ToArray() ?? [],
            statics?.ToArray() ?? [],
            lang?.ToArray() ?? [])
        {
            TextAnchors = anchors?.ToArray() ?? [],
        };

    // ── 人格：身份发现 + 全资源种类挂接 ──────────────────────────────

    [Fact]
    public void Discovers_persona_identity_and_links_all_resource_kinds()
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

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        var subject = Assert.Single(graph.Subjects);
        Assert.Equal("persona:10201", subject.SubjectId);
        Assert.Equal(RelationCategories.Persona, subject.SubjectKind);
        Assert.Equal("人格", subject.CategoryLabel);
        Assert.Equal("浮士德 · LCB", subject.DisplayName);
        Assert.Equal("LCB", subject.Subtitle);
        Assert.Equal("Faust", subject.Character);
        Assert.Equal("010201", subject.SortKey);

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
        Assert.Equal("audio", audioLink.MediaKind);
        // 没有锚点 → 只能推到「属于该人格」，不得伪装成精确命中。
        Assert.Equal(RelationPreviewKind.Derived, audioLink.PreviewKind);

        // 图像：Profile（rank 0）与 SkillIcon（rank 6）都在。
        Assert.Equal(2, graph.Links.Count(x => x.Kind == RelationKind.Image));

        // 语音台词文件按「文件名里的 5 位 id」精确命中。
        Assert.Contains(graph.Links, x => x.Kind == RelationKind.Text
            && x.RefKey == "PersonalityVoiceDlg/Voice_Faust_LCB_10201.json");

        // SD 人格预制体本身也是一条关联（prefab，且路径里带 id）。
        Assert.Contains(graph.Links, x => x.RefKey == SdPrefab);

        // 卡片封面取立绘候选里最优的一条；角标数与实际关联数一致。
        Assert.Equal("Assets/Sprite/Unit/Profile/10201.png", subject.CoverRef);
        Assert.Equal(graph.Links.Count(x => x.SubjectId == subject.SubjectId), subject.LinkCount);
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

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        Assert.Single(graph.Subjects);
        Assert.All(graph.Links, x => Assert.Equal("persona:10201", x.SubjectId));
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

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        Assert.Contains(graph.Links, x => x.RefKey == "AbDlg_Faust.json");
        Assert.DoesNotContain(graph.Links, x => x.RefKey == "AbDlg_DonQuixote.json");
    }

    [Fact]
    public void Voice_file_name_supplies_identity_when_no_prefab_exists()
    {
        var inputs = Inputs(lang: [new RelationLangFact("PersonalityVoiceDlg/Voice_Outis_Wuthering_10408.json", 5)]);

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        var subject = Assert.Single(graph.Subjects);
        Assert.Equal("persona:10408", subject.SubjectId);
        Assert.Equal("奥提斯 · Wuthering", subject.DisplayName);
    }

    [Fact]
    public void No_identity_source_means_empty_graph()
    {
        var inputs = Inputs(assets: [new RelationAssetFact("Assets/Sprite/Unit/Profile/10201.png", AssetType.Sprite, 1)]);

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

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

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

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

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        Assert.Equal(2, graph.Subjects.Count);
        Assert.All(graph.Subjects, x => Assert.Equal("希斯克利夫", x.DisplayName));
        Assert.All(graph.Subjects, x => Assert.Equal("Heathcliff", x.Character));
        // 角色的对话文本挂到该角色的两个人格上（归并后才算「同一个角色」）。
        Assert.Equal(2, graph.Links.Count(x => x.RefKey == "AbDlg_Heathcliff.json"));
    }

    // ── 多类别：敌人 / 异想体 / 播报员 ───────────────────────────────

    [Fact]
    public void Enemy_and_abnormality_identities_come_from_authority_paths_only()
    {
        var inputs = Inputs(
            assets:
            [
                // 权威来源：Appearance 预制体。
                new RelationAssetFact("Assets/Prefab/SD/Enemy/90005_GoldenApple_Appearance.prefab", AssetType.GameObject, 1),
                new RelationAssetFact("Assets/Prefab/SD/Abnormality/1080_BlackSwan_Appearance.prefab", AssetType.GameObject, 1),
                // 中立路径里的同号数字：**不得**创造身份。
                new RelationAssetFact("Assets/Sprite/EnemyIcon/90005.png", AssetType.Sprite, 1),
            ]);

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        Assert.Equal(2, graph.Subjects.Count);
        var enemy = Assert.Single(graph.Subjects, x => x.SubjectKind == RelationCategories.Enemy);
        Assert.Equal("enemy:90005", enemy.SubjectId);
        Assert.Equal("敌人单位", enemy.CategoryLabel);
        var abnormality = Assert.Single(graph.Subjects, x => x.SubjectKind == RelationCategories.Abnormality);
        Assert.Equal("abnormality:1080", abnormality.SubjectId);
        Assert.Equal("异想体", abnormality.CategoryLabel);

        // 中立路径的图标挂到了敌人上（数字唯一命中 enemy），且不是权威来源。
        Assert.Contains(graph.Links, x => x.SubjectId == "enemy:90005" && x.RefKey.EndsWith("90005.png", StringComparison.Ordinal));
    }

    [Fact]
    public void Announcer_identity_is_the_sprite_basename_and_the_sprite_itself_is_linked()
    {
        var inputs = Inputs(
            assets: [new RelationAssetFact("Assets/Sprite/BattleAnnouncer/gregor_announcer.png", AssetType.Sprite, 42)]);

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        var announcer = Assert.Single(graph.Subjects);
        Assert.Equal("announcer:gregor_announcer", announcer.SubjectId);
        Assert.Equal("播报员", announcer.CategoryLabel);
        // 显示名去掉 _announcer 后缀。
        Assert.Equal("gregor", announcer.DisplayName);
        // 播报图本身既是权威来源、又是资源 → 必须有一条关联（也当卡片封面）。
        Assert.Contains(graph.Links, x => x.SubjectId == announcer.SubjectId
            && x.RefKey == "Assets/Sprite/BattleAnnouncer/gregor_announcer.png");
    }

    // ── 一对多：同号 id 跨类别、同资源跨对象 ─────────────────────────

    [Fact]
    public void Same_number_in_two_categories_is_linked_to_both_and_marked_ambiguous()
    {
        // 人格 10201 与敌人 10201 同时存在（真实数据里 5 位号段确实会重合）。
        var inputs = Inputs(
            assets:
            [
                new RelationAssetFact(SdPrefab, AssetType.GameObject, 1),
                new RelationAssetFact("Assets/Prefab/SD/Enemy/10201_Doubt_Appearance.prefab", AssetType.GameObject, 1),
                // 中立路径（没有类别标记）→ 不能静默二选一。
                new RelationAssetFact("Assets/Sprite/Portrait/10201.png", AssetType.Sprite, 1),
            ]);

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        var neutral = "Assets/Sprite/Portrait/10201.png";
        var both = graph.Links.Where(x => x.RefKey == neutral).ToArray();
        Assert.Equal(2, both.Length);
        Assert.Equal(["enemy:10201", "persona:10201"], both.Select(x => x.SubjectId).Order(StringComparer.Ordinal).ToArray());
        Assert.All(both, x => Assert.Equal(RelationPreviewKind.Ambiguous, x.PreviewKind));

        // 带类别标记的权威路径只挂它自己的类别（敌人预制体不挂到人格上）。
        const string enemyPrefab = "Assets/Prefab/SD/Enemy/10201_Doubt_Appearance.prefab";
        var enemyOnly = Assert.Single(graph.Links, x => x.RefKey == enemyPrefab);
        Assert.Equal("enemy:10201", enemyOnly.SubjectId);
    }

    [Fact]
    public void One_audio_sample_can_be_linked_to_multiple_subjects()
    {
        // 需求：单个键值允许对多条数据关联（不是 1:1）。真实场景：一条战斗语音的文件名里
        // 同时带人格 id 与敌人 id（「对谁说的」与「谁说的」都在名字里）。
        var sample = "battle_10201_1080_1";
        var inputs = Inputs(
            assets:
            [
                new RelationAssetFact(SdPrefab, AssetType.GameObject, 1),
                new RelationAssetFact("Assets/Prefab/SD/Abnormality/1080_BlackSwan_Appearance.prefab", AssetType.GameObject, 1),
            ],
            audio: [new RelationAudioFact(@"C:\game\Voice.bank", sample, "Vorbis", 10)]);

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        var audioLinks = graph.Links.Where(x => x.Kind == RelationKind.Audio).ToArray();
        Assert.Equal(2, audioLinks.Length);
        Assert.Equal(2, audioLinks.Select(x => x.SubjectId).Distinct(StringComparer.Ordinal).Count());
        // 一条音频的两个对象：人格 + 异想体；同一条 RefKey 出现两次是设计。
        Assert.All(audioLinks, x => Assert.Equal(@"C:\game\Voice.bank" + "\u0000" + sample, x.RefKey));

        // 跨资源边同样是多对多：一个 FromRef 指向两个 ToRef。
        var edges = graph.Xrefs.Where(x => x.FromRef == sample).ToArray();
        Assert.Equal(2, edges.Length);
        Assert.Equal(2, edges.Select(x => x.ToRef).Distinct(StringComparer.Ordinal).Count());
    }

    // ── 交叉关联：音频 ⇄ 台词、静态表 ⇄ lang ─────────────────────────

    [Fact]
    public void Exact_anchor_turns_an_audio_link_into_the_voice_text_preview()
    {
        // 真实数据实测：人格语音库的**样本名与 lang 台词 id 完全同名**（如 battle_break_10201_1）。
        const string sample = "battle_break_10201_1";
        var inputs = Inputs(
            assets: [new RelationAssetFact(SdPrefab, AssetType.GameObject, 1)],
            audio:
            [
                new RelationAudioFact(@"C:\game\Voice_Default_S5.bank", sample, "Vorbis", 999)
                {
                    SampleRate = 48000,
                    SampleCount = 96000,
                },
            ],
            anchors:
            [
                new RelationTextAnchor(sample, "PersonalityVoiceDlg/Voice_Faust_LCB_10201.json", null, "自身混乱", "台词原文",
                    RelationAnchorTables.Voice),
            ]);

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        var link = Assert.Single(graph.Links, x => x.Kind == RelationKind.Audio);
        Assert.Equal(RelationPreviewKind.Exact, link.PreviewKind);
        Assert.Equal("台词原文", link.PreviewText);
        // 时长由 sample_count / sample_rate 算出，**不需要解码音频**。
        Assert.Equal(2.0, link.DurationSec);
        Assert.Equal(sample, RelationDeepLink.Part(link.DeepLink, 1));

        // 跨资源边：音频 → 台词（精确），并且带上台词分类 desc 作为补充。
        var voiceEdge = Assert.Single(graph.Xrefs, x => x.Relation == RelationXrefKinds.AudioToVoiceText);
        Assert.Equal(sample, voiceEdge.FromRef);
        Assert.Equal(sample, voiceEdge.ToRef);
        Assert.Equal(nameof(RelationPreviewKind.Exact), voiceEdge.Confidence);
        Assert.Equal("自身混乱", voiceEdge.Detail);

        // 同时仍然有「音频 → 对象」这条边（两条边并存，不是二选一）。
        Assert.Contains(graph.Xrefs, x => x.Relation == RelationXrefKinds.AudioToSubject
            && x.FromRef == sample && x.ToRef == "persona:10201");
    }

    [Fact]
    public void Static_table_numeric_foreign_key_resolves_to_lang_anchor_and_is_crosslinked()
    {
        // 静态表把中文全放在数值外键上（如被动 1010101）→ 靠 lang 锚点解出中文。
        var inputs = Inputs(
            assets: [new RelationAssetFact(SdPrefab, AssetType.GameObject, 1)],
            statics:
            [
                new RelationStaticFact("Assets/x/passive/passive.json", "passive", "passive",
                    "{\"id\":1010101,\"personality\":10201}", 2048),
            ],
            anchors: [new RelationTextAnchor("1010101", "Passives.json", "穿刺抵抗", "受到的穿刺伤害降低", null,
                RelationAnchorTables.Passive)]);

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        var link = Assert.Single(graph.Links, x => x.Kind == RelationKind.StaticData);
        Assert.Equal("persona:10201", link.SubjectId);
        // 预览文本必须是锚点的**真文本**，不是「命中了几条」这种计数串
        // （计数串落到维基页面上就是「标题对了、正文是空的」）。
        Assert.Equal("穿刺抵抗：受到的穿刺伤害降低", link.PreviewText);
        Assert.Equal(RelationPreviewKind.Derived, link.PreviewKind);

        var edge = Assert.Single(graph.Xrefs, x => x.Relation == RelationXrefKinds.StaticToLang);
        Assert.Equal("Assets/x/passive/passive.json", edge.FromRef);
        Assert.Equal("1010101", edge.ToRef);
        Assert.Equal("穿刺抵抗", edge.Detail);
    }

    [Fact]
    public void Anchor_is_optional_and_never_breaks_the_graph()
    {
        // 锚点缺失（没选语言目录 / lang 文件坏了）时：关联主体照常建，只是拿不到预览内容。
        var inputs = Inputs(
            assets: [new RelationAssetFact(SdPrefab, AssetType.GameObject, 1)],
            audio: [new RelationAudioFact(@"C:\game\V.bank", "voice_faust_10201_1", null, 1)]);

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        var link = Assert.Single(graph.Links, x => x.Kind == RelationKind.Audio);
        Assert.Null(link.PreviewText);
        Assert.Equal(RelationPreviewKind.Derived, link.PreviewKind);
        Assert.Null(link.DurationSec);
        // 跨资源边不依赖锚点：音频 → 对象这条照常产出，只是少了「音频 → 台词」那层。
        var edge = Assert.Single(graph.Xrefs);
        Assert.Equal(RelationXrefKinds.AudioToSubject, edge.Relation);
        Assert.Equal("persona:10201", edge.ToRef);
        Assert.DoesNotContain(graph.Xrefs, x => x.Relation == RelationXrefKinds.AudioToVoiceText);
        Assert.Null(graph.Subjects[0].PreviewText);
    }

    [Fact]
    public void Subject_preview_prefers_an_exact_hit_over_a_derived_one()
    {
        const string sample = "battle_break_10201_1";
        var inputs = Inputs(
            assets: [new RelationAssetFact(SdPrefab, AssetType.GameObject, 1)],
            lang: [new RelationLangFact("AbDlg_Faust.json", 2)],
            audio: [new RelationAudioFact(@"C:\game\V.bank", sample, "Vorbis", 1)],
            anchors: [new RelationTextAnchor(sample, "PersonalityVoiceDlg/x.json", null, null, "第一句台词",
                RelationAnchorTables.Voice)]);

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        // 人格的卡片正面预览：优先取精确命中（台词原文），而不是角色级推导。
        Assert.Equal("第一句台词", graph.Subjects[0].PreviewText);
    }

    [Fact]
    public void Many_links_per_key_are_kept_and_a_single_subject_has_no_artificial_ceiling()
    {
        // 需求：一个对象可以有任意多条关联（不设上限、不去重成 1 条）。
        var assets = Enumerable.Range(0, 40)
            .Select(i => new RelationAssetFact($"Assets/Sprite/Unit/Profile/10201_{i}.png", AssetType.Sprite, i))
            .Append(new RelationAssetFact(SdPrefab, AssetType.GameObject, 1))
            .ToArray();

        var graph = SubjectRelationAnalyzer.Analyze(Inputs(assets: assets));

        Assert.Equal(41, graph.Links.Count(x => x.SubjectId == "persona:10201"));
    }

    // ── E.G.O 装备 / E.G.O 饰品 ─────────────────────────────────────

    [Fact]
    public void Ego_identity_comes_from_the_lang_definition_and_links_its_resources()
    {
        // 权威来源实测：lang 的 Egos.json（id 5 位、20101 起）。
        // 资源（立绘/EgoBanner/技能图）只负责「链接」，不负责「定义身份」。
        var inputs = Inputs(
            assets:
            [
                new RelationAssetFact("Assets/Sprite/Unit/Profile/Ego/20101.png", AssetType.Sprite, 10),
                new RelationAssetFact("Assets/UI/EgoBanner/20101.png", AssetType.Sprite, 20),
            ],
            anchors: [new RelationTextAnchor("20101", "Egos.json", "乌瞰刀", "李箱的基础E.G.O装备", null,
                RelationAnchorTables.Ego)]);

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        var ego = Assert.Single(graph.Subjects);
        Assert.Equal("ego:20101", ego.SubjectId);
        Assert.Equal(RelationCategories.Ego, ego.SubjectKind);
        Assert.Equal("E.G.O 装备", ego.CategoryLabel);
        Assert.Equal("乌瞰刀", ego.DisplayName);
        Assert.Equal("李箱的基础E.G.O装备", ego.Subtitle);

        // lang 文件本身是定义处 → 一条精确强度的文本关联。
        var text = Assert.Single(graph.Links, x => x.Kind == RelationKind.Text);
        Assert.Equal("Egos.json", text.RefKey);
        Assert.Equal(RelationPreviewKind.Exact, text.PreviewKind);
        Assert.Equal("乌瞰刀", text.PreviewText);
        Assert.Equal("20101", RelationDeepLink.Part(text.DeepLink, 1));

        // 资源图挂上了（Profile/Ego 路径自带类别标记 → 无歧义）。
        Assert.Contains(graph.Links, x => x.RefKey == "Assets/Sprite/Unit/Profile/Ego/20101.png");

        // 跨资源边：文本 → 对象，且是「一个文件对多个对象」的那种多对多。
        var edge = Assert.Single(graph.Xrefs, x => x.Relation == RelationXrefKinds.TextToSubject);
        Assert.Equal("Egos.json", edge.FromRef);
        Assert.Equal("ego:20101", edge.ToRef);
    }

    [Fact]
    public void Ego_is_never_invented_from_a_resource_number_that_lang_does_not_list()
    {
        // 实测：资源侧有 17 个 20xxx 数字（侵蚀/觉醒后缀）并不在 Egos.json 里。
        // 拿资源当身份源会造出「幽灵 EGO」——所以 20121 不得成为对象。
        var inputs = Inputs(
            assets:
            [
                new RelationAssetFact("Assets/Sprite/Unit/Profile/Ego/20101.png", AssetType.Sprite, 10),
                new RelationAssetFact("Assets/Sprite/ErosionAppearance_20121.png", AssetType.Sprite, 10),
            ],
            anchors: [new RelationTextAnchor("20101", "Egos.json", "乌瞰刀", null, null, RelationAnchorTables.Ego)]);

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        Assert.Single(graph.Subjects);
        Assert.DoesNotContain(graph.Links, x => x.RefKey.Contains("20121", StringComparison.Ordinal));
    }

    [Fact]
    public void Ego_gift_ids_are_normalised_to_the_four_digit_base_and_layered_ids_merge()
    {
        // 实测：同一个饰品在 lang 里是 9701 / 19701 / 29701（+10000 / +20000 分层）。
        // 不归一化会把一个饰品拆成三个对象。
        var inputs = Inputs(
            assets: [new RelationAssetFact("Assets/Sprite/EgoGiftIcon/9701.png", AssetType.Sprite, 5)],
            anchors:
            [
                new RelationTextAnchor("9701", "EGOgift_MirrorDungeon.json", "火热多汁琵琶腿", "施加3层烧伤", null,
                    RelationAnchorTables.EgoGift),
                new RelationTextAnchor("19701", "EGOgift_StoryDungeon.json", "火热多汁琵琶腿", null, null,
                    RelationAnchorTables.EgoGift),
            ]);

        var graph = SubjectRelationAnalyzer.Analyze(inputs);

        var gift = Assert.Single(graph.Subjects);
        Assert.Equal("ego_gift:9701", gift.SubjectId);
        Assert.Equal("E.G.O 饰品", gift.CategoryLabel);
        Assert.Equal("火热多汁琵琶腿", gift.DisplayName);

        // 两个 lang 文件都指向同一个对象（多对一的那一侧）。
        Assert.Equal(2, graph.Links.Count(x => x.Kind == RelationKind.Text));
        Assert.Equal(2, graph.Xrefs.Count(x => x.Relation == RelationXrefKinds.TextToSubject));

        // 图标按基准 4 位 id 挂上；路径自带 EgoGiftIcon 标记 → 不会被当成异想体。
        var icon = Assert.Single(graph.Links, x => x.RefKey == "Assets/Sprite/EgoGiftIcon/9701.png");
        Assert.Equal("ego_gift:9701", icon.SubjectId);
    }

    [Theory]
    [InlineData(9701, "9701")]
    [InlineData(19701, "9701")]
    [InlineData(29701, "9701")]
    [InlineData(1001, "1001")]
    [InlineData(9995, "9995")]
    // EGO 的 20101 落到 101（不是 4 位）→ 判为「不是饰品 id」，宁可丢掉也不造假。
    [InlineData(20101, null)]
    [InlineData(101, null)]
    [InlineData(0, null)]
    public void Gift_key_normalisation_is_pinned(int rawId, string? expected)
        => Assert.Equal(expected, RelationEntityKeys.GiftKeyOf(rawId));

    // ── 关联键编码 ───────────────────────────────────────────────────

    [Fact]
    public void Subject_ids_carry_a_category_prefix_and_round_trip()
    {
        var id = SubjectIds.Make(RelationCategories.Abnormality, "1080");
        Assert.Equal("abnormality:1080", id);
        Assert.Equal(RelationCategories.Abnormality, SubjectIds.CategoryOf(id));
        Assert.Equal("1080", SubjectIds.KeyOf(id));

        // 不是「类别:键」形态时不崩（旧库 / 手工数据）。
        Assert.Equal(string.Empty, SubjectIds.CategoryOf("1080"));
        Assert.Equal("1080", SubjectIds.KeyOf("1080"));
        Assert.Equal(string.Empty, SubjectIds.CategoryOf(null));
    }

    [Fact]
    public void Every_category_has_a_chinese_label_and_is_registered()
    {
        foreach (var category in RelationCategories.All)
        {
            Assert.True(RelationCategories.IsKnown(category));
            Assert.False(string.IsNullOrWhiteSpace(RelationCategories.Label(category)));
        }
        Assert.False(RelationCategories.IsKnown("unknown_category"));
    }

    [Fact]
    public void Relation_format_version_is_v4()
        => Assert.Equal("v4", RelationIndexSource.FormatVersion);

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
    [InlineData("Assets/Sprite/Unit/Profile/10201.png")]
    [InlineData("Assets/Sprite/Unit/CG/10201_normal.png")]
    public void Cover_rank_prefers_the_texture_record_of_the_same_path(string path)
    {
        var spriteRank = RelationDisplayRules.CoverCandidateRank(path, AssetType.Sprite);
        var textureRank = RelationDisplayRules.CoverCandidateRank(path, AssetType.Texture);
        // 同路径下 Texture 一定比 Sprite 优先（解码只能走 Texture 的 pathId）；
        // 且不得越过相邻的 Portrait 档位相邻性（差值固定为 1）。
        Assert.Equal(1, spriteRank - textureRank);
        Assert.True(textureRank >= 0);
    }

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

    // ── 精确跳转载荷 ─────────────────────────────────────────────────

    [Fact]
    public void Deep_link_payload_round_trips_and_tolerates_empty_segments()
    {
        var payload = RelationDeepLink.ForAudio(@"C:\g\v.bank", "voice_x_10201_1");
        Assert.Equal(@"C:\g\v.bank", RelationDeepLink.Part(payload, 0));
        Assert.Equal("voice_x_10201_1", RelationDeepLink.Part(payload, 1));

        // 只有资源级定位时也保持同一口径（文本/静态的记录键可为空）。
        Assert.Equal("PersonalityVoiceDlg/x.json", RelationDeepLink.ForText("PersonalityVoiceDlg/x.json", null));
        Assert.Equal(2, RelationDeepLink.Decode(RelationDeepLink.ForText("a.json", "k")).Count);

        Assert.Empty(RelationDeepLink.Decode(null));
        Assert.Equal(string.Empty, RelationDeepLink.Part(null, 3));
    }
}
