using System.Diagnostics;
using LimbusModEditor.Application.Caching;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Formats.Bank;

namespace LimbusModEditor.Application.Assets;

/// <summary>
/// plan-11：音频工作台的索引编排——「枚举目录 → 逐文件签名比对 → 只重解析变过的文件
/// → 单事务落库」，带进度回调与取消。
///
/// <para><b>解析全部复用既有实现</b>：类型判定与样本表都走
/// <see cref="BankDirectoryService"/>（前缀快路径 + 流式探测、<c>Fsb5Parser</c>），
/// 本类<b>不新增第三处 bank 解析逻辑</b>，只负责「什么时候需要解析」与「结果怎么存」。</para>
///
/// <para><b>并行度受限</b>（默认 4）：1531 个文件的读取在机械盘上并行过高反而更慢；
/// 且解析在大文件上会占内存，因此不做无界并发。</para>
///
/// <para><b>可直接复用的单文件入口</b>：<see cref="ParseBankFileAsync"/> 与
/// <see cref="BuildEntry"/> 被 <c>BankDirectoryService.ScanDirectory</c>、
/// <c>BankDirectoryService.ReadSampleTable</c> 与「有缓存 vs 删库」一致性测试共用——
/// 两条路径的产物因此逐字段相同（这是本计划最重要的正确性不变量）。</para>
/// </summary>
public sealed class BankIndexService
{
    /// <summary>默认并行度上限（机械盘友好）。</summary>
    public const int DefaultMaxDegreeOfParallelism = 4;

    private readonly BankDirectoryService _banks = new();
    private readonly BankIndexStore _store;
    private readonly int _maxDegreeOfParallelism;

    /// <param name="store">索引库（页面传 <c>new BankIndexStore(host.Env.CacheDirectory)</c>）。</param>
    /// <param name="maxDegreeOfParallelism">并行解析上限（≤ 4）。</param>
    public BankIndexService(BankIndexStore store, int maxDegreeOfParallelism = DefaultMaxDegreeOfParallelism)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _maxDegreeOfParallelism = Math.Clamp(maxDegreeOfParallelism, 1, DefaultMaxDegreeOfParallelism);
    }

    /// <summary>解析用到的 bank 目录服务（页面直接用它做试听/样本表等既有功能）。</summary>
    public BankDirectoryService Banks => _banks;

    /// <summary>索引库（页面诊断/清缓存用）。</summary>
    public BankIndexStore Store => _store;

    // ── 目录 / 缓存读取 ──────────────────────────────────────────────

    /// <summary>解析 &lt;游戏目录&gt;/…/FMODBuilds/Desktop；缺失返回 null（页面据此提示设置游戏目录）。</summary>
    public string? ResolveBankDirectory(string? gameDirectory) => _banks.ResolveBankDirectory(gameDirectory);

    /// <summary>
    /// 读缓存快照（不碰磁盘上的 bank 文件）。库不存在 / 源换了 / 库是空的时
    /// <see cref="IsUsable"/> 为 false，页面据此走一次冷建索引。
    /// </summary>
    public BankIndexRefreshSnapshot Load(BankIndexSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var storedSource = _store.ReadSourceKey();
        var sameSource = storedSource is not null &&
                         string.Equals(storedSource, source.SourceKey, StringComparison.OrdinalIgnoreCase);
        var snapshot = _store.ReadSnapshot(source.BankDirectory);
        var usable = sameSource && snapshot.Entries.Count > 0;
        return new BankIndexRefreshSnapshot(snapshot, usable, _store.Exists, sameSource);
    }

    /// <summary>是否存在可用的索引库（不做源校验，供「清空缓存」按钮判断可用性）。</summary>
    public bool HasCache => _store.ReadBankCount() > 0;

    // ── 刷新索引 ─────────────────────────────────────────────────────

    /// <summary>
    /// 刷新索引：枚举目录 → 逐文件签名比对 → 只重解析变过的文件（并行、可取消、带进度）
    /// → 单事务落库 → 删除磁盘上已不存在的条目。
    /// </summary>
    /// <param name="source">当前源（目录变了即视为换源）。</param>
    /// <param name="progress">进度回调（建议页面用 <c>IProgress&lt;T&gt;</c> 转到 UI 线程）。</param>
    /// <param name="cancellationToken">取消（页面卸载/切页时取消，不阻塞 UI）。</param>
    public async Task<BankIndexRefreshResult> RefreshAsync(
        BankIndexSource source,
        IProgress<BankIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var watch = Stopwatch.StartNew();

        // ① 源变了（换游戏目录 / 换 bank 目录）→ 整库重建；只有目录 mtime 变了 → 只更新签名，
        //    真正的失效交给逐文件比对与行集对账（否则目录动一下就要重解析 1531 个文件）。
        var storedSource = _store.ReadSourceKey();
        if (storedSource is null ||
            !string.Equals(storedSource, source.SourceKey, StringComparison.OrdinalIgnoreCase))
        {
            _store.ClearTables();
        }
        _store.RecordSource(source);

        var disk = await Task.Run(() => EnumerateDiskFiles(source.BankDirectory), cancellationToken)
            .ConfigureAwait(false);
        var cached = _store.ReadEntries().ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);

        var changed = new List<BankFileObservation>();
        foreach (var observation in disk)
        {
            if (!cached.TryGetValue(observation.Path, out var entry) ||
                entry.SizeBytes != observation.SizeBytes ||
                entry.MTimeUtcTicks != observation.MTimeUtcTicks)
            {
                changed.Add(observation);
            }
        }
        var diskPaths = disk.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = cached.Keys.Where(x => !diskPaths.Contains(x)).ToList();

        var elapsedBeforeParse = watch.Elapsed;
        var entries = new List<BankIndexEntry>(changed.Count);
        var processed = 0;
        if (changed.Count > 0)
        {
            using var gate = new SemaphoreSlim(_maxDegreeOfParallelism);
            var results = await Task.WhenAll(changed.Select(async observation =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var entry = await ParseBankFileAsync(observation, cancellationToken).ConfigureAwait(false);
                    var done = Interlocked.Increment(ref processed);
                    // 逐个上报会淹没 UI 线程（1531 次）；节流到每 16 个 + 最后一个。
                    if (done % 16 == 0 || done == changed.Count)
                        progress?.Report(new BankIndexProgress(done, changed.Count, entry.FileName, watch.Elapsed));
                    return entry;
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
            entries.AddRange(results);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (entries.Count > 0) _store.PersistEntries(entries);
        if (removed.Count > 0) _store.RemoveEntries(removed);
        // 解析期间目录可能又动过（新增/删除文件）：重记一次签名，避免下次白跑一轮全量比对。
        _store.RecordSource(BankIndexSource.Describe(source.BankDirectory));

        watch.Stop();
        var sampleCount = entries.Sum(x => x.Samples.Count);
        return new BankIndexRefreshResult(
            source.BankDirectory,
            disk.Count,
            changed.Count,
            disk.Count - changed.Count,
            removed.Count,
            watch.Elapsed,
            sampleCount);
    }

    /// <summary>
    /// 枚举目录下的全部 <c>*.bank</c>（含 <c>&lt;id&gt;.assets.bank</c>），只读元数据
    /// （大小 + mtime），按文件名排序（与 <c>BankDirectoryService.ScanDirectory</c> 同序）。
    /// </summary>
    public static IReadOnlyList<BankFileObservation> EnumerateDiskFiles(string bankDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankDirectory);
        if (!Directory.Exists(bankDirectory))
            throw new DirectoryNotFoundException($"bank 目录不存在：{bankDirectory}");
        var list = new List<BankFileObservation>();
        foreach (var path in Directory.EnumerateFiles(bankDirectory, "*.bank", SearchOption.TopDirectoryOnly)
                     .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new FileInfo(path);
                list.Add(new BankFileObservation(path, info.Name, info.Length, info.LastWriteTimeUtc.Ticks));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件读不到元数据：跳过（与 ScanDirectory 的容错同策略），不中断整个枚举。
            }
        }
        return list;
    }

    /// <summary>按磁盘观测值解析一个 bank（元数据已经在手，不重复读文件系统）。</summary>
    public Task<BankIndexEntry> ParseBankFileAsync(BankFileObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var entry = BuildEntry(observation, cancellationToken);
        return Task.FromResult(entry);
    }

    /// <summary>解析一个 bank 文件路径（先读元数据；不存在时抛中文异常）。</summary>
    public Task<BankIndexEntry> ParseBankFileAsync(string bankPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankPath);
        var info = new FileInfo(bankPath);
        if (!info.Exists) throw new FileNotFoundException("bank 文件不存在。", bankPath);
        return ParseBankFileAsync(new BankFileObservation(info.FullName, info.Name, info.Length, info.LastWriteTimeUtc.Ticks), cancellationToken);
    }

    // ── 单文件解析（与「无缓存」路径共用同一实现）────────────────────

    /// <summary>
    /// 解析一个 bank 的文件级事实 + 样本行。<b>路径选择</b>：
    /// 先试 <see cref="BankDirectoryService.ReadSampleTable"/>（音频 bank 一次拿到全部样本，
    /// 事件 bank 拿到 RIFF 块清单）；抛异常（加密 / 无法识别）则回退
    /// <see cref="BankDirectoryService.ScanDirectory"/> 同款的类型判定——
    /// 因此结果与「无缓存」路径逐字段相同，不引入第二套判定。
    /// </summary>
    public BankIndexEntry BuildEntry(BankFileObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        var scanned = DateTimeOffset.UtcNow.UtcTicks;
        try
        {
            var table = _banks.ReadSampleTable(observation.Path);
            var samples = new List<BankSampleRecord>();
            var note = table.Note;
            foreach (var fsbTable in table.FsbTables)
            {
                if (fsbTable.Fsb is null)
                {
                    // 单个 FSB 结构坏：不影响该 bank 的其它 FSB，原因记进 note（不猜内容）。
                    note = string.IsNullOrWhiteSpace(note)
                        ? $"FSB {fsbTable.FsbIndex} 无法解析：{fsbTable.UnresolvedReason}"
                        : $"{note}；FSB {fsbTable.FsbIndex} 无法解析：{fsbTable.UnresolvedReason}";
                    continue;
                }
                foreach (var sample in fsbTable.Fsb.Samples)
                {
                    samples.Add(new BankSampleRecord(
                        observation.Path,
                        fsbTable.FsbIndex,
                        sample.Index,
                        observation.FileName,
                        string.IsNullOrWhiteSpace(sample.Name)
                            ? $"FSB {fsbTable.FsbIndex} 样本 {sample.Index}"
                            : sample.Name!,
                        fsbTable.Fsb.CodecName,
                        sample.SampleRate,
                        sample.Channels,
                        sample.SampleCount,
                        sample.DataSize ?? 0,
                        sample.DataOffset));
                }
            }
            return new BankIndexEntry(
                observation.Path,
                observation.FileName,
                observation.SizeBytes,
                observation.MTimeUtcTicks,
                table.Kind,
                null,
                table.Kind == BankKind.Event ? 0 : table.FsbTables.Count,
                note,
                samples,
                scanned);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // 加密 / 无法识别 / 读取失败：回退到「扫描判定」口径拿类型与 FSB 数，样本为空。
            var fallback = FindScanEntry(observation.Path);
            return new BankIndexEntry(
                observation.Path,
                observation.FileName,
                observation.SizeBytes,
                observation.MTimeUtcTicks,
                fallback.Kind,
                fallback.Detail ?? ex.Message,
                fallback.FsbCount,
                ex.Message,
                [],
                scanned);
        }
    }

    /// <summary>用 <c>BankDirectoryService</c> 的<b>单文件</b>判定口径拿类型与 FSB 数
    /// （与 <c>ScanDirectory</c> 逐行同源：后者内部就是逐文件调用 <c>ScanFile</c>）。</summary>
    private BankFileEntry FindScanEntry(string bankPath) => _banks.ScanFile(bankPath);

    /// <summary>读缓存快照用的内部载体。</summary>
    /// <param name="Snapshot">库里的 bank 条目与样本。</param>
    /// <param name="IsUsable">库存在、源一致且非空（可直接用）。</param>
    /// <param name="DatabaseExists">库文件是否存在。</param>
    /// <param name="SameSource">库里记录的源是不是当前源。</param>
    public sealed record BankIndexRefreshSnapshot(
        BankIndexSnapshot Snapshot,
        bool IsUsable,
        bool DatabaseExists,
        bool SameSource);
}
