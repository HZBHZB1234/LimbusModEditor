using System.IO;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Domain.Tests;

/// <summary>傻瓜化核心：游戏资源扫描（引用模式）+ 程序目录索引缓存增量
/// 复用 + 缓存 bundle 编辑实体化。真实缓存样本缺失时自动跳过。</summary>
public class UnityCacheScanServiceTests : IDisposable
{
    private readonly string _root;

    public UnityCacheScanServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lme-cachescan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* temp cleanup */ }
    }

    private static (string? CacheRoot, string? GameDirectory) FindRealEnvironment()
    {
        var gameDirectory = Environment.GetEnvironmentVariable("LME_GAME_DIR") ??
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company";
        if (!File.Exists(Path.Combine(gameDirectory, "LimbusCompany.exe"))) return (null, null);
        var candidates = new List<string?>();
        var overrideDir = Environment.GetEnvironmentVariable("LME_UNITY_CACHE_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir)) candidates.Add(overrideDir);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            candidates.Add(Path.Combine(profile, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany"));
        foreach (var candidate in candidates)
        {
            if (UnityCacheScanService.EnumerateCacheEntries(candidate).Count > 0) return (candidate, gameDirectory);
        }
        return (null, null);
    }

    [Fact]
    public void Enumerates_cache_v2_entries()
    {
        var cache = Path.Combine(_root, "cache");
        var entry = Path.Combine(cache, new string('a', 32), new string('b', 32));
        Directory.CreateDirectory(entry);
        File.WriteAllBytes(Path.Combine(entry, "__data"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(cache, "not-a-key"));

        var entries = UnityCacheScanService.EnumerateCacheEntries(cache);
        Assert.Single(entries);
        Assert.Equal(new string('a', 32), entries[0].OuterKey);
        Assert.Equal(new string('b', 32), entries[0].InnerKey);
        Assert.True(File.Exists(entries[0].DataPath));
    }

    [Fact]
    public async Task Real_cache_scan_registers_reference_assets_and_second_scan_uses_index()
    {
        var (realCache, gameDirectory) = FindRealEnvironment();
        if (realCache is null) return; // 无真实样本的机器自动跳过

        // 只取一个足够小的真实 bundle 建临时缓存，控制测试时间。
        var picked = EnumerateRealEntries(realCache)
            .FirstOrDefault(x => new FileInfo(x.DataPath).Length is > 0 and < 8 * 1024 * 1024);
        if (picked is null) return;
        var tempCache = Path.Combine(_root, "cache");
        var tempEntry = Path.Combine(tempCache, picked.OuterKey, picked.InnerKey);
        Directory.CreateDirectory(tempEntry);
        File.Copy(picked.DataPath, Path.Combine(tempEntry, "__data"));

        var indexFile = Path.Combine(_root, "index", "unity-cache-index.json");
        var service = new UnityCacheScanService(indexFile);
        var project = new ModProject { Name = "ScanTest" };
        var first = await service.ScanIntoProjectAsync(project, tempCache, gameDirectory);

        Assert.Equal(1, first.TotalEntries);
        Assert.True(first.AddedAssets > 0);
        Assert.All(project.Assets, asset =>
        {
            Assert.Equal("true", asset.Metadata["reference"]);
            Assert.Equal(picked.OuterKey, asset.Metadata["cacheOuter"]);
            Assert.Equal(picked.InnerKey, asset.Metadata["cacheInner"]);
            Assert.True(File.Exists(asset.SourcePath!));
        });
        Assert.True(File.Exists(Path.ChangeExtension(indexFile, ".db")), "扫描索引应写进指定路径（SQLite 索引库 .db）");

        // 第二次扫描走索引缓存：不再解析 bundle，结果等价。
        var project2 = new ModProject { Name = "ScanTest2" };
        var second = await service.ScanIntoProjectAsync(project2, tempCache, gameDirectory);
        Assert.Equal(0, second.ScannedBundles);
        Assert.Equal(1, second.IndexedBundles);
        Assert.Equal(first.AddedAssets, second.AddedAssets);
        Assert.Equal(
            project.Assets.Select(x => x.LogicalPath).OrderBy(x => x).ToArray(),
            project2.Assets.Select(x => x.LogicalPath).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Materializing_an_edited_asset_copies_its_bundle_and_repoints_siblings()
    {
        var (realCache, _) = FindRealEnvironment();
        if (realCache is null) return;
        var picked = EnumerateRealEntries(realCache)
            .FirstOrDefault(x => new FileInfo(x.DataPath).Length is > 0 and < 8 * 1024 * 1024);
        if (picked is null) return;
        var tempCache = Path.Combine(_root, "cache");
        var tempEntry = Path.Combine(tempCache, picked.OuterKey, picked.InnerKey);
        Directory.CreateDirectory(tempEntry);
        File.Copy(picked.DataPath, Path.Combine(tempEntry, "__data"));

        var project = new ModProject { Name = "Materialize" };
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        project.SourceDirectory = Path.Combine(projectRoot, "sources");
        await new UnityCacheScanService().ScanIntoProjectAsync(project, tempCache);

        var seed = project.Assets[0];
        var cachePath = seed.SourcePath!;
        var local = await UnityCacheMaterializationService.MaterializeForEditingAsync(project, seed, projectRoot);

        Assert.True(File.Exists(local));
        Assert.Contains(Path.Combine("sources", "cache"), local);
        Assert.EndsWith($"{picked.OuterKey}_{picked.InnerKey}.bundle", local);
        Assert.NotEqual(cachePath, seed.SourcePath);
        Assert.False(seed.Metadata.ContainsKey("reference"));
        Assert.True(seed.Metadata.ContainsKey("materialized"));
        // 同一 bundle 的全部资源都改指本地副本。
        Assert.All(project.Assets, asset => Assert.Equal(local, asset.SourcePath));
        // 二次实体化是 no-op。
        var again = await UnityCacheMaterializationService.MaterializeForEditingAsync(project, seed, projectRoot);
        Assert.Equal(local, again);
    }

    private static IEnumerable<UnityCacheScanEntry> EnumerateRealEntries(string realCache)
    {
        foreach (var outer in Directory.EnumerateDirectories(realCache))
        foreach (var inner in Directory.EnumerateDirectories(outer))
        {
            var data = Path.Combine(inner, "__data");
            if (File.Exists(data))
                yield return new UnityCacheScanEntry(Path.GetFileName(outer), Path.GetFileName(inner), data);
        }
    }
}
