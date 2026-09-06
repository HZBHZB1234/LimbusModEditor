using System.Text.RegularExpressions;
using LimbusModEditor.Application.Formats;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Lunartique;
using LimbusModEditor.Formats.Rebank;
using LimbusModEditor.Formats.Bank;

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
        Directory.CreateDirectory(outputDirectory);
        var items = new List<MultiFormatExportItem>();
        foreach (var source in project.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = string.IsNullOrWhiteSpace(source.DisplayName) ? Path.GetFileNameWithoutExtension(source.Path) : source.DisplayName;
            if (source.Format is ModFormatKind.Unknown or ModFormatKind.Directory)
            {
                items.Add(new MultiFormatExportItem(name, source.Format, false, null, "该来源没有可自动选择的导出格式（目录来源请在导出向导中手动选择目标格式）。", null));
                continue;
            }
            if (string.IsNullOrWhiteSpace(source.Path) || !File.Exists(source.Path))
            {
                items.Add(new MultiFormatExportItem(name, source.Format, false, null, "源文件不存在。", null));
                continue;
            }
            var output = Path.Combine(outputDirectory, SanitizeFileName(name) + ExtensionFor(source.Format));
            try
            {
                var result = await ExportWithEditsAsync(source.Path, project, output, source.Format, codec, cancellationToken);
                items.Add(new MultiFormatExportItem(name, source.Format, true, result.OutputPath, null, result));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                items.Add(new MultiFormatExportItem(name, source.Format, false, null, ex.Message, null));
            }
        }
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
            package = CreatePackageFromDirectory(project, format);
            handler = FindHandler(format);
        }
        else
        {
            if (!File.Exists(sourceFullPath)) throw new FileNotFoundException("源模组不存在。", sourceFullPath);
            await using var input = File.OpenRead(sourceFullPath);
            var probe = await registry.ProbeAsync(input, Path.GetFileName(sourceFullPath), cancellationToken) ?? throw new InvalidDataException("无法识别源模组格式。");
            sourceFormat = probe.Format;
            format = targetFormat ?? InferTargetFormat(outputPath, sourceFormat);
            var isLunartiqueToCarra = sourceFormat == ModFormatKind.Lunartique && IsCarraFamily(format, format);
            if (isLunartiqueToCarra) sourceFormat = format;
            if (format != sourceFormat && !IsCarraFamily(format, sourceFormat)) throw new NotSupportedException($"当前尚未实现 {sourceFormat} 到 {format} 的跨格式导出。");
            input.Position = 0;
            handler = registry.Handlers.FirstOrDefault(x => x.Descriptor.Kind == format)
                ?? (IsCarraFamily(format, sourceFormat)
                    ? registry.Handlers.FirstOrDefault(x => x.Descriptor.Kind is ModFormatKind.Carra or ModFormatKind.Carra2)
                    : null)
                ?? throw new InvalidDataException($"未注册输出格式处理器：{format}");
            if (isLunartiqueToCarra)
                handler = registry.Handlers.First(x => x.Descriptor.Kind == ModFormatKind.Lunartique);
            package = await handler.ImportAsync(input, new(Path.GetDirectoryName(sourceFullPath), true, cancellationToken));
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
                await AtomicOutput.WriteAsync(convertedOutput, async (stream, token) =>
                {
                    await using var output = stream;
                    await handler.ExportAsync(package, output, new(format, true, true, token, new JovelerXzCodec()));
                }, cancellationToken);
                var diagnostics = converted.Diagnostics.Select(x => $"{x.RelativePath}: {x.Message}").ToArray();
                return new(format, convertedOutput, replacementCount + converted.AddedObjects + converted.ModifiedObjects, diagnostics, statuses);
            }
        }
        var (applied, assetStatuses) = await ApplyReplacementsAsync(package, project, codec as IFmodAudioCodec, cancellationToken);
        var validation = await handler.ValidateAsync(package, cancellationToken);
        if (validation.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error)) throw new InvalidDataException(string.Join("; ", validation.Diagnostics.Select(x => x.Message)));
        // 真实加载器（LCTA launcher）按缓存外层键匹配 Carra 对象；游戏更新会
        // 更换缓存外层键，旧键的模组会被静默跳过。导出时若配置了缓存目录，
        // 逐外层键核对缓存中是否仍有对应 bundle，把失配写进导出诊断。
        var alignment = format is ModFormatKind.Carra or ModFormatKind.Carra2 && package.Payload is CarraPackage carra
            ? CheckCarraCacheAlignment(carra, project.UnityCacheDirectory).ToArray()
            : [];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var effectiveCodec = codec;
        if (effectiveCodec is null && format is ModFormatKind.Carra or ModFormatKind.Carra2) effectiveCodec = new JovelerXzCodec();
        var outputFullPath = Path.GetFullPath(outputPath);
        await AtomicOutput.WriteAsync(outputFullPath, async (stream, token) =>
        {
            await using var output = stream;
            await handler.ExportAsync(package, output, new(format, true, true, token, effectiveCodec));
        }, cancellationToken);
        var finalDiagnostics = validation.Diagnostics.Select(x => x.Message)
            .Concat(alignment.Select(x => $"缓存对齐：{x}"))
            .ToArray();
        return new(format, outputFullPath, applied, finalDiagnostics, assetStatuses);
    }

    /// <summary>真实加载器按 Carra2 键的第一段（Unity 缓存外层键）在
    /// &lt;缓存根&gt;/&lt;外层&gt;/&lt;内层&gt;/__data 定位目标 bundle。本检查
    /// 对每个外层键核对缓存中仍存在对应 bundle；缺失即游戏更新后该键已更换，
    /// 加载器会静默跳过这些对象 —— 作为导出诊断给出，而不是静默成功。</summary>
    private static IEnumerable<string> CheckCarraCacheAlignment(CarraPackage package, string? cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory)) yield break;
        foreach (var group in package.Entries.GroupBy(x => x.Key.Account, StringComparer.Ordinal))
        {
            var outerDir = Path.Combine(cacheDirectory, group.Key);
            if (!Directory.Exists(outerDir))
            {
                yield return $"外层键 {group.Key} 不在配置的缓存目录中（游戏可能已更新），这些对象将无法被加载器匹配";
                continue;
            }
            foreach (var inner in group.Select(x => x.Key.Bundle).Distinct(StringComparer.Ordinal).Take(4))
            {
                if (!File.Exists(Path.Combine(outerDir, inner, "__data")))
                    yield return $"bundle {inner} 不在缓存 {group.Key} 下（游戏可能已更新），相关对象将无法被匹配";
            }
        }
    }

    private IModFormatHandler FindHandler(ModFormatKind format)
        => registry.Handlers.FirstOrDefault(x => x.Descriptor.Kind == format)
           ?? (format is ModFormatKind.Carra or ModFormatKind.Carra2
               ? registry.Handlers.FirstOrDefault(x => x.Descriptor.Kind is ModFormatKind.Carra or ModFormatKind.Carra2)
               : null)
           ?? throw new InvalidDataException($"未注册输出格式处理器：{format}");

    private static ModPackage CreatePackageFromDirectory(ModProject project, ModFormatKind format)
    {
        var assets = project.Assets.Where(x => x.SourcePath is not null && File.Exists(x.SourcePath)).ToArray();
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
            if (!match.Success) continue;
            var key = new CarraObjectKey(match.Groups["account"].Value, match.Groups["bundle"].Value, long.Parse(match.Groups["id"].Value), match.Groups["type"].Success ? int.Parse(match.Groups["type"].Value) : null);
            package.Entries.Add(CarraEntry.CreateNew(key, File.ReadAllBytes(CurrentAssetPath(asset))));
        }
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
            if (parts.Length < 2 || !int.TryParse(parts[0], out var index)) continue;
            files.Add((index, parts[^1], File.ReadAllBytes(CurrentAssetPath(asset)), "added"));
        }
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
        return new ModPackage { SourceFormat = ModFormatKind.Lunartique, Payload = package };
    }

    private static async Task<(int Applied, List<ExportAssetStatus> Statuses)> ApplyReplacementsAsync(
        ModPackage package, ModProject project, IFmodAudioCodec? audioCodec, CancellationToken cancellationToken)
    {
        var count = 0;
        var statuses = new List<ExportAssetStatus>();
        foreach (var asset in project.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!asset.Metadata.TryGetValue("replacementPath", out var path) || !File.Exists(path)) continue;
            var data = File.ReadAllBytes(path);
            switch (package.Payload)
            {
                case CarraPackage carra when carra.Find(asset.LogicalPath) is { } entry:
                    entry.ReplaceData(data); count++;
                    statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Applied, null));
                    break;
                case CarraPackage:
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
                    else statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped, "包内没有匹配的 Lunartique 资源。"));
                    break;
                case RebankPackage rebank:
                    var file = rebank.Files.FirstOrDefault(x => string.Equals($"{x.Index}/{x.Name}", asset.LogicalPath, StringComparison.OrdinalIgnoreCase));
                    if (file is not null)
                    {
                        rebank.Files[rebank.Files.IndexOf(file)] = file with { Data = data };
                        count++;
                        statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Applied, null));
                    }
                    else statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped, "包内没有匹配的 Rebank 文件。"));
                    break;
                case LimbusModEditor.Formats.Bank.BankPackage bank:
                    var bankIndex = ParseFsbIndex(asset.LogicalPath);
                    if (bankIndex < 0 || bankIndex >= bank.FsbData.Count)
                    {
                        statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped, "FSB 索引超出 Bank 范围。"));
                        continue;
                    }
                    if (data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                    {
                        if (audioCodec is null || !audioCodec.IsAvailable)
                            throw new NotSupportedException("WAV 替换需要配置兼容的 FMOD/FSBANK 编码器。");
                        data = await audioCodec.EncodeWaveToFsbAsync(data, cancellationToken);
                    }
                    if (data.Length < 4 || !data.AsSpan(0, 4).SequenceEqual("FSB5"u8))
                        throw new NotSupportedException("Bank 只能直接替换为完整的 FSB5 数据；WAV 需要 FMOD/FSBANK 编码器。");
                    bank.FsbData[bankIndex] = data;
                    bank.HasModifications = true;
                    count++;
                    statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Applied, null));
                    break;
                default:
                    statuses.Add(new ExportAssetStatus(asset.LogicalPath, ExportAssetStatus.Skipped, "当前包格式不支持该替换。"));
                    break;
            }
        }
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
