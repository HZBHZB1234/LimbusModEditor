using LimbusModEditor.Application.Debugging;

namespace LimbusModEditor.Domain.Tests;

/// <summary>Real-machine verification of the 自动获取资源地址 locators against
/// the actual Limbus Company install (game dir, Unity cache, mods root).
/// Every test skips silently when the corresponding real location is absent.
/// </summary>
public class RealLocatorTests
{
    private static readonly string? RealGameDir = FindGameDir();
    private static readonly string? RealModsDir = FindModsDir();

    private static string? FindGameDir()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company",
            @"E:\SteamLibrary\steamapps\common\Limbus Company",
        };
        var overrideDir = Environment.GetEnvironmentVariable("LME_GAME_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir)) candidates = [overrideDir, .. candidates];
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, GameDirectoryLocator.GameExecutable)));
    }

    private static string? FindModsDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var real = string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "LimbusCompanyMods");
        return real is not null && Directory.Exists(real) ? real : null;
    }

    [Fact]
    public void Locator_finds_the_real_game_install_on_this_machine()
    {
        if (RealGameDir is null) return;

        var lookup = GameDirectoryLocator.Scan(GameDirectoryLocator.DefaultCandidateRoots());
        Assert.True(lookup.Found, "自动定位应在真实安装上找到游戏目录");
        Assert.True(File.Exists(Path.Combine(lookup.GameDirectory!, GameDirectoryLocator.GameExecutable)));
        // the real install carries the documented data layout
        Assert.True(Directory.Exists(Path.Combine(lookup.GameDirectory!, "LimbusCompany_Data")),
            "真实安装应包含 LimbusCompany_Data");
    }

    [Fact]
    public void Locator_suggests_the_real_mods_root_on_this_machine()
    {
        if (RealModsDir is null) return;

        var candidates = ModDirectoryLocator.SuggestCandidates();
        Assert.Contains(RealModsDir, candidates);
        // the real loader's directory holds at least one entry (mod or disabled mod)
        Assert.True(ModDirectoryLocator.CountEntries(RealModsDir) > 0,
            "真实模组目录应包含至少一个条目");
    }

    [Fact]
    public void Locator_suggests_the_real_unity_cache_on_this_machine()
    {
        var localLow = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cache = Path.Combine(localLow, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany");
        if (!Directory.Exists(cache) || !Directory.EnumerateDirectories(cache).Any()) return;

        var candidates = UnityCacheLocator.SuggestCandidates(RealGameDir);
        Assert.Contains(candidates, c => string.Equals(c.Path, cache, StringComparison.OrdinalIgnoreCase));
    }
}
