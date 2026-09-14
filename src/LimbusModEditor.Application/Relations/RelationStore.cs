using System.Globalization;
using LimbusModEditor.Application.Caching;
using Microsoft.Data.Sqlite;

namespace LimbusModEditor.Application.Relations;

/// <summary>
/// <c>cache/relation-index.db</c> 的读写：资源关联图的持久化与查询。
///
/// <para><b>它是派生缓存</b>（由四个上游缓存库算出，见 <see cref="RelationIndexSource"/>）：
/// 删除它功能完全不受影响，只是下次要重新跑一遍关联分析。与其它工作台缓存一样，
/// <b>只存原版事实</b>，编辑集绝不落这里；损坏即删库重建。</para>
///
/// <para><b>三张表</b>（与 <see cref="WorkbenchCacheSchema.RelationIndexSql"/> 一致）：
/// <c>subjects</c> = 预设对象（人格）；<c>links</c> = 对象 → 关联资源；
/// <c>subjects_by_ref</c> = 反向索引（资源键 → 对象），资源预览的「关联资源」板块靠它
/// 做 O(1) 反查（给定一个正在预览的资源，立刻知道它属于哪些人格）。</para>
/// </summary>
public sealed class RelationStore
{
    /// <summary><c>subjects</c> 表名。</summary>
    public const string SubjectsTable = "subjects";

    /// <summary><c>links</c> 表名。</summary>
    public const string LinksTable = "links";

    /// <summary><c>subjects_by_ref</c> 表名。</summary>
    public const string SubjectsByRefTable = "subjects_by_ref";

    /// <summary>
    /// <c>xref</c> 表名：**跨资源边**（音频 ⇄ 文本、静态表 ⇄ lang、资源 ⇄ 对象）。
    /// 与 <c>links</c> 的区别是它描述的是两个<b>资源之间</b>的关系（多对多、双向可查），
    /// 而 <c>links</c> 描述的是「对象 → 资源」。
    /// </summary>
    public const string XrefTable = "xref";

    private readonly SqliteTableCache _cache;

    /// <param name="cacheDirectory">缓存目录（<c>AppEnvironment.CacheDirectory</c>）。</param>
    public RelationStore(string cacheDirectory)
        : this(SqliteTableCache.Create(WorkbenchCacheKind.ResourceRelations, cacheDirectory))
    {
    }

    /// <summary>直接注入底座（单测用临时目录建库）。</summary>
    public RelationStore(SqliteTableCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    /// <summary>库文件路径（诊断/测试用）。</summary>
    public string DatabasePath => _cache.DbFile;

    /// <summary>库文件是否存在。</summary>
    public bool Exists => _cache.Exists;

    /// <summary>本实例（或底层库）是否因损坏而执行过删库重建。</summary>
    public bool WasRecreated => _cache.WasRecreated;

    // ── 源签名 ───────────────────────────────────────────────────────

    /// <summary>库里记录的是不是这个源（四个上游源任一变化都不再匹配）。</summary>
    public bool MatchesSource(RelationIndexSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _cache.EnsureSchema();
        return _cache.MatchesSource(source.SourceKey, source.Signature);
    }

    /// <summary>
    /// 保证库按当前源建立：源不一致即<b>整库重建</b>（清空三张业务表）并返回 true
    /// （= 调用方必须重跑关联分析）；一致时返回 false。
    /// </summary>
    public bool EnsureSource(RelationIndexSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _cache.EnsureSource(source.SourceKey, source.Signature);
    }

    /// <summary>库里记录的源标识；没有记录返回 null。</summary>
    public string? ReadSourceKey()
        => _cache.Read(static connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT source_key FROM index_meta LIMIT 1";
            var value = command.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToString(value);
        });

    // ── 读 ───────────────────────────────────────────────────────────

    /// <summary>读全部预设对象（按 sort_key 排序，即数字序）。库为空返回空列表。</summary>
    public IReadOnlyList<RelationSubject> ReadSubjects()
        => _cache.Read(static connection =>
        {
            var rows = new List<RelationSubject>();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT subject_id, subject_kind, display_name, subtitle, character, sort_key,
                       category_label, cover_ref, preview_text, link_count
                FROM {SubjectsTable} ORDER BY sort_key
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(new RelationSubject(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    reader.IsDBNull(5) ? reader.GetString(0) : reader.GetString(5))
                {
                    // v2 的四列必须读回来：封面、卡片正面预览、角标数、类别名。
                    // 少了它们，UI 就只能自己重算（重算口径一旦不一致，卡片与详情会互相打脸）。
                    CategoryLabel = NullableText(reader, 6) ?? string.Empty,
                    CoverRef = NullableText(reader, 7),
                    PreviewText = NullableText(reader, 8),
                    LinkCount = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                });
            return (IReadOnlyList<RelationSubject>)rows;
        });

    /// <summary>读某个对象的关联资源（按类别 + 定位键排序）。</summary>
    public IReadOnlyList<RelationLink> ReadLinks(string subjectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        return ReadLinksCore(subjectId, null);
    }

    /// <summary>读某个对象某一类别的关联资源。</summary>
    public IReadOnlyList<RelationLink> ReadLinks(string subjectId, RelationKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        return ReadLinksCore(subjectId, kind);
    }

    private IReadOnlyList<RelationLink> ReadLinksCore(string subjectId, RelationKind? kind)
        => _cache.Read(connection =>
        {
            var rows = new List<RelationLink>();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT subject_id, category, kind, ref_key, display, detail, size_bytes,
                       preview_text, preview_kind, media_kind, duration_sec, ref_path, deep_link, target_subject_id
                FROM {LinksTable}
                WHERE subject_id = $id{(kind is null ? string.Empty : " AND kind = $kind")}
                ORDER BY kind, ref_key
                """;
            command.Parameters.AddWithValue("$id", subjectId);
            if (kind is not null) command.Parameters.AddWithValue("$kind", kind.Value.ToString());
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Enum.TryParse<RelationKind>(reader.GetString(2), out var parsed)) parsed = RelationKind.Unknown;
                if (!Enum.TryParse<RelationPreviewKind>(reader.IsDBNull(8) ? null : reader.GetString(8), out var strength))
                    strength = RelationPreviewKind.None;
                rows.Add(new RelationLink(
                    reader.GetString(0),
                    reader.GetString(1),
                    parsed,
                    reader.GetString(3),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetInt64(6))
                {
                    PreviewText = reader.IsDBNull(7) ? null : reader.GetString(7),
                    PreviewKind = strength,
                    MediaKind = reader.IsDBNull(9) ? "other" : reader.GetString(9),
                    DurationSec = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                    RefPath = reader.IsDBNull(11) ? null : reader.GetString(11),
                    DeepLink = reader.IsDBNull(12) ? null : reader.GetString(12),
                    TargetSubjectId = reader.IsDBNull(13) ? null : reader.GetString(13),
                });
            }
            return (IReadOnlyList<RelationLink>)rows;
        });

    /// <summary>
    /// 反查跨资源边：以 <paramref name="fromRef"/> 为起点的全部边（含 <paramref name="relation"/>
    /// 为 null 时的所有关系）。**返回的是列表**——一条起点可以有多条边，这是设计而非例外。
    /// </summary>
    public IReadOnlyList<RelationXref> ReadXrefs(string fromRef, string? relation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromRef);
        return _cache.Read(connection =>
        {
            var rows = new List<RelationXref>();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT from_ref, to_ref, relation, from_kind, to_kind, confidence, detail
                FROM {XrefTable}
                WHERE from_ref = $ref{(relation is null ? string.Empty : " AND relation = $relation")}
                ORDER BY relation, to_ref
                """;
            command.Parameters.AddWithValue("$ref", fromRef);
            if (relation is not null) command.Parameters.AddWithValue("$relation", relation);
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(ReadXref(reader));
            return (IReadOnlyList<RelationXref>)rows;
        });
    }

    /// <summary>反查跨资源边：以 <paramref name="toRef"/> 为终点的全部边（多对多的另一侧）。</summary>
    public IReadOnlyList<RelationXref> ReadXrefsTo(string toRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toRef);
        return _cache.Read(connection =>
        {
            var rows = new List<RelationXref>();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT from_ref, to_ref, relation, from_kind, to_kind, confidence, detail
                FROM {XrefTable} WHERE to_ref = $ref ORDER BY relation, from_ref
                """;
            command.Parameters.AddWithValue("$ref", toRef);
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(ReadXref(reader));
            return (IReadOnlyList<RelationXref>)rows;
        });
    }

    private static RelationXref ReadXref(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
        reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
        reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6));

    /// <summary>跨资源边数（诊断/测试用）。</summary>
    public int ReadXrefCount() => _cache.Read(static connection => Count(connection, XrefTable));

    /// <summary>
    /// 反查：引用了某个资源键的全部对象 id（资源预览的「关联资源」板块用）。
    /// <paramref name="refKey"/> 必须与 <see cref="RelationLink.RefKey"/> 的口径一致。
    /// </summary>
    public IReadOnlyList<string> ReadSubjectIdsByRef(string refKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refKey);
        return _cache.Read(connection =>
        {
            var rows = new List<string>();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT subject_id FROM {SubjectsByRefTable} WHERE ref_key = $key ORDER BY subject_id";
            command.Parameters.AddWithValue("$key", refKey);
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(reader.GetString(0));
            return (IReadOnlyList<string>)rows;
        });
    }

    /// <summary>对象数（诊断/测试用）。</summary>
    public int ReadSubjectCount()
        => _cache.Read(static connection => Count(connection, SubjectsTable));

    /// <summary>关联边数（诊断/测试用）。</summary>
    public int ReadLinkCount()
        => _cache.Read(static connection => Count(connection, LinksTable));

    // ── 写 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 单事务整图落盘（先清空三张表再写）：<c>subjects</c> + <c>links</c> +
    /// <c>subjects_by_ref</c>（反向索引由 <c>links</c> 直接派生，不额外算）。
    /// 中途失败整体回滚，旧库保持可用。
    /// </summary>
    public void PersistGraph(RelationIndexSource source, RelationGraph graph)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(graph);
        _cache.EnsureSchema();
        _cache.Write((connection, transaction) =>
        {
            using (var pragma = Command(connection, transaction, "PRAGMA temp_store=MEMORY; PRAGMA cache_size=-8192;"))
                pragma.ExecuteNonQuery();

            Execute(connection, transaction, $"DELETE FROM {SubjectsTable}");
            Execute(connection, transaction, $"DELETE FROM {LinksTable}");
            Execute(connection, transaction, $"DELETE FROM {SubjectsByRefTable}");
            Execute(connection, transaction, $"DELETE FROM {XrefTable}");

            using (var meta = Command(connection, transaction, """
                INSERT INTO index_meta (source_key, signature) VALUES ($key, $signature)
                ON CONFLICT(source_key) DO UPDATE SET signature = $signature
                """))
            {
                meta.Parameters.AddWithValue("$key", source.SourceKey);
                meta.Parameters.AddWithValue("$signature", source.Signature);
                meta.ExecuteNonQuery();
            }

            using (var insertSubject = Command(connection, transaction, $"""
                INSERT INTO {SubjectsTable}
                    (subject_id, subject_kind, category_label, display_name, subtitle, character, sort_key,
                     cover_ref, preview_text, link_count)
                VALUES ($id, $kind, $label, $name, $subtitle, $character, $sort, $cover, $preview, $count)
                ON CONFLICT(subject_id) DO UPDATE SET
                    subject_kind = $kind, category_label = $label, display_name = $name,
                    subtitle = $subtitle, character = $character, sort_key = $sort,
                    cover_ref = $cover, preview_text = $preview, link_count = $count
                """))
            {
                var id = insertSubject.Parameters.Add("$id", SqliteType.Text);
                var kind = insertSubject.Parameters.Add("$kind", SqliteType.Text);
                var label = insertSubject.Parameters.Add("$label", SqliteType.Text);
                var name = insertSubject.Parameters.Add("$name", SqliteType.Text);
                var subtitle = insertSubject.Parameters.Add("$subtitle", SqliteType.Text);
                var character = insertSubject.Parameters.Add("$character", SqliteType.Text);
                var sort = insertSubject.Parameters.Add("$sort", SqliteType.Text);
                var cover = insertSubject.Parameters.Add("$cover", SqliteType.Text);
                var preview = insertSubject.Parameters.Add("$preview", SqliteType.Text);
                var count = insertSubject.Parameters.Add("$count", SqliteType.Integer);
                foreach (var subject in graph.Subjects)
                {
                    id.Value = subject.SubjectId;
                    kind.Value = subject.SubjectKind;
                    label.Value = Nullable(subject.CategoryLabel);
                    name.Value = Nullable(subject.DisplayName);
                    subtitle.Value = Nullable(subject.Subtitle);
                    character.Value = Nullable(subject.Character);
                    sort.Value = Nullable(subject.SortKey);
                    cover.Value = Nullable(subject.CoverRef);
                    preview.Value = Nullable(subject.PreviewText);
                    count.Value = subject.LinkCount;
                    insertSubject.ExecuteNonQuery();
                }
            }

            using var insertLink = Command(connection, transaction, $"""
                INSERT INTO {LinksTable}
                    (subject_id, category, kind, ref_key, display, detail, size_bytes,
                     preview_text, preview_kind, media_kind, duration_sec, ref_path, deep_link, target_subject_id)
                VALUES ($id, $category, $kind, $ref, $display, $detail, $size,
                        $previewText, $previewKind, $mediaKind, $duration, $refPath, $deepLink, $target)
                ON CONFLICT(subject_id, kind, ref_key) DO UPDATE SET
                    category = $category, display = $display, detail = $detail, size_bytes = $size,
                    preview_text = $previewText, preview_kind = $previewKind, media_kind = $mediaKind,
                    duration_sec = $duration, ref_path = $refPath, deep_link = $deepLink,
                    target_subject_id = $target
                """);
            using var insertRef = Command(connection, transaction, $"""
                INSERT INTO {SubjectsByRefTable} (ref_key, subject_id, category) VALUES ($ref, $id, $category)
                ON CONFLICT(ref_key, subject_id) DO UPDATE SET category = $category
                """);
            using var insertXref = Command(connection, transaction, $"""
                INSERT INTO {XrefTable} (from_ref, to_ref, relation, from_kind, to_kind, confidence, detail)
                VALUES ($from, $to, $relation, $fromKind, $toKind, $confidence, $detail)
                ON CONFLICT(from_ref, relation, to_ref) DO UPDATE SET
                    from_kind = $fromKind, to_kind = $toKind, confidence = $confidence, detail = $detail
                """);
            var linkId = insertLink.Parameters.Add("$id", SqliteType.Text);
            var linkCategory = insertLink.Parameters.Add("$category", SqliteType.Text);
            var linkKind = insertLink.Parameters.Add("$kind", SqliteType.Text);
            var linkRef = insertLink.Parameters.Add("$ref", SqliteType.Text);
            var linkDisplay = insertLink.Parameters.Add("$display", SqliteType.Text);
            var linkDetail = insertLink.Parameters.Add("$detail", SqliteType.Text);
            var linkSize = insertLink.Parameters.Add("$size", SqliteType.Integer);
            var linkPreviewText = insertLink.Parameters.Add("$previewText", SqliteType.Text);
            var linkPreviewKind = insertLink.Parameters.Add("$previewKind", SqliteType.Text);
            var linkMediaKind = insertLink.Parameters.Add("$mediaKind", SqliteType.Text);
            var linkDuration = insertLink.Parameters.Add("$duration", SqliteType.Real);
            var linkRefPath = insertLink.Parameters.Add("$refPath", SqliteType.Text);
            var linkDeepLink = insertLink.Parameters.Add("$deepLink", SqliteType.Text);
            var linkTarget = insertLink.Parameters.Add("$target", SqliteType.Text);
            var refRef = insertRef.Parameters.Add("$ref", SqliteType.Text);
            var refId = insertRef.Parameters.Add("$id", SqliteType.Text);
            var refCategory = insertRef.Parameters.Add("$category", SqliteType.Text);
            var xrefFrom = insertXref.Parameters.Add("$from", SqliteType.Text);
            var xrefTo = insertXref.Parameters.Add("$to", SqliteType.Text);
            var xrefRelation = insertXref.Parameters.Add("$relation", SqliteType.Text);
            var xrefFromKind = insertXref.Parameters.Add("$fromKind", SqliteType.Text);
            var xrefToKind = insertXref.Parameters.Add("$toKind", SqliteType.Text);
            var xrefConfidence = insertXref.Parameters.Add("$confidence", SqliteType.Text);
            var xrefDetail = insertXref.Parameters.Add("$detail", SqliteType.Text);
            foreach (var link in graph.Links)
            {
                linkId.Value = link.SubjectId;
                linkCategory.Value = link.Category;
                linkKind.Value = link.Kind.ToString();
                linkRef.Value = link.RefKey;
                linkDisplay.Value = link.Display;
                linkDetail.Value = (object?)link.Detail ?? DBNull.Value;
                linkSize.Value = link.SizeBytes;
                linkPreviewText.Value = Nullable(link.PreviewText);
                // 强度按**名字**落库（不是数字）：枚举加成员时不会让旧库的语义整体错位。
                linkPreviewKind.Value = Nullable(link.PreviewKind.ToString());
                linkMediaKind.Value = Nullable(link.MediaKind);
                linkDuration.Value = (object?)link.DurationSec ?? DBNull.Value;
                linkRefPath.Value = Nullable(link.RefPath);
                linkDeepLink.Value = Nullable(link.DeepLink);
                linkTarget.Value = Nullable(link.TargetSubjectId);
                insertLink.ExecuteNonQuery();

                refRef.Value = link.RefKey;
                refId.Value = link.SubjectId;
                refCategory.Value = link.Category;
                insertRef.ExecuteNonQuery();
            }

            // 跨资源边：多对多（一个 from_ref 可以有多条），所以这里是独立的表、独立的循环。
            foreach (var xref in graph.Xrefs)
            {
                xrefFrom.Value = xref.FromRef;
                xrefTo.Value = xref.ToRef;
                xrefRelation.Value = xref.Relation;
                xrefFromKind.Value = Nullable(xref.FromKind);
                xrefToKind.Value = Nullable(xref.ToKind);
                xrefConfidence.Value = Nullable(xref.Confidence);
                xrefDetail.Value = Nullable(xref.Detail);
                insertXref.ExecuteNonQuery();
            }
        });
    }

    /// <summary>整库删除（含边车文件）；下次使用会自动重建。「清空缓存」入口用。</summary>
    public void DeleteDatabase() => _cache.DeleteDatabase();

    /// <summary>清空业务表但保留 index_meta（诊断用）。</summary>
    public void ClearTables() => _cache.ClearTables();

    // ── 内部工具 ─────────────────────────────────────────────────────

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

    private static object Nullable(string? value) => value is null ? DBNull.Value : value;

    /// <summary>读一列可空文本：NULL → null（与空串区分开，空串是「有值但为空」）。</summary>
    private static string? NullableText(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int Count(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table}";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
