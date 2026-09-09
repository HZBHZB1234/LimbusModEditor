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
    string? DataPath = null,
    string? CatalogPath = null)
{
    public bool IsCached => !string.IsNullOrWhiteSpace(DataPath) && File.Exists(DataPath);

    /// <summary>给用户看的一行定位说明（缓存未命中时给出可执行的下一步）。
    /// 同时写明 catalog 来源——游戏安装目录里的 catalog 与运行时 catalog
    /// 可能不是同一份（热修后内容哈希不同），定位错源会指向不存在的缓存条目。</summary>
    public string Describe()
    {
        var outer = OuterKey is null ? "（catalog 未给出外层键）" : OuterKey;
        var catalog = string.IsNullOrWhiteSpace(CatalogPath) ? string.Empty : $"\ncatalog {CatalogPath}";
        return IsCached
            ? $"静态数据 bundle：{BundleName}\n外层键 {outer}{catalog}\n缓存文件 {DataPath}"
            : $"已从 catalog 定位静态数据 bundle：{BundleName}\n外层键 {outer}{catalog}\n" +
              "但缓存里还没有这个条目 —— 请启动一次游戏让它下载/生成缓存后重试（编辑器只读缓存，不写游戏目录）。";
    }
}

/// <summary>
/// 静态数据 bundle（<c>static_s1_0_assets_all_&lt;32hex&gt;.bundle</c>）定位器
/// （plan-08 第 1 步）。事实来源：LCTA <c>launcher/staticmod.py:65,108-156,466-469</c> ——
/// 在 catalog 里按 bundle 名找 32 位内容哈希（= 缓存内层键），记录区 +0x10 是
/// 外层键；缓存条目为 <c>&lt;缓存根&gt;/&lt;外层键&gt;/&lt;内层键&gt;/__data</c>。
/// 复用既有 <see cref="CatalogFileService"/> 解析，不新写 catalog 解析。
///
/// <para>catalog 来源（2026-09 修正）：游戏**运行时**读的是
/// <c>%LOCALAPPDATA%\..\LocalLow\ProjectMoon\LimbusCompany\com.unity.addressables\catalog_S1.bin</c>
/// （LCTA <c>staticmod.py:466-469</c> 的 <c>_catalog_path()</c>，也是加载器双写的目标），
/// 游戏安装目录 <c>&lt;游戏&gt;/LimbusCompany_Data/StreamingAssets/aa/catalog.bin</c>
/// 只是随包目录。两者在热修后可能指向**不同的内容哈希**（实测：安装目录给
/// <c>edb72aec…</c> 而运行时给 <c>62d6e466…</c>，缓存里只有后者）——只读安装目录
/// 会「定位成功但缓存永远不命中」。因此按「运行时 catalog → 安装目录 catalog」
/// 排序逐个尝试，并优先返回**缓存真正命中**的那一个。</para>
/// </summary>
public static class StaticBundleLocator
{
    /// <summary>静态数据 bundle 名前缀（LCTA staticmod.py:65 同名约定）。</summary>
    public const string BundleNamePrefix = "static_s1_0_assets_all_";

    private static readonly Regex StaticBundlePattern = new(
        @"^static_s1_0_assets_all_([0-9a-fA-F]{32})\.bundle$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>catalog 文件候选名（运行时目录与安装目录同名候选）。</summary>
    private static readonly string[] CatalogFileNames = ["catalog.bin", "catalog_S1.bin"];

    /// <summary>由 Unity 缓存根推导 LocalLow 根：
    /// <c>&lt;LocalLow&gt;/Unity/ProjectMoon_LimbusCompany</c> → <c>&lt;LocalLow&gt;</c>。
    /// 路径层级不符（缓存被迁到 D:\Unity 之类）时返回 null，不猜。</summary>
    public static string? LocalLowBaseFromCacheRoot(string? cacheRoot)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot)) return null;
        string full;
        try { full = Path.GetFullPath(cacheRoot); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var current = new DirectoryInfo(Path.TrimEndingDirectorySeparator(full));
        if (string.Equals(current.Name, "ProjectMoon_LimbusCompany", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.Parent?.Name, "Unity", StringComparison.OrdinalIgnoreCase))
            return current.Parent?.Parent?.FullName;
        return null;
    }

    /// <summary>运行时 Addressables catalog（游戏实际读取 + 加载器双写的那一份）。</summary>
    public static string? RuntimeCatalogPath(string? cacheRoot)
    {
        var localLow = LocalLowBaseFromCacheRoot(cacheRoot);
        if (localLow is null) return null;
        return FirstExistingCatalog(Path.Combine(localLow, "ProjectMoon", "LimbusCompany", "com.unity.addressables"));
    }

    /// <summary>catalog 候选路径，按可信度排序去重：运行时 catalog（缓存根推导）
    /// → 游戏安装目录 StreamingAssets/aa。</summary>
    public static IReadOnlyList<string> FindCatalogCandidates(string? gameDirectory, IEnumerable<string?>? cacheRoots)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
            if (!File.Exists(full)) return;
            if (seen.Add(full)) result.Add(full);
        }

        foreach (var root in cacheRoots ?? [])
            Add(RuntimeCatalogPath(root));
        if (!string.IsNullOrWhiteSpace(gameDirectory))
        {
            var installDirectory = Path.Combine(gameDirectory, "LimbusCompany_Data", "StreamingAssets", "aa");
            foreach (var name in CatalogFileNames) Add(Path.Combine(installDirectory, name));
        }
        return result;
    }

    private static string? FirstExistingCatalog(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        foreach (var name in CatalogFileNames)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>找 catalog 文件（找不到返回 null，不猜路径）。优先运行时 catalog，
    /// 需缓存根时请用 <see cref="FindCatalogCandidates"/>。</summary>
    public static string? FindCatalogPath(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return null;
        return FirstExistingCatalog(Path.Combine(gameDirectory, "LimbusCompany_Data", "StreamingAssets", "aa"));
    }

    /// <summary>从 catalog 定位静态数据 bundle（只读；解析失败返回 null）。</summary>
    public static StaticBundleLocation? LocateFromCatalog(string? catalogPath)
    {
        if (string.IsNullOrWhiteSpace(catalogPath) || !File.Exists(catalogPath)) return null;
        CatalogFileService catalog;
        try { catalog = CatalogFileService.Load(catalogPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
        return LocateFromCatalog(catalog, catalogPath);
    }

    /// <summary>从已解析的 catalog 定位静态数据 bundle。</summary>
    public static StaticBundleLocation? LocateFromCatalog(CatalogFileService catalog, string? catalogPath = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        foreach (var name in catalog.Names)
        {
            var match = StaticBundlePattern.Match(name);
            if (!match.Success) continue;
            var innerHash = match.Groups[1].Value.ToLowerInvariant();
            var record = catalog.FindByInnerHash(innerHash);
            return new StaticBundleLocation(name, innerHash, record?.OuterKey, record, CatalogPath: catalogPath);
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

    /// <summary>一步到位：catalog 候选（运行时 → 安装目录）→ 缓存根候选 → 定位。
    ///
    /// <para>逐个 catalog 尝试，**优先返回缓存真正命中**的那一个：安装目录的 catalog
    /// 可能比运行时 catalog 旧（热修后内容哈希不同），若只认第一个「定位成功」的
    /// catalog，就会指向一个缓存里根本不存在的内层键，表现为「永远缓存未命中」。
    /// 全部 catalog 都未命中缓存时，返回第一个成功定位的结果（保留可执行提示）。</para>
    ///
    /// <para>本方法只认调用方给出的 <paramref name="cacheRoots"/>（纯函数，便于测试与
    /// 可预测性）；游戏缓存被迁移/双写到其它盘（如 <c>D:\Unity\…</c>）时由 UI 层
    /// 追加候选根（见 <see cref="StaticWorkbenchPage"/> 与 LCTA <c>staticmod.py:418-425</c>）。</para>
    /// </summary>
    public static StaticBundleLocation? Locate(string? gameDirectory, IEnumerable<string?> cacheRoots)
    {
        ArgumentNullException.ThrowIfNull(cacheRoots);
        var roots = cacheRoots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(root => root!).Distinct().ToList();
        StaticBundleLocation? firstLocated = null;
        foreach (var catalogPath in FindCatalogCandidates(gameDirectory, roots))
        {
            var location = LocateFromCatalog(catalogPath);
            if (location is null) continue;
            firstLocated ??= location;
            var resolved = LocateInCache(location, roots);
            if (resolved.IsCached) return resolved;
        }
        return firstLocated;
    }

    /// <summary>缓存根候选：给定根 + LCTA 事实中的 <c>D:\Unity\ProjectMoon_LimbusCompany</c>
    /// （游戏缓存被迁移/双写到该盘时仍能命中；不存在则忽略，已存在则不重复）。</summary>
    public static IReadOnlyList<string> WithMigratedCacheRoot(IEnumerable<string?> cacheRoots)
    {
        ArgumentNullException.ThrowIfNull(cacheRoots);
        var result = new List<string>();
        foreach (var root in cacheRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (result.Any(existing => string.Equals(existing, root, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(root);
        }
        if (OperatingSystem.IsWindows() && Directory.Exists(MigratedCacheRoot) &&
            !result.Any(root => string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(MigratedCacheRoot)),
                StringComparison.OrdinalIgnoreCase)))
            result.Add(MigratedCacheRoot);
        return result;
    }

    /// <summary>LCTA 事实中的备用缓存根（<c>staticmod.py:422</c> 同时读写该盘缓存）。</summary>
    public const string MigratedCacheRoot = @"D:\Unity\ProjectMoon_LimbusCompany";

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

    /// <summary>一条静态数据表（bundle 内 TextAsset）的完整定位信息。</summary>
    /// <param name="ContainerEntry">m_Container 里的游戏内资源路径（.staticmod 的 container 字段）。</param>
    /// <param name="DataClass">数据类（.staticmod 的 dataClass 字段）：
    /// 容器路径的倒数第二段（<c>…/static-data/&lt;类&gt;/&lt;文件&gt;.json</c>）；
    /// 无容器时退回 m_Name 里 '/' 之前的部分，都没有则为「未分组」。</param>
    /// <param name="FileName">表名（.staticmod 的 file 字段）：容器路径的文件名去扩展名；
    /// 无容器时退回 m_Name 里 '/' 之后的部分。</param>
    public sealed record StaticTextAsset(
        string ContainerEntry,
        string SerializedFile,
        long PathId,
        string DataClass,
        string FileName,
        string Name,
        byte[] Data)
    {
        /// <summary>UTF-8 正文（非 UTF-8 返回 null，不猜编码）。</summary>
        public string? TryDecodeUtf8()
            => new UnityTextAsset(SerializedFile, PathId, Name, Data).TryDecodeUtf8();

        /// <summary>表格大小（列表显示用）。</summary>
        public string SizeLabel => Data.Length >= 1024 * 1024
            ? $"{Data.Length / 1024.0 / 1024.0:0.0} MB"
            : Data.Length >= 1024 ? $"{Data.Length / 1024.0:0.0} KB" : $"{Data.Length} B";
    }

    /// <summary>由容器路径 + m_Name 推导 (dataClass, fileName)。
    ///
    /// <para>事实（2026-09 实测 1392 张表）：静态表 TextAsset 的 <c>m_Name</c> 不含 '/'
    /// 也不含扩展名（如 <c>stagenodereward91-12</c>），真正的分类只在容器路径里
    /// （<c>Assets/Resources_moved/StaticData/static-data/&lt;类&gt;/&lt;文件&gt;.json</c>）。
    /// 因此 dataClass 取容器倒数第二段、fileName 取容器文件名去扩展名；这样
    /// 加载器 <c>_read_textasset_json</c>（staticmod.py:270）的
    /// <c>dataClass/file</c> 名字兜底与工作台分组都能工作，而不再全部落到「未分组」。</para>
    /// </summary>
    public static (string DataClass, string FileName) SplitTableIdentity(string? containerEntry, string name)
    {
        var segments = (containerEntry ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length >= 2)
        {
            var fileName = Path.GetFileNameWithoutExtension(segments[^1]);
            if (!string.IsNullOrWhiteSpace(fileName)) return (segments[^2], fileName);
        }

        // 兜底：旧式 m_Name（"dataClass/file" 或裸文件名）。
        var separator = name.IndexOf('/');
        return separator > 0
            ? (name[..separator], name[(separator + 1)..])
            : ("未分组", name);
    }

    /// <summary>枚举静态 bundle 内的 TextAsset 及其容器路径/分组（静态数据工作台用）。
    /// 复用 ScanBundle（含 m_Container 解析）+ ReadBundleTextAsset（类型树读取）。</summary>
    public static IReadOnlyList<StaticTextAsset> ReadTextAssetEntries(StaticBundleLocation location,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!location.IsCached)
            throw new FileNotFoundException(
                $"静态数据 bundle 的缓存条目不存在（{location.BundleName}）：请启动一次游戏生成缓存后重试。",
                location.DataPath ?? string.Empty);
        var service = new UnityAssetService();
        var entries = new List<StaticTextAsset>();
        foreach (var descriptor in service.ScanBundle(location.DataPath!, cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (descriptor.Type != Domain.Assets.AssetType.Text || descriptor.UnityPathId is not { } pathId) continue;
            var asset = service.ReadBundleTextAsset(location.DataPath!, descriptor.ContainerPath!, pathId, cancellationToken);
            var name = asset.Name;
            var container = descriptor.Metadata.TryGetValue("containerEntry", out var entry) ? entry : string.Empty;
            var (dataClass, fileName) = SplitTableIdentity(container, name);
            entries.Add(new StaticTextAsset(container, descriptor.ContainerPath ?? string.Empty, pathId,
                dataClass, fileName, name, asset.Data));
        }
        return entries;
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string directory)
    {
        try { return Directory.EnumerateDirectories(directory); }
        catch (Exception) { return []; }
    }
}
