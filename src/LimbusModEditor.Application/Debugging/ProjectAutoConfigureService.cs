using LimbusModEditor.Domain.Projects;

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
    public static AutoConfigureReport Apply(ModProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
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
            }
        }

        if (string.IsNullOrWhiteSpace(project.UnityCacheDirectory))
        {
            var candidates = UnityCacheLocator.SuggestCandidates(project.GameDirectory);
            if (candidates.Count > 0)
            {
                project.UnityCacheDirectory = candidates[0].Path;
                report = report with { UnityCacheDirectoryFilled = true };
            }
        }

        if (string.IsNullOrWhiteSpace(project.ModDirectory))
        {
            var candidates = ModDirectoryLocator.SuggestCandidates();
            if (candidates.Count > 0)
            {
                project.ModDirectory = candidates[0];
                report = report with { ModDirectoryFilled = true };
            }
        }

        return report;
    }
}
