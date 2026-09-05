using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Assets;

public sealed record AssetSearchQuery(
    string? Text = null,
    AssetType? Type = null,
    AssetEditState? State = null,
    string? Container = null,
    long? UnityPathId = null,
    int? UnityTypeId = null,
    long? MinSize = null,
    long? MaxSize = null,
    bool? HasReplacement = null);

public sealed class AssetSearchService
{
    public IReadOnlyList<AssetRecord> Search(ModProject project, AssetSearchQuery query)
    {
        var text = query.Text?.Trim();
        return project.Assets.Where(asset =>
            (string.IsNullOrEmpty(text) || asset.LogicalPath.Contains(text, StringComparison.OrdinalIgnoreCase) || asset.SourcePath?.Contains(text, StringComparison.OrdinalIgnoreCase) == true) &&
            (query.Type is null || asset.Type == query.Type) &&
            (query.State is null || asset.EditState == query.State) &&
            (string.IsNullOrEmpty(query.Container) || asset.ContainerPath?.Contains(query.Container, StringComparison.OrdinalIgnoreCase) == true) &&
            (query.UnityPathId is null || asset.UnityPathId == query.UnityPathId) &&
            (query.UnityTypeId is null || asset.UnityTypeId == query.UnityTypeId) &&
            (query.MinSize is null || asset.Size >= query.MinSize) &&
            (query.MaxSize is null || query.MaxSize < 0 || asset.Size <= query.MaxSize) &&
            (query.HasReplacement is null || asset.Metadata.ContainsKey("replacementPath") == query.HasReplacement))
            .OrderBy(x => x.LogicalPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
