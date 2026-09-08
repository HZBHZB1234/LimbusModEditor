using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Build;

/// <summary>导出思路的类别（UI 按它路由到具体出口）。</summary>
public enum ExportIdeaKind
{
    /// <summary>项目里还没有资源：先扫描 / 导入。</summary>
    ScanFirst,
    /// <summary>有资源但还没有修改：先做修改。</summary>
    EditFirst,
    /// <summary>Unity 资源修改 → 一键 Carra2（主推荐通道）。</summary>
    OneClickCarra2,
    /// <summary>已登记源模组 → 导出向导（选目标格式 / 输出位置）。</summary>
    ExportWizard,
    /// <summary>多个源或混合修改 → 导出全部（多格式）。</summary>
    MultiFormat,
    /// <summary>文本修改 → lang 补丁。</summary>
    LangText,
    /// <summary>音频修改 → Bank / Rebank。</summary>
    AudioBank,
    /// <summary>本地立刻验证：构建调试覆盖层并启动游戏。</summary>
    DebugOverlay,
}

/// <summary>一条「导出思路」：给用户看的标题 / 一句话结论 / 展开说明，
/// 以及是否推荐、是否可用（不可用时给出中文原因）。</summary>
public sealed record ExportIdea(
    ExportIdeaKind Kind,
    string Title,
    string Summary,
    string Detail,
    int AssetCount,
    bool Recommended,
    bool Enabled,
    string? BlockedReason = null);

/// <summary>分析导出思路需要的环境事实（由 UI 层传入，保持本层可测试）。</summary>
public sealed record ExportAdvisorContext(
    string? GameDirectory = null,
    string? ModDirectory = null,
    bool FmodAvailable = false);

/// <summary>导出思路分析器（P3.10）：用户点「导出」时不要让他自己判断
/// Carra2 / Rebank / lang 补丁 / 多格式导出的区别——先看项目里到底改了什么，
/// 再给出「这次该用哪个出口」的清单，推荐项排最前。</summary>
public sealed class ExportAdvisor
{
    public IReadOnlyList<ExportIdea> Analyze(ModProject project, ExportAdvisorContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        context ??= new ExportAdvisorContext();

        var edited = project.Assets.Where(AssetEditService.HasEdits).ToList();
        var unityEdited = edited.Where(IsUnityAsset).ToList();
        var audioEdited = edited.Where(x => x.Type == AssetType.Audio).ToList();
        var sourceCount = project.Sources.Count;
        var gameDirectoryReady = !string.IsNullOrWhiteSpace(context.GameDirectory) && Directory.Exists(context.GameDirectory);

        var ideas = new List<ExportIdea>();

        if (project.Assets.Count == 0)
        {
            ideas.Add(new ExportIdea(
                ExportIdeaKind.ScanFirst,
                "先获取资源",
                "项目里还没有任何资源，导出只会得到空模组",
                "在左栏「① 获取资源」点「扫描游戏资源（推荐）」索引游戏素材（引用模式，不复制文件），"
                + "或用「导入资源包… / 导入文件夹…」把已有模组与素材登记进来。",
                0, Recommended: true, Enabled: true));
        }

        if (project.Assets.Count > 0 && edited.Count == 0)
        {
            ideas.Add(new ExportIdea(
                ExportIdeaKind.EditFirst,
                "先做点修改",
                $"已索引 {project.Assets.Count} 个资源，但还没有任何修改",
                "在资源视图里选中图片 / 音频 / 文本，用右侧「替换选中资源…」或双击完成修改；"
                + "所有修改都暂存在这个 .lmeproj 里，随时可以继续。",
                0, Recommended: true, Enabled: true));
        }

        if (unityEdited.Count > 0)
        {
            ideas.Add(new ExportIdea(
                ExportIdeaKind.OneClickCarra2,
                "一键导出模组（推荐）",
                $"{unityEdited.Count} 个 Unity 资源修改 → Carra2",
                "按真实加载器布局（<缓存外层键>/<内层键>/<pathId>.<类型表索引>，逐条目 XZ）打包，"
                + "默认输出到模组目录，游戏加载器下次启动即读取。",
                unityEdited.Count, Recommended: true, Enabled: true));
        }

        if (audioEdited.Count > 0)
        {
            ideas.Add(new ExportIdea(
                ExportIdeaKind.AudioBank,
                "导出音频模组（Bank / Rebank）",
                $"{audioEdited.Count} 个音频修改 → .bank / .rebank",
                context.FmodAvailable
                    ? "音频走 FMOD bank 通道：WAV 会用随包 DLL 重编码为 FSB 再回填，需要对应的源 bank 作为骨架。"
                    : "音频走 FMOD bank 通道：当前没找到 FMOD DLL，只能保留原 FSB 结构（无法 WAV→FSB 重编码）；"
                      + "把 fmod64.dll / fsbank64.dll 放进程序目录的 fmod\\ 后重试即可。",
                audioEdited.Count, Recommended: false, Enabled: true));
        }

        if (sourceCount > 0)
        {
            ideas.Add(new ExportIdea(
                ExportIdeaKind.ExportWizard,
                "导出模组（向导）…",
                $"从 {sourceCount} 个已登记的源模组里挑目标格式",
                "支持 Carra / Carra2 / Rebank / Bank / Lunartique，并在选择前给出兼容性矩阵；"
                + "需要精细控制输出格式与位置时用这个。",
                edited.Count, Recommended: false, Enabled: true));

            ideas.Add(new ExportIdea(
                ExportIdeaKind.MultiFormat,
                "导出全部（多格式）",
                $"{sourceCount} 个源各自导出到自己的格式",
                "项目里既有 Unity 资源修改又有音频 / 包体修改时，一次把每个源都导出到对应格式，"
                + "输出目录里会得到完整的模组集合。",
                edited.Count, Recommended: false, Enabled: true));
        }

        ideas.Add(new ExportIdea(
            ExportIdeaKind.LangText,
            "文本模组（lang 补丁）",
            "改游戏文本 → 生成 lang 补丁",
            "打开文本模组工作台：改文本、与官方 JSON 差分、把补丁写进模组目录；"
            + "真实加载器启动时应用（目标文件自动 .bak，退出还原）。",
            0, Recommended: false, Enabled: gameDirectoryReady,
            gameDirectoryReady ? null : "没找到游戏目录，无法定位 lang 目录。"));

        ideas.Add(new ExportIdea(
            ExportIdeaKind.DebugOverlay,
            "本地立刻验证（调试覆盖层）",
            "把修改直接铺到游戏目录并启动游戏",
            "适合自己验收：先自动备份原文件，编辑器关闭时可恢复；"
            + "分发仍然要用上面的导出通道产出模组包。",
            edited.Count, Recommended: false, Enabled: gameDirectoryReady,
            gameDirectoryReady ? null : "没找到游戏目录，无法定位覆盖目标。"));

        // 推荐项排最前（OrderByDescending 是稳定排序，同类保持添加顺序）。
        return ideas.OrderByDescending(x => x.Recommended).ToArray();
    }

    /// <summary>Unity bundle / SerializedFile 来源的修改（走 Carra2 写回）。
    /// 导入的旧式包体（Carra/Rebank/Lunartique）不算，它们走多格式导出。</summary>
    private static bool IsUnityAsset(AssetRecord asset)
        => asset.Metadata.ContainsKey("unityBundle")
           || asset.Metadata.ContainsKey("unitySerializedFile")
           || asset.Metadata.TryGetValue("reference", out var reference) && reference == "true";
}
