using System.Diagnostics;
using System.IO;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Projects;
using Xunit.Abstractions;

namespace LimbusModEditor.Domain.Tests;

/// <summary>Manual smoke test for the real full-cache first-run experience
/// (scale, wall time, project file size). Runs only when
/// LME_FULL_SCAN_SMOKE=1 so the regular suite stays fast.</summary>
public class RealFullScanSmokeTests
{
    private readonly ITestOutputHelper _output;

    public RealFullScanSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Full_real_cache_scan_completes_and_saves()
    {
        if (Environment.GetEnvironmentVariable("LME_FULL_SCAN_SMOKE") != "1") return;
        var overrideDir = Environment.GetEnvironmentVariable("LME_UNITY_CACHE_DIR");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cacheRoot = !string.IsNullOrWhiteSpace(overrideDir)
            ? overrideDir
            : Path.Combine(profile, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany");
        if (!Directory.Exists(cacheRoot)) return;

        // LME_SCAN_BUDGET=N：只把前 N 个真实 bundle（≤8MB）复制进临时缓存再
        // 扫描 —— 用于有界的吞吐量测量，避免全量 15GB 首扫占用过久。
        var budgetText = Environment.GetEnvironmentVariable("LME_SCAN_BUDGET");
        string scanRoot = cacheRoot;
        string? tempCache = null;
        if (int.TryParse(budgetText, out var budget) && budget > 0)
        {
            tempCache = Path.Combine(Path.GetTempPath(), "lme-scanbudget-" + Guid.NewGuid().ToString("N"));
            var copied = 0;
            foreach (var outer in Directory.EnumerateDirectories(cacheRoot))
            {
                foreach (var inner in Directory.EnumerateDirectories(outer))
                {
                    var data = Path.Combine(inner, "__data");
                    if (!File.Exists(data)) continue;
                    if (new FileInfo(data).Length > 8 * 1024 * 1024) continue;
                    var target = Path.Combine(tempCache, Path.GetFileName(outer)!, Path.GetFileName(inner)!);
                    Directory.CreateDirectory(target);
                    File.Copy(data, Path.Combine(target, "__data"));
                    copied++;
                    break;
                }
                if (copied >= budget) break;
            }
            _output.WriteLine($"budget cache: copied {copied} bundles -> {tempCache}");
            scanRoot = tempCache;
        }

        try
        {
            var projectRoot = Path.Combine(Path.GetTempPath(), "lme-fullscan-" + Guid.NewGuid().ToString("N"));
            var projects = new ProjectService();
            var project = await projects.CreateAsync(projectRoot, "FullScanSmoke");
            var projectFile = Path.Combine(projectRoot, "FullScanSmoke.lmeproj");

            var clock = Stopwatch.StartNew();
            var service = new UnityCacheScanService(Path.Combine(
                AppContext.BaseDirectory, "smoke-index.json"));
            var lastReported = 0;
            var progress = new Progress<UnityCacheScanProgress>(p =>
            {
                if (p.ProcessedEntries - lastReported >= 100)
                {
                    lastReported = p.ProcessedEntries;
                    _output.WriteLine($"  {p.ProcessedEntries}/{p.TotalEntries} bundles…");
                }
            });
            var result = await service.ScanIntoProjectAsync(project, scanRoot,
                gameDirectory: null, progress: progress);
            clock.Stop();
            _output.WriteLine($"scan: {result.TotalEntries} entries, {result.ScannedBundles} parsed, " +
                              $"{result.IndexedBundles} from index, {result.AddedAssets} assets in {clock.Elapsed} " +
                              $"(failures: {result.Diagnostics.Count})");
            foreach (var failure in result.Diagnostics.Take(5))
                _output.WriteLine($"  [诊断] {failure}");

            clock.Restart();
            await projects.SaveAsync(project, projectFile);
            clock.Stop();
            _output.WriteLine($"save: {new FileInfo(projectFile).Length / 1024.0:F0} KiB in {clock.Elapsed}");

            clock.Restart();
            var rescanned = await service.ScanIntoProjectAsync(project, scanRoot);
            clock.Stop();
            _output.WriteLine($"rescan: parsed {rescanned.ScannedBundles}, indexed {rescanned.IndexedBundles} in {clock.Elapsed}");

            Assert.True(result.AddedAssets > 100, "真实缓存应索引出大量资源");
            Assert.Equal(result.TotalEntries, rescanned.IndexedBundles);
        }
        finally
        {
            if (tempCache is not null)
                try { Directory.Delete(tempCache, true); } catch (Exception) { /* temp cleanup */ }
        }
    }
}
