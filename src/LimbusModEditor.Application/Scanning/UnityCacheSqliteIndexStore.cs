using System.Diagnostics;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Formats.Unity;
using Microsoft.Data.Sqlite;
using NLog;

namespace LimbusModEditor.Application.Scanning;

/// <param name="ContainerEntry">容器条目（可读的游戏内路径）；无条目时为 <see langword="null"/>。</param>
/// <param name="StaticKind">这一行的**行级**静态位。生产者必须用
/// <c>AssetStaticClassifier.RowBits(ContainerEntry)</c> 算出来（扫描期就这一步），
/// 空着不会报错但会让这一行的静态结论丢失 —— 即「资源列表里又冒出静态数据表」。</param>
public sealed record UnityCacheIndexRow(
    int BundleIndex, string Container, long PathId, int TypeId, AssetType Type, long Size, string? Baseline,
    string? ContainerEntry = null, StaticKind StaticKind = StaticKind.None);

public sealed record UnityCacheIndexBundle(
    string DataPath, long Size, long MTimeUtcTicks, string Outer, string Inner,
    StaticKind StaticKind = StaticKind.None)
{
    /// <summary>是不是静态数据 bundle（= 位非零）。保留布尔读法，调用方不必关心位的细节。</summary>
    public bool StaticBundle => StaticKind != StaticKind.None;
}

/// <summary>
/// 可以交给 SQLite 判的索引列条件（资源列表筛选下推的入参）。
///
/// <para><b>绝大多数条件都只是「不漏行」的保证</b>：SQL 侧筛出来的是超集，调用方必须再走
/// 一遍完整判据。理由是索引列只记录扫描时的事实 —— 编辑状态、替换文件是否存在都不在库里。</para>
///
/// <para><see cref="Type"/> 与 <see cref="IsStatic"/> 是例外：它们的 SQL 形态与目录判据
/// <b>完整等价</b>。<see cref="Type"/> 是因为一行索引的目录类型只由 <c>type_id</c>（认得的
/// class id）与 <c>type</c>（类型树兜底）两个列决定；<see cref="IsStatic"/> 是因为静态判据
/// 已经**在扫描期算完落进 <c>assets.static_kind</c>**（见
/// <c>AssetStaticClassifier</c>），查询期只比一次整数，两侧不可能给出不同答案。</para>
///
/// <para><see cref="Text"/> 走派生层检索索引：<c>FTS(dp∪lp)</c> 是旧
/// <c>AssetSearchService.MatchesText</c>（dp ∪ lp ∪ src）的超集，所以不会漏行；退化的
/// 三种情形（&lt; 3 字符、含反斜杠、命中 src 的公共前后缀）直接不加这条条件。</para>
/// </summary>
public sealed record UnityCacheIndexFilter(
    AssetType? Type = null,
    long? PathId = null,
    int? TypeId = null,
    long? MinSize = null,
    long? MaxSize = null,
    string? Container = null,
    bool? HasContainerEntry = null,
    string? Text = null,
    bool? IsStatic = null);

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
    /// 所以不会导致整库重扫。
    /// <para>修订历史：<c>d1</c> = 检索索引只含显示路径 <c>dp</c>；<c>d2</c> = 同时含
    /// <c>lp</c>（LogicalPath）—— 因为旧 <c>AssetSearchService.MatchesText</c> 搜的是
    /// <c>DisplayPath ∪ LogicalPath ∪ SourcePath</c>，只索引 <c>dp</c> 会让「按 LogicalPath
    /// 搜」从能搜到变成搜不到。实测 <c>lp</c> 的边际代价 +131.1 MiB / +34.2 s 灌入。</para></summary>
    public const string DerivedSchemaRevision = "d2";

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
                // asset_fts 的形状随修订号变（d1 只有 dp，d2 起是 dp+lp）。而
                // `CREATE VIRTUAL TABLE IF NOT EXISTS` 对**已存在**的表什么都不做 ——
                // 老库会留着只有 dp 的旧形状，写入端却已按 (rowid,dp,lp) 插，直接报
                // "table asset_fts has no column named lp"。派生层是可丢弃的（重建只花
                // 几十秒、不需要重扫 bundle），所以列名不符就直接 DROP 重建。
                // ── 就地迁移（**绝不 +1 SchemaVersion**）────────────────────────
                // 老 v2 库缺 assets.static_kind，且 ix_assets_named 是旧列序。两件事都必须
                // 在下面那段 CREATE 之前处理：新建索引的 DDL 里已经用了这一列。
                // 只加列 / 换索引，不 DROP 表、不重扫 1,471 个 bundle —— 结论可以就地算出来：
                // bundles.static_bundle 与 assets.container_entry 都已在库里。
                var staticKindAdded = false;
                if (!reset)
                {
                    staticKindAdded = EnsureStaticKindColumn(connection, command);
                    if (NamedIndexOrderDiffers(connection))
                    {
                        Log.Info("ix_assets_named 列序与当前选型不符（static_kind 需前置），摘掉按新形态重建");
                        command.CommandText = "DROP INDEX IF EXISTS ix_assets_named;";
                        command.ExecuteNonQuery();
                    }
                }
                if (SearchIndexColumnsDiffer(connection))
                {
                    Log.Info("检索索引形状与修订 {0} 不符，丢弃重建（派生层可丢弃，不影响 assets）",
                        DerivedSchemaRevision);
                    command.CommandText = "DROP TABLE IF EXISTS asset_fts;";
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
                        static_kind INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY(bundle_id, bundle_index)
                    ) WITHOUT ROWID;
                    -- ix_assets_named：**筛选下推的主力索引**。谓词与
                    -- AssetSearchService.HasContainerEntry 的「有容器条目」完全同形
                    -- （`IS NOT NULL AND <> ''`，不是 trim 形态 —— 后者与部分索引谓词不匹配，
                    -- planner 会退化成全扫 127 万名次表，实测 1,300 ms vs 157 ms）。
                    -- **列序以 static_kind 打头**（2026-09 实测选型）：静态行只占 0.22%，
                    -- 打头之后 `static_kind = 0` 成为前缀约束，默认视图（隐静态）从 191 ms
                    -- 降到 157 ms；只看静态表走 ix_assets_static，4.2 ms。
                    -- 代价是「不带静态条件」的查询退化成索引全扫（169 → 172 ms，可忽略：
                    -- 容器内有条目的行只有 51,376 条，扫完也就几十毫秒）。
                    CREATE INDEX IF NOT EXISTS ix_assets_named
                        ON assets(static_kind, container_entry, type, size)
                        WHERE container_entry IS NOT NULL AND container_entry <> '';
                    -- 静态数据表的专用部分索引：只有静态行在册（真实库 2,802 条），
                    -- 所以「只看静态数据表」是一次极小的索引扫。
                    CREATE INDEX IF NOT EXISTS ix_assets_static
                        ON assets(container_entry, static_kind, type, size)
                        WHERE static_kind <> 0;
                    -- ── 派生层（分页名次 + 子串检索）──────────────────────────
                    -- catalog_rank：目录全序的稠密名次 r → (bundle_id,bundle_index)。
                    --   r 以主键形态存在，所以「第 k 页」= 一次主键区间扫（实测 1.4 ms/页，
                    --   与页码深度无关），既不用 OFFSET 也不用把自定义自然序注册成
                    --   SQLite collation（编码成 BINARY 可比的排序键实测 43.9% 顺序不一致）。
                    CREATE TABLE IF NOT EXISTS catalog_rank (
                        r INTEGER PRIMARY KEY,
                        bundle_id INTEGER NOT NULL, bundle_index INTEGER NOT NULL
                    ) WITHOUT ROWID;
                    -- ix_catalog_rank_bundle：(bundle_id,bundle_index) 的反查索引。
                    --   **没有它筛选就完全下推不了** —— 实测：assets 那边有部分索引
                    --   ix_assets_named 也没用，因为「assets 驱动 → 反查明次」这一步在
                    --   catalog_rank 上无索引可用，planner 只能 `SCAN k`（127 万行 ×
                    --   3 次 B 树查找，实测 3.9–6.0 s/次）。补上这个索引之后同一条件的
                    --   计划变成 a、k 双向 COVERING INDEX，实测 168 ms（约 25×）。
                    --   代价：+16.8 MiB，重建时 DROP→批量建（1.1 s）而不是逐行维护。
                    CREATE INDEX IF NOT EXISTS ix_catalog_rank_bundle
                        ON catalog_rank(bundle_id, bundle_index);
                    -- asset_fts：索引 **dp（显示路径）+ lp（LogicalPath）** 两列。
                    --   口径来源是旧 AssetSearchService.MatchesText：
                    --     DisplayPath ∪ LogicalPath ∪ SourcePath
                    --   · dp 是可读的游戏内路径（Assets/Animation/SD/…），用户真正会搜的东西。
                    --   · lp 是「外层哈希/内层哈希/CAB-哈希/pathId.typeId」—— 除编号外全是
                    --     机器哈希，没人会手打；但旧实现确实能搜到它（以及由它构成的
                    --     SourcePath），为「行为等价」必须一起索引。边际代价实测
                    --     +131.1 MiB / +34.2 s 灌入（dp 单列 +49.7 MiB / 10.7 s）。
                    --   · SourcePath（= bundles.data_path）不必单列索引：实测
                    --     data_path = <缓存根>\<outer>\<inner>\__data，**可变内容只有
                    --     outer/inner，而 lp 已含这两段**；只剩缓存根与 \__data 这类
                    --     所有行都相同的常量（见 SearchRanks 的常量规则）。
                    --   content='' 是 contentless（不存原文副本，体积最小）；detail=none
                    --   只存词项不存位置 —— 因此**不支持裸短语查询**，只能用「手工 AND 词项」
                    --   形态，得到的是超集候选，由调用方复核（见 SearchRanks）。
                    --   rowid = 名次 r：检索命中直接就是名次，排序/分页零转换。
                    CREATE VIRTUAL TABLE IF NOT EXISTS asset_fts USING fts5(
                        dp, lp, content='', detail=none, columnsize=0, tokenize='trigram'
                    );
                    CREATE TABLE IF NOT EXISTS derived_state (k TEXT PRIMARY KEY, v TEXT NOT NULL);
                    PRAGMA user_version=2;
                    """;
                command.ExecuteNonQuery();
                if (staticKindAdded)
                {
                    var watch = Stopwatch.StartNew();
                    var (markedBundles, markedRows) = BackfillStaticKind(connection, transaction);
                    Log.Info("静态判据就地迁移完成（未重扫）：bundle 级标记 {0} 个 · 行级容器路径标记 {1} 条 · 耗时 {2:0} ms",
                        markedBundles, markedRows, watch.Elapsed.TotalMilliseconds);
                }
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

    /// <summary>
    /// 把本次扫描的 bundle 与资产写进索引库。
    ///
    /// <para><paramref name="bundleKinds"/> 是**本次扫描对每个 bundle 的静态位结论**
    /// （key = <c>data_path</c>）。它不是可选的装饰：只 upsert「变化过的」bundle 意味着
    /// 「catalog 判定变了但文件没变」时静态结论永远不会刷新（真实场景：游戏热修换键，
    /// 旧静态 bundle 留在缓存里、文件字节没动）。所以这里额外做一次**位级刷新** ——
    /// 对没被重写的 bundle，若结论与库里记的不同，就只改那几位：
    /// <c>assets.static_kind</c> 的高位（行级容器路径位）原样保留，只替换 bundle 级两位。</para>
    /// </summary>
    public void PersistAll(IReadOnlyList<UnityCacheScanEntry> currentEntries,
        IEnumerable<(UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow> Rows)> changedBundles,
        IReadOnlyDictionary<string, StaticKind>? bundleKinds = null,
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
            INSERT INTO assets(bundle_id,bundle_index,container_id,path_id,type_id,type,size,baseline_id,container_entry,static_kind)
            VALUES($b,$i,$c,$p,$tid,$t,$s,$bl,$ce,$sk)
            """, "$b", "$i", "$c", "$p", "$tid", "$t", "$s", "$bl", "$ce", "$sk");
        var written = 0;
        var rewritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (bundle, rows) in changedBundles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!keep.Contains(bundle.DataPath))
                throw new InvalidDataException($"变化的 bundle 不在本次枚举结果中：{bundle.DataPath}");
            rewritten.Add(bundle.DataPath);
            upsert.Parameters[0].Value = bundle.DataPath;
            upsert.Parameters[1].Value = bundle.Size;
            upsert.Parameters[2].Value = bundle.MTimeUtcTicks;
            upsert.Parameters[3].Value = bundle.Outer;
            upsert.Parameters[4].Value = bundle.Inner;
            upsert.Parameters[5].Value = (int)bundle.StaticKind;
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
                // 行里的结论 = bundle 级位（位1|位2，取自本支 bundle 的最终判定）| 行自己的
                // 容器路径位（位4）。**合成一处写、一处读**：查询期只比这一个整数。
                insert.Parameters[9].Value = (int)AssetStaticClassifier.Merge(bundle.StaticKind, row.StaticKind);
                insert.ExecuteNonQuery();
            }
            written++;
        }
        cancellationToken.ThrowIfCancellationRequested();
        // 静态位的**位级刷新**（只对没被重写的 bundle）。它不改行集，所以刻意**不**清
        // derived_state：名次与检索索引跟静态位无关，为它触发一次三十余秒的派生层重建
        // 是纯浪费（那正是老实现里「换了 catalog 之后要等很久」的一部分）。
        var refreshed = 0;
        if (bundleKinds is { Count: > 0 })
        {
            using var updateBundle = Command(connection, transaction,
                "UPDATE bundles SET static_bundle=$k WHERE id=$id", "$k", "$id");
            // 只替换 bundle 级两位（位1|位2），行级的容器路径位（位4）原样保留 ——
            // 它是逐行事实，与 bundle 标记翻不翻转无关。
            using var updateRows = Command(connection, transaction,
                $"UPDATE assets SET static_kind = (static_kind & ~{(int)StaticKind.BundleMask}) | $k WHERE bundle_id=$id",
                "$k", "$id");
            foreach (var (id, bundle) in bundles)
            {
                if (rewritten.Contains(bundle.DataPath)) continue;
                if (!bundleKinds.TryGetValue(bundle.DataPath, out var kind) || kind == bundle.StaticKind) continue;
                updateBundle.Parameters[0].Value = (int)kind;
                updateBundle.Parameters[1].Value = id;
                updateBundle.ExecuteNonQuery();
                updateRows.Parameters[0].Value = (int)kind;
                updateRows.Parameters[1].Value = id;
                updateRows.ExecuteNonQuery();
                refreshed++;
                Log.Debug("静态位刷新（文件未变，仅结论变）：{0} · {1} → {2}",
                    bundle.DataPath, AssetStaticClassifier.Describe(bundle.StaticKind),
                    AssetStaticClassifier.Describe(kind));
            }
        }
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
        Log.Debug("资源索引事务完成：枚举 {0} 个 bundle，重写 {1} 个，静态位刷新 {2} 个",
            currentEntries.Count, written, refreshed);
    }

    private static List<(long Id, UnityCacheIndexBundle Bundle)> ReadBundles(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction,
            "SELECT id,data_path,size,mtime_ticks,outer_key,inner_key,static_bundle FROM bundles ORDER BY id");
        using var reader = command.ExecuteReader();
        var bundles = new List<(long, UnityCacheIndexBundle)>();
        while (reader.Read())
            bundles.Add((reader.GetInt64(0), new(reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetString(4), reader.GetString(5), (StaticKind)reader.GetInt32(6))));
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
            SELECT bundle_id,bundle_index,container_id,path_id,type_id,type,size,baseline_id,container_entry,static_kind
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
                        reader.IsDBNull(7) ? null : strings[reader.GetInt64(7)],
                        reader.IsDBNull(8) ? null : reader.GetString(8), (StaticKind)reader.GetInt32(9)));
                hasRow = reader.Read();
            }
            yield return (bundle, rows);
        }
    }

    public sealed record UnityCacheContainerRow(string ContainerEntry, AssetType Type, long Size);

    /// <summary>
    /// 读出<b>非静态数据表</b>的容器行：与 <see cref="ReadContainerRows"/> 同一口径，
    /// 再用 <c>bundles.static_bundle</c> 与 <c>assets.static_kind</c> 两列把静态数据排除掉。
    ///
    /// <para><b>为什么生成维基页时要排除</b>：静态表另有一路事实源
    /// （<c>StaticBundleLocator</c> 读出的 <c>RelationStaticFact</c>），
    /// 把同一批 <c>static-data/*.json</c> 既当「资源」又当「静态表」会在页面里出现两次、
    /// 且资源侧的 type 是 TextAsset、没有容器路径语义（<c>AssetStaticClassifier</c> 的判据）。</para>
    /// </summary>
    public IReadOnlyList<UnityCacheContainerRow> ReadNonStaticContainerRows()
    {
        using var connection = OpenEnsured();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.container_entry, a.type, a.size
            FROM assets a JOIN bundles b ON b.id = a.bundle_id
            WHERE a.container_entry IS NOT NULL AND a.container_entry <> ''
              AND b.static_bundle = 0 AND a.static_kind = 0
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<UnityCacheContainerRow>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), (AssetType)reader.GetInt32(1), reader.GetInt64(2)));
        return rows;
    }

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

    /// <summary><c>asset_fts</c> 的列，**顺序敏感**（<c>PRAGMA table_info</c> 按声明序返回）。
    /// 只在形状自愈时用来比对，见 <see cref="SearchIndexColumnsDiffer"/>。</summary>
    private static readonly string[] SearchIndexColumns = ["dp", "lp"];

    /// <summary>已存在的 <c>asset_fts</c> 与当前修订号要求的列形状是否不一致。
    /// 表不存在时返回 false（没什么可丢的）。</summary>
    private static bool SearchIndexColumnsDiffer(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(asset_fts)";
        using var reader = command.ExecuteReader();
        var columns = new List<string>(SearchIndexColumns.Length);
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns.Count != 0 && !columns.SequenceEqual(SearchIndexColumns, StringComparer.Ordinal);
    }

    /// <summary><c>ix_assets_named</c> 的列序是否与当前选型不一致（见 EnsureSchema 里的选型说明）。
    /// 索引不存在、或表不存在时返回 false（下面那段 CREATE 会按新形态建）。</summary>
    private static bool NamedIndexOrderDiffers(SqliteConnection connection)
    {
        // 期望形态：static_kind 打头，让 `static_kind = 0` 成为前缀约束。
        string[] expected = ["static_kind", "container_entry", "type", "size"];
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_info(ix_assets_named)";
        using var reader = command.ExecuteReader();
        var columns = new List<string>(expected.Length);
        while (reader.Read()) columns.Add(reader.GetString(2));
        return columns.Count != 0 && !columns.SequenceEqual(expected, StringComparer.Ordinal);
    }

    /// <summary>
    /// 给老 v2 库补上 <c>assets.static_kind</c>。返回是否真的加了一次。
    ///
    /// <para><c>ALTER TABLE ... ADD COLUMN ... NOT NULL DEFAULT 0</c> 只改表头（SQLite 3.2+ 起
    /// 不重写数据），实测 15 ms / 127 万行 —— 这是「给索引库扩容」唯一被允许的形态：
    /// 改 <see cref="SchemaVersion"/> 会 DROP 重建 = 逼用户重扫 1,471 个 bundle（约 34 s + 重解析）。</para>
    /// </summary>
    private static bool EnsureStaticKindColumn(SqliteConnection connection, SqliteCommand command)
    {
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "PRAGMA table_info(assets)";
            using var reader = probe.ExecuteReader();
            while (reader.Read())
                if (string.Equals(reader.GetString(1), "static_kind", StringComparison.OrdinalIgnoreCase))
                    return false;
        }
        command.CommandText = "ALTER TABLE assets ADD COLUMN static_kind INTEGER NOT NULL DEFAULT 0;";
        command.ExecuteNonQuery();
        return true;
    }

    /// <summary>
    /// 就地回填 <c>static_kind</c>（迁移专用，只在列刚被加上时跑一次）。返回
    /// <c>(bundle 级标记数, 行级容器路径标记数)</c>。
    ///
    /// <para><b>为什么不写成纯 SQL</b>：路径判据的本体是 C#（
    /// <see cref="StaticBundleLocator.LooksLikeStaticTablePath"/>：反斜杠归一 + 前缀/段两条）。
    /// 在 SQL 里重写一遍就多了一份判据 —— 那正是这次改造要消灭的东西（迁移出来的结论与
    /// 扫描出来的结论必须是同一个函数算的）。代价是读 51,376 行、写 ~4,200 行，实测
    /// 数百毫秒级、只发生一次。</para>
    ///
    /// <para>两段都**只对位非零的行/包发 UPDATE**：真实库 1,471 个 bundle 里只有 2 个需要动，
    /// 51,376 个有容器条目的行里只有 2,801 条命中路径判据。</para>
    /// </summary>
    private static (int Bundles, int Rows) BackfillStaticKind(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        var bundleBits = new Dictionary<long, StaticKind>();
        using (var readBundles = Command(connection, transaction,
            "SELECT id,inner_key,static_bundle FROM bundles"))
        using (var reader = readBundles.ExecuteReader())
        {
            while (reader.Read())
            {
                // 老库的 static_bundle 是 bool 语义（1 = catalog 内层键命中）→ 迁到位1；
                // 位2（bundle 名）由名字现算 —— 名字是稳定事实，与 catalog 是否可用无关。
                var bits = AssetStaticClassifier.BundleBits(reader.GetInt64(2) != 0, reader.GetString(1));
                if (AssetStaticClassifier.IsStatic(bits)) bundleBits[reader.GetInt64(0)] = bits;
            }
        }
        var markedBundles = 0;
        using (var updateBundle = Command(connection, transaction,
            "UPDATE bundles SET static_bundle=$b WHERE id=$i", "$b", "$i"))
        using (var markBundleRows = Command(connection, transaction,
            "UPDATE assets SET static_kind = static_kind | $b WHERE bundle_id=$i", "$b", "$i"))
        {
            foreach (var (id, bits) in bundleBits)
            {
                updateBundle.Parameters[0].Value = (int)bits;
                updateBundle.Parameters[1].Value = id;
                markedBundles += updateBundle.ExecuteNonQuery();
                markBundleRows.Parameters[0].Value = (int)bits;
                markBundleRows.Parameters[1].Value = id;
                markBundleRows.ExecuteNonQuery();
            }
        }
        // 行级：只读「有容器条目」的行（4%），按 bundle 归组后分批打位。
        var pathHits = new Dictionary<long, List<int>>();
        using (var readRows = Command(connection, transaction,
            "SELECT bundle_id,bundle_index,container_entry FROM assets " +
            "WHERE container_entry IS NOT NULL AND container_entry <> ''"))
        using (var reader = readRows.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!AssetStaticClassifier.RowBits(reader.GetString(2)).HasFlag(StaticKind.ContainerPath)) continue;
                var bundleId = reader.GetInt64(0);
                if (!pathHits.TryGetValue(bundleId, out var indexes)) pathHits[bundleId] = indexes = [];
                indexes.Add(reader.GetInt32(1));
            }
        }
        var markedRows = 0;
        foreach (var (bundleId, indexes) in pathHits)
        {
            for (var offset = 0; offset < indexes.Count; offset += StaticBackfillBatch)
            {
                var count = Math.Min(StaticBackfillBatch, indexes.Count - offset);
                var builder = new System.Text.StringBuilder(count * 6 + 128);
                builder.Append("UPDATE assets SET static_kind = static_kind | ")
                    .Append((int)StaticKind.ContainerPath)
                    .Append(" WHERE bundle_id=$b AND bundle_index IN (");
                using var mark = connection.CreateCommand();
                mark.Transaction = transaction;
                mark.Parameters.AddWithValue("$b", bundleId);
                for (var i = 0; i < count; i++)
                {
                    if (i > 0) builder.Append(',');
                    builder.Append($"$x{i}");
                    mark.Parameters.AddWithValue($"$x{i}", indexes[offset + i]);
                }
                builder.Append(')');
                mark.CommandText = builder.ToString();
                markedRows += mark.ExecuteNonQuery();
            }
        }
        return (markedBundles, markedRows);
    }

    /// <summary>迁移回填时每条 UPDATE 带多少个主键（400 实测比逐条快一个数量级，
    /// 又远低于 SQLite 的参数上限）。</summary>
    private const int StaticBackfillBatch = 400;

    private static long CatalogKey(long bundleId, int bundleIndex)
        => (bundleId << RidShift) | (uint)bundleIndex;

    private static long BundleIdOf(long key) => key >> RidShift;
    private static int BundleIndexOf(long key) => (int)(key & RidMask);

    /// <summary><c>derived_state</c> 的快照。<paramref name="Rows"/> = 上次成功建成时的行数，
    /// -1 表示没有记录。
    /// <para><paramref name="SourcePrefix"/> / <paramref name="SourceSuffix"/> 是所有
    /// <c>bundles.data_path</c> 的公共前后缀。检索要覆盖旧实现的 <c>SourcePath</c> 字段，
    /// 而 <c>data_path</c> 的可变内容只有 outer/inner（已含在 <c>lp</c> 里），公共前后缀是
    /// **所有行都相同**的常量 —— 命中它就等于「没在过滤」，见
    /// <see cref="SearchRanks"/> 的常量规则。</para></summary>
    public sealed record DerivedState(string? Revision, long Rows, string? SourcePrefix = null, string? SourceSuffix = null);

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
        // 公共前后缀要在**开事务之前**读：Microsoft.Data.Sqlite 要求连接上有活动事务时
        // 每条命令都得显式带上同一个事务对象，而这种只读辅助查询没必要塞进写事务里。
        var affixes = ReadSourcePathAffixes(connection);
        // 名次与检索索引放在**同一个事务**：检索索引以名次为 rowid，两者不同步就等于
        // 搜到的行指向别的资源。要么都是新的，要么都还是旧的那一份。
        using (var transaction = connection.BeginTransaction())
        {
            WriteDerivedLayer(connection, transaction, displayPaths, keys, affixes, cancellationToken);
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
        var rows = StreamByRanks(ranks).ToList();
        rows.Sort(static (a, b) => a.Rank.CompareTo(b.Rank));
        return rows;
    }

    /// <summary>
    /// 按名次**流式**读行：一个连接内分批（<see cref="RankChunk"/>，压在 SQLite 参数上限之下）。
    /// <para>为什么要流式：资源列表按页查询时候选集可能有几十万条，一次性返回既费内存，
    /// 又会让每批都重开一次连接（WAL 下每次 open 都要读 schema）。调用方在这条流上
    /// 边读边判、只留名次即可。</para>
    /// <para>输入名次升序时输出也升序（批内 <c>ORDER BY k.r</c>、批间按输入顺序推进）。
    /// 输入不保证升序时请用 <see cref="ReadByRanks"/>。</para>
    /// </summary>
    public IEnumerable<UnityCachePageRow> StreamByRanks(
        IReadOnlyList<int> ranks, CancellationToken cancellationToken = default)
    {
        if (ranks.Count == 0) yield break;
        using var connection = OpenEnsured();
        EnsureDerivedForQuery(connection);
        for (var offset = 0; offset < ranks.Count; offset += RankChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(RankChunk, ranks.Count - offset);
            var slice = new int[count];
            for (var i = 0; i < count; i++) slice[i] = ranks[offset + i];
            foreach (var row in ReadPageRowsByRank(connection, slice)) yield return row;
        }
    }

    /// <summary>
    /// 按**子串**检索，返回已精确复核的名次（升序）。
    /// <para>匹配面与旧 <c>AssetSearchService.MatchesText</c> 等价：
    /// <c>DisplayPath ∪ LogicalPath ∪ SourcePath</c>，大小写不敏感。</para>
    /// <para>① FTS 候选：索引里是 <c>dp</c> 与 <c>lp</c> 两列。trigram 把查询串切成
    /// 3 字符窗口手工 AND —— <c>detail=none</c> 下裸短语会直接报错，而 AND 出来的只是
    /// **超集**（"abcd" 会命中「含 abc、也含 bcd，但两者不相邻」的文本）。</para>
    /// <para>② 复核：把候选行读回来算 dp / lp / src 三种文本，用 <c>OrdinalIgnoreCase</c>
    /// 精确判子串（见 <see cref="MatchesReverifyKeys"/>）。</para>
    /// <para>③ <c>src</c> 不入索引但不等价性丢失 —— 见
    /// <see cref="MatchesSourcePathConstant"/>：它的可变内容就是 <c>lp</c> 的前两段。</para>
    /// <para>查询串短于 3 字符时索引里没有任何可用词项，退化为流式全表扫描
    /// （逐行算三种文本，不物化记录；真实规模实测 1.5–3 s）。含反斜杠的查询串同理
    /// —— 那可能只在 <c>src</c> 的分隔符处命中。</para>
    /// </summary>
    public IReadOnlyList<int> SearchRanks(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var needle = text.Trim();
        if (needle.Length == 0) return [];
        using var connection = OpenEnsured();
        EnsureDerivedForQuery(connection);
        var state = ReadDerivedState(connection);
        if (MatchesSourcePathConstant(state, needle))
        {
            // 命中「所有行都相同」的那段 —— 旧实现在这里也没有过滤作用，返回全部。
            Log.Debug("检索「{0}」：命中 SourcePath 的公共前后缀（每行都相同）⇒ 等价于不过滤，返回全部名次", needle);
            return ReadAllRanks(connection);
        }
        var match = needle.Length < TrigramLength ? string.Empty : TrigramAndQuery(needle);
        if (match.Length == 0 || needle.Contains('\\'))
        {
            Log.Debug("检索退化全表扫描：查询串「{0}」（{1} 字符，词项取不到或可能只命中 SourcePath 的分隔符）",
                needle, needle.Length);
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
            foreach (var row in ReadReverifyKeysByRank(connection, slice))
                if (MatchesReverifyKeys(row, needle)) verified.Add(row.Rank);
        }
        verified.Sort();
        Log.Debug("检索「{0}」：候选 {1:N0} → 复核 {2:N0}（词项 {3}）", needle, candidates.Count, verified.Count, match);
        return verified;
    }

    /// <summary>
    /// 按 <b>索引列</b>筛出候选名次（<b>升序</b>）。资源列表「筛选下推」的入口：能在 SQL 里判的
    /// 条件全部交给 SQLite，只把库里根本没有的事实（编辑状态、替换文件是否存在）
    /// 留给调用方在内存里复核。
    ///
    /// <para><b>静态判据现在也在库里</b>（<see cref="UnityCacheIndexFilter.IsStatic"/>）：
    /// 它是扫描期算好落进 <c>assets.static_kind</c> 的位掩码合成值，查询期只做一次整数比较。
    /// 在这之前它是运行时逐行重算三道字符串判据 —— 每查一次列表 1.8 s，这正是老功能
    /// 「时灵时不灵 + 卡」的代价。</para>
    ///
    /// <para>与 <see cref="SearchRanks"/> 的分工：后者是「只按文本搜、并当场复核文本」的完整
    /// 答案；本方法是「多条件筛选的候选集」，文本那一部分复用同一张检索索引，但**不在这里
    /// 复核** —— 复核由调用方连其它条件一起做，免得同一行被拆成两处判定。</para>
    ///
    /// <para><b>为什么不写 <c>ORDER BY k.r</c></b>（这是一个实测踩出来的坑）：
    /// 排序需求会让 planner 认定「按主键扫名次表」是最省的计划，于是**每次查询都全扫
    /// 1,275,623 行 catalog_rank 再逐行 join**，实测 3.9–6.0 s —— 筛选下推等于白做
    /// （连 assets 上的部分索引 <c>ix_assets_named</c> 都用不上，因为反查明次那一步在
    /// catalog_rank 上没有索引）。去掉 ORDER BY 之后同一个条件变成两侧 COVERING INDEX，
    /// 实测 168 ms。名次是 int，调用方排序几万条只要几毫秒 —— 这个交换是划算的。</para>
    ///
    /// <para>同理 JOIN 也按需最小化：没有 assets 侧条件时连 assets 都不碰
    /// （<c>SELECT r FROM catalog_rank</c> 实测 39 ms 出 127 万行，而原来的写法
    /// 41.8 s）；<c>bundles</c> 从前被 join 进来却从未在条件里用过，纯浪费。</para>
    ///
    /// <para>约束：索引行都是扫描产生的缓存引用（<c>reference=true</c>），所以
    /// <see cref="UnityCacheIndexFilter.HasContainerEntry"/> 可以直接落成
    /// <c>container_entry</c> 的空/非空 —— 与 <c>AssetSearchService</c> 对这类记录的判定同口径。
    /// 导入的旧式资源不在索引里，永远由项目态提供。</para>
    /// </summary>
    public IReadOnlyList<int> ReadCandidateRanks(
        UnityCacheIndexFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        using var connection = OpenEnsured();
        EnsureDerivedForQuery(connection);
        using var command = connection.CreateCommand();
        var conditions = new List<string>(8);
        // assets 侧条件（能只看 a 一行判定）—— 有它就说明必须 join assets，
        // 而且 planner 有机会走 ix_assets_named 的部分索引。
        var needsAssets = false;
        if (filter.Type is { } type)
        {
            // 目录类型 = Map(type_id, 索引里的 type)，所以「type 列等于目标」与
            // 「type_id 属于映射到目标的那些 class id」并起来是完整覆盖（不是近似）。
            var classIds = UnityClassId.ClassIdsOf(type);
            if (classIds.Count == 0)
            {
                conditions.Add("a.type = $type");
            }
            else
            {
                var placeholders = new System.Text.StringBuilder(classIds.Count * 9);
                for (var i = 0; i < classIds.Count; i++)
                {
                    if (i > 0) placeholders.Append(',');
                    placeholders.Append("$t").Append(i);
                    command.Parameters.AddWithValue($"$t{i}", classIds[i]);
                }
                conditions.Add($"(a.type = $type OR a.type_id IN ({placeholders}))");
            }
            command.Parameters.AddWithValue("$type", (int)type);
            needsAssets = true;
        }
        if (filter.PathId is { } pathId)
        {
            conditions.Add("a.path_id = $pathId");
            command.Parameters.AddWithValue("$pathId", pathId);
            needsAssets = true;
        }
        if (filter.TypeId is { } typeId)
        {
            conditions.Add("a.type_id = $typeId");
            command.Parameters.AddWithValue("$typeId", typeId);
            needsAssets = true;
        }
        if (filter.MinSize is { } minSize)
        {
            conditions.Add("a.size >= $minSize");
            command.Parameters.AddWithValue("$minSize", minSize);
            needsAssets = true;
        }
        if (filter.MaxSize is { } maxSize)
        {
            conditions.Add("a.size <= $maxSize");
            command.Parameters.AddWithValue("$maxSize", maxSize);
            needsAssets = true;
        }
        if (!string.IsNullOrEmpty(filter.Container))
        {
            // instr+lower 而不是 LIKE：容器段是 CAB-<32hex> 这类机器串，没有通配符语义，
            // 而 LIKE 会把 _ / % 当元字符（`CAB-_` 之类的查询串会变成模糊匹配）。
            conditions.Add("instr(lower(s.value), lower($container)) > 0");
            command.Parameters.AddWithValue("$container", filter.Container);
            needsAssets = true;
        }
        if (filter.HasContainerEntry is { } hasContainerEntry)
        {
            // trim：元数据侧写的是「非空才写 containerEntry」，空白串在记录上等于没有条目，
            // 所以两侧都按 trim 后是否为空判，避免「SQL 说有条目、内存说没有」。
            //
            // **「有条目」那一支刻意不写 trim**（2026-09 实测）：部分索引 ix_assets_named 的
            // 谓词是 `IS NOT NULL AND <> ''`，而 `trim(coalesce(...)) <> ''` 与它**不匹配** ⇒
            // planner 用不上索引，退化成「全扫 127 万名次表 + 逐行 PK 探」，默认视图实测
            // 1,300.9 ms → 62.7/157 ms（约 8×）。两者只在「空白串 / 带前后空格」上不同：
            // 真库这类行**为 0**，而这种差异方向是**多带行**（SQL 超集、内存判据复核掉），
            // 不是漏行。「无条目」那一支保持 trim（它本来就用不上那个部分索引，无代价）。
            conditions.Add(hasContainerEntry
                ? "a.container_entry IS NOT NULL AND a.container_entry <> ''"
                : "trim(coalesce(a.container_entry,'')) = ''");
            needsAssets = true;
        }
        if (filter.IsStatic is { } isStatic)
        {
            // 静态判据（2026-09 恢复）：索引列 static_kind 是扫描期算好的位掩码合成值，
            // 所以这里是一次整数比较 —— `= 0` 精确匹配 ix_assets_named 的**前置**列
            // （静态行只占 0.22%，前置让「隐藏静态」成为一次前缀区间扫：实测 157 ms，
            // 对照：换回运行时的表达式判据 1,844.7 ms）。
            conditions.Add(isStatic ? "a.static_kind <> 0" : "a.static_kind = 0");
            needsAssets = true;
        }
        var needsStrings = !string.IsNullOrEmpty(filter.Container);
        if (!string.IsNullOrEmpty(filter.Text))
        {
            var needle = filter.Text.Trim();
            var state = ReadDerivedState(connection);
            // 三种情形不加条件（退回调用方复核，结果不变、只是慢）：命中 src 的公共前后缀
            // （= 每一行都命中）、含反斜杠（可能只命中 src 的分隔符）、词项取不到（< 3 字符）。
            if (needle.Length > 0 && !needle.Contains('\\') && !MatchesSourcePathConstant(state, needle))
            {
                var match = needle.Length < TrigramLength ? string.Empty : TrigramAndQuery(needle);
                if (match.Length > 0)
                {
                    conditions.Add("k.r IN (SELECT rowid FROM asset_fts WHERE asset_fts MATCH $fts)");
                    command.Parameters.AddWithValue("$fts", match);
                }
            }
        }
        // 驱动表选择见方法注释：有 assets 侧条件时从 assets 起（能吃到部分索引），
        // 否则直接扫名次表 —— 无条件时连 assets 都不需要碰。
        var sql = new System.Text.StringBuilder(360);
        if (needsAssets)
        {
            sql.Append("SELECT k.r FROM assets a")
                .Append(" JOIN catalog_rank k ON k.bundle_id = a.bundle_id AND k.bundle_index = a.bundle_index");
            if (needsStrings) sql.Append(" JOIN strings s ON s.id = a.container_id");
        }
        else
        {
            sql.Append("SELECT k.r FROM catalog_rank k");
        }
        for (var i = 0; i < conditions.Count; i++) sql.Append(i == 0 ? " WHERE " : " AND ").Append(conditions[i]);
        command.CommandText = sql.ToString();
        var ranks = new List<int>();
        using var reader = command.ExecuteReader();
        var seen = 0;
        while (reader.Read())
        {
            if (seen++ % 4096 == 0) cancellationToken.ThrowIfCancellationRequested();
            ranks.Add(reader.GetInt32(0));
        }
        // SQL 侧刻意不排序（见方法注释），升序在这里补回来 —— 调用方（分页/树）按「名次升序
        // = 目录全序」处理结果，返回无序的列表会静默给出乱序的页。
        ranks.Sort();
        Log.Debug("候选名次：{0:N0} 条（条件 {1} 个，文本「{2}」，驱动表 {3}）",
            ranks.Count, conditions.Count, filter.Text ?? "-", needsAssets ? "assets" : "catalog_rank");
        return ranks;
    }

    /// <summary>
    /// 按 <see cref="AssetRecord.LogicalPath"/> 定位目录名次（找不到返回 -1）。
    /// <para>名字的分段就是索引列本身：<c>&lt;outerKey&gt;/&lt;innerKey&gt;/&lt;container&gt;/&lt;pathId&gt;.&lt;typeId&gt;</c>
    /// （见 <see cref="AssetDisplay.CacheRowLogicalPath"/>）。列表/树改成按页查询之后，
    /// 这是**唯一稳定的定位手段** —— <c>AssetRecord.AssetId</c> 每次回灌都会变
    /// （<c>Guid.NewGuid()</c>），拿它定位会在「重新物化」后失效。</para>
    /// <para>导入的旧式资源不在索引里（它的 LogicalPath 是项目记录自己的名字），返回 -1。</para>
    /// </summary>
    public int FindRank(string logicalPath)
    {
        if (!TryParseLogicalPath(logicalPath, out var outer, out var inner, out var container,
                out var pathId, out var typeId))
            return -1;
        using var connection = OpenEnsured();
        EnsureDerivedForQuery(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT k.r FROM catalog_rank k
            JOIN assets a ON a.bundle_id = k.bundle_id AND a.bundle_index = k.bundle_index
            JOIN bundles b ON b.id = a.bundle_id
            JOIN strings s ON s.id = a.container_id
            WHERE b.outer_key = $outer AND b.inner_key = $inner AND s.value = $container
              AND a.path_id = $pathId AND a.type_id = $typeId
            """;
        command.Parameters.AddWithValue("$outer", outer);
        command.Parameters.AddWithValue("$inner", inner);
        command.Parameters.AddWithValue("$container", container);
        command.Parameters.AddWithValue("$pathId", pathId);
        command.Parameters.AddWithValue("$typeId", typeId);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? -1 : Convert.ToInt32(value);
    }

    /// <summary>拆 <c>&lt;outerKey&gt;/&lt;innerKey&gt;/&lt;container&gt;/&lt;pathId&gt;.&lt;typeId&gt;</c>。
    /// 段数不对 / 编号不是十进制整数就当它不是索引里的路径（不猜、不模糊匹配）。
    /// <para><b>必须允许前导正负号</b>：<c>path_id</c> 是 Unity 的对象 id（内部是带符号的
    /// 64 位哈希），真实缓存里有<em>大量</em>负值 —— 用 <c>NumberStyles.None</c> 会把它们
    /// 全部判成「不是索引路径」，于是 <see cref="FindRank"/> 对这些资源永远返回 -1，
    /// 表现是「精确跳转/定位到某资源」静默失效（列表里明明看得到它）。
    /// 实测踩到：探针里对页码 0 的第一条资源调 <c>Locate</c> 直接返回 null。</para>
    /// <para>只放开符号位，仍然拒绝空白、千位分隔、十六进制 —— 那些是真的不像索引路径。</para>
    /// </summary>
    private static bool TryParseLogicalPath(string? logicalPath, out string outer, out string inner,
        out string container, out long pathId, out int typeId)
    {
        const System.Globalization.NumberStyles Integer = System.Globalization.NumberStyles.AllowLeadingSign;
        outer = inner = container = string.Empty;
        pathId = 0;
        typeId = 0;
        if (string.IsNullOrWhiteSpace(logicalPath)) return false;
        var segments = logicalPath.Split('/');
        if (segments.Length != 4) return false;
        var tail = segments[3].Split('.');
        if (tail.Length != 2) return false;
        if (!long.TryParse(tail[0], Integer,
                System.Globalization.CultureInfo.InvariantCulture, out pathId)) return false;
        if (!int.TryParse(tail[1], Integer,
                System.Globalization.CultureInfo.InvariantCulture, out typeId)) return false;
        outer = segments[0];
        inner = segments[1];
        container = segments[2];
        return true;
    }

    /// <summary>
    /// <c>SourcePath</c>（= <c>bundles.data_path</c>）的等价性补丁。
    /// <para>旧实现的匹配面含 SourcePath，而派生层只索引 dp+lp。之所以不为 src 单列索引：
    /// 实测 <c>data_path = &lt;缓存根&gt;\&lt;outer&gt;\&lt;inner&gt;\__data</c>（1471/1471 条同形），
    /// **可变内容只有 outer/inner —— 而 lp 已含这两段**，所以任何落在 outer/inner 里的
    /// 子串都会由 lp 命中。src 里剩下的只有「所有行都相同」的两段常量：公共前缀（缓存根
    /// 目录）与公共后缀（<c>\__data</c>）。</para>
    /// <para>于是规则是：<b>needle 含于公共前后缀 ⇒ 它命中了每一行</b>（旧实现在这种情况下
    /// 同样没有任何过滤作用），直接返回全部名次。跨 outer/inner 或跨进 <c>\__data</c> 的
    /// needle 含反斜杠，由 <see cref="SearchRanks"/> 退化为全表扫描。其余情形
    /// FTS(dp∪lp) 已是 src 匹配面的超集。</para>
    /// </summary>
    private static bool MatchesSourcePathConstant(DerivedState state, string needle)
        => (state.SourcePrefix is { Length: > 0 } prefix && prefix.Contains(needle, StringComparison.OrdinalIgnoreCase))
        || (state.SourceSuffix is { Length: > 0 } suffix && suffix.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static List<int> ReadAllRanks(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT r FROM catalog_rank ORDER BY r";
        using var reader = command.ExecuteReader();
        var ranks = new List<int>();
        while (reader.Read()) ranks.Add(reader.GetInt32(0));
        return ranks;
    }

    // ── 派生层内部实现 ──────────────────────────────────────────────────

    private static DerivedState ReadDerivedState(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT k, v FROM derived_state";
        string? revision = null;
        string? prefix = null;
        string? suffix = null;
        var rows = -1L;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var value = reader.GetString(1);
            switch (reader.GetString(0))
            {
                case "revision": revision = value; break;
                case "rows": long.TryParse(value, out rows); break;
                case "src_prefix": prefix = value; break;
                case "src_suffix": suffix = value; break;
            }
        }
        return new(revision, rows, prefix, suffix);
    }

    /// <summary>所有 <c>data_path</c> 的公共前缀与公共后缀（各读一遍 1471 行，微秒级）。
    /// 供检索的 SourcePath 常量规则使用。</summary>
    private static (string Prefix, string Suffix) ReadSourcePathAffixes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data_path FROM bundles";
        using var reader = command.ExecuteReader();
        var paths = new List<string>(2048);
        while (reader.Read()) paths.Add(reader.GetString(0));
        if (paths.Count == 0) return (string.Empty, string.Empty);
        var prefix = paths[0];
        foreach (var path in paths)
        {
            var n = Math.Min(prefix.Length, path.Length);
            var i = 0;
            while (i < n && prefix[i] == path[i]) i++;
            prefix = prefix[..i];
            if (prefix.Length == 0) break;
        }
        var reversed = paths.ConvertAll(static p => new string(p.Reverse().ToArray()));
        var suffix = reversed[0];
        foreach (var path in reversed)
        {
            var n = Math.Min(suffix.Length, path.Length);
            var i = 0;
            while (i < n && suffix[i] == path[i]) i++;
            suffix = suffix[..i];
            if (suffix.Length == 0) break;
        }
        return (prefix, new string(suffix.Reverse().ToArray()));
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
        var logicalPaths = ReadLogicalPaths(connection, tiedKeys.ToArray(), 0, tiedKeys.Count, cancellationToken);
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

    /// <summary>取给定打包键（<c>bundle_id/bundle_index</c>）对应的 LogicalPath。
    /// 两个调用点都是「按批 IN」而非全表：打平的行只有几万条，检索索引写入每批一千条。
    /// <para>拼串走 <see cref="AssetDisplay.CacheRowLogicalPath"/> —— 与检索复核同一个函数，
    /// 否则「索引里写的 lp」和「复核时算的 lp」可能不是一个串，造成假阴性。</para></summary>
    private static Dictionary<long, string> ReadLogicalPaths(SqliteConnection connection, long[] keys,
        int start, int count, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        var result = new Dictionary<long, string>(count);
        const int batch = 200;
        for (var offset = 0; offset < count; offset += batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = Math.Min(batch, count - offset);
            var builder = new System.Text.StringBuilder(take * 12 + 200);
            builder.Append("""
                SELECT a.bundle_id, a.bundle_index, b.outer_key, b.inner_key, s.value, a.path_id, a.type_id
                FROM assets a JOIN bundles b ON b.id = a.bundle_id JOIN strings s ON s.id = a.container_id
                WHERE (a.bundle_id, a.bundle_index) IN (VALUES 
                """);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            for (var i = 0; i < take; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append($"($b{i},$x{i})");
                var key = keys[start + offset + i];
                command.Parameters.AddWithValue($"$b{i}", BundleIdOf(key));
                command.Parameters.AddWithValue($"$x{i}", BundleIndexOf(key));
            }
            builder.Append(')');
            command.CommandText = builder.ToString();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result[CatalogKey(reader.GetInt64(0), reader.GetInt32(1))] = AssetDisplay.CacheRowLogicalPath(
                    reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetInt64(5), reader.GetInt32(6));
        }
        return result;
    }

    private static void WriteDerivedLayer(SqliteConnection connection, SqliteTransaction transaction,
        string[] displayPaths, long[] keys, (string Prefix, string Suffix) affixes, CancellationToken cancellationToken)
    {
        // 反查索引在重建期间先摘掉：127 万行逐行维护索引比整批建贵得多（实测批量建 1.1 s），
        // 而且下面这一删一插的中间态没有任何读者（派生层要么是旧的、要么是新的）。
        using (var dropIndex = Command(connection, transaction, "DROP INDEX IF EXISTS ix_catalog_rank_bundle"))
            dropIndex.ExecuteNonQuery();
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
        // 名次写完再整批建反查索引（见 WriteDerivedLayer 开头为什么先摘掉）。
        using (var createIndex = Command(connection, transaction,
            "CREATE INDEX IF NOT EXISTS ix_catalog_rank_bundle ON catalog_rank(bundle_id, bundle_index)"))
            createIndex.ExecuteNonQuery();
        InsertSearchRows(connection, transaction, displayPaths, keys, cancellationToken);
        // 先清再写。常规路径下 derived_state 已被 PersistAll 清空，所以这一步看着多余 ——
        // 但**修订号变化**时（如 d1→d2）状态行还在，直接 INSERT 会撞主键
        // （`UNIQUE constraint failed: derived_state.k`）。修订号不符本来就是重建的合法触发
        // 条件之一，不能只在「表被整个删掉」的情形下才成立。派生层本就可丢弃，清掉再写最省心。
        using (var clearState = Command(connection, transaction, "DELETE FROM derived_state"))
            clearState.ExecuteNonQuery();
        using var state = Command(connection, transaction, """
            INSERT INTO derived_state(k,v) VALUES
                ('revision',$rev),('rows',$rows),('src_prefix',$pre),('src_suffix',$suf)
            """, "$rev", "$rows", "$pre", "$suf");
        state.Parameters[0].Value = DerivedSchemaRevision;
        state.Parameters[1].Value = displayPaths.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
        state.Parameters[2].Value = affixes.Prefix;
        state.Parameters[3].Value = affixes.Suffix;
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
    /// 排序/分页不需要任何转换。
    /// <para><c>lp</c> 按批现取（<see cref="ReadLogicalPaths"/>），**不整表驻留**：
    /// <c>dp</c> 数组本身已是约 130 MiB 的瞬时分配，再并排留一份 127 万条的 <c>lp</c>
    /// 就是翻倍，而这层重建是后台可取消任务，没必要为它顶到峰值。
    /// 批量取 1000 行（×3 个参数，远低于 SQLite 的变量上限），比 200 行少 5 倍往返。</para></summary>
    private static void InsertSearchRows(SqliteConnection connection, SqliteTransaction transaction,
        string[] displayPaths, long[] keys, CancellationToken cancellationToken)
    {
        const int batch = 1000;
        var builder = new System.Text.StringBuilder(batch * 64 + 64);
        for (var start = 0; start < displayPaths.Length; start += batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(batch, displayPaths.Length - start);
            var logicalPaths = ReadLogicalPaths(connection, keys, start, count, cancellationToken, transaction);
            builder.Clear().Append("INSERT INTO asset_fts(rowid, dp, lp) VALUES ");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            for (var i = 0; i < count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append($"($r{i},$d{i},$l{i})");
                command.Parameters.AddWithValue($"$r{i}", start + i);
                command.Parameters.AddWithValue($"$d{i}", displayPaths[start + i]);
                command.Parameters.AddWithValue($"$l{i}", logicalPaths[keys[start + i]]);
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

    /// <summary>词项取不到（查询串 &lt; 3 字符）或可能只命中 <c>SourcePath</c> 分隔符时的兜底：
    /// 流式扫全表逐行算 dp / lp / src 三种文本。判据与 FTS 路径**同一个函数**
    /// （<see cref="MatchesReverifyKeys"/>），所以两条路径的结果一致。
    /// 仍然不物化 AssetRecord —— 只是慢，不是不可用。</summary>
    private static List<int> ScanDisplayPaths(SqliteConnection connection, string needle,
        CancellationToken cancellationToken)
    {
        var hits = new List<int>();
        using var command = connection.CreateCommand();
        command.CommandText = ReverifyColumns + " ORDER BY k.r";
        using var reader = command.ExecuteReader();
        var seen = 0;
        while (reader.Read())
        {
            if (seen++ % 4096 == 0) cancellationToken.ThrowIfCancellationRequested();
            var row = ReadReverifyKeys(reader);
            if (MatchesReverifyKeys(row, needle)) hits.Add(row.Rank);
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

    /// <summary>复核一行需要的全部文本来源：显示路径（<c>dp</c>）、LogicalPath（<c>lp</c>）、
    /// 源文件路径（<c>src</c>）。三者齐全，复核才与旧
    /// <c>AssetSearchService.MatchesText</c> 的匹配面等价。</summary>
    private readonly record struct ReverifyKeys(
        int Rank, string? ContainerEntry, int TypeId, AssetType Type, long PathId,
        string OuterKey, string InnerKey, string Container, string SourcePath);

    /// <summary>复核用的列（比整行轻，但够算出 dp / lp / src 三种文本）。</summary>
    private static List<ReverifyKeys> ReadReverifyKeysByRank(SqliteConnection connection, int[] ranks)
    {
        if (ranks.Length == 0) return [];
        var builder = new System.Text.StringBuilder(200 + ranks.Length * 6);
        builder.Append(ReverifyColumns).Append(" WHERE k.r IN (");
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
        var rows = new List<ReverifyKeys>(ranks.Length);
        while (reader.Read()) rows.Add(ReadReverifyKeys(reader));
        return rows;
    }

    /// <summary>文本列投影：<c>dp</c> 的来源、<c>lp</c> 的三段、<c>src</c> 本身。</summary>
    private const string ReverifyColumns = """
        SELECT k.r, a.container_entry, a.type_id, a.type, a.path_id,
               b.outer_key, b.inner_key, s.value, b.data_path
        FROM catalog_rank k
        JOIN assets a ON a.bundle_id = k.bundle_id AND a.bundle_index = k.bundle_index
        JOIN bundles b ON b.id = a.bundle_id
        JOIN strings s ON s.id = a.container_id
        """;

    private static ReverifyKeys ReadReverifyKeys(SqliteDataReader reader)
        => new(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2), (AssetType)reader.GetInt32(3), reader.GetInt64(4),
            reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8));

    /// <summary>复核判据：与旧 <c>AssetSearchService.MatchesText</c> 同口径 ——
    /// 显示路径 ∪ LogicalPath ∪ SourcePath，大小写不敏感。
    /// <para>拼接一律走 <see cref="AssetDisplay"/> 的两个 <c>CacheRow*</c> 函数，
    /// 与派生层写入检索索引时用的是同一份实现，所以「索引命中」与「复核通过」
    /// 不可能因为拼接口径不同而打架。</para></summary>
    private static bool MatchesReverifyKeys(in ReverifyKeys row, string needle)
        => AssetDisplay.CacheRowDisplayPath(row.TypeId, row.Type, row.PathId, row.ContainerEntry)
               .Contains(needle, StringComparison.OrdinalIgnoreCase)
        || AssetDisplay.CacheRowLogicalPath(row.OuterKey, row.InnerKey, row.Container, row.PathId, row.TypeId)
               .Contains(needle, StringComparison.OrdinalIgnoreCase)
        || row.SourcePath.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private const string PageColumns = """
        SELECT k.r,
               b.data_path, b.size, b.mtime_ticks, b.outer_key, b.inner_key, b.static_bundle,
               a.bundle_index, s.value, a.path_id, a.type_id, a.type, a.size, bs.value, a.container_entry, a.static_kind
        FROM catalog_rank k
        JOIN assets a ON a.bundle_id = k.bundle_id AND a.bundle_index = k.bundle_index
        JOIN bundles b ON b.id = a.bundle_id
        JOIN strings s ON s.id = a.container_id
        LEFT JOIN strings bs ON bs.id = a.baseline_id
        """;

    private static UnityCacheIndexBundle ReadBundle(SqliteDataReader reader, int offset)
        => new(reader.GetString(offset), reader.GetInt64(offset + 1), reader.GetInt64(offset + 2),
            reader.GetString(offset + 3), reader.GetString(offset + 4), (StaticKind)reader.GetInt32(offset + 5));

    /// <summary>读一行索引资产（<c>offset</c> 指向 <c>bundle_index</c>；<c>static_kind</c> 紧跟
    /// <c>container_entry</c>，所以它就是 <c>offset+7</c>）。</summary>
    private static UnityCacheIndexRow ReadRow(SqliteDataReader reader, int offset)
        => new(reader.GetInt32(offset), reader.GetString(offset + 1), reader.GetInt64(offset + 2),
            reader.GetInt32(offset + 3), (AssetType)reader.GetInt32(offset + 4), reader.GetInt64(offset + 5),
            reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6),
            reader.IsDBNull(offset + 7) ? null : reader.GetString(offset + 7),
            (StaticKind)reader.GetInt32(offset + 8));

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
