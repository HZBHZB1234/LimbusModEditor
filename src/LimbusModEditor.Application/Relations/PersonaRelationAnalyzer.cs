using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Relations;

/// <summary>关联分析里与「游戏内路径命名约定」相关的纯规则（无 IO，可直接单测）。</summary>
public static class RelationDisplayRules
{
    /// <summary>Spine 骨骼资源的路径判据（真实数据里 Spine 资源分散在四处）：
    /// ① <c>Prefab/SpineIllustPrefab/&lt;id&gt;_gacksung.prefab</c>（人格立绘立牌）；
    /// ② <c>Story/StandingModel/*.psb</c>（立绘 PSB，内含 Skeleton 与图集切片）；
    /// ③ <c>Story/CG/.../*_SkeletonData.asset</c>（Spine SkeletonDataAsset）；
    /// ④ <c>Story/Spine/**</c>（剧情 Spine 专用目录）。
    /// <b>不猜格式</b>：这里只做路径归类，能否解析由 <c>SpineAssetService</c> 按真实文件判定。</summary>
    public static bool IsSpinePath(string? containerEntry)
    {
        if (string.IsNullOrEmpty(containerEntry)) return false;
        return containerEntry.Contains("/SpineIllustPrefab/", StringComparison.OrdinalIgnoreCase)
            || containerEntry.EndsWith(".psb", StringComparison.OrdinalIgnoreCase)
            || containerEntry.Contains("SkeletonData", StringComparison.OrdinalIgnoreCase)
            || containerEntry.Contains("/Story/Spine/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把一条资源归到关联类别。</summary>
    public static RelationKind Classify(string containerEntry, AssetType type)
    {
        if (IsSpinePath(containerEntry)) return RelationKind.Spine;
        return type switch
        {
            AssetType.Video => RelationKind.Video,
            AssetType.Animation => RelationKind.Animation,
            AssetType.Sprite or AssetType.Texture or AssetType.SpriteAtlas => RelationKind.Image,
            AssetType.Text or AssetType.Json => RelationKind.Text,
            AssetType.Mesh => RelationKind.Mesh,
            AssetType.GameObject or AssetType.Component or AssetType.MonoBehaviour or AssetType.MonoScript
                or AssetType.ScriptableObject => RelationKind.Prefab,
            _ => RelationKind.Other,
        };
    }

    /// <summary>关联类别的中文栏目标题（资源预览 / 卡片详情分组用）。</summary>
    public static string KindLabel(RelationKind kind) => kind switch
    {
        RelationKind.Text => "文本",
        RelationKind.StaticData => "静态数据",
        RelationKind.Audio => "音频",
        RelationKind.Image => "图像",
        RelationKind.Video => "视频",
        RelationKind.Spine => "Spine 骨骼",
        RelationKind.Animation => "动画",
        RelationKind.Prefab => "预制体",
        RelationKind.Mesh => "网格",
        RelationKind.Other => "其它",
        _ => "未知",
    };

    /// <summary>「人格立绘」候选的优先级（越小越优先）。卡片封面按这个顺序挑第一张
    /// 能在缓存里成功解码的图；全是 -1 表示没有可用立绘。
    /// <para>真实数据里的候选（按用户视角的「像不像立绘」排序）：
    /// ① <c>Sprite/Unit/Profile/**</c>（人格头像，带底座，最合适做卡片封面）；
    /// ② <c>Sprite/UnitCgThumbnail/**</c>（CG 缩略图）；
    /// ③ <c>Sprite/Unit/CG/**</c>（CG 立绘）；
    /// ④ <c>Sprite/Unit/Info/**</c>；⑤ <c>Sprite/SkinPreview/**</c>；
    /// ⑥ 其余 <c>Sprite/**</c> 与 <c>Texture2D</c>。</para></summary>
    public static int PortraitRank(string? containerEntry, AssetType type)
    {
        if (string.IsNullOrEmpty(containerEntry)) return -1;
        if (containerEntry.Contains("/Sprite/Unit/Profile/", StringComparison.OrdinalIgnoreCase)) return 0;
        if (containerEntry.Contains("/Sprite/UnitCgThumbnail/", StringComparison.OrdinalIgnoreCase)) return 1;
        if (containerEntry.Contains("/Sprite/Unit/CG/", StringComparison.OrdinalIgnoreCase)) return 2;
        if (containerEntry.Contains("/Sprite/Unit/Info/", StringComparison.OrdinalIgnoreCase)) return 3;
        if (containerEntry.Contains("/Sprite/SkinPreview/", StringComparison.OrdinalIgnoreCase)) return 4;
        if (containerEntry.Contains("/UserInfoSuppotPortrait/", StringComparison.OrdinalIgnoreCase)) return 5;
        if (containerEntry.Contains("/Sprite/", StringComparison.OrdinalIgnoreCase)) return 6;
        return type == AssetType.Texture ? 7 : -1;
    }
}

/// <summary>
/// 人格关联分析器：把「四个索引库 + lang 文件清单」里的事实整理成
/// <b>人格 id ⇄ 资源</b> 的关联图。
///
/// <para><b>关联键是什么</b>：Limbus 的人格 id（5 位，真实数据 <c>10101…11216</c>）——
/// 它同时出现在四处：</para>
/// <list type="bullet">
/// <item>lang：<c>PersonalityVoiceDlg/Voice_&lt;角色&gt;_&lt;风格&gt;_&lt;id&gt;.json</c>
/// （语音台词，文件名带 id，直接命中该人格）与 <c>AbDlg_&lt;角色&gt;.json</c>
/// （该角色的对话文本，文件名里没有 5 位 id，按<b>角色</b>挂到该角色的全部人格上）；</item>
/// <item>static-data：<c>personality*/**</c> 表正文里的 <c>id</c> / <c>personalityID</c>
/// 以及 <c>&lt;id&gt;01</c> 形态的技能 id；</item>
/// <item>音频：bank 样本名 <c>voice_&lt;别名&gt;_&lt;id&gt;_&lt;n&gt;</c>；</item>
/// <item>Unity 资源：<c>PersonalityVideo/&lt;id&gt;.mp4</c>、<c>Prefab/SD/Personality/&lt;id&gt;_*.prefab</c>、
/// <c>Sprite/Unit/CG/&lt;id&gt;_normal.png</c>、<c>Sprite/SkillIcon/&lt;id&gt;01.png</c>、
/// <c>Animation/SD/&lt;id&gt;_*/…</c> 等。</item>
/// </list>
///
/// <para><b>人格 id 的权威来源只有三处</b>（语音文件名 / 人格立绘视频文件名 / SD 人格
/// 预制体文件名）—— 不从任意数字串里凭空造人格。否则像事件表里的 <c>eventId=10711</c>
/// 会被误当成一个人格。所有「关联」都只在<b>已知人格 id 集合</b>上做匹配，
/// 因此不会产生假人格。</para>
///
/// <para>纯函数、无 IO、无 SQLite：输入是事实列表，输出是 <see cref="RelationGraph"/>，
/// 因此可以完全用合成数据单测。</para>
/// </summary>
public static class PersonaRelationAnalyzer
{
    /// <summary>人格 id 的取值范围（含）。真实数据 10101–11216；放宽到 10000–12999
    /// 以容纳后续赛季，同时把事件 id（9xxxx / 1xxxx 之外的号段）挡在外面。</summary>
    private const int PersonaIdMin = 10000;
    private const int PersonaIdMax = 12999;

    /// <summary>角色名 → （规范英文名, 中文名）。中文名只用于显示；
    /// 表里没有的角色 token 原样显示（不做猜测式翻译）。</summary>
    private static readonly Dictionary<string, (string English, string Chinese)> Characters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["yisang"] = ("YiSang", "李箱"),
            ["faust"] = ("Faust", "浮士德"),
            ["donquixote"] = ("DonQuixote", "唐吉诃德"),
            ["ryoshu"] = ("Ryoshu", "良秀"),
            ["meursault"] = ("Meursault", "默尔索"),
            ["honglu"] = ("HongLu", "鸿路"),
            ["heathcliff"] = ("Heathcliff", "希斯克利夫"),
            ["ishmael"] = ("Ishmael", "以实玛利"),
            ["rodion"] = ("Rodion", "罗佳"),
            ["sinclair"] = ("Sinclair", "辛克莱"),
            ["outis"] = ("Outis", "奥提斯"),
            ["gregor"] = ("Gregor", "格里高尔"),
        };

    /// <summary>
    /// 游戏资源文件名里角色 token 的<b>已知拼写错误</b>（真实数据实测，2026-09）：
    /// 部分 SD 人格预制体的文件名写的是 <c>Heathclif</c> / <c>Meursalut</c> / <c>Ishmeal</c>，
    /// 而语音文件名与其它资源写的是规范拼写。不归并的话同一个角色会被拆成两组
    /// （显示名也会退回原始 token，卡片流里就会出现「希斯克利夫」与「Heathclif」并存）。
    ///
    /// <para>这不是猜测：键都来自真实文件名，且只在<b>完全相等</b>时归并；
    /// 表里没有的 token 一律原样使用。</para>
    /// </summary>
    private static readonly Dictionary<string, string> CharacterAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["heathclif"] = "heathcliff",
        ["meursalut"] = "meursault",
        ["ishmeal"] = "ishmael",
    };

    /// <summary>把角色 token 归一到规范名；未登记的 token 原样返回（不猜）。</summary>
    private static string NormalizeCharacter(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return string.Empty;
        var key = token.Trim();
        return CharacterAliases.TryGetValue(key, out var canonical) ? canonical : key;
    }

    public static RelationGraph Analyze(RelationInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        // ── ① 人格 id 与身份（角色 / 风格）──
        var identities = new Dictionary<int, Identity>();
        foreach (var asset in inputs.Assets) ReadIdentityFromAsset(asset.ContainerEntry, identities);
        foreach (var lang in inputs.LangFiles) ReadIdentityFromVoiceFile(lang.RelativePath, identities);
        if (identities.Count == 0) return RelationGraph.Empty;

        // ── ② 逐类资源做单趟扫描，命中已有人格 id 就记一条关联 ──
        var links = new List<RelationLink>();
        var seen = new HashSet<(int Pid, RelationKind Kind, string Ref)>();
        void Add(int pid, RelationKind kind, string refKey, string display, string? detail, long size)
        {
            if (!seen.Add((pid, kind, refKey))) return;
            links.Add(new RelationLink(pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                RelationCategories.Persona, kind, refKey, display, detail, size));
        }

        foreach (var asset in inputs.Assets)
        {
            if (string.IsNullOrEmpty(asset.ContainerEntry)) continue;
            var kind = RelationDisplayRules.Classify(asset.ContainerEntry, asset.Type);
            var detail = $"{AssetTypeLabel(asset.Type)} · {HumanSize(asset.SizeBytes)}";
            foreach (var pid in ExtractPersonaIds(asset.ContainerEntry, identities))
                Add(pid, kind, asset.ContainerEntry, FileName(asset.ContainerEntry), detail, asset.SizeBytes);
        }

        foreach (var sample in inputs.Audio)
        {
            foreach (var pid in ExtractPersonaIds(sample.SampleName, identities))
                Add(pid, RelationKind.Audio, sample.BankPath + "\u0000" + sample.SampleName, sample.SampleName,
                    (sample.CodecName ?? "未知编码") + " · " + HumanSize(sample.SizeBytes), sample.SizeBytes);
        }

        foreach (var table in inputs.StaticTables)
        {
            if (string.IsNullOrEmpty(table.Text)) continue;
            var detail = (string.IsNullOrWhiteSpace(table.DataClass) ? "未分组" : table.DataClass)
                + " · " + HumanSize(table.SizeBytes);
            foreach (var pid in ExtractPersonaIds(table.Text, identities))
                Add(pid, RelationKind.StaticData, table.ContainerEntry, table.Name, detail, table.SizeBytes);
        }

        // 角色 → 该角色的人格集合。给「文件名里没有 5 位 id、但按角色组织」的文本用
        // （真实数据里就是 AbDlg_<角色>.json：它是这个角色的对话文本，本身就是该角色
        // 全部人格的文本资源——这个判据完整且不需要猜）。
        var byCharacter = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, identity) in identities)
        {
            if (string.IsNullOrEmpty(identity.Character)) continue;
            if (!byCharacter.TryGetValue(identity.Character, out var list)) byCharacter[identity.Character] = list = [];
            list.Add(pid);
        }

        foreach (var lang in inputs.LangFiles)
        {
            var matched = false;
            foreach (var pid in ExtractPersonaIds(lang.RelativePath, identities))
            {
                Add(pid, RelationKind.Text, lang.RelativePath, lang.RelativePath, $"{lang.KeyCount} 个键", lang.KeyCount);
                matched = true;
            }
            if (matched) continue;

            var character = ReadDlgCharacter(lang.RelativePath);
            if (character is null
                || !byCharacter.TryGetValue(NormalizeCharacter(character), out var characterIds)) continue;
            foreach (var pid in characterIds)
                Add(pid, RelationKind.Text, lang.RelativePath, lang.RelativePath, $"{lang.KeyCount} 个键", lang.KeyCount);
        }

        // ── ③ 主体（人格）──
        var subjects = identities
            .OrderBy(x => x.Key)
            .Select(x => x.Value.ToSubject(x.Key))
            .ToArray();
        return new RelationGraph(subjects, links);
    }

    /// <summary>人格身份（角色 token / 风格 token / 显示名）。</summary>
    private sealed record Identity(string Character, string Style)
    {
        public RelationSubject ToSubject(int pid)
        {
            var display = Characters.TryGetValue(Character, out var mapped) ? mapped.Chinese : Character;
            var name = string.IsNullOrWhiteSpace(Style) || string.Equals(Style, "Base", StringComparison.OrdinalIgnoreCase)
                ? display
                : $"{display} · {Style}";
            var id = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var english = Characters.TryGetValue(Character, out var m2) ? m2.English : Character;
            return new RelationSubject(id, RelationCategories.Persona, name, Style, english, id);
        }
    }

    /// <summary>从 SD 人格预制体 / 人格立绘视频 / 立绘贴图路径里读人格 id 与身份。</summary>
    private static void ReadIdentityFromAsset(string? containerEntry, Dictionary<int, Identity> identities)
    {
        if (string.IsNullOrEmpty(containerEntry)) return;
        var marker = "/Prefab/SD/Personality/";
        var index = containerEntry.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            // <id>_<角色>_<风格>Appearance.prefab
            var rest = containerEntry[(index + marker.Length)..];
            rest = TrimPrefix(rest, "Fools_");
            // 必须去扩展名：风格 token 是 "LCBAppearance"，带 ".prefab" 会让 TrimSuffix 落空。
            var name = Path.GetFileNameWithoutExtension(rest);
            var tokens = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 2 && TryParsePersonaId(tokens[0], out var pid))
            {
                var character = NormalizeCharacter(tokens[1]);
                var style = tokens.Length >= 3
                    ? TrimSuffix(string.Join('_', tokens[2..]), "Appearance")
                    : string.Empty;
                if (!identities.ContainsKey(pid)) identities[pid] = new Identity(character, style);
                else if (string.IsNullOrEmpty(identities[pid].Style) && style.Length > 0)
                    identities[pid] = identities[pid] with { Style = style };
            }
            return;
        }

        if (containerEntry.Contains("/PersonalityVideo/", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParsePersonaId(Path.GetFileNameWithoutExtension(containerEntry), out var pid)
                && !identities.ContainsKey(pid))
                identities[pid] = new Identity(string.Empty, string.Empty);
        }
    }

    /// <summary>从 <c>PersonalityVoiceDlg/Voice_&lt;角色&gt;_&lt;风格&gt;_&lt;id&gt;.json</c> 读身份。</summary>
    private static void ReadIdentityFromVoiceFile(string? relativePath, Dictionary<int, Identity> identities)
    {
        if (string.IsNullOrEmpty(relativePath)) return;
        var name = Path.GetFileNameWithoutExtension(relativePath);
        if (!name.StartsWith("Voice_", StringComparison.OrdinalIgnoreCase)) return;
        var tokens = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3) return;
        if (!TryParsePersonaId(tokens[^1], out var pid)) return;
        var character = NormalizeCharacter(tokens[1]);
        var style = tokens.Length >= 4 ? string.Join('_', tokens[2..^1]) : string.Empty;
        if (identities.TryGetValue(pid, out var existing))
        {
            // 已有身份（来自 SD 预制体，更具体）：只补空缺字段。
            if (string.IsNullOrEmpty(existing.Character)) identities[pid] = existing with { Character = character };
            if (string.IsNullOrEmpty(existing.Style)) identities[pid] = identities[pid] with { Style = style };
            return;
        }
        identities[pid] = new Identity(character, style);
    }

    /// <summary>读 <c>AbDlg_&lt;角色&gt;.json</c> 的角色 token；不匹配返回 null。</summary>
    private static string? ReadDlgCharacter(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return null;
        var name = Path.GetFileNameWithoutExtension(relativePath);
        const string prefix = "AbDlg_";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var character = name[prefix.Length..];
        return character.Length == 0 ? null : character;
    }

    /// <summary>
    /// 从任意文本里扫出「已知人格 id」：逐个数字串取长度 5 的窗口做集合匹配。
    /// <para>为什么是窗口而不是边界匹配：真实数据的 id 会与后续编号连写
    /// （<c>Sprite/SkillIcon/1020101.png</c> = 人格 <c>10201</c> + 技能 <c>01</c>；
    /// <c>Announce/...</c> 同理）。只在<b>已知集合</b>上匹配，所以窗口法不会引入噪声。</para>
    /// </summary>
    private static IEnumerable<int> ExtractPersonaIds(string? text, Dictionary<int, Identity> identities)
    {
        if (string.IsNullOrEmpty(text) || identities.Count == 0) yield break;
        var index = 0;
        while (index < text.Length)
        {
            if (!char.IsAsciiDigit(text[index])) { index++; continue; }
            var start = index;
            while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
            var runLength = index - start;
            if (runLength < 5) continue;
            for (var offset = 0; offset + 5 <= runLength; offset++)
            {
                var value = 0;
                for (var i = 0; i < 5; i++) value = value * 10 + (text[start + offset + i] - '0');
                if (value is >= PersonaIdMin and <= PersonaIdMax && identities.ContainsKey(value)) yield return value;
            }
        }
    }

    /// <summary>文本是否就是一个合法人格 id（文件名里的 id 走这条）。</summary>
    private static bool TryParsePersonaId(string? value, out int pid)
    {
        pid = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Length != 5) return false;
        var result = 0;
        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c)) return false;
            result = result * 10 + (c - '0');
        }
        if (result is < PersonaIdMin or > PersonaIdMax) return false;
        pid = result;
        return true;
    }

    private static string FileName(string path)
    {
        var index = path.Replace('\\', '/').LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }

    private static string TrimPrefix(string value, string prefix)
        => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;

    private static string TrimSuffix(string value, string suffix)
        => value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? value[..^suffix.Length] : value;

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.##} MB",
    };

    private static string AssetTypeLabel(AssetType type) => type switch
    {
        AssetType.Texture => "纹理",
        AssetType.Sprite => "精灵图",
        AssetType.Text => "文本",
        AssetType.Json => "JSON",
        AssetType.Video => "视频",
        AssetType.Animation => "动画",
        AssetType.Mesh => "网格",
        AssetType.GameObject => "游戏对象",
        AssetType.Component => "组件",
        AssetType.MonoBehaviour => "脚本数据",
        _ => type.ToString(),
    };
}
