using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LimbusModEditor.Application.Texts;

/// <summary>语言文本的导出格式（plan-16 §3）。</summary>
public enum LangExportFormat
{
    /// <summary>LCTA <c>webutils/fancy/bus.py</c> 认得的 <c>lcta-bus</c> 规则集（按字段赋值）。</summary>
    Bus,
    /// <summary>LCTA <c>launcher/changes.py</c> 直接消费的 <c>{"patchs": {键: [RFC6902…]}}</c>。</summary>
    Patch,
    /// <summary>pathset 覆盖清单（<c>launcher/staticmod.py</c> 的 pathset 语义）。</summary>
    Pathset,
}

/// <summary>一个格式的产出（<paramref name="JsonText"/> 为 null 表示该格式表达不了这条改动）。</summary>
/// <param name="Format">格式。</param>
/// <param name="JsonText">JSON 文本（缩进 2，中文不转义）；无法表达时为 null。</param>
/// <param name="Notes">诊断 / 说明（中文；跳过原因写在这里）。</param>
public sealed record LangFormatResult(LangExportFormat Format, string? JsonText, IReadOnlyList<string> Notes)
{
    /// <summary>是否成功产出。</summary>
    public bool Written => JsonText is not null;

    /// <summary>产出文本的字节数（报告用）。</summary>
    public int ByteCount => JsonText is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(JsonText);
}

/// <summary>
/// plan-16 §3：把「一条被改文本表的 vanilla → modified 差分」翻译成 LCTA 认得的多套格式。
///
/// <para><b>为什么是三种</b>：加载器的文本通道有两套语义 —— <c>changes.py</c> 的
/// <c>patchs</c>（RFC6902，精确但只认这一个键）与 <c>modfancy.py</c> 认得的
/// 规则集（<c>lcta-bus</c> 等，按路径赋值）。编辑器同时产出两者，用户按需要取用；
/// pathset 是第三份「人类可读的改动清单」（现版本 <c>changes.py</c> 不消费它，报告里如实标注）。</para>
///
/// <para><b>可表达性门（plan-16 §3.4）</b>：bus 没有删除动作、也不能表达数组长度变化，
/// 这类改动会让规则集「加载器不报错但改动丢失」。因此逐条检查差分里的操作，
/// 表达不了就<b>只跳过那一个格式</b>并给出中文原因，其余格式照常产出。</para>
/// </summary>
public static class LangExportFormatter
{
    /// <summary>bus 规则集的格式标识（<c>webutils/fancy/bus.py:12</c>）。</summary>
    public const string BusFormat = "lcta-bus";
    /// <summary>bus 规则集版本（<c>bus.py:13</c>）。</summary>
    public const int BusVersion = 1;
    /// <summary>pathset 文档的格式标识（自定义标记，供用户识别；加载器不看它）。</summary>
    public const string PathsetFormat = "lcta-pathset";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>pathset/加载器接受的路径段语法（与 <c>staticmod._pathset_to_jsonpatch</c> 同口径）。</summary>
    private static readonly Regex PathSegmentPattern = new(
        @"^([A-Za-z_][A-Za-z0-9_]*)(?:\[(\d+)\])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 生成三种格式。参数用「相对活动语言目录的条目路径」（决定 <c>files</c> 里的匹配串）
    /// 与「加载器口径的补丁键」（<c>patch</c> 的键、报告里的定位）。
    /// </summary>
    /// <param name="relativePath">条目口径（如 <c>StoryData/S1.json</c>）。</param>
    /// <param name="patchKey">加载器口径（如 <c>LLc-CN-LCTA/StoryData/S1.json</c>）。</param>
    /// <param name="modifiedLabel">报告用的人类可读名（一般是模组名，可为空）。</param>
    /// <param name="vanillaJson">官方原文。</param>
    /// <param name="modifiedJson">修改后文本。</param>
    public static IReadOnlyList<LangFormatResult> Format(
        string relativePath, string patchKey, string? modifiedLabel, string vanillaJson, string modifiedJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(patchKey);
        ArgumentNullException.ThrowIfNull(vanillaJson);
        ArgumentNullException.ThrowIfNull(modifiedJson);

        JsonNode? before;
        JsonNode? after;
        try
        {
            before = JsonNode.Parse(vanillaJson);
            after = JsonNode.Parse(modifiedJson);
        }
        catch (JsonException ex)
        {
            return
            [
                new(LangExportFormat.Bus, null, [$"无法解析 JSON：{ex.Message}"]),
                new(LangExportFormat.Patch, null, [$"无法解析 JSON：{ex.Message}"]),
                new(LangExportFormat.Pathset, null, [$"无法解析 JSON：{ex.Message}"]),
            ];
        }

        var ops = new TextDiffService().Generate(before, after);
        return
        [
            BuildBus(relativePath, modifiedLabel, before, ops),
            BuildPatch(patchKey, ops),
            BuildPathset(relativePath, modifiedLabel, before, ops),
        ];
    }

    // ── bus（lcta-bus 规则集）────────────────────────────────────────

    private static LangFormatResult BuildBus(string relativePath, string? label, JsonNode? vanilla, JsonArray ops)
    {
        if (ops.Count == 0)
            return new(LangExportFormat.Bus, null, ["与官方版本无差异"]);

        var fileName = Path.GetFileName(relativePath.Replace('\\', '/'));
        var rules = new JsonArray();
        var notes = new List<string>();
        var skipped = 0;
        foreach (var node in ops)
        {
            if (node is not JsonObject op) continue;
            var kind = op["op"]?.GetValue<string>() ?? string.Empty;
            var pointer = op["path"]?.GetValue<string>() ?? string.Empty;
            if (kind is not ("replace" or "add"))
            {
                skipped++;
                notes.Add($"bus 无法表达 op={kind}（路径 {pointer}）：bus 只有「按路径赋值」，没有删除/数组增删语义；" +
                          "相关改动请用 patch 格式（RFC6902）");
                continue;
            }
            if (!TryToBusPath(pointer, out var busPath, out var reason))
            {
                skipped++;
                notes.Add($"bus 路径换算失败（{pointer}）：{reason}");
                continue;
            }
            if (!TargetExistsInVanilla(vanilla, PointerToTokens(pointer)))
            {
                skipped++;
                notes.Add($"bus 无法表达 {pointer}：赋值格式只能改**已存在**的位置，而这里在原文档里还不存在" +
                          "（数组新增元素请用 patch 格式）——写进规则集会被加载器静默跳过");
                continue;
            }
            if (!op.ContainsKey("value"))
            {
                skipped++;
                notes.Add($"bus 无法表达缺 value 的 {kind}（{pointer}）");
                continue;
            }
            var value = op["value"];
            rules.Add(new JsonObject
            {
                ["name"] = $"{(string.IsNullOrWhiteSpace(label) ? "LME" : label)} · {busPath}",
                ["files"] = new JsonArray(fileName),
                ["path"] = busPath,
                ["replacements"] = new JsonArray(new JsonObject { ["set"] = value?.DeepClone() }),
            });
        }
        // 有操作表达不了 → **整份不产出**：半份规则集会让加载器改一半、用户以为全改了，
        // 这比「没有这个格式」危险得多（铁律：不静默丢改动）。
        if (skipped > 0)
        {
            notes.Insert(0, $"这条改动有 {skipped} 个操作无法用 bus 表达，**未生成规则集**（宁缺勿错）；" +
                            "完整改动请用 patch 格式（RFC6902），它可以表达全部操作");
            return new(LangExportFormat.Bus, null, notes);
        }
        if (rules.Count == 0)
        {
            notes.Add("这条改动没有任何可写入 bus 的操作，未生成规则集");
            return new(LangExportFormat.Bus, null, notes);
        }

        var document = new JsonObject
        {
            ["format"] = BusFormat,
            ["version"] = BusVersion,
            ["name"] = string.IsNullOrWhiteSpace(label) ? fileName : $"{label} · {fileName}",
            ["files"] = new JsonArray(fileName),
            ["rules"] = rules,
        };
        return new(LangExportFormat.Bus, document.ToJsonString(WriteOptions), notes);
    }

    /// <summary>
    /// RFC6901 指针（<c>/dataList/3/name</c>）→ bus 路径（<c>dataList[3].name</c>）。
    /// 段语法必须落在加载器接受的范围内（键段 + <c>[数字]</c>），否则返回 false 并给原因。
    /// </summary>
    public static bool TryToBusPath(string pointer, out string busPath, out string reason)
    {
        busPath = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrEmpty(pointer)) { reason = "空路径（整文档替换无法用字段级规则表达）"; return false; }
        if (!pointer.StartsWith('/')) { reason = "不是 JSON Pointer"; return false; }
        var tokens = pointer[1..].Split('/').Select(x => x.Replace("~1", "/").Replace("~0", "~")).ToArray();
        var segments = new List<string>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token == "-") { reason = "数组追加（-）无法定位到具体元素"; return false; }
            if (int.TryParse(token, out var index))
            {
                if (segments.Count == 0) { reason = "路径以数组下标开头（根是数组，bus 规则集需要键段）"; return false; }
                segments[^1] += $"[{index}]";
                continue;
            }
            if (!Regex.IsMatch(token, "^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                reason = $"键段「{token}」不是 A-Za-z0-9_ 形式，bus 路径语法表示不了";
                return false;
            }
            segments.Add(token);
        }
        if (segments.Count == 0) { reason = "路径为空"; return false; }
        busPath = string.Join('.', segments);
        return true;
    }

    /// <summary>RFC6901 指针 → 段（去掉转义；不做语法校验，校验由各格式自己的方法负责）。</summary>
    private static string[] PointerToTokens(string pointer)
        => pointer.Length == 0 || !pointer.StartsWith('/')
            ? []
            : pointer[1..].Split('/').Select(x => x.Replace("~1", "/").Replace("~0", "~")).ToArray();

    /// <summary>
    /// 该指针在<b>原文档</b>里是否指向一个已存在的位置。
    ///
    /// <para><b>为什么必须有这道门</b>：bus 与 pathset 的语义都是「按路径赋值」——
    /// 路径在目标文档里不存在时加载器会<b>静默跳过</b>（bus 的 <c>_resolve_paths</c> 返回空、
    /// pathset 的 jsonpatch <c>add</c> 下标越界即失败），用户看到的是「规则集写出来了但游戏里没变」。
    /// 因此这里只放行「原文档已存在」的位置；新增（含数组新增元素）一律交给 patch 格式。</para>
    /// </summary>
    private static bool TargetExistsInVanilla(JsonNode? vanilla, string[] tokens)
    {
        if (vanilla is null) return false;
        if (tokens.Length == 0) return false; // 整文档替换：赋值格式表达不了
        var current = vanilla;
        for (var i = 0; i < tokens.Length; i++)
        {
            switch (current)
            {
                case JsonObject obj when obj.TryGetPropertyValue(tokens[i], out var child):
                    current = child;
                    break;
                case JsonArray array when int.TryParse(tokens[i], out var index) && index >= 0 && index < array.Count:
                    current = array[index];
                    break;
                default:
                    return false; // 该级不存在（含数组下标越界）
            }
        }
        return true; // 路径存在（末值即使是 JSON null 也算存在）
    }

    // ── patch（RFC6902 patchs 文档）──────────────────────────────────

    private static LangFormatResult BuildPatch(string patchKey, JsonArray ops)
    {
        if (ops.Count == 0)
            return new(LangExportFormat.Patch, null, ["与官方版本无差异"]);
        var document = new JsonObject
        {
            ["patchs"] = new JsonObject { [patchKey] = JsonNode.Parse(ops.ToJsonString())!.AsArray() },
        };
        return new(LangExportFormat.Patch, document.ToJsonString(WriteOptions), []);
    }

    // ── pathset（覆盖清单）───────────────────────────────────────────

    private static LangFormatResult BuildPathset(string relativePath, string? label, JsonNode? vanilla, JsonArray ops)
    {
        if (ops.Count == 0)
            return new(LangExportFormat.Pathset, null, ["与官方版本无差异"]);

        var fileName = Path.GetFileName(relativePath.Replace('\\', '/'));
        var pathset = new JsonObject();
        var notes = new List<string>();
        var skipped = 0;
        foreach (var node in ops)
        {
            if (node is not JsonObject op) continue;
            var kind = op["op"]?.GetValue<string>() ?? string.Empty;
            var pointer = op["path"]?.GetValue<string>() ?? string.Empty;
            if (kind is not ("replace" or "add"))
            {
                skipped++;
                notes.Add($"pathset 无法表达 op={kind}（{pointer}）");
                continue;
            }
            if (!TryToPathsetPath(pointer, out var path, out var reason))
            {
                skipped++;
                notes.Add($"pathset 路径换算失败（{pointer}）：{reason}");
                continue;
            }
            if (!TargetExistsInVanilla(vanilla, PointerToTokens(pointer)))
            {
                skipped++;
                notes.Add($"pathset 无法表达 {pointer}：覆盖清单只能改**已存在**的位置，而这里在原文档里还不存在" +
                          "（数组新增元素请用 patch 格式）——写进清单会被加载器静默跳过");
                continue;
            }
            if (!op.ContainsKey("value"))
            {
                skipped++;
                notes.Add($"pathset 无法表达缺 value 的 {kind}（{pointer}）");
                continue;
            }
            pathset[path] = op["value"]?.DeepClone();
        }
        // 与 bus 同一条原则：有操作表达不了就整份不产出（半份清单 = 用户以为全改了）。
        if (skipped > 0)
        {
            notes.Insert(0, $"这条改动有 {skipped} 个操作无法用 pathset 表达，**未生成覆盖清单**（宁缺勿错）；" +
                            "完整改动请用 patch 格式（RFC6902）");
            return new(LangExportFormat.Pathset, null, notes);
        }
        if (pathset.Count == 0)
        {
            notes.Add("这条改动没有任何可写入 pathset 的操作，未生成覆盖清单");
            return new(LangExportFormat.Pathset, null, notes);
        }

        var document = new JsonObject
        {
            ["format"] = PathsetFormat,
            ["version"] = 1,
            ["name"] = string.IsNullOrWhiteSpace(label) ? fileName : $"{label} · {fileName}",
            ["files"] = new JsonArray(fileName),
            ["pathset"] = pathset,
        };
        return new(LangExportFormat.Pathset, document.ToJsonString(WriteOptions), notes);
    }

    /// <summary>
    /// RFC6901 指针 → pathset 路径（<c>dataList[3].name</c>）。
    /// 段语法与加载器 <c>_pathset_to_jsonpatch</c> 完全一致（<c>key</c> 或 <c>key[数字]</c>）。
    /// </summary>
    public static bool TryToPathsetPath(string pointer, out string path, out string reason)
    {
        path = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrEmpty(pointer)) { reason = "空路径"; return false; }
        if (!pointer.StartsWith('/')) { reason = "不是 JSON Pointer"; return false; }
        var tokens = pointer[1..].Split('/').Select(x => x.Replace("~1", "/").Replace("~0", "~")).ToArray();
        var segments = new List<string>();
        foreach (var token in tokens)
        {
            if (token == "-") { reason = "数组追加（-）没有确定下标"; return false; }
            if (int.TryParse(token, out var index))
            {
                if (segments.Count == 0) { reason = "路径以数组下标开头"; return false; }
                segments[^1] += $"[{index}]";
                continue;
            }
            if (!PathSegmentPattern.IsMatch(token))
            {
                reason = $"键段「{token}」不符合加载器 pathset 语法（只接受 A-Za-z_ 开头的标识符）";
                return false;
            }
            segments.Add(token);
        }
        if (segments.Count == 0) { reason = "路径为空"; return false; }
        // 末段允许带下标；中间段也允许（加载器逐段解析）。
        path = string.Join('.', segments);
        return true;
    }
}
