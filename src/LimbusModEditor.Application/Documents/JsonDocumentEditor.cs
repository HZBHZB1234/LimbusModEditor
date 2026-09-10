using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LimbusModEditor.Application.Texts;

namespace LimbusModEditor.Application.Documents;

/// <summary>JSON 节点的值类型（用于「按原值类型写回」判定）。</summary>
public enum JsonValueKind
{
    /// <summary>对象（容器）。</summary>
    Object,
    /// <summary>数组（容器）。</summary>
    Array,
    /// <summary>字符串。</summary>
    String,
    /// <summary>整数（JSON 数字里能用 Int64 表示的那些）。</summary>
    Integer,
    /// <summary>浮点数。</summary>
    Number,
    /// <summary>布尔。</summary>
    Boolean,
    /// <summary>JSON null。</summary>
    Null,
}

/// <summary>
/// JSON 树/展平表里的一行（plan-09 共享 JsonTreeEditor 的视图模型）。
/// 保留 <see cref="Parent"/>/<see cref="Key"/>/<see cref="Index"/> 是为了「按原值类型
/// 就地写回」——与旧的 <c>TextWorkbenchPage.KvRow</c> / <c>StaticWorkbenchPage.KvRow</c>
/// 完全同构（那两处的行为是本类的提取来源）。
/// </summary>
/// <param name="Path">展平键路径（对象 <c>a/b</c>，数组 <c>a/0</c>；根为空串）。</param>
/// <param name="Name">显示名（对象键名 / 数组下标 / 根名）。</param>
/// <param name="Node">对应的 JsonNode；JSON null 值这里就是 null。</param>
/// <param name="Parent">父容器（写回/删除用）；根为 null。</param>
/// <param name="Key">对象子节点的键名；数组子节点为 null。</param>
/// <param name="Index">数组子节点的下标；对象子节点为 -1。</param>
/// <param name="Depth">深度（根为 0）。</param>
public sealed record JsonEditRow(
    string Path, string Name, JsonNode? Node, JsonNode? Parent, string? Key, int Index, int Depth)
{
    /// <summary>是否是容器（对象 / 数组）；JSON null 不是容器。</summary>
    public bool IsContainer => Node is JsonObject or JsonArray;

    /// <summary>是否是可编辑的叶子（字符串/整数/浮点/布尔/null）。</summary>
    public bool IsLeaf => !IsContainer;

    /// <summary>值类型。</summary>
    public JsonValueKind Kind => JsonDocumentEditor.DescribeKind(Node);

    /// <summary>子项数（容器有效）。</summary>
    public int ChildCount => JsonDocumentEditor.CountChildren(Node);

    /// <summary>单行预览文本（容器显示「N 个键 / N 项」）。</summary>
    public string Preview => JsonDocumentEditor.Preview(Node);
}

/// <summary>
/// plan-09：JSON 文档的纯逻辑编辑内核（无 WPF 依赖，供 Domain.Tests 直接覆盖）。
///
/// <para>提取来源（行为逐条对齐，见每个方法的注释）：
/// <c>TextWorkbenchPage.Flatten/SetNodeValue/RefreshValueEditor</c> 与
/// <c>StaticWorkbenchPage</c> 的同名逻辑。共享控件 <c>JsonTreeEditor</c> 只做绑定，
/// 一切判定都在这里——这样「类型写回、非法 JSON 拒绝、展平/树枚举」都能被单元测试覆盖。</para>
///
/// <para><b>与旧实现的两处刻意差异</b>（已在报告中说明）：
/// ① 旧 <c>Flatten</c> 遇 <c>rows.Count &gt; 5000</c> 直接返回（大文件后半截看不到），
/// 本类的展平不截断，树视图按层惰性展开；
/// ② 旧 <c>SetNodeValue</c> 对布尔值解析失败会静默写成 <c>false</c>
/// （<c>bool.TryParse(text, out b) &amp;&amp; b</c>），本类改为明确的中文错误。</para>
///
/// <para>本类不写任何文件、不管导出：调用方拿到文本后自行决定存编辑集还是导出。</para>
/// </summary>
public static class JsonDocumentEditor
{
    /// <summary>预览文本的最大长度（超出截断加省略号），与旧实现一致。</summary>
    public const int PreviewLength = 120;

    /// <summary>根节点的显示名（路径仍为空串，与旧实现的展平口径一致）。</summary>
    public const string RootName = "根";

    // ── 解析 / 序列化 ────────────────────────────────────────────────

    /// <summary>严格解析（非法 JSON / 内容为空 → <see cref="InvalidDataException"/>，中文提示）。
    /// 根为 JSON <c>null</c> 视为空文档并拒绝（无法呈现树）。</summary>
    public static JsonNode ParseStrict(string? jsonText)
    {
        if (string.IsNullOrWhiteSpace(jsonText)) throw new InvalidDataException("JSON 内容为空。");
        JsonNode? document;
        try
        {
            document = JsonNode.Parse(jsonText);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"JSON 非法：{ex.Message}", ex);
        }
        return document ?? throw new InvalidDataException("JSON 内容为 null，无法作为待编辑文档。");
    }

    /// <summary>非抛出式解析：非法时 <paramref name="error"/> 给中文原因、返回 false。</summary>
    public static bool TryParse(string? jsonText, out JsonNode? document, out string? error)
    {
        try
        {
            document = ParseStrict(jsonText);
            error = null;
            return true;
        }
        catch (InvalidDataException ex)
        {
            document = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>序列化为缩进 JSON 文本（与旧实现 <c>ToJsonString(WriteIndented)</c> 一致）。</summary>
    public static string Serialize(JsonNode? document)
        => document is null
            ? "null"
            : document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    // ── 类型判定与预览 ───────────────────────────────────────────────

    /// <summary>值类型判定：顺序与旧实现一致（字符串 → 布尔 → 整数 → 浮点）。
    /// 注意 JSON null 的 <paramref name="node"/> 就是 null。</summary>
    public static JsonValueKind DescribeKind(JsonNode? node)
    {
        switch (node)
        {
            case null: return JsonValueKind.Null;
            case JsonObject: return JsonValueKind.Object;
            case JsonArray: return JsonValueKind.Array;
            case JsonValue value:
                if (value.TryGetValue<string>(out _)) return JsonValueKind.String;
                if (value.TryGetValue<bool>(out _)) return JsonValueKind.Boolean;
                if (value.TryGetValue<long>(out _)) return JsonValueKind.Integer;
                if (value.TryGetValue<double>(out _)) return JsonValueKind.Number;
                return JsonValueKind.String; // 未识别的 JsonValue：按字符串处理（旧实现同兜底）
            default: return JsonValueKind.Null;
        }
    }

    /// <summary>子项数（对象键数 / 数组项数）。</summary>
    public static int CountChildren(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Count,
        JsonArray array => array.Count,
        _ => 0,
    };

    /// <summary>单行预览：容器显示「N 个键 / N 项」，叶子显示值文本（超长截断）。</summary>
    public static string Preview(JsonNode? node, int maxLength = PreviewLength)
    {
        switch (node)
        {
            case null: return "null";
            case JsonObject obj: return $"{{ {obj.Count} 个键 }}";
            case JsonArray array: return $"[ {array.Count} 项 ]";
            case JsonValue value:
                var text = value.TryGetValue<string>(out var str) ? str : value.ToJsonString();
                return Truncate(text, maxLength);
            default: return Truncate(node.ToJsonString(), maxLength);
        }
    }

    /// <summary>值编辑框的初始文本：字符串取原文，其它类型取 JSON 字面量（旧实现同口径）；
    /// <b>容器行只给短预览</b>——绝不把整棵子树序列化进文本框（MB 级表会直接冻住 UI）。</summary>
    public static string InitialEditorText(JsonEditRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.IsContainer ? row.Preview : LeafText(row.Node);
    }

    /// <summary>值编辑框的初始文本：字符串取原文，其它类型取 JSON 字面量（旧实现同口径）。</summary>
    public static string LeafText(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var str) ? str : node?.ToJsonString() ?? "null";

    /// <summary>数一数文档里有多少个节点（容器 + 叶子），**最多数到 <paramref name="limit"/> 个**
    /// 就停——用于「已载入 N 个节点」这类状态文案，避免为了显示一个数字把 MB 级文档全量物化。
    /// 返回值等于 <paramref name="limit"/> 时表示「至少这么多（可能更多）」。</summary>
    public static int CountNodes(JsonNode? document, int limit = 20000)
    {
        if (document is null) return 0;
        var count = 0;
        CountInto(document, ref count, Math.Max(1, limit));
        return count;
    }

    private static void CountInto(JsonNode node, ref int count, int limit)
    {
        count++;
        if (count >= limit) return;
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    if (count >= limit) return;
                    if (child is not null) CountInto(child, ref count, limit);
                    else count++; // JSON null 也是一个节点
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (count >= limit) return;
                    if (child is not null) CountInto(child, ref count, limit);
                    else count++;
                }
                break;
        }
    }

    /// <summary>截断超长文本（加省略号）。</summary>
    public static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";

    // ── 树 / 展平枚举 ───────────────────────────────────────────────

    /// <summary>构造根行（路径为空串，与旧实现展平口径一致）。</summary>
    public static JsonEditRow CreateRoot(JsonNode? document)
        => new(string.Empty, RootName, document, null, null, -1, 0);

    /// <summary>按层枚举直接子项（树视图惰性展开用；只生成这一层，不递归）。
    /// JSON null 值也作为叶子行列出（旧实现会漏掉它们，导致 null 值无法在界面里编辑）。</summary>
    public static IReadOnlyList<JsonEditRow> EnumerateChildren(JsonEditRow parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var rows = new List<JsonEditRow>();
        switch (parent.Node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                    rows.Add(new JsonEditRow(ObjectChildPath(parent.Path, key), key, value, obj, key, -1, parent.Depth + 1));
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    rows.Add(new JsonEditRow(ArrayChildPath(parent.Path, index),
                        index.ToString(CultureInfo.InvariantCulture), array[index], array, null, index, parent.Depth + 1));
                }
                break;
        }
        return rows;
    }

    /// <summary>展平出全部**叶子**行（不截断：旧实现的 5000 行上限已移除）。
    /// 路径口径与旧 <c>Flatten</c> 完全一致（对象 <c>a/b</c>；数组根层为 <c>/0</c>）。</summary>
    public static IReadOnlyList<JsonEditRow> FlattenLeaves(JsonNode? document)
    {
        var rows = new List<JsonEditRow>();
        FlattenInto(document, string.Empty, null, null, -1, 0, rows, includeContainers: false);
        return rows;
    }

    /// <summary>展平出全部行（含容器行，前序顺序）；树视图的「全部展开」与测试用。</summary>
    public static IReadOnlyList<JsonEditRow> FlattenAll(JsonNode? document)
    {
        var rows = new List<JsonEditRow>();
        FlattenInto(document, string.Empty, null, null, -1, 0, rows, includeContainers: true);
        return rows;
    }

    private static void FlattenInto(JsonNode? node, string path, JsonNode? parent, string? key, int index,
        int depth, List<JsonEditRow> rows, bool includeContainers)
    {
        var row = new JsonEditRow(path, DisplayNameOf(path, key, index, depth), node, parent, key, index, depth);
        switch (node)
        {
            case JsonObject obj:
                if (includeContainers) rows.Add(row);
                foreach (var (childKey, child) in obj)
                    FlattenInto(child, ObjectChildPath(path, childKey), obj, childKey, -1, depth + 1, rows, includeContainers);
                break;
            case JsonArray array:
                if (includeContainers) rows.Add(row);
                for (var i = 0; i < array.Count; i++)
                    FlattenInto(array[i], ArrayChildPath(path, i), array, null, i, depth + 1, rows, includeContainers);
                break;
            default:
                rows.Add(row); // JsonValue 或 JSON null：叶子
                break;
        }
    }

    private static string DisplayNameOf(string path, string? key, int index, int depth)
        => depth == 0 ? RootName
            : key ?? (index >= 0 ? index.ToString(CultureInfo.InvariantCulture) : path);

    private static string ObjectChildPath(string parentPath, string key)
        => parentPath.Length == 0 ? key : $"{parentPath}/{key}";

    private static string ArrayChildPath(string parentPath, int index)
        => $"{parentPath}/{index.ToString(CultureInfo.InvariantCulture)}";

    // ── 值编辑 / 删除 ────────────────────────────────────────────────

    /// <summary>按原值类型写回叶子文本，返回整份文档的新 JSON 文本（缩进）。
    ///
    /// <para>类型规则：字符串 → 原样写字符串；整数 → 必须能解析为 Int64，否则中文报错；
    /// 浮点 → 必须能解析为 double（InvariantCulture，JSON 数字本就是点号小数），否则中文报错；
    /// 布尔 → 必须 true/false，否则中文报错；JSON null → 输入 <c>null</c>（或空）写回 null，
    /// 其它文本按字符串写回。</para>
    ///
    /// <para>容器行不可直接编辑（抛 <see cref="InvalidDataException"/>，中文提示），
    /// 与旧实现的提示语义一致。</para></summary>
    public static string SetLeafText(JsonEditRow row, string? text, JsonNode? root = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsContainer)
            throw new InvalidDataException("只能编辑叶子值（字符串/整数/浮点/布尔/null）；数组与对象请用「原文」页编辑。");

        var replacement = BuildReplacement(row.Kind, text ?? string.Empty);
        switch (row.Parent)
        {
            case JsonObject obj when row.Key is { } key:
                obj[key] = replacement;
                break;
            case JsonArray array when row.Index >= 0:
                if (row.Index >= array.Count)
                    throw new InvalidDataException($"数组下标越界：{row.Path}（长度 {array.Count}）。");
                array[row.Index] = replacement;
                break;
            default:
                throw new InvalidOperationException("该节点没有可写的父容器。");
        }
        return Serialize(root ?? row.Parent);
    }

    private static JsonNode? BuildReplacement(JsonValueKind kind, string text)
    {
        var trimmed = text.Trim();
        switch (kind)
        {
            case JsonValueKind.Boolean:
                if (!bool.TryParse(trimmed, out var boolean))
                    throw new InvalidDataException("该键原值是布尔值，请输入 true 或 false。");
                return JsonValue.Create(boolean);
            case JsonValueKind.Integer:
                if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    throw new InvalidDataException("该键原值是整数，请输入整数。");
                return JsonValue.Create(integer);
            case JsonValueKind.Number:
                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw new InvalidDataException("该键原值是数字，请输入数字。");
                return JsonValue.Create(number);
            case JsonValueKind.Null:
                // JSON null 没有可继承的类型：输入 null（或留空）保持 null，其它文本按字符串写回。
                return trimmed.Length == 0 || trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : JsonValue.Create(text);
            default:
                return JsonValue.Create(text);
        }
    }

    /// <summary>从父容器删除该节点（对象删键 / 数组删下标）。失败时 <paramref name="error"/> 给中文原因。</summary>
    public static bool RemoveNode(JsonEditRow row, out string? error)
    {
        ArgumentNullException.ThrowIfNull(row);
        error = null;
        switch (row.Parent)
        {
            case JsonObject obj when row.Key is { } key:
                if (!obj.Remove(key))
                {
                    error = $"键不存在：{row.Path}";
                    return false;
                }
                return true;
            case JsonArray array when row.Index >= 0:
                if (row.Index >= array.Count)
                {
                    error = $"数组下标越界：{row.Path}（长度 {array.Count}）";
                    return false;
                }
                array.RemoveAt(row.Index);
                return true;
            default:
                error = "该节点没有可删除的父容器（根节点不能删除）。";
                return false;
        }
    }

    // ── 与官方版本的差异摘要（plan-12 的 diff Tab 复用）──────────────

    /// <summary>生成把 <paramref name="before"/> 变为 <paramref name="after"/> 的 RFC6902 操作序列。</summary>
    public static JsonArray GenerateDiff(JsonNode? before, JsonNode? after) => new TextDiffService().Generate(before, after);

    /// <summary>两份 JSON 文本之间的 RFC6902 操作数；任一份非法即抛 <see cref="InvalidDataException"/>。</summary>
    public static int CountDiffOperations(string? vanillaJsonText, string? modifiedJsonText)
        => GenerateDiff(ParseStrict(vanillaJsonText), ParseStrict(modifiedJsonText)).Count;

    /// <summary>中文差异摘要（与旧 StaticWorkbenchPage 同口径：无差异 / N 个 RFC6902 操作 / 无法计算）。</summary>
    public static string DescribeDiff(string? vanillaJsonText, string? modifiedJsonText)
    {
        try
        {
            var operations = CountDiffOperations(vanillaJsonText, modifiedJsonText);
            return operations == 0
                ? "与官方版本无差异。"
                : $"与官方版本差异：{operations} 个 RFC6902 操作。";
        }
        catch (InvalidDataException)
        {
            return "（差异无法计算：JSON 非法）";
        }
    }
}
