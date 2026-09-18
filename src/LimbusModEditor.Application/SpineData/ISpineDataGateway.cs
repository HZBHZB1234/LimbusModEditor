namespace LimbusModEditor.Application.SpineData;

/// <summary>
/// C# 侧 Spine 数据网关：定位并流式喂原始字节给前端。
/// 替换现有 SpineAnimationSourceService（解析逻辑前移前端）。
/// 所有方法绝不抛异常；失败返回中文原因。
/// </summary>
public interface ISpineDataGateway
{
    /// <summary>按资产 ID 获取 Spine 原始素材。</summary>
    Task<(SpineRawData? Data, string? Error)> GetSpineDataAsync(
        string assetId, CancellationToken cancellationToken = default);

    /// <summary>按容器路径获取 Spine 原始素材（供前端按路径定位）。</summary>
    Task<(SpineRawData? Data, string? Error)> GetSpineDataByPathAsync(
        string containerEntry, CancellationToken cancellationToken = default);

    /// <summary>批量获取（供前端预取窗口）。</summary>
    Task<IReadOnlyList<SpineRawData>> GetSpineDataBatchAsync(
        IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default);

    /// <summary>枚举所有完整的 Spine 三件套集合（骨架 + atlas + 纹理）。</summary>
    Task<IReadOnlyList<SpineSetInfo>> EnumerateCompleteSetsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>按目录前缀查找 Spine 集合（不限完整三件套）。</summary>
    Task<IReadOnlyList<SpineSetInfo>> FindByFolderPrefixAsync(
        string folderPrefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>只列名册、不解素材</b>：把索引库里所有「值得试一次引用链」的 Spine 挂点列出来
    /// （<c>SpineIllustPrefab</c> + <c>Prefab/SD/**</c> 的 prefab），按
    /// <paramref name="keyword"/> 过滤骨架名。
    ///
    /// <para><b>为什么不在这里解三件套</b>：解一套要整读一个大 bundle（实测平均 2.1 s），
    /// 全库几百套一次性解就是几十分钟。名册只做索引查询（毫秒级），
    /// 具体某一条的三件套由 <see cref="GetSpineDataByPathAsync"/> <b>按需</b>取
    /// （并有按 refKey 的结果缓存）。</para>
    ///
    /// <para><paramref name="excludeRefKeys"/> 用来排除「已被维基页面绑定」的条目，
    /// 便于只浏览没进过页面的那批。</para>
    /// </summary>
    Task<SpineCatalogPage> BrowseCatalogAsync(
        SpineCatalogQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// 名册 + 概览<b>一次取数</b>给出。名册要扫一遍 127 万行的 <c>assets</c>
    /// （容器路径是 <c>LIKE '%…%'</c>，用不上索引），分成两次调用就是把这个开销付两遍 ——
    /// <c>spine.catalog</c> 走这条。
    /// </summary>
    Task<SpineCatalogResult> BrowseCatalogWithSummaryAsync(
        SpineCatalogQuery query, CancellationToken cancellationToken = default);

    /// <summary>名册概览计数（总数 / 已绑定 / 未绑定），不列举明细。</summary>
    Task<SpineCatalogSummary> SummarizeCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>清空结果缓存（重新扫描缓存目录后由调用方按需调用）。</summary>
    void InvalidateCache();
}
