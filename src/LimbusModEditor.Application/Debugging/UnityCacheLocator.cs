using LimbusModEditor.Domain.Diagnostics;
using NLog;

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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 本机规范的 Unity 缓存根 <c>&lt;用户目录&gt;/AppData/LocalLow/Unity/ProjectMoon_LimbusCompany</c>；
    /// 目录不存在返回 null（不推荐、不创建 —— 这条路径只在真实装机上存在）。
    ///
    /// <para>用途：项目字段与共享配置都没配缓存目录时的<b>最后一级回退</b>（静态表读取）。
    /// 不做候选枚举（<see cref="SuggestCandidates"/> 会遍历整个缓存，太贵）。</para>
    /// </summary>
    public static string? CanonicalCacheRoot()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) return null;
        var root = Path.Combine(profile, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany");
        return Directory.Exists(root) ? root : null;
    }

    public static IReadOnlyList<UnityCacheCandidate> SuggestCandidates(string? gameDirectory)
    {
        using var scope = Log.Scope("推荐 Unity 缓存目录");
        Log.Info("推荐 Unity 缓存目录开始：游戏目录 {0}", gameDirectory ?? "-");
        var results = new List<UnityCacheCandidate>();
        void Add(string path, Func<string, int> count)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                if (Log.IsTraceEnabled) Log.Trace("跳过缓存候选（路径为空或不存在）：{0}", path);
                return;
            }
            if (results.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                if (Log.IsTraceEnabled) Log.Trace("跳过重复的缓存候选：{0}", path);
                return;
            }
            try
            {
                var n = count(path);
                if (n > 0) results.Add(new UnityCacheCandidate(path, n));
                else Log.Debug("缓存候选不含任何条目，未推荐：{0}", path);
            }
            catch (Exception ex)
            {
                // 原来静默跳过不可读的候选 —— 留痕以便区分「目录不存在」与「读不了」
                Log.Warn(ex, "缓存候选计数失败，跳过该候选：{0}", path);
            }
        }

        // Game-local Addressables data (only exists in some installs).
        int CountBundles(string path)
            => Directory.EnumerateFiles(path, "*.bundle", SearchOption.TopDirectoryOnly).Count();
        if (!string.IsNullOrWhiteSpace(gameDirectory) && Directory.Exists(gameDirectory))
        {
            Add(Path.Combine(gameDirectory, "LimbusCompany_Data", "StreamingAssets", "aa"), CountBundles);
            Add(Path.Combine(gameDirectory, "LimbusCompany_Data"), CountBundles);
        }
        else
        {
            Log.Debug("游戏目录为空或不存在，跳过游戏内 Addressables 候选探测：{0}", gameDirectory ?? "-");
        }

        // The real bundle cache: <outer>/<inner>/__data pairs. Enumerate both
        // the (possibly junctioned) canonical root and skip non-cache dirs.
        int CountCacheV2(string path)
        {
            Log.Debug("统计缓存布局条目开始：{0}", path);
            var count = 0;
            foreach (var outer in Directory.EnumerateDirectories(path))
            {
                foreach (var inner in Directory.EnumerateDirectories(outer))
                {
                    if (File.Exists(Path.Combine(inner, "__data"))) count++;
                    if (Log.IsTraceEnabled) Log.Trace("缓存条目：{0}（已计 {1} 条）", inner, count);
                    if (count % 5000 == 0 && count > 0)
                        Log.Every(count, 5000, LogLevel.Info, () => $"缓存条目计数已到 {count}：{path}");
                    if (count >= 100_000)
                    {
                        Log.Warn("缓存条目计数达到上限 100000，停止枚举（沿用当前计数）：{0}", path);
                        return count; // hard bound for pathological caches
                    }
                }
            }
            Log.Debug("统计缓存布局条目完成：{0}，{1} 条 <outer>/<inner>/__data", path, count);
            return count;
        }
        if (OperatingSystem.IsWindows())
        {
            var localLow = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(localLow))
            {
                // Canonical Unity cache root (junctions traverse transparently).
                var cacheRoot = Path.Combine(localLow, "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany");
                Log.Debug("Unity 缓存根探测：{0}，存在 {1}", cacheRoot, Directory.Exists(cacheRoot));
                Add(cacheRoot, CountCacheV2);
                // Cache-migrated installs sometimes expose the cache directly
                // under <drive>\Unity\<name>; suggest the canonical path only —
                // the junction resolves to the same data.
            }
            else
            {
                Log.Warn("拿不到用户主目录，跳过 Unity 缓存根探测（原本要探测 {0}）", "LocalLow/Unity/ProjectMoon_LimbusCompany");
            }
        }
        Log.Info("推荐 Unity 缓存目录完成：{0} 个候选（{1}）", results.Count,
            string.Join("、", results.Select(x => $"{x.Path}={x.EntryCount} 条")));
        return results;
    }
}
