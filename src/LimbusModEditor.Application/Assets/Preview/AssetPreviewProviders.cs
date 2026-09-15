using System.Text.Json;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Editing.Images;
using LimbusModEditor.Formats.Bank;
using LimbusModEditor.Formats.Unity;
using NLog;

namespace LimbusModEditor.Application.Assets.Preview;

/// <summary>提供者共用的读取工具（只读，失败返回 null 交下一个提供者）。</summary>
internal static class PreviewRead
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static bool IsBundleAsset(AssetRecord asset)
    {
        var result = asset.Metadata.GetValueOrDefault("unityBundle") == "true"
           && asset.UnityPathId is not null
           && !string.IsNullOrWhiteSpace(asset.SourcePath)
           && File.Exists(asset.SourcePath);
        if (Log.IsTraceEnabled)
            Log.Trace("bundle 判定：元数据 unityBundle={0}，PathId={1}，文件「{2}」存在={3} → 结论：{4}",
                asset.Metadata.GetValueOrDefault("unityBundle") ?? "-",
                asset.UnityPathId?.ToString() ?? "-",
                asset.SourcePath ?? "-",
                asset.SourcePath is { Length: > 0 } && File.Exists(asset.SourcePath),
                result ? "是 bundle 资源" : "不是 bundle 资源");
        return result;
    }

    public static bool TryGetReplacement(AssetRecord asset, out string path)
    {
        path = string.Empty;
        if (!asset.Metadata.TryGetValue("replacementPath", out var candidate) || string.IsNullOrWhiteSpace(candidate))
        {
            if (Log.IsTraceEnabled)
                Log.Trace("替换文件判定：资源「{0}」没有 replacementPath 元数据 → 无替换。", asset.LogicalPath ?? "-");
            return false;
        }
        if (!File.Exists(candidate))
        {
            Log.Debug("替换文件判定：元数据 replacementPath「{0}」指向的文件不存在（资源「{1}」）→ 按无替换处理。",
                candidate, asset.LogicalPath ?? "-");
            return false;
        }
        path = candidate;
        if (Log.IsTraceEnabled)
            Log.Trace("替换文件判定：资源「{0}」使用替换文件「{1}」。", asset.LogicalPath ?? "-", candidate);
        return true;
    }

    public static string? ReadableFile(AssetRecord asset)
    {
        if (TryGetReplacement(asset, out var replacement)) return replacement;
        var readable = !string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath) ? asset.SourcePath : null;
        if (readable is null && Log.IsDebugEnabled)
            Log.Debug("可读文件判定：资源「{0}」的 SourcePath「{1}」不存在或为空 → 交给后续 provider。",
                asset.LogicalPath ?? "-", asset.SourcePath ?? "-");
        return readable;
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
        var truncated = rows.Count >= maxRows;
        if (truncated) rows.Add(new AssetPreviewRow("…", $"字段过多，只显示前 {maxRows} 行（完整字段树见「Unity 字段编辑」）", 0));
        // 字段树节点可能是十万级：只在 Trace 打开时才拼这条消息。
        if (Log.IsTraceEnabled)
            Log.Trace("字段树展平：根「{0}」→ {1} 行（maxDepth={2}，maxRows={3}，被截断={4}）。",
                node.Name ?? "-", rows.Count, maxDepth, maxRows, truncated);
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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public string Name => "纹理预览";

    public bool CanPreview(AssetRecord asset) => asset.Type == AssetType.Texture;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            using var scope = Log.Scope("纹理预览");
            var hasReplacement = PreviewRead.TryGetReplacement(asset, out var replacement);
            Log.Debug("纹理预览：资源「{0}」，替换文件={1}，bundle 资源={2}。",
                asset.LogicalPath ?? "-", replacement ?? "-", PreviewRead.IsBundleAsset(asset));
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
                    Log.Debug("纹理预览：bundle 解码 {0}（PathId {1}），摘要「{2}」。",
                        bundlePng is null ? "无结果" : $"{bundlePng.Length:N0} 字节 PNG",
                        asset.UnityPathId?.ToString() ?? "-", info ?? "-");
                }
                catch (Exception ex)
                {
                    info = $"纹理读取失败：{ex.Message}";
                    Log.Error(ex, "纹理预览失败：bundle {0}，PathId {1}（回退为失败说明卡）",
                        asset.SourcePath ?? "-", asset.UnityPathId?.ToString() ?? "-");
                }
            }

            // 替换优先：与既有行为一致（替换后预览显示替换后的内容）。
            if (hasReplacement && ImagePreviewService.IsSupportedExtension(Path.GetExtension(replacement)))
            {
                try
                {
                    var preview = new ImagePreviewService().CreatePreviewFromFile(replacement!);
                    Log.Info("纹理预览：使用替换图 {0}×{1}（{2}），源「{3}」", preview.Width, preview.Height, preview.Format, replacement ?? "-");
                    return new AssetPreview(AssetPreviewKind.Image,
                        $"{preview.Width} × {preview.Height} · {preview.Format}（替换后）" + (info is null ? string.Empty : $" · 原图 {info}"),
                        ImagePng: preview.ThumbnailPng, AlternateImagePng: bundlePng, AlternateLabel: "原图（缓存）");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "替换图预览失败，改用 bundle 原图或说明卡：{0}", replacement ?? "-");
                    if (bundlePng is null) return new AssetPreview(AssetPreviewKind.Message, $"替换图预览失败：{ex.Message}");
                }
            }

            if (bundlePng is not null)
            {
                Log.Info("纹理预览完成：形态 Image，PNG {0:N0} 字节，来源 bundle。", bundlePng.Length);
                return new AssetPreview(AssetPreviewKind.Image, info ?? "Unity Texture2D 预览", ImagePng: bundlePng);
            }

            // 导入的独立图片文件（非 bundle）。
            var file = PreviewRead.ReadableFile(asset);
            if (file is not null && ImagePreviewService.IsSupportedExtension(Path.GetExtension(file)))
            {
                try
                {
                    var preview = new ImagePreviewService().CreatePreviewFromFile(file);
                    Log.Info("纹理预览完成：形态 Image，独立文件 {0} → {1}×{2}（{3}）。", file, preview.Width, preview.Height, preview.Format);
                    return new AssetPreview(AssetPreviewKind.Image, $"{preview.Width} × {preview.Height} · {preview.Format}", ImagePng: preview.ThumbnailPng);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "独立图片文件预览失败：{0}", file);
                    return new AssetPreview(AssetPreviewKind.Message, $"预览失败：{ex.Message}");
                }
            }
            Log.Debug("纹理预览不处理该资源（无可读文件或不支持的扩展名），交给后续 provider：{0}", asset.LogicalPath ?? "-");
            return null; // 交给后续提供者（十六进制兜底）
        }, cancellationToken);
}

/// <summary>Sprite 预览：被引用 Texture2D 解码后按图集裁剪区域合成子图。</summary>
public sealed class SpritePreviewProvider : IAssetPreviewProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public string Name => "Sprite 合成预览";

    public bool CanPreview(AssetRecord asset) => asset.Type == AssetType.Sprite;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            using var scope = Log.Scope("Sprite 合成预览");
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
                    Log.Debug("Sprite 合成：bundle {0}，PathId {1}，合成 PNG {2}，{3}",
                        asset.SourcePath ?? "-", asset.UnityPathId.Value.ToString(),
                        compositePng is null ? "无" : $"{compositePng.Length:N0} 字节", info ?? "-");
                }
                catch (Exception ex)
                {
                    info = $"Sprite 合成失败：{ex.Message}";
                    Log.Error(ex, "Sprite 合成失败：bundle {0}，容器 {1}，PathId {2}（回退为说明卡）",
                        asset.SourcePath ?? "-", asset.ContainerPath ?? "-", asset.UnityPathId?.ToString() ?? "-");
                }
            }

            if (PreviewRead.TryGetReplacement(asset, out var replacement) &&
                ImagePreviewService.IsSupportedExtension(Path.GetExtension(replacement)))
            {
                try
                {
                    var preview = new ImagePreviewService().CreatePreviewFromFile(replacement!);
                    Log.Info("Sprite 预览：使用替换图 {0}×{1}（{2}），源「{3}」", preview.Width, preview.Height, preview.Format, replacement ?? "-");
                    return new AssetPreview(AssetPreviewKind.Image,
                        $"{preview.Width} × {preview.Height} · {preview.Format}（替换后）" + (info is null ? string.Empty : $" · {info}"),
                        ImagePng: preview.ThumbnailPng, AlternateImagePng: compositePng, AlternateLabel: "原图（图集裁剪）");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Sprite 替换图预览失败，改用图集合成原图或说明卡：{0}", replacement ?? "-");
                    if (compositePng is null) return new AssetPreview(AssetPreviewKind.Message, $"替换图预览失败：{ex.Message}");
                }
            }

            if (compositePng is not null)
            {
                Log.Info("Sprite 预览完成：形态 Image，合成 PNG {0:N0} 字节。", compositePng.Length);
                return new AssetPreview(AssetPreviewKind.Image, info ?? "Sprite 预览", ImagePng: compositePng);
            }
            if (info is not null)
            {
                Log.Warn("Sprite 预览没有图像，只有失败说明（形态 Message）：{0}", info);
                return new AssetPreview(AssetPreviewKind.Message, info);
            }
            Log.Debug("Sprite 预览不处理该资源（既无替换图也无合成结果），交给后续 provider：{0}", asset.LogicalPath ?? "-");
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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Func<string?> _fmodDirectory;

    public AudioPreviewProvider(Func<string?>? fmodDirectory = null)
        => _fmodDirectory = fmodDirectory ?? (() => null);

    public string Name => "音频预览";

    public bool CanPreview(AssetRecord asset) => asset.Type == AssetType.Audio;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            using var scope = Log.Scope("音频预览");
            var isBankFsb = asset.LogicalPath.StartsWith("fsb/", StringComparison.OrdinalIgnoreCase);
            Log.Debug("音频预览：资源「{0}」，Bank FSB 路径={1}，bundle 资源={2}。",
                asset.LogicalPath ?? "-", isBankFsb, PreviewRead.IsBundleAsset(asset));
            byte[]? fsb = null;
            string sourceLabel;
            if (isBankFsb)
            {
                try { fsb = new BankAudioService().ReadFsbAsync(asset, cancellationToken).GetAwaiter().GetResult(); }
                catch (Exception ex)
                {
                    Log.Error(ex, "读取 Bank 音频失败，返回说明卡：资源「{0}」", asset.LogicalPath ?? "-");
                    return new AssetPreview(AssetPreviewKind.Message, $"读取 Bank 音频失败：{ex.Message}");
                }
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
                    Log.Debug("音频预览：Unity AudioClip 负载 {0}，来源「{1}」。",
                        fsb is null ? "无" : $"{fsb.Length:N0} 字节", sourceLabel);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "读取 Unity 音频负载失败，返回说明卡：bundle {0}，PathId {1}",
                        asset.SourcePath ?? "-", asset.UnityPathId?.ToString() ?? "-");
                    return new AssetPreview(AssetPreviewKind.Message, $"读取 Unity 音频负载失败：{ex.Message}");
                }
            }
            else
            {
                Log.Debug("音频预览不处理该资源（既非 fsb/ 路径也非可读 bundle），交给后续 provider：{0}", asset.LogicalPath ?? "-");
                return null;
            }

            var structure = Fsb5Parser.TryParse(fsb);
            var structureInfo = structure is null
                ? "负载不是可解析的 FSB5"
                : $"FSB5 codec={structure.CodecName} 样本 {structure.SampleCount} 个";
            if (structure is null)
                Log.Warn("音频负载不是可解析的 FSB5（负载 {0}），只给出结构说明、不试听：{1}",
                    fsb is null ? "无" : $"{fsb.Length:N0} 字节", asset.LogicalPath ?? "-");

            var fmodDirectory = _fmodDirectory();
            if (string.IsNullOrWhiteSpace(fmodDirectory) || !Directory.Exists(fmodDirectory))
            {
                Log.Warn("音频预览降级为「仅结构说明」：FMOD DLL 目录不可用「{0}」（资源「{1}」，{2}）。",
                    fmodDirectory ?? "-", asset.LogicalPath ?? "-", structureInfo);
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
                Log.Info("音频预览完成：WAV {0:N0} 字节 / {1:0.##} 秒 / {2} 声道，来源「{3}」。",
                    wave.Length, duration, envelope.Channels, sourceLabel);
                return new AssetPreview(AssetPreviewKind.Audio, info,
                    Audio: new AssetPreviewAudio(fsb, wave, null,
                        envelope.SampleRate, envelope.Channels, duration, envelope.Envelope));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "音频解码失败，降级为「仅结构说明」：资源「{0}」，{1}", asset.LogicalPath ?? "-", structureInfo);
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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public string Name => "文本预览";

    /// <summary>
    /// 预览正文的字符上限。
    ///
    /// <para><b>为什么从 20 万降到 2 万</b>：UI 侧要拿这段字符串建控件
    /// （行号 + 正文两个 TextBox，或 JSON 树 + 2000 个 TreeViewItem），
    /// 20 万字符的静态表（实测 1392 张里 1338 张是合法 JSON、最大 3.4 MB）
    /// 会在 **UI 线程**上做整段文本布局 / 上万次控件构造 —— 点一下预览就
    /// 「未响应」。2 万字符足够看清结构，完整正文请用静态数据工作台或
    /// 「文本内容编辑」窗口。</para>
    /// </summary>
    public const int PreviewCharLimit = 20_000;

    public bool CanPreview(AssetRecord asset)
        => asset.Type is AssetType.Text or AssetType.Json || TextPreviewService.LooksTextual(asset);

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            using var scope = Log.Scope("文本预览");
            Log.Debug("文本预览：资源「{0}」，类型 {1}，bundle 资源={2}，内容上限 {3:N0} 字符。",
                AssetDisplay.DisplayPath(asset), AssetDisplay.TypeLabel(asset.Type),
                PreviewRead.IsBundleAsset(asset), PreviewCharLimit);
            if (PreviewRead.IsBundleAsset(asset) && asset.Type is AssetType.Text or AssetType.Json)
            {
                try
                {
                    var textAsset = new UnityAssetService().ReadBundleTextAsset(
                        asset.SourcePath!, asset.ContainerPath!, asset.UnityPathId!.Value, cancellationToken);
                    var decoded = textAsset.TryDecodeUtf8();
                    if (decoded is null)
                    {
                        Log.Warn("TextAsset 不是可读 UTF-8，返回说明卡：{0}（{1:N0} 字节），资源「{2}」",
                            textAsset.Name ?? "-", textAsset.Data.Length, AssetDisplay.DisplayPath(asset));
                        return new AssetPreview(AssetPreviewKind.Message,
                            $"TextAsset {textAsset.Name} 不是可读文本（{textAsset.Data.Length:N0} 字节，非 UTF-8）。",
                            "可用「十六进制预览」查看原始字节。");
                    }
                    var truncated = decoded.Length > PreviewCharLimit;
                    var shown = truncated ? decoded[..PreviewCharLimit] : decoded;
                    Log.Debug("TextAsset 解码：名称「{0}」，{1:N0} 字符 / {2:N0} 字节，是否截断={3}（上限 {4:N0}，实际给出 {5:N0} 字符）。",
                        textAsset.Name ?? "-", decoded.Length, textAsset.Data.Length, truncated, PreviewCharLimit, shown.Length);
                    // 静态数据表（static_s1_0_assets_all_*）本来就是「大表」：这里只给摘要 +
                    // 前若干行，并明确指路静态数据工作台（那里有惰性树 / 差异视图 / 有界正文缓存）。
                    if (IsStaticTableAsset(asset))
                    {
                        var head = HeadText(shown, 40);
                        Log.Info("文本预览走静态数据表分支（形态 Message，不返回正文）：资源「{0}」，正文 {1:N0} 字符 / {2:N0} 字节，摘要 {3:N0} 字符，截断={4}。",
                            AssetDisplay.DisplayPath(asset), decoded.Length, textAsset.Data.Length, head.Length, truncated);
                        return new AssetPreview(AssetPreviewKind.Message,
                            $"静态数据表 {textAsset.Name} · UTF-8 · {decoded.Length:N0} 字符 / {textAsset.Data.Length:N0} 字节" +
                            " · 资源工作台只给开头摘要。",
                            "这是静态数据 bundle 里的表：请到「🧩 静态数据工作台」编辑（树形浏览 / 差异视图 / JSON 编辑器），" +
                            "那里不会把整张表塞进预览。\n\n" + head);
                    }
                    var kind = IsJson(shown) && !truncated ? AssetPreviewKind.Json : AssetPreviewKind.Text;
                    if (truncated)
                        Log.Info("文本预览已按 PreviewCharLimit 截断：原始 {0:N0} 字符 → 只给前 {1:N0} 字符（原始 {2:N0} 字节），形态 {3}，资源「{4}」。",
                            decoded.Length, PreviewCharLimit, textAsset.Data.Length, kind, AssetDisplay.DisplayPath(asset));
                    else
                        Log.Debug("文本预览完成：形态 {0}，正文 {1:N0} 字符，资源「{2}」。", kind, shown.Length, AssetDisplay.DisplayPath(asset));
                    return new AssetPreview(kind,
                        $"TextAsset {textAsset.Name} · UTF-8 · {decoded.Length:N0} 字符 / {textAsset.Data.Length:N0} 字节" +
                        (truncated ? $" · 仅显示前 {PreviewCharLimit:N0} 字符" : string.Empty),
                        Text: shown);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "TextAsset 读取失败，返回说明卡：bundle {0}，容器 {1}，PathId {2}",
                        asset.SourcePath ?? "-", asset.ContainerPath ?? "-", asset.UnityPathId?.ToString() ?? "-");
                    return new AssetPreview(AssetPreviewKind.Message, $"TextAsset 读取失败：{ex.Message}");
                }
            }

            var preview = TextPreviewService.TryPreview(asset);
            if (preview is null)
            {
                Log.Warn("文本预览落空（TryPreview 返回 null），返回说明卡：资源「{0}」，类型 {1}",
                    AssetDisplay.DisplayPath(asset), AssetDisplay.TypeLabel(asset.Type));
                return new AssetPreview(AssetPreviewKind.Message,
                    "这个文件不是可读文本（非 UTF-8 / UTF-16，或含二进制内容）。",
                    "可用「十六进制预览」查看原始字节。");
            }
            var textKind = IsJson(preview.Text) ? AssetPreviewKind.Json : AssetPreviewKind.Text;
            Log.Info("文本预览完成（独立文件）：形态 {0}，编码 {1}，{2:N0} 字节，正文 {3:N0} 字符，内部截断={4}，资源「{5}」。",
                textKind, preview.EncodingName ?? "-", preview.TotalBytes, preview.Text?.Length ?? 0, preview.Truncated,
                AssetDisplay.DisplayPath(asset));
            return new AssetPreview(textKind,
                $"{preview.EncodingName} · {preview.TotalBytes:N0} 字节" + (preview.Truncated ? " · 仅显示开头部分" : string.Empty),
                Text: preview.Text);
        }, cancellationToken);

    /// <summary>是否为静态数据表（扫描期落库的结论 + 两处不依赖索引的兜底）。
    /// <para><b>判据本体不在这里</b>：三条判据（catalog 标记 / bundle 名 / 容器路径）在
    /// <see cref="AssetStaticClassifier"/> 里，扫描期算一次、写进记录元数据与索引列
    /// （<c>assets.static_kind</c>）。这里只**读**结论，不再自己判 —— 早先它与
    /// <c>AssetSearchService</c> 各有一份实现，口径一漂就会出现「列表藏了、预览不认」
    /// 这种只在用户那里才看得见的错位（2026-09 资源列表恢复静态筛选时统一）。</para>
    /// <para>两处兜底仍然保留，因为它们的输入不来自索引：<c>SourcePath</c> 的文件名
    /// （已实体化的本地副本）与 <c>Bundle</c> 字段（离线构造的记录）。</para></summary>
    private static bool IsStaticTableAsset(AssetRecord asset)
    {
        var kind = AssetStaticClassifier.Of(asset);
        if (AssetStaticClassifier.IsStatic(kind))
        {
            Log.Debug("静态表判定（记录结论）：{0}；输入：bundle「{1}」，资源「{2}」",
                AssetStaticClassifier.Describe(kind), asset.Bundle ?? "-", AssetDisplay.DisplayPath(asset));
            return true;
        }
        if (StaticBundleLocator.LooksLikeStaticBundle(asset.Bundle))
        {
            Log.Warn("静态表判定：记录上没有静态结论，由 bundle 名兜底判定为静态数据（bundle「{0}」），资源「{1}」——索引可能缺失/旧版/未回灌。",
                asset.Bundle ?? "-", AssetDisplay.DisplayPath(asset));
            return true;
        }
        var fileName = asset.SourcePath is { Length: > 0 } path ? Path.GetFileName(path) : null;
        if (StaticBundleLocator.LooksLikeStaticBundle(fileName))
        {
            Log.Warn("静态表判定：记录上没有静态结论，由 SourcePath 文件名兜底判定为静态数据（文件名「{0}」，完整路径「{1}」），资源「{2}」。",
                fileName ?? "-", asset.SourcePath ?? "-", AssetDisplay.DisplayPath(asset));
            return true;
        }
        // 第三道判据：容器路径。缓存里旧版本静态 bundle 只剩裸哈希目录名，
        // 前两道判据（catalog 标记 / bundle 名）会同时失效，只能靠这条不依赖 catalog 的
        // 路径事实兜住（真实数据：62d6e466… 旧版本残留）。
        // 正常路径下它在扫描期就已经落成了结论（位4），走到这里说明记录不是从索引来的。
        var containerEntry = AssetDisplay.ContainerEntryPath(asset);
        if (StaticBundleLocator.LooksLikeStaticTablePath(containerEntry))
        {
            Log.Warn("静态表判定：结论与 bundle 名均未命中，由资源路径兜底判定为静态数据（容器路径「{0}」），资源「{1}」——缓存里存在旧版本静态 bundle。",
                containerEntry, AssetDisplay.DisplayPath(asset));
            return true;
        }
        Log.Debug("静态表判定结论：非静态数据（结论为空，bundle 名「{0}」、文件名「{1}」、容器路径「{2}」均不匹配），资源「{3}」。",
            asset.Bundle ?? "-", fileName ?? "-", containerEntry, AssetDisplay.DisplayPath(asset));
        return false;
    }

    /// <summary>取正文开头的若干行（按行截断，避免半个字符 / 半个 JSON 词）。</summary>
    private static string HeadText(string text, int maxLines)
    {
        var lines = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (++lines < maxLines) continue;
            return text[..(i + 1)] + $"\n…（只显示前 {maxLines} 行）";
        }
        return text;
    }

    private static bool IsJson(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            if (Log.IsTraceEnabled)
                Log.Trace("JSON 判定：首字符不是 {{ 或 [（{0:N0} 字符）→ 非 JSON，按纯文本预览。", text.Length);
            return false;
        }
        try { using var _ = JsonDocument.Parse(text); return true; }
        catch (JsonException ex)
        {
            Log.Debug("JSON 判定：解析失败（{0:N0} 字符），按纯文本形态预览（不做 JSON 树）。原因：{1}", text.Length, ex.Message);
            return false;
        }
    }
}

/// <summary>
/// 对象字段树预览：MonoBehaviour / MonoScript / Component / GameObject / ScriptableObject
/// —— 一律走「读 Unity 对象的字段树」这条通道。
///
/// <para><b>为什么把 Component / GameObject 也纳进来</b>：它们原先没有任何提供者接手，
/// 于是一路掉到十六进制兜底——用户看到的是「这个资源加载不出预览」。而字段树读取本来就是
/// 通用的（<c>ReadBundleObjectFields</c> 按对象结构展开，不依赖是不是脚本），
/// Transform / MeshRenderer / Animator 这些组件照样能列出字段与引用，比十六进制有信息量得多。</para>
///
/// <para>脚本类元信息（程序集 / 类型树缺失原因）只有 MonoBehaviour / MonoScript 才有，
/// 读不到就跳过那几行，不影响字段树本身。</para>
/// </summary>
public sealed class ScriptPreviewProvider : IAssetPreviewProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public string Name => "对象字段树";

    public bool CanPreview(AssetRecord asset)
        => (asset.Type is AssetType.MonoBehaviour or AssetType.MonoScript
                or AssetType.Component or AssetType.GameObject or AssetType.ScriptableObject)
           && PreviewRead.IsBundleAsset(asset);

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            using var scope = Log.Scope("对象字段树预览");
            var service = new UnityAssetService();
            var pathId = asset.UnityPathId!.Value;
            var fields = service.ReadBundleObjectFields(asset.SourcePath!, asset.ContainerPath!, pathId, cancellationToken);
            if (fields.Count == 0)
            {
                Log.Debug("对象字段树预览：读不到对象字段（PathId {0}），交给后续 provider：bundle {1}，容器 {2}",
                    pathId, asset.SourcePath ?? "-", asset.ContainerPath ?? "-");
                return null;
            }
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
                Log.Debug("对象字段树预览：脚本信息 {0}，PathId {1}，已加 {2} 行元信息。",
                    script is null ? "不可读" : $"可读（{script.ClassName ?? "-"}）", pathId, rows.Count);
            }
            catch (Exception ex) { /* 脚本信息尽力而为 */
                Log.Warn(ex, "脚本信息读取失败，仅显示字段树（尽力而为分支）：bundle {0}，PathId {1}", asset.SourcePath ?? "-", pathId);
            }
            rows.AddRange(PreviewRead.Flatten(fields[0]));
            Log.Info("对象字段树预览完成：形态 Rows，字段树行 {0} 条，资源「{1}」。", rows.Count, AssetDisplay.DisplayPath(asset));
            return new AssetPreview(AssetPreviewKind.Rows,
                $"只读字段树 · {AssetDisplay.TypeLabel(asset.Type)} · Path {pathId}（编辑请用「Unity 字段编辑」）",
                Rows: rows);
        }, cancellationToken);
}

/// <summary>Mesh / AnimationClip / Font：结构摘要卡。</summary>
public sealed class SummaryPreviewProvider : IAssetPreviewProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public string Name => "对象摘要";

    public bool CanPreview(AssetRecord asset)
        => asset.Type is AssetType.Mesh or AssetType.Animation or AssetType.Font;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            using var scope = Log.Scope("对象摘要预览");
            if (!PreviewRead.IsBundleAsset(asset))
            {
                Log.Debug("对象摘要预览不处理该资源（不是可读 bundle），交给后续 provider：{0}", AssetDisplay.DisplayPath(asset));
                return null;
            }
            var service = new UnityAssetService();
            var summary = service.ReadBundleObjectSummary(
                asset.SourcePath!, asset.ContainerPath!, asset.UnityPathId!.Value, cancellationToken);
            if (summary is null)
            {
                Log.Debug("对象摘要预览：读不到对象摘要（PathId {0}），交给后续 provider：bundle {1}",
                    asset.UnityPathId.Value.ToString(), asset.SourcePath ?? "-");
                return null;
            }
            var rows = summary.Fields.Select(f => new AssetPreviewRow(f.Label, f.Value)).ToList();
            rows.AddRange(summary.Notes.Select(n => new AssetPreviewRow("⚠", n, 0, Highlight: true)));
            Log.Info("对象摘要预览完成：形态 Rows，{0}（Path {1}），行 {2} 条（含 {3} 条提示），资源「{4}」。",
                summary.TypeName ?? "-", summary.PathId, rows.Count, summary.Notes.Count, AssetDisplay.DisplayPath(asset));
            return new AssetPreview(AssetPreviewKind.Rows,
                $"{summary.TypeName}（Path {summary.PathId}）{summary.ObjectName ?? string.Empty} · 只读摘要",
                Rows: rows);
        }, cancellationToken);
}

/// <summary>Material（class 21）只读预览：名称、引用的 Shader、属性表统计与字段树。</summary>
public sealed class MaterialPreviewProvider : IAssetPreviewProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public string Name => "材质预览";

    public bool CanPreview(AssetRecord asset) => asset.Type == AssetType.Material;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            using var scope = Log.Scope("材质预览");
            if (!PreviewRead.IsBundleAsset(asset))
            {
                Log.Debug("材质预览不处理该资源（不是可读 bundle），交给后续 provider：{0}", AssetDisplay.DisplayPath(asset));
                return null;
            }
            var service = new UnityAssetService();
            var pathId = asset.UnityPathId!.Value;
            var fields = service.ReadBundleObjectFields(asset.SourcePath!, asset.ContainerPath!, pathId, cancellationToken);
            if (fields.Count == 0)
            {
                Log.Debug("材质预览：读不到对象字段（PathId {0}），交给后续 provider：bundle {1}", pathId, asset.SourcePath ?? "-");
                return null;
            }
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
            Log.Info("材质预览完成：形态 Rows，Path {0}，名称「{1}」，行 {2} 条，资源「{3}」。",
                pathId, name ?? "-", rows.Count, AssetDisplay.DisplayPath(asset));
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
        catch (Exception ex) { /* 着色器对象不可读时退回坐标显示 */
            Log.Warn(ex, "着色器对象不可读，退回引用坐标显示：bundle {0}，Shader PathId {1}", asset.SourcePath ?? "-", shader.PPtrPathId);
        }
        return $"Path {shader.PPtrPathId}";
    }
}

/// <summary>Shader（class 48）只读预览：名称 + 字段树（.shadergraph 等）。</summary>
public sealed class ShaderPreviewProvider : IAssetPreviewProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public string Name => "着色器预览";

    public bool CanPreview(AssetRecord asset) => asset.Type == AssetType.Shader;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            using var scope = Log.Scope("着色器预览");
            if (!PreviewRead.IsBundleAsset(asset))
            {
                Log.Debug("着色器预览不处理该资源（不是可读 bundle），交给后续 provider：{0}", AssetDisplay.DisplayPath(asset));
                return null;
            }
            var service = new UnityAssetService();
            var pathId = asset.UnityPathId!.Value;
            var fields = service.ReadBundleObjectFields(asset.SourcePath!, asset.ContainerPath!, pathId, cancellationToken);
            if (fields.Count == 0)
            {
                Log.Debug("着色器预览：读不到对象字段（PathId {0}），交给后续 provider：bundle {1}", pathId, asset.SourcePath ?? "-");
                return null;
            }
            var rows = new List<AssetPreviewRow>();
            var name = PreviewRead.FindNode(fields[0], "m_Name", "name")?.Value;
            if (!string.IsNullOrWhiteSpace(name)) rows.Add(new AssetPreviewRow("名称", name, 0, Highlight: true));
            rows.AddRange(PreviewRead.FlattenTail(fields[0], 200));
            Log.Info("着色器预览完成：形态 Rows，Path {0}，名称「{1}」，行 {2} 条，资源「{3}」。",
                pathId, name ?? "-", rows.Count, AssetDisplay.DisplayPath(asset));
            return new AssetPreview(AssetPreviewKind.Rows,
                $"着色器（Shader）· Path {pathId}（只读字段树）", Rows: rows);
        }, cancellationToken);
}

/// <summary>VideoClip（class 329）只读预览：分辨率 / 时长 / 帧数 / 编码与负载大小。
/// 视频字节可能位于内联数组或 .resS 流；本提供者只读元数据，不尝试解码播放。</summary>
public sealed class VideoClipPreviewProvider : IAssetPreviewProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public string Name => "视频元数据预览";

    public bool CanPreview(AssetRecord asset) => asset.Type == AssetType.Video;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            using var scope = Log.Scope("视频元数据预览");
            if (!PreviewRead.IsBundleAsset(asset))
            {
                Log.Debug("视频元数据预览不处理该资源（不是可读 bundle），交给后续 provider：{0}", AssetDisplay.DisplayPath(asset));
                return null;
            }
            var service = new UnityAssetService();
            var pathId = asset.UnityPathId!.Value;
            var fields = service.ReadBundleObjectFields(asset.SourcePath!, asset.ContainerPath!, pathId, cancellationToken);
            if (fields.Count == 0)
            {
                Log.Debug("视频元数据预览：读不到对象字段（PathId {0}），交给后续 provider：bundle {1}", pathId, asset.SourcePath ?? "-");
                return null;
            }
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
            Log.Info("视频元数据预览完成：形态 Rows，Path {0}，名称「{1}」，行 {2} 条，资源「{3}」。",
                pathId, name ?? "-", rows.Count, AssetDisplay.DisplayPath(asset));
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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public string Name => "图集预览";

    public bool CanPreview(AssetRecord asset) => asset.Type == AssetType.SpriteAtlas;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            using var scope = Log.Scope("图集预览");
            if (!PreviewRead.IsBundleAsset(asset))
            {
                Log.Debug("图集预览不处理该资源（不是可读 bundle），交给后续 provider：{0}", AssetDisplay.DisplayPath(asset));
                return null;
            }
            var service = new UnityAssetService();
            var pathId = asset.UnityPathId!.Value;
            var fields = service.ReadBundleObjectFields(asset.SourcePath!, asset.ContainerPath!, pathId, cancellationToken);
            if (fields.Count == 0)
            {
                Log.Debug("图集预览：读不到对象字段（PathId {0}），交给后续 provider：bundle {1}", pathId, asset.SourcePath ?? "-");
                return null;
            }
            var root = fields[0];
            var rows = new List<AssetPreviewRow>();
            var name = PreviewRead.FindNode(root, "m_Name", "name")?.Value;
            if (!string.IsNullOrWhiteSpace(name)) rows.Add(new AssetPreviewRow("名称", name, 0, Highlight: true));
            var packed = PreviewRead.FindNode(root, "m_PackedSprites", "m_PackedSpritesData");
            if (packed is { IsArray: true }) rows.Add(new AssetPreviewRow("打包精灵", $"{packed.ArraySize:N0} 个", 0));
            var packables = PreviewRead.FindNode(root, "m_Packables", "m_PackablesData");
            if (packables is { IsArray: true }) rows.Add(new AssetPreviewRow("可打包对象", $"{packables.ArraySize:N0} 个", 0));
            rows.AddRange(PreviewRead.FlattenTail(root, 200));
            Log.Info("图集预览完成：形态 Rows，Path {0}，名称「{1}」，行 {2} 条，资源「{3}」。",
                pathId, name ?? "-", rows.Count, AssetDisplay.DisplayPath(asset));
            return new AssetPreview(AssetPreviewKind.Rows,
                $"图集（SpriteAtlas）· Path {pathId} · 只读字段树", Rows: rows);
        }, cancellationToken);
}

/// <summary>十六进制兜底：任何能读到文件字节的资源都有预览（plan-05：
/// 未知类型不空白）。</summary>
public sealed class HexPreviewProvider : IAssetPreviewProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public string Name => "十六进制兜底";

    public bool CanPreview(AssetRecord asset) => PreviewRead.ReadableFile(asset) is not null;

    public Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run<AssetPreview?>(() =>
        {
            using var scope = Log.Scope("十六进制兜底预览");
            var file = PreviewRead.ReadableFile(asset);
            if (file is null)
            {
                Log.Debug("十六进制兜底：无可读文件，返回 null（交由注册表给出说明性 Message）：{0}", AssetDisplay.DisplayPath(asset));
                return null;
            }
            var dump = HexDumpService.DumpFile(file);
            if (dump is null)
            {
                Log.Warn("十六进制兜底：DumpFile 返回 null（文件不可读或为空），返回 null：{0}", file);
                return null;
            }
            var explanation = $"没有专用预览形态（{AssetDisplay.TypeLabel(asset.Type)}），显示十六进制转储。";
            Log.Info("十六进制兜底预览：文件「{0}」，{1}，转储文本 {2:N0} 字符，资源「{3}」。",
                file, dump.Describe() ?? "-", dump.Text?.Length ?? 0, AssetDisplay.DisplayPath(asset));
            return new AssetPreview(AssetPreviewKind.Hex,
                $"{explanation} {dump.Describe()}",
                Text: dump.Text);
        }, cancellationToken);
}
