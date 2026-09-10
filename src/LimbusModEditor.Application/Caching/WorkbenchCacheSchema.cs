namespace LimbusModEditor.Application.Caching;

/// <summary>
/// plan-09：三个缓存库的表结构定义（本次就建好并建表；plan-10/11/12 只写读写逻辑）。
///
/// <para>共同契约：</para>
/// <list type="bullet">
/// <item><c>index_meta(source_key PRIMARY KEY, signature)</c>：记录「本库是按哪个源建的」。
/// source_key = 源目录 / 源 bundle 的标识；signature = 源的新鲜度签名
/// （文件/目录用 <see cref="CacheSignature"/> 的 <c>"length:mtimeTicks"</c>，内容哈希类
/// 直接用哈希文本）。<b>source_key 或 signature 变 → 整库重建</b>
/// （<see cref="SqliteTableCache.EnsureSource"/>）。</item>
/// <item>只存<b>原版（vanilla）事实</b>——编辑集（文本/静态的内存修改）绝不写进缓存，
/// 避免「缓存里是原版还是改动版」的歧义。</item>
/// <item>不做查询优化：四个库都是万级行，索引够用即可（plan-09 §7）。</item>
/// </list>
/// </summary>
public static class WorkbenchCacheSchema
{
    /// <summary>index_meta 表名（三库一致）。</summary>
    public const string IndexMetaTable = "index_meta";

    /// <summary>公共建表片段：index_meta（调用方拼接在自己的建表脚本前面）。</summary>
    public const string IndexMetaSql = """
        CREATE TABLE IF NOT EXISTS index_meta (
            source_key TEXT PRIMARY KEY,
            signature  TEXT NOT NULL
        );
        """;

    /// <summary><c>cache/bank-index.db</c>（plan-11 §2）：
    /// <c>banks</c> = 逐个 bank 文件的头信息与类型判定结果；
    /// <c>samples</c> = 逐样本行（「所有音频」总表的数据源，7 万行级别）。
    /// 失效规则：bank 的 <c>(size_bytes, mtime_ticks)</c> 与磁盘不符（或增删文件）才重新探测该文件。</summary>
    public const string BankIndexSql = """
        CREATE TABLE IF NOT EXISTS banks (
            path         TEXT PRIMARY KEY,
            name         TEXT NOT NULL,
            size_bytes   INTEGER NOT NULL,
            mtime_ticks  INTEGER NOT NULL,
            kind         TEXT NOT NULL,
            kind_note    TEXT,
            fsb_count    INTEGER NOT NULL,
            note         TEXT,
            scanned_ticks INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS samples (
            bank_path    TEXT NOT NULL,
            fsb_index    INTEGER NOT NULL,
            sample_index INTEGER NOT NULL,
            name         TEXT NOT NULL,
            codec_name   TEXT,
            sample_rate  INTEGER NOT NULL,
            channels     INTEGER NOT NULL,
            sample_count INTEGER NOT NULL,
            data_size    INTEGER NOT NULL,
            data_offset  INTEGER NOT NULL,
            PRIMARY KEY (bank_path, fsb_index, sample_index)
        );
        CREATE INDEX IF NOT EXISTS ix_samples_name ON samples(name);
        """;

    /// <summary><c>cache/static-tables.db</c>（plan-12 §2）：
    /// <c>tables</c> = 索引与搜索用的元数据（dataClass / 名称 / 大小）；
    /// <c>documents</c> = <b>按需 + 有界</b>缓存的 JSON 原文快照（只在首次打开某表时写入，
    /// 且总字节数受上限约束——1392 张表全量文本是 GB 级，绝不无界预缓存）。
    /// 失效规则：source_key（内层内容哈希）变 → 整库重建（绝不缓存 hash 常量）。</summary>
    public const string StaticTablesSql = """
        CREATE TABLE IF NOT EXISTS tables (
            container_entry TEXT PRIMARY KEY,
            name            TEXT NOT NULL,
            data_class      TEXT,
            serialized_file TEXT,
            path_id         INTEGER NOT NULL,
            size_bytes      INTEGER NOT NULL,
            is_utf8         INTEGER NOT NULL,
            cached_ticks    INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS documents (
            container_entry TEXT PRIMARY KEY,
            text            TEXT NOT NULL,
            size_bytes      INTEGER NOT NULL
        );
        """;

    /// <summary><c>cache/text-index.db</c>（plan-10 §2）：
    /// <c>files</c> = 文件级索引（代替每次进页面全目录重读），签名 = <c>(size, mtime_ticks)</c>；
    /// <c>hits</c> = 文件名/键/值命中（搜索改走 SQL，不再逐文件现解码），
    /// <c>kind ∈ {File, Key, Value}</c>。
    /// <b>不存键值行</b>：树按需只读被选中的那一个文件。</summary>
    public const string TextIndexSql = """
        CREATE TABLE IF NOT EXISTS files (
            rel_path    TEXT PRIMARY KEY,
            size        INTEGER NOT NULL,
            mtime_ticks INTEGER NOT NULL,
            key_count   INTEGER NOT NULL,
            is_utf8     INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS hits (
            rel_path TEXT NOT NULL,
            kind     TEXT NOT NULL,
            key_path TEXT,
            snip     TEXT,
            seq      INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_hits_path ON hits(rel_path, kind);
        """;

    /// <summary>某库的完整建表脚本（index_meta + 业务表）。</summary>
    public static string For(WorkbenchCacheKind kind)
    {
        var business = kind switch
        {
            WorkbenchCacheKind.BankIndex => BankIndexSql,
            WorkbenchCacheKind.StaticTables => StaticTablesSql,
            WorkbenchCacheKind.TextIndex => TextIndexSql,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的缓存库种类。"),
        };
        return IndexMetaSql + "\n" + business;
    }

    /// <summary>某库的业务表名（不含 index_meta；测试与诊断用）。</summary>
    public static IReadOnlyList<string> BusinessTables(WorkbenchCacheKind kind) => kind switch
    {
        WorkbenchCacheKind.BankIndex => ["banks", "samples"],
        WorkbenchCacheKind.StaticTables => ["tables", "documents"],
        WorkbenchCacheKind.TextIndex => ["files", "hits"],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的缓存库种类。"),
    };
}
