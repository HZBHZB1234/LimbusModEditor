using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Caching;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// 启动扫描（每次启动跑一遍全部资源 + 四个缓存库）：
/// ① 四个库都必须被建出来（这是「no such table」类故障的根治点）；
/// ② 0 字节 / 缺表的库必须被补建；
/// ③ 前提不成立（没配游戏目录）时只跳过、绝不抛异常、也绝不影响其它步骤；
/// ④ 报告文案与步骤顺序稳定（UI 状态栏与提示条直接用它）。
/// </summary>
public sealed class StartupScanServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-startupscan-" + Guid.NewGuid().ToString("N"));
    private readonly AppEnvironment _env;
    private readonly StartupScanService _service;

    public StartupScanServiceTests()
    {
        Directory.CreateDirectory(_root);
        _env = new AppEnvironment(_root);
        _service = new StartupScanService(_env,
            new UnityCacheScanService(Path.Combine(_env.CacheDirectory, "unity-cache-index.json")));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (Exception) { /* 临时目录清理失败不影响结论 */ }
    }

    private static ModProject Project() => new() { Name = "启动扫描测试" };

    private static bool HasTable(string databasePath, string table)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
        }.ToString();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    // ── 四个缓存库 ───────────────────────────────────────────────────

    [Fact]
    public void EnsureCacheDatabases_creates_all_four_databases_with_their_tables()
    {
        var repaired = _service.EnsureCacheDatabases();

        Assert.Equal(4, repaired.Count);
        var paths = StartupScanService.CacheDatabasePaths(_env.CacheDirectory);
        Assert.Equal(4, paths.Count);
        Assert.Equal(StartupScanService.CacheDatabaseFileNames.Count, paths.Distinct().Count());
        foreach (var path in paths) Assert.True(File.Exists(path), $"缺少缓存库：{path}");

        // 资源索引库：bundles / assets
        Assert.True(HasTable(paths[0], "bundles"));
        Assert.True(HasTable(paths[0], "assets"));
        // 音频库：banks / samples + index_meta
        Assert.True(HasTable(paths[1], "banks"));
        Assert.True(HasTable(paths[1], "samples"));
        Assert.True(HasTable(paths[1], WorkbenchCacheSchema.IndexMetaTable));
        // 静态表库：tables / documents + index_meta
        Assert.True(HasTable(paths[2], "tables"));
        Assert.True(HasTable(paths[2], "documents"));
        Assert.True(HasTable(paths[2], WorkbenchCacheSchema.IndexMetaTable));
        // 文本索引库：files / hits + index_meta
        Assert.True(HasTable(paths[3], "files"));
        Assert.True(HasTable(paths[3], "hits"));
        Assert.True(HasTable(paths[3], WorkbenchCacheSchema.IndexMetaTable));
    }

    [Fact]
    public void EnsureCacheDatabases_is_idempotent_and_reports_nothing_to_repair()
    {
        Assert.Equal(4, _service.EnsureCacheDatabases().Count);
        Assert.Empty(_service.EnsureCacheDatabases()); // 第二次：四个库都完好
        Assert.Empty(new StartupScanService(_env, new UnityCacheScanService(
            Path.Combine(_env.CacheDirectory, "unity-cache-index.json"))).EnsureCacheDatabases());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EnsureCacheDatabases_repairs_zero_byte_databases(int index)
    {
        // 真实现场：cache/text-index.db 是 0 字节（上次建库被打断），页面一读就报
        // 「no such table: index_meta」。启动扫描必须把它补建成有表的空库。
        var paths = StartupScanService.CacheDatabasePaths(_env.CacheDirectory);
        var target = paths[index];
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, []);
        Assert.Equal(0, new FileInfo(target).Length);

        var repaired = _service.EnsureCacheDatabases();

        Assert.Contains(target, repaired);
        Assert.True(new FileInfo(target).Length > 0);
        Assert.True(HasTable(target, index == 0 ? "bundles" : WorkbenchCacheSchema.IndexMetaTable));
    }

    // ── 全量扫描 ─────────────────────────────────────────────────────

    [Fact]
    public async Task ScanAllAsync_skips_missing_prerequisites_without_throwing()
    {
        var report = await _service.ScanAllAsync(Project());

        Assert.Equal(5, report.Steps.Count);
        Assert.Equal(
            [
                StartupScanService.CacheDatabaseStep,
                StartupScanService.UnityAssetsStep,
                StartupScanService.BankIndexStep,
                StartupScanService.StaticTablesStep,
                StartupScanService.TextIndexStep,
            ],
            report.Steps.Select(x => x.Key).ToArray());

        // 没配游戏目录 / 缓存目录：资源 + 音频 + 静态表 + lang 四个步骤全部跳过（不是失败），
        // 只有缓存库步骤是新建。
        Assert.Equal(0, report.FailedCount);
        Assert.Equal(4, report.SkippedCount);
        Assert.Equal(1, report.ScannedCount);
        Assert.Equal(StartupScanStepStatus.Scanned, report.Steps[0].Status);
        Assert.All(report.Steps.Skip(1), x => Assert.Equal(StartupScanStepStatus.Skipped, x.Status));
        Assert.Contains("启动扫描完成", report.Describe());
        Assert.Contains("缓存库", report.Describe());
        Assert.Contains("游戏资源", report.DescribeSteps());
    }

    [Fact]
    public async Task ScanAllAsync_indexes_a_synthetic_lang_directory_and_reuses_it_next_time()
    {
        // 合成一个最小 lang 根（config.json + 活动语言目录），验证「启动扫描真的建索引，
        // 第二次启动走 AlreadyFresh」——这正是「每次启动都扫描但热启动很快」的证据。
        var langRoot = Path.Combine(_root, "LimbusCompany_Data", "lang");
        Directory.CreateDirectory(Path.Combine(langRoot, "LLC_zh-CN"));
        File.WriteAllText(Path.Combine(langRoot, "config.json"), """{"lang":"LLC_zh-CN","titleFont":"","contextFont":""}""");
        File.WriteAllText(Path.Combine(langRoot, "LLC_zh-CN", "AbDlg.json"), """{"dataList":[{"id":1,"dialog":"浮士德"}]}""");
        _env.Config.GameDirectory = _root;

        var first = await _service.ScanAllAsync(Project());
        var textStep = first.Steps.Single(x => x.Key == StartupScanService.TextIndexStep);
        Assert.Equal(StartupScanStepStatus.Scanned, textStep.Status);
        // plan-14：只有活动语言目录下的 *.json 进索引（config.json 不再是工作台文件），
        // 所以这里合成的那 1 个文件就是全部。
        Assert.Contains("1 个 JSON 文件", textStep.Detail);

        var second = await _service.ScanAllAsync(Project());
        var cachedStep = second.Steps.Single(x => x.Key == StartupScanService.TextIndexStep);
        Assert.Equal(StartupScanStepStatus.AlreadyFresh, cachedStep.Status);
        Assert.Equal(0, second.ScannedCount); // 缓存库也已在第一次建好
    }

    [Fact]
    public async Task ScanAllAsync_scans_the_unity_cache_into_the_project()
    {
        // 合成一个最小 Unity 缓存布局：<缓存根>/<外层>/<内层>/__data。
        // 空 __data 会被记为「无法解析的 bundle」诊断，但扫描本身必须成功返回，
        // 也就是「每次启动都真的枚举全部资源」而不是直接跳过。
        var cacheRoot = Path.Combine(_root, "unity-cache");
        var dataPath = Path.Combine(cacheRoot, "0123456789abcdef0123456789abcdef",
            "fedcba9876543210fedcba9876543210", "__data");
        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
        File.WriteAllBytes(dataPath, [0x55, 0x6E, 0x69, 0x74, 0x79, 0x46, 0x53]);
        _env.Config.UnityCacheDirectory = cacheRoot;

        var project = Project();
        var report = await _service.ScanAllAsync(project);

        var assetsStep = report.Steps.Single(x => x.Key == StartupScanService.UnityAssetsStep);
        Assert.NotEqual(StartupScanStepStatus.Skipped, assetsStep.Status);
        Assert.Contains("共 1 个 bundle", assetsStep.Detail);
        Assert.Equal(0, report.FailedCount);
    }
}
