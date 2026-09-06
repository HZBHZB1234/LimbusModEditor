using System.Security.Cryptography;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Assets;

public sealed record AssetReplacementResult(Guid AssetId, string StoredPath, long Size, string Hash);

/// <summary>Outcome of a batch replacement registration (P3.3 工作流).</summary>
public sealed record BatchReplacementReport(
    int Matched,
    IReadOnlyList<string> FilesWithoutAsset,
    IReadOnlyList<string> AssetsWithoutFile,
    int SkippedAlreadyReplaced)
{
    public string Describe() =>
        $"匹配并登记 {Matched} 个替换；{FilesWithoutAsset.Count} 个文件没有对应的资源；" +
        $"{AssetsWithoutFile.Count} 个资源没有提供文件；跳过已替换 {SkippedAlreadyReplaced} 个。";
}

/// <summary>
/// Stores user replacement files inside the project workspace and records a
/// reversible edit operation. Format-specific writers consume the stored file
/// later, so the UI does not need to know whether the target is Carra or Bank.
/// </summary>
public sealed class AssetEditService
{
    /// <summary>批量登记替换：matches files in a folder to project assets by
    /// file name (case-insensitive) and registers every match through the same
    /// reversible pipeline as single replacement.</summary>
    public async Task<BatchReplacementReport> BatchReplaceFromDirectoryAsync(
        ModProject project,
        string folder,
        string projectDirectory,
        bool onlyUnreplaced = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"替换文件夹不存在：{folder}");
        var byFileName = project.Assets
            .GroupBy(x => Path.GetFileName(x.LogicalPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var matched = 0;
        var skipped = 0;
        var filesWithoutAsset = new List<string>();
        var matchedAssets = new HashSet<Guid>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            if (fileName.StartsWith(".", StringComparison.Ordinal)) continue; // skip sidecar/temp files
            if (!byFileName.TryGetValue(fileName, out var asset)) { filesWithoutAsset.Add(fileName); continue; }
            if (onlyUnreplaced && asset.Metadata.ContainsKey("replacementPath")) { skipped++; continue; }
            await ReplaceFromFileAsync(project, asset.AssetId, file, projectDirectory, cancellationToken);
            matchedAssets.Add(asset.AssetId);
            matched++;
        }
        var assetsWithoutFile = project.Assets
            .Where(x => !matchedAssets.Contains(x.AssetId) &&
                        (!onlyUnreplaced || !x.Metadata.ContainsKey("replacementPath")))
            .Select(x => x.LogicalPath)
            .ToList();
        return new BatchReplacementReport(matched, filesWithoutAsset, assetsWithoutFile, skipped);
    }

    public Task<AssetReplacementResult> ReplaceFromBytesAsync(
        ModProject project, Guid assetId, ReadOnlyMemory<byte> data,
        string suggestedExtension, string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var extension = string.IsNullOrWhiteSpace(suggestedExtension) ? ".bin" : suggestedExtension;
        if (!extension.StartsWith('.')) extension = "." + extension;
        var temporaryRoot = Path.Combine(Path.GetFullPath(projectDirectory), "edits", "assets");
        Directory.CreateDirectory(temporaryRoot);
        var source = Path.Combine(temporaryRoot, $"{assetId:N}{extension}");
        return ReplaceFromBytesCoreAsync(project, assetId, data, source, cancellationToken);
    }

    public async Task<byte[]> ReadCurrentBytesAsync(
        ModProject project, Guid assetId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var asset = project.Assets.FirstOrDefault(x => x.AssetId == assetId)
            ?? throw new KeyNotFoundException($"未找到资源: {assetId}");
        var path = asset.Metadata.TryGetValue("replacementPath", out var replacement) && File.Exists(replacement)
            ? replacement : asset.SourcePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("资源没有可读取的本地文件。", path);
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    public async Task<AssetReplacementResult> ReplaceFromFileAsync(
        ModProject project,
        Guid assetId,
        string replacementPath,
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        var asset = project.Assets.FirstOrDefault(x => x.AssetId == assetId)
            ?? throw new KeyNotFoundException($"未找到资源: {assetId}");
        var source = Path.GetFullPath(replacementPath);
        if (!File.Exists(source)) throw new FileNotFoundException("替换文件不存在。", source);

        var editsRoot = Path.Combine(Path.GetFullPath(projectDirectory), "edits", "assets");
        Directory.CreateDirectory(editsRoot);
        var extension = Path.GetExtension(source);
        var stored = Path.Combine(editsRoot, asset.AssetId.ToString("N") + extension);
        var temporary = stored + ".tmp";
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(temporary))
                await input.CopyToAsync(output, cancellationToken);
            File.Move(temporary, stored, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        var info = new FileInfo(stored);
        await using var hashInput = File.OpenRead(stored);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashInput, cancellationToken));
        asset.ModifiedHash = hash;
        asset.EditState = asset.EditState == AssetEditState.Added ? AssetEditState.Added : AssetEditState.Modified;
        CaptureOriginalSize(asset);
        asset.Size = info.Length;
        asset.Metadata["replacementPath"] = stored;
        project.Edits.Add(new EditOperation
        {
            Kind = EditOperationKind.ReplaceAsset,
            AssetId = asset.AssetId,
            TargetPath = asset.LogicalPath,
            SourcePath = stored,
            BeforeHash = asset.OriginalHash,
            AfterHash = hash
        });
        return new(asset.AssetId, stored, info.Length, hash);
    }

    private async Task<AssetReplacementResult> ReplaceFromBytesCoreAsync(
        ModProject project, Guid assetId, ReadOnlyMemory<byte> data,
        string source, CancellationToken cancellationToken)
    {
        var temporary = source + ".tmp";
        try
        {
            await using (var output = File.Create(temporary))
                await output.WriteAsync(data, cancellationToken);
            File.Move(temporary, source, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return await RecordReplacementAsync(project, assetId, source, cancellationToken);
    }

    private async Task<AssetReplacementResult> RecordReplacementAsync(
        ModProject project, Guid assetId, string stored, CancellationToken cancellationToken)
    {
        var asset = project.Assets.FirstOrDefault(x => x.AssetId == assetId)
            ?? throw new KeyNotFoundException($"未找到资源: {assetId}");
        var info = new FileInfo(stored);
        await using var hashInput = File.OpenRead(stored);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashInput, cancellationToken));
        asset.ModifiedHash = hash;
        asset.EditState = asset.EditState == AssetEditState.Added ? AssetEditState.Added : AssetEditState.Modified;
        CaptureOriginalSize(asset);
        asset.Size = info.Length;
        asset.Metadata["replacementPath"] = stored;
        project.Edits.Add(new EditOperation { Kind = EditOperationKind.ReplaceAsset, AssetId = asset.AssetId, TargetPath = asset.LogicalPath, SourcePath = stored, BeforeHash = asset.OriginalHash, AfterHash = hash });
        return new(asset.AssetId, stored, info.Length, hash);
    }

    /// <summary>首次替换时记下原始大小，撤销修改时还原列表显示。</summary>
    private static void CaptureOriginalSize(AssetRecord asset)
    {
        if (!asset.Metadata.ContainsKey("originalSize"))
            asset.Metadata["originalSize"] = asset.Size.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>资源上是否存在任何可导出的编辑（替换文件 / Unity 字段 /
    /// Sprite 元数据）。这是导出与「已修改」判定的唯一口径。</summary>
    public static bool HasEdits(AssetRecord asset) =>
        asset.Metadata.ContainsKey("replacementPath") ||
        asset.Metadata.ContainsKey("unityFieldEdits") ||
        asset.Metadata.ContainsKey("spriteMetadata");

    /// <summary>撤销一个资源上的全部编辑：清除替换文件 / Unity 字段 /
    /// Sprite 元数据三类编辑标记并把资源还原为 Unchanged。替换的暂存文件
    /// （位于项目 edits/assets 下）会被删除；替换前的显示大小由
    /// originalSize 还原。资源本身没有编辑时返回 false。</summary>
    public bool ClearEdits(ModProject project, Guid assetId, string? projectDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var asset = project.Assets.FirstOrDefault(x => x.AssetId == assetId)
            ?? throw new KeyNotFoundException($"未找到资源: {assetId}");
        if (!HasEdits(asset)) return false;

        if (asset.Metadata.TryGetValue("replacementPath", out var stored) && !string.IsNullOrWhiteSpace(stored))
        {
            try
            {
                var fullStored = Path.GetFullPath(stored);
                var editsRoot = string.IsNullOrWhiteSpace(projectDirectory)
                    ? null
                    : Path.GetFullPath(Path.Combine(projectDirectory, "edits", "assets"))
                        .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                // 只删项目 edits/assets 内的暂存文件；用户手选的外部文件不动。
                if (editsRoot is null || fullStored.StartsWith(editsRoot, StringComparison.OrdinalIgnoreCase))
                    if (File.Exists(fullStored)) File.Delete(fullStored);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉暂存文件不阻断撤销：编辑标记已清除，导出不会再带上它。
            }
        }

        asset.Metadata.Remove("replacementPath");
        asset.Metadata.Remove("unityFieldEdits");
        asset.Metadata.Remove("spriteMetadata");
        if (asset.Metadata.TryGetValue("originalSize", out var original) &&
            long.TryParse(original, out var size))
            asset.Size = size;
        asset.Metadata.Remove("originalSize");
        asset.ModifiedHash = null;
        asset.EditState = AssetEditState.Unchanged;
        return true;
    }
}
