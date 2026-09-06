using AssetsTools.NET.Extra;
using AssetsTools.NET;
using System.Text;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Editing.Images;

namespace LimbusModEditor.Formats.Unity;

public sealed record UnityAssetDescriptor(string ContainerPath, long PathId, int TypeId, uint ByteSize, AssetType AssetType);
public sealed record UnitySerializedObject(long PathId, int TypeId, byte[] Data);
/// <summary>Raw serialized payload of one object inside a bundle, with the
/// SerializedFile's TYPE TABLE facts. <paramref name="TypeTableIndex"/> is the
/// per-object stored type index (UnityPy's obj.type_id) — the value real Carra2
/// keys carry — while <paramref name="TypeTableClassId"/> is the global class id
/// of the referenced type-table entry.</summary>
public sealed record UnityBundleSerializedObject(
    string ContainerPath, long PathId, int TypeTableIndex, int TypeTableClassId, int TypeTableCount, byte[] Data);
public sealed record UnityTextureObject(string ContainerPath, long PathId, int Width, int Height, int TextureFormat, byte[] PixelData);
public readonly record struct UnitySpriteRect(float X, float Y, float Width, float Height);
public readonly record struct UnitySpriteVector2(float X, float Y);
public readonly record struct UnitySpriteBorder(float Left, float Bottom, float Right, float Top);
public sealed record UnitySpriteObject(
    string ContainerPath,
    long PathId,
    UnitySpriteRect Rect,
    UnitySpriteVector2 Offset,
    UnitySpriteVector2 Pivot,
    UnitySpriteBorder Border,
    float PixelsToUnits,
    long? TexturePathId,
    long? AlphaTexturePathId);

/// <summary>
/// Thin adapter over AssetsTools.NET. UnityFS/SerializedFile parsing and writing
/// remain delegated to the upstream library; this class only maps results into
/// the editor's neutral asset model.
/// </summary>
public sealed class AssetsToolsBackend : IDisposable
{
    private readonly AssetsManager _manager = new() { UseQuickLookup = true, UseTemplateFieldCache = true };
    private readonly List<BundleFileInstance> _bundles = [];

    public IReadOnlyList<UnityFieldNode> ReadObjectFields(string serializedFilePath, long pathId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = _manager.LoadAssetsFile(serializedFilePath, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFilePath}");
        var info = file.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        var template = LoadTemplate(file, info);
        var fields = GetBaseField(file, info, template);
        return [BuildFieldNode(fields, template, fields.FieldName, fields.FieldName)];
    }

    public IReadOnlyList<UnityFieldNode> ReadBundleObjectFields(string bundlePath, string serializedFileName,
        long pathId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bundle = _manager.LoadBundleFile(bundlePath, unpackIfPacked: true)
            ?? throw new InvalidDataException($"无法读取 Unity Bundle: {bundlePath}");
        _bundles.Add(bundle);
        var index = bundle.file.GetFileIndex(serializedFileName);
        if (index < 0 || !bundle.file.IsAssetsFile(index)) throw new KeyNotFoundException($"Bundle 中不存在 SerializedFile: {serializedFileName}");
        var file = _manager.LoadAssetsFileFromBundle(bundle, serializedFileName, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFileName}");
        var info = file.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        var template = LoadTemplate(file, info);
        var fields = GetBaseField(file, info, template);
        return [BuildFieldNode(fields, template, fields.FieldName, fields.FieldName)];
    }

    /// <summary>Script provenance for a MonoBehaviour object: the serialized
    /// m_Script PPtr, class data when the MonoScript lives in the same
    /// SerializedFile, the referenced external file otherwise, and the reason
    /// the field tree is unavailable.</summary>
    public UnityScriptInfo ReadScriptInfo(string serializedFilePath, long pathId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = _manager.LoadAssetsFile(serializedFilePath, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFilePath}");
        var info = file.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        return ReadScriptInfoCore(file, info);
    }

    /// <summary>Bundle variant of <see cref="ReadScriptInfo"/>.</summary>
    public UnityScriptInfo ReadBundleScriptInfo(string bundlePath, string serializedFileName, long pathId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bundle = _manager.LoadBundleFile(bundlePath, unpackIfPacked: true)
            ?? throw new InvalidDataException($"无法读取 Unity Bundle: {bundlePath}");
        _bundles.Add(bundle);
        var index = bundle.file.GetFileIndex(serializedFileName);
        if (index < 0 || !bundle.file.IsAssetsFile(index)) throw new KeyNotFoundException($"Bundle 中不存在 SerializedFile: {serializedFileName}");
        var file = _manager.LoadAssetsFileFromBundle(bundle, serializedFileName, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFileName}");
        var info = file.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        return ReadScriptInfoCore(file, info);
    }

    /// <summary>Validates field edits against the real serialized types of a
    /// standalone SerializedFile without writing anything.</summary>
    public IReadOnlyList<UnityFieldEditDiagnostic> ValidateObjectFieldEdits(string serializedFilePath, long pathId,
        IReadOnlyDictionary<string, string> edits, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedFilePath);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0) throw new ArgumentException("至少需要一个字段修改。", nameof(edits));
        var file = _manager.LoadAssetsFile(serializedFilePath, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFilePath}");
        var info = file.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        var template = LoadTemplate(file, info);
        var fields = GetBaseField(file, info, template);
        return ValidateFieldEdits(file, fields, template, edits);
    }

    /// <summary>Bundle variant of <see cref="ValidateObjectFieldEdits"/>.</summary>
    public IReadOnlyList<UnityFieldEditDiagnostic> ValidateBundleObjectFieldEdits(string bundlePath,
        string serializedFileName, long pathId, IReadOnlyDictionary<string, string> edits,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedFileName);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0) throw new ArgumentException("至少需要一个字段修改。", nameof(edits));
        var file = LoadBundleAssetsFile(bundlePath, serializedFileName);
        var info = file.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        var template = LoadTemplate(file, info);
        var fields = GetBaseField(file, info, template);
        return ValidateFieldEdits(file, fields, template, edits);
    }

    public IReadOnlyList<UnityObjectReference> ReadObjectReferences(string serializedFilePath, long pathId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = _manager.LoadAssetsFile(serializedFilePath, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFilePath}");
        var info = file.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        var fields = _manager.GetBaseField(file, info, AssetReadFlags.None);
        var result = new List<UnityObjectReference>();
        CollectReferences(fields, fields.FieldName, result);
        return result;
    }

    public IReadOnlyList<UnityObjectReference> ReadBundleObjectReferences(string bundlePath, string serializedFileName,
        long pathId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bundle = _manager.LoadBundleFile(bundlePath, unpackIfPacked: true)
            ?? throw new InvalidDataException($"无法读取 Unity Bundle: {bundlePath}");
        _bundles.Add(bundle);
        var index = bundle.file.GetFileIndex(serializedFileName);
        if (index < 0 || !bundle.file.IsAssetsFile(index)) throw new KeyNotFoundException($"Bundle 中不存在 SerializedFile: {serializedFileName}");
        var file = _manager.LoadAssetsFileFromBundle(bundle, serializedFileName, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFileName}");
        var info = file.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        var fields = _manager.GetBaseField(file, info, AssetReadFlags.None);
        var result = new List<UnityObjectReference>();
        CollectReferences(fields, fields.FieldName, result);
        return result;
    }

    /// <summary>Resolves every PPtr field of one object against the file's own
    /// object table and external list (P1.2 dependency view).</summary>
    public IReadOnlyList<UnityDependency> ReadObjectDependencies(string serializedFilePath, long pathId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = _manager.LoadAssetsFile(serializedFilePath, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFilePath}");
        var info = file.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        var fields = _manager.GetBaseField(file, info, AssetReadFlags.None);
        var result = new List<UnityDependency>();
        CollectDependencyInfos(fields, fields.FieldName, file, result);
        return result;
    }

    /// <summary>Bundle variant of <see cref="ReadObjectDependencies"/>.</summary>
    public IReadOnlyList<UnityDependency> ReadBundleObjectDependencies(string bundlePath, string serializedFileName,
        long pathId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = LoadBundleAssetsFile(bundlePath, serializedFileName);
        var info = file.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        var fields = _manager.GetBaseField(file, info, AssetReadFlags.None);
        var result = new List<UnityDependency>();
        CollectDependencyInfos(fields, fields.FieldName, file, result);
        return result;
    }

    /// <summary>Finds every same-file object that points at the target path ID,
    /// with the field that carries the pointer (P1.2 referencer check).</summary>
    public IReadOnlyList<UnityReferencer> FindReferencers(string serializedFilePath, long targetPathId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = _manager.LoadAssetsFile(serializedFilePath, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFilePath}");
        return FindReferencersCore(file, targetPathId, fileName: null, crossFileTargetPathId: 0, cancellationToken);
    }

    /// <summary>Finds every object in the bundle (including other SerializedFiles
    /// inside it) that points at the target object, matching in-file references
    /// and cross-file references that resolve to the target file by name.</summary>
    public IReadOnlyList<UnityReferencer> FindBundleReferencers(string bundlePath, string serializedFileName,
        long targetPathId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bundle = _manager.LoadBundleFile(bundlePath, unpackIfPacked: true)
            ?? throw new InvalidDataException($"无法读取 Unity Bundle: {bundlePath}");
        _bundles.Add(bundle);
        var result = new List<UnityReferencer>();
        foreach (var fileName in bundle.file.GetAllFileNames())
        {
            if (!bundle.file.IsAssetsFile(bundle.file.GetFileIndex(fileName))) continue;
            var file = _manager.LoadAssetsFileFromBundle(bundle, fileName, loadDeps: false);
            if (file is null) continue;
            var isTargetFile = fileName.Equals(serializedFileName, StringComparison.OrdinalIgnoreCase);
            result.AddRange(FindReferencersCore(file,
                sameFileTargetPathId: targetPathId,
                fileName: fileName,
                crossFileTargetPathId: targetPathId,
                cancellationToken,
                crossFileTargetName: isTargetFile ? null : serializedFileName));
        }
        return result;
    }

    private IReadOnlyList<UnityReferencer> FindReferencersCore(AssetsFileInstance file, long sameFileTargetPathId,
        string? fileName, long crossFileTargetPathId, CancellationToken cancellationToken, string? crossFileTargetName = null)
    {
        var result = new List<UnityReferencer>();
        foreach (var info in file.file.AssetInfos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssetTypeValueField fields;
            try { fields = _manager.GetBaseField(file, info, AssetReadFlags.None); }
            catch (Exception) { continue; }
            var sourceType = MapType(info.GetTypeId(file.file)).ToString();
            CollectReferencerNodes(fields, fields.FieldName, file, info.PathId, sourceType,
                sameFileTargetPathId, crossFileTargetPathId, crossFileTargetName, fileName, result);
        }
        return result;
    }

    /// <summary>Verifies that rewriting a standalone SerializedFile kept every
    /// previously resolvable dependency resolvable (P1.2 repack check). Both
    /// snapshots are read from disk through a separate backend so in-place
    /// edits on this backend's cached instances cannot pollute the comparison.</summary>
    public UnityReferenceVerifyReport VerifySerializedReferences(string originalPath, string modifiedPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new AssetsToolsBackend();
        var before = reader.CollectAllObjectDependencies(originalPath, cancellationToken);
        var after = reader.CollectAllObjectDependencies(modifiedPath, cancellationToken);
        var changes = DiffDependencies(Path.GetFileName(originalPath), before, after);
        return new UnityReferenceVerifyReport(changes.All(c => !c.IsRegression), changes);
    }

    /// <summary>Verifies that repacking a bundle kept every previously
    /// resolvable dependency resolvable, across all contained SerializedFiles.</summary>
    public UnityReferenceVerifyReport VerifyBundleReferences(string originalBundle, string modifiedBundle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new AssetsToolsBackend();
        var before = reader.CollectAllBundleObjectDependencies(originalBundle, cancellationToken);
        var after = reader.CollectAllBundleObjectDependencies(modifiedBundle, cancellationToken);
        var changes = new List<UnityDependencyCheck>();
        foreach (var (fileName, beforeObjects) in before)
        {
            if (!after.TryGetValue(fileName, out var afterObjects))
            {
                foreach (var (pathId, deps) in beforeObjects)
                    foreach (var dep in deps)
                        changes.Add(new UnityDependencyCheck(fileName, pathId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            dep.FieldPath, dep.Identity(), "Missing|file-removed"));
                continue;
            }
            changes.AddRange(DiffDependencies(fileName, beforeObjects, afterObjects));
        }
        return new UnityReferenceVerifyReport(changes.All(c => !c.IsRegression), changes);
    }

    private Dictionary<long, List<UnityDependency>> CollectAllObjectDependencies(string path, CancellationToken cancellationToken)
    {
        var file = _manager.LoadAssetsFile(path, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {path}");
        return CollectAllObjectDependenciesCore(file, cancellationToken);
    }

    private Dictionary<string, Dictionary<long, List<UnityDependency>>> CollectAllBundleObjectDependencies(string bundlePath, CancellationToken cancellationToken)
    {
        var bundle = _manager.LoadBundleFile(bundlePath, unpackIfPacked: true)
            ?? throw new InvalidDataException($"无法读取 Unity Bundle: {bundlePath}");
        _bundles.Add(bundle);
        var result = new Dictionary<string, Dictionary<long, List<UnityDependency>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in bundle.file.GetAllFileNames())
        {
            if (!bundle.file.IsAssetsFile(bundle.file.GetFileIndex(fileName))) continue;
            var file = _manager.LoadAssetsFileFromBundle(bundle, fileName, loadDeps: false);
            if (file is null) continue;
            result[fileName] = CollectAllObjectDependenciesCore(file, cancellationToken);
        }
        return result;
    }

    private Dictionary<long, List<UnityDependency>> CollectAllObjectDependenciesCore(AssetsFileInstance file, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, List<UnityDependency>>();
        foreach (var info in file.file.AssetInfos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssetTypeValueField fields;
            try { fields = _manager.GetBaseField(file, info, AssetReadFlags.None); }
            catch (Exception) { continue; }
            var deps = new List<UnityDependency>();
            CollectDependencyInfos(fields, fields.FieldName, file, deps);
            result[info.PathId] = deps;
        }
        return result;
    }

    private static List<UnityDependencyCheck> DiffDependencies(string fileName,
        Dictionary<long, List<UnityDependency>> before, Dictionary<long, List<UnityDependency>> after)
    {
        var changes = new List<UnityDependencyCheck>();
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var (pathId, deps) in before)
        {
            if (!after.TryGetValue(pathId, out var afterDeps))
            {
                foreach (var dep in deps)
                    changes.Add(new UnityDependencyCheck(fileName, pathId.ToString(culture), dep.FieldPath,
                        dep.Identity(), "Missing|object-removed"));
                continue;
            }
            // ToLookup, not ToDictionary: field paths can repeat on exotic
            // templates, and a duplicate key must not abort the whole verify.
            var afterLookup = afterDeps.ToLookup(d => d.FieldPath, d => d, StringComparer.Ordinal);
            foreach (var dep in deps)
            {
                var candidates = afterLookup[dep.FieldPath].ToList();
                if (candidates.Count == 0)
                {
                    changes.Add(new UnityDependencyCheck(fileName, pathId.ToString(culture), dep.FieldPath,
                        dep.Identity(), "Missing|field-removed"));
                    continue;
                }
                if (!candidates.Any(m => m.Identity().Equals(dep.Identity(), StringComparison.Ordinal)))
                    changes.Add(new UnityDependencyCheck(fileName, pathId.ToString(culture), dep.FieldPath,
                        dep.Identity(), candidates[0].Identity()));
            }
        }
        return changes;
    }

    private void CollectDependencyInfos(AssetTypeValueField field, string path, AssetsFileInstance file, List<UnityDependency> result)
    {
        var fileIdField = field.Children.FirstOrDefault(x => x.FieldName.Equals("m_FileID", StringComparison.OrdinalIgnoreCase) || x.FieldName.Equals("fileID", StringComparison.OrdinalIgnoreCase));
        var pathIdField = field.Children.FirstOrDefault(x => x.FieldName.Equals("m_PathID", StringComparison.OrdinalIgnoreCase) || x.FieldName.Equals("pathID", StringComparison.OrdinalIgnoreCase));
        if (fileIdField is not null && pathIdField is not null
            && TryReadLong(fileIdField, out var fileIdValue) && TryReadLong(pathIdField, out var pathIdValue))
            result.Add(ResolveDependency(file, path, fileIdValue, pathIdValue, field.TypeName));
        var isArray = field.TemplateField?.IsArray == true || field.Value?.ValueType == AssetValueType.Array;
        for (var i = 0; i < field.Children.Count; i++)
        {
            var child = field.Children[i];
            string childName;
            string childPath;
            if (isArray) { childName = $"[{i}]"; childPath = $"{path}[{i}]"; }
            else
            {
                childName = string.IsNullOrWhiteSpace(child.FieldName) ? $"[{i}]" : child.FieldName;
                childPath = childName.StartsWith("[", StringComparison.Ordinal) ? $"{path}{childName}" : $"{path}.{childName}";
            }
            CollectDependencyInfos(child, childPath, file, result);
        }
    }

    private void CollectReferencerNodes(AssetTypeValueField field, string path, AssetsFileInstance file,
        long sourcePathId, string sourceType, long sameFileTargetPathId, long crossFileTargetPathId,
        string? crossFileTargetName, string? originatingFile, List<UnityReferencer> result)
    {
        var fileIdField = field.Children.FirstOrDefault(x => x.FieldName.Equals("m_FileID", StringComparison.OrdinalIgnoreCase) || x.FieldName.Equals("fileID", StringComparison.OrdinalIgnoreCase));
        var pathIdField = field.Children.FirstOrDefault(x => x.FieldName.Equals("m_PathID", StringComparison.OrdinalIgnoreCase) || x.FieldName.Equals("pathID", StringComparison.OrdinalIgnoreCase));
        if (fileIdField is not null && pathIdField is not null
            && TryReadLong(fileIdField, out var fileIdValue) && TryReadLong(pathIdField, out var pathIdValue))
        {
            var isSameFileRef = fileIdValue == 0 && pathIdValue == sameFileTargetPathId;
            var isCrossFileRef = crossFileTargetName is not null && fileIdValue > 0 && pathIdValue == crossFileTargetPathId
                && ExternalMatchesFileName(file, fileIdValue, crossFileTargetName);
            if (isSameFileRef || isCrossFileRef)
                result.Add(new UnityReferencer(sourcePathId, sourceType, path, fileIdValue, originatingFile));
        }
        var isArray = field.TemplateField?.IsArray == true || field.Value?.ValueType == AssetValueType.Array;
        for (var i = 0; i < field.Children.Count; i++)
        {
            var child = field.Children[i];
            string childName;
            string childPath;
            if (isArray) { childName = $"[{i}]"; childPath = $"{path}[{i}]"; }
            else
            {
                childName = string.IsNullOrWhiteSpace(child.FieldName) ? $"[{i}]" : child.FieldName;
                childPath = childName.StartsWith("[", StringComparison.Ordinal) ? $"{path}{childName}" : $"{path}.{childName}";
            }
            CollectReferencerNodes(child, childPath, file, sourcePathId, sourceType,
                sameFileTargetPathId, crossFileTargetPathId, crossFileTargetName, originatingFile, result);
        }
    }

    private static bool ExternalMatchesFileName(AssetsFileInstance file, long fileId, string fileName)
    {
        var externals = file.file.Metadata.Externals;
        var index = (int)fileId - 1;
        if (index < 0 || index >= externals.Count) return false;
        var externalPath = externals[index].PathName ?? string.Empty;
        if (externalPath.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return true;
        // bundle-internal references are often written as "archive:/CAB-xxx/CAB-xxx"
        return externalPath.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase);
    }

    private static UnityDependency ResolveDependency(AssetsFileInstance file, string fieldPath,
        long fileId, long pathId, string? targetType)
    {
        if (fileId == 0 && pathId == 0)
            return new UnityDependency(fieldPath, 0, 0, targetType, UnityDependencyResolution.NullReference);
        if (fileId == 0)
        {
            var info = file.file.GetAssetInfo(pathId);
            if (info is null)
                return new UnityDependency(fieldPath, 0, pathId, targetType, UnityDependencyResolution.Missing);
            var typeId = info.GetTypeId(file.file);
            return new UnityDependency(fieldPath, 0, pathId, targetType, UnityDependencyResolution.SameFile,
                TargetTypeId: typeId, TargetTypeName: MapType(typeId).ToString());
        }
        var externals = file.file.Metadata.Externals;
        var externalIndex = (int)fileId - 1;
        if (externalIndex < 0 || externalIndex >= externals.Count)
            return new UnityDependency(fieldPath, fileId, pathId, targetType, UnityDependencyResolution.Missing);
        return new UnityDependency(fieldPath, fileId, pathId, targetType, UnityDependencyResolution.ExternalFile,
            ExternalPath: externals[externalIndex].PathName,
            ExternalGuid: externals[externalIndex].Guid.ToString());
    }

    public IReadOnlyList<UnityAssetDescriptor> InspectBundle(string path)
    {
        var bundle = _manager.LoadBundleFile(path, unpackIfPacked: true) ?? throw new InvalidDataException($"无法读取 Unity Bundle: {path}");
        _bundles.Add(bundle);
        var assets = new List<UnityAssetDescriptor>();
        foreach (var fileName in bundle.file.GetAllFileNames())
        {
            if (!bundle.file.IsAssetsFile(bundle.file.GetFileIndex(fileName))) continue;
            var file = _manager.LoadAssetsFileFromBundle(bundle, fileName, loadDeps: false);
            if (file is null) continue;
            foreach (var info in file.file.AssetInfos)
                assets.Add(new UnityAssetDescriptor(fileName, info.PathId, info.GetTypeId(file.file), info.ByteSize, MapType(info.GetTypeId(file.file))));
        }
        return assets;
    }

    /// <summary>Read-only diagnostic probe (used by real-sample verification):
    /// reports, for each SerializedFile inside a bundle, whether embedded type
    /// trees are present, the recorded Unity version string and the embedded
    /// type count. Makes no changes to the file.</summary>
    public IReadOnlyList<(bool TypeTreeEnabled, string UnityVersion, int TypeCount)> SurveyBundle(string path)
    {
        var bundle = _manager.LoadBundleFile(path, unpackIfPacked: true) ?? throw new InvalidDataException($"无法读取 Unity Bundle: {path}");
        _bundles.Add(bundle);
        var results = new List<(bool, string, int)>();
        foreach (var fileName in bundle.file.GetAllFileNames())
        {
            if (!bundle.file.IsAssetsFile(bundle.file.GetFileIndex(fileName))) continue;
            var file = _manager.LoadAssetsFileFromBundle(bundle, fileName, loadDeps: false);
            if (file is null) continue;
            results.Add((file.file.Metadata.TypeTreeEnabled, file.file.Metadata.UnityVersion ?? string.Empty,
                file.file.Metadata.TypeTreeTypes.Count));
        }
        return results;
    }

    /// <summary>Enumerates raw object payloads in a standalone SerializedFile
    /// (for example a Lunartique __data file).</summary>
    public IReadOnlyList<UnitySerializedObject> ReadSerializedObjects(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = _manager.LoadAssetsFile(path, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {path}");
        var result = new List<UnitySerializedObject>();
        foreach (var info in file.file.AssetInfos)
        {
            var offset = info.GetAbsoluteByteOffset(file.file);
            if (offset < 0 || info.ByteSize > int.MaxValue || offset + info.ByteSize > file.AssetsStream.Length)
                throw new InvalidDataException($"SerializedFile 对象范围无效: {info.PathId}");
            file.AssetsStream.Position = offset;
            var data = new byte[info.ByteSize];
            var read = 0;
            while (read < data.Length)
            {
                var count = file.AssetsStream.Read(data, read, data.Length - read);
                if (count == 0) throw new EndOfStreamException($"SerializedFile 对象数据不完整: {info.PathId}");
                read += count;
            }
            result.Add(new UnitySerializedObject(info.PathId, info.GetTypeId(file.file), data));
        }
        return result;
    }

    /// <summary>打开 bundle 的「全部块解压后拼接」数据流（vanilla 基线 CRC 对比
    /// 用，口径与 LCTA staticmod.bundle_decompressed_crc 一致）。流由本后端持有
    /// （随 Dispose 释放），不是可解压的 UnityFS 时返回 false。</summary>
    public bool TryLoadBundleForCrc(string bundlePath, out Stream decompressed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        try
        {
            var bundle = _manager.LoadBundleFile(bundlePath, unpackIfPacked: true);
            if (bundle is null) { decompressed = Stream.Null; return false; }
            _bundles.Add(bundle);
            decompressed = bundle.DataStream;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or EndOfStreamException)
        {
            decompressed = Stream.Null;
            return false;
        }
    }

    /// <summary>Reads one object's raw serialized bytes from a SerializedFile
    /// inside a bundle (the payload shape real Carra2 mods carry per entry),
    /// together with the type-table index the real loader checks against.</summary>
    public UnityBundleSerializedObject ReadBundleSerializedObject(string bundlePath, string serializedFileName, long pathId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedFileName);
        var bundle = _manager.LoadBundleFile(bundlePath, unpackIfPacked: true)
            ?? throw new InvalidDataException($"无法读取 Unity Bundle: {bundlePath}");
        _bundles.Add(bundle);
        var index = bundle.file.GetFileIndex(serializedFileName);
        if (index < 0 || !bundle.file.IsAssetsFile(index)) throw new KeyNotFoundException($"Bundle 中不存在 SerializedFile: {serializedFileName}");
        var instance = _manager.LoadAssetsFileFromBundle(bundle, serializedFileName, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFileName}");
        var info = instance.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        var types = instance.file.Metadata.TypeTreeTypes;
        var typeIndex = info.TypeIdOrIndex;
        if (typeIndex < 0 || typeIndex >= types.Count)
            throw new InvalidDataException($"类型表索引越界: {typeIndex}（类型表共 {types.Count} 项，暂不支持无类型表的文件）");
        var offset = info.GetAbsoluteByteOffset(instance.file);
        if (offset < 0 || info.ByteSize > int.MaxValue || offset + info.ByteSize > instance.AssetsStream.Length)
            throw new InvalidDataException($"SerializedFile 对象范围无效: {info.PathId}");
        instance.AssetsStream.Position = offset;
        var data = new byte[info.ByteSize];
        var read = 0;
        while (read < data.Length)
        {
            var count = instance.AssetsStream.Read(data, read, data.Length - read);
            if (count == 0) throw new EndOfStreamException($"SerializedFile 对象数据不完整: {info.PathId}");
            read += count;
        }
        return new(serializedFileName, info.PathId, typeIndex, types[typeIndex].TypeId, types.Count, data);
    }

    public UnityTextureObject? ReadTexture(string path, long pathId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bundle = _manager.LoadBundleFile(path, unpackIfPacked: true) ?? throw new InvalidDataException($"无法读取 Unity Bundle: {path}");
        _bundles.Add(bundle);
        foreach (var fileName in bundle.file.GetAllFileNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = bundle.file.GetFileIndex(fileName);
            if (!bundle.file.IsAssetsFile(index)) continue;
            var file = _manager.LoadAssetsFileFromBundle(bundle, fileName, loadDeps: false);
            if (file is null) continue;
            var info = file.file.GetAssetInfo(pathId);
            if (info is null || info.GetTypeId(file.file) != 28) continue;
            var fields = _manager.GetBaseField(file, info, AssetReadFlags.None);
            var width = ReadInt(fields, "m_Width", "width");
            var height = ReadInt(fields, "m_Height", "height");
            var format = ReadInt(fields, "m_TextureFormat", "textureFormat");
            var pixels = ReadTexturePixelData(bundle, fields, pathId);
            if (width <= 0 || height <= 0 || format < 0 || pixels is null || pixels.Length == 0) return null;
            return new UnityTextureObject(fileName, pathId, width, height, format, pixels);
        }
        return null;
    }

    /// <summary>Reads the pixel payload of a Texture2D. Modern Unity objects
    /// keep their bytes in a .resS resource stream referenced by m_StreamData
    /// ("archive:/CAB-xxx/CAB-xxx.resS" = a file inside this bundle; anything
    /// else = a file next to the bundle); older objects store an inline byte
    /// array. Returns null only when neither source can be located.</summary>
    private byte[]? ReadTexturePixelData(BundleFileInstance bundle, AssetTypeValueField fields, long pathId)
    {
        var direct = FindField(fields, "m_TextureData", "m_ImageData", "image data", "data")?.AsByteArray;
        if (direct is { Length: > 0 }) return direct;

        var streamData = FindField(fields, "m_StreamData");
        if (streamData is null) return null;
        var resPath = FindField(streamData, "path")?.Value?.AsString;
        if (string.IsNullOrEmpty(resPath)) return null;
        var offset = FindField(streamData, "offset")?.Value?.AsLong ?? 0;
        var size = FindField(streamData, "size")?.Value?.AsLong ?? 0;
        if (offset < 0 || size <= 0) return null;

        // Unity's virtual archive:/ paths point at container files inside the
        // bundle; the block directory gives each file's byte range in the
        // (already unpacked) data stream.
        if (resPath.StartsWith("archive:/", StringComparison.OrdinalIgnoreCase))
        {
            var inner = resPath["archive:/".Length..];
            foreach (var name in new[] { inner, Path.GetFileName(inner) })
            {
                var resIndex = bundle.file.GetFileIndex(name);
                if (resIndex < 0) continue;
                bundle.file.GetFileRange(resIndex, out var rangeOffset, out var rangeSize);
                if (offset + size > rangeSize)
                    throw new InvalidDataException($"纹理流数据越界（Path {pathId}）: {resPath} 范围 {offset}+{size} 超过块大小 {rangeSize}");
                var data = new byte[size];
                lock (bundle.DataStream)
                {
                    bundle.DataStream.Position = rangeOffset + offset;
                    var read = 0;
                    while (read < data.Length)
                    {
                        var count = bundle.DataStream.Read(data, read, data.Length - read);
                        if (count == 0) throw new EndOfStreamException($"纹理流数据不完整（Path {pathId}）: {resPath}");
                        read += count;
                    }
                }
                return data;
            }
            return null;
        }

        var external = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(bundle.path))!, resPath);
        if (!File.Exists(external)) return null;
        using var source = File.OpenRead(external);
        if (offset + size > source.Length)
            throw new InvalidDataException($"纹理流数据越界（Path {pathId}）: {resPath} 范围 {offset}+{size} 超过文件大小 {source.Length}");
        source.Position = offset;
        var externalData = new byte[size];
        var total = 0;
        while (total < externalData.Length)
        {
            var count = source.Read(externalData, total, externalData.Length - total);
            if (count == 0) throw new EndOfStreamException($"纹理流数据不完整（Path {pathId}）: {resPath}");
            total += count;
        }
        return externalData;
    }

    /// <summary>Neutralizes a Texture2D's m_StreamData after the pixel bytes
    /// have been written inline, so the game reads the new data instead of the
    /// stale .resS stream. A no-op when the type tree has no such field.</summary>
    private static void ClearStreamData(AssetTypeValueField fields)
    {
        var streamData = FindField(fields, "m_StreamData");
        if (streamData is null) return;
        var path = FindField(streamData, "path");
        var offset = FindField(streamData, "offset");
        var size = FindField(streamData, "size");
        if (path?.Value is not null) path.Value.AsString = string.Empty;
        if (offset?.Value is not null) offset.Value.AsLong = 0;
        if (size?.Value is not null) size.Value.AsLong = 0;
    }

    /// <summary>Reads the editable metadata of a Sprite object. Pixel data remains
    /// owned by the referenced Texture2D; this method intentionally does not
    /// flatten SpriteAtlas data or rewrite atlas meshes.</summary>
    public UnitySpriteObject? ReadSprite(string path, long pathId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bundle = _manager.LoadBundleFile(path, unpackIfPacked: true)
            ?? throw new InvalidDataException($"无法读取 Unity Bundle: {path}");
        _bundles.Add(bundle);
        foreach (var fileName in bundle.file.GetAllFileNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = bundle.file.GetFileIndex(fileName);
            if (!bundle.file.IsAssetsFile(index)) continue;
            var file = _manager.LoadAssetsFileFromBundle(bundle, fileName, loadDeps: false);
            if (file is null) continue;
            var info = file.file.GetAssetInfo(pathId);
            if (info is null || info.GetTypeId(file.file) != UnityClassId.Sprite) continue;
            var fields = _manager.GetBaseField(file, info, AssetReadFlags.None);
            return ReadSpriteFields(fileName, pathId, fields);
        }
        return null;
    }

    /// <summary>Updates Sprite rect, pivot, border and pixels-per-unit while
    /// preserving all other serialized fields.</summary>
    public void ReplaceSpriteMetadata(string serializedFilePath, long pathId,
        UnitySpriteRect rect, UnitySpriteVector2 pivot, UnitySpriteBorder border,
        float pixelsToUnits, string outputPath)
    {
        var file = _manager.LoadAssetsFile(serializedFilePath, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFilePath}");
        var info = file.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        if (info.GetTypeId(file.file) != UnityClassId.Sprite) throw new InvalidOperationException("目标对象不是 Sprite。");
        var fields = _manager.GetBaseField(file, info, AssetReadFlags.None);
        SetSpriteFields(fields, rect, pivot, border, pixelsToUnits);
        info.SetNewData(fields);
        WriteSerializedFile(file, outputPath);
    }

    /// <summary>Bundle variant of <see cref="ReplaceSpriteMetadata"/>.</summary>
    public void ReplaceBundleSpriteMetadata(string bundlePath, string serializedFileName, long pathId,
        UnitySpriteRect rect, UnitySpriteVector2 pivot, UnitySpriteBorder border,
        float pixelsToUnits, string outputPath)
    {
        var bundle = _manager.LoadBundleFile(bundlePath, unpackIfPacked: true)
            ?? throw new InvalidDataException($"无法读取 Unity Bundle: {bundlePath}");
        var index = bundle.file.GetFileIndex(serializedFileName);
        if (index < 0 || !bundle.file.IsAssetsFile(index))
            throw new KeyNotFoundException($"Bundle 中不存在 SerializedFile: {serializedFileName}");
        var instance = _manager.LoadAssetsFileFromBundle(bundle, serializedFileName, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFileName}");
        var info = instance.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        if (info.GetTypeId(instance.file) != UnityClassId.Sprite) throw new InvalidOperationException("目标对象不是 Sprite。");
        var fields = _manager.GetBaseField(instance, info, AssetReadFlags.None);
        SetSpriteFields(fields, rect, pivot, border, pixelsToUnits);
        info.SetNewData(fields);
        var directory = bundle.file.BlockAndDirInfo.DirectoryInfos.FirstOrDefault(x =>
            string.Equals(x.Name, serializedFileName, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Bundle 目录中不存在文件: {serializedFileName}");
        directory.SetNewData(instance.file);
        WritePackedBundle(bundle, outputPath);
    }

    /// <summary>Rewrites a standalone SerializedFile using AssetsTools.NET's
    /// replacer support. Bundle repacking remains a separate operation because
    /// the enclosing bundle compression must be preserved.</summary>
    public void ReplaceSerializedAsset(string serializedFilePath, long pathId, byte[] serializedData, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(serializedData);
        var file = _manager.LoadAssetsFile(serializedFilePath, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFilePath}");
        var info = file.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 PathId: {pathId}");
        info.SetNewData(serializedData);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var temporary = outputPath + ".tmp";
        try
        {
            using (var writer = new AssetsFileWriter(temporary))
                file.file.Write(writer, 0);
            File.Move(temporary, outputPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void ReplaceSerializedAssets(string serializedFilePath, IReadOnlyDictionary<long, byte[]> replacements, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedFilePath);
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0) throw new ArgumentException("至少需要一个对象替换。", nameof(replacements));
        var file = _manager.LoadAssetsFile(serializedFilePath, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFilePath}");
        foreach (var replacement in replacements)
        {
            var info = file.file.GetAssetInfo(replacement.Key)
                ?? throw new KeyNotFoundException($"SerializedFile 中不存在 PathId: {replacement.Key}");
            info.SetNewData(replacement.Value ?? throw new ArgumentNullException(nameof(replacements), "替换数据不能为 null。"));
        }
        WriteSerializedFile(file, outputPath);
    }

    public void ReplaceObjectFields(string serializedFilePath, long pathId,
        IReadOnlyDictionary<string, string> edits, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedFilePath);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0) throw new ArgumentException("至少需要一个字段修改。", nameof(edits));
        var file = _manager.LoadAssetsFile(serializedFilePath, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFilePath}");
        var info = file.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        var fields = _manager.GetBaseField(file, info, AssetReadFlags.None);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        ApplyFieldEdits(fields, fields.FieldName, edits, matched);
        ThrowForUnknownFieldEdits(edits, matched);
        info.SetNewData(fields);
        WriteSerializedFile(file, outputPath);
    }

    public void ReplaceBundleObjectFields(string bundlePath, string serializedFileName, long pathId,
        IReadOnlyDictionary<string, string> edits, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedFileName);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0) throw new ArgumentException("至少需要一个字段修改。", nameof(edits));
        var bundle = _manager.LoadBundleFile(bundlePath, unpackIfPacked: true)
            ?? throw new InvalidDataException($"无法读取 Unity Bundle: {bundlePath}");
        _bundles.Add(bundle);
        var index = bundle.file.GetFileIndex(serializedFileName);
        if (index < 0 || !bundle.file.IsAssetsFile(index)) throw new KeyNotFoundException($"Bundle 中不存在 SerializedFile: {serializedFileName}");
        var file = _manager.LoadAssetsFileFromBundle(bundle, serializedFileName, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFileName}");
        var info = file.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        var fields = _manager.GetBaseField(file, info, AssetReadFlags.None);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        ApplyFieldEdits(fields, fields.FieldName, edits, matched);
        ThrowForUnknownFieldEdits(edits, matched);
        info.SetNewData(fields);
        var directory = bundle.file.BlockAndDirInfo.DirectoryInfos.FirstOrDefault(x => string.Equals(x.Name, serializedFileName, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Bundle 目录中不存在文件: {serializedFileName}");
        directory.SetNewData(file.file);
        WritePackedBundle(bundle, outputPath);
    }

    public void ReplaceTextureFromPng(string serializedFilePath, long pathId, ReadOnlySpan<byte> pngData, string outputPath)
    {
        var file = _manager.LoadAssetsFile(serializedFilePath, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFilePath}");
        var info = file.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 PathId: {pathId}");
        if (info.GetTypeId(file.file) != 28) throw new InvalidOperationException("目标对象不是 Texture2D。");
        var fields = _manager.GetBaseField(file, info, AssetReadFlags.None);
        var textureFormat = ReadInt(fields, "m_TextureFormat", "textureFormat");
        var pixelFormat = textureFormat switch
        {
            3 => UnityTexturePixelFormat.Rgb24,
            4 or 5 => UnityTexturePixelFormat.Rgba32,
            10 => UnityTexturePixelFormat.Dxt1,
            12 => UnityTexturePixelFormat.Dxt5,
            14 => UnityTexturePixelFormat.Bgra32,
            _ => throw new NotSupportedException($"暂不支持写回 Unity TextureFormat {textureFormat}。")
        };
        var texture = new UnityTextureCodec().FromPng(pngData, pixelFormat);
        SetInt(fields, texture.Width, "m_Width", "width");
        SetInt(fields, texture.Height, "m_Height", "height");
        var dataField = FindField(fields, "m_TextureData", "m_ImageData", "image data", "data")
            ?? throw new InvalidDataException("Texture2D 中未找到像素数据字段。");
        dataField.AsByteArray = texture.PixelData;
        // pixel bytes are now inline: an m_StreamData still pointing at the
        // original .resS stream would make the game read the OLD image
        ClearStreamData(fields);
        info.SetNewData(fields);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var temporary = outputPath + ".tmp";
        try
        {
            using (var writer = new AssetsFileWriter(temporary)) file.file.Write(writer, 0);
            File.Move(temporary, outputPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void ReplaceBundleFile(string bundlePath, string entryName, byte[] replacementData, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
        ArgumentNullException.ThrowIfNull(replacementData);
        var bundle = _manager.LoadBundleFile(bundlePath, unpackIfPacked: true)
            ?? throw new InvalidDataException($"无法读取 Unity Bundle: {bundlePath}");
        if (!bundle.file.GetAllFileNames().Contains(entryName, StringComparer.Ordinal))
            throw new KeyNotFoundException($"Bundle 中不存在文件: {entryName}");
        var directory = bundle.file.BlockAndDirInfo.DirectoryInfos.FirstOrDefault(x => string.Equals(x.Name, entryName, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Bundle 目录中不存在文件: {entryName}");
        directory.SetNewData(replacementData);
        WritePackedBundle(bundle, outputPath);
    }

    /// <summary>
    /// Replaces an object inside a Unity bundle and repacks the enclosing
    /// bundle with its original compression. AssetsTools.NET owns the
    /// serialized-file layout; this adapter only connects the edited
    /// SerializedFile back to its bundle directory entry.
    /// </summary>
    public void ReplaceBundleSerializedAsset(string bundlePath, string serializedFileName,
        long pathId, byte[] serializedData, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedFileName);
        ArgumentNullException.ThrowIfNull(serializedData);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var bundle = _manager.LoadBundleFile(bundlePath, unpackIfPacked: true)
            ?? throw new InvalidDataException($"无法读取 Unity Bundle: {bundlePath}");
        var index = bundle.file.GetFileIndex(serializedFileName);
        if (index < 0 || !bundle.file.IsAssetsFile(index))
            throw new KeyNotFoundException($"Bundle 中不存在 SerializedFile: {serializedFileName}");
        var instance = _manager.LoadAssetsFileFromBundle(bundle, serializedFileName, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFileName}");
        var info = instance.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        info.SetNewData(serializedData);
        var directory = bundle.file.BlockAndDirInfo.DirectoryInfos.FirstOrDefault(x =>
            string.Equals(x.Name, serializedFileName, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Bundle 目录中不存在文件: {serializedFileName}");
        directory.SetNewData(instance.file);
        WritePackedBundle(bundle, outputPath);
    }

    /// <summary>Applies several object replacements in one load/repack pass.</summary>
    public void ReplaceBundleSerializedAssets(string bundlePath, string serializedFileName,
        IReadOnlyDictionary<long, byte[]> replacements, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedFileName);
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0) throw new ArgumentException("至少需要一个对象替换。", nameof(replacements));
        var bundle = _manager.LoadBundleFile(bundlePath, unpackIfPacked: true)
            ?? throw new InvalidDataException($"无法读取 Unity Bundle: {bundlePath}");
        var index = bundle.file.GetFileIndex(serializedFileName);
        if (index < 0 || !bundle.file.IsAssetsFile(index))
            throw new KeyNotFoundException($"Bundle 中不存在 SerializedFile: {serializedFileName}");
        var instance = _manager.LoadAssetsFileFromBundle(bundle, serializedFileName, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFileName}");
        foreach (var replacement in replacements)
        {
            var info = instance.file.GetAssetInfo(replacement.Key)
                ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {replacement.Key}");
            info.SetNewData(replacement.Value ?? throw new ArgumentNullException(nameof(replacements), "替换数据不能为 null。"));
        }
        var directory = bundle.file.BlockAndDirInfo.DirectoryInfos.FirstOrDefault(x =>
            string.Equals(x.Name, serializedFileName, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Bundle 目录中不存在文件: {serializedFileName}");
        directory.SetNewData(instance.file);
        WritePackedBundle(bundle, outputPath);
    }

    /// <summary>PNG-specialized variant for Texture2D objects in a bundle.</summary>
    public void ReplaceBundleTextureFromPng(string bundlePath, string serializedFileName,
        long pathId, ReadOnlySpan<byte> pngData, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var bundle = _manager.LoadBundleFile(bundlePath, unpackIfPacked: true)
            ?? throw new InvalidDataException($"无法读取 Unity Bundle: {bundlePath}");
        var index = bundle.file.GetFileIndex(serializedFileName);
        if (index < 0 || !bundle.file.IsAssetsFile(index))
            throw new KeyNotFoundException($"Bundle 中不存在 SerializedFile: {serializedFileName}");
        var instance = _manager.LoadAssetsFileFromBundle(bundle, serializedFileName, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFileName}");
        var info = instance.file.GetAssetInfo(pathId)
            ?? throw new KeyNotFoundException($"SerializedFile 中不存在 Path ID: {pathId}");
        if (info.GetTypeId(instance.file) != 28) throw new InvalidOperationException("目标对象不是 Texture2D。");
        var fields = _manager.GetBaseField(instance, info, AssetReadFlags.None);
        var textureFormat = ReadInt(fields, "m_TextureFormat", "textureFormat");
        var pixelFormat = textureFormat switch
        {
            3 => UnityTexturePixelFormat.Rgb24,
            4 or 5 => UnityTexturePixelFormat.Rgba32,
            10 => UnityTexturePixelFormat.Dxt1,
            12 => UnityTexturePixelFormat.Dxt5,
            14 => UnityTexturePixelFormat.Bgra32,
            _ => throw new NotSupportedException($"暂不支持写回 Unity TextureFormat {textureFormat}。")
        };
        var texture = new UnityTextureCodec().FromPng(pngData, pixelFormat);
        SetInt(fields, texture.Width, "m_Width", "width");
        SetInt(fields, texture.Height, "m_Height", "height");
        var dataField = FindField(fields, "m_TextureData", "m_ImageData", "image data", "data")
            ?? throw new InvalidDataException("Texture2D 中未找到像素数据字段。");
        dataField.AsByteArray = texture.PixelData;
        // pixel bytes are now inline: an m_StreamData still pointing at the
        // original .resS stream would make the game read the OLD image
        ClearStreamData(fields);
        info.SetNewData(fields);
        var directory = bundle.file.BlockAndDirInfo.DirectoryInfos.FirstOrDefault(x =>
            string.Equals(x.Name, serializedFileName, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Bundle 目录中不存在文件: {serializedFileName}");
        directory.SetNewData(instance.file);
        WritePackedBundle(bundle, outputPath);
    }

    private static void WritePackedBundle(BundleFileInstance bundle, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // SetNewData 只登记 Replacer，真正的数据落盘发生在未压缩 Write 路径；
            // 直接 Pack 只会重新压缩原始 DataReader，把全部修改静默丢弃
            // （AssetsTools.NET v3 的 Pack 不处理 Replacer，已在真实 bundle 上复现）。
            // 因此先写未压缩 UnityFS（此步应用修改），再重载并按原压缩类型打包。
            using (var uncompressed = new MemoryStream())
            {
                // AssetsFileWriter(=BinaryWriter) 释放时会关闭底层流；沿用
                // AssetsTools 自家 BundleHelper 的用法：不 dispose，直接复用流
                var writer = new AssetsFileWriter(uncompressed);
                bundle.file.Write(writer, -1);
                writer.Flush();
                uncompressed.Position = 0;
                var repacked = new AssetBundleFile();
                repacked.Read(new AssetsFileReader(uncompressed));
                using (var packWriter = new AssetsFileWriter(temporary))
                    repacked.Pack(packWriter, bundle.originalCompression, true, null);
            }
            // dispose before moving: the writer holds the temp stream open
            MoveWithRetry(temporary, outputPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteSerializedFile(AssetsFileInstance file, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            {
                using var writer = new AssetsFileWriter(temporary);
                file.file.Write(writer, 0);
            }
            // dispose before moving: the writer holds the temp stream open
            MoveWithRetry(temporary, outputPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Moves the freshly written temp file into place, retrying
    /// briefly: antivirus and search indexers occasionally hold new files
    /// for a moment right after close.</summary>
    private static void MoveWithRetry(string source, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(120 * (attempt + 1));
            }
            catch (IOException ex)
            {
                var sourceState = ProbeFile(source);
                var targetState = ProbeFile(destination);
                throw new IOException(
                    $"移动 {source} → {destination} 失败: {ex.Message}；源状态 {sourceState}；目标状态 {targetState}", ex);
            }
        }
    }

    private static string ProbeFile(string path)
    {
        if (!File.Exists(path)) return "不存在";
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return $"可读写 ({stream.Length} 字节)";
        }
        catch (Exception ex) { return $"被锁 ({ex.GetType().Name}: {ex.Message})"; }
    }

    public void Dispose()
    {
        _manager.UnloadAllAssetsFiles(clearCache: true);
        _manager.UnloadAllBundleFiles();
        _bundles.Clear();
    }

    private static AssetType MapType(int typeId) => UnityClassId.Map(typeId);

    private static int ReadInt(AssetTypeValueField root, params string[] names)
    {
        var field = FindField(root, names);
        if (field is null) return -1;
        return field.Value.ValueType switch
        {
            AssetValueType.Int32 => field.AsInt,
            AssetValueType.UInt32 => checked((int)field.AsUInt),
            AssetValueType.Int16 => field.AsShort,
            AssetValueType.UInt16 => field.AsUShort,
            AssetValueType.UInt8 => field.AsByte,
            _ => -1
        };
    }

    private static UnitySpriteObject ReadSpriteFields(string containerPath, long pathId, AssetTypeValueField root)
    {
        var rect = ReadRect(FindField(root, "m_Rect", "rect"));
        var offset = ReadVector2(FindField(root, "m_Offset", "offset"));
        var pivot = ReadVector2(FindField(root, "m_Pivot", "pivot"));
        var border = ReadBorder(FindField(root, "m_Border", "border"));
        var ppu = ReadFloat(root, "m_PixelsToUnits", "pixelsToUnits");
        var renderData = FindField(root, "m_RD", "m_RenderData", "renderData");
        var texture = ReadPPtrPathId(renderData is null ? null : FindField(renderData, "m_Texture", "texture"));
        var alpha = ReadPPtrPathId(renderData is null ? null : FindField(renderData, "m_AlphaTexture", "alphaTexture"));
        return new(containerPath, pathId, rect, offset, pivot, border, ppu, texture, alpha);
    }

    private static void SetSpriteFields(AssetTypeValueField root, UnitySpriteRect rect,
        UnitySpriteVector2 pivot, UnitySpriteBorder border, float pixelsToUnits)
    {
        SetRect(FindField(root, "m_Rect", "rect"), rect, "m_Rect");
        SetVector2(FindField(root, "m_Pivot", "pivot"), pivot.X, pivot.Y, "m_Pivot");
        SetVector4(FindField(root, "m_Border", "border"), border.Left, border.Bottom, border.Right, border.Top, "m_Border");
        SetFloat(root, pixelsToUnits, "m_PixelsToUnits", "pixelsToUnits");
    }

    private static UnitySpriteRect ReadRect(AssetTypeValueField? field)
        => field is null ? default : new(ReadFloat(field, "x"), ReadFloat(field, "y"), ReadFloat(field, "width"), ReadFloat(field, "height"));
    private static UnitySpriteVector2 ReadVector2(AssetTypeValueField? field)
        => field is null ? default : new(ReadFloat(field, "x"), ReadFloat(field, "y"));
    private static UnitySpriteBorder ReadBorder(AssetTypeValueField? field)
        => field is null ? default : new(ReadFloat(field, "x"), ReadFloat(field, "y"), ReadFloat(field, "z"), ReadFloat(field, "w"));

    private static float ReadFloat(AssetTypeValueField root, params string[] names)
    {
        var field = FindField(root, names);
        if (field is null) return 0;
        return field.Value.ValueType switch
        {
            AssetValueType.Float => field.AsFloat,
            AssetValueType.Double => (float)field.AsDouble,
            AssetValueType.Int32 => field.AsInt,
            AssetValueType.UInt32 => field.AsUInt,
            _ => 0
        };
    }

    private static long? ReadPPtrPathId(AssetTypeValueField? field)
    {
        if (field is null) return null;
        var path = FindField(field, "m_PathID", "pathID", "m_PathId", "pathId");
        if (path is null) return null;
        return path.Value.ValueType switch
        {
            AssetValueType.Int64 => path.AsLong,
            AssetValueType.UInt64 => checked((long)path.AsULong),
            AssetValueType.Int32 => path.AsInt,
            AssetValueType.UInt32 => path.AsUInt,
            _ => null
        };
    }

    private static void SetFloat(AssetTypeValueField root, float value, params string[] names)
    {
        var field = FindField(root, names) ?? throw new InvalidDataException($"缺少 Sprite 字段: {names[0]}");
        switch (field.Value.ValueType)
        {
            case AssetValueType.Float: field.AsFloat = value; break;
            case AssetValueType.Double: field.AsDouble = value; break;
            default: throw new InvalidDataException($"字段 {names[0]} 不是浮点类型。");
        }
    }

    private static void SetVector2(AssetTypeValueField? field, float x, float y, string name)
    {
        if (field is null) throw new InvalidDataException($"缺少 Sprite 字段: {name}");
        SetFloat(field, x, "x"); SetFloat(field, y, "y");
    }

    private static void SetVector4(AssetTypeValueField? field, float x, float y, float z, float w, string name)
    {
        if (field is null) throw new InvalidDataException($"缺少 Sprite 字段: {name}");
        SetFloat(field, x, "x"); SetFloat(field, y, "y"); SetFloat(field, z, "z"); SetFloat(field, w, "w");
    }

    private static void SetRect(AssetTypeValueField? field, UnitySpriteRect rect, string name)
    {
        if (field is null) throw new InvalidDataException($"缺少 Sprite 字段: {name}");
        SetFloat(field, rect.X, "x"); SetFloat(field, rect.Y, "y");
        SetFloat(field, rect.Width, "width"); SetFloat(field, rect.Height, "height");
    }

    private static void SetInt(AssetTypeValueField root, int value, params string[] names)
    {
        var field = FindField(root, names) ?? throw new InvalidDataException($"缺少 Texture2D 字段: {names[0]}");
        switch (field.Value.ValueType)
        {
            case AssetValueType.Int32: field.AsInt = value; break;
            case AssetValueType.UInt32: field.AsUInt = checked((uint)value); break;
            case AssetValueType.Int16: field.AsShort = checked((short)value); break;
            case AssetValueType.UInt16: field.AsUShort = checked((ushort)value); break;
            default: throw new InvalidDataException($"字段 {names[0]} 不是整数类型。");
        }
    }

    private static AssetTypeValueField? FindField(AssetTypeValueField root, params string[] names)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return FindFieldCore(root, wanted, 0);
    }

    private static AssetTypeValueField? FindFieldCore(AssetTypeValueField field, HashSet<string> names, int depth)
    {
        if (depth > 16) return null;
        if (names.Contains(field.FieldName)) return field;
        foreach (var child in field.Children)
        {
            var found = FindFieldCore(child, names, depth + 1);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>Builds the editor field tree by walking the parsed value tree
    /// and the serialized type template side by side, so enum, PPtr, array and
    /// byte-array nodes carry their real type information.</summary>
    private static UnityFieldNode BuildFieldNode(AssetTypeValueField field, AssetTypeTemplateField? template,
        string name, string path, bool parentIsArraySize = false)
    {
        var value = field.Value;
        var valueType = MapValueType(value?.ValueType ?? AssetValueType.None);
        var typeName = template?.Type ?? field.TypeName ?? value?.ValueType.ToString() ?? string.Empty;
        var isPPtr = typeName.StartsWith("PPtr<", StringComparison.Ordinal);
        var isEnum = template is not null && IsEnumTemplate(template);
        // An enum is serialized as a container with one "value" child; edits
        // on the enum path must parse as the child's integer type.
        if (isEnum && template!.Children.Count == 1)
        {
            valueType = MapValueType(template.Children[0].ValueType);
            value = field.Children.Count == 1 ? field.Children[0].Value : null;
        }
        var isByteArray = value?.ValueType == AssetValueType.ByteArray;
        var isArray = template?.IsArray == true && !isByteArray;

        long pptrFileId = 0, pptrPathId = 0;
        string? pptrTarget = null;
        if (isPPtr)
        {
            pptrTarget = ExtractPPtrTarget(typeName);
            foreach (var child in field.Children)
            {
                if (child.FieldName.Equals("m_FileID", StringComparison.OrdinalIgnoreCase) ||
                    child.FieldName.Equals("fileID", StringComparison.OrdinalIgnoreCase)) TryReadLong(child, out pptrFileId);
                else if (child.FieldName.Equals("m_PathID", StringComparison.OrdinalIgnoreCase) ||
                         child.FieldName.Equals("pathID", StringComparison.OrdinalIgnoreCase)) TryReadLong(child, out pptrPathId);
            }
        }

        var children = new List<UnityFieldNode>();
        if (isByteArray)
        {
            // byte arrays carry their payload in the node value, not children
        }
        else if (template is not null && template.IsArray)
        {
            // AssetsTools vector templates carry [size, itemTemplate]; the
            // parsed value children are the items, all built from the item
            // template (their own field names are the template's, e.g. "Array").
            var itemTemplate = GetArrayItemTemplate(template);
            for (var i = 0; i < field.Children.Count; i++)
            {
                var child = field.Children[i];
                children.Add(BuildFieldNode(child, itemTemplate, $"[{i}]", $"{path}[{i}]"));
            }
        }
        else
        {
            for (var i = 0; i < field.Children.Count; i++)
            {
                var child = field.Children[i];
                var childTemplate = template is not null && template.Children.Count > i ? template.Children[i] : null;
                var childName = string.IsNullOrWhiteSpace(child.FieldName) ? $"[{i}]" : child.FieldName;
                var childPath = childName.StartsWith("[", StringComparison.Ordinal) ? $"{path}{childName}" : $"{path}.{childName}";
                var childNode = BuildFieldNode(child, childTemplate, childName, childPath);
                if (isArray && childName.Equals("size", StringComparison.Ordinal)) childNode = childNode with { Editable = false };
                children.Add(childNode);
            }
        }

        var editable = (UnityFieldNode.IsEditableValueType(valueType) || isEnum) && !parentIsArraySize && !isArray && !isByteArray && !isPPtr;
        long byteArrayLength = 0;
        string byteArrayPreview = string.Empty;
        if (isByteArray)
        {
            var bytes = field.AsByteArray;
            byteArrayLength = bytes?.Length ?? 0;
            byteArrayPreview = ToHexPreview(bytes);
        }

        return new UnityFieldNode(
            path, name, typeName,
            isEnum && field.Children.Count == 1 ? ReadFieldValue(field.Children[0]) : ReadFieldValue(field),
            children,
            valueType,
            isArray, isArray ? CountArrayItems(field) : 0,
            isEnum, isEnum ? typeName : string.Empty,
            isPPtr, pptrFileId, pptrPathId, pptrTarget,
            byteArrayLength, byteArrayPreview,
            editable);
    }

    /// <summary>The item template of an array node. AssetsTools vector
    /// templates carry [size, itemTemplate]; alternate shapes carry the item
    /// template as the single child.</summary>
    private static AssetTypeTemplateField? GetArrayItemTemplate(AssetTypeTemplateField template)
    {
        if (template.Children.Count >= 2) return template.Children[1];
        if (template.Children.Count == 1) return template.Children[0];
        return null;
    }

    private static int CountArrayItems(AssetTypeValueField field)
    {
        if (field.Value?.ValueType == AssetValueType.ByteArray) return 0;
        var container = templateArrayContainer(field);
        return container?.Children.Count ?? 0;

        static AssetTypeValueField? templateArrayContainer(AssetTypeValueField f)
        {
            // value-side shape A: items stored directly on the node
            if (f.Children.Count > 0 && f.Children.All(x => !x.FieldName.Equals("size", StringComparison.OrdinalIgnoreCase)))
                return f;
            // value-side shape B: a nested "Array"/"data" vector holds the items
            var nested = f.Children.FirstOrDefault(x =>
                x.FieldName.Equals("Array", StringComparison.OrdinalIgnoreCase) ||
                x.FieldName.Equals("data", StringComparison.OrdinalIgnoreCase));
            return nested;
        }
    }

    private static string ToHexPreview(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return string.Empty;
        const int previewLength = 16;
        var count = Math.Min(previewLength, bytes.Length);
        var builder = new StringBuilder(count * 3);
        for (var i = 0; i < count; i++)
        {
            if (i > 0) builder.Append(' ');
            builder.Append(bytes[i].ToString("X2"));
        }
        if (bytes.Length > count) builder.Append(" …");
        return builder.ToString();
    }

    /// <summary>Returns the serialized type template of an object, or null with
    /// a reason when the file has no usable type tree.</summary>
    private AssetTypeTemplateField? LoadTemplate(AssetsFileInstance file, AssetFileInfo info, out string? missingReason)
    {
        missingReason = null;
        var metadata = file.file.Metadata;
        if (!metadata.TypeTreeEnabled)
        {
            missingReason = "SerializedFile 未包含类型树（TypeTreeEnabled=false），无法读取字段结构。";
            return null;
        }
        var typeId = info.GetTypeId(file.file);
        var tree = metadata.TypeTreeTypes.FirstOrDefault(t => t.TypeId == typeId);
        if (tree is null)
        {
            missingReason = $"类型 {typeId} 的类型树被剥离（stripped），需要 class package 才能读取字段结构。";
            return null;
        }
        var template = new AssetTypeTemplateField();
        template.FromTypeTree(tree);
        NormalizeTemplateChildren(template);
        return template;
    }

    /// <summary>FromTypeTree can leave null Children lists on leaf nodes;
    /// every walk in this backend dereferences Children.</summary>
    private static void NormalizeTemplateChildren(AssetTypeTemplateField node)
    {
        node.Children ??= [];
        foreach (var child in node.Children) NormalizeTemplateChildren(child);
    }

    /// <summary>Loads one SerializedFile out of a bundle, registering the
    /// bundle for later unload.</summary>
    private AssetsFileInstance LoadBundleAssetsFile(string bundlePath, string serializedFileName)
    {
        var bundle = _manager.LoadBundleFile(bundlePath, unpackIfPacked: true)
            ?? throw new InvalidDataException($"无法读取 Unity Bundle: {bundlePath}");
        _bundles.Add(bundle);
        var index = bundle.file.GetFileIndex(serializedFileName);
        if (index < 0 || !bundle.file.IsAssetsFile(index)) throw new KeyNotFoundException($"Bundle 中不存在 SerializedFile: {serializedFileName}");
        return _manager.LoadAssetsFileFromBundle(bundle, serializedFileName, loadDeps: false)
            ?? throw new InvalidDataException($"无法读取 SerializedFile: {serializedFileName}");
    }

    private AssetTypeTemplateField? LoadTemplate(AssetsFileInstance file, AssetFileInfo info)
        => LoadTemplate(file, info, out _);

    private AssetTypeValueField GetBaseField(AssetsFileInstance file, AssetFileInfo info, AssetTypeTemplateField? template)
    {
        try
        {
            return _manager.GetBaseField(file, info, AssetReadFlags.None);
        }
        catch (Exception ex) when (template is null)
        {
            throw new InvalidDataException($"{ex.Message}");
        }
    }

    private UnityScriptInfo ReadScriptInfoCore(AssetsFileInstance file, AssetFileInfo info)
    {
        var typeId = info.GetTypeId(file.file);
        if (typeId != UnityClassId.MonoBehaviour)
            throw new InvalidOperationException($"目标对象不是 MonoBehaviour（类型 {typeId}），无法读取脚本信息。");
        var template = LoadTemplate(file, info, out var missingReason);
        if (template is null) return new UnityScriptInfo(0, 0, null, null, null, null, null, missingReason);
        AssetTypeValueField fields;
        try { fields = _manager.GetBaseField(file, info, AssetReadFlags.None); }
        catch (Exception ex) { return new UnityScriptInfo(0, 0, null, null, null, null, null, $"读取字段树失败: {ex.Message}"); }

        var script = FindField(fields, "m_Script", "script");
        long fileId = 0, pathId = 0;
        if (script is not null)
        {
            var idField = FindField(script, "m_FileID", "fileID");
            var pathField = FindField(script, "m_PathID", "pathID");
            if (idField is not null) TryReadLong(idField, out fileId);
            if (pathField is not null) TryReadLong(pathField, out pathId);
        }

        string? className = null, namespaceName = null, assemblyName = null, externalPath = null, externalGuid = null;
        if (fileId == 0 && pathId != 0)
        {
            var scriptInfo = file.file.GetAssetInfo(pathId);
            if (scriptInfo is null)
            {
                missingReason ??= $"m_Script 指向的 Path ID {pathId} 在本文件中不存在（悬空引用）。";
            }
            else if (scriptInfo.GetTypeId(file.file) == UnityClassId.MonoScript)
            {
                try
                {
                    var scriptFields = _manager.GetBaseField(file, scriptInfo, AssetReadFlags.None);
                    className = FindField(scriptFields, "m_ClassName", "className")?.AsString;
                    namespaceName = FindField(scriptFields, "m_Namespace", "namespace")?.AsString;
                    assemblyName = FindField(scriptFields, "m_AssemblyName", "m_Assembly", "assemblyName")?.AsString;
                }
                catch (Exception ex) { missingReason ??= $"读取 MonoScript 失败: {ex.Message}"; }
            }
            else
            {
                missingReason ??= $"m_Script 指向 Path ID {pathId} 的类型不是 MonoScript。";
            }
        }
        else if (fileId > 0)
        {
            var externals = file.file.Metadata.Externals;
            var externalIndex = (int)fileId - 1;
            if (externalIndex < 0 || externalIndex >= externals.Count)
            {
                missingReason ??= $"m_FileID {fileId} 超出外部引用表范围。";
            }
            else
            {
                externalPath = externals[externalIndex].PathName;
                externalGuid = externals[externalIndex].Guid.ToString();
            }
        }

        return new UnityScriptInfo(fileId, pathId, className, namespaceName, assemblyName, externalPath, externalGuid, missingReason);
    }

    private IReadOnlyList<UnityFieldEditDiagnostic> ValidateFieldEdits(AssetsFileInstance? file, AssetTypeValueField root,
        AssetTypeTemplateField? template, IReadOnlyDictionary<string, string> edits)
    {
        var nodes = new Dictionary<string, (string ValueType, string TypeName, bool Editable)>(StringComparer.Ordinal);
        IndexNodes(root, template, root.FieldName, nodes, templateIsArray: false);
        var results = new List<UnityFieldEditDiagnostic>();
        string? ResolveNodePath(string key)
        {
            if (nodes.ContainsKey(key)) return key;
            if (nodes.ContainsKey("Base." + key)) return "Base." + key;
            var trimmed = key.Contains('.') ? key[(key.IndexOf('.') + 1)..] : null;
            return trimmed is not null && nodes.ContainsKey(trimmed) ? trimmed : null;
        }
        foreach (var edit in edits)
        {
            if (!nodes.TryGetValue(edit.Key, out var node)
                && !nodes.TryGetValue("Base." + edit.Key, out node))
            {
                var trimmed = edit.Key.Contains('.') ? edit.Key[(edit.Key.IndexOf('.') + 1)..] : null;
                if (trimmed is null || !nodes.TryGetValue(trimmed, out node))
                {
                    results.Add(new UnityFieldEditDiagnostic(edit.Key, UnityFieldEditStatus.UnknownPath, string.Empty, "未找到字段路径。"));
                    continue;
                }
            }
            if (!node.Editable)
            {
                results.Add(new UnityFieldEditDiagnostic(edit.Key, UnityFieldEditStatus.NotEditable, node.TypeName, "该字段不是可编辑的基础字段。"));
                continue;
            }
            if (!UnityFieldValueParser.TryValidate(node.ValueType, edit.Value, out var error))
            {
                results.Add(new UnityFieldEditDiagnostic(edit.Key, UnityFieldEditStatus.ParseError, node.TypeName, error ?? "值无效。"));
                continue;
            }
            if (file is not null)
            {
                var targetProblem = ValidatePointerTarget(ResolveNodePath, nodes, root, file, edit, edits);
                if (targetProblem is not null)
                {
                    results.Add(new UnityFieldEditDiagnostic(edit.Key, UnityFieldEditStatus.InvalidTarget, node.TypeName, targetProblem));
                    continue;
                }
            }
            results.Add(new UnityFieldEditDiagnostic(edit.Key, UnityFieldEditStatus.Ok, node.TypeName, string.Empty));
        }
        return results;
    }

    /// <summary>Semantic check for edits on the two halves of a PPtr: m_FileID
    /// must stay within the external table, and a m_PathID that points inside
    /// the same file (FileID 0) must name an object that actually exists.</summary>
    private static string? ValidatePointerTarget(Func<string, string?> resolveNodePath,
        Dictionary<string, (string ValueType, string TypeName, bool Editable)> nodes,
        AssetTypeValueField root, AssetsFileInstance file,
        KeyValuePair<string, string> edit, IReadOnlyDictionary<string, string> edits)
    {
        var nodePath = resolveNodePath(edit.Key);
        if (nodePath is null) return null;
        var lastDot = nodePath.LastIndexOf('.');
        if (lastDot < 0) return null;
        var parentPath = nodePath[..lastDot];
        var segmentName = nodePath[(lastDot + 1)..];
        var isFileId = segmentName.Equals("m_FileID", StringComparison.OrdinalIgnoreCase) || segmentName.Equals("fileID", StringComparison.OrdinalIgnoreCase);
        var isPathId = segmentName.Equals("m_PathID", StringComparison.OrdinalIgnoreCase) || segmentName.Equals("pathID", StringComparison.OrdinalIgnoreCase);
        if (!isFileId && !isPathId) return null;
        if (!nodes.TryGetValue(parentPath, out var parentNode) || !parentNode.TypeName.StartsWith("PPtr<", StringComparison.Ordinal)) return null;

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (!long.TryParse(edit.Value, System.Globalization.NumberStyles.Integer, culture, out var value)) return null;

        if (isFileId)
        {
            var externalCount = file.file.Metadata.Externals.Count;
            if (value < 0 || value > externalCount)
                return $"m_FileID {value} 超出外部引用表范围（0..{externalCount}）。";
            if (value == 0)
            {
                // The pointer becomes same-file: the (possibly edited) sibling
                // m_PathID must name an object that actually exists here, or
                // the edit silently dangles.
                long effectivePathId = 0;
                var siblingOwner = NavigateField(root, parentPath);
                if (siblingOwner is not null)
                {
                    var sibling = siblingOwner.Children.FirstOrDefault(x => x.FieldName.Equals("m_PathID", StringComparison.OrdinalIgnoreCase) || x.FieldName.Equals("pathID", StringComparison.OrdinalIgnoreCase));
                    if (sibling is not null) TryReadLong(sibling, out effectivePathId);
                }
                var siblingPathIdPath = parentPath + ".m_PathID";
                foreach (var other in edits)
                {
                    if (resolveNodePath(other.Key)?.Equals(siblingPathIdPath, StringComparison.Ordinal) != true) continue;
                    if (long.TryParse(other.Value, System.Globalization.NumberStyles.Integer, culture, out var overridden)) effectivePathId = overridden;
                }
                if (effectivePathId != 0 && file.file.GetAssetInfo(effectivePathId) is null)
                    return $"将 m_FileID 置 0 后，同文件 Path ID {effectivePathId} 不存在（将产生悬空引用）。";
            }
            return null;
        }

        // m_PathID: resolve the effective file ID (sibling current value or an
        // edit in the same set that overrides it).
        long effectiveFileId = 0;
        var parentField = NavigateField(root, parentPath);
        if (parentField is not null)
        {
            var sibling = parentField.Children.FirstOrDefault(x => x.FieldName.Equals("m_FileID", StringComparison.OrdinalIgnoreCase) || x.FieldName.Equals("fileID", StringComparison.OrdinalIgnoreCase));
            if (sibling is not null) TryReadLong(sibling, out effectiveFileId);
        }
        var siblingPath = parentPath + ".m_FileID";
        foreach (var other in edits)
        {
            if (resolveNodePath(other.Key)?.Equals(siblingPath, StringComparison.Ordinal) != true) continue;
            if (long.TryParse(other.Value, System.Globalization.NumberStyles.Integer, culture, out var overridden)) effectiveFileId = overridden;
        }

        if (effectiveFileId != 0) return null;
        if (value == 0) return null; // deliberately clearing the pointer is legal
        return file.file.GetAssetInfo(value) is null
            ? $"同文件指针指向的 Path ID {value} 不存在（将产生悬空引用）。"
            : null;
    }

    /// <summary>Walks a value tree by a dot/bracket path such as
    /// "Base.m_Tags[0].data"; returns null when a segment is missing.</summary>
    private static AssetTypeValueField? NavigateField(AssetTypeValueField root, string path)
    {
        var current = root;
        foreach (var rawSegment in path.Split('.'))
        {
            if (rawSegment.Length == 0) continue;
            var bracketStart = rawSegment.IndexOf('[');
            var name = bracketStart < 0 ? rawSegment : rawSegment[..bracketStart];
            if (bracketStart < 0)
            {
                if (current.FieldName.Equals(name, StringComparison.Ordinal)) continue;
                var child = current.Children.FirstOrDefault(x => x.FieldName.Equals(name, StringComparison.Ordinal));
                if (child is null) return null;
                current = child;
            }
            else
            {
                if (!current.FieldName.Equals(name, StringComparison.Ordinal)) return null;
                var bracketEnd = rawSegment.IndexOf(']');
                if (bracketStart + 1 >= bracketEnd || !int.TryParse(rawSegment[(bracketStart + 1)..bracketEnd], out var index)) return null;
                if (index < 0 || index >= current.Children.Count) return null;
                current = current.Children[index];
            }
        }
        return current;
    }

    private static void IndexNodes(AssetTypeValueField field, AssetTypeTemplateField? template, string path,
        IDictionary<string, (string, string, bool)> nodes, bool templateIsArray)
    {
        var value = field.Value;
        var valueType = MapValueType(value?.ValueType ?? AssetValueType.None);
        var typeName = template?.Type ?? field.TypeName ?? value?.ValueType.ToString() ?? string.Empty;
        var isEnum = template is not null && IsEnumTemplate(template);
        // An enum is serialized as a container with one "value" child; edits
        // on the enum path must parse as the child's integer type.
        if (isEnum && template!.Children.Count == 1) valueType = MapValueType(template.Children[0].ValueType);
        var isPPtr = typeName.StartsWith("PPtr<", StringComparison.Ordinal);
        var isByteArray = value?.ValueType == AssetValueType.ByteArray;
        var isArray = template?.IsArray == true && !isByteArray;
        var editable = (UnityFieldNode.IsEditableValueType(valueType) || isEnum) && !templateIsArray && !isArray && !isByteArray && !isPPtr;
        nodes[path] = (valueType, typeName, editable);
        if (isByteArray) return;
        if (template is not null && template.IsArray)
        {
            var itemTemplate = GetArrayItemTemplate(template);
            for (var i = 0; i < field.Children.Count; i++)
                IndexNodes(field.Children[i], itemTemplate, $"{path}[{i}]", nodes, templateIsArray: false);
            return;
        }
        for (var i = 0; i < field.Children.Count; i++)
        {
            var child = field.Children[i];
            var childTemplate = template is not null && template.Children.Count > i ? template.Children[i] : null;
            var childName = string.IsNullOrWhiteSpace(child.FieldName) ? $"[{i}]" : child.FieldName;
            var childPath = childName.StartsWith("[", StringComparison.Ordinal) ? $"{path}{childName}" : $"{path}.{childName}";
            var childIsSize = isArray && childName.Equals("size", StringComparison.Ordinal);
            IndexNodes(child, childTemplate, childPath, nodes, childIsSize);
        }
    }

    private static string MapValueType(AssetValueType valueType) => valueType switch
    {
        AssetValueType.Bool => "bool",
        AssetValueType.Int8 => "int8",
        AssetValueType.UInt8 => "uint8",
        AssetValueType.Int16 => "int16",
        AssetValueType.UInt16 => "uint16",
        AssetValueType.Int32 => "int32",
        AssetValueType.UInt32 => "uint32",
        AssetValueType.Int64 => "int64",
        AssetValueType.UInt64 => "uint64",
        AssetValueType.Float => "float",
        AssetValueType.Double => "double",
        AssetValueType.String => "string",
        AssetValueType.ByteArray => "byteArray",
        AssetValueType.Array => "array",
        _ => "unknown"
    };

    private static bool IsEnumTemplate(AssetTypeTemplateField template)
    {
        if (template.Children.Count != 1) return false;
        var child = template.Children[0];
        if (!string.Equals(child.Name, "value", StringComparison.Ordinal)) return false;
        return child.ValueType is AssetValueType.Int8 or AssetValueType.UInt8 or AssetValueType.Int16
            or AssetValueType.UInt16 or AssetValueType.Int32 or AssetValueType.UInt32
            && !IsPrimitiveTypeName(template.Type);
    }

    private static bool IsPrimitiveTypeName(string? typeName) => typeName is
        "int" or "unsigned int" or "SInt8" or "UInt8" or "short" or "unsigned short" or
        "SInt16" or "SInt64" or "UInt64" or "long long" or "unsigned long long" or
        "float" or "double" or "bool" or "string" or "TypelessData" or "Type[]" or "vector";

    private static string ExtractPPtrTarget(string typeName)
    {
        var start = typeName.IndexOf('<');
        var end = typeName.LastIndexOf('>');
        if (start < 0 || end <= start) return typeName;
        var target = typeName[(start + 1)..end];
        return target.StartsWith('$') ? target[1..] : target;
    }

    private static void CollectReferences(AssetTypeValueField field, string path, ICollection<UnityObjectReference> result)
    {
        var fileId = field.Children.FirstOrDefault(x => x.FieldName.Equals("m_FileID", StringComparison.OrdinalIgnoreCase) || x.FieldName.Equals("fileID", StringComparison.OrdinalIgnoreCase));
        var pathId = field.Children.FirstOrDefault(x => x.FieldName.Equals("m_PathID", StringComparison.OrdinalIgnoreCase) || x.FieldName.Equals("pathID", StringComparison.OrdinalIgnoreCase));
        if (fileId is not null && pathId is not null && TryReadLong(fileId, out var fileValue) && TryReadLong(pathId, out var pathValue) && pathValue != 0)
            result.Add(new UnityObjectReference(path, fileValue, pathValue, field.TypeName));
        var isArray = field.TemplateField?.IsArray == true || field.Value?.ValueType == AssetValueType.Array;
        for (var i = 0; i < field.Children.Count; i++)
        {
            var child = field.Children[i];
            string childName;
            string childPath;
            if (isArray) { childName = $"[{i}]"; childPath = $"{path}[{i}]"; }
            else
            {
                childName = string.IsNullOrWhiteSpace(child.FieldName) ? $"[{i}]" : child.FieldName;
                childPath = childName.StartsWith("[", StringComparison.Ordinal) ? $"{path}{childName}" : $"{path}.{childName}";
            }
            CollectReferences(child, childPath, result);
        }
    }

    private static bool TryReadLong(AssetTypeValueField field, out long value)
    {
        value = 0;
        try
        {
            value = field.Value.ValueType switch
            {
                AssetValueType.Int8 => field.AsSByte,
                AssetValueType.UInt8 => field.AsByte,
                AssetValueType.Int16 => field.AsShort,
                AssetValueType.UInt16 => field.AsUShort,
                AssetValueType.Int32 => field.AsInt,
                AssetValueType.UInt32 => field.AsUInt,
                AssetValueType.Int64 => field.AsLong,
                AssetValueType.UInt64 => checked((long)field.AsULong),
                _ => throw new InvalidDataException()
            };
            return true;
        }
        catch (Exception) { return false; }
    }

    private static void ApplyFieldEdits(AssetTypeValueField field, string path, IReadOnlyDictionary<string, string> edits, ISet<string> matched)
    {
        if (field.Value?.ValueType == AssetValueType.ByteArray) return;
        string? matchedKey = null;
        if (edits.TryGetValue(path, out var value)) matchedKey = path;
        else if (path.StartsWith("Base.", StringComparison.Ordinal)
            && edits.TryGetValue(path[5..], out value)) matchedKey = path[5..];
        if (matchedKey is not null)
        {
            // Enums serialize as a container with a single "value" child;
            // an edit targeting the enum path delegates to that child. A
            // container's Value is null (or None), never a primitive.
            var isContainer = field.Value is null || field.Value.ValueType == AssetValueType.None;
            var target = isContainer
                && field.Children.Count == 1
                && field.Children[0].FieldName.Equals("value", StringComparison.OrdinalIgnoreCase)
                ? field.Children[0]
                : field;
            SetPrimitiveField(target, value!, path);
            matched.Add(matchedKey);
        }
        var template = field.TemplateField;
        var isArray = template?.IsArray == true || field.Value?.ValueType == AssetValueType.Array;
        for (var i = 0; i < field.Children.Count; i++)
        {
            var child = field.Children[i];
            string childName;
            string childPath;
            if (isArray)
            {
                // Array items are unnamed index entries; their serialized field
                // names (e.g. "Array") must not leak into edit paths.
                childName = $"[{i}]";
                childPath = $"{path}[{i}]";
            }
            else
            {
                childName = string.IsNullOrWhiteSpace(child.FieldName) ? $"[{i}]" : child.FieldName;
                childPath = childName.StartsWith("[", StringComparison.Ordinal) ? $"{path}{childName}" : $"{path}.{childName}";
            }
            ApplyFieldEdits(child, childPath, edits, matched);
        }
    }

    private static void ThrowForUnknownFieldEdits(IReadOnlyDictionary<string, string> edits, ISet<string> matched)
    {
        var unknown = edits.Keys.Where(x => !matched.Contains(x)).ToArray();
        if (unknown.Length > 0) throw new KeyNotFoundException($"未找到 Unity 字段路径: {string.Join(", ", unknown)}");
    }

    private static void SetPrimitiveField(AssetTypeValueField field, string value, string path)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var valueType = field.Value?.ValueType
            ?? throw new InvalidDataException($"字段 {path} 没有可写的基本值。");
        try
        {
            switch (valueType)
            {
                case AssetValueType.Bool: field.AsBool = bool.Parse(value); break;
                case AssetValueType.Int8: field.AsSByte = sbyte.Parse(value, culture); break;
                case AssetValueType.UInt8: field.AsByte = byte.Parse(value, culture); break;
                case AssetValueType.Int16: field.AsShort = short.Parse(value, culture); break;
                case AssetValueType.UInt16: field.AsUShort = ushort.Parse(value, culture); break;
                case AssetValueType.Int32: field.AsInt = int.Parse(value, culture); break;
                case AssetValueType.UInt32: field.AsUInt = uint.Parse(value, culture); break;
                case AssetValueType.Int64: field.AsLong = long.Parse(value, culture); break;
                case AssetValueType.UInt64: field.AsULong = ulong.Parse(value, culture); break;
                case AssetValueType.Float: field.AsFloat = float.Parse(value, culture); break;
                case AssetValueType.Double: field.AsDouble = double.Parse(value, culture); break;
                case AssetValueType.String: field.AsString = value; break;
                default: throw new NotSupportedException($"字段 {path} 的类型 {field.Value.ValueType} 不支持文本修改。");
            }
        }
        catch (FormatException ex) { throw new InvalidDataException($"字段 {path} 的值无效: {value}", ex); }
        catch (OverflowException ex) { throw new InvalidDataException($"字段 {path} 的值超出范围: {value}", ex); }
    }

    private static string? ReadFieldValue(AssetTypeValueField field)
    {
        try
        {
            return field.Value.ValueType switch
            {
                AssetValueType.Bool => field.AsBool.ToString(),
                AssetValueType.Int8 => field.AsSByte.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AssetValueType.UInt8 => field.AsByte.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AssetValueType.Int16 => field.AsShort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AssetValueType.UInt16 => field.AsUShort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AssetValueType.Int32 => field.AsInt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AssetValueType.UInt32 => field.AsUInt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AssetValueType.Int64 => field.AsLong.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AssetValueType.UInt64 => field.AsULong.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AssetValueType.Float => field.AsFloat.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                AssetValueType.Double => field.AsDouble.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                AssetValueType.String => field.AsString,
                AssetValueType.ByteArray => $"<{field.AsByteArray?.Length ?? 0} bytes>",
                _ => null
            };
        }
        catch (Exception) { return null; }
    }
}
