using System.Diagnostics;
using System.Text.Json;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Domain.Diagnostics;
using NLog;

namespace LimbusModEditor.Cli;

/// <summary>
/// 无界面的「生成维基页面」：与 Wiki 首页「生成页面」按钮走**同一条链路**
/// （<see cref="WikiAutoGenerationService"/>：本地事实源 → 权威引擎 → 编排 → 落
/// <c>cache/wiki-pages.db</c>），但没有 WPF 消息循环。
///
/// <para>用途：在真实数据上量出页面/分节/条目数量与耗时，并导出某个页面的完整内容
/// （每条条目的 <c>Authority</c> / <c>Confidence</c> / <c>WritableSource</c>），
/// 用于验证「只写本地能推出来的数据」这条口径。</para>
///
/// <para>环境变量：<c>LME_BASE=&lt;程序目录&gt;</c> 指定共享配置目录（默认取 CLI 自己的程序目录，
/// 那里通常没有 config/ 与 cache/，因此真实数据自测要显式给，一般指向发布目录
/// <c>artifacts/publish-win-x64</c>）。</para>
/// </summary>
internal static class WikiGenerateCommand
{
    private static readonly Logger Log = LogManager.GetLogger("LimbusModEditor.Cli.WikiGenerate");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>执行一次生成。返回进程退出码（0 成功 / 1 前提缺失或失败）。</summary>
    public static async Task<int> RunAsync(
        string projectFile, string? baseDirectory, string? dumpPageId, string? outputPath)
    {
        using var scope = Log.Scope($"wiki-generate {projectFile}");
        var project = await new ProjectService().LoadAsync(projectFile);
        Log.Info("项目已载入：{0}", project.Name);

        var environment = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppEnvironment.Current
            : new AppEnvironment(baseDirectory);
        var service = new WikiAutoGenerationService(environment);
        var store = service.CreateStore();
        Log.Info("维基页库：{0}（已存在 {1}）", store.DatabasePath, store.Exists);

        var progressCount = 0;
        var progress = new Progress<WikiGenerationProgress>(step =>
        {
            var index = Interlocked.Increment(ref progressCount);
            if (index == 1 || index % 25 == 0 || step.Current >= step.Total)
            {
                Log.Debug("进度 #{0}：{1}", index, step.Message);
                Console.WriteLine($"  [{step.Phase}] {step.Current}/{step.Total} {step.Message}");
            }
        });

        var watch = Stopwatch.StartNew();
        var result = await service.GenerateAsync(project, progress, CancellationToken.None);
        watch.Stop();

        Console.WriteLine(result.Describe());
        foreach (var category in result.Categories)
            Console.WriteLine($"  · {category.Label}（{category.Category}）：{category.PageCount} 个页面");

        var (auto, revised) = store.SourceStatistics();
        Console.WriteLine($"条目来源：auto {auto} / revised {revised}");
        Console.WriteLine(
            $"本次写入 {result.WrittenEntries} 条 · 保留修订 {result.RevisedPreserved} 条 · " +
            $"无权威来源（只读）{result.UnknownSourceEntries} 条 · 墙钟 {watch.Elapsed.TotalSeconds:0.00} 秒");
        Log.Info("维基生成结束：{0}", result.Describe());

        if (!string.IsNullOrWhiteSpace(dumpPageId))
        {
            // pageId 允许逗号分隔：一次导出多个页面（如人格页 + 剧情页）
            var pages = dumpPageId!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => DumpPage(store, id))
                .Where(dump => dump is not null)
                .ToList();
            if (pages.Count == 0)
            {
                Console.WriteLine($"页面不存在：{dumpPageId}");
                return result.Ok ? 0 : 1;
            }

            var dump = pages.Count == 1
                ? JsonSerializer.Serialize(pages[0], JsonOptions)
                : JsonSerializer.Serialize(pages, JsonOptions);

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                Console.WriteLine(dump);
            }
            else
            {
                var full = Path.GetFullPath(outputPath!);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, dump, new System.Text.UTF8Encoding(false));
                Console.WriteLine($"页面内容已写入：{full}");
            }
        }

        return result.Ok ? 0 : 1;
    }

    /// <summary>把一个页面（分节 → 条目 → 绑定 + 权威三元组）序列化成可导出的对象。</summary>
    private static object? DumpPage(WikiPageStore store, string pageId)
    {
        var page = store.ReadPage(pageId);
        if (page is null) return null;

        var subPages = new List<object>();
        foreach (var sub in store.ReadSubPages(pageId))
        {
            var entries = new List<object>();
            foreach (var entry in store.ReadEntries(sub.SubPageId))
            {
                var bindings = store.ReadBindings(entry.EntryId).Select(b => new
                {
                    b.BindingId,
                    b.RefKey,
                    b.Kind,
                    b.Display,
                    b.DeepLink,
                    b.MediaKind,
                }).ToList();
                entries.Add(new
                {
                    entry.EntryId,
                    entry.Title,
                    entry.Body,
                    entry.SortOrder,
                    entry.Source,
                    entry.Authority,
                    entry.Confidence,
                    entry.WritableSource,
                    entry.WritableSourcePath,
                    entry.SourceDetail,
                    Editable = entry.Editable,
                    Bindings = bindings,
                });
            }
            subPages.Add(new { sub.SubPageId, sub.Title, sub.SortOrder, EntryCount = entries.Count, Entries = entries });
        }

        var payload = new
        {
            page.PageId,
            page.Category,
            page.Title,
            page.Subtitle,
            page.SortKey,
            page.CoverRef,
            SubPageCount = subPages.Count,
            SubPages = subPages,
        };
        return payload;
    }
}
