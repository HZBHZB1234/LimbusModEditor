namespace LimbusModEditor.Application.Relations;

/// <summary>
/// 精确跳转载荷的编解码。目标页要「选中唯一那一行 / 展开到那个键」，光有资源级定位键不够，
/// 还需要子定位信息（文本里的键路径、静态表里的记录键）。
///
/// <para><b>为什么沿用 <c>'\0'</c> 分隔</b>：音频的 <see cref="RelationLink.RefKey"/>
/// 早就是 <c>bank 路径 + '\0' + 样本名</c> 的口径，文本/静态沿用同一约定最省事、
/// 也让「拆开看」的代码只有一处（见 <see cref="Decode"/>）。</para>
///
/// <para>载荷是<b>纯数据</b>，不参与判重、不影响关联图正确性——升级格式时不需要改
/// <see cref="RelationIndexSource.FormatVersion"/>。</para>
/// </summary>
public static class RelationDeepLink
{
    /// <summary>子定位分隔符（与音频 RefKey 的约定一致）。</summary>
    public const char Separator = '\0';

    /// <summary>音频样本：<c>bank 路径 + '\0' + 样本名</c>。</summary>
    public static string ForAudio(string bankPath, string sampleName)
        => Join(bankPath, sampleName);

    /// <summary>文本：<c>相对路径 + '\0' + 键路径</c>（键路径可为空 → 只定位到文件）。</summary>
    public static string ForText(string relativePath, string? keyPath)
        => Join(relativePath, keyPath);

    /// <summary>静态数据：<c>容器路径 + '\0' + 记录键</c>（记录键可为空 → 只定位到表）。</summary>
    public static string ForStatic(string containerEntry, string? recordKey)
        => Join(containerEntry, recordKey);

    /// <summary>Unity 资源：只有容器路径（资源级定位）。</summary>
    public static string ForAsset(string containerEntry) => containerEntry;

    /// <summary>拆开载荷；空载荷返回空数组，任何一段都不会是 null。</summary>
    public static IReadOnlyList<string> Decode(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return [];
        var parts = payload.Split(Separator);
        for (var i = 0; i < parts.Length; i++) parts[i] ??= string.Empty;
        return parts;
    }

    /// <summary>取第 <paramref name="index"/> 段；不存在返回空串。</summary>
    public static string Part(string? payload, int index)
    {
        var parts = Decode(payload);
        return index >= 0 && index < parts.Count ? parts[index] : string.Empty;
    }

    private static string Join(string head, string? tail)
        => string.IsNullOrEmpty(tail) ? head : string.Concat(head, Separator.ToString(), tail);
}
