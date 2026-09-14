using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// 预设卡片 / 详情的展示层服务（<see cref="PresetWorkbenchService"/>）语义测试。
///
/// <para>覆盖这些容易写错的事：① 多类别切换与计数；② 封面挑选（立绘优先级 +
/// 「能渲染的胜过不能渲染的」+ <b>同路径优先 Texture</b>——选错必然封面留白）；
/// ③ 定位键 → <see cref="AssetRecord"/> 的还原；④ 详情分组的用户视角顺序；
/// ⑤ 卡片/详情的预览内容与<b>强度</b>；⑥ 精确跳转载荷。</para>
/// </summary>
public sealed class PresetWorkbenchServiceTests : IDisposable
{
    private readonly string _cacheDir;
    private readonly string _fileDir;

    public PresetWorkbenchServiceTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "lme-preset-" + Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Combine(root, "cache");
        _fileDir = Path.Combine(root, "files");
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_fileDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(_cacheDir)!, true); } catch (Exception) { /* 临时目录 */ }
    }

    private PresetWorkbenchService Service(RelationGraph graph)
    {
        var store = new RelationStore(_cacheDir);
        var source = RelationIndexSource.From("u", "b", "s", "t");
        store.EnsureSource(source);
        store.PersistGraph(source, graph);
        return new PresetWorkbenchService(new RelationQueryService(store));
    }

    private static RelationSubject Subject(string id, string name, string character = "Faust")
        => new(SubjectIds.Make(RelationCategories.Persona, id), RelationCategories.Persona, name, "LCB", character, id);

    private static RelationLink Link(string id, RelationKind kind, string reference, string? display = null, string? detail = null)
        => new(SubjectIds.Make(RelationCategories.Persona, id), RelationCategories.Persona, kind,
            reference, display ?? reference, detail, 100);

    /// <summary>建一个「存在磁盘上」的资源（<c>Renderable</c> 要求源文件真实存在）。</summary>
    private AssetRecord RenderableAsset(string containerEntry, AssetType type)
    {
        var file = Path.Combine(_fileDir, Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(file, "x");
        return Asset(containerEntry, type, file, 42);
    }

    private static AssetRecord Asset(string containerEntry, AssetType type, string? sourcePath = null, long? pathId = null)
    {
        var asset = new AssetRecord { Type = type, SourcePath = sourcePath, UnityPathId = pathId, LogicalPath = containerEntry };
        asset.Metadata["containerEntry"] = containerEntry;
        return asset;
    }

    [Fact]
    public void Card_picks_the_profile_portrait_and_resolves_its_asset()
    {
        const string profile = "Assets/Sprite/Unit/Profile/10201.png";
        const string cg = "Assets/Sprite/Unit/CG/10201_normal.png";
        var graph = new RelationGraph(
            [Subject("10201", "浮士德 · LCB")],
            [
                Link("10201", RelationKind.Image, cg),
                Link("10201", RelationKind.Image, profile),
                Link("10201", RelationKind.Audio, @"C:\game\v.bank" + "\u0000" + "voice_faust_10201_1"),
            ]);
        var profileAsset = RenderableAsset(profile, AssetType.Sprite);
        var cgAsset = RenderableAsset(cg, AssetType.Sprite);

        var card = Assert.Single(Service(graph).BuildCards([cgAsset, profileAsset]));

        Assert.Equal("persona:10201", card.SubjectId);
        Assert.Equal(RelationCategories.Persona, card.Category);
        Assert.Equal("人格", card.CategoryLabel);
        Assert.Equal("浮士德 · LCB", card.Title);
        Assert.Equal(profile, card.PortraitLink!.RefKey);
        Assert.Same(profileAsset, card.PortraitAsset);
        Assert.Equal(3, card.LinkCount);
        // 摘要按条数降序：图像有 2 条（CG + 头像），音频 1 条。
        Assert.Contains("图像 2", card.Summary);
        Assert.Contains("音频 1", card.Summary);
    }

    [Fact]
    public void Card_prefers_a_renderable_portrait_over_a_better_ranked_unresolvable_one()
    {
        const string profile = "Assets/Sprite/Unit/Profile/10201.png";   // rank 0，但项目里没有这条资源
        const string info = "Assets/Sprite/Unit/Info/10201.png";         // rank 3，可渲染
        var graph = new RelationGraph(
            [Subject("10201", "浮士德 · LCB")],
            [Link("10201", RelationKind.Image, profile), Link("10201", RelationKind.Image, info)]);
        var infoAsset = RenderableAsset(info, AssetType.Sprite);

        var card = Assert.Single(Service(graph).BuildCards([infoAsset]));

        Assert.Equal(info, card.PortraitLink!.RefKey);
        Assert.Same(infoAsset, card.PortraitAsset);
    }

    [Fact]
    public void Card_without_any_image_has_no_portrait()
    {
        var graph = new RelationGraph(
            [Subject("10201", "浮士德 · LCB")],
            [Link("10201", RelationKind.Text, "PersonalityVoiceDlg/Voice_Faust_LCB_10201.json")]);

        var card = Assert.Single(Service(graph).BuildCards([]));

        Assert.Null(card.PortraitLink);
        Assert.Null(card.PortraitAsset);
        Assert.Equal(1, card.LinkCount);
    }

    [Fact]
    public void Cards_can_be_filtered_by_id_name_or_character_token()
    {
        var graph = new RelationGraph(
            [Subject("10201", "浮士德 · LCB", "Faust"), Subject("10301", "唐吉诃德 · LCB", "DonQuixote")],
            [Link("10201", RelationKind.Text, "a.json"), Link("10301", RelationKind.Text, "b.json")]);
        var service = Service(graph);

        Assert.Equal(2, service.BuildCards([]).Count);
        Assert.Equal("persona:10201", Assert.Single(service.BuildCards([], null, "102")).SubjectId);
        Assert.Equal("persona:10301", Assert.Single(service.BuildCards([], null, "唐吉")).SubjectId);
        Assert.Equal("persona:10301", Assert.Single(service.BuildCards([], null, "donquixote")).SubjectId);
        Assert.Empty(service.BuildCards([], null, "不存在的关键词"));
    }

    // ── 多类别 ───────────────────────────────────────────────────────

    [Fact]
    public void Categories_only_list_the_kinds_that_actually_have_objects()
    {
        var graph = new RelationGraph(
            [
                Subject("10201", "浮士德 · LCB"),
                new(SubjectIds.Make(RelationCategories.Ego, "20101"), RelationCategories.Ego,
                    "乌瞰刀", "李箱的基础E.G.O装备", string.Empty, "20101"),
                new(SubjectIds.Make(RelationCategories.EgoGift, "9701"), RelationCategories.EgoGift,
                    "火热多汁琵琶腿", string.Empty, string.Empty, "9701"),
            ],
            [Link("10201", RelationKind.Text, "a.json")]);

        var categories = Service(graph).Categories();

        // 只列真的有内容的类别，且顺序沿用 RelationCategories.All。
        Assert.Equal([RelationCategories.Persona, RelationCategories.Ego, RelationCategories.EgoGift],
            categories.Select(x => x.Category).ToArray());
        Assert.Equal(["人格", "E.G.O 装备", "E.G.O 饰品"], categories.Select(x => x.Label).ToArray());
        Assert.All(categories, x => Assert.Equal(1, x.Count));
    }

    [Fact]
    public void Cards_can_be_filtered_down_to_one_category()
    {
        var graph = new RelationGraph(
            [
                Subject("10201", "浮士德 · LCB"),
                new(SubjectIds.Make(RelationCategories.Ego, "20101"), RelationCategories.Ego,
                    "乌瞰刀", "李箱的基础E.G.O装备", string.Empty, "20101"),
            ],
            [Link("10201", RelationKind.Text, "a.json")]);
        var service = Service(graph);

        Assert.Equal(2, service.BuildCards([]).Count);
        var ego = Assert.Single(service.BuildCards([], RelationCategories.Ego));
        Assert.Equal("ego:20101", ego.SubjectId);
        Assert.Equal("E.G.O 装备", ego.CategoryLabel);
        // E.G.O 的副标题来自 lang 的说明，不是风格 token。
        Assert.Equal("李箱的基础E.G.O装备", ego.Subtitle);
        Assert.Empty(service.BuildCards([], RelationCategories.Enemy));
    }

    // ── 卡片正面预览（强度必须显式） ───────────────────────────────

    [Fact]
    public void Card_preview_prefers_an_exact_hit_and_reports_its_strength()
    {
        var graph = new RelationGraph(
            [Subject("10201", "浮士德 · LCB")],
            [
                // 精确：样本名 == 台词 id → 带台词原文。
                new RelationLink("persona:10201", RelationCategories.Persona, RelationKind.Audio,
                    @"C:\g\v.bank" + "\u0000" + "battle_break_10201_1", "battle_break_10201_1", "Vorbis", 10)
                {
                    PreviewText = "台词原文",
                    PreviewKind = RelationPreviewKind.Exact,
                    MediaKind = "audio",
                    DurationSec = 2.5,
                },
                // 推导：只是角色级关联，没有正文。
                new RelationLink("persona:10201", RelationCategories.Persona, RelationKind.Text,
                    "AbDlg_Faust.json", "AbDlg_Faust.json", "3 个键", 3)
                {
                    PreviewKind = RelationPreviewKind.Derived,
                    MediaKind = "text",
                },
            ]);

        var card = Assert.Single(Service(graph).BuildCards([]));

        Assert.Equal("台词原文", card.PreviewText);
        Assert.Equal(RelationPreviewKind.Exact, card.PreviewKind);
    }

    [Fact]
    public void Card_preview_is_empty_when_nothing_can_be_shown()
    {
        var graph = new RelationGraph(
            [Subject("10201", "浮士德 · LCB")],
            [Link("10201", RelationKind.Text, "AbDlg_Faust.json")]);

        var card = Assert.Single(Service(graph).BuildCards([]));

        Assert.Null(card.PreviewText);
        Assert.NotEqual(RelationPreviewKind.Exact, card.PreviewKind);
    }

    [Fact]
    public void Detail_rows_carry_everything_the_inline_preview_needs()
    {
        var graph = new RelationGraph(
            [Subject("10201", "浮士德 · LCB")],
            [
                new RelationLink("persona:10201", RelationCategories.Persona, RelationKind.Audio,
                    @"C:\g\v.bank" + "\u0000" + "battle_break_10201_1", "battle_break_10201_1", "Vorbis · 10 B", 10)
                {
                    PreviewText = "台词原文",
                    PreviewKind = RelationPreviewKind.Exact,
                    MediaKind = "audio",
                    DurationSec = 2.5,
                    DeepLink = RelationDeepLink.ForAudio(@"C:\g\v.bank", "battle_break_10201_1"),
                },
            ]);

        var row = Assert.Single(Assert.Single(Service(graph).BuildDetail("persona:10201", [])).Rows);

        Assert.Equal("台词原文", row.PreviewText);
        Assert.Equal(RelationPreviewKind.Exact, row.PreviewKind);
        Assert.Equal("audio", row.MediaKind);
        Assert.Equal(2.5, row.DurationSec!.Value);
        Assert.Equal("battle_break_10201_1", RelationDeepLink.Part(row.DeepLink, 1));
    }

    [Fact]
    public void Detail_groups_are_ordered_for_the_user_and_resolve_assets()
    {
        const string table = "Assets/Resources_moved/StaticData/static-data/personality/personality-10201.json";
        const string icon = "Assets/Sprite/SkillIcon/1020101.png";
        var graph = new RelationGraph(
            [Subject("10201", "浮士德 · LCB")],
            [
                Link("10201", RelationKind.Image, icon, "1020101.png", "精灵图 · 200 B"),
                Link("10201", RelationKind.StaticData, table, "personality-10201.json"),
                Link("10201", RelationKind.Audio, @"C:\game\v.bank" + "\u0000" + "voice_faust_10201_1", "voice_faust_10201_1"),
                Link("10201", RelationKind.Text, "PersonalityVoiceDlg/Voice_Faust_LCB_10201.json"),
            ]);
        var iconAsset = RenderableAsset(icon, AssetType.Sprite);

        var groups = Service(graph).BuildDetail("persona:10201", [iconAsset]);

        // 用户视角顺序：静态数据 → 文本 → 音频 → 图像。
        Assert.Equal(
            [RelationKind.StaticData, RelationKind.Text, RelationKind.Audio, RelationKind.Image],
            groups.Select(g => g.Kind).ToArray());
        Assert.Equal("静态数据", groups[0].Title);
        Assert.Equal("图像", groups[3].Title);

        var imageRow = Assert.Single(groups[3].Rows);
        Assert.Same(iconAsset, imageRow.Asset);
        Assert.Equal("精灵图 · 200 B", imageRow.Detail);

        // 文本 / 音频在关联图里没有对应的项目资源 → Asset 为 null（靠「定位」跳工作台）。
        Assert.Null(groups[1].Rows[0].Asset);
        Assert.Null(groups[2].Rows[0].Asset);
    }

    // ── 精确跳转 ───────────────────────────────────────────────────

    [Fact]
    public void Target_carries_the_precise_payload_so_the_target_page_can_select_the_row()
    {
        var graph = new RelationGraph(
            [Subject("10201", "浮士德 · LCB")],
            [
                new RelationLink("persona:10201", RelationCategories.Persona, RelationKind.Text,
                    "PersonalityVoiceDlg/Voice_Faust_LCB_10201.json", "Voice_Faust_LCB_10201.json", "12 个键", 12)
                {
                    DeepLink = RelationDeepLink.ForText("PersonalityVoiceDlg/Voice_Faust_LCB_10201.json", "battle_break_10201_1"),
                    MediaKind = "text",
                },
            ]);

        var group = Assert.Single(Service(graph).BuildDetail("persona:10201", []));
        var row = Assert.Single(group.Rows);

        var target = PresetWorkbenchService.TargetFor(group.Kind, row);
        Assert.Equal(WorkbenchPageKeys.Text, target.PageKey);
        // 载荷是「文件 + 键路径」两段，目标页据此选中唯一那一行。
        Assert.Equal("PersonalityVoiceDlg/Voice_Faust_LCB_10201.json", RelationDeepLink.Part(target.Payload, 0));
        Assert.Equal("battle_break_10201_1", RelationDeepLink.Part(target.Payload, 1));
    }

    [Fact]
    public void Target_falls_back_to_the_ref_key_when_there_is_no_deep_link()
    {
        const string table = "Assets/Resources_moved/StaticData/static-data/personality/personality-10201.json";
        var graph = new RelationGraph(
            [Subject("10201", "浮士德 · LCB")],
            [Link("10201", RelationKind.StaticData, table, "personality-10201.json")]);

        var group = Assert.Single(Service(graph).BuildDetail("persona:10201", []));
        var target = PresetWorkbenchService.TargetFor(group.Kind, Assert.Single(group.Rows));

        Assert.Equal(WorkbenchPageKeys.Static, target.PageKey);
        Assert.Equal(table, target.Payload);
    }

    [Fact]
    public void Open_button_routes_each_kind_to_its_own_workbench()
    {
        Assert.Equal(WorkbenchPageKeys.Text, PresetWorkbenchService.WorkbenchKeyFor(RelationKind.Text));
        Assert.Equal(WorkbenchPageKeys.Bank, PresetWorkbenchService.WorkbenchKeyFor(RelationKind.Audio));
        Assert.Equal(WorkbenchPageKeys.Static, PresetWorkbenchService.WorkbenchKeyFor(RelationKind.StaticData));
        Assert.Equal(WorkbenchPageKeys.Assets, PresetWorkbenchService.WorkbenchKeyFor(RelationKind.Image));
        Assert.Equal(WorkbenchPageKeys.Assets, PresetWorkbenchService.WorkbenchKeyFor(RelationKind.Spine));
    }

    // ── 封面记录挑选（「图片加载不出来」的头号根因） ─────────────────

    [Fact]
    public void Container_entry_index_prefers_the_row_that_can_actually_be_read()
    {
        const string entry = "Assets/Sprite/Unit/Profile/10201.png";
        var bare = Asset(entry, AssetType.Sprite);                       // 没有源文件 / PathId
        var renderable = RenderableAsset(entry, AssetType.Sprite);

        var index = PresetWorkbenchService.IndexByContainerEntry([bare, renderable]);

        Assert.Same(renderable, index[entry]);
        // 顺序反过来也成立（先到先得不能把可渲染的那条顶掉）。
        Assert.Same(renderable, PresetWorkbenchService.IndexByContainerEntry([renderable, bare])[entry]);
    }

    [Fact]
    public void Container_entry_index_prefers_the_texture_record_over_the_sprite_one()
    {
        // 真实数据：同一条 container_entry 既有 Sprite 行也有 Texture 行，两条都「可渲染」
        // （同一个 bundle、都有 pathId）。但读位图只能按 Texture2D 的 pathId 解 → 选 Sprite 必然留白。
        const string entry = "Assets/Sprite/Unit/Profile/10201.png";
        var sprite = RenderableAsset(entry, AssetType.Sprite);
        var texture = RenderableAsset(entry, AssetType.Texture);

        Assert.Same(texture, PresetWorkbenchService.IndexByContainerEntry([sprite, texture])[entry]);
        Assert.Same(texture, PresetWorkbenchService.IndexByContainerEntry([texture, sprite])[entry]);
    }

    [Fact]
    public void Card_ranks_the_texture_record_of_the_same_path_higher()
    {
        const string entry = "Assets/Sprite/Unit/Profile/10201.png";
        var graph = new RelationGraph(
            [Subject("10201", "浮士德 · LCB")],
            [Link("10201", RelationKind.Image, entry)]);
        var texture = RenderableAsset(entry, AssetType.Texture);

        var card = Assert.Single(Service(graph).BuildCards([texture]));

        Assert.Same(texture, card.PortraitAsset);
        Assert.Equal(AssetType.Texture, card.PortraitAsset!.Type);
    }
}
