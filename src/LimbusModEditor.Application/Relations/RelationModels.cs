using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Relations;

// 类别常量与关联键编码在 RelationCategories.cs（多类别后有自己的规格与 <category>:<key> 编码）。

/// <summary>关联资源的种类（决定预览面板里归到哪一栏、以及卡片流里的图标）。</summary>
public enum RelationKind
{
    Unknown,
    /// <summary>lang 文本文件（相对活动语言目录的路径）。</summary>
    Text,
    /// <summary>static-data 静态数据表（容器路径）。</summary>
    StaticData,
    /// <summary>FMOD bank 样本（ref = bank 源文件 + <c>\u0000</c> + 样本名）。</summary>
    Audio,
    /// <summary>图像 / 精灵图。</summary>
    Image,
    /// <summary>视频（PersonalityVideo/&lt;id&gt;.mp4）。</summary>
    Video,
    /// <summary>Spine 骨骼动画资源（骨架 JSON / 图集描述 / SkeletonData / .psb 立绘）。</summary>
    Spine,
    /// <summary>Unity 动画片段。</summary>
    Animation,
    /// <summary>Prefab / GameObject / 组件 / MonoBehaviour。</summary>
    Prefab,
    /// <summary>网格。</summary>
    Mesh,
    /// <summary>其它。</summary>
    Other,
}

/// <summary>
/// 一条关联的「关联强度」——决定 UI 怎么标注，也决定它能不能被当成事实展示。
///
/// <para><b>为什么必须有这个</b>：真实数据里大量关联是<b>推导</b>出来的而不是精确命中的
/// （例如「样本名里含某人格 id → 这个音频属于该人格」是人格级、不是台词级）。
/// 把推导结果伪装成精确命中会误导用户，所以强度必须显式落库、显式展示。</para>
/// </summary>
public enum RelationPreviewKind
{
    /// <summary>没有可展示的内容。</summary>
    None,
    /// <summary>精确命中（如样本名 == 台词 id，能直接给出该条台词的原文）。</summary>
    Exact,
    /// <summary>推导到对象级（如音频属于某人格，但定位不到具体哪条台词）。</summary>
    Derived,
    /// <summary>只能到「按关卡组织」的粒度（敌人/异常的文本按关卡，不按 id）。</summary>
    ChapterLevel,
    /// <summary>同号 id 在多个类别里都存在，无法静默二选一。</summary>
    Ambiguous,
}

/// <summary>预设对象（人格 / 敌人 / 异想体 / 播报员）的展示记录。</summary>
/// <param name="SubjectId">跨类别唯一的关联键（<c>&lt;category&gt;:&lt;key&gt;</c>，见 <see cref="SubjectIds"/>）。</param>
/// <param name="SubjectKind">类别（<see cref="RelationCategories"/>）。</param>
/// <param name="DisplayName">卡片标题里的名字（如「浮士德 · LCB」）。</param>
/// <param name="Subtitle">副标题（原始风格 token，如 <c>LCB</c>）。</param>
/// <param name="Character">角色名 token（如 <c>Faust</c>）。</param>
/// <param name="SortKey">排序键（补零后的 id，保证数字序）。</param>
public sealed record RelationSubject(
    string SubjectId,
    string SubjectKind,
    string DisplayName,
    string Subtitle,
    string Character,
    string SortKey)
{
    /// <summary>类别的中文名（<see cref="RelationCategories.Label"/>）。</summary>
    public string CategoryLabel { get; init; } = string.Empty;

    /// <summary>预选封面（资源侧定位键）。分析阶段就定好，避免每个页面各自重算。</summary>
    public string? CoverRef { get; init; }

    /// <summary>卡片正面的一句话预览（如代表台词）。</summary>
    public string? PreviewText { get; init; }

    /// <summary>关联资源总数（卡片角标；避免 UI 为每张卡再查一次）。</summary>
    public int LinkCount { get; init; }
}

/// <summary>对象 → 一条关联资源。</summary>
/// <param name="RefKey">定位键：文本=相对路径；静态=容器路径；音频=bank 路径 + <c>\u0000</c> + 样本名；
/// 图像/动画/Spine=Unity 容器路径（游戏内资源路径）。</param>
/// <param name="SizeBytes">资源体积（字节）；文本类记的是键数、不是字节数时由 <paramref name="Detail"/> 说明。</param>
public sealed record RelationLink(
    string SubjectId,
    string Category,
    RelationKind Kind,
    string RefKey,
    string Display,
    string? Detail,
    long SizeBytes)
{
    /// <summary>可直接展示的内容：音频=台词原文，静态数据=记录 name·desc，文本=片段。
    /// 空表示这条关联拿不到内容（UI 就不该画预览区）。</summary>
    public string? PreviewText { get; init; }

    /// <summary>这条关联的强度（见 <see cref="RelationPreviewKind"/>）。</summary>
    public RelationPreviewKind PreviewKind { get; init; } = RelationPreviewKind.None;

    /// <summary>预览形态：<c>audio</c> / <c>text</c> / <c>static</c> / <c>image</c> / <c>video</c> / <c>spine</c> / <c>other</c>。
    /// UI 按它选预览器，不需要自己再判 <see cref="Kind"/>。</summary>
    public string MediaKind { get; init; } = "other";

    /// <summary>音频时长（秒）；非音频为 null。由 <c>sample_count / sample_rate</c> 算出，无需解码。</summary>
    public double? DurationSec { get; init; }

    /// <summary>读字节 / 跳转用的真实路径：文本=相对活动语言目录，音频=bank 路径，其它=容器路径。</summary>
    public string? RefPath { get; init; }

    /// <summary>精确跳转载荷（<c>'\0'</c> 分隔，见 <see cref="RelationDeepLink"/>）。空则只能按资源级定位。</summary>
    public string? DeepLink { get; init; }

    /// <summary>交叉关联指向的其它对象 id。</summary>
    public string? TargetSubjectId { get; init; }
}

/// <summary>
/// 一条显式的跨资源边。**多对多**：一个 <see cref="FromRef"/> 可以有很多条，
/// 一个 <see cref="ToRef"/> 也可以被很多条指向（例如一个台词 id 既能对上音频样本、
/// 又能对上人格、还能对上关卡）。设计上不设「一条起点只能有一条边」的约束。
/// </summary>
/// <param name="FromRef">起点定位键（音频样本名 / 文本相对路径 / 静态表容器路径 / 对象 id）。</param>
/// <param name="ToRef">终点定位键。</param>
/// <param name="Relation">关系名，见 <see cref="RelationXrefKinds"/>。</param>
/// <param name="FromKind">起点的形态（audio/text/static/subject/image…），便于不 join 也能筛。</param>
/// <param name="ToKind">终点的形态。</param>
/// <param name="Confidence">强度（<see cref="RelationPreviewKind"/> 的名字）。</param>
/// <param name="Detail">中文补充（如命中的台词分类 desc / 技能名）。</param>
public sealed record RelationXref(
    string FromRef,
    string ToRef,
    string Relation,
    string FromKind,
    string ToKind,
    string Confidence,
    string? Detail);

/// <summary>跨资源边的关系名（稳定字符串，落库；改名要同步 <c>FormatVersion</c>）。</summary>
public static class RelationXrefKinds
{
    /// <summary>音频样本 → 台词文本（实测为精确一对一：样本名 == 台词 id）。</summary>
    public const string AudioToVoiceText = "audio->voice_text";

    /// <summary>音频样本 → 对象（人格/播报员…）；精确 id 未命中时的降级关联。</summary>
    public const string AudioToSubject = "audio->subject";

    /// <summary>静态表记录 → lang 记录（数值 id 外键链）。</summary>
    public const string StaticToLang = "static->lang";

    /// <summary>资源 → 对象（含中立路径的歧义关联）。</summary>
    public const string ResourceToSubject = "resource->subject";
}

/// <summary>一条 lang 里可被当成「锚点」的事实：一个 id 对应的可展示内容。</summary>
/// <param name="Anchor">锚点 id（语音=台词 id，如 <c>battle_break_10201_1</c>；被动/EGO=数值 id，如 <c>1010101</c> / <c>20101</c>）。</param>
/// <param name="RelativePath">所在 lang 文件（相对活动语言目录）。</param>
/// <param name="Name">名称（被动/EGO 的 <c>name</c>）；语音为 null。</param>
/// <param name="Desc">描述（语音的 <c>desc</c> 如「自身混乱」；被动/EGO 的 <c>desc</c> 描述）。</param>
/// <param name="Body">正文（语音的 <c>dlg</c> 台词原文）。</param>
public sealed record RelationTextAnchor(
    string Anchor, string RelativePath, string? Name, string? Desc, string? Body);

/// <summary>分析输入：Unity 资源索引里**有容器路径**的资源事实（容器路径是用户可读口径）。</summary>
public sealed record RelationAssetFact(string ContainerEntry, AssetType Type, long SizeBytes);

/// <summary>分析输入：bank 样本事实。</summary>
public sealed record RelationAudioFact(string BankPath, string SampleName, string? CodecName, long SizeBytes)
{
    /// <summary>采样率（Hz）。0 = 未知（拿不到时长）。</summary>
    public int SampleRate { get; init; }

    /// <summary>采样点数。与 <see cref="SampleRate"/> 一起算出时长，**不需要解码音频**。</summary>
    public int SampleCount { get; init; }

    /// <summary>时长（秒）；拿不到采样信息时为 null。</summary>
    public double? DurationSeconds => SampleRate > 0 && SampleCount > 0
        ? (double)SampleCount / SampleRate
        : null;
}

/// <summary>分析输入：静态数据表（正文可为 null —— 非 UTF-8 或未读取时）。</summary>
/// <param name="SizeBytes">正文字节数（正文不可读时给 0）。</param>
public sealed record RelationStaticFact(
    string ContainerEntry, string Name, string DataClass, string? Text, long SizeBytes);

/// <summary>分析输入：lang 文件（相对活动语言目录）。</summary>
public sealed record RelationLangFact(string RelativePath, int KeyCount);

/// <summary>分析产出的完整关联图（纯数据，可直接落缓存 / 直接断言）。</summary>
public sealed record RelationGraph(
    IReadOnlyList<RelationSubject> Subjects,
    IReadOnlyList<RelationLink> Links)
{
    /// <summary>显式的跨资源边（多对多）。分析器不产出时为空。</summary>
    public IReadOnlyList<RelationXref> Xrefs { get; init; } = [];

    public static readonly RelationGraph Empty = new([], []);

    /// <summary>某对象的全部关联资源（按类别 + 定位键排序）。</summary>
    public IReadOnlyList<RelationLink> LinksOf(string subjectId)
        => Links.Where(x => string.Equals(x.SubjectId, subjectId, StringComparison.Ordinal))
            .OrderBy(x => x.Kind).ThenBy(x => x.RefKey, StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>分析输入打包（便于调用方一次性传入、也便于单测构造）。</summary>
public sealed record RelationInputs(
    IReadOnlyList<RelationAssetFact> Assets,
    IReadOnlyList<RelationAudioFact> Audio,
    IReadOnlyList<RelationStaticFact> StaticTables,
    IReadOnlyList<RelationLangFact> LangFiles)
{
    public static readonly RelationInputs Empty = new([], [], [], []);

    /// <summary>
    /// lang 里抽出来的「锚点」（台词 id → 台词原文、被动/EGO 数值 id → 名称描述）。
    /// <b>可选输入</b>：不提供时只是拿不到「预览内容」，关联主体与资源仍照常建
    /// （这样分析器在任何输入缺档的情况下都不崩、只是少一层信息）。
    /// </summary>
    public IReadOnlyList<RelationTextAnchor> TextAnchors { get; init; } = [];

    /// <summary>锚点索引（按 <see cref="RelationTextAnchor.Anchor"/> 精确查）。</summary>
    public IReadOnlyDictionary<string, RelationTextAnchor> AnchorIndex()
    {
        var map = new Dictionary<string, RelationTextAnchor>(StringComparer.Ordinal);
        foreach (var anchor in TextAnchors)
            if (!string.IsNullOrEmpty(anchor.Anchor)) map.TryAdd(anchor.Anchor, anchor);
        return map;
    }
}

/// <summary>
/// <c>cache/relation-index.db</c> 的源标识：它是<b>派生</b>缓存，源 = 四个上游缓存库 + lang 锚点。
///
/// <para><b>为什么用四个源的签名拼接</b>：关联图完全由四个库的内容决定，
/// 只要其中一个库换了源（换游戏目录 / 换活动语言 / 热修换内容哈希 / 重扫 bundle），
/// 关联图就可能整体不同，必须整库重建。四个组件各自的签名已经由各自的服务算好了，
/// 这里只做拼接与去重，<b>不重算、不缓存 hash 常量</b>。</para>
///
/// <para><b>为什么不拿四个库文件的 mtime 当签名</b>：SQLite 在 WAL 模式下写事务未必改变
/// 主库文件 mtime（只写 <c>-wal</c>），拿它判「源变没变」会漏判；而四个组件签名是各自
/// 服务真实使用的语义判据（目录签名 / 内容哈希 / 内容口径版本），语义正确。</para>
/// </summary>
/// <param name="SourceKey">存进 <c>index_meta.source_key</c> 的源标识（四个源键的稳定摘要）。</param>
/// <param name="Signature">最终签名文本（内容口径版本 + 四个源签名）。</param>
public sealed record RelationIndexSource(string SourceKey, string Signature)
{
    /// <summary>
    /// 关联图的口径版本。只要「抽哪些事实 / 怎么算关联 / 表结构」变了就 +1，
    /// 旧的派生库不能继续被当成新鲜的。
    /// <para><b>v1 → v2</b>：subject_id 加类别前缀（多类别）、links 增加预览与跳转载荷、
    /// 新增 <c>xref</c> 表、新增 lang 锚点输入。</para>
    /// </summary>
    public const string FormatVersion = "v2";

    /// <summary>由四个上游源的签名构造。</summary>
    /// <param name="unitySignature">资源索引（Unity 缓存 bundle 集合）的签名。</param>
    /// <param name="bankSignature">音频索引的签名（bank 目录签名）。</param>
    /// <param name="staticSignature">静态表索引的签名（内层内容哈希 + 内容签名）。</param>
    /// <param name="textSignature">文本索引的签名（内容口径版本 + 目录签名 + config 哈希 + 语言目录）。</param>
    public static RelationIndexSource From(
        string unitySignature, string bankSignature, string staticSignature, string textSignature)
    {
        ArgumentNullException.ThrowIfNull(unitySignature);
        ArgumentNullException.ThrowIfNull(bankSignature);
        ArgumentNullException.ThrowIfNull(staticSignature);
        ArgumentNullException.ThrowIfNull(textSignature);
        var signature = $"{FormatVersion}|{unitySignature}|{bankSignature}|{staticSignature}|{textSignature}";
        // 源键只用于「是不是同一个源」的快速比较；用整个签名即可（不额外做摘要求值，
        // 避免引入一个新的哈希实现——签名本身就是稳定文本）。
        return new RelationIndexSource("relations", signature);
    }
}
