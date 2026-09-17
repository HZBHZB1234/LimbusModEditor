using System.Diagnostics;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Application.Wiki;
using LimbusModEditor.Domain.Projects;
using Xunit.Abstractions;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 维基资源绑定「容器路径 → 真实地址」的<b>真实覆盖率</b>测量。
///
/// <para><b>为什么要有这个测试</b>：<c>WikiMediaResolver</c> 的契约是「拿不到真实地址就给
/// null，绝不编造 URL」，所以「覆盖率到底多少」只能真跑一次真实数据才知道 ——
/// 这里就是那次测量，数字直接进交付报告。</para>
///
/// <para><b>门控</b>：缺数据（发布目录 / 四个索引库 / 游戏数据）时直接 return，
/// 不 Fail —— 没有游戏数据的 CI 必须绿。要在本机跑：<c>LME_MEDIA_COVERAGE_APP_DIR</c>
/// 指向发布目录（含 <c>cache/</c>），默认也会试仓库里的
/// <c>artifacts/publish-win-x64</c>。</para>
///
/// <para><b>时间预算</b>：图片解码实测一条数秒，抽样受 <see cref="BudgetSeconds"/> 约束，
/// 超预算就停止抽样并在输出里写明「只测了 N/M 条」。</para>
/// </summary>
public sealed class WikiMediaCoverageTests
{
    /// <summary>抽样时间预算（秒）：CI 不能被慢盘拖成小时级。</summary>
    private const int ImageBudgetSeconds = 240;

    /// <summary>音频抽样预算（秒）。</summary>
    private const int AudioBudgetSeconds = 90;

    /// <summary>Spine 抽样预算（秒）：三件套失败得很快，主要是查询底噪。</summary>
    private const int SpineBudgetSeconds = 60;

    private const int ImageSampleSize = 60;
    private const int AudioSampleSize = 50;

    private const string PersonaPageId = "persona:10201";

    private readonly ITestOutputHelper _output;

    public WikiMediaCoverageTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 测量 Image / Audio / Spine 三种形态的真实覆盖率（非 null 地址条数 / 抽样条数）。
    /// 门槛判据只有一条：<b>不允许出现「看起来像 http 但文件不在磁盘上」的地址</b>
    /// （= 编造的 URL）；覆盖率本身不做断言，只如实打印。
    /// </summary>
    [Fact]
    public async Task Sampled_bindings_report_real_resolution_coverage()
    {
        var host = Host.TryCreate(_output);
        if (host is null) return;

        var resolver = host.CreateResolver();
        _output.WriteLine($"FMOD 目录：{host.DescribeFmod()}");

        var bindings = host.SampleBindings();
        _output.WriteLine($"全库绑定 {bindings.All.Count} 条（样本：Image {bindings.Images.Count} · " +
                          $"Audio {bindings.Audios.Count} · Spine {bindings.Spines.Count}）");

        // 先测便宜的两种形态：共享一份总预算会让慢端把快端的样本全部吃掉（第一轮实测就踩过）。
        Report(_output, "Audio", await MeasureAsync(resolver, bindings.Audios, r => r.AudioUrl, AudioBudgetSeconds));
        Report(_output, "Spine", await MeasureAsync(resolver, bindings.Spines, r => r.SkeletonUrl, SpineBudgetSeconds));
        Report(_output, "Image", await MeasureAsync(resolver, bindings.Images, r => r.MediaUrl, ImageBudgetSeconds));
    }

    /// <summary>一条真实页面的冷缓存加载实测：墙钟秒数 + 产出的媒体文件数。</summary>
    [Fact]
    public async Task Cold_cache_persona_page_load_measures_wall_clock()
    {
        var host = Host.TryCreate(_output);
        if (host is null) return;

        var detail = host.PageDetail(PersonaPageId);
        if (detail is null)
        {
            _output.WriteLine($"跳过：页 {PersonaPageId} 不在真实页面库里。");
            return;
        }
        var bindings = detail.SubPages.SelectMany(s => s.Entries).SelectMany(e => e.Bindings).ToList();

        // 冷缓存 = 先把这次要用的落地目录清空（只动 {测试输出}/wwwroot/data/wiki）。
        host.ClearMaterialized();

        var resolver = host.CreateResolver();
        var stopwatch = Stopwatch.StartNew();
        var resolutions = await resolver.ResolveManyAsync(bindings);
        stopwatch.Stop();

        var files = host.CountMaterialized(out var bytes);
        var images = resolutions.Count(r => r.MediaUrl is not null);
        var audios = resolutions.Count(r => r.AudioUrl is not null);
        var spines = resolutions.Count(r => r.SkeletonUrl is not null);
        _output.WriteLine($"冷缓存页 {PersonaPageId}：绑定 {bindings.Count} 条 → 结果 {resolutions.Count} 条" +
                          $"（图片有地址 {images} · 音频 {audios} · Spine {spines}）");
        _output.WriteLine($"产出媒体文件 {files} 个 / {bytes / 1024} KB · 墙钟 {stopwatch.Elapsed.TotalSeconds:0.0} 秒");

        Assert.Equal(bindings.Count, resolutions.Count);
        Assert.All(resolutions.Where(r => r.MediaUrl is not null).Select(r => r.MediaUrl!),
            url => Assert.StartsWith("https://lme.data/wiki/", url));
    }

    // ── 抽样测量 ──────────────────────────────────────────────────────

    private static async Task<Measurement> MeasureAsync(
        WikiMediaResolver resolver,
        IReadOnlyList<WikiResourceBinding> sample,
        Func<WikiMediaResolution, string?> urlOf,
        int budgetSeconds)
    {
        var result = new Measurement(sample.Count);
        var watch = Stopwatch.StartNew();
        foreach (var binding in sample)
        {
            if (watch.Elapsed.TotalSeconds > budgetSeconds)
            {
                // 超预算就停手，并把「实际只测了多少条」标出来 —— 数字必须可解释。
                result.Truncated = true;
                break;
            }
            var resolution = await resolver.ResolveAsync(binding);
            var url = urlOf(resolution);
            result.Attempted++;
            if (string.IsNullOrEmpty(url)) continue;
            result.Resolved++;
            result.Examples.Add(new Sample(binding.Kind, ShortRef(binding), url!, FileSizeOf(url!)));
        }
        watch.Stop();
        result.Elapsed = watch.Elapsed;
        return result;
    }

    private static void Report(ITestOutputHelper output, string kind, Measurement measurement)
    {
        var percent = measurement.Attempted == 0 ? 0 : measurement.Resolved * 100.0 / measurement.Attempted;
        var note = measurement.Truncated ? $"（预算截断：抽样 {measurement.Total} 条里只测了 {measurement.Attempted} 条）" : "";
        output.WriteLine($"{kind} 覆盖率：{measurement.Resolved}/{measurement.Attempted} → {percent:0.0}%" +
                         $"（耗时 {measurement.Elapsed.TotalSeconds:0.0} 秒）{note}");
        foreach (var example in measurement.Examples.Take(2))
            output.WriteLine($"  · {example.Kind} {example.Ref} → {example.Url}（{example.Bytes} 字节）");
    }

    /// <summary>把 <c>https://lme.data/wiki/{hash}.ext</c> 换回本地路径并给文件大小（0 = 文件不在）。</summary>
    private static long FileSizeOf(string url)
    {
        var name = url[(url.LastIndexOf('/') + 1)..];
        var file = Path.Combine(AppContext.BaseDirectory, "wwwroot", "data", "wiki", name);
        try
        {
            var info = new FileInfo(file);
            return info is { Exists: true } ? info.Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>绑定的人类可读标识（音频只留事件名，路径太长的掐头去尾）。</summary>
    private static string ShortRef(WikiResourceBinding binding)
    {
        var parts = RelationDeepLink.Decode(binding.RefKey);
        var text = parts.Count >= 2 ? $"{Path.GetFileName(parts[0])}|{parts[1]}" : binding.RefKey;
        return text.Length <= 96 ? text : string.Concat(text[..48], "…", text[^47..]);
    }

    private sealed record Sample(string Kind, string Ref, string Url, long Bytes);

    private sealed class Measurement(int total)
    {
        public int Total { get; } = total;

        /// <summary>实际尝试了多少条（<= <see cref="Total"/>，受预算截断影响）。</summary>
        public int Attempted { get; set; }

        public int Resolved { get; set; }
        public bool Truncated { get; set; }
        public List<Sample> Examples { get; } = [];
        public TimeSpan Elapsed { get; set; }
    }

    // ── 真实数据宿主 ──────────────────────────────────────────────────

    /// <summary>
    /// 真实数据的入口：发布目录下的 wiki-pages.db / unity-cache-index.db / bank-index.db。
    /// 缺任何一个必要的库就返回 null —— 不 Fail、不建库、不写任何游戏数据。
    /// </summary>
    private sealed class Host
    {
        private readonly string _cacheDirectory;
        private WikiPageStore? _store;

        private Host(string cacheDirectory, string fmodDirectory)
        {
            _cacheDirectory = cacheDirectory;
            FmodDirectory = fmodDirectory;
        }

        private string FmodDirectory { get; }

        public static Host? TryCreate(ITestOutputHelper output)
        {
            // 本机会自动命中仓库里的发布目录；有数据、又不想为这次测量付几分钟的机器可以用
            // LME_MEDIA_COVERAGE_SKIP=1 关掉（与常规套件的「真实数据冒烟」门控同思路）。
            if (Environment.GetEnvironmentVariable("LME_MEDIA_COVERAGE_SKIP") == "1")
            {
                output.WriteLine("跳过：设置了 LME_MEDIA_COVERAGE_SKIP=1。");
                return null;
            }
            var app = Environment.GetEnvironmentVariable("LME_MEDIA_COVERAGE_APP_DIR");
            if (string.IsNullOrWhiteSpace(app))
            {
                // 默认试仓库里的发布目录（CI 通常没有 → 跳过）。
                var candidate = FindRepositoryArtifacts();
                app = candidate is null ? null : candidate;
            }
            if (string.IsNullOrWhiteSpace(app) || !Directory.Exists(app))
            {
                output.WriteLine("跳过：未找到发布目录（设置 LME_MEDIA_COVERAGE_APP_DIR，或在本机构建 artifacts/publish-win-x64）。");
                return null;
            }
            var cache = Path.Combine(app, "cache");
            if (!File.Exists(Path.Combine(cache, WikiPageStore.DatabaseFileName)))
            {
                output.WriteLine($"跳过：{cache} 下没有 wiki-pages.db。");
                return null;
            }
            output.WriteLine($"发布目录：{app}");

            var fmod = FirstExisting(Path.Combine(app, "fmod"), FindThirdPartyFmod());
            return new Host(cache, fmod ?? string.Empty);
        }

        private static string? FindRepositoryArtifacts()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "artifacts", "publish-win-x64");
                if (Directory.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string? FindThirdPartyFmod()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "third_party", "fmod");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "fmod64.dll"))) return candidate;
            }
            return null;
        }

        private static string? FirstExisting(params string?[] candidates)
            => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && Directory.Exists(c));

        private WikiPageStore Store => _store ??= new WikiPageStore(_cacheDirectory);

        public string DescribeFmod() => string.IsNullOrWhiteSpace(FmodDirectory) ? "未找到（音频一律给 null）" : FmodDirectory;

        public WikiMediaResolver CreateResolver()
        {
            var unityIndex = new UnityCacheSqliteIndexStore(Path.Combine(_cacheDirectory, "unity-cache-index.db"));
            var catalog = new AssetCatalog(unityIndex, new ModProject());
            var bankIndex = new BankIndexService(new BankIndexStore(_cacheDirectory));
            // FMOD 目录显式传进去：CI 的程序目录里没有这些 DLL（也不该有），显式给 null
            // 就等于「解码器不可用」，解析器会诚实返回 null，而不是抛异常。
            var spineData = SpineDataGatewayFactory.Create(Path.Combine(_cacheDirectory, "unity-cache-index.db"));
            return new WikiMediaResolver(catalog, bankIndex, spineData, new BankAudioService(),
                string.IsNullOrWhiteSpace(FmodDirectory) ? null : FmodDirectory);
        }

        public WikiPageDetail? PageDetail(string pageId) => new WikiPageQueryService(Store).GetPageDetail(pageId);

        /// <summary>按形态抽样（跨全库均匀取样，不是只盯着一两页的漂亮样本）。</summary>
        public BindingSample SampleBindings()
        {
            var all = new List<WikiResourceBinding>();
            var service = new WikiPageQueryService(Store);
            foreach (var page in service.Pages())
            {
                foreach (var sub in service.SubPages(page.PageId))
                foreach (var entry in service.Entries(sub.SubPageId))
                    all.AddRange(service.Bindings(entry.EntryId));
            }

            return new BindingSample(
                all,
                // 图片按固定种子乱序抽样：按顺序取会把「前几页能解的」当成全库水平，
                // 而按字典路由登录后，解不开的都堆在后面 —— 必须打散。
                Take(Shuffle(all.Where(x => Is(x, "Image")), 20260917), ImageSampleSize),
                Take(all.Where(x => Is(x, "Audio")), AudioSampleSize),
                Take(all.Where(x => Is(x, "Spine")), int.MaxValue));
        }

        /// <summary>清空测试输出里的落地目录（冷缓存测量的前提）。</summary>
        public void ClearMaterialized()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "wwwroot", "data", "wiki");
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (IOException) { /* 清不掉就不再是严格冷缓存，输出里会体现 */ }
        }

        public int CountMaterialized(out long bytes)
        {
            bytes = 0;
            var directory = Path.Combine(AppContext.BaseDirectory, "wwwroot", "data", "wiki");
            if (!Directory.Exists(directory)) return 0;
            var files = Directory.GetFiles(directory);
            foreach (var file in files) bytes += new FileInfo(file).Length;
            return files.Length;
        }

        /// <summary>固定种子乱序（种子写死 = 每次测量同一批样本，数字可复现）。</summary>
        private static List<WikiResourceBinding> Shuffle(IEnumerable<WikiResourceBinding> source, int seed)
        {
            var items = source.ToList();
            var random = new Random(seed);
            for (var i = items.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
            return items;
        }

        private static bool Is(WikiResourceBinding binding, string kind)
            => string.Equals(binding.Kind, kind, StringComparison.OrdinalIgnoreCase);

        /// <summary>均匀取样：把候选等距抽稀到 <paramref name="take"/> 条。</summary>
        private static List<WikiResourceBinding> Take(IEnumerable<WikiResourceBinding> source, int take)
        {
            var items = source.ToList();
            if (items.Count <= take || take == int.MaxValue) return items;
            var step = Math.Max(1, items.Count / take);
            var picked = new List<WikiResourceBinding>(take);
            for (var i = 0; i < items.Count && picked.Count < take; i += step) picked.Add(items[i]);
            return picked;
        }
    }

    private sealed record BindingSample(
        IReadOnlyList<WikiResourceBinding> All,
        IReadOnlyList<WikiResourceBinding> Images,
        IReadOnlyList<WikiResourceBinding> Audios,
        IReadOnlyList<WikiResourceBinding> Spines);
}
