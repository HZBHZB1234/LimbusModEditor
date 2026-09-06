using System.Security.Cryptography;

namespace LimbusModEditor.Format.Tests;

/// <summary>
/// Locates real Limbus Company samples on the current machine (Unity cache
/// roots and installed mods), following the paths verified against
/// LCTA's launcher code (resource_updater/core.py:78-94, launcher/modfolder.py).
/// Every test using these helpers returns early when a sample is absent, so
/// the suite stays green on machines without the game while still exercising
/// real data wherever it exists.
/// </summary>
internal static class RealSamples
{
    /// <summary>The Unity Addressables cache root: LocalLow/Unity/&lt;company&gt;_&lt;project&gt;,
    /// possibly a junction to another drive. Layout: &lt;outer 32-hex&gt;/&lt;inner 32-hex&gt;/__data.</summary>
    public static readonly string? UnityCacheRoot = FindUnityCacheRoot();

    /// <summary>First real .carra2 mod file found under %APPDATA%\LimbusCompanyMods.</summary>
    public static readonly string? RealCarra2 = FindCarra2();

    private static string? FindUnityCacheRoot()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var localLow = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(localLow))
                candidates.Add(Path.Combine(localLow, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany"));
        }
        var overrideDir = Environment.GetEnvironmentVariable("LME_UNITY_CACHE_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir)) candidates.Insert(0, overrideDir);

        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate)) continue;
            try
            {
                // cache-v2 layout proof: at least one <outer>/<inner>/__data pair
                foreach (var outer in Directory.EnumerateDirectories(candidate))
                {
                    foreach (var inner in Directory.EnumerateDirectories(outer))
                    {
                        if (File.Exists(Path.Combine(inner, "__data"))) return candidate;
                    }
                }
            }
            catch (Exception) { /* unreadable candidates are skipped */ }
        }
        return null;
    }

    private static string? FindCarra2()
    {
        var modsRoot = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LimbusCompanyMods")
            : null;
        if (modsRoot is null || !Directory.Exists(modsRoot)) return null;
        try
        {
            return Directory.EnumerateFiles(modsRoot, "*.carra2", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Enumerates up to <paramref name="limit"/> real bundle __data files.</summary>
    public static IReadOnlyList<string> BundleDataFiles(int limit, string? root = null)
    {
        var result = new List<string>();
        var cacheRoot = root ?? UnityCacheRoot;
        if (cacheRoot is null) return result;
        try
        {
            foreach (var outer in Directory.EnumerateDirectories(cacheRoot))
            {
                foreach (var inner in Directory.EnumerateDirectories(outer))
                {
                    var data = Path.Combine(inner, "__data");
                    if (!File.Exists(data)) continue;
                    result.Add(data);
                    if (result.Count >= limit) return result;
                }
            }
        }
        catch (Exception) { /* partial results are fine */ }
        return result;
    }

    public static string Sha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
