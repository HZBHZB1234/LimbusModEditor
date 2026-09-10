using System.Diagnostics;
using System.Globalization;
using LimbusModEditor.Application.Caching;
using LimbusModEditor.Domain.Assets;
using Microsoft.Data.Sqlite;

namespace LimbusModEditor.Application.Assets;

/// <summary>
/// plan-11：<c>cache/bank-index.db</c> 的读写（bank 头信息 + 逐样本行）。
///
/// <para><b>它只是加速旁路，语义与现状完全一致</b>：库里存的全部是「读一次 bank 文件
/// 就能重新算出来」的原版事实——<c>banks</c> 存类型判定与 FSB 数，<c>samples</c> 存每个样本的
/// 名称 / codec / 采样率 / 声道 / 帧数 / 字节数 / 偏移。把库删掉功能完全不受影响，
/// 只是每次进页面要重新解析 1531 个 bank 文件
/// （真实门控测试 <c>RealBankIndexSmokeTests</c> 用「有缓存 vs 删库」逐字段比对证明这点）。</para>
///
/// <para><b>失效规则（两条，无例外）</b>：</para>
/// <list type="number">
/// <item><b>逐文件</b>：<c>banks</c> 行的 <c>(size_bytes, mtime_ticks)</c> 与磁盘不符才重新解析
/// 该文件，并<b>只删该 bank 的 <c>samples</c> 行</b>（不做全库重建）。</item>
/// <item><b>行集对账</b>：磁盘上已不存在的 bank 直接从两表删除（不依赖目录 mtime 是否可靠）。</item>
/// </list>
///
/// <para>编辑集（样本替换登记）不进这份缓存：它属于项目数据（<c>AssetRecord.Metadata</c>），
/// 缓存只存游戏原版事实。</para>
/// </summary>
public sealed class BankIndexStore
{
    /// <summary><c>banks</c> 表名（与 <see cref="WorkbenchCacheSchema.BankIndexSql"/> 一致）。</summary>
    public const string BanksTable = "banks";

    /// <summary><c>samples</c> 表名。</summary>
    public const string SamplesTable = "samples";

    private readonly SqliteTableCache _cache;

    /// <param name="cacheDirectory">缓存目录（<c>AppEnvironment.CacheDirectory</c>，即
    /// <c>&lt;程序目录&gt;/cache</c>）。</param>
    public BankIndexStore(string cacheDirectory)
        : this(SqliteTableCache.Create(WorkbenchCacheKind.BankIndex, cacheDirectory))
    {
    }

    /// <summary>直接注入底座（单测用临时目录建库）。</summary>
    public BankIndexStore(SqliteTableCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    /// <summary>库文件路径（诊断/测试用）。</summary>
    public string DatabasePath => _cache.DbFile;

    /// <summary>库文件是否存在（不存在 = 首次使用，需要冷建索引）。</summary>
    public bool Exists => _cache.Exists;

    /// <summary>本实例（或底层库）是否因损坏而执行过删库重建。</summary>
    public bool WasRecreated => _cache.WasRecreated;

    // ── 源签名 ───────────────────────────────────────────────────────

    /// <summary>库里记录的是不是这个源（换游戏目录 / 换 bank 目录即整体失效）。</summary>
    public bool MatchesSource(BankIndexSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _cache.EnsureSchema();
        return _cache.MatchesSource(source.SourceKey, source.DirectorySignature);
    }

    /// <summary>
    /// 记录当前源与目录签名。<b>不清空业务表</b>：目录 mtime 变只说明「目录动过」，
    /// 逐文件签名比对与行集对账才是真正的失效判据（否则每次目录动一下就要重解析 1531 个文件）。
    /// </summary>
    public void RecordSource(BankIndexSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _cache.EnsureSchema();
        _cache.WriteSourceSignature(source.SourceKey, source.DirectorySignature);
    }

    /// <summary>库里记录的源路径（规范化后的 bank 目录）；没有记录返回 null。</summary>
    public string? ReadSourceKey()
    {
        _cache.EnsureSchema(); // 首次使用（库还不存在）时先建表，否则 index_meta 不存在
        return _cache.Read(static connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT source_key FROM index_meta LIMIT 1";
            var value = command.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToString(value);
        });
    }

    // ── 读 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 读全量快照（bank 条目及其样本，按文件名排序）。
    /// <paramref name="directoryOverride"/> 用于「库里的相对信息 + 当前目录」场景；
    /// 缺省用索引记录的源路径。库不存在或为空时返回空快照（调用方走冷建索引）。
    /// </summary>
    public BankIndexSnapshot ReadSnapshot(string? directoryOverride = null)
    {
        var watch = Stopwatch.StartNew();
        var directory = directoryOverride ?? ReadSourceKey() ?? string.Empty;
        var entries = ReadEntries();
        watch.Stop();
        return new BankIndexSnapshot(directory, entries, watch.Elapsed);
    }

    /// <summary>读全部 bank 条目（含各自样本行），按文件名排序。</summary>
    public IReadOnlyList<BankIndexEntry> ReadEntries()
    {
        _cache.EnsureSchema();
        var samples = ReadSamplesGrouped();
        return _cache.Read(connection =>
        {
            var rows = new List<BankIndexEntry>();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT path, name, size_bytes, mtime_ticks, kind, kind_note, fsb_count, note, scanned_ticks
                FROM banks ORDER BY name
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                rows.Add(new BankIndexEntry(
                    path,
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    ParseKind(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    samples.TryGetValue(path, out var found) ? found : [],
                    reader.GetInt64(8)));
            }
            return (IReadOnlyList<BankIndexEntry>)rows;
        });
    }

    /// <summary>读某个 bank 的样本行（按 FSB 序号 + 样本序号排序）。</summary>
    public IReadOnlyList<BankSampleRecord> ReadSamples(string bankPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankPath);
        _cache.EnsureSchema();
        return _cache.Read(connection =>
        {
            var rows = new List<BankSampleRecord>();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT bank_path, fsb_index, sample_index, name, codec_name, sample_rate,
                       channels, sample_count, data_size, data_offset
                FROM samples WHERE bank_path = $path ORDER BY fsb_index, sample_index
                """;
            command.Parameters.AddWithValue("$path", bankPath);
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(ReadSampleRow(reader));
            return (IReadOnlyList<BankSampleRecord>)rows;
        });
    }

    /// <summary>库里的 bank 条目数（诊断/测试用）。</summary>
    public int ReadBankCount()
    {
        _cache.EnsureSchema();
        return _cache.Read(static connection => Count(connection, BanksTable));
    }

    /// <summary>库里的样本行数（诊断/测试用）。</summary>
    public int ReadSampleCount()
    {
        _cache.EnsureSchema();
        return _cache.Read(static connection => Count(connection, SamplesTable));
    }

    /// <summary>库里的全部样本行（诊断/性能证据用；大表不建议给 UI 直接绑定）。</summary>
    public IReadOnlyList<BankSampleRecord> ReadAllSamples()
    {
        _cache.EnsureSchema();
        return _cache.Read(connection =>
        {
            var rows = new List<BankSampleRecord>();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT bank_path, fsb_index, sample_index, name, codec_name, sample_rate,
                       channels, sample_count, data_size, data_offset
                FROM samples ORDER BY bank_path, fsb_index, sample_index
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(ReadSampleRow(reader));
            return (IReadOnlyList<BankSampleRecord>)rows;
        });
    }

    // ── 写 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 单事务 upsert 一批 bank 条目及其样本：<b>先删该 bank 的旧样本行</b>（避免样本数变少时
    /// 残留旧行），再写 banks 行，再写 samples 行。一个事务提交，中途失败整体回滚
    /// （旧库内容保持可用）。
    /// </summary>
    public void PersistEntries(IEnumerable<BankIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries as IReadOnlyList<BankIndexEntry> ?? entries.ToList();
        if (list.Count == 0) return;
        _cache.EnsureSchema();
        _cache.Write((connection, transaction) =>
        {
            ApplyWritePragmas(connection, transaction);
            using var deleteSamples = Command(connection, transaction, $"DELETE FROM {SamplesTable} WHERE bank_path = $path");
            var deleteParameter = deleteSamples.Parameters.Add("$path", SqliteType.Text);

            using var upsertBank = Command(connection, transaction, $"""
                INSERT INTO {BanksTable} (path, name, size_bytes, mtime_ticks, kind, kind_note, fsb_count, note, scanned_ticks)
                VALUES ($path, $name, $size, $ticks, $kind, $kindNote, $fsb, $note, $scanned)
                ON CONFLICT(path) DO UPDATE SET
                    name = $name, size_bytes = $size, mtime_ticks = $ticks, kind = $kind,
                    kind_note = $kindNote, fsb_count = $fsb, note = $note, scanned_ticks = $scanned
                """);
            var bankPath = upsertBank.Parameters.Add("$path", SqliteType.Text);
            var bankName = upsertBank.Parameters.Add("$name", SqliteType.Text);
            var bankSize = upsertBank.Parameters.Add("$size", SqliteType.Integer);
            var bankTicks = upsertBank.Parameters.Add("$ticks", SqliteType.Integer);
            var bankKind = upsertBank.Parameters.Add("$kind", SqliteType.Text);
            var bankKindNote = upsertBank.Parameters.Add("$kindNote", SqliteType.Text);
            var bankFsb = upsertBank.Parameters.Add("$fsb", SqliteType.Integer);
            var bankNote = upsertBank.Parameters.Add("$note", SqliteType.Text);
            var bankScanned = upsertBank.Parameters.Add("$scanned", SqliteType.Integer);

            using var insertSample = Command(connection, transaction, $"""
                INSERT INTO {SamplesTable}
                    (bank_path, fsb_index, sample_index, name, codec_name, sample_rate,
                     channels, sample_count, data_size, data_offset)
                VALUES ($path, $fsb, $sample, $name, $codec, $rate, $channels, $count, $size, $offset)
                """);
            var samplePath = insertSample.Parameters.Add("$path", SqliteType.Text);
            var sampleFsb = insertSample.Parameters.Add("$fsb", SqliteType.Integer);
            var sampleIndex = insertSample.Parameters.Add("$sample", SqliteType.Integer);
            var sampleName = insertSample.Parameters.Add("$name", SqliteType.Text);
            var sampleCodec = insertSample.Parameters.Add("$codec", SqliteType.Text);
            var sampleRate = insertSample.Parameters.Add("$rate", SqliteType.Integer);
            var sampleChannels = insertSample.Parameters.Add("$channels", SqliteType.Integer);
            var sampleCount = insertSample.Parameters.Add("$count", SqliteType.Integer);
            var sampleSize = insertSample.Parameters.Add("$size", SqliteType.Integer);
            var sampleOffset = insertSample.Parameters.Add("$offset", SqliteType.Integer);

            foreach (var entry in list)
            {
                var scanned = entry.ScannedTicks != 0 ? entry.ScannedTicks : DateTimeOffset.UtcNow.UtcTicks;
                deleteParameter.Value = entry.Path;
                deleteSamples.ExecuteNonQuery();

                bankPath.Value = entry.Path;
                bankName.Value = entry.FileName;
                bankSize.Value = entry.SizeBytes;
                bankTicks.Value = entry.MTimeUtcTicks;
                bankKind.Value = entry.Kind.ToString();
                bankKindNote.Value = (object?)entry.KindNote ?? DBNull.Value;
                bankFsb.Value = entry.FsbCount;
                bankNote.Value = (object?)entry.Note ?? DBNull.Value;
                bankScanned.Value = scanned;
                upsertBank.ExecuteNonQuery();

                foreach (var sample in entry.Samples)
                {
                    samplePath.Value = entry.Path;
                    sampleFsb.Value = sample.FsbIndex;
                    sampleIndex.Value = sample.SampleIndex;
                    sampleName.Value = sample.Name;
                    sampleCodec.Value = sample.CodecName;
                    sampleRate.Value = sample.SampleRate;
                    sampleChannels.Value = sample.Channels;
                    sampleCount.Value = sample.SampleCount;
                    sampleSize.Value = sample.DataSize;
                    sampleOffset.Value = sample.DataOffset;
                    insertSample.ExecuteNonQuery();
                }
            }
        });
    }

    /// <summary>从两表删除一批 bank（磁盘上已不存在的条目）。</summary>
    public void RemoveEntries(IEnumerable<string> bankPaths)
    {
        ArgumentNullException.ThrowIfNull(bankPaths);
        var list = bankPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return;
        _cache.EnsureSchema();
        _cache.Write((connection, transaction) =>
        {
            ApplyWritePragmas(connection, transaction);
            using var deleteSamples = Command(connection, transaction, $"DELETE FROM {SamplesTable} WHERE bank_path = $path");
            using var deleteBank = Command(connection, transaction, $"DELETE FROM {BanksTable} WHERE path = $path");
            var sampleParameter = deleteSamples.Parameters.Add("$path", SqliteType.Text);
            var bankParameter = deleteBank.Parameters.Add("$path", SqliteType.Text);
            foreach (var path in list)
            {
                sampleParameter.Value = path;
                deleteSamples.ExecuteNonQuery();
                bankParameter.Value = path;
                deleteBank.ExecuteNonQuery();
            }
        });
    }

    /// <summary>整库删除（含边车文件）；下次使用会自动重建。「清空缓存」入口用。</summary>
    public void DeleteDatabase() => _cache.DeleteDatabase();

    /// <summary>清空业务表但保留 index_meta（诊断用）。</summary>
    public void ClearTables() => _cache.ClearTables();

    // ── 内部工具 ─────────────────────────────────────────────────────

    /// <summary>批量写前的连接级调优（样本表插入是万行级，机械盘友好）：
    /// <c>temp_store=MEMORY</c> 让排序/临时表不进磁盘，<c>cache_size=-8192</c> 给 8MB 页缓存。
    /// <c>synchronous=NORMAL</c> 由 <see cref="SqliteTableCache.Write"/> 自己设，不重复。</summary>
    private static void ApplyWritePragmas(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var pragma = Command(connection, transaction, "PRAGMA temp_store=MEMORY; PRAGMA cache_size=-8192;");
        pragma.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static int Count(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table}";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private Dictionary<string, List<BankSampleRecord>> ReadSamplesGrouped()
        => _cache.Read(static connection =>
        {
            var grouped = new Dictionary<string, List<BankSampleRecord>>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT bank_path, fsb_index, sample_index, name, codec_name, sample_rate,
                       channels, sample_count, data_size, data_offset
                FROM samples ORDER BY bank_path, fsb_index, sample_index
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var record = ReadSampleRow(reader);
                if (!grouped.TryGetValue(record.BankPath, out var list))
                {
                    list = [];
                    grouped[record.BankPath] = list;
                }
                list.Add(record);
            }
            return grouped;
        });

    private static BankSampleRecord ReadSampleRow(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetInt32(1),
        reader.GetInt32(2),
        Path.GetFileName(reader.GetString(0)),
        reader.GetString(3),
        reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        (uint)reader.GetInt64(7),
        reader.GetInt64(8),
        reader.GetInt64(9));

    /// <summary>类型文本 → 枚举（旧库 / 被手改的库按「无法识别」处理，不抛）。</summary>
    private static BankKind ParseKind(string text)
        => Enum.TryParse<BankKind>(text, ignoreCase: true, out var kind) ? kind : BankKind.Unknown;
}
