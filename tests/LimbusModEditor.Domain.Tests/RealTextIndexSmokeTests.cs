using System.Diagnostics;
using System.Text.Json.Nodes;
using LimbusModEditor.Application.Texts;
using Xunit.Abstractions;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// plan-10：真实 lang 目录上的索引缓存冒烟（无游戏目录的机器自动跳过）。
///
/// <para>测的是三件事：① 索引覆盖真实的活动语言目录（子目录 + 根级文件，活动语言
/// <b>跟随 config.json，不写死</b>）；② 命中行能在真实文件里定位到键路径
/// （选中文件后由 <c>JsonTreeEditor.SelectPath</c> 定位）；③ 冷建索引 vs 二次进页面的实测耗时。
/// 缓存写在临时目录里，不碰程序目录的 cache/。</para>
/// </summary>
public sealed class RealTextIndexSmokeTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lme-textindex-real-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public RealTextIndexSmokeTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
        }
        catch (Exception) { /* 临时目录清理失败不影响测试结论 */ }
    }

    private static string? RealLangDir()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company\LimbusCompany_Data\lang",
            @"E:\SteamLibrary\steamapps\common\Limbus Company\LimbusCompany_Data\lang",
        };
        var overrideGame = Environment.GetEnvironmentVariable("LME_GAME_DIR");
        if (!string.IsNullOrWhiteSpace(overrideGame))
            candidates = [Path.Combine(overrideGame, "LimbusCompany_Data", "lang"), .. candidates];
        return candidates.FirstOrDefault(Directory.Exists);
    }

    [Fact]
    public void Real_lang_index_is_built_reused_and_locates_key_paths_with_measured_timings()
    {
        var langRoot = RealLangDir();
        if (langRoot is null) return; // 无游戏目录：跳过

        var store = new TextIndexStore(Path.Combine(_work, "cache"));
        var service = new LangTextWorkbenchService();
        var source = TextIndexStore.DescribeSource(langRoot);

        // 活动语言跟随 config.json（玩家可能启用汉化组目录 LLc-CN-LCTA，不能写死 LLC_zh-CN）。
        var active = service.ReadActiveLanguage(langRoot);
        Assert.False(string.IsNullOrWhiteSpace(active));

        // ── 冷建索引 ─────────────────────────────────────────────────
        Assert.True(store.EnsureSource(source), "首次应要求整库重建");
        var coldWatch = Stopwatch.StartNew();
        var files = service.EnumerateFiles(langRoot);
        store.PersistFiles(source, files);
        coldWatch.Stop();

        var languagePrefix = active + "/";
        var relatives = files.Select(x => x.RelativePath).ToList();
        // plan-14：config.json 不再是工作台文件（只用来解析活动语言 + 进索引签名）。
        Assert.DoesNotContain("config.json", relatives);
        // 全部条目都在活动语言目录之下（大小写照磁盘，config.json 里可能写成别的大小写）。
        Assert.All(relatives, x => Assert.StartsWith(languagePrefix, x, StringComparison.OrdinalIgnoreCase));
        // 子目录（StoryData）+ 根级文件并存。
        Assert.Contains(relatives, x => x.StartsWith(languagePrefix + "StoryData/", StringComparison.Ordinal));
        Assert.Contains(relatives, x => x.StartsWith(languagePrefix, StringComparison.Ordinal) && x.Count(c => c == '/') == 1);
        Assert.True(files.Count >= 1000, $"真实 lang 目录应有上千个 JSON，实际 {files.Count}");
        Assert.Equal(files.Count, store.ReadFileCount());

        // ── 二次进页面：签名命中，不再读全部文件 ──────────────────────
        Assert.False(store.EnsureSource(TextIndexStore.DescribeSource(langRoot)), "二次进页面不应重建");
        var cachedMap = store.ReadFileMap(langRoot);
        var warmWatch = Stopwatch.StartNew();
        var again = service.EnumerateFiles(langRoot, cachedMap);
        store.PersistFiles(source, again);
        warmWatch.Stop();

        Assert.Equal(files.Count, again.Count);
        for (var i = 0; i < files.Count; i++) Assert.Equal(files[i], again[i]);
        // 缓存路径应当明显快于冷路径（不做硬断言，避免慢机器误报；只在报告里给数字）。
        Assert.True(warmWatch.Elapsed < coldWatch.Elapsed,
            $"二次进页面（{warmWatch.ElapsedMilliseconds}ms）应快于冷建索引（{coldWatch.ElapsedMilliseconds}ms）");

        // ── 搜索：SQL 侧命中 + 真实文件里定位到键路径 ────────────────
        var hits = store.ReadHits();
        var searchWatch = Stopwatch.StartNew();
        var cachedHits = service.Search("dialog", again, cached: hits);
        searchWatch.Stop();
        var liveWatch = Stopwatch.StartNew();
        var liveHits = service.Search("dialog", again);
        liveWatch.Stop();

        Assert.NotEmpty(cachedHits);
        Assert.Equal(liveHits, cachedHits); // 真实数据上「有缓存 = 无缓存」

        // 真实值命中 → 该键路径能在文件里定位（JsonTreeEditor.SelectPath 的口径：
        // 展平键路径逐段展开；这里直接按同一口径在文档里走一遍）。
        var valueHit = cachedHits.FirstOrDefault(x => x.Kind == LangTextSearchKind.Value && x.KeyPath is { Length: > 0 });
        Assert.NotNull(valueHit);
        var target = again.Single(x => x.RelativePath == valueHit!.RelativePath);
        var document = JsonNode.Parse(File.ReadAllText(target.FullPath));
        var located = Walk(document, valueHit!.KeyPath!);
        Assert.NotNull(located);
        Assert.Contains(TrimEllipsis(valueHit.Snippet!), located!.ToJsonString());

        // 大文件（BattleSpeechBubbleDlg / Skills 之类）也在索引里，且命中表带了它的键。
        var big = files.OrderByDescending(x => x.SizeBytes).First();
        Assert.True(big.SizeBytes > 100_000, $"真实目录里应有 100KB 以上的表，最大 {big.RelativePath} {big.SizeBytes}");
        Assert.True(HasHits(hits, big.RelativePath), $"大文件应在命中表里: {big.RelativePath}");

        // 实测数字（审查证据；冷/热耗时与库大小）。
        _output.WriteLine(
            $"活动语言 {active} / 文件 {files.Count} 个 / 命中行 {store.ReadHitCount()} 条 / " +
            $"库 {new FileInfo(store.DatabasePath).Length / 1024}KB");
        _output.WriteLine(
            $"冷建索引 {coldWatch.ElapsedMilliseconds}ms → 二次进页面 {warmWatch.ElapsedMilliseconds}ms（含逐字段比对）");
        _output.WriteLine(
            $"搜索「dialog」：SQL 候选 + 现判 {searchWatch.ElapsedMilliseconds}ms（{cachedHits.Count} 条） vs " +
            $"逐文件现读 {liveWatch.ElapsedMilliseconds}ms（{liveHits.Count} 条）");
    }

    private static bool HasHits(IReadOnlyDictionary<string, IReadOnlyList<LangTextSearchHit>> hits, string relativePath)
        => hits.TryGetValue(relativePath, out var rows) && rows.Count > 0;

    private static string TrimEllipsis(string snippet)
        => snippet.Trim('…');

    /// <summary>按展平键路径（<c>a/b/0/c</c>）在文档里走一遍，与 JsonTreeEditor.SelectPath 同口径。</summary>
    private static JsonNode? Walk(JsonNode? node, string path)
    {
        foreach (var token in path.Split('/'))
        {
            node = node switch
            {
                JsonObject obj when obj.ContainsKey(token) => obj[token],
                JsonArray array when int.TryParse(token, out var index) && index >= 0 && index < array.Count => array[index],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }
}
