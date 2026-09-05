namespace LimbusModEditor.Domain.Edits;

public enum EditOperationKind
{
    ReplaceAsset,
    AddAsset,
    DeleteAsset,
    ReplaceFile,
    PatchJson,
    Rename
}

public sealed class EditOperation
{
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public EditOperationKind Kind { get; init; }
    public Guid? AssetId { get; init; }
    public string TargetPath { get; init; } = string.Empty;
    public string? SourcePath { get; init; }
    public string? BeforeHash { get; init; }
    public string? AfterHash { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
