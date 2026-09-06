namespace LimbusModEditor.Application.Debugging;

/// <summary>One suggested Unity-cache candidate directory with the number of
/// cache entries found inside it.</summary>
public sealed record UnityCacheCandidate(string Path, int EntryCount);

/// <summary>
/// 自动获取资源地址（续）：derives plausible Unity data/cache directories from
/// the game directory and the verified Limbus cache layouts, verifying each
/// candidate actually contains data before suggesting it. Nothing is filled
/// in automatically — the user picks from verified candidates.
///
/// Verified against the real game (paths documented by LCTA's
/// resource_updater/core.py:78-94 and confirmed on a real install):
/// the Addressables cache root is <c>LocalLow/Unity/ProjectMoon_LimbusCompany</c>
/// (often a junction to another drive) holding cache-v2 entries
/// <c>&lt;outer 32-hex&gt;/&lt;inner 32-hex&gt;/__data</c>, while
/// <c>LocalLow/ProjectMoon/LimbusCompany</c> is the game's own runtime data
/// (saves, FMOD cache) and contains no bundles.
/// </summary>
public static class UnityCacheLocator
{
    public static IReadOnlyList<UnityCacheCandidate> SuggestCandidates(string? gameDirectory)
    {
        var results = new List<UnityCacheCandidate>();
        void Add(string path, Func<string, int> count)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            if (results.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            try
            {
                var n = count(path);
                if (n > 0) results.Add(new UnityCacheCandidate(path, n));
            }
            catch (Exception) { /* unreadable candidates are skipped */ }
        }

        // Game-local Addressables data (only exists in some installs).
        int CountBundles(string path)
            => Directory.EnumerateFiles(path, "*.bundle", SearchOption.TopDirectoryOnly).Count();
        if (!string.IsNullOrWhiteSpace(gameDirectory) && Directory.Exists(gameDirectory))
        {
            Add(Path.Combine(gameDirectory, "LimbusCompany_Data", "StreamingAssets", "aa"), CountBundles);
            Add(Path.Combine(gameDirectory, "LimbusCompany_Data"), CountBundles);
        }

        // The real bundle cache: <outer>/<inner>/__data pairs. Enumerate both
        // the (possibly junctioned) canonical root and skip non-cache dirs.
        int CountCacheV2(string path)
        {
            var count = 0;
            foreach (var outer in Directory.EnumerateDirectories(path))
            {
                foreach (var inner in Directory.EnumerateDirectories(outer))
                {
                    if (File.Exists(Path.Combine(inner, "__data"))) count++;
                    if (count >= 100_000) return count; // hard bound for pathological caches
                }
            }
            return count;
        }
        if (OperatingSystem.IsWindows())
        {
            var localLow = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(localLow))
            {
                // Canonical Unity cache root (junctions traverse transparently).
                Add(Path.Combine(localLow, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany"), CountCacheV2);
                // Cache-migrated installs sometimes expose the cache directly
                // under <drive>\Unity\<name>; suggest the canonical path only —
                // the junction resolves to the same data.
            }
        }
        return results;
    }
}
