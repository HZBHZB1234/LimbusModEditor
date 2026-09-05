using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Editing.Images;

namespace LimbusModEditor.Formats.Unity;

public sealed record UnityFieldNode(string Path, string Name, string Type, string? Value,
    IReadOnlyList<UnityFieldNode> Children);
public sealed record UnityObjectReference(string FieldPath, long FileId, long PathId, string? TargetType);

/// <summary>
/// Converts AssetsTools.NET descriptors into the editor's neutral asset model.
/// The service deliberately does not expose AssetsTools.NET types to the rest of
/// the application, which keeps format-specific dependencies at the boundary.
/// </summary>
public sealed class UnityAssetService
{
    public IReadOnlyList<UnityFieldNode> ReadObjectFields(string serializedFilePath, long pathId,
        CancellationToken cancellationToken = default)
    {
        using var backend = new AssetsToolsBackend();
        return backend.ReadObjectFields(serializedFilePath, pathId, cancellationToken);
    }

    public IReadOnlyList<UnityFieldNode> ReadBundleObjectFields(string bundlePath, string serializedFileName,
        long pathId, CancellationToken cancellationToken = default)
    {
        using var backend = new AssetsToolsBackend();
        return backend.ReadBundleObjectFields(bundlePath, serializedFileName, pathId, cancellationToken);
    }

    public IReadOnlyList<UnityObjectReference> ReadObjectReferences(string serializedFilePath, long pathId,
        CancellationToken cancellationToken = default)
    {
        using var backend = new AssetsToolsBackend();
        return backend.ReadObjectReferences(serializedFilePath, pathId, cancellationToken);
    }

    public IReadOnlyList<UnityObjectReference> ReadBundleObjectReferences(string bundlePath, string serializedFileName,
        long pathId, CancellationToken cancellationToken = default)
    {
        using var backend = new AssetsToolsBackend();
        return backend.ReadBundleObjectReferences(bundlePath, serializedFileName, pathId, cancellationToken);
    }

    public IReadOnlyList<AssetRecord> ScanSerializedFile(string serializedFilePath,
        string? account = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedFilePath);
        using var backend = new AssetsToolsBackend();
        var fileName = Path.GetFileName(serializedFilePath);
        return backend.ReadSerializedObjects(serializedFilePath)
            .Select((obj, index) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var type = obj.TypeId switch
                {
                    28 => AssetType.Texture,
                    83 => AssetType.Audio,
                    114 => AssetType.MonoBehaviour,
                    115 => AssetType.Sprite,
                    128 => AssetType.Font,
                    142 => AssetType.Binary,
                    213 => AssetType.ScriptableObject,
                    1 => AssetType.GameObject,
                    _ => AssetType.Unknown
                };
                return new AssetRecord
                {
                    LogicalPath = $"{fileName}/{obj.PathId}.{obj.TypeId}",
                    SourcePath = serializedFilePath,
                    ContainerPath = fileName,
                    Account = account,
                    Bundle = fileName,
                    UnityPathId = obj.PathId,
                    UnityTypeId = obj.TypeId,
                    Type = type,
                    Size = obj.Data.LongLength,
                    Metadata =
                    {
                        ["serializedFileIndex"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["unitySerializedFile"] = "true"
                    }
                };
            }).ToArray();
    }

    public byte[]? ReadTexturePng(string bundlePath, long pathId,
        CancellationToken cancellationToken = default)
    {
        using var backend = new AssetsToolsBackend();
        var texture = backend.ReadTexture(bundlePath, pathId, cancellationToken);
        if (texture is null || !Enum.IsDefined(typeof(UnityTexturePixelFormat), texture.TextureFormat)) return null;
        return new UnityTextureCodec().ToPng(new UnityTextureInfo(texture.Width, texture.Height,
            (UnityTexturePixelFormat)texture.TextureFormat, texture.PixelData));
    }

    public UnitySpriteObject? ReadSprite(string bundlePath, long pathId,
        CancellationToken cancellationToken = default)
    {
        using var backend = new AssetsToolsBackend();
        return backend.ReadSprite(bundlePath, pathId, cancellationToken);
    }

    public IReadOnlyList<AssetRecord> ScanBundle(
        string bundlePath,
        string? account = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        cancellationToken.ThrowIfCancellationRequested();

        using var backend = new AssetsToolsBackend();
        var descriptors = backend.InspectBundle(bundlePath);
        var bundleName = Path.GetFileName(bundlePath);

        return descriptors.Select((descriptor, index) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logicalPath = $"{bundleName}/{descriptor.ContainerPath}/{descriptor.PathId}.{descriptor.TypeId}";
            return new AssetRecord
            {
                LogicalPath = logicalPath,
                SourcePath = bundlePath,
                ContainerPath = descriptor.ContainerPath,
                Account = account,
                Bundle = bundleName,
                UnityPathId = descriptor.PathId,
                UnityTypeId = descriptor.TypeId,
                Type = descriptor.AssetType,
                Size = descriptor.ByteSize,
                Metadata =
                {
                    ["bundleIndex"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["unityBundle"] = "true"
                }
            };
        }).ToArray();
    }
}
