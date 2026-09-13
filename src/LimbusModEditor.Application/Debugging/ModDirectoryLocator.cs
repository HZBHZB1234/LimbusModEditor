using LimbusModEditor.Domain.Diagnostics;
using NLog;

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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static IReadOnlyList<string> SuggestCandidates()
    {
        using var scope = Log.Scope("推荐模组目录");
        var results = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            Log.Debug("非 Windows 平台，不推荐模组目录（当前平台 {0}）", Environment.OSVersion.Platform);
            return results;
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            Log.Warn("拿不到 %APPDATA%，跳过模组目录推荐（原本要探测 {0}）", "LimbusCompanyMods");
            return results;
        }
        var real = Path.Combine(appData, "LimbusCompanyMods");
        Log.Debug("模组目录探测：候选 {0}，存在 {1}", real, Directory.Exists(real));
        if (Directory.Exists(real)) results.Add(real);
        else Log.Debug("模组目录候选不存在，未推荐：{0}", real);
        Log.Info("模组目录推荐完成：{0} 个候选", results.Count);
        return results;
    }

    /// <summary>Counts mod-like entries (files/directories, including the
    /// loader's "_disable" suffixed ones) inside a candidate directory.</summary>
    public static int CountEntries(string directory)
    {
        try
        {
            var count = Directory.EnumerateFileSystemEntries(directory).Count();
            Log.Debug("模组条目计数：目录 {0}，{1} 条（含 _disable 后缀）", directory, count);
            return count;
        }
        catch (Exception ex)
        {
            // 原来静默返回 0（候选目录不可读时）——留痕以便区分「空目录」与「读不了」
            Log.Warn(ex, "模组条目计数失败，返回 0：目录 {0}", directory);
            return 0;
        }
    }
}
