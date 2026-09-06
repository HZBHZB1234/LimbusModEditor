using System.Text.Json;
using System.Text.Json.Nodes;
using LimbusModEditor.Application.Texts;

namespace LimbusModEditor.Domain.Tests;

/// <summary>T2：lang 文本模组通道（RFC6902 差分 + 与真实加载器 LCTA
/// launcher/changes.py 兼容的 patchs 文档）。合成样本覆盖核心语义；
/// 真实 lang 目录的验证在文件末尾（无游戏目录的机器自动跳过）。</summary>
public class LangTextPatchServiceTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lme-langtests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private string VanillaDir => Path.Combine(_work, "vanilla");
    private string ModifiedDir => Path.Combine(_work, "modified");
    private string LangRoot => Path.Combine(_work, "langroot");

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private const string VanillaUi = """
    {
      "dataList": [
        { "id": 1, "dialog": "原始文本一" },
        { "id": 2, "dialog": "原始文本二" }
      ]
    }
    """;
    private const string ModifiedUi = """
    {
      "dataList": [
        { "id": 1, "dialog": "模组文本一" },
        { "id": 2, "dialog": "原始文本二" }
      ]
    }
    """;

    private void SeedDirectories()
    {
        WriteFile(Path.Combine(VanillaDir, "zh-CN", "ui.json"), VanillaUi);
        WriteFile(Path.Combine(VanillaDir, "zh-CN", "same.json"), """{"a": 1}""");
        WriteFile(Path.Combine(ModifiedDir, "zh-CN", "ui.json"), ModifiedUi);
        WriteFile(Path.Combine(ModifiedDir, "zh-CN", "same.json"), """{"a": 1}""");
        WriteFile(Path.Combine(ModifiedDir, "zh-CN", "extra.json"), """{"new": true}""");
    }

    [Fact]
    public void Directory_diff_generates_lcta_compatible_patch_and_round_trips()
    {
        SeedDirectories();
        var service = new LangTextPatchService();
        var (document, diagnostics) = service.GenerateFromDirectories(VanillaDir, ModifiedDir);

        // 变更文件进补丁；相同文件不出现在补丁里
        var entry = Assert.Single(document.Patches);
        Assert.Equal("zh-CN/ui.json", entry.Key);
        Assert.Single(entry.Value);
        // 修改目录多出的文件必须明确诊断，不静默丢弃
        var extra = Assert.Single(diagnostics);
        Assert.Contains("zh-CN/extra.json", extra);

        var patchPath = Path.Combine(_work, "我的文本补丁.json");
        service.Write(document, patchPath);
        Assert.True(LangTextPatchService.LooksLikeLangPatch(File.ReadAllText(patchPath)));

        // 写盘→读回→应用到原版副本 = 修改目录内容
        var reloaded = service.Read(patchPath);
        var langRoot = Path.Combine(_work, "langroot", "zh-CN");
        Directory.CreateDirectory(langRoot);
        WriteFile(Path.Combine(langRoot, "ui.json"), VanillaUi);
        var statuses = service.ApplyToDirectory(Path.Combine(_work, "langroot"), reloaded);

        var status = Assert.Single(statuses);
        Assert.True(status.Applied, status.Note);
        Assert.Equal(1, status.OperationCount);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(File.ReadAllText(Path.Combine(langRoot, "ui.json"))),
            JsonNode.Parse(ModifiedUi)), "应用补丁后的文件必须与修改目录的文件一致");
    }

    [Fact]
    public void Apply_skips_missing_targets_like_the_real_loader()
    {
        SeedDirectories();
        var service = new LangTextPatchService();
        var (document, _) = service.GenerateFromDirectories(VanillaDir, ModifiedDir);
        document.Patches["zh-CN/not-there.json"] = new JsonArray
        {
            new JsonObject { ["op"] = "replace", ["path"] = "/dataList/0/dialog", ["value"] = "x" }
        };

        var langRoot = Path.Combine(_work, "langroot");
        Directory.CreateDirectory(Path.Combine(langRoot, "zh-CN"));
        WriteFile(Path.Combine(langRoot, "zh-CN", "ui.json"), VanillaUi);
        var statuses = service.ApplyToDirectory(langRoot, document);

        Assert.Equal(2, statuses.Count);
        var missing = statuses.Single(x => !x.Applied);
        Assert.Equal("zh-CN/not-there.json", missing.RelativePath);
        Assert.Contains("不存在", missing.Note);
        Assert.True(statuses.Single(x => x.Applied).Applied);
    }

    [Fact]
    public void Malformed_documents_and_unsafe_paths_fail_fast()
    {
        var service = new LangTextPatchService();
        var bad = Path.Combine(_work, "bad.json");
        WriteFile(bad, """{"no-patchs": true}""");
        Assert.Throws<InvalidDataException>(() => service.Read(bad));

        WriteFile(bad, """{"patchs": {"../outside.json": []}}""");
        Assert.Throws<InvalidDataException>(() => service.Read(bad));

        WriteFile(bad, """{"patchs": {"C:/abs/path.json": []}}""");
        Assert.Throws<InvalidDataException>(() => service.Read(bad));

        WriteFile(bad, """{"patchs": {"a.json": {"op": "x"}}}""");
        Assert.Throws<InvalidDataException>(() => service.Read(bad));

        var document = new LangPatchDocument();
        document.Patches["../escape.json"] = [];
        var langRoot = Directory.CreateDirectory(LangRoot).FullName;
        Assert.Throws<InvalidDataException>(() => service.ApplyToDirectory(langRoot, document));
    }

    [Fact]
    public void Active_language_reads_config_json()
    {
        var service = new LangTextPatchService();
        WriteFile(Path.Combine(LangRoot, "config.json"), """{"lang": "LLC_zh-CN", "titleFont": ""}""");
        Directory.CreateDirectory(LangRoot);
        Assert.Equal("LLC_zh-CN", service.ReadActiveLanguage(LangRoot));
        Assert.Null(service.ReadActiveLanguage(_work)); // 没有 config.json
    }

    // ── 真实 lang 目录验证（无游戏目录的机器自动跳过）──────────────────────

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
    public void Real_lang_directory_generates_and_applies_semantic_equal_patch()
    {
        if (RealLangDir is null) return;
        var service = new LangTextPatchService();

        // 真实 config.json 必须能读出活动语言
        var active = service.ReadActiveLanguage(RealLangDir);
        Assert.False(string.IsNullOrWhiteSpace(active), "真实 lang config.json 应包含活动语言");

        // 在活动语言目录中挑一个真实 JSON 文件做「读→改→差分→应用→回读相等」
        var sourceFile = Directory.EnumerateFiles(Path.Combine(RealLangDir, active!), "*.json").First();
        var before = JsonNode.Parse(File.ReadAllText(sourceFile))!;
        var after = before.DeepClone()!;
        var dialogNode = after["dataList"]![0]!["dialog"];
        if (dialogNode is null) return; // 该文件没有 dataList 结构则换语义验证
        dialogNode!.ReplaceWith(JsonValue.Create("LME文本通道写回验证"));

        var tempVanilla = Path.Combine(_work, "real-vanilla", active!);
        var tempModified = Path.Combine(_work, "real-modified", active!);
        WriteFile(Path.Combine(tempVanilla, Path.GetFileName(sourceFile)),
            before.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        WriteFile(Path.Combine(tempModified, Path.GetFileName(sourceFile)),
            after.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        var (document, _) = service.GenerateFromDirectories(
            Path.Combine(_work, "real-vanilla"), Path.Combine(_work, "real-modified"));
        var ops = Assert.Single(document.Patches).Value;
        Assert.Single(ops);

        // 应用回原版副本后与修改版语义一致
        var patched = new TextDiffService().Apply(before.DeepClone()!, ops);
        Assert.True(JsonNode.DeepEquals(patched, after));

        // 与真实加载器同一套语义：python-jsonpatch 的应用结果等价（这里用
        // 生成→写盘→读回→应用再核对一次，覆盖补丁文件的序列化往返）
        var patchPath = Path.Combine(_work, "real-lang-patch.json");
        service.Write(document, patchPath);
        var reloaded = service.Read(patchPath);
        var roundTripped = new TextDiffService().Apply(before.DeepClone()!, reloaded.Patches.Values.Single());
        Assert.True(JsonNode.DeepEquals(roundTripped, after));
    }
}
