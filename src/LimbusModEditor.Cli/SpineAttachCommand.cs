using System.Diagnostics;
using System.Text.Json;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Caching;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.SpineData;
using NLog;

namespace LimbusModEditor.Cli;

/// <summary>
/// 「未绑定 Spine 接入维基」的真实数据端到端证据：
/// ① 读全库 Spine 名册 ② 把未绑定的挂到既有页 ③ <b>再跑一遍</b>证明幂等
/// ④ 逐页核对落库的绑定行与三件套地址。
///
/// <para>它直接调生产服务（<see cref="SpineWikiBindingService"/>），不复制一份逻辑 ——
/// 所以这里的输出就是「按钮按下会发生什么」。</para>
///
/// <para>环境变量 <c>LME_BASE</c> 指定共享配置目录（一般指向 <c>artifacts/publish-win-x64</c>，
/// 那里的 <c>cache/</c> 有真实的五个索引库）。</para>
/// </summary>
internal static class SpineAttachCommand
{
    private static readonly Logger Log = LogManager.GetLogger("LimbusModEditor.Cli.SpineAttach");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(string? baseDirectory, string? dumpPageIds, string? outputPath)
    {
        var environment = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppEnvironment.Current
            : new AppEnvironment(baseDirectory);
        Log.Info("base={0} → cache={1}", environment.BaseDirectory, environment.CacheDirectory);

        var store = new WikiPageStore(environment.CacheDirectory);
        if (!File.Exists(store.DatabasePath))
        {
            Console.Error.WriteLine(
                $"[spine-attach] 找不到维基页库 {store.DatabasePath}。" +
                "请先跑一次 wiki-generate 建好页面，本命令只往既有页上挂 Spine。");
            return 1;
        }

        var gateway = SpineDataGatewayFactory.Create(
            Path.Combine(environment.CacheDirectory, WorkbenchCachePaths.UnityCacheIndexFileName));

        // ── ① 名册（未绑定口径，limit 给足）────────────────────────────
        var catalogWatch = Stopwatch.StartNew();
        var catalog = await gateway.BrowseCatalogAsync(
            new SpineCatalogQuery(null, OnlyUnbound: true, Offset: 0, Limit: int.MaxValue));
        var summary = await gateway.SummarizeCatalogAsync();
        catalogWatch.Stop();

        Console.WriteLine(
            $"[名册] 挂点 {summary.Total} · 已绑定 {summary.Bound} · 未绑定 {summary.Unbound} · " +
            $"bundle 不在本机 {summary.BundleMissing}（未绑定明细 {catalog.Items.Count} 条，耗时 {catalogWatch.Elapsed.TotalSeconds:0.00}s）");

        var before = CountSpineBindings(store);
        Console.WriteLine($"[接入前] resource_bindings 的 Spine 绑定 {before.KindSpine} 条 · media_kind=spine {before.MediaSpine} 条 · " +
                          $"条目 {before.Entries} · 绑定合计 {before.Bindings}");

        // ── ② 第一次接入 ───────────────────────────────────────────────
        var service = new SpineWikiBindingService(environment);
        var firstWatch = Stopwatch.StartNew();
        var first = service.Attach(store, catalog.Items, Progress(), CancellationToken.None);
        firstWatch.Stop();
        Console.WriteLine($"[第一次] {first.Describe()} · 耗时 {firstWatch.Elapsed.TotalSeconds:0.00}s");

        var afterFirst = CountSpineBindings(store);
        Console.WriteLine($"[第一次后] Spine 绑定 {afterFirst.KindSpine} 条 · media_kind=spine {afterFirst.MediaSpine} 条 · " +
                          $"条目 {afterFirst.Entries} · 绑定合计 {afterFirst.Bindings}");

        // ── ③ 第二次接入（幂等验证）────────────────────────────────────
        var secondCatalog = await gateway.BrowseCatalogAsync(
            new SpineCatalogQuery(null, OnlyUnbound: true, Offset: 0, Limit: int.MaxValue));
        var secondWatch = Stopwatch.StartNew();
        var second = service.Attach(store, secondCatalog.Items, null, CancellationToken.None);
        secondWatch.Stop();
        Console.WriteLine($"[第二次] 名册未绑定 {secondCatalog.Items.Count} 条 · {second.Describe()} · 耗时 {secondWatch.Elapsed.TotalSeconds:0.00}s");

        var afterSecond = CountSpineBindings(store);
        Console.WriteLine($"[第二次后] Spine 绑定 {afterSecond.KindSpine} 条 · media_kind=spine {afterSecond.MediaSpine} 条 · " +
                          $"条目 {afterSecond.Entries} · 绑定合计 {afterSecond.Bindings}");

        var idempotent = afterFirst == afterSecond;
        Console.WriteLine(idempotent
            ? "=== 幂等验证：通过（第二次重跑后计数与第一次完全一致）==="
            : $"=== 幂等验证：不通过 ===\n  第一次 {afterFirst}\n  第二次 {afterSecond}");

        // ── ④ 逐页核对：页面 → Spine 绑定 → 三件套地址 ─────────────────
        var dumped = new List<object>();
        var probePages = (dumpPageIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pageId in probePages)
        {
            var detail = DumpPageSpine(store, pageId);
            if (detail is null)
            {
                Console.WriteLine($"[核对] 页面不存在或没有 Spine 绑定：{pageId}");
                continue;
            }
            dumped.Add(detail);
            Console.WriteLine($"[核对] {pageId}：「Spine 与动画」分节 {detail.SpineEntries} 条");
        }

        var payload = new
        {
            summary = new
            {
                summary.Total, summary.Bound, summary.Unbound, summary.BundleMissing,
                catalogElapsedSeconds = Math.Round(catalogWatch.Elapsed.TotalSeconds, 2),
            },
            before,
            first = Describe(first, firstWatch.Elapsed),
            afterFirst,
            second = Describe(second, secondWatch.Elapsed),
            afterSecond,
            idempotent,
            pages = dumped,
        };

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var full = Path.GetFullPath(outputPath!);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, JsonSerializer.Serialize(payload, JsonOptions),
                new System.Text.UTF8Encoding(false));
            Console.WriteLine($"证据已写入：{full}");
        }

        return idempotent ? 0 : 1;
    }

    private static IProgress<WikiGenerationProgress> Progress()
        => new Progress<WikiGenerationProgress>(step =>
        {
            if (step.Current == 0 || step.Current % 50 == 0 || step.Current >= step.Total)
                Console.WriteLine($"  [{step.Phase}] {step.Current}/{step.Total} {step.Message}");
        });

    private static object Describe(SpineWikiBindingService.SpineBindingResult r, TimeSpan elapsed) => new
    {
        r.HookPoints, r.AlreadyBound, r.Unbound, r.Attached, r.AttachedBindings, r.PagesUpdated,
        r.BundleMissing, r.Uncategorized, r.TargetMissing, r.RevisedPreserved, r.Cancelled,
        coveragePercent = Math.Round(r.Coverage, 1),
        elapsedSeconds = Math.Round(elapsed.TotalSeconds, 2),
        summary = r.Describe(),
    };

    /// <summary>库里的计数快照（幂等比较用）。</summary>
    private sealed record Counts(int KindSpine, int MediaSpine, int Entries, int Bindings)
    {
        public override string ToString() => $"Spine {KindSpine} · media {MediaSpine} · 条目 {Entries} · 绑定 {Bindings}";
    }

    private static Counts CountSpineBindings(WikiPageStore store)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={store.DatabasePath};Mode=ReadOnly");
        connection.Open();
        int Scalar(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(command.ExecuteScalar());
        }
        return new Counts(
            Scalar("SELECT COUNT(*) FROM resource_bindings WHERE kind='Spine'"),
            Scalar("SELECT COUNT(*) FROM resource_bindings WHERE media_kind='spine'"),
            Scalar("SELECT COUNT(*) FROM entries"),
            Scalar("SELECT COUNT(*) FROM resource_bindings"));
    }

    /// <summary>一个页面里「Spine 与动画」分节的内容（含真实三件套地址）。</summary>
    private static PageDump? DumpPageSpine(WikiPageStore store, string pageId)
    {
        var page = store.ReadPage(pageId);
        if (page is null) return null;

        var entries = new List<object>();
        foreach (var sub in store.ReadSubPages(pageId))
        {
            if (!string.Equals(sub.Title, "Spine 与动画", StringComparison.Ordinal)) continue;
            foreach (var entry in store.ReadEntries(sub.SubPageId))
            {
                var bindings = store.ReadBindings(entry.EntryId).Select(b => new
                {
                    b.RefKey, b.Kind, b.MediaKind, b.Display, b.BindingId,
                }).ToList();
                entries.Add(new { entry.EntryId, entry.Title, entry.Body, entry.Source, entry.SourceDetail, Bindings = bindings });
            }
        }
        if (entries.Count == 0) return null;
        return new PageDump(pageId, page.Title, page.Category, entries.Count, entries);
    }

    private sealed record PageDump(
        string PageId, string Title, string Category, int SpineEntries, IReadOnlyList<object> Entries);
}
