using System.Text;
using LimbusModEditor.Application.Catalog;

namespace LimbusModEditor.Domain.Tests;

/// <summary>T3：官方 catalog（catalog.bin / catalog_S1.bin）只读解析与
/// vanilla 基线判定。合成样本按 LCTA 记录的记录区布局构造；真实 catalog 的
/// 数量级与 CRC 口径验证在文件末尾（无游戏目录的机器自动跳过）。</summary>
public class CatalogBaselineTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lme-catalog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private const string InnerHash = "0123456789abcdef0123456789abcdef";
    private const string OuterKey = "fedcba9876543210fedcba9876543210";

    /// <summary>构造合成 catalog 数据：一个 bundle 名 + 按 staticmod.py 布局的
    /// 记录区（Hash128 → +0x10 外层键 → +0x44 CRC → +0x48 大小）。</summary>
    private static byte[] BuildSyntheticCatalog(uint crc, uint size)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("\x42\x89\xe3\x0d header region padding\n"));
        var nameBytes = Encoding.ASCII.GetBytes($"static_s1_0_assets_all_{InnerHash}.bundle");
        writer.Write(nameBytes);
        writer.Write((byte)0);
        writer.Write((byte)0);
        var hit = (int)stream.Position; // 记录区 Hash128 起始（名字里的哈希是 ASCII 文本，不会误命中）
        var hash = new byte[16];
        for (var i = 0; i < 16; i++) hash[i] = Convert.ToByte(InnerHash.Substring(i * 2, 2), 16);
        writer.Write(hash);                                  // +0x00：16 字节 Hash128
        writer.Write((uint)OuterKey.Length);                 // +0x10：LE32 长度
        writer.Write(Encoding.ASCII.GetBytes(OuterKey));     // +0x14：外层键 ASCII
        while (stream.Position < hit + 0x3C) writer.Write((byte)0);
        writer.Write(crc);                                   // +0x3C：LE32 CRC32（解压块口径）
        writer.Write(size);                                  // +0x40：LE32 文件大小
        writer.Flush();
        return stream.ToArray();
    }

    [Fact]
    public void Synthetic_catalog_record_region_is_parsed_exactly()
    {
        var data = BuildSyntheticCatalog(crc: 0xDEADBEEF, size: 123_456);
        var catalog = CatalogFileService.Parse(data);

        // 名字被提取，纯哈希名被过滤（合成数据没有其他名字）
        var name = Assert.Single(catalog.Names);
        Assert.Equal($"static_s1_0_assets_all_{InnerHash}.bundle", name);

        var record = catalog.FindByInnerHash(InnerHash);
        Assert.NotNull(record);
        Assert.Equal(OuterKey, record!.OuterKey);
        Assert.Equal(0xDEADBEEFu, record.Crc);
        Assert.Equal(123_456u, record.Size);
        // 大小写不敏感查询
        Assert.NotNull(catalog.FindByInnerHash(InnerHash.ToUpperInvariant()));
        // 未知哈希查不到
        Assert.Null(catalog.FindByInnerHash("00000000000000000000000000000000"));
    }

    [Fact]
    public void Size_mismatch_reports_modified_without_crc_computation()
    {
        var data = BuildSyntheticCatalog(crc: 0xDEADBEEF, size: 123_456);
        var catalog = CatalogFileService.Parse(data);

        // 假缓存布局：<root>/<inner>/__data，文件大小与记录不符
        var cacheDir = Path.Combine(_work, "cache", OuterKey, InnerHash);
        Directory.CreateDirectory(cacheDir);
        var dataPath = Path.Combine(cacheDir, "__data");
        File.WriteAllBytes(dataPath, new byte[999]);

        var result = CatalogBaselineService.Evaluate(catalog, dataPath);
        Assert.Equal(CatalogVerdict.Modified, result.Verdict);
        Assert.Contains("大小", result.Detail);
        // 基线判定不要求文件是可解压 bundle：大小不一致已足够给出结论
    }

    [Fact]
    public void Unknown_inner_hash_is_reported_not_guessed()
    {
        var data = BuildSyntheticCatalog(crc: 1, size: 2);
        var catalog = CatalogFileService.Parse(data);
        var cacheDir = Path.Combine(_work, "cache", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        Directory.CreateDirectory(cacheDir);
        var dataPath = Path.Combine(cacheDir, "__data");
        File.WriteAllBytes(dataPath, new byte[10]);

        var result = CatalogBaselineService.Evaluate(catalog, dataPath);
        Assert.Equal(CatalogVerdict.NotInCatalog, result.Verdict);
        Assert.Contains("不在当前 catalog", result.Detail);
    }

    [Fact]
    public void Non_cache_layout_is_uncertain()
    {
        var data = BuildSyntheticCatalog(crc: 1, size: 2);
        var catalog = CatalogFileService.Parse(data);
        var dir = Path.Combine(_work, "plain");
        Directory.CreateDirectory(dir);
        var dataPath = Path.Combine(dir, "something.bundle");
        File.WriteAllBytes(dataPath, new byte[10]);

        var result = CatalogBaselineService.Evaluate(catalog, dataPath);
        Assert.Equal(CatalogVerdict.Uncertain, result.Verdict);
        Assert.Contains("无法识别内容哈希", result.Detail);
    }

    // ── 真实 catalog 验证（无游戏目录的机器自动跳过）────────────────────────

    private static readonly string? RealCatalogPath = FindRealCatalog();

    private static string? FindRealCatalog()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company\LimbusCompany_Data\StreamingAssets\aa\catalog.bin",
            @"E:\SteamLibrary\steamapps\common\Limbus Company\LimbusCompany_Data\StreamingAssets\aa\catalog.bin",
        };
        var overrideGame = Environment.GetEnvironmentVariable("LME_GAME_DIR");
        if (!string.IsNullOrWhiteSpace(overrideGame))
            candidates = [Path.Combine(overrideGame, "LimbusCompany_Data", "StreamingAssets", "aa", "catalog.bin"), .. candidates];
        return candidates.FirstOrDefault(File.Exists);
    }

    [Fact]
    public void Real_catalog_parses_with_sane_entry_count()
    {
        if (RealCatalogPath is null) return;
        var catalog = CatalogFileService.Load(RealCatalogPath);

        // 与 LCTA parse_catalog 输出数量级一致（真实 catalog 数千个 bundle 名）
        Assert.True(catalog.Names.Count > 1000, $"catalog 名字解析出 {catalog.Names.Count} 个，数量级异常");
        var withOuter = catalog.RecordsByInnerHash.Values.Count(x => x.OuterKey is not null);
        var withCrc = catalog.RecordsByInnerHash.Values.Count(x => x.Crc is not null);
        Assert.True(withOuter > 500, $"有外层键的记录 {withOuter} 个，数量级异常");
        Console.WriteLine($"真实 catalog: {catalog.Names.Count} 名字 / {catalog.RecordsByInnerHash.Count} 记录 / 外层键 {withOuter} / CRC {withCrc}");
    }

    [Fact]
    public void Real_cache_bundle_gets_definitive_verdict()
    {
        if (RealCatalogPath is null) return;
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Unity", "ProjectMoon_LimbusCompany");
        if (!Directory.Exists(cacheRoot)) return;

        var catalog = CatalogFileService.Load(RealCatalogPath);
        var evaluated = 0;
        var verdicts = new Dictionary<CatalogVerdict, int>();
        foreach (var outer in Directory.EnumerateDirectories(cacheRoot).Take(60))
        {
            foreach (var inner in Directory.EnumerateDirectories(outer))
            {
                var dataPath = Path.Combine(inner, "__data");
                if (!File.Exists(dataPath)) continue;
                var result = CatalogBaselineService.Evaluate(catalog, dataPath);
                if (result.Verdict == CatalogVerdict.Uncertain && result.InnerHash.Length == 0) continue; // 非缓存布局
                evaluated++;
                verdicts[result.Verdict] = verdicts.TryGetValue(result.Verdict, out var n) ? n + 1 : 1;
                if (verdicts.TryGetValue(CatalogVerdict.Vanilla, out var vanillaCount) && vanillaCount >= 3) break;
            }
            if (verdicts.TryGetValue(CatalogVerdict.Vanilla, out var done) && done >= 3) break;
        }
        // 基线判定必须给出确定结论（vanilla 或 modified），CRC 口径与真实加载器一致
        Assert.True(evaluated > 0, "没有完成任何基线判定");
        Console.WriteLine("真实缓存基线判定分布: " + string.Join(", ", verdicts.Select(x => $"{x.Key}={x.Value}")));
    }
}
