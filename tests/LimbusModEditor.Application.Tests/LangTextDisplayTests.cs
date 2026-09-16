using LimbusModEditor.Application.Texts;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// plan-16 §5：文本工作台的**路径口径**护栏。
///
/// <para>本轮把条目口径改成「相对活动语言目录」（树 / 列表 / 索引库 / 编辑集 / 搜索命中
/// 一律 <c>AbDlg_Faust.json</c>、<c>StoryData/S1.json</c>——不再带 <c>LLc-CN-LCTA/</c> 那一层），
/// 而补丁文档的键仍必须是「相对 lang 根」（真实加载器按
/// <c>&lt;游戏&gt;/LimbusCompany_Data/lang/&lt;键&gt;</c> 应用）。</para>
///
/// <para>于是只剩**一个方向**的转换：条目口径 → 加载器口径（<see cref="LangTextDisplay.ToPatchKey"/>
/// 与服务形态 <see cref="LangTextWorkbenchService.ToPatchKey"/>）。这里把它逐条钉死，
/// 免得将来有人「顺手」把条目路径直接写进补丁（那会让加载器在整个 lang 根下找
/// <c>AbDlg_Faust.json</c>，静默不生效）。</para>
/// </summary>
public sealed class LangTextDisplayTests
{
    private const string LangRoot = @"C:\game\LimbusCompany_Data\lang";

    [Fact]
    public void Prefix_is_the_language_directory_name_plus_separator()
    {
        Assert.Equal("LLc-CN-LCTA/", LangTextDisplay.PrefixOf(LangRoot, @"C:\game\LimbusCompany_Data\lang\LLc-CN-LCTA"));
        // 嵌套目录（理论上不会出现，但前缀计算不该因此错位）
        Assert.Equal("group/LLC_zh-CN/", LangTextDisplay.PrefixOf(LangRoot, @"C:\game\LimbusCompany_Data\lang\group\LLC_zh-CN"));
        // 没有活动语言目录：没有可补的一层
        Assert.Equal(string.Empty, LangTextDisplay.PrefixOf(LangRoot, null));
        Assert.Equal(string.Empty, LangTextDisplay.PrefixOf(LangRoot, ""));
        // 语言目录就是 lang 根本身：没有可补的一层
        Assert.Equal(string.Empty, LangTextDisplay.PrefixOf(LangRoot, LangRoot));
        // lang 根之外的目录：绝不补前缀（否则会指向不存在的位置）
        Assert.Equal(string.Empty, LangTextDisplay.PrefixOf(LangRoot, @"C:\other\lang"));
    }

    [Fact]
    public void Patch_key_adds_the_language_directory_prefix_and_is_idempotent()
    {
        const string prefix = "LLc-CN-LCTA/";
        Assert.Equal("LLc-CN-LCTA/AbDlg_Faust.json", LangTextDisplay.ToPatchKey(prefix, "AbDlg_Faust.json"));
        Assert.Equal("LLc-CN-LCTA/StoryData/S1.json", LangTextDisplay.ToPatchKey(prefix, "StoryData/S1.json"));
        // 已经是内部键（加载器口径）：不重复补前缀
        Assert.Equal("LLc-CN-LCTA/AbDlg_Faust.json", LangTextDisplay.ToPatchKey(prefix, "LLc-CN-LCTA/AbDlg_Faust.json"));
        // 大小写不同（config.json 的写法可能与磁盘/前缀不一致）：仍按前缀命中，不重复补
        // （不命中时的返回会保留调用方给的大小写，所以这里断言的是「没有被补第二次」）。
        Assert.Equal("llc-cn-lcta/AbDlg_Faust.json", LangTextDisplay.ToPatchKey(prefix, "llc-cn-lcta/AbDlg_Faust.json"));
        // 没有语言目录：补丁键就是条目路径本身
        Assert.Equal("AbDlg_Faust.json", LangTextDisplay.ToPatchKey(string.Empty, "AbDlg_Faust.json"));
    }

    /// <summary>加载器口径的键与条目口径之间只有「一层前缀」的差别——多级子目录不许被改写。</summary>
    [Fact]
    public void Patch_key_never_rewrites_the_inner_path()
    {
        const string prefix = "LLc-CN-LCTA/";
        foreach (var relative in new[] { "AbDlg_Faust.json", "StoryData/S1.json", "a/b/c.json" })
        {
            var key = LangTextDisplay.ToPatchKey(prefix, relative);
            Assert.StartsWith(prefix, key, StringComparison.Ordinal);
            Assert.Equal(relative, key[prefix.Length..]);
        }
    }
}
