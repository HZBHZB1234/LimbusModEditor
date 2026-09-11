using LimbusModEditor.Application.Caching;

namespace LimbusModEditor.Domain.Tests;

/// <summary>plan-09：表缓存底座——建表幂等 / 单事务批量写往返 / 损坏库删库重建 /
/// index_meta 源变整库重建 / 三个库的表结构就位。</summary>
public class SqliteTableCacheTests : IDisposable
{
    private const string ItemsSchema = """
        CREATE TABLE IF NOT EXISTS items (
            id   INTEGER PRIMARY KEY,
            name TEXT NOT NULL
        );
        """;

    private readonly string _root;

    public SqliteTableCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lme-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (Exception) { /* temp cleanup */ }
    }

    private string DbPath(string name = "test.db") => Path.Combine(_root, "cache", name);

    private SqliteTableCache CreateStore(string name = "test.db")
        => new(DbPath(name), WorkbenchCacheSchema.IndexMetaSql + "\n" + ItemsSchema);

    private static void InsertItems(SqliteTableCache store, int count, bool failHalfway = false)
    {
        store.Write((connection, transaction) =>
        {
            for (var i = 0; i < count; i++)
            {
                if (failHalfway && i == count / 2) throw new InvalidOperationException("故意失败（验证事务回滚）");
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO items (id, name) VALUES ($id, $name)";
                command.Parameters.AddWithValue("$id", i);
                command.Parameters.AddWithValue("$name", "item-" + i);
                command.ExecuteNonQuery();
            }
        });
    }

    private static int CountItems(SqliteTableCache store)
        => store.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM items";
            return Convert.ToInt32(command.ExecuteScalar());
        });

    // ── 建表幂等 ────────────────────────────────────────────────────

    [Fact]
    public void EnsureSchema_is_idempotent_and_creates_the_file()
    {
        var store = CreateStore();
        store.EnsureSchema();
        Assert.True(store.Exists);
        store.EnsureSchema(); // 第二次不抛、不重复建
        store.EnsureSchema();
        Assert.Equal(0, CountItems(store));
        Assert.False(store.WasRecreated);
    }

    [Theory]
    [InlineData(WorkbenchCacheKind.BankIndex, "banks", "samples")]
    [InlineData(WorkbenchCacheKind.StaticTables, "tables", "documents")]
    [InlineData(WorkbenchCacheKind.TextIndex, "files", "hits")]
    public void Each_cache_kind_creates_its_documented_tables(WorkbenchCacheKind kind, string table1, string table2)
    {
        var cacheDirectory = WorkbenchCachePaths.CacheDirectory(_root);
        var store = SqliteTableCache.Create(kind, cacheDirectory);
        store.EnsureSchema();

        var tables = store.Read(connection =>
        {
            var names = new List<string>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
            return names;
        });
        Assert.Contains(WorkbenchCacheSchema.IndexMetaTable, tables);
        Assert.Contains(table1, tables);
        Assert.Contains(table2, tables);
        Assert.Equal(WorkbenchCacheSchema.BusinessTables(kind),
            WorkbenchCacheSchema.BusinessTables(kind).Where(tables.Contains).ToList());
    }

    [Fact]
    public void Documented_columns_exist_for_plan_10_11_12()
    {
        var cacheDirectory = WorkbenchCachePaths.CacheDirectory(_root);
        // bank-index.db：banks / samples 的字段（plan-11 §2）
        var bank = SqliteTableCache.Create(WorkbenchCacheKind.BankIndex, cacheDirectory);
        bank.EnsureSchema();
        Assert.Equal(
            new[] { "path", "name", "size_bytes", "mtime_ticks", "kind", "kind_note", "fsb_count", "note", "scanned_ticks" },
            ColumnsOf(bank, "banks"));
        Assert.Equal(
            new[] { "bank_path", "fsb_index", "sample_index", "name", "codec_name", "sample_rate", "channels",
                    "sample_count", "data_size", "data_offset" },
            ColumnsOf(bank, "samples"));

        // static-tables.db：tables / documents（plan-12 §2）
        var statics = SqliteTableCache.Create(WorkbenchCacheKind.StaticTables, cacheDirectory);
        statics.EnsureSchema();
        Assert.Equal(
            new[] { "container_entry", "name", "data_class", "serialized_file", "path_id", "size_bytes", "is_utf8", "cached_ticks" },
            ColumnsOf(statics, "tables"));
        Assert.Equal(new[] { "container_entry", "text", "size_bytes" }, ColumnsOf(statics, "documents"));

        // text-index.db：files / hits（plan-10 §2）
        var text = SqliteTableCache.Create(WorkbenchCacheKind.TextIndex, cacheDirectory);
        text.EnsureSchema();
        Assert.Equal(new[] { "rel_path", "size", "mtime_ticks", "key_count", "is_utf8" },
            ColumnsOf(text, "files"));
        Assert.Equal(new[] { "rel_path", "kind", "key_path", "snip", "seq" }, ColumnsOf(text, "hits"));

        // 三个库都落在程序目录的 cache/ 下
        foreach (var path in WorkbenchCachePaths.AllDatabasePaths(cacheDirectory))
        {
            Assert.True(File.Exists(path), $"缓存库应已建好：{path}");
            Assert.Equal(cacheDirectory, Path.GetDirectoryName(path));
        }
    }

    private static IReadOnlyList<string> ColumnsOf(SqliteTableCache store, string table)
        => store.Read(connection =>
        {
            var columns = new List<string>();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = command.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
            return columns;
        });

    // ── 单事务批量写往返 ─────────────────────────────────────────────

    [Fact]
    public void Transactional_write_round_trips()
    {
        var store = CreateStore();
        store.EnsureSchema();
        InsertItems(store, 200);
        Assert.Equal(200, CountItems(store));

        // 重新打开（新实例）也能读到 —— 证明真的落盘了
        var reopened = CreateStore();
        Assert.Equal(200, CountItems(reopened));
        var name = reopened.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM items WHERE id = 199";
            return Convert.ToString(command.ExecuteScalar());
        });
        Assert.Equal("item-199", name);
    }

    [Fact]
    public void Transactional_write_rolls_back_completely_on_failure()
    {
        var store = CreateStore();
        store.EnsureSchema();
        Assert.Throws<InvalidOperationException>(() => InsertItems(store, 100, failHalfway: true));
        // 单事务：一半也不落库
        Assert.Equal(0, CountItems(store));
    }

    // ── 损坏库 / 删除后自动重建 ──────────────────────────────────────

    [Fact]
    public void Truncated_database_is_deleted_and_recreated()
    {
        var store = CreateStore();
        store.EnsureSchema();
        InsertItems(store, 10);

        // 模拟「库文件被写坏」：截断到前 120 字节
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(DbPath());
        File.WriteAllBytes(DbPath(), bytes[..120]);

        var reopened = CreateStore();
        reopened.EnsureSchema(); // 不抛：删库重建
        Assert.True(reopened.WasRecreated);
        Assert.True(reopened.Exists);
        Assert.Equal(0, CountItems(reopened)); // 旧数据没了（索引只影响速度）
        InsertItems(reopened, 3);              // 重建后立即可用
        Assert.Equal(3, CountItems(reopened));
    }

    [Fact]
    public void Garbage_database_file_is_deleted_and_recreated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath())!);
        File.WriteAllText(DbPath(), "这不是一个 SQLite 数据库，只是一段文本。");

        var store = CreateStore();
        store.EnsureSchema();
        Assert.True(store.WasRecreated);
        InsertItems(store, 5);
        Assert.Equal(5, CountItems(store));
    }

    [Fact]
    public void Deleted_database_file_is_recreated_on_next_use()
    {
        var store = CreateStore();
        store.EnsureSchema();
        InsertItems(store, 7);
        store.DeleteDatabase();
        Assert.False(File.Exists(DbPath()));

        var reopened = CreateStore();
        reopened.EnsureSchema();
        Assert.True(reopened.Exists);
        InsertItems(reopened, 2);
        Assert.Equal(2, CountItems(reopened));
    }

    // ── index_meta：源变即整库重建 ──────────────────────────────────

    [Fact]
    public void Same_source_and_signature_keeps_the_cache()
    {
        var store = CreateStore();
        Assert.True(store.EnsureSource("D:/game/banks", "100:200"));
        InsertItems(store, 4);
        Assert.True(store.MatchesSource("D:/game/banks", "100:200"));

        Assert.False(store.EnsureSource("D:/game/banks", "100:200")); // 命中：不重建
        Assert.Equal(4, CountItems(store));
    }

    [Fact]
    public void Changed_source_key_rebuilds_the_whole_cache()
    {
        var store = CreateStore();
        store.EnsureSource("D:/game/banks", "100:200");
        InsertItems(store, 4);

        // 共享配置改了游戏目录 → 源变 → 整库重建，旧数据不残留
        Assert.True(store.EnsureSource("E:/other/banks", "100:200"));
        Assert.Equal(0, CountItems(store));
        Assert.False(store.MatchesSource("D:/game/banks", "100:200"));
        Assert.True(store.MatchesSource("E:/other/banks", "100:200"));
    }

    [Fact]
    public void Changed_signature_rebuilds_the_whole_cache()
    {
        var store = CreateStore();
        store.EnsureSource("D:/game/banks", "100:200");
        InsertItems(store, 4);

        Assert.True(store.EnsureSource("D:/game/banks", "100:999")); // 签名变（文件被热修/更新）
        Assert.Equal(0, CountItems(store));
        Assert.True(store.MatchesSource("D:/game/banks", "100:999"));
    }

    [Fact]
    public void EnsureSource_writes_readable_signature_and_clear_keeps_meta()
    {
        var store = CreateStore();
        store.EnsureSource("D:/game/lang", "hash-abc");
        Assert.Equal("hash-abc", store.ReadSourceSignature("D:/game/lang"));
        Assert.Null(store.ReadSourceSignature("D:/game/unknown"));

        InsertItems(store, 6);
        store.ClearTables(); // 「清空缓存」入口：业务表清空但索引元信息保留
        Assert.Equal(0, CountItems(store));
        Assert.Equal("hash-abc", store.ReadSourceSignature("D:/game/lang"));
    }

    // ── 轻量迁移 ────────────────────────────────────────────────────

    [Fact]
    public void EnsureColumn_adds_missing_column_once()
    {
        var store = CreateStore();
        store.EnsureSchema();
        InsertItems(store, 1);

        store.EnsureColumn("items", "container_entry", "TEXT");
        store.EnsureColumn("items", "container_entry", "TEXT"); // 幂等：再来一次不抛
        Assert.Contains("container_entry", ColumnsOf(store, "items"));
        Assert.Equal(1, CountItems(store)); // 旧行保留，新列为 null
    }

    // ── 缺表自愈（真实缺陷回归：「载入 lang 文件失败：no such table: index_meta」）──

    [Fact]
    public void Read_on_a_missing_database_file_creates_the_schema_instead_of_throwing()
    {
        // 连接串是 ReadWriteCreate：以前「库文件不存在」时读路径会先建出一个空库，
        // 随后 SELECT … FROM index_meta 抛 no such table。读路径必须自己补建表。
        var store = CreateStore();
        Assert.False(File.Exists(DbPath()));

        Assert.Null(store.ReadSourceSignature("D:/game/lang"));
        Assert.True(File.Exists(DbPath()));
        Assert.Equal(0, CountItems(store)); // 业务表也建好了
    }

    [Fact]
    public void Read_on_a_zero_byte_database_file_repairs_it()
    {
        // 真实现场：程序目录 cache/text-index.db 是 0 字节（上次建库被打断），
        // 页面一读就报 no such table: index_meta。0 字节库必须被无声补建。
        var path = DbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []); // 0 字节
        Assert.Equal(0, new FileInfo(path).Length);

        var store = CreateStore();
        Assert.Null(store.ReadSourceSignature("D:/game/lang"));
        Assert.True(new FileInfo(path).Length > 0);

        store.EnsureSource("D:/game/lang", "sig-1");
        InsertItems(store, 3);
        Assert.True(store.MatchesSource("D:/game/lang", "sig-1"));
        Assert.Equal(3, CountItems(store));
    }

    [Fact]
    public void Write_to_a_database_without_tables_creates_them_first()
    {
        var path = DbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        var store = CreateStore();

        InsertItems(store, 2); // 以前这里会抛 no such table: items
        Assert.Equal(2, CountItems(store));
    }

    [Fact]
    public void Deleted_database_is_recreated_on_the_next_read()
    {
        var store = CreateStore();
        store.EnsureSource("D:/game/lang", "sig-1");
        InsertItems(store, 5);
        store.DeleteDatabase();

        // 删库后同一个实例继续读：必须重新建表（不能以为表还在）
        Assert.Null(store.ReadSourceSignature("D:/game/lang"));
        Assert.Equal(0, CountItems(store));
        InsertItems(store, 1);
        Assert.Equal(1, CountItems(store));
    }
}
