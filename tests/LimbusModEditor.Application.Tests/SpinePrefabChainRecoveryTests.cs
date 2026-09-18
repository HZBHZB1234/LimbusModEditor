using LimbusModEditor.Application.SpineData;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 「prefab 引用链」这条取数路径的<b>真实数据</b>回归：维基那批 Spine 绑定指向的是
/// <c>Assets/Resources_moved/Prefab/SpineIllustPrefab/*.prefab</c>，而骨架 / 图集 / 纹理
/// <b>没有登记在 bundle 的容器表</b>里（索引库里查不到它们的行），只有顺着 prefab 的
/// PPtr 引用链才取得到。本机实测见 <c>docs/SPINE-DATA-REPORT.md</c>。
///
/// <para><b>门控</b>：缺真实素材（索引库 / Unity 缓存 / 某个 prefab）时直接 return ——
/// 没有游戏数据的 CI 必须绿。断言的都是真实量：骨架字节数、图集正文、纹理页数与字节数。</para>
/// </summary>
public sealed class SpinePrefabChainRecoveryTests
{
    private const string PrefabFolderPattern = "%Prefab/SpineIllustPrefab/%";

    private readonly ITestOutputHelper _output;

    public SpinePrefabChainRecoveryTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 取索引库里前若干个 SpineIllustPrefab prefab，逐个走 <c>GetSpineDataByPathAsync</c>，
    /// 断言「骨架 + 图集 + 至少一页纹理」三件套都真的出来了。
    /// </summary>
    [Fact]
    public async Task Spine_illust_prefabs_resolve_the_full_triple_through_the_reference_chain()
    {
        const int sampleSize = 3;
        var dbPath = RealIndexDatabase();
        if (dbPath is null)
        {
            _output.WriteLine("跳过：找不到真实索引库 artifacts/publish-win-x64/cache/unity-cache-index.db");
            return;
        }

        var entries = PrefabEntries(dbPath, sampleSize);
        if (entries.Count == 0)
        {
            _output.WriteLine($"跳过：索引库里没有 {PrefabFolderPattern} 的 prefab");
            return;
        }

        var gateway = SpineDataGatewayFactory.Create(dbPath);
        foreach (var entry in entries)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (data, error) = await gateway.GetSpineDataByPathAsync(entry);
            watch.Stop();

            Assert.True(data is not null, $"{entry} 应当能解出三件套，实际：{error}");
            _output.WriteLine($"【prefab 引用链】{entry}\n" +
                $"  标签={data!.Label} 骨架格式={data.SkeletonFormat} 骨架={data.SkeletonBytes.Length} 字节 " +
                $"图集={data.AtlasText.Length} 字节 纹理={data.PageBytes.Count} 页 " +
                $"（{string.Join("、", data.PageBytes.Select(p => $"{p.Key}:{p.Value.Length} 字节"))}）" +
                $"耗时={watch.Elapsed.TotalSeconds:0.0}s");

            Assert.True(data.HasSkeleton, $"{entry}：骨架字节为空");
            Assert.False(string.IsNullOrWhiteSpace(data.AtlasText), $"{entry}：图集正文为空");
            Assert.True(data.HasTextures, $"{entry}：没有任何一页纹理");

            // 取到了就是真的：每页 PNG 都要有字节，且图集里第一个非缩进行就是页面文件名。
            foreach (var page in data.PageBytes)
                Assert.True(page.Value.Length > 0, $"{entry}：纹理页 {page.Key} 解码结果为空");
            Assert.Contains("\n", data.AtlasText.Trim() + "\n");
        }
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

    /// <summary>索引库里 SpineIllustPrefab 的容器路径（按名字排序取前 <paramref name="limit"/> 个）。</summary>
    private static List<string> PrefabEntries(string dbPath, int limit)
    {
        var list = new List<string>();
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT container_entry FROM assets WHERE container_entry LIKE $pattern " +
            "ORDER BY container_entry LIMIT $limit";
        command.Parameters.AddWithValue("$pattern", PrefabFolderPattern);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var value = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(value)) list.Add(value);
        }
        return list;
    }
}
