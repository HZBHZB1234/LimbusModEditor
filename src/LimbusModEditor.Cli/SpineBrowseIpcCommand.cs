using System.Diagnostics;
using System.Text.Json;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Caching;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Ipc;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Diagnostics;
using NLog;

namespace LimbusModEditor.Cli;

/// <summary>
/// 「Spine 全库浏览」的真实 IPC 证据：构造与宿主（<c>WebView2MainWindow</c>）一致的网关组合，
/// 走真正的 <c>spine.catalog</c> / <c>spine.resolve</c>，把响应 payload <b>原样</b>落成 JSON。
///
/// <para>为什么要有它：<c>spine.catalog</c> / <c>spine.resolve</c> 是前端「Spine 总览」页
/// 唯二的取数入口，验证它们不能靠读代码 —— 要拿真实缓存跑一遍，看会话真的能取到
/// 三件套地址（<c>https://lme.data/wiki/*.json</c> / <c>.atlas.txt</c> / <c>.png</c>），
/// 并且这些地址指向的文件真的在磁盘上、非空。</para>
///
/// <para><b>目录布局要求</b>：与 <c>wiki-export-ipc</c> 相同 —— 网关内部经静态
/// <c>AppEnvironment.Current</c> 解析 <c>wwwroot/data/wiki</c>，所以必须把 CLI 放在与宿主
/// 同布局的目录运行（用目录联接挂 <c>cache/</c>，不要复制几百 MB）。</para>
///
/// <para>用法：<c>spine-browse-ipc &lt;关键词&gt; &lt;条数&gt; &lt;out.json&gt;</c></para>
/// </summary>
internal static class SpineBrowseIpcCommand
{
    private static readonly Logger Log = LogManager.GetLogger("LimbusModEditor.Cli.SpineBrowseIpc");

    public static async Task<int> RunAsync(string keyword, int count, string outputPath)
    {
        using var scope = Log.Scope($"spine-browse-ipc [{keyword}] × {count}");
        var environment = AppEnvironment.Current;
        Log.Info("base={0} → cache={1}", environment.BaseDirectory, environment.CacheDirectory);
        if (!Directory.Exists(environment.CacheDirectory))
        {
            Console.Error.WriteLine(
                $"[spine-browse-ipc] 找不到缓存目录 {environment.CacheDirectory}。" +
                "本命令须在与宿主同布局的目录运行（cache/ 与 exe 同级）。");
            return 1;
        }

        // 与 WebView2MainWindow 相同的组合根（无 DI，就地拼装）。
        var store = UnityCacheSqliteIndexStore.ForCacheDirectory(environment.CacheDirectory);
        var catalog = new AssetCatalog(store, EmptyAssetStateSource.Instance);
        var spineData = SpineDataGatewayFactory.Create(
            Path.Combine(environment.CacheDirectory, WorkbenchCachePaths.UnityCacheIndexFileName));
        var bankIndex = new BankIndexService(new BankIndexStore(environment.CacheDirectory));
        var langText = new LangTextWorkbenchService();
        var staticIndex = new StaticIndexService(new StaticTableIndexStore(environment.CacheDirectory));
        var gateway = new IpcGateway(catalog, new ProjectState(), spineData, bankIndex, langText, staticIndex);

        // ── ① spine.catalog：只列名册，不解素材 ─────────────────────
        var catalogWatch = Stopwatch.StartNew();
        var catalogPayload = await SendAsync(gateway, "spine.catalog", new
        {
            keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword,
            onlyUnbound = true,
            offset = 0,
            limit = Math.Max(1, count),
        });
        catalogWatch.Stop();
        if (catalogPayload is null) return 1;

        var summary = catalogPayload.Value.GetProperty("summary");
        Console.WriteLine(
            $"[spine.catalog] 挂点 {summary.GetProperty("total").GetInt32()} · " +
            $"已绑定 {summary.GetProperty("bound").GetInt32()} · " +
            $"未绑定 {summary.GetProperty("unbound").GetInt32()} · " +
            $"bundle 不在本机 {summary.GetProperty("bundleMissing").GetInt32()} " +
            $"（命中 {catalogPayload.Value.GetProperty("total").GetInt32()} 条，耗时 {catalogWatch.Elapsed.TotalSeconds:0.00}s）");

        var items = catalogPayload.Value.GetProperty("items").EnumerateArray().ToList();
        Console.WriteLine($"名册前 {Math.Min(5, items.Count)} 条：");
        foreach (var item in items.Take(5))
            Console.WriteLine($"    {item.GetProperty("name").GetString()} [{item.GetProperty("group").GetString()}]" +
                $" 绑定 {item.GetProperty("boundPageCount").GetInt32()} 页" +
                $" bundle在={item.GetProperty("bundlePresent").GetBoolean()}");

        // ── ② spine.resolve：按需解三件套，逐个核对落盘文件 ──────────
        var report = new List<object>();
        var ok = 0;
        var fail = 0;
        foreach (var item in items)
        {
            var refKey = item.GetProperty("refKey").GetString()!;
            var resolveWatch = Stopwatch.StartNew();
            var payload = await SendAsync(gateway, "spine.resolve", new { refKey });
            resolveWatch.Stop();
            if (payload is null) return 1;

            var isOk = payload.Value.GetProperty("ok").GetBoolean();
            var skeletonUrl = Text(payload.Value, "skeletonUrl");
            var atlasUrl = Text(payload.Value, "atlasUrl");
            var reason = Text(payload.Value, "reason");
            var pages = payload.Value.TryGetProperty("textureUrls", out var tex) && tex.ValueKind == JsonValueKind.Object
                ? tex.EnumerateObject().Select(p => p.Name).Distinct().ToList()
                : [];

            // 地址 → 磁盘文件：地址是 https://lme.data/wiki/{name}，宿主把它映射到 wwwroot/data/wiki。
            var onDisk = new Dictionary<string, long>();
            foreach (var url in new[] { skeletonUrl, atlasUrl }.Where(u => u is not null))
                RecordOnDisk(environment, url!, onDisk);

            if (isOk) ok++; else fail++;
            Console.WriteLine(isOk
                ? $"  ✓ {item.GetProperty("name").GetString()}（{resolveWatch.Elapsed.TotalSeconds:0.0}s）骨架={skeletonUrl} 图集={atlasUrl} 纹理 {pages.Count} 页"
                : $"  ✗ {item.GetProperty("name").GetString()}（{resolveWatch.Elapsed.TotalSeconds:0.0}s）{reason}");

            report.Add(new
            {
                refKey,
                ok = isOk,
                skeletonUrl,
                atlasUrl,
                texturePages = pages,
                reason,
                elapsedSeconds = Math.Round(resolveWatch.Elapsed.TotalSeconds, 2),
                onDiskBytes = onDisk,
            });
        }

        Console.WriteLine($"\n=== 结果 ===\nspine.resolve 命中 {ok} / 未命中 {fail}（名册里未绑定的前 {items.Count} 条）");

        var full = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        await File.WriteAllTextAsync(full, JsonSerializer.Serialize(new
        {
            catalogSummary = JsonSerializer.Deserialize<JsonElement>(summary.GetRawText()),
            catalogElapsedSeconds = Math.Round(catalogWatch.Elapsed.TotalSeconds, 2),
            resolved = report,
        }, options), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"IPC payload 已导出 → {full}");
        return 0;
    }

    /// <summary>把 lme.data 地址映射回磁盘文件并记录字节数（核对「地址不是编的」）。</summary>
    private static void RecordOnDisk(AppEnvironment environment, string url, Dictionary<string, long> into)
    {
        const string prefix = "https://lme.data/";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
        var relative = url[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        var file = Path.Combine(environment.BaseDirectory, "wwwroot", "data", relative);
        into[Path.GetFileName(file)] = File.Exists(file) ? new FileInfo(file).Length : -1;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>发一条真实 IPC 请求并取回 payload（失败打印中文原因并返回 null）。</summary>
    private static async Task<JsonElement?> SendAsync(IpcGateway gateway, string method, object payload)
    {
        var requestJson = JsonSerializer.Serialize(new
        {
            id = "cli-" + Guid.NewGuid().ToString("N"),
            kind = "request",
            method,
            payload,
        });
        var responseJson = await gateway.HandleRequestAsync(requestJson);
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var okElement) || !okElement.GetBoolean())
        {
            var message = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var m) ? m.GetString() : "(无错误信息)";
            Console.Error.WriteLine($"[spine-browse-ipc] {method} ✗ {message}");
            Log.Error("{0} 失败：{1}", method, message);
            return null;
        }
        Log.Info("{0} 成功", method);
        return root.GetProperty("payload").Clone();
    }
}
