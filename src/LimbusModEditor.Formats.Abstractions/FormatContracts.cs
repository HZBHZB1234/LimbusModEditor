using System.IO;
using LimbusModEditor.Domain.Formats;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Formats.Abstractions;

public sealed record FormatProbeResult(
    ModFormatKind Format,
    bool IsMatch,
    int Confidence,
    string Description,
    IReadOnlyList<FormatDiagnostic> Diagnostics);

public sealed record ImportContext(
    string? ProjectDirectory = null,
    bool PreserveUnknownFiles = true,
    CancellationToken CancellationToken = default);

public sealed record ExportContext(
    ModFormatKind TargetFormat,
    bool PreserveUnknownFiles = true,
    bool ValidateBeforeExport = true,
    CancellationToken CancellationToken = default,
    object? Codec = null);

public sealed class ModPackage
{
    public ModFormatKind SourceFormat { get; init; } = ModFormatKind.Unknown;
    public ModProject Project { get; init; } = new();
    public List<string> PreservedFiles { get; } = [];
    public object? Payload { get; init; }
}

public interface IModFormatHandler
{
    FormatDescriptor Descriptor { get; }
    ValueTask<FormatProbeResult> ProbeAsync(Stream input, string? fileName = null, CancellationToken cancellationToken = default);
    Task<ModPackage> ImportAsync(Stream input, ImportContext context);
    Task<ValidationReport> ValidateAsync(ModPackage package, CancellationToken cancellationToken = default);
    Task ExportAsync(ModPackage package, Stream output, ExportContext context);
}
