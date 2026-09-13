using System.Globalization;
using System.Text.Json;

namespace LimbusModEditor.Application.Spine;

/// <summary>图集里的一个区域（<c>name</c> + <c>bounds</c> + 可选旋转/裁剪信息）。</summary>
/// <param name="Name">区域名（骨架里 attachment 的 <c>path</c> 或名字）。</param>
/// <param name="X">页内左上角 X（像素）。</param>
/// <param name="Y">页内左上角 Y（像素）。</param>
/// <param name="Width">页内存储宽度（像素）。</param>
/// <param name="Height">页内存储高度（像素）。</param>
/// <param name="Rotation">页内旋转角度（0/90/180/270）。</param>
/// <param name="OriginalWidth">裁剪前原始宽（未裁剪时为 0）。</param>
/// <param name="OriginalHeight">裁剪前原始高（未裁剪时为 0）。</param>
/// <param name="OffsetX">裁剪偏移 X。</param>
/// <param name="OffsetY">裁剪偏移 Y。</param>
public sealed record SpineAtlasRegion(
    string Name, int X, int Y, int Width, int Height, int Rotation,
    int OriginalWidth, int OriginalHeight, int OffsetX, int OffsetY);

/// <summary>图集页（一张贴图 + 排布在它上面的区域）。</summary>
/// <param name="Name">页名（通常是贴图文件名，如 <c>cg_40.png</c>）。</param>
/// <param name="Width">页宽（像素）。</param>
/// <param name="Height">页高（像素）。</param>
/// <param name="Scale">打包缩放（<c>scale:</c>，缺省 1）；骨架单位 × Scale ≈ 页像素。</param>
/// <param name="Regions">页内区域。</param>
public sealed record SpineAtlasPage(
    string Name, int Width, int Height, double Scale, IReadOnlyList<SpineAtlasRegion> Regions);

/// <summary>Spine 图集（一个 <c>.atlas.txt</c> 可含多页）。</summary>
public sealed record SpineAtlas(IReadOnlyList<SpineAtlasPage> Pages)
{
    public int RegionCount => Pages.Sum(p => p.Regions.Count);
}

/// <summary>骨架里的一根骨骼（setup 姿态）。</summary>
public sealed record SpineBone(
    string Name, string? Parent, double X, double Y, double Length,
    double Rotation, double ScaleX, double ScaleY, string Inherit);

/// <summary>骨架里的一个槽位（绘制顺序 = 列表顺序，靠前的先画 = 在下面）。</summary>
public sealed record SpineSlot(string Name, string Bone, string? Attachment);

/// <summary>
/// 一个区域（region）附件。仅支持区域附件：网格附件需要完整蒙皮求值，
/// 本工具按「不支持」计数并跳过（<see cref="SpineSkeleton.UnsupportedAttachmentCount"/>）。
/// </summary>
public sealed record SpineRegionAttachment(
    string Name, string Path, double X, double Y, double Rotation,
    double ScaleX, double ScaleY, double Width, double Height);

/// <summary>
/// 一个关键帧。<b>属性为 null 表示该时刻这条时间线没有键</b>，
/// 求值时回落到 setup 姿态的值（Spine 的语义）。
///
/// <para>这是一个「按目标类型取用字段」的联合记录：骨骼时间线只填
/// <paramref name="X"/>/<paramref name="Y"/>/<paramref name="Rotation"/>/<paramref name="ScaleX"/>/<paramref name="ScaleY"/>，
/// 槽位时间线只填 <paramref name="Attachment"/>（换装 / 显隐）与 <paramref name="Alpha"/>（<c>rgba</c> 淡入淡出）。</para>
/// </summary>
/// <param name="Alpha">槽位颜色的 alpha（0–1，来自 <c>rgba</c> 时间线）；骨骼时间线恒为 null。</param>
/// <param name="Stepped"><b>本键到下一键</b>之间是阶梯（不插值）；最后一个键没有下一键，恒为 false。</param>
public sealed record SpineKey(
    double Time, double? X, double? Y, double? Rotation, double? ScaleX, double? ScaleY,
    string? Attachment, double? Alpha, bool Stepped);

/// <summary>一个动画：时长（秒）+ 按骨骼 / 槽位分组的全部关键帧。</summary>
public sealed record SpineAnimation(
    string Name,
    double Duration,
    IReadOnlyDictionary<string, IReadOnlyList<SpineKey>> Bones,
    IReadOnlyDictionary<string, IReadOnlyList<SpineKey>> Slots)
{
    /// <summary>该动画的关键帧总数（诊断 / 卡片摘要用）。</summary>
    public int KeyCount => Bones.Values.Sum(x => x.Count) + Slots.Values.Sum(x => x.Count);
}

/// <summary>
/// 解析后的 Spine 骨架（Spine 4.0 JSON 口径）。
///
/// <para><b>为什么自己解析</b>：游戏里的 Spine 数据就是 TextAsset（<c>.json</c> / <c>.atlas.txt</c>），
/// 仓库里没有任何 Spine 运行时可用；本工具只需要「结构 + region 附件 + 关键帧」这三样，
/// 不需要完整的 Spine 引擎（蒙皮 / 变形 / 动画混合 / IK / 变形约束全都不做）。</para>
/// </summary>
public sealed record SpineSkeleton(
    string Version,
    double X, double Y, double Width, double Height,
    IReadOnlyList<SpineBone> Bones,
    IReadOnlyList<SpineSlot> Slots,
    IReadOnlyDictionary<string, SpineRegionAttachment> Attachments,
    IReadOnlyList<SpineAnimation> Animations,
    int UnsupportedAttachmentCount,
    IReadOnlyDictionary<string, int> AttachmentKindCounts)
{
    /// <summary><c>inherit</c> 不是 normal / onlyTranslation 的骨骼数（求值按 normal 近似，列出来提示简化）。</summary>
    public int SimplifiedInheritCount => Bones.Count(b => b.Inherit is not ("normal" or "onlyTranslation"));
}

/// <summary>Spine 文本的解析结果：骨架 / 图集 / 都不像。</summary>
public sealed record SpineParseResult(SpineSkeleton? Skeleton, SpineAtlas? Atlas)
{
    public static readonly SpineParseResult None = new(null, null);

    public bool Any => Skeleton is not null || Atlas is not null;
}

/// <summary>
/// Spine 文本解析（纯函数、无 IO，可直接单测）。<b>不猜格式</b>：
/// 结构不符就返回 <see cref="SpineParseResult.None"/>，让上层交回普通文本预览。
/// </summary>
public static class SpineTextParser
{
    /// <summary>最多解析的动画数（防止病态文件把 UI 拖住）。</summary>
    private const int MaxAnimations = 400;

    /// <summary>单条时间线最多解析的关键帧数。</summary>
    private const int MaxKeysPerTimeline = 20_000;

    /// <summary>最多解析的区域附件数。</summary>
    private const int MaxAttachments = 20_000;

    /// <summary>尝试按「骨架 JSON」或「图集文本」解析。</summary>
    /// <param name="text">TextAsset 解码后的文本（可含 BOM）。</param>
    public static SpineParseResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return SpineParseResult.None;
        var trimmed = text.TrimStart('\uFEFF', '\u200B', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith('{')) return new SpineParseResult(ParseSkeleton(trimmed), null);
        if (LooksLikeAtlas(trimmed)) return new SpineParseResult(null, ParseAtlas(trimmed));
        return SpineParseResult.None;
    }

    /// <summary>图集文本判据：文件头部（第一段内）出现 <c>size:</c> 行。</summary>
    public static bool LooksLikeAtlas(string text)
    {
        var lines = SplitLines(text);
        for (var i = 0; i < lines.Count && i < 12; i++)
        {
            if (lines[i].StartsWith("size:", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ── 骨架 JSON ────────────────────────────────────────────────────

    /// <summary>解析 Spine 骨架 JSON；结构不符时返回 null。</summary>
    public static SpineSkeleton? ParseSkeleton(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("bones", out var bonesElement) || bonesElement.ValueKind != JsonValueKind.Array)
                return null;

            var version = root.TryGetProperty("skeleton", out var skeleton) && skeleton.ValueKind == JsonValueKind.Object
                ? ReadString(skeleton, "spine") ?? string.Empty
                : string.Empty;
            var (sx, sy, sw, sh) = ReadCanvas(root);

            var bones = new List<SpineBone>();
            foreach (var item in bonesElement.EnumerateArray())
            {
                var name = ReadString(item, "name");
                if (string.IsNullOrEmpty(name)) continue;
                bones.Add(new SpineBone(
                    name,
                    ReadString(item, "parent"),
                    ReadNumber(item, "x"),
                    ReadNumber(item, "y"),
                    ReadNumber(item, "length"),
                    ReadNumber(item, "rotation"),
                    ReadNumber(item, "scaleX", 1),
                    ReadNumber(item, "scaleY", 1),
                    ReadString(item, "inherit") ?? "normal"));
            }
            if (bones.Count == 0) return null;

            var slots = new List<SpineSlot>();
            if (root.TryGetProperty("slots", out var slotsElement) && slotsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in slotsElement.EnumerateArray())
                {
                    var name = ReadString(item, "name");
                    if (string.IsNullOrEmpty(name)) continue;
                    slots.Add(new SpineSlot(name, ReadString(item, "bone") ?? string.Empty, ReadString(item, "attachment")));
                }
            }

            var attachments = new Dictionary<string, SpineRegionAttachment>(StringComparer.Ordinal);
            var kindCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var unsupported = ReadDefaultSkin(root, attachments, kindCounts);
            var animations = ReadAnimations(root);

            return new SpineSkeleton(version, sx, sy, sw, sh, bones, slots, attachments, animations,
                unsupported, kindCounts);
        }
    }

    private static (double X, double Y, double Width, double Height) ReadCanvas(JsonElement root)
    {
        if (!root.TryGetProperty("skeleton", out var skeleton) || skeleton.ValueKind != JsonValueKind.Object)
            return (0, 0, 0, 0);
        return (ReadNumber(skeleton, "x"), ReadNumber(skeleton, "y"),
            ReadNumber(skeleton, "width"), ReadNumber(skeleton, "height"));
    }

    /// <summary>
    /// 读默认皮肤里的附件，统计各类型数量。
    /// <c>skins</c> 在 Spine 4.0 是数组（<c>[{name, attachments}]</c>），3.8 是对象
    /// （<c>{default: {slot: {...}}}</c>）—— 两种都认；只取第一份非空皮肤（默认皮肤在最前）。
    /// </summary>
    private static int ReadDefaultSkin(
        JsonElement root, Dictionary<string, SpineRegionAttachment> attachments, Dictionary<string, int> kindCounts)
    {
        if (!root.TryGetProperty("skins", out var skins)) return 0;
        var unsupported = 0;

        void Collect(JsonElement skinAttachments)
        {
            if (skinAttachments.ValueKind != JsonValueKind.Object) return;
            foreach (var slotEntry in skinAttachments.EnumerateObject())
            {
                if (slotEntry.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var attachmentEntry in slotEntry.Value.EnumerateObject())
                {
                    var attachment = attachmentEntry.Value;
                    if (attachment.ValueKind != JsonValueKind.Object) continue;
                    var kind = ReadString(attachment, "type") ?? "region";
                    kindCounts[kind] = kindCounts.GetValueOrDefault(kind) + 1;
                    if (!string.Equals(kind, "region", StringComparison.Ordinal))
                    {
                        // 网格 / 包围盒 / 路径 / 裁剪 / 点附件：需要完整蒙皮或没有可绘制内容 → 跳过。
                        unsupported++;
                        continue;
                    }
                    if (attachments.Count >= MaxAttachments) continue;
                    var name = ReadString(attachment, "name") ?? attachmentEntry.Name;
                    attachments[attachmentEntry.Name] = new SpineRegionAttachment(
                        name,
                        ReadString(attachment, "path") ?? name,
                        ReadNumber(attachment, "x"),
                        ReadNumber(attachment, "y"),
                        ReadNumber(attachment, "rotation"),
                        ReadNumber(attachment, "scaleX", 1),
                        ReadNumber(attachment, "scaleY", 1),
                        ReadNumber(attachment, "width", 1),
                        ReadNumber(attachment, "height", 1));
                }
            }
        }

        if (skins.ValueKind == JsonValueKind.Array)
        {
            foreach (var skin in skins.EnumerateArray())
            {
                if (skin.ValueKind == JsonValueKind.Object && skin.TryGetProperty("attachments", out var map)) Collect(map);
                if (attachments.Count > 0) break;
            }
        }
        else if (skins.ValueKind == JsonValueKind.Object)
        {
            foreach (var skin in skins.EnumerateObject())
            {
                Collect(skin.Value);
                if (attachments.Count > 0) break;
            }
        }
        return unsupported;
    }

    private static IReadOnlyList<SpineAnimation> ReadAnimations(JsonElement root)
    {
        if (!root.TryGetProperty("animations", out var animations) || animations.ValueKind != JsonValueKind.Object)
            return [];

        var result = new List<SpineAnimation>();
        foreach (var animation in animations.EnumerateObject())
        {
            if (result.Count >= MaxAnimations) break;
            if (animation.Value.ValueKind != JsonValueKind.Object) continue;

            var bones = new Dictionary<string, IReadOnlyList<SpineKey>>(StringComparer.Ordinal);
            var slots = new Dictionary<string, IReadOnlyList<SpineKey>>(StringComparer.Ordinal);
            var duration = 0.0;

            if (animation.Value.TryGetProperty("bones", out var boneGroup) && boneGroup.ValueKind == JsonValueKind.Object)
            {
                foreach (var target in boneGroup.EnumerateObject())
                {
                    var keys = ReadBoneKeys(target.Value);
                    if (keys.Count == 0) continue;
                    bones[target.Name] = keys;
                    duration = Math.Max(duration, keys[^1].Time);
                }
            }

            if (animation.Value.TryGetProperty("slots", out var slotGroup) && slotGroup.ValueKind == JsonValueKind.Object)
            {
                foreach (var target in slotGroup.EnumerateObject())
                {
                    var keys = ReadSlotKeys(target.Value);
                    if (keys.Count == 0) continue;
                    slots[target.Name] = keys;
                    duration = Math.Max(duration, keys[^1].Time);
                }
            }

            result.Add(new SpineAnimation(animation.Name, duration, bones, slots));
        }
        return result.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>一条原始时间线：属性值可为空（该键没写这个属性 = 用 setup 默认值）。</summary>
    private sealed record RouteLine(string Property, IReadOnlyList<(double Time, double? A, double? B, string? Text, bool Stepped)> Keys);

    /// <summary>把 <c>rotate</c> / <c>translate</c> / <c>scale</c> 三条时间线合成统一关键帧序列。</summary>
    private static IReadOnlyList<SpineKey> ReadBoneKeys(JsonElement bone)
    {
        if (bone.ValueKind != JsonValueKind.Object) return [];
        var rotate = ReadTimeline(bone, "rotate", "value", null);
        var translate = ReadTimeline(bone, "translate", "x", "y");
        var scale = ReadTimeline(bone, "scale", "x", "y");
        var times = new SortedSet<double>();
        foreach (var line in new[] { rotate, translate, scale })
            foreach (var key in line) times.Add(key.Time);
        if (times.Count == 0) return [];

        var keys = new List<SpineKey>(times.Count);
        var ordered = times.ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var t = ordered[i];
            var r = rotate.FirstOrDefault(k => Math.Abs(k.Time - t) < 1e-6);
            var tr = translate.FirstOrDefault(k => Math.Abs(k.Time - t) < 1e-6);
            var sc = scale.FirstOrDefault(k => Math.Abs(k.Time - t) < 1e-6);
            // 旋转/缩放是「单值属性」，用 A 承载；平移用 A/B 承载 x/y。
            keys.Add(new SpineKey(
                t,
                tr.A, tr.B,
                r.A,
                sc.A, sc.B,
                null, null,
                (r.Stepped || tr.Stepped || sc.Stepped) && i < ordered.Length - 1));
        }
        return keys;
    }

    /// <summary>
    /// 槽位时间线：<c>attachment</c>（换装 / 显隐，名字为空 = 该槽位此段不可见）
    /// 与 <c>rgba</c>（槽位颜色，只取 alpha——预览关心的是淡入淡出）。
    /// 两条时间线按时间并成一条键序列，同一时刻都有关键帧就合并成一个键。
    /// </summary>
    private static IReadOnlyList<SpineKey> ReadSlotKeys(JsonElement slot)
    {
        if (slot.ValueKind != JsonValueKind.Object) return [];
        var attachment = ReadTimeline(slot, "attachment", "name", null);
        var rgba = ReadRgbaTimeline(slot);
        if (attachment.Count == 0 && rgba.Count == 0) return [];

        var times = new SortedSet<double>();
        foreach (var key in attachment) times.Add(key.Time);
        foreach (var key in rgba) times.Add(key.Time);

        var ordered = times.ToArray();
        var keys = new List<SpineKey>(ordered.Length);
        for (var i = 0; i < ordered.Length; i++)
        {
            var t = ordered[i];
            var att = attachment.FirstOrDefault(k => Math.Abs(k.Time - t) < 1e-6);
            var color = rgba.FirstOrDefault(k => Math.Abs(k.Time - t) < 1e-6);
            keys.Add(new SpineKey(t, null, null, null, null, null,
                att.Text, color.A,
                (att.Stepped || color.Stepped) && i < ordered.Length - 1));
        }
        return keys;
    }

    /// <summary>槽位 <c>rgba</c> 时间线：把颜色字符串的 alpha 提到 A 位（其余属性不适用）。</summary>
    private static List<(double Time, double? A, double? B, string? Text, bool Stepped)> ReadRgbaTimeline(JsonElement slot)
    {
        var keys = new List<(double, double?, double?, string?, bool)>();
        if (!slot.TryGetProperty("rgba", out var array) || array.ValueKind != JsonValueKind.Array) return keys;
        foreach (var item in array.EnumerateArray())
        {
            if (keys.Count >= MaxKeysPerTimeline) break;
            var time = item.TryGetProperty("time", out var t) ? ReadDouble(t, 0) : 0;
            var alpha = item.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.String
                ? AlphaOf(color.GetString())
                : null;
            var stepped = item.TryGetProperty("curve", out var curve)
                          && curve.ValueKind == JsonValueKind.String
                          && string.Equals(curve.GetString(), "stepped", StringComparison.OrdinalIgnoreCase);
            keys.Add((time, alpha, null, null, stepped));
        }
        keys.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        return keys;
    }

    /// <summary>Spine 颜色字符串的 alpha（0–1）。<c>RRGGBBAA</c> 取末两位；<c>RRGGBB</c> / <c>RGB</c> 视为不透明。</summary>
    private static double? AlphaOf(string? color)
    {
        if (string.IsNullOrEmpty(color)) return null;
        if (color.Length >= 8
            && byte.TryParse(color.AsSpan(color.Length - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var alpha))
            return alpha / 255.0;
        return color.Length is 6 or 3 ? 1.0 : null;
    }

    /// <summary>读一条时间线（<c>[{time, a, b, curve}]</c>）。第一条没有 <c>time</c> 视为 0。</summary>
    private static List<(double Time, double? A, double? B, string? Text, bool Stepped)> ReadTimeline(
        JsonElement owner, string property, string aName, string? bName)
    {
        var keys = new List<(double, double?, double?, string?, bool)>();
        if (!owner.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return keys;
        foreach (var item in array.EnumerateArray())
        {
            if (keys.Count >= MaxKeysPerTimeline) break;
            var time = item.TryGetProperty("time", out var t) ? ReadDouble(t, 0) : 0;
            string? text = null;
            double? a = null;
            double? b = null;
            if (item.TryGetProperty(aName, out var av))
            {
                if (av.ValueKind == JsonValueKind.String) text = av.GetString();
                else a = ReadDouble(av, 0);
            }
            if (bName is not null && item.TryGetProperty(bName, out var bv)) b = ReadDouble(bv, 0);
            var stepped = item.TryGetProperty("curve", out var curve)
                          && curve.ValueKind == JsonValueKind.String
                          && string.Equals(curve.GetString(), "stepped", StringComparison.OrdinalIgnoreCase);
            keys.Add((time, a, b, text, stepped));
        }
        keys.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        return keys;
    }

    // ── 图集文本 ─────────────────────────────────────────────────────

    /// <summary>
    /// 解析 <c>.atlas.txt</c>（Spine 图集文本格式，可含多页）。
    ///
    /// <para>口径：页头 = 「无冒号的行（页名）」紧跟「<c>size:</c>」；页内区域 = 无冒号的行（区域名）
    /// 后跟若干 <c>key: value</c> 属性行。区域性行不可能以 <c>size:</c> 开头，因此页/区域不会误判。</para>
    /// </summary>
    public static SpineAtlas ParseAtlas(string text)
    {
        var lines = SplitLines(text);
        var pages = new List<SpineAtlasPage>();
        var regions = new List<SpineAtlasRegion>();

        string? pageName = null;
        var pageWidth = 0;
        var pageHeight = 0;
        var pageScale = 1.0;

        string? regionName = null;
        int rx = 0, ry = 0, rw = 0, rh = 0, rrot = 0, row = 0, roh = 0, rox = 0, roy = 0;
        var hasBounds = false;

        void FlushRegion()
        {
            if (regionName is not null && hasBounds)
                regions.Add(new SpineAtlasRegion(regionName, rx, ry, rw, rh, rrot, row, roh, rox, roy));
            regionName = null;
            hasBounds = false;
            rrot = 0; row = 0; roh = 0; rox = 0; roy = 0;
        }

        void FlushPage()
        {
            FlushRegion();
            if (pageName is null) return;
            pages.Add(new SpineAtlasPage(pageName, pageWidth, pageHeight, pageScale, regions.ToArray()));
            regions = [];
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0) continue;

            if (line.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line[5..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                {
                    pageWidth = w;
                    pageHeight = h;
                }
                continue;
            }
            if (line.StartsWith("scale:", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(line[6..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0)
                    pageScale = s;
                continue;
            }
            if (line.StartsWith("filter:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("pma:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("repeat:", StringComparison.OrdinalIgnoreCase))
                continue;

            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                var key = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();
                if (key is "bounds" or "xy" or "orig" or "offsets")
                    ApplyRegionAttribute(key, value, ref rx, ref ry, ref rw, ref rh, ref rox, ref roy, ref row, ref roh, ref hasBounds);
                else if (key == "rotate" && int.TryParse(value, out var rotation))
                    rrot = rotation;
                continue;
            }

            // 无冒号 = 页名或区域名：看下一行（跳过空行）是不是 size:。
            var next = NextNonEmpty(lines, i + 1);
            if (next is not null && next.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
            {
                FlushPage();
                pageName = line;
                pageWidth = 0;
                pageHeight = 0;
                pageScale = 1.0;
                continue;
            }
            FlushRegion();
            regionName = line;
        }

        FlushPage();
        return new SpineAtlas(pages);
    }

    private static string? NextNonEmpty(IReadOnlyList<string> lines, int start)
    {
        for (var i = start; i < lines.Count; i++)
        {
            var candidate = lines[i].Trim();
            if (candidate.Length > 0) return candidate;
        }
        return null;
    }

    private static void ApplyRegionAttribute(
        string key, string value,
        ref int x, ref int y, ref int w, ref int h,
        ref int ox, ref int oy, ref int ow, ref int oh, ref bool hasBounds)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]);
        switch (key)
        {
            case "bounds" when numbers.Length >= 4:
                x = numbers[0]; y = numbers[1]; w = numbers[2]; h = numbers[3];
                hasBounds = true;
                break;
            case "xy" when numbers.Length >= 2:
                // 裁剪后的相对偏移（Spine 3.6+ 口径）：叠加到已有 bounds 上。
                x += numbers[0]; y += numbers[1];
                break;
            case "orig" when numbers.Length >= 2:
                ow = numbers[0]; oh = numbers[1];
                break;
            case "offsets" when numbers.Length >= 2:
                ox = numbers[0]; oy = numbers[1];
                break;
        }
    }

    // ── 基本读取工具 ─────────────────────────────────────────────────

    private static List<string> SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double ReadNumber(JsonElement element, string property, double fallback = 0)
        => element.TryGetProperty(property, out var value) ? ReadDouble(value, fallback) : fallback;

    private static double ReadDouble(JsonElement value, double fallback)
        => value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out var number) ? number : fallback,
            JsonValueKind.String => double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback,
            _ => fallback,
        };
}
