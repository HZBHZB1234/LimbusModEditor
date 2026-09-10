using System.Text.Json.Nodes;
using LimbusModEditor.Application.Documents;

namespace LimbusModEditor.Domain.Tests;

/// <summary>plan-09：共享 JSON 编辑内核（JsonTreeEditor 的可测部分）——
/// 类型写回、非法 JSON 拒绝、展平/树枚举、删除键、差异摘要。
/// 提取自 TextWorkbenchPage/StaticWorkbenchPage 的既有行为，故这些断言同时是
/// 「提取前后行为一致」的回归门。</summary>
public class JsonDocumentEditorTests
{
    private static JsonNode Doc(string json) => JsonDocumentEditor.ParseStrict(json);

    private static JsonEditRow Leaf(JsonNode document, string path)
        => JsonDocumentEditor.FlattenLeaves(document).Single(x => x.Path == path);

    // ── 解析 / 非法 JSON 拒绝 ────────────────────────────────────────

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"a\":1,}")]
    [InlineData("null")]          // 根为 JSON null：无法呈现树，按空文档拒绝
    public void Invalid_json_is_rejected(string text)
    {
        var error = Assert.Throws<InvalidDataException>(() => JsonDocumentEditor.ParseStrict(text));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void Try_parse_reports_chinese_error_without_throwing()
    {
        Assert.False(JsonDocumentEditor.TryParse("{ not json", out var document, out var error));
        Assert.Null(document);
        Assert.NotNull(error);
        Assert.Contains("JSON 非法", error);

        Assert.True(JsonDocumentEditor.TryParse("{\"a\":1}", out var ok, out var none));
        Assert.NotNull(ok);
        Assert.Null(none);
    }

    [Fact]
    public void Serialize_round_trips_through_parse()
    {
        var document = Doc("{\"a\":[1,2,{\"b\":\"x\"}],\"c\":null}");
        var text = JsonDocumentEditor.Serialize(document);
        Assert.True(JsonNode.DeepEquals(document, JsonDocumentEditor.ParseStrict(text)));
        Assert.Equal("null", JsonDocumentEditor.Serialize(null));
    }

    // ── 展平 / 树枚举 ───────────────────────────────────────────────

    [Fact]
    public void Flatten_leaf_paths_match_legacy_conventions()
    {
        // 对象子：a/b；数组子：a/0；根数组：/0（旧 Flatten 的拼接口径）
        var document = Doc("{\"a\":{\"b\":1},\"arr\":[{\"c\":2}],\"s\":\"t\"}");
        var paths = JsonDocumentEditor.FlattenLeaves(document).Select(x => x.Path).ToList();
        Assert.Equal(new[] { "a/b", "arr/0/c", "s" }, paths);

        var rootArray = JsonDocumentEditor.FlattenLeaves(Doc("[{\"c\":2}]"));
        Assert.Equal(new[] { "/0/c" }, rootArray.Select(x => x.Path));
    }

    [Fact]
    public void Flatten_leaves_has_no_row_cap()
    {
        // 旧实现 rows.Count > 5000 直接 return：6000 个元素时后半截不可见。
        var array = new JsonArray();
        for (var i = 0; i < 6000; i++) array.Add(i);
        var document = new JsonObject { ["list"] = array };

        var rows = JsonDocumentEditor.FlattenLeaves(document);
        Assert.Equal(6000, rows.Count);
        Assert.Equal("list/5999", rows[^1].Path);
        Assert.Equal(5999, rows[^1].Node!.GetValue<int>());
    }

    [Fact]
    public void Enumerate_children_is_one_level_only()
    {
        var document = Doc("{\"obj\":{\"k\":1},\"arr\":[7,8]}");
        var root = JsonDocumentEditor.CreateRoot(document);
        Assert.Equal(0, root.Depth);
        Assert.Equal(string.Empty, root.Path);
        Assert.True(root.IsContainer);

        var level1 = JsonDocumentEditor.EnumerateChildren(root);
        Assert.Equal(new[] { "obj", "arr" }, level1.Select(x => x.Name));
        Assert.All(level1, x => Assert.Equal(1, x.Depth));
        Assert.Equal("obj", level1[0].Key);
        Assert.Equal(-1, level1[0].Index);
        Assert.True(level1[0].IsContainer);

        // 数组子项：Key 为 null、Index 有值、名字是下标
        var items = JsonDocumentEditor.EnumerateChildren(level1[1]);
        Assert.Equal(new[] { "0", "1" }, items.Select(x => x.Name));
        Assert.Equal(new[] { "arr/0", "arr/1" }, items.Select(x => x.Path));
        Assert.Null(items[0].Key);
        Assert.Equal(0, items[0].Index);
        Assert.True(items[0].IsLeaf);

        // 叶子没有子项（惰性展开到叶子即止）
        Assert.Empty(JsonDocumentEditor.EnumerateChildren(items[0]));
    }

    [Fact]
    public void Json_null_values_are_listed_as_leaf_rows()
    {
        var document = Doc("{\"n\":null}");
        var row = Leaf(document, "n");
        Assert.True(row.IsLeaf);
        Assert.Equal(JsonValueKind.Null, row.Kind);
        Assert.Equal("null", row.Preview);
        Assert.Equal("null", JsonDocumentEditor.LeafText(row.Node));
        // 容器行只是容器，不算叶子
        Assert.True(JsonDocumentEditor.CreateRoot(document).IsContainer);
        Assert.False(JsonDocumentEditor.CreateRoot(document).IsLeaf);
    }

    [Fact]
    public void Preview_shows_container_counts_and_truncates_long_text()
    {
        var document = Doc("{\"o\":{\"a\":1,\"b\":2},\"arr\":[1],\"long\":\"" + new string('x', 300) + "\"}");
        Assert.Equal("{ 2 个键 }", JsonDocumentEditor.Preview(document["o"]));
        Assert.Equal("[ 1 项 ]", JsonDocumentEditor.Preview(document["arr"]));
        var preview = JsonDocumentEditor.Preview(document["long"]);
        Assert.Equal(JsonDocumentEditor.PreviewLength + 1, preview.Length); // 截断 + 省略号
        Assert.EndsWith("…", preview);
        Assert.Equal("null", JsonDocumentEditor.Preview(null));
    }

    [Fact]
    public void Initial_editor_text_never_dumps_a_whole_subtree()
    {
        var document = Doc("{\"obj\":{\"a\":1},\"s\":\"文本\",\"n\":null}");

        // 叶子：字符串取原文、null 取 "null"
        Assert.Equal("文本", JsonDocumentEditor.InitialEditorText(Leaf(document, "s")));
        Assert.Equal("null", JsonDocumentEditor.InitialEditorText(Leaf(document, "n")));

        // 容器：只给短预览（旧实现把整棵子树 ToJsonString 塞进文本框，MB 级表会冻住 UI）
        var container = JsonDocumentEditor.EnumerateChildren(JsonDocumentEditor.CreateRoot(document))
            .Single(x => x.Name == "obj");
        Assert.Equal("{ 1 个键 }", JsonDocumentEditor.InitialEditorText(container));
    }

    [Fact]
    public void Count_nodes_is_bounded()
    {
        var array = new JsonArray();
        for (var i = 0; i < 5000; i++) array.Add(i);
        var document = new JsonObject { ["list"] = array, ["n"] = null };

        // 5000 个元素 + 1 个 null + list + 根 = 5003
        Assert.Equal(5003, JsonDocumentEditor.CountNodes(document));
        // 有上限：显示「至少 N 个」时不必把 MB 级文档全量走一遍
        Assert.Equal(10, JsonDocumentEditor.CountNodes(document, limit: 10));
        Assert.Equal(0, JsonDocumentEditor.CountNodes(null));
    }

    // ── 值编辑：按原值类型写回 ───────────────────────────────────────

    [Fact]
    public void Set_leaf_text_writes_back_original_types()
    {
        var document = Doc("{\"s\":\"旧\",\"i\":3,\"f\":1.5,\"b\":true,\"n\":null}");
        var text = JsonDocumentEditor.SetLeafText(Leaf(document, "s"), "新文本", document);
        document = JsonDocumentEditor.ParseStrict(text);
        text = JsonDocumentEditor.SetLeafText(Leaf(document, "i"), "42", document);
        document = JsonDocumentEditor.ParseStrict(text);
        text = JsonDocumentEditor.SetLeafText(Leaf(document, "f"), "2.25", document);
        document = JsonDocumentEditor.ParseStrict(text);
        text = JsonDocumentEditor.SetLeafText(Leaf(document, "b"), "false", document);
        document = JsonDocumentEditor.ParseStrict(text);
        text = JsonDocumentEditor.SetLeafText(Leaf(document, "n"), "null", document);
        document = JsonDocumentEditor.ParseStrict(text);

        // 类型没有被字符串化（字符串仍是字符串、数字仍是数字、布尔仍是布尔、null 仍是 null）
        Assert.Equal(JsonValueKind.String, JsonDocumentEditor.DescribeKind(document["s"]));
        Assert.Equal(JsonValueKind.Integer, JsonDocumentEditor.DescribeKind(document["i"]));
        Assert.Equal(JsonValueKind.Number, JsonDocumentEditor.DescribeKind(document["f"]));
        Assert.Equal(JsonValueKind.Boolean, JsonDocumentEditor.DescribeKind(document["b"]));
        Assert.Equal(JsonValueKind.Null, JsonDocumentEditor.DescribeKind(document["n"]));
        Assert.Equal("新文本", document["s"]!.GetValue<string>());
        Assert.Equal(42L, document["i"]!.GetValue<long>());
        Assert.Equal(2.25, document["f"]!.GetValue<double>());
        Assert.False(document["b"]!.GetValue<bool>());
        Assert.Null(document["n"]);
    }

    [Fact]
    public void Set_leaf_text_writes_into_array_items()
    {
        var document = Doc("{\"arr\":[\"a\",1]}");
        var text = JsonDocumentEditor.SetLeafText(Leaf(document, "arr/1"), "9", document);
        var updated = JsonDocumentEditor.ParseStrict(text);
        Assert.Equal(9L, updated["arr"]![1]!.GetValue<long>());
        Assert.Equal(JsonValueKind.String, JsonDocumentEditor.DescribeKind(updated["arr"]![0]));
    }

    [Fact]
    public void Set_leaf_text_rejects_wrong_types()
    {
        var document = Doc("{\"i\":3,\"f\":1.5,\"b\":true}");
        var integerError = Assert.Throws<InvalidDataException>(
            () => JsonDocumentEditor.SetLeafText(Leaf(document, "i"), "abc", document));
        Assert.Contains("整数", integerError.Message);
        Assert.Contains("请输入整数", integerError.Message);

        var numberError = Assert.Throws<InvalidDataException>(
            () => JsonDocumentEditor.SetLeafText(Leaf(document, "f"), "abc", document));
        Assert.Contains("数字", numberError.Message);

        var boolError = Assert.Throws<InvalidDataException>(
            () => JsonDocumentEditor.SetLeafText(Leaf(document, "b"), "yes", document));
        Assert.Contains("布尔值", boolError.Message);
        Assert.Contains("true 或 false", boolError.Message);

        // 类型不匹配时文档保持不变
        Assert.Equal(3L, document["i"]!.GetValue<long>());
        Assert.True(document["b"]!.GetValue<bool>());
    }

    [Fact]
    public void Set_leaf_text_rejects_container_rows()
    {
        var document = Doc("{\"obj\":{\"a\":1}}");
        var container = JsonDocumentEditor.EnumerateChildren(JsonDocumentEditor.CreateRoot(document))
            .Single(x => x.Name == "obj");
        var error = Assert.Throws<InvalidDataException>(
            () => JsonDocumentEditor.SetLeafText(container, "x", document));
        Assert.Contains("只能编辑叶子值", error.Message);
    }

    [Fact]
    public void Set_leaf_text_on_root_leaf_without_parent_throws()
    {
        var document = Doc("42");
        var row = JsonDocumentEditor.FlattenLeaves(document).Single();
        Assert.Throws<InvalidOperationException>(() => JsonDocumentEditor.SetLeafText(row, "43", document));
    }

    // ── 删除键 ──────────────────────────────────────────────────────

    [Fact]
    public void Remove_node_deletes_object_key_and_array_item()
    {
        var document = Doc("{\"a\":1,\"b\":2,\"arr\":[1,2,3]}");

        Assert.True(JsonDocumentEditor.RemoveNode(Leaf(document, "a"), out var none));
        Assert.Null(none);
        Assert.False(document.AsObject().ContainsKey("a"));

        Assert.True(JsonDocumentEditor.RemoveNode(Leaf(document, "arr/1"), out _));
        Assert.Equal(new[] { 1, 3 }, document["arr"]!.AsArray().Select(x => x!.GetValue<int>()));
    }

    [Fact]
    public void Remove_node_on_root_reports_chinese_error()
    {
        var document = Doc("{\"a\":1}");
        var root = JsonDocumentEditor.CreateRoot(document);
        Assert.False(JsonDocumentEditor.RemoveNode(root, out var error));
        Assert.NotNull(error);
        Assert.Contains("根节点不能删除", error);
    }

    // ── 差异摘要（plan-12 的 diff Tab 复用）─────────────────────────

    [Fact]
    public void Diff_summary_counts_rfc6902_operations()
    {
        Assert.Equal("与官方版本无差异。",
            JsonDocumentEditor.DescribeDiff("{\"a\":1}", "{\"a\":1}"));
        Assert.Equal("与官方版本差异：1 个 RFC6902 操作。",
            JsonDocumentEditor.DescribeDiff("{\"a\":1}", "{\"a\":2}"));
        Assert.Equal("（差异无法计算：JSON 非法）",
            JsonDocumentEditor.DescribeDiff("{\"a\":1}", "{ broken"));
        Assert.Equal(0, JsonDocumentEditor.CountDiffOperations("{\"a\":1}", "{\"a\": 1}"));
        Assert.Equal(1, JsonDocumentEditor.CountDiffOperations("{\"a\":1}", "{\"a\":2}"));
    }

    [Fact]
    public void Diff_operations_are_replayable_by_text_diff_service()
    {
        // 生成 → 回放 必须还原（plan-10/12 导出通道同口径）。
        var vanilla = Doc("{\"k\":\"旧\",\"n\":1}");
        var modified = Doc("{\"k\":\"新\",\"n\":1,\"extra\":true}");
        var ops = JsonDocumentEditor.GenerateDiff(vanilla, modified);
        var replayed = new LimbusModEditor.Application.Texts.TextDiffService().Apply(vanilla!, ops);
        Assert.True(JsonNode.DeepEquals(modified, replayed));
    }
}
