namespace LimbusModEditor.Application.Debugging;

/// <summary>
/// 自动获取资源地址（续）：suggests the mods root directory used by the real
/// loader. LCTA's launcher (the loader actually in use) manages
/// %APPDATA%\LimbusCompanyMods — flat mod files or one directory per mod,
/// with a "_disable" suffix toggling entries. Verified candidates are listed
/// with their entry count; nothing is filled in automatically.
/// </summary>
public static class ModDirectoryLocator
{
    public static IReadOnlyList<string> SuggestCandidates()
    {
        var results = new List<string>();
        if (!OperatingSystem.IsWindows()) return results;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) return results;
        var real = Path.Combine(appData, "LimbusCompanyMods");
        if (Directory.Exists(real)) results.Add(real);
        return results;
    }

    /// <summary>Counts mod-like entries (files/directories, including the
    /// loader's "_disable" suffixed ones) inside a candidate directory.</summary>
    public static int CountEntries(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Count();
        }
        catch (Exception) { return 0; }
    }
}
