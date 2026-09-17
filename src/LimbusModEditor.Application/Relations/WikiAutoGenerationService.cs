using System.Diagnostics;
using System.Globalization;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Relations.Authority;
using LimbusModEditor.Application.Relations.WikiBaseData;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using Microsoft.Data.Sqlite;
using NLog;

namespace LimbusModEditor.Application.Relations;

/// <summary>一次生成进度的汇报（走 WEB-IPC-CONTRACT §4 的 <c>progress</c> 事件）。</summary>
/// <param name="Phase">阶段：<c>inputs</c> 读事实源 / <c>entities</c> 实体页 / <c>story</c> 剧情页。</param>
/// <param name="Current">已完成数。</param>
/// <param name="Total">总数。</param>
/// <param name="Message">中文说明（状态栏/进度条）。</param>
public sealed record WikiGenerationProgress(string Phase, int Current, int Total, string Message);

/// <summary>一个类别的页面数。</summary>
public sealed record WikiCategoryCount(string Category, string Label, int PageCount);

/// <summary>
/// 一次「维基页面生成」的结果。
/// </summary>
/// <param name="Ok">是否真的生成了（false = 前提缺失，<paramref name="Message"/> 说明缺什么）。</param>
/// <param name="Message">中文结论（直接给用户看）。</param>
/// <param name="PageCount">库里的主页面总数。</param>
/// <param name="SubPageCount">库里的分节总数。</param>
/// <param name="EntryCount">库里的条目总数。</param>
/// <param name="BindingCount">库里的资源绑定总数。</param>
/// <param name="WrittenEntries">本次写入的条目数。</param>
/// <param name="RevisedPreserved">因 <c>source = revised</c> 跳过未覆盖的条目数。</param>
/// <param name="UnknownSourceEntries">本次写入里「本地无权威来源」的条目数（只读、不宣称出处）。</param>
/// <param name="Elapsed">耗时。</param>
/// <param name="Categories">各类别页面数。</param>
public sealed record WikiGenerationResult(
    bool Ok,
    string Message,
    int PageCount,
    int SubPageCount,
    int EntryCount,
    int BindingCount,
    int WrittenEntries,
    int RevisedPreserved,
    int UnknownSourceEntries,
    TimeSpan Elapsed,
    IReadOnlyList<WikiCategoryCount> Categories)
{
    /// <summary>中文摘要（状态栏/日志用）。</summary>
    public string Describe()
        => $"{Message} · 页面 {PageCount} · 分节 {SubPageCount} · 条目 {EntryCount} · 绑定 {BindingCount} · 用时 {Elapsed.TotalSeconds:0.0} 秒";
}

/// <summary>
/// 维基页面的<b>生成服务</b>（G-01 缺失的那条生产链路）：
/// 「本地事实源 → 权威引擎 → 页面编排 → 落 <c>cache/wiki-pages.db</c>」。
///
/// <para><b>读哪些既有事实源（全部只读，不新造解析）</b>：</para>
/// <list type="number">
/// <item><c>relation-index.db</c>：<see cref="RelationQueryService"/> 出的对象与关联边
/// （<see cref="WikiPageArranger"/> 用它搭页面树）。</item>
/// <item><c>unity-cache-index.db</c>：带容器路径的资源行；按
/// <c>bundles.static_bundle</c> + <c>assets.static_kind</c> 排除静态数据表（那份走第 4 路）。</item>
/// <item><c>bank-index.db</c>：音频样本（样本名 ↔ 台词 id 精确匹配）。</item>
/// <item><c>static-tables.db</c> + 静态 bundle：静态表正文（外键 / 容器路径）。</item>
/// <item><c>text-index.db</c> + lang 目录：lang 文件清单；剧情正文另由
/// <see cref="StoryDataReader"/> 读 <c>StoryData/*.json</c>。</item>
/// </list>
///
/// <para><b>口径（逐条对应交付要求）</b>：</para>
/// <list type="bullet">
/// <item><b>只写本地能推出来的</b>：条目全部来自上面的事实源；拿不到正文就留空 body；
/// 生成不出内容的分节<b>不建</b>（不占位、不写「暂无数据」）。</item>
/// <item><b>禁止 id 数字窗口猜测</b>：归属一律由 <see cref="WikiPageAuthorityEngine"/>
/// 用权威来源判定（容器路径前缀 / lang 权威清单 / 静态表外键 / 样本名精确匹配），
/// 本服务只做「查表 + 标注」，不自己匹配 id。</item>
/// <item><b>幂等</b>：条目/绑定 id 由 <see cref="WikiStableIds"/> 按内容键算出，
/// 同一内容两次生成落同一行（UPSERT），不产生重复。</item>
/// <item><b>绝不覆盖用户修订</b>：<c>source = revised</c> 的行整条跳过（见
/// <see cref="WikiPageStore.SaveGeneratedPage"/>）。</item>
/// <item><b>可编辑性</b>：只有 <see cref="WritableSourceKind.Path"/> 的条目
/// <see cref="WikiEntry.Editable"/> 为真，其余只读。</item>
/// </list>
/// </summary>
public sealed class WikiAutoGenerationService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>剧情页的类别（<see cref="RelationCategories"/> 里没有它：剧情不是「预设对象」，
    /// 而是由 lang 的 <c>StoryData/</c> 目录推出的页面族）。</summary>
    public const string StoryCategory = "story";

    private const string StoryIndexPageId = "story:index";

    private readonly AppEnvironment _env;

    /// <param name="env">程序环境（决定缓存目录、游戏目录、Unity 缓存目录的生效值）。</param>
    public WikiAutoGenerationService(AppEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        _env = env;
    }

    /// <summary>维基页库（<c>cache/wiki-pages.db</c>）。</summary>
    public WikiPageStore CreateStore() => new(_env.CacheDirectory);

    /// <summary>在后台线程跑一次生成。</summary>
    public Task<WikiGenerationResult> GenerateAsync(
        ModProject? project,
        IProgress<WikiGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Generate(project, progress, cancellationToken), cancellationToken);

    /// <summary>
    /// 跑一次生成（同步）。<b>不抛「前提缺失」类异常</b>：关联索引没建好时给出中文原因，
    /// 因为维基页是派生内容，缺了只是「没有页面」。
    /// </summary>
    public WikiGenerationResult Generate(
        ModProject? project,
        IProgress<WikiGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var store = CreateStore();
        var cacheDirectory = _env.CacheDirectory;
        var gameDirectory = _env.EffectiveGameDirectory(project);
        var unityCacheDirectory = _env.EffectiveUnityCacheDirectory(project);

        var relations = new RelationQueryService(new RelationStore(cacheDirectory));
        if (!relations.IsReady)
        {
            watch.Stop();
            const string why = "关联索引（cache/relation-index.db）还没建好：请先启动一次让它完成扫描后重试。";
            Log.Warn("维基页面生成跳过：{0}", why);
            progress?.Report(new WikiGenerationProgress("inputs", 0, 0, why));
            return new WikiGenerationResult(false, why, 0, 0, 0, 0, 0, 0, 0, watch.Elapsed, []);
        }

        var subjects = relations.Subjects();
        var languageDirectory = ResolveLanguageDirectory(gameDirectory);

        // ── ① 四源事实（只在真的要生成时读一次）──
        progress?.Report(new WikiGenerationProgress("inputs", 0, subjects.Count, "正在读取本地事实源…"));
        var facts = BuildContext(
            relations, subjects, cacheDirectory, gameDirectory, unityCacheDirectory, languageDirectory, cancellationToken);
        var context = facts.Context;
        cancellationToken.ThrowIfCancellationRequested();
        Log.Info("维基生成开始：对象 {0} · 资源行 {1:N0}（命中关联键 {2:N0}）· 音频样本 {3:N0} · 静态表 {4:N0} · lang 文件 {5:N0} · 锚点 {6:N0}",
            subjects.Count, facts.ScannedRows, context.Assets.Count, context.Audio.Count,
            context.StaticTables.Count, context.LangFiles.Count, context.TextAnchors.Count);

        var writeStats = new WriteStats();

        // ── 基础数据三节（数据/技能/被动）的取数目录：静态表行级 + Lang 实体 ──
        // 前提缺失（索引没建/游戏目录没有）→ null → 人格页退回原编排，不阻断。
        using var staticRows = StaticRowCatalog.TryCreate(cacheDirectory, unityCacheDirectory, gameDirectory);
        var langCatalog = WikiLangCatalog.TryCreate(languageDirectory);

        // ── ② 逐对象生成实体页 ──
        var arranger = new WikiPageArranger(relations);
        var engine = new WikiPageAuthorityEngine();
        var entityPages = 0;

        for (var i = 0; i < subjects.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subject = subjects[i];
            var arranged = arranger.Arrange(subject.SubjectId, context.Assets, facts.AssetIndex);
            if (arranged is null) continue;

            var byRef = new Dictionary<string, AuthorityFact>(StringComparer.Ordinal);
            foreach (var fact in engine.Extract(subject.SubjectId, context).Facts)
                byRef.TryAdd(fact.RefKey, fact);

            var detail = Materialize(subject, arranged, byRef, writeStats);
            // 人格页：按静态表行级数据补「数据/技能/被动」三节（缺数据就维持原样）。
            detail = MergePersonaBaseData(subject.SubjectId, detail, staticRows, langCatalog);
            if (detail.SubPages.Count == 0) continue;

            var save = store.SaveGeneratedPage(detail);
            writeStats.Entries += save.Entries;
            writeStats.Revised += save.RevisedPreserved;
            entityPages++;

            if ((i + 1) % 25 == 0 || i + 1 == subjects.Count)
                progress?.Report(new WikiGenerationProgress("entities", i + 1, subjects.Count,
                    $"正在生成实体页：{i + 1}/{subjects.Count}（{subject.DisplayName}）"));
        }

        // ── ③ 剧情页 ──
        var storyPages = GenerateStoryPages(store, context, languageDirectory, engine, writeStats, progress, cancellationToken);

        watch.Stop();
        var categories = new List<WikiCategoryCount>();
        foreach (var category in RelationCategories.All)
        {
            var count = store.CountByCategory(category);
            if (count > 0) categories.Add(new WikiCategoryCount(category, RelationCategories.Label(category), count));
        }
        var storyCount = store.CountByCategory(StoryCategory);
        if (storyCount > 0) categories.Add(new WikiCategoryCount(StoryCategory, "剧情", storyCount));

        var message = $"已生成 {entityPages + storyPages} 个页面（写入条目 {writeStats.Entries}，" +
                      $"保留修订 {writeStats.Revised}，无权威来源 {writeStats.Unknown}）";
        Log.Info("维基生成结束：{0} · 分节 {1} · 条目 {2} · 绑定 {3} · 用时 {4:0.0} 秒",
            message, store.ReadSubPageCount(), store.ReadEntryCount(), store.ReadBindingCount(),
            watch.Elapsed.TotalSeconds);
        progress?.Report(new WikiGenerationProgress("done", subjects.Count, subjects.Count, message));

        return new WikiGenerationResult(
            true, message, store.ReadPageCount(), store.ReadSubPageCount(), store.ReadEntryCount(),
            store.ReadBindingCount(), writeStats.Entries, writeStats.Revised, writeStats.Unknown,
            watch.Elapsed, categories);
    }

    /// <summary>写入计数（小类：避免把 ref 参数传进 lambda）。</summary>
    private sealed class WriteStats
    {
        public int Entries;
        public int Revised;
        public int Unknown;
    }

    // ── 事实源装载 ──────────────────────────────────────────────────

    private static string? ResolveLanguageDirectory(string? gameDirectory)
    {
        var service = new LangTextWorkbenchService();
        var root = service.ResolveLangRoot(gameDirectory);
        return root is null ? null : service.ResolveLanguageDirectory(root);
    }

    /// <summary>四源事实 + 编排器要用的资源索引（一次建好，全程复用）。</summary>
    /// <param name="Context">权威引擎的输入。</param>
    /// <param name="AssetIndex">「容器路径 → 资源」索引。</param>
    /// <param name="ScannedRows">实际扫过的资源行数（日志用，用来说明筛掉了多少）。</param>
    private sealed record LoadedFacts(
        AuthorityExtractionContext Context,
        IReadOnlyDictionary<string, AssetRecord> AssetIndex,
        long ScannedRows);

    /// <summary>
    /// 把五个索引库的事实读成 <see cref="AuthorityExtractionContext"/>。
    /// 与 <see cref="PersonaRelationIndexService"/> 的 <c>BuildInputs</c> 同一口径，
    /// 差别只有两处：
    /// <list type="number">
    /// <item>资源行排除静态数据表（那份走静态表这一路），且<b>只保留关联图里真正引用到的</b>——
    /// 真实规模下资源行有 127 万条，全量物化成 <see cref="AssetRecord"/> 要 1.2 GiB 托管堆
    /// （见 <c>UnityCacheSqliteIndexStore</c> 的派生层注释）；而页面内容只可能来自关联边，
    /// 所以按关联键预筛既不丢内容、又把内存压到「命中数」量级（真实数据约 1 万条）。</item>
    /// <item>资源用 <see cref="AssetRecord"/>（编排器要挑封面、要类型）。</item>
    /// </list>
    /// </summary>
    private static LoadedFacts BuildContext(
        RelationQueryService relations,
        IReadOnlyList<RelationSubject> subjects,
        string cacheDirectory,
        string? gameDirectory,
        string? unityCacheDirectory,
        string? languageDirectory,
        CancellationToken cancellationToken)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subject in subjects)
            foreach (var link in relations.Links(subject.SubjectId))
                if (!string.IsNullOrEmpty(link.RefKey)) wanted.Add(link.RefKey);

        var assets = new List<AssetRecord>();
        long scanned = 0;
        foreach (var row in UnityCacheSqliteIndexStore.ForCacheDirectory(cacheDirectory)
                     .ReadNonStaticContainerRows())
        {
            scanned++;
            if (!wanted.Contains(row.ContainerEntry)) continue;
            assets.Add(new AssetRecord
            {
                LogicalPath = row.ContainerEntry,
                ContainerPath = row.ContainerEntry,
                Type = row.Type,
                Size = row.Size,
            });
        }
        cancellationToken.ThrowIfCancellationRequested();

        var audio = new List<RelationAudioFact>();
        foreach (var sample in new BankIndexStore(cacheDirectory).ReadAllSamples())
        {
            audio.Add(new RelationAudioFact(sample.BankPath, sample.Name, sample.CodecName, sample.DataSize)
            {
                SampleRate = sample.SampleRate,
                SampleCount = (int)Math.Min(sample.SampleCount, int.MaxValue),
            });
        }
        cancellationToken.ThrowIfCancellationRequested();

        var statics = ReadStaticFacts(cacheDirectory, gameDirectory, unityCacheDirectory, cancellationToken);

        var langFiles = new List<RelationLangFact>();
        foreach (var file in new TextIndexStore(cacheDirectory).ReadFiles(languageDirectory))
            langFiles.Add(new RelationLangFact(file.RelativePath, file.KeyCount));

        var anchors = LangAnchorReader.Read(languageDirectory, cancellationToken);
        var context = new AuthorityExtractionContext(assets, audio, statics, langFiles, anchors);
        return new LoadedFacts(context, PresetWorkbenchService.IndexByContainerEntry(assets), scanned);
    }

    /// <summary>静态表正文（快路径探针不命中时回落 catalog 重定位；都不成立返回空：
    /// 静态表只是少一路输入，不阻断生成）。</summary>
    private static IReadOnlyList<RelationStaticFact> ReadStaticFacts(
        string cacheDirectory, string? gameDirectory, string? unityCacheDirectory, CancellationToken cancellationToken)
    {
        var resolved = StaticIndexService.LocateForReads(
            new StaticTableIndexStore(cacheDirectory), gameDirectory, StaticIndexService.CacheRoots(unityCacheDirectory));
        if (resolved is null || !resolved.IsCached)
        {
            Log.Debug("静态数据 bundle 未定位或缓存里没有该条目：本次生成不含静态表事实");
            return [];
        }

        var facts = new List<RelationStaticFact>();
        foreach (var asset in StaticBundleLocator.ReadTextAssetEntries(resolved, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            facts.Add(new RelationStaticFact(
                asset.ContainerEntry, asset.FileName, asset.DataClass, asset.TryDecodeUtf8(), asset.Data.LongLength));
        }
        return facts;
    }

    // ── 实体页：编排结果 → 稳定 id + 权威标注 ────────────────────────

    /// <summary>
    /// 把编排器产出的页面树物化成「可幂等落库」的形态：
    /// 换掉 GUID、按 <see cref="AuthorityFact"/> 标注来源/置信度/可写出处、补上概览摘要。
    /// </summary>
    private static WikiPageDetail Materialize(
        RelationSubject subject,
        WikiPageDetail arranged,
        IReadOnlyDictionary<string, AuthorityFact> factsByRef,
        WriteStats stats)
    {
        var pageId = arranged.Page.PageId;
        var subPages = new List<WikiSubPageDetail>(arranged.SubPages.Count);

        foreach (var sub in arranged.SubPages)
        {
            var subPageId = WikiStableIds.Of(pageId, sub.SubPage.Title,
                sub.SubPage.SortOrder.ToString(CultureInfo.InvariantCulture));
            var entries = new List<WikiEntryDetail>();
            var index = 0;

            foreach (var entryDetail in sub.Entries)
            {
                var binding = entryDetail.Bindings.Count > 0 ? entryDetail.Bindings[0] : null;
                var refKey = binding?.RefKey;
                if (string.IsNullOrEmpty(refKey)) continue;      // 无定位键 = 本地没这条事实 → 不写

                factsByRef.TryGetValue(refKey, out var fact);
                if (fact is null) stats.Unknown++;

                var entryId = WikiStableIds.Of(subPageId, refKey);
                var entry = new WikiEntry(
                    entryId,
                    subPageId,
                    fact?.Display ?? entryDetail.Entry.Title,
                    // 真正文优先：权威 provider 当次抽到的 Detail/PreviewText 排在前面，
                    // 关系索引里缓存的 preview_text 只作兜底（它可能是历史计数串）。
                    NonPlaceholder(FirstNonEmpty(fact?.Detail, fact?.PreviewText, binding?.PreviewText)) ?? string.Empty,
                    index)
                {
                    Source = WikiEntrySources.Auto,
                    Authority = fact?.Source.ToString() ?? nameof(AuthoritySource.Unknown),
                    Confidence = fact?.Confidence.ToString() ?? nameof(ConfidenceLevel.None),
                    WritableSource = fact?.WritableSource.ToString() ?? nameof(WritableSourceKind.Unknown),
                    WritableSourcePath = fact?.WritableSourcePath,
                    SourceDetail = fact?.SourceDetail,
                };

                var stableBinding = new WikiResourceBinding(
                    WikiStableIds.Of(entryId, refKey),
                    entryId,
                    binding!.RefKey,
                    binding.Kind,
                    fact?.Display ?? binding.Display,
                    0)
                {
                    DeepLink = binding.DeepLink ?? fact?.DeepLink,
                    PreviewText = binding.PreviewText ?? fact?.PreviewText,
                    MediaKind = binding.MediaKind,
                    DurationSec = binding.DurationSec ?? fact?.DurationSec,
                };

                // 推不出正文、又不是可直接展示的媒体（图/音/视频/Spine）→ 不落条目，
                // 页面上不留「有标题、无内容」的空壳。
                if (entry.Body.Length == 0 && !IsRenderableMedia(binding!.Kind)) continue;

                entries.Add(new WikiEntryDetail(entry, [stableBinding]));
                index++;
            }

            // 编排器给「概览」建的是空分节（它的 Kinds 是空集）：拿得到摘要才建，拿不到就不建。
            if (entries.Count == 0)
            {
                var overview = BuildOverviewEntry(subject, subPageId);
                if (overview is not null)
                {
                    entries.Add(overview);
                    stats.Unknown++;
                }
            }
            if (entries.Count == 0) continue;                    // 无内容 = 不占位

            subPages.Add(new WikiSubPageDetail(
                new WikiSubPage(subPageId, pageId, sub.SubPage.Title, sub.SubPage.SortOrder), entries));
        }

        return new WikiPageDetail(arranged.Page, subPages);
    }

    /// <summary>
    /// 人格页专属：按静态表行级数据构建「数据/技能/被动」三节并合并进页面树
    /// （见 <see cref="WikiBaseData.PersonaBaseDataSections"/>）。非人格/前提缺失/本地无该行 → 原样返回。
    /// </summary>
    private static WikiPageDetail MergePersonaBaseData(
        string subjectId, WikiPageDetail detail,
        StaticRowCatalog? statics, WikiLangCatalog? lang)
    {
        if (statics is null || lang is null) return detail;
        var separator = subjectId.IndexOf(':');
        if (separator <= 0) return detail;
        if (!string.Equals(subjectId[..separator], RelationCategories.Persona, StringComparison.Ordinal)) return detail;
        if (!long.TryParse(subjectId[(separator + 1)..], out var personaId)) return detail;

        var baseData = PersonaBaseDataSections.TryBuild(personaId, statics, lang);
        if (baseData is null) return detail;

        var merged = PersonaBaseDataMerge.MergeInto(detail, baseData);
        Log.Debug("人格 {0} 基础数据三节：数据 {1} / 技能 {2} / 被动 {3} 条",
            subjectId, baseData.BaseData.Count, baseData.Skills.Count, baseData.Passives.Count);
        return merged;
    }

    /// <summary>
    /// 概览里唯一有本地来源的一项：关联索引里的摘要文本。
    /// <b>没有可写出处</b>（分析器只存了文本、没记它出自哪个 lang 键），所以标
    /// <c>Unknown / None / Unknown</c> → 只读。
    /// </summary>
    private static WikiEntryDetail? BuildOverviewEntry(RelationSubject subject, string subPageId)
    {
        var summary = NonPlaceholder(subject.PreviewText);
        if (summary is null) return null;                        // 摘要是计数串/空 = 没有可展示正文 → 不占位
        var entry = new WikiEntry(WikiStableIds.Of(subPageId, "overview:summary"), subPageId,
            "摘要", summary, 0)
        {
            Source = WikiEntrySources.Auto,
            Authority = nameof(AuthoritySource.Unknown),
            Confidence = nameof(ConfidenceLevel.None),
            WritableSource = nameof(WritableSourceKind.Unknown),
            SourceDetail = "relation-index.db subjects.preview_text（关联分析产出的摘要，未记录可写出处）",
        };
        return new WikiEntryDetail(entry, []);
    }

    // ── 剧情页 ──────────────────────────────────────────────────────

    /// <summary>
    /// 生成剧情页：<b>一个总览页 + 每个章节一个页</b>。返回写入的页面数。
    ///
    /// <para>章节键由 <see cref="StoryDataReader.ChapterKeyOf"/> 按 lang 文件名约定推出
    /// （<c>S001A</c> → <c>S001</c>），不是猜 id；章节正文读 <c>StoryData/*.json</c> 的
    /// <c>dataList[].content</c>（真实台词）。</para>
    ///
    /// <para>分节按 <see cref="WikiSectionTables.Story"/>：
    /// 章节导航 / 章节正文 / 登场角色 / 事件与选择 / 过场。
    /// 某一节在本地没有来源时<b>不建</b>，不在页面上留空壳。</para>
    /// </summary>
    private static int GenerateStoryPages(
        WikiPageStore store,
        AuthorityExtractionContext context,
        string? languageDirectory,
        WikiPageAuthorityEngine engine,
        WriteStats stats,
        IProgress<WikiGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var facts = engine.Extract(StoryIndexPageId, context).Facts;
        var storyContent = facts.Where(f => f.ContentType == "story_content").ToList();
        if (storyContent.Count == 0) return 0;                   // 没有剧情文件 = 不建剧情页

        var chapters = storyContent
            .GroupBy(f => StoryDataReader.ChapterKeyOf(f.RefKey), StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(f => f.RefKey, StringComparer.Ordinal).ToList())
            .ToList();
        if (chapters.Count == 0) return 0;

        var keys = chapters.Select(c => StoryDataReader.ChapterKeyOf(c[0].RefKey)).ToList();
        var dialogs = new Dictionary<string, IReadOnlyList<StoryDialogLine>>(StringComparer.Ordinal);
        IReadOnlyList<StoryDialogLine> DialogOf(string path)
        {
            if (dialogs.TryGetValue(path, out var cached)) return cached;
            var lines = StoryDataReader.Read(languageDirectory, path);
            dialogs[path] = lines;
            return lines;
        }

        var pages = 0;

        // ① 总览页：章节导航（静态表章节定义 + 全部章节）+ 章节正文（每章一条，写真实对白条数）
        //    + 登场角色 + 事件与选择 + 过场
        var indexSubPages = new List<WikiSubPageDetail>();

        var chapterNav = new List<WikiEntryDetail>();
        foreach (var fact in facts.Where(f => f.ContentType == "story_chapter").OrderBy(f => f.RefKey, StringComparer.Ordinal))
            chapterNav.Add(FactEntry(StoryIndexPageId, WikiSectionTables.StoryChapters, fact, chapterNav.Count, stats));
        foreach (var chapter in chapters)
        {
            var key = StoryDataReader.ChapterKeyOf(chapter[0].RefKey);
            chapterNav.Add(new WikiEntryDetail(new WikiEntry(
                WikiStableIds.Of(StoryIndexPageId, WikiSectionTables.StoryChapters, "chapter:" + key),
                WikiStableIds.Of(StoryIndexPageId, WikiSectionTables.StoryChapters),
                key,
                ChapterFileList(chapter),
                chapterNav.Count)
            {
                Source = WikiEntrySources.Auto,
                Authority = nameof(AuthoritySource.LangFileName),
                Confidence = nameof(ConfidenceLevel.Authoritative),
                WritableSource = nameof(WritableSourceKind.Path),
                WritableSourcePath = chapter[0].RefKey,
                SourceDetail = $"Lang 文件名约定 StoryData/{key}*.json",
            }, []));
        }
        AddSubPage(indexSubPages, StoryIndexPageId, WikiSectionTables.StoryChapters, chapterNav);

        var contentEntries = new List<WikiEntryDetail>();
        foreach (var chapter in chapters)
        {
            var key = StoryDataReader.ChapterKeyOf(chapter[0].RefKey);
            contentEntries.Add(new WikiEntryDetail(new WikiEntry(
                WikiStableIds.Of(StoryIndexPageId, WikiSectionTables.StoryContent, "content:" + key),
                WikiStableIds.Of(StoryIndexPageId, WikiSectionTables.StoryContent),
                key,
                ChapterDialogExcerpt(chapter, DialogOf),
                contentEntries.Count)
            {
                Source = WikiEntrySources.Auto,
                Authority = nameof(AuthoritySource.LangFileName),
                Confidence = nameof(ConfidenceLevel.Authoritative),
                WritableSource = nameof(WritableSourceKind.Path),
                WritableSourcePath = chapter[0].RefKey,
                SourceDetail = $"Lang 文件正文 StoryData/{key}*.json 的 dataList[].content",
            }, []));
        }
        AddSubPage(indexSubPages, StoryIndexPageId, WikiSectionTables.StoryContent, contentEntries);
        AddFactSubPage(indexSubPages, StoryIndexPageId, WikiSectionTables.StoryCharacters, "story_character", facts, stats);
        AddFactSubPage(indexSubPages, StoryIndexPageId, WikiSectionTables.StoryEvents, "story_event", facts, stats);
        AddFactSubPage(indexSubPages, StoryIndexPageId, WikiSectionTables.StoryCutscenes, "cutscene", facts, stats);

        if (indexSubPages.Count > 0)
        {
            var save = store.SaveGeneratedPage(new WikiPageDetail(
                new WikiPage(StoryIndexPageId, StoryCategory, "剧情", string.Empty, "000000"), indexSubPages));
            stats.Entries += save.Entries;
            stats.Revised += save.RevisedPreserved;
            pages++;
        }

        // ② 每章一页：概览 + 章节导航（上/下章）+ 章节正文（真实台词）
        for (var i = 0; i < chapters.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chapter = chapters[i];
            var key = keys[i];
            var pageId = StoryCategory + ":" + key;
            var subPages = new List<WikiSubPageDetail>();

            AddSubPage(subPages, pageId, WikiSectionTables.Overview,
            [
                new WikiEntryDetail(new WikiEntry(
                    WikiStableIds.Of(pageId, WikiSectionTables.Overview, "overview"),
                    WikiStableIds.Of(pageId, WikiSectionTables.Overview),
                    key,
                    $"章节键 {key} · {chapter.Count} 个剧情文件 · 第 {i + 1}/{chapters.Count} 章",
                    0)
                {
                    Source = WikiEntrySources.Auto,
                    Authority = nameof(AuthoritySource.LangFileName),
                    Confidence = nameof(ConfidenceLevel.Authoritative),
                    WritableSource = nameof(WritableSourceKind.Path),
                    WritableSourcePath = chapter[0].RefKey,
                    SourceDetail = $"Lang 文件名约定 StoryData/{key}*.json",
                }, []),
            ]);

            var nav = new List<WikiEntryDetail>();
            if (i > 0) nav.Add(new WikiEntryDetail(NeighborEntry(pageId, "上一章", keys[i - 1], nav.Count), []));
            if (i + 1 < keys.Count) nav.Add(new WikiEntryDetail(NeighborEntry(pageId, "下一章", keys[i + 1], nav.Count), []));
            AddSubPage(subPages, pageId, WikiSectionTables.StoryChapters, nav);

            var body = new List<WikiEntryDetail>();
            foreach (var fact in chapter)
            {
                var lines = DialogOf(fact.RefKey);
                var text = string.Join("\n", lines.Select(line =>
                {
                    var speaker = line.Teller ?? line.Model;
                    return string.IsNullOrWhiteSpace(speaker) ? line.Content : $"{speaker}：{line.Content}";
                }));
                body.Add(new WikiEntryDetail(new WikiEntry(
                    WikiStableIds.Of(pageId, WikiSectionTables.StoryContent, fact.RefKey),
                    WikiStableIds.Of(pageId, WikiSectionTables.StoryContent),
                    fact.Display ?? fact.RefKey,
                    text,
                    body.Count)
                {
                    Source = WikiEntrySources.Auto,
                    Authority = fact.Source.ToString(),
                    Confidence = fact.Confidence.ToString(),
                    WritableSource = fact.WritableSource.ToString(),
                    WritableSourcePath = fact.WritableSourcePath,
                    SourceDetail = fact.SourceDetail,
                }, []));
            }
            AddSubPage(subPages, pageId, WikiSectionTables.StoryContent, body);

            // 登场角色：本章对白里出现过的说话人（StoryData 的 model / teller 字段，实测为角色名）。
            // 这是文件里真实存在的字段值，不是猜 id；出现次数一并记下来。
            AddSubPage(subPages, pageId, WikiSectionTables.StoryCharacters, ChapterSpeakers(pageId, chapter, DialogOf));

            if (subPages.Count > 0)
            {
                var save = store.SaveGeneratedPage(new WikiPageDetail(
                    new WikiPage(pageId, StoryCategory, key, string.Empty, key), subPages));
                stats.Entries += save.Entries;
                stats.Revised += save.RevisedPreserved;
                pages++;
            }

            if ((i + 1) % 25 == 0 || i + 1 == chapters.Count)
                progress?.Report(new WikiGenerationProgress("story", i + 1, chapters.Count,
                    $"正在生成剧情页：{i + 1}/{chapters.Count}（{key}）"));
        }

        return pages;
    }

    /// <summary>剧情摘录/章节列表的截断上限（行）。</summary>
    private const int MaxStoryExcerptLines = 3;

    /// <summary>
    /// 可以直接展示出来的媒体绑定：即使没有正文，图片/音频/视频/动画本身也是内容。
    /// 其余（<c>StaticData</c> / <c>Text</c> / <c>Prefab</c> / <c>Mesh</c> …）拿不到正文就不渲染。
    /// </summary>
    private static bool IsRenderableMedia(string? kind)
        => string.Equals(kind, "Image", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "Audio", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "Video", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "Spine", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 本章的剧情文件清单（真实文件名）。写不出文件名时返回空串 —— 不写「N 个文件」这种计数串。
    /// </summary>
    private static string ChapterFileList(IReadOnlyList<AuthorityFact> chapter)
    {
        var names = new List<string>();
        foreach (var fact in chapter)
        {
            var name = FirstNonEmpty(fact.Display, FileNameOf(fact.RefKey));
            if (name is not null) names.Add(name);
        }
        return names.Count == 0 ? string.Empty : string.Join('\n', names);
    }

    /// <summary>路径的最后一段（lang 的 refKey 用 <c>/</c> 分隔）。</summary>
    private static string? FileNameOf(string? refKey)
    {
        if (string.IsNullOrWhiteSpace(refKey)) return null;
        var index = refKey.LastIndexOf('/');
        var name = index >= 0 ? refKey[(index + 1)..] : refKey;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// 本章的真实台词摘录（前若干行，含说话人）。读不到台词时返回空串 —— 不写「N 句对白」。
    /// </summary>
    private static string ChapterDialogExcerpt(
        IReadOnlyList<AuthorityFact> chapter, Func<string, IReadOnlyList<StoryDialogLine>> dialogOf)
    {
        var texts = new List<string>();
        foreach (var fact in chapter)
        {
            foreach (var line in dialogOf(fact.RefKey))
            {
                var speaker = line.Teller ?? line.Model;
                var content = line.Content?.Trim();
                if (string.IsNullOrEmpty(content)) continue;
                texts.Add(string.IsNullOrWhiteSpace(speaker) ? content : $"{speaker}：{content}");
                if (texts.Count >= MaxStoryExcerptLines) break;
            }
            if (texts.Count >= MaxStoryExcerptLines) break;
        }
        return texts.Count == 0 ? string.Empty : string.Join('\n', texts);
    }

    /// <summary>
    /// 本章的登场角色：对白行里的 <c>model</c> / <c>teller</c> 字段值（实测是角色名），
    /// 按出现句数从多到少排；正文写该角色在本章的**第一句真实台词**（不是句数）。
    /// 文件里没有说话人标注时返回空 —— 不猜、不占位。
    /// </summary>
    private static List<WikiEntryDetail> ChapterSpeakers(
        string pageId,
        IReadOnlyList<AuthorityFact> chapter,
        Func<string, IReadOnlyList<StoryDialogLine>> dialogOf)
    {
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstFile = new Dictionary<string, string>(StringComparer.Ordinal);
        var firstLine = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in chapter)
        {
            foreach (var line in dialogOf(fact.RefKey))
            {
                var name = FirstNonEmpty(line.Model, line.Teller);
                if (name is null) continue;
                lines[name] = lines.GetValueOrDefault(name) + 1;
                firstFile.TryAdd(name, fact.RefKey);
                var content = line.Content?.Trim();
                if (!string.IsNullOrEmpty(content)) firstLine.TryAdd(name, content);
            }
        }

        var entries = new List<WikiEntryDetail>();
        foreach (var name in lines.OrderByDescending(pair => pair.Value)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                     .Select(pair => pair.Key))
        {
            var path = firstFile[name];
            // 正文必须是真台词；拿不到台词的角色不落条目（不写「N 句对白」，不占位）。
            var spoken = firstLine.GetValueOrDefault(name);
            if (string.IsNullOrWhiteSpace(spoken)) continue;
            entries.Add(new WikiEntryDetail(new WikiEntry(
                WikiStableIds.Of(pageId, WikiSectionTables.StoryCharacters, "speaker:" + name),
                WikiStableIds.Of(pageId, WikiSectionTables.StoryCharacters),
                name,
                spoken.Length > 160 ? spoken[..160] + "…" : spoken,
                entries.Count)
            {
                Source = WikiEntrySources.Auto,
                Authority = nameof(AuthoritySource.LangFileName),
                Confidence = nameof(ConfidenceLevel.Authoritative),
                WritableSource = nameof(WritableSourceKind.Path),
                WritableSourcePath = path,
                SourceDetail = $"StoryData 对白字段 model/teller（{path}）",
            }, []));
        }
        return entries;
    }

    private static WikiEntry NeighborEntry(string pageId, string label, string chapterKey, int order)
        => new(WikiStableIds.Of(pageId, WikiSectionTables.StoryChapters, "nav:" + chapterKey),
            WikiStableIds.Of(pageId, WikiSectionTables.StoryChapters),
            $"{label}：{chapterKey}", $"../{chapterKey}", order)
        {
            Source = WikiEntrySources.Auto,
            Authority = nameof(AuthoritySource.LangFileName),
            Confidence = nameof(ConfidenceLevel.Authoritative),
            WritableSource = nameof(WritableSourceKind.Unknown),
            SourceDetail = $"按章节键顺序（Lang 文件名约定）推出的相邻章节：{chapterKey}",
        };

    private static WikiEntryDetail FactEntry(
        string pageId, string sectionId, AuthorityFact fact, int order, WriteStats stats)
    {
        if (fact.WritableSource != WritableSourceKind.Path) stats.Unknown++;
        return new WikiEntryDetail(new WikiEntry(
            WikiStableIds.Of(pageId, sectionId, fact.RefKey),
            WikiStableIds.Of(pageId, sectionId),
            fact.Display ?? fact.RefKey,
            fact.PreviewText ?? string.Empty,
            order)
        {
            Source = WikiEntrySources.Auto,
            Authority = fact.Source.ToString(),
            Confidence = fact.Confidence.ToString(),
            WritableSource = fact.WritableSource.ToString(),
            WritableSourcePath = fact.WritableSourcePath,
            SourceDetail = fact.SourceDetail,
        }, []);
    }

    private static void AddFactSubPage(
        List<WikiSubPageDetail> subPages, string pageId, string sectionId, string contentType,
        IReadOnlyList<AuthorityFact> facts, WriteStats stats)
    {
        var entries = new List<WikiEntryDetail>();
        foreach (var fact in facts.Where(f => f.ContentType == contentType).OrderBy(f => f.RefKey, StringComparer.Ordinal))
        {
            var entry = FactEntry(pageId, sectionId, fact, entries.Count, stats);
            // 推不出真正文的事实不落条目：页面上不留「有标题、无正文」的空壳。
            if (string.IsNullOrWhiteSpace(entry.Entry.Body)) continue;
            entries.Add(entry);
        }
        AddSubPage(subPages, pageId, sectionId, entries);
    }

    private static void AddSubPage(
        List<WikiSubPageDetail> subPages, string pageId, string sectionId, List<WikiEntryDetail> entries)
    {
        if (entries.Count == 0) return;                          // 本地无来源 / 无内容 → 不占位
        subPages.Add(new WikiSubPageDetail(
            new WikiSubPage(WikiStableIds.Of(pageId, sectionId), pageId, TitleOf(sectionId), subPages.Count),
            entries));
    }

    private static string TitleOf(string sectionId)
    {
        foreach (var section in WikiSectionTables.Story)
            if (string.Equals(section.SectionId, sectionId, StringComparison.Ordinal)) return section.Title;
        return sectionId;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    /// <summary>
    /// 拦掉「N 条可解析文本 / N 句对白 / N 个剧情文件」这类**计数串**：它们不是正文，
    /// 写进 <c>body</c> 就是「标题对了、正文是空的」。拦截后返回 null，由调用方决定降级。
    /// </summary>
    private static string? NonPlaceholder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(
            value, @"^\d+\s*(条可解析文本|句对白|个剧情文件|条事实)$") ? null : value;
    }
}
