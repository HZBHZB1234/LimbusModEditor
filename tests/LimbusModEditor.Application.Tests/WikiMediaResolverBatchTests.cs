using System.Security.Cryptography;
using System.Text;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Application.Wiki;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Tests;

/// <summary>与 <see cref="WikiMediaCoverageTests"/> 串行的测试集合：两者都读写
/// testhost 的 <c>wwwroot/data/wiki</c> 落地目录（覆盖率测试的冷缓存测量会整个删掉它），
/// 并行执行会互相拆台 —— 同一集合内 xUnit 顺序执行。</summary>
[CollectionDefinition("wiki-media")]
public sealed class WikiMediaCollection
{
}

/// <summary>
/// <see cref="WikiMediaResolver.ResolveManyAsync"/> 的批量契约（图片有界并行实现后必须仍然成立）：
/// <b>输出顺序与输入一致</b>、<b>按 ref_key 去重</b>、<b>Image 配额每页 16 条</b>、
/// 未知形态给空结果、并行不抛异常不丢槽位。
///
/// <para><b>为什么不依赖游戏数据也能测出顺序与配额</b>：<c>MaterializeImage</c> 对
/// 「落地文件已存在」的键直接给地址、不解码。测试预先在测试输出目录的
/// <c>wwwroot/data/wiki</c> 里造好这些文件，解析结果就成了可断言的确定值：
/// 命中 → <c>https://lme.data/wiki/{hash}.png</c>；超配额/未知形态 → 全 null。
/// 哈希口径与解析器一致（SHA256("Image|{refKey}") 截断 32 位十六进制）——
/// 这是与已落地文件之间的稳定契约，改口径本就该连落地文件一起迁移、此处必须响。</para>
/// </summary>
[Collection("wiki-media")]
public sealed class WikiMediaResolverBatchTests : IDisposable
{
    /// <summary>与 <c>WikiMediaResolver.ImageQuotaPerPage</c> 一致（私有常量，改动时同步这里）。</summary>
    private const int ImageQuota = 16;

    private static readonly string WikiDataDirectory =
        Path.Combine(AppContext.BaseDirectory, "wwwroot", "data", "wiki");

    private readonly string _tempDbRoot;
    private readonly List<string> _stubFiles = [];

    public WikiMediaResolverBatchTests()
    {
        // 独立的临时目录放空索引库：UnityCacheSqliteIndexStore 首查会自动建 schema，
        // 查询恒返回 0 行 —— 图片走「文件已存在」分支，其余形态诚实返回 null。
        _tempDbRoot = Path.Combine(Path.GetTempPath(), "lme-wiki-batch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDbRoot);
    }

    public void Dispose()
    {
        // 桩文件只删自己造的那几个：wwwroot/data/wiki 是与覆盖率测试共享的目录，
        // 不能整个删（覆盖率测试的冷缓存测量正在往里落真实媒体文件）。
        foreach (var file in _stubFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (IOException) { /* 删不掉不影响断言 */ }
        }
        try { if (Directory.Exists(_tempDbRoot)) Directory.Delete(_tempDbRoot, true); }
        catch (IOException) { /* 清不掉不影响断言 */ }
    }

    [Fact]
    public async Task Batch_resolution_preserves_order_quota_and_dedup_without_game_data()
    {
        var imageKeys = Enumerable.Range(0, 20).Select(i => $"test-batch/unit-{i:D3}.png").ToList();
        foreach (var key in imageKeys) PreMaterialize(key);

        var bindings = new List<WikiResourceBinding>();
        // 0–19：20 条 Image（超过 16 条配额，后 4 条必须全 null）。
        foreach (var key in imageKeys) bindings.Add(Binding(key, "Image"));
        // 20：与第 0 条同键 —— 命中去重缓存，结果必须与第 0 条一致。
        bindings.Add(Binding(imageKeys[0], "Image"));
        // 21：Spine（网关桩返回无数据 → 全 null）。
        bindings.Add(Binding("test-batch/spine.prefab", "Spine"));
        // 22：未知形态 → 全 null。
        bindings.Add(Binding("test-batch/unknown.bin", "Video"));
        // 23：音频（bank 载荷不存在 → 全 null）。
        bindings.Add(Binding(Path.Combine(_tempDbRoot, "missing.bank") + "\0no-such-event", "Audio"));

        var resolver = CreateResolver();
        var results = await resolver.ResolveManyAsync(bindings);

        Assert.Equal(bindings.Count, results.Count);
        // 顺序 + 配额：前 16 条 Image 有地址、第 17–20 条（下标 16–19）超配额全 null。
        for (var i = 0; i < ImageQuota + 4; i++)
        {
            var expected = i < ImageQuota ? ExpectedUrl(imageKeys[i]) : null;
            var actual = results[i].MediaUrl;
            Assert.True(expected is null ? actual is null : expected == actual,
                $"槽位 {i} 期望 {(expected is null ? "null" : expected)}，实际 {(actual is null ? "null" : actual)}");
        }
        // 去重：同键槽位与首次出现的结果一致。
        Assert.Equal(results[0].MediaUrl, results[20].MediaUrl);
        // 音频/Spine/未知形态并行混跑后仍是全 null（不编造地址）。
        Assert.Null(results[21].SkeletonUrl);
        Assert.Null(results[22].MediaUrl);
        Assert.Null(results[23].AudioUrl);
    }

    [Fact]
    public async Task Empty_batch_returns_empty_list()
    {
        var results = await CreateResolver().ResolveManyAsync([]);
        Assert.Empty(results);
    }

    // ── 测具 ──────────────────────────────────────────────────────────

    private WikiMediaResolver CreateResolver()
    {
        var catalog = new AssetCatalog(
            new UnityCacheSqliteIndexStore(Path.Combine(_tempDbRoot, "unity-cache-index.db")), new ModProject());
        var bankIndex = new BankIndexService(new BankIndexStore(_tempDbRoot));
        var spineData = new StubSpineGateway();
        return new WikiMediaResolver(catalog, bankIndex, spineData, new BankAudioService(), fmodDirectory: null);
    }

    private static WikiResourceBinding Binding(string refKey, string kind) => new(
        BindingId: $"b-{Guid.NewGuid():N}", EntryId: "e-test", RefKey: refKey, Kind: kind,
        Display: Path.GetFileName(refKey), SortOrder: 0);

    /// <summary>预置落地文件：文件已存在即命中，跳过解码（见 <c>MaterializeImage</c>）。</summary>
    private string PreMaterialize(string refKey)
    {
        Directory.CreateDirectory(WikiDataDirectory);
        var file = Path.Combine(WikiDataDirectory, Hash($"Image|{refKey}") + ".png");
        File.WriteAllBytes(file, "test-stub-png"u8.ToArray());
        _stubFiles.Add(file);
        return file;
    }

    private static string ExpectedUrl(string refKey) => "https://lme.data/wiki/" + Hash($"Image|{refKey}") + ".png";

    /// <summary>与 <c>WikiMediaResolver.Hash</c> 同口径：SHA256 截断 32 位十六进制。</summary>
    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32];

    /// <summary>Spine 网关桩：诚实返回「无数据」，不触碰任何真实 bundle。</summary>
    private sealed class StubSpineGateway : ISpineDataGateway
    {
        public Task<(SpineRawData? Data, string? Error)> GetSpineDataAsync(
            string assetId, CancellationToken cancellationToken = default)
            => Task.FromResult<(SpineRawData?, string?)>((null, "测试桩：无数据"));

        public Task<(SpineRawData? Data, string? Error)> GetSpineDataByPathAsync(
            string containerEntry, CancellationToken cancellationToken = default)
            => Task.FromResult<(SpineRawData?, string?)>((null, "测试桩：无数据"));

        public Task<IReadOnlyList<SpineRawData>> GetSpineDataBatchAsync(
            IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SpineRawData>>([]);

        public Task<IReadOnlyList<SpineSetInfo>> EnumerateCompleteSetsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SpineSetInfo>>([]);

        public Task<IReadOnlyList<SpineSetInfo>> FindByFolderPrefixAsync(
            string folderPrefix, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SpineSetInfo>>([]);

        public Task<SpineCatalogPage> BrowseCatalogAsync(
            SpineCatalogQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new SpineCatalogPage(0, []));

        public Task<SpineCatalogResult> BrowseCatalogWithSummaryAsync(
            SpineCatalogQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new SpineCatalogResult(new SpineCatalogSummary(0, 0, 0, 0), new SpineCatalogPage(0, [])));

        public Task<SpineCatalogSummary> SummarizeCatalogAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SpineCatalogSummary(0, 0, 0, 0));

        public void InvalidateCache() { }
    }
}
