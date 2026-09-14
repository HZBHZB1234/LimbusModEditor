using LimbusModEditor.Domain.Assets;
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
/// </summary>
public sealed class UnityCacheSqliteIndexStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    public const int SchemaVersion = 2;
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
                    command.CommandText = "DROP TABLE IF EXISTS assets; DROP TABLE IF EXISTS bundles; DROP TABLE IF EXISTS strings;";
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
}
