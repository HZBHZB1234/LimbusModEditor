using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Domain.Tests;

/// <summary>面向用户的资源显示层：容器条目（m_Container）优先，缓存键 /
/// Path ID 一律不出现在用户看到的路径里；缺名资源用「类型中文 + #编号」兜底。</summary>
public class AssetDisplayTests
{
    private static AssetRecord CacheAsset(string? containerEntry, long pathId = 42, AssetType type = AssetType.Texture)
    {
        var asset = new AssetRecord
        {
            LogicalPath = $"outerB/innerB/CAB-b/{pathId}.28",
            ContainerPath = "CAB-b",
            UnityPathId = pathId,
            Type = type,
            Size = 10,
            Metadata = { ["reference"] = "true" },
        };
        if (containerEntry is not null) asset.Metadata["containerEntry"] = containerEntry;
        return asset;
    }

    [Fact]
    public void Container_entry_drives_folder_and_name()
    {
        var asset = CacheAsset("assets/assetbundle/chapter1/portrait.png");
        Assert.Equal("assets/assetbundle/chapter1", AssetDisplay.DisplayFolder(asset));
        Assert.Equal("portrait.png", AssetDisplay.DisplayName(asset));
        Assert.Equal("assets/assetbundle/chapter1/portrait.png", AssetDisplay.DisplayPath(asset));
        // 缓存键与 CAB 名（ContainerPath 字段）绝不进入用户可见路径。
        Assert.DoesNotContain("outerB", AssetDisplay.DisplayPath(asset));
        Assert.DoesNotContain("CAB-b", AssetDisplay.DisplayPath(asset));
    }

    [Fact]
    public void Root_level_container_entry_has_no_folder()
    {
        var asset = CacheAsset("single_root_file.png");
        Assert.Equal(string.Empty, AssetDisplay.DisplayFolder(asset));
        Assert.Equal("single_root_file.png", AssetDisplay.DisplayPath(asset));
    }

    [Fact]
    public void Missing_container_entry_uses_unnamed_folder_and_type_fallback()
    {
        var asset = CacheAsset(null, pathId: 777, type: AssetType.Sprite);
        Assert.Equal(AssetDisplay.UnnamedFolder, AssetDisplay.DisplayFolder(asset));
        Assert.Equal("精灵图 #777", AssetDisplay.DisplayName(asset));
        Assert.Equal($"{AssetDisplay.UnnamedFolder}/精灵图 #777", AssetDisplay.DisplayPath(asset));
    }

    [Fact]
    public void Imported_asset_strips_pure_numeric_leaf()
    {
        var asset = new AssetRecord { LogicalPath = "lang/cn/123.45", Type = AssetType.Text };
        Assert.Equal("lang/cn", AssetDisplay.DisplayPath(asset));

        var named = new AssetRecord { LogicalPath = "lang/cn/strings.json", Type = AssetType.Json };
        Assert.Equal("lang/cn/strings.json", AssetDisplay.DisplayPath(named));
    }

    [Theory]
    [InlineData(AssetType.Texture, "纹理")]
    [InlineData(AssetType.Sprite, "精灵图")]
    [InlineData(AssetType.Audio, "音频")]
    [InlineData(AssetType.MonoBehaviour, "脚本数据")]
    [InlineData(AssetType.GameObject, "游戏对象")]
    [InlineData(AssetType.Unknown, "未知类型")]
    public void Type_labels_are_chinese(AssetType type, string expected)
        => Assert.Equal(expected, AssetDisplay.TypeLabel(type));

    [Theory]
    [InlineData(AssetEditState.Unchanged, "未修改")]
    [InlineData(AssetEditState.Modified, "已修改")]
    [InlineData(AssetEditState.Added, "新增")]
    [InlineData(AssetEditState.Conflict, "冲突")]
    public void State_labels_are_chinese(AssetEditState state, string expected)
        => Assert.Equal(expected, AssetDisplay.StateLabel(state));

    [Fact]
    public void Name_comparison_sorts_digit_runs_numerically()
    {
        var names = new[] { "icon10.png", "icon2.png", "Icon1.png", "texture.png" };
        Array.Sort(names, AssetDisplay.ComparerInstance);
        Assert.Equal(["Icon1.png", "icon2.png", "icon10.png", "texture.png"], names);
    }

    [Fact]
    public void Split_tree_path_drops_empty_segments()
        => Assert.Equal(["a", "b", "c"], AssetDisplay.SplitTreePath("/a//b/c/"));

    /// <summary>
    /// <see cref="AssetDisplay.SegmentAt"/> 是目录树展开热路径上的**零数组分配**
    /// 替代品（原先每条资源一次 <see cref="AssetDisplay.SplitTreePath"/> 会分配一个
    /// <c>string[]</c>，展开一个 30 万条的根节点就是几十 MB 的瞬时分配）。
    /// 它必须与 <c>SplitTreePath</c> 的分段口径**逐格一致**，
    /// 否则树会静默丢段 / 错层 —— 因此这里对真实形态的路径做全量等价比对。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("a")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("/a//b/c/")]
    [InlineData("assets/assetbundle/ui/icon2.png")]
    [InlineData("未命名资源/未命名 #-1234567890123456789")]
    [InlineData("未命名资源/未命名 #0")]
    [InlineData("Assets/Story/Ishmael.psb")]
    [InlineData("a/b/c/d/e/f/g")]
    public void Segment_at_matches_split_tree_path_exactly(string path)
    {
        var expected = AssetDisplay.SplitTreePath(path);
        // 段数与 SplitTreePath 完全一致。
        AssetDisplay.SegmentAt(path, -1, out var count);
        Assert.Equal(expected.Length, count);

        // 每一段（含越界返回空）都与数组版逐字一致。
        for (var i = 0; i < expected.Length + 2; i++)
        {
            var span = AssetDisplay.SegmentAt(path, i, out var countAgain);
            Assert.Equal(count, countAgain); // 顺带验证段数不受索引影响
            Assert.Equal(i < expected.Length ? expected[i] : string.Empty, span.ToString());
        }

        // 负索引与远超段数的索引都返回空，不得抛。
        Assert.True(AssetDisplay.SegmentAt(path, int.MinValue, out _).IsEmpty);
        Assert.True(AssetDisplay.SegmentAt(path, int.MaxValue, out _).IsEmpty);
    }
}
