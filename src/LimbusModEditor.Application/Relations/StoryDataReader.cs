using System.Text.Json;
using NLog;

namespace LimbusModEditor.Application.Relations;

/// <summary>剧情文件里的一行对白（<c>StoryData/&lt;章节&gt;.json</c> 的 <c>dataList</c> 元素）。</summary>
/// <param name="Id">文件内序号（<c>dataList[].id</c>）。</param>
/// <param name="Place">场景（<c>place</c>，可空）。</param>
/// <param name="Model">说话人模型名（<c>model</c>，可空；真实数据里是韩文角色名）。</param>
/// <param name="Teller">旁白/说话人显示名（<c>teller</c>，可空）。</param>
/// <param name="Title">标题（<c>title</c>，可空）。</param>
/// <param name="Content">正文（<c>content</c>；真实数据里这才是台词正文，不是 <c>dialog</c>）。</param>
public sealed record StoryDialogLine(
    int Id, string? Place, string? Model, string? Teller, string? Title, string? Content);

/// <summary>
/// 剧情正文读取器：把 lang 里 <c>StoryData/&lt;章节&gt;.json</c> 的 <c>dataList</c> 读成对白行。
///
/// <para><b>真实格式（实测 2026-09，LLc-CN-LCTA 共 920 个文件）</b>：
/// <c>{"dataList":[{"id":0,"place":"…","model":"…","teller":"…","title":"…","content":"…"}]}</c>。
/// 字段名叫 <c>content</c> 不是 <c>dialog</c>——早期文档/注释里写的 <c>dialog</c> 是错的，
/// 按 <c>content</c> 实现；读不到就返回空，<b>绝不猜</b>。</para>
///
/// <para><b>只读、绝不抛</b>：单个文件坏了只是这一章没正文（生成照跑），失败按 warn 留痕。</para>
/// </summary>
public static class StoryDataReader
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>剧情目录名（相对活动语言目录）。</summary>
    public const string StoryDirectory = "StoryData";

    /// <summary>
    /// 读一个剧情文件的全部对白行。目录/文件缺失、JSON 坏、结构不符时返回空列表。
    /// </summary>
    /// <param name="languageDirectory">活动语言目录（<c>lang/&lt;语言&gt;</c>）。</param>
    /// <param name="relativePath">相对语言目录的路径（如 <c>StoryData/S001A.json</c>）。</param>
    public static IReadOnlyList<StoryDialogLine> Read(string? languageDirectory, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(languageDirectory) || string.IsNullOrWhiteSpace(relativePath)) return [];
        var full = FullPath(languageDirectory, relativePath);
        if (full is null || !File.Exists(full)) return [];

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(full));
            if (!document.RootElement.TryGetProperty("dataList", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return [];

            var lines = new List<StoryDialogLine>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                lines.Add(new StoryDialogLine(
                    item.TryGetProperty("id", out var id) && id.TryGetInt32(out var n) ? n : lines.Count,
                    Text(item, "place"),
                    Text(item, "model"),
                    Text(item, "teller"),
                    Text(item, "title"),
                    Text(item, "content") ?? string.Empty));
            }
            return lines;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "读剧情正文失败（该章无正文，不是致命错误）：{0}", relativePath);
            return [];
        }
    }

    /// <summary>
    /// 由剧情文件相对路径推出<b>章节键</b>：文件名去掉扩展名，再去掉最后一个数字之后的
    /// 「分部标记」（A/B/X/I…）。例：<c>S001A</c> → <c>S001</c>、<c>1D101B</c> → <c>1D101</c>、
    /// <c>ES001B</c> → <c>ES001</c>；没有分部标记时整体当键（<c>P01011</c> → <c>P01011</c>）。
    ///
    /// <para><b>这是文件名约定（<c>AuthoritySource.LangFileName</c>）而不是 id 窗口猜测</b>：
    /// 键只由「文件名里最后一个数字之前的部分」决定，同一文件的键恒定；
    /// 不做「扫任意位置的 5 位数字去匹配人格 id」那类推断。</para>
    /// </summary>
    public static string ChapterKeyOf(string? relativePath)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath ?? string.Empty);
        if (name.Length == 0) return string.Empty;

        var last = -1;
        for (var i = 0; i < name.Length; i++)
            if (char.IsAsciiDigit(name[i])) last = i;
        return last < 0 ? name : name[..(last + 1)];
    }

    /// <summary>把语言目录与相对路径拼成绝对路径；拼不出来（非法字符）返回 null。</summary>
    private static string? FullPath(string languageDirectory, string relativePath)
    {
        try
        {
            var combined = Path.Combine(languageDirectory, relativePath.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));
            return Path.GetFullPath(combined);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Log.Debug(ex, "剧情文件路径不合法：{0}", relativePath);
            return null;
        }
    }

    private static string? Text(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
