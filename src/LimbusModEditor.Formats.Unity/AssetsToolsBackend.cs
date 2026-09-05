using AssetsTools.NET.Extra;
using AssetsTools.NET;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Editing.Images;

namespace LimbusModEditor.Formats.Unity;

public sealed record UnityAssetDescriptor(string ContainerPath, long PathId, int TypeId, uint ByteSize, AssetType AssetType);
public sealed record UnitySerializedObject(long PathId, int TypeId, byte[] Data);
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
        var fields = _manager.GetBaseField(file, info, AssetReadFlags.None);
        return [BuildFieldNode(fields, fields.FieldName, fields.FieldName)];
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
        var fields = _manager.GetBaseField(file, info, AssetReadFlags.None);
        return [BuildFieldNode(fields, fields.FieldName, fields.FieldName)];
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
            var pixels = FindField(fields, "m_TextureData", "m_ImageData", "image data", "data")?.AsByteArray;
            if (width <= 0 || height <= 0 || format < 0 || pixels is null || pixels.Length == 0) return null;
            return new UnityTextureObject(fileName, pathId, width, height, format, pixels.ToArray());
        }
        return null;
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
            if (info is null || info.GetTypeId(file.file) != 115) continue;
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
        if (info.GetTypeId(file.file) != 115) throw new InvalidOperationException("目标对象不是 Sprite。");
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
        if (info.GetTypeId(instance.file) != 115) throw new InvalidOperationException("目标对象不是 Sprite。");
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
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var temporary = outputPath + ".tmp";
        try
        {
            using var writer = new AssetsFileWriter(temporary);
            bundle.file.Pack(writer, bundle.file.GetCompressionType(), true, null);
            File.Move(temporary, outputPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
        var temporary = outputPath + ".tmp";
        try
        {
            using var writer = new AssetsFileWriter(temporary);
            bundle.file.Pack(writer, bundle.file.GetCompressionType(), true, null);
            File.Move(temporary, outputPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteSerializedFile(AssetsFileInstance file, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var temporary = outputPath + ".tmp";
        try
        {
            using var writer = new AssetsFileWriter(temporary);
            file.file.Write(writer, 0);
            File.Move(temporary, outputPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Dispose()
    {
        _manager.UnloadAllAssetsFiles(clearCache: true);
        _manager.UnloadAllBundleFiles();
        _bundles.Clear();
    }

    private static AssetType MapType(int typeId) => typeId switch
    {
        1 => AssetType.GameObject,
        28 => AssetType.Texture,
        83 => AssetType.Audio,
        114 => AssetType.MonoBehaviour,
        115 => AssetType.Sprite,
        128 => AssetType.Font,
        142 => AssetType.Binary,
        213 => AssetType.ScriptableObject,
        _ => AssetType.Unknown
    };

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

    private static UnityFieldNode BuildFieldNode(AssetTypeValueField field, string name, string path)
    {
        var children = field.Children.Select((child, index) =>
        {
            var childName = string.IsNullOrWhiteSpace(child.FieldName) ? $"[{index}]" : child.FieldName;
            var childPath = childName.StartsWith("[", StringComparison.Ordinal) ? $"{path}{childName}" : $"{path}.{childName}";
            return BuildFieldNode(child, childName, childPath);
        }).ToArray();
        return new UnityFieldNode(path, name, field.TypeName ?? field.Value.ValueType.ToString(), ReadFieldValue(field), children);
    }

    private static void CollectReferences(AssetTypeValueField field, string path, ICollection<UnityObjectReference> result)
    {
        var fileId = field.Children.FirstOrDefault(x => x.FieldName.Equals("m_FileID", StringComparison.OrdinalIgnoreCase) || x.FieldName.Equals("fileID", StringComparison.OrdinalIgnoreCase));
        var pathId = field.Children.FirstOrDefault(x => x.FieldName.Equals("m_PathID", StringComparison.OrdinalIgnoreCase) || x.FieldName.Equals("pathID", StringComparison.OrdinalIgnoreCase));
        if (fileId is not null && pathId is not null && TryReadLong(fileId, out var fileValue) && TryReadLong(pathId, out var pathValue) && pathValue != 0)
            result.Add(new UnityObjectReference(path, fileValue, pathValue, field.TypeName));
        for (var i = 0; i < field.Children.Count; i++)
        {
            var child = field.Children[i];
            var childName = string.IsNullOrWhiteSpace(child.FieldName) ? $"[{i}]" : child.FieldName;
            var childPath = childName.StartsWith("[", StringComparison.Ordinal) ? $"{path}{childName}" : $"{path}.{childName}";
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
        string? matchedKey = null;
        if (edits.TryGetValue(path, out var value)) matchedKey = path;
        else if (field.FieldName.Length > 0 && path.IndexOf('.') >= 0 && edits.TryGetValue(path[(path.IndexOf('.') + 1)..], out value)) matchedKey = path[(path.IndexOf('.') + 1)..];
        if (matchedKey is not null)
        {
            SetPrimitiveField(field, value!, path);
            matched.Add(matchedKey);
        }
        for (var i = 0; i < field.Children.Count; i++)
        {
            var child = field.Children[i];
            var childName = string.IsNullOrWhiteSpace(child.FieldName) ? $"[{i}]" : child.FieldName;
            var childPath = childName.StartsWith("[", StringComparison.Ordinal) ? $"{path}{childName}" : $"{path}.{childName}";
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
        try
        {
            switch (field.Value.ValueType)
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
