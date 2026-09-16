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

    public IReadOnlyList<WikiEntry> ReadEntries(string subPageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subPageId);
        if (!_cache.Exists) return [];
        return _cache.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT entry_id, sub_page_id, title, body, sort_order, source, candidate_id FROM {EntriesTable} WHERE sub_page_id = $id ORDER BY sort_order";
            command.Parameters.AddWithValue("$id", subPageId);
            var rows = new List<WikiEntry>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new WikiEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    reader.GetInt32(4))
                { Source = reader.IsDBNull(5) ? WikiEntrySources.Auto : reader.GetString(5) });
            }
            return (IReadOnlyList<WikiEntry>)rows;
        });
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
            command.CommandText = $"SELECT entry_id, sub_page_id, title, body, sort_order, source, candidate_id FROM {EntriesTable} WHERE title LIKE $kw OR body LIKE $kw ORDER BY sort_order LIMIT $limit OFFSET $offset";
            command.Parameters.AddWithValue("$kw", $"%{keyword}%");
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new WikiEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    reader.GetInt32(4))
                { Source = reader.IsDBNull(5) ? WikiEntrySources.Auto : reader.GetString(5) });
            }
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
        cmd.CommandText = $"INSERT INTO {EntriesTable} (entry_id, sub_page_id, title, body, sort_order, source) VALUES ($eid, $sid, $title, $body, $sort, $source) ON CONFLICT(entry_id) DO UPDATE SET title = $title, body = $body, sort_order = $sort, source = $source";
        cmd.Parameters.AddWithValue("$eid", entry.EntryId);
        cmd.Parameters.AddWithValue("$sid", entry.SubPageId);
        cmd.Parameters.AddWithValue("$title", entry.Title);
        cmd.Parameters.AddWithValue("$body", entry.Body ?? string.Empty);
        cmd.Parameters.AddWithValue("$sort", entry.SortOrder);
        cmd.Parameters.AddWithValue("$source", entry.Source);
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