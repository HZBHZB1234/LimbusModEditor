using LimbusModEditor.Domain.Formats;

namespace LimbusModEditor.Domain.Projects;

/// <summary>A package or directory imported into an authoring project.</summary>
public sealed class ProjectSource
{
    public Guid SourceId { get; init; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public ModFormatKind Format { get; set; } = ModFormatKind.Unknown;
    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;
}
