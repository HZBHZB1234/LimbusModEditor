using System.Text.RegularExpressions;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Application.StaticMods;

/// <summary>静态数据 bundle 的定位结果。**每次动态解析**——官方热修会更换
/// content hash 并重写 catalog，任何 hash/偏移常量都不得硬编码或缓存。</summary>
public sealed record StaticBundleLocation(
    string BundleName,
    string InnerHash,
    string? OuterKey,
    CatalogBundleRecord? Record,
    string? CacheRoot = null,
    string? DataPath = null)
{
    public bool IsCached => !string.IsNullOrWhiteSpace(DataPath) && File.Exists(DataPath);

    /// <summary>给用户看的一行定位说明（缓存未命中时给出可执行的下一步）。</summary>
    public string Describe()
    {
        var outer = OuterKey is null ? "（catalog 未给出外层键）" : OuterKey;
        return IsCached
            ? $"静态数据 bundle：{BundleName}\n外层键 {outer}\n缓存文件 {DataPath}"
            : $"已从 catalog 定位静态数据 bundle：{BundleName}\n外层键 {outer}\n" +
              "但缓存里还没有这个条目 —— 请启动一次游戏让它下载/生成缓存后重试（编辑器只读缓存，不写游戏目录）。";
    }
}

/// <summary>
/// 静态数据 bundle（<c>static_s1_0_assets_all_&lt;32hex&gt;.bundle</c>）定位器
/// （plan-08 第 1 步）。事实来源：LCTA <c>launcher/staticmod.py:65,108-156</c> ——
/// 在 catalog 里按 bundle 名找 32 位内容哈希（= 缓存内层键），记录区 +0x10 是
/// 外层键；缓存条目为 <c>&lt;缓存根&gt;/&lt;外层键&gt;/&lt;内层键&gt;/__data</c>。
/// 复用既有 <see cref="CatalogFileService"/> 解析，不新写 catalog 解析。
/// </summary>
public static class StaticBundleLocator
{
    /// <summary>静态数据 bundle 名前缀（LCTA staticmod.py:65 同名约定）。</summary>
    public const string BundleNamePrefix = "static_s1_0_assets_all_";

    private static readonly Regex StaticBundlePattern = new(
        @"^static_s1_0_assets_all_([0-9a-fA-F]{32})\.bundle$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>catalog 文件候选名（<c>&lt;游戏&gt;/LimbusCompany_Data/StreamingAssets/aa/</c>）。</summary>
    private static readonly string[] CatalogFileNames = ["catalog.bin", "catalog_S1.bin"];

    /// <summary>找 catalog 文件（找不到返回 null，不猜路径）。</summary>
    public static string? FindCatalogPath(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return null;
        var directory = Path.Combine(gameDirectory, "LimbusCompany_Data", "StreamingAssets", "aa");
        if (!Directory.Exists(directory)) return null;
        foreach (var name in CatalogFileNames)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>从 catalog 定位静态数据 bundle（只读；解析失败返回 null）。</summary>
    public static StaticBundleLocation? LocateFromCatalog(string? catalogPath)
    {
        if (string.IsNullOrWhiteSpace(catalogPath) || !File.Exists(catalogPath)) return null;
        CatalogFileService catalog;
        try { catalog = CatalogFileService.Load(catalogPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
        return LocateFromCatalog(catalog);
    }

    /// <summary>从已解析的 catalog 定位静态数据 bundle。</summary>
    public static StaticBundleLocation? LocateFromCatalog(CatalogFileService catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        foreach (var name in catalog.Names)
        {
            var match = StaticBundlePattern.Match(name);
            if (!match.Success) continue;
            var innerHash = match.Groups[1].Value.ToLowerInvariant();
            var record = catalog.FindByInnerHash(innerHash);
            return new StaticBundleLocation(name, innerHash, record?.OuterKey, record);
        }
        return null;
    }

    /// <summary>catalog 中全部静态 bundle 的内层键集合（扫描时一次性算出，
    /// 之后按内层键 O(1) 判定；catalog 缺失/解析失败返回空集）。</summary>
    public static IReadOnlySet<string> StaticInnerHashes(CatalogFileService? catalog)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (catalog is null) return result;
        foreach (var name in catalog.Names)
        {
            var match = StaticBundlePattern.Match(name);
            if (match.Success) result.Add(match.Groups[1].Value.ToLowerInvariant());
        }
        return result;
    }

    /// <summary>在候选缓存根里找 <c>&lt;外层键&gt;/&lt;内层键&gt;/__data</c>；
    /// 外层键优先用 catalog 记录，缺失时按「任意外层键下存在该内层键」兜底
    /// （游戏更新会换外层键，catalog 记录可能滞后）。</summary>
    public static StaticBundleLocation LocateInCache(StaticBundleLocation location,
        IEnumerable<string?> cacheRoots)
    {
        ArgumentNullException.ThrowIfNull(location);
        foreach (var root in cacheRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            if (!string.IsNullOrWhiteSpace(location.OuterKey))
            {
                var direct = Path.Combine(root, location.OuterKey, location.InnerHash, "__data");
                if (File.Exists(direct)) return location with { CacheRoot = root, DataPath = direct };
            }
            foreach (var outer in EnumerateDirectoriesSafe(root))
            {
                var data = Path.Combine(outer, location.InnerHash, "__data");
                if (!File.Exists(data)) continue;
                return location with
                {
                    CacheRoot = root,
                    DataPath = data,
                    // 记录实际命中的外层键（catalog 记录可能滞后于缓存目录）。
                    OuterKey = Path.GetFileName(outer)
                };
            }
        }
        return location;
    }

    /// <summary>一步到位：catalog（缺省从游戏目录推导）→ 缓存根候选 → 定位。</summary>
    public static StaticBundleLocation? Locate(string? gameDirectory, IEnumerable<string?> cacheRoots)
    {
        var catalogPath = FindCatalogPath(gameDirectory);
        var location = LocateFromCatalog(catalogPath);
        if (location is null) return null;
        return LocateInCache(location, cacheRoots);
    }

    /// <summary>枚举静态 bundle 内的全部 TextAsset（class 49）：只读引用模式，
    /// 不复制文件、不写缓存。</summary>
    public static IReadOnlyList<UnityTextAsset> ReadTextAssets(StaticBundleLocation location,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!location.IsCached)
            throw new FileNotFoundException(
                $"静态数据 bundle 的缓存条目不存在（{location.BundleName}）：请启动一次游戏生成缓存后重试。",
                location.DataPath ?? string.Empty);
        var service = new UnityAssetService();
        var textAssets = new List<UnityTextAsset>();
        foreach (var descriptor in service.ScanBundle(location.DataPath!, cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (descriptor.Type != Domain.Assets.AssetType.Text || descriptor.UnityPathId is not { } pathId) continue;
            textAssets.Add(service.ReadBundleTextAsset(
                location.DataPath!, descriptor.ContainerPath!, pathId, cancellationToken));
        }
        return textAssets;
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string directory)
    {
        try { return Directory.EnumerateDirectories(directory); }
        catch (Exception) { return []; }
    }
}
