using System.Diagnostics;
using System.Text.RegularExpressions;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Lunartique;
using LimbusModEditor.Formats.Rebank;
using LimbusModEditor.Formats.Bank;
using NLog;

namespace LimbusModEditor.Application.Build;

public sealed record ModExportResult(
    ModFormatKind Format,
    string OutputPath,
    int AppliedReplacements,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<ExportAssetStatus> AssetStatuses)
{
    public ModExportResult(ModFormatKind format, string outputPath, int appliedReplacements, IReadOnlyList<string> diagnostics)
        : this(format, outputPath, appliedReplacements, diagnostics, []) { }
}

/// <summary>One per-source outcome of a multi-format (导出全部) run.</summary>
public sealed record MultiFormatExportItem(
    string SourceName,
    ModFormatKind Format,
    bool Succeeded,
    string? OutputPath,
    string? Error,
    ModExportResult? Result);

/// <summary>Aggregated outcome of exporting every registered source of a
/// multi-format project (a standard Limbus project mixes Unity-bundle edits
/// (Carra2) and audio edits (Bank/Rebank) and exports each to its own format).
/// </summary>
public sealed record MultiFormatExportResult(IReadOnlyList<MultiFormatExportItem> Items)
{
    public int SucceededCount => Items.Count(x => x.Succeeded);
    public int FailedCount => Items.Count(x => !x.Succeeded);
}

/// <summary>Builds a portable package from project sources and replacements.</summary>
public sealed class ModExportService(FormatRegistry registry)
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Canonical output extension per format (mirrors the export
    /// wizard's mapping).</summary>
    public static string ExtensionFor(ModFormatKind kind) => kind switch
    {
        ModFormatKind.Carra => ".carra",
        ModFormatKind.Carra2 => ".carra2",
        ModFormatKind.Rebank => ".rebank",
        ModFormatKind.Bank => ".bank",
        ModFormatKind.Lunartique => ".zip",
        _ => ".bin"
    };

    /// <summary>导出全部（多格式项目）：a standard project may hold Unity-bundle
    /// edits and bank edits at once. Each registered file source is exported to
    /// its own registered format; failures are collected per source instead of
    /// aborting the whole run. Directory sources are skipped (they need an
    /// interactive target-format choice).</summary>
    public async Task<MultiFormatExportResult> ExportAllAsync(ModProject project, string outputDirectory,
        object? codec = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        using var scope = Log.Scope("导出全部（多格式）");
        Log.Info("导出全部开始：输出目录 {0}，来源 {1} 个", outputDirectory, project.Sources.Count);
        Directory.CreateDirectory(outputDirectory);
        var items = new List<MultiFormatExportItem>();
        var sourceIndex = 0;
        foreach (var source in project.Sources)
        {
            sourceIndex++;
            cancellationToken.ThrowIfCancellationRequested();
            var name = string.IsNullOrWhiteSpace(source.DisplayName) ? Path.GetFileNameWithoutExtension(source.Path)! : source.DisplayName;
            if (Log.IsDebugEnabled)
                Log.Debug("导出全部：正在处理第 {0}/{1} 个来源 {2}（格式 {3}，路径 {4}）",
                    sourceIndex, project.Sources.Count, name, source.Format, source.Path ?? "-");
            if (source.Format is ModFormatKind.Unknown or ModFormatKind.Directory)
            {
                Log.Warn("导出全部跳过来源：{0} 的格式 {1} 无法自动选择目标格式（目录来源需在向导中手动选择），源文件 {2}",
                    name, source.Format, source.Path ?? "-");
                items.Add(new MultiFormatExportItem(name, source.Format, false, null, "该来源没有可自动选择的导出格式（目录来源请在导出向导中手动选择目标格式）。", null));
                continue;
            }
            if (string.IsNullOrWhiteSpace(source.Path) || !File.Exists(source.Path))
            {
                Log.Warn("导出全部跳过来源：{0} 的源文件不存在：{1}", name, source.Path ?? "-");
                items.Add(new MultiFormatExportItem(name, source.Format, false, null, "源文件不存在。", null));
                continue;
            }
            var output = Path.Combine(outputDirectory, SanitizeFileName(name) + ExtensionFor(source.Format));
            if (Log.IsDebugEnabled)
                Log.Debug("导出全部：来源 {0} → 目标 {1}（格式 {2}）", name, output, source.Format);
            try
            {
                var result = await ExportWithEditsAsync(source.Path, project, output, source.Format, codec, cancellationToken);
                items.Add(new MultiFormatExportItem(name, source.Format, true, result.OutputPath, null, result));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "导出全部：来源 {0} 导出失败（格式 {1}，目标 {2}）", name, source.Format, output);
                items.Add(new MultiFormatExportItem(name, source.Format, false, null, ex.Message, null));
            }
            catch (OperationCanceledException)
            {
                Log.Info("已取消：导出全部（来源 {0}，目标 {1}）", name, output);
                throw;
            }
        }
        Log.Info("导出全部完成：成功 {0} 个，失败 {1} 个 → {2}",
            items.Count(x => x.Succeeded), items.Count(x => !x.Succeeded), outputDirectory);
        return new MultiFormatExportResult(items);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "export" : cleaned;
    }

    public async Task<ModExportResult> ExportWithEditsAsync(string? sourcePackagePath, ModProject project, string outputPath, ModFormatKind? targetFormat = null, object? codec = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        using var scope = Log.Scope("导出并应用替换");
        var exportStopwatch = Stopwatch.StartNew();
        Log.Info("导出开始：来源 {0}，目标 {1}，目标格式 {2}，资源 {3} 个",
            sourcePackagePath ?? "-", outputPath, targetFormat?.ToString() ?? "-", project.Assets.Count);
        sourcePackagePath = ResolveSourcePath(sourcePackagePath, project, targetFormat);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePackagePath);
        var sourceFullPath = Path.GetFullPath(sourcePackagePath);
        var requestedOutput = Path.GetFullPath(outputPath);
        if (File.Exists(sourceFullPath) && string.Equals(sourceFullPath, requestedOutput, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("输出路径不能覆盖项目源文件，请选择新的输出文件。");
        ModPackage package;
        IModFormatHandler handler;
        ModFormatKind sourceFormat;
        ModFormatKind format;
        if (Directory.Exists(sourceFullPath))
        {
            sourceFormat = ModFormatKind.Directory;
            format = targetFormat ?? InferTargetFormat(outputPath, ModFormatKind.Carra2);
            Log.Debug("导出槽位开始：目录来源 {0} → 格式 {1}", sourceFullPath, format);
            package = CreatePackageFromDirectory(project, format);
            handler = FindHandler(format);
        }
        else
        {
            if (!File.Exists(sourceFullPath)) throw new FileNotFoundException("源模组不存在。", sourceFullPath);
            await using var input = File.OpenRead(sourceFullPath);
            var probeStopwatch = Stopwatch.StartNew();
            var probe = await registry.ProbeAsync(input, Path.GetFileName(sourceFullPath), cancellationToken) ?? throw new InvalidDataException("无法识别源模组格式。");
            sourceFormat = probe.Format;
            format = targetFormat ?? InferTargetFormat(outputPath, sourceFormat);
            var isLunartiqueToCarra = sourceFormat == ModFormatKind.Lunartique && IsCarraFamily(format, format);
            var probeElapsedMs = probeStopwatch.ElapsedMilliseconds;
            Log.Debug("导出槽位开始：源 {0}（{1} 字节），识别格式 {2} → 目标格式 {3}，耗时 {4} ms",
                sourceFullPath, input.Length, sourceFormat, format, probeElapsedMs);
            if (isLunartiqueToCarra)
                Log.Info("导出路径：Lunartique → Carra 对象级转换：{0} → {1}", sourceFullPath, format);
            if (isLunartiqueToCarra) sourceFormat = format;
            if (format != sourceFormat && !IsCarraFamily(format, sourceFormat)) throw new NotSupportedException($"当前尚未实现 {sourceFormat} 到 {format} 的跨格式导出。");
            input.Position = 0;
            var directHandler = registry.Handlers.FirstOrDefault(x => x.Descriptor.Kind == format);
            var fallbackHandler = IsCarraFamily(format, sourceFormat)
                ? registry.Handlers.FirstOrDefault(x => x.Descriptor.Kind is ModFormatKind.Carra or ModFormatKind.Carra2)
                : null;
            handler = directHandler ?? fallbackHandler ?? throw new InvalidDataException($"未注册输出格式处理器：{format}");
            if (directHandler is null)
                Log.Warn("回退：未找到 {0} 的专属处理器，改用 Carra 系处理器（{1}）", format, handler.Descriptor.Kind);
            if (isLunartiqueToCarra)
                handler = registry.Handlers.First(x => x.Descriptor.Kind == ModFormatKind.Lunartique);
            var importStopwatch = Stopwatch.StartNew();
            package = await handler.ImportAsync(input, new(Path.GetDirectoryName(sourceFullPath), true, cancellationToken));
            Log.Debug("导入源包完成：格式 {0}，耗时 {1} ms", sourceFormat, importStopwatch.ElapsedMilliseconds);
            if (isLunartiqueToCarra)
            {
                var (replacementCount, statuses) = await ApplyReplacementsAsync(package, project, codec as IFmodAudioCodec, cancellationToken);
                var sourcePackage = (LunartiquePackage)package.Payload!;
                var converted = await new LunartiqueCarraConversionService().ConvertAsync(sourcePackage, cancellationToken);
                if (converted.Package.Entries.Count == 0)
                    throw new InvalidDataException("Lunartique 中没有可转换的 Carra 对象；请确认 Installation 资源是有效的 Unity SerializedFile。");
                converted.Package.UnknownFiles.AddRange(sourcePackage.PreservedFiles);
                foreach (var preserved in sourcePackage.PreservedFiles)
                    statuses.Add(new ExportAssetStatus(preserved.Path, ExportAssetStatus.Preserved, "无法转换为对象级 Carra 的未知文件，按原样保留。"));
                package = new ModPackage { SourceFormat = format, Payload = converted.Package };
                handler = FindHandler(format);
                var report = await handler.ValidateAsync(package, cancellationToken);
                if (!report.IsValid) throw new InvalidDataException(string.Join("; ", report.Diagnostics.Select(x => x.Message)));
                var convertedOutput = Path.GetFullPath(outputPath); Directory.CreateDirectory(Path.GetDirectoryName(convertedOutput)!);
                Log.Debug("Lunartique→Carra 转换完成：对象 {0} 个（新增 {1}，修改 {2}），保留未知文件 {3} 个，校验通过 → {4}",
                    converted.Package.Entries.Count, converted.AddedObjects, converted.ModifiedObjects,
                    sourcePackage.PreservedFiles.Count, convertedOutput);
                await AtomicOutput.WriteAsync(convertedOutput, async (stream, token) =>
                {
                    await using var output = stream;
                    await handler.ExportAsync(package, output, new(format, true, true, token, new JovelerXzCodec()));
                }, cancellationToken);
                var diagnostics = converted.Diagnostics.Select(x => $"{x.RelativePath}: {x.Message}").ToArray();
                Log.Info("导出完成（Lunartique→Carra）：{0}，替换 {1} 个，产物 {2}，总耗时 {3} ms",
                    format, replacementCount + converted.AddedObjects + converted.ModifiedObjects, convertedOutput, exportStopwatch.ElapsedMilliseconds);
                return new(format, convertedOutput, replacementCount + converted.AddedObjects + converted.ModifiedObjects, diagnostics, statuses);
            }
        }
        var (applied, assetStatuses) = await ApplyReplacementsAsync(package, project, codec as IFmodAudioCodec, cancellationToken);
        if (Log.IsDebugEnabled)
            Log.Debug("替换应用完成：已应用 {0} 个，状态条目 {1} 个（跳过 {2} 个）",
                applied, assetStatuses.Count, assetStatuses.Count(x => x.Status == ExportAssetStatus.Skipped));
        var validation = await handler.ValidateAsync(package, cancellationToken);
        if (validation.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error)) throw new InvalidDataException(string.Join("; ", validation.Diagnostics.Select(x => x.Message)));
        if (Log.IsDebugEnabled)
            Log.Debug("导出校验通过：格式 {0}，诊断 {1} 条（警告 {2} 条）",
                format, validation.Diagnostics.Count, validation.Diagnostics.Count(x => x.Severity != DiagnosticSeverity.Error));
        // 真实加载器（LCTA launcher）按缓存外层键匹配 Carra 对象；游戏更新会
        // 更换缓存外层键，旧键的模组会被静默跳过。导出时若配置了缓存目录，
        // 逐外层键核对缓存中是否仍有对应 bundle，把失配写进导出诊断。
        var alignment = format is ModFormatKind.Carra or ModFormatKind.Carra2 && package.Payload is CarraPackage carra
            ? CheckCarraCacheAlignment(carra, project.UnityCacheDirectory).ToArray()
            : [];
        if (alignment.Length > 0)
            Log.Warn("缓存对齐检查发现 {0} 处失配（相关对象在真实加载器中会被静默跳过）：{1}",
                alignment.Length, string.Join(" | ", alignment));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var effectiveCodec = codec;
        if (effectiveCodec is null && format is ModFormatKind.Carra or ModFormatKind.Carra2) effectiveCodec = new JovelerXzCodec();
        if (codec is null && format is ModFormatKind.Carra or ModFormatKind.Carra2)
            Log.Debug("未提供 codec，Carra 系导出回退为内置 JovelerXzCodec（格式 {0}）", format);
        var outputFullPath = Path.GetFullPath(outputPath);
        var outputBytes = -1L;
        if (Log.IsDebugEnabled)
            Log.Debug("写出产物开始：{0}（格式 {1}，包负载类型 {2}）", outputFullPath, format, package.Payload?.GetType().Name ?? "-");
        await AtomicOutput.WriteAsync(outputFullPath, async (stream, token) =>
        {
            await using var output = stream;
            await handler.ExportAsync(package, output, new(format, true, true, token, effectiveCodec));
            outputBytes = output.Length;
        }, cancellationToken);
        var finalDiagnostics = validation.Diagnostics.Select(x => x.Message)
            .Concat(alignment.Select(x => $"缓存对齐：{x}"))
            .ToArray();
        Log.Debug("写出产物完成：{0}，{1} 字节，打包耗时 {2} ms", outputFullPath, outputBytes, exportStopwatch.ElapsedMilliseconds);
        Log.Info("导出完成：格式 {0}，已应用替换 {1} 个，产物 {2}，总耗时 {3} ms",
            format, applied, outputFullPath, exportStopwatch.ElapsedMilliseconds);
        return new(format, outputFullPath, applied, finalDiagnostics, assetStatuses);
    }

    /// <summary>真实加载器按 Carra2 键的第一段（Unity 缓存外层键）在
    /// &lt;缓存根&gt;/&lt;外层&gt;/&lt;内层&gt;/__data 定位目标 bundle。本检查
    /// 对每个外层键核对缓存中仍存在对应 bundle；缺失即游戏更新后该键已更换，
    /// 加载器会静默跳过这些对象 —— 作为导出诊断给出，而不是静默成功。</summary>
    private static IEnumerable<string> CheckCarraCacheAlignment(CarraPackage package, string? cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory))
        {
            Log.Warn("缓存对齐检查跳过：未配置或找不到 Unity 缓存目录（{0}），无法核对加载器是否还能匹配这些对象", cacheDirectory ?? "-");
            yield break;
        }
        Log.Debug("缓存对齐检查开始：缓存目录 {0}，Carra 对象 {1} 个", cacheDirectory, package.Entries.Count);
        foreach (var group in package.Entries.GroupBy(x => x.Key.Account, StringComparer.Ordinal))
        {
            var outerDir = Path.Combine(cacheDirectory, group.Key);
            if (!Directory.Exists(outerDir))
            {
                Log.Warn("缓存对齐失配：外层键 {0} 不在缓存目录 {1} 中（游戏可能已更新），该组 {2} 个对象无法被加载器匹配",
                    group.Key, cacheDirectory, group.Count());
                yield return $"外层键 {group.Key} 不在配置的缓存目录中（游戏可能已更新），这些对象将无法被加载器匹配";
                continue;
            }
            foreach (var inner in group.Select(x => x.Key.Bundle).Distinct(StringComparer.Ordinal).Take(4))
            {
                if (!File.Exists(Path.Combine(outerDir, inner, "__data")))
                {
                    if (Log.IsDebugEnabled)
                        Log.Debug("缓存对齐失配：bundle {0} 在缓存 {1} 下缺少 __data（游戏可能已更新）", inner, outerDir);
                    yield return $"bundle {inner} 不在缓存 {group.Key} 下（游戏可能已更新），相关对象将无法被匹配";
                }
            }
        }
    }

    private IModFormatHandler FindHandler(ModFormatKind format)
    {
        var direct = registry.Handlers.FirstOrDefault(x => x.Descriptor.Kind == format);
        if (direct is not null) return direct;
        if (format is ModFormatKind.Carra or ModFormatKind.Carra2)
        {
            var fallback = registry.Handlers.FirstOrDefault(x => x.Descriptor.Kind is ModFormatKind.Carra or ModFormatKind.Carra2);
            if (fallback is not null)
            {
                Log.Warn("回退：未注册 {0} 处理器，改用 Carra 系处理器（{1}）", format, fallback.Descriptor.Kind);
                return fallback;
            }
        }
        Log.Error("未注册输出格式处理器：{0}（已注册 {1} 个处理器）", format, registry.Handlers.Count);
        throw new InvalidDataException($"未注册输出格式处理器：{format}");
    }

    private static ModPackage CreatePackageFromDirectory(ModProject project, ModFormatKind format)
    {
        var assets = project.Assets.Where(x => x.SourcePath is not null && File.Exists(x.SourcePath)).ToArray();
        Log.Debug("从目录构建包：项目 {0}，目标格式 {1}，可用资源 {2}/{3} 个",
            project.Name, format, assets.Length, project.Assets.Count);
        return format switch
        {
            ModFormatKind.Carra or ModFormatKind.Carra2 => CreateCarraPackage(assets, format),
            ModFormatKind.Rebank => CreateRebankPackage(project, assets),
            ModFormatKind.Lunartique => CreateLunartiquePackage(assets),
            _ => throw new NotSupportedException($"不支持从目录创建 {format} 包。")
        };
    }

    private static ModPackage CreateCarraPackage(IReadOnlyList<AssetRecord> assets, ModFormatKind format)
    {
        var package = new CarraPackage();
        var pattern = new Regex("^(?<account>[^/\\\\]+)/(?<bundle>[^/\\\\]+)/(?<id>-?\\d+)(?:\\.(?<type>\\d+))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        foreach (var asset in assets)
        {
            if (IsStructuredUnityAsset(asset)) continue;
            var match = pattern.Match(asset.LogicalPath.Replace('\\', '/'));
            if (!match.Success)
            {
                if (Log.IsTraceEnabled) Log.Trace("跳过不符合 account/bundle/path_id.type_id 的资源：{0}", asset.LogicalPath);
                continue;
            }
            var key = new CarraObjectKey(match.Groups["account"].Value, match.Groups["bundle"].Value, long.Parse(match.Groups["id"].Value), match.Groups["type"].Success ? int.Parse(match.Groups["type"].Value) : null);
            package.Entries.Add(CarraEntry.CreateNew(key, File.ReadAllBytes(CurrentAssetPath(asset))));
        }
        Log.Debug("Carra 包创建完成：输入资源 {0} 个 → 条目 {1} 个（格式 {2}）", assets.Count, package.Entries.Count, format);
        if (package.Entries.Count == 0) throw new InvalidDataException("目录中没有符合 account/bundle/path_id.type_id 的 Carra 对象。");
        return new ModPackage { SourceFormat = format, Payload = package };
    }

    private static ModPackage CreateRebankPackage(ModProject project, IReadOnlyList<AssetRecord> assets)
    {
        var files = new List<(int Index, string Name, byte[] Data, string Status)>();
        foreach (var asset in assets)
        {
            if (IsStructuredUnityAsset(asset)) continue;
            var parts = asset.LogicalPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out var index))
            {
                if (Log.IsTraceEnabled) Log.Trace("跳过不符合 <索引>/<文件名> 的资源：{0}", asset.LogicalPath);
                continue;
            }
            files.Add((index, parts[^1], File.ReadAllBytes(CurrentAssetPath(asset)), "added"));
        }
        Log.Debug("Rebank 差分创建完成：输入资源 {0} 个 → 文件 {1} 个（base_bank {2}.bank）", assets.Count, files.Count, project.Name);
        return new ModPackage { SourceFormat = ModFormatKind.Rebank, Payload = RebankDiffService.Create(project.Name + ".bank", project.Name, project.Version, project.Author, project.Description, files) };
    }

    private static ModPackage CreateLunartiquePackage(IReadOnlyList<AssetRecord> assets)
    {
        var package = new LunartiquePackage { Root = "LimbusModEditorMod" };
        foreach (var asset in assets)
        {
            if (IsStructuredUnityAsset(asset)) continue;
            var data = File.ReadAllBytes(CurrentAssetPath(asset)); package.Resources.Add(new LunartiqueResource(asset.LogicalPath, [], data));
        }
        Log.Debug("Lunartique 包创建完成：输入资源 {0} 个 → 资源条目 {1} 个", assets.Count, package.Resources.Count);
        return new ModPackage { SourceFormat = ModFormatKind.Lunartique, Payload = package };
    }

    private static async Task<(int Applied, List<ExportAssetStatus> Statuses)> ApplyReplacementsAsync(
        ModPackage package, ModProject project, IFmodAudioCodec? audioCodec, CancellationToken cancellationToken)
    {
        var count = 0;
        var statuses = new List<ExportAssetStatus>();
        var scanned = 0;
        var noReplacement = 0;
        Log.Debug("替换应用开始：资源 {0} 个", project.Assets.Count);
        foreach (var asset in project.Assets)
        {
            scanned++;
            cancellationToken.ThrowIfCancellationRequested();
            Log.Every(scanned, 1000, LogLevel.Debug, () => $"替换应用进行中：已检查 {scanned} 个资源，已应用 {count} 个");
            if (!asset.Metadata.TryGetValue("replacementPath", out var path) || !File.Exists(path))
            {
                noReplacement++;
                continue;
            }
            var data = File.ReadAllBytes(path);
            if (Log.IsTraceEnabled) Log.Trace("应用替换：{0}（{1} 字节），源文件 {2}", asset.LogicalPath, data.Length, path);
            switch (package.Payload)
            {
                case CarraPackage carra when carra.Find(asset.LogicalPath) is { } entry:
                    entry.ReplaceData(data); count++;
                    statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Applied, null));
                    break;
                case CarraPackage:
                    Log.Warn("替换被跳过：{0} 在包内没有匹配的 Carra 对象（替换文件 {1}）", asset.LogicalPath, path);
                    statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped, "包内没有匹配的 Carra 对象。"));
                    break;
                case LunartiquePackage lunartique:
                    var resource = lunartique.Resources.FirstOrDefault(x => string.Equals(x.RelativePath, asset.LogicalPath, StringComparison.OrdinalIgnoreCase));
                    if (resource is not null)
                    {
                        lunartique.Resources[lunartique.Resources.IndexOf(resource)] = resource with { Installation = data };
                        count++;
                        statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Applied, null));
                    }
                    else
                    {
                        Log.Warn("替换被跳过：{0} 在包内没有匹配的 Lunartique 资源（替换文件 {1}）", asset.LogicalPath, path);
                        statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped, "包内没有匹配的 Lunartique 资源。"));
                    }
                    break;
                case RebankPackage rebank:
                    var file = rebank.Files.FirstOrDefault(x => string.Equals($"{x.Index}/{x.Name}", asset.LogicalPath, StringComparison.OrdinalIgnoreCase));
                    if (file is not null)
                    {
                        rebank.Files[rebank.Files.IndexOf(file)] = file with { Data = data };
                        count++;
                        statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Applied, null));
                    }
                    else
                    {
                        Log.Warn("替换被跳过：{0} 在包内没有匹配的 Rebank 文件（替换文件 {1}）", asset.LogicalPath, path);
                        statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped, "包内没有匹配的 Rebank 文件。"));
                    }
                    break;
                case LimbusModEditor.Formats.Bank.BankPackage bank:
                    var bankIndex = ParseFsbIndex(asset.LogicalPath);
                    if (bankIndex < 0 || bankIndex >= bank.FsbData.Count)
                    {
                        Log.Warn("替换被跳过：{0} 的 FSB 索引 {1} 超出 Bank 范围（0..{2}）", asset.LogicalPath, bankIndex, bank.FsbData.Count - 1);
                        statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped, "FSB 索引超出 Bank 范围。"));
                        continue;
                    }
                    if (data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                    {
                        if (audioCodec is null || !audioCodec.IsAvailable)
                            throw new NotSupportedException("WAV 替换需要配置兼容的 FMOD/FSBANK 编码器。");
                        Log.Debug("WAV 替换需要编码：{0}（{1} 字节，FSB 索引 {2}）", asset.LogicalPath, data.Length, bankIndex);
                        var encodeStopwatch = Stopwatch.StartNew();
                        data = await audioCodec.EncodeWaveToFsbAsync(data, cancellationToken);
                        Log.Debug("WAV→FSB 编码完成：{0}，{1} → {2} 字节，耗时 {3} ms",
                            asset.LogicalPath, path, data.Length, encodeStopwatch.ElapsedMilliseconds);
                    }
                    if (data.Length < 4 || !data.AsSpan(0, 4).SequenceEqual("FSB5"u8))
                        throw new NotSupportedException("Bank 只能直接替换为完整的 FSB5 数据；WAV 需要 FMOD/FSBANK 编码器。");
                    bank.FsbData[bankIndex] = data;
                    bank.HasModifications = true;
                    count++;
                    statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Applied, null));
                    break;
                default:
                    Log.Warn("替换被跳过：{0} 的包格式 {1} 不支持替换（替换文件 {2}）", asset.LogicalPath, package.SourceFormat, path);
                    statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped, "当前包格式不支持该替换。"));
                    break;
            }
        }
        if (Log.IsDebugEnabled)
            Log.Debug("替换应用完成：已应用 {0} 个，跳过 {1} 个，未配置 replacementPath 或缺失 {2} 个（包格式 {3}）",
                count, statuses.Count(x => x.Status == ExportAssetStatus.Skipped), noReplacement, package.SourceFormat);
        return (count, statuses);
    }

    private static string? ResolveSourcePath(string? requested, ModProject project, ModFormatKind? targetFormat)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested;
        return project.Sources.Where(x => targetFormat is null || x.Format == ModFormatKind.Directory || x.Format == targetFormat || (targetFormat is ModFormatKind.Carra or ModFormatKind.Carra2 && x.Format is ModFormatKind.Carra or ModFormatKind.Carra2)).OrderByDescending(x => x.ImportedAt).FirstOrDefault()?.Path;
    }

    private static ModFormatKind InferTargetFormat(string outputPath, ModFormatKind sourceFormat) => Path.GetExtension(outputPath).ToLowerInvariant() switch
    {
        ".carra" => ModFormatKind.Carra,
        ".carra2" => ModFormatKind.Carra2,
        ".rebank" => ModFormatKind.Rebank,
        ".bank" => ModFormatKind.Bank,
        _ => sourceFormat
    };

    private static bool IsCarraFamily(ModFormatKind left, ModFormatKind right) => left is ModFormatKind.Carra or ModFormatKind.Carra2 && right is ModFormatKind.Carra or ModFormatKind.Carra2;

    private static int ParseFsbIndex(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0].Equals("fsb", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[1], out var index) ? index : -1;
    }

    private static bool IsStructuredUnityAsset(AssetRecord asset)
        => asset.Metadata.TryGetValue("unityBundle", out var bundle) && bundle == "true" ||
           asset.Metadata.TryGetValue("unitySerializedFile", out var serialized) && serialized == "true";

    private static string CurrentAssetPath(AssetRecord asset)
    {
        if (asset.Metadata.TryGetValue("replacementPath", out var replacement) && File.Exists(replacement))
            return replacement;
        if (!string.IsNullOrWhiteSpace(asset.SourcePath) && File.Exists(asset.SourcePath)) return asset.SourcePath;
        throw new FileNotFoundException($"资源没有可用的源文件: {asset.LogicalPath}");
    }
}
