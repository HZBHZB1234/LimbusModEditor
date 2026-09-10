using LimbusModEditor.Application.Caching;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Assets;

/// <summary>
/// plan-11（音频工作台）：一个 bank 源（= 游戏 bank 目录）的缓存标识。
/// <para>签名只取<b>目录自身</b>的 mtime：目录里每个文件的新鲜度由 <c>banks</c> 表自己的
/// <c>(size_bytes, mtime_ticks)</c> 负责（与 <c>cache/text-index.db</c> 同口径）。
/// 目录签名变只说明「目录动过」（很可能新增/删除了 bank），**不代表内容全变了**，
/// 因此不整体重建，而是交给逐文件签名比对——这样「目录 mtime 变化」不会导致
/// 1531 个文件全部重解析。</para>
/// </summary>
/// <param name="BankDirectory">bank 目录（原样大小写，用于拼路径）。</param>
/// <param name="SourceKey">存进 <c>index_meta.source_key</c> 的源标识（小写规范化）。</param>
/// <param name="DirectorySignature">目录签名（<c>0:mtimeTicks</c> 文本）。</param>
public sealed record BankIndexSource(string BankDirectory, string SourceKey, string DirectorySignature)
{
    /// <summary>按目录路径构造源标识。</summary>
    public static BankIndexSource Describe(string bankDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankDirectory);
        var full = Path.GetFullPath(bankDirectory);
        return new BankIndexSource(full, full.ToLowerInvariant(), CacheSignature.FromDirectory(full).Format());
    }
}

/// <summary>
/// plan-11：索引里的一条样本记录（= 「全部音频」总表的一行）。
/// 字段与 <c>FSB5</c> 现读结果一一对应（<see cref="Fsb5SampleInfo"/> + bank 级 codec），
/// 不含任何推断值：采样率为 0 时时长显示「—」而不是猜。
/// </summary>
/// <param name="BankPath">所属 bank 的完整路径（关联 <c>banks.path</c>）。</param>
/// <param name="FsbIndex">该样本所在的 FSB 在 bank 内的序号。</param>
/// <param name="SampleIndex">样本在 FSB 内的序号（试听/替换/导出都用它定位）。</param>
/// <param name="BankFileName">bank 文件名（UI 显示用，去掉路径）。</param>
/// <param name="Name">样本名（FSB 名字表；缺失时由调用方给出占位名）。</param>
/// <param name="CodecName">bank 级 codec（FSB5 头部字段，如 Vorbis / PCM16）。</param>
/// <param name="SampleRate">采样率（Hz）。</param>
/// <param name="Channels">声道数。</param>
/// <param name="SampleCount">样本帧数（每声道）。</param>
/// <param name="DataSize">样本负载字节数（由下一条目的偏移推导）。</param>
/// <param name="DataOffset">样本负载在 FSB 内的偏移。</param>
public sealed record BankSampleRecord(
    string BankPath,
    int FsbIndex,
    int SampleIndex,
    string BankFileName,
    string Name,
    string CodecName,
    int SampleRate,
    int Channels,
    uint SampleCount,
    long DataSize,
    long DataOffset)
{
    /// <summary>时长（秒）；采样率为 0 时无法计算，返回 null（不猜）。</summary>
    public double? DurationSeconds => SampleRate > 0 ? SampleCount / (double)SampleRate : null;

    /// <summary>时长显示文案；无法计算时为「—」。</summary>
    public string DurationLabel => DurationSeconds is { } seconds ? $"{seconds:0.##} 秒" : "—";

    /// <summary>大小显示文案。</summary>
    public string SizeLabel => $"{DataSize:N0}";

    /// <summary>「FSB 3 · 样本 7」形式的定位文案（同一 bank 内唯一定位一个样本）。</summary>
    public string LocationLabel => $"FSB {FsbIndex} · 样本 {SampleIndex}";
}

/// <summary>
/// plan-11：索引里的一个 bank 文件条目（= 「bank 列表」视图的一行）。
/// <see cref="Samples"/> 是该 bank 的全部样本（事件 bank / 加密 bank / 无法识别为空）。
/// </summary>
/// <param name="Path">完整路径。</param>
/// <param name="FileName">文件名。</param>
/// <param name="SizeBytes">文件大小。</param>
/// <param name="MTimeUtcTicks">最后写入时间（UTC ticks，缓存新鲜度判据）。</param>
/// <param name="Kind">类型判定（与 <see cref="BankDirectoryService"/> 同一套判定）。</param>
/// <param name="KindNote">无法识别时的中文原因（其它类型为 null）。</param>
/// <param name="FsbCount">FSB 数。</param>
/// <param name="Note">补充说明（如单个 FSB 解析失败的原因）。</param>
/// <param name="Samples">样本行；非音频 bank 为空集合。</param>
/// <param name="ScannedTicks">本条目写入索引的时间（诊断用）。</param>
public sealed record BankIndexEntry(
    string Path,
    string FileName,
    long SizeBytes,
    long MTimeUtcTicks,
    BankKind Kind,
    string? KindNote,
    int FsbCount,
    string? Note,
    IReadOnlyList<BankSampleRecord> Samples,
    long ScannedTicks = 0)
{
    /// <summary>中文类型标签（与 <see cref="BankFileEntry.KindLabel"/> 同口径）。</summary>
    public string KindLabel => Kind switch
    {
        BankKind.Event => "事件 bank",
        BankKind.Audio => "音频 bank",
        BankKind.Encrypted => "加密 bank",
        _ => "无法识别",
    };

    /// <summary>列表视图用：转成既有的 <see cref="BankFileEntry"/> 显示模型。</summary>
    public BankFileEntry ToFileEntry() => new(FileName, Path, SizeBytes, Kind, FsbCount, KindNote ?? Note);
}

/// <summary>plan-11：一次刷新里对某个 bank 文件的磁盘观测值（只读元数据，不读内容）。</summary>
/// <param name="Path">完整路径。</param>
/// <param name="FileName">文件名。</param>
/// <param name="SizeBytes">文件大小。</param>
/// <param name="MTimeUtcTicks">最后写入时间（UTC ticks）。</param>
public sealed record BankFileObservation(string Path, string FileName, long SizeBytes, long MTimeUtcTicks);

/// <summary>plan-11：索引刷新的进度（UI 显示用）。</summary>
/// <param name="Processed">已处理的文件数。</param>
/// <param name="Total">需要处理的文件总数。</param>
/// <param name="CurrentFileName">刚处理完的文件名。</param>
/// <param name="Elapsed">已用时间。</param>
public sealed record BankIndexProgress(int Processed, int Total, string CurrentFileName, TimeSpan Elapsed)
{
    /// <summary>中文进度文案（如「正在建立音频索引 320/1531（21%）」）。</summary>
    public string Describe()
        => Total <= 0
            ? "正在建立音频索引…"
            : $"正在建立音频索引 {Processed}/{Total}（{Processed * 100 / Total}%）· 已用 {Elapsed.TotalSeconds:0.0} 秒 · {CurrentFileName}";
}

/// <summary>
/// plan-11：一次刷新索引的结果（供页面写状态与决定「是否需要重载视图」）。
/// </summary>
/// <param name="Directory">bank 目录。</param>
/// <param name="DiskFileCount">磁盘上的 bank 文件总数。</param>
/// <param name="ParseCount">本次真正重新解析（含读取 FSB 样本表）的文件数。</param>
/// <param name="ReusedCount">签名命中、直接复用缓存的文件数。</param>
/// <param name="RemovedCount">从索引里删除的（磁盘上已不存在）条目数。</param>
/// <param name="Elapsed">总耗时。</param>
/// <param name="Samples">本次索引得到的全部样本行数。</param>
public sealed record BankIndexRefreshResult(
    string Directory,
    int DiskFileCount,
    int ParseCount,
    int ReusedCount,
    int RemovedCount,
    TimeSpan Elapsed,
    int Samples)
{
    /// <summary>中文摘要文案。</summary>
    public string Describe()
        => $"已扫描 {DiskFileCount} 个 bank：重新解析 {ParseCount} · 复用缓存 {ReusedCount} · 移除 {RemovedCount}" +
           $" · 样本 {Samples} 行 · 用时 {Elapsed.TotalSeconds:0.0} 秒";
}

/// <summary>plan-11：从索引读出的完整快照（bank 列表 + 全部样本行）。</summary>
/// <param name="Directory">bank 目录（索引记录的源路径）。</param>
/// <param name="Entries">bank 条目（按文件名排序）。</param>
/// <param name="ReadElapsed">读库耗时（性能证据用）。</param>
public sealed record BankIndexSnapshot(string Directory, IReadOnlyList<BankIndexEntry> Entries, TimeSpan ReadElapsed)
{
    /// <summary>跨全部 bank 的样本行（「全部音频」总表的数据源，按 bank 名 + FSB + 样本号排序）。</summary>
    public IReadOnlyList<BankSampleRecord> AllSamples => Entries.SelectMany(x => x.Samples).ToList();
}
