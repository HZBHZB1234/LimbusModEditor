using System.Text.Json;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Editing.Images;
using LimbusModEditor.Formats.Bank;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Application.Assets.Preview;

/// <summary>提供者共用的读取工具（只读，失败返回 null 交下一个提供者）。</summary>
internal static class PreviewRead
{
    public static bool IsBundleAsset(AssetRecord asset)
        => asset.Metadata.GetValueOrDefault("unityBundle") == "true"
           && asset.UnityPathId is not null
           && !string.IsNullOrWhiteSpace(asset.SourcePath)
           && File.Exists(asset.SourcePath);

    public static bool TryGetReplacement(AssetRecord asset, out string path)
    {
        path = string.Empty;
        if (!asset.Metadata.TryGetValue("replacementPath", out var candidate) || string.IsNullOrWhiteSpace(candidate)) return false;
        if (!File.Exists(candidate)) return false;
        path = candidate;
        return true;
    }

    public static string? ReadableFile(AssetRecord asset)
    {
        if (TryGetReplacement(asset, out var replacement)) return replacement;
        return !string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath) ? asset.SourcePath : null;
    }

    /// <summary>把字段树展平为可显示的行（限制深度与数量，防大对象卡 UI）。</summary>
    public static List<AssetPreviewRow> Flatten(UnityFieldNode node, int maxDepth = 4, int maxRows = 400)
    {
        var rows = new List<AssetPreviewRow>();
        void Walk(UnityFieldNode current, int depth)
        {
            if (rows.Count >= maxRows) return;
            var value = current.Value;
            if (current.ByteArrayLength > 0) value = $"{current.ByteArrayLength:N0} 字节";
            else if (current.IsArray) value = $"{current.ArraySize:N0} 项";
            else if (current.IsPPtr && string.IsNullOrEmpty(value)) value = $"File {current.PPtrFileId} / Path {current.PPtrPathId}";
            rows.Add(new AssetPreviewRow(current.Name, value ?? string.Empty, depth));
            if (depth >= maxDepth) return;
            foreach (var child in current.Children) Walk(child, depth + 1);
        }
        Walk(node, 0);
        if (rows.Count >= maxRows) rows.Add(new AssetPreviewRow("…", $"字段过多，只显示前 {maxRows} 行（完整字段树见「Unity 字段编辑」）", 0));
        return rows;
    }
}

/// <summary>Texture2D 预览：解码 PNG + 尺寸/格式信息；有替换时主图显示替换图、
/// 备选图显示缓存原图（plan-05「原图 ↔ 替换图」切换）。</summary>
public sealed class TexturePreviewProvider : IAssetPreviewProvider
{
    public string Name => "纹理预览";

    public bool CanPreview(AssetRecord asset) => asset.Type == AssetType.Texture;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            var hasReplacement = PreviewRead.TryGetReplacement(asset, out var replacement);
            byte[]? bundlePng = null;
            string? info = null;
            if (PreviewRead.IsBundleAsset(asset))
            {
                try
                {
                    var service = new UnityAssetService();
                    bundlePng = service.ReadTexturePng(asset.SourcePath!, asset.UnityPathId!.Value, cancellationToken);
                    if (bundlePng is not null)
                        info = service.ReadTextureSummary(asset.SourcePath!, asset.UnityPathId.Value, cancellationToken)?.Describe();
                }
                catch (Exception ex) { info = $"纹理读取失败：{ex.Message}"; }
            }

            // 替换优先：与既有行为一致（替换后预览显示替换后的内容）。
            if (hasReplacement && ImagePreviewService.IsSupportedExtension(Path.GetExtension(replacement)))
            {
                try
                {
                    var preview = new ImagePreviewService().CreatePreviewFromFile(replacement);
                    return new AssetPreview(AssetPreviewKind.Image,
                        $"{preview.Width} × {preview.Height} · {preview.Format}（替换后）" + (info is null ? string.Empty : $" · 原图 {info}"),
                        ImagePng: preview.ThumbnailPng, AlternateImagePng: bundlePng, AlternateLabel: "原图（缓存）");
                }
                catch (Exception ex)
                {
                    if (bundlePng is null) return new AssetPreview(AssetPreviewKind.Message, $"替换图预览失败：{ex.Message}");
                }
            }

            if (bundlePng is not null)
                return new AssetPreview(AssetPreviewKind.Image, info ?? "Unity Texture2D 预览", ImagePng: bundlePng);

            // 导入的独立图片文件（非 bundle）。
            var file = PreviewRead.ReadableFile(asset);
            if (file is not null && ImagePreviewService.IsSupportedExtension(Path.GetExtension(file)))
            {
                try
                {
                    var preview = new ImagePreviewService().CreatePreviewFromFile(file);
                    return new AssetPreview(AssetPreviewKind.Image, $"{preview.Width} × {preview.Height} · {preview.Format}", ImagePng: preview.ThumbnailPng);
                }
                catch (Exception ex) { return new AssetPreview(AssetPreviewKind.Message, $"预览失败：{ex.Message}"); }
            }
            return null; // 交给后续提供者（十六进制兜底）
        }, cancellationToken);
}

/// <summary>Sprite 预览：被引用 Texture2D 解码后按图集裁剪区域合成子图。</summary>
public sealed class SpritePreviewProvider : IAssetPreviewProvider
{
    public string Name => "Sprite 合成预览";

    public bool CanPreview(AssetRecord asset) => asset.Type == AssetType.Sprite;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            byte[]? compositePng = null;
            string? info = null;
            if (PreviewRead.IsBundleAsset(asset))
            {
                try
                {
                    var composite = new UnityAssetService().ReadBundleSpriteComposite(
                        asset.SourcePath!, asset.ContainerPath!, asset.UnityPathId!.Value, cancellationToken);
                    compositePng = composite.Png;
                    info = $"Sprite {composite.Name}：裁剪 {composite.CropRect.Width:0.#}×{composite.CropRect.Height:0.#} @({composite.CropRect.X:0.#},{composite.CropRect.Y:0.#})" +
                           $" · 逻辑 rect {composite.SpriteRect.Width:0.#}×{composite.SpriteRect.Height:0.#}" +
                           $" · 纹理 {composite.TextureWidth}×{composite.TextureHeight}（{composite.TextureName}）" +
                           $" · pivot ({composite.Pivot.X:0.##},{composite.Pivot.Y:0.##})";
                }
                catch (Exception ex) { info = $"Sprite 合成失败：{ex.Message}"; }
            }

            if (PreviewRead.TryGetReplacement(asset, out var replacement) &&
                ImagePreviewService.IsSupportedExtension(Path.GetExtension(replacement)))
            {
                try
                {
                    var preview = new ImagePreviewService().CreatePreviewFromFile(replacement);
                    return new AssetPreview(AssetPreviewKind.Image,
                        $"{preview.Width} × {preview.Height} · {preview.Format}（替换后）" + (info is null ? string.Empty : $" · {info}"),
                        ImagePng: preview.ThumbnailPng, AlternateImagePng: compositePng, AlternateLabel: "原图（图集裁剪）");
                }
                catch (Exception ex)
                {
                    if (compositePng is null) return new AssetPreview(AssetPreviewKind.Message, $"替换图预览失败：{ex.Message}");
                }
            }

            if (compositePng is not null) return new AssetPreview(AssetPreviewKind.Image, info ?? "Sprite 预览", ImagePng: compositePng);
            if (info is not null) return new AssetPreview(AssetPreviewKind.Message, info);
            return null;
        }, cancellationToken);
}

/// <summary>
/// 音频预览：Bank FSB（项目内 fsb/ 资源）与 Unity AudioClip 两条通道。
/// 有 FMOD DLL 时解码为 WAV（试听 + 波形）；缺 DLL 时给出具体原因并保留
/// FSB 结构信息（不装作能播）。
/// </summary>
public sealed class AudioPreviewProvider : IAssetPreviewProvider
{
    private readonly Func<string?> _fmodDirectory;

    public AudioPreviewProvider(Func<string?>? fmodDirectory = null)
        => _fmodDirectory = fmodDirectory ?? (() => null);

    public string Name => "音频预览";

    public bool CanPreview(AssetRecord asset) => asset.Type == AssetType.Audio;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            var isBankFsb = asset.LogicalPath.StartsWith("fsb/", StringComparison.OrdinalIgnoreCase);
            byte[]? fsb = null;
            string sourceLabel;
            if (isBankFsb)
            {
                try { fsb = new BankAudioService().ReadFsbAsync(asset, cancellationToken).GetAwaiter().GetResult(); }
                catch (Exception ex) { return new AssetPreview(AssetPreviewKind.Message, $"读取 Bank 音频失败：{ex.Message}"); }
                sourceLabel = "Bank FSB";
            }
            else if (PreviewRead.IsBundleAsset(asset))
            {
                try
                {
                    var clip = new UnityAssetService().ReadBundleAudioClipData(
                        asset.SourcePath!, asset.ContainerPath!, asset.UnityPathId!.Value, cancellationToken);
                    fsb = clip.Data;
                    sourceLabel = $"Unity AudioClip（{clip.Name}，format={clip.Format}，{clip.Channels} 声道，{clip.Frequency} Hz）";
                }
                catch (Exception ex) { return new AssetPreview(AssetPreviewKind.Message, $"读取 Unity 音频负载失败：{ex.Message}"); }
            }
            else
            {
                return null;
            }

            var structure = Fsb5Parser.TryParse(fsb);
            var structureInfo = structure is null
                ? "负载不是可解析的 FSB5"
                : $"FSB5 codec={structure.CodecName} 样本 {structure.SampleCount} 个";

            var fmodDirectory = _fmodDirectory();
            if (string.IsNullOrWhiteSpace(fmodDirectory) || !Directory.Exists(fmodDirectory))
            {
                return new AssetPreview(AssetPreviewKind.Audio,
                    $"{sourceLabel} · {structureInfo}",
                    Audio: new AssetPreviewAudio(fsb, null,
                        "需要 FMOD DLL 才能解码试听与绘制波形：把 fmod64.dll / fsbank64.dll 放进程序目录 fmod\\ 或在设置页指定目录。",
                        structure?.Samples.FirstOrDefault()?.SampleRate ?? 0,
                        structure?.Samples.FirstOrDefault()?.Channels ?? 0,
                        0, []));
            }

            try
            {
                using var codec = new NativeFmodAudioCodec(fmodDirectory);
                var wave = codec.DecodeFsbToWaveAsync(fsb, cancellationToken).GetAwaiter().GetResult();
                var envelope = AudioWaveform.BuildEnvelope(wave);
                var duration = envelope.DurationSeconds > 0 ? envelope.DurationSeconds
                    : structure?.Samples.Sum(s => s.SampleRate > 0 ? (double)s.SampleCount / s.SampleRate : 0) ?? 0;
                var info = $"{sourceLabel} · {structureInfo} · WAV {wave.Length / 1024} KB";
                if (duration > 0) info += $" · 约 {duration:0.##} 秒";
                return new AssetPreview(AssetPreviewKind.Audio, info,
                    Audio: new AssetPreviewAudio(fsb, wave, null,
                        envelope.SampleRate, envelope.Channels, duration, envelope.Envelope));
            }
            catch (Exception ex)
            {
                return new AssetPreview(AssetPreviewKind.Audio,
                    $"{sourceLabel} · {structureInfo}",
                    Audio: new AssetPreviewAudio(fsb, null, $"解码失败：{ex.Message}",
                        structure?.Samples.FirstOrDefault()?.SampleRate ?? 0,
                        structure?.Samples.FirstOrDefault()?.Channels ?? 0, 0, []));
            }
        }, cancellationToken);
}

/// <summary>文本 / JSON 预览：bundle TextAsset 走类型树读取，独立文件走
/// TextPreviewService；JSON 可解析时给出树视图标记（UI 侧切换）。</summary>
public sealed class TextPreviewProvider : IAssetPreviewProvider
{
    public string Name => "文本预览";

    public bool CanPreview(AssetRecord asset)
        => asset.Type is AssetType.Text or AssetType.Json || TextPreviewService.LooksTextual(asset);

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            if (PreviewRead.IsBundleAsset(asset) && asset.Type is AssetType.Text or AssetType.Json)
            {
                try
                {
                    var textAsset = new UnityAssetService().ReadBundleTextAsset(
                        asset.SourcePath!, asset.ContainerPath!, asset.UnityPathId!.Value, cancellationToken);
                    var decoded = textAsset.TryDecodeUtf8();
                    if (decoded is null)
                        return new AssetPreview(AssetPreviewKind.Message,
                            $"TextAsset {textAsset.Name} 不是可读文本（{textAsset.Data.Length:N0} 字节，非 UTF-8）。",
                            "可用「十六进制预览」查看原始字节。");
                    // 静态表可能达 MB 级：截断到 20 万字符保护 UI（信息行注明）。
                    const int maxChars = 200_000;
                    var truncated = decoded.Length > maxChars;
                    var shown = truncated ? decoded[..maxChars] : decoded;
                    var kind = IsJson(shown) ? AssetPreviewKind.Json : AssetPreviewKind.Text;
                    return new AssetPreview(kind,
                        $"TextAsset {textAsset.Name} · UTF-8 · {decoded.Length:N0} 字符 / {textAsset.Data.Length:N0} 字节" +
                        (truncated ? $" · 仅显示前 {maxChars:N0} 字符" : string.Empty),
                        Text: shown);
                }
                catch (Exception ex) { return new AssetPreview(AssetPreviewKind.Message, $"TextAsset 读取失败：{ex.Message}"); }
            }

            var preview = TextPreviewService.TryPreview(asset);
            if (preview is null)
                return new AssetPreview(AssetPreviewKind.Message,
                    "这个文件不是可读文本（非 UTF-8 / UTF-16，或含二进制内容）。",
                    "可用「十六进制预览」查看原始字节。");
            var textKind = IsJson(preview.Text) ? AssetPreviewKind.Json : AssetPreviewKind.Text;
            return new AssetPreview(textKind,
                $"{preview.EncodingName} · {preview.TotalBytes:N0} 字节" + (preview.Truncated ? " · 仅显示开头部分" : string.Empty),
                Text: preview.Text);
        }, cancellationToken);

    private static bool IsJson(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return false;
        try { using var _ = JsonDocument.Parse(text); return true; }
        catch (JsonException) { return false; }
    }
}

/// <summary>MonoBehaviour / MonoScript：只读字段树 + 脚本来源信息。</summary>
public sealed class ScriptPreviewProvider : IAssetPreviewProvider
{
    public string Name => "脚本字段树";

    public bool CanPreview(AssetRecord asset)
        => asset.Type is AssetType.MonoBehaviour or AssetType.MonoScript && PreviewRead.IsBundleAsset(asset);

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            var service = new UnityAssetService();
            var pathId = asset.UnityPathId!.Value;
            var fields = service.ReadBundleObjectFields(asset.SourcePath!, asset.ContainerPath!, pathId, cancellationToken);
            if (fields.Count == 0) return null;
            var rows = new List<AssetPreviewRow>();
            try
            {
                var script = service.ReadBundleObjectScriptInfo(asset.SourcePath!, asset.ContainerPath!, pathId, cancellationToken);
                if (script is not null)
                {
                    var className = string.IsNullOrWhiteSpace(script.Namespace) ? script.ClassName : $"{script.Namespace}.{script.ClassName}";
                    rows.Add(new AssetPreviewRow("脚本类", className ?? "（不可解析）", 0, Highlight: true));
                    if (!string.IsNullOrWhiteSpace(script.AssemblyName))
                        rows.Add(new AssetPreviewRow("程序集", script.AssemblyName, 0));
                    if (script.ExternalGuid is not null) rows.Add(new AssetPreviewRow("外部 GUID", script.ExternalGuid, 0));
                    if (script.TypeTreeMissingReason is not null)
                        rows.Add(new AssetPreviewRow("类型树", script.TypeTreeMissingReason, 0, Highlight: true));
                }
            }
            catch (Exception) { /* 脚本信息尽力而为 */ }
            rows.AddRange(PreviewRead.Flatten(fields[0]));
            return new AssetPreview(AssetPreviewKind.Rows,
                $"只读字段树 · {AssetDisplay.TypeLabel(asset.Type)} · Path {pathId}（编辑请用「Unity 字段编辑」）",
                Rows: rows);
        }, cancellationToken);
}

/// <summary>Mesh / AnimationClip / Font：结构摘要卡。</summary>
public sealed class SummaryPreviewProvider : IAssetPreviewProvider
{
    public string Name => "对象摘要";

    public bool CanPreview(AssetRecord asset)
        => asset.Type is AssetType.Mesh or AssetType.Animation or AssetType.Font;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            if (!PreviewRead.IsBundleAsset(asset)) return null;
            var service = new UnityAssetService();
            var summary = service.ReadBundleObjectSummary(
                asset.SourcePath!, asset.ContainerPath!, asset.UnityPathId!.Value, cancellationToken);
            if (summary is null) return null;
            var rows = summary.Fields.Select(f => new AssetPreviewRow(f.Label, f.Value)).ToList();
            rows.AddRange(summary.Notes.Select(n => new AssetPreviewRow("⚠", n, 0, Highlight: true)));
            return new AssetPreview(AssetPreviewKind.Rows,
                $"{summary.TypeName}（Path {summary.PathId}）{summary.ObjectName ?? string.Empty} · 只读摘要",
                Rows: rows);
        }, cancellationToken);
}

/// <summary>十六进制兜底：任何能读到文件字节的资源都有预览（plan-05：
/// 未知类型不空白）。</summary>
public sealed class HexPreviewProvider : IAssetPreviewProvider
{
    public string Name => "十六进制兜底";

    public bool CanPreview(AssetRecord asset) => PreviewRead.ReadableFile(asset) is not null;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            var file = PreviewRead.ReadableFile(asset);
            if (file is null) return null;
            var dump = HexDumpService.DumpFile(file);
            if (dump is null) return null;
            var explanation = $"没有专用预览形态（{AssetDisplay.TypeLabel(asset.Type)}），显示十六进制转储。";
            return new AssetPreview(AssetPreviewKind.Hex,
                $"{explanation} {dump.Describe()}",
                Text: dump.Text);
        }, cancellationToken);
}
