using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Relations;

/// <summary>关联图的「预设对象类别」。当前只实现人格（Identity）；后续 EGO / 异常 / 关卡
/// 只是再喂一份 <see cref="PersonaRelationAnalyzer"/> 的输入即可复用同一张表与同一个页面。</summary>
public static class RelationCategories
{
    /// <summary>人格（Identity）。真实数据里以 5 位人格 id（10101…11216）为关联键。</summary>
    public const string Persona = "persona";
}

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

/// <summary>预设对象（人格）的展示记录。</summary>
/// <param name="SubjectId">关联键（人格 id，如 <c>10201</c>）。</param>
/// <param name="SubjectKind">类别（<see cref="RelationCategories"/>）。</param>
/// <param name="DisplayName">卡片标题（如「浮士德 · LCB」）。</param>
/// <param name="Subtitle">副标题（原始风格 token，如 <c>LCB</c>）。</param>
/// <param name="Character">角色名 token（如 <c>Faust</c>）。</param>
/// <param name="SortKey">排序键（补零后的人格 id，保证数字序）。</param>
public sealed record RelationSubject(
    string SubjectId,
    string SubjectKind,
    string DisplayName,
    string Subtitle,
    string Character,
    string SortKey);

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
    long SizeBytes);

/// <summary>分析输入：Unity 资源索引里**有容器路径**的资源事实（容器路径是用户可读口径）。</summary>
public sealed record RelationAssetFact(string ContainerEntry, AssetType Type, long SizeBytes);

/// <summary>分析输入：bank 样本事实。</summary>
public sealed record RelationAudioFact(string BankPath, string SampleName, string? CodecName, long SizeBytes);

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
}

/// <summary>
/// <c>cache/relation-index.db</c> 的源标识：它是<b>派生</b>缓存，源 = 四个上游缓存库。
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
    /// <summary>关联图的口径版本。只要「抽哪些事实 / 怎么算关联」变了就 +1，
    /// 旧的派生库不能继续被当成新鲜的。</summary>
    public const string FormatVersion = "v1";

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
