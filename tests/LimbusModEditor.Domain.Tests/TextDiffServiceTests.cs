using System.Text.Json;
using System.Text.Json.Nodes;
using LimbusModEditor.Application.Texts;

namespace LimbusModEditor.Domain.Tests;

/// <summary>T4：RFC6902 生成/应用的核心语义（供 lang 文本模组通道使用）。
/// 重点：生成 → 应用 → 回读相等，以及非法补丁一律 fail fast 中文报错。</summary>
public class TextDiffServiceTests
{
    private static JsonNode? Parse(string json) => JsonNode.Parse(json);
    private static TextDiffService NewService() => new();

    [Fact]
    public void Generated_patch_round_trips_nested_documents()
    {
        var before = Parse("""
        {
          "dataList": [
            { "id": 36, "teller": "堂吉诃德", "dialog": "旧台词" },
            { "id": 37, "teller": "堂吉诃德", "dialog": "保留台词" }
          ],
          "meta": { "version": 1, "unused": true }
        }
        """);
        var after = Parse("""
        {
          "dataList": [
            { "id": 36, "teller": "堂吉诃德", "dialog": "新台词" },
            { "id": 37, "teller": "堂吉诃德", "dialog": "保留台词" }
          ],
          "meta": { "version": 2, "extra": "added" }
        }
        """);

        var service = NewService();
        var patch = service.Generate(before, after);
        Assert.NotEmpty(patch);
        Assert.Equal(4, patch.Count); // dialog replace / unused remove / version replace / extra add

        var patched = service.Apply(before!.DeepClone()!, patch);
        Assert.True(JsonNode.DeepEquals(patched, after),
            "生成→应用后的文档必须与目标文档完全一致");
    }

    [Fact]
    public void Array_edits_keep_prefix_and_generate_minimal_ops()
    {
        var before = Parse("""{"dataList":[{"k":1},{"k":2},{"k":3},{"k":4}]}""");
        var after = Parse("""{"dataList":[{"k":1},{"k":9},{"k":3},{"k":5}]}""");

        var patch = NewService().Generate(before, after);
        Assert.Equal(2, patch.Count);
        Assert.All(patch, op => Assert.Equal("replace", op!["op"]!.GetValue<string>()));
        Assert.Equal("/dataList/1/k", patch[0]!["path"]!.GetValue<string>());
        Assert.Equal("/dataList/3/k", patch[1]!["path"]!.GetValue<string>());

        var patched = NewService().Apply(before!.DeepClone()!, patch);
        Assert.True(JsonNode.DeepEquals(patched, after));
    }

    [Fact]
    public void Array_insertion_and_removal_use_add_remove_with_valid_indices()
    {
        var before = Parse("""{"list":[1,2,3]}""");
        var after = Parse("""{"list":[1,9,2,3]}""");
        var service = NewService();
        var patch = service.Generate(before, after);
        var patched = service.Apply(before!.DeepClone()!, patch);
        Assert.True(JsonNode.DeepEquals(patched, after));

        // 删除中段元素：remove 按降序下标生成
        var removed = service.Generate(after, Parse("""{"list":[1,9,3]}"""));
        var removedResult = service.Apply(after!.DeepClone()!, removed);
        Assert.True(JsonNode.DeepEquals(removedResult, Parse("""{"list":[1,9,3]}""")));
    }

    [Fact]
    public void Pointer_escapes_and_append_marker_are_handled()
    {
        var before = Parse("""{"a/b": 1, "c~d": {"deep": true}}""");
        var after = Parse("""{"a/b": 2, "c~d": {"deep": true}, "appended": [4]}""");

        var service = NewService();
        var patch = service.Generate(before, after);
        var patched = service.Apply(before!.DeepClone()!, patch);
        Assert.True(JsonNode.DeepEquals(patched, after));

        // "-" 追加语义（真实加载器的 python-jsonpatch 支持的写法）
        var manual = Parse("""
        [ { "op": "add", "path": "/appended/-", "value": 5 } ]
        """);
        var appended = service.Apply(after!.DeepClone()!, manual!.AsArray());
        Assert.Equal(2, appended["appended"]!.AsArray().Count);
        Assert.Equal(5, appended["appended"]![1]!.GetValue<int>());
    }

    [Fact]
    public void Manual_move_copy_test_ops_apply_correctly()
    {
        var document = Parse("""{"a": {"x": 1}, "b": [1,2,3]}""");
        var patch = Parse("""
        [
          { "op": "test", "path": "/a/x", "value": 1 },
          { "op": "copy", "from": "/a/x", "path": "/copied" },
          { "op": "move", "from": "/b/0", "path": "/moved" }
        ]
        """)!.AsArray();
        var result = NewService().Apply(document!, patch);
        Assert.Equal(1, result["copied"]!.GetValue<int>());
        Assert.Equal(1, result["moved"]!.GetValue<int>());
        Assert.Equal(2, result["b"]![0]!.GetValue<int>());
    }

    [Fact]
    public void Root_replace_and_type_change_round_trip()
    {
        var service = NewService();
        var before = Parse("""{"a": 1}""");
        var after = Parse("""[1,2]""");
        var patch = service.Generate(before, after);
        var patched = service.Apply(before!.DeepClone()!, patch);
        Assert.True(JsonNode.DeepEquals(patched, after));
    }

    public static IEnumerable<object[]> MalformedPatches => new[]
    {
        ("未知 op", """[ { "op": "frobnicate", "path": "/a" } ]"""),
        ("缺 path", """[ { "op": "remove" } ]"""),
        ("路径越界", """[ { "op": "remove", "path": "/list/9" } ]"""),
        ("中间节点缺失", """[ { "op": "remove", "path": "/nope/deeper" } ]"""),
        ("replace 不创建新键", """[ { "op": "replace", "path": "/missing", "value": 1 } ]"""),
        ("test 失败", """[ { "op": "test", "path": "/a", "value": 2 } ]"""),
        ("move 进自身", """[ { "op": "move", "from": "/a", "path": "/a/b" } ]"""),
        ("非法 Pointer", """[ { "op": "remove", "path": "a" } ]"""),
        ("add 越界下标", """[ { "op": "add", "path": "/list/9", "value": 1 } ]"""),
    }.Select(x => new object[] { x.Item1, x.Item2 });

    [Theory]
    [MemberData(nameof(MalformedPatches))]
    public void Malformed_patches_fail_fast_with_chinese_errors(string _, string patchJson)
    {
        var document = Parse("""{"a": {"x": 1}, "b": [1,2,3], "list": [0]}""")!;
        var patch = Parse(patchJson)!.AsArray();
        var ex = Assert.Throws<InvalidDataException>(() => NewService().Apply(document, patch));
        Assert.True(
            ex.Message.Contains("补丁") || ex.Message.Contains("路径") || ex.Message.Contains("越界") || ex.Message.Contains("不存在"),
            ex.Message);
    }

    [Fact]
    public void Empty_difference_produces_empty_patch()
    {
        var document = Parse("""{"a": [1, {"b": "文"}]}""")!;
        var patch = NewService().Generate(document, document.DeepClone());
        Assert.Empty(patch);
    }

    [Fact]
    public void Patch_generation_survives_json_options_round_trip()
    {
        // 补丁会经 JSON 文本往返（写盘→读回），语义必须保持
        var before = Parse("""{"dataList":[{"dialog":"旧"}]}""")!;
        var after = Parse("""{"dataList":[{"dialog":"新"}]}""")!;
        var service = NewService();
        var patch = service.Generate(before, after);
        var text = patch.ToJsonString();
        var reparsed = JsonNode.Parse(text)!.AsArray();
        var patched = service.Apply(before.DeepClone()!, reparsed);
        Assert.True(JsonNode.DeepEquals(patched, after));
    }

    // ── plan-09 修复：JSON null 值的生成语义 ─────────────────────────

    [Fact]
    public void Identical_documents_with_null_values_produce_empty_patch()
    {
        // 修复前：null 值会被当成「before 为 null → add」，未修改的文档也产出
        // 一条缺 value 的非法 add（既误报差异，又让补丁回放直接失败）。
        var before = Parse("""{"a":null,"nested":{"b":null},"arr":[null,1]}""")!;
        var patch = NewService().Generate(before, before.DeepClone());
        Assert.Empty(patch);

        // 真实 lang 表常见的 null 值也走同一路径
        var lang = Parse("""{"dataList":[{"dialog":"台词","speaker":null}]}""")!;
        Assert.Empty(NewService().Generate(lang, lang.DeepClone()));
    }

    [Fact]
    public void Null_valued_keys_add_replace_and_round_trip()
    {
        var service = NewService();

        // 新增一个值为 null 的键 → 必须产出**带 value 的**合法 add
        var before = Parse("""{"a":1}""")!;
        var after = Parse("""{"a":1,"b":null}""")!;
        var patch = service.Generate(before, after);
        Assert.Single(patch);
        Assert.Equal("add", patch[0]!["op"]!.GetValue<string>());
        Assert.True(patch[0]!.AsObject().ContainsKey("value"), "add 必须显式带 value（JSON null 也要带）");
        Assert.True(JsonNode.DeepEquals(service.Apply(before.DeepClone()!, patch), after));

        // 把有值改成 null → replace，且回放一致
        var replaced = service.Generate(Parse("""{"a":1}"""), Parse("""{"a":null}"""));
        Assert.Single(replaced);
        Assert.Equal("replace", replaced[0]!["op"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(
            service.Apply(Parse("""{"a":1}""")!, replaced), Parse("""{"a":null}""")));

        // 数组里插入 null / 删除 null
        var arrayBefore = Parse("""[1,null,2]""")!;
        var arrayAfter = Parse("""[1,null,2,null]""")!;
        var arrayPatch = service.Generate(arrayBefore, arrayAfter);
        Assert.True(JsonNode.DeepEquals(service.Apply(arrayBefore.DeepClone()!, arrayPatch), arrayAfter));
    }
}
