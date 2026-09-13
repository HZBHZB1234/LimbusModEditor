using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// 预设卡片 / 详情的展示层服务（<see cref="PersonaPresetService"/>）语义测试。
///
/// <para>覆盖三件容易写错的事：① 封面挑选（立绘优先级 + 「能渲染的胜过不能渲染的」）；
/// ② 定位键 → <see cref="AssetRecord"/> 的还原（关联图的资源侧口径 = 容器路径）；
/// ③ 详情分组的用户视角顺序 + 「打开」该跳哪个工作台。</para>
/// </summary>
public sealed class PersonaPresetServiceTests : IDisposable
{
    private readonly string _cacheDir;
    private readonly string _fileDir;

    public PersonaPresetServiceTests()
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

    private PersonaPresetService Service(RelationGraph graph)
    {
        var store = new RelationStore(_cacheDir);
        var source = RelationIndexSource.From("u", "b", "s", "t");
        store.EnsureSource(source);
        store.PersistGraph(source, graph);
        return new PersonaPresetService(new RelationQueryService(store));
    }

    private static RelationSubject Subject(string id, string name, string character = "Faust")
        => new(id, RelationCategories.Persona, name, "LCB", character, id);

    private static RelationLink Link(string id, RelationKind kind, string reference, string? display = null, string? detail = null)
        => new(id, RelationCategories.Persona, kind, reference, display ?? reference, detail, 100);

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

        Assert.Equal("10201", card.SubjectId);
        Assert.Equal("10201 · 浮士德 · LCB", card.Title);
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
        Assert.Equal("10201", Assert.Single(service.BuildCards([], "102")).SubjectId);
        Assert.Equal("10301", Assert.Single(service.BuildCards([], "唐吉")).SubjectId);
        Assert.Equal("10301", Assert.Single(service.BuildCards([], "donquixote")).SubjectId);
        Assert.Empty(service.BuildCards([], "不存在的关键词"));
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

        var groups = Service(graph).BuildDetail("10201", [iconAsset]);

        // 用户视角顺序：静态数据 → 文本 → 音频 → 图像。
        Assert.Equal(
            [RelationKind.StaticData, RelationKind.Text, RelationKind.Audio, RelationKind.Image],
            groups.Select(g => g.Kind).ToArray());
        Assert.Equal("静态数据", groups[0].Title);
        Assert.Equal("图像", groups[3].Title);

        var imageRow = Assert.Single(groups[3].Rows);
        Assert.Same(iconAsset, imageRow.Asset);
        Assert.Equal("精灵图 · 200 B", imageRow.Detail);

        // 文本 / 音频在关联图里没有对应的项目资源 → Asset 为 null（靠「打开」跳工作台）。
        Assert.Null(groups[1].Rows[0].Asset);
        Assert.Null(groups[2].Rows[0].Asset);
    }

    [Fact]
    public void Detail_search_keyword_uses_the_file_name_not_the_full_path()
    {
        const string table = "Assets/Resources_moved/StaticData/static-data/personality/personality-10201.json";
        var graph = new RelationGraph(
            [Subject("10201", "浮士德 · LCB")],
            [Link("10201", RelationKind.StaticData, table, "personality-10201.json")]);

        var row = Assert.Single(Assert.Single(Service(graph).BuildDetail("10201", [])).Rows);

        Assert.Equal("personality-10201.json", PersonaPresetService.SearchKeywordFor(row));
    }

    [Fact]
    public void Open_button_routes_each_kind_to_its_own_workbench()
    {
        Assert.Equal(WorkbenchPageKeys.Text, PersonaPresetService.WorkbenchKeyFor(RelationKind.Text));
        Assert.Equal(WorkbenchPageKeys.Bank, PersonaPresetService.WorkbenchKeyFor(RelationKind.Audio));
        Assert.Equal(WorkbenchPageKeys.Static, PersonaPresetService.WorkbenchKeyFor(RelationKind.StaticData));
        Assert.Equal(WorkbenchPageKeys.Assets, PersonaPresetService.WorkbenchKeyFor(RelationKind.Image));
        Assert.Equal(WorkbenchPageKeys.Assets, PersonaPresetService.WorkbenchKeyFor(RelationKind.Spine));
    }

    [Fact]
    public void Container_entry_index_prefers_the_row_that_can_actually_be_read()
    {
        const string entry = "Assets/Sprite/Unit/Profile/10201.png";
        var bare = Asset(entry, AssetType.Sprite);                       // 没有源文件 / PathId
        var renderable = RenderableAsset(entry, AssetType.Sprite);

        var index = PersonaPresetService.IndexByContainerEntry([bare, renderable]);

        Assert.Same(renderable, index[entry]);
        // 顺序反过来也成立（先到先得不能把可渲染的那条顶掉）。
        Assert.Same(renderable, PersonaPresetService.IndexByContainerEntry([renderable, bare])[entry]);
    }
}
