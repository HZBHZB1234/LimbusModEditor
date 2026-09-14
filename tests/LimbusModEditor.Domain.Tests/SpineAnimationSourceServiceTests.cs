using LimbusModEditor.Application.Spine;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// Spine 动画素材解析（<see cref="SpineAnimationSourceService"/>）语义测试。
///
/// <para><b>为什么值得单独钉</b>：这个服务决定「哪个文件是骨架、哪个是图集、哪张图是哪一页」——
/// 判错了不会报错，只会给用户一个「加载失败」的莫名其妙的结果。真实数据里三件套在同一个
/// 容器目录（<c>cg_40.json</c> / <c>cg_40.atlas.txt</c> / <c>cg_40.png</c>），
/// 而用户可能从三者中任意一个点进来，所以「从任意入口都解析出同一套素材」必须成立。</para>
/// </summary>
public sealed class SpineAnimationSourceServiceTests : IDisposable
{
    private const string Folder = "Assets/Resources_moved/Story/CG/Ep9_3/StorySpine_Sinclair";
    private const string SkeletonJson = """{"skeleton":{"spine":"4.0.0"},"bones":[{"name":"root"}],"slots":[],"skins":[]}""";
    private const string AtlasText = "cg_40.png\nsize: 2048,2048\nformat: RGBA8888\nfilter: Linear,Linear\nrepeat: none\nbg\n  bounds: 0,0,64,64\n";

    private readonly string _root;

    public SpineAnimationSourceServiceTests()
        => _root = Path.Combine(Path.GetTempPath(), "lme-spine-src-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* 临时目录 */ }
    }

    /// <summary>建一个「磁盘上真有这个文件」的容器资源（骨架 / 图集文本走 File.ReadAllText）。</summary>
    private AssetRecord TextAsset(string containerEntry, string content)
    {
        var file = WriteFile(containerEntry, content);
        var asset = new AssetRecord
        {
            Type = containerEntry.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? AssetType.Json : AssetType.Text,
            SourcePath = file,
            UnityPathId = 1,
            LogicalPath = containerEntry,
        };
        asset.Metadata["containerEntry"] = containerEntry;
        return asset;
    }

    /// <summary>贴图资源：有名字就够了（这里只验证「页名 → 文件」的映射，不去真解码）。</summary>
    private static AssetRecord TextureAsset(string containerEntry)
    {
        var asset = new AssetRecord { Type = AssetType.Texture, LogicalPath = containerEntry, UnityPathId = 2 };
        asset.Metadata["containerEntry"] = containerEntry;
        return asset;
    }

    private string WriteFile(string containerEntry, string content)
    {
        var relative = containerEntry.Replace('/', Path.DirectorySeparatorChar);
        var file = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return file;
    }

    private SpineAnimationSourceService Service(params AssetRecord[] assets)
    {
        var list = (IReadOnlyList<AssetRecord>)assets;
        return new SpineAnimationSourceService(new SpinePreviewService(() => list));
    }

    private AssetRecord[] ThreePiece()
    {
        var skeletonEntry = Folder + "/cg_40.json";
        var atlasEntry = Folder + "/cg_40.atlas.txt";
        var textureEntry = Folder + "/cg_40.png";
        WriteFile(textureEntry, "not-a-real-png");
        return
        [
            TextAsset(skeletonEntry, SkeletonJson),
            TextAsset(atlasEntry, AtlasText),
            TextureAsset(textureEntry),
        ];
    }

    [Fact]
    public void Resolves_skeleton_atlas_and_page_names_from_the_same_folder()
    {
        var assets = ThreePiece();

        var (source, error) = Service(assets).Resolve(assets[0]);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Equal(SkeletonJson, source!.SkeletonJson);
        Assert.Equal(AtlasText, source.AtlasText);
        Assert.Equal("StorySpine_Sinclair", source.Label);
        Assert.Contains("cg_40.png", source.AvailablePages);
    }

    [Fact]
    public void Entering_from_the_atlas_asset_resolves_the_sibling_skeleton()
    {
        // 用户从资源页点 .atlas.txt 进来看动画：骨架是同目录的另一个资源，必须照样找得到。
        var assets = ThreePiece();

        var (source, error) = Service(assets).Resolve(assets[1]);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Equal(SkeletonJson, source!.SkeletonJson);
        Assert.Equal(AtlasText, source.AtlasText);
    }

    [Fact]
    public void Missing_skeleton_is_reported_in_chinese_instead_of_throwing()
    {
        var atlasEntry = Folder + "/cg_40.atlas.txt";
        var atlas = TextAsset(atlasEntry, AtlasText);

        var (source, error) = Service(atlas).Resolve(atlas);

        Assert.Null(source);
        Assert.Contains("骨架", error);
    }

    [Fact]
    public void Missing_atlas_is_reported_in_chinese_instead_of_throwing()
    {
        var skeletonEntry = Folder + "/cg_40.json";
        var skeleton = TextAsset(skeletonEntry, SkeletonJson);

        var (source, error) = Service(skeleton).Resolve(skeleton);

        Assert.Null(source);
        Assert.Contains("图集", error);
    }

    [Fact]
    public void Files_in_another_folder_do_not_leak_in()
    {
        // 同目录口径：另一个目录里的骨架 / 图集不能被当成这个资源的素材。
        var selfEntry = Folder + "/cg_40.json";
        var self = TextAsset(selfEntry, SkeletonJson);
        var other = TextAsset("Assets/Other/Elsewhere/cg_99.atlas.txt", AtlasText);

        var (source, error) = Service(self, other).Resolve(self);

        Assert.Null(source);
        Assert.Contains("图集", error);
    }

    [Fact]
    public void Unknown_page_name_yields_empty_bytes_rather_than_an_exception()
    {
        // 渲染器据此报「图集页缺失，无法渲染：xxx.png」——所以这里必须是空数组、不是异常。
        var assets = ThreePiece();
        var (source, error) = Service(assets).Resolve(assets[0]);
        Assert.Null(error);

        var bytes = source!.PageBytes("does_not_exist.png");

        Assert.Empty(bytes);
    }
}
