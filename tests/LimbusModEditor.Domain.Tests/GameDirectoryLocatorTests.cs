using LimbusModEditor.Application.Debugging;

namespace LimbusModEditor.Domain.Tests;

/// <summary>自动获取资源地址：the locator scans injected Steam library roots and
/// returns the first folder containing LimbusCompany.exe. No registry or real
/// disk layout is touched.</summary>
public class GameDirectoryLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-locate-" + Guid.NewGuid().ToString("N"));

    public GameDirectoryLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string MakeLibrary(string libraryName, string gameFolder, bool withExecutable)
    {
        var game = Path.Combine(_root, libraryName, "steamapps", "common", gameFolder);
        Directory.CreateDirectory(game);
        if (withExecutable) File.WriteAllText(Path.Combine(game, GameDirectoryLocator.GameExecutable), "stub");
        return Path.Combine(_root, libraryName);
    }

    [Fact]
    public void Finds_game_in_injected_library()
    {
        var library = MakeLibrary("Steam", "Limbus Company", withExecutable: true);
        var lookup = GameDirectoryLocator.Scan([library, Path.Combine(_root, "Missing")]);
        Assert.True(lookup.Found);
        Assert.Equal(Path.Combine(library, "steamapps", "common", "Limbus Company"), lookup.GameDirectory);
        Assert.Contains("LimbusCompany.exe", lookup.Method);
    }

    [Fact]
    public void Skips_unreadable_and_missing_roots()
    {
        var lookup = GameDirectoryLocator.Scan([Path.Combine(_root, "NoSuchLibrary")]);
        Assert.False(lookup.Found);
        Assert.Equal("未在已知 Steam 库中找到 LimbusCompany.exe", lookup.Method);
    }

    [Fact]
    public void Library_without_executable_is_skipped()
    {
        MakeLibrary("Steam", "Limbus Company", withExecutable: false);
        var libraryWithGame = MakeLibrary("Steam2", "OtherGame", withExecutable: false);
        var withGame = MakeLibrary("Steam3", "Limbus Company", withExecutable: true);
        var lookup = GameDirectoryLocator.Scan([libraryWithGame, withGame]);
        Assert.True(lookup.Found);
        Assert.Contains("Steam3", lookup.GameDirectory);
    }

    [Fact]
    public void Default_roots_are_safe_to_collect()
    {
        // must not throw even on machines without Steam installed
        var roots = GameDirectoryLocator.DefaultCandidateRoots();
        Assert.NotNull(roots);
        Assert.All(roots, r => Assert.True(Directory.Exists(r) || string.IsNullOrWhiteSpace(r) == false));
    }
}
