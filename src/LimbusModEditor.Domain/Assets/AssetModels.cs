namespace LimbusModEditor.Domain.Assets;

public enum AssetType
{
    Unknown,
    Texture,
    Sprite,
    Audio,
    Text,
    Json,
    MonoBehaviour,
    /// <summary>Unity class ID 115. Scriptable objects serialize as
    /// MonoBehaviour (114); there is no dedicated ScriptableObject class.</summary>
    MonoScript,
    /// <summary>Legacy marker kept for old saved projects. Unity serializes
    /// ScriptableObjects as MonoBehaviour (class ID 114).</summary>
    ScriptableObject,
    Mesh,
    Animation,
    Font,
    Binary,
    GameObject
}

public enum AssetEditState
{
    Unchanged,
    Modified,
    Added,
    Deleted,
    Conflict,
    Invalid
}

public sealed class AssetRecord
{
    public Guid AssetId { get; init; } = Guid.NewGuid();
    public string LogicalPath { get; set; } = string.Empty;
    public string? SourcePath { get; set; }
    public string? ContainerPath { get; set; }
    public string? Account { get; set; }
    public string? Bundle { get; set; }
    public long? UnityPathId { get; set; }
    public int? UnityTypeId { get; set; }
    public AssetType Type { get; set; } = AssetType.Unknown;
    public string? OriginalHash { get; set; }
    public string? ModifiedHash { get; set; }
    public AssetEditState EditState { get; set; } = AssetEditState.Unchanged;
    public long Size { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>导入 bundle 时对照官方 catalog（vanilla 基线）的判定摘要，
    /// 仅供列表显示；底层事实在 Metadata["catalogBaseline"]。</summary>
    public string? CatalogBaseline => Metadata.TryGetValue("catalogBaseline", out var value) ? value : null;
}
