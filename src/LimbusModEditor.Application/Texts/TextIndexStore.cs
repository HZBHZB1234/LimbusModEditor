using System.Globalization;
using LimbusModEditor.Application.Caching;
using Microsoft.Data.Sqlite;

namespace LimbusModEditor.Application.Texts;

/// <summary>命中类别在 <c>hits.kind</c> 列里的持久化标签（列名与取值都被单测钉死）。</summary>
public static class TextIndexHitKind
{
    /// <summary>文件名命中。</summary>
    public const string File = "File";
    /// <summary>键路径命中。</summary>
    public const string Key = "Key";
    /// <summary>值命中。</summary>
    public const string Value = "Value";

    /// <summary>落地标签（<c>File</c> / <c>Key</c> / <c>Value</c>）。</summary>
    public static string Format(LangTextSearchKind kind) => kind switch
    {
        LangTextSearchKind.FileName => File,
        LangTextSearchKind.Key => Key,
        LangTextSearchKind.Value => Value,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的命中类别。"),
    };

    /// <summary>解析落地标签；无法识别返回 false（旧库/被手改的库按「不在索引里」处理）。</summary>
    public static bool TryParse(string? text, out LangTextSearchKind kind)
    {
        switch (text)
        {
            case File: kind = LangTextSearchKind.FileName; return true;
            case Key: kind = LangTextSearchKind.Key; return true;
            case Value: kind = LangTextSearchKind.Value; return true;
            default: kind = default; return false;
        }
    }
}

/// <summary>
/// plan-10：<c>cache/text-index.db</c> 的读写（lang 文件索引 + 搜索命中）。
///
/// <para><b>它只是加速旁路，语义与现状完全一致</b>：库里存的全部是「读一次磁盘就能
/// 重新算出来」的原版（vanilla）事实——文件级索引（相对路径 / 大小 / mtime / 顶层键数 /
/// 是否 UTF-8）与搜索命中候选（文件名 / 键路径 / 完整值文本，<b>不存键值行</b>）。
/// 把库删掉功能完全不受影响，只是每次进页面要重新读全部文件
/// （单测 <c>TextIndexStoreTests</c> 用「有缓存 vs 删库」逐字段比对证明这一点）。</para>
///
/// <para><b>失效规则</b>（唯一实现，无例外）：
/// ① 整库：<c>index_meta.signature</c> = <b>内容口径版本 + 活动语言目录签名 + config.json 内容哈希</b>
/// （内容哈希是必需的：活动语言由 config.json 的 <c>lang</c> 字段决定，
/// 几十字节的小文件改写后大小可能不变，只比目录 mtime 会漏掉「玩家切换了活动语言」；
/// 版本前缀见 <see cref="IndexFormatVersion"/>——「收录哪些文件」的口径变了也要让旧库重建）；
/// ② 单文件：<c>files</c> 行的 <c>(size, mtime_ticks)</c> 与磁盘不符才重解析；
/// ③ 增删文件：目录结构每次都真实枚举，与库里行集不一致即整表重建。</para>
///
/// <para><b>编辑集绝不进这份缓存</b>：文本工作台的内存修改仍由
/// <see cref="LangTextWorkbenchService"/> 自己持有，导出/直接应用两个通道一字未改。</para>
/// </summary>
public sealed class TextIndexStore
{
    /// <summary>索引里的表名（与 <see cref="WorkbenchCacheSchema.TextIndexSql"/> 一致）。</summary>
    public const string FilesTable = "files";

    /// <summary>索引里的命中表名。</summary>
    public const string HitsTable = "hits";

    /// <summary>单文件命中候选的落盘口径：<b>全部可达候选</b>（键候选一条不落，
    /// 值候选只保留排序上轮得到被检查的那些）——由
    /// <see cref="LangTextWorkbenchService.ReadFileHits"/> 用默认的 per-file 上限算出来。
    /// 不做额外截断：截断会让「有缓存」与「无缓存」的搜索结果不一致。</summary>
    public const int HitCandidatesPerFile = LangTextWorkbenchService.DefaultMaxHitsPerFile;

    private readonly SqliteTableCache _cache;

    /// <param name="cacheDirectory">缓存目录（<c>AppEnvironment.CacheDirectory</c>，
    /// 即 <c>&lt;程序目录&gt;/cache</c>）。</param>
    public TextIndexStore(string cacheDirectory)
        : this(SqliteTableCache.Create(WorkbenchCacheKind.TextIndex, cacheDirectory))
    {
    }

    /// <summary>直接注入底座（单测用临时目录建库）。</summary>
    public TextIndexStore(SqliteTableCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    /// <summary>库文件路径（诊断/测试用）。</summary>
    public string DatabasePath => _cache.DbFile;

    /// <summary>
    /// 索引<b>内容口径</b>版本，参与源签名。只要「工作台收录哪些文件 / 每行存什么」这类
    /// 口径变了就必须 +1：签名里其它两项（目录 mtime、config.json 内容哈希）都不会因此变化，
    /// 不升版的话旧库会一直被判定为「新鲜」，旧口径的行（例如已废弃的 <c>config.json</c> 行）
    /// 就会一直留在库里并被读出来。
    /// <list type="bullet">
    /// <item><c>v1</c>：首版（活动语言目录 + 根级 <c>config.json</c>）。</item>
    /// <item><c>v2</c>（plan-14）：不再收录根级 <c>config.json</c>——它是「活动语言是谁」的输入，
    /// 不是可翻译的文本表。</item>
    /// </list>
    /// </summary>
    public const string IndexFormatVersion = "v2";

    /// <summary>本实例（或底层库）是否因损坏而执行过删库重建。</summary>
    public bool WasRecreated => _cache.WasRecreated;

    // ── 源签名 ───────────────────────────────────────────────────────

    /// <summary>
    /// 描述一个 lang 根：源键（规范化路径，忽略大小写）+ 签名（<b>内容口径版本</b>
    /// + 目录签名 <c>length:mtime</c> + config.json 内容哈希）。任何一项变
    /// （含切换活动语言、以及「收录哪些文件」的口径升级）都使整库失效重建。
    /// </summary>
    public static TextIndexSource DescribeSource(string langRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(langRoot);
        var full = Path.GetFullPath(langRoot);
        var directorySignature = CacheSignature.FromDirectory(full);
        var configHash = LangTextWorkbenchService.ComputeConfigContentHash(full);
        return new TextIndexSource(full, full.ToLowerInvariant(),
            directorySignature.Format(), configHash,
            $"{IndexFormatVersion}|{directorySignature.Format()}|{configHash}");
    }

    /// <summary>非抛出式：目录不存在时返回 null（页面按「没有 lang 目录」处理）。</summary>
    public static TextIndexSource? TryDescribeSource(string? langRoot)
    {
        if (string.IsNullOrWhiteSpace(langRoot) || !Directory.Exists(langRoot)) return null;
        try
        {
            return DescribeSource(langRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>库是否就是按这个源 + 这个签名建的；同时要求 <c>files</c> 表非空
    /// （空表 = 上次没建成，必须重建）。</summary>
    public bool IsFresh(TextIndexSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!_cache.Exists) return false;
        if (!_cache.MatchesSource(source.SourceKey, source.Signature)) return false;
        return ReadFileCount() > 0;
    }

    // ── 读 ───────────────────────────────────────────────────────────

    /// <summary>读索引里的文件条目（顺序与
    /// <see cref="LangTextWorkbenchService.EnumerateFiles"/> 完全一致：根级 <c>config.json</c> 在最前，
    /// 其余按相对路径字典序）。<paramref name="langRootOverride"/> 用于「库里的相对路径 + 当前 lang 根」
    /// 拼出完整路径；缺省用索引自己的源路径。</summary>
    public IReadOnlyList<LangTextFileInfo> ReadFiles(string? langRootOverride = null)
    {
        var langRoot = langRootOverride ?? ReadSourceKey();
        if (string.IsNullOrWhiteSpace(langRoot)) return [];
        return _cache.Read(connection =>
        {
            var rows = new List<LangTextFileInfo>();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT rel_path, size, mtime_ticks, key_count, is_utf8 FROM files
                ORDER BY CASE WHEN rel_path = 'config.json' THEN 0 ELSE 1 END, rel_path
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var relative = reader.GetString(0);
                rows.Add(new LangTextFileInfo(
                    relative,
                    Path.GetFullPath(Path.Combine(langRoot, relative.Replace('/', Path.DirectorySeparatorChar))),
                    reader.GetInt64(1),
                    reader.GetInt32(3),
                    reader.GetInt64(4) != 0,
                    reader.GetInt64(2)));
            }
            return (IReadOnlyList<LangTextFileInfo>)rows;
        });
    }

    /// <summary>索引里的文件条目字典（相对路径 → 条目），供
    /// <c>EnumerateFiles</c> 的加速旁路直接命中。</summary>
    public IReadOnlyDictionary<string, LangTextFileInfo> ReadFileMap(string? langRootOverride = null)
    {
        var map = new Dictionary<string, LangTextFileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ReadFiles(langRootOverride)) map[file.RelativePath] = file;
        return map;
    }

    /// <summary>库里的文件条目数（诊断/测试用）。</summary>
    public int ReadFileCount()
        => _cache.Read(static connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM files";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        });

    /// <summary>库里的命中候选行数（诊断/测试用）。</summary>
    public int ReadHitCount()
        => _cache.Read(static connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM hits";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        });

    /// <summary>
    /// 读命中候选：相对路径 → 候选行（键路径 / 值文本；<b>不含</b>文件名命中——
    /// 文件名命中由调用方按相对路径现判，见 <see cref="LangTextWorkbenchService.Search"/>）。
    /// 行序按 <c>seq</c>，与索引时逐文件枚举叶子节点的顺序一致。
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<LangTextSearchHit>> ReadHits()
    {
        return _cache.Read(connection =>
        {
            var temp = new Dictionary<string, List<LangTextSearchHit>>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT rel_path, kind, key_path, snip FROM hits ORDER BY rel_path, seq
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var relative = reader.GetString(0);
                if (!TextIndexHitKind.TryParse(reader.GetString(1), out var kind)) continue;
                if (kind == LangTextSearchKind.FileName) continue; // 文件名命中现判，不入候选
                var keyPath = reader.IsDBNull(2) ? null : reader.GetString(2);
                var snippet = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (!temp.TryGetValue(relative, out var list))
                {
                    list = [];
                    temp[relative] = list;
                }
                list.Add(new LangTextSearchHit(relative, kind, keyPath, snippet));
            }

            var result = new Dictionary<string, IReadOnlyList<LangTextSearchHit>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (relative, list) in temp) result[relative] = list;
            return (IReadOnlyDictionary<string, IReadOnlyList<LangTextSearchHit>>)result;
        });
    }

    /// <summary>索引里记录的源路径（规范化后的 lang 根）；没有记录返回 null。</summary>
    public string? ReadSourceKey()
        => _cache.Read(static connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT source_key FROM index_meta LIMIT 1";
            var value = command.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToString(value);
        });

    // ── 写：整库重建 / 增量刷新 ──────────────────────────────────────

    /// <summary>
    /// 保证库是按 <paramref name="source"/> 建的：签名不一致（换游戏目录 / 换活动语言 /
    /// 目录动过）或库不存在时<b>清空重建</b>并返回 true（= 调用方必须重新解析全部文件）；
    /// 一致时返回 false（可直接信任缓存）。
    ///
    /// <para>目录结构每次都真实枚举（<c>EnumerateFiles</c> 保证），所以「新增/删除文件」
    /// 由 <see cref="PersistFiles"/> 的行集比对兜底，不依赖目录 mtime 的可靠性。</para>
    /// </summary>
    public bool EnsureSource(TextIndexSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rebuilt = _cache.EnsureSource(source.SourceKey, source.Signature);
        if (!rebuilt) return false;
        return true;
    }

    /// <summary>
    /// 把一次枚举结果落盘（单事务 + 单文件命中候选）。
    /// 行集与库里不一致（新增 / 删除文件）或任一文件签名变过时整表重建
    /// （<c>--force</c> 语义），否则只更新/补写变过的行——这是「二次进页面几乎不写盘」的来源。
    /// </summary>
    /// <param name="source">当前源（<see cref="EnsureSource"/> 应已把它写进 index_meta）。</param>
    /// <param name="files">本次枚举出的全部文件（相对路径 + 大小 + mtime + 键数 + UTF-8 标记）。</param>
    public void PersistFiles(TextIndexSource source, IReadOnlyList<LangTextFileInfo> files)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(files);
        EnsureSource(source); // 签名一致时是无操作；不一致时已清空，下面的行集比对会判定「全部重写」

        var stored = _cache.Read(connection =>
        {
            var rows = new Dictionary<string, (long Size, long Ticks)>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT rel_path, size, mtime_ticks FROM files";
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
            return rows;
        });

        // 需要重新解析（= 重新算命中候选）的文件：库里没有、签名变过，或整库要重建。
        var changed = files
            .Where(x => !stored.TryGetValue(x.RelativePath, out var row) ||
                        row.Size != x.SizeBytes || row.Ticks != x.MTimeUtcTicks)
            .ToList();
        // 库里一条行都没有（首次使用 / 刚被 EnsureSource 清空 / 上次没建成）→ 整表重建。
        // 有行时：文件被删掉就要重建（不依赖目录 mtime 是否可靠），否则只补变过的行。
        var rebuild = files.Count == 0 || stored.Count == 0
            || stored.Keys.Any(x => !files.Any(f => string.Equals(f.RelativePath, x, StringComparison.OrdinalIgnoreCase)));

        _cache.Write((connection, transaction) =>
        {
            if (rebuild)
            {
                Execute(connection, transaction, $"DELETE FROM {FilesTable}");
                Execute(connection, transaction, $"DELETE FROM {HitsTable}");
            }
            else
            {
                using var deleteHits = connection.CreateCommand();
                deleteHits.Transaction = transaction;
                deleteHits.CommandText = $"DELETE FROM {HitsTable} WHERE rel_path = $path";
                using var deleteFile = connection.CreateCommand();
                deleteFile.Transaction = transaction;
                deleteFile.CommandText = $"DELETE FROM {FilesTable} WHERE rel_path = $path";
                var hitPath = deleteHits.Parameters.Add("$path", SqliteType.Text);
                var filePath = deleteFile.Parameters.Add("$path", SqliteType.Text);
                foreach (var path in changed.Select(x => x.RelativePath))
                {
                    hitPath.Value = path;
                    deleteHits.ExecuteNonQuery();
                    filePath.Value = path;
                    deleteFile.ExecuteNonQuery();
                }
            }

            using (var insertFile = connection.CreateCommand())
            {
                insertFile.Transaction = transaction;
                insertFile.CommandText = $"""
                    INSERT INTO {FilesTable} (rel_path, size, mtime_ticks, key_count, is_utf8)
                    VALUES ($path, $size, $ticks, $keys, $utf8)
                    ON CONFLICT(rel_path) DO UPDATE SET
                        size = $size, mtime_ticks = $ticks, key_count = $keys, is_utf8 = $utf8
                    """;
                var pathParameter = insertFile.Parameters.Add("$path", SqliteType.Text);
                var sizeParameter = insertFile.Parameters.Add("$size", SqliteType.Integer);
                var ticksParameter = insertFile.Parameters.Add("$ticks", SqliteType.Integer);
                var keysParameter = insertFile.Parameters.Add("$keys", SqliteType.Integer);
                var utf8Parameter = insertFile.Parameters.Add("$utf8", SqliteType.Integer);
                foreach (var file in files)
                {
                    pathParameter.Value = file.RelativePath;
                    sizeParameter.Value = file.SizeBytes;
                    ticksParameter.Value = file.MTimeUtcTicks;
                    keysParameter.Value = file.KeyCount;
                    utf8Parameter.Value = file.IsUtf8 ? 1 : 0;
                    insertFile.ExecuteNonQuery();
                }
            }

            // 命中候选：整库时写全部文件，增量时只写这次重解析过的文件。
            using var insertHit = connection.CreateCommand();
            insertHit.Transaction = transaction;
            insertHit.CommandText = $"""
                INSERT INTO {HitsTable} (rel_path, kind, key_path, snip, seq) VALUES ($path, $kind, $keyPath, $snip, $seq)
                """;
            var hitPathParameter = insertHit.Parameters.Add("$path", SqliteType.Text);
            var kindParameter = insertHit.Parameters.Add("$kind", SqliteType.Text);
            var keyPathParameter = insertHit.Parameters.Add("$keyPath", SqliteType.Text);
            var snipParameter = insertHit.Parameters.Add("$snip", SqliteType.Text);
            var seqParameter = insertHit.Parameters.Add("$seq", SqliteType.Integer);

            var toIndex = rebuild ? files : changed;
            foreach (var file in toIndex)
            {
                hitPathParameter.Value = file.RelativePath;
                kindParameter.Value = TextIndexHitKind.File;
                keyPathParameter.Value = DBNull.Value;
                snipParameter.Value = file.RelativePath;
                seqParameter.Value = 0;
                insertHit.ExecuteNonQuery();

                var candidates = LangTextWorkbenchService.ReadFileHits(file.FullPath, file.RelativePath);
                var seq = 1;
                foreach (var candidate in candidates)
                {
                    kindParameter.Value = TextIndexHitKind.Format(candidate.Kind);
                    keyPathParameter.Value = (object?)candidate.KeyPath ?? DBNull.Value;
                    snipParameter.Value = (object?)candidate.Snippet ?? DBNull.Value;
                    seqParameter.Value = seq++;
                    insertHit.ExecuteNonQuery();
                }
            }
        });
    }

    /// <summary>整库删除（含边车文件）；下次使用会自动重建。「清空缓存」入口用。</summary>
    public void DeleteDatabase() => _cache.DeleteDatabase();

    /// <summary>清空业务表但保留 index_meta（诊断用）。</summary>
    public void ClearTables() => _cache.ClearTables();

    // ── 内部工具 ─────────────────────────────────────────────────────

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// plan-10：一个 lang 源的缓存标识。签名 = 内容口径版本 + 活动语言目录签名
/// + config.json 内容哈希（任一变化都使整库失效）。
/// </summary>
/// <param name="LangRoot">规范化后的 lang 根路径（原样大小写，用于拼接完整路径）。</param>
/// <param name="SourceKey">存进 <c>index_meta.source_key</c> 的源标识（小写规范化，忽略大小写比较）。</param>
/// <param name="DirectorySignature">活动语言目录签名（<c>length:mtime</c>）。</param>
/// <param name="ConfigContentHash">config.json 内容哈希（十六进制小写；不存在为 <c>none</c>）。</param>
/// <param name="Signature">最终写进 <c>index_meta.signature</c> 的签名文本。</param>
public sealed record TextIndexSource(
    string LangRoot,
    string SourceKey,
    string DirectorySignature,
    string ConfigContentHash,
    string Signature);
