using System.Collections.ObjectModel;

namespace LimbusModEditor.Domain.Projects;

public sealed class ModProject
{
    public const int CurrentSchemaVersion = 2;

    public Guid ProjectId { get; init; } = Guid.NewGuid();
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Name { get; set; } = "未命名模组";
    public string Version { get; set; } = "0.1.0";
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? GameDirectory { get; set; }
    /// <summary>Optional directory where package mods are installed for
    /// debug-apply. This is distinct from the Unity Cache directory.</summary>
    public string? ModDirectory { get; set; }
    public string? GameExecutablePath { get; set; }
    public string? UnityCacheDirectory { get; set; }
    public string? FmodLibraryDirectory { get; set; }
    public bool RestoreDebugFilesOnClose { get; set; } = true;
    public string? SourceDirectory { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ObservableCollection<Assets.AssetRecord> Assets { get; set; } = [];
    public ObservableCollection<Edits.EditOperation> Edits { get; set; } = [];
    public ObservableCollection<ProjectSource> Sources { get; set; } = [];
}
