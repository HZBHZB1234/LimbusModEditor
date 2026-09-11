using System.Diagnostics;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Application.Scanning;
using Xunit.Abstractions;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// 真实数据门控（手动）冒烟：在**真实环境**上跑一次完整的启动扫描——真实 Unity 缓存
/// （1459 个 bundle）、真实 bank 目录（1531 个 .bank）、真实 lang 目录、真实静态数据 bundle，
/// 用来回答「每次启动自动扫描」的两个问题：① 热缓存下到底多慢；② 四个库（尤其此前是
/// 0 字节的 <c>text-index.db</c>）是否都能被建好/补好。
///
/// <para>默认跳过（常规套件保持快）：需要 <c>LME_STARTUP_SCAN_SMOKE=1</c>；
/// 扫描目标目录用 <c>LME_APP_DIR</c> 指定（应为发布版目录：复用已有索引 + 真实 shared-config）。
/// 复用已有索引 = 热扫描，正是用户每次启动时看到的那条路径。</para>
/// </summary>
public class RealStartupScanSmokeTests
{
    private readonly ITestOutputHelper _output;

    public RealStartupScanSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Startup_scan_over_the_real_environment_completes_every_step()
    {
        if (Environment.GetEnvironmentVariable("LME_STARTUP_SCAN_SMOKE") != "1") return;
        var appDirectory = Environment.GetEnvironmentVariable("LME_APP_DIR");
        if (string.IsNullOrWhiteSpace(appDirectory) || !Directory.Exists(appDirectory))
        {
            _output.WriteLine("LME_APP_DIR 未指向现有目录：跳过。");
            return;
        }

        var env = new AppEnvironment(appDirectory);
        var projectFile = Path.Combine(env.ProjectsDirectory, "MyMod.lmeproj");
        if (!File.Exists(projectFile))
        {
            _output.WriteLine($"没有真实项目文件（{projectFile}）：跳过。");
            return;
        }

        _output.WriteLine($"程序目录：{env.BaseDirectory}");
        _output.WriteLine($"游戏目录：{env.EffectiveGameDirectory(null)}");
        _output.WriteLine($"缓存目录：{env.EffectiveUnityCacheDirectory(null)}");

        // 扫描前：四个库的存在性与大小（0 字节就是那个「no such table」的现场）。
        foreach (var path in StartupScanService.CacheDatabasePaths(env.CacheDirectory))
            _output.WriteLine($"  [前] {Path.GetFileName(path)}：{(File.Exists(path) ? new FileInfo(path).Length : -1)} 字节");

        var service = new StartupScanService(env,
            new UnityCacheScanService(Path.Combine(env.CacheDirectory, "unity-cache-index.json")));

        var clock = Stopwatch.StartNew();
        var repaired = service.EnsureCacheDatabases();
        clock.Stop();
        _output.WriteLine($"建库/校表：补建 {repaired.Count} 个（{string.Join("、", repaired.Select(Path.GetFileName))}）· {clock.ElapsedMilliseconds} ms");

        var project = await new ProjectService().LoadAsync(projectFile);
        _output.WriteLine($"项目：{project.Name} · 项目文件里的资产 {project.Assets.Count} 条");

        // 与真实启动顺序一致：先做一次索引回灌（MainWindow.AfterProjectOpenedAsync 的第 3 步），
        // 再跑启动扫描。回灌后项目里已有全部引用资产，扫描阶段只需按 LogicalPath 对账，
        // 这才是用户每次启动实际经历的那条路径（不先回灌会凭空构造 127 万条记录，测不准）。
        var rehydrateClock = Stopwatch.StartNew();
        var rehydrated = await new UnityCacheScanService(
            Path.Combine(env.CacheDirectory, "unity-cache-index.json")).RehydrateFromIndexAsync(project);
        rehydrateClock.Stop();
        _output.WriteLine($"索引回灌：+{rehydrated} 条（现有 {project.Assets.Count} 条）· {rehydrateClock.Elapsed.TotalSeconds:0.0} 秒");

        clock.Restart();
        // 与 MainWindow.RunStartupScanAsync 完全一致：回灌成功后传 projectMatchesIndex: true
        // （跳过索引命中 bundle 的逐行对账）。这就是用户每次启动实际经历的耗时；
        // 报告里每步自带耗时，够定位「哪一步慢」（不要再在本测试里额外跑重复的重活，
        // 那会把内存/文件缓存搅浑，测出来的数字不再代表启动路径）。
        var report = await service.ScanAllAsync(project, progress: null, cancellationToken: default,
            projectMatchesIndex: true);
        clock.Stop();

        foreach (var step in report.Steps)
            _output.WriteLine($"  [{step.Status}] {step.Label}（{step.Elapsed.TotalSeconds:0.0} 秒）：{step.Detail}");
        _output.WriteLine($"总耗时：{report.Elapsed.TotalSeconds:0.0} 秒 —— {report.Describe()}");

        foreach (var path in StartupScanService.CacheDatabasePaths(env.CacheDirectory))
            _output.WriteLine($"  [后] {Path.GetFileName(path)}：{(File.Exists(path) ? new FileInfo(path).Length : -1)} 字节");

        // 断言：五步齐全、没有一步失败、四个库都非空（缓存只影响速度，但「建不起来」要暴露）。
        Assert.Equal(5, report.Steps.Count);
        Assert.Equal(0, report.FailedCount);
        Assert.DoesNotContain(report.Steps, x => x.Status == StartupScanStepStatus.Skipped);
        foreach (var path in StartupScanService.CacheDatabasePaths(env.CacheDirectory))
        {
            Assert.True(File.Exists(path), $"缓存库不存在：{path}");
            Assert.True(new FileInfo(path).Length > 0, $"缓存库是空的：{path}");
        }
    }
}
