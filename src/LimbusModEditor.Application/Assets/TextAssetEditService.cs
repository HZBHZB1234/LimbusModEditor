using System.Text;
using System.Text.Json;
using LimbusModEditor.Application.Assets.Preview;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using NLog;

namespace LimbusModEditor.Application.Assets;

public sealed record TextAssetDocument(Guid AssetId, string LogicalPath, string Text, Encoding Encoding, bool IsJson);

/// <summary>Small text/JSON editing boundary used by a future code editor UI.
/// It deliberately stores edits through AssetEditService so all file formats
/// share the same undo/audit trail.
///
/// <para><b>边界（2026-09 卡死事故后明确）</b>：内置编辑器只处理<b>松散文件</b>
/// 正文。bundle 内对象（<c>SourcePath</c> 指向整个 AssetBundle 容器
/// <c>&lt;外层键&gt;/&lt;内层键&gt;/__data</c>）不能进这里 —— 直接读容器字节
/// 会把几百 KB~MB 级二进制当正文，WPF 文本框排版这种内容实测 26.5 秒，
/// 界面被 Windows 判「未响应」后强杀（用户报的「预览 static-data 后崩溃」，
/// 实为挂起，因此没有 crash-*.log）。何况导出链也没有 TextAsset 写回通道。</para></summary>
public sealed class TextAssetEditService(AssetEditService edits)
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>内置文本编辑器的正文上限（字符）。超过这个量级 WPF 文本框本身就是
    /// 卡死风险，因此直接拒绝并引导走外部编辑器 + 「替换选中资源…」。</summary>
    public const int MaxEditableChars = 400_000;

    /// <summary>不可编辑时的中文说明（按钮 ToolTip 与错误对话框共用一份文案）。</summary>
    public const string BundleAssetHint =
        "该资源的正文在 AssetBundle 容器里，内置文本编辑器不支持它（导出链没有 TextAsset 写回通道）。" +
        "选中后双击可直接预览内容；改字段用「Unity 字段编辑」，换整个文件用「替换选中资源…」；" +
        "静态数据表请在「静态数据」工作台里编辑。";

    /// <summary>内置文本编辑器能否编辑这个资源：类型是文本 / JSON，且正文不是
    /// bundle 容器字节。已经登记过「替换文件」的资源按松散文件处理（用户自己选过文件）。</summary>
    public static bool CanEditText(AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return asset.Type is AssetType.Text or AssetType.Json
            && (!PreviewRead.IsBundleAsset(asset) || AssetEditService.TryGetReplacementFile(asset, out _));
    }

    public async Task<TextAssetDocument> OpenAsync(ModProject project, Guid assetId, CancellationToken cancellationToken = default)
    {
        var asset = project.Assets.FirstOrDefault(x => x.AssetId == assetId)
            ?? throw new KeyNotFoundException($"未找到资源: {assetId}");
        if (asset.Type is not (AssetType.Text or AssetType.Json))
            throw new InvalidOperationException("只有文本或 JSON 资源可以使用文本编辑器。");
        if (!CanEditText(asset))
        {
            Log.Warn("拒绝打开文本编辑器：资源「{0}」的正文在 AssetBundle 容器里（SourcePath「{1}」，pathId {2}），读容器字节会卡死界面。",
                asset.LogicalPath ?? "-", asset.SourcePath ?? "-", asset.UnityPathId?.ToString() ?? "-");
            throw new NotSupportedException(BundleAssetHint);
        }
        var bytes = await edits.ReadCurrentBytesAsync(project, assetId, cancellationToken);
        // 形状校验：宁可拒绝，也不要把二进制塞进 WPF 文本框（实测 26.5 秒卡死）。
        if (LooksLikeBinary(bytes))
        {
            Log.Warn("拒绝打开文本编辑器：资源「{0}」读到的 {1:N0} 字节看起来是二进制（首 8 字节 {2}）。",
                asset.LogicalPath ?? "-", bytes.Length, HeadHex(bytes, 8));
            throw new NotSupportedException(
                $"读到的内容不是文本（{bytes.Length:N0} 字节，疑似二进制/容器）。"
                + "可以改用预览、十六进制预览，或用「替换选中资源…」换掉整个文件。");
        }
        var encoding = DetectEncoding(bytes);
        var text = encoding.GetString(StripBom(bytes, encoding));
        if (text.Length > MaxEditableChars)
        {
            Log.Warn("拒绝打开文本编辑器：资源「{0}」正文 {1:N0} 字符，超过上限 {2:N0} 字符。",
                asset.LogicalPath ?? "-", text.Length, MaxEditableChars);
            throw new NotSupportedException(
                $"正文 {text.Length:N0} 字符，超过内置编辑器上限 {MaxEditableChars:N0} 字符。"
                + "请用外部编辑器改好文件，再用「替换选中资源…」登记。");
        }
        var logicalPath = asset.LogicalPath ?? string.Empty;
        var isJson = asset.Type == AssetType.Json
            || Path.GetExtension(logicalPath).Equals(".json", StringComparison.OrdinalIgnoreCase);
        Log.Info("文本编辑器正文就绪：资源={0}，{1:N0} 字节 → {2:N0} 字符，编码={3}，形态={4}",
            logicalPath.Length > 0 ? logicalPath : "-", bytes.Length, text.Length, encoding.WebName,
            isJson ? "JSON" : "文本");
        return new(assetId, logicalPath, text, encoding, isJson);
    }

    public async Task<AssetReplacementResult> SaveAsync(ModProject project, TextAssetDocument document, string projectDirectory, bool formatJson = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        // 与 OpenAsync 同一道上限：缩进重排（JsonDocument.Parse + WriteIndented）
        // 对超大正文是秒级~分钟级的 UI 线程冻结，必须在进来之前拦住。
        if (document.Text.Length > MaxEditableChars)
            throw new NotSupportedException(
                $"正文 {document.Text.Length:N0} 字符，超过内置编辑器上限 {MaxEditableChars:N0} 字符，已拒绝保存。");
        var text = formatJson || document.IsJson ? FormatJson(document.Text) : document.Text;
        var bytes = document.Encoding.GetBytes(text);
        return await edits.ReplaceFromBytesAsync(project, document.AssetId, bytes, Path.GetExtension(document.LogicalPath), projectDirectory, cancellationToken);
    }

    /// <summary>看起来不是文本：UnityFS 容器魔数，或开头就有 NUL 字节。
    /// 这是「容器字节被当正文」的第二道防线（第一道是 <see cref="CanEditText"/>）。</summary>
    private static bool LooksLikeBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 7 && bytes[..7].SequenceEqual("UnityFS"u8)) return true;
        var limit = Math.Min(bytes.Length, 8192);
        return bytes[..limit].Contains((byte)0);
    }

    private static string HeadHex(ReadOnlySpan<byte> bytes, int count)
        => Convert.ToHexString(bytes[..Math.Min(bytes.Length, count)]);

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
