using LimbusModEditor.Application.Caching;

namespace LimbusModEditor.Application.StaticMods;

/// <summary>
/// plan-12：静态数据表索引的源标识。
///
/// <para><b>为什么用内层内容哈希而不是外层键</b>：外层键是 Unity 缓存目录名，热修后
/// 会随新内容重新分配（再次启动游戏时可能与先前不同），拿它做缓存键会导致「内容没变、
/// 只是外层目录名变了」也判定为换源而整库重建。内层内容哈希（
/// <c>static_s1_0_assets_all_&lt;32hex&gt;.bundle</c> 里的 32hex）才是内容的身份——
/// 内容变 → 哈希变 → 缓存自然作废，符合「绝不缓存 hash 常量、每次动态解析」的铁律。</para>
///
/// <para>外层键与缓存路径不进签名（它们只是「这次从哪读」），但缓存行里仍记
/// dataPath，便于诊断与「缓存未命中」提示。</para>
/// </summary>
/// <param name="BundleName">bundle 文件名（含内容哈希）。</param>
/// <param name="InnerHash">内层内容哈希（bundle 名里的 32hex）。</param>
/// <param name="OuterKey">本次解析得到的外层键（可空；仅诊断用，不作缓存键）。</param>
/// <param name="DataPath">本次解析得到的缓存文件路径（<c>__data</c>）。</param>
/// <param name="ContentSignature">内容签名（<c>__data</c> 的 length:mtimeTicks；缺失时为空串）。</param>
public sealed record StaticIndexSource(
    string BundleName,
    string InnerHash,
    string? OuterKey,
    string? DataPath,
    string ContentSignature)
{
    /// <summary>存进 <c>index_meta.source_key</c> 的源标识（内层哈希，小写）。</summary>
    public string SourceKey => InnerHash.ToLowerInvariant();

    /// <summary>按定位结果构造（<c>__data</c> 不存在时签名为 <c>0</c>，仍可用于「已定位但未缓存」提示）。</summary>
    public static StaticIndexSource From(StaticBundleLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var signature = location.IsCached
            ? CacheSignature.FromFile(location.DataPath!).Format()
            : new CacheSignature(0, 0).Format();
        return new StaticIndexSource(location.BundleName, location.InnerHash, location.OuterKey, location.DataPath, signature);
    }
}

/// <summary>
/// plan-12：索引里的一张静态表（元数据，不含正文）。
/// 与 <see cref="StaticBundleLocator.StaticTextAsset"/> 的容器/分组口径一致
/// （dataClass 与 fileName 由既有的 <c>SplitTableIdentity</c> 推导，不重写规则）。
/// </summary>
/// <param name="ContainerEntry">m_Container 游戏内资源路径（.staticmod 的 container 字段）。</param>
/// <param name="Name">m_Name（TextAsset 名）。</param>
/// <param name="DataClass">数据类（分组）。</param>
/// <param name="FileName">表名（.staticmod 的 file 字段）。</param>
/// <param name="SerializedFile">SerializedFile 名（技术键）。</param>
/// <param name="PathId">PathId（精确定位）。</param>
/// <param name="SizeBytes">正文字节数。</param>
/// <param name="IsUtf8">正文是否合法 UTF-8（非 UTF-8 不猜编码）。</param>
/// <param name="CachedTicks">该行写入索引的时间（诊断用）。</param>
public sealed record StaticTableEntry(
    string ContainerEntry,
    string Name,
    string DataClass,
    string FileName,
    string SerializedFile,
    long PathId,
    long SizeBytes,
    bool IsUtf8,
    long CachedTicks = 0)
{
    /// <summary>列表/树显示用的大小文案。</summary>
    public string SizeLabel => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024.0:0.0} MB"
        : SizeBytes >= 1024 ? $"{SizeBytes / 1024.0:0.0} KB" : $"{SizeBytes} B";

    /// <summary>索引主键（容器路径；空容器时退回 name + pathId，保证唯一）。</summary>
    public string Key => string.IsNullOrWhiteSpace(ContainerEntry) ? $"{Name}|{PathId}" : ContainerEntry;
}

/// <summary>
/// plan-12：从索引读出的一张表 + 其正文（正文可能来自缓存或现场解码）。
/// </summary>
/// <param name="Entry">元数据。</param>
/// <param name="Text">UTF-8 正文；非 UTF-8 或未解码时为 null。</param>
/// <param name="FromCache">正文是否直接来自 <c>documents</c> 缓存。</param>
public sealed record StaticTableDocument(StaticTableEntry Entry, string? Text, bool FromCache);

/// <summary>
/// plan-12：索引装载结果。
/// </summary>
/// <param name="Source">本次的源标识。</param>
/// <param name="Entries">索引里的表（按 dataClass + 表名排序）。</param>
/// <param name="IsUsable">索引存在、源一致且非空，可直接使用。</param>
/// <param name="DatabaseExists">库文件是否存在。</param>
/// <param name="SameSource">库里记录的是不是当前源（内层哈希一致）。</param>
/// <param name="ReadElapsed">读库耗时。</param>
public sealed record StaticIndexLoad(
    StaticIndexSource Source,
    IReadOnlyList<StaticTableEntry> Entries,
    bool IsUsable,
    bool DatabaseExists,
    bool SameSource,
    TimeSpan ReadElapsed)
{
    /// <summary>按数据类分组（🗂 树视图的骨架）。</summary>
    public IReadOnlyList<(string DataClass, IReadOnlyList<StaticTableEntry> Tables)> GroupedByDataClass()
        => Entries.GroupBy(x => x.DataClass, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => (x.Key, (IReadOnlyList<StaticTableEntry>)x.OrderBy(y => y.FileName, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
}

/// <summary>plan-12：建索引的进度（UI 显示用）。</summary>
/// <param name="Processed">已处理表数。</param>
/// <param name="Total">总表数。</param>
/// <param name="CurrentTable">刚处理完的表名。</param>
/// <param name="Elapsed">已用时间。</param>
public sealed record StaticIndexProgress(int Processed, int Total, string CurrentTable, TimeSpan Elapsed)
{
    /// <summary>中文进度文案。</summary>
    public string Describe() => Total <= 0
        ? "正在读取静态数据表…"
        : $"正在读取静态数据表 {Processed}/{Total}（{Processed * 100 / Total}%）· 已用 {Elapsed.TotalSeconds:0.0} 秒 · {CurrentTable}";
}

/// <summary>plan-12：一次建索引的结果。</summary>
/// <param name="BundleName">bundle 名。</param>
/// <param name="TableCount">索引到的表数。</param>
/// <param name="TotalBytes">全部表正文字节数合计。</param>
/// <param name="Elapsed">总耗时。</param>
/// <param name="DiskCacheHit">是否命中 <c>__data</c> 缓存（false = 需要启动一次游戏）。</param>
public sealed record StaticIndexBuildResult(
    string BundleName,
    int TableCount,
    long TotalBytes,
    TimeSpan Elapsed,
    bool DiskCacheHit)
{
    /// <summary>中文摘要。</summary>
    public string Describe()
        => DiskCacheHit
            ? $"已索引 {TableCount} 张静态表（正文合计 {TotalBytes / 1024.0 / 1024.0:0.0} MB）· 用时 {Elapsed.TotalSeconds:0.0} 秒"
            : "缓存里还没有这个 bundle 的条目：请启动一次游戏生成缓存后重试（编辑器只读缓存，不写游戏目录）。";
}
