using System.Diagnostics;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using Microsoft.Data.Sqlite;
using NLog;

namespace LimbusModEditor.Application.Scanning;

public sealed record UnityCacheIndexRow(
    int BundleIndex, string Container, long PathId, int TypeId, AssetType Type, long Size, string? Baseline,
    string? ContainerEntry = null);

public sealed record UnityCacheIndexBundle(
    string DataPath, long Size, long MTimeUtcTicks, string Outer, string Inner, bool StaticBundle = false);

/// <summary>
/// v2 可重建索引：路径和共享字符串只存一次，对象表按整数 bundle/index 聚簇。
/// 同一快照内流式读取，仅保留共享字典和当前 bundle；更新与淘汰在单个事务内完成。
/// 版本不符时清空缓存，由扫描器从只读游戏源重建。
///
/// <para><b>派生层</b>（<c>catalog_rank</c> / <c>asset_fts</c> / <c>derived_state</c>）
/// 是 v2 之上的**纯增量扩容**：只用 <c>CREATE ... IF NOT EXISTS</c> 添加，
/// 不动 <see cref="SchemaVersion"/> —— 后者不符是 DROP 重建（= 逼用户重扫 1471 个
/// bundle），而派生层随时可以花几十秒重建。见 <see cref="EnsureDerived"/>。</para>
/// </summary>
public sealed class UnityCacheSqliteIndexStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    public const int SchemaVersion = 2;

    /// <summary>派生层（分页名次 + 检索索引）的实现修订号。**改了算法或口径就 +1**：
    /// 下次打开时会发现状态不符，在后台重建一次；不改 <see cref="SchemaVersion"/>，
    /// 所以不会导致整库重扫。</summary>
    public const string DerivedSchemaRevision = "d1";

    private readonly string _connectionString;
    private readonly string _dbFile;
    private readonly object _schemaLock = new();
    private bool _schemaReady;

    public UnityCacheSqliteIndexStore(string dbFile)
    {
        _dbFile = Path.GetFullPath(dbFile);
        Directory.CreateDirectory(Path.GetDirectoryName(_dbFile)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbFile, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true
        }.ToString();
    }

    public bool Exists => File.Exists(_dbFile);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private SqliteConnection OpenEnsured()
    {
        EnsureSchema();
        return Open();
    }

    public void EnsureSchema()
    {
        lock (_schemaLock)
        {
            if (_schemaReady) return;
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA user_version;";
            var reset = Convert.ToInt32(command.ExecuteScalar()) != SchemaVersion;
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('bundles','assets','strings')";
            reset |= Convert.ToInt32(command.ExecuteScalar()) != 3;
            using (var transaction = connection.BeginTransaction())
            {
                command.Transaction = transaction;
                if (reset)
                {
                    // asset_fts 是虚表：只 DROP 自己，影子表由 SQLite 一并删掉。
                    command.CommandText = """
                        DROP TABLE IF EXISTS assets; DROP TABLE IF EXISTS bundles; DROP TABLE IF EXISTS strings;
                        DROP TABLE IF EXISTS catalog_rank; DROP TABLE IF EXISTS asset_fts;
                        DROP TABLE IF EXISTS derived_state;
                        """;
                    command.ExecuteNonQuery();
                }
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS bundles (
                        id INTEGER PRIMARY KEY,
                        data_path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                        size INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL,
                        outer_key TEXT NOT NULL, inner_key TEXT NOT NULL,
                        static_bundle INTEGER NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS strings (
                        id INTEGER PRIMARY KEY, value TEXT NOT NULL UNIQUE
                    );
                    CREATE TABLE IF NOT EXISTS assets (
                        bundle_id INTEGER NOT NULL REFERENCES bundles(id) ON DELETE CASCADE,
                        bundle_index INTEGER NOT NULL,
                        container_id INTEGER NOT NULL REFERENCES strings(id),
                        path_id INTEGER NOT NULL, type_id INTEGER NOT NULL,
                        type INTEGER NOT NULL, size INTEGER NOT NULL,
                        baseline_id INTEGER REFERENCES strings(id), container_entry TEXT,
                        PRIMARY KEY(bundle_id, bundle_index)
                    ) WITHOUT ROWID;
                    CREATE INDEX IF NOT EXISTS ix_assets_named ON assets(container_entry, type, size)
                        WHERE container_entry IS NOT NULL AND container_entry <> '';
                    -- ── 派生层（分页名次 + 子串检索）──────────────────────────
                    -- catalog_rank：目录全序的稠密名次 r → (bundle_id,bundle_index)。
                    --   r 以主键形态存在，所以「第 k 页」= 一次主键区间扫（实测 1.4 ms/页，
                    --   与页码深度无关），既不用 OFFSET 也不用把自定义自然序注册成
                    --   SQLite collation（编码成 BINARY 可比的排序键实测 43.9% 顺序不一致）。
                    CREATE TABLE IF NOT EXISTS catalog_rank (
                        r INTEGER PRIMARY KEY,
                        bundle_id INTEGER NOT NULL, bundle_index INTEGER NOT NULL
                    ) WITHOUT ROWID;
                    -- asset_fts：**只索引显示路径 dp**。实测 lp 是
                    --   「外层哈希/内层哈希/CAB-哈希/pathId.typeId」，除编号外全是机器哈希，
                    --   没有用户会手打的文本；真正可搜的内容（Assets/Animation/SD/…）全在 dp。
                    --   content='' 是 contentless（不存原文副本，体积最小）；detail=none
                    --   只存词项不存位置 —— 因此**不支持裸短语查询**，只能用「手工 AND 词项」
                    --   形态，得到的是超集候选，由调用方复核（见 SearchRanks）。
                    --   rowid = 名次 r：检索命中直接就是名次，排序/分页零转换。
                    CREATE VIRTUAL TABLE IF NOT EXISTS asset_fts USING fts5(
                        dp, content='', detail=none, columnsize=0, tokenize='trigram'
                    );
                    CREATE TABLE IF NOT EXISTS derived_state (k TEXT PRIMARY KEY, v TEXT NOT NULL);
                    PRAGMA user_version=2;
                    """;
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            // 只在升级时回收旧库的空页；正常扫描不 VACUUM。
            if (reset)
            {
                command.Transaction = null;
                command.CommandText = "VACUUM;";
                command.ExecuteNonQuery();
                Log.Info("资源索引已初始化为 v{0}：{1}（旧缓存需重建）", SchemaVersion, _dbFile);
            }
            _schemaReady = true;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction,
        string sql, params string[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var name in parameters) command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        command.Prepare();
        return command;
    }

    public void PersistAll(IReadOnlyList<UnityCacheScanEntry> currentEntries,
        IEnumerable<(UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow> Rows)> changedBundles,
        CancellationToken cancellationToken = default)
    {
        using var connection = OpenEnsured();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA synchronous=NORMAL";
            pragma.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();
        var keep = currentEntries.Select(x => x.DataPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bundles = ReadBundles(connection, transaction);
        using var delete = Command(connection, transaction, "DELETE FROM bundles WHERE id=$id", "$id");
        foreach (var (id, bundle) in bundles)
        {
            if (keep.Contains(bundle.DataPath)) continue;
            delete.Parameters[0].Value = id;
            delete.ExecuteNonQuery();
        }
        var strings = ReadStrings(connection, transaction).ToDictionary(x => x.Value, x => x.Key, StringComparer.Ordinal);
        using var addString = Command(connection, transaction,
            "INSERT INTO strings(value) VALUES($v) RETURNING id", "$v");
        long Intern(string value)
        {
            if (strings.TryGetValue(value, out var id)) return id;
            addString.Parameters[0].Value = value;
            id = (long)addString.ExecuteScalar()!;
            strings.Add(value, id);
            return id;
        }
        using var upsert = Command(connection, transaction, """
            INSERT INTO bundles(data_path,size,mtime_ticks,outer_key,inner_key,static_bundle)
            VALUES($p,$s,$m,$o,$i,$sb)
            ON CONFLICT(data_path) DO UPDATE SET size=$s,mtime_ticks=$m,outer_key=$o,inner_key=$i,static_bundle=$sb
            RETURNING id
            """, "$p", "$s", "$m", "$o", "$i", "$sb");
        using var clear = Command(connection, transaction, "DELETE FROM assets WHERE bundle_id=$id", "$id");
        using var insert = Command(connection, transaction, """
            INSERT INTO assets(bundle_id,bundle_index,container_id,path_id,type_id,type,size,baseline_id,container_entry)
            VALUES($b,$i,$c,$p,$tid,$t,$s,$bl,$ce)
            """, "$b", "$i", "$c", "$p", "$tid", "$t", "$s", "$bl", "$ce");
        var written = 0;
        foreach (var (bundle, rows) in changedBundles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!keep.Contains(bundle.DataPath))
                throw new InvalidDataException($"变化的 bundle 不在本次枚举结果中：{bundle.DataPath}");
            upsert.Parameters[0].Value = bundle.DataPath;
            upsert.Parameters[1].Value = bundle.Size;
            upsert.Parameters[2].Value = bundle.MTimeUtcTicks;
            upsert.Parameters[3].Value = bundle.Outer;
            upsert.Parameters[4].Value = bundle.Inner;
            upsert.Parameters[5].Value = bundle.StaticBundle ? 1 : 0;
            var bundleId = (long)upsert.ExecuteScalar()!;
            clear.Parameters[0].Value = bundleId;
            clear.ExecuteNonQuery();
            var rowNumber = 0;
            foreach (var row in rows)
            {
                if (rowNumber++ % 1024 == 0) cancellationToken.ThrowIfCancellationRequested();
                insert.Parameters[0].Value = bundleId;
                insert.Parameters[1].Value = row.BundleIndex;
                insert.Parameters[2].Value = Intern(row.Container);
                insert.Parameters[3].Value = row.PathId;
                insert.Parameters[4].Value = row.TypeId;
                insert.Parameters[5].Value = (int)row.Type;
                insert.Parameters[6].Value = row.Size;
                insert.Parameters[7].Value = row.Baseline is null ? DBNull.Value : Intern(row.Baseline);
                insert.Parameters[8].Value = (object?)row.ContainerEntry ?? DBNull.Value;
                insert.ExecuteNonQuery();
            }
            written++;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (written > 0)
        {
            // 行集变了 → 目录名次与检索索引同时失效：名次是**整表全序**，
            // 任何一支 bundle 的增删都会挪动它后面的名次；检索索引以名次为 rowid，
            // 所以两者必须一起重建。这里只把状态清掉（同一事务，回滚也一致），
            // 真正的重建交给 EnsureDerived —— 后台线程、带进度、可取消。
            using var stale = Command(connection, transaction, "DELETE FROM derived_state");
            stale.ExecuteNonQuery();
        }
        transaction.Commit();
        Log.Debug("资源索引事务完成：枚举 {0} 个 bundle，重写 {1} 个", currentEntries.Count, written);
    }

    private static List<(long Id, UnityCacheIndexBundle Bundle)> ReadBundles(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction,
            "SELECT id,data_path,size,mtime_ticks,outer_key,inner_key,static_bundle FROM bundles ORDER BY id");
        using var reader = command.ExecuteReader();
        var bundles = new List<(long, UnityCacheIndexBundle)>();
        while (reader.Read())
            bundles.Add((reader.GetInt64(0), new(reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetString(4), reader.GetString(5), reader.GetBoolean(6))));
        return bundles;
    }

    private static Dictionary<long, string> ReadStrings(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, "SELECT id,value FROM strings");
        using var reader = command.ExecuteReader();
        var strings = new Dictionary<long, string>();
        while (reader.Read()) strings.Add(reader.GetInt64(0), reader.GetString(1));
        return strings;
    }

    public Dictionary<string, UnityCacheIndexBundle> ReadBundleIndex(StringComparer comparer)
    {
        using var connection = OpenEnsured();
        using var transaction = connection.BeginTransaction(deferred: true);
        return ReadBundles(connection, transaction).ToDictionary(x => x.Bundle.DataPath, x => x.Bundle, comparer);
    }

    /// <summary>按聚簇主键扫描，无额外排序和整库聚合。可仅实例化指定 bundle。</summary>
    public IEnumerable<(UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow> Rows)> ReadAll(
        IReadOnlySet<string>? dataPaths = null, CancellationToken cancellationToken = default)
    {
        using var connection = OpenEnsured();
        using var transaction = connection.BeginTransaction(deferred: true);
        var bundles = ReadBundles(connection, transaction);
        if (dataPaths is not null) bundles.RemoveAll(x => !dataPaths.Contains(x.Bundle.DataPath));
        if (bundles.Count == 0) yield break;
        var strings = ReadStrings(connection, transaction);
        // IN 仅包含数据库读出的整数主键；SQLite 按主键查选中范围，不读取其它 bundle。
        var filter = dataPaths is null ? string.Empty : " WHERE bundle_id IN (" +
            string.Join(",", bundles.Select(x => x.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))) + ")";
        using var command = Command(connection, transaction, """
            SELECT bundle_id,bundle_index,container_id,path_id,type_id,type,size,baseline_id,container_entry
            FROM assets
            """ + filter + " ORDER BY bundle_id,bundle_index");
        using var reader = command.ExecuteReader();
        var hasRow = reader.Read();
        foreach (var (id, bundle) in bundles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = new List<UnityCacheIndexRow>();
            while (hasRow && reader.GetInt64(0) == id)
            {
                if (rows.Count % 1024 == 0) cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new(reader.GetInt32(1), strings[reader.GetInt64(2)], reader.GetInt64(3),
                        reader.GetInt32(4), (AssetType)reader.GetInt32(5), reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : strings[reader.GetInt64(7)], reader.IsDBNull(8) ? null : reader.GetString(8)));
                hasRow = reader.Read();
            }
            yield return (bundle, rows);
        }
    }

    public sealed record UnityCacheContainerRow(string ContainerEntry, AssetType Type, long Size);

    public IReadOnlyList<UnityCacheContainerRow> ReadContainerRows()
    {
        using var connection = OpenEnsured();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT container_entry,type,size FROM assets
            WHERE container_entry IS NOT NULL AND container_entry <> ''
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<UnityCacheContainerRow>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), (AssetType)reader.GetInt32(1), reader.GetInt64(2)));
        return rows;
    }

    // ── 派生层：目录名次（分页）与子串检索索引 ────────────────────────────
    //
    // 目标：资源列表按「第 k 页」查询，而不是把 127 万条 AssetRecord 全物化
    // （实测 1,028 字节/条 → 1,250.9 MiB 托管堆，一次强制 gen2 全回收 872 ms）。
    //
    // 为什么名次放**单独一张表**、而不是给 assets 加一列 r：
    //   1,275,623 行逐条 `UPDATE assets SET r=?` 实测 43–50 µs/行 → 55–65 s；
    //   而 (r 主键, bundle_id, bundle_index) 的表按 r 序批量插入是 4.7 µs/行
    //   → 5.9 s，快一个数量级；assets 表结构因此**完全不用动**（迁移只剩
    //   `CREATE ... IF NOT EXISTS`，零 DROP 风险）。
    //   代价：名次变化会挪动 FTS 的 rowid，所以两者必须一起重建 —— 这是一条
    //   明确的顺序规则（见 PersistAll 里清 derived_state 的失效标记），不是隐藏耦合。
    //
    // 为什么名次不能下推给 SQL 排：AssetDisplay 的自然序（数字段按数值比）在
    // SQLite 里没有对应排序；编码成 BINARY 可比的键后实测 43.9% 的前后顺序不一致。

    /// <summary>bundle_id 与 bundle_index 打包成一个整数，作为**排序载体**在
    /// <c>Array.Sort</c> 里跟着显示路径走。（不是身份键、不落盘为列，只是省一次数组。）
    /// 2^24 上限 = 单个 bundle 最多 1,677 万个对象；真实缓存实测最大 bundle_index 40,587。</summary>
    private const int RidShift = 24;
    private const long RidMask = (1L << RidShift) - 1;

    /// <summary>trigram 索引的词项长度。短于它的查询串在索引里取不到任何词项。</summary>
    private const int TrigramLength = 3;

    /// <summary>复核/按名次取行时单批 IN 的行数（真实数据里候选集是数百到数千级）。</summary>
    private const int RankChunk = 400;

    private static long CatalogKey(long bundleId, int bundleIndex)
        => (bundleId << RidShift) | (uint)bundleIndex;

    private static long BundleIdOf(long key) => key >> RidShift;
    private static int BundleIndexOf(long key) => (int)(key & RidMask);

    /// <summary><c>derived_state</c> 的快照。<paramref name="Rows"/> = 上次成功建成时的行数，
    /// -1 表示没有记录。</summary>
    public sealed record DerivedState(string? Revision, long Rows);

    /// <summary>一次派生层重建的结果（<paramref name="Rebuilt"/> = false 表示本来就不用重建）。</summary>
    public sealed record DerivedBuildResult(bool Rebuilt, long Rows, int Ties, TimeSpan Elapsed);

    /// <summary>按名次读回的一行：名次 + 它所属的 bundle + 索引行本身。
    /// 三者齐了才能重建出与内存路径**同口径**的 AssetRecord（见
    /// <c>UnityCacheScanService.BuildRecord</c>）。</summary>
    public sealed record UnityCachePageRow(int Rank, UnityCacheIndexBundle Bundle, UnityCacheIndexRow Row);

    /// <summary>
    /// 派生层是否需要重建。**只读一行状态、不扫表**，供启动流程判断要不要起后台任务。
    /// </summary>
    public bool NeedsDerived()
    {
        using var connection = OpenEnsured();
        var state = ReadDerivedState(connection);
        return state.Revision != DerivedSchemaRevision || state.Rows != CountAssets(connection);
    }

    /// <summary>目录资源总数（以 assets 为准）。派生层还没建时也给出正确值。</summary>
    public long Count()
    {
        using var connection = OpenEnsured();
        return CountAssets(connection);
    }

    /// <summary>
    /// 重建派生层：读出全表 → 按目录全序算稠密名次 → 一个事务里重写
    /// <c>catalog_rank</c> 与 <c>asset_fts</c> → 记下状态。
    /// <para>已经是最新时**直接返回**（<see cref="DerivedBuildResult.Rebuilt"/> = false），
    /// 所以可以放心地每次扫描后都调一次。真实规模实测：排序 + 写名次 ~6 s、
    /// 灌检索索引 ~28 s（合计三十余秒，只在缓存真的变过之后发生一次）。</para>
    /// </summary>
    public DerivedBuildResult EnsureDerived(
        IProgress<UnityCacheScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var connection = OpenEnsured();
        return EnsureDerivedCore(connection, progress, cancellationToken);
    }

    /// <summary>
    /// 派生层是否已就绪 —— **只读一行状态、不扫表**（区别于
    /// <see cref="NeedsDerived"/> 那种「连行数一起核」的重判定）。
    /// </summary>
    /// <remarks>
    /// 廉价的依据：行集只可能在 <see cref="PersistAll"/> 里变，而它**同一事务**内就删掉了
    /// <c>derived_state</c>。所以「状态行缺失」等价于「行集变过」——查询路径靠这一行
    /// 就能判断该不该自愈，不必每次去数 127 万行。
    /// </remarks>
    public bool IsDerivedReady()
    {
        using var connection = OpenEnsured();
        return ReadDerivedState(connection).Revision == DerivedSchemaRevision;
    }

    /// <summary>
    /// 查询路径的自愈闸门：状态行不对就先补建派生层。
    /// <para>没有它会出现**静默错答**：名次表空着时 <see cref="ReadPage"/> 返回空页、
    /// <see cref="SearchRanks"/> 返回零命中 —— 用户看到的是「资源没了」而不是「还在建」。
    /// 命中这条路径只发生在「上次扫描写完索引就被杀掉」或「上次重建失败」之后，
    /// 代价是那一次查询等三十余秒，之后一直走快路径。</para>
    /// </summary>
    private static void EnsureDerivedForQuery(SqliteConnection connection)
    {
        if (ReadDerivedState(connection).Revision == DerivedSchemaRevision) return;
        Log.Warn("派生层缺失/过期，按需补建后再返回查询结果（本次查询会偏慢）");
        EnsureDerivedCore(connection, null, CancellationToken.None);
    }

    private static DerivedBuildResult EnsureDerivedCore(
        SqliteConnection connection, IProgress<UnityCacheScanProgress>? progress, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var total = CountAssets(connection);
        var state = ReadDerivedState(connection);
        if (state.Revision == DerivedSchemaRevision && state.Rows == total)
            return new(false, total, 0, watch.Elapsed);
        using var scope = Log.Scope("重建资源索引派生层");
        Log.Info("派生层需要重建：库内 {0:N0} 条 · 已记录修订 {1}（当前 {2}）、已记录行数 {3}",
            total, state.Revision ?? "-", DerivedSchemaRevision, state.Rows);

        progress?.Report(new UnityCacheScanProgress(0, 0, string.Empty, 0, 0, Phase: "正在整理资源目录顺序…"));
        var (displayPaths, keys) = ReadCatalogRows(connection, cancellationToken);
        var ties = SortCatalogOrder(connection, displayPaths, keys, cancellationToken);
        Log.Debug("目录序排序完成：{0:N0} 条 · 显示路径打平（靠 LogicalPath 才分先后的）{1:N0} 条", displayPaths.Length, ties);

        progress?.Report(new UnityCacheScanProgress(0, 0, string.Empty, 0, 0,
            Phase: "正在写入资源目录名次与检索索引…"));
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA synchronous=NORMAL";
            pragma.ExecuteNonQuery();
        }
        // 名次与检索索引放在**同一个事务**：检索索引以名次为 rowid，两者不同步就等于
        // 搜到的行指向别的资源。要么都是新的，要么都还是旧的那一份。
        using (var transaction = connection.BeginTransaction())
        {
            WriteDerivedLayer(connection, transaction, displayPaths, keys, cancellationToken);
            transaction.Commit();
        }
        Log.Info("派生层重建完成：{0:N0} 条 · 打平 {1:N0} 条 · 用时 {2:0.0} 秒",
            displayPaths.Length, ties, watch.Elapsed.TotalSeconds);
        return new(true, displayPaths.Length, ties, watch.Elapsed);
    }

    /// <summary>
    /// 按名次区间读一页（<c>r ∈ [from, from+take)</c>）。实测冷缓存下单页 1.4 ms，
    /// **与页码深度无关**（名次是主键，这是一次主键区间扫，不是 OFFSET 翻页 ——
    /// OFFSET 1,000,000 实测 9.9 s）。
    /// </summary>
    public IReadOnlyList<UnityCachePageRow> ReadPage(long from, int take)
    {
        using var connection = OpenEnsured();
        EnsureDerivedForQuery(connection);
        return ReadPageRows(connection, from, take);
    }

    /// <summary>按给定名次取行（返回顺序 = 名次升序）。检索结果复核与「定位到某一页」用。</summary>
    public IReadOnlyList<UnityCachePageRow> ReadByRanks(IReadOnlyList<int> ranks)
    {
        using var connection = OpenEnsured();
        EnsureDerivedForQuery(connection);
        var rows = new List<UnityCachePageRow>(ranks.Count);
        for (var offset = 0; offset < ranks.Count; offset += RankChunk)
        {
            var count = Math.Min(RankChunk, ranks.Count - offset);
            var slice = new int[count];
            for (var i = 0; i < count; i++) slice[i] = ranks[offset + i];
            foreach (var row in ReadPageRowsByRank(connection, slice)) rows.Add(row);
        }
        rows.Sort(static (a, b) => a.Rank.CompareTo(b.Rank));
        return rows;
    }

    /// <summary>
    /// 按**显示路径子串**检索，返回已精确复核的名次（升序）。
    /// <para>① FTS 候选：trigram 把查询串切成 3 字符窗口手工 AND —— <c>detail=none</c>
    /// 下裸短语会直接报错，而 AND 出来的只是**超集**（"abcd" 会命中「含 abc、
    /// 也含 bcd，但两者不相邻」的文本）。</para>
    /// <para>② 复核：把候选行读回来算显示路径，用 <c>OrdinalIgnoreCase</c> 精确判子串。
    /// 复核用的是 <see cref="AssetDisplay.CacheRowDisplayPath"/> —— 与列表显示、
    /// 与派生层回填**同一个函数**，所以「搜到的」严格等于「看到的」。</para>
    /// <para>查询串短于 3 字符时索引里没有任何可用词项，退化为流式全表扫描
    /// （逐行算显示路径，不物化记录；真实规模实测 1.5–3 s）。</para>
    /// </summary>
    public IReadOnlyList<int> SearchRanks(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var needle = text.Trim();
        if (needle.Length == 0) return [];
        using var connection = OpenEnsured();
        EnsureDerivedForQuery(connection);
        var match = needle.Length < TrigramLength ? string.Empty : TrigramAndQuery(needle);
        if (match.Length == 0)
        {
            Log.Debug("检索退化全表扫描：查询串「{0}」（{1} 字符，trigram 取不到词项）", needle, needle.Length);
            return ScanDisplayPaths(connection, needle, cancellationToken);
        }
        var candidates = new List<int>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT rowid FROM asset_fts WHERE asset_fts MATCH $q ORDER BY rowid";
            command.Parameters.AddWithValue("$q", match);
            using var reader = command.ExecuteReader();
            while (reader.Read()) candidates.Add((int)reader.GetInt64(0));
        }
        if (candidates.Count == 0) return [];
        var verified = new List<int>(Math.Min(candidates.Count, 4096));
        for (var offset = 0; offset < candidates.Count; offset += RankChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(RankChunk, candidates.Count - offset);
            var slice = new int[count];
            for (var i = 0; i < count; i++) slice[i] = candidates[offset + i];
            foreach (var (rank, containerEntry, typeId, type, pathId) in ReadDisplayKeysByRank(connection, slice))
            {
                var path = AssetDisplay.CacheRowDisplayPath(typeId, type, pathId, containerEntry);
                if (path.Contains(needle, StringComparison.OrdinalIgnoreCase)) verified.Add(rank);
            }
        }
        verified.Sort();
        Log.Debug("检索「{0}」：候选 {1:N0} → 复核 {2:N0}（词项 {3}）", needle, candidates.Count, verified.Count, match);
        return verified;
    }

    // ── 派生层内部实现 ──────────────────────────────────────────────────

    private static DerivedState ReadDerivedState(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT k, v FROM derived_state";
        string? revision = null;
        var rows = -1L;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(0) == "revision") revision = reader.GetString(1);
            else if (reader.GetString(0) == "rows") long.TryParse(reader.GetString(1), out rows);
        }
        return new(revision, rows);
    }

    private static long CountAssets(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM assets";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>读出全表用于排序的两列：显示路径（算出来的）与打包键。
    /// 一台 127 万行的机器上这是约 130 MiB 的**瞬时**分配 —— 相比「全量常驻
    /// AssetRecord」的 1,250.9 MiB 常驻堆，这是唯一可以接受的一次性代价。</summary>
    private static (string[] DisplayPaths, long[] Keys) ReadCatalogRows(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bundle_id, bundle_index, container_entry, type_id, type, path_id FROM assets";
        using var reader = command.ExecuteReader();
        var displayPaths = new List<string>(4096);
        var keys = new List<long>(4096);
        var seen = 0;
        while (reader.Read())
        {
            if (seen++ % 4096 == 0) cancellationToken.ThrowIfCancellationRequested();
            var containerEntry = reader.IsDBNull(2) ? null : reader.GetString(2);
            displayPaths.Add(AssetDisplay.CacheRowDisplayPath(
                reader.GetInt32(3), (AssetType)reader.GetInt32(4), reader.GetInt64(5), containerEntry));
            keys.Add(CatalogKey(reader.GetInt64(0), reader.GetInt32(1)));
        }
        return (displayPaths.ToArray(), keys.ToArray());
    }

    /// <summary>
    /// 就地排成目录全序，返回「显示路径打平」的行数。
    /// 比较器前半段就是列表默认排序用的 <see cref="AssetDisplay.ComparePaths"/>（零分配）；
    /// 打平的行再用 LogicalPath 的序数不区分大小写兜底 —— 与
    /// <c>AssetSearchService.SortByKeys</c> 的默认分支同序。
    /// </summary>
    private static int SortCatalogOrder(SqliteConnection connection, string[] displayPaths, long[] keys,
        CancellationToken cancellationToken)
    {
        Array.Sort(displayPaths, keys, PathComparer.Instance);
        var tied = 0;
        var n = displayPaths.Length;
        for (var i = 1; i < n; i++)
            if (AssetDisplay.ComparePaths(displayPaths[i - 1], displayPaths[i]) == 0) tied++;
        if (tied == 0) return 0;
        // 打平的行数很少（真实数据里同容器路径重名约 4.8 万条），先一次性把它们的
        // LogicalPath 取回来，再逐段重排 —— 逐段单独查会产生上万次微型查询。
        var tiedKeys = new List<long>(tied + 1);
        for (var i = 1; i < n; i++)
            if (AssetDisplay.ComparePaths(displayPaths[i - 1], displayPaths[i]) == 0)
            {
                if (tiedKeys.Count == 0 || tiedKeys[^1] != keys[i - 1]) tiedKeys.Add(keys[i - 1]);
                tiedKeys.Add(keys[i]);
            }
        var logicalPaths = ReadLogicalPaths(connection, tiedKeys, cancellationToken);
        var comparer = new LogicalPathComparer(logicalPaths);
        var start = 0;
        while (start < n)
        {
            var end = start + 1;
            while (end < n && AssetDisplay.ComparePaths(displayPaths[end - 1], displayPaths[end]) == 0) end++;
            if (end - start > 1) Array.Sort(keys, start, end - start, comparer);
            start = end;
        }
        return tiedKeys.Count;
    }

    /// <summary>取给定打包键（bundle_id/bundle_index）对应的 LogicalPath。
    /// 只在打平的行上调用，所以分块 IN 足够。</summary>
    private static Dictionary<long, string> ReadLogicalPaths(
        SqliteConnection connection, IReadOnlyList<long> keys, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, string>(keys.Count);
        const int batch = 200;
        for (var offset = 0; offset < keys.Count; offset += batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(batch, keys.Count - offset);
            var builder = new System.Text.StringBuilder(count * 12 + 200);
            builder.Append("""
                SELECT a.bundle_id, a.bundle_index, b.outer_key, b.inner_key, s.value, a.path_id, a.type_id
                FROM assets a JOIN bundles b ON b.id = a.bundle_id JOIN strings s ON s.id = a.container_id
                WHERE (a.bundle_id, a.bundle_index) IN (VALUES 
                """);
            using var command = connection.CreateCommand();
            for (var i = 0; i < count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append($"($b{i},$x{i})");
                command.Parameters.AddWithValue($"$b{i}", BundleIdOf(keys[offset + i]));
                command.Parameters.AddWithValue($"$x{i}", BundleIndexOf(keys[offset + i]));
            }
            builder.Append(')');
            command.CommandText = builder.ToString();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result[CatalogKey(reader.GetInt64(0), reader.GetInt32(1))] =
                    $"{reader.GetString(2)}/{reader.GetString(3)}/{reader.GetString(4)}/" +
                    $"{reader.GetInt64(5).ToString(System.Globalization.CultureInfo.InvariantCulture)}." +
                    $"{reader.GetInt32(6).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }
        return result;
    }

    private static void WriteDerivedLayer(SqliteConnection connection, SqliteTransaction transaction,
        string[] displayPaths, long[] keys, CancellationToken cancellationToken)
    {
        using (var clearRank = Command(connection, transaction, "DELETE FROM catalog_rank"))
            clearRank.ExecuteNonQuery();
        // contentless 表（content=''）**没有原文可扫**，所以 `DELETE FROM asset_fts`
        // 一定报 `asset_fts: table does not support scanning` —— 带不带 WHERE 都一样。
        // 唯一的清空手段是 FTS5 的专用指令；实测可重复调用、之后重插与检索都正常、
        // `integrity-check` 通过（logs/probe_fts_clear.py）。
        using (var clearFts = Command(connection, transaction,
            "INSERT INTO asset_fts(asset_fts) VALUES('delete-all')"))
            clearFts.ExecuteNonQuery();
        InsertRankRows(connection, transaction, keys, cancellationToken);
        InsertSearchRows(connection, transaction, displayPaths, cancellationToken);
        using var state = Command(connection, transaction,
            "INSERT INTO derived_state(k,v) VALUES('revision',$rev),('rows',$rows)", "$rev", "$rows");
        state.Parameters[0].Value = DerivedSchemaRevision;
        state.Parameters[1].Value = displayPaths.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
        state.ExecuteNonQuery();
    }

    /// <summary>批量写名次表。多行 VALUES（每条 200 行）实测比逐条 prepared insert 快数倍
    /// —— 127 万行 5.9 s。每条语句的 SQL 文本对整批是同一个，SQLite 会复用已编译语句。</summary>
    private static void InsertRankRows(SqliteConnection connection, SqliteTransaction transaction,
        long[] keys, CancellationToken cancellationToken)
    {
        const int batch = 200;
        var builder = new System.Text.StringBuilder(batch * 24 + 64);
        for (var start = 0; start < keys.Length; start += batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(batch, keys.Length - start);
            builder.Clear().Append("INSERT INTO catalog_rank(r,bundle_id,bundle_index) VALUES ");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            for (var i = 0; i < count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append($"($r{i},$b{i},$x{i})");
                var key = keys[start + i];
                command.Parameters.AddWithValue($"$r{i}", start + i);
                command.Parameters.AddWithValue($"$b{i}", BundleIdOf(key));
                command.Parameters.AddWithValue($"$x{i}", BundleIndexOf(key));
            }
            command.CommandText = builder.ToString();
            command.ExecuteNonQuery();
        }
    }

    /// <summary>批量灌检索索引。<c>rowid</c> 直接用名次，所以检索命中的 rowid 就是名次，
    /// 排序/分页不需要任何转换。</summary>
    private static void InsertSearchRows(SqliteConnection connection, SqliteTransaction transaction,
        string[] displayPaths, CancellationToken cancellationToken)
    {
        const int batch = 200;
        var builder = new System.Text.StringBuilder(batch * 48 + 64);
        for (var start = 0; start < displayPaths.Length; start += batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(batch, displayPaths.Length - start);
            builder.Clear().Append("INSERT INTO asset_fts(rowid, dp) VALUES ");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            for (var i = 0; i < count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append($"($r{i},$d{i})");
                command.Parameters.AddWithValue($"$r{i}", start + i);
                command.Parameters.AddWithValue($"$d{i}", displayPaths[start + i]);
            }
            command.CommandText = builder.ToString();
            command.ExecuteNonQuery();
        }
    }

    /// <summary>trigram 的 AND 形态（<c>detail=none</c> 下唯一可用的查询写法）。
    /// <para>每个窗口都**加双引号**：FTS5 里裸写的多词查询会被当成短语，
    /// 而 <c>detail=none</c> 不支持短语（直接报错）；引号里则是单条字符串，
    /// 由 trigram 分词器切成一个词项。实测 trigram **跨空白建词项**，
    /// 所以 <c>"本 #"</c> 这种含空格的窗口本身就是合法词项
    /// （<c>logs/probe_trigram_space.py</c>）。</para>
    /// <para>跳过纯空白窗口只是防御：少一个 AND 词项只会让候选集变大，仍是超集，
    /// 复核照样把它滤掉。真正的反例只有「查询串里连着三个以上空格」这种。</para></summary>
    private static string TrigramAndQuery(string text)
    {
        var parts = new List<string>(Math.Max(1, text.Length - TrigramLength + 1));
        for (var i = 0; i + TrigramLength <= text.Length; i++)
        {
            var gram = text.Substring(i, TrigramLength);
            if (string.IsNullOrWhiteSpace(gram)) continue;
            parts.Add("\"" + gram.Replace("\"", "\"\"") + "\"");
        }
        return string.Join(" AND ", parts);
    }

    /// <summary>trigram 覆盖不到时的兜底：流式扫全表逐行算显示路径。
    /// 仍然不物化 AssetRecord —— 只是慢，不是不可用。</summary>
    private static List<int> ScanDisplayPaths(SqliteConnection connection, string needle,
        CancellationToken cancellationToken)
    {
        var hits = new List<int>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT k.r, a.container_entry, a.type_id, a.type, a.path_id
            FROM catalog_rank k JOIN assets a ON a.bundle_id = k.bundle_id AND a.bundle_index = k.bundle_index
            ORDER BY k.r
            """;
        using var reader = command.ExecuteReader();
        var seen = 0;
        while (reader.Read())
        {
            if (seen++ % 4096 == 0) cancellationToken.ThrowIfCancellationRequested();
            var containerEntry = reader.IsDBNull(1) ? null : reader.GetString(1);
            var path = AssetDisplay.CacheRowDisplayPath(
                reader.GetInt32(2), (AssetType)reader.GetInt32(3), reader.GetInt64(4), containerEntry);
            if (path.Contains(needle, StringComparison.OrdinalIgnoreCase)) hits.Add(reader.GetInt32(0));
        }
        return hits;
    }

    private static IReadOnlyList<UnityCachePageRow> ReadPageRows(SqliteConnection connection, long from, int take)
    {
        using var command = connection.CreateCommand();
        command.CommandText = PageColumns + " WHERE k.r >= $from AND k.r < $to ORDER BY k.r";
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", from + take);
        using var reader = command.ExecuteReader();
        var rows = new List<UnityCachePageRow>(take);
        while (reader.Read()) rows.Add(new(reader.GetInt32(0), ReadBundle(reader, 1), ReadRow(reader, 7)));
        return rows;
    }

    private static IReadOnlyList<UnityCachePageRow> ReadPageRowsByRank(SqliteConnection connection, int[] ranks)
    {
        if (ranks.Length == 0) return [];
        var builder = new System.Text.StringBuilder(PageColumns.Length + ranks.Length * 6);
        builder.Append(PageColumns).Append(" WHERE k.r IN (");
        using var command = connection.CreateCommand();
        for (var i = 0; i < ranks.Length; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append($"$k{i}");
            command.Parameters.AddWithValue($"$k{i}", ranks[i]);
        }
        builder.Append(") ORDER BY k.r");
        command.CommandText = builder.ToString();
        using var reader = command.ExecuteReader();
        var rows = new List<UnityCachePageRow>(ranks.Length);
        while (reader.Read()) rows.Add(new(reader.GetInt32(0), ReadBundle(reader, 1), ReadRow(reader, 7)));
        return rows;
    }

    /// <summary>只取复核显示路径需要的列（比整行轻）。</summary>
    private static List<(int Rank, string? ContainerEntry, int TypeId, AssetType Type, long PathId)>
        ReadDisplayKeysByRank(SqliteConnection connection, int[] ranks)
    {
        var builder = new System.Text.StringBuilder(160 + ranks.Length * 6);
        builder.Append("""
            SELECT k.r, a.container_entry, a.type_id, a.type, a.path_id
            FROM catalog_rank k JOIN assets a ON a.bundle_id = k.bundle_id AND a.bundle_index = k.bundle_index
            WHERE k.r IN (
            """);
        using var command = connection.CreateCommand();
        for (var i = 0; i < ranks.Length; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append($"$k{i}");
            command.Parameters.AddWithValue($"$k{i}", ranks[i]);
        }
        builder.Append(") ORDER BY k.r");
        command.CommandText = builder.ToString();
        using var reader = command.ExecuteReader();
        var rows = new List<(int, string?, int, AssetType, long)>(ranks.Length);
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2), (AssetType)reader.GetInt32(3), reader.GetInt64(4)));
        return rows;
    }

    private const string PageColumns = """
        SELECT k.r,
               b.data_path, b.size, b.mtime_ticks, b.outer_key, b.inner_key, b.static_bundle,
               a.bundle_index, s.value, a.path_id, a.type_id, a.type, a.size, bs.value, a.container_entry
        FROM catalog_rank k
        JOIN assets a ON a.bundle_id = k.bundle_id AND a.bundle_index = k.bundle_index
        JOIN bundles b ON b.id = a.bundle_id
        JOIN strings s ON s.id = a.container_id
        LEFT JOIN strings bs ON bs.id = a.baseline_id
        """;

    private static UnityCacheIndexBundle ReadBundle(SqliteDataReader reader, int offset)
        => new(reader.GetString(offset), reader.GetInt64(offset + 1), reader.GetInt64(offset + 2),
            reader.GetString(offset + 3), reader.GetString(offset + 4), reader.GetBoolean(offset + 5));

    private static UnityCacheIndexRow ReadRow(SqliteDataReader reader, int offset)
        => new(reader.GetInt32(offset), reader.GetString(offset + 1), reader.GetInt64(offset + 2),
            reader.GetInt32(offset + 3), (AssetType)reader.GetInt32(offset + 4), reader.GetInt64(offset + 5),
            reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6),
            reader.IsDBNull(offset + 7) ? null : reader.GetString(offset + 7));

    /// <summary>显示路径比较器（委托 <see cref="AssetDisplay.ComparePaths"/>，零分配）。</summary>
    private sealed class PathComparer : IComparer<string>
    {
        public static readonly PathComparer Instance = new();
        public int Compare(string? x, string? y) => AssetDisplay.ComparePaths(x, y);
    }

    /// <summary>打平行之间的兜底序：LogicalPath 的序数不区分大小写。</summary>
    private sealed class LogicalPathComparer(IReadOnlyDictionary<long, string> logicalPaths) : IComparer<long>
    {
        public int Compare(long x, long y)
            => string.Compare(
                logicalPaths.TryGetValue(x, out var a) ? a : null,
                logicalPaths.TryGetValue(y, out var b) ? b : null,
                StringComparison.OrdinalIgnoreCase);
    }
}
