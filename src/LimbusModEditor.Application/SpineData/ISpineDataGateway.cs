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
}
