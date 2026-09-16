namespace LimbusModEditor.Application.Relations;

/// <summary>
/// 维基分节定义：描述一个类别的页面应包含哪些分节、每个分节包含哪些资源种类、以及分节的显示顺序。
///
/// <para><b>为什么必须有这个</b>：不同类别的实体有不同的信息结构。
/// 人格有 CV/性别/身高/体重等字段，E.G.O 装备有等级/罪孽属性/理智消耗，
/// 敌人有危险等级/属性抗性。分节表把这些差异集中、可测地定义出来，
/// 避免在编排引擎里散落大量的 if/switch。</para>
///
/// <para><b>分节顺序</b>：按"用户视角"排序——先概览与基础信息，再数据与文本，
/// 再语音与立绘，最后技术资源与考据。这个顺序沿用
/// <see cref="PresetWorkbenchService.BuildDetail"/> 的既有分组经验。</para>
/// </summary>
/// <param name="SectionId">分节唯一标识（稳定字符串，落库；改名要同步格式版本号）。</param>
/// <param name="Title">分节中文标题（如「概览」「数据」「文本」「语音」）。</param>
/// <param name="Order">显示顺序（从小到大）。</param>
/// <param name="Kinds">该分节包含的资源种类（<see cref="RelationKind"/> 的名字集合）。</param>
public sealed record WikiSectionDefinition(
    string SectionId,
    string Title,
    int Order,
    IReadOnlyList<string> Kinds)
{
    /// <summary>该分节是否包含指定资源种类。</summary>
    public bool ContainsKind(string kind) => Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 6 类别各自的维基分节表。每个类别有固定的分节顺序与归类规则。
///
/// <para><b>归类规则</b>：资源按 <see cref="RelationKind"/> 归类到对应分节。
/// 归属由权威来源决定（资源目录前缀、lang 的 Egos.json 等），<b>不</b>由文件名猜测。</para>
/// </summary>
public static class WikiSectionTables
{
    /// <summary>通用分节：概览（所有类别都有）。包含信息框字段与一句话摘要。</summary>
    public const string Overview = "overview";

    /// <summary>通用分节：数据（静态表）。包含 static-data 资源。</summary>
    public const string StaticData = "static_data";

    /// <summary>通用分节：文本。包含 lang 文本文件。</summary>
    public const string Text = "text";

    /// <summary>通用分节：语音。包含 bank 音频样本。</summary>
    public const string Audio = "audio";

    /// <summary>通用分节：立绘与图集。包含图像资源。</summary>
    public const string Images = "images";

    /// <summary>通用分节：Spine 与动画。包含 Spine 骨骼动画与 Unity 动画片段。</summary>
    public const string SpineAndAnimation = "spine_animation";

    /// <summary>通用分节：其它资源。包含 Prefab/网格/视频/其它。</summary>
    public const string OtherResources = "other_resources";

    /// <summary>通用分节：考据。包含跨资源引用（xref）。</summary>
    public const string Trivia = "trivia";

    /// <summary>人格专属：人格剧情。</summary>
    public const string PersonalityStory = "personality_story";

    /// <summary>敌人专属：遭遇战。</summary>
    public const string Encounter = "encounter";

    /// <summary>异想体专属：异想体日志。</summary>
    public const string AbnormalityLog = "abnormality_log";

    /// <summary>播报员专属：播报语音。</summary>
    public const string AnnouncerVoice = "announcer_voice";

    /// <summary>E.G.O 专属：侵蚀与觉醒。</summary>
    public const string EgoCorruption = "ego_corruption";

    /// <summary>E.G.O 饰品专属：饰品效果。</summary>
    public const string GiftEffect = "gift_effect";

    // ── 人格（Persona）分节表 ───────────────────────────────────────

    /// <summary>人格类别的分节表。顺序：概览 → 数据 → 文本 → 语音 → 立绘 → Spine → 剧情 → 其它 → 考据。</summary>
    public static readonly IReadOnlyList<WikiSectionDefinition> Persona =
    [
        new(Overview, "概览", 0, []),
        new(StaticData, "数据", 1, ["StaticData"]),
        new(Text, "文本", 2, ["Text"]),
        new(Audio, "语音", 3, ["Audio"]),
        new(Images, "立绘与图集", 4, ["Image"]),
        new(SpineAndAnimation, "Spine 与动画", 5, ["Spine", "Animation"]),
        new(PersonalityStory, "人格剧情", 6, []),
        new(OtherResources, "其它资源", 7, ["Prefab", "Mesh", "Video", "Other"]),
        new(Trivia, "考据", 8, []),
    ];

    // ── 敌人（Enemy）分节表 ─────────────────────────────────────────

    /// <summary>敌人类别的分节表。顺序：概览 → 数据 → 文本 → 语音 → 立绘 → Spine → 遭遇战 → 其它 → 考据。</summary>
    public static readonly IReadOnlyList<WikiSectionDefinition> Enemy =
    [
        new(Overview, "概览", 0, []),
        new(StaticData, "数据", 1, ["StaticData"]),
        new(Text, "文本", 2, ["Text"]),
        new(Audio, "语音", 3, ["Audio"]),
        new(Images, "立绘与图集", 4, ["Image"]),
        new(SpineAndAnimation, "Spine 与动画", 5, ["Spine", "Animation"]),
        new(Encounter, "遭遇战", 6, []),
        new(OtherResources, "其它资源", 7, ["Prefab", "Mesh", "Video", "Other"]),
        new(Trivia, "考据", 8, []),
    ];

    // ── 异想体（Abnormality）分节表 ─────────────────────────────────

    /// <summary>异想体类别的分节表。顺序：概览 → 数据 → 文本 → 语音 → 立绘 → Spine → 日志 → 其它 → 考据。</summary>
    public static readonly IReadOnlyList<WikiSectionDefinition> Abnormality =
    [
        new(Overview, "概览", 0, []),
        new(StaticData, "数据", 1, ["StaticData"]),
        new(Text, "文本", 2, ["Text"]),
        new(Audio, "语音", 3, ["Audio"]),
        new(Images, "立绘与图集", 4, ["Image"]),
        new(SpineAndAnimation, "Spine 与动画", 5, ["Spine", "Animation"]),
        new(AbnormalityLog, "异想体日志", 6, []),
        new(OtherResources, "其它资源", 7, ["Prefab", "Mesh", "Video", "Other"]),
        new(Trivia, "考据", 8, []),
    ];

    // ── 播报员（Announcer）分节表 ───────────────────────────────────

    /// <summary>播报员类别的分节表。顺序：概览 → 数据 → 文本 → 播报语音 → 立绘 → 其它 → 考据。</summary>
    public static readonly IReadOnlyList<WikiSectionDefinition> Announcer =
    [
        new(Overview, "概览", 0, []),
        new(StaticData, "数据", 1, ["StaticData"]),
        new(Text, "文本", 2, ["Text"]),
        new(AnnouncerVoice, "播报语音", 3, ["Audio"]),
        new(Images, "立绘与图集", 4, ["Image"]),
        new(OtherResources, "其它资源", 5, ["Prefab", "Mesh", "Video", "Other"]),
        new(Trivia, "考据", 6, []),
    ];

    // ── E.G.O 装备（Ego）分节表 ─────────────────────────────────────

    /// <summary>E.G.O 装备类别的分节表。顺序：概览 → 数据 → 文本 → 语音 → 立绘 → Spine → 侵蚀/觉醒 → 其它 → 考据。</summary>
    public static readonly IReadOnlyList<WikiSectionDefinition> Ego =
    [
        new(Overview, "概览", 0, []),
        new(StaticData, "数据", 1, ["StaticData"]),
        new(Text, "文本", 2, ["Text"]),
        new(Audio, "语音", 3, ["Audio"]),
        new(Images, "立绘与图集", 4, ["Image"]),
        new(SpineAndAnimation, "Spine 与动画", 5, ["Spine", "Animation"]),
        new(EgoCorruption, "侵蚀与觉醒", 6, []),
        new(OtherResources, "其它资源", 7, ["Prefab", "Mesh", "Video", "Other"]),
        new(Trivia, "考据", 8, []),
    ];

    // ── E.G.O 饰品（EgoGift）分节表 ─────────────────────────────────

    /// <summary>E.G.O 饰品类别的分节表。顺序：概览 → 数据 → 文本 → 语音 → 立绘 → 饰品效果 → 其它 → 考据。</summary>
    public static readonly IReadOnlyList<WikiSectionDefinition> EgoGift =
    [
        new(Overview, "概览", 0, []),
        new(StaticData, "数据", 1, ["StaticData"]),
        new(Text, "文本", 2, ["Text"]),
        new(Audio, "语音", 3, ["Audio"]),
        new(Images, "立绘与图集", 4, ["Image"]),
        new(GiftEffect, "饰品效果", 5, []),
        new(OtherResources, "其它资源", 6, ["Prefab", "Mesh", "Video", "Other"]),
        new(Trivia, "考据", 7, []),
    ];

    /// <summary>获取指定类别的分节表。未知类别返回通用分节表（概览 + 数据 + 文本 + 语音 + 立绘 + 其它 + 考据）。</summary>
    public static IReadOnlyList<WikiSectionDefinition> ForCategory(string category) => category switch
    {
        RelationCategories.Persona => Persona,
        RelationCategories.Enemy => Enemy,
        RelationCategories.Abnormality => Abnormality,
        RelationCategories.Announcer => Announcer,
        RelationCategories.Ego => Ego,
        RelationCategories.EgoGift => EgoGift,
        _ => Generic,
    };

    /// <summary>通用分节表（未知类别回退）。</summary>
    private static readonly IReadOnlyList<WikiSectionDefinition> Generic =
    [
        new(Overview, "概览", 0, []),
        new(StaticData, "数据", 1, ["StaticData"]),
        new(Text, "文本", 2, ["Text"]),
        new(Audio, "语音", 3, ["Audio"]),
        new(Images, "立绘与图集", 4, ["Image"]),
        new(SpineAndAnimation, "Spine 与动画", 5, ["Spine", "Animation"]),
        new(OtherResources, "其它资源", 6, ["Prefab", "Mesh", "Video", "Other"]),
        new(Trivia, "考据", 7, []),
    ];
}
