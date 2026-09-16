using LimbusModEditor.Application.Texts;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// plan-16 §5 的**口径回归门**：文本工作台的条目路径不再带「根文件夹」
/// （即活动语言目录那一层）。
///
/// <para>用户口径：库里的条目要直接从 <c>lang/a_folder_name</c> 开始。因此
/// <see cref="LangTextFileInfo.RelativePath"/>、索引库的 <c>files.rel_path</c> /
/// <c>hits.rel_path</c>、编辑集键、搜索命中一律是「相对活动语言目录」的路径；
/// 而导出补丁时的键必须由 <see cref="LangTextWorkbenchService.ToPatchKey"/> 把语言目录
/// 那层补回来（加载器按 lang 根应用，见 <see cref="LangTextPatchService"/>）。</para>
///
/// <para>本类用真实目录布局（<c>&lt;lang&gt;/&lt;活动语言&gt;/…</c>）把这三件事逐条钉死：
/// ① 条目里没有语言目录名；② 条目 + 语言目录能拼回真实文件；③ 补丁键往返得到加载器口径。</para>
/// </summary>
public sealed class LangEntryCaliberTests : IDisposable
{
    private const string ActiveLanguage = "LLc-CN-LCTA";

    private readonly string _work = Path.Combine(Path.GetTempPath(), "lme-caliber-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
        }
        catch (Exception) { /* 临时目录清理失败不影响测试结论 */ }
    }

    private string LangRoot => Path.Combine(_work, "LimbusCompany_Data", "lang");
    private string LanguageDirectory => Path.Combine(LangRoot, ActiveLanguage);

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Seed()
    {
        WriteFile(Path.Combine(LangRoot, "config.json"), $$"""{"lang":"{{ActiveLanguage}}","titleFont":"","contextFont":""}""");
        WriteFile(Path.Combine(LanguageDirectory, "AbDlg_Faust.json"),
            """{"dataList":[{"id":1,"dialog":"浮士德会亲自处理。"}]}""");
        WriteFile(Path.Combine(LanguageDirectory, "StoryData", "S1.json"), """{"dataList":[{"dialog":"故事文本"}]}""");
        WriteFile(Path.Combine(LanguageDirectory, "a_folder_name", "deep", "x.json"), """{"k":"v"}""");
        // 其它语言目录：不索引（也不该出现在条目里）
        WriteFile(Path.Combine(LangRoot, "LLC_zh-CN", "en.json"), """{"other":"语言"}""");
    }

    [Fact]
    public void Relative_paths_start_inside_the_language_directory()
    {
        Seed();
        var service = new LangTextWorkbenchService();
        var files = service.EnumerateFiles(LangRoot);
        var relatives = files.Select(x => x.RelativePath).ToList();

        // ① 条目里既没有 lang 根，也没有语言目录那一层。
        Assert.All(relatives, x => Assert.DoesNotContain(ActiveLanguage, x, StringComparison.OrdinalIgnoreCase));
        Assert.All(relatives, x => Assert.False(x.StartsWith("lang/", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(
            new[] { "AbDlg_Faust.json", "StoryData/S1.json", "a_folder_name/deep/x.json" }.OrderBy(x => x, StringComparer.Ordinal),
            relatives.OrderBy(x => x, StringComparer.Ordinal));
        // 其它语言目录整目录不进条目
        Assert.DoesNotContain(relatives, x => x.Contains("en.json", StringComparison.OrdinalIgnoreCase));

        // ② 条目 + 语言目录 = 磁盘上真实存在的文件
        Assert.Equal(Path.GetFullPath(LanguageDirectory), service.CurrentLanguageDirectory);
        Assert.All(files, x => Assert.True(File.Exists(x.FullPath), $"FullPath 应真实存在: {x.FullPath}"));
        foreach (var relative in relatives)
            Assert.True(File.Exists(Path.Combine(LanguageDirectory, relative.Replace('/', Path.DirectorySeparatorChar))),
                $"条目应能拼回真实文件: {relative}");
    }

    [Fact]
    public void Patch_key_restores_the_loader_caliber()
    {
        Seed();
        var service = new LangTextWorkbenchService();
        service.EnumerateFiles(LangRoot);

        Assert.Equal($"{ActiveLanguage}/", service.LanguageDirectoryPrefix);
        Assert.Equal($"{ActiveLanguage}/AbDlg_Faust.json", service.ToPatchKey("AbDlg_Faust.json"));
        Assert.Equal($"{ActiveLanguage}/StoryData/S1.json", service.ToPatchKey("StoryData/S1.json"));
        // 幂等：已经是加载器口径的键不再补一次
        Assert.Equal($"{ActiveLanguage}/AbDlg_Faust.json", service.ToPatchKey($"{ActiveLanguage}/AbDlg_Faust.json"));
        // 两套口径的差别只有一层前缀 —— 这是「DB 去根、导出补键」的接缝
        Assert.Equal("AbDlg_Faust.json", service.ToPatchKey("AbDlg_Faust.json")[$"{ActiveLanguage}/".Length..]);
    }

    [Fact]
    public void Attach_lang_root_resolves_the_language_directory_for_the_index_hit_path()
    {
        Seed();
        // 页面走「索引命中」路径时不调 EnumerateFiles，只 AttachLangRoot —— 它必须同时
        // 解析出活动语言目录，否则条目拼不回磁盘路径、编辑集也没有基线。
        var service = new LangTextWorkbenchService();
        service.AttachLangRoot(LangRoot);

        Assert.Equal(Path.GetFullPath(LanguageDirectory), service.CurrentLanguageDirectory);
        Assert.Equal($"{ActiveLanguage}/", service.LanguageDirectoryPrefix);
        var text = service.BeginEdit("AbDlg_Faust.json"); // 条目口径，不含语言目录
        Assert.Contains("浮士德", text);
        Assert.Equal($"{ActiveLanguage}/AbDlg_Faust.json", service.ToPatchKey("AbDlg_Faust.json"));
    }

    [Fact]
    public void Config_without_a_language_directory_degrades_to_the_lang_root()
    {
        Seed();
        File.Delete(Path.Combine(LangRoot, "config.json"));

        var service = new LangTextWorkbenchService();
        service.AttachLangRoot(LangRoot);
        Assert.Equal(Path.GetFullPath(LangRoot), service.CurrentLanguageDirectory);
        Assert.Equal(string.Empty, service.LanguageDirectoryPrefix); // 没有可补的一层
        Assert.Equal("AbDlg_Faust.json", service.ToPatchKey("AbDlg_Faust.json"));
        // 退化时枚举为空（没有活动语言就没有可浏览的文本表），但调用不抛。
        Assert.Empty(service.EnumerateFiles(LangRoot));
    }
}
