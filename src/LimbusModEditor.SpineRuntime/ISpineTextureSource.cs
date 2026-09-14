namespace LimbusModEditor.SpineRuntime;

/// <summary>
/// 图集页 PNG 字节的来源。
/// <para>
/// 真实的 Limbus Company 骨架中，图集页 PNG 藏在 AssetBundle 里。调用方负责把这些 PNG 从
/// bundle 中解出，并按“图集页名 → PNG 字节”提供给渲染器。页名即 atlas 文本里每个 page 的
/// <c>name</c> 字段（例如 <c>wcorp_meursault.png</c>）。
/// </para>
/// <para>返回 <c>null</c> 表示缺失该页，渲染器会据此给出中文失败原因而不是抛异常。</para>
/// </summary>
public interface ISpineTextureSource
{
    /// <summary>
    /// 返回指定图集页名对应的 PNG 文件字节；若无法提供（缺失/未解包）则返回 <c>null</c>。
    /// </summary>
    byte[] GetPageBytes(string pageName);
}
