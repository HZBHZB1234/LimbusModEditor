using System.Globalization;
using System.Text.Json;
using LimbusModEditor.Application.Relations.WikiBaseData;

namespace LimbusModEditor.Application.StaticMods;

/// <summary>静态表正文里的**一行**（记录）：工作台按页展示的最小单位。</summary>
/// <param name="RecordId">
/// 记录 id：行里有 <c>id</c> 字段就用它（数值/字符串原样取），没有就退回<b>行下标</b>
/// ——下标在「同一次解析」里稳定，后续 read/edit 也按同一口径回表。
/// </param>
/// <param name="Summary">一行摘要（id + 名称类字段），键列表里扫一眼能认出是哪条。</param>
/// <param name="RawJson">该行的原始 JSON（编辑器直接吃它建树），不做裁剪。</param>
public sealed record StaticTableRecord(string RecordId, string Summary, string RawJson);

/// <summary>
/// 静态表正文 → 记录分页。只做「行集合 → 记录」这一层，<b>不重写解析</b>：
/// 行集合沿用既有的 <see cref="StaticTableRows.Parse"/>（认 <c>{"list":[…]}</c> /
/// 顶层数组 / 单对象三种形态）。
/// </summary>
public static class StaticTableRecords
{
    /// <summary>单页上限：一张表可能有上万行，前端一页最多取这么多。</summary>
    public const int MaxTake = 500;

    /// <summary>UTF-8 BOM：正文开头带它时 JSON 解析器会直接失败，必须先剥掉。</summary>
    public const char Bom = '\uFEFF';

    /// <summary>解析正文得到全部行（解析失败 → 空集合，不抛）。</summary>
    /// <remarks>
    /// 部分正文带 UTF-8 BOM（<c>\uFEFF</c>）开头，<see cref="JsonDocument.Parse"/> 会直接
    /// JsonException —— 那会让**整张表**被当成「解析不出记录」。这里先剥掉 BOM 再交给既有解析器。
    /// </remarks>
    public static IReadOnlyList<JsonElement> Rows(string? text)
        => StaticTableRows.Parse(text?.TrimStart(Bom));

    /// <summary>按 <paramref name="offset"/> / <paramref name="take"/> 取一页记录。</summary>
    public static IReadOnlyList<StaticTableRecord> Page(
        IReadOnlyList<JsonElement> rows, int offset, int take)
    {
        var start = Math.Max(0, offset);
        var count = Math.Clamp(take <= 0 ? 200 : take, 1, MaxTake);
        var page = new List<StaticTableRecord>(Math.Min(count, rows.Count));
        for (var i = start; i < rows.Count && page.Count < count; i++)
            page.Add(Describe(rows[i], i));
        return page;
    }

    /// <summary>
    /// 按记录 id 定位行下标（找不到返回 -1）。口径与 <see cref="Describe"/> 完全一致：
    /// 行里有 <c>id</c> 就比 id，没有就比行下标 —— 保证「列表里看到的那条」就是
    /// 「read / edit 改到的那条」。
    /// </summary>
    public static int IndexOf(IReadOnlyList<JsonElement> rows, string? recordId)
    {
        if (string.IsNullOrWhiteSpace(recordId)) return -1;
        for (var i = 0; i < rows.Count; i++)
        {
            var id = IdOf(rows[i]) ?? i.ToString(CultureInfo.InvariantCulture);
            if (string.Equals(id, recordId, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>单行 → 记录。</summary>
    public static StaticTableRecord Describe(JsonElement row, int index)
    {
        var id = IdOf(row) ?? index.ToString(CultureInfo.InvariantCulture);
        return new StaticTableRecord(id, SummaryOf(row, id), row.GetRawText());
    }

    /// <summary>行主键：优先 <c>id</c> 字段；没有则为 null（调用方退回下标）。</summary>
    private static string? IdOf(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        if (!row.TryGetProperty("id", out var id)) return null;
        return id.ValueKind switch
        {
            JsonValueKind.Number => id.GetRawText(),
            JsonValueKind.String => id.GetString(),
            _ => null,
        };
    }

    /// <summary>
    /// 摘要 = <c>id</c> + 一个能认出这行的字段（名称类优先，否则取第一个标量字段）。
    /// 只有 id 也认不出时就不补字段——不编造内容。
    /// </summary>
    private static string SummaryOf(JsonElement row, string id)
    {
        var parts = new List<string> { $"id={id}" };
        var label = FirstString(row, "name", "title", "displayName", "keyword");
        if (label is not null)
        {
            parts.Add(Trim(label));
        }
        else if (row.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in row.EnumerateObject())
            {
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) continue;
                if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase)) continue;
                parts.Add($"{property.Name}={Trim(property.Value.ToString())}");
                break;
            }
        }
        return string.Join(" · ", parts);
    }

    private static string? FirstString(JsonElement row, params string[] names)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!row.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        }
        return null;
    }

    private static string Trim(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var text = value.ReplaceLineEndings(" ");
        return text.Length <= 60 ? text : string.Concat(text.AsSpan(0, 60), "…");
    }
}
