namespace LimbusModEditor.Application.Relations.Authority;

/// <summary>
/// 权威来源类型：描述一条归属事实的数据来源。
///
/// <para><b>为什么需要这个</b>：不同的数据来源有不同的可靠度。
/// 容器路径前缀是权威的（Unity 资产数据库里的硬关系），
/// 而文件名猜测只是推导。来源类型决定了置信度（<see cref="ConfidenceLevel"/>）。</para>
/// </summary>
public enum AuthoritySource
{
    /// <summary>未知来源。</summary>
    Unknown,

    // ── 权威来源（Authoritative）────────────────────────────────────

    /// <summary>Unity m_Container 容器路径前缀（如 /Prefab/SD/Personality/&lt;id&gt;_...）。</summary>
    ContainerPathPrefix,

    /// <summary>Lang 文件名约定（如 Voice_&lt;prefix&gt;_&lt;character&gt;_&lt;id&gt;.json）。</summary>
    LangFileName,

    /// <summary>Lang 权威清单（Egos.json 的 id 集合、EGOgift*.json 的 id 集合）。</summary>
    LangAuthoritativeList,

    /// <summary>静态表显式外键字段（characterId / skillId / EGOId）。</summary>
    StaticTableForeignKey,

    /// <summary>Bank 样本名精确匹配（样本名 == 台词 id）。</summary>
    BankSampleExactMatch,

    /// <summary>Prefab 内部真实引用链（AssetsTools.NET 可读引用）。</summary>
    PrefabReferenceChain,

    /// <summary>Sprite → Texture / SpriteAtlas → 子图 引用链。</summary>
    AssetReferenceChain,

    /// <summary>Sprite 基名精确匹配（如 announcer_&lt;name&gt;.png 的 _announcer 后缀）。</summary>
    SpriteBaseNameMatch,

    // ── 推导来源（Derived）─────────────────────────────────────────

    /// <summary>AbDlg_&lt;字符&gt;.json 文件名中的角色 token 匹配。</summary>
    LangCharacterToken,

    /// <summary>Announcer_&lt;字符&gt;_&lt;n&gt;.json 文件名中的角色 token 匹配。</summary>
    LangAnnouncerToken,

    /// <summary>静态表 imgStr 反查（播报员）。</summary>
    StaticTableImgStrLookup,

    /// <summary>E.G.O 饰品 id 归一化（id % 10000，仅限饰品类别）。</summary>
    EgoGiftIdNormalization,

    // ── 交叉校验来源（仅用于验证，不直接产出归属）───────────────

    /// <summary>数字窗口匹配（已知集合内）。</summary>
    NumericWindowMatch,

    /// <summary>目录约定（Spine/CG 等）。</summary>
    DirectoryConvention,
}
