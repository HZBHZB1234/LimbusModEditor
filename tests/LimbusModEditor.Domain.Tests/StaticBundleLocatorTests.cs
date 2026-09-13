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
        var catalog = CatalogFileService.Parse(BuildCatalog($"audio_s1_0_assets_all_{InnerHash}.bundle", InnerHash, OuterKey));        Assert.Null(StaticBundleLocator.LocateFromCatalog(catalog));
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

    /// <summary>
    /// bundle 名兜底判定：不依赖 catalog 也能认出静态数据 bundle
    /// （修复「资源工作台仍然包含 static-data」——标记只由 catalog 判定产生，
    /// catalog 缺失/滞后时标记为假，页面于是又把 static-data 全列出来）。
    /// 只认带前缀的全名：缓存目录里只有内层哈希，裸 32hex 无法反推所属 bundle。
    /// </summary>
    [Theory]
    [InlineData("static_s1_0_assets_all_abcdef0123456789abcdef0123456789.bundle", true)]
    [InlineData("static_s1_0_assets_all_ABCDEF0123456789ABCDEF0123456789.bundle", true)] // 大小写
    [InlineData("static_s1_0_assets_all_abcdef0123456789abcdef0123456789", true)]       // 无扩展名
    [InlineData(@"C:\cache\64bd0105\static_s1_0_assets_all_abcdef0123456789abcdef0123456789.bundle", true)]
    [InlineData("abcdef0123456789abcdef0123456789", false)]                              // 裸内层哈希：不可判定
    [InlineData("audio_s1_0_assets_all_abcdef0123456789abcdef0123456789.bundle", false)]
    [InlineData("assets_all_abcdef0123456789abcdef0123456789.bundle", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Static_bundle_is_recognised_by_name_without_a_catalog(string? name, bool expected)
        => Assert.Equal(expected, StaticBundleLocator.LooksLikeStaticBundle(name));

    /// <summary>真实静态 bundle 名（本机 catalog 实测口径）：前缀 + 32 位内容哈希。</summary>
    private const string StaticBundleName = "static_s1_0_assets_all_abcdef0123456789abcdef0123456789.bundle";

    /// <summary>
    /// 关键回归：资源工作台在**没有** <c>staticBundle</c> 元数据标记时，
    /// 依然要把静态数据 bundle 的资源默认隐藏（走 bundle 名兜底）。
    /// </summary>
    [Fact]
    public void Default_search_hides_static_assets_even_without_the_metadata_flag()
    {
        var project = new ModProject { Name = "NoFlag" };
        // 模拟旧索引 / catalog 缺失的扫描结果：没有 staticBundle 标记。
        project.Assets.Add(new Domain.Assets.AssetRecord
        {
            LogicalPath = $"{OuterKey}/{InnerHash}/CAB-x/1.49",
            SourcePath = Path.Combine(_root, "nope", "__data"),
            ContainerPath = "CAB-x",
            Account = OuterKey,
            Bundle = StaticBundleName,
            UnityPathId = 1,
            UnityTypeId = 49,
            Type = Domain.Assets.AssetType.Text,
            Metadata = { ["unityBundle"] = "true" },
        });
        var search = new AssetSearchService();
        Assert.Empty(search.Search(project.Assets.ToArray(), new AssetSearchQuery()));
        Assert.Single(search.Search(project.Assets.ToArray(), new AssetSearchQuery(ShowStaticTables: true)));
    }

    /// <summary>
    /// 关键回归（2026-09 真实数据）：游戏更新换键后，旧版本静态 bundle 会以
    /// 「裸哈希目录」留在 Unity 缓存里（本机实测 <c>62d6e466…</c>（9/3）与 catalog 里的
    /// <c>fa6984…</c>（9/10）同外层键、同 CAB、内容同源）。这种资源的 bundle 名只是
    /// 32 位内容哈希、元数据标记也不会命中 —— 前两道判据同时失效，static-data 又出现在
    /// 资源工作台里。它的容器路径恒为
    /// <c>Assets/Resources_moved/StaticData/static-data/&lt;类&gt;/&lt;文件&gt;.json</c>，
    /// 判定必须靠这条不依赖 catalog 的路径事实把它隐藏。
    /// </summary>
    [Fact]
    public void Default_search_hides_stale_cache_static_assets_by_container_path()
    {
        var project = new ModProject { Name = "StaleCache" };
        project.Assets.Add(new Domain.Assets.AssetRecord
        {
            LogicalPath = $"{OuterKey}/62d6e466f528b73cf836882c2a786cc2/CAB-f0c1a8cf/1.49",
            SourcePath = Path.Combine(_root, "stale", "__data"),
            ContainerPath = "CAB-f0c1a8cf301ccdcbe041bb35c6769a32",
            Account = OuterKey,
            Bundle = "62d6e466f528b73cf836882c2a786cc2",
            UnityPathId = 1,
            UnityTypeId = 49,
            Type = Domain.Assets.AssetType.Text,
            Metadata =
            {
                ["unityBundle"] = "true",
                ["containerEntry"] = "Assets/Resources_moved/StaticData/static-data/battle-exp-dungeon/battle-exp-dungeon-03-a1c6p3.json",
            },
        });
        var search = new AssetSearchService();
        Assert.Empty(search.Search(project.Assets.ToArray(), new AssetSearchQuery()));
        Assert.Single(search.Search(project.Assets.ToArray(), new AssetSearchQuery(ShowStaticTables: true)));
    }

    /// <summary>反向保护：普通资源的容器路径前缀不是静态数据，不能被路径判据误伤。</summary>
    [Theory]
    [InlineData("Assets/Prefab/Unit/unit-01.prefab", false)]
    [InlineData("Assets/FX/effect.png", false)]
    [InlineData("Assets/Resources_moved/StaticData/static-data/item/item-02.json", true)]
    [InlineData(@"Assets\Resources_moved\StaticData\static-data\item\item-02.json", true)]
    [InlineData("", false)]
    public void Static_table_path_judgment_matches_only_static_data_paths(string containerEntry, bool expected)
        => Assert.Equal(expected, StaticBundleLocator.LooksLikeStaticTablePath(containerEntry));

    /// <summary>
    /// catalog 不可用时（本机没有 catalog），扫描必须**保留**静态 bundle 的标记，
    /// 而不是把已有标记清掉（早先的写法会清除，导致资源工作台又列出 static-data）。
    /// </summary>
    [Fact]
    public async Task Scan_keeps_static_flag_when_the_catalog_is_unavailable()
    {
        var bundle = FindSmallRealBundle();
        if (bundle is null) return; // 无真实缓存样本：跳过

        // 只有一个名字像静态 bundle 的缓存条目，**没有任何 catalog**。
        // 扫描索引只记内层哈希，但 AssetRecord.Bundle 保留的是条目目录名口径 →
        // 这里直接用静态全名当内层目录名，覆盖「名字可判」这条兜底路径。
        var cache = Path.Combine(_root, "cache-nocatalog");
        var entry = Path.Combine(cache, OuterKey, StaticBundleName);
        Directory.CreateDirectory(entry);
        File.Copy(bundle, Path.Combine(entry, "__data"));

        var project = new ModProject { Name = "NoCatalog" };
        var service = new UnityCacheScanService(Path.Combine(_root, "index2", "idx.json"));
        var result = await service.ScanIntoProjectAsync(project, cache, gameDirectory: null);

        Assert.True(result.AddedAssets > 0);
        // 元数据标记由 catalog 判定（此处无 catalog → 不写），但页面侧靠 bundle 名兜底隐藏。
        Assert.Empty(new AssetSearchService().Search(project.Assets.ToArray(), new AssetSearchQuery()));
        Assert.NotEmpty(new AssetSearchService().Search(project.Assets.ToArray(), new AssetSearchQuery(ShowStaticTables: true)));
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

    // ── catalog 来源（2026-09 修复：只读安装目录 catalog → 缓存永不命中）──

    /// <summary>本机复现过的真实形态：游戏安装目录的 catalog 与运行时 catalog
    /// 指向**不同**内容哈希，缓存里只有运行时那一份。</summary>
    private (string GameDirectory, string CacheRoot, string RuntimeInner, string InstallInner) BuildDivergedCatalogs()
    {
        var localLow = Path.Combine(_root, "LocalLow");
        var cacheRoot = Path.Combine(localLow, "Unity", "ProjectMoon_LimbusCompany");
        var runtimeDirectory = Path.Combine(localLow, "ProjectMoon", "LimbusCompany", "com.unity.addressables");
        var installDirectory = Path.Combine(_root, "game", "LimbusCompany_Data", "StreamingAssets", "aa");
        Directory.CreateDirectory(runtimeDirectory);
        Directory.CreateDirectory(installDirectory);

        const string runtimeInner = "62d6e466f528b73cf836882c2a786cc2";
        const string installInner = "edb72aecf54cf3d92152f862727b6819";
        File.WriteAllBytes(Path.Combine(runtimeDirectory, "catalog_S1.bin"),
            BuildCatalog($"static_s1_0_assets_all_{runtimeInner}.bundle", runtimeInner, OuterKey));
        File.WriteAllBytes(Path.Combine(installDirectory, "catalog.bin"),
            BuildCatalog($"static_s1_0_assets_all_{installInner}.bundle", installInner, OuterKey));

        // 缓存里只有运行时 catalog 的条目。
        var entry = Path.Combine(cacheRoot, OuterKey, runtimeInner);
        Directory.CreateDirectory(entry);
        File.WriteAllBytes(Path.Combine(entry, "__data"), [1, 2, 3]);
        return (Path.Combine(_root, "game"), cacheRoot, runtimeInner, installInner);
    }

    [Fact]
    public void Runtime_catalog_is_preferred_over_the_install_directory_catalog()
    {
        var (gameDirectory, cacheRoot, runtimeInner, _) = BuildDivergedCatalogs();

        Assert.Equal(Path.Combine(_root, "LocalLow"), StaticBundleLocator.LocalLowBaseFromCacheRoot(cacheRoot));
        var candidates = StaticBundleLocator.FindCatalogCandidates(gameDirectory, [cacheRoot]);
        Assert.Equal(2, candidates.Count);
        Assert.Contains(Path.Combine("ProjectMoon", "LimbusCompany", "com.unity.addressables"), candidates[0]);
        Assert.EndsWith("catalog.bin", candidates[1]); // 安装目录只作兜底

        // 关键回归：定位必须落在**缓存真正存在**的那份 catalog 上。
        var location = StaticBundleLocator.Locate(gameDirectory, [cacheRoot]);
        Assert.NotNull(location);
        Assert.True(location!.IsCached);
        Assert.Equal(runtimeInner, location.InnerHash);
        Assert.Equal(Path.Combine(cacheRoot, OuterKey, runtimeInner, "__data"), location.DataPath);
        Assert.Equal(candidates[0], location.CatalogPath);
        Assert.Contains("catalog", location.Describe());
    }

    [Fact]
    public void Install_catalog_is_still_used_when_the_runtime_entry_is_not_cached()
    {
        var (gameDirectory, cacheRoot, runtimeInner, installInner) = BuildDivergedCatalogs();
        Directory.Delete(Path.Combine(cacheRoot, OuterKey, runtimeInner), true);
        var installEntry = Path.Combine(cacheRoot, OuterKey, installInner);
        Directory.CreateDirectory(installEntry);
        File.WriteAllBytes(Path.Combine(installEntry, "__data"), [4, 5, 6]);

        var location = StaticBundleLocator.Locate(gameDirectory, [cacheRoot]);
        Assert.NotNull(location);
        Assert.True(location!.IsCached);
        Assert.Equal(installInner, location.InnerHash);
    }

    [Fact]
    public void Missing_runtime_cache_still_reports_the_runtime_located_bundle()
    {
        var (gameDirectory, cacheRoot, runtimeInner, _) = BuildDivergedCatalogs();
        Directory.Delete(Path.Combine(cacheRoot, OuterKey, runtimeInner), true);

        var location = StaticBundleLocator.Locate(gameDirectory, [cacheRoot]);
        Assert.NotNull(location);
        Assert.False(location!.IsCached);
        Assert.Equal(runtimeInner, location.InnerHash); // 第一个成功定位者（运行时）作为提示
        Assert.Contains("启动一次游戏", location.Describe());
    }

    /// <summary>缓存根候选：配置根在前、迁移盘兜底在后，且不重复。</summary>
    [Fact]
    public void Migrated_cache_root_is_appended_once_when_present()
    {
        var configured = Path.Combine(_root, "cache");
        var roots = StaticBundleLocator.WithMigratedCacheRoot([configured, configured, null]);
        Assert.Equal(configured, roots[0]);
        Assert.Equal(roots.Distinct(StringComparer.OrdinalIgnoreCase).Count(), roots.Count);
        if (OperatingSystem.IsWindows() && Directory.Exists(StaticBundleLocator.MigratedCacheRoot))
        {
            Assert.Contains(StaticBundleLocator.MigratedCacheRoot, roots);
            // 已显式给出迁移盘根时不重复追加。
            var explicitRoots = StaticBundleLocator.WithMigratedCacheRoot([StaticBundleLocator.MigratedCacheRoot]);
            Assert.Single(explicitRoots);
        }
    }

    /// <summary>dataClass/fileName 由容器路径推导（静态表 m_Name 不含 '/'，分类只在
    /// 容器路径里）；旧式 m_Name 仍作兜底。</summary>
    [Theory]
    [InlineData("Assets/Resources_moved/StaticData/static-data/stagenodereward/stagenodereward91-12.json",
        "stagenodereward91-12", "stagenodereward", "stagenodereward91-12")]
    [InlineData("Assets/Resources_moved/StaticData/static-data/skill/assistant-skill-a1c7p2.json",
        "assistant-skill-a1c7p2", "skill", "assistant-skill-a1c7p2")]
    [InlineData("Assets/StaticData/static-data/event/walpu8-mission.json",
        "walpu8-mission", "event", "walpu8-mission")]
    // 反斜杠分隔与大小写不敏感由调用方按原样传递，路径分段本身要归一化。
    [InlineData(@"Assets\Resources_moved\StaticData\static-data\buff\buff-enemy-a1c7p1.json",
        "buff-enemy-a1c7p1", "buff", "buff-enemy-a1c7p1")]
    // 旧式 m_Name（"dataClass/file"）在无容器时兜底。
    [InlineData("", "event-mission/walpu8-mission", "event-mission", "walpu8-mission")]
    // 裸名无容器：无法分类 → 未分组。
    [InlineData("", "loose-table", "未分组", "loose-table")]
    // 容器只有文件名（无目录）时退回 m_Name 兜底。
    [InlineData("only-name.json", "only-name", "未分组", "only-name")]
    public void Table_identity_is_derived_from_the_container_path(string container, string name,
        string expectedClass, string expectedFile)
    {
        var (dataClass, fileName) = StaticBundleLocator.SplitTableIdentity(container, name);
        Assert.Equal(expectedClass, dataClass);
        Assert.Equal(expectedFile, fileName);
    }

    /// <summary>真实环境端到端：运行时 catalog → 缓存 <c>__data</c> 命中（本机
    /// 复现的正是这条链路）。无游戏/catalog 时跳过。</summary>
    [Fact]
    public void Real_runtime_catalog_locates_a_cached_static_bundle_when_available()
    {
        var cacheRoot = Environment.GetEnvironmentVariable("LME_UNITY_CACHE_DIR");
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile)) return;
            cacheRoot = Path.Combine(profile, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany");
        }
        if (!Directory.Exists(cacheRoot)) return;
        var runtimeCatalog = StaticBundleLocator.RuntimeCatalogPath(cacheRoot);
        if (runtimeCatalog is null) return; // 本机没有运行时 catalog：跳过

        var location = StaticBundleLocator.Locate(null, [cacheRoot]);
        Assert.NotNull(location);
        Assert.StartsWith(StaticBundleLocator.BundleNamePrefix, location!.BundleName);
        Assert.Equal(runtimeCatalog, location.CatalogPath);
        if (!location.IsCached) return; // 缓存确实没有该条目：只验证定位来源
        Assert.True(File.Exists(location.DataPath));
        Assert.Equal(location.InnerHash, Path.GetFileName(Path.GetDirectoryName(location.DataPath)));

        // 真实 bundle：分类/表名来自容器路径（m_Name 不含 '/'，否则全部落「未分组」）。
        var tables = StaticBundleLocator.ReadTextAssetEntries(location);
        Assert.NotEmpty(tables);
        var withContainer = tables.Where(t => !string.IsNullOrWhiteSpace(t.ContainerEntry)).ToList();
        if (withContainer.Count == 0) return;
        Assert.DoesNotContain("未分组", withContainer.Select(t => t.DataClass));
        Assert.All(withContainer, table =>
            Assert.Equal(Path.GetFileNameWithoutExtension(table.ContainerEntry.Replace('\\', '/')), table.FileName));
        Assert.True(withContainer.Select(t => t.DataClass).Distinct(StringComparer.Ordinal).Count() > 1);
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
