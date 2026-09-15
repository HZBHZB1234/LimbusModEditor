using System.Globalization;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.StaticMods;

/// <summary>
/// 「这一条资源是不是静态数据表」的**判据位掩码**（位可以叠加，所以能回答「为什么被当成静态」）。
///
/// <para><b>为什么要位掩码而不是 bool</b>：三个判据的**可信来源不同** —— 位1 依赖 catalog
/// （游戏热修换键后可能暂时不可用），位2 只看 bundle 名，位4 只看资源自己的容器路径。
/// 合成一个 bool 之后，catalog 一旦缺席就无法再恢复「本来是静态」的结论（这正是老功能
/// 「时灵时不灵」的根因）；而且翻转时只能整体重算、丢掉另外两条判据的依据。
/// 分开存位之后：catalog 缺席只影响位1，位2/位4 仍是既成事实，结论依旧成立。</para>
///
/// <para><b>判定成本</b>：位1/位2 是 bundle 级（1,471 个），位4 是行级但只对**有容器条目**
/// 的行（真实缓存 51,376 / 1,275,623 = 4%）才真的要解析路径，其余行一次空串判断就退出。</para>
///
/// <para><b>判据本体不在这里</b>：静态数据 bundle 名与静态数据容器路径的事实定义在
/// <see cref="StaticBundleLocator"/>（静态数据工作台 / 关系索引 / ModApply 也在用它，
/// 口径必须只有一份）。本类只负责「把三条判据装进一个可索引的整数」。</para>
/// </summary>
[Flags]
public enum StaticKind
{
    /// <summary>不是静态数据表。</summary>
    None = 0,

    /// <summary>catalog 里 <c>static_s1_0_assets_all_*.bundle</c> 的内层键命中该 bundle。</summary>
    CatalogMark = 1,

    /// <summary>bundle 名带静态前缀（不依赖 catalog；缓存目录名是裸哈希时命中不了）。</summary>
    BundleName = 2,

    /// <summary>资源的容器路径落在 <c>Assets/Resources_moved/StaticData/static-data/</c>。</summary>
    ContainerPath = 4,

    /// <summary>bundle 级位（位1|位2）—— 与具体某一行无关，整支 bundle 一致。</summary>
    BundleMask = CatalogMark | BundleName,
}

/// <summary>
/// 静态数据表判据的**唯一出处**（扫描期算一次、落进索引与项目元数据，查询期只比一次整数）。
///
/// <para>静态标记曾经是「运行时逐行重算三条判据」，代价是每查一次列表都要对有容器条目的
/// 5 万行做字符串判据（真实库实测 1.8 s/次）。现在改为扫描期算完落库（
/// <c>assets.static_kind</c>），查询期只做 <c>static_kind = 0</c> 这样一次可走索引的整数比较。</para>
///
/// <para><b>读写都要走这里</b>：写（扫描）用 <see cref="BundleBits"/> / <see cref="RowBits"/> /
/// <see cref="Merge"/>，读（列表判据、预览、筛选）用 <see cref="Of(AssetRecord)"/> 或
/// <see cref="IsStatic"/>。任何一侧自己拼字符串，都会出现「列表藏了、预览不认」这类错位。</para>
/// </summary>
public static class AssetStaticClassifier
{
    /// <summary>bundle 级位：catalog 内层键命中（位1）| bundle 名前缀（位2）。</summary>
    /// <param name="catalogMark">catalog 的静态内层键集合里是否有这个 bundle。
    /// <b>catalog 不可用时必须传 false</b>（不知道 ≠ 不是），位2/位4 会兜住结论。</param>
    /// <param name="innerKey">bundle 的内层键（缓存目录里通常是裸 32 位哈希）。</param>
    public static StaticKind BundleBits(bool catalogMark, string? innerKey)
        => (catalogMark ? StaticKind.CatalogMark : StaticKind.None)
           | (StaticBundleLocator.LooksLikeStaticBundle(innerKey) ? StaticKind.BundleName : StaticKind.None);

    /// <summary>行级位：容器路径（位4）。无容器条目的行恒为 <see cref="StaticKind.None"/>。</summary>
    public static StaticKind RowBits(string? containerEntry)
        => StaticBundleLocator.LooksLikeStaticTablePath(containerEntry) ? StaticKind.ContainerPath : StaticKind.None;

    /// <summary>合并两处结论（幂等：同一个位叠两次还是一位）。</summary>
    public static StaticKind Merge(StaticKind left, StaticKind right) => left | right;

    public static bool IsStatic(StaticKind kind) => kind != StaticKind.None;

    /// <summary>
    /// 解析落库/落盘的值。兼容两端的历史形态：
    /// <list type="bullet">
    /// <item>旧记录的 <c>"true"</c>（老版本只写这一个字符串）→ 记作位1（catalog 标记）——
    /// 那时能写下这个标记的唯一来源就是 catalog 内层键命中。</item>
    /// <item>十进制整数（现形态，如 <c>"5"</c> = 位1|位4）。</item>
    /// <item>空 / 无法解析 → <see cref="StaticKind.None"/>（保守：宁可不藏，也不错藏）。</item>
    /// </list>
    /// </summary>
    public static StaticKind Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return StaticKind.None;
        var text = value.Trim();
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) return StaticKind.CatalogMark;
        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) return StaticKind.None;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits)) return StaticKind.None;
        if (bits <= 0) return StaticKind.None;
        // 只保留认得的位：将来加了新判据而旧版本读到新库时，未知位不该被当成「静态」的理由。
        var known = (StaticKind)(bits & (int)(StaticKind.CatalogMark | StaticKind.BundleName | StaticKind.ContainerPath));
        return known;
    }

    /// <summary>落库的形态（十进制整数；<see cref="StaticKind.None"/> 不写）。</summary>
    public static string Format(StaticKind kind) => ((int)kind).ToString(CultureInfo.InvariantCulture);

    /// <summary>一条记录上的静态结论（元数据键见 <c>UnityCacheScanService.StaticBundleMetadataKey</c>）。</summary>
    public static StaticKind Of(AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return asset.Metadata.TryGetValue(Scanning.UnityCacheScanService.StaticBundleMetadataKey, out var value)
            ? Parse(value)
            : StaticKind.None;
    }

    /// <summary>给日志/诊断用的一行「为什么被判成静态」（没有命中位时是「非静态」）。</summary>
    public static string Describe(StaticKind kind)
    {
        if (kind == StaticKind.None) return "非静态";
        var parts = new List<string>(3);
        if (kind.HasFlag(StaticKind.CatalogMark)) parts.Add("catalog 标记");
        if (kind.HasFlag(StaticKind.BundleName)) parts.Add("bundle 名");
        if (kind.HasFlag(StaticKind.ContainerPath)) parts.Add("容器路径");
        var unknown = (int)(kind & ~(StaticKind.CatalogMark | StaticKind.BundleName | StaticKind.ContainerPath));
        if (unknown != 0) parts.Add($"未知位 {unknown}");
        return string.Join(" + ", parts);
    }
}
