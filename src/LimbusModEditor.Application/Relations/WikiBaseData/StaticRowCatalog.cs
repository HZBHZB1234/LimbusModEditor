using System.Text.Json;
using LimbusModEditor.Application.Caching;
using LimbusModEditor.Application.StaticMods;
using Microsoft.Data.Sqlite;
using NLog;

namespace LimbusModEditor.Application.Relations.WikiBaseData;

/// <summary>
/// 静态表正文的行集合解析（<c>{"list":[…]}</c> / 顶层数组 / 单对象三种形态都认）。
/// 独立成类是为了让「按行匹配」逻辑可以脱离 Unity 缓存做单元测试。
/// </summary>
public static class StaticTableRows
{
    /// <summary>把一张静态表的正文解析成行集合。空/非 UTF-8（null）/解析失败 → 空表。</summary>
    public static IReadOnlyList<JsonElement> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
            return EnumerateRows(doc.RootElement).Select(r => r.Clone()).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>在行集合里按 <paramref name="keyProperty"/> 精确匹配数值主键（坑 1：行级必须回表，不信文件级 links）。</summary>
    public static JsonElement? Find(IReadOnlyList<JsonElement> rows, string keyProperty, long key)
    {
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty(keyProperty, out var value)) continue;
            if (value.ValueKind != JsonValueKind.Number) continue;
            if (value.TryGetInt64(out var parsed) && parsed == key) return row;
        }
        return null;
    }

    private static IEnumerable<JsonElement> EnumerateRows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray()) yield return item;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "list", "dataList", "dataArray" })
            {
                if (root.TryGetProperty(propertyName, out var array) && array.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in array.EnumerateArray()) yield return item;
                    yield break;
                }
            }
            yield return root; // 单对象表（如 season-info）
        }
    }
}

/// <summary>
/// 静态表「按行读取」目录：给定表名 + 数值主键，读出该表正文并在<b>表内</b>做行级精确匹配。
///
/// <para><b>为什么要有这个</b>：维基人格页的基础数据/技能/被动数值都在静态表 JSON 的行里，
/// 而 relation-index 的 <c>links</c> 只到文件级（还带噪声——含同数字的文件都会被链上）。
/// 行级归属必须回表按主键匹配（调查报告 <c>base-data-sources.md</c> §3 坑 1）。</para>
///
/// <para><b>正文缓存</b>：经 <see cref="StaticIndexService.LoadDocumentAsync"/> 读正文，
/// <c>cacheDocument: true</c> —— 首次现读 Unity 缓存后落 <c>documents</c> 表
/// （当前该缓存为空，启用后后续生成直接命中）。</para>
///
/// <para><b>只读游戏数据</b>：bundle 只读；唯一写入是编辑器自己的
/// <c>static-tables.db</c> 正文缓存。</para>
/// </summary>
public sealed class StaticRowCatalog : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly StaticIndexService _service;
    private readonly StaticBundleLocation _location;
    private readonly Dictionary<string, StaticTableEntry?> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<JsonElement>> _rows = new(StringComparer.OrdinalIgnoreCase);
    private int _cacheHits;
    private int _freshReads;

    private StaticRowCatalog(StaticIndexService service, StaticBundleLocation location)
    {
        _service = service;
        _location = location;
        foreach (var entry in service.Load(StaticIndexSource.From(location)).Entries)
            _tables[entry.Name] = entry;
    }

    /// <summary>
    /// 建目录：定位静态 bundle（与 <c>WikiAutoGenerationService.ReadStaticFacts</c> 同一口径——
    /// 索引里的源键在缓存根下命中；热点热修换过 bundle 时回落 catalog 重定位）。
    /// 前提缺失返回 null（静态表只是少一路输入，不阻断生成）。
    /// </summary>
    public static StaticRowCatalog? TryCreate(
        string cacheDirectory, string? unityCacheDirectory, string? gameDirectory)
    {
        try
        {
            var store = new StaticTableIndexStore(cacheDirectory);
            var sourceKey = store.ReadSourceKey();
            var resolved = StaticIndexService.LocateForReads(
                store, gameDirectory, StaticIndexService.CacheRoots(unityCacheDirectory));
            if (resolved is null || !resolved.IsCached)
            {
                Log.Debug("静态数据 bundle 未定位或缓存里没有该条目：基础数据分节跳过");
                return null;
            }

            // 定位到的 bundle 换过内容哈希（游戏热修）→ 索引里的 PathId/偏移还是旧 bundle 的，
            // 先按新 bundle 重建元数据索引，再建目录（此后后续生成直接走快路径探针）。
            if (!string.Equals(sourceKey, resolved.InnerHash, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("静态表源键 {0} → {1}：重建静态表元数据索引", sourceKey ?? "(无)", resolved.InnerHash);
                var service = new StaticIndexService(store);
                service.RebuildAsync(resolved, StaticIndexSource.From(resolved)).GetAwaiter().GetResult();
            }

            var catalog = new StaticRowCatalog(new StaticIndexService(store), resolved);
            Log.Debug("静态行目录就绪：索引表 {0} 张", catalog._tables.Count);
            return catalog;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn(ex, "建静态行目录失败：基础数据分节跳过");
            return null;
        }
    }

    /// <summary>索引里的表元数据（按表名）。</summary>
    public StaticTableEntry? Table(string tableName) => _tables.GetValueOrDefault(tableName);

    /// <summary>
    /// 按表名 + 主键属性 + 数值主键取行（表正文惰性加载并缓存；未命中返回 null，不猜）。
    /// </summary>
    public JsonElement? Row(string tableName, string keyProperty, long key)
    {
        if (!TryGetRows(tableName, out var rows)) return null;
        return StaticTableRows.Find(rows, keyProperty, key);
    }

    /// <summary>表是否存在（不加载正文）。</summary>
    public bool HasTable(string tableName) => _tables.ContainsKey(tableName);

    /// <summary>
    /// 表名候选：先<b>精确同名</b>（如 <c>passive</c>），再按前缀展开（如 <c>personality-</c> →
    /// 16 张人格表），按名称排序保证确定性。用于「扫一类表找行」的场景。
    /// </summary>
    public IEnumerable<string> TableNames(string prefixOrName)
    {
        if (_tables.ContainsKey(prefixOrName)) yield return prefixOrName;
        foreach (var name in _tables.Keys
                     .Where(n => n.Length != prefixOrName.Length &&
                                 n.StartsWith(prefixOrName, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            yield return name;
    }

    /// <summary>诊断：正文缓存命中数 / 现读数。</summary>
    public (int CacheHits, int FreshReads) DocumentStats => (_cacheHits, _freshReads);

    private bool TryGetRows(string tableName, out IReadOnlyList<JsonElement> rows)
    {
        if (_rows.TryGetValue(tableName, out var cached))
        {
            rows = cached;
            return true;
        }
        rows = [];

        if (!_tables.TryGetValue(tableName, out var entry) || entry is null) return false;
        if (!_documents.TryGetValue(tableName, out string? text))
        {
            // cacheDocument:true —— 首次现读后落 documents 缓存，后续生成直接命中。
            var document = _service
                .LoadDocumentAsync(_location, entry, cacheDocument: true)
                .GetAwaiter().GetResult();
            text = document.Text;
            _documents[tableName] = text;
            if (document.FromCache) _cacheHits++;
            else _freshReads++;
            if (text is null)
                Log.Debug("静态表 {0} 非 UTF-8 或读不到正文：该表行级匹配跳过", tableName);
        }

        rows = StaticTableRows.Parse(text);
        _rows[tableName] = rows;
        return true;
    }

    public void Dispose()
    {
        // JsonDocument 都在 Parse 里即读即弃（行元素已 Clone），这里没有要释放的非托管资源；
        // 保留 Dispose 是为了未来持有 JsonDocument 时不改调用方。
    }
}
