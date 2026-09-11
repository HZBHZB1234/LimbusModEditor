namespace LimbusModEditor.Application.Texts;

/// <summary>
/// 文本工作台的**显示口径**映射（纯函数，可单测）。
///
/// <para><b>为什么需要它</b>：补丁键的口径必须是「相对 lang 根」——真实加载器按
/// <c>&lt;游戏&gt;/LimbusCompany_Data/lang/&lt;键&gt;</c> 备份并应用补丁
/// （见 <see cref="LangTextPatchService"/>）。而用户看工作台时，语言目录那外层
/// （本机实测是汉化组的 <c>LLc-CN-LCTA</c>）与 <c>config.json</c> 都是噪声：
/// 他要的是「从语言目录内部开始」的文件树。</para>
///
/// <para>两条口径因此被显式分开：</para>
/// <list type="bullet">
/// <item><b>补丁键（内部键）</b>：<c>LLc-CN-LCTA/AbDlg_Faust.json</c> —— 索引行、编辑集、
/// 搜索命中、补丁文档一律用它，绝不改。</item>
/// <item><b>显示路径</b>：<c>AbDlg_Faust.json</c> —— 只有界面用它（树 / 列表 / 状态文案）。</item>
/// </list>
///
/// <para>映射规则是「去掉一层前缀」，两个方向都只做前缀判断，不做路径重写；
/// 前缀按 <see cref="StringComparison.OrdinalIgnoreCase"/> 比对（Windows 路径大小写不敏感，
/// 且 config.json 里写的语言名大小写未必与磁盘一致）。</para>
/// </summary>
public static class LangTextDisplay
{
    /// <summary>
    /// 显示前缀 = 活动语言目录相对 lang 根的路径 + <c>'/'</c>（如 <c>LLc-CN-LCTA/</c>）；
    /// 语言目录不在 lang 根之下（或未解析出来）时返回空串（显示路径 = 内部键，不做映射）。
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

    /// <summary>内部键 → 显示路径（前缀命中才去掉；其它情况原样返回，例如退化路径）。</summary>
    public static string ToDisplayPath(string prefix, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return prefix.Length > 0 && relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? relativePath[prefix.Length..]
            : relativePath;
    }

    /// <summary>显示路径 → 补丁键（补前缀；已经是带前缀的内部键时不重复补）。</summary>
    public static string ToPatchKey(string prefix, string displayPath)
    {
        ArgumentNullException.ThrowIfNull(displayPath);
        if (prefix.Length == 0) return displayPath;
        return displayPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? displayPath : prefix + displayPath;
    }
}
