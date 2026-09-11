using LimbusModEditor.Application.Caching;
using LimbusModEditor.Application.Texts;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// plan-10：<c>cache/text-index.db</c> 索引缓存单测。核心是「缓存只是加速旁路，
/// 语义与现状完全一致」——两条最重要的证据测试：
/// ① <see cref="Cached_and_uncached_enumeration_and_search_return_identical_results"/>
///    （有缓存 vs 删库，返回值逐字段比对）；
/// ② <see cref="Config_content_change_invalidates_the_whole_index"/>
///    （config.json 内容变 → 整库重建，不能漏掉「玩家切换了活动语言」）。
/// </summary>
public sealed class TextIndexStoreTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lme-textindex-" + Guid.NewGuid().ToString("N"));
    private readonly string _cacheDirectory;
    private readonly TextIndexStore _store;

    public TextIndexStoreTests()
    {
        _cacheDirectory = Path.Combine(_work, "cache");
        Directory.CreateDirectory(_cacheDirectory);
        _store = new TextIndexStore(_cacheDirectory);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
        }
        catch (Exception) { /* 临时目录清理失败不影响测试结论 */ }
    }

    // ── 测试数据 ─────────────────────────────────────────────────────

    private string LangRoot => Path.Combine(_work, "LimbusCompany_Data", "lang");

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteRawFile(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    /// <summary>按真实布局搭一个合成 lang 根；返回游戏目录。</summary>
    private string SeedLangRoot(string activeLanguage = "LLC_zh-CN")
    {
        WriteFile(Path.Combine(LangRoot, "config.json"), $$"""{"lang":"{{activeLanguage}}","titleFont":"","contextFont":""}""");
        WriteFile(Path.Combine(LangRoot, activeLanguage, "AbDlg_Faust.json"), """
        {
          "dataList": [
            { "id": 1, "dialog": "浮士德会亲自处理。" },
            { "id": 2, "dialog": "原始文本二" }
          ]
        }
        """);
        WriteFile(Path.Combine(LangRoot, activeLanguage, "StoryData", "S1.json"), """{"dataList":[{"dialog":"故事文本"}]}""");
        WriteFile(Path.Combine(LangRoot, activeLanguage, "arr.json"), """[1, 2, 3]""");
        WriteFile(Path.Combine(LangRoot, "LLC_en", "en.json"), """{"only":"english"}"""); // 其他语言目录：不索引
        // 非 UTF-8 样本：GBK 编码的「你」（0xC4 0xE3）
        WriteRawFile(Path.Combine(LangRoot, activeLanguage, "gbk.json"), [0x7B, 0xC4, 0xE3, 0x7D]);
        return _work;
    }

    /// <summary>「有缓存」路径：走索引（必要时建索引）。</summary>
    private IReadOnlyList<LangTextFileInfo> EnumerateViaCache(LangTextWorkbenchService service, out bool rebuilt)
    {
        var source = TextIndexStore.DescribeSource(LangRoot);
        rebuilt = _store.EnsureSource(source);
        var files = service.EnumerateFiles(LangRoot, _store.ReadFileMap());
        _store.PersistFiles(source, files);
        return files;
    }

    /// <summary>「无缓存」路径：全新服务 + 强制删库。</summary>
    private IReadOnlyList<LangTextFileInfo> EnumerateWithoutCache()
    {
        _store.DeleteDatabase();
        var service = new LangTextWorkbenchService();
        var files = service.EnumerateFiles(LangRoot);
        _store.PersistFiles(TextIndexStore.DescribeSource(LangRoot), files);
        return files;
    }

    private void AssertSameFiles(IReadOnlyList<LangTextFileInfo> expected, IReadOnlyList<LangTextFileInfo> actual)
    {
        Assert.Equal(expected.Select(x => x.RelativePath), actual.Select(x => x.RelativePath));
        for (var i = 0; i < expected.Count; i++)
        {
            // 逐字段比对（含顺序）：缓存路径不许改变任何一处返回值。
            Assert.Equal(expected[i], actual[i]);
        }
    }

    // ── 缓存往返 ─────────────────────────────────────────────────────

    [Fact]
    public void Index_round_trips_file_rows_and_search_hits()
    {
        SeedLangRoot();
        var service = new LangTextWorkbenchService();

        var files = EnumerateViaCache(service, out var rebuilt);
        Assert.True(rebuilt); // 首次：无记录 → 整库重建
        // 合成 lang 根里活动语言目录下有 4 个文件（plan-14：根级 config.json 不再收录）。
        Assert.Equal(4, files.Count);
        Assert.DoesNotContain(files, x => x.RelativePath == "config.json");

        // 表结构 / 列名被单测钉死（plan-10 §2）。
        var columns = ReadColumns("files");
        Assert.Equal(["rel_path", "size", "mtime_ticks", "key_count", "is_utf8"], columns);
        Assert.Equal(["rel_path", "kind", "key_path", "snip", "seq"], ReadColumns("hits"));

        // 命中行的 kind 取值必须是 File / Key / Value。
        var kinds = ReadDistinctHitKinds();
        Assert.Contains("File", kinds);
        Assert.Contains("Key", kinds);
        Assert.Contains("Value", kinds);
        Assert.All(kinds, k => Assert.Contains(k, new[] { "File", "Key", "Value" }));

        // 文件名命中与键路径命中都落进了 hits（可被 SQL 直接查出来）。
        Assert.Equal(1, CountHits("File", "LLC_zh-CN/AbDlg_Faust.json", null));
        Assert.Equal(1, CountHits("Key", "LLC_zh-CN/AbDlg_Faust.json", "dataList/0/dialog"));

        // 非 UTF-8 文件被标记，不是猜出来的。
        var gbk = ReadFileRow("LLC_zh-CN/gbk.json");
        Assert.NotNull(gbk);
        Assert.False(gbk!.Value.Utf8);
        Assert.Equal(0, gbk.Value.KeyCount);

        // 缓存往返：读回来的文件条目与枚举结果逐字段一致。
        AssertSameFiles(files, _store.ReadFiles(LangRoot));

        // 二次进页面：签名命中 → 不重建。
        var again = service.EnumerateFiles(LangRoot);
        Assert.False(_store.EnsureSource(TextIndexStore.DescribeSource(LangRoot)));
        AssertSameFiles(files, again);
    }

    [Fact]
    public void Second_visit_reuses_cached_rows_without_rebuilding()
    {
        SeedLangRoot();
        var service = new LangTextWorkbenchService();
        var first = EnumerateViaCache(service, out _);

        var reloaded = _store.ReadFileMap(LangRoot);
        Assert.Equal(first.Count, reloaded.Count);
        foreach (var file in first) Assert.True(CacheHit(file, reloaded), $"应命中缓存: {file.RelativePath}");

        // 签名一致 → EnsureSource 不重建，行集不变。
        Assert.False(_store.EnsureSource(TextIndexStore.DescribeSource(LangRoot)));
        Assert.Equal(first.Count, _store.ReadFileCount());
    }

    private static bool CacheHit(LangTextFileInfo file, IReadOnlyDictionary<string, LangTextFileInfo> cached)
        => cached.TryGetValue(file.RelativePath, out var hit) && hit == file;

    // ── 签名失效 ─────────────────────────────────────────────────────

    [Fact]
    public void File_content_change_invalidates_that_file_only()
    {
        SeedLangRoot();
        var service = new LangTextWorkbenchService();
        var before = EnumerateViaCache(service, out _);

        var target = Path.Combine(LangRoot, "LLC_zh-CN", "AbDlg_Faust.json");
        WriteFile(target, """{"dataList":[{"id":1,"dialog":"改过的文本"},{"id":2,"dialog":"原始文本二"}],"added":true}""");
        var expected = new LangTextWorkbenchService().EnumerateFiles(LangRoot); // 现读（真值）

        var cachedMap = _store.ReadFileMap(LangRoot);
        var actual = service.EnumerateFiles(LangRoot, cachedMap);
        _store.PersistFiles(TextIndexStore.DescribeSource(LangRoot), actual);

        var expectedTarget = expected.Single(x => x.RelativePath == "LLC_zh-CN/AbDlg_Faust.json");
        var actualTarget = actual.Single(x => x.RelativePath == "LLC_zh-CN/AbDlg_Faust.json");
        Assert.Equal(expectedTarget, actualTarget);
        Assert.NotEqual(before.Single(x => x.RelativePath == "LLC_zh-CN/AbDlg_Faust.json").KeyCount, actualTarget.KeyCount);

        // 其余文件仍然命中缓存（没被无谓重解析）。
        AssertSameFiles(expected, actual);
    }

    [Fact]
    public void Config_content_change_invalidates_the_whole_index()
    {
        SeedLangRoot("LLC_zh-CN");
        var service = new LangTextWorkbenchService();
        var first = EnumerateViaCache(service, out _);
        Assert.Contains("LLC_zh-CN/AbDlg_Faust.json", first.Select(x => x.RelativePath));

        // 玩家切换活动语言：目录没动，只有 config.json 的内容变了。
        WriteFile(Path.Combine(LangRoot, "config.json"), """{"lang":"LLC_en","titleFont":"","contextFont":""}""");
        var current = TextIndexStore.DescribeSource(LangRoot);
        Assert.True(_store.EnsureSource(current), "config.json 内容变了必须整库重建（否则会继续读旧语言的索引）");
        Assert.Equal(0, _store.ReadFileCount());

        var afterEnumeration = service.EnumerateFiles(LangRoot, _store.ReadFileMap());
        _store.PersistFiles(current, afterEnumeration);
        Assert.Contains("LLC_en/en.json", afterEnumeration.Select(x => x.RelativePath));
        Assert.DoesNotContain("LLC_zh-CN/AbDlg_Faust.json", afterEnumeration.Select(x => x.RelativePath));

        // 换游戏目录（源键变）同样整库重建。
        Assert.False(_store.EnsureSource(current));
        var otherRoot = Path.Combine(_work, "other-game", "LimbusCompany_Data", "lang");
        Directory.CreateDirectory(otherRoot);
        WriteFile(Path.Combine(otherRoot, "config.json"), """{"lang":"LLC_zh-CN"}""");
        Assert.True(_store.EnsureSource(TextIndexStore.DescribeSource(otherRoot)));
        Assert.Equal(0, _store.ReadFileCount());
    }

    /// <summary>
    /// plan-14 的口径升级自愈：v1 库（收录过根级 <c>config.json</c>）在口径升到
    /// <see cref="TextIndexStore.IndexFormatVersion"/> 后必须被判为过期 → 整库重建，
    /// 否则目录 mtime 与 config.json 内容都没变，「新鲜」的旧库会继续吐出一行
    /// <c>config.json</c>，用户就会在文本工作台里看到一个本该消失的条目。
    /// </summary>
    [Fact]
    public void Legacy_index_content_format_is_invalidated_and_the_config_row_disappears()
    {
        SeedLangRoot();
        var service = new LangTextWorkbenchService();
        var current = TextIndexStore.DescribeSource(LangRoot);

        // 伪造一个 v1 库：签名不带口径版本，并塞一行根级 config.json（v1 的口径）。
        var legacy = current with { Signature = $"{current.DirectorySignature}|{current.ConfigContentHash}" };
        Assert.True(_store.EnsureSource(legacy));
        var configPath = Path.Combine(LangRoot, "config.json");
        _store.PersistFiles(legacy, [
            new LangTextFileInfo("config.json", configPath, new FileInfo(configPath).Length, 3, true, 0),
            .. service.EnumerateFiles(LangRoot),
        ]);
        Assert.Contains("config.json", _store.ReadFiles(LangRoot).Select(x => x.RelativePath));

        // 换到当前口径：签名不同 → 整库重建，旧行不再存在。
        Assert.True(_store.EnsureSource(current), "内容口径版本变了必须重建整库");
        var files = service.EnumerateFiles(LangRoot, _store.ReadFileMap());
        _store.PersistFiles(current, files);

        var rows = _store.ReadFiles(LangRoot).Select(x => x.RelativePath).ToList();
        Assert.DoesNotContain("config.json", rows);
        Assert.Equal(files.Count, rows.Count);
    }

    [Fact]
    public void Deleted_and_added_files_reconcile_the_rows()
    {
        SeedLangRoot();
        var service = new LangTextWorkbenchService();
        EnumerateViaCache(service, out _);

        File.Delete(Path.Combine(LangRoot, "LLC_zh-CN", "arr.json"));
        WriteFile(Path.Combine(LangRoot, "LLC_zh-CN", "StoryData", "S2.json"), """{"dataList":[{"dialog":"新增"}]}""");

        var source = TextIndexStore.DescribeSource(LangRoot);
        var files = service.EnumerateFiles(LangRoot, _store.ReadFileMap());
        _store.PersistFiles(source, files);

        var rows = _store.ReadFiles(LangRoot).Select(x => x.RelativePath).ToList();
        Assert.DoesNotContain("LLC_zh-CN/arr.json", rows);
        Assert.Contains("LLC_zh-CN/StoryData/S2.json", rows);
        Assert.Equal(files.Count, rows.Count);
        Assert.Equal(files.Count, _store.ReadFileCount());

        // 新增文件也进了命中表（否则搜索会漏掉它）。
        Assert.Equal(1, CountHits("Key", "LLC_zh-CN/StoryData/S2.json", "dataList/0/dialog"));
    }

    // ── Kind / KeyPath 命中写入与查询 ────────────────────────────────

    [Fact]
    public void Hit_rows_store_kind_keypath_and_value_text()
    {
        SeedLangRoot();
        var service = new LangTextWorkbenchService();
        EnumerateViaCache(service, out _);

        // 键命中：key_path 是展平口径，snip 与 key_path 同值（与 Search 的产物一致）。
        Assert.Equal(1, CountHits("Key", "LLC_zh-CN/AbDlg_Faust.json", "dataList/1/dialog"));
        // 值命中：key_path 指向叶子，snip 存完整值文本（查询到 Search 里才比对）。
        Assert.Equal(1, CountHits("Value", "LLC_zh-CN/AbDlg_Faust.json", "dataList/0/dialog"));
        var valueRows = ReadHitRows("Value", "LLC_zh-CN/AbDlg_Faust.json");
        Assert.Contains("浮士德会亲自处理。", valueRows);

        // 非 UTF-8 / 非法 JSON 文件只留文件名命中行（键值事实一个都不许猜）。
        var gbkRows = ReadHitRows(null, "LLC_zh-CN/gbk.json");
        Assert.Single(gbkRows);
        Assert.Equal("File", ReadDistinctHitKinds("LLC_zh-CN/gbk.json").Single());
    }

    [Fact]
    public void Hit_rows_keep_every_key_candidate_and_only_reachable_value_candidates()
    {
        SeedLangRoot();
        // 一个文件里放 30 个叶子（键候选超过 20 条上限）。
        var many = string.Join(",", Enumerable.Range(0, 30).Select(i => $$"""{"dialog":"行 {{i}}"}"""));
        WriteFile(Path.Combine(LangRoot, "LLC_zh-CN", "many.json"), $$"""{"list":[{{many}}]}""");

        var service = new LangTextWorkbenchService();
        EnumerateViaCache(service, out _);

        var kinds = ReadDistinctHitKinds("LLC_zh-CN/many.json");
        Assert.Contains("File", kinds);
        Assert.Contains("Key", kinds);
        Assert.Contains("Value", kinds);

        // 键候选一条不落（30 条）：键命中会让 per-file 计数涨到上限之上，
        // 截断键候选就会让「有缓存」漏掉搜索结果。
        Assert.Equal(30, ReadHitRows("Key", "LLC_zh-CN/many.json").Count);
        // 值候选只保留排序上轮得到被检查的那些（前 19 个键之后的值永远轮不到）。
        Assert.Equal(19, ReadHitRows("Value", "LLC_zh-CN/many.json").Count);
    }

    [Fact]
    public void Search_over_a_key_heavy_file_matches_with_and_without_cache()
    {
        SeedLangRoot();
        var many = string.Join(",", Enumerable.Range(0, 30).Select(i => $$"""{"dialog":"行 {{i}}"}"""));
        WriteFile(Path.Combine(LangRoot, "LLC_zh-CN", "many.json"), $$"""{"list":[{{many}}]}""");

        var cachedService = new LangTextWorkbenchService();
        var files = EnumerateViaCache(cachedService, out _);
        var liveService = new LangTextWorkbenchService();
        var liveFiles = liveService.EnumerateFiles(LangRoot);

        foreach (var query in new[] { "dialog", "行 25", "list/2", "行 29" })
        {
            var expected = liveService.Search(query, liveFiles);
            Assert.Equal(expected, cachedService.Search(query, files, cached: _store.ReadHits()));
        }
    }

    // ── 有缓存 vs 无缓存：返回值一致（本计划最重要的正确性证据）────────

    [Theory]
    [InlineData("Faust")]
    [InlineData("dataList")]
    [InlineData("浮士德")]
    [InlineData("dialog")]
    [InlineData("StoryData")]
    [InlineData("不存在的关键字")]
    public void Cached_and_uncached_search_return_identical_results(string query)
    {
        SeedLangRoot();
        var cachedService = new LangTextWorkbenchService();
        var files = EnumerateViaCache(cachedService, out _);

        var liveService = new LangTextWorkbenchService();
        var liveFiles = liveService.EnumerateFiles(LangRoot);
        var liveHits = liveService.Search(query, liveFiles);

        var cachedHits = cachedService.Search(query, files, cached: _store.ReadHits());
        Assert.Equal(liveHits, cachedHits);

        // 删库后（无缓存）同一条查询也必须给出同样的结果。
        var afterDeleteService = new LangTextWorkbenchService();
        var afterDeleteFiles = afterDeleteService.EnumerateFiles(LangRoot);
        Assert.Equal(liveHits, afterDeleteService.Search(query, afterDeleteFiles));
    }

    [Fact]
    public void Cached_and_uncached_enumeration_and_search_return_identical_results()
    {
        SeedLangRoot();
        var cachedService = new LangTextWorkbenchService();
        var cached = EnumerateViaCache(cachedService, out _);
        var cachedHits = cachedService.Search("dialog", cached, cached: _store.ReadHits());

        // 逐字段比对枚举结果。
        var withoutCache = EnumerateWithoutCache();
        AssertSameFiles(withoutCache, cached);

        // 删除缓存库后：枚举与搜索都不受影响（功能一致，只是变慢）。
        var liveService = new LangTextWorkbenchService();
        var live = liveService.EnumerateFiles(LangRoot);
        AssertSameFiles(cached, live);
        Assert.Equal(liveService.Search("dialog", live), cachedHits);
    }

    [Fact]
    public void Search_with_cache_respects_total_and_per_file_limits()
    {
        SeedLangRoot();
        var service = new LangTextWorkbenchService();
        var files = EnumerateViaCache(service, out _);
        var hits = _store.ReadHits();

        var capped = service.Search("dialog", files, maxTotalHits: 1, cached: hits);
        Assert.Single(capped);
        Assert.Equal(service.Search("dialog", files, maxTotalHits: 1), capped);

        var perFile = service.Search("dialog", files, maxHitsPerFile: 1, cached: hits);
        Assert.Equal(service.Search("dialog", files, maxHitsPerFile: 1), perFile);
    }

    // ── 损坏库删重建后功能可用 ───────────────────────────────────────

    [Fact]
    public void Corrupted_database_is_deleted_rebuilt_and_still_usable()
    {
        SeedLangRoot();
        var service = new LangTextWorkbenchService();
        var before = EnumerateViaCache(service, out _);
        var beforeHits = service.Search("dialog", before, cached: _store.ReadHits());
        Assert.NotEmpty(beforeHits);

        // 把库写成一堆垃圾（模拟磁盘损坏 / 被截断）。
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.WriteAllText(_store.DatabasePath, "这不是一个 SQLite 库，是一堆垃圾字节。");

        var store = new TextIndexStore(_cacheDirectory);
        var source = TextIndexStore.DescribeSource(LangRoot);
        // 建表时探测到损坏即删库重建（plan-09 已定的语义：删库重建是最便宜的修复，
        // 不往上抛——缓存只影响速度）。重建后必须报告「需要重新解析」。
        Assert.True(store.EnsureSource(source), "损坏库重建后必须要求调用方重新解析全部文件");
        Assert.True(store.WasRecreated);
        Assert.Equal(0, store.ReadFileCount());

        // 重建后功能完全可用：枚举与搜索结果与之前一致。
        var files = service.EnumerateFiles(LangRoot, store.ReadFileMap());
        store.PersistFiles(source, files);
        AssertSameFiles(before, files);
        Assert.Equal(beforeHits, service.Search("dialog", files, cached: store.ReadHits()));
        Assert.True(store.IsFresh(source));
    }

    [Fact]
    public void Deleting_the_cache_database_leaves_functionality_intact()
    {
        SeedLangRoot();
        var service = new LangTextWorkbenchService();
        var before = EnumerateViaCache(service, out _);
        var beforeHits = service.Search("Faust", before, cached: _store.ReadHits());

        _store.DeleteDatabase();
        Assert.False(File.Exists(_store.DatabasePath));
        Assert.False(_store.IsFresh(TextIndexStore.DescribeSource(LangRoot)));

        // 页面在这种状态下会走「无缓存」路径：结果一字不差。
        var live = service.EnumerateFiles(LangRoot);
        AssertSameFiles(before, live);
        Assert.Equal(beforeHits, service.Search("Faust", live));
    }

    // ── 0 字节 / 无表库自愈（真实缺陷回归）───────────────────────────

    [Fact]
    public void Zero_byte_database_file_is_repaired_instead_of_failing_with_no_such_table()
    {
        // 真实现场（本机 artifacts/publish-win-x64/cache/text-index.db = 0 字节）：
        // 库文件在但没有任何表时，以前 IsFresh → ReadSourceSignature → SELECT … FROM index_meta
        // 直接抛「SQLite Error 1: 'no such table: index_meta'」，页面显示成
        // 「载入 lang 文件失败」。缓存是纯加速旁路，缺表必须被无声补建。
        SeedLangRoot();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.WriteAllBytes(_store.DatabasePath, []);
        Assert.Equal(0, new FileInfo(_store.DatabasePath).Length);

        var store = new TextIndexStore(_cacheDirectory);
        var source = TextIndexStore.DescribeSource(LangRoot);

        Assert.False(store.IsFresh(source));          // 不抛，判定为「需要重建」
        Assert.Equal(0, store.ReadFileCount());       // 表已补建：读得动，只是空
        Assert.Empty(store.ReadFiles(LangRoot));
        Assert.Empty(store.ReadHits());
        Assert.Null(store.ReadSourceKey());

        // 补建之后完全可用：建索引、命中、搜索都与无缓存路径一致。
        var service = new LangTextWorkbenchService();
        var files = service.EnumerateFiles(LangRoot);
        store.PersistFiles(source, files);
        Assert.True(store.IsFresh(source));
        Assert.Equal(files.Count, store.ReadFileCount());
        Assert.Equal(service.Search("Faust", files), service.Search("Faust", files, cached: store.ReadHits()));
    }

    [Fact]
    public void Persistent_files_into_a_freshly_created_database_works_without_EnsureSource_first()
    {
        // 页面冷启动路径：库文件根本不存在 → PersistFiles 内部会先 EnsureSource + 建表。
        SeedLangRoot();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_store.DatabasePath)) File.Delete(_store.DatabasePath);

        var store = new TextIndexStore(_cacheDirectory);
        var source = TextIndexStore.DescribeSource(LangRoot);
        var files = new LangTextWorkbenchService().EnumerateFiles(LangRoot);
        store.PersistFiles(source, files);

        Assert.Equal(files.Count, store.ReadFileCount());
        Assert.True(store.IsFresh(source));
    }

    // ── SQL 辅助（测试直接查库，把列名与取值钉死）────────────────────

    private IReadOnlyList<string> ReadColumns(string table)
        => Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = command.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(1));
            return (IReadOnlyList<string>)names;
        });

    private int CountHits(string kind, string relPath, string? keyPath)
        => Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = keyPath is null
                ? "SELECT count(*) FROM hits WHERE kind = $kind AND rel_path = $path"
                : "SELECT count(*) FROM hits WHERE kind = $kind AND rel_path = $path AND key_path = $key";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$path", relPath);
            if (keyPath is not null) command.Parameters.AddWithValue("$key", keyPath);
            return Convert.ToInt32(command.ExecuteScalar());
        });

    private IReadOnlyList<string> ReadDistinctHitKinds(string? relPath = null)
        => Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = relPath is null
                ? "SELECT DISTINCT kind FROM hits ORDER BY kind"
                : "SELECT DISTINCT kind FROM hits WHERE rel_path = $path ORDER BY kind";
            if (relPath is not null) command.Parameters.AddWithValue("$path", relPath);
            using var reader = command.ExecuteReader();
            var kinds = new List<string>();
            while (reader.Read()) kinds.Add(reader.GetString(0));
            return (IReadOnlyList<string>)kinds;
        });

    private IReadOnlyList<string?> ReadHitRows(string? kind, string relPath)
        => Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = kind is null
                ? "SELECT snip FROM hits WHERE rel_path = $path ORDER BY seq"
                : "SELECT snip FROM hits WHERE rel_path = $path AND kind = $kind ORDER BY seq";
            command.Parameters.AddWithValue("$path", relPath);
            if (kind is not null) command.Parameters.AddWithValue("$kind", kind);
            using var reader = command.ExecuteReader();
            var values = new List<string?>();
            while (reader.Read()) values.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
            return (IReadOnlyList<string?>)values;
        });

    private (int KeyCount, bool Utf8)? ReadFileRow(string relPath)
        => Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key_count, is_utf8 FROM files WHERE rel_path = $path";
            command.Parameters.AddWithValue("$path", relPath);
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetInt32(0), reader.GetInt64(1) != 0) : ((int, bool)?)null;
        });

    private T Read<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, T> query)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_store.DatabasePath}");
        connection.Open();
        return query(connection);
    }
}
