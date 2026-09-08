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
}
