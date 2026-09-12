namespace LimbusModEditor.Application.Texts;

/// <summary>
/// 文本工作台的**路径前缀工具**（纯函数，可单测）。
///
/// <para><b>plan-16 §5 之前</b>它承担「显示口径 ⇄ 内部键」的双向映射：内部键（相对 lang 根，
/// 形如 <c>LLc-CN-LCTA/AbDlg_Faust.json</c>）与显示路径（语言目录内部，形如
/// <c>AbDlg_Faust.json</c>）互相转换。**本轮起条目口径本身就改成「相对活动语言目录」**
/// （<see cref="LangTextWorkbenchService.CurrentLanguageDirectory"/>），界面、索引库、
/// 编辑集、搜索命中、导出源全部统一到它——于是「显示路径 = 条目路径」，双向映射不再需要。</para>
///
/// <para>仍然保留的是「条目口径 → 加载器口径」的**单向前缀补回**：
/// 真实加载器（LCTA <c>launcher/changes.py</c>）按
/// <c>&lt;游戏&gt;/LimbusCompany_Data/lang/&lt;键&gt;</c> 定位并应用补丁，所以补丁文档的键必须是
/// 「相对 lang 根」的（<c>LLc-CN-LCTA/AbDlg_Faust.json</c>）。该转换的**正式入口**是
/// <see cref="LangTextWorkbenchService.ToPatchKey"/>（服务已持有 lang 根与语言目录）；
/// 本类的 <see cref="PrefixOf"/> / <see cref="ToPatchKey"/> 是同一规则的纯函数形态，
/// 供只需要路径计算、不持有服务实例的调用方（与单测）使用。</para>
///
/// <para>前缀按 <see cref="StringComparison.OrdinalIgnoreCase"/> 比对（Windows 路径大小写不敏感，
/// 且 config.json 里写的语言名大小写未必与磁盘一致）。</para>
/// </summary>
public static class LangTextDisplay
{
    /// <summary>
    /// 语言目录前缀 = 活动语言目录相对 lang 根的路径 + <c>'/'</c>（如 <c>LLc-CN-LCTA/</c>）；
    /// 语言目录不在 lang 根之下（或未解析出来）时返回空串（表示「不需要补前缀」）。
    /// </summary>
    public static string PrefixOf(string langRoot, string? languageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(langRoot);
        if (string.IsNullOrWhiteSpace(languageDirectory)) return string.Empty;
        var relative = Path.GetRelativePath(Path.GetFullPath(langRoot), Path.GetFullPath(languageDirectory))
            .Replace('\\', '/');
        if (relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal)) return string.Empty;
        return relative.Length == 0 ? string.Empty : relative.TrimEnd('/') + "/";
    }

    /// <summary>
    /// 条目口径（相对活动语言目录）→ 加载器口径（相对 lang 根）：补回前缀。
    /// 已经是带前缀的路径时不重复补。**导出补丁文档的键必须走这里**（或其服务形态
    /// <see cref="LangTextWorkbenchService.ToPatchKey"/>）。
    /// </summary>
    public static string ToPatchKey(string prefix, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (prefix.Length == 0) return relativePath;
        return relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? relativePath : prefix + relativePath;
    }
}
