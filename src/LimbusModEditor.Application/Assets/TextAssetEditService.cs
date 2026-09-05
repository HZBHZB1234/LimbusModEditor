using System.Text;
using System.Text.Json;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Assets;

public sealed record TextAssetDocument(Guid AssetId, string LogicalPath, string Text, Encoding Encoding, bool IsJson);

/// <summary>Small text/JSON editing boundary used by a future code editor UI.
/// It deliberately stores edits through AssetEditService so all file formats
/// share the same undo/audit trail.</summary>
public sealed class TextAssetEditService(AssetEditService edits)
{
    public async Task<TextAssetDocument> OpenAsync(ModProject project, Guid assetId, CancellationToken cancellationToken = default)
    {
        var asset = project.Assets.FirstOrDefault(x => x.AssetId == assetId)
            ?? throw new KeyNotFoundException($"未找到资源: {assetId}");
        if (asset.Type is not (AssetType.Text or AssetType.Json))
            throw new InvalidOperationException("只有文本或 JSON 资源可以使用文本编辑器。");
        var bytes = await edits.ReadCurrentBytesAsync(project, assetId, cancellationToken);
        var encoding = DetectEncoding(bytes);
        var text = encoding.GetString(StripBom(bytes, encoding));
        return new(assetId, asset.LogicalPath, text, encoding, asset.Type == AssetType.Json || Path.GetExtension(asset.LogicalPath).Equals(".json", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AssetReplacementResult> SaveAsync(ModProject project, TextAssetDocument document, string projectDirectory, bool formatJson = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = formatJson || document.IsJson ? FormatJson(document.Text) : document.Text;
        var bytes = document.Encoding.GetBytes(text);
        return await edits.ReplaceFromBytesAsync(project, document.AssetId, bytes, Path.GetExtension(document.LogicalPath), projectDirectory, cancellationToken);
    }

    private static string FormatJson(string text)
    {
        using var json = JsonDocument.Parse(text);
        return JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    private static Encoding DetectEncoding(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 2 && bytes[..2].SequenceEqual(new byte[] { 0xFF, 0xFE }) ? Encoding.Unicode
         : bytes.Length >= 2 && bytes[..2].SequenceEqual(new byte[] { 0xFE, 0xFF }) ? Encoding.BigEndianUnicode
         : bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }) ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
         : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static ReadOnlySpan<byte> StripBom(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        if (encoding is UnicodeEncoding && bytes.Length >= 2) return bytes[2..];
        if (encoding is UTF8Encoding && bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })) return bytes[3..];
        return bytes;
    }
}
