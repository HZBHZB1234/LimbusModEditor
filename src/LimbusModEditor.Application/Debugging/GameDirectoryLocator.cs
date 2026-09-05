namespace LimbusModEditor.Application.Debugging;

/// <summary>Result of an automatic game-directory probe.</summary>
public sealed record GameDirectoryLookup(string? GameDirectory, string Method, IReadOnlyList<string> ScannedRoots)
{
    public bool Found => GameDirectory is not null;
}

/// <summary>
/// 自动获取资源地址（P3.1 辅助）：scans known Steam library roots for a Limbus
/// Company installation (a folder that contains LimbusCompany.exe). Candidate
/// roots are injected so tests never touch the real registry or disk layout.
/// </summary>
public static class GameDirectoryLocator
{
    public const string GameExecutable = "LimbusCompany.exe";

    /// <summary>Scans the given candidate roots for
    /// steamapps/common/*/{LimbusCompany.exe}.</summary>
    public static GameDirectoryLookup Scan(IEnumerable<string> candidateRoots)
    {
        var scanned = new List<string>();
        foreach (var root in candidateRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var common = Path.Combine(root, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            try
            {
                foreach (var install in Directory.EnumerateDirectories(common))
                {
                    scanned.Add(install);
                    if (File.Exists(Path.Combine(install, GameExecutable)))
                        return new GameDirectoryLookup(install, $"在 {root} 的 steamapps/common 下发现 {GameExecutable}", scanned);
                }
            }
            catch (Exception) { /* unreadable library roots are skipped */ }
        }
        return new GameDirectoryLookup(null, "未在已知 Steam 库中找到 LimbusCompany.exe", scanned);
    }

    /// <summary>Builds the default candidate roots: registry Steam path,
    /// its libraryfolders.vdf entries and the standard install locations.</summary>
    public static IReadOnlyList<string> DefaultCandidateRoots()
    {
        var roots = new List<string>();
        if (!OperatingSystem.IsWindows()) return roots;
        try
        {
            var steamPath = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (!string.IsNullOrWhiteSpace(steamPath))
            {
                roots.Add(steamPath);
                var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (var line in File.ReadLines(vdf))
                    {
                        // VDF entries look like:  "path"  "D:\\SteamLibrary"
                        var match = System.Text.RegularExpressions.Regex.Match(
                            line, "\"path\"\\s+\"(.+?)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            var library = match.Groups[1].Value.Replace(@"\\", "\\");
                            if (Directory.Exists(library)) roots.Add(library);
                        }
                    }
                }
            }
        }
        catch (Exception) { /* registry-less environments fall back to defaults */ }
        foreach (var fallback in new[]
                 {
                     @"C:\Program Files (x86)\Steam",
                     @"C:\Program Files\Steam",
                     @"D:\Steam",
                     @"E:\Steam"
                 })
            if (Directory.Exists(fallback)) roots.Add(fallback);
        return roots;
    }
}
