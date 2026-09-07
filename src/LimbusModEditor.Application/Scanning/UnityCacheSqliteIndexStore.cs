using Microsoft.Data.Sqlite;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Scanning;

/// <summary>一条索引资产的行数据（与 bundle 内对象一一对应）。</summary>
public sealed record UnityCacheIndexRow(
    int BundleIndex, string Container, long PathId, int TypeId, AssetType Type, long Size, string? Baseline);

/// <summary>一个 bundle 的索引快照（新鲜度检查 + 资产行）。</summary>
public sealed record UnityCacheIndexBundle(
    string DataPath, long Size, long MTimeUtcTicks, string Outer, string Inner);

/// <summary>
/// 扫描索引的 SQLite 存储（阶段 C 性能改造）：替代原「一个 164MB JSON 全文件
/// 重写」的索引缓存。热扫描/回灌从全量 JSON 解析（≈100s）降为流式 SQL 读取
/// （秒级），扫描写回只增删变化的 bundle，不再整文件重写。
/// 表结构：<c>bundles</c>（新鲜度 + 归属键）、<c>assets</c>（逐对象行）。
/// 索引损坏只影响速度不影响正确性：整库删掉重建即可（冷扫描 ≈20s）。
/// </summary>
public sealed class UnityCacheSqliteIndexStore
{
    private readonly string _connectionString;
    private readonly string _dbFile;

    public UnityCacheSqliteIndexStore(string dbFile)
    {
        _dbFile = Path.GetFullPath(dbFile);
        var directory = Path.GetDirectoryName(_dbFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbFile, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }

    /// <summary>数据库文件是否已存在（不存在 = 需要冷扫描建库）。</summary>
    public bool Exists => File.Exists(_dbFile);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>建表（幂等）。写连接顺带开启 WAL，提升并发读与批量写表现。</summary>
    public void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS bundles (
                data_path   TEXT PRIMARY KEY,
                size        INTEGER NOT NULL,
                mtime_ticks INTEGER NOT NULL,
                outer_key   TEXT NOT NULL,
                inner_key   TEXT NOT NULL
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS assets (
                data_path   TEXT NOT NULL,
                bundle_index INTEGER NOT NULL,
                container   TEXT NOT NULL,
                path_id     INTEGER NOT NULL,
                type_id     INTEGER NOT NULL,
                type        INTEGER NOT NULL,
                size        INTEGER NOT NULL,
                baseline    TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_assets_bundle ON assets(data_path, bundle_index);
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>一次性持久化：prune（收缩到本次枚举的条目）+ 全部变化 bundle
    /// 的 upsert 在**单个事务**里完成（阶段 C2：此前每 bundle 一个事务，
    /// 1459 次 WAL fsync 把全缓存冷扫描拖到 ≈290s；合并后 fsync 一次，
    /// synchronous=NORMAL 在 WAL 下不逐事务刷盘——索引可整库重建，安全）。</summary>
    public void PersistAll(
        IReadOnlyList<UnityCacheScanEntry> currentEntries,
        IEnumerable<(UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow> Rows)> changedBundles)
    {
        using var connection = Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA synchronous=NORMAL";
            pragma.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();
        try
        {
            var keep = currentEntries.Select(x => x.DataPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stale = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT data_path FROM bundles";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var path = reader.GetString(0);
                    if (!keep.Contains(path)) stale.Add(path);
                }
            }
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM assets WHERE data_path = $p; DELETE FROM bundles WHERE data_path = $p;";
            var deleteParam = delete.Parameters.Add("$p", SqliteType.Text);

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO assets (data_path, bundle_index, container, path_id, type_id, type, size, baseline)
                VALUES ($p, $i, $c, $pid, $tid, $t, $s, $b)
                """;
            var p = insert.Parameters.Add("$p", SqliteType.Text);
            var i = insert.Parameters.Add("$i", SqliteType.Integer);
            var c = insert.Parameters.Add("$c", SqliteType.Text);
            var pid = insert.Parameters.Add("$pid", SqliteType.Integer);
            var tid = insert.Parameters.Add("$tid", SqliteType.Integer);
            var t = insert.Parameters.Add("$t", SqliteType.Integer);
            var s = insert.Parameters.Add("$s", SqliteType.Integer);
            var b = insert.Parameters.Add("$b", SqliteType.Text);

            using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO bundles (data_path, size, mtime_ticks, outer_key, inner_key)
                VALUES ($p, $s, $m, $o, $in)
                ON CONFLICT(data_path) DO UPDATE SET
                    size = $s, mtime_ticks = $m, outer_key = $o, inner_key = $in
                """;
            var up = upsert.Parameters.Add("$p", SqliteType.Text);
            var us = upsert.Parameters.Add("$s", SqliteType.Integer);
            var um = upsert.Parameters.Add("$m", SqliteType.Integer);
            var uo = upsert.Parameters.Add("$o", SqliteType.Text);
            var uin = upsert.Parameters.Add("$in", SqliteType.Text);

            foreach (var path in stale)
            {
                deleteParam.Value = path;
                delete.ExecuteNonQuery();
            }
            foreach (var (bundle, rows) in changedBundles)
            {
                deleteParam.Value = bundle.DataPath;
                delete.ExecuteNonQuery();
                foreach (var row in rows)
                {
                    p.Value = bundle.DataPath;
                    i.Value = row.BundleIndex;
                    c.Value = row.Container;
                    pid.Value = row.PathId;
                    tid.Value = row.TypeId;
                    t.Value = (int)row.Type;
                    s.Value = row.Size;
                    b.Value = (object?)row.Baseline ?? DBNull.Value;
                    insert.ExecuteNonQuery();
                }
                up.Value = bundle.DataPath;
                us.Value = bundle.Size;
                um.Value = bundle.MTimeUtcTicks;
                uo.Value = bundle.Outer;
                uin.Value = bundle.Inner;
                upsert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>一次查询载入全部 bundle 元数据（1459 行级别，毫秒级）。
    /// 扫描的新鲜度检查用它做内存字典查找——实测逐 bundle 开连接查询
    /// （1459 次）会因连接建立开销把并行解析阶段拖慢一个数量级。</summary>
    public Dictionary<string, UnityCacheIndexBundle> ReadBundleIndex(StringComparer comparer)
    {
        var index = new Dictionary<string, UnityCacheIndexBundle>(comparer);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data_path, size, mtime_ticks, outer_key, inner_key FROM bundles";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var bundle = new UnityCacheIndexBundle(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4));
            index[bundle.DataPath] = bundle;
        }
        return index;
    }

    /// <summary>单条流式查询按 bundle 分组读出全部资产行（热扫描合并用；
    /// 只在有索引命中的 bundle 时调用）。冷扫描完全不需要。
    /// 不带 ORDER BY：行按 bundle 连续插入，按键聚合即可，省掉 119 万行的
    /// 排序（组内顺序无关紧要，bundle_index 随行存储）。</summary>
    public Dictionary<string, List<UnityCacheIndexRow>> ReadAllRowsGrouped(StringComparer comparer)
    {
        var grouped = new Dictionary<string, List<UnityCacheIndexRow>>(comparer);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT data_path, bundle_index, container, path_id, type_id, type, size, baseline
            FROM assets
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var dataPath = reader.GetString(0);
            if (!grouped.TryGetValue(dataPath, out var rows))
                grouped[dataPath] = rows = [];
            rows.Add(new UnityCacheIndexRow(
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt32(4),
                (AssetType)reader.GetInt32(5),
                reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return grouped;
    }

    /// <summary>整库读出（回灌用）： bundles 全表 + assets 按 data_path 聚合，
    /// 不带 ORDER BY（省掉 119 万行排序；组内顺序无关紧要，bundle_index 随行
    /// 存储，聚合按键分组不依赖物理顺序）。调用方逐 bundle 重建 AssetRecord。</summary>
    public IEnumerable<(UnityCacheIndexBundle Bundle, IReadOnlyList<UnityCacheIndexRow> Rows)> ReadAll()
    {
        using var connection = Open();
        var bundles = new List<UnityCacheIndexBundle>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT data_path, size, mtime_ticks, outer_key, inner_key FROM bundles";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                bundles.Add(new UnityCacheIndexBundle(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4)));
        }
        var grouped = ReadAllRowsGrouped(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in bundles)
        {
            var rows = grouped.TryGetValue(bundle.DataPath, out var found)
                ? found
                : (IReadOnlyList<UnityCacheIndexRow>)Array.Empty<UnityCacheIndexRow>();
            yield return (bundle, rows);
        }
    }
}
