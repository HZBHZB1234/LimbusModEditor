using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Formats.Abstractions;

namespace LimbusModEditor.Formats.Rebank;

public sealed record RebankFile(int Index, string Name, byte[] Data, string Status = "modified");
public sealed class RebankPackage
{
    public Dictionary<string, JsonElement> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<RebankFile> Files { get; } = [];
    public List<(string Path, byte[] Data)> PreservedFiles { get; } = [];
}

public static class RebankDiffService
{
    public static RebankPackage CreateFromBank(
        string baseBank,
        string name,
        string version,
        string author,
        string description,
        LimbusModEditor.Formats.Bank.BankPackage original,
        LimbusModEditor.Formats.Bank.BankPackage modified)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        var files = new List<(int Index, string Name, byte[] Data, string Status)>();
        var count = Math.Min(original.FsbData.Count, modified.FsbData.Count);
        for (var i = 0; i < modified.FsbData.Count; i++)
        {
            var data = modified.FsbData[i];
            var isAdded = i >= original.FsbData.Count;
            var changed = isAdded || !SHA256.HashData(original.FsbData[i]).AsSpan().SequenceEqual(SHA256.HashData(data));
            if (changed) files.Add((i, $"{i}.fsb", data, isAdded ? "added" : "modified"));
        }
        return Create(baseBank, name, version, author, description, files);
    }

    public static RebankPackage Create(string baseBank, string name, string version, string author, string description, IEnumerable<(int Index, string Name, byte[] Data, string Status)> files)
    {
        var result = new RebankPackage();
        using var document = JsonDocument.Parse($"{{\"format\":\"rebank\",\"name\":{JsonSerializer.Serialize(name)},\"version\":{JsonSerializer.Serialize(version)},\"author\":{JsonSerializer.Serialize(author)},\"description\":{JsonSerializer.Serialize(description)},\"base_bank\":{JsonSerializer.Serialize(baseBank)}}}");
        foreach (var property in document.RootElement.EnumerateObject()) result.Metadata[property.Name] = property.Value.Clone();
        result.Files.AddRange(files.Select(x => new RebankFile(x.Index, x.Name, x.Data, x.Status)));
        result.Metadata["count"] = JsonSerializer.SerializeToElement(result.Files.Count);
        return result;
    }
}

public static class RebankArchive
{
    public static RebankPackage Read(Stream input)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
        var package = new RebankPackage();
        var config = zip.GetEntry("rebank.json") ?? throw new InvalidDataException("缺少 rebank.json");
        using (var reader = new StreamReader(config.Open()))
        {
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            foreach (var property in document.RootElement.EnumerateObject()) package.Metadata[property.Name] = property.Value.Clone();
        }
        foreach (var entry in zip.Entries.Where(x => !string.IsNullOrEmpty(x.Name) && !x.FullName.Equals("rebank.json", StringComparison.OrdinalIgnoreCase)))
        {
            var parts = entry.FullName.Replace('\\', '/').Split('/');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var index))
            {
                using var unknownSource = entry.Open(); using var unknownOutput = new MemoryStream(); unknownSource.CopyTo(unknownOutput);
                var unknownPath = entry.FullName.Replace('\\', '/');
                EnsureSafePath(unknownPath);
                package.PreservedFiles.Add((unknownPath, unknownOutput.ToArray()));
                continue;
            }
            using var source = entry.Open(); using var output = new MemoryStream(); source.CopyTo(output);
            package.Files.Add(new(index, parts[^1], output.ToArray()));
        }
        return package;
    }

    public static void Write(RebankPackage package, Stream output)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, true);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rebank.json" };
        var config = zip.CreateEntry("rebank.json");
        using (var configStream = config.Open())
        using (var writer = new Utf8JsonWriter(configStream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject(); foreach (var pair in package.Metadata) { writer.WritePropertyName(pair.Key); pair.Value.WriteTo(writer); } writer.WriteEndObject();
        }
        foreach (var file in package.Files.OrderBy(x => x.Index).ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            var path = $"{file.Index}/{file.Name}";
            EnsureSafePath(path);
            if (!paths.Add(path)) throw new InvalidDataException($"Rebank 包含重复路径: {path}");
            using var target = zip.CreateEntry(path).Open();
            target.Write(file.Data);
        }
        foreach (var (path, data) in package.PreservedFiles)
        {
            EnsureSafePath(path);
            if (!paths.Add(path)) throw new InvalidDataException($"Rebank 包含重复路径: {path}");
            using var target = zip.CreateEntry(path).Open();
            target.Write(data);
        }
    }

    private static void EnsureSafePath(string path)
    {
        if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(x => x is "" or ".." or "."))
            throw new InvalidDataException($"Rebank 包含非法路径: {path}");
    }
}

public sealed class RebankFormatHandler : IModFormatHandler
{
    public FormatDescriptor Descriptor { get; } = new(ModFormatKind.Rebank, "Rebank 音频差分", [".rebank"], true, true);
    public ValueTask<FormatProbeResult> ProbeAsync(Stream input, string? fileName = null, CancellationToken cancellationToken = default)
    { try { using var zip = new ZipArchive(input, ZipArchiveMode.Read, true); var ok = zip.GetEntry("rebank.json") is not null; return ValueTask.FromResult(new FormatProbeResult(ModFormatKind.Rebank, ok, ok ? 99 : 0, ok ? "检测到 rebank.json" : "缺少 rebank.json", [])); } catch (InvalidDataException) { return ValueTask.FromResult(new FormatProbeResult(ModFormatKind.Rebank, false, 0, "不是有效 ZIP", [])); } }
    public Task<ModPackage> ImportAsync(Stream input, ImportContext context)
    {
        var payload = RebankArchive.Read(input);
        var package = new ModPackage { SourceFormat = ModFormatKind.Rebank, Payload = payload };
        foreach (var file in payload.Files)
            package.Project.Assets.Add(new LimbusModEditor.Domain.Assets.AssetRecord { LogicalPath = $"{file.Index}/{file.Name}", ContainerPath = $"{file.Index}/{file.Name}", Type = LimbusModEditor.Domain.Assets.AssetType.Audio, Size = file.Data.LongLength, EditState = LimbusModEditor.Domain.Assets.AssetEditState.Modified });
        package.PreservedFiles.AddRange(payload.PreservedFiles.Select(x => x.Path));
        return Task.FromResult(package);
    }
    public Task<ValidationReport> ValidateAsync(ModPackage package, CancellationToken cancellationToken = default)
    {
        var report = new ValidationReport();
        if (package.Payload is not RebankPackage rebank) report.Diagnostics.Add(new(DiagnosticSeverity.Error, "REBANK_PAYLOAD", "不是有效的 Rebank 数据模型。"));
        else if (!rebank.Metadata.TryGetValue("base_bank", out _)) report.Diagnostics.Add(new(DiagnosticSeverity.Warning, "REBANK_BASE", "未声明 base_bank，加载器可能无法定位目标 bank。"));
        return Task.FromResult(report);
    }
    public Task ExportAsync(ModPackage package, Stream output, ExportContext context)
    {
        if (package.Payload is not RebankPackage rebank) return Task.FromException(new InvalidDataException("Rebank 项目缺少音频差分数据。"));
        RebankArchive.Write(rebank, output); return Task.CompletedTask;
    }
}
