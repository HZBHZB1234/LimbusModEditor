using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Application.Scanning;

namespace LimbusModEditor.Domain.Tests;

/// <summary>文件管理器式资源目录树（P3.8 改版）：树完全建立在
/// Unity m_Container 的容器条目（<c>metadata["containerEntry"]</c>）之上，
/// 用户看到的是游戏内真实资源路径的逐段展开，而不是缓存键 / Path ID。
/// 真实缓存样本缺失时真实样本测试自动跳过。</summary>
public class AssetTreeBuilderTests
{
    /// <summary>扫描索引形态的资源：LogicalPath 是缓存键路径，容器条目才是
    /// 用户可见路径（与 UnityCacheScanService 写入的元数据一致）。</summary>
    private static AssetRecord ContainerAsset(
        string containerEntry,
        string cacheKey = "outerA/innerA/CAB-aaa",
        AssetType type = AssetType.Texture,
        long size = 100,
        long? pathId = null)
        => new()
        {
            LogicalPath = $"{cacheKey}/{pathId ?? 1}.28",
            ContainerPath = "CAB-aaa",
            UnityPathId = pathId ?? 1,
            Type = type,
            Size = size,
            Metadata =
            {
                ["reference"] = "true",
                ["containerEntry"] = containerEntry,
            },
        };

    /// <summary>导入的旧式资源：没有容器条目，显示路径就是 LogicalPath。</summary>
    private static AssetRecord ImportedAsset(string logicalPath, AssetType type = AssetType.Text, long size = 100)
        => new() { LogicalPath = logicalPath, Type = type, Size = size };

    [Fact]
    public void Roots_group_by_container_first_segment_directories_before_leaves()
    {
        var roots = AssetTreeBuilder.BuildRoots(
        [
            ContainerAsset("ui/logo.png"),
            ContainerAsset("assets/assetbundle/icon.png"),
            ContainerAsset("assets/assetbundle/text.json", type: AssetType.Json),
        ]);
        Assert.Equal(2, roots.Count);
        Assert.Equal(["assets", "ui"], roots.Select(x => x.Name).ToArray());
        Assert.All(roots, x => Assert.False(x.IsLeaf));
        Assert.Equal(2, roots[0].Count);
        Assert.Equal(1, roots[1].Count);
    }

    [Fact]
    public void Root_level_container_entry_becomes_a_leaf_at_root()
    {
        var root = Assert.Single(AssetTreeBuilder.BuildRoots([ContainerAsset("bundle_root.png")]));
        Assert.True(root.IsLeaf);
        Assert.Equal("bundle_root.png", root.Name);
        Assert.Equal("bundle_root.png", AssetDisplay.DisplayPath(root.Asset!));
    }

    [Fact]
    public void Expand_walks_container_segments_and_ends_in_named_leaves()
    {
        var root = Assert.Single(AssetTreeBuilder.BuildRoots(
        [
            ContainerAsset("assets/assetbundle/ui/logo.png"),
            ContainerAsset("assets/assetbundle/ui/icon.png"),
        ]));
        var second = Assert.Single(root.Expand());
        Assert.Equal("assetbundle", second.Name);
        Assert.False(second.IsLeaf);

        var ui = Assert.Single(second.Expand());
        Assert.Equal("ui", ui.Name);
        var leaves = ui.Expand();
        Assert.Equal(["icon.png", "logo.png"], leaves.Select(x => x.Name).ToArray());
        Assert.All(leaves, x => Assert.True(x.IsLeaf));
        Assert.Equal(3, leaves[0].Depth);
        // 叶子不再展开出子节点。
        Assert.Empty(leaves[0].Expand());
    }

    [Fact]
    public void Assets_without_container_entry_fall_into_unnamed_folder()
    {
        // 容器外的支撑对象（没有 m_Container 条目）→ 统一归入「未命名资源」，
        // 叶子名退化为「类型中文 #编号」，保证仍能互相区分。
        var support = new AssetRecord
        {
            LogicalPath = "outerA/innerA/CAB-aaa/777.114",
            ContainerPath = "CAB-aaa",
            UnityPathId = 777,
            Type = AssetType.MonoBehaviour,
            Metadata = { ["reference"] = "true" },
        };
        var root = Assert.Single(AssetTreeBuilder.BuildRoots([support]));
        Assert.Equal(AssetDisplay.UnnamedFolder, root.Name);
        var leaf = Assert.Single(root.Expand());
        Assert.True(leaf.IsLeaf);
        Assert.Equal("脚本数据 #777", leaf.Name);
    }

    [Fact]
    public void Imported_assets_keep_their_logical_path_but_strip_internal_leaf()
    {
        var roots = AssetTreeBuilder.BuildRoots(
        [
            ImportedAsset("lang/cn/strings.json", AssetType.Json),
            ImportedAsset("fsb/bgm.10.83", AssetType.Audio),
        ]);
        Assert.Equal(["fsb", "lang"], roots.Select(x => x.Name).ToArray());
        Assert.Equal("bgm.10.83", Assert.Single(roots[0].Expand()).Name);
        Assert.Equal("strings.json", Assert.Single(Assert.Single(roots[1].Expand()).Expand()).Name);
    }

    [Fact]
    public void Directory_header_count_matches_subtree_assets()
    {
        var root = Assert.Single(AssetTreeBuilder.BuildRoots(
        [
            ContainerAsset("assets/ui/logo.png"),
            ContainerAsset("assets/icon/1.png"),
            ContainerAsset("assets/icon/2.png"),
        ]));
        Assert.Equal(3, root.Count);
        var children = root.Expand();
        Assert.Equal(1, Assert.Single(children, x => x.Name == "ui").Count);
        Assert.Equal(2, Assert.Single(children, x => x.Name == "icon").Count);
    }

    [Fact]
    public void Sibling_names_sort_naturally_and_directories_come_first()
    {
        var root = Assert.Single(AssetTreeBuilder.BuildRoots(
        [
            ContainerAsset("assets/icon10.png"),
            ContainerAsset("assets/icon2.png"),
            ContainerAsset("assets/sub/1.png"),
        ]));
        var children = root.Expand();
        Assert.Equal(["sub", "icon2.png", "icon10.png"], children.Select(x => x.Name).ToArray());
    }

    [Fact]
    public async Task Real_cache_tree_reaches_every_asset_exactly_once()
    {
        // 真实环境门控（与 UnityCacheScanServiceTests 相同口径）。
        var gameDirectory = Environment.GetEnvironmentVariable("LME_GAME_DIR") ??
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company";
        if (!File.Exists(Path.Combine(gameDirectory, "LimbusCompany.exe"))) return;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var realCache = Path.Combine(profile, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany");
        var realEntries = UnityCacheScanService.EnumerateCacheEntries(realCache);
        var picked = realEntries.FirstOrDefault(x => new FileInfo(x.DataPath).Length is > 0 and < 8 * 1024 * 1024);
        if (picked is null) return;

        var root = Path.Combine(Path.GetTempPath(), "lme-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "cache", picked.OuterKey, picked.InnerKey));
        File.Copy(picked.DataPath, Path.Combine(root, "cache", picked.OuterKey, picked.InnerKey, "__data"));
        try
        {
            var project = new ModProject { Name = "TreeTest" };
            await new UnityCacheScanService().ScanIntoProjectAsync(project, Path.Combine(root, "cache"));
            Assert.True(project.Assets.Count > 0);

            var roots = AssetTreeBuilder.BuildRoots(project.Assets);
            Assert.Equal(project.Assets.Count, roots.Sum(x => x.Assets.Count));
            var seen = new HashSet<string>();
            void Walk(AssetTreeNode node)
            {
                if (node.IsLeaf)
                {
                    Assert.True(seen.Add(node.Asset!.AssetId.ToString()), "每条资源在树中恰好出现一次");
                    return;
                }
                var children = node.Expand();
                var covered = children.Sum(x => x.Assets.Count);
                Assert.Equal(node.Assets.Count, covered); // 分组不丢不重
                foreach (var child in children) Walk(child);
            }
            foreach (var r in roots) Walk(r);
            Assert.Equal(project.Assets.Count, seen.Count);
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }
}
