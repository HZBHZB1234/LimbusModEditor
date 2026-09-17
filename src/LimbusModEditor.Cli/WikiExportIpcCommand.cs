using System.Diagnostics;
using System.Text.Json;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Ipc;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Application.Relations;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Diagnostics;
using NLog;

namespace LimbusModEditor.Cli;

/// <summary>
/// 「真实 IPC 导出」：构造与宿主（<c>WebView2MainWindow</c>）一致的网关组合，
/// 对每个页面 id 走一次真正的 <c>wiki.getPage</c>，把响应 <b>payload 原样</b>落成 JSON。
///
/// <para>与 <c>wiki-generate</c> 的 dump 不同：那条路导出的是**数据库模型**
/// （PascalCase：PageId/SubPages/Entries/CoverRef），会掩盖前端契约问题；
/// 本命令经 <see cref="IpcGateway"/> + <c>IpcJson</c>（camelCase）序列化，
/// 与 WebView2 里前端实际收到的字节一致，可直接当验收夹具。</para>
///
/// <para><b>目录布局要求</b>：<see cref="IpcGateway"/> 内部经静态
/// <c>AppEnvironment.Current</c>（= CLI 程序自身所在目录）解析 wiki-pages.db，
/// 因此本命令**不受 LME_BASE 影响** —— 必须把 CLI 放在与宿主同布局的目录运行
/// （<c>cache/</c>、<c>config/</c>、<c>fmod/</c> 与 exe 同级；自测时可用目录联接
/// 把发布目录的这几个目录挂到 CLI 旁边，不要复制数百 MB 的缓存）。</para>
/// </summary>
internal static class WikiExportIpcCommand
{
    private static readonly Logger Log = LogManager.GetLogger("LimbusModEditor.Cli.WikiExportIpc");

    /// <summary>执行一次导出。返回进程退出码（0 成功 / 1 前提缺失或失败）。</summary>
    public static async Task<int> RunAsync(
        string projectFile, string pageIdsCsv, string outputPath)
    {
        using var scope = Log.Scope($"wiki-export-ipc {projectFile} [{pageIdsCsv}]");
        _ = await new ProjectService().LoadAsync(projectFile);

        var environment = AppEnvironment.Current;
        Log.Info("base={0} → cache={1}", environment.BaseDirectory, environment.CacheDirectory);
        if (!Directory.Exists(environment.CacheDirectory))
        {
            Console.Error.WriteLine(
                $"[wiki-export-ipc] 找不到缓存目录 {environment.CacheDirectory}。" +
                "本命令须在与宿主同布局的目录运行（cache/ 与 exe 同级）；LME_BASE 对它无效。");
            return 1;
        }

        // 与 WebView2MainWindow 相同的组合根（无 DI，就地拼装）。
        var store = new UnityCacheSqliteIndexStore(
            Path.Combine(environment.CacheDirectory, "unity-cache-index.json"));
        var catalog = new AssetCatalog(store, EmptyAssetStateSource.Instance);
        var spineData = SpineDataGatewayFactory.Create(
            Path.Combine(environment.CacheDirectory, "spine-data.db"));
        var bankIndex = new BankIndexService(new BankIndexStore(environment.CacheDirectory));
        var langText = new LangTextWorkbenchService();
        var staticIndex = new StaticIndexService(new StaticTableIndexStore(environment.CacheDirectory));
        var gateway = new IpcGateway(catalog, new ProjectState(), spineData, bankIndex, langText, staticIndex);

        var pageIds = pageIdsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        var payloads = new List<JsonElement>();
        foreach (var pageId in pageIds)
        {
            var requestJson = JsonSerializer.Serialize(new
            {
                id = "cli-export-" + Guid.NewGuid().ToString("N"),
                kind = "request",
                method = "wiki.getPage",
                payload = new { pageId },
            });
            var watch = Stopwatch.StartNew();
            string responseJson;
            try
            {
                responseJson = await gateway.HandleRequestAsync(requestJson);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "wiki.getPage 抛异常：{0}", pageId);
                Console.Error.WriteLine($"[wiki-export-ipc] {pageId} ✗ {ex.Message}");
                return 1;
            }
            watch.Stop();

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var message = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var m) ? m.GetString() : "(无错误信息)";
                Log.Error("wiki.getPage 失败：{0} → {1}", pageId, message);
                Console.Error.WriteLine($"[wiki-export-ipc] {pageId} ✗ {message}");
                return 1;
            }

            // payload 原样（不加工）：Clone 脱离 JsonDocument 生命周期后再入列。
            payloads.Add(root.GetProperty("payload").Clone());
            Log.Info(
                "wiki.getPage 成功：{0}（{1:0.0}s，响应 {2} 字节）",
                pageId, watch.Elapsed.TotalSeconds, responseJson.Length);
            Console.WriteLine($"  {pageId} ✓ {watch.Elapsed.TotalSeconds:0.0}s");
        }

        var full = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // 序列化只做缩进与中文可读转义，不改动任何字段名/结构。
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        await File.WriteAllTextAsync(full, JsonSerializer.Serialize(payloads, options), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"IPC payload 已导出（{payloads.Count} 页）→ {full}");
        return 0;
    }
}
