using System.Diagnostics;
using System.Text.Json.Nodes;
using LimbusModEditor.Application.Texts;

namespace LimbusModEditor.Domain.Tests;

/// <summary>plan-07 文本工作台后端服务（LangTextWorkbenchService）单测：
/// 临时目录覆盖枚举 / 搜索 / 编辑集 / 导出补丁与回放 / 非 UTF-8 容错；
/// 真实 lang 目录验证在文件末尾（无游戏目录的机器自动跳过，预算 &lt; 60 秒）。</summary>
public class LangTextWorkbenchServiceTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lme-langwb-" + Guid.NewGuid().ToString("N"));
    private readonly LangTextWorkbenchService _service = new();

    public void Dispose()
    {
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    /// <summary>按真实布局搭一个合成 lang 根：_work/LimbusCompany_Data/lang。</summary>
    private string LangRoot => Path.Combine(_work, "LimbusCompany_Data", "lang");

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string SeedLangRoot()
    {
        WriteFile(Path.Combine(LangRoot, "config.json"), """{"lang":"LLC_zh-CN","titleFont":"","contextFont":""}""");
        WriteFile(Path.Combine(LangRoot, "LLC_zh-CN", "AbDlg_Faust.json"), """
        {
          "dataList": [
            { "id": 1, "dialog": "浮士德会亲自处理。" },
            { "id": 2, "dialog": "原始文本二" }
          ]
        }
        """);
        WriteFile(Path.Combine(LangRoot, "LLC_zh-CN", "StoryData", "S1.json"), """{"dataList":[{"dialog":"故事文本"}]}""");
        WriteFile(Path.Combine(LangRoot, "LLC_zh-CN", "arr.json"), """[1, 2, 3]""");
        WriteFile(Path.Combine(LangRoot, "LLC_en", "en.json"), """{"only":"english"}"""); // 其他语言目录：不索引
        // 非 UTF-8 样本：GBK 编码的「你」（0xC4 0xE3）
        Directory.CreateDirectory(Path.Combine(LangRoot, "LLC_zh-CN"));
        File.WriteAllBytes(Path.Combine(LangRoot, "LLC_zh-CN", "gbk.json"), [0x7B, 0xC4, 0xE3, 0x7D]);
        return _work; // 返回游戏目录
    }

    // ── 枚举 ─────────────────────────────────────────────────────────

    [Fact]
    public void Enumerate_lists_active_language_root_config_and_excludes_other_languages()
    {
        SeedLangRoot();
        _service.EnumerateFiles(LangRoot); // 再跑一遍确认幂等
        var files = _service.EnumerateFiles(LangRoot);
        var relatives = files.Select(x => x.RelativePath).ToArray();

        Assert.Equal("config.json", relatives[0]); // 根级 config.json 必在
        Assert.Contains("LLC_zh-CN/AbDlg_Faust.json", relatives);
        Assert.Contains("LLC_zh-CN/StoryData/S1.json", relatives); // 子目录逐层展开
        Assert.DoesNotContain("LLC_en/en.json", relatives);        // 其余语言目录不索引
        Assert.All(relatives, x => Assert.DoesNotContain('\\', x)); // 相对路径恒 '/' 分隔

        var faust = files.Single(x => x.RelativePath == "LLC_zh-CN/AbDlg_Faust.json");
        Assert.Equal(1, faust.KeyCount); // 顶层键数（JSON 对象才计）：顶层只有 "dataList"
        Assert.True(faust.IsUtf8);
        Assert.Equal(new FileInfo(faust.FullPath).Length, faust.SizeBytes);
        Assert.True(Path.IsPathRooted(faust.FullPath) && File.Exists(faust.FullPath));

        Assert.Equal(1, files.Single(x => x.RelativePath == "LLC_zh-CN/StoryData/S1.json").KeyCount);
        Assert.Equal(0, files.Single(x => x.RelativePath == "LLC_zh-CN/arr.json").KeyCount); // 非对象不计键

        var gbk = files.Single(x => x.RelativePath == "LLC_zh-CN/gbk.json");
        Assert.False(gbk.IsUtf8); // 非 UTF-8 明确标记不猜
        Assert.Equal(0, gbk.KeyCount);

        Assert.Equal("LLC_zh-CN", _service.ReadActiveLanguage(LangRoot)); // 转调 LangTextPatchService
    }

    [Fact]
    public void Resolve_lang_root_handles_missing_and_existing_directories()
    {
        Assert.Null(_service.ResolveLangRoot(null));
        Assert.Null(_service.ResolveLangRoot(""));
        Assert.Null(_service.ResolveLangRoot(Path.Combine(_work, "not-a-game")));
        Assert.Equal(Path.GetFullPath(LangRoot), _service.ResolveLangRoot(SeedLangRoot()));
    }

    // ── 搜索 ─────────────────────────────────────────────────────────

    [Fact]
    public void Search_matches_file_names_keys_and_values_with_per_file_fault_tolerance()
    {
        SeedLangRoot();
        var files = _service.EnumerateFiles(LangRoot);

        // 文件名命中（无需读内容）
        var byName = _service.Search("Faust", files).ToList();
        Assert.Single(byName);
        Assert.Equal("LLC_zh-CN/AbDlg_Faust.json", byName[0].RelativePath);
        Assert.Equal(LangTextSearchKind.FileName, byName[0].Kind);

        // 键命中（展平键路径）
        var byKey = _service.Search("dataList", files).ToList();
        Assert.All(byKey, x => Assert.Equal(LangTextSearchKind.Key, x.Kind));
        Assert.Contains(byKey, x => x.RelativePath == "LLC_zh-CN/StoryData/S1.json" && x.KeyPath == "dataList/0/dialog");
        Assert.Contains(byKey, x => x.RelativePath == "LLC_zh-CN/AbDlg_Faust.json" && x.KeyPath == "dataList/1/dialog");

        // 值命中（读文件内容；snippet 截取匹配窗口）
        var byValue = _service.Search("浮士德", files).ToList();
        Assert.Single(byValue);
        var valueHit = byValue[0];
        Assert.Equal(LangTextSearchKind.Value, valueHit.Kind);
        Assert.Equal("LLC_zh-CN/AbDlg_Faust.json", valueHit.RelativePath);
        Assert.Equal("dataList/0/dialog", valueHit.KeyPath);
        Assert.Contains("浮士德", valueHit.Snippet);

        // 非 UTF-8 文件不参与键值搜索也不让搜索崩溃
        Assert.Empty(_service.Search("你", files));
        Assert.Empty(_service.Search("", files)); // 空查询直接空结果
    }

    // ── 编辑集 ───────────────────────────────────────────────────────

    [Fact]
    public void Edit_set_requires_enumerated_lang_root()
    {
        Assert.Throws<InvalidOperationException>(() => _service.BeginEdit("config.json"));
    }

    [Fact]
    public void Edit_set_begin_modify_revert_and_validation()
    {
        SeedLangRoot();
        _service.EnumerateFiles(LangRoot);
        const string rel = "LLC_zh-CN/AbDlg_Faust.json";
        var original = File.ReadAllText(Path.Combine(LangRoot, "LLC_zh-CN", "AbDlg_Faust.json"));

        var text = _service.BeginEdit(rel);
        Assert.Equal(original, text); // 首次 BeginEdit 返回原文（vanilla 快照）
        Assert.Equal(original, _service.TryGetVanillaText(rel));

        const string modified = """{"dataList":[{"id":1,"dialog":"改后文本"}],"newKey":true}""";
        _service.SetModified(rel, modified);
        Assert.True(_service.IsModified(rel));
        Assert.Equal(modified, _service.TryGetModifiedText(rel));

        // 重复 BeginEdit 不重置 vanilla 快照，返回当前修改文本
        Assert.Equal(modified, _service.BeginEdit(rel));
        Assert.Equal(original, _service.TryGetVanillaText(rel));

        // 非法 JSON 拒绝且编辑集状态不变
        Assert.Throws<InvalidDataException>(() => _service.SetModified(rel, "{ broken"));
        Assert.Equal(modified, _service.TryGetModifiedText(rel));

        // 未 BeginEdit 的文件不能 SetModified
        Assert.Throws<InvalidOperationException>(() => _service.SetModified("config.json", "{}"));

        // 还原单文件（内存编辑，lang 目录从未被改动）
        Assert.True(_service.Revert(rel));
        Assert.False(_service.IsModified(rel));
        Assert.Null(_service.TryGetModifiedText(rel));
        Assert.False(_service.Revert(rel)); // 再还原是无操作
    }

    [Fact]
    public void Unsafe_or_missing_relative_paths_fail_fast()
    {
        SeedLangRoot();
        _service.EnumerateFiles(LangRoot);
        Assert.Throws<InvalidDataException>(() => _service.BeginEdit("../outside.json"));
        Assert.Throws<InvalidDataException>(() => _service.BeginEdit("C:/abs/path.json"));
        Assert.Throws<InvalidDataException>(() => _service.BeginEdit("LLC_zh-CN//double.json"));
        Assert.Throws<FileNotFoundException>(() => _service.BeginEdit("LLC_zh-CN/missing.json"));
    }

    [Fact]
    public void BeginEdit_on_non_utf8_file_fails_without_guessing()
    {
        SeedLangRoot();
        _service.EnumerateFiles(LangRoot);
        var ex = Assert.Throws<InvalidDataException>(() => _service.BeginEdit("LLC_zh-CN/gbk.json"));
        Assert.Contains("UTF-8", ex.Message);
    }

    // ── 导出补丁与回放 ───────────────────────────────────────────────

    [Fact]
    public async Task Export_writes_lcta_compatible_patch_and_replays_to_modified()
    {
        SeedLangRoot();
        _service.EnumerateFiles(LangRoot);
        const string rel = "LLC_zh-CN/AbDlg_Faust.json";
        const string modifiedText = """
        {
          "dataList": [
            { "id": 1, "dialog": "模组文本一" },
            { "id": 2, "dialog": "原始文本二" }
          ]
        }
        """;
        _service.BeginEdit(rel);
        _service.SetModified(rel, modifiedText);

        // 无差异文件进编辑集但不进补丁
        const string unchangedRel = "LLC_zh-CN/StoryData/S1.json";
        _service.BeginEdit(unchangedRel);
        _service.SetModified(unchangedRel, File.ReadAllText(Path.Combine(LangRoot, "LLC_zh-CN", "StoryData", "S1.json")));

        var outputPath = Path.Combine(_work, "out", "mod-lang.json");
        var report = await _service.ExportPatchAsync(outputPath);

        Assert.Equal(2, report.EditedFileCount);
        Assert.Equal(1, report.PatchedFileCount);
        Assert.Equal(Path.GetFullPath(outputPath), report.OutputPath);
        Assert.True(File.Exists(report.OutputPath));

        var patched = report.Files.Where(x => x.OperationCount > 0).ToList();
        Assert.Single(patched);
        Assert.Equal(rel, patched[0].RelativePath);
        var skipped = report.Files.Single(x => x.OperationCount == 0);
        Assert.Equal(unchangedRel, skipped.RelativePath);
        Assert.Contains("无差异", skipped.Note);

        // 补丁文档：{"patchs": { "相对路径('/'分隔)": [RFC6902 ops…] }} —— 与 changes.py 语义一致
        var root = JsonNode.Parse(File.ReadAllText(outputPath))!;
        var patchs = root["patchs"]!.AsObject();
        var entry = Assert.Single(patchs);
        Assert.Equal(rel, entry.Key);
        var ops = entry.Value!.AsArray();
        Assert.All(ops, op =>
        {
            Assert.Equal("replace", op!["op"]!.GetValue<string>());
            Assert.StartsWith("/dataList", op["path"]!.GetValue<string>());
        });

        // 回放还原：vanilla 快照 + ops = 修改后内容（语义一致）
        var vanilla = JsonNode.Parse(_service.TryGetVanillaText(rel)!)!;
        var replayed = new TextDiffService().Apply(vanilla, ops);
        Assert.True(JsonNode.DeepEquals(replayed, JsonNode.Parse(modifiedText)), "补丁回放必须还原出修改后内容");

        // 真实加载器同一套读取语义：LangTextPatchService 能读回补丁文档
        var document = new LangTextPatchService().Read(outputPath);
        Assert.True(document.Patches.ContainsKey(rel));
    }

    [Fact]
    public void Export_with_empty_edit_set_writes_empty_patchs_document()
    {
        SeedLangRoot();
        _service.EnumerateFiles(LangRoot);
        var path = Path.Combine(_work, "empty-patch.json");
        var report = _service.ExportPatch(path);
        Assert.Equal(0, report.EditedFileCount);
        Assert.Equal(0, report.PatchedFileCount);
        Assert.True(LangTextPatchService.LooksLikeLangPatch(File.ReadAllText(path)));
    }

    // ── 真实 lang 目录门控测试（无游戏目录的机器自动跳过；预算 < 60 秒）──

    private static readonly string? RealLangDir = FindRealLangDir();

    private static string? FindRealLangDir()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company\LimbusCompany_Data\lang",
            @"E:\SteamLibrary\steamapps\common\Limbus Company\LimbusCompany_Data\lang",
        };
        var overrideGame = Environment.GetEnvironmentVariable("LME_GAME_DIR");
        if (!string.IsNullOrWhiteSpace(overrideGame))
            candidates = [Path.Combine(overrideGame, "LimbusCompany_Data", "lang"), .. candidates];
        return candidates.FirstOrDefault(Directory.Exists);
    }

    [Fact]
    public void Real_lang_directory_enumerates_active_language_with_story_data_and_key_counts()
    {
        if (RealLangDir is null) return;
        var service = new LangTextWorkbenchService();

        // 定位与活动语言（本机 config.json 指向 LLC_zh-CN）
        var gameDir = Path.GetFullPath(Path.Combine(RealLangDir, "..", ".."));
        Assert.Equal(Path.GetFullPath(RealLangDir), service.ResolveLangRoot(gameDir));
        Assert.Equal("LLC_zh-CN", service.ReadActiveLanguage(RealLangDir));

        var stopwatch = Stopwatch.StartNew();
        var files = service.EnumerateFiles(RealLangDir);
        stopwatch.Stop();

        var relatives = files.Select(x => x.RelativePath).ToList();
        Assert.Contains("config.json", relatives);
        Assert.All(relatives, x => Assert.DoesNotContain('\\', x));
        Assert.All(files, x => Assert.True(File.Exists(x.FullPath), $"枚举条目应真实存在: {x.RelativePath}"));

        // StoryData 子目录逐层展开（实测约 920 文件，留充足余量防游戏版本波动）
        var storyData = relatives.Where(x => x.StartsWith("LLC_zh-CN/StoryData/", StringComparison.Ordinal)).ToList();
        Assert.True(storyData.Count >= 100, $"StoryData 应包含大量文件，实际 {storyData.Count}");

        // 根级文件与子目录文件并存
        Assert.Contains(relatives, x => x.StartsWith("LLC_zh-CN/", StringComparison.Ordinal) && x.Count(c => c == '/') == 1);
        Assert.Contains(relatives, x => x.Count(c => c == '/') >= 2);

        // 抽样验证键数与大小（只读这一个文件的内容，不全量读）
        var sampleRel = relatives.FirstOrDefault(x => x == "LLC_zh-CN/AbDlg_Faust.json")
                        ?? relatives.First(x => x.StartsWith("LLC_zh-CN/", StringComparison.Ordinal) && x.Count(c => c == '/') == 1);
        var sample = files.Single(x => x.RelativePath == sampleRel);
        Assert.True(sample.IsUtf8, $"抽样文件应为 UTF-8: {sampleRel}");
        var sampleNode = JsonNode.Parse(File.ReadAllText(sample.FullPath));
        var expectedKeys = sampleNode is JsonObject obj ? obj.Count : 0;
        Assert.Equal(expectedKeys, sample.KeyCount);
        Assert.Equal(new FileInfo(sample.FullPath).Length, sample.SizeBytes);

        Assert.True(stopwatch.Elapsed.TotalSeconds < 60, $"枚举耗时 {stopwatch.Elapsed.TotalSeconds:F1}s，超出 60s 预算");
    }
}
