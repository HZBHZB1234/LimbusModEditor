using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Domain.Tests;

/// <summary>文件管理器式资源目录树（P3.8 改版）：树完全建立在
/// Unity m_Container 的容器条目（<c>metadata["containerEntry"]</c>）之上，
/// 用户看到的是游戏内真实资源路径的逐段展开，而不是缓存键 / Path ID。
/// 真实缓存样本缺失时真实样本测试自动跳过。</summary>
public class AssetTreeBuilderTests
{
    /// <summary>扫描索引形态的资源：LogicalPath 是缓存键路径，容器条目才是
    /// 用户可见路径（与 UnityCacheScanService 写入的元数据一致，含 cacheOuter /
    /// cacheInner —— 消歧后缀的 bundle 归属回退用得到）。</summary>
    private static AssetRecord ContainerAsset(
        string containerEntry,
        string cacheKey = "outerA/innerA/CAB-aaa",
        AssetType type = AssetType.Texture,
        long size = 100,
        long? pathId = null)
    {
        var parts = cacheKey.Split('/', 3);
        return new()
        {
            LogicalPath = $"{cacheKey}/{pathId ?? 1}.28",
            ContainerPath = parts.Length > 2 ? parts[2] : "CAB-aaa",
            UnityPathId = pathId ?? 1,
            Type = type,
            Size = size,
            Metadata =
            {
                ["reference"] = "true",
                ["containerEntry"] = containerEntry,
                ["cacheOuter"] = parts.Length > 0 ? parts[0] : string.Empty,
                ["cacheInner"] = parts.Length > 1 ? parts[1] : string.Empty,
            },
        };
    }

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
    public void Same_container_path_leaves_are_disambiguated_by_type_and_path_id()
    {
        // 真实缓存里 92.7% 的容器条目是重名的（一个 m_Container 路径挂多个对象：
        // 入口 + 它连带的 Prefab / GameObject / Transform / Renderer 等零件）。
        // 消歧前这两条在树里显示成两个完全一样的 "Ishmael.psb"，用户无法分辨。
        var root = Assert.Single(AssetTreeBuilder.BuildRoots(
        [
            ContainerAsset("Assets/Story/Ishmael.psb", type: AssetType.Texture, pathId: 11),
            ContainerAsset("Assets/Story/Ishmael.psb", type: AssetType.GameObject, pathId: 22),
        ]));
        var story = Assert.Single(root.Expand(), x => x.Name == "Story");
        var leaves = story.Expand();
        Assert.Equal(
            ["Ishmael.psb · 游戏对象 #22", "Ishmael.psb · 纹理 #11"],
            leaves.Select(x => x.Name).ToArray());
        // 消歧不改变资源归属：两条资源各自仍是独立叶子、仍可分别选中。
        Assert.All(leaves, x => Assert.True(x.IsLeaf));
        Assert.Equal(2, leaves.Select(x => x.Asset!.AssetId).Distinct().Count());
        Assert.Equal(2, root.Count);
    }

    [Fact]
    public void Unique_leaves_keep_their_bare_name()
    {
        // 唯一名（23,326 条唯一容器路径的常态）不得被加上任何后缀。
        var root = Assert.Single(AssetTreeBuilder.BuildRoots(
        [
            ContainerAsset("Assets/Sprites/logo.png", pathId: 11),
            ContainerAsset("Assets/Sprites/icon.png", pathId: 22),
            ContainerAsset("Assets/Prefab/solo.prefab", pathId: 33),
        ]));
        var children = root.Expand();
        Assert.Equal(["Prefab", "Sprites"], children.Select(x => x.Name).ToArray());
        Assert.Equal(
            ["solo.prefab"],
            Assert.Single(children, x => x.Name == "Prefab").Expand().Select(x => x.Name).ToArray());
        Assert.Equal(
            ["icon.png", "logo.png"],
            Assert.Single(children, x => x.Name == "Sprites").Expand().Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Root_level_duplicate_leaves_are_disambiguated_too()
    {
        // 容器条目本身就是单段路径时叶子直接挂在根层，同样要消歧。
        var roots = AssetTreeBuilder.BuildRoots(
        [
            ContainerAsset("standalone.psb", type: AssetType.Texture, pathId: 11),
            ContainerAsset("standalone.psb", type: AssetType.GameObject, pathId: 22),
        ]);
        Assert.Equal(
            ["standalone.psb · 游戏对象 #22", "standalone.psb · 纹理 #11"],
            roots.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Same_type_and_path_id_fall_back_to_bundle_label()
    {
        // 极窄的一类：同类型 + 同编号，只差 bundle。bundle 被删掉后同级仍必须可分辨。
        var root = Assert.Single(AssetTreeBuilder.BuildRoots(
        [
            ContainerAsset("Assets/dup.psb", cacheKey: "outerA/innerA/CAB-a", type: AssetType.Texture, pathId: 7),
            ContainerAsset("Assets/dup.psb", cacheKey: "outerB/innerB/CAB-b", type: AssetType.Texture, pathId: 7),
        ]));
        var expanded = root.Expand();
        var names = expanded.Select(x => x.Name).ToArray();
        Assert.Equal(2, names.Distinct().Count());
        Assert.All(names, n => Assert.StartsWith("dup.psb · 纹理 #7 ", n));
        Assert.Contains(names, n => n.EndsWith("outerA/innerA", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("outerB/innerB", StringComparison.Ordinal));
    }

    [Fact]
    public void Disambiguated_leaves_still_cover_every_asset_once()
    {
        var assets = new List<AssetRecord>();
        for (var i = 0; i < 30; i++)
            assets.Add(ContainerAsset("Assets/Story/Ishmael.psb", type: AssetType.Component, pathId: 100 + i));
        assets.Add(ContainerAsset("Assets/Story/unique.png", pathId: 999));
        var root = Assert.Single(AssetTreeBuilder.BuildRoots(assets));
        var story = Assert.Single(root.Expand(), x => x.Name == "Story");
        var leaves = story.Expand();
        Assert.Equal(31, leaves.Count);
        Assert.Equal(31, leaves.Select(x => x.Asset!.AssetId).Distinct().Count());
        Assert.Equal(31, leaves.Select(x => x.Name).Distinct().Count()); // 同级叶子名两两不同
    }

    [Fact]
    public async Task Real_cache_sibling_leaves_are_never_ambiguous()
    {
        // 真实环境门控：在真实 bundle 上验证「同级叶子名两两不同」。
        // 单测用的是合成数据；这里只要有一个真实样本撑住，消歧规则就算落地了。
        var gameDirectory = Environment.GetEnvironmentVariable("LME_GAME_DIR") ??
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company";
        if (!File.Exists(Path.Combine(gameDirectory, "LimbusCompany.exe"))) return;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var realCache = Path.Combine(profile, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany");
        // 挑一个「结构上真的含重复容器条目」的真实 bundle，否则断言是空转。
        // 实测 1,471 个真实 bundle 里 336 个含重复（最大一组 19 条），所以
        // 从大到小扫几个就能命中。
        UnityCacheScanEntry? picked = null;
        foreach (var candidate in UnityCacheScanService.EnumerateCacheEntries(realCache)
                     .Where(x => new FileInfo(x.DataPath).Length is > 0 and < 10 * 1024 * 1024)
                     .OrderByDescending(x => new FileInfo(x.DataPath).Length))
        {
            IReadOnlyList<AssetRecord> descriptors;
            try { descriptors = new UnityAssetService().ScanBundle(candidate.DataPath); }
            catch (Exception) { continue; }
            // 只数真正有容器条目的资源：缺 containerEntry 的支撑对象在树里全部落到
            // 「未命名资源」分支（叶子名是「类型 #编号」，本来就互不相同），
            // 把它们算进来会让每个 bundle 都「看起来有重复」。
            var hasDuplicates = descriptors
                .Where(x => x.Metadata.TryGetValue("containerEntry", out var e) && e.Length > 0)
                .GroupBy(x => x.Metadata["containerEntry"], StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Count() > 1);
            if (hasDuplicates) { picked = candidate; break; }
        }
        if (picked is null) return;

        var root = Path.Combine(Path.GetTempPath(), "lme-tree-unique-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "cache", picked.OuterKey, picked.InnerKey));
        File.Copy(picked.DataPath, Path.Combine(root, "cache", picked.OuterKey, picked.InnerKey, "__data"));
        try
        {
            var project = new ModProject { Name = "TreeUniqueTest" };
            await new UnityCacheScanService().ScanIntoProjectAsync(project, Path.Combine(root, "cache"));
            Assert.True(project.Assets.Count > 0);

            var sampled = 0;
            var dirs = 0;
            var disambiguated = 0;
            void Walk(AssetTreeNode node)
            {
                var children = node.Expand();
                // 同级子项名必须两两不同（含目录 vs 叶子）—— 这正是消歧要保证的不变量。
                var names = children.Select(x => x.Name).ToArray();
                Assert.Equal(names.Length, names.Distinct().Count());
                foreach (var child in children)
                {
                    if (child.Asset is not { } asset) continue;
                    sampled++;
                    var bare = AssetDisplay.DisplayName(asset);
                    if (child.Name != bare) disambiguated++;
                    Assert.True(child.Name == bare || child.Name.StartsWith(bare + AssetDisplay.LeafDisambiguatorSeparator, StringComparison.Ordinal),
                        $"叶子名必须是「原名」或「原名 + 消歧后缀」：{child.Name} / 原名 {bare}");
                }
                foreach (var child in children)
                {
                    if (child.IsLeaf) continue;
                    dirs++;
                    Walk(child);
                }
            }
            foreach (var r in AssetTreeBuilder.BuildRoots(project.Assets)) Walk(r);
            // 样本必须真的展开过东西，否则断言是空转。
            Assert.True(sampled > 0, "真实样本里没有任何叶子可供验证");
            Assert.True(disambiguated > 0, $"选中的 bundle 应含重复容器条目，实际消歧 {disambiguated} 条");
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
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
