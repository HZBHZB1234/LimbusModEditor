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
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Diagnostics;
using NLog;

namespace LimbusModEditor.Cli;
/// <summary>
/// <c>spine.export</c> 的真实 IPC 证据：构造与宿主（<c>WebView2MainWindow</c>）一致的网关组合，
/// 走真正的 <c>spine.export</c> 把一个或多个 refKey 的三件套导出到临时目录，
/// 然后<b>回到磁盘上逐个文件核对</b>：非空、字节数与响应一致、头字节正确
/// （骨架 JSON 以 <c>{</c> 开头、PNG 以 <c>\x89PNG</c> 开头）。
///
/// <para><b>为什么要有它</b>：导出是唯一会往用户磁盘写东西的 Spine 通路，
/// 「响应说写了」不等于「磁盘上真有、而且内容对」。这个命令把 IPC 的响应和真实文件
/// 摆在一张表里，任何一方撒谎都会露馅。</para>
///
/// <para>用法：<c>spine-export-ipc &lt;refKey1[,refKey2,…]&gt; &lt;目标目录&gt; [out.json]</c></para>
///
/// <para>环境变量 <c>LME_BASE</c> 指定共享配置目录（一般指向 <c>artifacts/publish-win-x64</c>，
/// 那里的 <c>cache/</c> 有真实的索引库）。</para>
/// </summary>
internal static class SpineExportIpcCommand
{
    private static readonly Logger Log = LogManager.GetLogger("LimbusModEditor.Cli.SpineExportIpc");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>PNG 文件头（8 字节魔数）。</summary>
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static async Task<int> RunAsync(string? baseDirectory, string refKeysArg, string targetDirectory, string? outputPath)
    {
        using var scope = Log.Scope($"spine-export-ipc → {targetDirectory}");
        var environment = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppEnvironment.Current
            : new AppEnvironment(baseDirectory);
        Log.Info("base={0} → cache={1}", environment.BaseDirectory, environment.CacheDirectory);

        if (!File.Exists(Path.Combine(environment.CacheDirectory, WorkbenchCachePaths.UnityCacheIndexFileName)))
        {
            Console.Error.WriteLine(
                $"[spine-export-ipc] 找不到索引库 {Path.Combine(environment.CacheDirectory, WorkbenchCachePaths.UnityCacheIndexFileName)}。" +
                "请用 LME_BASE 指向带真实 cache/ 的目录。");
            return 1;
        }

        var refKeys = refKeysArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (refKeys.Count == 0)
        {
            Console.Error.WriteLine("[spine-export-ipc] 至少要给一个 refKey。");
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

        // ── 走真正的 spine.export ──────────────────────────────────────
        var watch = Stopwatch.StartNew();
        var payload = await SendAsync(gateway, "spine.export", new
        {
            assetIds = refKeys,
            targetDirectory = Path.GetFullPath(targetDirectory),
        });
        watch.Stop();
        if (payload is null) return 1;

        var outputDirectory = Text(payload.Value, "outputDirectory") ?? Path.GetFullPath(targetDirectory);
        Console.WriteLine($"[spine.export] {Text(payload.Value, "info")}");
        Console.WriteLine($"  覆盖策略：{Text(payload.Value, "overwrite")} · 耗时 {watch.Elapsed.TotalSeconds:0.00}s");
        Console.WriteLine($"  输出目录：{outputDirectory}");

        // ── 回到磁盘逐个文件核对（响应说的和磁盘上的必须是同一件事）──
        var verified = new List<object>();
        var mismatches = new List<string>();
        long totalBytes = 0;

        foreach (var item in (payload.Value.TryGetProperty("items", out var items) ? items : default).EnumerateArray())
        {
            var refKey = item.GetProperty("refKey").GetString()!;
            var ok = item.GetProperty("ok").GetBoolean();
            var name = item.GetProperty("name").GetString()!;
            var reason = Text(item, "reason");

            if (!ok)
            {
                Console.WriteLine($"  ✗ {name} —— {reason}");
                verified.Add(new { refKey, name, ok, reason, files = Array.Empty<object>() });
                continue;
            }

            var files = new List<object>();
            var itemBytes = 0L;
            Console.WriteLine($"  ✓ {name}");
            foreach (var file in item.GetProperty("files").EnumerateArray())
            {
                var relative = file.GetProperty("path").GetString()!;
                var role = file.GetProperty("role").GetString()!;
                var claimed = file.GetProperty("bytes").GetInt64();
                var onDisk = Path.Combine(outputDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

                var header = Header(onDisk, 8);
                var actual = File.Exists(onDisk) ? new FileInfo(onDisk).Length : -1;
                var headerOk = HeaderMatches(role, header);
                if (actual != claimed)
                    mismatches.Add($"{relative}：响应说 {claimed} 字节，磁盘上 {actual} 字节");
                if (!headerOk)
                    mismatches.Add($"{relative}：头字节不对（{Describe(header)}）");

                totalBytes += actual;
                itemBytes += actual;
                Console.WriteLine($"      {actual,10:N0}  {relative}  [{role}] 头={Describe(header)}{(headerOk ? "" : "  ← 不对")}");
                files.Add(new
                {
                    path = relative,
                    role,
                    claimedBytes = claimed,
                    onDiskBytes = actual,
                    header = Describe(header),
                    headerOk,
                });
            }
            verified.Add(new { refKey, name, ok, files, bytes = itemBytes });
        }

        // 被「不覆盖」跳过的文件也要看得见。
        var skippedFiles = new List<string>();
        foreach (var item in (payload.Value.TryGetProperty("items", out var items2) ? items2 : default).EnumerateArray())
        {
            if (!item.TryGetProperty("skippedFiles", out var skippedFilesField)) continue;
            foreach (var s in skippedFilesField.EnumerateArray())
            {
                var value = s.GetString()!;
                skippedFiles.Add(value);
                Console.WriteLine($"  · 已存在，跳过：{value}");
            }
        }

        var written = payload.Value.GetProperty("written").EnumerateArray().Select(x => x.GetString()!).ToList();
        var skipped = payload.Value.GetProperty("skipped").EnumerateArray().Select(x => x.GetString()!).ToList();
        Console.WriteLine($"\n=== 结果 ===\n成功 {written.Count} / 失败 {skipped.Count} 条 · 落盘合计 {totalBytes:N0} 字节");

        if (mismatches.Count > 0)
        {
            Console.Error.WriteLine("\n=== 核对失败（响应与磁盘不一致）===");
            foreach (var mismatch in mismatches) Console.Error.WriteLine("  ! " + mismatch);
        }
        else
        {
            Console.WriteLine("=== 核对通过：每个文件的字节数与头字节都与响应一致 ===");
        }

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var full = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, JsonSerializer.Serialize(new
            {
                elapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2),
                outputDirectory,
                response = JsonSerializer.Deserialize<JsonElement>(payload.Value.GetRawText()),
                verified,
                skippedFiles,
                totalBytes,
                mismatches,
            }, JsonOptions), new System.Text.UTF8Encoding(false));
            Console.WriteLine($"IPC payload 与核对明细已导出 → {full}");
        }

        return mismatches.Count == 0 ? 0 : 1;
    }

    /// <summary>读文件头 n 个字节（文件不存在时返回空）。</summary>
    private static byte[] Header(string path, int count)
    {
        try
        {
            if (!File.Exists(path)) return [];
            using var stream = File.OpenRead(path);
            var buffer = new byte[count];
            var read = stream.Read(buffer, 0, count);
            return read == count ? buffer : buffer[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "读文件头失败：{0}", path);
            return [];
        }
    }

    /// <summary>
    /// 头字节核对：骨架必须是 <c>{</c>（Spine JSON）或二进制 <c>.skel</c> 的魔数；
    /// 图集必须是文本（可打印 ASCII）；纹理必须是 PNG 魔数。
    /// </summary>
    private static bool HeaderMatches(string role, byte[] header)
    {
        if (header.Length == 0) return false;
        return role switch
        {
            "skeleton" => header[0] == (byte)'{'
                          || (header.Length >= 4 && header[0] == 0x00 && header[1] == 0x00
                              && header[2] == 0x00 && header[3] != 0x00),
            "atlas" => header[0] is > 0x1F and < 0x7F,
            "texture" => header.Length >= PngMagic.Length && header.Take(PngMagic.Length).SequenceEqual(PngMagic),
            _ => true,
        };
    }

    private static string Describe(byte[] header)
        => header.Length == 0 ? "(读不到)"
            : string.Join(' ', header.Take(4).Select(b => b.ToString("X2")));

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
            Console.Error.WriteLine($"[spine-export-ipc] {method} ✗ {message}");
            Log.Error("{0} 失败：{1}", method, message);
            return null;
        }
        Log.Info("{0} 成功", method);
        return root.GetProperty("payload").Clone();
    }
}
