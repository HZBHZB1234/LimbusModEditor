using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Assets.Preview;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Formats.Unity;
using NLog;

namespace LimbusModEditor.Application.Spine;

/// <summary>一次 Spine 资源导出的结果。</summary>
/// <param name="Written">写出的文件相对路径（相对导出根下的目录）。</param>
/// <param name="Directory">产物目录绝对路径。</param>
/// <param name="Detail">中文摘要（给用户看，含跳过原因）。</param>
public sealed record SpineExportResult(IReadOnlyList<string> Written, string Directory, string Detail)
{
    public int FileCount => Written.Count;
}

/// <summary>
/// Spine 资源导出：把「骨架 + 图集 + 图集贴图」三件套从 Unity 容器里取出来，
/// 按原始文件名落到磁盘，交给外部 Spine 工具（Skeleton Viewer / Spine Editor）查看。
///
/// <para><b>为什么不做成导出槽位</b>：模组导出计划的口径是「只导出被修改过的资源」，
/// 而 Spine 导出多半是「我想看看这个动画长什么样」——把未修改资源塞进模组包会污染
/// 那个语义（也会让「导出模组」莫名其妙多出几百个文件）。因此它是一个独立动作。</para>
///
/// <para><b>不猜</b>：只导出同目录下的 Spine 文本（<c>.json</c> / <c>.atlas.txt</c>）
/// 与图集页贴图；其余 Unity 对象（材质 / 预制体 / SkeletonData.asset）不导出，
/// 因为离开 Unity 它们没有意义。</para>
/// </summary>
public sealed class SpineExportService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly SpinePreviewService _preview;

    /// <param name="preview">Spine 预览服务（它持有「同目录资源」索引，导出复用同一份）。</param>
    public SpineExportService(SpinePreviewService preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        _preview = preview;
    }

    /// <summary>目录名净化（容器目录名一般安全，这里是兜底）。</summary>
    public static string FolderNameFor(string containerEntry)
    {
        var folder = SpineSiblingIndex.FolderOf(containerEntry);
        var leaf = folder.Length == 0 ? "spine" : folder[(folder.LastIndexOf('/') + 1)..];
        return string.IsNullOrWhiteSpace(leaf) ? "spine" : Sanitize(leaf);
    }

    private static string Sanitize(string name)
        => new(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

    /// <summary>
    /// 导出 <paramref name="spineAsset"/> 所在目录的 Spine 资源到
    /// <c>&lt;targetDirectory&gt;/&lt;目录名&gt;/</c>。
    /// </summary>
    public async Task<SpineExportResult> ExportAsync(
        AssetRecord spineAsset, string targetDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spineAsset);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        using var scope = Log.Scope("导出 Spine 资源");

        var selfEntry = AssetDisplay.ContainerEntryPath(spineAsset) ?? string.Empty;
        var folder = SpineSiblingIndex.FolderOf(selfEntry);
        var subDirectory = Path.Combine(Path.GetFullPath(targetDirectory), FolderNameFor(selfEntry));
        Directory.CreateDirectory(subDirectory);

        var siblings = _preview.Index().Siblings(selfEntry);
        var written = new List<string>();
        var skipped = new List<string>();

        // ① Spine 文本：自身 + 同目录全部 .json / .atlas.txt（骨架与图集可能分属两个资源）。
        var textAssets = new List<AssetRecord> { spineAsset };
        textAssets.AddRange(siblings.Where(x =>
            x.Type is AssetType.Text or AssetType.Json
            && !ReferenceEquals(x, spineAsset)
            && IsSpineTextName(SpineSiblingIndex.FileNameOf(AssetDisplay.ContainerEntryPath(x) ?? string.Empty))));
        foreach (var asset in textAssets.DistinctBy(x => x.AssetId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = SpineSiblingIndex.FileNameOf(AssetDisplay.ContainerEntryPath(asset) ?? asset.LogicalPath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(name)) name = $"spine-{asset.AssetId:N}";
            var text = await Task.Run(() => ReadText(asset, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (text is null) { skipped.Add($"{name}（读取失败）"); continue; }
            var path = Path.Combine(subDirectory, EnsureTextExtension(name));
            await File.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
            written.Add(Path.GetFileName(path));
        }

        // ② 图集贴图：目录里所有 PNG/JPG 贴图（图集页名与文件名一致，直接按原名导出）。
        foreach (var asset in siblings.Where(x => x.Type is AssetType.Texture or AssetType.Sprite))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = SpineSiblingIndex.FileNameOf(AssetDisplay.ContainerEntryPath(asset) ?? string.Empty);
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(asset.SourcePath) || asset.UnityPathId is null) { skipped.Add($"{name}（无字节）"); continue; }
            try
            {
                var png = await Task.Run(
                    () => new UnityAssetService().ReadTexturePng(asset.SourcePath!, asset.UnityPathId!.Value, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                if (png is null || png.Length == 0) { skipped.Add($"{name}（解码失败）"); continue; }
                var path = Path.Combine(subDirectory, name);
                await File.WriteAllBytesAsync(path, png, cancellationToken).ConfigureAwait(false);
                written.Add(name);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                Log.Warn(ex, "Spine 贴图导出失败：{0}", name);
                skipped.Add($"{name}（导出失败）");
            }
        }

        var detail = written.Count == 0
            ? "没有可导出的 Spine 文件（同目录既没有骨架/图集文本，也没有图集贴图）。"
            : $"已导出 {written.Count} 个文件到 {subDirectory}"
              + (skipped.Count == 0 ? "。" : $"；跳过 {skipped.Count} 项：{string.Join("、", skipped.Take(5))}");
        Log.Info("Spine 导出完成：目录 {0}，写出 {1} 个，跳过 {2} 个", subDirectory, written.Count, skipped.Count);
        return new SpineExportResult(written, subDirectory, detail);
    }

    private static bool IsSpineTextName(string name)
        => name.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>图集文本名必须以 <c>.txt</c> 结尾才会被 Spine 工具识别（<c>x.atlas.txt</c> → 保持原名）。</summary>
    private static string EnsureTextExtension(string name)
        => Path.HasExtension(name) ? name : name + ".txt";

    private static string? ReadText(AssetRecord asset, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(asset.SourcePath) || asset.UnityPathId is null) return null;
        try
        {
            if (asset.Metadata.TryGetValue("unityBundle", out var isBundle) && isBundle == "true")
            {
                var textAsset = new UnityAssetService().ReadBundleTextAsset(
                    asset.SourcePath!, asset.ContainerPath ?? string.Empty, asset.UnityPathId.Value, cancellationToken);
                return textAsset.TryDecodeUtf8();
            }
            return File.Exists(asset.SourcePath) ? File.ReadAllText(asset.SourcePath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            Log.Warn(ex, "Spine 文本导出时读取失败：{0}", AssetDisplay.DisplayPath(asset));
            return null;
        }
    }
}
