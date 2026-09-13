using LimbusModEditor.Domain.Diagnostics;
using NLog;

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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public const string GameExecutable = "LimbusCompany.exe";

    /// <summary>Scans the given candidate roots for
    /// steamapps/common/*/{LimbusCompany.exe}.</summary>
    public static GameDirectoryLookup Scan(IEnumerable<string> candidateRoots)
    {
        using var scope = Log.Scope("扫描 Steam 库定位游戏目录");
        var scanned = new List<string>();
        var roots = candidateRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Log.Info("游戏目录定位开始：候选库根 {0} 个", roots.Count);
        var missingCommon = 0;
        var unreadableCommon = 0;
        foreach (var root in roots)
        {
            var common = Path.Combine(root, "steamapps", "common");
            if (!Directory.Exists(common))
            {
                missingCommon++;
                Log.Warn("游戏目录定位跳过候选库：{0} 下没有 steamapps/common（期望的安装位置）", root);
                continue;
            }
            try
            {
                foreach (var install in Directory.EnumerateDirectories(common))
                {
                    scanned.Add(install);
                    Log.Debug("检查候选安装目录：{0}（期望文件 {1}）", install, GameExecutable);
                    if (File.Exists(Path.Combine(install, GameExecutable)))
                    {
                        Log.Info("游戏目录定位成功：{0}（方法：在 {1} 的 steamapps/common 下发现 {2}）",
                            install, root, GameExecutable);
                        return new GameDirectoryLookup(install, $"在 {root} 的 steamapps/common 下发现 {GameExecutable}", scanned);
                    }
                }
            }
            catch (Exception ex)
            {
                /* unreadable library roots are skipped */
                unreadableCommon++;
                Log.Warn(ex, "游戏目录定位跳过候选库：{0} 的 steamapps/common 无法枚举（{1}）", root, ex.Message);
            }
        }
        Log.Warn("游戏目录定位失败：{0} 个候选库根都没找到 {1}（其中 {2} 个缺少 steamapps/common、{3} 个无法读取，已检查 {4} 个安装目录），返回 null",
            roots.Count, GameExecutable, missingCommon, unreadableCommon, scanned.Count);
        return new GameDirectoryLookup(null, "未在已知 Steam 库中找到 LimbusCompany.exe", scanned);
    }

    /// <summary>Builds the default candidate roots: registry Steam path,
    /// its libraryfolders.vdf entries and the standard install locations.</summary>
    public static IReadOnlyList<string> DefaultCandidateRoots()
    {
        using var scope = Log.Scope("收集默认游戏目录候选");
        var roots = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            Log.Warn("游戏目录候选回退：当前系统不是 Windows（{0}），原本要查注册表 Steam 路径，实际返回 0 个候选根",
                Environment.OSVersion.Platform);
            return roots;
        }
        try
        {
            var steamPath = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (string.IsNullOrWhiteSpace(steamPath))
            {
                Log.Warn("游戏目录候选回退：注册表 HKEY_CURRENT_USER\\Software\\Valve\\Steam 的 SteamPath 缺失或为空，改用约定安装路径");
            }
            else
            {
                Log.Debug("注册表 Steam 路径：{0}", steamPath);
                roots.Add(steamPath);
                var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    var vdfLibraries = 0;
                    foreach (var line in File.ReadLines(vdf))
                    {
                        // VDF entries look like:  "path"  "D:\\SteamLibrary"
                        var match = System.Text.RegularExpressions.Regex.Match(
                            line, "\"path\"\\s+\"(.+?)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            var library = match.Groups[1].Value.Replace(@"\\", "\\");
                            if (Directory.Exists(library))
                            {
                                roots.Add(library);
                                vdfLibraries++;
                            }
                            else
                            {
                                Log.Warn("游戏目录候选跳过 Steam 库：libraryfolders.vdf 里的 {0} 不存在", library);
                            }
                        }
                    }
                    Log.Debug("Steam 库扫描完成：{0} 记录 {1} 个可用库目录", vdf, vdfLibraries);
                }
                else
                {
                    Log.Warn("游戏目录候选回退：没有 libraryfolders.vdf（{0}），只用注册表里的 Steam 主目录", vdf);
                }
            }
        }
        catch (Exception ex)
        {
            /* registry-less environments fall back to defaults */
            Log.Warn(ex, "游戏目录候选回退：读取注册表 Steam 路径失败（{0}），改用约定安装路径", ex.Message);
        }
        foreach (var fallback in new[]
                 {
                     @"C:\Program Files (x86)\Steam",
                     @"C:\Program Files\Steam",
                     @"D:\Steam",
                     @"E:\Steam"
                 })
            if (Directory.Exists(fallback)) roots.Add(fallback);
        Log.Info("游戏目录候选收集完成：{0} 个库根（{1}）", roots.Count, string.Join("; ", roots));
        return roots;
    }
}
