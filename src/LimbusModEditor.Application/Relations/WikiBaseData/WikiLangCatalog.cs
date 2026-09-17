using System.Text.Json;
using NLog;

namespace LimbusModEditor.Application.Relations.WikiBaseData;

/// <summary>一条技能的本地化文本（名称/描述/硬币效果）。</summary>
/// <param name="Name">技能名（最高等级条目的 name）。</param>
/// <param name="Desc">技能描述（最高等级条目的 desc，可能为空串）。</param>
/// <param name="CoinDescs">硬币效果文本（最高等级条目 coinlist[].coindescs[].desc 的非空集合）。</param>
public sealed record WikiLangSkill(long Id, string? Name, string? Desc, IReadOnlyList<string> CoinDescs);

/// <summary>一条具名文本（被动：name/desc；来源 Passives.json）。</summary>
public sealed record WikiLangNamed(long Id, string? Name, string? Desc);

/// <summary>一条人格的本地化文本（Personalities.json：title/name/desc）。</summary>
public sealed record WikiLangPersonality(long Id, string? Title, string? Name, string? Desc);

/// <summary>
/// 维基生成用的 Lang 实体目录：<b>按文件类别</b>读游戏 Lang JSON 并按数值 id 建索引。
///
/// <para><b>为什么必须按文件类别（坑 2）</b>：同一个数字 id 在
/// <c>Skills.json</c> 与 <c>Passives.json</c> 里是完全不同的条目（1020101 既是「纵斩」
/// 也是被动「分析」）；relation-index 的 <c>xref static→lang</c> 是启发式撞数字的
/// Derived 结果，<b>禁止</b>当名称来源。本目录只按下面四类文件取：</para>
/// <list type="bullet">
/// <item>技能：<c>Skills.json</c> + <c>Skills_personality-*.json</c>（文件名前缀 <c>Skills</c>）；</item>
/// <item>被动：<c>Passives.json</c>；</item>
/// <item>人格名：<c>Personalities.json</c>；</item>
/// <item>罪孽属性中文名：<c>AttributeText.json</c>（INDIGO→傲慢 等，本地权威映射）。</item>
/// </list>
///
/// <para><b>只读</b>：Lang 目录在游戏安装目录下，全部只读。</para>
/// </summary>
public sealed class WikiLangCatalog
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly string _directory;
    private Dictionary<long, WikiLangSkill>? _skills;
    private Dictionary<long, WikiLangNamed>? _passives;
    private Dictionary<long, WikiLangPersonality>? _personalities;
    private Dictionary<string, string>? _attributeLabels;

    private WikiLangCatalog(string directory) => _directory = directory;

    /// <summary>建目录：语言目录存在才可用，否则返回 null（名称缺失时分节照常出、名称回落到数值 id）。</summary>
    public static WikiLangCatalog? TryCreate(string? languageDirectory)
    {
        if (string.IsNullOrWhiteSpace(languageDirectory) || !Directory.Exists(languageDirectory))
        {
            Log.Debug("语言目录不可用（{0}）：维基名称/描述将缺失", languageDirectory ?? "-");
            return null;
        }
        return new WikiLangCatalog(languageDirectory);
    }

    /// <summary>技能文本（<c>Skills*.json</c> 全部文件按 id 合并；同 id 只认第一个文件）。</summary>
    public WikiLangSkill? Skill(long id)
    {
        _skills ??= LoadSkills();
        return _skills.GetValueOrDefault(id);
    }

    /// <summary>被动文本（<c>Passives.json</c>）。</summary>
    public WikiLangNamed? Passive(long id)
    {
        _passives ??= LoadNamedFile("Passives.json");
        return _passives.GetValueOrDefault(id);
    }

    /// <summary>人格文本（<c>Personalities.json</c>）。</summary>
    public WikiLangPersonality? Personality(long id)
    {
        _personalities ??= LoadPersonalities();
        return _personalities.GetValueOrDefault(id);
    }

    /// <summary>罪孽属性中文名（<c>AttributeText.json</c>；INDIGO→傲慢）。查不到返回 null，调用方回落原值。</summary>
    public string? AttributeLabel(string? attributeId)
    {
        _attributeLabels ??= LoadAttributeLabels();
        return attributeId is null ? null : _attributeLabels.GetValueOrDefault(attributeId);
    }

    // ── 解析（惰性，每类文件只读一次）──────────────────────────────

    private Dictionary<long, WikiLangSkill> LoadSkills()
    {
        var result = new Dictionary<long, WikiLangSkill>();
        foreach (var name in Directory.EnumerateFiles(_directory, "Skills*.json")
                     .Select(Path.GetFileName)
                     .OfType<string>()
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            ReadEntries(Path.Combine(_directory, name), entry =>
            {
                var id = ReadInt64(entry, "id");
                if (id is null || result.ContainsKey(id.Value)) return;

                // levelList 按等级存多条（觉醒成长文本）；取最高等级那条作当前名/描述。
                if (!entry.TryGetProperty("levelList", out var levels) || levels.ValueKind != JsonValueKind.Array) return;
                JsonElement best = default;
                long bestLevel = long.MinValue;
                foreach (var level in levels.EnumerateArray())
                {
                    var lv = ReadInt64(level, "level") ?? long.MinValue;
                    if (lv >= bestLevel)
                    {
                        bestLevel = lv;
                        best = level;
                    }
                }
                if (best.ValueKind != JsonValueKind.Object) return;

                var coinDescs = new List<string>();
                if (best.TryGetProperty("coinlist", out var coins) && coins.ValueKind == JsonValueKind.Array)
                    foreach (var coin in coins.EnumerateArray())
                    {
                        if (!coin.TryGetProperty("coindescs", out var descs) || descs.ValueKind != JsonValueKind.Array) continue;
                        foreach (var desc in descs.EnumerateArray())
                        {
                            var text = ReadString(desc, "desc");
                            if (!string.IsNullOrWhiteSpace(text)) coinDescs.Add(text.Trim());
                        }
                    }

                result[id.Value] = new WikiLangSkill(
                    id.Value, ReadString(best, "name"), ReadString(best, "desc"), coinDescs);
            });
        Log.Debug("Lang 技能目录：{0} 条（Skills*.json）", result.Count);
        return result;
    }

    private Dictionary<long, WikiLangNamed> LoadNamedFile(string fileName)
    {
        var result = new Dictionary<long, WikiLangNamed>();
        ReadEntries(Path.Combine(_directory, fileName), entry =>
        {
            var id = ReadInt64(entry, "id");
            if (id is null || result.ContainsKey(id.Value)) return;
            result[id.Value] = new WikiLangNamed(id.Value, ReadString(entry, "name"), ReadString(entry, "desc"));
        });
        Log.Debug("Lang 目录 {0}：{1} 条", fileName, result.Count);
        return result;
    }

    private Dictionary<long, WikiLangPersonality> LoadPersonalities()
    {
        var result = new Dictionary<long, WikiLangPersonality>();
        ReadEntries(Path.Combine(_directory, "Personalities.json"), entry =>
        {
            var id = ReadInt64(entry, "id");
            if (id is null || result.ContainsKey(id.Value)) return;
            result[id.Value] = new WikiLangPersonality(
                id.Value, ReadString(entry, "title"), ReadString(entry, "name"), ReadString(entry, "desc"));
        });
        Log.Debug("Lang 目录 Personalities.json：{0} 条", result.Count);
        return result;
    }

    private Dictionary<string, string> LoadAttributeLabels()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ReadText(Path.Combine(_directory, "AttributeText.json"), text =>
        {
            foreach (var entry in StaticTableRows.Parse(text))
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var id = ReadString(entry, "id");
                var name = ReadString(entry, "name");
                if (id is not null && name is not null) result[id] = name;
            }
        });
        Log.Debug("Lang 目录 AttributeText.json：{0} 条", result.Count);
        return result;
    }

    // ── 小工具 ─────────────────────────────────────────────────────

    private void ReadEntries(string path, Action<JsonElement> consume)
        => ReadText(path, text =>
        {
            foreach (var entry in StaticTableRows.Parse(text))
                if (entry.ValueKind == JsonValueKind.Object)
                    consume(entry);
        });

    private void ReadText(string path, Action<string> consume)
    {
        try
        {
            if (!File.Exists(path)) return;
            consume(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Debug(ex, "读 Lang 文件失败（跳过）：{0}", path);
        }
    }

    private static long? ReadInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetInt64(out var parsed) ? parsed : null;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
