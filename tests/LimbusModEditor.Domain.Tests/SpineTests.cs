using LimbusModEditor.Application.Assets.Preview;
using LimbusModEditor.Application.Spine;
using LimbusModEditor.Domain.Assets;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// Spine 文本解析（骨架 / 图集）与相关纯逻辑的语义测试。
///
/// <para>夹具刻意贴近<b>真实数据口径</b>（Spine 4.0.64：<c>skins</c> 是数组、
/// 动画按 <c>bones</c>/<c>slots</c> 分组、第一条关键帧没有 <c>time</c>、
/// 图集有 <c>scale:</c> 与 <c>rotate:</c>），因为这些正是最容易解析错的地方。</para>
/// </summary>
public sealed class SpineTests
{
    private const string SkeletonJson = """
    {
      "skeleton": { "hash": "4dSPmSXO/zo", "spine": "4.0.64", "x": 672.6, "y": -1356.2, "width": 914.58, "height": 1158.88 },
      "bones": [
        { "name": "root", "scaleX": 0.3333, "scaleY": 0.3333 },
        { "name": "com", "parent": "root", "x": 3984.95, "y": -2078.71 },
        { "name": "body_1", "parent": "com", "length": 438.29, "rotation": 88.71, "x": -7.14, "y": 0.48, "color": "ff4bd2ff" }
      ],
      "slots": [
        { "name": "b_hair", "bone": "body_1", "attachment": "b_hair" },
        { "name": "bird1", "bone": "com", "attachment": "bird1" }
      ],
      "skins": [
        { "name": "default", "attachments": {
            "b_hair": { "b_hair": { "x": 1.5, "y": -2.5, "rotation": 10, "width": 199, "height": 137 } },
            "bird1": { "bird1": { "x": 46.86, "y": -11.16, "rotation": -97.7, "width": 126, "height": 135 } },
            "mesh_slot": { "mesh_att": { "type": "mesh", "vertices": [0, 0], "uvs": [0, 0], "triangles": [0] } }
        } }
      ],
      "animations": {
        "animation": {
          "slots": { "off_l_ef2": { "rgba": [ { "time": 0.8333, "color": "ffffffff" }, { "time": 0.9667, "color": "ffffff00", "curve": "stepped" }, { "time": 1.2, "color": "ff0000ff" } ] } },
          "bones": {
            "body_1": { "translate": [ { "curve": [0.667, 0, 1.333, 0] }, { "time": 2, "y": 3.66 } ] },
            "body_15": { "rotate": [ { "value": -10.16 }, { "time": 4, "value": -12.49 } ] }
          }
        },
        "animation2": { "bones": { "body_2": { "scale": [ { "time": 1, "x": 1.1, "y": 0.9 } ] } } }
      }
    }
    """;

    private const string AtlasText = """
    cg_40.png
    size:1969,967
    filter:Linear,Linear
    pma:true
    scale:0.333
    b_coat
    bounds:2,607,358,452
    rotate:90
    b_hair
    bounds:635,279,199,137
    rotate:90
    bird1
    bounds:1564,70,42,45
    birdie
    bounds:100,110,20,30
    orig:50,60
    offsets:5,6

    front.png
    size:512,512
    filter:Linear,Linear
    front
    bounds:0,0,512,512
    """;

    // ── 骨架 ─────────────────────────────────────────────────────────

    [Fact]
    public void Skeleton_parses_bones_slots_attachments_and_animations()
    {
        var skeleton = SpineTextParser.ParseSkeleton(SkeletonJson);

        Assert.NotNull(skeleton);
        Assert.Equal("4.0.64", skeleton!.Version);
        Assert.Equal(914.58, skeleton.Width, 3);
        Assert.Equal(3, skeleton.Bones.Count);
        Assert.Equal("root", skeleton.Bones[0].Name);
        Assert.Null(skeleton.Bones[0].Parent);
        Assert.Equal(0.3333, skeleton.Bones[0].ScaleX, 4);
        Assert.Equal("com", skeleton.Bones[1].Name);
        Assert.Equal("root", skeleton.Bones[1].Parent);
        Assert.Equal(88.71, skeleton.Bones[2].Rotation, 3);
        Assert.Equal(2, skeleton.Slots.Count);
        Assert.Equal("body_1", skeleton.Slots[0].Bone);

        // 只收 region 附件；mesh 计入「不支持」。
        Assert.Equal(2, skeleton.Attachments.Count);
        Assert.Equal(1, skeleton.UnsupportedAttachmentCount);
        Assert.Equal(1, skeleton.AttachmentKindCounts["mesh"]);
        var hair = skeleton.Attachments["b_hair"];
        Assert.Equal(199, hair.Width);
        Assert.Equal(10, hair.Rotation);
        Assert.Equal("b_hair", hair.Path);

        Assert.Equal(2, skeleton.Animations.Count);
        // 没写 inherit = normal → 不算「简化继承」。
        Assert.Equal(0, skeleton.SimplifiedInheritCount);
    }

    [Fact]
    public void Animation_duration_and_keyframes_follow_spine_semantics()
    {
        var skeleton = SpineTextParser.ParseSkeleton(SkeletonJson)!;
        var animation = skeleton.Animations.Single(a => a.Name == "animation");

        // 时长 = 全部时间线里最大的时间（rotate 到 4 秒）。
        Assert.Equal(4, animation.Duration, 3);
        Assert.Equal(2, animation.Bones.Count);

        // translate：第一条关键帧没有 time（= 0）且没写 x/y → 两个属性都是 null，
        // 由求值方回落到 setup 值（这是 Spine 的语义，不是解析缺陷）。
        var body1 = animation.Bones["body_1"];
        Assert.Equal(2, body1.Count);
        Assert.Equal(0, body1[0].Time);
        Assert.Null(body1[0].X);
        Assert.Null(body1[0].Y);
        Assert.Equal(2, body1[1].Time);
        Assert.Equal(3.66, body1[1].Y!.Value, 3);

        // rotate 是单值属性，落在 Rotation 上。
        var body15 = animation.Bones["body_15"];
        Assert.Equal(-10.16, body15[0].Rotation!.Value, 3);
        Assert.Equal(-12.49, body15[1].Rotation!.Value, 3);

        // 槽位：attachment（换装 / 显隐）与 rgba（淡入淡出）并成一条键序列。
        Assert.Single(animation.Slots);
        var fade = animation.Slots["off_l_ef2"];
        Assert.Equal(3, fade.Count);
        // rgba 只取 alpha（RRGGBBAA 的末两位）——预览关心的是淡出，不是颜色本身。
        Assert.Equal(1.0, fade[0].Alpha!.Value, 3);
        Assert.Equal(0.0, fade[1].Alpha!.Value, 3);
        Assert.Equal(1.0, fade[2].Alpha!.Value, 3);
        // stepped 只表示「本键到下一键之间不插值」；最后一个键没有下一键，因此恒为 false。
        Assert.False(fade[0].Stepped);
        Assert.True(fade[1].Stepped);
        Assert.False(fade[2].Stepped);
    }

    [Fact]
    public void Skeleton_detection_rejects_non_spine_json()
    {
        Assert.Null(SpineTextParser.ParseSkeleton("""{"a":1}"""));
        Assert.Null(SpineTextParser.ParseSkeleton("""{"bones":"not-an-array"}"""));
        Assert.Null(SpineTextParser.ParseSkeleton("not json at all"));
        // 有 bones 但为空 = 不是骨架。
        Assert.Null(SpineTextParser.ParseSkeleton("""{"bones":[]}"""));
    }

    // ── 图集 ─────────────────────────────────────────────────────────

    [Fact]
    public void Atlas_parses_regions_with_rotation_trim_and_multiple_pages()
    {
        var atlas = SpineTextParser.ParseAtlas(AtlasText);

        Assert.Equal(2, atlas.Pages.Count);
        Assert.Equal(5, atlas.RegionCount);

        var page = atlas.Pages[0];
        Assert.Equal("cg_40.png", page.Name);
        Assert.Equal(1969, page.Width);
        Assert.Equal(967, page.Height);
        Assert.Equal(0.333, page.Scale, 4);
        Assert.Equal(4, page.Regions.Count);

        var coat = page.Regions.Single(r => r.Name == "b_coat");
        Assert.Equal(2, coat.X);
        Assert.Equal(607, coat.Y);
        Assert.Equal(358, coat.Width);
        Assert.Equal(452, coat.Height);
        Assert.Equal(90, coat.Rotation);

        // 裁剪信息（orig / offsets）也要读进来。
        var birdie = page.Regions.Single(r => r.Name == "birdie");
        Assert.Equal(50, birdie.OriginalWidth);
        Assert.Equal(60, birdie.OriginalHeight);
        Assert.Equal(5, birdie.OffsetX);
        Assert.Equal(6, birdie.OffsetY);

        Assert.Equal("front.png", atlas.Pages[1].Name);
        Assert.Equal(512, atlas.Pages[1].Width);
    }

    [Fact]
    public void Atlas_detection_needs_a_size_header()
    {
        Assert.True(SpineTextParser.LooksLikeAtlas(AtlasText));
        Assert.False(SpineTextParser.LooksLikeAtlas("hello world\nthis is not an atlas"));
        Assert.False(SpineTextParser.LooksLikeAtlas(""));
    }

    [Fact]
    public void Parse_dispatches_by_content_and_tolerates_a_bom()
    {
        var skeleton = SpineTextParser.Parse("\uFEFF" + SkeletonJson);
        Assert.NotNull(skeleton.Skeleton);
        Assert.Null(skeleton.Atlas);

        var atlas = SpineTextParser.Parse("\uFEFF" + AtlasText);
        Assert.NotNull(atlas.Atlas);
        Assert.Null(atlas.Skeleton);

        Assert.False(SpineTextParser.Parse("just some text").Any);
        Assert.False(SpineTextParser.Parse(null).Any);
        Assert.False(SpineTextParser.Parse("").Any);
    }

    // ── 同目录索引 ───────────────────────────────────────────────────

    [Fact]
    public void Sibling_index_groups_assets_by_container_folder()
    {
        var folder = "Assets/Resources_moved/Story/CG/Ep9_3/StorySpine_Sinclair";
        var index = new SpineSiblingIndex(
        [
            Asset(folder + "/cg_40.json", AssetType.Text),
            Asset(folder + "/cg_40.atlas.txt", AssetType.Text),
            Asset(folder + "/cg_40.png", AssetType.Texture),
            Asset("Assets/other/Elsewhere.json", AssetType.Text),
            Asset("no-container-entry", AssetType.Text, withEntry: false),
        ]);

        Assert.Equal(4, index.IndexedAssetCount);
        Assert.Equal(2, index.FolderCount);
        var siblings = index.Siblings(folder + "/cg_40.json");
        Assert.Equal(3, siblings.Count);
        Assert.Empty(index.Siblings("Assets/nothing/Here.json"));
        Assert.Empty(index.Siblings(string.Empty));

        Assert.Equal(folder, SpineSiblingIndex.FolderOf(folder + "/cg_40.json"));
        Assert.Equal("cg_40.json", SpineSiblingIndex.FileNameOf(folder + "/cg_40.json"));
    }

    [Fact]
    public void Spine_text_detection_is_path_based_and_cheap()
    {
        Assert.True(SpinePreviewService.LooksLikeSpineText(Asset("Assets/x/cg_40.atlas.txt", AssetType.Text)));
        Assert.True(SpinePreviewService.LooksLikeSpineText(Asset("Assets/Prefab/SpineIllustPrefab/10202_gacksung.prefab", AssetType.Text)));
        Assert.True(SpinePreviewService.LooksLikeSpineText(Asset("Assets/Story/StandingModel/RyoshuF.psb", AssetType.Json)));
        Assert.False(SpinePreviewService.LooksLikeSpineText(Asset("Assets/StaticData/static-data/personality/a.json", AssetType.Json)));
        Assert.False(SpinePreviewService.LooksLikeSpineText(Asset("Assets/x/tex.png", AssetType.Texture)));
    }

    [Fact]
    public void Export_folder_name_comes_from_the_container_folder_leaf()
    {
        Assert.Equal("StorySpine_Sinclair",
            SpineExportService.FolderNameFor("Assets/Resources_moved/Story/CG/Ep9_3/StorySpine_Sinclair/cg_40.json"));
        Assert.Equal("spine", SpineExportService.FolderNameFor("cg_40.json"));
        Assert.Equal("spine", SpineExportService.FolderNameFor(string.Empty));
    }

    // ── 图集布局叠加图 ───────────────────────────────────────────────

    [Fact]
    public void Layout_overlay_draws_region_boxes_without_touching_the_outside()
    {
        byte[] white;
        using (var image = new Image<Rgba32>(8, 8, new Rgba32(255, 255, 255, 255)))
        using (var stream = new MemoryStream())
        {
            image.SaveAsPng(stream);
            white = stream.ToArray();
        }
        var page = new SpineAtlasPage("p.png", 8, 8, 1.0,
            [new SpineAtlasRegion("r", 2, 2, 4, 4, 0, 0, 0, 0, 0)]);

        var result = SpinePreviewService.DrawRegionBoxes(white, page);

        using var drawn = Image.Load<Rgba32>(result);
        Assert.Equal(8, drawn.Width);
        // 区域外一个像素都不许动。
        Assert.Equal(new Rgba32(255, 255, 255, 255), drawn[0, 0]);
        Assert.Equal(new Rgba32(255, 255, 255, 255), drawn[7, 7]);
        // 四角是红框。
        Assert.Equal(new Rgba32(255, 64, 64, 255), drawn[2, 2]);
        Assert.Equal(new Rgba32(255, 64, 64, 255), drawn[5, 5]);
        // 内部被蓝色半透明覆盖（不再是纯白）。
        var inside = drawn[3, 3];
        Assert.Equal(255, inside.A);
        Assert.True(inside.B > inside.R, $"内部像素应偏蓝，实际 {inside}");
    }

    [Fact]
    public void Layout_overlay_scales_down_oversized_pages()
    {
        byte[] png;
        using (var image = new Image<Rgba32>(2048, 64, new Rgba32(10, 20, 30, 255)))
        using (var stream = new MemoryStream())
        {
            image.SaveAsPng(stream);
            png = stream.ToArray();
        }
        var page = new SpineAtlasPage("big.png", 2048, 64, 1.0, []);

        using var drawn = Image.Load<Rgba32>(SpinePreviewService.DrawRegionBoxes(png, page));

        Assert.Equal(SpinePreviewService.MaxLayoutDimension, drawn.Width);
    }

    // ── 预览 provider 的契约 ─────────────────────────────────────────

    [Fact]
    public void Provider_declines_paths_that_are_not_spine_and_accepts_spine_ones()
    {
        var service = new SpinePreviewService(() => []);
        var provider = new SpinePreviewProvider(service);

        Assert.Equal("Spine 预览", provider.Name);
        Assert.False(provider.CanPreview(Asset("Assets/x/plain.txt", AssetType.Text)));
        // 真实口径：剧情 Spine 在 <c>Story/Spine/**</c> 下（不是「Spine/Story」）。
        Assert.True(provider.CanPreview(Asset("Assets/Resources_moved/Story/Spine/ch1/thing.json", AssetType.Json)));
    }

    private static AssetRecord Asset(string containerEntry, AssetType type, bool withEntry = true)
    {
        var asset = new AssetRecord { Type = type, LogicalPath = containerEntry };
        if (withEntry) asset.Metadata["containerEntry"] = containerEntry;
        return asset;
    }
}
