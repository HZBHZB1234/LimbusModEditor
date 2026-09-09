using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Assets.Preview;

/// <summary>预览形态（UI 按此切换模板，plan-05 第 2 步）。</summary>
public enum AssetPreviewKind
{
    /// <summary>无选中资源。</summary>
    None,
    /// <summary>图像（纹理 / Sprite 合成子图）。</summary>
    Image,
    /// <summary>纯文本（含行号展示）。</summary>
    Text,
    /// <summary>JSON（文本 + 树视图切换）。</summary>
    Json,
    /// <summary>音频（试听 + 波形）。</summary>
    Audio,
    /// <summary>键值行（摘要卡 / 只读字段树 / FSB 结构）。</summary>
    Rows,
    /// <summary>十六进制转储（未知/二进制兜底）。</summary>
    Hex,
    /// <summary>纯说明（未知类型、无法预览时的明确原因）。</summary>
    Message,
}

/// <summary>一行预览内容（<see cref="Depth"/> 用于字段树缩进）。</summary>
public sealed record AssetPreviewRow(string Label, string Value, int Depth = 0, bool Highlight = false);

/// <summary>音频预览数据：原始 FSB 负载 + 可选的解码 WAV + 波形包络。</summary>
/// <param name="Fsb">原始 FSB5 负载（试听失败时仍可导出/检查结构）。</param>
/// <param name="Wave">解码后的 WAV 字节；无 FMOD DLL 或解码失败时为 null。</param>
/// <param name="UnavailableReason">不能试听时的中文原因（缺 DLL / 未知编解码）。</param>
/// <param name="Envelope">波形包络（每桶 0..1 峰值；无波形时为空）。</param>
public sealed record AssetPreviewAudio(
    byte[]? Fsb,
    byte[]? Wave,
    string? UnavailableReason,
    int SampleRate,
    int Channels,
    double DurationSeconds,
    IReadOnlyList<float> Envelope)
{
    public bool CanPlay => Wave is { Length: > 0 };
}

/// <summary>
/// 一次预览的产物。<see cref="AlternateImagePng"/> 用于「原图 ↔ 替换图」切换
/// （有替换时主图显示替换后内容，备选图显示缓存原图）。
/// </summary>
public sealed record AssetPreview(
    AssetPreviewKind Kind,
    string InfoLine,
    string? Text = null,
    byte[]? ImagePng = null,
    byte[]? AlternateImagePng = null,
    string? AlternateLabel = null,
    IReadOnlyList<AssetPreviewRow>? Rows = null,
    AssetPreviewAudio? Audio = null)
{
    public static AssetPreview None(string info = "无预览") => new(AssetPreviewKind.None, info);

    public static AssetPreview Message(string info, string? text = null) => new(AssetPreviewKind.Message, info, text);
}

/// <summary>
/// 预览提供者（plan-05 第 1 步）。实现必须只读资源、不修改任何文件；
/// 无法处理时返回 null 交注册表尝试下一个提供者。
/// </summary>
public interface IAssetPreviewProvider
{
    /// <summary>提供者名称（诊断/测试用）。</summary>
    string Name { get; }

    /// <summary>是否可能处理该资源（快速判定，不读盘）。</summary>
    bool CanPreview(AssetRecord asset);

    /// <summary>生成预览；返回 null 表示「实际处理不了」，由后续提供者接手。</summary>
    Task<AssetPreview?> PreviewAsync(AssetRecord asset, IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// 预览提供者注册表：按注册顺序取第一个能产出的结果；全部落空时给出
/// 「无专用预览」说明（绝不返回空白）。
/// </summary>
public sealed class AssetPreviewRegistry
{
    private readonly IReadOnlyList<IAssetPreviewProvider> _providers;

    public AssetPreviewRegistry(IEnumerable<IAssetPreviewProvider> providers)
        => _providers = providers?.ToArray() ?? throw new ArgumentNullException(nameof(providers));

    /// <summary>默认注册顺序：图像 → Sprite → 音频 → 文本/JSON → 脚本字段 → 摘要 → 十六进制兜底。
    /// <paramref name="fmodDirectoryProvider"/> 提供当前 FMOD DLL 目录（设置可随时改，
    /// 因此每次预览都现取；为 null 表示没有可用 DLL）。</summary>
    public static AssetPreviewRegistry CreateDefault(Func<string?>? fmodDirectoryProvider = null) => new(
    [
        new TexturePreviewProvider(),
        new SpritePreviewProvider(),
        new AudioPreviewProvider(fmodDirectoryProvider),
        new TextPreviewProvider(),
        new ScriptPreviewProvider(),
        new SummaryPreviewProvider(),
        new HexPreviewProvider(),
    ]);

    public IReadOnlyList<IAssetPreviewProvider> Providers => _providers;

    public async Task<AssetPreview> PreviewAsync(AssetRecord? asset, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (asset is null) return AssetPreview.None();
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!provider.CanPreview(asset)) continue;
            try
            {
                var preview = await provider.PreviewAsync(asset, progress, cancellationToken).ConfigureAwait(false);
                if (preview is not null) return preview;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 单个提供者失败不阻塞后续：记录原因，继续尝试下一种形态。
                progress?.Report($"{provider.Name} 预览失败：{ex.Message}");
            }
        }
        return AssetPreview.Message(
            "没有可用的预览形态。",
            $"资源类型：{AssetDisplay.TypeLabel(asset.Type)}\n路径：{AssetDisplay.DisplayPath(asset)}\n" +
            "可用「十六进制预览」查看原始字节，或用「替换…」换成已知格式。");
    }
}
