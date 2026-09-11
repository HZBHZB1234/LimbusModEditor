using System.Diagnostics;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Caching;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Scanning;

/// <summary>一个启动扫描步骤的结果状态。</summary>
public enum StartupScanStepStatus
{
    /// <summary>本次真的扫描/重建了（缓存有更新）。</summary>
    Scanned,
    /// <summary>已是最新（增量扫描的快速路径：签名全命中，没重解析任何文件）。</summary>
    AlreadyFresh,
    /// <summary>跳过（缺目录 / 缺 catalog / 缺项目，前提不成立）。</summary>
    Skipped,
    /// <summary>失败（原因进 <see cref="StartupScanStepResult.Detail"/>，不中断其它步骤）。</summary>
    Failed,
}

/// <summary>启动扫描的一个步骤结果。</summary>
/// <param name="Key">稳定标识（诊断/测试用）。</param>
/// <param name="Label">中文步骤名（状态栏显示）。</param>
/// <param name="Status">结果状态。</param>
/// <param name="Detail">中文细节（数量、耗时、跳过或失败原因）。</param>
/// <param name="Elapsed">该步骤耗时（诊断用：真实规模下「哪一步慢」是首要问题）。</param>
public sealed record StartupScanStepResult(
    string Key,
    string Label,
    StartupScanStepStatus Status,
    string Detail,
    TimeSpan Elapsed = default);

/// <summary>启动扫描的进度（在后台线程上报，UI 用 <c>IProgress&lt;T&gt;</c> 转回主线程）。</summary>
/// <param name="Label">当前步骤中文名。</param>
/// <param name="Detail">当前步骤细节（如「320/1531」）。</param>
public sealed record StartupScanProgress(string Label, string Detail);

/// <summary>一次启动扫描的完整报告。</summary>
/// <param name="Steps">按执行顺序的步骤结果。</param>
/// <param name="Elapsed">总耗时。</param>
public sealed record StartupScanReport(IReadOnlyList<StartupScanStepResult> Steps, TimeSpan Elapsed)
{
    /// <summary>真正扫描过的步骤数。</summary>
    public int ScannedCount => Steps.Count(x => x.Status == StartupScanStepStatus.Scanned);

    /// <summary>失败的步骤数。</summary>
    public int FailedCount => Steps.Count(x => x.Status == StartupScanStepStatus.Failed);

    /// <summary>跳过的步骤数。</summary>
    public int SkippedCount => Steps.Count(x => x.Status == StartupScanStepStatus.Skipped);

    /// <summary>状态栏一行摘要（傻瓜化：只说「扫了什么、有几个要处理」）。</summary>
    public string Describe()
    {
        var scanned = Steps.Where(x => x.Status == StartupScanStepStatus.Scanned).Select(x => x.Label).ToList();
        var head = scanned.Count == 0
            ? "启动扫描完成：全部资源已是最新"
            : $"启动扫描完成：{string.Join("、", scanned)} 已更新";
        var failed = Steps.Where(x => x.Status == StartupScanStepStatus.Failed).Select(x => x.Label).ToList();
        var tail = failed.Count == 0 ? string.Empty : $" · 失败：{string.Join("、", failed)}";
        return $"{head} · 用时 {Elapsed.TotalSeconds:0.0} 秒{tail}";
    }

    /// <summary>逐步骤的中文明细（提示条 / 诊断用，一行一步；含耗时）。</summary>
    public string DescribeSteps()
    {
        var symbol = new Dictionary<StartupScanStepStatus, string>
        {
            [StartupScanStepStatus.Scanned] = "✓",
            [StartupScanStepStatus.AlreadyFresh] = "＝",
            [StartupScanStepStatus.Skipped] = "－",
            [StartupScanStepStatus.Failed] = "✗",
        };
        return string.Join("\n", Steps.Select(x =>
            $"{symbol[x.Status]} {x.Label}：{x.Detail}（{x.Elapsed.TotalSeconds:0.0} 秒）"));
    }
}

/// <summary>
/// 启动扫描（本次新增）：<b>每次启动都把全部资源扫一遍，而不是等用户点按钮或等页面首次打开</b>。
///
/// <para><b>为什么</b>：此前的行为是「空项目才引导一次扫描，四个工作台的缓存库各自由页面首次
/// 打开时才建/才刷新」，于是出现两类问题——① 用户看不到全局进度，进某个工作台才发现要等索引；
/// ② 缓存库从来没被创建过时，页面首次读取会撞上「库文件存在但没表」（见
/// <c>SqliteTableCache</c> 的说明），表现为「载入 lang 文件失败：no such table: index_meta」。
/// 现在四个缓存库（<c>unity-cache-index.db</c> / <c>bank-index.db</c> / <c>static-tables.db</c> /
/// <c>text-index.db</c>）在启动时统一建库，并各自做一次增量扫描。</para>
///
/// <para><b>增量语义</b>：每一步都复用既有的「源签名 + 逐文件签名比对」实现——
/// 每次启动都<b>真实枚举磁盘</b>，但只重解析签名变过的文件，因此热启动是秒级；
/// <b>本类不新增任何解析逻辑</b>（bank 走 <see cref="BankIndexService"/>，静态表走
/// <see cref="StaticIndexService"/>，lang 走 <see cref="LangTextWorkbenchService.EnumerateFiles"/>，
/// 资源走 <see cref="UnityCacheScanService"/>），缓存只影响速度不影响正确性。</para>
///
/// <para><b>失败隔离</b>：任何一步失败都只记录原因并继续下一步（扫描是加速旁路，
/// 绝不能因为「没配游戏目录」这类前提缺失把启动流程打断）。</para>
/// </summary>
public sealed class StartupScanService
{
    /// <summary>步骤标识：四个缓存库建库/校表。</summary>
    public const string CacheDatabaseStep = "cache-databases";
    /// <summary>步骤标识：游戏资源（Unity 缓存全部 bundle）。</summary>
    public const string UnityAssetsStep = "unity-assets";
    /// <summary>步骤标识：音频索引（bank）。</summary>
    public const string BankIndexStep = "bank-index";
    /// <summary>步骤标识：静态数据表索引。</summary>
    public const string StaticTablesStep = "static-tables";
    /// <summary>步骤标识：lang 文本索引。</summary>
    public const string TextIndexStep = "text-index";

    private readonly AppEnvironment _env;
    private readonly UnityCacheScanService _cacheScan;

    /// <param name="env">程序环境（决定 <c>cache/</c> 目录与共享目录设置）。</param>
    /// <param name="cacheScan">游戏资源扫描服务（与侧边栏「自动加载游戏资源」<b>共用同一实例</b>，
    /// 避免两个实例同时写同一个索引库）。</param>
    public StartupScanService(AppEnvironment env, UnityCacheScanService cacheScan)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(cacheScan);
        _env = env;
        _cacheScan = cacheScan;
    }

    // ── 四个缓存库 ───────────────────────────────────────────────────

    /// <summary>四个缓存库的文件名（顺序固定：资源 / 音频 / 静态表 / 文本）。</summary>
    public static IReadOnlyList<string> CacheDatabaseFileNames =>
    [
        WorkbenchCachePaths.UnityCacheIndexFileName,
        WorkbenchCachePaths.BankIndexFileName,
        WorkbenchCachePaths.StaticTablesFileName,
        WorkbenchCachePaths.TextIndexFileName,
    ];

    /// <summary>四个缓存库的完整路径（启动建库 / 诊断 / 清理入口用）。</summary>
    public static IReadOnlyList<string> CacheDatabasePaths(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        return CacheDatabaseFileNames
            .Select(x => Path.Combine(Path.GetFullPath(cacheDirectory), x))
            .ToArray();
    }

    /// <summary>
    /// 建好 / 校好四个缓存库的表结构（幂等）。返回本次<b>新建或补建</b>的库路径。
    ///
    /// <para>「补建」包含「文件在但表不在」（0 字节库 / 上次建库被打断）的情况：
    /// 这正是「no such table」的直接来源，启动时补掉后页面就不会再撞。</para>
    /// </summary>
    public IReadOnlyList<string> EnsureCacheDatabases()
    {
        var repaired = new List<string>();
        foreach (var ensure in DatabaseEnsurers())
        {
            try
            {
                var path = ensure();
                if (!string.IsNullOrEmpty(path)) repaired.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                // 单个库建不起来不影响其它库，也不影响功能（缓存只影响速度）。
            }
        }
        return repaired;
    }

    /// <summary>四个库的建表动作；返回值 = 本次<b>需要补建</b>的库路径（本来就完好时返回空串）。</summary>
    private IEnumerable<Func<string>> DatabaseEnsurers()
    {
        var cacheDirectory = _env.CacheDirectory;

        yield return () =>
        {
            var path = Path.Combine(cacheDirectory, WorkbenchCachePaths.UnityCacheIndexFileName);
            var healthy = HasTable(path, "bundles");
            new UnityCacheSqliteIndexStore(path).EnsureSchema();
            return healthy ? string.Empty : path;
        };

        yield return () => EnsureTableCache(WorkbenchCacheKind.BankIndex, cacheDirectory);
        yield return () => EnsureTableCache(WorkbenchCacheKind.StaticTables, cacheDirectory);
        yield return () => EnsureTableCache(WorkbenchCacheKind.TextIndex, cacheDirectory);
    }

    private static string EnsureTableCache(WorkbenchCacheKind kind, string cacheDirectory)
    {
        var cache = SqliteTableCache.Create(kind, cacheDirectory);
        var path = cache.DbFile;
        var healthy = HasTable(path, WorkbenchCacheSchema.IndexMetaTable);
        cache.EnsureSchema();
        return healthy ? string.Empty : path;
    }

    /// <summary>库文件里到底有没有这张表（不存在 / 0 字节 / 半成品库一律 false）。</summary>
    private static bool HasTable(string dbFile, string table)
    {
        if (!File.Exists(dbFile) || new FileInfo(dbFile).Length == 0) return false;
        try
        {
            var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = dbFile,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            }.ToString();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            command.Parameters.AddWithValue("$name", table);
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
        }
        catch (Exception)
        {
            return false; // 打不开（损坏 / 被占用）：当作需要补建
        }
    }

    // ── 全量启动扫描 ─────────────────────────────────────────────────

    /// <summary>
    /// 依次扫描：四个缓存库 → 游戏资源（全部 bundle）→ 音频索引 → 静态表索引 → lang 文本索引。
    /// <b>不抛异常</b>：每步失败都记进报告并继续。
    /// </summary>
    /// <param name="project">当前项目（资源扫描要把对象索引登记进它）。</param>
    /// <param name="progress">进度回调（后台线程调用）。</param>
    /// <param name="cancellationToken">取消（关窗 / 切项目时取消）。</param>
    /// <param name="projectMatchesIndex">调用方保证「索引里每一行都能在项目里找到对应记录」
    /// （打开项目刚成功回灌过）。启动路径上恒为 true —— 正是它让「每次启动都扫一遍」
    /// 从几十秒降到几秒；不确定时传 false（慢但绝对安全）。</param>
    public async Task<StartupScanReport> ScanAllAsync(
        ModProject project,
        IProgress<StartupScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool projectMatchesIndex = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        var watch = Stopwatch.StartNew();
        var steps = new List<StartupScanStepResult>();

        steps.Add(Timed(() => ScanCacheDatabases(progress)));
        steps.Add(await TimedAsync(() => ScanUnityAssetsAsync(project, progress, projectMatchesIndex, cancellationToken)).ConfigureAwait(false));
        steps.Add(await TimedAsync(() => ScanBankIndexAsync(project, progress, cancellationToken)).ConfigureAwait(false));
        steps.Add(await TimedAsync(() => ScanStaticTablesAsync(project, progress, cancellationToken)).ConfigureAwait(false));
        steps.Add(await TimedAsync(() => ScanTextIndexAsync(project, progress, cancellationToken)).ConfigureAwait(false));

        watch.Stop();
        return new StartupScanReport(steps, watch.Elapsed);
    }

    /// <summary>给步骤包一层计时（耗时进结果，便于诊断「哪一步慢」）。</summary>
    private static StartupScanStepResult Timed(Func<StartupScanStepResult> step)
    {
        var watch = Stopwatch.StartNew();
        var result = step();
        watch.Stop();
        return result with { Elapsed = watch.Elapsed };
    }

    /// <summary>异步步骤的计时包装。</summary>
    private static async Task<StartupScanStepResult> TimedAsync(Func<Task<StartupScanStepResult>> step)
    {
        var watch = Stopwatch.StartNew();
        var result = await step().ConfigureAwait(false);
        watch.Stop();
        return result with { Elapsed = watch.Elapsed };
    }

    /// <summary>步骤 ①：四个缓存库建库/补表。</summary>
    private StartupScanStepResult ScanCacheDatabases(IProgress<StartupScanProgress>? progress)
    {
        progress?.Report(new StartupScanProgress("检查缓存库", $"{CacheDatabaseFileNames.Count} 个库"));
        try
        {
            var repaired = EnsureCacheDatabases();
            var names = CacheDatabaseFileNames.Count;
            var detail = repaired.Count == 0
                ? $"{names} 个缓存库均已就绪"
                : $"{names} 个缓存库已就绪（新建/补表 {repaired.Count} 个：{string.Join("、", repaired.Select(Path.GetFileName))}）";
            return new StartupScanStepResult(CacheDatabaseStep, "缓存库", 
                repaired.Count == 0 ? StartupScanStepStatus.AlreadyFresh : StartupScanStepStatus.Scanned, detail);
        }
        catch (Exception ex)
        {
            return new StartupScanStepResult(CacheDatabaseStep, "缓存库", StartupScanStepStatus.Failed, ex.Message);
        }
    }

    /// <summary>步骤 ②：游戏资源 = Unity 缓存里的全部 bundle（引用模式，只登记对象索引）。</summary>
    private async Task<StartupScanStepResult> ScanUnityAssetsAsync(
        ModProject project, IProgress<StartupScanProgress>? progress, bool projectMatchesIndex, CancellationToken cancellationToken)
    {
        var cacheDirectory = _env.EffectiveUnityCacheDirectory(project);
        if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory))
        {
            return new StartupScanStepResult(UnityAssetsStep, "游戏资源", StartupScanStepStatus.Skipped,
                "未找到 Unity 缓存目录（启动一次游戏生成缓存，或在「设置」页指定）");
        }

        progress?.Report(new StartupScanProgress("扫描游戏资源", "正在枚举缓存条目…"));
        try
        {
            var reporter = progress is null
                ? null
                : new Progress<UnityCacheScanProgress>(p => progress.Report(
                    new StartupScanProgress("扫描游戏资源", Describe(p))));
            var result = await _cacheScan.ScanIntoProjectAsync(
                project, cacheDirectory, _env.EffectiveGameDirectory(project), reporter, cancellationToken,
                projectMatchesIndex: projectMatchesIndex).ConfigureAwait(false);
            var detail = $"共 {result.TotalEntries} 个 bundle（解析 {result.ScannedBundles} · 索引命中 {result.IndexedBundles}）" +
                         $" · 新增资源 {result.AddedAssets} · 更新 {result.UpdatedAssets}" +
                         (result.SkippedMerge
                             ? "（项目与索引已一致、无变化，未对账也未重建资产表）"
                             : result.SkippedRowReconciliation ? "（项目与索引已一致，未逐条对账）" : string.Empty) +
                         (result.Diagnostics.Count > 0 ? $" · 诊断 {result.Diagnostics.Count} 条" : string.Empty);
            return new StartupScanStepResult(UnityAssetsStep, "游戏资源", StartupScanStepStatus.Scanned, detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StartupScanStepResult(UnityAssetsStep, "游戏资源", StartupScanStepStatus.Failed, ex.Message);
        }
    }

    private static string Describe(UnityCacheScanProgress progress)
        => progress.TotalEntries <= 0
            ? progress.Phase ?? "正在扫描…"
            : $"{progress.ProcessedEntries}/{progress.TotalEntries} 个缓存条目 · 解析 {progress.BundlesScanned} · 索引命中 {progress.BundlesFromIndex}";

    /// <summary>步骤 ③：音频索引（bank 目录 → 逐文件签名比对 → 只重解析变过的）。</summary>
    private async Task<StartupScanStepResult> ScanBankIndexAsync(
        ModProject project, IProgress<StartupScanProgress>? progress, CancellationToken cancellationToken)
    {
        var gameDirectory = _env.EffectiveGameDirectory(project);
        var bankDirectory = new BankDirectoryService().ResolveBankDirectory(gameDirectory);
        if (string.IsNullOrWhiteSpace(bankDirectory) || !Directory.Exists(bankDirectory))
        {
            return new StartupScanStepResult(BankIndexStep, "音频索引", StartupScanStepStatus.Skipped,
                "未找到 FMOD bank 目录（需要游戏目录）");
        }

        progress?.Report(new StartupScanProgress("扫描音频索引", "正在枚举 bank…"));
        try
        {
            var service = new BankIndexService(new BankIndexStore(_env.CacheDirectory));
            var source = BankIndexSource.Describe(bankDirectory);
            var reporter = progress is null
                ? null
                : new Progress<BankIndexProgress>(p => progress.Report(new StartupScanProgress("扫描音频索引", p.Describe())));
            var result = await service.RefreshAsync(source, reporter, cancellationToken).ConfigureAwait(false);
            var status = result.ParseCount == 0 ? StartupScanStepStatus.AlreadyFresh : StartupScanStepStatus.Scanned;
            return new StartupScanStepResult(BankIndexStep, "音频索引", status, result.Describe());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StartupScanStepResult(BankIndexStep, "音频索引", StartupScanStepStatus.Failed, ex.Message);
        }
    }

    /// <summary>步骤 ④：静态数据表索引（catalog 定位 → 源签名一致即跳过，否则重建元数据索引）。
    ///
    /// <para><b>为什么先走「不解析 catalog」的探针</b>：真实 <c>catalog.bin</c>（5 MB）解析一次
    /// 约 17 秒（本机实测），而它在「索引已是最新」时毫无用处。缓存布局固定为
    /// <c>&lt;缓存根&gt;/&lt;外层键&gt;/&lt;内层键&gt;/__data</c>，而索引里恰好记着上次建索引用的
    /// 内层内容哈希（<c>index_meta.source_key</c>）——按它在缓存根下找目录即可判定
    /// 「换源了吗 / 签名变了吗」，只花几百毫秒。探针不成立（没建过索引 / 缓存里找不到 /
    /// 签名变过 / 索引为空）时原样回落到 catalog 全路径，语义不变。</para></summary>
    private async Task<StartupScanStepResult> ScanStaticTablesAsync(
        ModProject project, IProgress<StartupScanProgress>? progress, CancellationToken cancellationToken)
    {
        var gameDirectory = _env.EffectiveGameDirectory(project);
        var cacheRoots = StaticIndexService.CacheRoots(_env.EffectiveUnityCacheDirectory(project));
        var store = new StaticTableIndexStore(_env.CacheDirectory);

        progress?.Report(new StartupScanProgress("扫描静态数据表", "正在核对缓存里的静态数据 bundle…"));
        try
        {
            if (ProbeFreshStaticBundle(store, cacheRoots) is { } probe)
            {
                return new StartupScanStepResult(StaticTablesStep, "静态数据表", StartupScanStepStatus.AlreadyFresh,
                    $"{probe.TableCount} 张表（缓存签名一致，未解析 catalog）");
            }

            progress?.Report(new StartupScanProgress("扫描静态数据表", "正在从 catalog 定位 bundle…"));
            var location = await Task.Run(() => StaticIndexService.Locate(gameDirectory, cacheRoots), cancellationToken)
                .ConfigureAwait(false);
            if (location is null)
            {
                return new StartupScanStepResult(StaticTablesStep, "静态数据表", StartupScanStepStatus.Skipped,
                    "没有定位到静态数据 bundle（确认游戏目录 / Unity 缓存目录，并启动一次游戏生成缓存）");
            }
            if (!location.IsCached)
            {
                return new StartupScanStepResult(StaticTablesStep, "静态数据表", StartupScanStepStatus.Skipped,
                    $"{location.BundleName} 已定位，但缓存条目还不存在");
            }

            var service = new StaticIndexService(store);
            var source = StaticIndexSource.From(location);
            var load = service.Load(source);
            if (load.IsUsable)
            {
                return new StartupScanStepResult(StaticTablesStep, "静态数据表", StartupScanStepStatus.AlreadyFresh,
                    $"{load.Entries.Count} 张表（源未变，索引直接复用）");
            }

            progress?.Report(new StartupScanProgress("扫描静态数据表", "正在枚举 bundle 内的全部 TextAsset…"));
            var reporter = progress is null
                ? null
                : new Progress<StaticIndexProgress>(p => progress.Report(new StartupScanProgress("扫描静态数据表", p.Describe())));
            var result = await service.RebuildAsync(location, source, reporter, cancellationToken).ConfigureAwait(false);
            return new StartupScanStepResult(StaticTablesStep, "静态数据表", StartupScanStepStatus.Scanned,
                $"{result.TableCount} 张表 · {result.TotalBytes / 1024.0 / 1024.0:0.0} MB · 用时 {result.Elapsed.TotalSeconds:0.0} 秒");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StartupScanStepResult(StaticTablesStep, "静态数据表", StartupScanStepStatus.Failed, ex.Message);
        }
    }

    /// <summary>探针结果（表数）。</summary>
    private sealed record StaticTableFreshProbe(int TableCount);

    /// <summary>
    /// 「不解析 catalog」的新鲜度探针：拿索引里记录的源键（= 静态数据 bundle 的内层内容哈希）
    /// 去缓存根下找 <c>&lt;外层键&gt;/&lt;内层哈希&gt;/__data</c>，找到且 <c>(size,mtime)</c> 签名
    /// 与索引一致、且索引非空 → 认为已是最新。返回 null 表示探针不成立，交回 catalog 全路径。
    /// <b>绝不缓存 hash 常量</b>：源键每次都从索引里现读。
    /// </summary>
    private static StaticTableFreshProbe? ProbeFreshStaticBundle(
        StaticTableIndexStore store, IReadOnlyList<string> cacheRoots)
    {
        string? sourceKey;
        try { sourceKey = store.ReadSourceKey(); }
        catch (Exception) { return null; } // 索引库不可读：走全路径（会重新建库/重建索引）
        if (string.IsNullOrWhiteSpace(sourceKey)) return null;

        foreach (var root in cacheRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            IEnumerable<string> outers;
            try { outers = Directory.EnumerateDirectories(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var outer in outers)
            {
                var dataPath = Path.Combine(outer, sourceKey, "__data");
                if (!File.Exists(dataPath)) continue;
                var location = new StaticBundleLocation(
                    $"{StaticBundleLocator.BundleNamePrefix}{sourceKey}.bundle",
                    sourceKey, Path.GetFileName(outer), Record: null, CacheRoot: root, DataPath: dataPath);
                if (!store.MatchesSource(StaticIndexSource.From(location))) return null; // 签名变过：走全路径
                var count = store.ReadTableCount();
                return count > 0 ? new StaticTableFreshProbe(count) : null; // 空索引必须重建
            }
        }
        return null;
    }

    /// <summary>步骤 ⑤：lang 文本索引（活动语言目录 + config.json 签名一致即跳过，否则重枚举）。</summary>
    private Task<StartupScanStepResult> ScanTextIndexAsync(
        ModProject project, IProgress<StartupScanProgress>? progress, CancellationToken cancellationToken)
    {
        var langRoot = new LangTextWorkbenchService().ResolveLangRoot(_env.EffectiveGameDirectory(project));
        if (langRoot is null)
        {
            return Task.FromResult(new StartupScanStepResult(TextIndexStep, "lang 文本索引", StartupScanStepStatus.Skipped,
                "未定位 lang 目录（需要游戏目录）"));
        }

        progress?.Report(new StartupScanProgress("扫描 lang 文本", "正在检查文本索引…"));
        try
        {
            var store = new TextIndexStore(_env.CacheDirectory);
            var source = TextIndexStore.DescribeSource(langRoot);
            if (store.IsFresh(source))
            {
                return Task.FromResult(new StartupScanStepResult(TextIndexStep, "lang 文本索引", StartupScanStepStatus.AlreadyFresh,
                    $"{store.ReadFileCount()} 个 JSON 文件（签名一致，索引直接复用）"));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var service = new LangTextWorkbenchService();
            var cached = store.ReadFileMap(langRoot);
            var files = service.EnumerateFiles(langRoot, cached);
            store.PersistFiles(source, files);
            return Task.FromResult(new StartupScanStepResult(TextIndexStep, "lang 文本索引", StartupScanStepStatus.Scanned,
                $"{files.Count} 个 JSON 文件已索引（活动语言：{service.ReadActiveLanguage(langRoot) ?? "未指定"}）"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new StartupScanStepResult(TextIndexStep, "lang 文本索引", StartupScanStepStatus.Failed, ex.Message));
        }
    }
}
