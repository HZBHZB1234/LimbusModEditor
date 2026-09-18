using System.Diagnostics;
using LimbusModEditor.Application.Caching;
using LimbusModEditor.Application.SpineData;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 「未被任何维基页面绑定的 Spine」这条链路的<b>真实数据</b>回归。
///
/// <para>背景：Spine 三件套一直都在本地缓存里，但只有「被维基页面绑定的那一小批」
/// 会出现在界面上。战斗用的那批挂在 <c>Assets/Resources_moved/Prefab/SD/**</c>，
/// <b>路径名里没有任何 Spine 字样</b>，旧判据把它们全部挡在取数之前 ——
/// 实测抽样 24 个「未绑定」候选，0 个进得了引用链。</para>
///
/// <para><b>门控</b>：缺真实素材（索引库 / Unity 缓存 / 候选）时直接 return ——
/// 没有游戏数据的 CI 必须绿。断言的都是真实量：骨架名与字节数、图集正文、纹理页字节。</para>
///
/// <para><b>为什么只取几条</b>：解一套要整读一个大 bundle，实测平均约 2 s
/// （见报告 §4）。全库 700 个候选逐个跑要二十多分钟，不能进测试。
/// 这里取样几条 <c>SD/Personality</c>（实测命中率最高的那组，见
/// <c>artifacts/workbuddy-reports/spine-unbound.md</c>）。</para>
/// </summary>
public sealed class SpineUnboundDiscoveryTests
{
    private const string SdPersonalityPattern = "%/Prefab/SD/Personality/%Appearance.prefab";

    private readonly ITestOutputHelper _output;

    public SpineUnboundDiscoveryTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// <b>本轮的核心回归</b>：一批从未被任何维基页面绑定的 <c>SD</c> prefab，
    /// 现在能走引用链取出真正的三件套（骨架 + 图集 + 纹理页）。
    /// 这条测试红了就说明「未绑定的 Spine 在界面上可见」这件事坏了。
    /// </summary>
    [Fact]
    public async Task Unbound_sd_appearance_prefabs_now_resolve_the_full_triple()
    {
        var dbPath = RealIndexDatabase();
        if (dbPath is null)
        {
            _output.WriteLine("跳过：找不到真实索引库 artifacts/publish-win-x64/cache/unity-cache-index.db");
            return;
        }

        var bound = BoundSpineRefKeys(dbPath);
        var candidates = SdCandidates(dbPath, bound, sampleSize: 6);
        if (candidates.Count == 0)
        {
            _output.WriteLine($"跳过：索引库里没有可用的 {SdPersonalityPattern} 候选（bundle 可能被 Unity 清了）");
            return;
        }

        var gateway = SpineDataGatewayFactory.Create(dbPath);
        var resolved = 0;
        var attempted = 0;
        foreach (var entry in candidates)
        {
            attempted++;
            var watch = Stopwatch.StartNew();
            var (data, error) = await gateway.GetSpineDataByPathAsync(entry);
            watch.Stop();

            if (data is null)
            {
                // SD 目录里确实混着不是 Spine 的 prefab（判据只放行、不认定），
                // 所以「取不到」本身不算失败 —— 但原因必须说得出中文、不能是异常。
                _output.WriteLine($"[{attempted}] ✗ {Path.GetFileName(entry)}（{watch.Elapsed.TotalSeconds:0.0}s）{error}");
                Assert.False(string.IsNullOrWhiteSpace(error),
                    $"{entry}：取不到时也必须给出中文原因，不能静默返回 null");
                continue;
            }

            resolved++;
            _output.WriteLine($"[{attempted}] ✓ {Path.GetFileName(entry)} 骨架={data.SkeletonBytes.Length}B " +
                $"图集={data.AtlasText.Length}B 纹理={data.PageBytes.Count} 页（{watch.Elapsed.TotalSeconds:0.0}s）");

            Assert.True(data.HasSkeleton, $"{entry}：骨架字节为空");
            Assert.False(string.IsNullOrWhiteSpace(data.AtlasText), $"{entry}：图集正文为空");
            Assert.True(data.HasTextures, $"{entry}：没有任何一页纹理");
            foreach (var page in data.PageBytes)
                Assert.True(page.Value.Length > 0, $"{entry}：纹理页 {page.Key} 解码结果为空");
        }

        Assert.True(resolved > 0,
            $"取样 {attempted} 个未绑定的 SD prefab，一个都没解出三件套 —— " +
            "「未绑定 Spine 可见」这条链路断了（旧行为就是 0 个，见报告）");
    }

    /// <summary>
    /// 名册能列出未绑定的 SD 挂点，且<b>不解任何素材</b>。
    ///
    /// <para>名册本身要扫一遍 127 万行的 <c>assets</c>（容器路径是 <c>LIKE '%…%'</c>，
    /// 用不上索引），实测 1～2 s —— <b>这是索引扫描的钱，不是解包的钱</b>。
    /// 要锁住的是「名册没有走解包」：解一套实测约 2 s，名册列几百条却仍在同一量级，
    /// 所以阈值取「远小于 每套 2s × 条数」，而不是一个绝对秒数。</para>
    /// </summary>
    [Fact]
    public async Task Catalog_lists_unbound_sd_entries_without_resolving_any_bundle()
    {
        var dbPath = RealIndexDatabase();
        if (dbPath is null)
        {
            _output.WriteLine("跳过：找不到真实索引库");
            return;
        }

        var gateway = SpineDataGatewayFactory.Create(dbPath);
        var watch = Stopwatch.StartNew();
        var result = await gateway.BrowseCatalogWithSummaryAsync(
            new SpineCatalogQuery(null, OnlyUnbound: true, 0, 200));
        watch.Stop();
        var summary = result.Summary;
        var page = result.Page;

        _output.WriteLine($"名册概览：挂点 {summary.Total} · 已绑定 {summary.Bound} · 未绑定 {summary.Unbound} " +
            $"· bundle 不在本机 {summary.BundleMissing}（概览+一页 200 条共 {watch.Elapsed.TotalSeconds:0.00}s）");

        if (summary.Total == 0)
        {
            _output.WriteLine("跳过：名册为空（索引库可能不含 prefab 行）");
            return;
        }

        Assert.True(summary.Total >= summary.Bound, "总数不该少于已绑定数");
        Assert.Equal(summary.Total - summary.Bound, summary.Unbound);
        Assert.True(page.Total > 0, "只看未绑定时应当还有条目（全库绝大多数 Spine 都没有页面绑定）");

        // 名册只查索引库：整册远快于「按需解一套」。解一套实测约 2s，
        // 名册一次取数（含 200 条明细 + 概览 + File.Exists 探测）不该到那个量级的十倍。
        Assert.True(watch.Elapsed.TotalSeconds < 20,
            $"名册查询耗时 {watch.Elapsed.TotalSeconds:0.00}s —— 名册不该解任何 bundle（解一套就要约 2s）");

        // 名册条目必须带着「它在哪」的信息，前端才点得开。
        foreach (var item in page.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.RefKey));
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.False(string.IsNullOrWhiteSpace(item.Group));
        }
        _output.WriteLine("名册样例：" + string.Join("、", page.Items.Take(5).Select(i => $"{i.Name}[{i.Group}]")));
    }

    /// <summary>
    /// 结果缓存真的省掉了重复解析：同一条取两次，第二次必须快一个数量级。
    /// </summary>
    [Fact]
    public async Task Second_resolve_of_the_same_entry_is_served_from_cache()
    {
        var dbPath = RealIndexDatabase();
        if (dbPath is null)
        {
            _output.WriteLine("跳过：找不到真实索引库");
            return;
        }

        var candidates = SdCandidates(dbPath, BoundSpineRefKeys(dbPath), sampleSize: 6);
        if (candidates.Count == 0)
        {
            _output.WriteLine("跳过：没有可用候选");
            return;
        }

        var gateway = SpineDataGatewayFactory.Create(dbPath);
        string? hit = null;
        double firstSeconds = 0;
        foreach (var entry in candidates)
        {
            var watch = Stopwatch.StartNew();
            var (data, _) = await gateway.GetSpineDataByPathAsync(entry);
            watch.Stop();
            if (data is null) continue;
            hit = entry;
            firstSeconds = watch.Elapsed.TotalSeconds;
            break;
        }

        if (hit is null)
        {
            _output.WriteLine("跳过：取样的候选里没有能解开的（bundle 可能被清了）");
            return;
        }

        var secondWatch = Stopwatch.StartNew();
        var (again, _) = await gateway.GetSpineDataByPathAsync(hit);
        secondWatch.Stop();

        Assert.NotNull(again);
        _output.WriteLine($"首次 {firstSeconds:0.000}s → 缓存命中 {secondWatch.Elapsed.TotalSeconds:0.000}s（{hit}）");
        Assert.True(secondWatch.Elapsed.TotalSeconds < Math.Max(0.05, firstSeconds / 5),
            $"第二次取同一条用了 {secondWatch.Elapsed.TotalSeconds:0.000}s，首次 {firstSeconds:0.000}s —— 结果缓存没生效");
    }

    /// <summary>
    /// <b>同一路径多行时不能只试第一行</b>：真实索引库里 <c>Prefab/SD/**</c> 的 682 个路径
    /// 各有两行 —— <c>type_id=1</c>（GameObject，prefab 真根节点）与 <c>type_id=43</c>
    /// （PrefabImporter，正文是导入设置、<b>不是</b>对象图）。老实现只取第一行，
    /// 于是约一半的 SD prefab 会先在 type-43 行上失败并报「引用链里没有骨架与图集」，
    /// 而它旁边的 type-1 行本来是能解开的。
    ///
    /// <para>实测样本：<c>10103_Yisang_SwordGroupAppearance.prefab</c> —— type-43 行解不出，
    /// type-1 行解出骨架 <c>Yisang_blade_idle</c> 24348 字节 + 图集 592 字节 + 纹理。</para>
    /// </summary>
    [Fact]
    public async Task Prefab_paths_with_two_rows_resolve_through_the_gameobject_row()
    {
        var dbPath = RealIndexDatabase();
        if (dbPath is null)
        {
            _output.WriteLine("跳过：找不到真实索引库");
            return;
        }

        var multiRow = MultiRowPrefabEntries(dbPath, limit: 4);
        if (multiRow.Count == 0)
        {
            _output.WriteLine("跳过：索引库里没有「同一路径两行」的 prefab（数据形态可能已变）");
            return;
        }

        var gateway = SpineDataGatewayFactory.Create(dbPath);
        var resolved = 0;
        foreach (var entry in multiRow)
        {
            var (data, error) = await gateway.GetSpineDataByPathAsync(entry);
            _output.WriteLine(data is null
                ? $"✗ {Path.GetFileName(entry)} —— {error}"
                : $"✓ {Path.GetFileName(entry)} 骨架={data.SkeletonBytes.Length}B 图集={data.AtlasText.Length}B 纹理={data.PageBytes.Count} 页");
            if (data is not null) resolved++;
        }

        // 取样的这几条来自「实测能解开的 SD/Personality」，两条命中里至少要有一条通 ——
        // 全 0 说明多行回退没生效（老行为就是大约一半解不出）。
        Assert.True(resolved > 0,
            $"取样 {multiRow.Count} 个多行 prefab，一个都没解出 —— 多行回退（type-1 优先）没生效");
    }

    /// <summary>真的有两行（type 1 + 43）的 SD prefab 路径，且 bundle 在本机。</summary>
    private static List<string> MultiRowPrefabEntries(string dbPath, int limit)
    {
        var list = new List<string>();
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT a.container_entry, COUNT(DISTINCT a.type_id) AS kinds, MAX(b.data_path) AS data_path
            FROM assets a JOIN bundles b ON a.bundle_id = b.id
            WHERE a.container_entry LIKE '%/Prefab/SD/Personality/%Appearance.prefab'
            GROUP BY a.container_entry
            HAVING kinds > 1
            ORDER BY a.container_entry";
        using var reader = command.ExecuteReader();
        while (reader.Read() && list.Count < limit)
        {
            var entry = reader.GetString(0);
            if (reader.IsDBNull(2) || !File.Exists(reader.GetString(2))) continue;
            list.Add(entry);
        }
        return list;
    }

    /// <summary>仓库里的真实索引库（发布产物缓存；只读引用）。</summary>
    private static string? RealIndexDatabase()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "artifacts", "publish-win-x64", "cache", "unity-cache-index.db");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    /// <summary>维基已经绑定过的 Spine ref_key（这些不算「未绑定」，要排除）。</summary>
    private static HashSet<string> BoundSpineRefKeys(string indexDbPath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var relationDb = Path.Combine(
            Path.GetDirectoryName(indexDbPath)!, WorkbenchCachePaths.RelationIndexFileName);
        if (!File.Exists(relationDb)) return set;

        using var connection = new SqliteConnection($"Data Source={relationDb};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT ref_key FROM links WHERE kind='Spine'";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(0)) set.Add(reader.GetString(0));
        return set;
    }

    /// <summary>未被绑定、且 bundle 在本机的 <c>SD/Personality</c> 候选（取前几条就够）。</summary>
    private static List<string> SdCandidates(string dbPath, HashSet<string> bound, int sampleSize)
    {
        var list = new List<string>();
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT DISTINCT a.container_entry, b.data_path
            FROM assets a JOIN bundles b ON a.bundle_id = b.id
            WHERE a.container_entry LIKE $pattern
            ORDER BY a.container_entry";
        command.Parameters.AddWithValue("$pattern", SdPersonalityPattern);
        using var reader = command.ExecuteReader();
        while (reader.Read() && list.Count < sampleSize)
        {
            var entry = reader.GetString(0);
            var bundlePath = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (bound.Contains(entry)) continue;
            if (!File.Exists(bundlePath)) continue;
            list.Add(entry);
        }
        return list;
    }
}
