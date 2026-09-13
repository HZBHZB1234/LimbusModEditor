using System.Diagnostics;
using System.IO;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Domain.Diagnostics;
using NLog;

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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

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
        if (!isCacheReference)
        {
            if (Log.IsDebugEnabled)
                Log.Debug("无需实体化（不是缓存引用，直接用源文件）：{0}", sourcePath);
            return sourcePath;
        }
        Log.Info("缓存引用实体化开始：资源={0} · bundle 来源={1} · 项目={2}", seed.LogicalPath, sourcePath, project.Name);
        using var scope = Log.Scope($"实体化 {(seed.Bundle is { Length: > 0 } bundleName ? bundleName : seed.LogicalPath)}");

        var hasOriginalPath = seed.Metadata.TryGetValue("originalSourcePath", out var original);
        var originalExists = hasOriginalPath && File.Exists(original);
        var source = originalExists ? original! : sourcePath;
        if (hasOriginalPath && !originalExists)
            Log.Warn("缓存引用实体化：记录里的 originalSourcePath 已不存在，回落用当前 SourcePath：{0}", sourcePath);
        if (!File.Exists(source))
        {
            Log.Error("缓存引用实体化失败：缓存 bundle 不存在（游戏可能已更新缓存）：{0}", source);
            throw new FileNotFoundException("缓存 bundle 不存在（游戏可能已更新缓存）。", source);
        }

        var outer = seed.Metadata.TryGetValue("cacheOuter", out var o) ? o : Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(source))));
        var inner = seed.Metadata.TryGetValue("cacheInner", out var i) ? i : Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(source)));
        if (string.IsNullOrWhiteSpace(outer) || string.IsNullOrWhiteSpace(inner))
        {
            Log.Error("缓存引用实体化失败：无法识别缓存布局（外层/内层键缺失）：{0}", source);
            throw new InvalidDataException($"无法识别缓存布局（外层/内层键缺失）：{source}");
        }
        Log.Debug("缓存引用实体化：外层键={0} · 内层键={1} · 源 bundle={2}", outer, inner, source);

        var sourcesRoot = !string.IsNullOrWhiteSpace(project.SourceDirectory)
            ? Path.GetFullPath(project.SourceDirectory)
            : string.IsNullOrWhiteSpace(projectRoot)
                ? throw new InvalidDataException("项目缺少 sources 目录信息，无法创建本地副本。")
                : Path.Combine(Path.GetFullPath(projectRoot), "sources");
        var destination = Path.Combine(sourcesRoot, "cache", $"{outer}_{inner}.bundle");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var destinationExisted = File.Exists(destination);
        if (!destinationExisted)
        {
            var copyWatch = Stopwatch.StartNew();
            await using var input = File.OpenRead(source);
            await using var output = File.Create(destination);
            await input.CopyToAsync(output, cancellationToken);
            copyWatch.Stop();
            Log.Debug("缓存 bundle 已复制为本地可编辑副本：{0} → {1} · {2:0.0} MB · 用时 {3:0.0} 秒",
                source, destination, input.Length / 1024.0 / 1024.0, copyWatch.Elapsed.TotalSeconds);
        }
        else if (Log.IsDebugEnabled)
        {
            Log.Debug("本地副本已存在，跳过复制：{0}", destination);
        }

        var oldFull = Path.GetFullPath(source);
        var repointed = 0;
        var scannedAssets = 0;
        foreach (var asset in project.Assets)
        {
            scannedAssets++;
            Log.Every(scannedAssets, 500_000, LogLevel.Debug,
                () => $"实体化：已扫描项目资产 {scannedAssets} 条 · 已改指本地副本 {repointed} 条");
            if (asset.SourcePath is null) continue;
            if (!string.Equals(Path.GetFullPath(asset.SourcePath), oldFull, StringComparison.OrdinalIgnoreCase)) continue;
            asset.SourcePath = destination;
            asset.Metadata.Remove("reference");
            asset.Metadata["materialized"] = "true";
            if (!asset.Metadata.ContainsKey("originalSourcePath")) asset.Metadata["originalSourcePath"] = source;
            if (!asset.Metadata.ContainsKey("cacheOuter")) asset.Metadata["cacheOuter"] = outer;
            if (!asset.Metadata.ContainsKey("cacheInner")) asset.Metadata["cacheInner"] = inner;
            repointed++;
        }
        Log.Info("缓存引用实体化完成：{0} 条资产改指本地副本 · 目标 {1} · 本次复制={2}（扫描项目资产 {3} 条）",
            repointed, destination, !destinationExisted, scannedAssets);
        return destination;
    }
}
