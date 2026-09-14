using System.Diagnostics;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Caching;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Projects;
using Microsoft.Data.Sqlite;
using NLog;

namespace LimbusModEditor.Application.Relations;

/// <summary>一次关联分析的结果。</summary>
/// <param name="Rebuilt">是否真的重跑了分析（false = 源未变，直接复用缓存）。</param>
/// <param name="SubjectCount">对象数（人格 / 敌人 / 异想体 / 播报员 / E.G.O 装备 / E.G.O 饰品 合计）。</param>
/// <param name="LinkCount">关联边数。</param>
/// <param name="Elapsed">本次耗时（复用缓存时是读计数的耗时）。</param>
/// <param name="Detail">中文细节（状态栏/日志用）。</param>
public sealed record RelationIndexResult(
    bool Rebuilt, int SubjectCount, int LinkCount, TimeSpan Elapsed, string Detail)
{
    /// <summary>
    /// 中文摘要。**直接用 <see cref="Detail"/>**：那里已经带了类别分解与跨资源边数
    /// （由 <see cref="PersonaRelationIndexService"/> 组装）。在这里另拼一份就会与真实
    /// 计数口径分叉——曾经就这样把「1312 个对象」显示成「1312 个人格」。
    /// </summary>
    public string Describe() => $"{Detail} · 用时 {Elapsed.TotalSeconds:0.0} 秒";
}

/// <summary>
/// 关联分析的编排：<b>在四个索引库都就绪之后</b>，把它们的原版事实读出来交给
/// <see cref="SubjectRelationAnalyzer"/>，产出「对象 ⇄ 资源」关联图并落
/// <c>cache/relation-index.db</c>。
///
/// <para><b>输入来自四个库</b>（都不新增解析逻辑，只读既有缓存）：</para>
/// <list type="number">
/// <item><b>资源索引</b>（<c>unity-cache-index.db</c>）：<b>只读有容器路径的资产行</b>
/// （真实规模 127 万行里约 5.1 万行，实测；整表读出会白白实例化百万级无路径行，
/// 见 <see cref="UnityCacheSqliteIndexStore.ReadContainerRows"/>）。</item>
/// <item><b>音频索引</b>（<c>bank-index.db</c>）：全部样本行（5.3 万行），样本名里含人格 id。</item>
/// <item><b>静态表索引</b>（<c>static-tables.db</c>）：按内层内容哈希在缓存根下探到 bundle 的
/// <c>__data</c>，临时读出全部表正文（真实规模约 46 MB），扫出引用人格 id 的表。
/// <b>不解析 catalog</b>（那要 ~17 秒，而这里只需要「bundle 在哪」）。</item>
/// <item><b>文本索引</b>（<c>text-index.db</c>）：lang 文件清单（相对活动语言目录）。</item>
/// </list>
///
/// <para><b>缓存语义</b>：源签名 = 四个上游源的签名拼接（<see cref="RelationIndexSource"/>），
/// 任一上游换源即整库重建；一致则直接复用（热启动是读两个计数的毫秒级操作）。
/// 与其它工作台缓存一样，<b>只影响速度、不影响正确性</b>：删库即重算。</para>
///
/// <para><b>为什么静态表正文要读出全部而不是只读名字像的</b>：人格 id 会出现在
/// <c>battle-effect-personality-*</c>、<c>rental-personality-list-*</c>、
/// <c>storytheater-personality-*</c> 等各式表里，靠表名挑会漏；正文合计只 46 MB，
/// 一次性读完并把「哪张表引用了哪个人格」固化进缓存，比每次现场扫更划算。</para>
/// </summary>
public sealed class PersonaRelationIndexService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly AppEnvironment _env;

    /// <param name="env">程序环境（决定缓存目录、游戏目录、Unity 缓存目录的生效值）。</param>
    public PersonaRelationIndexService(AppEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        _env = env;
    }

    /// <summary>关联索引库（诊断 / 查询入口；资源预览的「关联资源」也用它）。</summary>
    public RelationStore CreateStore() => new(_env.CacheDirectory);

    /// <summary>在后台线程跑一次「读四个库 → 分析 → 落库」。</summary>
    public Task<RelationIndexResult> RefreshAsync(
        ModProject? project, CancellationToken cancellationToken = default)
        => Task.Run(() => Refresh(project, cancellationToken), cancellationToken);

    /// <summary>
    /// 跑一次关联分析（同步）。<b>不抛「前提缺失」类异常</b>：没有游戏目录 / 没有 lang 目录时
    /// 只是输入变少，分析照跑（可能产出空图），因为关联图是<b>派生</b>旁路。
    /// </summary>
    public RelationIndexResult Refresh(ModProject? project, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var cacheDirectory = _env.CacheDirectory;
        var gameDirectory = _env.EffectiveGameDirectory(project);
        var unityCacheDirectory = _env.EffectiveUnityCacheDirectory(project);

        // ── ① 四个上游源的签名（只做「变没变」的判定，不重算各自的解析）──
        var unityStore = new UnityCacheSqliteIndexStore(
            Path.Combine(cacheDirectory, WorkbenchCachePaths.UnityCacheIndexFileName));
        var unitySignature = DescribeUnityIndex(unityStore);

        var bankDirectory = new BankDirectoryService().ResolveBankDirectory(gameDirectory);
        var bankSignature = string.IsNullOrWhiteSpace(bankDirectory)
            ? "none"
            : BankIndexSource.Describe(bankDirectory).DirectorySignature;

        var staticStore = new StaticTableIndexStore(cacheDirectory);
        var cacheRoots = StaticIndexService.CacheRoots(unityCacheDirectory);
        var staticLocation = ResolveStaticLocation(staticStore, cacheRoots);
        var staticSignature = staticLocation is null
            ? "none"
            : StaticIndexSource.From(staticLocation).ContentSignature;

        var textService = new LangTextWorkbenchService();
        var langRoot = textService.ResolveLangRoot(gameDirectory);
        var languageDirectory = langRoot is null ? null : textService.ResolveLanguageDirectory(langRoot);
        var languageName = languageDirectory is null
            ? null
            : Path.GetFileName(languageDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var textSignature = langRoot is null ? "none" : TextIndexStore.DescribeSource(langRoot, languageName).Signature;

        var source = RelationIndexSource.From(unitySignature, bankSignature, staticSignature, textSignature);
        var store = new RelationStore(cacheDirectory);

        // ── ② 源一致即复用 ──
        var rebuilt = store.EnsureSource(source);
        if (!rebuilt)
        {
            var subjects = TryReadCount(store.ReadSubjectCount);
            var links = TryReadCount(store.ReadLinkCount);
            watch.Stop();
            Log.Debug("关联图已是最新（四个上游源未变）：{0} 个对象 · {1} 条关联 · 用时 {2:0.0} 秒",
                subjects, links, watch.Elapsed.TotalSeconds);
            return new RelationIndexResult(false, subjects, links, watch.Elapsed,
                $"{subjects} 个对象 · {links:N0} 条关联（源未变，直接复用）");
        }

        // ── ③ 读四个库的事实 → 分析 → 落库 ──
        Log.Info("关联分析开始：重建关联图（四个上游源有变化）");
        var inputs = BuildInputs(cacheDirectory, staticLocation, languageDirectory, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var graph = SubjectRelationAnalyzer.Analyze(inputs);
        cancellationToken.ThrowIfCancellationRequested();
        store.PersistGraph(source, graph);
        watch.Stop();
        Log.Info("关联分析结束：{0} 个对象（{1}） · {2:N0} 条关联 · {3:N0} 条跨资源边 · " +
                 "资源行 {4:N0} · 音频样本 {5:N0} · 静态表 {6:N0} · lang 文件 {7:N0} · 锚点 {8:N0} · 用时 {9:0.0} 秒",
            graph.Subjects.Count, DescribeCategories(graph.Subjects), graph.Links.Count, graph.Xrefs.Count,
            inputs.Assets.Count, inputs.Audio.Count, inputs.StaticTables.Count, inputs.LangFiles.Count,
            inputs.TextAnchors.Count, watch.Elapsed.TotalSeconds);
        return new RelationIndexResult(true, graph.Subjects.Count, graph.Links.Count, watch.Elapsed,
            $"已重建关联图：{graph.Subjects.Count} 个对象（{DescribeCategories(graph.Subjects)}） · " +
            $"{graph.Links.Count:N0} 条关联 · {graph.Xrefs.Count:N0} 条跨资源边");
    }

    /// <summary>
    /// 把对象按类别汇总成「人格 185 · 敌人单位 239 · …」这样一段短文本。
    /// 类别是<b>动态</b>的（分析器产出什么就是什么），所以按 <see cref="RelationCategories.All"/>
    /// 的顺序排、未知类别排在最后，绝不因为「表里没有」就丢掉一个类别。
    /// </summary>
    private static string DescribeCategories(IReadOnlyList<RelationSubject> subjects)
    {
        if (subjects.Count == 0) return "空图";
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var subject in subjects)
            counts[subject.SubjectKind] = counts.TryGetValue(subject.SubjectKind, out var n) ? n + 1 : 1;

        var ordered = RelationCategories.All.Where(counts.ContainsKey)
            .Concat(counts.Keys.Where(x => !RelationCategories.IsKnown(x)).Order(StringComparer.Ordinal));
        return string.Join(" · ", ordered.Select(x => $"{RelationCategories.Label(x)} {counts[x]}"));
    }

    /// <summary>把四个库的原版事实读成一个分析输入包。</summary>
    private static RelationInputs BuildInputs(
        string cacheDirectory,
        StaticBundleLocation? staticLocation,
        string? languageDirectory,
        CancellationToken cancellationToken)
    {
        var assets = new List<RelationAssetFact>();
        foreach (var row in new UnityCacheSqliteIndexStore(
                     Path.Combine(cacheDirectory, WorkbenchCachePaths.UnityCacheIndexFileName)).ReadContainerRows())
            assets.Add(new RelationAssetFact(row.ContainerEntry, row.Type, row.Size));

        var audio = new List<RelationAudioFact>();
        foreach (var sample in new BankIndexStore(cacheDirectory).ReadAllSamples())
            audio.Add(new RelationAudioFact(sample.BankPath, sample.Name, sample.CodecName, sample.DataSize)
            {
                SampleRate = sample.SampleRate,
                SampleCount = (int)Math.Min(sample.SampleCount, int.MaxValue),
            });

        var statics = ReadStaticFacts(staticLocation, cancellationToken);

        var langFiles = new List<RelationLangFact>();
        foreach (var file in new TextIndexStore(cacheDirectory).ReadFiles(languageDirectory))
            langFiles.Add(new RelationLangFact(file.RelativePath, file.KeyCount));

        // lang 锚点：语音台词（id → dlg）与被动/EGO/技能的数值 id（→ 中文 name/desc）。
        // 这是「交叉关联」的关键输入——没有它，音频与静态数据就只能显示文件名。
        var anchors = LangAnchorReader.Read(languageDirectory, cancellationToken);

        return new RelationInputs(assets, audio, statics, langFiles) { TextAnchors = anchors };
    }

    /// <summary>
    /// 读出静态表的正文事实（只在本轮真的要重建关联图时调用）。
    /// <paramref name="location"/> 为 null / 缓存未命中时返回空列表（消息在
    /// <see cref="ResolveStaticLocation"/> 里已留痕，不重复报错）。
    /// </summary>
    private static IReadOnlyList<RelationStaticFact> ReadStaticFacts(
        StaticBundleLocation? location, CancellationToken cancellationToken)
    {
        if (location is null || !location.IsCached) return [];
        var facts = new List<RelationStaticFact>();
        // 一次性读出全部 TextAsset（内部只 ScanBundle 一次）；46 MB 级，峰值可控。
        foreach (var asset in StaticBundleLocator.ReadTextAssetEntries(location, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            facts.Add(new RelationStaticFact(
                asset.ContainerEntry, asset.FileName, asset.DataClass, asset.TryDecodeUtf8(), asset.Data.LongLength));
        }
        return facts;
    }

    /// <summary>
    /// 资源索引的签名：<c>bundles</c> 表的「条目数 : 最新 mtime : 字节合计」。
    ///
    /// <para>为什么不用库文件 mtime：SQLite 在 WAL 下写事务未必改主库 mtime（只写 <c>-wal</c>），
    /// 会漏判「索引被重扫过」。bundles 表里每一行都带 <c>mtime_ticks</c>，取最大值 + 行数 +
    /// 字节合计即可稳定反映「bundle 集合或其内容变过」。</para>
    /// </summary>
    private static string DescribeUnityIndex(UnityCacheSqliteIndexStore store)
    {
        try
        {
            if (!store.Exists) return "none";
            var bundles = store.ReadBundleIndex(StringComparer.OrdinalIgnoreCase);
            long latest = 0, bytes = 0;
            foreach (var bundle in bundles.Values)
            {
                if (bundle.MTimeUtcTicks > latest) latest = bundle.MTimeUtcTicks;
                bytes += bundle.Size;
            }
            return $"{bundles.Count}:{latest}:{bytes}";
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // 资源索引读不出来只是「少了一份输入」：给一个恒定的 none 让分析照跑。
            Log.Warn(ex, "读资源索引 bundles 表失败：关联分析本次按「无资源输入」处理");
            return "none";
        }
    }

    /// <summary>
    /// 用静态表索引里的内层内容哈希（<c>index_meta.source_key</c>）在缓存根下探到
    /// <c>&lt;外层键&gt;/&lt;内层哈希&gt;/__data</c>。**不解析 catalog**（那要 ~17 秒，
    /// 而这里只需要「bundle 文件在哪」）。探不到返回 null（静态表不参与本次关联）。
    /// </summary>
    private static StaticBundleLocation? ResolveStaticLocation(
        StaticTableIndexStore store, IReadOnlyList<string> cacheRoots)
    {
        string? sourceKey;
        try
        {
            sourceKey = store.ReadSourceKey();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn(ex, "读静态表索引的源键失败：本次关联分析不包含静态表");
            return null;
        }
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            Log.Debug("静态表索引里没有源键（还没建过索引）：本次关联分析不包含静态表");
            return null;
        }

        var probe = new StaticBundleLocation(
            $"{StaticBundleLocator.BundleNamePrefix}{sourceKey}.bundle", sourceKey, OuterKey: null, Record: null);
        var resolved = StaticBundleLocator.LocateInCache(probe, cacheRoots);
        if (!resolved.IsCached)
        {
            Log.Debug("静态数据 bundle 未在缓存根下命中（sourceKey={0}）：本次关联分析不包含静态表", sourceKey);
            return null;
        }
        return resolved;
    }

    /// <summary>读一个诊断用计数：读不到返回 0（只影响呈现，绝不影响分析结论）。</summary>
    private static int TryReadCount(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Debug(ex, "读关联索引计数失败（返回 0，仅影响呈现）：{0}", read.Method.Name);
            return 0;
        }
    }
}
