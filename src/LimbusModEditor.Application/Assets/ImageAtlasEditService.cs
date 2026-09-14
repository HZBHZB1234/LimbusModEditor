using System.Text.Json;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Editing.Images;

namespace LimbusModEditor.Application.Assets;

public sealed record AtlasSplitResult(Guid AssetId, string Directory, ImageLayout Layout);

/// <summary>Persists atlas regions and layout in a project workspace so artists
/// can edit individual sprites and later rebuild the original texture.</summary>
public sealed class ImageAtlasEditService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ImageAtlasService _atlas = new();
    private readonly AssetEditService _assetEdits = new();

    public Task<AtlasSplitResult> SplitAsync(ModProject project, Guid assetId, string imagePath, string projectDirectory, int columns, int rows, CancellationToken cancellationToken = default)
        => SplitAsync(project, FindImageAsset(project, assetId), imagePath, projectDirectory, columns, rows, cancellationToken);

    /// <summary>实体重载：调用方已持有资源对象，不必在全项目里线性查找
    /// （<see cref="FindImageAsset"/> 在真实规模下是 127 万条的一次线性扫描）。</summary>
    public async Task<AtlasSplitResult> SplitAsync(ModProject project, AssetRecord asset, string imagePath, string projectDirectory, int columns, int rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        RequireImage(asset);
        var result = _atlas.SplitGrid(await File.ReadAllBytesAsync(imagePath, cancellationToken), columns, rows, asset.AssetId.ToString("N"));
        var directory = Path.Combine(Path.GetFullPath(projectDirectory), "edits", "atlases", asset.AssetId.ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var region in result.Layout.Regions)
            await File.WriteAllBytesAsync(Path.Combine(directory, region.Id + ".png"), result.Regions[region.Id], cancellationToken);
        var layoutPath = Path.Combine(directory, "layout.json");
        await File.WriteAllTextAsync(layoutPath, JsonSerializer.Serialize(result.Layout, JsonOptions), cancellationToken);
        asset.Metadata["atlasDirectory"] = directory;
        asset.Metadata["atlasLayoutPath"] = layoutPath;
        asset.Metadata["atlasRegionCount"] = result.Layout.Regions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new(asset.AssetId, directory, result.Layout);
    }

    public Task<AssetReplacementResult> RepackAsync(ModProject project, Guid assetId, string projectDirectory, CancellationToken cancellationToken = default)
        => RepackAsync(project, FindImageAsset(project, assetId), projectDirectory, cancellationToken);

    /// <summary>实体重载：见 <see cref="SplitAsync(ModProject, AssetRecord, string, string, int, int, CancellationToken)"/>。</summary>
    public async Task<AssetReplacementResult> RepackAsync(ModProject project, AssetRecord asset, string projectDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        RequireImage(asset);
        if (!asset.Metadata.TryGetValue("atlasLayoutPath", out var layoutPath) || !File.Exists(layoutPath))
            throw new InvalidOperationException("该资源尚未拆分图集。");
        var layout = JsonSerializer.Deserialize<ImageLayout>(await File.ReadAllTextAsync(layoutPath, cancellationToken), JsonOptions)
            ?? throw new InvalidDataException("图集布局文件无效。");
        var regions = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var region in layout.Regions)
        {
            var path = Path.Combine(Path.GetDirectoryName(layoutPath)!, region.Id + ".png");
            if (!File.Exists(path)) throw new FileNotFoundException($"缺少图集区域: {region.Id}", path);
            regions[region.Id] = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        var data = _atlas.Repack(layout, regions);
        var temporary = Path.Combine(Path.GetTempPath(), $"lme-atlas-{asset.AssetId:N}.png");
        await File.WriteAllBytesAsync(temporary, data, cancellationToken);
        try { return await _assetEdits.ReplaceFromFileAsync(project, asset, temporary, projectDirectory, cancellationToken); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RequireImage(AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Type is not (AssetType.Texture or AssetType.Sprite)) throw new InvalidOperationException("只有 Texture/Sprite 资源支持图集操作。");
    }

    private static AssetRecord FindImageAsset(ModProject project, Guid assetId)
    {
        var asset = project.Assets.FirstOrDefault(x => x.AssetId == assetId)
            ?? throw new KeyNotFoundException($"未找到资源: {assetId}");
        RequireImage(asset);
        return asset;
    }
}
