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

/// <summary>Texture2D structural summary for previews (P1.3).</summary>
public sealed record UnityTextureSummary(
    int Width,
    int Height,
    int TextureFormat,
    string? FormatName,
    int MipCount,
    string? CapabilityNotes)
{
    public string Describe()
    {
        var name = FormatName ?? $"格式 {TextureFormat}（暂不支持预览/替换）";
        var text = $"{Width} × {Height} · {name} · mipmap {MipCount} 层";
        if (!string.IsNullOrWhiteSpace(CapabilityNotes)) text += $" ｜ {CapabilityNotes}";
        return text;
    }
}

public enum UnityFieldEditStatus { Ok, UnknownPath, NotEditable, ParseError, InvalidTarget }

/// <summary>How a PPtr dependency resolves against a SerializedFile's own
/// object table and external reference list.</summary>
public enum UnityDependencyResolution
{
    /// <summary>FileID=0 and PathID=0: a deliberately empty pointer.</summary>
    NullReference,
    /// <summary>The pointer resolves to an object inside the same SerializedFile.</summary>
    SameFile,
    /// <summary>The pointer references another file through the external table.</summary>
    ExternalFile,
    /// <summary>Dangling path ID, out-of-range file ID, or otherwise unresolvable.</summary>
    Missing
}

/// <summary>One resolved (or unresolvable) PPtr dependency of a serialized
/// object, with the in-file target or the external file it points at.</summary>
public sealed record UnityDependency(
    string FieldPath,
    long FileId,
    long PathId,
    string? TargetType,
    UnityDependencyResolution Resolution,
    string? ExternalPath = null,
    string? ExternalGuid = null,
    long? TargetTypeId = null,
    string? TargetTypeName = null)
{
    /// <summary>Compact human-readable resolution used in UI and reports.</summary>
    public string Describe() => Resolution switch
    {
        UnityDependencyResolution.NullReference => "空引用",
        UnityDependencyResolution.SameFile => $"同文件 Path {PathId}（{TargetTypeName ?? TargetType ?? "未知类型"}）",
        UnityDependencyResolution.ExternalFile => $"外部文件 {ExternalPath ?? "未知"} Path {PathId}",
        _ => FileId == 0 && PathId == 0 ? "空引用" : $"无法解析（File {FileId}, Path {PathId}）"
    };

    /// <summary>Stable identity used to compare dependencies across rewrites.
    /// The resolution comes first so regression checks can match by prefix.</summary>
    public string Identity() => $"{Resolution}|{FileId}|{PathId}|{ExternalPath ?? string.Empty}";
}

/// <summary>An object that points at a target object through one of its PPtr fields.</summary>
public sealed record UnityReferencer(long SourcePathId, string? SourceTypeName, string FieldPath, long FileId);

/// <summary>Before/after comparison of one dependency across a rewrite.</summary>
public sealed record UnityDependencyCheck(string SerializedFile, string SourcePathId, string FieldPath, string Before, string After)
{
    public bool IsRegression => Before.StartsWith("SameFile", StringComparison.Ordinal) && After.StartsWith("Missing", StringComparison.Ordinal)
        || Before.StartsWith("ExternalFile", StringComparison.Ordinal) && After.StartsWith("Missing", StringComparison.Ordinal);
}

/// <summary>Result of verifying that a rewrite kept every previously resolvable
/// dependency resolvable (same-file and external references alike).</summary>
public sealed record UnityReferenceVerifyReport(bool Ok, IReadOnlyList<UnityDependencyCheck> Changes)
{
    public static UnityReferenceVerifyReport Empty() => new(true, []);
}

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

    public IReadOnlyList<UnityDependency> ReadObjectDependencies(string serializedFilePath, long pathId,
        CancellationToken cancellationToken = default)
    {
        using var backend = new AssetsToolsBackend();
        return backend.ReadObjectDependencies(serializedFilePath, pathId, cancellationToken);
    }

    public IReadOnlyList<UnityDependency> ReadBundleObjectDependencies(string bundlePath, string serializedFileName,
        long pathId, CancellationToken cancellationToken = default)
    {
        using var backend = new AssetsToolsBackend();
        return backend.ReadBundleObjectDependencies(bundlePath, serializedFileName, pathId, cancellationToken);
    }

    public IReadOnlyList<UnityReferencer> FindReferencers(string serializedFilePath, long targetPathId,
        CancellationToken cancellationToken = default)
    {
        using var backend = new AssetsToolsBackend();
        return backend.FindReferencers(serializedFilePath, targetPathId, cancellationToken);
    }

    public IReadOnlyList<UnityReferencer> FindBundleReferencers(string bundlePath, string serializedFileName,
        long targetPathId, CancellationToken cancellationToken = default)
    {
        using var backend = new AssetsToolsBackend();
        return backend.FindBundleReferencers(bundlePath, serializedFileName, targetPathId, cancellationToken);
    }

    public UnityReferenceVerifyReport VerifySerializedReferences(string originalPath, string modifiedPath,
        CancellationToken cancellationToken = default)
    {
        using var backend = new AssetsToolsBackend();
        return backend.VerifySerializedReferences(originalPath, modifiedPath, cancellationToken);
    }

    public UnityReferenceVerifyReport VerifyBundleReferences(string originalBundle, string modifiedBundle,
        CancellationToken cancellationToken = default)
    {
        using var backend = new AssetsToolsBackend();
        return backend.VerifyBundleReferences(originalBundle, modifiedBundle, cancellationToken);
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

    /// <summary>Structural summary of a Texture2D: dimensions, format name,
    /// estimated mipmap level count and capability notes (P1.3).</summary>
    public UnityTextureSummary? ReadTextureSummary(string bundlePath, long pathId,
        CancellationToken cancellationToken = default)
    {
        using var backend = new AssetsToolsBackend();
        var texture = backend.ReadTexture(bundlePath, pathId, cancellationToken);
        if (texture is null) return null;
        UnityTexturePixelFormat? format = Enum.IsDefined(typeof(UnityTexturePixelFormat), texture.TextureFormat)
            ? (UnityTexturePixelFormat)texture.TextureFormat
            : null;
        var mipCount = 1;
        string? capability = null;
        if (format is { } known)
        {
            mipCount = UnityTextureMipmaps.EstimateMipCount(texture.Width, texture.Height, known, texture.PixelData.Length);
            capability = TextureFormatCatalog.Describe(known).Notes;
        }
        return new UnityTextureSummary(texture.Width, texture.Height, texture.TextureFormat,
            format?.ToString(), mipCount, capability);
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
