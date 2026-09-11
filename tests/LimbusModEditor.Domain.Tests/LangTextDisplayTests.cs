using LimbusModEditor.Application.Texts;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// plan-14.4：文本工作台的「显示口径 ⇄ 补丁键」映射。
///
/// <para>这条映射是<b>加载器兼容的护栏</b>：界面从语言目录内部开始显示
/// （不再有 <c>config.json</c> 与语言目录那一层），但补丁文档的键必须仍是
/// 「相对 lang 根」（真实加载器按 <c>&lt;游戏&gt;/LimbusCompany_Data/lang/&lt;键&gt;</c> 应用）。
/// 这里把前缀的生成与两个方向的转换逐条钉死，免得将来有人「顺手」把显示路径直接写进补丁。</para>
/// </summary>
public sealed class LangTextDisplayTests
{
    private const string LangRoot = @"C:\game\LimbusCompany_Data\lang";

    [Fact]
    public void Prefix_is_the_language_directory_name_plus_separator()
    {
        Assert.Equal("LLc-CN-LCTA/", LangTextDisplay.PrefixOf(LangRoot, @"C:\game\LimbusCompany_Data\lang\LLc-CN-LCTA"));
        // 嵌套目录（理论上不会出现，但映射不该因此错位）
        Assert.Equal("group/LLC_zh-CN/", LangTextDisplay.PrefixOf(LangRoot, @"C:\game\LimbusCompany_Data\lang\group\LLC_zh-CN"));
        // 没有活动语言目录：不做映射（显示路径 = 内部键）
        Assert.Equal(string.Empty, LangTextDisplay.PrefixOf(LangRoot, null));
        Assert.Equal(string.Empty, LangTextDisplay.PrefixOf(LangRoot, ""));
        // 语言目录就是 lang 根本身：没有可去掉的一层
        Assert.Equal(string.Empty, LangTextDisplay.PrefixOf(LangRoot, LangRoot));
        // lang 根之外的目录：绝不映射（否则会把路径改成不存在的位置）
        Assert.Equal(string.Empty, LangTextDisplay.PrefixOf(LangRoot, @"C:\other\lang"));
    }

    [Fact]
    public void Display_path_drops_the_language_directory_prefix_case_insensitively()
    {
        const string prefix = "LLc-CN-LCTA/";
        Assert.Equal("AbDlg_Faust.json", LangTextDisplay.ToDisplayPath(prefix, "LLc-CN-LCTA/AbDlg_Faust.json"));
        Assert.Equal("StoryData/S1.json", LangTextDisplay.ToDisplayPath(prefix, "LLc-CN-LCTA/StoryData/S1.json"));
        // config.json 里的大小写可能与磁盘不同（Windows 路径不敏感）：仍要能去掉前缀
        Assert.Equal("AbDlg_Faust.json", LangTextDisplay.ToDisplayPath(prefix, "llc-cn-lcta/AbDlg_Faust.json"));
        // 前缀不命中：原样返回（退化路径/其它语言目录不该被改写）
        Assert.Equal("LLC_en/en.json", LangTextDisplay.ToDisplayPath(prefix, "LLC_en/en.json"));
        Assert.Equal(string.Empty, LangTextDisplay.ToDisplayPath(string.Empty, string.Empty));
    }

    [Fact]
    public void Patch_key_adds_the_prefix_back_and_is_idempotent()
    {
        const string prefix = "LLc-CN-LCTA/";
        Assert.Equal("LLc-CN-LCTA/AbDlg_Faust.json", LangTextDisplay.ToPatchKey(prefix, "AbDlg_Faust.json"));
        Assert.Equal("LLc-CN-LCTA/StoryData/S1.json", LangTextDisplay.ToPatchKey(prefix, "StoryData/S1.json"));
        // 已经是内部键：不重复补前缀（同一个函数在两条路径上都能用）
        Assert.Equal("LLc-CN-LCTA/AbDlg_Faust.json", LangTextDisplay.ToPatchKey(prefix, "LLc-CN-LCTA/AbDlg_Faust.json"));
        // 没有前缀：补丁键就是路径本身
        Assert.Equal("AbDlg_Faust.json", LangTextDisplay.ToPatchKey(string.Empty, "AbDlg_Faust.json"));
    }

    [Fact]
    public void Display_and_patch_key_round_trip()
    {
        const string prefix = "LLc-CN-LCTA/";
        foreach (var key in new[] { "LLc-CN-LCTA/AbDlg_Faust.json", "LLc-CN-LCTA/StoryData/S1.json", "LLc-CN-LCTA/a/b/c.json" })
        {
            var display = LangTextDisplay.ToDisplayPath(prefix, key);
            Assert.DoesNotContain(prefix, display, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(key, LangTextDisplay.ToPatchKey(prefix, display));
        }
    }
}
