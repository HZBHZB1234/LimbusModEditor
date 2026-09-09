using System.Text;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

/// <summary>plan-08 第 1-2 步：静态数据 bundle 定位（catalog 名匹配 → 内层
/// hash + 外层键 → 缓存 __data）、扫描打标记、资源工作台默认过滤。
/// 合成样本覆盖两条分支；真实环境（有游戏目录/catalog）额外验证动态解析。</summary>
public class StaticBundleLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-static-" + Guid.NewGuid().ToString("N"));

    public StaticBundleLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* temp cleanup */ }
    }

    private const string InnerHash = "abcdef0123456789abcdef0123456789";
    private const string OuterKey = "0123456789abcdeffedcba9876543210";

    /// <summary>合成 catalog：静态 bundle 名 + 记录区（Hash128 → +0x10 外层键
    /// → +0x3C CRC → +0x40 大小），与 CatalogBaselineTests 同一布局约定。</summary>
    private static byte[] BuildCatalog(string bundleName, string innerHash, string outerKey, uint size = 2_257_695)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("\x42\x89\xe3\x0d padding\n"));
        writer.Write(Encoding.ASCII.GetBytes(bundleName));
        writer.Write((byte)0);
        writer.Write((byte)0);
        var hit = (int)stream.Position;
        var hash = new byte[16];
        for (var i = 0; i < 16; i++) hash[i] = Convert.ToByte(innerHash.Substring(i * 2, 2), 16);
        writer.Write(hash);
        writer.Write((uint)outerKey.Length);
        writer.Write(Encoding.ASCII.GetBytes(outerKey));
        while (stream.Position < hit + 0x3C) writer.Write((byte)0);
        writer.Write(0xDEADBEEFu);
        writer.Write(size);
        writer.Flush();
        return stream.ToArray();
    }

    [Fact]
    public void Catalog_locates_static_bundle_and_inner_hash_set()
    {
        var catalog = CatalogFileService.Parse(BuildCatalog($"static_s1_0_assets_all_{InnerHash}.bundle", InnerHash, OuterKey));
        var location = StaticBundleLocator.LocateFromCatalog(catalog);

        Assert.NotNull(location);
        Assert.Equal($"static_s1_0_assets_all_{InnerHash}.bundle", location!.BundleName);
        Assert.Equal(InnerHash, location.InnerHash);
        Assert.Equal(OuterKey, location.OuterKey);
        Assert.False(location.IsCached);

        var hashes = StaticBundleLocator.StaticInnerHashes(catalog);
        Assert.Contains(InnerHash, hashes);
        Assert.Single(hashes);
    }

    [Fact]
    public void Non_static_bundle_names_are_not_matched()
    {
        var catalog = CatalogFileService.Parse(BuildCatalog($"audio_s1_0_assets_all_{InnerHash}.bundle", InnerHash, OuterKey));
        Assert.Null(StaticBundleLocator.LocateFromCatalog(catalog));
        Assert.Empty(StaticBundleLocator.StaticInnerHashes(catalog));
    }

    [Fact]
    public void Cache_hit_and_miss_branches_are_both_handled()
    {
        var catalog = CatalogFileService.Parse(BuildCatalog($"static_s1_0_assets_all_{InnerHash}.bundle", InnerHash, OuterKey));
        var located = StaticBundleLocator.LocateFromCatalog(catalog)!;

        // 未命中：明确提示启动一次游戏生成缓存，而不是抛异常。
        var miss = StaticBundleLocator.LocateInCache(located, [Path.Combine(_root, "empty-cache")]);
        Assert.False(miss.IsCached);
        Assert.Contains("启动一次游戏", miss.Describe());

        // 命中：<外层键>/<内层键>/__data。
        var cache = Path.Combine(_root, "cache");
        var entry = Path.Combine(cache, OuterKey, InnerHash);
        Directory.CreateDirectory(entry);
        File.WriteAllBytes(Path.Combine(entry, "__data"), [1, 2, 3]);
        var hit = StaticBundleLocator.LocateInCache(located, [cache]);
        Assert.True(hit.IsCached);
        Assert.Equal(Path.Combine(entry, "__data"), hit.DataPath);
    }

    [Fact]
    public void Cache_lookup_falls_back_to_any_outer_key_when_catalog_key_is_stale()
    {
        var catalog = CatalogFileService.Parse(BuildCatalog($"static_s1_0_assets_all_{InnerHash}.bundle", InnerHash, OuterKey));
        var located = StaticBundleLocator.LocateFromCatalog(catalog)! with { OuterKey = new string('9', 32) };
        var cache = Path.Combine(_root, "cache2");
        var entry = Path.Combine(cache, new string('7', 32), InnerHash);
        Directory.CreateDirectory(entry);
        File.WriteAllBytes(Path.Combine(entry, "__data"), [1, 2, 3]);

        var hit = StaticBundleLocator.LocateInCache(located, [cache]);
        Assert.True(hit.IsCached);
        Assert.Equal(new string('7', 32), hit.OuterKey); // 用实际命中的外层键修正
    }

    [Fact]
    public async Task Scan_marks_static_bundle_assets_and_default_search_hides_them()
    {
        var bundle = FindSmallRealBundle();
        if (bundle is null) return; // 无真实缓存样本：跳过

        // 临时「游戏目录」只提供 catalog；缓存条目用真实 bundle 字节，内层键
        // 取 catalog 里的静态 hash，从而走「按内层键 O(1) 判定」这条路径。
        var gameDirectory = Path.Combine(_root, "game");
        var aa = Path.Combine(gameDirectory, "LimbusCompany_Data", "StreamingAssets", "aa");
        Directory.CreateDirectory(aa);
        File.WriteAllBytes(Path.Combine(aa, "catalog.bin"),
            BuildCatalog($"static_s1_0_assets_all_{InnerHash}.bundle", InnerHash, OuterKey));

        var cache = Path.Combine(_root, "cache3");
        var entry = Path.Combine(cache, OuterKey, InnerHash);
        Directory.CreateDirectory(entry);
        File.Copy(bundle, Path.Combine(entry, "__data"));

        var project = new ModProject { Name = "StaticScan" };
        var service = new UnityCacheScanService(Path.Combine(_root, "index", "idx.json"));
        var result = await service.ScanIntoProjectAsync(project, cache, gameDirectory);

        Assert.True(result.AddedAssets > 0);
        Assert.All(project.Assets, asset => Assert.Equal("true", asset.Metadata[UnityCacheScanService.StaticBundleMetadataKey]));

        // 资源工作台默认视图（不传 ShowStaticTables）看不到静态资源。
        var search = new AssetSearchService();
        Assert.Empty(search.Search(project.Assets.ToArray(), new AssetSearchQuery()));
        // 勾选「显示静态数据表」后可见。
        var shown = search.Search(project.Assets.ToArray(), new AssetSearchQuery(ShowStaticTables: true));
        Assert.Equal(project.Assets.Count, shown.Count);

        // 回灌（打开旧项目）后标记必须仍在：索引持久化了 static_bundle。
        var rehydrated = new ModProject { Name = "Rehydrate" };
        var added = await service.RehydrateFromIndexAsync(rehydrated);
        Assert.Equal(project.Assets.Count, added);
        Assert.All(rehydrated.Assets, asset => Assert.Equal("true", asset.Metadata[UnityCacheScanService.StaticBundleMetadataKey]));
        Assert.Empty(search.Search(rehydrated.Assets.ToArray(), new AssetSearchQuery()));
    }

    [Fact]
    public void Real_catalog_is_parsed_dynamically_when_the_game_is_installed()
    {
        var gameDirectory = Environment.GetEnvironmentVariable("LME_GAME_DIR") ??
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company";
        var catalogPath = StaticBundleLocator.FindCatalogPath(gameDirectory);
        if (catalogPath is null) return; // 无游戏目录：跳过

        var location = StaticBundleLocator.LocateFromCatalog(catalogPath);
        Assert.NotNull(location);
        Assert.StartsWith(StaticBundleLocator.BundleNamePrefix, location!.BundleName);
        Assert.Matches("^[0-9a-f]{32}$", location.InnerHash);
        Assert.NotNull(location.OuterKey); // catalog 记录区给出的外层键
        Assert.Matches("^[0-9a-f]{32}$", location.OuterKey!);
        // 不硬编码 hash：同一机器两次解析结果一致，且与 catalog 名一致。
        Assert.Contains(location.InnerHash, location.BundleName, StringComparison.Ordinal);
    }

    private static string? FindSmallRealBundle()
    {
        var overrideDir = Environment.GetEnvironmentVariable("LME_UNITY_CACHE_DIR");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string?>();
        if (!string.IsNullOrWhiteSpace(overrideDir)) candidates.Add(overrideDir);
        if (!string.IsNullOrWhiteSpace(profile))
            candidates.Add(Path.Combine(profile, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany"));
        foreach (var root in candidates)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            foreach (var entry in UnityCacheScanService.EnumerateCacheEntries(root))
            {
                var info = new FileInfo(entry.DataPath);
                if (info.Length is > 0 and < 4 * 1024 * 1024) return entry.DataPath;
            }
        }
        return null;
    }
}
