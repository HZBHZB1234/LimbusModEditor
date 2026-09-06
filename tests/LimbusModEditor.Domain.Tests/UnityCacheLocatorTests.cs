using LimbusModEditor.Application.Debugging;

namespace LimbusModEditor.Domain.Tests;

/// <summary>自动获取资源地址：Unity-cache suggestions must be verified to
/// actually contain cache data before being offered to the user. Layouts:
/// game-local .bundle files, or the real cache-v2 <c>outer/inner/__data</c>
/// tree under LocalLow/Unity/&lt;company&gt;_&lt;project&gt;.</summary>
public class UnityCacheLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-ucache-" + Guid.NewGuid().ToString("N"));

    public UnityCacheLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string MakeGameDirectory()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "LimbusCompany_Data", "StreamingAssets", "aa"));
        File.WriteAllText(Path.Combine(game, "LimbusCompany_Data", "bundle_a.bundle"), "x");
        File.WriteAllText(Path.Combine(game, "LimbusCompany_Data", "StreamingAssets", "bundle_b.bundle"), "x");
        // an empty candidate: must not be suggested
        Directory.CreateDirectory(Path.Combine(game, "empty"));
        return game;
    }

    [Fact]
    public void Suggests_only_directories_with_bundles()
    {
        var game = MakeGameDirectory();
        var candidates = UnityCacheLocator.SuggestCandidates(game);
        var paths = candidates.Select(x => x.Path).ToArray();
        Assert.Contains(Path.Combine(game, "LimbusCompany_Data"), paths);
        Assert.DoesNotContain(Path.Combine(game, "empty"), paths);
        Assert.Equal(1, candidates.Single(x => x.Path.EndsWith("LimbusCompany_Data", StringComparison.OrdinalIgnoreCase)).EntryCount);
    }

    [Fact]
    public void Missing_game_directory_contributes_no_candidates()
    {
        // With no game directory only machine-level candidates may appear;
        // nothing may be derived from the missing path itself.
        var missing = Path.Combine(_root, "no-such-game");
        var candidates = UnityCacheLocator.SuggestCandidates(missing);
        Assert.All(candidates, c => Assert.False(c.Path.StartsWith(missing, StringComparison.OrdinalIgnoreCase)));
        Assert.All(candidates, c => Assert.False(c.Path.StartsWith(Path.Combine(_root, "game"), StringComparison.OrdinalIgnoreCase)));

        // null behaves the same way (only machine-level candidates, possibly none)
        Assert.All(UnityCacheLocator.SuggestCandidates(null),
            c => Assert.False(c.Path.StartsWith(_root, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Duplicate_candidates_are_collapsed()
    {
        var game = MakeGameDirectory();
        var candidates = UnityCacheLocator.SuggestCandidates(game);
        Assert.Equal(candidates.Count, candidates.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Suggests_the_real_cache_root_when_present_on_this_machine()
    {
        // Real-install verification (LCTA path LocalLow/Unity/ProjectMoon_LimbusCompany).
        var localLow = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var canonical = Path.Combine(localLow, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany");
        if (!Directory.Exists(canonical) || !Directory.EnumerateDirectories(canonical).Any()) return;

        var candidates = UnityCacheLocator.SuggestCandidates(gameDirectory: null);
        var match = Assert.Single(candidates, c => string.Equals(c.Path, canonical, StringComparison.OrdinalIgnoreCase));
        // a real install carries well over a thousand cache entries
        Assert.True(match.EntryCount > 100, $"应发现大量缓存条目，实际 {match.EntryCount}");
    }
}
