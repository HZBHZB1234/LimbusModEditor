namespace LimbusModEditor.Application.Relations.Authority;

/// <summary>
/// 置信度：描述一条归属事实的可靠程度。
///
/// <para><b>为什么需要这个</b>：不同来源的可靠度不同。
/// 权威来源（如容器路径前缀）可以直接展示给用户，
/// 推导来源（如文件名猜测）需要标注为"推测"，
/// 歧义来源需要标注为"可能属于多个对象"。</para>
/// </summary>
public enum ConfidenceLevel
{
    /// <summary>无置信度（无来源）。</summary>
    None,

    /// <summary>权威（Authoritative）：来自硬关系（容器路径、权威清单、外键、精确匹配）。</summary>
    Authoritative,

    /// <summary>推导（Derived）：来自推导规则（文件名约定、角色 token、归一化）。</summary>
    Derived,

    /// <summary>歧义（Ambiguous）：同一内容同时命中多个类别，无法静默二选一。</summary>
    Ambiguous,
}
