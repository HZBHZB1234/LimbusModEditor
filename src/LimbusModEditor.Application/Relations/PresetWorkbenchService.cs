using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Relations;

/// <summary>卡片流里的一个类别（类别切换器的数据源）。</summary>
/// <param name="Category">类别标识（<see cref="RelationCategories"/>）。</param>
/// <param name="Label">中文名（如「E.G.O 装备」）。</param>
/// <param name="Count">该类别下的对象数（角标）。</param>
public sealed record PresetCategory(string Category, string Label, int Count);

/// <summary>
/// 预设卡片（卡片流的单元）：一个对象 + 它的资源构成 + 选中的封面 + 正面预览。
/// </summary>
/// <param name="SubjectId">关联键（<c>&lt;category&gt;:&lt;key&gt;</c>）。</param>
/// <param name="Category">类别标识。</param>
/// <param name="CategoryLabel">类别中文名（卡片上的类别小标签）。</param>
/// <param name="Title">卡片标题（如「浮士德 · LCB」）。</param>
/// <param name="Subtitle">副标题（风格 token / E.G.O 的说明）。</param>
/// <param name="Summary">资源构成摘要（如「图像 12 · 文本 8」）。</param>
/// <param name="PreviewText">卡片正面的一句话预览（代表台词 / E.G.O 名称）。</param>
/// <param name="PreviewKind">这条预览的强度（UI 据此标「精确 / 推导」）。</param>
/// <param name="PortraitLink">选中的封面链接（无可用立绘时为 null）。</param>
/// <param name="PortraitAsset">封面资源（能直接渲染时为非 null；否则卡片退化为首字母块）。</param>
/// <param name="LinkCount">该对象的关联资源总数。</param>
public sealed record PresetCard(
    string SubjectId,
    string Category,
    string CategoryLabel,
    string Title,
    string Subtitle,
    string Summary,
    string? PreviewText,
    RelationPreviewKind PreviewKind,
    RelationLink? PortraitLink,
    AssetRecord? PortraitAsset,
    int LinkCount);

/// <summary>
/// 详情里的一行（一个具体资源 / 文本文件）。除了「叫什么、能跳去哪」，
/// 还带上<b>内联预览所需的一切</b>：预览内容、强度、形态、时长、精确跳转载荷。
/// </summary>
/// <param name="Display">显示名（文件名 / 样本名 / 表名）。</param>
/// <param name="Detail">补充说明（编码 / 体积 / 键数）。</param>
/// <param name="RefKey">定位键（关联图里的口径）。</param>
/// <param name="Asset">能定位到项目资源时为非 null（可直接预览）。</param>
public sealed record PresetDetailRow(string Display, string? Detail, string RefKey, AssetRecord? Asset)
{
    /// <summary>可直接展示的内容（音频=台词原文、文本=名称/正文、静态=记录名）。</summary>
    public string? PreviewText { get; init; }

    /// <summary>预览内容的强度（精确 / 推导 / 歧义）。</summary>
    public RelationPreviewKind PreviewKind { get; init; }

    /// <summary>预览形态：<c>audio</c> / <c>text</c> / <c>static</c> / <c>image</c> / <c>video</c> / <c>spine</c>…</summary>
    public string MediaKind { get; init; } = "other";

    /// <summary>音频时长（秒）；非音频为 null。</summary>
    public double? DurationSec { get; init; }

    /// <summary>精确跳转载荷（<see cref="RelationDeepLink"/> 口径）。</summary>
    public string? DeepLink { get; init; }
}

/// <summary>详情里的一个分组（按关联类别）。</summary>
/// <param name="Kind">关联类别。</param>
/// <param name="Title">中文标题（<see cref="RelationDisplayRules.KindLabel"/>）。</param>
/// <param name="Rows">该类别下的全部资源。</param>
public sealed record PresetDetailGroup(RelationKind Kind, string Title, IReadOnlyList<PresetDetailRow> Rows);

/// <summary>
/// 一个「跳到某个资源」的意图：目标工作台 + 精确载荷。
/// <para><b>为什么带载荷而不是关键词</b>：按关键词过滤只能把用户送到「一屏里有这个名字」，
/// 还得自己再找一遍；载荷能让目标页<b>选中并滚到那一行</b>（见各页的
/// <c>IReferenceRevealable</c> 实现）。</para>
/// </summary>
/// <param name="PageKey">目标工作台（<see cref="PresetWorkbenchService.WorkbenchKeyFor"/>）。</param>
/// <param name="Payload">精确载荷（<see cref="RelationDeepLink"/> 的编码，或资源容器路径）。</param>
public sealed record PresetReferenceTarget(string PageKey, string Payload);

/// <summary>
/// 预设卡片流与详情的<b>展示层服务</b>：把关联图 + 项目资源索引整理成 UI 直接绑定的卡片 / 分组。
///
/// <para><b>为什么单独一层</b>：关联图给的是「对象 → 定位键」，而 UI 要的是「能画出来的卡片」——
/// 中间差三件事：① 从多个图像链接里挑一张最像立绘的封面；② 把定位键还原成
/// <see cref="AssetRecord"/>（只有后者才能读字节做预览）；③ 组装精确跳转载荷。
/// 这些都是纯逻辑（无 IO 判定），放在 Application 层可以被单测覆盖（App 没有测试工程）。</para>
///
/// <para><b>覆盖全部类别</b>：人格 / 敌人单位 / 异想体 / 播报员 / E.G.O 装备 / E.G.O 饰品
/// 共用同一条卡片流，靠 <see cref="Categories"/> 切换——因为它们在数据层就是同一张
/// <c>subjects</c> 表，只是 <c>subject_kind</c> 不同。</para>
/// </summary>
public sealed class PresetWorkbenchService
{
    private readonly RelationQueryService _relations;

    public PresetWorkbenchService(RelationQueryService relations)
    {
        ArgumentNullException.ThrowIfNull(relations);
        _relations = relations;
    }

    /// <summary>关联图是否已建好（没建好时页面显示「启动扫描后自动分析」）。</summary>
    public bool IsReady => _relations.IsReady;

    /// <summary>全部对象（跨类别，按类别 + id 排序）。</summary>
    public IReadOnlyList<RelationSubject> Subjects() => _relations.Subjects();

    /// <summary>
    /// 有对象的类别及其计数（切换器只列<b>真的有内容</b>的类别，
    /// 避免用户点进一个空页；顺序沿用 <see cref="RelationCategories.All"/>）。
    /// </summary>
    public IReadOnlyList<PresetCategory> Categories()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var subject in Subjects())
            counts[subject.SubjectKind] = counts.GetValueOrDefault(subject.SubjectKind) + 1;

        var ordered = RelationCategories.All.Where(counts.ContainsKey)
            .Concat(counts.Keys.Where(x => !RelationCategories.IsKnown(x)).Order(StringComparer.Ordinal));
        return ordered
            .Select(x => new PresetCategory(x, RelationCategories.Label(x), counts[x]))
            .ToArray();
    }

    /// <summary>
    /// 把项目资源索引成「容器路径 → 资源」的字典（关联图的资源侧口径 = 容器路径）。
    /// <paramref name="assets"/> 通常是 <c>project.Assets</c>。
    /// </summary>
    public static IReadOnlyDictionary<string, AssetRecord> IndexByContainerEntry(IReadOnlyList<AssetRecord> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var map = new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            var key = AssetDisplay.ContainerEntryPath(asset);
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!map.TryGetValue(key, out var existing)) { map[key] = asset; continue; }
            if (Preferable(asset, existing)) map[key] = asset;
        }
        return map;
    }

    /// <summary>
    /// 同一个容器路径的两条记录里该留哪一条。
    ///
    /// <para><b>真实数据里的关键事实</b>：同一条 <c>container_entry</c> 常常既有 Sprite 行
    /// 也有 Texture 行，两者都「可渲染」（同一个 bundle、都有 pathId）。而读位图用的是
    /// <c>ReadTexturePng(sourcePath, pathId)</c>，它<b>只能</b>按 Texture2D 的 pathId 解码——
    /// 选中 Sprite 行必然返回 null，表现成「封面一片空白」。所以 Texture 必须优先。</para>
    ///
    /// <para>判定顺序：可渲染 &gt; Texture &gt; Sprite；完全相同则保留先到的那条（稳定）。</para>
    /// </summary>
    private static bool Preferable(AssetRecord candidate, AssetRecord current)
    {
        var candidateRenderable = Renderable(candidate);
        var currentRenderable = Renderable(current);
        if (candidateRenderable != currentRenderable) return candidateRenderable;
        return IsTexture(candidate) && !IsTexture(current);
    }

    private static bool IsTexture(AssetRecord asset) => asset.Type == AssetType.Texture;

    /// <summary>是否较可能被渲染（有源文件 + 有 Unity PathId）。</summary>
    private static bool Renderable(AssetRecord asset)
        => asset.UnityPathId.HasValue
           && !string.IsNullOrWhiteSpace(asset.SourcePath)
           && File.Exists(asset.SourcePath);

    /// <summary>
    /// 构建卡片流。<paramref name="category"/> 为 null 时返回全部类别；
    /// <paramref name="keyword"/> 非空时按 id / 标题 / 角色 token 过滤（大小写不敏感）。
    /// </summary>
    public IReadOnlyList<PresetCard> BuildCards(
        IReadOnlyList<AssetRecord> assets, string? category = null, string? keyword = null)
    {
        var index = IndexByContainerEntry(assets);
        var filter = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        var cards = new List<PresetCard>();
        foreach (var subject in Subjects())
        {
            if (category is not null && !string.Equals(subject.SubjectKind, category, StringComparison.Ordinal))
                continue;
            if (filter is not null
                && !subject.SubjectId.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !subject.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !subject.Character.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var links = _relations.Links(subject.SubjectId);
            cards.Add(BuildCard(subject, links, index));
        }
        return cards;
    }

    private static PresetCard BuildCard(
        RelationSubject subject, IReadOnlyList<RelationLink> links, IReadOnlyDictionary<string, AssetRecord> index)
    {
        var counts = new Dictionary<RelationKind, int>();
        foreach (var link in links) counts[link.Kind] = counts.GetValueOrDefault(link.Kind) + 1;

        // 封面：图像链接里按「像不像立绘」排序，优先能定位到资源的。
        // 比较规则（字典序）：可渲染 > 不可渲染 → 立绘优先级 rank 小 → 定位键小（保证稳定）。
        RelationLink? best = null;
        AssetRecord? bestAsset = null;
        var bestRank = 0;
        var bestResolved = false;
        foreach (var link in links.Where(x => x.Kind == RelationKind.Image))
        {
            index.TryGetValue(link.RefKey, out var asset);
            // 用 CoverCandidateRank（而不是 PortraitRank）：它把「同路径优先 Texture」
            // 也编进了排序，与 IndexByContainerEntry 的取向保持一致。
            var rank = RelationDisplayRules.CoverCandidateRank(link.RefKey, asset?.Type ?? AssetType.Sprite);
            if (rank < 0) continue;
            var resolved = asset is not null && Renderable(asset);
            var better = best is null
                || (resolved != bestResolved && resolved)
                || (resolved == bestResolved && rank != bestRank && rank < bestRank)
                || (resolved == bestResolved && rank == bestRank
                    && string.CompareOrdinal(link.RefKey, best.RefKey) < 0);
            if (!better) continue;
            best = link;
            bestAsset = asset;
            bestRank = rank;
            bestResolved = resolved;
        }

        var composition = counts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key)
            .Take(4)
            .Select(x => $"{RelationDisplayRules.KindLabel(x.Key)} {x.Value}");
        var (previewText, previewKind) = PickPreview(links, subject.PreviewText);
        return new PresetCard(
            subject.SubjectId,
            subject.SubjectKind,
            string.IsNullOrWhiteSpace(subject.CategoryLabel)
                ? RelationCategories.Label(subject.SubjectKind)
                : subject.CategoryLabel,
            subject.DisplayName,
            subject.Subtitle,
            string.Join(" · ", composition),
            previewText,
            previewKind,
            best,
            bestAsset,
            links.Count);
    }

    /// <summary>
    /// 卡片正面的预览内容与<b>强度</b>。
    ///
    /// <para>强度只存在于 <see cref="RelationLink"/> 上、不在 subjects 表里，所以这里从链接重算
    /// （分析器算出的 <c>Subject.PreviewText</c> 只作为兜底文本，两者不一致时以这里的强度为准）。
    /// 精确命中优先于推导——把推导标成精确正是要避免的事。</para>
    /// </summary>
    private static (string? Text, RelationPreviewKind Kind) PickPreview(
        IReadOnlyList<RelationLink> links, string? fallback)
    {
        foreach (var link in links)
            if (link.PreviewKind == RelationPreviewKind.Exact && !string.IsNullOrWhiteSpace(link.PreviewText))
                return (link.PreviewText, RelationPreviewKind.Exact);
        foreach (var link in links)
            if (!string.IsNullOrWhiteSpace(link.PreviewText))
                return (link.PreviewText, link.PreviewKind);
        return (fallback, string.IsNullOrWhiteSpace(fallback) ? RelationPreviewKind.None : RelationPreviewKind.Derived);
    }

    /// <summary>
    /// 某对象的详情分组（按类别，类别顺序固定：数据 → 文本 → 音频 → 图像 → 视频 → Spine …）。
    /// </summary>
    public IReadOnlyList<PresetDetailGroup> BuildDetail(string subjectId, IReadOnlyList<AssetRecord> assets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        var index = IndexByContainerEntry(assets);
        var links = _relations.Links(subjectId);
        return links
            .GroupBy(x => x.Kind)
            .OrderBy(g => KindOrder(g.Key))
            .Select(g => new PresetDetailGroup(
                g.Key,
                RelationDisplayRules.KindLabel(g.Key),
                g.OrderBy(x => x.Display, StringComparer.OrdinalIgnoreCase)
                    .Select(link =>
                    {
                        index.TryGetValue(link.RefKey, out var asset);
                        return new PresetDetailRow(link.Display, link.Detail, link.RefKey, asset)
                        {
                            PreviewText = link.PreviewText,
                            PreviewKind = link.PreviewKind,
                            MediaKind = link.MediaKind,
                            DurationSec = link.DurationSec,
                            DeepLink = link.DeepLink,
                        };
                    })
                    .ToArray()))
            .Where(g => g.Rows.Count > 0)
            .ToArray();
    }

    /// <summary>详情分组的显示顺序（用户视角：先数据与文本，再影音，最后技术资源）。</summary>
    private static int KindOrder(RelationKind kind) => kind switch
    {
        RelationKind.StaticData => 0,
        RelationKind.Text => 1,
        RelationKind.Audio => 2,
        RelationKind.Image => 3,
        RelationKind.Video => 4,
        RelationKind.Spine => 5,
        RelationKind.Animation => 6,
        RelationKind.Prefab => 7,
        RelationKind.Mesh => 8,
        RelationKind.Other => 9,
        _ => 10,
    };

    /// <summary>
    /// 某条详情行该跳到哪个工作台（供「定位」按钮使用）：
    /// 文本 → 文本工作台；音频 → 音频工作台；静态数据 → 静态工作台；其余 Unity 资源 → 资源工作台。
    /// </summary>
    public static string WorkbenchKeyFor(RelationKind kind) => kind switch
    {
        RelationKind.Text => Application.AppConfig.WorkbenchPageKeys.Text,
        RelationKind.Audio => Application.AppConfig.WorkbenchPageKeys.Bank,
        RelationKind.StaticData => Application.AppConfig.WorkbenchPageKeys.Static,
        _ => Application.AppConfig.WorkbenchPageKeys.Assets,
    };

    /// <summary>
    /// 「定位」按钮的跳转意图：目标工作台 + <b>精确载荷</b>。
    /// 载荷优先用关联图给的 <see cref="RelationDeepLink"/>（能定位到「那一行」），
    /// 没有时退化为定位键（只能定位到资源级）。
    /// </summary>
    public static PresetReferenceTarget TargetFor(RelationKind kind, PresetDetailRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var payload = string.IsNullOrEmpty(row.DeepLink) ? row.RefKey : row.DeepLink;
        return new PresetReferenceTarget(WorkbenchKeyFor(kind), payload);
    }
}
