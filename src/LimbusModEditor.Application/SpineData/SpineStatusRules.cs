namespace LimbusModEditor.Application.SpineData;

/// <summary>
/// 「一条 Spine 挂点在界面上的解析状态」的判据（纯规则，可单测）。
///
/// <para><b>为什么要单独抽出来</b>：状态文案散在两处（名册 <c>spine.catalog</c> 与
/// 明细 <c>spine.resolve</c>）会各自漂移，用户在列表里看到「已解析」、
/// 点进去却是「取不到」，这种自相矛盾比没有状态更糟。两处共用同一份判据。</para>
///
/// <para><b>措辞上的诚实要求</b>：名册阶段<b>没有解过素材</b>，所以只能说
/// 「未命中（预期）」这类<b>基于已知事实</b>的话，不能说「已解析出三件套」——
/// 那是 <c>spine.resolve</c> 真的解出来之后才配说的话。</para>
/// </summary>
public static class SpineStatusRules
{
    /// <summary>解析状态：已解析出完整三件套（只有真的解过才给这个）。</summary>
    public const string Parsed = "parsed";

    /// <summary>名册阶段：bundle 在、归属明确，<b>预期可解但尚未解</b>。</summary>
    public const string Likely = "likely";

    /// <summary>bundle 文件此刻不在本机（Unity 缓存被清，非代码可补）。</summary>
    public const string BundleMissing = "bundle-missing";

    /// <summary>引用链里确实没有骨架与图集（该 prefab 本来就不是 Spine）—— 正确结果，不是缺陷。</summary>
    public const string NoSkeleton = "no-skeleton";

    /// <summary>在关联索引里没有归属，未硬塞给任何角色。</summary>
    public const string Uncategorized = "uncategorized";

    /// <summary>其它失败。</summary>
    public const string Failed = "failed";

    /// <summary>状态的中文说明。</summary>
    public static string Label(string status) => status switch
    {
        Parsed => "已解析出三件套",
        Likely => "可解析（尚未取，打开时按需解）",
        BundleMissing => "bundle 不在本机（Unity 缓存被清，跑一次游戏即回来）",
        NoSkeleton => "未命中：引用链里没有骨架与图集（该 prefab 本来就不是 Spine）",
        Uncategorized => "未归类：关联索引里查不到它属于哪个角色",
        Failed => "取数失败",
        _ => status,
    };

    /// <summary>
    /// 名册阶段的状态：<b>不解素材</b>，只按「bundle 在不在 + 有没有归属」说话。
    /// </summary>
    /// <param name="bundlePresent">bundle 文件此刻在不在本机。</param>
    /// <param name="ownerCount">关联索引里归属的页面数（0 = 未归类）。</param>
    public static string CatalogStatus(bool bundlePresent, int ownerCount)
    {
        if (!bundlePresent) return BundleMissing;
        return ownerCount > 0 ? Likely : Uncategorized;
    }

    /// <summary>
    /// 明细阶段（<c>spine.resolve</c>）的状态：按<b>真的解出来没有</b> + 中文原因定。
    /// </summary>
    /// <param name="ok">三件套是否取到。</param>
    /// <param name="bundlePresent">bundle 文件此刻在不在本机。</param>
    /// <param name="reason">失败时的中文原因（用于区分「本来不是 Spine」与「其它失败」）。</param>
    public static string ResolveStatus(bool ok, bool bundlePresent, string? reason)
    {
        if (ok) return Parsed;
        if (!bundlePresent) return BundleMissing;
        if (!string.IsNullOrEmpty(reason) && MeansNotASpinePrefab(reason)) return NoSkeleton;
        return Failed;
    }

    /// <summary>
    /// 这句中文原因是不是在说「这个 prefab 本来就不是 Spine」（而不是「取数出错了」）。
    ///
    /// <para>匹配的是<b>生产代码里真实存在的措辞</b>（<c>RelationDisplayRules.IsSpinePath</c>
    /// 的路径预检、<c>SpineDataGateway</c> 的同目录口径、<c>SpinePrefabChainResolver</c>
    /// 的引用链口径），不是随手编的字符串。措辞改了这里必须跟着改，
    /// 所以两边都有测试锁住（见 <c>SpineWikiBindingTests</c>）。</para>
    /// </summary>
    private static bool MeansNotASpinePrefab(string reason)
        => reason.Contains("不是 Spine", StringComparison.Ordinal)          // 预检：「路径 "X" 不是 Spine 资源。」
        || reason.Contains("找不到骨架", StringComparison.Ordinal)          // 「同目录里找不到骨架 JSON（*.json）…」
        || reason.Contains("没有骨架", StringComparison.Ordinal)            // 「…也没有从 "X" 的引用链里找到骨架与图集」
        || reason.Contains("找不到骨架与图集", StringComparison.Ordinal);

    /// <summary>
    /// 名册一条的「来源」中文说明（用户问的是「这条从哪来的、归谁」）。
    /// </summary>
    /// <param name="boundPageCount">已被多少页面绑定（&gt; 0 = 本来就在页面上）。</param>
    /// <param name="ownerCount">自动接入归属到的页面数。</param>
    /// <param name="bundlePresent">bundle 在不在。</param>
    public static string Source(bool bundlePresent, int boundPageCount, int ownerCount)
    {
        if (boundPageCount > 0) return $"既有页面绑定（{boundPageCount} 页）";
        if (!bundlePresent) return "未绑定 · bundle 不在本机";
        if (ownerCount > 0) return $"关联索引自动接入（{ownerCount} 页）";
        return "未绑定 · 关联索引未归类";
    }
}