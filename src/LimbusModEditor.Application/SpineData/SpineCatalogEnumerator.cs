using LimbusModEditor.Application.Relations;
using Microsoft.Data.Sqlite;
using NLog;

namespace LimbusModEditor.Application.SpineData;

/// <summary>
/// Spine 名册的一条：<b>还没解素材</b>，只有「它在哪、叫什么、大概是什么」。
/// 真正的三件套要靠 <see cref="ISpineDataGateway.GetSpineDataByPathAsync"/> 按需解。
/// </summary>
/// <param name="RefKey">容器路径（= 取数用的键）。</param>
/// <param name="Name">显示名（prefab 文件名去掉 <c>.prefab</c>，如 <c>10103_Yisang_SwordGroupAppearance</c>）。</param>
/// <param name="Folder">所在容器目录。</param>
/// <param name="Group">归类（中文，见 <see cref="SpineCatalogGroups"/>）。</param>
/// <param name="BundlePresent">它所在的 bundle 文件此刻在不在本机（不在 = 缓存被 Unity 清掉了）。</param>
/// <param name="BoundPageCount">有多少个维基页面绑定了它（0 = 界面上从来没出现过）。</param>
public sealed record SpineCatalogEntry(
    string RefKey,
    string Name,
    string Folder,
    string Group,
    bool BundlePresent,
    int BoundPageCount);

/// <summary>名册查询：关键词 + 是否只看未绑定 + 分页。</summary>
/// <param name="Keyword">骨架名/路径的子串（不区分大小写；空 = 不过滤）。</param>
/// <param name="OnlyUnbound">true = 只列没有任何页面绑定的（本轮要「接成可见」的那批）。</param>
/// <param name="Offset">跳过多少条（分页）。</param>
/// <param name="Limit">最多返回多少条。</param>
public sealed record SpineCatalogQuery(string? Keyword, bool OnlyUnbound, int Offset, int Limit);

/// <summary>名册一页 + 总数（前端做分页/计数用）。</summary>
public sealed record SpineCatalogPage(int Total, IReadOnlyList<SpineCatalogEntry> Items);

/// <summary>名册一次取数的完整结果：概览 + 一页明细（共用同一次扫描）。</summary>
public sealed record SpineCatalogResult(SpineCatalogSummary Summary, SpineCatalogPage Page);

/// <summary>名册概览计数。</summary>
/// <param name="Total">全部挂点数。</param>
/// <param name="Bound">被至少一个维基页面绑定的挂点数。</param>
/// <param name="Unbound">没有任何页面绑定的挂点数（界面上看不见的那批）。</param>
/// <param name="BundleMissing">其中 bundle 文件此刻不在本机的（Unity 缓存被清，非代码可补）。</param>
public sealed record SpineCatalogSummary(int Total, int Bound, int Unbound, int BundleMissing);

/// <summary>名册的归类（纯规则，可单测）。</summary>
public static class SpineCatalogGroups
{
    /// <summary>按容器路径归类到中文栏目。</summary>
    public static string Of(string containerEntry)
    {
        if (string.IsNullOrEmpty(containerEntry)) return "其它";
        if (containerEntry.Contains("/SpineIllustPrefab/", StringComparison.OrdinalIgnoreCase)) return "人格立绘";
        if (containerEntry.Contains("/Prefab/SD/Enemy/", StringComparison.OrdinalIgnoreCase)) return "敌方单位";
        if (containerEntry.Contains("/Prefab/SD/Abnormality/", StringComparison.OrdinalIgnoreCase)) return "异想体";
        if (containerEntry.Contains("/Prefab/SD/Personality/", StringComparison.OrdinalIgnoreCase)) return "人格(战斗)";
        if (containerEntry.Contains("/Prefab/SD/EGO/", StringComparison.OrdinalIgnoreCase)) return "E.G.O";
        if (containerEntry.Contains("/Prefab/SD/", StringComparison.OrdinalIgnoreCase)) return "战斗其它";
        return "其它";
    }
}

/// <summary>
/// Spine 名册枚举：<b>只查索引库</b>（assets/bundles/strings + relation 的绑定计数），
/// <b>一个 bundle 都不解</b>。这是「可浏览」这条需求的关键 —— 解一套实测平均 2.1 s，
/// 几百套一次性解不可接受；名册必须毫秒级，明细按需取。
/// </summary>
internal sealed class SpineCatalogEnumerator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly string _dbPath;

    public SpineCatalogEnumerator(string dbPath) => _dbPath = dbPath;

    /// <summary>
    /// 名册查询。<paramref name="relationDbPath"/> 给的是绑定计数来源
    /// （relation-index.db）；它不在时绑定数一律 0，只影响「已绑定/未绑定」的分组，
    /// 不影响名册本身。
    ///
    /// <para><b>一次取数同时给出概览与分页</b>：名册要扫一遍 127 万行的 <c>assets</c>
    /// （容器路径是 <c>LIKE '%…%'</c>，用不上索引），所以「概览 + 一页」必须共用同一次扫描 ——
    /// 分成两次调用就是把这个开销付两遍。</para>
    /// </summary>
    public SpineCatalogResult QueryWithSummary(SpineCatalogQuery query, string? relationDbPath)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            var boundCounts = LoadBoundCounts(relationDbPath);
            var all = LoadEntries(boundCounts);
            var summary = Summarize(all);
            return new SpineCatalogResult(summary, Filter(all, query));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Spine 名册查询失败（关键词={0}，只看未绑定={1}）", query.Keyword, query.OnlyUnbound);
            return new SpineCatalogResult(new SpineCatalogSummary(0, 0, 0, 0), new SpineCatalogPage(0, []));
        }
    }

    /// <summary>名册查询（不带概览；给只要一页的调用方）。</summary>
    public SpineCatalogPage Query(SpineCatalogQuery query, string? relationDbPath)
        => QueryWithSummary(query, relationDbPath).Page;

    /// <summary>概览计数。</summary>
    public SpineCatalogSummary Summarize(string? relationDbPath)
    {
        try
        {
            return Summarize(LoadEntries(LoadBoundCounts(relationDbPath)));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Spine 名册概览失败");
            return new SpineCatalogSummary(0, 0, 0, 0);
        }
    }

    private static SpineCatalogSummary Summarize(List<SpineCatalogEntry> all)
    {
        var bound = all.Count(e => e.BoundPageCount > 0);
        return new SpineCatalogSummary(all.Count, bound, all.Count - bound, all.Count(e => !e.BundlePresent));
    }

    private static SpineCatalogPage Filter(List<SpineCatalogEntry> all, SpineCatalogQuery query)
    {
        var keyword = query.Keyword?.Trim();
        var filtered = all.Where(e =>
        {
            if (query.OnlyUnbound && e.BoundPageCount > 0) return false;
            if (string.IsNullOrEmpty(keyword)) return true;
            return e.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || e.RefKey.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        var offset = Math.Max(0, query.Offset);
        var limit = query.Limit <= 0 ? 100 : query.Limit;
        return new SpineCatalogPage(filtered.Count, filtered.Skip(offset).Take(limit).ToList());
    }

    /// <summary>
    /// 名册条目：索引库里所有「值得试一次引用链」的挂点。
    ///
    /// <para>判据与 <see cref="RelationDisplayRules.IsSpinePath"/> 同源（<b>不另立</b>）：
    /// <c>SpineIllustPrefab/*.prefab</c> + <c>Prefab/SD/**/*.prefab</c>。
    /// 路径只决定「值不值得试」，是不是真 Spine 由正文判定 —— 所以名册会有一定比例的
    /// 条目在按需取数时报「引用链里没有骨架与图集」，那是<b>正确结果</b>（见报告实测比例）。</para>
    /// </summary>
    private List<SpineCatalogEntry> LoadEntries(IReadOnlyDictionary<string, int> boundCounts)
    {
        // 按「容器路径」去重，**不能**按 (容器路径, bundle 路径) 去重：
        // 同一个 prefab 路径可能出现在多个 bundle 里（实测 920 个不同路径对应 1637 行，
        // 683 个路径出现不止一次），按两列 DISTINCT 会让名册与概览计数虚高近一倍
        // （实测概览会报「挂点 1637」而不是 920）。
        var byPath = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using (var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT a.container_entry, b.data_path
                FROM assets a
                JOIN bundles b ON a.bundle_id = b.id
                WHERE a.container_entry LIKE $illust OR a.container_entry LIKE $sd";
            command.Parameters.AddWithValue("$illust", "%/SpineIllustPrefab/%.prefab");
            command.Parameters.AddWithValue("$sd", "%/Prefab/SD/%.prefab");

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var entry = reader.GetString(0);
                if (!RelationDisplayRules.IsSpinePath(entry)) continue; // 与取数预检同源，名册不能比取数更宽
                var bundlePath = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var present = BundlePresent(bundlePath);
                // 同名多份时：任一份的 bundle 还在本机就算「在」，不能因为先读到缺失的那份就判死。
                if (byPath.TryGetValue(entry, out var known))
                    byPath[entry] = known || present;
                else
                    byPath[entry] = present;
            }
        }

        var list = new List<SpineCatalogEntry>(byPath.Count);
        foreach (var (entry, bundlePresent) in byPath.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            list.Add(new SpineCatalogEntry(
                entry,
                NameOf(entry),
                SpinePathRules.FolderOf(entry),
                SpineCatalogGroups.Of(entry),
                bundlePresent,
                boundCounts.TryGetValue(entry, out var n) ? n : 0));
        }
        return list;
    }

    private static string NameOf(string entry)
    {
        var name = SpinePathRules.FileNameOf(entry);
        return name.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
            ? name[..^".prefab".Length]
            : name;
    }

    private static bool BundlePresent(string bundlePath)
    {
        if (string.IsNullOrWhiteSpace(bundlePath)) return false;
        try { return File.Exists(bundlePath); }
        catch (Exception) { return false; }
    }

    /// <summary>ref_key → 绑定它的页面数（relation-index.db 不在时给空表）。</summary>
    private IReadOnlyDictionary<string, int> LoadBoundCounts(string? relationDbPath)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(relationDbPath) || !File.Exists(relationDbPath)) return counts;
        try
        {
            using var connection = new SqliteConnection($"Data Source={relationDbPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT ref_key, COUNT(DISTINCT subject_id) FROM links WHERE kind='Spine' GROUP BY ref_key";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                counts[reader.GetString(0)] = reader.GetInt32(1);
            }
        }
        catch (Exception ex)
        {
            // 绑定表读不到只影响「已绑定/未绑定」的分组，不影响名册本身 —— 降级不抛。
            Log.Warn(ex, "读 Spine 绑定计数失败（名册仍可用，绑定数按 0 计）：{0}", relationDbPath);
        }
        return counts;
    }
}
