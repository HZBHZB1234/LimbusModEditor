using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Build;

/// <summary>
/// 一处音频修改：某个目标 bank 上一个或多个 FSB 样本被替换过。
/// </summary>
/// <param name="TargetBankFileName">游戏内目标 bank 文件名（如 <c>1D101A.assets.bank</c>）——
/// 加载器按文件名整包替换，所以它就是 <c>_fmod/bank/</c> 的产物名。</param>
/// <param name="OriginalBankPath">原版 bank 的本地副本（项目 <c>sources/banks</c> 下，bank 页实体化过）。</param>
/// <param name="Assets">该 bank 上被改的音频资源（<c>LogicalPath = fsb/&lt;序号&gt;</c>）。</param>
public sealed record BankEdit(string TargetBankFileName, string OriginalBankPath, IReadOnlyList<AssetRecord> Assets)
{
    /// <summary>被改的 FSB 序号（升序去重）。</summary>
    public IReadOnlyList<int> FsbIndexes => Assets
        .Select(x => ParseFsbIndex(x.LogicalPath))
        .Where(x => x >= 0)
        .Distinct()
        .OrderBy(x => x)
        .ToArray();

    /// <summary>产物基名（不含扩展名）：<c>1D101A.assets.bank</c> → <c>1D101A.assets</c>。</summary>
    public string BaseName => Path.GetFileNameWithoutExtension(TargetBankFileName);

    /// <summary><c>fsb/&lt;序号&gt;</c> → 序号；非该口径返回 -1。</summary>
    public static int ParseFsbIndex(string logicalPath)
    {
        var parts = logicalPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0].Equals("fsb", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(parts[1], out var index)
            ? index
            : -1;
    }
}

/// <summary>
/// 导出计划里的一个槽位（plan-16 §1）：要么「将写出 N 个产物」，要么「没有对应修改 / 缺条件，跳过」。
/// </summary>
/// <param name="Descriptor">槽位描述（目录名 / 扩展名 / 命名口径）。</param>
/// <param name="Planned">是否计划写出（false 时 <paramref name="SkipReason"/> 必须给出原因）。</param>
/// <param name="Directory">产物目录绝对路径（<c>&lt;目标目录&gt;/&lt;项目名&gt;_&lt;种类&gt;/&lt;格式&gt;</c>）。</param>
/// <param name="ArtifactCount">计划写出的产物个数。</param>
/// <param name="SkipReason">跳过原因（中文，给用户看）。</param>
/// <param name="Warnings">非致命提示（例如缺 FMOD DLL、缓存对齐风险）。</param>
public sealed record ModExportPlanItem(
    ExportSlotDescriptor Descriptor,
    bool Planned,
    string Directory,
    int ArtifactCount,
    string? SkipReason = null,
    IReadOnlyList<string>? Warnings = null)
{
    /// <summary>报告里的中文摘要。</summary>
    public string Describe() => Planned
        ? $"{Descriptor.DisplayName}：{ArtifactCount} 个产物 → {Directory}"
        : $"{Descriptor.DisplayName}：跳过（{SkipReason}）";
}

/// <summary>
/// 导出计划：<b>先分析、后写出</b>（plan-16 的核心设计）。分析阶段把「当前全部修改」映射到
/// 槽位，写出阶段只负责执行——这样报告与产物一定一致，也让用户能在写盘前看到会得到什么。
/// </summary>
public sealed record ModExportPlan(
    string RootDirectory,
    string ModName,
    IReadOnlyList<BankEdit> Banks,
    IReadOnlyList<AssetRecord> UnityObjects,
    IReadOnlyList<LangEditEntry> LangEntries,
    IReadOnlyList<StaticEditEntry> StaticEntries,
    IReadOnlyList<ModExportPlanItem> Items)
{
    /// <summary>计划写出的槽位数。</summary>
    public int PlannedSlotCount => Items.Count(x => x.Planned);

    /// <summary>计划写出的产物总数。</summary>
    public int PlannedArtifactCount => Items.Where(x => x.Planned).Sum(x => x.ArtifactCount);

    /// <summary>计划写出的槽位。</summary>
    public IReadOnlyList<ModExportPlanItem> PlannedItems => Items.Where(x => x.Planned).ToArray();

    /// <summary>计划写出的种类目录（去重，报告里列「已写入哪些文件夹」用）。</summary>
    public IReadOnlyList<string> PlannedGroupFolders => Items
        .Where(x => x.Planned)
        .Select(x => ExportLayout.GroupFolder(x.Descriptor.Group, ModName))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();
}

/// <summary>计划阶段需要的环境事实（由 UI 层传入，保持本层可测试）。</summary>
/// <param name="UnityCacheDirectory">Unity 缓存目录（Carra 外层键对齐核对用）。</param>
/// <param name="FmodDirectory">FMOD DLL 目录：给出后 rebank 槽位可解码逐样本 WAV
/// （加载器按「FSB 序号 + 样本名」匹配，必须有真实样本名），
/// 为空时 rebank 槽位会明确跳过而不是写出一个加载器匹配不上的包。</param>
public sealed record ModExportPlanContext(
    string? UnityCacheDirectory = null,
    string? FmodDirectory = null)
{
    /// <summary>FMOD 目录是否可用（存在且有 DLL）。</summary>
    public bool FmodCodecAvailable =>
        !string.IsNullOrWhiteSpace(FmodDirectory) && Directory.Exists(FmodDirectory);
}

/// <summary>
/// plan-16 S3：<b>把「当前项目的全部修改」分析成槽位计划</b>。
///
/// <para>数据来源（都不解析 bundle、不写盘）：</para>
/// <list type="bullet">
/// <item>音频：<see cref="AssetRecord"/> 里 <c>Type == Audio</c> 且有编辑的资源，
/// 按元数据 <c>bankSource</c>（bank 页写入的目标 bank 文件名）分组；</item>
/// <item>Unity 对象：与 <see cref="UnityCacheExportService.IsEditedCacheAsset"/> <b>同一口径</b>；</item>
/// <item>语言文本：<see cref="LangEditSession.Snapshot"/>（宿主持有，页面注入）；</item>
/// <item>静态数据：<see cref="StaticEditSession.Snapshot"/>。</item>
/// </list>
/// </summary>
public sealed class ModExportPlanService
{
    /// <summary>生成计划。<paramref name="rootDirectory"/> 是用户选定的目标目录。</summary>
    public ModExportPlan Plan(
        ModProject project,
        string rootDirectory,
        LangEditSession langEdits,
        StaticEditSession staticEdits,
        ModExportPlanContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(langEdits);
        ArgumentNullException.ThrowIfNull(staticEdits);
        context ??= new ModExportPlanContext();

        var root = Path.GetFullPath(rootDirectory);
        var modName = ExportLayout.Sanitize(project.Name);
        var banks = CollectBankEdits(project);
        var unityObjects = project.Assets.Where(UnityCacheExportService.IsEditedCacheAsset).ToArray();
        var langEntries = langEdits.Snapshot();
        var staticEntries = staticEdits.Snapshot();

        var items = new List<ModExportPlanItem>
        {
            PlanBank(banks, root, modName, context),
            PlanRebank(banks, root, modName, context),
            PlanCarra(unityObjects, root, modName, project, context),
            PlanLunartique(unityObjects, root, modName),
            PlanLang(ExportSlot.LangBus, langEntries, root, modName),
            PlanLang(ExportSlot.LangPatch, langEntries, root, modName),
            PlanLang(ExportSlot.LangPathset, langEntries, root, modName),
            PlanStaticMod(staticEntries, root, modName),
        };

        return new ModExportPlan(root, modName, banks, unityObjects, langEntries, staticEntries, items);
    }

    /// <summary>
    /// 收集音频修改：按 <c>bankSource</c> 分组。缺 <c>bankSource</c> 元数据（旧项目）或
    /// 原版 bank 副本不在磁盘上的条目会被<b>明确排除</b>——它们是「无法定位目标 bank」，
    /// 硬导出只会得到一个加载器不认的包。
    /// </summary>
    private static IReadOnlyList<BankEdit> CollectBankEdits(ModProject project)
    {
        var edits = new List<BankEdit>();
        var groups = project.Assets
            .Where(x => x.Type == AssetType.Audio && AssetEditService.HasEdits(x))
            .Where(x => x.Metadata.ContainsKey("bankSource"))
            .GroupBy(x => x.Metadata["bankSource"], StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var assets = group.Where(x => BankEdit.ParseFsbIndex(x.LogicalPath) >= 0).OrderBy(x => x.LogicalPath, StringComparer.Ordinal).ToArray();
            if (assets.Length == 0) continue;
            var original = assets
                .Select(x => x.SourcePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
            if (original is null) continue;
            edits.Add(new BankEdit(Path.GetFileName(group.Key), original, assets));
        }
        return edits.OrderBy(x => x.TargetBankFileName, StringComparer.Ordinal).ToArray();
    }

    private static ModExportPlanItem PlanBank(
        IReadOnlyList<BankEdit> banks, string root, string modName, ModExportPlanContext context)
    {
        var descriptor = ExportLayout.For(ExportSlot.Bank);
        var directory = Path.Combine(root, ExportLayout.GroupFolder(descriptor.Group, modName), descriptor.FolderName);
        if (banks.Count == 0)
            return new ModExportPlanItem(descriptor, false, directory, 0, "没有音频修改（bank 工作台里替换过样本才会有）");
        var warnings = new List<string>();
        if (!context.FmodCodecAvailable)
            warnings.Add("没找到 FMOD DLL：只能替换本来就是 FSB5 的样本；WAV 替换会明确报错，不会静默写出错包。");
        return new ModExportPlanItem(descriptor, true, directory, banks.Count, null, warnings);
    }

    private static ModExportPlanItem PlanRebank(
        IReadOnlyList<BankEdit> banks, string root, string modName, ModExportPlanContext context)
    {
        var descriptor = ExportLayout.For(ExportSlot.Rebank);
        var directory = Path.Combine(root, ExportLayout.GroupFolder(descriptor.Group, modName), descriptor.FolderName);
        if (banks.Count == 0)
            return new ModExportPlanItem(descriptor, false, directory, 0, "没有音频修改（差分包的输入就是被改的 bank）");
        if (!context.FmodCodecAvailable)
            return new ModExportPlanItem(descriptor, false, directory, 0,
                "缺少 FMOD DLL：差分包的条目名必须是**真实样本名**（加载器按 FSB 序号 + 样本名匹配），" +
                "拿不到样本名就写不出可用的 .rebank（宁可不写，也不产出加载器匹配不上的包）。");
        return new ModExportPlanItem(descriptor, true, directory, banks.Count);
    }

    private static ModExportPlanItem PlanCarra(
        IReadOnlyList<AssetRecord> unityObjects, string root, string modName,
        ModProject project, ModExportPlanContext context)
    {
        var descriptor = ExportLayout.For(ExportSlot.Carra);
        var directory = Path.Combine(root, ExportLayout.GroupFolder(descriptor.Group, modName), descriptor.FolderName);
        if (unityObjects.Count == 0)
            return new ModExportPlanItem(descriptor, false, directory, 0, "没有 Unity 资源修改（替换图片 / 编辑字段后才会有）");
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(context.UnityCacheDirectory) || !Directory.Exists(context.UnityCacheDirectory))
            warnings.Add("没配置 Unity 缓存目录，跳过「外层键是否仍在缓存中」的对齐核对（游戏更新后旧键的模组会被加载器静默跳过）。");
        return new ModExportPlanItem(descriptor, true, directory, 1, null, warnings);
    }

    private static ModExportPlanItem PlanLunartique(
        IReadOnlyList<AssetRecord> unityObjects, string root, string modName)
    {
        var descriptor = ExportLayout.For(ExportSlot.Lunartique);
        var directory = Path.Combine(root, ExportLayout.GroupFolder(descriptor.Group, modName), descriptor.FolderName);
        if (unityObjects.Count == 0)
            return new ModExportPlanItem(descriptor, false, directory, 0, "没有 Unity 资源修改（Lunartique 包由对象级改动构成）");
        return new ModExportPlanItem(descriptor, true, directory, 1);
    }

    private static ModExportPlanItem PlanLang(
        ExportSlot slot, IReadOnlyList<LangEditEntry> langEntries, string root, string modName)
    {
        var descriptor = ExportLayout.For(slot);
        var directory = Path.Combine(root, ExportLayout.GroupFolder(descriptor.Group, modName), descriptor.FolderName);
        if (langEntries.Count == 0)
            return new ModExportPlanItem(descriptor, false, directory, 0, "没有文本修改（文本工作台里改过并保存到编辑集才会有）");
        return new ModExportPlanItem(descriptor, true, directory, langEntries.Count);
    }

    private static ModExportPlanItem PlanStaticMod(
        IReadOnlyList<StaticEditEntry> staticEntries, string root, string modName)
    {
        var descriptor = ExportLayout.For(ExportSlot.StaticMod);
        var directory = Path.Combine(root, ExportLayout.GroupFolder(descriptor.Group, modName), descriptor.FolderName);
        if (staticEntries.Count == 0)
            return new ModExportPlanItem(descriptor, false, directory, 0, "没有静态数据表修改（静态工作台里改过并保存到编辑集才会有）");
        return new ModExportPlanItem(descriptor, true, directory, 1);
    }
}
