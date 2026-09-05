using System.Globalization;
using System.Text.Json;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Application.Assets;

/// <summary>Project-level Sprite metadata editing. Values are kept as a
/// deterministic JSON edit record and materialized by UnityBundleBuildService
/// so the source bundle remains untouched until build/debug/export.</summary>
public sealed class SpriteMetadataEditService
{
    private const string MetadataKey = "spriteMetadata";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public UnitySpriteMetadata? ReadStored(AssetRecord asset)
    {
        if (!asset.Metadata.TryGetValue(MetadataKey, out var json)) return null;
        try { return JsonSerializer.Deserialize<UnitySpriteMetadata>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    public void Set(ModProject project, AssetRecord asset, UnitySpriteMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(metadata);
        Validate(metadata);
        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        asset.Metadata[MetadataKey] = json;
        asset.EditState = asset.EditState == AssetEditState.Added ? AssetEditState.Added : AssetEditState.Modified;
        project.Edits.Add(new EditOperation
        {
            Kind = EditOperationKind.ReplaceAsset,
            AssetId = asset.AssetId,
            TargetPath = asset.LogicalPath,
            SourcePath = null,
            BeforeHash = asset.OriginalHash,
            AfterHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)))
        });
    }

    private static void Validate(UnitySpriteMetadata value)
    {
        var numbers = new[]
        {
            value.Rect.X, value.Rect.Y, value.Rect.Width, value.Rect.Height,
            value.Pivot.X, value.Pivot.Y,
            value.Border.Left, value.Border.Bottom, value.Border.Right, value.Border.Top,
            value.PixelsToUnits
        };
        if (numbers.Any(x => float.IsNaN(x) || float.IsInfinity(x)))
            throw new ArgumentException("Sprite 元数据不能包含 NaN 或无穷大。", nameof(value));
        if (value.Rect.Width < 0 || value.Rect.Height < 0)
            throw new ArgumentException("Sprite Rect 的宽度和高度不能为负数。", nameof(value));
        if (value.PixelsToUnits <= 0)
            throw new ArgumentException("Pixels/Unit 必须大于 0。", nameof(value));
    }
}

public sealed record UnitySpriteMetadata(
    UnitySpriteRect Rect,
    UnitySpriteVector2 Pivot,
    UnitySpriteBorder Border,
    float PixelsToUnits)
{
    public static UnitySpriteMetadata From(UnitySpriteObject sprite)
        => new(sprite.Rect, sprite.Pivot, sprite.Border, sprite.PixelsToUnits <= 0 ? 100 : sprite.PixelsToUnits);
}
