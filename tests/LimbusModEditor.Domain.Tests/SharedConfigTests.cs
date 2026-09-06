using System.Text.Json;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

/// <summary>傻瓜化改造：共享设置（程序目录 config/shared-config.json）的读写、
/// 旧项目迁移、无感自动配置与 FMOD DLL 自动发现。</summary>
public class SharedConfigTests : IDisposable
{
    private readonly string _root;

    public SharedConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lme-sharedconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* temp cleanup */ }
    }

    private AppEnvironment NewEnv() => new(_root);

    [Fact]
    public void Shared_config_round_trips()
    {
        var env = NewEnv();
        env.Config.GameDirectory = @"C:\games\Limbus";
        env.Config.ModDirectory = @"C:\mods";
        env.Config.RecentProjects.Add(new RecentProject(@"C:\p\A.lmeproj", "A", DateTimeOffset.UtcNow));
        env.Save();

        var reloaded = NewEnv();
        Assert.Equal(@"C:\games\Limbus", reloaded.Config.GameDirectory);
        Assert.Equal(@"C:\mods", reloaded.Config.ModDirectory);
        Assert.Single(reloaded.Config.RecentProjects);
        Assert.True(File.Exists(reloaded.ConfigFile));
        Assert.Contains("config", reloaded.ConfigFile);
    }

    [Fact]
    public void Corrupt_config_file_falls_back_to_defaults_and_is_backed_up()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "shared-config.json"), "{ not json");
        var env = NewEnv();
        Assert.NotNull(env.Config);
        Assert.Null(env.Config.GameDirectory);
        Assert.True(File.Exists(env.ConfigFile + ".bad"));
    }

    [Fact]
    public void Legacy_project_directories_migrate_into_empty_shared_slots_only()
    {
        var env = NewEnv();
        env.Config.GameDirectory = @"C:\manual\game"; // 手动值先存在
        var project = new ModProject
        {
            GameDirectory = @"C:\legacy\game",
            UnityCacheDirectory = @"C:\legacy\cache",
            FmodLibraryDirectory = @"C:\legacy\fmod"
        };
        var migrated = env.MigrateFromProject(project);
        Assert.Equal(2, migrated); // 只有空位迁移；GameDirectory 已有手动值不覆盖
        Assert.Equal(@"C:\manual\game", env.Config.GameDirectory);
        Assert.Equal(@"C:\legacy\cache", env.Config.UnityCacheDirectory);
        Assert.Equal(@"C:\legacy\fmod", env.Config.FmodLibraryDirectory);
    }

    [Fact]
    public void Auto_configure_never_overwrites_existing_shared_values()
    {
        var env = NewEnv();
        env.Config.GameDirectory = @"C:\custom\game";
        env.Config.ModDirectory = @"C:\custom\mods";
        var report = env.ApplyAutoConfigure(new ModProject());
        Assert.Equal(@"C:\custom\game", env.Config.GameDirectory);
        Assert.Equal(@"C:\custom\mods", env.Config.ModDirectory);
        Assert.False(report.GameDirectoryFilled);
        Assert.False(report.ModDirectoryFilled);
    }

    [Fact]
    public void Fmod_discovery_is_not_persisted_into_shared_config()
    {
        var env = NewEnv();
        // 程序目录不含 fmod/，真实机器上也许能通过游戏目录发现 —— 无论哪种，
        // 共享配置里都不应留下自动发现值（动态发现随环境自动更新）。
        env.ApplyAutoConfigure(new ModProject());
        Assert.Null(env.Config.FmodLibraryDirectory);
    }

    [Fact]
    public void Fmod_locator_prefers_bundled_directory_with_encode_support()
    {
        var baseDir = Path.Combine(_root, "app1");
        Directory.CreateDirectory(Path.Combine(baseDir, "fmod"));
        File.WriteAllBytes(Path.Combine(baseDir, "fmod", "fmod64.dll"), [1]);
        File.WriteAllBytes(Path.Combine(baseDir, "fmod", "fsbank64.dll"), [1]);
        var discovery = FmodLibraryLocator.Discover(baseDir, null);
        Assert.NotNull(discovery);
        Assert.True(discovery.HasEncode);
        Assert.True(discovery.HasDecode);
        Assert.EndsWith("fmod", discovery.Directory);
    }

    [Fact]
    public void Fmod_locator_falls_back_to_game_runtime()
    {
        var baseDir = Path.Combine(_root, "app2");
        Directory.CreateDirectory(baseDir);
        var gameDir = Path.Combine(_root, "game2");
        var plugins = Path.Combine(gameDir, "LimbusCompany_Data", "Plugins", "x86_64");
        Directory.CreateDirectory(plugins);
        File.WriteAllBytes(Path.Combine(plugins, "fmodstudio.dll"), [1]);
        var discovery = FmodLibraryLocator.Discover(baseDir, gameDir);
        Assert.NotNull(discovery);
        Assert.False(discovery.HasEncode); // 游戏自带运行库只够解码
        Assert.True(discovery.HasDecode);
        Assert.Equal(plugins, discovery.Directory);
    }

    [Fact]
    public void Recent_projects_are_deduplicated_pruned_and_tracked_as_last()
    {
        var env = NewEnv();
        var a = Path.Combine(_root, "A.lmeproj");
        var b = Path.Combine(_root, "B.lmeproj");
        File.WriteAllText(a, "{}");
        File.WriteAllText(b, "{}");
        env.RegisterRecentProject(a, "A");
        env.RegisterRecentProject(b, "B");
        env.RegisterRecentProject(a, "A");
        Assert.Equal(2, env.Config.RecentProjects.Count);
        Assert.Equal(Path.GetFullPath(a), env.Config.RecentProjects[0].Path);
        Assert.Equal(Path.GetFullPath(a), env.Config.LastProjectFile);

        File.Delete(b);
        env.RegisterRecentProject(a, "A");
        Assert.Single(env.Config.RecentProjects);
    }
}
