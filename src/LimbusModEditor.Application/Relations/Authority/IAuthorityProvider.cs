namespace LimbusModEditor.Application.Relations.Authority;

/// <summary>
/// 权威来源提供者接口：为特定类别提供权威事实抽取能力。
///
/// <para><b>设计原则</b>：每个类别有自己的提供者，负责从权威来源抽取事实。
/// 提供者只产出 <see cref="ConfidenceLevel.Authoritative"/> 的事实，
/// 不产出 Derived 或 Ambiguous（由引擎统一处理）。</para>
/// </summary>
public interface IAuthorityProvider
{
    /// <summary>该提供者支持的类别。</summary>
    string Category { get; }

    /// <summary>
    /// 为指定对象抽取权威事实。
    /// <param name="subjectId">对象 id。</param>
    /// <param name="context">抽取上下文（包含四源事实与索引）。</param>
    /// <returns>权威事实集合。只返回 <see cref="ConfidenceLevel.Authoritative"/> 的事实。</returns>
    /// </summary>
    AuthorityFacts Extract(string subjectId, AuthorityExtractionContext context);
}

/// <summary>
/// 权威抽取上下文：包含抽取所需的所有输入数据。
/// </summary>
/// <param name="Assets">Unity 资源索引（有容器路径的资源）。</param>
/// <param name="Audio">bank 样本事实。</param>
/// <param name="StaticTables">静态数据表事实。</param>
/// <param name="LangFiles">lang 文件事实。</param>
/// <param name="TextAnchors">lang 锚点（id → 可展示内容）。</param>
public sealed record AuthorityExtractionContext(
    IReadOnlyList<LimbusModEditor.Domain.Assets.AssetRecord> Assets,
    IReadOnlyList<RelationAudioFact> Audio,
    IReadOnlyList<RelationStaticFact> StaticTables,
    IReadOnlyList<RelationLangFact> LangFiles,
    IReadOnlyList<RelationTextAnchor> TextAnchors)
{
    /// <summary>锚点索引（按 anchor id 精确查）。</summary>
    public IReadOnlyDictionary<string, RelationTextAnchor> AnchorIndex()
    {
        var map = new Dictionary<string, RelationTextAnchor>(StringComparer.Ordinal);
        foreach (var anchor in TextAnchors)
            if (!string.IsNullOrEmpty(anchor.Anchor)) map.TryAdd(anchor.Anchor, anchor);
        return map;
    }
}
