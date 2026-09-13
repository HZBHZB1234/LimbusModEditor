using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Domain.Projects;
using NLog;

namespace LimbusModEditor.Application.Debugging;

/// <summary>What a silent auto-configure pass changed.</summary>
public sealed record AutoConfigureReport(
    bool GameDirectoryFilled,
    bool UnityCacheDirectoryFilled,
    bool ModDirectoryFilled)
{
    public bool Any => GameDirectoryFilled || UnityCacheDirectoryFilled || ModDirectoryFilled;
    public string Describe()
    {
        var parts = new List<string>();
        if (GameDirectoryFilled) parts.Add("游戏目录");
        if (UnityCacheDirectoryFilled) parts.Add("Unity 缓存目录");
        if (ModDirectoryFilled) parts.Add("模组目录");
        return parts.Count == 0 ? "无需自动配置" : string.Join("、", parts);
    }
}

/// <summary>
/// 无感自动化：fills each missing project directory from the verified
/// locators when a project is opened/created. Only empties are filled —
/// user-set values are never overwritten. FMOD DLL 目录从不自动填写
/// （必须由用户提供合法获得的 DLL）。
/// </summary>
public static class ProjectAutoConfigureService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static AutoConfigureReport Apply(ModProject project)
    {
        using var scope = Log.Scope("项目目录自动配置");
        ArgumentNullException.ThrowIfNull(project);
        Log.Info("项目目录自动配置开始：游戏目录 {0}、Unity 缓存 {1}、模组目录 {2}",
            project.GameDirectory ?? "-", project.UnityCacheDirectory ?? "-", project.ModDirectory ?? "-");
        var report = new AutoConfigureReport(false, false, false);

        // 只填充从未设置过的字段：用户设置过的值（即使目录暂时不存在，
        // 例如外置盘未挂载）永远不被自动覆盖。
        if (string.IsNullOrWhiteSpace(project.GameDirectory))
        {
            var lookup = GameDirectoryLocator.Scan(GameDirectoryLocator.DefaultCandidateRoots());
            if (lookup.Found)
            {
                project.GameDirectory = lookup.GameDirectory;
                report = report with { GameDirectoryFilled = true };
                Log.Debug("自动填入游戏目录：{0}", lookup.GameDirectory);
            }
            else
            {
                Log.Debug("游戏目录未自动填入（扫描未命中，保持为空）");
            }
        }
        else
        {
            Log.Debug("游戏目录已由用户设置，跳过自动配置：{0}", project.GameDirectory);
        }

        if (string.IsNullOrWhiteSpace(project.UnityCacheDirectory))
        {
            var candidates = UnityCacheLocator.SuggestCandidates(project.GameDirectory);
            if (candidates.Count > 0)
            {
                project.UnityCacheDirectory = candidates[0].Path;
                report = report with { UnityCacheDirectoryFilled = true };
                Log.Debug("自动填入 Unity 缓存目录：{0}（{1} 个缓存条目，共 {2} 个候选）",
                    candidates[0].Path, candidates[0].EntryCount, candidates.Count);
            }
            else
            {
                Log.Debug("Unity 缓存目录未自动填入（无候选，保持为空）");
            }
        }
        else
        {
            Log.Debug("Unity 缓存目录已由用户设置，跳过自动配置：{0}", project.UnityCacheDirectory);
        }

        if (string.IsNullOrWhiteSpace(project.ModDirectory))
        {
            var candidates = ModDirectoryLocator.SuggestCandidates();
            if (candidates.Count > 0)
            {
                project.ModDirectory = candidates[0];
                report = report with { ModDirectoryFilled = true };
                Log.Debug("自动填入模组目录：{0}（共 {1} 个候选，取首个）", candidates[0], candidates.Count);
            }
            else
            {
                Log.Debug("模组目录未自动填入（无候选，保持为空）");
            }
        }
        else
        {
            Log.Debug("模组目录已由用户设置，跳过自动配置：{0}", project.ModDirectory);
        }

        Log.Info("项目目录自动配置完成：{0}", report.Describe());
        return report;
    }
}
