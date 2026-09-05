using System.Security.Cryptography;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Edits;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Assets;

public sealed record AssetReplacementResult(Guid AssetId, string StoredPath, long Size, string Hash);

/// <summary>
/// Stores user replacement files inside the project workspace and records a
/// reversible edit operation. Format-specific writers consume the stored file
/// later, so the UI does not need to know whether the target is Carra or Bank.
/// </summary>
public sealed class AssetEditService
{
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
        asset.Size = info.Length;
        asset.Metadata["replacementPath"] = stored;
        project.Edits.Add(new EditOperation { Kind = EditOperationKind.ReplaceAsset, AssetId = asset.AssetId, TargetPath = asset.LogicalPath, SourcePath = stored, BeforeHash = asset.OriginalHash, AfterHash = hash });
        return new(asset.AssetId, stored, info.Length, hash);
    }
}
