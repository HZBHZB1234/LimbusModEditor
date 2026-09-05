namespace LimbusModEditor.Application.Debugging;

/// <summary>One suggested Unity-cache candidate directory with the number of
/// Unity bundles directly inside it.</summary>
public sealed record UnityCacheCandidate(string Path, int BundleCount);

/// <summary>
/// 自动获取资源地址（续）：derives plausible Unity data/cache directories from
/// the game directory and the standard Unity LocalLow layout, verifying each
/// candidate actually contains .bundle files before suggesting it. Nothing is
/// filled in automatically — the user picks from verified candidates.
/// </summary>
public static class UnityCacheLocator
{
    public static IReadOnlyList<UnityCacheCandidate> SuggestCandidates(string? gameDirectory)
    {
        var results = new List<UnityCacheCandidate>();
        void Add(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            if (results.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            try
            {
                var count = Directory.EnumerateFiles(path, "*.bundle", SearchOption.TopDirectoryOnly).Count();
                if (count > 0) results.Add(new UnityCacheCandidate(path, count));
            }
            catch (Exception) { /* unreadable candidates are skipped */ }
        }

        if (!string.IsNullOrWhiteSpace(gameDirectory) && Directory.Exists(gameDirectory))
        {
            Add(Path.Combine(gameDirectory, "LimbusCompany_Data"));
            Add(Path.Combine(gameDirectory, "LimbusCompany_Data", "StreamingAssets"));
            Add(Path.Combine(gameDirectory, "LimbusCompany_Data", "StreamingAssets", "aa"));
        }
        if (OperatingSystem.IsWindows())
        {
            var localLow = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(localLow))
            {
                Add(Path.Combine(localLow, "AppData", "LocalLow", "ProjectMoon", "LimbusCompany"));
                Add(Path.Combine(localLow, "AppData", "LocalLow", "ProjectMoon", "LimbusCompany", "Steam"));
            }
        }
        return results;
    }
}
