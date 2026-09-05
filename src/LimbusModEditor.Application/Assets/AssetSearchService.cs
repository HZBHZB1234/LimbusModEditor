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
        if (query.MinSize is < 0) throw new ArgumentException("MinSize 不能为负。", nameof(query));
        if (query.MaxSize is < 0) throw new ArgumentException("MaxSize 不能为负（负值曾经静默关闭上限）。", nameof(query));
        var text = query.Text?.Trim();
        return project.Assets.Where(asset =>
            (string.IsNullOrEmpty(text) || asset.LogicalPath.Contains(text, StringComparison.OrdinalIgnoreCase) || asset.SourcePath?.Contains(text, StringComparison.OrdinalIgnoreCase) == true) &&
            (query.Type is null || asset.Type == query.Type) &&
            (query.State is null || asset.EditState == query.State) &&
            (string.IsNullOrEmpty(query.Container) || asset.ContainerPath?.Contains(query.Container, StringComparison.OrdinalIgnoreCase) == true) &&
            (query.UnityPathId is null || asset.UnityPathId == query.UnityPathId) &&
            (query.UnityTypeId is null || asset.UnityTypeId == query.UnityTypeId) &&
            (query.MinSize is null || asset.Size >= query.MinSize) &&
            (query.MaxSize is null || asset.Size <= query.MaxSize) &&
            (query.HasReplacement is null || HasUsableReplacement(asset) == query.HasReplacement))
            .OrderBy(x => x.LogicalPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>An asset counts as "replaced" only when its registered
    /// replacement file still exists; a vanished replacement cannot be
    /// materialized by a build and must not hide behind the filter.</summary>
    private static bool HasUsableReplacement(AssetRecord asset)
        => asset.Metadata.TryGetValue("replacementPath", out var path) && File.Exists(path);
}
