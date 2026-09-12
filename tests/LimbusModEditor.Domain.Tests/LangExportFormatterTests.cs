using System.Text.Json.Nodes;
using LimbusModEditor.Application.Build;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Formats;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// plan-16 S5：语言文本的**多格式导出**（bus / patch / pathset）。
///
/// <para>核心不是「写出了 JSON」，而是「写出的 JSON 按加载器语义回放能得到修改后的文档」——
/// 每个格式都做一次<b>往返验证</b>：
/// ① patch：RFC6902 直接应用（加载器 <c>changes.py</c> + jsonpatch）；
/// ② pathset：<c>StaticModService.PathsetToJsonPatch</c>（与加载器 <c>_pathset_to_jsonpatch</c>
///    同语义）转成操作后应用；
/// ③ bus：按 <c>webutils/fancy/bus.py</c> 的规则语义（<c>path</c> 解析 + <c>replacements[set]</c> 赋值）
///    在本测试里实现等价解释器后应用 —— 这是「规则集真的能改出目标文档」的唯一证据。</para>
/// </summary>
public sealed class LangExportFormatterTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lme-langexport-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
        }
        catch (Exception) { /* 临时目录清理失败不影响结论 */ }
    }

    private const string Vanilla = """
    {
      "dataList": [
        { "id": 1, "dialog": "原始文本一", "speaker": "Faust" },
        { "id": 2, "dialog": "原始文本二", "speaker": "Outis" }
      ],
      "meta": { "version": 3 }
    }
    """;

    private const string Modified = """
    {
      "dataList": [
        { "id": 1, "dialog": "改后文本一", "speaker": "Faust" },
        { "id": 2, "dialog": "改后文本二", "speaker": "Outis" },
        { "id": 3, "dialog": "新增条目", "speaker": "Dante" }
      ],
      "meta": { "version": 4 }
    }
    """;

    /// <summary>只改「已存在的字段值」的修改（不动数组长度、不删键）——bus / pathset 能无损表达的形态。</summary>
    private const string ValueOnlyModified = """
    {
      "dataList": [
        { "id": 1, "dialog": "改后文本一", "speaker": "Faust" },
        { "id": 2, "dialog": "原始文本二", "speaker": "Outis" }
      ],
      "meta": { "version": 4 }
    }
    """;

    private static LangFormatResult Build(LangExportFormat format, string vanilla = Vanilla, string modified = Modified)
        => LangExportFormatter
            .Format("StoryData/S1.json", "LLc-CN-LCTA/StoryData/S1.json", "环指回调", vanilla, modified)
            .Single(x => x.Format == format);

    // ── patch（RFC6902）──────────────────────────────────────────────

    [Fact]
    public void Patch_round_trips_to_the_modified_document()
    {
        var result = Build(LangExportFormat.Patch);
        Assert.True(result.Written);

        var document = JsonNode.Parse(result.JsonText!)!;
        var patchs = document["patchs"]!.AsObject();
        // 键 = 加载器口径（相对 lang 根，含语言目录那一层）；加载器按它定位文件。
        var entry = Assert.Single(patchs);
        Assert.Equal("LLc-CN-LCTA/StoryData/S1.json", entry.Key);

        var replayed = new TextDiffService().Apply(JsonNode.Parse(Vanilla)!, entry.Value!.AsArray());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Modified), replayed), "patch 回放必须得到修改后的文档");
    }

    // ── pathset（与加载器 _pathset_to_jsonpatch 同语义）──────────────

    [Fact]
    public void Pathset_round_trips_to_the_modified_document()
    {
        var result = Build(LangExportFormat.Pathset, Vanilla, ValueOnlyModified);
        Assert.True(result.Written);

        var document = JsonNode.Parse(result.JsonText!)!;
        Assert.Equal(LangExportFormatter.PathsetFormat, document["format"]!.GetValue<string>());
        Assert.Equal("S1.json", document["files"]!.AsArray()[0]!.GetValue<string>());

        var ops = new StaticModService().PathsetToJsonPatch(document["pathset"]!.AsObject());
        var replayed = new TextDiffService().Apply(JsonNode.Parse(Vanilla)!, ops);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ValueOnlyModified), replayed), "pathset 回放必须得到修改后的文档");
    }

    /// <summary>数组长度变化 / 删键：pathset（与 bus）只能表达「按路径赋值」，这类改动明确跳过并说明原因。</summary>
    [Fact]
    public void Pathset_skips_structural_changes_with_a_reason()
    {
        var result = Build(LangExportFormat.Pathset); // Modified 多了一条数组元素
        Assert.False(result.Written);
        Assert.Contains(result.Notes, x => x.Contains("未生成覆盖清单", StringComparison.Ordinal));

        // 但 patch（RFC6902）能完整表达同一个改动。
        var patch = Build(LangExportFormat.Patch);
        Assert.True(patch.Written);
    }

    // ── bus（lcta-bus 规则集）────────────────────────────────────────

    [Fact]
    public void Bus_ruleset_round_trips_to_the_modified_document()
    {
        var result = Build(LangExportFormat.Bus, Vanilla, ValueOnlyModified);
        Assert.True(result.Written);

        var document = JsonNode.Parse(result.JsonText!)!;
        Assert.Equal(LangExportFormatter.BusFormat, document["format"]!.GetValue<string>());
        Assert.Equal(LangExportFormatter.BusVersion, document["version"]!.GetValue<int>());
        Assert.Equal("S1.json", document["files"]!.AsArray()[0]!.GetValue<string>());

        // 加载器侧的等价解释器（bus.py: resolve(path) + set value）。
        var replayed = ApplyBus(JsonNode.Parse(Vanilla)!, document["rules"]!.AsArray());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ValueOnlyModified), replayed), "bus 规则集回放必须得到修改后的文档");

        // 每条规则都是「按路径赋值」，且路径是加载器认得的 bus 语法。
        foreach (var rule in document["rules"]!.AsArray())
        {
            Assert.NotNull(rule!["path"]);
            Assert.Single(rule["replacements"]!.AsArray());
            Assert.NotNull(rule["replacements"]!.AsArray()[0]!["set"]);
        }
    }

    /// <summary>
    /// 数组长度变化：bus / pathset 只能「按路径赋值 / 覆盖已存在的位置」，表达不了数组增删 →
    /// **整份不产出**（半份规则集会让用户以为全改了）。
    /// </summary>
    [Fact]
    public void Bus_skips_array_length_changes_with_a_reason()
    {
        var result = Build(LangExportFormat.Bus); // Modified 多了一条数组元素
        Assert.False(result.Written, $"notes={string.Join("|", result.Notes)}；bus={result.JsonText}");
        Assert.Contains(result.Notes, x => x.Contains("未生成规则集", StringComparison.Ordinal));
    }

    [Fact]
    public void Bus_and_pathset_paths_are_validated_against_the_loader_grammar()
    {
        Assert.True(LangExportFormatter.TryToBusPath("/dataList/1/dialog", out var bus, out _));
        Assert.Equal("dataList[1].dialog", bus);
        Assert.True(LangExportFormatter.TryToBusPath("/meta/version", out var flat, out _));
        Assert.Equal("meta.version", flat);

        // 数组追加（-）：无法定位到具体元素 → bus 表示不了
        Assert.False(LangExportFormatter.TryToBusPath("/dataList/-", out _, out var appendReason));
        Assert.Contains("-", appendReason, StringComparison.Ordinal);
        // 含连字符的键：bus 路径语法表示不了（加载器按 . 分段）
        Assert.False(LangExportFormatter.TryToBusPath("/a-b/c", out _, out var dashReason));
        Assert.Contains("a-b", dashReason, StringComparison.Ordinal);

        Assert.True(LangExportFormatter.TryToPathsetPath("/dataList/0/id", out var pathset, out _));
        Assert.Equal("dataList[0].id", pathset);
    }

    // ── 可表达性门（§3.4）：删除 / 数组增删 ──────────────────────────

    [Fact]
    public void Deletion_skips_only_the_bus_format_and_explains_why()
    {
        const string withKey = """{"dataList":[{"id":1,"dialog":"a"}],"extra":true}""";
        const string withoutKey = """{"dataList":[{"id":1,"dialog":"a"}]}""";

        var bus = LangExportFormatter
            .Format("a.json", "LLc-CN-LCTA/a.json", "M", withKey, withoutKey)
            .Single(x => x.Format == LangExportFormat.Bus);
        var patch = LangExportFormatter
            .Format("a.json", "LLc-CN-LCTA/a.json", "M", withKey, withoutKey)
            .Single(x => x.Format == LangExportFormat.Patch);

        Assert.False(bus.Written);
        Assert.Contains(bus.Notes, x => x.Contains("bus", StringComparison.OrdinalIgnoreCase) || x.Contains("patch", StringComparison.Ordinal));
        // patch 照常产出，且回放正确
        Assert.True(patch.Written);
        var replayed = new TextDiffService().Apply(
            JsonNode.Parse(withKey)!, JsonNode.Parse(patch.JsonText!)!["patchs"]!["LLc-CN-LCTA/a.json"]!.AsArray());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(withoutKey), replayed));
    }

    [Fact]
    public void Identical_documents_produce_no_output_for_any_format()
    {
        foreach (var format in Enum.GetValues<LangExportFormat>())
        {
            var result = Build(format, Vanilla, Vanilla);
            Assert.False(result.Written, $"{format} 在无差异时不该产出");
            Assert.NotEmpty(result.Notes);
        }
    }

    // ── 端到端：三个槽位都落到 <项目名>_text/<格式>/<条目路径> ─────────

    [Fact]
    public async Task Export_writes_three_text_slots_under_the_mod_text_folder()
    {
        var langRoot = Path.Combine(_work, "LimbusCompany_Data", "lang");
        var languageDirectory = Path.Combine(langRoot, "LLc-CN-LCTA", "StoryData");
        Directory.CreateDirectory(languageDirectory);
        File.WriteAllText(Path.Combine(langRoot, "config.json"), """{"lang":"LLc-CN-LCTA"}""");
        File.WriteAllText(Path.Combine(languageDirectory, "S1.json"), Vanilla);

        var session = new LangEditSession();
        session.AttachLangRoot(langRoot);
        session.BeginEdit("StoryData/S1.json");
        // 只改已存在的字段值：三个格式都能无损表达（结构变化时只有 patch 能表达，见上一条测试）。
        session.SetModified("StoryData/S1.json", ValueOnlyModified);

        var project = new LimbusModEditor.Domain.Projects.ModProject { Name = "Mod" };
        var plan = new ModExportPlanService().Plan(project, _work, session, new StaticEditSession());
        var result = await new ModPackExportService().ExportAsync(project, _work, plan, new ModExportPlanContext());

        foreach (var slot in new[] { ExportSlot.LangBus, ExportSlot.LangPatch, ExportSlot.LangPathset })
        {
            var folder = slot switch
            {
                ExportSlot.LangBus => "bus",
                ExportSlot.LangPatch => "patch",
                _ => "pathset",
            };
            var written = result.Slots.Single(x => x.Descriptor.Slot == slot);
            Assert.True(written.Written, $"{slot} 应写出：{string.Join("；", written.Diagnostics)}");
            var file = Assert.Single(written.OutputPaths);
            // 产物沿用条目路径（子目录隔离），落在 <项目名>_text/<格式>/ 下
            Assert.Equal(Path.Combine(_work, "Mod_text", folder, "StoryData", "S1.json"), file);
            Assert.True(File.Exists(file));
            var json = JsonNode.Parse(File.ReadAllText(file));
            Assert.NotNull(json);
        }
    }

    // ── 加载器 bus 语义的等价解释器（仅测试用，只实现导出用到的子集）──

    private static JsonNode ApplyBus(JsonNode document, JsonArray rules)
    {
        foreach (var rule in rules)
        {
            var path = rule!["path"]!.GetValue<string>();
            var value = rule["replacements"]!.AsArray()[0]!["set"];
            ApplyBusRule(document, path, value);
        }
        return document;
    }

    /// <summary>
    /// 按 <c>bus.py</c> 的 <c>parse_bus_path</c> + <c>_resolve_paths(allow_missing_final=true)</c>
    /// + <c>_set_value</c> 的语义解析并赋值：键段直接取，<c>[n]</c> 取数组下标，
    /// 末段不存在时创建（这正是导出规则集依赖的 <c>allow_missing_final</c> 行为）。
    /// </summary>
    private static void ApplyBusRule(JsonNode document, string path, JsonNode? value)
    {
        var tokens = new List<(string? Key, int? Index)>();
        foreach (var segment in path.Split('.'))
        {
            var keyMatch = System.Text.RegularExpressions.Regex.Match(segment, "^[^\\[\\]]+");
            Assert.True(keyMatch.Success, $"bus 路径段非法: {segment}");
            var position = keyMatch.Length;
            tokens.Add((keyMatch.Value, null));
            while (position < segment.Length)
            {
                var bracket = System.Text.RegularExpressions.Regex.Match(segment[position..], "^\\[(\\d+)\\]");
                Assert.True(bracket.Success, $"bus 路径下标非法: {segment}");
                tokens.Add((null, int.Parse(bracket.Groups[1].Value)));
                position += bracket.Length;
            }
        }

        JsonNode current = document;
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var (key, index) = tokens[i];
            current = key is not null
                ? current.AsObject()[key] ?? throw new InvalidOperationException($"bus 路径中间键不存在: {path}")
                : current.AsArray()[index!.Value] ?? throw new InvalidOperationException($"bus 路径中间下标越界: {path}");
        }
        var last = tokens[^1];
        if (last.Key is { } finalKey)
        {
            current.AsObject()[finalKey] = value?.DeepClone();
            return;
        }
        current.AsArray()[last.Index!.Value] = value?.DeepClone();
    }
}
