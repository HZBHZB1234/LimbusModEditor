using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

/// <summary>P3.3: the asset search query covers text, type, edit state,
/// container, Unity Path/Type ID, size range and replacement presence.</summary>
public class AssetSearchServiceTests
{
    private static ModProject Project()
    {
        var project = new ModProject();
        project.Assets.Add(new AssetRecord { LogicalPath = "images/ui/logo.png", Type = AssetType.Texture, Size = 4096, UnityPathId = 100, UnityTypeId = 28, ContainerPath = "bundleA" });
        project.Assets.Add(new AssetRecord { LogicalPath = "images/ui/icon.png", Type = AssetType.Sprite, Size = 512, UnityPathId = 101, UnityTypeId = 213, ContainerPath = "bundleA", Metadata = { ["replacementPath"] = @"C:\repl.png" } });
        project.Assets.Add(new AssetRecord { LogicalPath = "audio/bgm1", Type = AssetType.Audio, Size = 1_000_000, UnityPathId = 200, UnityTypeId = 129, ContainerPath = "bundleB", EditState = AssetEditState.Modified });
        project.Assets.Add(new AssetRecord { LogicalPath = "data/stats.json", Type = AssetType.Json, Size = 256, ContainerPath = "bundleB", EditState = AssetEditState.Added, Metadata = { ["replacementPath"] = @"C:\stats.json" } });
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
    public void Replacement_presence_filter()
    {
        var service = new AssetSearchService();
        Assert.Equal(2, service.Search(Project(), new AssetSearchQuery(HasReplacement: true)).Count);
        Assert.Equal(2, service.Search(Project(), new AssetSearchQuery(HasReplacement: false)).Count);
    }

    [Fact]
    public void Container_substring_and_combined()
    {
        var service = new AssetSearchService();
        Assert.Equal(2, service.Search(Project(), new AssetSearchQuery(Container: "bundleA")).Count);
        var combined = service.Search(Project(), new AssetSearchQuery(Container: "bundleB", State: AssetEditState.Added));
        Assert.Equal("data/stats.json", combined.Single().LogicalPath);
    }
}
