using System.Text;
using LimbusModEditor.Application.SpineData;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// <see cref="SpineExportService"/> 的规则回归。
///
/// <para>导出是唯一会<b>往用户磁盘写东西</b>的 Spine 通路，所以这里锁住的都是「写坏了会怎样」
/// 的那几条：不造假文件、不覆盖已有文件、单条失败不中断整批、名字不能写出目录之外、
/// 取消后不谎报成功。</para>
///
/// <para>用假网关喂确定的字节：真实数据的端到端证据在
/// <c>SpineExportRealDataTests</c> 与 CLI <c>spine-export-ipc</c>，这里只管规则。</para>
/// </summary>
public sealed class SpineExportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lme-spine-export-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 临时目录清理失败不影响断言 */ }
    }

    private static SpineRawData Sample(string label = "sample", string atlas = "back.png\nsize:64,64\n") => new(
        SkeletonBytes: Encoding.UTF8.GetBytes("{\"skeleton\":{\"spine\":\"4.0.64\"},\"bones\":[]}"),
        SkeletonFormat: "json",
        AtlasText: atlas,
        PageBytes: new Dictionary<string, byte[]> { ["back.png"] = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3] },
        AvailablePages: ["back.png"],
        Label: label);

    /// <summary>可编排的假网关：按 refKey 给出数据或中文原因，并记录被问过的键。</summary>
    private sealed class FakeGateway : ISpineDataGateway
    {
        private readonly Dictionary<string, (SpineRawData? Data, string? Error)> _responses;

        public FakeGateway(Dictionary<string, (SpineRawData?, string?)> responses) => _responses = responses;

        public List<string> Asked { get; } = [];
        public int Calls { get; private set; }

        public Task<(SpineRawData? Data, string? Error)> GetSpineDataByPathAsync(
            string containerEntry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Asked.Add(containerEntry);
            if (_responses.TryGetValue(containerEntry, out var hit)) return Task.FromResult(hit);
            return Task.FromResult<(SpineRawData?, string?)>((null, "bundle 不在本机：这条素材所在文件已被 Unity 缓存清理。"));
        }

        public Task<(SpineRawData? Data, string? Error)> GetSpineDataAsync(
            string assetId, CancellationToken cancellationToken = default)
            => GetSpineDataByPathAsync(assetId, cancellationToken);

        public Task<IReadOnlyList<SpineRawData>> GetSpineDataBatchAsync(
            IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SpineSetInfo>> EnumerateCompleteSetsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SpineSetInfo>> FindByFolderPrefixAsync(
            string folderPrefix, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SpineCatalogPage> BrowseCatalogAsync(
            SpineCatalogQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SpineCatalogResult> BrowseCatalogWithSummaryAsync(
            SpineCatalogQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SpineCatalogSummary> SummarizeCatalogAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void InvalidateCache() { }
    }

    private static FakeGateway GatewayOf(params (string RefKey, SpineRawData? Data, string? Error)[] entries)
        => new(entries.ToDictionary(e => e.RefKey, e => (e.Data, e.Error), StringComparer.OrdinalIgnoreCase));

    /// <summary>导出目录里的全部文件（相对路径 → 字节数）。</summary>
    private Dictionary<string, long> FilesOnDisk()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_root)) return map;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(".lme-tmp-", StringComparison.Ordinal)) continue; // 临时文件不该留下
            map[Path.GetRelativePath(_root, file).Replace('\\', '/')] = new FileInfo(file).Length;
        }
        return map;
    }

    /// <summary>
    /// <b>三件套按原始形态落盘</b>：<c>&lt;骨架名&gt;/&lt;骨架名&gt;.json</c>、
    /// <c>.atlas.txt</c>、纹理页<b>按图集页名</b>（<c>back.png</c>，就是 spine 的 <c>materials</c> 顺序）。
    /// </summary>
    [Fact]
    public async Task Exports_skeleton_atlas_and_pages_in_their_original_shape()
    {
        const string key = "Assets/Resources_moved/Prefab/SpineIllustPrefab/10103_gacksung.prefab";
        var gateway = GatewayOf((key, Sample("10103_gacksung"), null));
        var service = new SpineExportService(gateway);

        var result = await service.ExportAsync([key], _root);

        Assert.True(result.Ok);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);

        var files = FilesOnDisk();
        Assert.Equal(
            ["10103_gacksung/10103_gacksung.atlas.txt", "10103_gacksung/10103_gacksung.json", "10103_gacksung/back.png"],
            files.Keys.OrderBy(k => k, StringComparer.Ordinal));

        // 骨架必须是真 JSON（以 { 开头），PNG 必须是真 PNG 魔数 —— 不造假文件。
        Assert.Equal((byte)'{', File.ReadAllBytes(Path.Combine(_root, "10103_gacksung/10103_gacksung.json"))[0]);
        var png = File.ReadAllBytes(Path.Combine(_root, "10103_gacksung/back.png"));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png.Take(4));
    }

    /// <summary>响应里的逐文件清单必须与磁盘一致（字节数取自真实 stat，不是内存估算）。</summary>
    [Fact]
    public async Task Reported_file_sizes_match_what_actually_landed_on_disk()
    {
        const string key = "Assets/a.prefab";
        var gateway = GatewayOf((key, Sample("a"), null));
        var service = new SpineExportService(gateway);

        var result = await service.ExportAsync([key], _root);

        var onDisk = FilesOnDisk();
        var reported = result.Items.Single().Files.ToDictionary(f => f.RelativePath, f => f.Bytes);
        Assert.Equal(onDisk.Count, reported.Count);
        foreach (var (path, bytes) in reported)
        {
            Assert.True(onDisk.ContainsKey(path), $"响应说有 {path}，磁盘上没有");
            Assert.Equal(onDisk[path], bytes);
            Assert.True(bytes > 0, $"{path} 是空文件");
        }
        Assert.Equal(onDisk.Values.Sum(), result.TotalBytes);
    }

    /// <summary>
    /// <b>不覆盖已有文件（默认策略）</b>：第二次导出不重写、字节数不变、如实列在 skippedFiles 里。
    /// 导出的东西不该悄悄盖掉用户已有的产物。
    /// </summary>
    [Fact]
    public async Task Existing_files_are_skipped_by_default_and_reported()
    {
        const string key = "Assets/a.prefab";
        var gateway = GatewayOf((key, Sample("a"), null));
        var service = new SpineExportService(gateway);

        await service.ExportAsync([key], _root);
        var before = FilesOnDisk();
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(_root, "a/a.json"));

        var second = await service.ExportAsync([key], _root);

        Assert.Equal(before, FilesOnDisk());
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(_root, "a/a.json")));
        var item = second.Items.Single();
        Assert.Empty(item.Files);
        Assert.Equal(3, item.SkippedFiles.Count);
        Assert.Contains("跳过", second.Overwrite);
        Assert.Contains("跳过", second.Info);
    }

    /// <summary>显式要求覆盖时才重写（仍走原子写，不留半个文件）。</summary>
    [Fact]
    public async Task Overwrite_policy_rewrites_existing_files()
    {
        const string key = "Assets/a.prefab";
        var gateway = GatewayOf((key, Sample("a"), null));
        var service = new SpineExportService(gateway);
        await service.ExportAsync([key], _root);

        var result = await service.ExportAsync([key], _root, SpineExportOverwrite.Overwrite);

        Assert.Equal(3, result.Items.Single().Files.Count);
        Assert.Empty(result.Items.Single().SkippedFiles);
        Assert.Contains("覆盖", result.Overwrite);
    }

    /// <summary>
    /// <b>取不到就如实报，不造假文件</b>：中文原因原样透出（区分得了「bundle 不在本机」与
    /// 「按正文判定不是 Spine」），且目标目录里<b>一个字节都不该多出来</b>。
    /// </summary>
    [Fact]
    public async Task Missing_source_writes_nothing_and_keeps_the_chinese_reason()
    {
        const string missing = "Assets/nope.prefab";
        const string notSpine = "Assets/Resources_moved/Prefab/SD/Abnormality/1080_Thing.prefab";
        var gateway = GatewayOf(
            (missing, null, "bundle 不在本机：这条素材所在文件已被 Unity 缓存清理。"),
            (notSpine, null, "同目录里找不到骨架 JSON（*.json），也无法从引用链里找到骨架与图集。"));
        var service = new SpineExportService(gateway);

        var result = await service.ExportAsync([missing, notSpine], _root);

        Assert.False(result.Ok);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(2, result.FailedCount);
        Assert.Empty(FilesOnDisk()); // 没有任何文件被凭空造出来
        Assert.Contains("bundle 不在本机", result.Items[0].Reason);
        Assert.Contains("找不到骨架", result.Items[1].Reason);
    }

    /// <summary>
    /// <b>单条失败不中断整批</b>：坏的一条夹在中间，后面的好条目照常导出。
    /// </summary>
    [Fact]
    public async Task One_failure_does_not_abort_the_rest_of_the_batch()
    {
        var gateway = GatewayOf(
            ("Assets/ok1.prefab", Sample("ok1"), null),
            ("Assets/bad.prefab", null, "bundle 不在本机：这条素材所在文件已被 Unity 缓存清理。"),
            ("Assets/ok2.prefab", Sample("ok2"), null));
        var service = new SpineExportService(gateway);

        var result = await service.ExportAsync(["Assets/ok1.prefab", "Assets/bad.prefab", "Assets/ok2.prefab"], _root);

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.False(result.Ok); // 整批 flag 如实为 false（有失败），但成功的两条真在盘上
        var files = FilesOnDisk();
        Assert.Contains("ok1/ok1.json", files.Keys);
        Assert.Contains("ok2/ok2.json", files.Keys);
        Assert.DoesNotContain(files.Keys, k => k.StartsWith("bad", StringComparison.OrdinalIgnoreCase));

        // 顺序与请求一致，且失败那条的位置没被打乱。
        Assert.Equal(["Assets/ok1.prefab", "Assets/bad.prefab", "Assets/ok2.prefab"],
            result.Items.Select(i => i.RefKey));
    }

    /// <summary>
    /// 名字里的路径分隔与非法字符必须被净化 —— 否则 <c>../</c> 之类会写到导出目录<b>之外</b>。
    /// 素材名来自图集正文与 prefab 名，不能假设它干净。
    /// </summary>
    [Fact]
    public async Task Hostile_source_names_cannot_escape_the_target_directory()
    {
        const string key = "Assets/evil.prefab";
        var evil = new SpineRawData(
            Encoding.UTF8.GetBytes("{}"), "json", "../escape.png\nsize:1,1\n",
            new Dictionary<string, byte[]> { ["../../pwned.png"] = [0x89, 0x50, 0x4E, 0x47] },
            ["../../pwned.png"], "../evil");
        var gateway = GatewayOf((key, evil, null));
        var service = new SpineExportService(gateway);

        var result = await service.ExportAsync([key], _root);

        Assert.True(result.Ok);
        var rootFull = Path.GetFullPath(_root);
        foreach (var file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
            Assert.StartsWith(rootFull, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);

        // 关键性质是「留在导出目录内」：分隔符被净化成下划线，所以路径里不该再有目录跳转段
        // （`../x` / `..\x` / 结尾的 `..`）。名字里残留的 `..` 字面量无害，但它不能构成一段路径。
        Assert.All(result.Items.Single().Files, f =>
        {
            var segments = f.RelativePath.Split('/');
            Assert.DoesNotContain("..", segments);
            Assert.DoesNotContain(".", segments);
        });
        Assert.DoesNotContain(FilesOnDisk().Keys, k => k.Split('/').Contains(".."));
    }

    /// <summary>
    /// <b>取消后不谎报成功</b>：未开始的那几条标成「已取消」，已写出的文件保留
    /// （原子写保证每个文件要么完整要么不存在）。
    /// </summary>
    [Fact]
    public async Task Cancellation_marks_the_untouched_entries_as_cancelled()
    {
        var gateway = GatewayOf(
            ("Assets/ok.prefab", Sample("ok"), null),
            ("Assets/never.prefab", Sample("never"), null));
        var service = new SpineExportService(gateway);

        using var cts = new CancellationTokenSource();
        var progress = new Progress<SpineExportProgress>(p =>
        {
            // 第一条开始时就取消：第二条（以及任何后续）都不该被处理。
            if (p.Current == 1) cts.Cancel();
        });

        var result = await service.ExportAsync(
            ["Assets/ok.prefab", "Assets/never.prefab"], _root,
            progress: progress, cancellationToken: cts.Token);

        Assert.True(result.Cancelled);
        var never = result.Items.Single(i => i.RefKey == "Assets/never.prefab");
        Assert.False(never.Ok);
        Assert.Contains("已取消", never.Reason);
        Assert.DoesNotContain("Assets/never.prefab", gateway.Asked);
    }

    /// <summary>进度逐条上报（批量要能看见「第几条 / 共几条」）。</summary>
    [Fact]
    public async Task Progress_reports_each_entry_with_its_ordinal()
    {
        var gateway = GatewayOf(
            ("Assets/a.prefab", Sample("a"), null),
            ("Assets/b.prefab", Sample("b"), null));
        var service = new SpineExportService(gateway);
        var seen = new List<SpineExportProgress>();

        // 用同步收集：Progress<T> 会走同步上下文，这里直接实现 IProgress 保证顺序确定。
        await service.ExportAsync(["Assets/a.prefab", "Assets/b.prefab"], _root,
            progress: new InlineProgress<SpineExportProgress>(seen.Add));

        Assert.Equal(2, seen.Count);
        Assert.Equal((1, 2), (seen[0].Current, seen[0].Total));
        Assert.Equal((2, 2), (seen[1].Current, seen[1].Total));
        Assert.Equal("Assets/b.prefab", seen[1].RefKey);
        Assert.All(seen, p => Assert.False(string.IsNullOrWhiteSpace(p.Message)));
    }

    /// <summary>空输入与空目录各自给中文原因，不抛异常、不建目录。</summary>
    [Fact]
    public async Task Empty_inputs_report_a_chinese_reason()
    {
        var service = new SpineExportService(GatewayOf());

        var noKeys = await service.ExportAsync([], _root);
        Assert.False(noKeys.Ok);
        Assert.Contains("refKey", noKeys.Info);

        var noDirectory = await service.ExportAsync(["Assets/a.prefab"], "  ");
        Assert.False(noDirectory.Ok);
        Assert.Contains("导出目录", noDirectory.Info);
    }

    /// <summary>二进制骨架用 <c>.skel</c> 扩展名（不硬编 <c>.json</c>）。</summary>
    [Fact]
    public async Task Binary_skeleton_is_written_with_a_skel_extension()
    {
        const string key = "Assets/bin.prefab";
        var binary = new SpineRawData([0x00, 0x01, 0x02, 0x03], "binary", "p.png\nsize:1,1\n",
            new Dictionary<string, byte[]> { ["p.png"] = [0x89, 0x50, 0x4E, 0x47] }, ["p.png"], "bin");
        var service = new SpineExportService(GatewayOf((key, binary, null)));

        await service.ExportAsync([key], _root);

        Assert.True(File.Exists(Path.Combine(_root, "bin/bin.skel")));
        Assert.False(File.Exists(Path.Combine(_root, "bin/bin.json")));
    }

    /// <summary>页名没带扩展名时补 <c>.png</c>（我们落盘的就是 PNG 编码结果）。</summary>
    [Fact]
    public async Task Page_names_without_an_extension_get_a_png_suffix()
    {
        const string key = "Assets/bare.prefab";
        var bare = new SpineRawData(Encoding.UTF8.GetBytes("{}"), "json", "back\nsize:1,1\n",
            new Dictionary<string, byte[]> { ["back"] = [0x89, 0x50, 0x4E, 0x47] }, ["back"], "bare");
        var service = new SpineExportService(GatewayOf((key, bare, null)));

        await service.ExportAsync([key], _root);

        Assert.True(File.Exists(Path.Combine(_root, "bare/back.png")));
    }

    /// <summary>导出不覆盖时不该在目标目录留下任何临时文件（原子写的清理纪律）。</summary>
    [Fact]
    public async Task No_temp_files_are_left_behind()
    {
        const string key = "Assets/a.prefab";
        var service = new SpineExportService(GatewayOf((key, Sample("a"), null)));

        await service.ExportAsync([key], _root);

        Assert.DoesNotContain(
            Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories),
            f => Path.GetFileName(f).Contains(".lme-tmp-", StringComparison.Ordinal));
    }

    /// <summary>同步执行的 <see cref="IProgress{T}"/>（测试里要确定顺序，不走线程池）。</summary>
    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public InlineProgress(Action<T> report) => _report = report;
        public void Report(T value) => _report(value);
    }
}
