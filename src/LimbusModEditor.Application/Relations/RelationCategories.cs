namespace LimbusModEditor.Application.Relations;

/// <summary>
/// 关联图的「预设对象类别」。每个类别有<b>自己的关联键与权威来源</b>，
/// 共享同一张 <c>subjects</c> / <c>links</c> 表与同一个卡片流页面。
///
/// <para><b>为什么 subject_id 必须带类别前缀</b>：不同类别的 id 会真的撞车——
/// 实测敌人 id 段含 <c>90005</c>，而播报员 id 段从 <c>90001</c> 起，
/// 异常 4 位段（1080–8633）与敌人 4 位段（1079…）相邻。
/// 所以关联键统一存成 <c>&lt;category&gt;:&lt;key&gt;</c>（见 <see cref="SubjectIds"/>）。</para>
///
/// <para><b>绝不复用「扫 5 位数字窗口」的通用匹配</b>：人格 id 是固定 5 位，
/// 但敌人是变长 4–5 位，4 位值会与事件 id、异常 id 撞。类别一律由<b>资源目录前缀</b>
/// 判定（<c>Prefab/SD/Enemy/</c> 等），中立路径靠「这个数字属于哪个类别的 id 集合」去歧义。</para>
/// </summary>
public static class RelationCategories
{
    /// <summary>人格（Identity）。键 = 5 位人格 id（10000–12999）。</summary>
    public const string Persona = "persona";

    /// <summary>敌人单位（Enemy）。键 = <c>Prefab/SD/Enemy/&lt;id&gt;_…Appearance.prefab</c> 里的数字 id（变长 4–5 位）。</summary>
    public const string Enemy = "enemy";

    /// <summary>异想体（Abnormality）。键 = <c>Prefab/SD/Abnormality/&lt;id&gt;_…Appearance.prefab</c> 里的 4 位 id。</summary>
    public const string Abnormality = "abnormality";

    /// <summary>播报员（Announcer）。键 = 播报图 sprite 基名（如 <c>gregor_announcer</c>），
    /// 静态表的 <c>imgStr</c> 也是这个口径。</summary>
    public const string Announcer = "announcer";

    /// <summary>全部类别（顺序即 UI 里的切换顺序）。</summary>
    public static readonly IReadOnlyList<string> All =
        [Persona, Enemy, Abnormality, Announcer];

    /// <summary>类别的中文名（卡片流页的类别切换、状态栏用）。</summary>
    public static string Label(string category) => category switch
    {
        Persona => "人格",
        Enemy => "敌人单位",
        Abnormality => "异想体",
        Announcer => "播报员",
        _ => category,
    };

    /// <summary>这个字符串是不是一个已登记的类别。</summary>
    public static bool IsKnown(string? category)
        => !string.IsNullOrEmpty(category) && All.Contains(category, StringComparer.Ordinal);
}

/// <summary>
/// 关联键的编码：<c>&lt;category&gt;:&lt;key&gt;</c>。
/// 解析端（查询门面 / 卡片流 / 精确跳转）都只走这两个方法，不手写拼接。
/// </summary>
public static class SubjectIds
{
    /// <summary>分隔符。用 <c>:</c> 是因为两个部分都不含它（类别是标识符，键是 id / sprite 基名）。</summary>
    public const char Separator = ':';

    /// <summary>拼出一个跨类别唯一的对象 id。</summary>
    public static string Make(string category, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return string.Concat(category, Separator.ToString(), key);
    }

    /// <summary>拆出类别；不是 <c>类别:键</c> 形态时返回空串（旧库 / 手工数据不崩）。</summary>
    public static string CategoryOf(string? subjectId)
    {
        if (string.IsNullOrEmpty(subjectId)) return string.Empty;
        var index = subjectId.IndexOf(Separator);
        return index <= 0 ? string.Empty : subjectId[..index];
    }

    /// <summary>拆出类别内的原始键；不是 <c>类别:键</c> 形态时原样返回。</summary>
    public static string KeyOf(string? subjectId)
    {
        if (string.IsNullOrEmpty(subjectId)) return string.Empty;
        var index = subjectId.IndexOf(Separator);
        return index <= 0 ? subjectId : subjectId[(index + 1)..];
    }
}
