namespace LimbusModEditor.Application.Relations.Authority;

/// <summary>
/// 可写出处类型：显式三态，避免 null/空串魔法值被误合并。
/// </summary>
public enum WritableSourceKind
{
    /// <summary>未知/尚未分析出出处（待办）。</summary>
    Unknown,

    /// <summary>可读但不可写（已确认无可写出处，如 Spine prefab 内部引用、atlas 文本）。</summary>
    None,

    /// <summary>有可写出处（如 lang 键路径、资源容器路径、静态表记录键）。</summary>
    Path,
}

/// <summary>
/// 一条可用事实：描述「某个资源/内容属于某个对象」的归属关系。
///
/// <para><b>核心设计</b>：每条事实都带有来源（<see cref="Source"/>）、
/// 置信度（<see cref="ConfidenceLevel"/>）、以及可写出处（<see cref="WritableSourceKind"/> + <see cref="WritableSourcePath"/>）。
/// 可写出处用于将来把编辑内容导出为模组（如 lang 键路径、资源容器路径）。</para>
///
/// <para><b>重要</b>：只产出有来源的事实。本地数据无来源的内容不产出 Fact，
/// 仅在覆盖度矩阵中标记为"本地无来源"。无来源 = 不渲染、不展示空白占位。</para>
/// </summary>
/// <param name="SubjectId">对象 id（格式 <c>&lt;category&gt;:&lt;key&gt;</c>）。</param>
/// <param name="ContentType">内容类型（如 "static_data", "text", "audio", "image", "spine"）。</param>
/// <param name="RefKey">资源定位键（关联图里的口径）。</param>
/// <param name="Source">权威来源类型。</param>
/// <param name="Confidence">置信度。</param>
/// <param name="WritableSource">可写出处类型（显式三态）。</param>
public sealed record AuthorityFact(
    string SubjectId,
    string ContentType,
    string RefKey,
    AuthoritySource Source,
    ConfidenceLevel Confidence,
    WritableSourceKind WritableSource)
{
    /// <summary>
    /// 可写出处路径（当 <see cref="WritableSource"/> 为 <see cref="WritableSourceKind.Path"/> 时有效）。
    /// 例如：lang 键路径、资源容器路径、静态表记录键。
    /// </summary>
    public string? WritableSourcePath { get; init; }

    /// <summary>
    /// 显示名（文件名 / 样本名 / 表名）。
    /// </summary>
    public string? Display { get; init; }

    /// <summary>
    /// 补充说明（编码 / 体积 / 键数）。
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// 预览内容（音频=台词原文、文本=片段、静态=记录名）。空表示拿不到内容。
    /// </summary>
    public string? PreviewText { get; init; }

    /// <summary>
    /// 预览形态：<c>audio</c> / <c>text</c> / <c>static</c> / <c>image</c> / <c>video</c> / <c>spine</c> / <c>other</c>。
    /// </summary>
    public string MediaKind { get; init; } = "other";

    /// <summary>
    /// 音频时长（秒）；非音频为 null。
    /// </summary>
    public double? DurationSec { get; init; }

    /// <summary>
    /// 精确跳转载荷（<see cref="RelationDeepLink"/> 口径）。空则只能按资源级定位。
    /// </summary>
    public string? DeepLink { get; init; }

    /// <summary>
    /// 来源详情（用于追溯到具体规则）。
    /// 例如：路径前缀名、外键字段名、匹配的 lang 文件。
    /// </summary>
    public string? SourceDetail { get; init; }
}

/// <summary>
/// 抽取结果：包含可用事实与不可用类型清单。
///
/// <para><b>设计原则</b>：只产出有来源的事实（Available）。
/// 本地数据无来源的内容不产出 Fact，仅在 Unavailable 中标记。
/// 前端只渲染 Available，不渲染 Unavailable（不展示空白占位）。</para>
/// </summary>
public sealed record AuthorityFacts(
    string SubjectId,
    IReadOnlyList<AuthorityFact> Facts,
    IReadOnlyList<UnavailableType> Unavailable)
{
    /// <summary>按置信度过滤。</summary>
    public IReadOnlyList<AuthorityFact> ByConfidence(ConfidenceLevel level)
        => Facts.Where(f => f.Confidence == level).ToArray();

    /// <summary>按内容类型过滤。</summary>
    public IReadOnlyList<AuthorityFact> ByContentType(string contentType)
        => Facts.Where(f => string.Equals(f.ContentType, contentType, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>只返回权威事实（不含 Derived / Ambiguous）。</summary>
    public IReadOnlyList<AuthorityFact> AuthoritativeOnly => ByConfidence(ConfidenceLevel.Authoritative);

    /// <summary>可用事实数量。</summary>
    public int AvailableCount => Facts.Count;

    /// <summary>不可用类型数量（本地无来源）。</summary>
    public int UnavailableCount => Unavailable.Count;
}

/// <summary>
/// 不可用类型：本地数据无来源的内容类型。
/// <para>不产出 Fact，仅在覆盖度矩阵中标记。</para>
/// </summary>
/// <param name="ContentType">内容类型（如 "static_data", "text", "audio"）。</param>
/// <param name="Reason">不可用的原因。</param>
public sealed record UnavailableType(string ContentType, string Reason);
