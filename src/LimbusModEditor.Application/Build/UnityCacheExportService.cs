using System.IO;
using System.Text;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Application.Build;

/// <summary>
/// 傻瓜化一键导出：把项目里所有「扫描自 Unity 缓存并已编辑」的对象按真实
/// 加载器的 Carra2 布局（键 = 缓存外层键/内层键/pathId.类型表索引，逐条目 XZ）
/// 打包成可直接放进模组目录的 .carra2。内部先用 UnityBundleBuildService 把
/// 每个被编辑的 bundle 重打包（含引用完整性验证），再从重打包结果读取修改后
/// 的对象原始字节（口径与 T1 真实写回验证一致）。
/// </summary>
public sealed class UnityCacheExportService
{
    private readonly UnityBundleBuildService _bundleBuilder = new();

    /// <param name="projectRoot">项目根目录（.lmeproj 所在目录），重打包
    /// 中间产物写入 &lt;projectRoot&gt;/builds/unity-bundles。</param>
    /// <param name="unityCacheDirectory">可选：用于缓存对齐诊断。</param>
    public async Task<ModExportResult> ExportCarra2Async(
        ModProject project, string projectRoot, string outputPath,
        string? unityCacheDirectory = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var edited = project.Assets.Where(IsEditedCacheAsset).ToList();
        if (edited.Count == 0)
            throw new InvalidDataException(
                "没有找到任何已编辑的缓存对象。请先在资源列表中替换图片、编辑 Sprite 元数据或 Unity 字段。");

        // 1) 重打包所有被编辑的 bundle（BuildAsync 自带 staging + 引用完整性验证）。
        var buildsDirectory = Path.Combine(Path.GetFullPath(projectRoot), "builds", "unity-bundles");
        var builds = await _bundleBuilder.BuildAsync(project, buildsDirectory, cancellationToken);
        var bySource = builds.ToDictionary(
            x => Path.GetFullPath(x.SourcePath), x => x.OutputPath, StringComparer.OrdinalIgnoreCase);

        // 2) 从重打包结果逐对象读回修改后的原始字节，组装 Carra2。
        var package = new CarraPackage();
        var statuses = new List<ExportAssetStatus>();
        using var backend = new AssetsToolsBackend();
        foreach (var asset in edited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (asset.SourcePath is null ||
                !bySource.TryGetValue(Path.GetFullPath(asset.SourcePath), out var repacked))
            {
                statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped, "所属 bundle 没有生成重打包结果。"));
                continue;
            }
            try
            {
                var raw = backend.ReadBundleSerializedObject(repacked, asset.ContainerPath!, asset.UnityPathId!.Value);
                if (raw.Data.Length == 0)
                {
                    statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped, "重打包后对象数据为空。"));
                    continue;
                }
                var outer = asset.Metadata["cacheOuter"];
                var inner = asset.Metadata["cacheInner"];
                var key = new CarraObjectKey(outer, inner, asset.UnityPathId!.Value, raw.TypeTableIndex);
                package.Entries.Add(CarraEntry.CreateNew(key, raw.Data));
                statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Applied, null));
            }
            catch (Exception ex)
            {
                statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped, $"读取修改后对象失败：{ex.Message}"));
            }
        }
        if (package.Entries.Count == 0)
            throw new InvalidDataException(
                "没有对象成功写入 Carra2 包；请查看逐资源状态了解原因。");
        package.UnknownFiles.Add(("carra.json",
            Encoding.UTF8.GetBytes($"{{\"format\": \"carra2\", \"objects\": {package.Entries.Count}}}")));

        var handler = new CarraFormatHandler();
        var validation = await handler.ValidateAsync(
            new ModPackage { SourceFormat = ModFormatKind.Carra2, Payload = package }, cancellationToken);
        if (validation.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException(string.Join("; ", validation.Diagnostics.Select(x => x.Message)));

        // 3) 缓存对齐诊断（与 ModExportService 同口径）：外层键缺失 = 游戏更新换键。
        var diagnostics = CheckCacheAlignment(package, unityCacheDirectory).ToList();

        var outputFullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);
        await AtomicOutput.WriteAsync(outputFullPath, async (stream, token) =>
        {
            await using var output = stream;
            await handler.ExportAsync(
                new ModPackage { SourceFormat = ModFormatKind.Carra2, Payload = package },
                output, new ExportContext(ModFormatKind.Carra2, Codec: new JovelerXzCodec(), CancellationToken: token));
        }, cancellationToken);

        var applied = statuses.Count(x => x.Status == ExportAssetStatus.Applied);
        return new ModExportResult(ModFormatKind.Carra2, outputFullPath, applied, diagnostics, statuses);
    }

    private static bool IsEditedCacheAsset(AssetRecord asset)
    {
        if (asset.UnityPathId is null || string.IsNullOrWhiteSpace(asset.ContainerPath)) return false;
        if (asset.SourcePath is null || !File.Exists(asset.SourcePath)) return false;
        if (!asset.Metadata.TryGetValue("unityBundle", out var isBundle) || isBundle != "true") return false;
        if (!asset.Metadata.ContainsKey("cacheOuter") || !asset.Metadata.ContainsKey("cacheInner")) return false;
        return asset.Metadata.ContainsKey("replacementPath")
            || asset.Metadata.ContainsKey("spriteMetadata")
            || asset.Metadata.ContainsKey("unityFieldEdits");
    }

    private static IEnumerable<string> CheckCacheAlignment(CarraPackage package, string? cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory)) yield break;
        foreach (var outer in package.Entries.Select(x => x.Key.Account).Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(Path.Combine(cacheDirectory, outer)))
                yield return $"缓存对齐：外层键 {outer} 不在当前缓存目录中（游戏可能已更新），加载器将无法匹配这些对象";
        }
    }
}
