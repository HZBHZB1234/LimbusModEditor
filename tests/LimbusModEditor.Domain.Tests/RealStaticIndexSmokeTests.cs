using System.Diagnostics;
using LimbusModEditor.Application.StaticMods;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// plan-12：静态表索引的真实环境门控测试（无游戏缓存 / catalog 的机器自动跳过）。
///
/// <para>验证：① 从 catalog 动态定位 + 枚举全部静态表并建索引；② 二次进页面<b>不再枚举 bundle</b>；
/// ③ 正文按需加载并与<b>现读</b>结果一致；④ <b>正文缓存有界</b>——只缓存打开过的表，
/// 不是把 1392 张表的正文都落库（那是 GB 级）。</para>
/// </summary>
public sealed class RealStaticIndexSmokeTests : IDisposable
{
    private readonly string _cacheDir;

    public RealStaticIndexSmokeTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "lme-static-index-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_cacheDir, true); } catch (Exception) { /* 临时目录 */ }
    }

    private static (string? GameDirectory, IReadOnlyList<string> CacheRoots) ResolveEnvironment()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company",
            @"E:\SteamLibrary\steamapps\common\Limbus Company",
        };
        var gameDirectory = Environment.GetEnvironmentVariable("LME_GAME_DIR") is { Length: > 0 } overrideGame
            ? overrideGame
            : candidates.FirstOrDefault(Directory.Exists);
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "..", "LocalLow", "Unity", "ProjectMoon_LimbusCompany");
        return (gameDirectory, StaticIndexService.CacheRoots(cacheRoot));
    }

    [Fact]
    public async Task Real_static_bundle_index_is_built_reused_and_documents_load_on_demand()
    {
        var (gameDirectory, cacheRoots) = ResolveEnvironment();
        var location = StaticIndexService.Locate(gameDirectory, cacheRoots);
        if (location is null || !location.IsCached) return; // 无真实环境：跳过

        var store = new StaticTableIndexStore(_cacheDir);
        var service = new StaticIndexService(store);
        var source = StaticIndexSource.From(location);

        // ① 冷建索引：枚举 bundle 内全部 TextAsset（只留元数据）。
        var reports = new List<StaticIndexProgress>();
        var cold = Stopwatch.StartNew();
        var result = await service.RebuildAsync(location, source, new Progress<StaticIndexProgress>(reports.Add));
        cold.Stop();

        Assert.True(result.DiskCacheHit);
        Assert.True(result.TableCount >= 1000, $"真实静态表应有上千张，实际 {result.TableCount}");
        Assert.True(result.TotalBytes > 10 * 1024 * 1024, $"正文合计应达到 MB 级，实际 {result.TotalBytes} 字节");
        Assert.NotEmpty(reports);
        Assert.Equal(result.TableCount, store.ReadTableCount());

        // dataClass 分组（🗂 树视图的骨架）必须真的分出来了。
        var entries = store.ReadEntries();
        var groups = entries.GroupBy(x => x.DataClass).ToList();
        Assert.True(groups.Count > 10, $"数据类应有多组，实际 {groups.Count}");
        Assert.DoesNotContain(entries, x => string.IsNullOrWhiteSpace(x.DataClass));
        Assert.All(entries, x => Assert.Equal(x.FileName, Path.GetFileNameWithoutExtension(x.ContainerEntry)));

        // ② 二次进页面：索引直接可用（不重新枚举 bundle）。
        var load = service.Load(source);
        Assert.True(load.IsUsable);
        Assert.Equal(entries.Count, load.Entries.Count);

        // ③ 正文按需加载，且与「现读」一致（同一张表两种路径结果相同）。
        var sample = entries.Where(x => x.IsUtf8).OrderByDescending(x => x.SizeBytes).First();
        var document = await service.LoadDocumentAsync(location, sample);
        Assert.NotNull(document.Text);
        Assert.Equal(sample.SizeBytes, System.Text.Encoding.UTF8.GetByteCount(document.Text!));
        Assert.True(document.Text!.Length > 1000, "取最大的表应是真的内容");

        var reread = await service.LoadDocumentAsync(location, sample);
        Assert.True(reread.FromCache, "第二次读取应命中正文缓存");
        Assert.Equal(document.Text, reread.Text);

        // ④ 正文缓存有界：只缓存了打开过的那几张表，不是全量。
        var usage = store.ReadDocumentCacheUsage();
        Assert.Equal(1, usage.Count);
        Assert.True(usage.Bytes < result.TotalBytes / 10,
            $"正文缓存（{usage.Bytes} 字节）不应接近全部正文（{result.TotalBytes} 字节）");

        Console.WriteLine(
            $"静态表索引：{result.TableCount} 张表 · 正文合计 {result.TotalBytes / 1024.0 / 1024.0:0.0} MB · " +
            $"数据类 {groups.Count} 组 · 冷建索引 {cold.Elapsed.TotalSeconds:0.0}s · " +
            $"读索引 {load.ReadElapsed.TotalMilliseconds:F0}ms · 正文缓存 {usage.Count} 张 / {usage.Bytes / 1024.0:0.0} KB");
    }

    [Fact]
    public async Task Real_static_index_warm_read_is_fast_and_does_not_touch_the_bundle()
    {
        var (gameDirectory, cacheRoots) = ResolveEnvironment();
        var location = StaticIndexService.Locate(gameDirectory, cacheRoots);
        if (location is null || !location.IsCached) return;

        var store = new StaticTableIndexStore(_cacheDir);
        var service = new StaticIndexService(store);
        var source = StaticIndexSource.From(location);
        await service.RebuildAsync(location, source);
        Assert.True(store.ReadTableCount() > 0);

        // 冷路径的代价：直接枚举 bundle（不落库）——用来对比。
        var bundleWatch = Stopwatch.StartNew();
        var live = StaticBundleLocator.ReadTextAssetEntries(location);
        bundleWatch.Stop();

        var warmWatch = Stopwatch.StartNew();
        var load = service.Load(source);
        warmWatch.Stop();

        Assert.Equal(live.Count, load.Entries.Count);
        Assert.True(warmWatch.Elapsed < TimeSpan.FromSeconds(1.5),
            $"热读索引 {warmWatch.Elapsed.TotalMilliseconds:F0}ms，超出 1.5s 预算");
        Assert.True(warmWatch.Elapsed < bundleWatch.Elapsed / 2,
            $"热读（{warmWatch.ElapsedMilliseconds}ms）应显著快于直接枚举 bundle（{bundleWatch.ElapsedMilliseconds}ms）");

        Console.WriteLine(
            $"静态表：直接枚举 bundle {bundleWatch.Elapsed.TotalSeconds:0.0}s（{live.Count} 张）→ " +
            $"读索引 {warmWatch.Elapsed.TotalMilliseconds:F0}ms（{load.Entries.Count} 张）");
    }
}
