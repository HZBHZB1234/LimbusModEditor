using System.Globalization;

namespace LimbusModEditor.Application.Relations;

/// <summary>
/// 维基化关联页的入口：一个<b>主对象页</b>（人格 / 敌人 / 异想体 / 播报员 / E.G.O / E.G.O 饰品）。
///
/// <para><b>为什么独立于 <see cref="RelationSubject"></b>：RelationSubject 是分析器产出的派生事实，
/// 本记录是<b>人工编纂</b>的页面——两者的生命周期完全不同：
/// 分析器每次重跑都会覆盖 RelationSubject，而 WikiPage 只在用户主动编辑时才变。</para>
///
/// <param name="PageId">跨类别唯一的页面 id（<c>&lt;category&gt;:&lt;key&gt;</c>，复用 <see cref="SubjectIds"/> 编码）。</param>
/// <param name="Category">类别（<see cref="RelationCategories"/>）。</param>
/// <param name="Title">页面标题（如「格里高尔」）。</param>
/// <param name="Subtitle">副标题（原始风格 token，如 <c>Gregor</c>）。</param>
/// <param name="SortKey">排序键（补零后的 id，保证数字序）。</param>
/// <param name="CoverRef">预选封面（资源侧定位键，可为 null）。</param>
public sealed record WikiPage(
    string PageId,
    string Category,
    string Title,
    string Subtitle,
    string SortKey)
{
    /// <summary>类别的中文名（<see cref="RelationCategories.Label"/>）。</summary>
    public string CategoryLabel => RelationCategories.Label(Category);

    /// <summary>预选封面（资源侧定位键，可为 null）。</summary>
    public string? CoverRef { get; init; }
}

/// <summary>
/// 主对象页下的<b>二级页面</b>（可多个）。
///
/// <para>对应维基里的「分节」概念：介绍 / 相关角色 / 标题语音 / 相关考据 / 立绘。
/// 每个二级页面有独立的标题与排序位置，删除主页面时级联删除。</para>
///
/// <param name="SubPageId">全局唯一 id（GUID）。</param>
/// <param name="PageId">所属主对象页 id。</param>
/// <param name="Title">二级页面标题（如「介绍」「相关角色」）。</param>
/// <param name="SortOrder">在主页面的显示顺序（从小到大）。</param>
public sealed record WikiSubPage(
    string SubPageId,
    string PageId,
    string Title,
    int SortOrder);

/// <summary>
/// 二级页面里的一条<b>条目</b>（段落 / 条目 / 卡片）。
///
/// <para><b>人工编纂是唯一事实源</strong>：条目的内容（<paramref name="Title"/> / <paramref name="Body"/>）
/// 只由用户编辑产生，分析器<b>不得</b>自动改写。分析器产出的关联资源只能作为「候选」
/// 进入可一键导入/忽略的通道（见 <see cref="WikiEntrySources"/>）。</para>
///
/// <param name="EntryId">全局唯一 id（GUID）。</param>
/// <param name="SubPageId">所属二级页面 id。</param>
/// <param name="Title">条目标题（如「大致原出处」「主要相关点」）。</param>
/// <param name="Body">正文（纯文本，可空）。</param>
/// <param name="SortOrder">在二级页面的显示顺序（从小到大）。</param>
public sealed record WikiEntry(
    string EntryId,
    string SubPageId,
    string Title,
    string Body,
    int SortOrder)
{
    /// <summary>来源：<c>human</c> = 人工编纂；<c>candidate</c> = 分析器候选（已导入）。</summary>
    public string Source { get; init; } = WikiEntrySources.HumanEdited;

    /// <summary>候选来源 id（可追溯是哪个分析结果导入的；人工编纂时为 null）。</summary>
    public string? CandidateId { get; init; }
}

/// <summary>
/// 条目与真实资源的<b>绑定</b>。
///
/// <para>一条条目可以绑定多个资源（如「格里高尔」的介绍条目绑定立绘、CG、语音）。
/// 绑定走 <see cref="RelationDeepLink"/> 口径与 <c>IReferenceRevealable</c> 语义——
/// 定位失败返回 false、不得抛异常。</para>
///
/// <param name="BindingId">全局唯一 id（GUID）。</param>
/// <param name="EntryId">所属条目 id。</param>
/// <param name="RefKey">资源定位键（关联图里的口径：文本=相对路径；静态=容器路径；音频=bank 路径 + <c>\u0000</c> + 样本名）。</param>
/// <param name="Kind">关联类别（<see cref="RelationKind"/> 的名字）。</param>
/// <param name="Display">显示名（文件名 / 样本名 / 表名）。</param>
/// <param name="SortOrder">在条目内的显示顺序。</param>
public sealed record WikiResourceBinding(
    string BindingId,
    string EntryId,
    string RefKey,
    string Kind,
    string Display,
    int SortOrder)
{
    /// <summary>精确跳转载荷（<see cref="RelationDeepLink"/> 口径）。空则只能按资源级定位。</summary>
    public string? DeepLink { get; init; }

    /// <summary>预览内容（音频=台词原文、文本=片段、静态=记录名）。空表示拿不到内容。</summary>
    public string? PreviewText { get; init; }

    /// <summary>预览形态：<c>audio</c> / <c>text</c> / <c>static</c> / <c>image</c> / <c>video</c> / <c>spine</c> / <c>other</c>。</summary>
    public string MediaKind { get; init; } = "other";

    /// <summary>音频时长（秒）；非音频为 null。</summary>
    public double? DurationSec { get; init; }
}

/// <summary>
/// 条目的来源标识。稳定字符串，落库；改名要同步格式版本号。
/// </summary>
public static class WikiEntrySources
{
    /// <summary>人工编纂（用户直接编辑的条目）。</summary>
    public const string HumanEdited = "human";

    /// <summary>分析器候选导入（由 <see cref="SubjectRelationAnalyzer"/> 产出，用户一键导入）。</summary>
    public const string Candidate = "candidate";
}

/// <summary>
/// 页面树的完整层级（主对象页 → 二级页面 → 条目 → 资源绑定）。
/// 用于 UI 一次性加载某个主对象页的全部内容。
/// </summary>
public sealed record WikiPageDetail(
    WikiPage Page,
    IReadOnlyList<WikiSubPageDetail> SubPages);

/// <summary>
/// 二级页面及其条目。</summary>
public sealed record WikiSubPageDetail(
    WikiSubPage SubPage,
    IReadOnlyList<WikiEntryDetail> Entries);

/// <summary>
/// 条目及其资源绑定。</summary>
public sealed record WikiEntryDetail(
    WikiEntry Entry,
    IReadOnlyList<WikiResourceBinding> Bindings);

/// <summary>
/// 候选导入的分析结果（从 <see cref="RelationGraph"/> 提取，供用户一键导入/忽略）。
///
/// <para><b>候选不是事实</b>：它只是「分析器认为这里可能有内容」，
/// 用户可以选择导入（转为人工编纂）或忽略。候选与已编纂内容可区分（来源字段）、
/// 可追溯来源（<c>CandidateId</c>）。</para>
/// </summary>
/// <param name="CandidateId">候选唯一 id（格式：<c>subjectId#kind#refKey</c>）。</param>
/// <param name="SubjectId">关联对象 id。</param>
/// <param name="Kind">关联类别。</param>
/// <param name="RefKey">资源定位键。</param>
/// <param name="Display">显示名。</param>
/// <param name="PreviewText">预览内容（可为 null）。</param>
/// <param name="PreviewKind">关联强度。</param>
public sealed record WikiCandidate(
    string CandidateId,
    string SubjectId,
    string Kind,
    string RefKey,
    string Display,
    string? PreviewText,
    string PreviewKind)
{
    /// <summary>精确跳转载荷（可为 null）。</summary>
    public string? DeepLink { get; init; }

    /// <summary>预览形态。</summary>
    public string MediaKind { get; init; } = "other";

    /// <summary>音频时长（秒）。</summary>
    public double? DurationSec { get; init; }
}

/// <summary>
/// 候选导入的结果。</summary>
/// <param name="Imported">成功导入的条目数。</param>
/// <param name="Skipped">跳过的条目数（已存在）。</param>
public sealed record WikiCandidateImportResult(int Imported, int Skipped);

/// <summary>
/// 维基页库的格式版本号。
///
/// <para>当前 <c>v1</c>：初始版本（pages / sub_pages / entries / resource_bindings 四张表）。</para>
/// <para>改表结构 / 改序列化口径时 +1，旧库整库重建。</para>
/// </summary>
public static class WikiPageStoreFormat
{
    /// <summary>当前格式版本号。</summary>
    public const string Version = "v1";
}
