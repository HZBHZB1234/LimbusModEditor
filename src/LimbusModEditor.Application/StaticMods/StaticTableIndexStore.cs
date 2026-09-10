using System.Diagnostics;
using System.Globalization;
using LimbusModEditor.Application.Caching;
using Microsoft.Data.Sqlite;

namespace LimbusModEditor.Application.StaticMods;

/// <summary>
/// plan-12：<c>cache/static-tables.db</c> 的读写（静态表元数据 + 有界按需正文缓存）。
///
/// <para><b>为什么必须缓存</b>：枚举静态 bundle 的 1392 张表要读<b>全部 TextAsset 正文</b>
/// （<c>StaticBundleLocator.ReadTextAssetEntries</c> 一次性把正文都读进内存），
/// 每次进页面重来一遍既慢又占内存。索引只存元数据（名称/分组/大小/是否 UTF-8），
/// 进页面直接读库。</para>
///
/// <para><b>为什么正文要「有界按需」</b>：1392 张表正文合计是 GB 级，全量落库不可接受。
/// 因此只在<b>首次打开某表</b>时把它的正文写进 <c>documents</c>，并按总字节上限
/// （默认 64 MB，<see cref="DefaultDocumentCacheLimitBytes"/>）淘汰最久未用的行；
/// 表内键/值全表搜索只覆盖已缓存的那部分，其余靠现场解码（后台 + 可取消）。</para>
///
/// <para><b>失效规则</b>：<c>index_meta.source_key</c> = 内层内容哈希
/// （<c>static_s1_0_assets_all_&lt;32hex&gt;</c> 的 32hex）。内容变 → 哈希变 → 整库重建；
/// 外层键与缓存路径<b>不作缓存键</b>（热修后外层键会重新分配，拿它当键会误判换源）。
/// 编辑器绝不写 catalog / 缓存 / 游戏目录。</para>
/// </summary>
public sealed class StaticTableIndexStore
{
    /// <summary><c>tables</c> 表名（与 <see cref="WorkbenchCacheSchema.StaticTablesSql"/> 一致）。</summary>
    public const string TablesTable = "tables";

    /// <summary><c>documents</c> 表名。</summary>
    public const string DocumentsTable = "documents";

    /// <summary>正文缓存总字节上限（默认 64 MB；超限淘汰最久未打开的表）。</summary>
    public const long DefaultDocumentCacheLimitBytes = 64L * 1024 * 1024;

    private readonly SqliteTableCache _cache;
    private readonly long _documentCacheLimitBytes;

    /// <param name="cacheDirectory">缓存目录（<c>AppEnvironment.CacheDirectory</c>）。</param>
    /// <param name="documentCacheLimitBytes">正文缓存上限（≤ 0 表示不限，测试用）。</param>
    public StaticTableIndexStore(string cacheDirectory, long documentCacheLimitBytes = DefaultDocumentCacheLimitBytes)
        : this(SqliteTableCache.Create(WorkbenchCacheKind.StaticTables, cacheDirectory), documentCacheLimitBytes)
    {
    }

    /// <summary>直接注入底座（单测用临时目录建库）。</summary>
    public StaticTableIndexStore(SqliteTableCache cache, long documentCacheLimitBytes = DefaultDocumentCacheLimitBytes)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _documentCacheLimitBytes = documentCacheLimitBytes;
    }

    /// <summary>库文件路径。</summary>
    public string DatabasePath => _cache.DbFile;

    /// <summary>轻量迁移：<c>documents.cached_ticks</c> 是后加的列（正文缓存的 LRU 淘汰依据）。
    /// 旧库补列；补不上（只读/被占用）时淘汰退化为「按插入顺序」，不影响正确性。</summary>
    private void EnsureDocumentTicksColumn()
        => _cache.EnsureColumn(DocumentsTable, "cached_ticks", "INTEGER NOT NULL DEFAULT 0");

    /// <summary>库文件是否存在。</summary>
    public bool Exists => _cache.Exists;

    /// <summary>本实例（或底层库）是否因损坏而执行过删库重建。</summary>
    public bool WasRecreated => _cache.WasRecreated;

    /// <summary>正文缓存上限（诊断/提示用）。</summary>
    public long DocumentCacheLimitBytes => _documentCacheLimitBytes;

    // ── 源签名 ───────────────────────────────────────────────────────

    /// <summary>库里记录的是不是这个源（内层内容哈希一致）。</summary>
    public bool MatchesSource(StaticIndexSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _cache.EnsureSchema();
        return _cache.MatchesSource(source.SourceKey, source.ContentSignature);
    }

    /// <summary>
    /// 保证库按当前源建立：<b>源（内层哈希）不一致即整库重建</b>（清空业务表 + 旧 index_meta），
    /// 返回 true（调用方必须重新枚举全部表）；源一致时只更新内容签名并返回 false
    /// （—— <c>__data</c> 的 mtime 变化可能只是被重新下载/校验，不代表表的集合变了，
    /// 因此不整体重建，交给调用方按需决定是否重建）。</summary>
    public bool EnsureSource(StaticIndexSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _cache.EnsureSchema(); // 首次使用（库还不存在）时先建表，否则 index_meta 不存在
        var storedKey = ReadSourceKeyRow();
        if (storedKey is null || !string.Equals(storedKey, source.SourceKey, StringComparison.OrdinalIgnoreCase))
        {
            _cache.ClearTables();
            _cache.WriteSourceSignature(source.SourceKey, source.ContentSignature);
            return true;
        }
        if (!_cache.MatchesSource(source.SourceKey, source.ContentSignature))
            _cache.WriteSourceSignature(source.SourceKey, source.ContentSignature);
        return false;
    }

    /// <summary>库里记录的源标识（内层哈希）；没有记录返回 null。</summary>
    public string? ReadSourceKey()
    {
        _cache.EnsureSchema();
        return ReadSourceKeyRow();
    }

    private string? ReadSourceKeyRow()
        => _cache.Read(static connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT source_key FROM index_meta LIMIT 1";
            var value = command.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToString(value);
        });

    // ── 读 ───────────────────────────────────────────────────────────

    /// <summary>读全部表的元数据（按 dataClass + 表名排序）。库为空时返回空列表。</summary>
    public IReadOnlyList<StaticTableEntry> ReadEntries()
    {
        var watch = Stopwatch.StartNew();
        var entries = ReadEntriesCore();
        watch.Stop();
        _lastReadElapsed = watch.Elapsed;
        return entries;
    }

    private TimeSpan _lastReadElapsed;

    /// <summary>上次 <see cref="ReadEntries"/> 的耗时（页面性能提示用）。</summary>
    public TimeSpan LastReadElapsed => _lastReadElapsed;

    private IReadOnlyList<StaticTableEntry> ReadEntriesCore()
    {
        _cache.EnsureSchema();
        return _cache.Read(static connection =>
        {
            var rows = new List<StaticTableEntry>();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT container_entry, name, data_class, serialized_file, path_id, size_bytes, is_utf8, cached_ticks
                FROM tables ORDER BY data_class, name
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var containerEntry = reader.GetString(0);
                var name = reader.GetString(1);
                rows.Add(new StaticTableEntry(
                    containerEntry,
                    name,
                    reader.IsDBNull(2) ? "未分组" : reader.GetString(2),
                    DeriveFileName(containerEntry, name),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6) != 0,
                    reader.GetInt64(7)));
            }
            return (IReadOnlyList<StaticTableEntry>)rows;
        });
    }

    /// <summary>库里的表数。</summary>
    public int ReadTableCount()
    {
        _cache.EnsureSchema();
        return _cache.Read(static connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {TablesTable}";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        });
    }

    /// <summary>正文缓存的行数与总字节数（诊断/清理入口用）。</summary>
    public (int Count, long Bytes) ReadDocumentCacheUsage()
    {
        _cache.EnsureSchema();
        return _cache.Read(static connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*), coalesce(sum(size_bytes), 0) FROM {DocumentsTable}";
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return (0, 0L);
            return (reader.GetInt32(0), reader.GetInt64(1));
        });
    }

    /// <summary>读某表的正文缓存（没有则返回 null，调用方现场解码）。</summary>
    public string? TryReadDocument(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _cache.EnsureSchema();
        return _cache.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT text FROM {DocumentsTable} WHERE container_entry = $key";
            command.Parameters.AddWithValue("$key", key);
            var value = command.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToString(value);
        });
    }

    /// <summary>已缓存正文的表的键集合（表内搜索用：只对已缓存的表走缓存）。</summary>
    public IReadOnlySet<string> ReadCachedDocumentKeys()
    {
        _cache.EnsureSchema();
        return _cache.Read(static connection =>
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT container_entry FROM {DocumentsTable}";
            using var reader = command.ExecuteReader();
            while (reader.Read()) keys.Add(reader.GetString(0));
            return (IReadOnlySet<string>)keys;
        });
    }

    /// <summary>从容器路径推导表名（.staticmod 的 <c>file</c> 字段口径：容器文件名去扩展名）。
    /// 无容器时退回 m_Name 里 '/' 之后的部分——与
    /// <c>StaticBundleLocator.SplitTableIdentity</c> 的事实口径一致，
    /// 但索引里只存了 dataClass，因此这里按同一规则重建 file 名（不产生新的分类规则）。</summary>
    private static string DeriveFileName(string containerEntry, string name)
    {
        var segments = (containerEntry ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length >= 2)
        {
            var fileName = Path.GetFileNameWithoutExtension(segments[^1]);
            if (!string.IsNullOrWhiteSpace(fileName)) return fileName;
        }
        var separator = name.IndexOf('/');
        return separator > 0 ? name[(separator + 1)..] : name;
    }

    // ── 写 ───────────────────────────────────────────────────────────

    /// <summary>单事务写入全部表的元数据（整表重建语义：先清空再写）。</summary>
    public void PersistEntries(StaticIndexSource source, IReadOnlyList<StaticTableEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entries);
        _cache.EnsureSchema();
        var now = DateTimeOffset.UtcNow.UtcTicks;
        _cache.Write((connection, transaction) =>
        {
            ApplyWritePragmas(connection, transaction);
            Execute(connection, transaction, $"DELETE FROM {TablesTable}");
            using (var insertMeta = Command(connection, transaction, """
                INSERT INTO index_meta (source_key, signature) VALUES ($key, $signature)
                ON CONFLICT(source_key) DO UPDATE SET signature = $signature
                """))
            {
                insertMeta.Parameters.AddWithValue("$key", source.SourceKey);
                insertMeta.Parameters.AddWithValue("$signature", source.ContentSignature);
                insertMeta.ExecuteNonQuery();
            }

            using var insert = Command(connection, transaction, $"""
                INSERT INTO {TablesTable}
                    (container_entry, name, data_class, serialized_file, path_id, size_bytes, is_utf8, cached_ticks)
                VALUES ($key, $name, $class, $file, $pathId, $size, $utf8, $ticks)
                """);
            var key = insert.Parameters.Add("$key", SqliteType.Text);
            var name = insert.Parameters.Add("$name", SqliteType.Text);
            var dataClass = insert.Parameters.Add("$class", SqliteType.Text);
            var serializedFile = insert.Parameters.Add("$file", SqliteType.Text);
            var pathId = insert.Parameters.Add("$pathId", SqliteType.Integer);
            var size = insert.Parameters.Add("$size", SqliteType.Integer);
            var isUtf8 = insert.Parameters.Add("$utf8", SqliteType.Integer);
            var ticks = insert.Parameters.Add("$ticks", SqliteType.Integer);
            foreach (var entry in entries)
            {
                key.Value = entry.Key;
                name.Value = entry.Name;
                dataClass.Value = entry.DataClass;
                serializedFile.Value = entry.SerializedFile;
                pathId.Value = entry.PathId;
                size.Value = entry.SizeBytes;
                isUtf8.Value = entry.IsUtf8 ? 1 : 0;
                ticks.Value = entry.CachedTicks != 0 ? entry.CachedTicks : now;
                insert.ExecuteNonQuery();
            }
        });
    }

    /// <summary>
    /// 写入某表的正文缓存，并按总字节上限淘汰最久未打开的行（<c>cached_ticks</c> 升序）。
    /// <paramref name="text"/> 为 null 时不写（非 UTF-8 表不进正文缓存）。
    /// </summary>
    public void PersistDocument(string key, string? text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (text is null) return;
        _cache.EnsureSchema();
        EnsureDocumentTicksColumn();
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        _cache.Write((connection, transaction) =>
        {
            ApplyWritePragmas(connection, transaction);
            using (var upsert = Command(connection, transaction, $"""
                INSERT INTO {DocumentsTable} (container_entry, text, size_bytes, cached_ticks)
                VALUES ($key, $text, $size, $ticks)
                ON CONFLICT(container_entry) DO UPDATE SET text = $text, size_bytes = $size, cached_ticks = $ticks
                """))
            {
                upsert.Parameters.AddWithValue("$key", key);
                upsert.Parameters.AddWithValue("$text", text);
                upsert.Parameters.AddWithValue("$size", bytes);
                upsert.Parameters.AddWithValue("$ticks", now);
                upsert.ExecuteNonQuery();
            }
            if (_documentCacheLimitBytes > 0) PruneDocuments(connection, transaction);
        });
    }

    /// <summary>清空正文缓存（保留元数据）。「清空正文缓存」入口用。</summary>
    public void ClearDocuments()
    {
        _cache.EnsureSchema();
        _cache.Write((connection, transaction) =>
        {
            ApplyWritePragmas(connection, transaction);
            Execute(connection, transaction, $"DELETE FROM {DocumentsTable}");
        });
    }

    /// <summary>整库删除（含边车文件）。</summary>
    public void DeleteDatabase() => _cache.DeleteDatabase();

    /// <summary>清空业务表但保留 index_meta（诊断用）。</summary>
    public void ClearTables() => _cache.ClearTables();

    // ── 内部工具 ─────────────────────────────────────────────────────

    /// <summary>超限时按「最久未打开」淘汰到上限的 80%（留余量避免每次写都淘汰）。</summary>
    private void PruneDocuments(SqliteConnection connection, SqliteTransaction transaction)
    {
        long total;
        using (var sum = Command(connection, transaction, $"SELECT coalesce(sum(size_bytes), 0) FROM {DocumentsTable}"))
            total = Convert.ToInt64(sum.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (total <= _documentCacheLimitBytes) return;

        var target = (long)(_documentCacheLimitBytes * 0.8);
        using var victims = Command(connection, transaction, $"""
            SELECT container_entry, size_bytes FROM {DocumentsTable} ORDER BY cached_ticks ASC
            """);
        using var reader = victims.ExecuteReader();
        using var delete = Command(connection, transaction, $"DELETE FROM {DocumentsTable} WHERE container_entry = $key");
        var parameter = delete.Parameters.Add("$key", SqliteType.Text);
        var removed = new List<string>();
        while (total > target && reader.Read())
        {
            var key = reader.GetString(0);
            total -= reader.GetInt64(1);
            removed.Add(key);
        }
        reader.Close();
        foreach (var key in removed)
        {
            parameter.Value = key;
            delete.ExecuteNonQuery();
        }
    }

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

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        command.ExecuteNonQuery();
    }
}
