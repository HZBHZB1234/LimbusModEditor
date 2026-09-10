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

    /// <summary>展平的尾部变体：默认深一点、行数上限低一点（专用预览的字段树尾巴）。</summary>
    public static List<AssetPreviewRow> FlattenTail(UnityFieldNode node, int maxRows = 200)
        => Flatten(node, maxDepth: 6, maxRows: maxRows);

    /// <summary>递归查找字段节点（按名字，大小写不敏感，首个命中）。</summary>
    public static UnityFieldNode? FindNode(UnityFieldNode node, params string[] names)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        UnityFieldNode? Walk(UnityFieldNode current, int depth)
        {
            if (depth > 24) return null;
            if (wanted.Contains(current.Name)) return current;
            foreach (var child in current.Children)
            {
                var found = Walk(child, depth + 1);
                if (found is not null) return found;
            }
            return null;
        }
        return Walk(node, 0);
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

/// <summary>Material（class 21）只读预览：名称、引用的 Shader、属性表统计与字段树。</summary>
public sealed class MaterialPreviewProvider : IAssetPreviewProvider
{
    public string Name => "材质预览";

    public bool CanPreview(AssetRecord asset) => asset.Type == AssetType.Material;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            if (!PreviewRead.IsBundleAsset(asset)) return null;
            var service = new UnityAssetService();
            var pathId = asset.UnityPathId!.Value;
            var fields = service.ReadBundleObjectFields(asset.SourcePath!, asset.ContainerPath!, pathId, cancellationToken);
            if (fields.Count == 0) return null;
            var root = fields[0];
            var rows = new List<AssetPreviewRow>();
            var name = PreviewRead.FindNode(root, "m_Name", "name")?.Value;
            if (!string.IsNullOrWhiteSpace(name)) rows.Add(new AssetPreviewRow("名称", name, 0, Highlight: true));
            rows.Add(new AssetPreviewRow("着色器", DescribeShader(service, asset, root, cancellationToken), 0));
            var properties = PreviewRead.FindNode(root, "m_SavedProperties", "m_Properties", "properties");
            if (properties is not null)
            {
                foreach (var (label, childName) in new[] { ("颜色", "m_Colors"), ("浮点", "m_Floats"), ("纹理", "m_TexEnvs"), ("向量", "m_Vectors") })
                {
                    var child = PreviewRead.FindNode(properties, childName);
                    if (child is { IsArray: true }) rows.Add(new AssetPreviewRow(label, $"{child.ArraySize:N0} 项", 0));
                }
            }
            rows.AddRange(PreviewRead.Flatten(root, maxDepth: 3, maxRows: 200));
            return new AssetPreview(AssetPreviewKind.Rows,
                $"材质（Material）· Path {pathId}（只读字段树）", Rows: rows);
        }, cancellationToken);

    /// <summary>材质引用的 Shader：同文件 PPtr 解析出着色器对象名，其余给出引用坐标。</summary>
    private static string DescribeShader(UnityAssetService service, AssetRecord asset,
        UnityFieldNode root, CancellationToken cancellationToken)
    {
        var shader = PreviewRead.FindNode(root, "m_Shader", "shader");
        if (shader is null) return "（字段树未包含 m_Shader）";
        if (!shader.IsPPtr) return shader.Value ?? "—";
        if (shader.PPtrFileId != 0)
            return $"外部文件 {shader.PPtrFileId} / Path {shader.PPtrPathId}";
        if (shader.PPtrPathId == 0) return "空引用";
        try
        {
            var shaderFields = service.ReadBundleObjectFields(
                asset.SourcePath!, asset.ContainerPath!, shader.PPtrPathId, cancellationToken);
            if (shaderFields.Count > 0)
            {
                var shaderName = PreviewRead.FindNode(shaderFields[0], "m_Name", "name")?.Value;
                if (!string.IsNullOrWhiteSpace(shaderName)) return $"{shaderName}（Path {shader.PPtrPathId}）";
            }
        }
        catch (Exception) { /* 着色器对象不可读时退回坐标显示 */ }
        return $"Path {shader.PPtrPathId}";
    }
}

/// <summary>Shader（class 48）只读预览：名称 + 字段树（.shadergraph 等）。</summary>
public sealed class ShaderPreviewProvider : IAssetPreviewProvider
{
    public string Name => "着色器预览";

    public bool CanPreview(AssetRecord asset) => asset.Type == AssetType.Shader;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            if (!PreviewRead.IsBundleAsset(asset)) return null;
            var service = new UnityAssetService();
            var pathId = asset.UnityPathId!.Value;
            var fields = service.ReadBundleObjectFields(asset.SourcePath!, asset.ContainerPath!, pathId, cancellationToken);
            if (fields.Count == 0) return null;
            var rows = new List<AssetPreviewRow>();
            var name = PreviewRead.FindNode(fields[0], "m_Name", "name")?.Value;
            if (!string.IsNullOrWhiteSpace(name)) rows.Add(new AssetPreviewRow("名称", name, 0, Highlight: true));
            rows.AddRange(PreviewRead.FlattenTail(fields[0], 200));
            return new AssetPreview(AssetPreviewKind.Rows,
                $"着色器（Shader）· Path {pathId}（只读字段树）", Rows: rows);
        }, cancellationToken);
}

/// <summary>VideoClip（class 329）只读预览：分辨率 / 时长 / 帧数 / 编码与负载大小。
/// 视频字节可能位于内联数组或 .resS 流；本提供者只读元数据，不尝试解码播放。</summary>
public sealed class VideoClipPreviewProvider : IAssetPreviewProvider
{
    public string Name => "视频元数据预览";

    public bool CanPreview(AssetRecord asset) => asset.Type == AssetType.Video;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            if (!PreviewRead.IsBundleAsset(asset)) return null;
            var service = new UnityAssetService();
            var pathId = asset.UnityPathId!.Value;
            var fields = service.ReadBundleObjectFields(asset.SourcePath!, asset.ContainerPath!, pathId, cancellationToken);
            if (fields.Count == 0) return null;
            var root = fields[0];
            var rows = new List<AssetPreviewRow>();
            var name = PreviewRead.FindNode(root, "m_Name", "name")?.Value;
            if (!string.IsNullOrWhiteSpace(name)) rows.Add(new AssetPreviewRow("名称", name, 0, Highlight: true));
            AddIfFound(rows, root, "分辨率", "m_OriginalWidth", "m_Width", "width");
            var height = PreviewRead.FindNode(root, "m_OriginalHeight", "m_Height", "height");
            if (height is not null)
            {
                var last = rows[^1];
                rows[^1] = new AssetPreviewRow(last.Label, $"{last.Value}×{height.Value}", last.Depth, last.Highlight);
            }
            AddIfFound(rows, root, "时长", "m_Length", "length");
            AddIfFound(rows, root, "帧数", "m_Frames", "frames");
            AddIfFound(rows, root, "帧率", "m_FrameRate", "frameRate");
            var format = PreviewRead.FindNode(root, "m_Format", "format", "m_EncodeTarget");
            if (format?.Value is { Length: > 0 })
                rows.Add(new AssetPreviewRow("编码", VideoCodecName(format.Value), 0));
            AddIfFound(rows, root, "质量", "m_Quality", "quality");
            rows.Add(new AssetPreviewRow("负载大小", DescribePayload(root), 0));
            rows.Add(new AssetPreviewRow("提示", "只读元数据预览；播放/导出需要视频解码器（可后续引入 FFmpeg）。", 0, Highlight: true));
            rows.AddRange(PreviewRead.FlattenTail(root, 150));
            return new AssetPreview(AssetPreviewKind.Rows,
                $"视频（VideoClip）· Path {pathId} · 只读元数据", Rows: rows);
        }, cancellationToken);

    private static void AddIfFound(List<AssetPreviewRow> rows, UnityFieldNode root, string label, params string[] names)
    {
        var node = PreviewRead.FindNode(root, names);
        if (node?.Value is { Length: > 0 } value) rows.Add(new AssetPreviewRow(label, value, 0));
    }

    /// <summary>Unity VideoClip.m_Format / m_EncodeTarget 的常见取值 → 编码名。</summary>
    private static string VideoCodecName(string raw)
    {
        return raw.Trim() switch
        {
            "0" => "VP8",
            "1" => "VP9",
            "2" => "H.264",
            "3" => "H.265/HEVC",
            "4" => "AV1",
            _ => $"格式 {raw}"
        };
    }

    /// <summary>视频负载字节：内联 m_ClipData / m_VideoData，或 StreamingInfo 形态
    /// （path/offset/size）的流大小；定位失败给出明确说明。</summary>
    private static string DescribePayload(UnityFieldNode root)
    {
        foreach (var dataName in new[] { "m_ClipData", "m_VideoData", "m_Data", "data" })
        {
            var node = PreviewRead.FindNode(root, dataName);
            if (node?.ByteArrayLength > 0) return $"{node.ByteArrayLength:N0} 字节（内联）";
        }
        var stream = PreviewRead.FindNode(root, "m_ExternalResources", "m_Resource", "m_ResourceData", "m_StreamData");
        if (stream is not null)
        {
            var path = PreviewRead.FindNode(stream, "path", "m_Source", "source");
            var bytes = PreviewRead.FindNode(stream, "m_Size", "size");
            if (bytes?.Value is { Length: > 0 })
                return $"{bytes.Value:N0} 字节（流 {path?.Value ?? "未知路径"}）";
        }
        return "（未定位到视频负载字段）";
    }
}

/// <summary>SpriteAtlas（ref-type 687078895）只读预览：名称、打包精灵数、字段树。</summary>
public sealed class SpriteAtlasPreviewProvider : IAssetPreviewProvider
{
    public string Name => "图集预览";

    public bool CanPreview(AssetRecord asset) => asset.Type == AssetType.SpriteAtlas;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            if (!PreviewRead.IsBundleAsset(asset)) return null;
            var service = new UnityAssetService();
            var pathId = asset.UnityPathId!.Value;
            var fields = service.ReadBundleObjectFields(asset.SourcePath!, asset.ContainerPath!, pathId, cancellationToken);
            if (fields.Count == 0) return null;
            var root = fields[0];
            var rows = new List<AssetPreviewRow>();
            var name = PreviewRead.FindNode(root, "m_Name", "name")?.Value;
            if (!string.IsNullOrWhiteSpace(name)) rows.Add(new AssetPreviewRow("名称", name, 0, Highlight: true));
            var packed = PreviewRead.FindNode(root, "m_PackedSprites", "m_PackedSpritesData");
            if (packed is { IsArray: true }) rows.Add(new AssetPreviewRow("打包精灵", $"{packed.ArraySize:N0} 个", 0));
            var packables = PreviewRead.FindNode(root, "m_Packables", "m_PackablesData");
            if (packables is { IsArray: true }) rows.Add(new AssetPreviewRow("可打包对象", $"{packables.ArraySize:N0} 个", 0));
            rows.AddRange(PreviewRead.FlattenTail(root, 200));
            return new AssetPreview(AssetPreviewKind.Rows,
                $"图集（SpriteAtlas）· Path {pathId} · 只读字段树", Rows: rows);
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
