namespace LimbusModEditor.Application.SpineData;

/// <summary>
/// Spine 原始素材：骨架字节 + 图集文本 + 纹理页字节（PNG 编码）。
/// C# 侧只负责定位、读取与纹理预解码；前端负责解析与 WebGL 渲染。
/// </summary>
/// <param name="SkeletonBytes">骨架字节（Spine 4.0 JSON 文本 或 .skel/.psb 二进制）。</param>
/// <param name="SkeletonFormat">骨架格式："json" 或 "binary"。</param>
/// <param name="AtlasText">图集文本（.atlas.txt 内容）。</param>
/// <param name="PageBytes">纹理页：页名 → PNG 字节（已由 C# 侧从 Unity 纹理格式解码为 PNG）。</param>
/// <param name="AvailablePages">可用纹理页名列表（诊断用）。</param>
/// <param name="Label">显示标签（资源所在目录名）。</param>
public sealed record SpineRawData(
    byte[] SkeletonBytes,
    string SkeletonFormat,
    string AtlasText,
    IReadOnlyDictionary<string, byte[]> PageBytes,
    IReadOnlyList<string> AvailablePages,
    string Label)
{
    /// <summary>骨架字节是否非空。</summary>
    public bool HasSkeleton => SkeletonBytes.Length > 0;

    /// <summary>是否至少有一页纹理。</summary>
    public bool HasTextures => PageBytes.Count > 0;
}
