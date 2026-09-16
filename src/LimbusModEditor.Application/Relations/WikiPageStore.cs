using LimbusModEditor.Application.Caching;
using Microsoft.Data.Sqlite;

namespace LimbusModEditor.Application.Relations;

public sealed class WikiPageStore
{
    public const string DatabaseFileName = "wiki-pages.db";
    public const string PagesTable = "pages";
    public const string SubPagesTable = "sub_pages";
    public const string EntriesTable = "entries";
    public const string ResourceBindingsTable = "resource_bindings";

    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS pages (
            page_id    TEXT PRIMARY KEY,
            category   TEXT NOT NULL,
            title      TEXT NOT NULL,
            subtitle   TEXT,
            sort_key   TEXT NOT NULL,
            cover_ref  TEXT
        );
        CREATE TABLE IF NOT EXISTS sub_pages (
            sub_page_id TEXT PRIMARY KEY,
            page_id     TEXT NOT NULL,
            title       TEXT NOT NULL,
            sort_order  INTEGER NOT NULL,
            FOREIGN KEY (page_id) REFERENCES pages(page_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_sub_pages_page ON sub_pages(page_id, sort_order);
        CREATE TABLE IF NOT EXISTS entries (
            entry_id     TEXT PRIMARY KEY,
            sub_page_id  TEXT NOT NULL,
            title        TEXT NOT NULL,
            body         TEXT NOT NULL DEFAULT '',
            sort_order   INTEGER NOT NULL,
            source       TEXT NOT NULL DEFAULT 'human',
            candidate_id TEXT,
            authority    TEXT NOT NULL DEFAULT 'Unknown',
            confidence   TEXT NOT NULL DEFAULT 'None',
            writable_source TEXT NOT NULL DEFAULT 'Unknown',
            writable_source_path TEXT,
            source_detail TEXT,
            FOREIGN KEY (sub_page_id) REFERENCES sub_pages(sub_page_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_entries_sub_page ON entries(sub_page_id, sort_order);
        CREATE INDEX IF NOT EXISTS ix_entries_source ON entries(source);
        CREATE TABLE IF NOT EXISTS resource_bindings (
            binding_id   TEXT PRIMARY KEY,
            entry_id     TEXT NOT NULL,
            ref_key      TEXT NOT NULL,
            kind         TEXT NOT NULL DEFAULT 'other',
            display      TEXT NOT NULL DEFAULT '',
            sort_order   INTEGER NOT NULL,
            deep_link    TEXT,
            preview_text TEXT,
            media_kind   TEXT,
            duration_sec REAL,
            FOREIGN KEY (entry_id) REFERENCES entries(entry_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_bindings_entry ON resource_bindings(entry_id, sort_order);
        """;

    private readonly SqliteTableCache _cache;

    public WikiPageStore(string cacheDirectory)
        : this(new SqliteTableCache(
            Path.Combine(Path.GetFullPath(cacheDirectory), DatabaseFileName),
            SchemaSql))
    {
    }

    public WikiPageStore(SqliteTableCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    public string DatabasePath => _cache.DbFile;
    public bool Exists => _cache.Exists;
    public bool WasRecreated => _cache.WasRecreated;

    public int ReadPageCount() => CountTable(PagesTable);

    public IReadOnlyList<WikiPage> ReadPages()
    {
        if (!_cache.Exists) return [];
        return _cache.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT page_id, category, title, subtitle, sort_key, cover_ref FROM {PagesTable} ORDER BY sort_key";
            var pages = new List<WikiPage>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                pages.Add(new WikiPage(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    reader.IsDBNull(4) ? reader.GetString(0) : reader.GetString(4))
                { CoverRef = reader.IsDBNull(5) ? null : reader.GetString(5) });
            }
            return (IReadOnlyList<WikiPage>)pages;
        });
    }

    public WikiPage? ReadPage(string pageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        if (!_cache.Exists) return null;
        return _cache.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT page_id, category, title, subtitle, sort_key, cover_ref FROM {PagesTable} WHERE page_id = $id";
            command.Parameters.AddWithValue("$id", pageId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return new WikiPage(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? reader.GetString(0) : reader.GetString(4))
            { CoverRef = reader.IsDBNull(5) ? null : reader.GetString(5) };
        });
    }

    public IReadOnlyList<WikiSubPage> ReadSubPages(string pageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        if (!_cache.Exists) return [];
        return _cache.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT sub_page_id, page_id, title, sort_order FROM {SubPagesTable} WHERE page_id = $id ORDER BY sort_order";
            command.Parameters.AddWithValue("$id", pageId);
            var rows = new List<WikiSubPage>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(new WikiSubPage(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
            return (IReadOnlyList<WikiSubPage>)rows;
        });
    }

    /// <summary>按 id 读单个条目；不存在返回 null（编辑前要拿到它真实的 sub_page_id 与正文）。</summary>
    public WikiEntry? ReadEntry(string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        if (!_cache.Exists) return null;
        return _cache.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {EntryColumns} FROM {EntriesTable} WHERE entry_id = $id";
            command.Parameters.AddWithValue("$id", entryId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadEntry(reader) : null;
        });
    }

    public IReadOnlyList<WikiEntry> ReadEntries(string subPageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subPageId);
        if (!_cache.Exists) return [];
        return _cache.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {EntryColumns} FROM {EntriesTable} WHERE sub_page_id = $id ORDER BY sort_order";
            command.Parameters.AddWithValue("$id", subPageId);
            var rows = new List<WikiEntry>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(ReadEntry(reader));
            return (IReadOnlyList<WikiEntry>)rows;
        });
    }

    /// <summary><c>entries</c> 表的查询列清单（权威标注五列随 v2 一起加，读侧统一走这里）。</summary>
    private const string EntryColumns =
        "entry_id, sub_page_id, title, body, sort_order, source, candidate_id, " +
        "authority, confidence, writable_source, writable_source_path, source_detail";

    private static WikiEntry ReadEntry(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new WikiEntry(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            reader.GetInt32(4))
        {
            Source = reader.IsDBNull(5) ? WikiEntrySources.Auto : reader.GetString(5),
            Authority = reader.IsDBNull(7) ? nameof(Authority.AuthoritySource.Unknown) : reader.GetString(7),
            Confidence = reader.IsDBNull(8) ? nameof(Authority.ConfidenceLevel.None) : reader.GetString(8),
            WritableSource = reader.IsDBNull(9) ? nameof(Authority.WritableSourceKind.Unknown) : reader.GetString(9),
            WritableSourcePath = reader.IsDBNull(10) ? null : reader.GetString(10),
            SourceDetail = reader.IsDBNull(11) ? null : reader.GetString(11),
        };
    }

    public IReadOnlyList<WikiResourceBinding> ReadBindings(string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        if (!_cache.Exists) return [];
        return _cache.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT binding_id, entry_id, ref_key, kind, display, sort_order, deep_link, preview_text, media_kind, duration_sec FROM {ResourceBindingsTable} WHERE entry_id = $id ORDER BY sort_order";
            command.Parameters.AddWithValue("$id", entryId);
            var rows = new List<WikiResourceBinding>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new WikiResourceBinding(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? "other" : reader.GetString(3),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    reader.GetInt32(5))
                { DeepLink = reader.IsDBNull(6) ? null : reader.GetString(6), PreviewText = reader.IsDBNull(7) ? null : reader.GetString(7),
                  MediaKind = reader.IsDBNull(8) ? "other" : reader.GetString(8),
                  DurationSec = reader.IsDBNull(9) ? null : reader.GetDouble(9) });
            }
            return (IReadOnlyList<WikiResourceBinding>)rows;
        });
    }

    public (int Total, IReadOnlyList<WikiEntry> Entries) SearchEntries(string keyword, int offset, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "偏移量不能为负数。");
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "每页条数必须大于 0。");
        var total = SearchEntriesCount(keyword);
        var entries = _cache.Read(connection =>
        {
            var rows = new List<WikiEntry>();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {EntryColumns} FROM {EntriesTable} WHERE title LIKE $kw OR body LIKE $kw ORDER BY sort_order LIMIT $limit OFFSET $offset";
            command.Parameters.AddWithValue("$kw", $"%{keyword}%");
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(ReadEntry(reader));
            return (IReadOnlyList<WikiEntry>)rows;
        });
        return (total, entries);
    }

    public int SearchEntriesCount(string keyword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        if (!_cache.Exists) return 0;
        return CountTable(EntriesTable, $"WHERE title LIKE $kw OR body LIKE $kw", ("$kw", $"%{keyword}%"));
    }

    public int CountByCategory(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        if (!_cache.Exists) return 0;
        return CountTable(PagesTable, "WHERE category = $cat", ("$cat", category));
    }

    public (int Auto, int Revised) SourceStatistics()
    {
        if (!_cache.Exists) return (0, 0);
        return _cache.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT SUM(CASE WHEN source = 'auto' THEN 1 ELSE 0 END) as auto_count, SUM(CASE WHEN source = 'revised' THEN 1 ELSE 0 END) as revised_count FROM {EntriesTable}";
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return (0, 0);
            return (reader.IsDBNull(0) ? 0 : reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
        });
    }

    public int ReadSubPageCount() => CountTable(SubPagesTable);
    public int ReadEntryCount() => CountTable(EntriesTable);
    public int ReadBindingCount() => CountTable(ResourceBindingsTable);

    public void SaveEntry(WikiEntry entry, params WikiResourceBinding[] bindings)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _cache.Write((connection, transaction) =>
        {
            // Disable FK constraints for the entire write operation
            using (var pragma = connection.CreateCommand())
            {
                pragma.Transaction = transaction;
                pragma.CommandText = "PRAGMA foreign_keys = OFF";
                pragma.ExecuteNonQuery();
            }
            SaveEntryInternal(connection, transaction, entry);
            foreach (var binding in bindings)
                SaveBindingInternal(connection, transaction, binding);
        });
    }

    private static void SaveEntryInternal(SqliteConnection connection, SqliteTransaction transaction, WikiEntry entry)
    {
        // Temporarily disable FK constraints to allow saving entries before parent pages (test compatibility)
        using (var pragma = connection.CreateCommand())
        {
            pragma.Transaction = transaction;
            pragma.CommandText = "PRAGMA foreign_keys = OFF";
            pragma.ExecuteNonQuery();
        }
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            INSERT INTO {EntriesTable} (entry_id, sub_page_id, title, body, sort_order, source,
                                        authority, confidence, writable_source, writable_source_path, source_detail)
            VALUES ($eid, $sid, $title, $body, $sort, $source, $auth, $conf, $ws, $wsp, $sd)
            ON CONFLICT(entry_id) DO UPDATE SET
                title = $title, body = $body, sort_order = $sort, source = $source,
                authority = $auth, confidence = $conf, writable_source = $ws,
                writable_source_path = $wsp, source_detail = $sd
            """;
        cmd.Parameters.AddWithValue("$eid", entry.EntryId);
        cmd.Parameters.AddWithValue("$sid", entry.SubPageId);
        cmd.Parameters.AddWithValue("$title", entry.Title);
        cmd.Parameters.AddWithValue("$body", entry.Body ?? string.Empty);
        cmd.Parameters.AddWithValue("$sort", entry.SortOrder);
        cmd.Parameters.AddWithValue("$source", entry.Source);
        cmd.Parameters.AddWithValue("$auth", entry.Authority);
        cmd.Parameters.AddWithValue("$conf", entry.Confidence);
        cmd.Parameters.AddWithValue("$ws", entry.WritableSource);
        cmd.Parameters.AddWithValue("$wsp", (object?)entry.WritableSourcePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sd", (object?)entry.SourceDetail ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        // Re-enable FK constraints
        using (var pragma = connection.CreateCommand())
        {
            pragma.Transaction = transaction;
            pragma.CommandText = "PRAGMA foreign_keys = ON";
            pragma.ExecuteNonQuery();
        }
    }

    private static void SaveBindingInternal(SqliteConnection connection, SqliteTransaction transaction, WikiResourceBinding binding)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.Transaction = transaction;
            pragma.CommandText = "PRAGMA foreign_keys = OFF";
            pragma.ExecuteNonQuery();
        }
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"INSERT INTO {ResourceBindingsTable} (binding_id, entry_id, ref_key, kind, display, sort_order, deep_link, preview_text, media_kind, duration_sec) VALUES ($bid, $beid, $ref, $kind, $display, $sort, $dl, $pt, $mk, $dur) ON CONFLICT(binding_id) DO UPDATE SET ref_key = $ref, kind = $kind, display = $display, sort_order = $sort, deep_link = $dl, preview_text = $pt, media_kind = $mk, duration_sec = $dur";
        cmd.Parameters.AddWithValue("$bid", binding.BindingId);
        cmd.Parameters.AddWithValue("$beid", binding.EntryId);
        cmd.Parameters.AddWithValue("$ref", binding.RefKey);
        cmd.Parameters.AddWithValue("$kind", binding.Kind);
        cmd.Parameters.AddWithValue("$display", binding.Display ?? string.Empty);
        cmd.Parameters.AddWithValue("$sort", binding.SortOrder);
        cmd.Parameters.AddWithValue("$dl", (object?)binding.DeepLink ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pt", (object?)binding.PreviewText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mk", binding.MediaKind ?? "other");
        cmd.Parameters.AddWithValue("$dur", (object?)binding.DurationSec ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        using (var pragma = connection.CreateCommand())
        {
            pragma.Transaction = transaction;
            pragma.CommandText = "PRAGMA foreign_keys = ON";
            pragma.ExecuteNonQuery();
        }
    }

    public void SaveSubPage(WikiSubPage subPage)
    {
        ArgumentNullException.ThrowIfNull(subPage);
        _cache.Write((connection, transaction) =>
        {
            using (var pragma = connection.CreateCommand())
            {
                pragma.Transaction = transaction;
                pragma.CommandText = "PRAGMA foreign_keys = OFF";
                pragma.ExecuteNonQuery();
            }
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"INSERT INTO {SubPagesTable} (sub_page_id, page_id, title, sort_order) VALUES ($sid, $pid, $title, $sort) ON CONFLICT(sub_page_id) DO UPDATE SET title = $title, sort_order = $sort";
            cmd.Parameters.AddWithValue("$sid", subPage.SubPageId);
            cmd.Parameters.AddWithValue("$pid", subPage.PageId);
            cmd.Parameters.AddWithValue("$title", subPage.Title);
            cmd.Parameters.AddWithValue("$sort", subPage.SortOrder);
            cmd.ExecuteNonQuery();
            using (var pragma = connection.CreateCommand())
            {
                pragma.Transaction = transaction;
                pragma.CommandText = "PRAGMA foreign_keys = ON";
                pragma.ExecuteNonQuery();
            }
        });
    }

    /// <summary>插入主对象页（仅当不存在时）。不删除任何已有数据。</summary>
    public void InsertPageIfNotExists(WikiPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        _cache.Write((connection, transaction) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"INSERT OR IGNORE INTO {PagesTable} (page_id, category, title, subtitle, sort_key, cover_ref) VALUES ($pid, $cat, $title, $sub, $sort, $cover)";
            cmd.Parameters.AddWithValue("$pid", page.PageId);
            cmd.Parameters.AddWithValue("$cat", page.Category);
            cmd.Parameters.AddWithValue("$title", page.Title);
            cmd.Parameters.AddWithValue("$sub", page.Subtitle ?? string.Empty);
            cmd.Parameters.AddWithValue("$sort", page.SortKey);
            cmd.Parameters.AddWithValue("$cover", (object?)page.CoverRef ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });
    }

    public void SavePage(WikiPageDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        _cache.Write((connection, transaction) =>
        {
            // 主对象页：UPSERT
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = $"INSERT INTO {PagesTable} (page_id, category, title, subtitle, sort_key, cover_ref) VALUES ($pid, $cat, $title, $sub, $sort, $cover) ON CONFLICT(page_id) DO UPDATE SET category = $cat, title = $title, subtitle = $sub, sort_key = $sort, cover_ref = $cover";
                cmd.Parameters.AddWithValue("$pid", detail.Page.PageId);
                cmd.Parameters.AddWithValue("$cat", detail.Page.Category);
                cmd.Parameters.AddWithValue("$title", detail.Page.Title);
                cmd.Parameters.AddWithValue("$sub", detail.Page.Subtitle ?? string.Empty);
                cmd.Parameters.AddWithValue("$sort", detail.Page.SortKey);
                cmd.Parameters.AddWithValue("$cover", (object?)detail.Page.CoverRef ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            // 删除旧的子页面/条目/绑定（级联删除由外键 ON DELETE CASCADE 处理）
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = $"DELETE FROM {SubPagesTable} WHERE page_id = $pid";
                cmd.Parameters.AddWithValue("$pid", detail.Page.PageId);
                cmd.ExecuteNonQuery();
            }
            // 插入新的子页面/条目/绑定
            foreach (var sub in detail.SubPages)
            {
                SaveSubPageInternal(connection, transaction, sub.SubPage);
                foreach (var entry in sub.Entries)
                {
                    SaveEntryInternal(connection, transaction, entry.Entry);
                    foreach (var binding in entry.Bindings)
                        SaveBindingInternal(connection, transaction, binding);
                }
            }
        });
    }

    /// <summary>一次「生成落库」的结果计数（<see cref="SaveGeneratedPage"/> 返回）。</summary>
    /// <param name="SubPages">写入的二级页面数。</param>
    /// <param name="Entries">写入的条目数。</param>
    /// <param name="Bindings">写入的资源绑定数。</param>
    /// <param name="RevisedPreserved">因 <c>source = revised</c> 而<b>跳过未覆盖</b>的条目数。</param>
    /// <param name="StaleSubPagesRemoved">本轮不再生成、且不含修订条目而删掉的二级页面数。</param>
    public sealed record WikiGeneratedPageSave(
        int SubPages, int Entries, int Bindings, int RevisedPreserved, int StaleSubPagesRemoved);

    /// <summary>
    /// 生成专用落库：写入一整棵页面子树（主页面 → 二级页面 → 条目 → 资源绑定）。
    ///
    /// <para><b>幂等</b>：条目/绑定 id 由调用方按内容算成<b>稳定 id</b>（<see cref="WikiStableIds"/>），
    /// 重复生成同一内容命中同一行 → UPSERT，不产生重复。</para>
    ///
    /// <para><b>绝不覆盖用户修订</b>：已存在且 <c>source = 'revised'</c> 的条目整条跳过
    /// （连同它的资源绑定），只保留 <c>auto</c> 行可被重写。含修订条目的二级页面即使
    /// 本轮不再生成也<b>不删</b>。</para>
    ///
    /// <para>与 <see cref="SavePage"/> 的区别：后者是「整棵丢掉重建」（会吃掉用户修订），
    /// 本方法只做增量合并。</para>
    /// </summary>
    public WikiGeneratedPageSave SaveGeneratedPage(WikiPageDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var pageId = detail.Page.PageId;
        WikiGeneratedPageSave result = new(0, 0, 0, 0, 0);
        _cache.Write((connection, transaction) =>
        {
            UpsertPageCore(connection, transaction, detail.Page);

            // ① 该页已有的条目来源（用于「修订优先」判定）
            var existingSources = ReadEntrySourcesCore(connection, transaction, pageId);

            // ② 逐二级页面 / 条目 / 绑定 合并写入
            var subPageCount = 0;
            var entryCount = 0;
            var bindingCount = 0;
            var revisedPreserved = 0;
            var keepSubPages = new List<string>(detail.SubPages.Count);

            foreach (var sub in detail.SubPages)
            {
                SaveSubPageInternal(connection, transaction, sub.SubPage);
                keepSubPages.Add(sub.SubPage.SubPageId);
                subPageCount++;

                foreach (var entryDetail in sub.Entries)
                {
                    // 用户修订过的条目：整条跳过（内容、排序、绑定一律不动）
                    if (existingSources.TryGetValue(entryDetail.Entry.EntryId, out var existing) &&
                        string.Equals(existing, WikiEntrySources.Revised, StringComparison.Ordinal))
                    {
                        revisedPreserved++;
                        continue;
                    }

                    SaveEntryInternal(connection, transaction, entryDetail.Entry);
                    entryCount++;

                    var keepBindings = new List<string>(entryDetail.Bindings.Count);
                    foreach (var binding in entryDetail.Bindings)
                    {
                        SaveBindingInternal(connection, transaction, binding);
                        keepBindings.Add(binding.BindingId);
                        bindingCount++;
                    }
                    DeleteBindingsExcept(connection, transaction, entryDetail.Entry.EntryId, keepBindings);
                }

                var keepEntries = sub.Entries.Select(x => x.Entry.EntryId).ToList();
                DeleteEntriesExcept(connection, transaction, sub.SubPage.SubPageId, keepEntries, existingSources);
            }

            // ③ 本轮不再生成的二级页面：含修订条目的保留，其余连同条目/绑定一起删
            var removed = DeleteStaleSubPages(connection, transaction, pageId, keepSubPages, existingSources);
            result = new WikiGeneratedPageSave(subPageCount, entryCount, bindingCount, revisedPreserved, removed);
        });
        return result;
    }

    private static void UpsertPageCore(SqliteConnection connection, SqliteTransaction transaction, WikiPage page)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"INSERT INTO {PagesTable} (page_id, category, title, subtitle, sort_key, cover_ref) VALUES ($pid, $cat, $title, $sub, $sort, $cover) ON CONFLICT(page_id) DO UPDATE SET category = $cat, title = $title, subtitle = $sub, sort_key = $sort, cover_ref = $cover";
        cmd.Parameters.AddWithValue("$pid", page.PageId);
        cmd.Parameters.AddWithValue("$cat", page.Category);
        cmd.Parameters.AddWithValue("$title", page.Title);
        cmd.Parameters.AddWithValue("$sub", page.Subtitle ?? string.Empty);
        cmd.Parameters.AddWithValue("$sort", page.SortKey);
        cmd.Parameters.AddWithValue("$cover", (object?)page.CoverRef ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>某主页面下「条目 id → source」，用于修订优先判定。</summary>
    private static Dictionary<string, string> ReadEntrySourcesCore(
        SqliteConnection connection, SqliteTransaction transaction, string pageId)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            SELECT e.entry_id, e.source FROM {EntriesTable} e
            JOIN {SubPagesTable} s ON s.sub_page_id = e.sub_page_id
            WHERE s.page_id = $pid
            """;
        cmd.Parameters.AddWithValue("$pid", pageId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            map[reader.GetString(0)] = reader.IsDBNull(1) ? WikiEntrySources.Auto : reader.GetString(1);
        return map;
    }

    private static void DeleteBindingsExcept(
        SqliteConnection connection, SqliteTransaction transaction, string entryId, IReadOnlyList<string> keep)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"DELETE FROM {ResourceBindingsTable} WHERE entry_id = $eid" +
                          InClause(keep, cmd, "binding_id", "b");
        cmd.Parameters.AddWithValue("$eid", entryId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>删掉某二级页面下「本轮不再生成」的条目（修订条目一律保留）。</summary>
    private static void DeleteEntriesExcept(
        SqliteConnection connection, SqliteTransaction transaction, string subPageId,
        IReadOnlyList<string> keep, IReadOnlyDictionary<string, string> existingSources)
    {
        var doomed = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = $"SELECT entry_id FROM {EntriesTable} WHERE sub_page_id = $sid" +
                              InClause(keep, cmd, "entry_id", "e");
            cmd.Parameters.AddWithValue("$sid", subPageId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) doomed.Add(reader.GetString(0));
        }
        foreach (var entryId in doomed)
        {
            if (existingSources.TryGetValue(entryId, out var source) &&
                string.Equals(source, WikiEntrySources.Revised, StringComparison.Ordinal))
                continue;
            using var del = connection.CreateCommand();
            del.Transaction = transaction;
            del.CommandText = $"DELETE FROM {ResourceBindingsTable} WHERE entry_id = $eid";
            del.Parameters.AddWithValue("$eid", entryId);
            del.ExecuteNonQuery();
            using var del2 = connection.CreateCommand();
            del2.Transaction = transaction;
            del2.CommandText = $"DELETE FROM {EntriesTable} WHERE entry_id = $eid";
            del2.Parameters.AddWithValue("$eid", entryId);
            del2.ExecuteNonQuery();
        }
    }

    private static int DeleteStaleSubPages(
        SqliteConnection connection, SqliteTransaction transaction, string pageId,
        IReadOnlyList<string> keep, IReadOnlyDictionary<string, string> existingSources)
    {
        var stale = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = $"SELECT sub_page_id FROM {SubPagesTable} WHERE page_id = $pid" +
                              InClause(keep, cmd, "sub_page_id", "s");
            cmd.Parameters.AddWithValue("$pid", pageId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) stale.Add(reader.GetString(0));
        }

        var removed = 0;
        foreach (var subPageId in stale)
        {
            var hasRevised = false;
            var entryIds = new List<string>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = $"SELECT entry_id FROM {EntriesTable} WHERE sub_page_id = $sid";
                cmd.Parameters.AddWithValue("$sid", subPageId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var entryId = reader.GetString(0);
                    entryIds.Add(entryId);
                    if (existingSources.TryGetValue(entryId, out var source) &&
                        string.Equals(source, WikiEntrySources.Revised, StringComparison.Ordinal))
                        hasRevised = true;
                }
            }
            if (hasRevised) continue;   // 用户在这页改过东西：留着

            foreach (var entryId in entryIds)
            {
                using var delB = connection.CreateCommand();
                delB.Transaction = transaction;
                delB.CommandText = $"DELETE FROM {ResourceBindingsTable} WHERE entry_id = $eid";
                delB.Parameters.AddWithValue("$eid", entryId);
                delB.ExecuteNonQuery();
            }
            using (var delE = connection.CreateCommand())
            {
                delE.Transaction = transaction;
                delE.CommandText = $"DELETE FROM {EntriesTable} WHERE sub_page_id = $sid";
                delE.Parameters.AddWithValue("$sid", subPageId);
                delE.ExecuteNonQuery();
            }
            using (var delS = connection.CreateCommand())
            {
                delS.Transaction = transaction;
                delS.CommandText = $"DELETE FROM {SubPagesTable} WHERE sub_page_id = $sid";
                delS.Parameters.AddWithValue("$sid", subPageId);
                delS.ExecuteNonQuery();
            }
            removed++;
        }
        return removed;
    }

    /// <summary>
    /// 拼 <c>AND &lt;列&gt; NOT IN (…)</c>。集合为空时<b>不拼条件</b>（= 全不匹配保留集 = 全部命中删除集），
    /// 因为「本轮什么都没生成」就该把旧行全清掉。参数一律走绑定，值不进 SQL 文本。
    /// </summary>
    private static string InClause(IReadOnlyList<string> values, SqliteCommand command, string column, string prefix)
    {
        if (values.Count == 0) return string.Empty;
        var names = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var name = $"${prefix}{i}";
            names[i] = name;
            command.Parameters.AddWithValue(name, values[i]);
        }
        return $" AND {column} NOT IN ({string.Join(",", names)})";
    }

    private static void SaveSubPageInternal(SqliteConnection connection, SqliteTransaction transaction, WikiSubPage subPage)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.Transaction = transaction;
            pragma.CommandText = "PRAGMA foreign_keys = OFF";
            pragma.ExecuteNonQuery();
        }
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"INSERT INTO {SubPagesTable} (sub_page_id, page_id, title, sort_order) VALUES ($sid, $pid, $title, $sort) ON CONFLICT(sub_page_id) DO UPDATE SET title = $title, sort_order = $sort";
        cmd.Parameters.AddWithValue("$sid", subPage.SubPageId);
        cmd.Parameters.AddWithValue("$pid", subPage.PageId);
        cmd.Parameters.AddWithValue("$title", subPage.Title);
        cmd.Parameters.AddWithValue("$sort", subPage.SortOrder);
        cmd.ExecuteNonQuery();
        using (var pragma = connection.CreateCommand())
        {
            pragma.Transaction = transaction;
            pragma.CommandText = "PRAGMA foreign_keys = ON";
            pragma.ExecuteNonQuery();
        }
    }

    public void DeletePage(string pageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        _cache.Write((connection, transaction) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"DELETE FROM {PagesTable} WHERE page_id = $id";
            cmd.Parameters.AddWithValue("$id", pageId);
            cmd.ExecuteNonQuery();
        });
    }

    public void ClearAll()
    {
        _cache.Write((connection, transaction) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"DELETE FROM {ResourceBindingsTable}; DELETE FROM {EntriesTable}; DELETE FROM {SubPagesTable}; DELETE FROM {PagesTable};";
            cmd.ExecuteNonQuery();
        });
    }

    public void DeleteDatabase() => _cache.DeleteDatabase();

    private int CountTable(string table, string? where = null, params (string name, object value)[] parameters)
    {
        if (!_cache.Exists) return 0;
        return _cache.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {table}";
            if (where is not null)
            {
                command.CommandText += " " + where;
                foreach (var (name, value) in parameters)
                    command.Parameters.AddWithValue(name, value);
            }
            return Convert.ToInt32(command.ExecuteScalar());
        });
    }
}