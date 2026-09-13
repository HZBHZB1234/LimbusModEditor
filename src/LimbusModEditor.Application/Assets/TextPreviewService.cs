using System.Text;
using LimbusModEditor.Domain.Assets;
using NLog;

namespace LimbusModEditor.Application.Assets;

/// <summary>文本预览结果：截断后的正文 + 编码说明 + 原始字节数。</summary>
public sealed record TextPreview(string Text, string EncodingName, long TotalBytes, bool Truncated);

/// <summary>文本 / JSON 资源的只读预览（P3.10）：用户在资源视图里选中文本类
/// 资源时，右栏直接显示可读内容，而不是十六进制转储。
/// <para>只认 UTF-8（含 BOM）与 UTF-16（BOM）；其它编码或二进制负载返回 null，
/// 由调用方给出中文说明——不猜测、不假装能读。</para></summary>
public static class TextPreviewService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>预览正文上限（字符）：再多也只是读不完的噪声。</summary>
    public const int DefaultMaxChars = 4000;

    private static readonly string[] TextExtensions =
        [".txt", ".json", ".csv", ".md", ".xml", ".yaml", ".yml", ".lang", ".cfg", ".ini", ".tsv"];

    /// <summary>是否值得当文本预览（类型或扩展名判定；不要求文件存在，
    /// 列表里刚导入还没落盘的资源也能得到正确判定）。</summary>
    public static bool LooksTextual(AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Type is AssetType.Text or AssetType.Json)
        {
            if (Log.IsTraceEnabled)
                Log.Trace("文本判定：类型 {0} 直接算文本，资源「{1}」。", AssetDisplay.TypeLabel(asset.Type), AssetDisplay.DisplayPath(asset));
            return true;
        }
        var candidate = ExtensionCandidate(asset);
        var extension = candidate is null ? null : Path.GetExtension(candidate).ToLowerInvariant();
        var textual = extension is not null && TextExtensions.Contains(extension);
        if (Log.IsTraceEnabled)
            Log.Trace("文本判定：候选路径「{0}」→ 扩展名「{1}」在白名单内={2} → 结论：{3}，资源「{4}」。",
                candidate ?? "-", extension ?? "-", textual, textual ? "按文本预览" : "不按文本预览", AssetDisplay.DisplayPath(asset));
        return textual;
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
        if (asset.Metadata.TryGetValue("replacementPath", out var replacement) && File.Exists(replacement))
        {
            Log.Debug("文本预览路径：使用替换文件「{0}」，资源「{1}」。", replacement, AssetDisplay.DisplayPath(asset));
            return replacement;
        }
        var readable = !string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath) ? asset.SourcePath : null;
        if (readable is null)
            Log.Debug("文本预览路径：没有可读文件（SourcePath「{0}」不可用，替换文件不可用），资源「{1}」。",
                asset.SourcePath ?? "-", AssetDisplay.DisplayPath(asset));
        return readable;
    }

    /// <summary>读取预览正文；不可读（缺文件 / 非 UTF 文本 / 二进制）返回 null。</summary>
    public static TextPreview? TryPreview(AssetRecord asset, int maxChars = DefaultMaxChars)
    {
        var path = PreviewPath(asset);
        if (path is null)
        {
            Log.Debug("文本预览落空：没有可读文件，返回 null（调用方会给说明卡），资源「{0}」。", AssetDisplay.DisplayPath(asset));
            return null;
        }
        return TryPreviewFile(path, maxChars);
    }

    public static TextPreview? TryPreviewFile(string path, int maxChars = DefaultMaxChars)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxChars), "预览字符上限必须为正数。");
        if (!File.Exists(path))
        {
            Log.Debug("文本预览落空：文件不存在「{0}」，返回 null。", path);
            return null;
        }
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
        if (head.Length == 0)
        {
            Log.Debug("文本预览：文件「{0}」是空文件（{1:N0} 字节），返回空正文。", path, totalBytes);
            return new TextPreview(string.Empty, "空文件", totalBytes, Truncated: false);
        }
        if (LooksBinary(head))
        {
            Log.Warn("文本预览回退：文件「{0}」判定为二进制（{1:N0} 字节，读了 {2:N0} 字节头部），返回 null —— 调用方应改用十六进制预览。",
                path, totalBytes, head.Length);
            return null;
        }
        if (!TryDecode(head, out var text, out var encodingName))
        {
            Log.Warn("文本预览回退：文件「{0}」不是 UTF-8/UTF-16 文本（{1:N0} 字节，读了 {2:N0} 字节头部，上限 {3:N0} 字符），返回 null。",
                path, totalBytes, head.Length, maxChars);
            return null;
        }
        var truncated = text.Length > maxChars || totalBytes > head.Length;
        if (text.Length > maxChars) text = text[..maxChars];
        Log.Debug("文本预览读取完成：文件「{0}」，编码 {1}，文件 {2:N0} 字节，头部 {3:N0} 字节，正文 {4:N0} 字符（上限 {5:N0}），截断={6}。",
            path, encodingName ?? "-", totalBytes, head.Length, text.Length, maxChars, truncated);
        return new TextPreview(text, encodingName!, totalBytes, truncated);
    }

    private static bool TryDecode(byte[] head, out string text, out string encodingName)
    {
        text = string.Empty;
        encodingName = string.Empty;
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            encodingName = "UTF-16 LE";
            text = Encoding.Unicode.GetString(head, 2, head.Length - 2);
            Log.Debug("文本解码：按 {0} 解码 {1:N0} 字节头部。", encodingName, head.Length);
            return true;
        }
        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
        {
            encodingName = "UTF-16 BE";
            text = Encoding.BigEndianUnicode.GetString(head, 2, head.Length - 2);
            Log.Debug("文本解码：按 {0} 解码 {1:N0} 字节头部。", encodingName, head.Length);
            return true;
        }
        var offset = head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? 3 : 0;
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            text = strictUtf8.GetString(head, offset, head.Length - offset);
            encodingName = "UTF-8";
            Log.Debug("文本解码：按 UTF-8 解码 {0:N0} 字节头部（BOM 偏移 {1}）。", head.Length, offset);
            return true;
        }
        catch (DecoderFallbackException ex)
        {
            Log.Debug("文本解码失败：头部 {0:N0} 字节不是合法 UTF-8（BOM 偏移 {1}）：{2}", head.Length, offset, ex.Message);
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
