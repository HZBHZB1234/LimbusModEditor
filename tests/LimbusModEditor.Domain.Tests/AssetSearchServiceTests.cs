using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

/// <summary>P3.3: the asset search query covers text, type, edit state,
/// container, Unity Path/Type ID, size range and replacement presence.
/// Replacement presence requires the registered file to still exist.</summary>
public class AssetSearchServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-search-" + Guid.NewGuid().ToString("N"));

    public AssetSearchServiceTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "repl.png"), "png");
        File.WriteAllText(Path.Combine(_root, "stats.json"), "{}");
        // "gone.json" is deliberately never created: the record referencing it
        // must not count as replaced.
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private ModProject Project()
    {
        var project = new ModProject();
        project.Assets.Add(new AssetRecord { LogicalPath = "images/ui/logo.png", Type = AssetType.Texture, Size = 4096, UnityPathId = 100, UnityTypeId = 28, ContainerPath = "bundleA" });
        project.Assets.Add(new AssetRecord { LogicalPath = "images/ui/icon.png", Type = AssetType.Sprite, Size = 512, UnityPathId = 101, UnityTypeId = 213, ContainerPath = "bundleA", Metadata = { ["replacementPath"] = Path.Combine(_root, "repl.png") } });
        project.Assets.Add(new AssetRecord { LogicalPath = "audio/bgm1", Type = AssetType.Audio, Size = 1_000_000, UnityPathId = 200, UnityTypeId = 129, ContainerPath = "bundleB", EditState = AssetEditState.Modified });
        project.Assets.Add(new AssetRecord { LogicalPath = "data/stats.json", Type = AssetType.Json, Size = 256, ContainerPath = "bundleB", EditState = AssetEditState.Added, Metadata = { ["replacementPath"] = Path.Combine(_root, "stats.json") } });
        return project;
    }

    [Fact]
    public void Text_matches_path_and_source()
    {
        var results = new AssetSearchService().Search(Project(), new AssetSearchQuery(Text: "icon"));
        Assert.Single(results);
        Assert.Equal("images/ui/icon.png", results[0].LogicalPath);
    }

    [Fact]
    public void Type_and_state_filters()
    {
        var service = new AssetSearchService();
        Assert.Single(service.Search(Project(), new AssetSearchQuery(Type: AssetType.Texture)));
        Assert.Equal(2, service.Search(Project(), new AssetSearchQuery(State: AssetEditState.Unchanged)).Count);
        var both = service.Search(Project(), new AssetSearchQuery(Type: AssetType.Audio, State: AssetEditState.Modified));
        Assert.Single(both);
    }

    [Fact]
    public void Unity_ids_and_size_range()
    {
        var service = new AssetSearchService();
        Assert.Equal("images/ui/icon.png", service.Search(Project(), new AssetSearchQuery(UnityPathId: 101)).Single().LogicalPath);
        Assert.Equal("images/ui/logo.png", service.Search(Project(), new AssetSearchQuery(UnityTypeId: 28)).Single().LogicalPath);
        Assert.Equal(2, service.Search(Project(), new AssetSearchQuery(MinSize: 1000, MaxSize: 2_000_000)).Count);
        Assert.Empty(service.Search(Project(), new AssetSearchQuery(MinSize: 10_000_000)));
    }

    [Fact]
    public void Replacement_presence_filter_requires_existing_file()
    {
        var project = Project();
        project.Assets.Add(new AssetRecord { LogicalPath = "data/gone.json", Type = AssetType.Json, Size = 128, Metadata = { ["replacementPath"] = Path.Combine(_root, "gone.json") } });
        var service = new AssetSearchService();
        Assert.Equal(2, service.Search(project, new AssetSearchQuery(HasReplacement: true)).Count);
        Assert.Equal(3, service.Search(project, new AssetSearchQuery(HasReplacement: false)).Count);
    }

    [Fact]
    public void Negative_size_bounds_are_rejected_loudly()
    {
        var service = new AssetSearchService();
        Assert.Throws<ArgumentException>(() => service.Search(Project(), new AssetSearchQuery(MaxSize: -1)));
        Assert.Throws<ArgumentException>(() => service.Search(Project(), new AssetSearchQuery(MinSize: -1)));
    }

    [Fact]
    public void Container_substring_and_combined()
    {
        var service = new AssetSearchService();
        Assert.Equal(2, service.Search(Project(), new AssetSearchQuery(Container: "bundleA")).Count);
        var combined = service.Search(Project(), new AssetSearchQuery(Container: "bundleB", State: AssetEditState.Added));
        Assert.Equal("data/stats.json", combined.Single().LogicalPath);
    }

    [Fact]
    public void Snapshot_overload_matches_project_overload()
    {
        // UI 在后台线程过滤时先在 UI 线程取快照：两个口径必须一致。
        var project = Project();
        var service = new AssetSearchService();
        var fromProject = service.Search(project, new AssetSearchQuery(Text: "ui", Type: AssetType.Texture));
        var fromSnapshot = service.Search(project.Assets.ToArray(), new AssetSearchQuery(Text: "ui", Type: AssetType.Texture));
        Assert.Equal(fromProject.Select(x => x.AssetId), fromSnapshot.Select(x => x.AssetId));
    }

    // ── 显示层：排序策略与「仅显示容器内资源」 ────────────────────────────

    /// <summary>扫描索引形态：容器内资源带 m_Container 条目，支撑对象没有。</summary>
    private static ModProject ContainerProject()
    {
        var project = new ModProject();
        project.Assets.Add(new AssetRecord
        {
            LogicalPath = "outerA/innerA/CAB-a/10.28", Type = AssetType.Texture, Size = 4096, ContainerPath = "CAB-a",
            Metadata = { ["reference"] = "true", ["containerEntry"] = "assets/ui/logo.png" },
        });
        project.Assets.Add(new AssetRecord
        {
            LogicalPath = "outerA/innerA/CAB-a/11.28", Type = AssetType.Sprite, Size = 512, ContainerPath = "CAB-a",
            Metadata = { ["reference"] = "true", ["containerEntry"] = "assets/ui/icon.png" },
        });
        project.Assets.Add(new AssetRecord
        {
            LogicalPath = "outerA/innerA/CAB-a/12.114", Type = AssetType.MonoBehaviour, Size = 2048, ContainerPath = "CAB-a",
            UnityPathId = 12,
            Metadata = { ["reference"] = "true" }, // 容器外的支撑对象
        });
        project.Assets.Add(new AssetRecord
        {
            LogicalPath = "imports/manual.png", Type = AssetType.Texture, Size = 128, EditState = AssetEditState.Modified,
        });
        return project;
    }

    [Fact]
    public void Sort_by_size_moves_biggest_or_smallest_first()
    {
        var service = new AssetSearchService();
        var descending = service.Search(ContainerProject(), new AssetSearchQuery(Sort: AssetSortKind.SizeDescending));
        Assert.Equal([4096L, 2048L, 512L, 128L], descending.Select(x => x.Size).ToArray());
        var ascending = service.Search(ContainerProject(), new AssetSearchQuery(Sort: AssetSortKind.SizeAscending));
        Assert.Equal([128L, 512L, 2048L, 4096L], ascending.Select(x => x.Size).ToArray());
    }

    [Fact]
    public void Sort_by_name_is_natural_and_default()
    {
        var project = ContainerProject();
        var service = new AssetSearchService();
        var byName = service.Search(project, new AssetSearchQuery());
        Assert.Equal(
            service.Search(project, new AssetSearchQuery(Sort: AssetSortKind.Name)).Select(x => x.AssetId),
            byName.Select(x => x.AssetId));
        // 显示路径逐段自然排序：assets/ui/* 在 imports/* 之前，容器外对象进「未命名资源」。
        Assert.Equal(
            ["assets/ui/icon.png", "assets/ui/logo.png", "imports/manual.png", $"{AssetDisplay.UnnamedFolder}/脚本数据 #12"],
            byName.Select(x => AssetDisplay.DisplayPath(x)).ToArray());
    }

    [Fact]
    public void Sort_modified_first_puts_edited_assets_on_top()
    {
        var results = new AssetSearchService().Search(ContainerProject(), new AssetSearchQuery(Sort: AssetSortKind.ModifiedFirst));
        Assert.Equal("imports/manual.png", AssetDisplay.DisplayPath(results[0]));
        Assert.Equal(4, results.Count);
    }

    [Fact]
    public void Sort_by_type_groups_the_same_label_together()
    {
        var results = new AssetSearchService().Search(ContainerProject(), new AssetSearchQuery(Sort: AssetSortKind.Type));
        var labels = results.Select(x => AssetDisplay.TypeLabel(x.Type)).ToArray();
        Assert.Equal(labels.OrderBy(x => x, StringComparer.CurrentCulture), labels);
    }

    [Fact]
    public void Container_entry_filter_hides_support_objects_but_keeps_imports()
    {
        var service = new AssetSearchService();
        var visible = service.Search(ContainerProject(), new AssetSearchQuery(HasContainerEntry: true));
        Assert.Equal(3, visible.Count); // 两个容器内资源 + 一个导入资源
        Assert.DoesNotContain(visible, x => x.Type == AssetType.MonoBehaviour);
        var hidden = service.Search(ContainerProject(), new AssetSearchQuery(HasContainerEntry: false));
        Assert.Equal(AssetType.MonoBehaviour, Assert.Single(hidden).Type);
    }

    [Fact]
    public void Static_bundle_assets_are_hidden_by_default_and_shown_on_request()
    {
        var project = ContainerProject();
        var staticAsset = project.Assets.First();
        staticAsset.Metadata[UnityCacheScanService.StaticBundleMetadataKey] = "true";
        var service = new AssetSearchService();

        var defaultView = service.Search(project.Assets.ToArray(), new AssetSearchQuery());
        Assert.DoesNotContain(defaultView, x => x.AssetId == staticAsset.AssetId);
        Assert.Equal(project.Assets.Count - 1, defaultView.Count);

        var withStatic = service.Search(project.Assets.ToArray(), new AssetSearchQuery(ShowStaticTables: true));
        Assert.Contains(withStatic, x => x.AssetId == staticAsset.AssetId);
        Assert.Equal(project.Assets.Count, withStatic.Count);
    }
}
