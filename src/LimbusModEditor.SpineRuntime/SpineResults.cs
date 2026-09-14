namespace LimbusModEditor.SpineRuntime;

/// <summary>
/// 解析/加载骨架的结果。解析失败时 <see cref="Success"/> 为 false 且 <see cref="Error"/> 携带
/// 中文原因——解析路径绝不向上抛异常（mod 编辑器里静默崩溃不可接受）。
/// </summary>
public sealed class SpineLoadResult
{
    /// <summary>是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>成功时有效，可渲染的文档。</summary>
    public SpineDocument Document { get; init; }

    /// <summary>失败时的中文原因（可为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>加载过程中缺失的图集页名（若用户需要提示“缺了哪些贴图”）。</summary>
    public IReadOnlyList<string> MissingPages { get; init; } = Array.Empty<string>();

    public static SpineLoadResult Ok(SpineDocument document, IReadOnlyList<string> missingPages) =>
        new() { Success = true, Document = document, MissingPages = missingPages };

    public static SpineLoadResult Fail(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>
/// 单帧渲染的结果。失败时 <see cref="Success"/> 为 false 且 <see cref="Error"/> 携带中文原因。
/// </summary>
public sealed class SpineRenderResult
{
    /// <summary>是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>成功时的 PNG 字节。</summary>
    public byte[]? PngBytes { get; init; }

    /// <summary>失败时的中文原因（可为 null）。</summary>
    public string? Error { get; init; }

    public static SpineRenderResult Ok(byte[] png) => new() { Success = true, PngBytes = png };

    public static SpineRenderResult Fail(string error) => new() { Success = false, Error = error };
}
