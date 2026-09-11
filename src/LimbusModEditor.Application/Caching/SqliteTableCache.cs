using Microsoft.Data.Sqlite;

namespace LimbusModEditor.Application.Caching;

/// <summary>
/// plan-09：工作台表缓存的 SQLite 底座（建表 / 轻量迁移 / 单事务批量写 / 损坏即删库重建）。
///
/// <para>复用既有惯例与依赖（<c>Microsoft.Data.Sqlite 8.0.11</c>：
/// 连接串构造、<c>EnsureColumn</c> 轻量迁移、WAL + <c>synchronous=NORMAL</c> +
/// 单事务批量写，全部照抄 <c>Scanning/UnityCacheSqliteIndexStore</c>，那份实现已在
/// 119 万行级别的扫描索引上验证过）。</para>
///
/// <para><b>缓存只影响速度，不影响正确性</b>：任何损坏（打不开、表结构坏、被截断）
/// 一律删库重建。</para>
///
/// <para><b>编辑集绝不进这份缓存</b>：这里只存原版（vanilla）事实。</para>
/// </summary>
public sealed class SqliteTableCache
{
    private readonly string _dbFile;
    private readonly string _schemaSql;
    private readonly string _connectionString;
    private bool _schemaReady;

    /// <param name="dbFile">数据库文件路径（通常来自 <see cref="WorkbenchCachePaths"/>）。</param>
    /// <param name="schemaSql">建表脚本（<see cref="WorkbenchCacheSchema"/>；必须幂等，用
    /// <c>CREATE TABLE IF NOT EXISTS</c>）。脚本里应包含 <c>index_meta</c> 表。</param>
    public SqliteTableCache(string dbFile, string schemaSql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbFile);
        ArgumentNullException.ThrowIfNull(schemaSql);
        _dbFile = Path.GetFullPath(dbFile);
        _schemaSql = schemaSql;
        var directory = Path.GetDirectoryName(_dbFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <summary>数据库文件路径。</summary>
    public string DbFile => _dbFile;

    /// <summary>数据库文件是否存在（不存在 = 首次使用，需要建库）。</summary>
    public bool Exists => File.Exists(_dbFile);

    /// <summary>本实例是否因损坏而执行过删库重建（供 UI 提示「缓存已重建」）。</summary>
    public bool WasRecreated { get; private set; }

    /// <summary>按「程序目录/cache」下的库名与给定形态建库（后续计划的标准入口）。</summary>
    public static SqliteTableCache Create(WorkbenchCacheKind kind, string cacheDirectory)
        => new(WorkbenchCachePaths.DatabasePath(kind, cacheDirectory), WorkbenchCacheSchema.For(kind));

    // ── 建表 / 轻量迁移 ─────────────────────────────────────────────

    /// <summary>幂等建表。库损坏（打不开 / 表结构坏）时删库重建后重试一次。
    /// 调用方应在每次使用缓存前调用它（<see cref="Read"/> / <see cref="Write"/> 已内置一次）。</summary>
    public void EnsureSchema()
    {
        try
        {
            EnsureSchemaCore();
        }
        catch (SqliteException ex)
        {
            // 建表都失败的库没有挽救价值：删掉重建（缓存只影响速度）。
            RecreateOrThrow(ex);
            EnsureSchemaCore();
        }
    }

    /// <summary>
    /// 读/写前的建表保证（每个实例只做一次）。
    ///
    /// <para><b>为什么必须内建</b>：<see cref="Open"/> 用的是
    /// <c>SqliteOpenMode.ReadWriteCreate</c>，因此「库文件不存在」或「库文件是 0 字节」
    /// （上次建库被打断 / 只创建了文件）时连接照样能开，随后的
    /// <c>SELECT … FROM index_meta</c> 会抛 <c>SQLite Error 1: 'no such table: index_meta'</c>
    /// —— 页面上表现成「载入 lang 文件失败」。缓存是纯加速旁路，
    /// <b>任何缺失都该被无声补建</b>，绝不能把缺表当业务错误抛给用户。</para>
    /// </summary>
    private void EnsureSchemaOnce()
    {
        if (_schemaReady) return;
        EnsureSchema();
        _schemaReady = true;
    }

    private void EnsureSchemaCore()
    {
        using var connection = Open();
        // 损坏探测：任何 sqlite_master 都读不出来的库在这里就暴露（不用等后续查询）。
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT count(*) FROM sqlite_master";
            probe.ExecuteScalar();
        }
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;\n" + _schemaSql;
        command.ExecuteNonQuery();
    }

    /// <summary>轻量迁移：列不存在则 <c>ALTER TABLE ADD COLUMN</c>；已存在（或补列失败）
    /// 静默跳过——与 <c>UnityCacheSqliteIndexStore.EnsureContainerEntryColumn</c> 同一写法。</summary>
    /// <param name="table">表名（本类自己拼进 SQL，调用方只传固定标识符，不传用户输入）。</param>
    /// <param name="column">列名。</param>
    /// <param name="columnDefinition">列定义（如 <c>"TEXT"</c> / <c>"INTEGER NOT NULL DEFAULT 0"</c>）。</param>
    public void EnsureColumn(string table, string column, string columnDefinition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnDefinition);
        EnsureSchema();
        using var connection = Open();
        EnsureColumnCore(connection, table, column, columnDefinition);
    }

    private static void EnsureColumnCore(SqliteConnection connection, string table, string column, string columnDefinition)
    {
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = $"SELECT {column} FROM {table} LIMIT 0";
            try { probe.ExecuteNonQuery(); return; }
            catch (SqliteException) { /* 缺列（或表结构坏）：走下面的 ALTER */ }
        }
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDefinition}";
        try { alter.ExecuteNonQuery(); }
        catch (SqliteException) { /* 列已存在 / 无写权限：静默跳过，旧行读出 null 由调用方兜底 */ }
    }

    // ── 连接 / 单事务批量写 / 读 ────────────────────────────────────

    /// <summary>打开一个连接（调用方负责 Dispose）。</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>单事务批量写：WAL + <c>synchronous=NORMAL</c>，全部 upsert 在一个事务里提交
    /// （照抄 <c>UnityCacheSqliteIndexStore.PersistAll</c>：逐条事务的 fsync 是性能杀手）。
    ///
    /// <para><b>委托里创建的命令必须设置 <c>command.Transaction = transaction</c></b>，
    /// 否则 Microsoft.Data.Sqlite 会抛「事务未关联」。</para>
    ///
    /// <para>库损坏（SQLITE_CORRUPT / SQLITE_NOTADB）时删库重建并抛
    /// <see cref="InvalidDataException"/>（中文提示），调用方重新加载即可。</para></summary>
    public void Write(Action<SqliteConnection, SqliteTransaction> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        EnsureSchemaOnce();
        try
        {
            WriteCore(work);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            RecreateOrThrow(ex);
            throw new InvalidDataException(
                $"缓存库已损坏，已删除并重建（{Path.GetFileName(_dbFile)}）；本次写入未生效，请重新载入数据。", ex);
        }
    }

    private void WriteCore(Action<SqliteConnection, SqliteTransaction> work)
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
            work(connection, transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>只读查询（连接用完即关，连接池复用）。库损坏时删库重建并抛
    /// <see cref="InvalidDataException"/>（中文提示），调用方重新加载即可。</summary>
    public T Read<T>(Func<SqliteConnection, T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        EnsureSchemaOnce();
        try
        {
            using var connection = Open();
            return query(connection);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            RecreateOrThrow(ex);
            throw new InvalidDataException(
                $"缓存库已损坏，已删除并重建（{Path.GetFileName(_dbFile)}）；请重新载入数据。", ex);
        }
    }

    /// <summary>SQLite 损坏类错误码（SQLITE_CORRUPT=11、SQLITE_NOTADB=26）。
    /// 其它错误（约束冲突、SQL 写错）原样抛出，避免把逻辑 bug 伪装成「缓存坏了」。</summary>
    private static bool IsCorruption(SqliteException ex) => ex.SqliteErrorCode is 11 or 26;

    // ── index_meta：本库是按哪个源建的 ──────────────────────────────

    /// <summary>读某源的存盘签名；没有记录返回 null。</summary>
    public string? ReadSourceSignature(string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        return Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT signature FROM index_meta WHERE source_key = $key";
            command.Parameters.AddWithValue("$key", sourceKey);
            var value = command.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToString(value);
        });
    }

    /// <summary>写入（覆盖）某源的签名。</summary>
    public void WriteSourceSignature(string sourceKey, string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentNullException.ThrowIfNull(signature);
        Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO index_meta (source_key, signature) VALUES ($key, $signature)
                ON CONFLICT(source_key) DO UPDATE SET signature = $signature
                """;
            command.Parameters.AddWithValue("$key", sourceKey);
            command.Parameters.AddWithValue("$signature", signature);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>库是否就是按这个源 + 这个签名建的（源换过 / 签名变过都返回 false）。</summary>
    public bool MatchesSource(string sourceKey, string signature)
    {
        var stored = ReadSourceSignature(sourceKey);
        return stored is not null && CacheSignature.Matches(stored, signature);
    }

    /// <summary>保证库是按当前源建的：源不一致 / 签名不一致 / 尚无记录时，
    /// <b>清空全部业务表与旧 index_meta</b> 并写入当前 (source_key, signature)，
    /// 返回 true（= 调用方必须重新解析源）。一致时返回 false（可直接信任缓存）。
    ///
    /// <para>这就是「源目录换（共享配置改游戏目录）即整体失效重建」的唯一实现。</para></summary>
    public bool EnsureSource(string sourceKey, string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentNullException.ThrowIfNull(signature);
        EnsureSchema();
        var sources = Read(ReadSourceRows);
        if (sources.Count == 1 && sources.TryGetValue(sourceKey, out var stored) &&
            CacheSignature.Matches(stored, signature))
        {
            return false;
        }
        Write((connection, transaction) =>
        {
            foreach (var table in ReadBusinessTables(connection, transaction))
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM \"{table}\"";
                delete.ExecuteNonQuery();
            }
            using var clearMeta = connection.CreateCommand();
            clearMeta.Transaction = transaction;
            clearMeta.CommandText = "DELETE FROM index_meta";
            clearMeta.ExecuteNonQuery();

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO index_meta (source_key, signature) VALUES ($key, $signature)";
            insert.Parameters.AddWithValue("$key", sourceKey);
            insert.Parameters.AddWithValue("$signature", signature);
            insert.ExecuteNonQuery();
        });
        return true;
    }

    /// <summary>清空全部业务表（保留 index_meta；页面提供「清空缓存」入口时用）。</summary>
    public void ClearTables()
    {
        EnsureSchema();
        Write((connection, transaction) =>
        {
            foreach (var table in ReadBusinessTables(connection, transaction))
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM \"{table}\"";
                delete.ExecuteNonQuery();
            }
        });
    }

    /// <summary>整库删除（含 WAL/SHM 边车文件）；下次 <see cref="EnsureSchema"/> 会自动重建。</summary>
    public void DeleteDatabase()
    {
        SqliteConnection.ClearAllPools(); // 池化会保留原生句柄，不清理在 Windows 上删不掉文件
        foreach (var path in SidecarFiles()) TryDelete(path);
        _schemaReady = false; // 库已删：下一次读/写要重新建表（否则会读到「刚删掉的库」的假象）
        WasRecreated = true;
    }

    // ── 内部工具 ────────────────────────────────────────────────────

    private static Dictionary<string, string> ReadSourceRows(SqliteConnection connection)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_key, signature FROM index_meta";
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows[reader.GetString(0)] = reader.GetString(1);
        return rows;
    }

    /// <summary>业务表名（除 index_meta 与 SQLite 内部表外的所有表）。</summary>
    private static List<string> ReadBusinessTables(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var tables = new List<string>();
        using var command = connection.CreateCommand();
        if (transaction is not null) command.Transaction = transaction;
        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name <> 'index_meta' AND name NOT LIKE 'sqlite_%'
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read()) tables.Add(reader.GetString(0));
        return tables;
    }

    private IEnumerable<string> SidecarFiles()
    {
        yield return _dbFile;
        yield return _dbFile + "-wal";
        yield return _dbFile + "-shm";
        yield return _dbFile + "-journal";
    }

    /// <summary>删库重建；删不掉（文件被占用）时抛中文异常并带上原始原因。</summary>
    private void RecreateOrThrow(SqliteException cause)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in SidecarFiles())
        {
            if (!TryDelete(path) && File.Exists(path))
                throw new IOException($"缓存库损坏且无法删除（可能被其他进程占用）：{path}", cause);
        }
        WasRecreated = true;
    }

    private static bool TryDelete(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
        return false;
    }
}
