using System.Text.Json.Nodes;

namespace LimbusModEditor.Application.Texts;

/// <summary>
/// RFC 6902 JSON Patch 的最小实现（生成 + 应用），供 lang 文本模组通道使用。
/// 路径语义遵循 RFC 6901 JSON Pointer（`~0`/`~1` 转义）；未知 op、路径越界、
/// test 失败等一律 fail fast 并给出中文错误（项目铁律：不猜测、不静默丢弃）。
/// 生成侧按「对象逐键、数组前缀/中段/后缀」策略产出 add/remove/replace，
/// 与 python-jsonpatch（真实加载器使用）的应用语义完全兼容。
/// </summary>
public sealed class TextDiffService
{
    /// <summary>生成把 <paramref name="before"/> 变为 <paramref name="after"/>
    /// 的 RFC6902 操作序列（可能为空数组）。</summary>
    public JsonArray Generate(JsonNode? before, JsonNode? after)
    {
        var ops = new JsonArray();
        GenerateInto(before, after, string.Empty, ops);
        return ops;
    }

    /// <summary>把 <paramref name="patch"/> 逐条应用到场点文档上（就地修改，
    /// 返回根节点——`add`/`replace` 根路径时可能是新根）。任何一条失败即抛出，
    /// 已应用的前序条目保留。</summary>
    public JsonNode Apply(JsonNode document, JsonArray patch)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(patch);
        var root = document;
        for (var index = 0; index < patch.Count; index++)
        {
            if (patch[index] is not JsonObject op)
                throw new InvalidDataException($"补丁第 {index + 1} 条不是 JSON 对象。");
            root = ApplyOne(root, op, index + 1);
        }
        return root;
    }

    private static JsonNode ApplyOne(JsonNode root, JsonObject op, int ordinal)
    {
        var kind = op["op"]?.GetValue<string>()
            ?? throw new InvalidDataException($"补丁第 {ordinal} 条缺少 op 字段。");
        var path = op["path"]?.GetValue<string>()
            ?? throw new InvalidDataException($"补丁第 {ordinal} 条（{kind}）缺少 path 字段。");

        switch (kind)
        {
            case "add": return ApplyAdd(root, ParsePointer(path, ordinal, kind), RequireValue(op, ordinal, kind), path);
            case "replace": return ApplyReplace(root, ParsePointer(path, ordinal, kind), RequireValue(op, ordinal, kind));
            case "remove": return ApplyRemove(root, ParsePointer(path, ordinal, kind));
            case "test":
                var tested = ResolveExisting(root, ParsePointer(path, ordinal, kind), path);
                if (tested is null || !JsonNode.DeepEquals(tested, RequireValue(op, ordinal, kind)))
                    throw new InvalidDataException($"补丁第 {ordinal} 条 test 失败：{path} 的当前值与期望值不一致。");
                return root;
            case "copy":
            case "move":
                var from = op["from"]?.GetValue<string>()
                    ?? throw new InvalidDataException($"补丁第 {ordinal} 条（{kind}）缺少 from 字段。");
                var fromTokens = ParsePointer(from, ordinal, kind);
                var pathTokens = ParsePointer(path, ordinal, kind);
                if (kind == "move" && IsDescendant(fromTokens, pathTokens))
                    throw new InvalidDataException($"补丁第 {ordinal} 条 move 的目标 {path} 位于来源 {from} 内部。");
                var source = ResolveExisting(root, fromTokens, from)
                    ?? throw new InvalidDataException($"补丁第 {ordinal} 条（{kind}）的来源不存在: {from}");
                if (kind == "move") root = ApplyRemove(root, fromTokens);
                return ApplyAdd(root, pathTokens, source.DeepClone(), path);
            default:
                throw new InvalidDataException($"补丁第 {ordinal} 条使用了不支持的 op: {kind}（支持 add/remove/replace/move/copy/test）。");
        }
    }

    private static JsonNode ApplyAdd(JsonNode root, string[] tokens, JsonNode value, string path)
    {
        if (tokens.Length == 0) return value.DeepClone();
        var (parent, last) = ResolveParent(root, tokens, path);
        switch (parent)
        {
            case JsonArray array when last.Index is { } index:
                if (index < 0 || index > array.Count)
                    throw new InvalidDataException($"add 目标数组下标越界: {path}（长度 {array.Count}）");
                if (index == array.Count) array.Add(value.DeepClone());
                else array.Insert(index, value.DeepClone());
                return root;
            case JsonObject obj when last.Key is { } key:
                obj[key] = value.DeepClone();
                return root;
            default:
                throw new InvalidDataException($"add 的父容器类型不支持: {path}");
        }
    }

    private static JsonNode ApplyReplace(JsonNode root, string[] tokens, JsonNode value)
    {
        if (tokens.Length == 0) return value.DeepClone();
        var (parent, last) = ResolveParent(root, tokens, PathOf(tokens));
        if (parent is JsonArray array && last.Index is { } index)
        {
            if (index < 0 || index >= array.Count)
                throw new InvalidDataException($"replace 目标数组下标越界: {PathOf(tokens)}（长度 {array.Count}）");
            array[index] = value.DeepClone();
            return root;
        }
        if (parent is JsonObject obj && last.Key is { } key)
        {
            if (!obj.ContainsKey(key))
                throw new InvalidDataException($"replace 目标不存在: {PathOf(tokens)}（replace 不创建新键，请用 add）");
            obj[key] = value.DeepClone();
            return root;
        }
        throw new InvalidDataException($"replace 的父容器类型不支持: {PathOf(tokens)}");
    }

    private static JsonNode ApplyRemove(JsonNode root, string[] tokens)
    {
        if (tokens.Length == 0)
            throw new InvalidDataException("remove 不能作用于根文档。");
        var path = PathOf(tokens);
        var (parent, last) = ResolveParent(root, tokens, path);
        if (parent is JsonArray array && last.Index is { } index)
        {
            if (index < 0 || index >= array.Count)
                throw new InvalidDataException($"remove 目标数组下标越界: {path}（长度 {array.Count}）");
            array.RemoveAt(index);
            return root;
        }
        if (parent is JsonObject obj && last.Key is { } key)
        {
            if (!obj.Remove(key))
                throw new InvalidDataException($"remove 目标不存在: {path}");
            return root;
        }
        throw new InvalidDataException($"remove 的父容器类型不支持: {path}");
    }

    /// <summary>解析到目标节点的父容器与最后一个 token；中间任何一级缺失即抛出。
    /// 数组的 `-`（追加）解析为当前长度。</summary>
    private static (JsonNode Parent, (int? Index, string? Key) Last) ResolveParent(
        JsonNode root, string[] tokens, string path)
    {
        var current = root;
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            current = ResolveExisting(current, [tokens[i]], path)
                ?? throw new InvalidDataException($"路径中间节点不存在: {path}（停在 {tokens[i]}）");
        }
        var last = tokens[^1];
        if (current is JsonArray array)
        {
            var index = last switch
            {
                "-" => array.Count,
                _ when int.TryParse(last, out var parsed) => parsed,
                _ => -1
            };
            return (array, (index, null));
        }
        if (current is JsonObject obj) return (obj, (null, last));
        throw new InvalidDataException($"路径父节点不是容器: {path}");
    }

    private static JsonNode? ResolveExisting(JsonNode current, string[] tokens, string path)
    {
        foreach (var token in tokens)
        {
            switch (current)
            {
                case JsonObject obj when token != "-" && obj.TryGetPropertyValue(token, out var child) && child is not null:
                    current = child;
                    break;
                case JsonArray array when int.TryParse(token, out var index) && index >= 0 && index < array.Count && array[index] is not null:
                    current = array[index]!;
                    break;
                default:
                    return null;
            }
        }
        return current;
    }

    private static JsonNode RequireValue(JsonObject op, int ordinal, string kind)
        => op["value"]?.DeepClone()
           ?? throw new InvalidDataException($"补丁第 {ordinal} 条（{kind}）缺少 value 字段。");

    private static string[] ParsePointer(string pointer, int ordinal, string kind)
    {
        if (pointer.Length == 0) return [];
        if (!pointer.StartsWith('/'))
            throw new InvalidDataException($"补丁第 {ordinal} 条（{kind}）的路径不是合法 JSON Pointer（需以 / 开头）: {pointer}");
        return pointer[1..].Split('/').Select(t => t.Replace("~1", "/").Replace("~0", "~")).ToArray();
    }

    private static bool IsDescendant(string[] from, string[] path)
        => from.Length < path.Length && from.AsSpan().SequenceEqual(path.AsSpan(0, from.Length));

    private static void GenerateInto(JsonNode? before, JsonNode? after, string pointer, JsonArray ops)
    {
        if (before is null) { ops.Add(MakeOp("add", pointer, after)); return; }
        if (after is null) { ops.Add(MakeOp("remove", pointer)); return; }
        if (before is JsonObject beforeObject && after is JsonObject afterObject)
        {
            foreach (var (key, value) in beforeObject)
            {
                var childPointer = $"{pointer}/{EscapeToken(key)}";
                if (!afterObject.TryGetPropertyValue(key, out var afterValue))
                    ops.Add(MakeOp("remove", childPointer));
                else
                    GenerateInto(value, afterValue, childPointer, ops);
            }
            foreach (var (key, value) in afterObject)
            {
                if (!beforeObject.TryGetPropertyValue(key, out _))
                    ops.Add(MakeOp("add", $"{pointer}/{EscapeToken(key)}", value));
            }
            return;
        }
        if (before is JsonArray beforeArray && after is JsonArray afterArray)
        {
            GenerateArrayDiff(beforeArray, afterArray, pointer, ops);
            return;
        }
        if (!JsonNode.DeepEquals(before, after))
            ops.Add(MakeOp("replace", pointer, after));
    }

    private static void GenerateArrayDiff(JsonArray before, JsonArray after, string pointer, JsonArray ops)
    {
        var prefix = 0;
        while (prefix < before.Count && prefix < after.Count && JsonNode.DeepEquals(before[prefix], after[prefix]))
            prefix++;
        var suffix = 0;
        while (suffix < before.Count - prefix && suffix < after.Count - prefix &&
               JsonNode.DeepEquals(before[before.Count - 1 - suffix], after[after.Count - 1 - suffix]))
            suffix++;
        var beforeMiddle = before.Count - prefix - suffix;
        var afterMiddle = after.Count - prefix - suffix;

        if (beforeMiddle == afterMiddle)
        {
            // 等长中段逐项递归：文本模组常见的「同位置改字段」产出精准 replace
            for (var i = 0; i < afterMiddle; i++)
                GenerateInto(before[prefix + i], after[prefix + i], $"{pointer}/{prefix + i}", ops);
            return;
        }
        // 增删条目：从尾部往前删保证下标有效，再按下标逐个插入
        for (var i = beforeMiddle - 1; i >= 0; i--)
            ops.Add(MakeOp("remove", $"{pointer}/{prefix + i}"));
        for (var i = 0; i < afterMiddle; i++)
            ops.Add(MakeOp("add", $"{pointer}/{prefix + i}", after[prefix + i]));
    }

    private static JsonObject MakeOp(string op, string path, JsonNode? value = null)
    {
        var result = new JsonObject { ["op"] = op, ["path"] = path };
        if (value is not null) result["value"] = value.DeepClone();
        return result;
    }

    private static string EscapeToken(string token) => token.Replace("~", "~0").Replace("/", "~1");

    private static string PathOf(string[] tokens) => tokens.Length == 0 ? "" : "/" + string.Join('/', tokens);
}
