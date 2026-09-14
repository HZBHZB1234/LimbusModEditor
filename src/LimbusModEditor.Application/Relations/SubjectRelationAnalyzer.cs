using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Relations;

/// <summary>
/// 多类别关联分析器：把「四个索引库 + lang 文件清单 + lang 锚点」里的事实整理成
/// <b>对象 ⇄ 资源</b> 的关联图，并产出**显式的跨资源边**。
///
/// <para><b>类别是怎么判定的</b>：绝不靠数字串反推类别——敌人 4 位 id 会与事件 id、
/// 异常 id 撞车。做法是：</para>
/// <list type="number">
/// <item><b>建身份</b>：只看<b>权威来源</b>（人格 = SD 人格预制体 / 人格立绘视频 / 语音文件名；
/// 敌人 = <c>Prefab/SD/Enemy/*Appearance.prefab</c>；异想体 = <c>Prefab/SD/Abnormality/…</c>；
/// 播报员 = <c>Sprite/BattleAnnouncer/*_announcer.png</c> 的基名）。</item>
/// <item><b>建数字索引</b>：把各身份里「键是数字」的那些编成 <c>数字 → 类别集合</c>。</item>
/// <item><b>挂资源/文本/音频</b>：扫出 4–5 位数字窗口，只在<b>已知集合</b>上匹配，
/// 命中即挂到对应类别。同一个数字属于 ≥2 个类别时<b>不静默二选一</b>：
/// 路径自带类别标记时按标记走，否则挂到全部并标 <see cref="RelationPreviewKind.Ambiguous"/>。</item>
/// </list>
///
/// <para><b>为什么要有「强度」</b>：真实数据里大量关联是推导出来的（「样本名里含某人格 id
/// → 这个音频属于该人格」是推导，不是精确命中）。不标注强度就会把推导伪装成事实。</para>
///
/// <para>纯函数、无 IO、无 SQLite：输入是事实列表，输出是 <see cref="RelationGraph"/>，
/// 因此可以完全用合成数据单测。</para>
/// </summary>
public static class SubjectRelationAnalyzer
{
    /// <summary>数字 id 的窗口长度范围：实测敌人/异想体是 4 位，人格/播报员是 5 位。</summary>
    private const int MinIdDigits = 4;
    private const int MaxIdDigits = 5;

    /// <summary>
    /// <b>创造身份</b>的路径标记——只有这些路径会让一个对象「存在」。
    /// 判据是「这个路径本身就是一份实体清单」：Appearance 预制体一个文件对应一个实体。
    /// </summary>
    private static readonly (string Marker, string Category)[] IdentityMarkers =
    [
        ("/Prefab/SD/Personality/", RelationCategories.Persona),
        ("/Prefab/SD/Enemy/", RelationCategories.Enemy),
        ("/Prefab/SD/Abnormality/", RelationCategories.Abnormality),
    ];

    /// <summary>
    /// <b>只用于给路径提示类别</b>（绝不创造身份）的标记。
    /// 用途：路径自带类别标记时，同号 id 的歧义可以直接消解（<c>Sprite/EgoGiftIcon/9701.png</c>
    /// 一定是饰品 9701，不用再问「9701 是饰品还是异想体」）。
    /// </summary>
    private static readonly (string Marker, string Category)[] PathCategoryMarkers =
    [
        ("/Sprite/BattleAnnouncer/", RelationCategories.Announcer),
        ("/Sprite/EgoGiftIcon/", RelationCategories.EgoGift),
        ("/Sprite/Unit/Profile/Ego/", RelationCategories.Ego),
    ];

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
    /// 而语音文件名与其它资源写的是规范拼写。不归并的话同一个角色会被拆成两组。
    /// **这不是猜测**：键都来自真实文件名，且只在<b>完全相等</b>时归并。
    /// </summary>
    private static readonly Dictionary<string, string> CharacterAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["heathclif"] = "heathcliff",
        ["meursalut"] = "meursault",
        ["ishmeal"] = "ishmael",
    };

    /// <summary>播报员 sprite 基名的后缀（真实数据：<c>gregor_announcer.png</c>）。</summary>
    private const string AnnouncerSuffix = "_announcer";

    /// <summary>跑一次分析。</summary>
    public static RelationGraph Analyze(RelationInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return new Builder(inputs).Run();
    }

    /// <summary>把角色 token 归一到规范名；未登记的 token 原样返回（不猜）。</summary>
    private static string NormalizeCharacter(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return string.Empty;
        var key = token.Trim();
        return CharacterAliases.TryGetValue(key, out var canonical) ? canonical : key;
    }

    /// <summary>纯函数版的 id 窗口扫描（4–5 位，只在已知集合上匹配）。</summary>
    private static IEnumerable<int> NumericWindows(string? text)
        => NumericWindows(text, MinIdDigits, MaxIdDigits);

    /// <summary>
    /// 指定窗口长度的版本。静态表解锚点时要放宽到 4–8 位：被动 id 实测 7 位（<c>1010101</c>）、
    /// EGO 装备 id 5 位（<c>20101</c>）。扫描本身不做白名单过滤——命中与否完全由调用方手里
    /// 的「已知锚点集合 / 数字索引」决定，这样同一条规则既能用于对象身份也能用于文本锚点。
    /// </summary>
    private static IEnumerable<int> NumericWindows(string? text, int minLength, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        if (minLength < 1 || maxLength < minLength) yield break;
        var index = 0;
        while (index < text.Length)
        {
            if (!char.IsAsciiDigit(text[index])) { index++; continue; }
            var start = index;
            while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
            var runLength = index - start;
            if (runLength < minLength) continue;
            for (var offset = 0; offset + minLength <= runLength; offset++)
            {
                for (var length = minLength; length <= maxLength && offset + length <= runLength; length++)
                {
                    var value = 0;
                    for (var i = 0; i < length; i++) value = value * 10 + (text[start + offset + i] - '0');
                    yield return value;
                }
            }
        }
    }

    private static string FileName(string path)
    {
        var index = path.Replace('\\', '/').LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }

    private static string WithoutExtension(string value)
    {
        var dot = value.LastIndexOf('.');
        return dot <= 0 ? value : value[..dot];
    }

    private static string TrimPrefix(string value, string prefix)
        => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;

    private static string TrimSuffix(string value, string suffix)
        => value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? value[..^suffix.Length] : value;

    private static bool TryParseId(string? value, out int id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c)) return false;
            id = id * 10 + (c - '0');
        }
        return value.Length > 0;
    }

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

    /// <summary>一个对象身份（同类内唯一）。</summary>
    private sealed record Identity(string Category, string Key, string Character, string Style)
    {
        public string SubjectId => SubjectIds.Make(Category, Key);

        /// <summary>lang 里给出的权威名称（E.G.O 装备/饰品的显示名来自这里）。
        /// 空 = 该类别没有 lang 名称，显示名按类别规则算。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>lang 里给出的补充说明（如「李箱的基础E.G.O装备」）——当副标题用。</summary>
        public string Note { get; init; } = string.Empty;
    }

    /// <summary>把「事实 → 关联图」的过程收在一个可变构造器里，避免到处传中间集合。</summary>
    private sealed class Builder(RelationInputs inputs)
    {
        private readonly RelationInputs _inputs = inputs;
        private readonly Dictionary<string, Identity> _identities = new(StringComparer.Ordinal);
        private readonly List<RelationLink> _links = [];
        private readonly List<RelationXref> _xrefs = [];
        private readonly HashSet<(string Id, RelationKind Kind, string Ref)> _seen = new();
        private readonly HashSet<(string From, string Relation, string To)> _seenEdges = new();
        private readonly Dictionary<int, HashSet<string>> _numericIndex = [];
        private readonly Dictionary<string, string> _announcerIdToKey = new(StringComparer.Ordinal);
        private readonly HashSet<string> _linkableAnnouncerKeys = new(StringComparer.OrdinalIgnoreCase);

        public RelationGraph Run()
        {
            ReadPersonaIdentities();
            ReadPrefabIdentities();
            ReadAnnouncerIdentities();
            ReadAnchorIdentities();
            BuildNumericIndex();
            LinkAssets();
            LinkAudio();
            LinkStaticTables();
            LinkLangFiles();
            return Build();
        }

        // ── ① 身份 ───────────────────────────────────────────────────

        /// <summary>人格身份：SD 人格预制体 / 人格立绘视频 / 语音文件名（三处权威来源）。</summary>
        private void ReadPersonaIdentities()
        {
            foreach (var asset in _inputs.Assets)
                ReadPersonaIdentityFromAsset(asset.ContainerEntry);
            foreach (var lang in _inputs.LangFiles)
                ReadPersonaIdentityFromVoiceFile(lang.RelativePath);
        }

        private void ReadPersonaIdentityFromAsset(string? containerEntry)
        {
            if (string.IsNullOrEmpty(containerEntry)) return;

            const string marker = "/Prefab/SD/Personality/";
            var index = containerEntry.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                // <id>_<角色>_<风格>Appearance.prefab
                var rest = TrimPrefix(containerEntry[(index + marker.Length)..], "Fools_");
                // 必须去扩展名：风格 token 是 "LCBAppearance"，带 ".prefab" 会让 TrimSuffix 落空。
                var tokens = WithoutExtension(rest).Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 2 && TryParseId(tokens[0], out var pid))
                {
                    var character = NormalizeCharacter(tokens[1]);
                    var style = tokens.Length >= 3 ? TrimSuffix(string.Join('_', tokens[2..]), "Appearance") : string.Empty;
                    EnsureIdentity(RelationCategories.Persona, tokens[0], out var identity);
                    if (string.IsNullOrEmpty(identity.Character))
                        _identities[identity.SubjectId] = identity with { Character = character };
                    if (string.IsNullOrEmpty(identity.Style) && style.Length > 0)
                        _identities[identity.SubjectId] = _identities[identity.SubjectId] with { Style = style };
                }
                return;
            }

            if (containerEntry.Contains("/PersonalityVideo/", StringComparison.OrdinalIgnoreCase))
            {
                var name = WithoutExtension(FileName(containerEntry));
                if (TryParseId(name, out _)) EnsureIdentity(RelationCategories.Persona, name, out _);
            }
        }

        private void ReadPersonaIdentityFromVoiceFile(string? relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return;
            var name = WithoutExtension(FileName(relativePath));
            if (!name.StartsWith("Voice_", StringComparison.OrdinalIgnoreCase)) return;
            var tokens = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3) return;
            if (!TryParseId(tokens[^1], out _)) return;
            var character = NormalizeCharacter(tokens[1]);
            var style = tokens.Length >= 4 ? string.Join('_', tokens[2..^1]) : string.Empty;
            EnsureIdentity(RelationCategories.Persona, tokens[^1], out var identity);
            if (string.IsNullOrEmpty(identity.Character) && character.Length > 0)
                _identities[identity.SubjectId] = identity with { Character = character };
            if (string.IsNullOrEmpty(identity.Style) && style.Length > 0)
                _identities[identity.SubjectId] = _identities[identity.SubjectId] with { Style = style };
        }

        /// <summary>
        /// 权威来源：<c>Egos.json</c>（E.G.O 装备）与 <c>EGOgift*.json</c>（E.G.O 饰品）。
        /// 只有这两类锚点的 id 集合「就是实体清单」，所以由它们创造身份；
        /// 被动/技能/语音的数值 id 只是外键，绝不能当实体清单用（会造出幽灵对象）。
        /// </summary>
        private void ReadAnchorIdentities()
        {
            foreach (var anchor in _inputs.TextAnchors)
            {
                switch (anchor.Table)
                {
                    case RelationAnchorTables.Ego:
                        if (!TryParseId(anchor.Anchor, out _)) continue;
                        RegisterAnchorIdentity(RelationCategories.Ego, anchor.Anchor, anchor);
                        break;

                    case RelationAnchorTables.EgoGift:
                        // 分层 id 归一化到 4 位基准键：9701 / 19701 / 29701 是同一个饰品。
                        if (!TryParseId(anchor.Anchor, out var raw)) continue;
                        var giftKey = RelationEntityKeys.GiftKeyOf(raw);
                        if (giftKey is null) continue;
                        RegisterAnchorIdentity(RelationCategories.EgoGift, giftKey, anchor);
                        break;
                }
            }
        }

        /// <summary>
        /// 建（或补齐）一个由 lang 锚点定义的身份，并顺手把「文件 → 对象」这条边记下来。
        /// <b>补齐而不覆盖</b>：同一个实体常被多个 lang 文件提到，先到的名字更权威。
        /// </summary>
        private void RegisterAnchorIdentity(string category, string key, RelationTextAnchor anchor)
        {
            var subjectId = SubjectIds.Make(category, key);
            if (!_identities.TryGetValue(subjectId, out var existing))
            {
                _identities[subjectId] = new Identity(category, key, string.Empty, string.Empty)
                {
                    Name = anchor.Name ?? string.Empty,
                    Note = anchor.Desc ?? string.Empty,
                };
            }
            else
            {
                var updated = existing;
                if (updated.Name.Length == 0 && !string.IsNullOrWhiteSpace(anchor.Name))
                    updated = updated with { Name = anchor.Name };
                if (updated.Note.Length == 0 && !string.IsNullOrWhiteSpace(anchor.Desc))
                    updated = updated with { Note = anchor.Desc };
                if (!ReferenceEquals(updated, existing)) _identities[subjectId] = updated;
            }

            // lang 文件本身就是该实体的定义处 → 一条精确强度的文本关联 + 一条跨资源边。
            AddLink(category, key, RelationKind.Text, anchor.RelativePath, anchor.RelativePath,
                RelationCategories.Label(category) + "定义", anchor.Anchor.Length,
                previewText: anchor.Name ?? anchor.Body,
                previewKind: RelationPreviewKind.Exact,
                mediaKind: "text",
                refPath: anchor.RelativePath,
                deepLink: RelationDeepLink.ForText(anchor.RelativePath, anchor.Anchor),
                targetSubjectId: subjectId);
            AddXref(anchor.RelativePath, subjectId, RelationXrefKinds.TextToSubject,
                "text", "subject", nameof(RelationPreviewKind.Exact), anchor.Name);
        }

        /// <summary>敌人 / 异想体身份：只认 <c>Prefab/SD/(Enemy|Abnormality)/&lt;id&gt;_…Appearance.prefab</c>。</summary>
        private void ReadPrefabIdentities()
        {
            foreach (var asset in _inputs.Assets)
            {
                var entry = asset.ContainerEntry;
                if (string.IsNullOrEmpty(entry)) continue;
                foreach (var (marker, category) in IdentityMarkers)
                {
                    if (category == RelationCategories.Persona) continue;
                    if (!entry.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
                    var rest = TrimPrefix(entry[(entry.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length)..], "Fools_");
                    var tokens = WithoutExtension(rest).Split('_', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length < 2 || !TryParseId(tokens[0], out _)) continue;
                    EnsureIdentity(category, tokens[0], out var identity);
                    if (string.IsNullOrEmpty(identity.Character))
                        _identities[identity.SubjectId] = identity with { Character = tokens[1] };
                }
            }
        }

        /// <summary>播报员身份：<c>Sprite/BattleAnnouncer/*_announcer.png</c> 的基名。
        /// 顺带从可读的静态表正文里把 <c>imgStr ⇄ id</c> 配出来（用于把 <c>announcer_*</c>
        /// 音频挂回去）——这条是<b>推导</b>，不是权威来源。</summary>
        private void ReadAnnouncerIdentities()
        {
            foreach (var asset in _inputs.Assets)
            {
                var entry = asset.ContainerEntry;
                if (string.IsNullOrEmpty(entry)) continue;
                if (!entry.Contains("/Sprite/BattleAnnouncer/", StringComparison.OrdinalIgnoreCase)) continue;
                var name = WithoutExtension(FileName(entry));
                if (!name.EndsWith(AnnouncerSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                EnsureIdentity(RelationCategories.Announcer, name.ToLowerInvariant(), out _);
                _linkableAnnouncerKeys.Add(name.ToLowerInvariant());
            }

            foreach (var table in _inputs.StaticTables)
            {
                if (string.IsNullOrEmpty(table.Text) || !table.Text.Contains("imgStr", StringComparison.Ordinal)) continue;
                foreach (Match match in AnnouncerImgStrPattern.Matches(table.Text))
                {
                    var key = match.Groups[1].Value.ToLowerInvariant();
                    if (key.Length == 0) continue;
                    var idMatch = AnnouncerIdPattern.Match(table.Text, Math.Max(0, match.Index - 300),
                        Math.Min(match.Index, 300));
                    if (!idMatch.Success) continue;
                    if (!_linkableAnnouncerKeys.Contains(key))
                    {
                        EnsureIdentity(RelationCategories.Announcer, key, out _);
                        _linkableAnnouncerKeys.Add(key);
                    }
                    _announcerIdToKey.TryAdd(idMatch.Groups[2].Value, key);
                }
            }
        }

        // 只用普通 Regex（而非 [GeneratedRegex]）：本分析器要在<b>合成数据</b>下也能单测，
        // 源码生成器会把 Regex 的构造时机与程序集初始化绑在一起，反而让测试更脆。模式是常量，
        // 编译一次、长期复用，性能与生成版无实质差别。
        private static readonly Regex AnnouncerImgStrPattern =
            new("\"imgStr\"\\s*:\\s*\"([A-Za-z0-9_]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AnnouncerIdPattern =
            new("\"id\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private void EnsureIdentity(string category, string key, out Identity identity)
        {
            if (!_identities.TryGetValue(SubjectIds.Make(category, key), out var existing))
            {
                existing = new Identity(category, key, string.Empty, string.Empty);
                _identities[existing.SubjectId] = existing;
            }
            identity = existing;
        }

        /// <summary>把「键是数字」的身份编成 <c>数字 → 类别集合</c> 索引。</summary>
        private void BuildNumericIndex()
        {
            foreach (var identity in _identities.Values)
            {
                if (!TryParseId(identity.Key, out var value)) continue;
                if (!_numericIndex.TryGetValue(value, out var categories))
                    _numericIndex[value] = categories = new HashSet<string>(StringComparer.Ordinal);
                categories.Add(identity.Category);
            }
            foreach (var (id, key) in _announcerIdToKey)
            {
                // 播报员的 imgStr 是角色 token（非数字），只有它旁边的 "id" 才是数字键：
                // 用这个数字把「播报员」也编进数字索引，播报员的立绘/语音才能反向命中。
                if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)) continue;
                if (!_numericIndex.TryGetValue(numeric, out var categories))
                    _numericIndex[numeric] = categories = new HashSet<string>(StringComparer.Ordinal);
                categories.Add(RelationCategories.Announcer);
            }
        }

        // ── ② 挂资源 ─────────────────────────────────────────────────

        private void LinkAssets()
        {
            foreach (var asset in _inputs.Assets)
            {
                var entry = asset.ContainerEntry;
                if (string.IsNullOrEmpty(entry)) continue;
                var kind = RelationDisplayRules.Classify(entry, asset.Type);
                var detail = $"{AssetTypeLabel(asset.Type)} · {HumanSize(asset.SizeBytes)}";
                var mediaKind = RelationDisplayRules.MediaKindOf(kind);
                var categoryMarker = CategoryFromPath(entry);

                foreach (var (category, key) in MatchNumeric(entry))
                {
                    // 路径自带类别标记时以标记为准；标不上、且该数字属于多个类别 → 标歧义。
                    if (categoryMarker is not null && !string.Equals(categoryMarker, category, StringComparison.Ordinal))
                        continue;
                    var ambiguous = categoryMarker is null && CategoryCount(entry) > 1;
                    AddLink(category, key, kind, entry, FileName(entry), detail, asset.SizeBytes,
                        mediaKind: mediaKind,
                        refPath: entry,
                        deepLink: RelationDeepLink.ForAsset(entry),
                        previewKind: ambiguous ? RelationPreviewKind.Ambiguous : RelationPreviewKind.None);
                }

                LinkAnnouncerSprite(entry, kind, detail, asset.SizeBytes);
            }
        }

        /// <summary>播报图对播报员是「权威来源本身就是资源」，所以单独挂一条（也当卡片封面）。</summary>
        private void LinkAnnouncerSprite(string entry, RelationKind kind, string detail, long size)
        {
            if (!entry.Contains("/Sprite/BattleAnnouncer/", StringComparison.OrdinalIgnoreCase)) return;
            var name = WithoutExtension(FileName(entry));
            if (!name.EndsWith(AnnouncerSuffix, StringComparison.OrdinalIgnoreCase)) return;
            var key = name.ToLowerInvariant();
            if (!_identities.ContainsKey(SubjectIds.Make(RelationCategories.Announcer, key))) return;
            AddLink(RelationCategories.Announcer, key, kind, entry, name, detail, size,
                mediaKind: RelationDisplayRules.MediaKindOf(kind),
                refPath: entry,
                deepLink: RelationDeepLink.ForAsset(entry));
        }

        private void LinkAudio()
        {
            var anchors = _inputs.AnchorIndex();
            foreach (var sample in _inputs.Audio)
            {
                var refKey = sample.BankPath + RelationDeepLink.Separator + sample.SampleName;
                var detail = $"{sample.CodecName ?? "未知编码"} · {HumanSize(sample.SizeBytes)}";
                var mediaKind = "audio";
                var duration = sample.DurationSeconds;

                // ① 精确：样本名 == 台词 id（实测一对一）→ 直接把台词原文当预览内容。
                anchors.TryGetValue(sample.SampleName, out var anchor);
                var exact = anchor is not null;
                if (exact)
                {
                    AddXref(sample.SampleName, anchor!.Anchor, RelationXrefKinds.AudioToVoiceText,
                        "audio", "text", nameof(RelationPreviewKind.Exact), anchor.Desc);
                }

                // ② 播报员音频：announcer_<关卡>_<播报员id>_<n> → 由静态表配出的 imgStr 反查。
                if (sample.SampleName.StartsWith("announcer_", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (category, key) in MatchNumeric(sample.SampleName))
                    {
                        if (!string.Equals(category, RelationCategories.Announcer, StringComparison.Ordinal)) continue;
                        AddLink(category, key, RelationKind.Audio, refKey, sample.SampleName, detail, sample.SizeBytes,
                            previewText: exact ? anchor!.Body : null,
                            previewKind: exact ? RelationPreviewKind.Exact : RelationPreviewKind.Derived,
                            mediaKind: mediaKind,
                            durationSec: duration,
                            refPath: sample.BankPath,
                            deepLink: RelationDeepLink.ForAudio(sample.BankPath, sample.SampleName));
                        AddXref(sample.SampleName, SubjectIds.Make(category, key), RelationXrefKinds.AudioToSubject,
                            "audio", "subject", nameof(RelationPreviewKind.Derived), null);
                    }
                }

                foreach (var (category, key) in MatchNumeric(sample.SampleName))
                {
                    if (string.Equals(category, RelationCategories.Announcer, StringComparison.Ordinal)) continue;
                    var subjectId = SubjectIds.Make(category, key);
                    AddLink(category, key, RelationKind.Audio, refKey, sample.SampleName, detail, sample.SizeBytes,
                        previewText: exact ? anchor!.Body : null,
                        previewKind: exact ? RelationPreviewKind.Exact : RelationPreviewKind.Derived,
                        mediaKind: mediaKind,
                        durationSec: duration,
                        refPath: sample.BankPath,
                        deepLink: RelationDeepLink.ForAudio(sample.BankPath, sample.SampleName),
                        targetSubjectId: subjectId);
                    AddXref(sample.SampleName, subjectId, RelationXrefKinds.AudioToSubject,
                        "audio", "subject",
                        exact ? nameof(RelationPreviewKind.Exact) : nameof(RelationPreviewKind.Derived), anchor?.Desc);
                }
            }
        }

        private void LinkStaticTables()
        {
            var anchors = _inputs.AnchorIndex();
            foreach (var table in _inputs.StaticTables)
            {
                if (string.IsNullOrEmpty(table.Text)) continue;
                var detailBase = string.IsNullOrWhiteSpace(table.DataClass) ? "未分组" : table.DataClass;
                var hitAnchors = new List<string>();
                // 锚点放宽到 4–8 位：实测被动 id 是 7 位（1010101）、EGO 装备 id 是 5 位（20101）。
                foreach (var value in NumericWindows(table.Text, MinIdDigits, 8))
                {
                    if (!anchors.TryGetValue(value.ToString(CultureInfo.InvariantCulture), out var anchor)) continue;
                    hitAnchors.Add(anchor.Anchor);
                }

                foreach (var (category, key) in MatchNumeric(table.Text))
                {
                    var subjectId = SubjectIds.Make(category, key);
                    AddLink(category, key, RelationKind.StaticData, table.ContainerEntry, table.Name,
                        detailBase + " · " + HumanSize(table.SizeBytes), table.SizeBytes,
                        previewText: hitAnchors.Count > 0 ? hitAnchors.Count + " 条可解析文本" : null,
                        previewKind: hitAnchors.Count > 0 ? RelationPreviewKind.Derived : RelationPreviewKind.None,
                        mediaKind: "static",
                        refPath: table.ContainerEntry,
                        deepLink: RelationDeepLink.ForStatic(table.ContainerEntry, null),
                        targetSubjectId: subjectId);
                }

                foreach (var anchorRef in hitAnchors)
                {
                    var anchor = anchors[anchorRef];
                    AddXref(table.ContainerEntry, anchor.Anchor, RelationXrefKinds.StaticToLang,
                        "static", "text", nameof(RelationPreviewKind.Derived), anchor.Name);
                }
            }
        }

        /// <summary>
        /// lang 文件挂接。四路：
        /// ① 语音文件名里的 5 位 id → 该人格（精确）；
        /// ② <c>AbDlg_&lt;角色&gt;.json</c> → 该角色的全部人格；
        /// ③ <c>BattleAnnouncerDlg/Announcer_&lt;角色&gt;_&lt;n&gt;.json</c> → 该角色的播报员；
        /// ④ 文件名里出现已知角色 token → 该角色的全部人格（推导；StoryData 这类
        ///    <b>按局部节点序号组织</b>的剧情文本只能靠这一路拿到「角色级」关联）。
        /// </summary>
        private void LinkLangFiles()
        {
            var byCharacter = new Dictionary<string, List<Identity>>(StringComparer.OrdinalIgnoreCase);
            foreach (var identity in _identities.Values)
            {
                if (identity.Category != RelationCategories.Persona) continue;
                if (string.IsNullOrEmpty(identity.Character)) continue;
                if (!byCharacter.TryGetValue(identity.Character, out var list))
                    byCharacter[identity.Character] = list = [];
                list.Add(identity);
            }

            var announcersByCharacter = _identities.Values
                .Where(x => x.Category == RelationCategories.Announcer)
                .ToArray();

            foreach (var lang in _inputs.LangFiles)
            {
                var path = lang.RelativePath;
                if (string.IsNullOrEmpty(path)) continue;
                var detail = $"{lang.KeyCount} 个键";
                var tagged = false;

                foreach (var (category, key) in MatchNumeric(path))
                {
                    AddLink(category, key, RelationKind.Text, path, path, detail, lang.KeyCount,
                        mediaKind: "text", refPath: path, deepLink: RelationDeepLink.ForText(path, null));
                    tagged = true;
                }
                if (tagged) continue;

                var name = WithoutExtension(FileName(path));

                if (name.StartsWith("AbDlg_", StringComparison.OrdinalIgnoreCase))
                {
                    var character = NormalizeCharacter(name["AbDlg_".Length..]);
                    if (byCharacter.TryGetValue(character, out var personas))
                        foreach (var persona in personas)
                            AddLink(persona.Category, persona.Key, RelationKind.Text, path, path, detail, lang.KeyCount,
                                previewKind: RelationPreviewKind.Derived,
                                mediaKind: "text", refPath: path, deepLink: RelationDeepLink.ForText(path, null));
                    continue;
                }

                if (name.StartsWith("Announcer_", StringComparison.OrdinalIgnoreCase))
                {
                    var tokens = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 2)
                    {
                        var character = tokens[1].ToLowerInvariant();
                        foreach (var announcer in announcersByCharacter)
                            if (announcer.Key.StartsWith(character + "_", StringComparison.OrdinalIgnoreCase))
                                AddLink(announcer.Category, announcer.Key, RelationKind.Text, path, path, detail, lang.KeyCount,
                                    previewKind: RelationPreviewKind.Derived,
                                    mediaKind: "text", refPath: path, deepLink: RelationDeepLink.ForText(path, null));
                    }
                    continue;
                }

                // ④ 文件名里的角色 token（整个 token 相等，不做前缀/模糊匹配，避免假命中）。
                foreach (var token in name.Split(['_', '-', '.', ' '], StringSplitOptions.RemoveEmptyEntries))
                {
                    var character = NormalizeCharacter(token);
                    if (character.Length == 0 || !byCharacter.TryGetValue(character, out var personas)) continue;
                    foreach (var persona in personas)
                        AddLink(persona.Category, persona.Key, RelationKind.Text, path, path, detail, lang.KeyCount,
                            previewKind: RelationPreviewKind.Derived,
                            mediaKind: "text", refPath: path, deepLink: RelationDeepLink.ForText(path, null));
                }
            }
        }

        // ── ③ 产出 ───────────────────────────────────────────────────

        private RelationGraph Build()
        {
            var bySubject = _links
                .GroupBy(x => x.SubjectId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

            var subjects = new List<RelationSubject>(_identities.Count);
            foreach (var identity in _identities.Values)
            {
                var links = bySubject.TryGetValue(identity.SubjectId, out var found) ? found : [];
                subjects.Add(new RelationSubject(
                    identity.SubjectId,
                    identity.Category,
                    DisplayNameOf(identity),
                    SubtitleOf(identity),
                    string.IsNullOrEmpty(identity.Character) ? string.Empty : EnglishNameOf(identity.Character),
                    SortKeyOf(identity))
                {
                    CategoryLabel = RelationCategories.Label(identity.Category),
                    CoverRef = PickCover(links),
                    PreviewText = PickSubjectPreview(links),
                    LinkCount = links.Length,
                });
            }

            subjects.Sort(static (a, b) =>
            {
                var byCategory = string.CompareOrdinal(a.SubjectKind, b.SubjectKind);
                return byCategory != 0 ? byCategory : string.CompareOrdinal(a.SortKey, b.SortKey);
            });
            return new RelationGraph(subjects, _links) { Xrefs = _xrefs };
        }

        /// <summary>封面：立绘候选里按 <see cref="RelationDisplayRules.CoverCandidateRank"/> 取最优。</summary>
        private static string? PickCover(IReadOnlyList<RelationLink> links)
        {
            string? best = null;
            var bestRank = int.MaxValue;
            foreach (var link in links)
            {
                if (link.Kind != RelationKind.Image) continue;
                var type = link.MediaKind == "image" && link.RefKey.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? AssetType.Sprite
                    : AssetType.Texture;
                var rank = RelationDisplayRules.CoverCandidateRank(link.RefKey, type);
                if (rank < 0 || rank >= bestRank) continue;
                bestRank = rank;
                best = link.RefKey;
            }
            return best;
        }

        private static string? PickSubjectPreview(IReadOnlyList<RelationLink> links)
        {
            foreach (var link in links)
                if (link.PreviewKind == RelationPreviewKind.Exact && !string.IsNullOrWhiteSpace(link.PreviewText))
                    return link.PreviewText;
            foreach (var link in links)
                if (!string.IsNullOrWhiteSpace(link.PreviewText))
                    return link.PreviewText;
            return null;
        }

        private static string DisplayNameOf(Identity identity)
        {
            // E.G.O 装备 / 饰品：显示名只认 lang 里的权威名称（如「乌瞰刀」），
            // 不去猜、也不拿 id 当名字。lang 缺名称时退回 id。
            if (identity.Category is RelationCategories.Ego or RelationCategories.EgoGift)
                return string.IsNullOrWhiteSpace(identity.Name) ? identity.Key : identity.Name;

            if (identity.Category == RelationCategories.Persona)
            {
                var display = Characters.TryGetValue(identity.Character, out var mapped) ? mapped.Chinese : identity.Character;
                if (string.IsNullOrEmpty(display)) return identity.Key;
                return string.IsNullOrWhiteSpace(identity.Style)
                       || string.Equals(identity.Style, "Base", StringComparison.OrdinalIgnoreCase)
                    ? display
                    : $"{display} · {identity.Style}";
            }
            if (identity.Category == RelationCategories.Announcer)
                return TrimSuffix(identity.Key, AnnouncerSuffix);
            // 敌人 / 异想体：显示名用文件名里的名字 token（原样，不猜）。
            return string.IsNullOrEmpty(identity.Character) ? identity.Key : identity.Character;
        }

        /// <summary>副标题：E.G.O 类用 lang 的说明（如「李箱的基础E.G.O装备」），其余用风格 token。</summary>
        private static string SubtitleOf(Identity identity)
            => identity.Category is RelationCategories.Ego or RelationCategories.EgoGift
                ? identity.Note
                : identity.Style;

        private static string EnglishNameOf(string character)
            => Characters.TryGetValue(character, out var mapped) ? mapped.English : character;

        private static string SortKeyOf(Identity identity)
        {
            if (TryParseId(identity.Key, out var value))
                return value.ToString("D6", CultureInfo.InvariantCulture);
            return identity.Key;
        }

        // ── 小工具 ───────────────────────────────────────────────────

        private void AddLink(
            string category, string key, RelationKind kind, string refKey, string display, string? detail, long size,
            string? previewText = null,
            RelationPreviewKind previewKind = RelationPreviewKind.None,
            string mediaKind = "other",
            double? durationSec = null,
            string? refPath = null,
            string? deepLink = null,
            string? targetSubjectId = null)
        {
            var subjectId = SubjectIds.Make(category, key);
            if (!_seen.Add((subjectId, kind, refKey))) return;
            _links.Add(new RelationLink(subjectId, category, kind, refKey, display, detail, size)
            {
                PreviewText = previewText,
                PreviewKind = previewKind,
                MediaKind = mediaKind,
                DurationSec = durationSec,
                RefPath = refPath,
                DeepLink = deepLink,
                TargetSubjectId = targetSubjectId,
            });
        }

        private void AddXref(string fromRef, string toRef, string relation, string fromKind, string toKind,
            string confidence, string? detail)
        {
            if (string.IsNullOrEmpty(fromRef) || string.IsNullOrEmpty(toRef)) return;
            if (!_seenEdges.Add((fromRef, relation, toRef))) return;
            _xrefs.Add(new RelationXref(fromRef, toRef, relation, fromKind, toKind, confidence, detail));
        }

        /// <summary>扫出文本里命中的「已知数字 id」，带上它所属的类别。</summary>
        private IEnumerable<(string Category, string Key)> MatchNumeric(string? text)
        {
            if (string.IsNullOrEmpty(text) || _numericIndex.Count == 0) yield break;
            var emitted = new HashSet<(string, string)>();
            foreach (var value in NumericWindows(text))
            {
                if (!_numericIndex.TryGetValue(value, out var categories)) continue;
                var key = value.ToString(CultureInfo.InvariantCulture);
                // 数字 id 只匹配「键就是该数字」的身份；4 位 id 同时命中 5 位前缀的情况由
                // 集合成员判定排除（5 位身份不会被 4 位窗口命中，因为窗口值不同）。
                foreach (var category in categories)
                {
                    if (!_identities.ContainsKey(SubjectIds.Make(category, key))) continue;
                    if (emitted.Add((category, key))) yield return (category, key);
                }
            }
        }

        /// <summary>这段文本命中了多少个<b>不同类别</b>的数字 id（判断歧义用）。</summary>
        private int CategoryCount(string text)
        {
            var categories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in NumericWindows(text))
                if (_numericIndex.TryGetValue(value, out var found))
                    categories.UnionWith(found);
            return categories.Count;
        }

        /// <summary>路径自带的类别标记（权威来源目录），没有返回 null。</summary>
        private static string? CategoryFromPath(string entry)
        {
            foreach (var (marker, category) in IdentityMarkers)
                if (entry.Contains(marker, StringComparison.OrdinalIgnoreCase)) return category;
            foreach (var (marker, category) in PathCategoryMarkers)
                if (entry.Contains(marker, StringComparison.OrdinalIgnoreCase)) return category;
            return null;
        }
    }
}
