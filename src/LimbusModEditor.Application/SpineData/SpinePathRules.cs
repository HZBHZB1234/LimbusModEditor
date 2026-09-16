using LimbusModEditor.Application.Relations;

namespace LimbusModEditor.Application.SpineData;

/// <summary>
/// Spine 资源路径与文件名的纯规则（无 IO，可单测）。
/// 路径判据单一来源：委托 RelationDisplayRules.IsSpinePath，不另立实现（ADR §7）。
/// </summary>
internal static class SpinePathRules
{
    /// <summary>路径是否为 Spine 资源。委托 RelationDisplayRules.IsSpinePath（单一判据来源）。</summary>
    public static bool IsSpinePath(string? containerEntry)
        => RelationDisplayRules.IsSpinePath(containerEntry);

    /// <summary>文件名是否为 Spine 骨架 JSON。</summary>
    public static bool IsSkeletonJsonFileName(string fileName)
        => fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
           && !fileName.EndsWith(".atlas.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>文件名是否为 Spine 图集文本。</summary>
    public static bool IsAtlasFileName(string fileName)
        => fileName.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase)
           || fileName.EndsWith(".atlas.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>容器路径的目录部分（统一 /，不含末尾斜杠）。</summary>
    public static string FolderOf(string containerEntry)
    {
        var normalized = containerEntry.Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        return index <= 0 ? string.Empty : normalized[..index];
    }

    /// <summary>容器路径的文件名部分。</summary>
    public static string FileNameOf(string containerEntry)
    {
        var normalized = containerEntry.Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }
}

/// <summary>
/// 纹理格式判定与 PNG 编码路径选择。纯函数，无 IO，可单测。
/// </summary>
internal static class TextureFormatRules
{
    /// <summary>Unity TextureFormat 值是否为 DXT 压缩（需预解码）。</summary>
    public static bool IsDxt(int textureFormat)
        => textureFormat == 10 /* Dxt1 */ || textureFormat == 12 /* Dxt5 */;

    /// <summary>Unity TextureFormat 值是否可原样透传（RGBA32/RGB24/BGRA32/ARGB32）。</summary>
    public static bool IsPassthrough(int textureFormat)
        => textureFormat == 3 /* Rgb24 */
           || textureFormat == 4 /* Rgba32 */
           || textureFormat == 5 /* Argb32 */
           || textureFormat == 14 /* Bgra32 */;

    /// <summary>获取纹理格式的中文显示名。</summary>
    public static string FormatName(int textureFormat) => textureFormat switch
    {
        1 => "Alpha8",
        2 => "ARGB4444",
        3 => "RGB24",
        4 => "RGBA32",
        5 => "ARGB32",
        7 => "RGB565",
        8 => "BGR24",
        9 => "R16",
        10 => "DXT1",
        12 => "DXT5",
        13 => "RGBA4444",
        14 => "BGRA32",
        62 => "RG16",
        63 => "R8",
        _ => $"未知格式 {textureFormat}"
    };
}
