using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using NLog;

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
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

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
        Log.Debug("打开表缓存：db={0} · 建前已存在={1} · 建表脚本 {2} 字符",
            Path.GetFileName(_dbFile), File.Exists(_dbFile), _schemaSql.Length);
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
            Log.Error(ex, "表缓存建表失败（删库重建后重试一次）：db={0} · 错误码={1}",
                Path.GetFileName(_dbFile), ex.SqliteErrorCode);
            RecreateOrThrow(ex);
            EnsureSchemaCore();
            Log.Warn("表缓存已删库重建：db={0}", Path.GetFileName(_dbFile));
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
        Log.Debug("表缓存建表保证完成（本实例只做一次）：db={0} · 重建过={1}",
            Path.GetFileName(_dbFile), WasRecreated);
    }

    private void EnsureSchemaCore()
    {
        using var connection = Open();
        // 损坏探测：任何 sqlite_master 都读不出来的库在这里就暴露（不用等后续查询）。
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT count(*) FROM sqlite_master";
            var tableCount = probe.ExecuteScalar();
            if (Log.IsTraceEnabled)
                Log.Trace("表缓存完整性探测：db={0} · sqlite_master 对象数={1}", Path.GetFileName(_dbFile), tableCount);
        }
        HealSchemaDrift(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;\n" + _schemaSql;
        command.ExecuteNonQuery();
        if (Log.IsDebugEnabled)
            Log.Debug("表缓存建表脚本已执行（WAL + CREATE TABLE IF NOT EXISTS）：db={0}", Path.GetFileName(_dbFile));
    }

    // ── 表结构漂移自愈 ───────────────────────────────────────────────

    /// <summary><c>CREATE TABLE IF NOT EXISTS &lt;表&gt; (&lt;列…&gt;);</c> 的解析（建表脚本是受控格式）。</summary>
    private static readonly Regex CreateTablePattern = new(
        @"CREATE\s+TABLE\s+IF\s+NOT\s+EXISTS\s+(?<table>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<body>.*?)\)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>表定义里不是列名的行首关键字（表级约束）。</summary>
    private static readonly HashSet<string> TableConstraintKeywords =
        new(StringComparer.OrdinalIgnoreCase) { "PRIMARY", "UNIQUE", "FOREIGN", "CONSTRAINT", "CHECK", "KEY" };

    /// <summary>
    /// 表结构漂移自愈：**已存在**的表的列集缺了当前脚本声明的列时，把该表丢掉重建。
    ///
    /// <para><b>为什么必须有这一层</b>：建表脚本全是 <c>CREATE TABLE IF NOT EXISTS</c>——
    /// 表一旦存在，脚本就<b>不会</b>把它的结构改成新的。只要某个库的列集变过
    /// （真实案例：关联图 v1 → v2 给 <c>subjects</c> 加了 <c>category_label</c>、给 <c>links</c>
    /// 加了预览列、新增了 <c>xref</c> 表），盘上的旧库就会以「老结构 + 新签名」的状态继续被复用，
    /// 轻则查询报 <c>no such column</c>，重则被当成新鲜缓存返回<b>空结论</b>——
    /// 缓存只该影响速度，绝不该改变结论。</para>
    ///
    /// <para><b>判据取「缺列」而不是「列集完全相等」</b>：脚本解析只要漏读一列就永远不会
    /// 触发自愈（安全方向），而「旧表缺新列」正是唯一会让读写直接失败的情形。
    /// 一旦发生漂移，连同 <c>index_meta</c> 一起清空——数据没了，新鲜度声明必须同时作废，
    /// 否则下一次仍会把空表当「源未变」复用。</para>
    /// </summary>
    private void HealSchemaDrift(SqliteConnection connection)
    {
        var expected = ExpectedColumns(_schemaSql);
        if (expected.Count == 0) return;

        var drifted = new List<string>();
        foreach (var (table, columns) in expected)
        {
            var actual = ActualColumns(connection, table);
            if (actual.Count == 0) continue;                        // 表还不存在 → 交给建表脚本
            if (columns.All(actual.Contains)) continue;             // 声明的列都在 → 无漂移
            drifted.Add(table);
        }
        if (drifted.Count == 0) return;

        using (var transaction = connection.BeginTransaction())
        {
            foreach (var table in drifted)
            {
                using var drop = connection.CreateCommand();
                drop.Transaction = transaction;
                drop.CommandText = $"DROP TABLE IF EXISTS \"{table}\"";
                drop.ExecuteNonQuery();
            }
            using var clearMeta = connection.CreateCommand();
            clearMeta.Transaction = transaction;
            clearMeta.CommandText = "DELETE FROM index_meta";
            clearMeta.ExecuteNonQuery();
            transaction.Commit();
        }
        Log.Warn("表缓存结构已过时，已丢弃重建（数据随表一起作废）：db={0} · 表={1}",
            Path.GetFileName(_dbFile), string.Join(", ", drifted));
    }

    /// <summary>从建表脚本里解析出「表 → 声明的列名」。</summary>
    private static Dictionary<string, HashSet<string>> ExpectedColumns(string script)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in CreateTablePattern.Matches(script))
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in match.Groups["body"].Value.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var tokens = line.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;
                var name = tokens[0].Trim('"', '`', '[', ']');
                if (name.Length == 0 || TableConstraintKeywords.Contains(name)) continue;
                columns.Add(name);
            }
            if (columns.Count > 0) map[match.Groups["table"].Value] = columns;
        }
        return map;
    }

    /// <summary>读一张表实际存在的列名（表不存在时返回空集）。</summary>
    private HashSet<string> ActualColumns(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        catch (SqliteException ex)
        {
            // 读不到结构（库损坏等）→ 交回「无漂移」，让既有的删库重建路径去处理。
            Log.Debug(ex, "读表结构失败（按无漂移处理）：db={0} · 表={1}", Path.GetFileName(_dbFile), table);
        }
        return columns;
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
            catch (SqliteException ex)
            {
                /* 缺列（或表结构坏）：走下面的 ALTER */
                Log.Debug(ex, "探测列失败（缺列或表结构坏，走 ALTER 补列）：{0}.{1} · 错误码={2}",
                    table, column, ex.SqliteErrorCode);
            }
        }
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDefinition}";
        try
        {
            alter.ExecuteNonQuery();
            Log.Info("表缓存轻量迁移：已为 {0} 表补列 {1} {2}", table, column, columnDefinition);
        }
        catch (SqliteException ex)
        {
            /* 列已存在 / 无写权限：静默跳过，旧行读出 null 由调用方兜底 */
            Log.Warn(ex, "补列未生效（列已存在或无写权限）：{0}.{1} {2} · 错误码={3}",
                table, column, columnDefinition, ex.SqliteErrorCode);
        }
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

    /// <summary>读某源的「活动语言前缀」（相对源根的子目录名 + <c>/</c>；没有记录返回 null）。</summary>
    public string? ReadSourceLanguagePrefix(string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        return Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT language_prefix FROM index_meta WHERE source_key = $key";
            command.Parameters.AddWithValue("$key", sourceKey);
            var value = command.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToString(value);
        });
    }

    /// <summary>库是否就是按这个源 + 这个签名建的（源换过 / 签名变过都返回 false）。</summary>
    public bool MatchesSource(string sourceKey, string signature)
    {
        var stored = ReadSourceSignature(sourceKey);
        return stored is not null && CacheSignature.Matches(stored, signature);
    }

    /// <summary>保证库是按当前源建的：源不一致 / 签名不一致 / 尚无记录时，
    /// <b>清空全部业务表与旧 index_meta</b> 并写入当前 (source_key, signature, language_prefix)，
    /// 返回 true（= 调用方必须重新解析源）。一致时返回 false（可直接信任缓存）。
    ///
    /// <para><paramref name="languagePrefix"/> 是「条目相对哪个子目录」（plan-16 §5 的活动语言
    /// 目录前缀）。它与签名一起写入、一起对账：**前缀变了同样整库重建**，因为条目口径以它为基准，
    /// 前缀与行集不匹配就会把文件路径拼到错误的位置。</para>
    ///
    /// <para><b>「源变了」只清数据、不动结构</b>：结构是否跟得上当前脚本由
    /// <see cref="HealSchemaDrift"/> 单独负责（<see cref="EnsureSchema"/> 里已经跑过）。
    /// 两件事各有单一负责人，避免「谁该负责重建」在两条路径上出现分歧。</para>
    ///
    /// <para>这就是「源目录换（共享配置改游戏目录）即整体失效重建」的唯一实现。</para></summary>
    public bool EnsureSource(string sourceKey, string signature, string languagePrefix = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(languagePrefix);
        EnsureSchema();
        var sources = Read(ReadSourceRows);
        var storedPrefix = ReadSourceLanguagePrefix(sourceKey) ?? string.Empty;
        if (sources.Count == 1 && sources.TryGetValue(sourceKey, out var stored) &&
            CacheSignature.Matches(stored, signature) &&
            string.Equals(storedPrefix, languagePrefix, StringComparison.OrdinalIgnoreCase))
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
            insert.CommandText = "INSERT INTO index_meta (source_key, signature, language_prefix) VALUES ($key, $signature, $prefix)";
            insert.Parameters.AddWithValue("$key", sourceKey);
            insert.Parameters.AddWithValue("$signature", signature);
            insert.Parameters.AddWithValue("$prefix", languagePrefix);
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

    /// <summary>index_meta 的活动语言前缀列（轻量迁移用；plan-16 §5 起）。</summary>
    public const string MetaLanguagePrefixColumn = "language_prefix";

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
