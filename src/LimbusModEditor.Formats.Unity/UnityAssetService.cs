using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Editing.Images;

namespace LimbusModEditor.Formats.Unity;

/// <summary>
/// One node of a Unity serialized-object field tree. Editable metadata mirrors
/// the underlying serialized type so the editor can validate before writing:
/// <see cref="ValueType"/> uses normalized names (bool, int8..uint64, float,
/// double, string, byteArray, array, pptr, unknown); enum nodes carry the
/// serialized enum type name; PPtr nodes carry their target file/path IDs.
/// </summary>
public sealed record UnityFieldNode(
    string Path,
    string Name,
    string Type,
    string? Value,
    IReadOnlyList<UnityFieldNode> Children,
    string ValueType = "",
    bool IsArray = false,
    int ArraySize = 0,
    bool IsEnum = false,
    string EnumTypeName = "",
    bool IsPPtr = false,
    long PPtrFileId = 0,
    long PPtrPathId = 0,
    string? PPtrTargetType = null,
    long ByteArrayLength = 0,
    string ByteArrayPreviewHex = "",
    bool Editable = false)
{
    /// <summary>Normalized value type for the primitive kinds that can be
    /// edited as text; null for structure nodes.</summary>
    public static bool IsEditableValueType(string valueType) => valueType switch
    {
        "bool" or "int8" or "uint8" or "int16" or "uint16" or "int32" or
        "uint32" or "int64" or "uint64" or "float" or "double" or "string" => true,
        _ => false
    };
}
public sealed record UnityObjectReference(string FieldPath, long FileId, long PathId, string? TargetType);

public enum UnityFieldEditStatus { Ok, UnknownPath, NotEditable, ParseError }

/// <summary>Result of validating one field edit against the real serialized
/// type before it is stored or written.</summary>
public sealed record UnityFieldEditDiagnostic(string Path, UnityFieldEditStatus Status, string FieldType, string Message)
{
    public bool IsOk => Status == UnityFieldEditStatus.Ok;
}

/// <summary>Script provenance of a MonoBehaviour/ScriptableObject object:
/// the serialized m_Script PPtr, the resolved MonoScript class data when the
/// script lives in the same SerializedFile, the referenced external file when
/// it does not, and the reason the field tree could not be read.</summary>
public sealed record UnityScriptInfo(
    long ScriptFileId,
    long ScriptPathId,
    string? ClassName,
    string? Namespace,
    string? AssemblyName,
    string? ExternalPath,
    string? ExternalGuid,
    string? TypeTreeMissingReason);

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

    public UnityScriptInfo? ReadObjectScriptInfo(string serializedFilePath, long pathId,
        CancellationToken cancellationToken = default)
    {
        using var backend = new AssetsToolsBackend();
        return backend.ReadScriptInfo(serializedFilePath, pathId, cancellationToken);
    }

    public UnityScriptInfo? ReadBundleObjectScriptInfo(string bundlePath, string serializedFileName,
        long pathId, CancellationToken cancellationToken = default)
    {
        using var backend = new AssetsToolsBackend();
        return backend.ReadBundleScriptInfo(bundlePath, serializedFileName, pathId, cancellationToken);
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
                var type = UnityClassId.Map(obj.TypeId);
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
