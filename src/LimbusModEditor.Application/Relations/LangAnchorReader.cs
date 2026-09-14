using System.Text.Json;
using NLog;

namespace LimbusModEditor.Application.Relations;

/// <summary>
/// 从活动语言目录里读出「锚点」——即「一个 id 对应的可展示内容」：
/// 语音台词（<c>id → dlg</c>，实测**样本名与这个 id 同名同值**，所以音频能精确对上台词）、
/// 被动 / EGO / 技能（数值 id → 中文 <c>name</c>/<c>desc</c>，静态表的数值外键靠它解出中文）。
///
/// <para><b>只读这三类文件，不遍历全部 2048 个 lang 文件</b>：锚点只存在于
/// <c>PersonalityVoiceDlg/</c>、<c>EGOVoiceDig/</c> 与语言根下的
/// <c>Passives.json</c> / <c>Egos.json</c> / <c>Skills.json</c>（实测路径与结构，2026-09）。
/// 其余（<c>StoryData/**</c> 920 个文件等）是按局部节点序号组织、没有稳定实体 id，
/// 硬扫只会产生假阳性。</para>
///
/// <para><b>绝不抛</b>：单个文件坏了只是少一批锚点（分析器在任何输入缺档时都能跑完），
/// 失败按 warn 留痕。</para>
/// </summary>
public static class LangAnchorReader
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>语音子目录（相对语言目录）。</summary>
    private static readonly string[] VoiceDirectories = ["PersonalityVoiceDlg", "EGOVoiceDig"];

    /// <summary>语言根下带数值 id 的表（文件名 → 锚点类别）。</summary>
    private static readonly (string FileName, string Table)[] EntityTables =
    [
        ("Passives.json", RelationAnchorTables.Passive),
        ("Skills.json", RelationAnchorTables.Skill),
        ("Egos.json", RelationAnchorTables.Ego),
    ];

    /// <summary>
    /// 语言根下前缀匹配的实体表。E.G.O 饰品实测有 26 个文件
    /// （<c>EGOgift_MirrorDungeon.json</c> / <c>EGOgift_StoryDungeon-*.json</c> …），
    /// 没有单一入口文件，所以只能按前缀收。
    /// </summary>
    private const string EgoGiftPrefix = "EGOgift";

    /// <summary>读全部锚点；语言目录为空/不存在时返回空列表。</summary>
    public static IReadOnlyList<RelationTextAnchor> Read(string? languageDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(languageDirectory) || !Directory.Exists(languageDirectory)) return [];

        var anchors = new List<RelationTextAnchor>();
        foreach (var directory in VoiceDirectories)
        {
            var path = Path.Combine(languageDirectory, directory);
            if (!Directory.Exists(path)) continue;
            foreach (var file in EnumerateJson(path, cancellationToken))
                ReadVoiceFile(file, Path.GetRelativePath(languageDirectory, file), anchors);
        }

        foreach (var (name, table) in EntityTables)
        {
            var file = Path.Combine(languageDirectory, name);
            if (File.Exists(file)) ReadEntityFile(file, name, table, anchors);
        }

        foreach (var file in EnumerateJson(languageDirectory, cancellationToken))
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith(EgoGiftPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (EntityTables.Any(x => string.Equals(x.FileName, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            ReadEntityFile(file, name, RelationAnchorTables.EgoGift, anchors);
        }

        return anchors;
    }

    private static IEnumerable<string> EnumerateJson(string directory, CancellationToken cancellationToken)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "枚举语言目录失败：{0}", directory);
            yield break;
        }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    /// <summary>语音文件：<c>{"dataList":[{"id","desc","dlg"}]}</c>（实测）。</summary>
    private static void ReadVoiceFile(string file, string relativePath, List<RelationTextAnchor> anchors)
    {
        using var document = TryParse(file);
        if (document is null) return;
        foreach (var entry in DataList(document.RootElement))
        {
            var id = ReadString(entry, "id");
            if (string.IsNullOrEmpty(id)) continue;
            anchors.Add(new RelationTextAnchor(id, relativePath, null, ReadString(entry, "desc"),
                ReadString(entry, "dlg"), RelationAnchorTables.Voice));
        }
    }

    /// <summary>
    /// 实体表：<c>Passives.json</c> / <c>Egos.json</c> 是 <c>{id,name,desc}</c>；
    /// <c>Skills.json</c> 把中文放在 <c>levelList[0].name/desc</c>；
    /// <c>EGOgift*.json</c> 是 <c>{id,name,desc,simpleDesc[]}</c>（均为实测）。
    /// </summary>
    private static void ReadEntityFile(string file, string relativePath, string table, List<RelationTextAnchor> anchors)
    {
        using var document = TryParse(file);
        if (document is null) return;
        foreach (var entry in DataList(document.RootElement))
        {
            if (!entry.TryGetProperty("id", out var idProperty)) continue;
            var id = idProperty.ValueKind switch
            {
                JsonValueKind.Number => idProperty.GetRawText(),
                JsonValueKind.String => idProperty.GetString(),
                _ => null,
            };
            if (string.IsNullOrEmpty(id)) continue;

            var name = ReadString(entry, "name");
            var desc = ReadString(entry, "desc");
            if ((name is null || desc is null) && entry.TryGetProperty("levelList", out var levels)
                && levels.ValueKind == JsonValueKind.Array && levels.GetArrayLength() > 0)
            {
                var first = levels[0];
                name ??= ReadString(first, "name");
                desc ??= ReadString(first, "desc");
            }
            anchors.Add(new RelationTextAnchor(id, relativePath, name, desc, null, table));
        }
    }

    private static IEnumerable<JsonElement> DataList(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) yield break;
        if (!root.TryGetProperty("dataList", out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var entry in list.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.Object) yield return entry;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    /// <summary>解析一个 JSON 文件；失败返回 null（warn 留痕，绝不让分析挂掉）。</summary>
    private static JsonDocument? TryParse(string file)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn(ex, "读 lang 锚点文件失败（本文件不产出锚点）：{0}", file);
            return null;
        }
    }
}
