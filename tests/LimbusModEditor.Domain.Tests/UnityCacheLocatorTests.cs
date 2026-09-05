using LimbusModEditor.Application.Debugging;

namespace LimbusModEditor.Domain.Tests;

/// <summary>自动获取资源地址：Unity-cache suggestions must be verified to
/// actually contain .bundle files before being offered to the user.</summary>
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
        Assert.Contains(Path.Combine(game, "LimbusCompany_Data", "StreamingAssets"), paths);
        Assert.DoesNotContain(Path.Combine(game, "empty"), paths);
        Assert.Equal(1, candidates.Single(x => x.Path.EndsWith("LimbusCompany_Data", StringComparison.OrdinalIgnoreCase)).BundleCount);
    }

    [Fact]
    public void Missing_game_directory_returns_empty()
    {
        Assert.Empty(UnityCacheLocator.SuggestCandidates(Path.Combine(_root, "no-such-game")));
        Assert.Empty(UnityCacheLocator.SuggestCandidates(null));
    }

    [Fact]
    public void Duplicate_candidates_are_collapsed()
    {
        var game = MakeGameDirectory();
        var candidates = UnityCacheLocator.SuggestCandidates(game);
        Assert.Equal(candidates.Count, candidates.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
