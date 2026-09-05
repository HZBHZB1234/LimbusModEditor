using System.Text.Json;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Assets;

/// <summary>Stores validated primitive field edits for Unity serialized
/// objects. The format layer applies them during Bundle/SerializedFile build.</summary>
public sealed class UnityFieldEditService
{
    public const string MetadataKey = "unityFieldEdits";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public IReadOnlyDictionary<string, string>? ReadStored(AssetRecord asset)
    {
        if (!asset.Metadata.TryGetValue(MetadataKey, out var json)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options); }
        catch (JsonException) { return null; }
    }

    public void Set(ModProject project, AssetRecord asset, IReadOnlyDictionary<string, string> edits)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0) throw new ArgumentException("至少需要一个字段修改。", nameof(edits));
        if (edits.Keys.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("字段路径不能为空。", nameof(edits));
        var normalized = edits.ToDictionary(x => x.Key.Trim(), x => x.Value ?? string.Empty, StringComparer.Ordinal);
        asset.Metadata[MetadataKey] = JsonSerializer.Serialize(normalized, Options);
        asset.EditState = asset.EditState == AssetEditState.Added ? AssetEditState.Added : AssetEditState.Modified;
        project.Edits.Add(new EditOperation
        {
            Kind = EditOperationKind.ReplaceAsset,
            AssetId = asset.AssetId,
            TargetPath = asset.LogicalPath,
            BeforeHash = asset.OriginalHash
        });
    }

    public void Clear(ModProject project, AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        asset.Metadata.Remove(MetadataKey);
        if (asset.EditState == AssetEditState.Modified) asset.EditState = AssetEditState.Unchanged;
    }
}
