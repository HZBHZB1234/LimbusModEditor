using System.IO;
using LimbusModEditor.Application.Debugging;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.AppConfig;

/// <summary>One directory discovered automatically (for the status hint).</summary>
public sealed record FmodDiscovery(string Directory, IReadOnlyList<string> FoundDlls, bool HasEncode)
{
    public bool HasDecode => FoundDlls.Count > 0;
}

/// <summary>
/// FMOD DLL 自动发现：按「随包目录（程序目录/fmod）→ 程序目录本身 → 游戏目录
/// （用户合法拥有的游戏自带 FMOD 运行库）」顺序找到第一个含 FMOD/FSBANK DLL
/// 的目录。手动指定的目录永远优先（在 SharedAppConfig.FmodLibraryDirectory）。
/// </summary>
public static class FmodLibraryLocator
{
    /// <summary>引擎（解码）与 FSBANK（编码）DLL 的候选文件名；游戏随附的
    /// fmodstudio.dll 静态链接引擎，同样导出 FMOD_System_* 供解码使用。</summary>
    public static readonly string[] EngineDllNames = ["fmod64.dll", "fmod.dll", "fmodstudio64.dll", "fmodstudio.dll"];
    public static readonly string[] FsbankDllNames = ["fsbank64.dll", "fsbank.dll"];

    public static FmodDiscovery? Discover(string baseDirectory, string? gameDirectory)
    {
        var candidates = new List<string?>
        {
            string.IsNullOrWhiteSpace(baseDirectory) ? null : Path.Combine(baseDirectory, "fmod"),
            string.IsNullOrWhiteSpace(baseDirectory) ? null : baseDirectory,
            string.IsNullOrWhiteSpace(gameDirectory) ? null : Path.Combine(gameDirectory, "LimbusCompany_Data", "Plugins", "x86_64"),
            string.IsNullOrWhiteSpace(gameDirectory) ? null : gameDirectory,
        };
        FmodDiscovery? engineOnly = null;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) continue;
            var found = EngineDllNames.Concat(FsbankDllNames)
                .Where(name => File.Exists(Path.Combine(candidate, name)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (found.Length == 0) continue;
            var hasEncode = found.Any(name => FsbankDllNames.Contains(name, StringComparer.OrdinalIgnoreCase));
            var discovery = new FmodDiscovery(candidate, found, hasEncode);
            if (hasEncode) return discovery;
            engineOnly ??= discovery;
        }
        return engineOnly;
    }
}

/// <summary>What a shared auto-configure pass changed (for the status line).</summary>
public sealed record SharedAutoConfigureReport(
    bool GameDirectoryFilled,
    bool UnityCacheDirectoryFilled,
    bool ModDirectoryFilled,
    bool FmodDirectoryDiscovered,
    int MigratedFromProject)
{
    public bool Any => GameDirectoryFilled || UnityCacheDirectoryFilled || ModDirectoryFilled
        || FmodDirectoryDiscovered || MigratedFromProject > 0;

    public string Describe()
    {
        var parts = new List<string>();
        if (GameDirectoryFilled) parts.Add("游戏目录");
        if (UnityCacheDirectoryFilled) parts.Add("Unity 缓存目录");
        if (ModDirectoryFilled) parts.Add("模组目录");
        if (FmodDirectoryDiscovered) parts.Add("FMOD DLL（自动发现）");
        if (MigratedFromProject > 0) parts.Add($"从项目迁移 {MigratedFromProject} 项");
        return parts.Count == 0 ? "无需自动配置" : string.Join("、", parts);
    }
}

/// <summary>
/// 编辑器全局环境（傻瓜化改造核心）：共享设置保存在程序目录，目录解析顺序为
/// 「共享配置（用户/迁移值）→ 旧项目字段（兼容）→ 自动发现」。自动配置只填充
/// 空位，手动值（含迁移来的手动值）永不被覆盖。
/// </summary>
public sealed class AppEnvironment
{
    private static AppEnvironment? _current;
    public static AppEnvironment Current => _current ??= new AppEnvironment();

    /// <summary>Program directory (the exe location). Shared settings and
    /// caches live here so nothing has to be reconfigured per project.</summary>
    public string BaseDirectory { get; }
    public string ConfigDirectory { get; }
    public string ConfigFile { get; }
    public string CacheDirectory { get; }
    public string ProjectsDirectory { get; }

    public SharedAppConfig Config { get; private set; }

    public AppEnvironment(string? baseDirectory = null)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        ConfigDirectory = Path.Combine(BaseDirectory, "config");
        ConfigFile = Path.Combine(ConfigDirectory, "shared-config.json");
        CacheDirectory = Path.Combine(BaseDirectory, "cache");
        ProjectsDirectory = Path.Combine(BaseDirectory, "projects");
        Config = SharedConfigService.Load(ConfigFile);
    }

    public void Reload() => Config = SharedConfigService.Load(ConfigFile);

    public void Save() => SharedConfigService.Save(Config, ConfigFile);

    // ── 有效目录解析（共享 → 项目 → 无）─────────────────────────────

    public string? EffectiveGameDirectory(ModProject? project)
        => FirstNonEmpty(Config.GameDirectory, project?.GameDirectory);

    public string? EffectiveUnityCacheDirectory(ModProject? project)
        => FirstNonEmpty(Config.UnityCacheDirectory, project?.UnityCacheDirectory);

    public string? EffectiveModDirectory(ModProject? project)
        => FirstNonEmpty(Config.ModDirectory, project?.ModDirectory);

    /// <summary>FMOD DLL 目录：手动指定 → 旧项目字段 → 自动发现（随包/程序
    /// 目录/游戏目录）。自动发现不落盘、不覆盖任何手动值。</summary>
    public string? EffectiveFmodLibraryDirectory(ModProject? project)
        => FirstNonEmpty(Config.FmodLibraryDirectory, project?.FmodLibraryDirectory)
           ?? FmodLibraryLocator.Discover(BaseDirectory, EffectiveGameDirectory(project))?.Directory;

    /// <summary>Same as the effective value, but also reports where it came
    /// from so the UI can mark 共享配置/本项目/自动发现.</summary>
    public (string? Path, string Source) ResolveWithSource(
        string? shared, string? fromProject, Func<string?> discover, string discoveredLabel)
    {
        if (!string.IsNullOrWhiteSpace(shared)) return (shared, "共享配置");
        if (!string.IsNullOrWhiteSpace(fromProject)) return (fromProject, "本项目（旧）");
        var found = discover();
        return string.IsNullOrWhiteSpace(found) ? (null, "未配置") : (found, discoveredLabel);
    }

    // ── 自动配置 / 迁移 ────────────────────────────────────────────

    /// <summary>打开/新建项目后的无感自动化（共享配置版）：先把旧项目里的
    /// 手动目录值迁移进共享空位，再对仍然缺失的项做自动发现。</summary>
    public SharedAutoConfigureReport ApplyAutoConfigure(ModProject? project)
    {
        ArgumentNullException.ThrowIfNull(Config);
        var migrated = 0;
        if (project is not null)
        {
            migrated = MigrateFromProject(project);
        }

        var report = new SharedAutoConfigureReport(false, false, false, false, migrated);

        var gameDirectory = FirstNonEmpty(Config.GameDirectory, project?.GameDirectory);
        if (string.IsNullOrWhiteSpace(Config.GameDirectory))
        {
            var lookup = GameDirectoryLocator.Scan(GameDirectoryLocator.DefaultCandidateRoots());
            if (lookup.Found)
            {
                Config.GameDirectory = lookup.GameDirectory;
                gameDirectory = lookup.GameDirectory;
                report = report with { GameDirectoryFilled = true };
            }
        }

        if (string.IsNullOrWhiteSpace(Config.UnityCacheDirectory))
        {
            var candidates = UnityCacheLocator.SuggestCandidates(gameDirectory);
            if (candidates.Count > 0)
            {
                Config.UnityCacheDirectory = candidates[0].Path;
                report = report with { UnityCacheDirectoryFilled = true };
            }
        }

        if (string.IsNullOrWhiteSpace(Config.ModDirectory))
        {
            var modCandidates = ModDirectoryLocator.SuggestCandidates();
            if (modCandidates.Count > 0)
            {
                Config.ModDirectory = modCandidates[0];
                report = report with { ModDirectoryFilled = true };
            }
        }

        // FMOD 目录只做动态发现，不落盘：随包目录/游戏目录变化后自动跟上；
        // 用户手动指定的值（Config.FmodLibraryDirectory）永远优先。
        var fmodDiscovered = FirstNonEmpty(Config.FmodLibraryDirectory, project?.FmodLibraryDirectory) is not null
            || FmodLibraryLocator.Discover(BaseDirectory, gameDirectory) is not null;

        if (report.Any) Save();
        return report with { FmodDirectoryDiscovered = fmodDiscovered };
    }

    /// <summary>旧项目兼容：把项目文件里已保存的目录值迁移进共享配置的空位。
    /// 只填充空位；共享配置里已有的值（后来的手动值）永远不覆盖。</summary>
    public int MigrateFromProject(ModProject project)
    {
        var migrated = 0;
        migrated += MigrateSlot(Config.GameDirectory, project.GameDirectory, value => Config.GameDirectory = value);
        migrated += MigrateSlot(Config.UnityCacheDirectory, project.UnityCacheDirectory, value => Config.UnityCacheDirectory = value);
        migrated += MigrateSlot(Config.ModDirectory, project.ModDirectory, value => Config.ModDirectory = value);
        migrated += MigrateSlot(Config.FmodLibraryDirectory, project.FmodLibraryDirectory, value => Config.FmodLibraryDirectory = value);
        return migrated;
    }

    private static int MigrateSlot(string? shared, string? fromProject, Action<string> assign)
    {
        if (!string.IsNullOrWhiteSpace(shared) || string.IsNullOrWhiteSpace(fromProject)) return 0;
        assign(fromProject);
        return 1;
    }

    // ── 最近项目 ───────────────────────────────────────────────────

    public void RegisterRecentProject(string projectFile, string projectName)
    {
        var full = Path.GetFullPath(projectFile);
        Config.LastProjectFile = full;
        var recents = Config.RecentProjects.Where(x => !string.Equals(x.Path, full, StringComparison.OrdinalIgnoreCase)).ToList();
        recents.Insert(0, new RecentProject(full, projectName, DateTimeOffset.UtcNow));
        Config.RecentProjects = recents
            .Where(x => File.Exists(x.Path))
            .Take(10)
            .ToList();
        Save();
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
