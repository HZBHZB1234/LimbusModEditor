using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Domain.Tests;

/// <summary>VS Code 式目录树（P3.8）：把扁平 LogicalPath 按路径段分组，
/// 像文件夹一样浏览。真实缓存样本缺失时真实样本测试自动跳过。</summary>
public class AssetTreeBuilderTests
{
    private static AssetRecord Asset(string logicalPath, AssetType type = AssetType.Texture, long size = 100)
        => new() { LogicalPath = logicalPath, Type = type, Size = size };

    [Fact]
    public void Roots_group_by_first_segment_directories_before_leaves()
    {
        var roots = AssetTreeBuilder.BuildRoots(
        [
            Asset("zzz-file"),
            Asset("outerB/inner1/1.28"),
            Asset("outerA/inner1/2.28"),
        ]);
        Assert.Equal(3, roots.Count);
        Assert.Equal(["outerA", "outerB", "zzz-file"], roots.Select(x => x.Name).ToArray());
        Assert.False(roots[0].IsLeaf);
        Assert.Equal(1, roots[0].Count);
        Assert.True(roots[2].IsLeaf);
        Assert.Equal("zzz-file", roots[2].Asset!.LogicalPath);
    }

    [Fact]
    public void Expand_splits_by_depth_and_ends_in_leaves()
    {
        var root = AssetTreeBuilder.BuildRoots([Asset("outer/inner1/10.28"), Asset("outer/inner2/20.129")]).Single();
        var inners = root.Expand();
        Assert.Equal(["inner1", "inner2"], inners.Select(x => x.Name).ToArray());
        Assert.All(inners, x => Assert.False(x.IsLeaf));

        var leaves = inners[0].Expand();
        var leaf = Assert.Single(leaves);
        Assert.True(leaf.IsLeaf);
        Assert.Equal("10.28", leaf.Name);
        Assert.Same(inners[0].Assets[0], leaf.Asset);
        Assert.Equal(2, leaf.Depth);
        // 叶子不再展开出子节点。
        Assert.Empty(leaf.Expand());
    }

    [Fact]
    public void Deep_container_paths_group_segment_by_segment()
    {
        var root = AssetTreeBuilder.BuildRoots(
        [
            Asset("outer/inner/assets/ui/logo/11.28"),
            Asset("outer/inner/assets/ui/icon/12.28"),
            Asset("outer/inner/31.82"), // 容器为空：直接挂在 inner 下
        ]).Single();
        var inner = Assert.Single(root.Expand());
        var second = inner.Expand();
        var container = Assert.Single(second, x => x.Name == "assets");
        Assert.Equal(2, container.Count); // 两个深层资产，容器为空的叶子不进目录

        var ui = Assert.Single(container.Expand());
        var icon = Assert.Single(ui.Expand(), x => x.Name == "icon");
        var logo = Assert.Single(ui.Expand(), x => x.Name == "logo");
        Assert.Equal("12.28", Assert.Single(icon.Expand()).Name);
        var leaf = Assert.Single(logo.Expand());
        Assert.True(leaf.IsLeaf);
        Assert.Equal("11.28", leaf.Name);
        Assert.Equal(5, leaf.Depth);

        var direct = Assert.Single(second, x => x.Name == "31.82");
        Assert.True(direct.IsLeaf);
    }

    [Fact]
    public void Directory_header_count_matches_subtree_assets()
    {
        var root = AssetTreeBuilder.BuildRoots(
        [
            Asset("outer/inner1/1.28"),
            Asset("outer/inner2/2.28"),
            Asset("outer/inner2/3.28"),
        ]).Single();
        Assert.Equal(3, root.Count);
        var inners = root.Expand();
        Assert.Equal(1, inners[0].Count);
        Assert.Equal(2, inners[1].Count);
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
