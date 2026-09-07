using System.Diagnostics;
using Xunit.Abstractions;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

/// <summary>性能基线（阶段 C）：用真实 Unity 缓存量化热点路径的耗时，
/// 为「先量化、再优化」提供数据。默认**跳过**——只有显式设置 LME_BENCH=1
/// 才运行（全缓存扫描约 20 秒）；断言只做健全性检查，不卡时间阈值，
/// 耗时通过 ITestOutputHelper 输出人工评估。</summary>
public class PerformanceBaselineTests(ITestOutputHelper output)
{
    private static (string gameDirectory, string cacheRoot) LocateRealEnvironment()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("LME_BENCH"), "1", StringComparison.Ordinal))
            return ("", "");
        var gameDirectory = Environment.GetEnvironmentVariable("LME_GAME_DIR") ??
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company";
        if (!File.Exists(Path.Combine(gameDirectory, "LimbusCompany.exe"))) return ("", "");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cacheRoot = Path.Combine(profile, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany");
        if (!Directory.Exists(cacheRoot)) return ("", "");
        return (gameDirectory, cacheRoot);
    }

    [Fact]
    public async Task Real_cache_hot_path_timings()
    {
        var (gameDirectory, cacheRoot) = LocateRealEnvironment();
        if (gameDirectory.Length == 0)
        {
            output.WriteLine("跳过：未设置 LME_BENCH=1 或真实环境不可用。");
            return;
        }

        var benchRoot = Path.Combine(Path.GetTempPath(), "lme-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(benchRoot);
        try
        {
            var projects = new ProjectService();
            var projectFile = Path.Combine(benchRoot, "Bench.lmeproj");
            var indexFile = Path.Combine(benchRoot, "unity-cache-index.json");

            // ① 冷扫描：全量解析缓存 bundle + 建 SQLite 索引库。
            // 阶段拆分：最后一次进度回调 ≈ 解析阶段结束（合并/持久化在其后）。
            var project = new ModProject { Name = "Bench" };
            var coldStopwatch = Stopwatch.StartNew();
            long parseDoneMs = 0;
            var progress = new SynchronousProgress<UnityCacheScanProgress>(_ => parseDoneMs = coldStopwatch.ElapsedMilliseconds);
            var result = await new UnityCacheScanService(indexFile).ScanIntoProjectAsync(project, cacheRoot, gameDirectory, progress);
            coldStopwatch.Stop();
            output.WriteLine($"① 冷扫描（{result.TotalEntries} bundle，新增 {result.AddedAssets} 资产，索引库 {new FileInfo(Path.ChangeExtension(indexFile, ".db")).Length / 1024.0 / 1024.0:F0} MB）: {coldStopwatch.ElapsedMilliseconds} ms（其中解析 ≈ {parseDoneMs} ms，合并+持久化 ≈ {coldStopwatch.ElapsedMilliseconds - parseDoneMs} ms）");
            Assert.True(project.Assets.Count > 100_000, $"期望真实缓存规模 > 10 万，实际 {project.Assets.Count}");

            // ② 热扫描：SQLite 索引命中（新鲜度检查 + 按行读回 + 合并）。
            var warmProject = new ModProject { Name = "Bench" };
            var warmStopwatch = Stopwatch.StartNew();
            await new UnityCacheScanService(indexFile).ScanIntoProjectAsync(warmProject, cacheRoot, gameDirectory);
            warmStopwatch.Stop();
            output.WriteLine($"② 热扫描（索引命中）: {warmStopwatch.ElapsedMilliseconds} ms");

            // ③ 项目保存（引用资产已不入文件）。
            var saveStopwatch = Stopwatch.StartNew();
            await projects.SaveAsync(project, projectFile);
            saveStopwatch.Stop();
            output.WriteLine($"③ 项目保存（{project.Assets.Count} 资产，文件 {new FileInfo(projectFile).Length / 1024.0:F0} KB）: {saveStopwatch.ElapsedMilliseconds} ms");

            // ④ 项目加载（旧项目文件里的引用资产也会加载，向后兼容）。
            var loadStopwatch = Stopwatch.StartNew();
            var loaded = await projects.LoadAsync(projectFile);
            loadStopwatch.Stop();
            output.WriteLine($"④ 项目加载（{loaded.Assets.Count} 资产）: {loadStopwatch.ElapsedMilliseconds} ms");

            // ⑤ 打开项目后的索引回灌（重建 119 万引用资产，不解析 bundle）。
            var rehydrateStopwatch = Stopwatch.StartNew();
            var rehydrated = await new UnityCacheScanService(indexFile).RehydrateFromIndexAsync(loaded);
            rehydrateStopwatch.Stop();
            output.WriteLine($"⑤ 索引回灌（重建 {rehydrated} 条引用资产）: {rehydrateStopwatch.ElapsedMilliseconds} ms");
            Assert.True(loaded.Assets.Count > 100_000);

            // ⑥ 全量搜索（无过滤词 = 纯枚举 + 排序，最坏情况）。
            var snapshot = loaded.Assets.ToArray();
            var searchStopwatch = Stopwatch.StartNew();
            var hits = new AssetSearchService().Search(snapshot, new AssetSearchQuery(null, null, null, null, null, null, null, null, null));
            searchStopwatch.Stop();
            output.WriteLine($"⑥ 全量搜索过滤+排序（{snapshot.Length} 资产 → {hits.Count} 命中）: {searchStopwatch.ElapsedMilliseconds} ms");

            // ⑦ 目录树根层构建（UI 打开树视图的即时成本）。
            var rootsStopwatch = Stopwatch.StartNew();
            var roots = AssetTreeBuilder.BuildRoots(snapshot);
            rootsStopwatch.Stop();
            output.WriteLine($"⑦ 树根层构建（{roots.Count} 个根）: {rootsStopwatch.ElapsedMilliseconds} ms");

            // ⑧ 树全展开成本（用户逐层展开完的最坏情况合计）。
            var expandStopwatch = Stopwatch.StartNew();
            var stack = new Stack<AssetTreeNode>(roots);
            var directories = 0;
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.IsLeaf) continue;
                directories++;
                foreach (var child in node.Expand()) stack.Push(child);
            }
            expandStopwatch.Stop();
            output.WriteLine($"⑧ 树全展开（{directories} 个目录节点）: {expandStopwatch.ElapsedMilliseconds} ms");
        }
        finally { try { Directory.Delete(benchRoot, true); } catch (Exception) { } }
    }

    /// <summary>测试内同步进度（Progress&lt;T&gt; 会异步封送到同步上下文，计时用）。</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
