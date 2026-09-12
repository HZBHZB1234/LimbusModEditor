namespace LimbusModEditor.Domain.Formats;

/// <summary>导出产物的一级分类（plan-16 §1）：一个种类 = 目标目录下的一个文件夹。</summary>
public enum ExportGroup
{
    /// <summary>音频（FMOD）：<c>&lt;项目名&gt;_fmod/</c>。</summary>
    Fmod,
    /// <summary>资源 / 数据：<c>&lt;项目名&gt;_data/</c>。</summary>
    Data,
    /// <summary>语言文本：<c>&lt;项目名&gt;_text/</c>。</summary>
    Text,
    /// <summary>静态数据表：<c>&lt;项目名&gt;_static/</c>。</summary>
    Static,
}

/// <summary>种类下的具体格式槽位（一个槽位 = 二级文件夹 + 该格式的全部产物）。</summary>
public enum ExportSlot
{
    /// <summary>完整音频包 <c>.bank</c>（加载器按文件名整包替换游戏 bank）。</summary>
    Bank,
    /// <summary>音频差分 <c>.rebank</c>（加载器按 <c>base_bank</c> 就地重打包）。</summary>
    Rebank,
    /// <summary>Unity 对象级资源包 <c>.carra</c>。</summary>
    Carra,
    /// <summary>Lunartique 安装/卸载配对包 <c>.zip</c>。</summary>
    Lunartique,
    /// <summary>语言文本：每条被改文本表一份 lcta-bus 规则集。</summary>
    LangBus,
    /// <summary>语言文本：每条被改文本表一份 RFC6902 <c>patchs</c> 文档。</summary>
    LangPatch,
    /// <summary>语言文本：每条被改文本表一份 pathset 覆盖文档。</summary>
    LangPathset,
    /// <summary>静态数据模组 <c>.staticmod</c>。</summary>
    StaticMod,
}

/// <summary>
/// 一个格式槽位的静态描述。<see cref="FolderName"/> 是二级文件夹名，
/// <see cref="SourceRelative"/> 说明产物按什么命名（加载器匹配口径，见 plan-16 §1 第 3 条）。
/// </summary>
/// <param name="Slot">槽位标识。</param>
/// <param name="Group">所属种类。</param>
/// <param name="FolderName">二级文件夹名（<c>bank</c> / <c>rebank</c> / <c>carra</c> / <c>lunartique</c> / <c>bus</c> / <c>patch</c> / <c>pathset</c> / <c>staticmod</c>）。</param>
/// <param name="DisplayName">中文显示名（报告用）。</param>
/// <param name="Extension">产物扩展名（含点；文本槽位为 <c>.json</c>）。</param>
/// <param name="NameBySource">产物是否按「来源名」命名（false = 单文件归档，按项目名）。</param>
/// <param name="DebugOverwritePatch">调试应用语义：true = 导出成 <c>__data</c>/<c>.bank</c> 后备份覆盖；
/// false = 标准 patch（写盘 + <c>.bak</c>）。</param>
public sealed record ExportSlotDescriptor(
    ExportSlot Slot,
    ExportGroup Group,
    string FolderName,
    string DisplayName,
    string Extension,
    bool NameBySource,
    bool DebugOverwritePatch);

/// <summary>
/// plan-16 §1：导出目录布局的唯一事实来源（纯数据，无 IO，可单测）。
///
/// <para>布局：<c>&lt;目标目录&gt;/&lt;项目名&gt;_&lt;种类&gt;/&lt;格式&gt;/&lt;产物&gt;</c>。
/// 「种类 → 格式」两层文件夹是用户口径；空槽位不建文件夹（不留空目录、不产空包）。</para>
/// </summary>
public static class ExportLayout
{
    /// <summary>音频种类的目录后缀。</summary>
    public const string GroupSuffixFmod = "_fmod";
    /// <summary>资源/数据种类的目录后缀。</summary>
    public const string GroupSuffixData = "_data";
    /// <summary>语言文本种类的目录后缀。</summary>
    public const string GroupSuffixText = "_text";
    /// <summary>静态数据种类的目录后缀。</summary>
    public const string GroupSuffixStatic = "_static";

    /// <summary>全部槽位（顺序 = 报告与执行顺序）。</summary>
    public static IReadOnlyList<ExportSlotDescriptor> All { get; } =
    [
        new(ExportSlot.Bank, ExportGroup.Fmod, "bank", "完整音频包（.bank）", ".bank", NameBySource: true, DebugOverwritePatch: true),
        new(ExportSlot.Rebank, ExportGroup.Fmod, "rebank", "音频差分（.rebank）", ".rebank", NameBySource: true, DebugOverwritePatch: true),
        new(ExportSlot.Carra, ExportGroup.Data, "carra", "资源对象包（.carra）", ".carra", NameBySource: false, DebugOverwritePatch: true),
        new(ExportSlot.Lunartique, ExportGroup.Data, "lunartique", "Lunartique 包（.zip）", ".zip", NameBySource: false, DebugOverwritePatch: true),
        new(ExportSlot.LangBus, ExportGroup.Text, "bus", "语言美化规则集（lcta-bus）", ".json", NameBySource: true, DebugOverwritePatch: false),
        new(ExportSlot.LangPatch, ExportGroup.Text, "patch", "语言补丁（RFC6902 patchs）", ".json", NameBySource: true, DebugOverwritePatch: false),
        new(ExportSlot.LangPathset, ExportGroup.Text, "pathset", "语言覆盖清单（pathset）", ".json", NameBySource: true, DebugOverwritePatch: false),
        new(ExportSlot.StaticMod, ExportGroup.Static, "staticmod", "静态数据模组（.staticmod）", ".staticmod", NameBySource: false, DebugOverwritePatch: false),
    ];

    /// <summary>某槽位的描述（未知槽位抛异常：槽位是闭集合，不该出现未登记的取值）。</summary>
    public static ExportSlotDescriptor For(ExportSlot slot)
        => All.FirstOrDefault(x => x.Slot == slot)
           ?? throw new ArgumentOutOfRangeException(nameof(slot), slot, "未登记的导出槽位。");

    /// <summary>某种类的目录名（<c>&lt;项目名&gt;_fmod</c> 形态）。</summary>
    public static string GroupFolder(ExportGroup group, string modName) => Sanitize(modName) + group switch
    {
        ExportGroup.Fmod => GroupSuffixFmod,
        ExportGroup.Data => GroupSuffixData,
        ExportGroup.Text => GroupSuffixText,
        ExportGroup.Static => GroupSuffixStatic,
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "未登记的导出种类。"),
    };

    /// <summary>某槽位的二级文件夹名。</summary>
    public static string FolderName(ExportGroup group, ExportSlot slot)
    {
        var descriptor = For(slot);
        if (descriptor.Group != group)
            throw new ArgumentException($"槽位 {slot} 属于 {descriptor.Group}，不属于 {group}。", nameof(group));
        return descriptor.FolderName;
    }

    /// <summary>产物的相对路径 = <c>&lt;项目名&gt;_&lt;种类&gt;/&lt;格式&gt;/&lt;文件名&gt;</c>。</summary>
    public static string RelativeOutputPath(ExportGroup group, ExportSlot slot, string modName, string outputFileName)
        => Path.Combine(GroupFolder(group, modName), FolderName(group, slot), outputFileName);

    /// <summary>文件名净化（模组名可能是用户随手写的中文/符号；Windows 下非法字符替换为 <c>_</c>）。</summary>
    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((string.IsNullOrWhiteSpace(name) ? "LME" : name.Trim())
            .Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "LME" : cleaned;
    }
}
