using System.IO;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Scanning;

/// <summary>
/// 缓存引用 → 本地可编辑副本：用户首次编辑某个「扫描引用」资源时，把所属
/// bundle（&lt;缓存根&gt;/&lt;外层&gt;/&lt;内层&gt;/__data）复制进项目
/// sources/cache/&lt;外层&gt;_&lt;内层&gt;.bundle（每个 bundle 只复制一次），
/// 同一 bundle 的全部资源改指本地副本。此后编辑/构建都基于项目自有副本，
/// 缓存被游戏清理或换键也不再影响进行中的项目。
/// </summary>
public static class UnityCacheMaterializationService
{
    /// <summary>Returns the editable local bundle path for the seed asset
    /// (no-op for assets that are not cache references).</summary>
    public static async Task<string> MaterializeForEditingAsync(
        ModProject project, AssetRecord seed, string? projectRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(seed);
        var sourcePath = seed.SourcePath ?? throw new InvalidOperationException("资源没有可用的源文件。");
        var isCacheReference = (seed.Metadata.TryGetValue("reference", out var reference) && reference == "true")
            || sourcePath.EndsWith("__data", StringComparison.OrdinalIgnoreCase);
        if (!isCacheReference) return sourcePath;

        var source = seed.Metadata.TryGetValue("originalSourcePath", out var original) && File.Exists(original)
            ? original
            : sourcePath;
        if (!File.Exists(source)) throw new FileNotFoundException("缓存 bundle 不存在（游戏可能已更新缓存）。", source);

        var outer = seed.Metadata.TryGetValue("cacheOuter", out var o) ? o : Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(source))));
        var inner = seed.Metadata.TryGetValue("cacheInner", out var i) ? i : Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(source)));
        if (string.IsNullOrWhiteSpace(outer) || string.IsNullOrWhiteSpace(inner))
            throw new InvalidDataException($"无法识别缓存布局（外层/内层键缺失）：{source}");

        var sourcesRoot = !string.IsNullOrWhiteSpace(project.SourceDirectory)
            ? Path.GetFullPath(project.SourceDirectory)
            : string.IsNullOrWhiteSpace(projectRoot)
                ? throw new InvalidDataException("项目缺少 sources 目录信息，无法创建本地副本。")
                : Path.Combine(Path.GetFullPath(projectRoot), "sources");
        var destination = Path.Combine(sourcesRoot, "cache", $"{outer}_{inner}.bundle");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination))
        {
            await using var input = File.OpenRead(source);
            await using var output = File.Create(destination);
            await input.CopyToAsync(output, cancellationToken);
        }

        var oldFull = Path.GetFullPath(source);
        foreach (var asset in project.Assets)
        {
            if (asset.SourcePath is null) continue;
            if (!string.Equals(Path.GetFullPath(asset.SourcePath), oldFull, StringComparison.OrdinalIgnoreCase)) continue;
            asset.SourcePath = destination;
            asset.Metadata.Remove("reference");
            asset.Metadata["materialized"] = "true";
            if (!asset.Metadata.ContainsKey("originalSourcePath")) asset.Metadata["originalSourcePath"] = source;
            if (!asset.Metadata.ContainsKey("cacheOuter")) asset.Metadata["cacheOuter"] = outer;
            if (!asset.Metadata.ContainsKey("cacheInner")) asset.Metadata["cacheInner"] = inner;
        }
        return destination;
    }
}
