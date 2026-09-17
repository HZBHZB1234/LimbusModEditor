using System.Globalization;
using System.Text.Json;
using LimbusModEditor.Application.Relations.Authority;

namespace LimbusModEditor.Application.Relations.WikiBaseData;

/// <summary>
/// 人格页「数据 / 技能 / 被动」三节的<b>内容构建器</b>。
///
/// <para><b>取数口径（逐条来自调查报告 <c>base-data-sources.md</c> §5 的映射表）</b>：</para>
/// <list type="bullet">
/// <item>基础数值：扫 16 张 <c>personality-*</c> 表按行 <c>id == 人格数值 id</c> 精确匹配
/// （坑 1：不信文件级 links）；体力 <c>hp.defaultStat + incrementByLevel</c>（注明按等级推导）、
/// 速度 <c>min/maxSpeedList</c>（按理智档位）、防御修正、抗性（斩击/突刺/打击倍率）、
/// 混乱阈值分段 <c>breakSection.sectionList</c>、属性/稀有度/赛季、
/// 登场时间 <c>updatedDate</c>（<c>yyyymmdd</c> → <c>yyyy.MM.dd</c>）。</item>
/// <item>技能：槽位来自人格行 <c>attributeList</c>（skillId+数量）与 <c>defenseSkillIDList</c>；
/// 数值按 skillId 回 <c>personality-skill-*</c> 表行级匹配（觉醒等级分档的威力/硬币）；
/// 名称/描述按 Lang <c>Skills*.json</c> 文件类别取（坑 2：禁 xref）。</item>
/// <item>被动：<c>personality-passive-*</c> 表按 <c>personalityID</c> 行级匹配出
/// 战斗/支援被动与解锁等级；共鸣条件回 <c>passive</c> 表；名称/描述取 Lang <c>Passives.json</c>。</item>
/// </list>
///
/// <para><b>口径</b>：推不出来就不落条目、不占位、不编造（缺哪行就少哪行）；
/// 人格<b>没有</b>独立「攻击力」字段——伤害在技能的威力/硬币里，不造人格级数字。
/// 每条带 <c>Authority/Confidence/SourceDetail</c>，正文是格式化摘要 → 只读
/// （<c>WritableSource=None</c>）。</para>
/// </summary>
public static class PersonaBaseDataSections
{
    /// <summary>构建结果：<paramref name="BaseData"/>/<paramref name="Skills"/>/<paramref name="Passives"/>
    /// 是三节的条目（可为空集 = 该节没有本地数据，不建）。</summary>
    public sealed record Result(
        IReadOnlyList<PreparedEntry> BaseData,
        IReadOnlyList<PreparedEntry> Skills,
        IReadOnlyList<PreparedEntry> Passives,
        string SourceTables);

    /// <summary>未落 id 的条目（id 在合并进页面树时按稳定 id 规则统一分配）。</summary>
    /// <param name="Authority">权威来源（<see cref="AuthoritySource"/> 枚举名）：
    /// 数值行 = StaticTableForeignKey（主键精确匹配）；纯 Lang 文本 = LangAuthoritativeList（权威 id 清单）。</param>
    public sealed record PreparedEntry(
        string Key, string Title, string Body, string SourceDetail, string Authority);

    private const string PersonalityKey = "id";
    private const string PassiveOwnerKey = "personalityID";

    /// <summary>
    /// 按人格数值 id（如 10201）构建三节条目。人格行都找不到（本地没这份数据）→ 返回 null。
    /// </summary>
    public static Result? TryBuild(long personaId, StaticRowCatalog statics, WikiLangCatalog lang)
    {
        var (personality, personalityTable) = FindRow(statics, "personality-", PersonalityKey, personaId);
        if (personality is null || personalityTable is null) return null;
        var tableNames = new List<string> { personalityTable };

        var baseData = BuildBaseData(personaId, personality.Value, personalityTable, lang);
        var (skills, skillTables) = BuildSkills(personality.Value, statics, lang);
        var (passives, passiveTables) = BuildPassives(personaId, statics, lang);
        tableNames.AddRange(skillTables);
        tableNames.AddRange(passiveTables);

        return new Result(baseData, skills, passives, string.Join(" · ", tableNames.Distinct()));
    }

    // ── 数据节：基础数值 ───────────────────────────────────────────

    private static IReadOnlyList<PreparedEntry> BuildBaseData(
        long personaId, JsonElement row, string tableName, WikiLangCatalog lang)
    {
        var entries = new List<PreparedEntry>();
        string TableDetail() => $"{tableName} 行 {PersonalityKey}={personaId}（静态表行级匹配）";

        // 人格名/称号：Personalities.json（本地有才写；名通常是页面标题本身，这里补称号与描述出处）。
        var personalityText = lang.Personality(personaId);
        if (personalityText?.Title is { } title && title.Trim().Length > 0)
            entries.Add(new PreparedEntry("base:title", "称号", OneLine(title),
                $"Lang Personalities.json id={personaId}；数值行：" + TableDetail(),
                nameof(AuthoritySource.LangAuthoritativeList)));
        if (personalityText?.Desc is { } desc && desc.Trim().Length > 0)
            entries.Add(new PreparedEntry("base:desc", "人格描述", OneLine(desc),
                $"Lang Personalities.json id={personaId}",
                nameof(AuthoritySource.LangAuthoritativeList)));

        // 登场时间：行内 updatedDate（yyyymmdd 整数）→ yyyy.MM.dd。
        // 16 张 personality-* 表 185 行里唯一的日期列且 185/185 有值，与真实维基「登场时间」一致。
        if (FormatDate(row, "updatedDate") is { } debut)
            entries.Add(new PreparedEntry("base:debut", "登场时间", debut, TableDetail(),
                nameof(AuthoritySource.StaticTableForeignKey)));

        // 体力：defaultStat + incrementByLevel（游戏按等级成长 —— 注明推导，不造具体等级值）。
        if (row.TryGetProperty("hp", out var hp) && hp.ValueKind == JsonValueKind.Object)
        {
            var bodyParts = new List<string>();
            if (hp.TryGetProperty("defaultStat", out var def) && def.ValueKind == JsonValueKind.Number)
                bodyParts.Add($"初始 {FormatNumber(def)}");
            if (hp.TryGetProperty("incrementByLevel", out var inc) && inc.ValueKind == JsonValueKind.Number)
                bodyParts.Add($"每级 +{FormatNumber(inc)}（按等级推导：初始 + (等级-1) × 每级增量）");
            if (bodyParts.Count > 0)
                entries.Add(new PreparedEntry("base:hp", "体力", string.Join("，", bodyParts), TableDetail(),
                    nameof(AuthoritySource.StaticTableForeignKey)));
        }

        // 速度：min/maxSpeedList 按理智档位成对展示。
        var speeds = PairSpeed(row, "minSpeedList", "maxSpeedList");
        if (speeds is not null)
            entries.Add(new PreparedEntry("base:speed", "速度", speeds + "（数组按理智档位）", TableDetail(),
                nameof(AuthoritySource.StaticTableForeignKey)));

        if (row.TryGetProperty("defCorrection", out var defCorrection) && defCorrection.ValueKind == JsonValueKind.Number)
            entries.Add(new PreparedEntry("base:def", "防御修正", FormatNumber(defCorrection), TableDetail(),
                nameof(AuthoritySource.StaticTableForeignKey)));

        // 抗性：SLASH/PENETRATE/HIT → 斩击/突刺/打击（倍率原样）。
        if (row.TryGetProperty("resistInfo", out var resist) &&
            resist.TryGetProperty("atkResistList", out var resists) && resists.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in resists.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var type)) continue;
                if (!item.TryGetProperty("value", out var value)) continue;
                parts.Add($"{AttackTypeLabel(type.GetString())} ×{FormatNumber(value)}");
            }
            if (parts.Count > 0)
                entries.Add(new PreparedEntry("base:resist", "抗性", string.Join(" / ", parts), TableDetail(),
                    nameof(AuthoritySource.StaticTableForeignKey)));
        }

        // 混乱阈值分段。
        if (row.TryGetProperty("breakSection", out var breakSection) &&
            breakSection.TryGetProperty("sectionList", out var sections) && sections.ValueKind == JsonValueKind.Array)
        {
            var values = sections.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.Number)
                .Select(v => FormatNumber(v)).ToList();
            if (values.Count > 0)
                entries.Add(new PreparedEntry("base:break", "混乱阈值分段", string.Join(" / ", values), TableDetail(),
                    nameof(AuthoritySource.StaticTableForeignKey)));
        }

        // 属性：uniqueAttribute → AttributeText.json 的中文名（本地权威映射，查不到回落原值）。
        if (row.TryGetProperty("uniqueAttribute", out var attribute) && attribute.ValueKind == JsonValueKind.String)
        {
            var raw = attribute.GetString();
            var label = lang.AttributeLabel(raw);
            var text = label is null ? raw : $"{label}（{raw}）";
            entries.Add(new PreparedEntry("base:attribute", "属性", text!,
                TableDetail() + "；中文名取自 Lang AttributeText.json",
                nameof(AuthoritySource.StaticTableForeignKey)));
        }

        // 稀有度/赛季：本地只有数值枚举，没有可信的中文映射 —— 原样展示，不编。
        if (row.TryGetProperty("rank", out var rank) && rank.ValueKind == JsonValueKind.Number)
            entries.Add(new PreparedEntry("base:rank", "稀有度（rank）", FormatNumber(rank), TableDetail(),
                nameof(AuthoritySource.StaticTableForeignKey)));
        if (row.TryGetProperty("season", out var season) && season.ValueKind == JsonValueKind.Number)
            entries.Add(new PreparedEntry("base:season", "赛季（season）", FormatNumber(season), TableDetail(),
                nameof(AuthoritySource.StaticTableForeignKey)));

        return entries;
    }

    // ── 技能节 ─────────────────────────────────────────────────────

    private static (IReadOnlyList<PreparedEntry>, IReadOnlyList<string>) BuildSkills(
        JsonElement personalityRow, StaticRowCatalog statics, WikiLangCatalog lang)
    {
        var entries = new List<PreparedEntry>();
        var usedTables = new List<string>();

        void AddSkill(long skillId, int? slotCount, bool isDefense, string slotSource)
        {
            var (skillRow, tableName) = FindRow(statics, "personality-skill-", PersonalityKey, skillId);
            var langSkill = lang.Skill(skillId);
            if (skillRow is null && langSkill is null) return; // 数值与文本都缺 → 不占位
            if (tableName is not null) usedTables.Add(tableName);

            var title = langSkill?.Name ?? $"技能 {skillId}";
            if (isDefense) title += "（防御）";
            else if (slotCount is > 1) title += $" ×{slotCount}";

            var body = new List<string>();
            if (!string.IsNullOrWhiteSpace(langSkill?.Desc)) body.Add(OneLine(langSkill!.Desc!));
            if (langSkill?.CoinDescs.Count > 0)
                body.Add("硬币效果：" + string.Join("；", langSkill.CoinDescs));

            if (skillRow is not null)
            {
                // 数值行：skillData 按觉醒等级（gaksungLevel）分档，字段是「稀疏覆盖」——
                // 只展示各等级显式给出的项，未给出的不外推。
                var lines = new List<string>();
                string? attributeType = null, atkType = null, defType = null;
                if (skillRow.Value.TryGetProperty("skillData", out var skillData) &&
                    skillData.ValueKind == JsonValueKind.Array)
                {
                    foreach (var level in skillData.EnumerateArray())
                    {
                        var parts = new List<string>();
                        if (level.TryGetProperty("gaksungLevel", out var gaksung) &&
                            gaksung.ValueKind == JsonValueKind.Number)
                            parts.Add($"觉醒{FormatNumber(gaksung)}");
                        if (level.TryGetProperty("defaultValue", out var defaultValue) &&
                            defaultValue.ValueKind == JsonValueKind.Number)
                            parts.Add($"基础威力 {FormatNumber(defaultValue)}");
                        if (level.TryGetProperty("coinList", out var coins) && coins.ValueKind == JsonValueKind.Array)
                        {
                            var scales = new List<string>();
                            foreach (var coin in coins.EnumerateArray())
                                if (coin.TryGetProperty("scale", out var scale) && scale.ValueKind == JsonValueKind.Number)
                                    scales.Add(FormatNumber(scale));
                            if (scales.Count > 0)
                                parts.Add($"硬币 ×{scales.Count}（{string.Join("/", scales)}）");
                        }
                        if (parts.Count > 1) lines.Add(parts[0] + "：" + string.Join("，", parts.Skip(1)));
                        if (attributeType is null && level.TryGetProperty("attributeType", out var a) &&
                            a.ValueKind == JsonValueKind.String) attributeType = a.GetString();
                        if (atkType is null && level.TryGetProperty("atkType", out var t) &&
                            t.ValueKind == JsonValueKind.String) atkType = t.GetString();
                        if (defType is null && level.TryGetProperty("defType", out var d) &&
                            d.ValueKind == JsonValueKind.String) defType = d.GetString();
                    }
                }

                var meta = new List<string>();
                if (attributeType is not null)
                {
                    var label = lang.AttributeLabel(attributeType);
                    meta.Add(label is null ? attributeType : $"{label}（{attributeType}）");
                }
                if (atkType is not null && atkType != "NONE") meta.Add(AttackTypeLabel(atkType));
                if (defType is not null && defType != "ATTACK") meta.Add($"防御类型 {defType}");
                if (meta.Count > 0) body.Insert(0, string.Join(" · ", meta));
                if (lines.Count > 0) body.Add(string.Join("；", lines));
            }

            if (slotCount is > 1)
                body.Add($"槽位数量 ×{slotCount}（{slotSource}）");
            else
                body.Add($"槽位来源：{slotSource}");

            var sourceDetail = new List<string>();
            if (tableName is not null) sourceDetail.Add($"{tableName} 行 id={skillId}");
            if (langSkill is not null) sourceDetail.Add("名称/文本取自 Lang Skills*.json");
            entries.Add(new PreparedEntry(
                $"skill:{skillId}", title, string.Join("\n", body),
                string.Join("；", sourceDetail),
                skillRow is not null
                    ? nameof(AuthoritySource.StaticTableForeignKey)
                    : nameof(AuthoritySource.LangAuthoritativeList)));
        }

        // 槽位：attributeList（skillId + number=数量）+ defenseSkillIDList（防御技）。
        if (personalityRow.TryGetProperty("attributeList", out var attributeList) &&
            attributeList.ValueKind == JsonValueKind.Array)
        {
            foreach (var slot in attributeList.EnumerateArray())
            {
                if (!slot.TryGetProperty("skillId", out var skillId) || skillId.ValueKind != JsonValueKind.Number) continue;
                if (!skillId.TryGetInt64(out var id)) continue;
                int? number = slot.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number
                    ? (int)n.GetDouble()
                    : null;
                AddSkill(id, number, isDefense: false, "attributeList");
            }
        }

        if (personalityRow.TryGetProperty("defenseSkillIDList", out var defenseList) &&
            defenseList.ValueKind == JsonValueKind.Array)
        {
            foreach (var defense in defenseList.EnumerateArray())
            {
                if (defense.ValueKind != JsonValueKind.Number || !defense.TryGetInt64(out var id)) continue;
                AddSkill(id, null, isDefense: true, "defenseSkillIDList");
            }
        }

        return (entries, usedTables);
    }

    // ── 被动节 ─────────────────────────────────────────────────────

    private static (IReadOnlyList<PreparedEntry>, IReadOnlyList<string>) BuildPassives(
        long personaId, StaticRowCatalog statics, WikiLangCatalog lang)
    {
        var entries = new List<PreparedEntry>();
        var usedTables = new List<string>();

        var (ownerRow, ownerTable) = FindRow(statics, "personality-passive-", PassiveOwnerKey, personaId);
        if (ownerRow is null) return (entries, usedTables);
        usedTables.Add(ownerTable!);

        void AddPassive(long passiveId, int unlockLevel, bool isSupport)
        {
            var langPassive = lang.Passive(passiveId);
            var (passiveRow, passiveTable) = FindRow(statics, "passive", PersonalityKey, passiveId);

            var title = langPassive?.Name ?? $"被动 {passiveId}";
            var body = new List<string>();
            if (!string.IsNullOrWhiteSpace(langPassive?.Desc)) body.Add(OneLine(langPassive!.Desc!));

            // 共鸣条件：passive 表行内的 attributeResonanceCondition（INDIGO×2 之类）。
            if (passiveRow is not null &&
                passiveRow.Value.TryGetProperty("attributeResonanceCondition", out var conditions) &&
                conditions.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var condition in conditions.EnumerateArray())
                {
                    if (!condition.TryGetProperty("type", out var type)) continue;
                    if (!condition.TryGetProperty("value", out var value)) continue;
                    var raw = type.GetString();
                    var label = lang.AttributeLabel(raw);
                    parts.Add($"{(label ?? raw)} ×{FormatNumber(value)}");
                }
                if (parts.Count > 0) body.Add("共鸣条件：" + string.Join("，", parts));
            }

            body.Add(isSupport
                ? $"支援被动 · 人格等级 {unlockLevel} 解锁"
                : $"战斗被动 · 人格等级 {unlockLevel} 解锁");

            var sourceDetail = new List<string>
            {
                $"{ownerTable} 行 {PassiveOwnerKey}={personaId}（战斗/支援被动与解锁等级）",
            };
            if (passiveTable is not null) sourceDetail.Add($"{passiveTable} 行 id={passiveId}（共鸣条件）");
            if (langPassive is not null) sourceDetail.Add("名称/描述取自 Lang Passives.json");

            entries.Add(new PreparedEntry(
                $"passive:{passiveId}", title, string.Join("\n", body),
                string.Join("；", sourceDetail),
                passiveRow is not null
                    ? nameof(AuthoritySource.StaticTableForeignKey)
                    : nameof(AuthoritySource.LangAuthoritativeList)));
        }

        foreach (var (listName, isSupport) in new[] { ("battlePassiveList", false), ("supporterPassiveList", true) })
        {
            if (!ownerRow.Value.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var group in list.EnumerateArray())
            {
                var level = group.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number
                    ? (int)lv.GetDouble()
                    : 0;
                if (!group.TryGetProperty("passiveIDList", out var ids) || ids.ValueKind != JsonValueKind.Array) continue;
                foreach (var id in ids.EnumerateArray())
                {
                    if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var passiveId)) continue;
                    AddPassive(passiveId, level, isSupport);
                }
            }
        }

        return (entries, usedTables);
    }

    // ── 行查找与小工具 ─────────────────────────────────────────────

    /// <summary>在表名前缀匹配的一组表里按主键找行（返回行 + 命中表名）。找得到行才有效（坑 1：行级回表）。</summary>
    private static (JsonElement?, string?) FindRow(
        StaticRowCatalog statics, string tableNamePrefix, string keyProperty, long key)
    {
        foreach (var tableName in statics.TableNames(tableNamePrefix))
        {
            var row = statics.Row(tableName, keyProperty, key);
            if (row is not null) return (row, tableName);
        }
        return (null, null);
    }

    private static string? PairSpeed(JsonElement row, string minProperty, string maxProperty)
    {
        if (!row.TryGetProperty(minProperty, out var min) || min.ValueKind != JsonValueKind.Array) return null;
        if (!row.TryGetProperty(maxProperty, out var max) || max.ValueKind != JsonValueKind.Array) return null;

        var pairs = new List<string>();
        var count = Math.Min(min.GetArrayLength(), max.GetArrayLength());
        for (var i = 0; i < count; i++)
        {
            var lo = min[i];
            var hi = max[i];
            if (lo.ValueKind != JsonValueKind.Number || hi.ValueKind != JsonValueKind.Number) continue;
            pairs.Add($"{FormatNumber(lo)}–{FormatNumber(hi)}");
        }
        return pairs.Count > 0 ? string.Join(" / ", pairs) : null;
    }

    private static string AttackTypeLabel(string? type) => type switch
    {
        "SLASH" => "斩击",
        "PENETRATE" => "突刺",
        "HIT" => "打击",
        "GUARD" => "防御（防护）",
        "EVADE" => "防御（闪避）",
        "COUNTER" => "防御（反击）",
        _ => type ?? "-",
    };

    private static string FormatNumber(JsonElement value)
    {
        var raw = value.GetRawText();
        // 整数不带小数点（84 不是 84.0）；小数保留原样（2.9、0.5）。
        return double.TryParse(raw, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(number % 1 == 0 ? "0" : "0.###", CultureInfo.InvariantCulture)
            : raw;
    }

    /// <summary>
    /// 静态表日期列（<c>yyyymmdd</c> 整数，如 20230227）→ <c>yyyy.MM.dd</c>。
    /// 缺列 / 非数字 / 不是合法日期 → null（不占位、不外推）。
    /// </summary>
    internal static string? FormatDate(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var raw)) return null;
        if (!DateOnly.TryParseExact(raw.ToString("00000000", CultureInfo.InvariantCulture), "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
        return $"{date.Year:0000}.{date.Month:00}.{date.Day:00}";
    }

    private static string OneLine(string text)
        => text.Replace("\r", string.Empty).Replace("\n", " ").Trim();
}
