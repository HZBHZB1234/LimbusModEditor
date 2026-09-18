using LimbusModEditor.Application.Caching;
using LimbusModEditor.Application.SpineData;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// <c>spine.export</c> 的<b>真实数据</b>回归：拿真实索引库里的两个<b>不同来源</b>的挂点
/// （<c>SpineIllustPrefab</c> 走 prefab 引用链、<c>Prefab/SD/**</c> 走 SD 形态），
/// 真的导出到临时目录，再回磁盘核对「文件非空 + 头字节正确」。
///
/// <para>为什么要回磁盘核对：导出是唯一会写用户磁盘的 Spine 通路，
/// 「服务说写了」不等于「文件真在、内容真是那个东西」。这里断言的是<b>字节事实</b>：
/// 骨架以 <c>{</c> 开头（Spine JSON）、纹理页以 PNG 魔数 <c>\x89PNG</c> 开头。</para>
///
/// <para><b>门控</b>：缺真实索引库 / Unity 缓存 / 候选时直接 return —— 没有游戏数据的 CI 保持绿。</para>
///
/// <para><b>为什么只取几条</b>：解一套要整读一个大 bundle（实测平均约 1～3 s），
/// 这里两个来源各取 1 条就够证明「文件和字节都对上」。</para>
/// </summary>
public sealed class SpineExportRealDataTests : IDisposable
{
    private const string IllustPrefabPattern = "%/Prefab/SpineIllustPrefab/%_gacksung.prefab";
    private const string SdPersonalityPattern = "%/Prefab/SD/Personality/%Appearance.prefab";

    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lme-spine-export-real", Guid.NewGuid().ToString("N"));

    public SpineExportRealDataTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 临时目录清理失败不影响断言 */ }
    }

    /// <summary>
    /// 两个不同来源的 refKey 各导一条，逐文件核对字节数与头字节。
    /// 骨架必须以 <c>{</c> 开头、纹理必须以 PNG 魔数开头 —— 只要解码/落盘任何一环错了这条就红。
    /// </summary>
    [Fact]
    public async Task Exports_from_two_different_sources_and_the_files_are_real()
    {
        var dbPath = RealIndexDatabase();
        if (dbPath is null)
        {
            _output.WriteLine("跳过：找不到真实索引库 artifacts/publish-win-x64/cache/unity-cache-index.db");
            return;
        }

        var illust = FirstPresentEntry(dbPath, IllustPrefabPattern);
        var sd = FirstPresentEntry(dbPath, SdPersonalityPattern);
        if (illust is null || sd is null)
        {
            _output.WriteLine($"跳过：两个来源里至少一个没有「bundle 在本机」的候选（illust={illust} sd={sd}）");
            return;
        }

        _output.WriteLine($"来源①（SpineIllustPrefab，走 prefab 引用链）：{illust}");
        _output.WriteLine($"来源②（SD/Personality，走 SD 形态）：{sd}");

        var gateway = SpineDataGatewayFactory.Create(dbPath);
        var service = new SpineExportService(gateway);
        var result = await service.ExportAsync([illust, sd], _root);

        foreach (var item in result.Items)
            _output.WriteLine(item.Ok
                ? $"✓ {item.Name} → {item.OutputDirectory}（{item.Files.Count} 个文件）"
                : $"✗ {item.Name} —— {item.Reason}");

        // 这两个来源的 bundle 都在本机（上面刚探过），取不到就是真回归了。
        Assert.Equal(2, result.SucceededCount);
        Assert.Empty(result.Items.Where(i => !i.Ok).Select(i => i.Reason));
        Assert.True(result.FileCount >= 6, $"两条至少各 3 个文件（骨架/图集/纹理），实得 {result.FileCount}");

        var verified = 0;
        foreach (var item in result.Items)
        {
            var skeleton = item.Files.SingleOrDefault(f => f.Role == "skeleton");
            var atlas = item.Files.SingleOrDefault(f => f.Role == "atlas");
            var textures = item.Files.Where(f => f.Role == "texture").ToList();

            Assert.NotNull(skeleton);
            Assert.NotNull(atlas);
            Assert.NotEmpty(textures);

            // ① 骨架：真 Spine JSON（以 { 开头），且文件非空、大小与响应一致。
            var skeletonBytes = ReadFile(result.OutputDirectory, skeleton!);
            Assert.Equal((byte)'{', skeletonBytes[0]);
            Assert.Equal(skeleton!.Bytes, skeletonBytes.LongLength);
            Assert.Contains("\"skeleton\"", System.Text.Encoding.UTF8.GetString(skeletonBytes));

            // ② 图集：非空文本，首行是页名（.png/.jpg）。
            var atlasBytes = ReadFile(result.OutputDirectory, atlas!);
            Assert.Equal(atlas!.Bytes, atlasBytes.LongLength);
            var atlasText = System.Text.Encoding.UTF8.GetString(atlasBytes);
            Assert.Matches(@"^\S+\.(png|jpg)", atlasText);

            // ③ 纹理页：PNG 魔数 + 非空。页名按图集 materials 顺序（如 back.png）。
            foreach (var texture in textures)
            {
                var png = ReadFile(result.OutputDirectory, texture);
                Assert.True(png.Length > 0, $"{texture.RelativePath} 是空文件");
                Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png.Take(8));
                Assert.Equal(texture.Bytes, png.LongLength);
                _output.WriteLine($"    {png.Length,10:N0}  {texture.RelativePath}");
            }
            verified++;
        }

        Assert.Equal(2, verified);
        _output.WriteLine($"落盘合计 {result.TotalBytes:N0} 字节，{result.FileCount} 个文件 —— 头字节与字节数全部核对通过");
        Assert.Contains("导出完成", result.Info);
    }

    /// <summary>
    /// <b>默认不覆盖</b>：对同一个真实挂点连导两次，第二次不重写、字节数不变、如实报跳过。
    /// </summary>
    [Fact]
    public async Task Second_export_of_the_same_real_entry_skips_existing_files()
    {
        var dbPath = RealIndexDatabase();
        if (dbPath is null)
        {
            _output.WriteLine("跳过：找不到真实索引库");
            return;
        }

        var entry = FirstPresentEntry(dbPath, IllustPrefabPattern);
        if (entry is null)
        {
            _output.WriteLine("跳过：没有「bundle 在本机」的 SpineIllustPrefab 候选");
            return;
        }

        var service = new SpineExportService(SpineDataGatewayFactory.Create(dbPath));
        var first = await service.ExportAsync([entry], _root);
        if (!first.Ok)
        {
            _output.WriteLine($"跳过：这一条没解出三件套（bundle 可能被清了）—— {first.Items[0].Reason}");
            return;
        }

        var before = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => File.GetLastWriteTimeUtc(f));

        var second = await service.ExportAsync([entry], _root);

        Assert.True(second.Ok);
        var item = second.Items.Single();
        Assert.Empty(item.Files);
        Assert.Equal(first.FileCount, item.SkippedFiles.Count);
        Assert.Equal(before, Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => File.GetLastWriteTimeUtc(f)));
        _output.WriteLine($"第二次导出：写出 0 个文件，跳过 {item.SkippedFiles.Count} 个已存在的文件（时间戳未变）");
    }

    /// <summary>
    /// 从导出<b>根目录</b>读回一个文件。注意 <see cref="SpineExportedFile.RelativePath"/>
    /// 是相对<b>根目录</b>的（如 <c>10103_gacksung/10103_gacksung.json</c>），
    /// 不是相对每条的 <c>OutputDirectory</c> —— 所以这里必须先确认它没跳出根目录。
    /// </summary>
    private static byte[] ReadFile(string outputRoot, SpineExportedFile file)
    {
        var full = Path.GetFullPath(Path.Combine(outputRoot, file.RelativePath));
        Assert.StartsWith(Path.GetFullPath(outputRoot), full, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(full), $"响应说有 {file.RelativePath}，磁盘上却没有");
        return File.ReadAllBytes(full);
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

    /// <summary>按容器路径模式取第一条「bundle 文件此刻真在本机」的 type-1（GameObject）行。</summary>
    private static string? FirstPresentEntry(string dbPath, string pattern)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT DISTINCT a.container_entry, b.data_path
            FROM assets a JOIN bundles b ON a.bundle_id = b.id
            WHERE a.container_entry LIKE $pattern AND a.type_id = 1
            ORDER BY a.container_entry";
        command.Parameters.AddWithValue("$pattern", pattern);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1)) continue;
            var dataPath = reader.GetString(1);
            if (File.Exists(dataPath)) return reader.GetString(0);
        }
        return null;
    }
}
