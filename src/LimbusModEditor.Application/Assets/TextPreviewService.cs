using System.Text;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Assets;

/// <summary>文本预览结果：截断后的正文 + 编码说明 + 原始字节数。</summary>
public sealed record TextPreview(string Text, string EncodingName, long TotalBytes, bool Truncated);

/// <summary>文本 / JSON 资源的只读预览（P3.10）：用户在资源视图里选中文本类
/// 资源时，右栏直接显示可读内容，而不是十六进制转储。
/// <para>只认 UTF-8（含 BOM）与 UTF-16（BOM）；其它编码或二进制负载返回 null，
/// 由调用方给出中文说明——不猜测、不假装能读。</para></summary>
public static class TextPreviewService
{
    /// <summary>预览正文上限（字符）：再多也只是读不完的噪声。</summary>
    public const int DefaultMaxChars = 4000;

    private static readonly string[] TextExtensions =
        [".txt", ".json", ".csv", ".md", ".xml", ".yaml", ".yml", ".lang", ".cfg", ".ini", ".tsv"];

    /// <summary>是否值得当文本预览（类型或扩展名判定；不要求文件存在，
    /// 列表里刚导入还没落盘的资源也能得到正确判定）。</summary>
    public static bool LooksTextual(AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Type is AssetType.Text or AssetType.Json) return true;
        var candidate = ExtensionCandidate(asset);
        return candidate is not null && TextExtensions.Contains(Path.GetExtension(candidate).ToLowerInvariant());
    }

    /// <summary>用于判定扩展名的候选路径（替换文件 → 源文件 → 逻辑路径）。</summary>
    private static string? ExtensionCandidate(AssetRecord asset)
    {
        if (asset.Metadata.TryGetValue("replacementPath", out var replacement) && !string.IsNullOrWhiteSpace(replacement)) return replacement;
        if (!string.IsNullOrWhiteSpace(asset.SourcePath)) return asset.SourcePath;
        return string.IsNullOrWhiteSpace(asset.LogicalPath) ? null : asset.LogicalPath;
    }

    /// <summary>资源当前应该被预览的文件（替换文件优先，否则源文件）。</summary>
    public static string? PreviewPath(AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Metadata.TryGetValue("replacementPath", out var replacement) && File.Exists(replacement)) return replacement;
        return !string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath) ? asset.SourcePath : null;
    }

    /// <summary>读取预览正文；不可读（缺文件 / 非 UTF 文本 / 二进制）返回 null。</summary>
    public static TextPreview? TryPreview(AssetRecord asset, int maxChars = DefaultMaxChars)
    {
        var path = PreviewPath(asset);
        return path is null ? null : TryPreviewFile(path, maxChars);
    }

    public static TextPreview? TryPreviewFile(string path, int maxChars = DefaultMaxChars)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxChars), "预览字符上限必须为正数。");
        if (!File.Exists(path)) return null;
        var totalBytes = new FileInfo(path).Length;
        // 只读前一段：大文件（几十 MB 的 JSON）不该为预览全量加载。
        var headSize = (int)Math.Min(totalBytes, Math.Max(64 * 1024, maxChars * 8L));
        var head = new byte[headSize];
        using (var stream = File.OpenRead(path))
        {
            var read = 0;
            while (read < headSize)
            {
                var chunk = stream.Read(head, read, headSize - read);
                if (chunk <= 0) break;
                read += chunk;
            }
            if (read < headSize) Array.Resize(ref head, read);
        }
        if (head.Length == 0) return new TextPreview(string.Empty, "空文件", totalBytes, Truncated: false);
        if (LooksBinary(head)) return null;
        if (!TryDecode(head, out var text, out var encodingName)) return null;
        var truncated = text.Length > maxChars || totalBytes > head.Length;
        if (text.Length > maxChars) text = text[..maxChars];
        return new TextPreview(text, encodingName, totalBytes, truncated);
    }

    private static bool TryDecode(byte[] head, out string text, out string encodingName)
    {
        text = string.Empty;
        encodingName = string.Empty;
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            encodingName = "UTF-16 LE";
            text = Encoding.Unicode.GetString(head, 2, head.Length - 2);
            return true;
        }
        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
        {
            encodingName = "UTF-16 BE";
            text = Encoding.BigEndianUnicode.GetString(head, 2, head.Length - 2);
            return true;
        }
        var offset = head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? 3 : 0;
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            text = strictUtf8.GetString(head, offset, head.Length - offset);
            encodingName = "UTF-8";
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>二进制判定：出现 NUL 字节或控制字符比例过高就不当文本读。</summary>
    private static bool LooksBinary(byte[] head)
    {
        var controls = 0;
        foreach (var b in head)
        {
            if (b == 0) return true;
            if (b < 0x20 && b is not ((byte)'\n') and not ((byte)'\r') and not ((byte)'\t')) controls++;
        }
        return controls > head.Length / 20;
    }
}
