using System.IO;
using System.Text.RegularExpressions;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Unity;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LimbusModEditor.Domain.Tests;

/// <summary>傻瓜化一键导出链路（真实缓存样本，无样本机器自动跳过）：
/// 扫描 → 实体化 → PNG 替换 → ExportCarra2 → Carra2 重新导入逐字段核对。
/// 口径与 RealWriteBackTests（T1）一致，但走的是新的一键流程与服务。</summary>
public class UnityCacheExportServiceTests : IDisposable
{
    private readonly string _root;

    public UnityCacheExportServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lme-cacheexport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* temp cleanup */ }
    }

    [Fact]
    public async Task One_click_export_produces_valid_carra2_from_a_scanned_project()
    {
        var realCache = FindRealCacheRoot();
        if (realCache is null) return;

        // 找一个含小 Texture2D 的真实 bundle（替换走纹理管线）。
        var service = new UnityCacheScanService();
        string? chosenData = null;
        IReadOnlyList<Domain.Assets.AssetRecord> chosenAssets = [];
        foreach (var outer in Directory.EnumerateDirectories(realCache))
        {
            foreach (var inner in Directory.EnumerateDirectories(outer))
            {
                var data = Path.Combine(inner, "__data");
                if (!File.Exists(data)) continue;
                var size = new FileInfo(data).Length;
                if (size is 0 or > 12 * 1024 * 1024) continue;
                IReadOnlyList<Domain.Assets.AssetRecord> assets;
                try { assets = new UnityAssetService().ScanBundle(data); }
                catch (Exception) { continue; }
                if (assets.Any(x => x.Type == Domain.Assets.AssetType.Texture && x.Size is > 0 and <= 512 * 1024))
                {
                    chosenData = data;
                    chosenAssets = assets;
                    break;
                }
            }
            if (chosenData is not null) break;
        }
        if (chosenData is null) return;
        var outerKey = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(chosenData)))!;
        var innerKey = Path.GetFileName(Path.GetDirectoryName(chosenData))!;

        // 临时缓存布局 + 扫描进项目。
        var tempCache = Path.Combine(_root, "cache");
        var tempEntry = Path.Combine(tempCache, outerKey, innerKey);
        Directory.CreateDirectory(tempEntry);
        File.Copy(chosenData, Path.Combine(tempEntry, "__data"));
        var project = new ModProject { Name = "OneClick" };
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        project.SourceDirectory = Path.Combine(projectRoot, "sources");
        await service.ScanIntoProjectAsync(project, tempCache);
        var texture = project.Assets
            .FirstOrDefault(x => x.Type == Domain.Assets.AssetType.Texture && x.Size <= 512 * 1024);
        Assert.NotNull(texture);

        // 实体化 + PNG 替换（与主窗口一键导出前相同的准备步骤）。
        await UnityCacheMaterializationService.MaterializeForEditingAsync(project, texture, projectRoot);
        var pngPath = Path.Combine(_root, "replacement.png");
        File.WriteAllBytes(pngPath, BuildPng());
        await new AssetEditService().ReplaceFromFileAsync(project, texture.AssetId, pngPath, projectRoot);

        var output = Path.Combine(_root, "out", "OneClick.carra2");
        var result = await new UnityCacheExportService().ExportCarra2Async(project, projectRoot, output, tempCache);

        Assert.True(File.Exists(output));
        Assert.True(result.AppliedReplacements >= 1);
        Assert.All(result.AssetStatuses.Where(x => x.Status == ExportAssetStatus.Applied),
            x => Assert.Null(x.Note));

        // 导出物必须被格式探针识别，键与被编辑对象一一对应。
        var handler = new CarraFormatHandler();
        await using var input = File.OpenRead(output);
        var probe = await handler.ProbeAsync(input, Path.GetFileName(output));
        Assert.True(probe.IsMatch, "导出的 carra2 未通过格式探针");
        input.Position = 0;
        var imported = await handler.ImportAsync(input, new ImportContext());
        var payload = Assert.IsType<CarraPackage>(imported.Payload);
        var entry = Assert.Single(payload.Entries);
        Assert.Matches(@"^[0-9a-f]{32}/[0-9a-f]{32}/-?\d+\.\d+$", entry.Key.LogicalPath);
        Assert.Equal(outerKey, entry.Key.Account, ignoreCase: true);
        Assert.Equal(innerKey, entry.Key.Bundle, ignoreCase: true);
        Assert.Equal(texture.UnityPathId, entry.Key.PathId);
        // 重新导入的数据应可读回，且与 vanilla 原始对象字节不同（替换已生效）。
        var roundTrip = entry.ReadData();
        Assert.True(roundTrip.Length > 0);
        using (var backend = new AssetsToolsBackend())
        {
            var vanilla = backend.ReadBundleSerializedObject(
                Path.Combine(tempEntry, "__data"), texture.ContainerPath!, texture.UnityPathId!.Value);
            Assert.NotEqual(vanilla.Data, roundTrip);
        }
    }

    private static string? FindRealCacheRoot()
    {
        var overrideDir = Environment.GetEnvironmentVariable("LME_UNITY_CACHE_DIR");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in new[] { overrideDir, string.IsNullOrWhiteSpace(profile) ? null : Path.Combine(profile, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany") })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) continue;
            try
            {
                foreach (var outer in Directory.EnumerateDirectories(candidate))
                foreach (var inner in Directory.EnumerateDirectories(outer))
                    if (File.Exists(Path.Combine(inner, "__data"))) return candidate;
            }
            catch (Exception) { /* unreadable caches are skipped */ }
        }
        return null;
    }

    private static byte[] BuildPng()
    {
        using var image = new Image<Rgba32>(8, 8);
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                image[x, y] = new Rgba32(255, 255, 255, 255);
        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);
        return buffer.ToArray();
    }
}
