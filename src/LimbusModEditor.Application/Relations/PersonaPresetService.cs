using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Relations;

/// <summary>
/// 预设卡片（卡片流的单元）：一个人格 + 它的资源构成 + 选中的封面。
/// </summary>
/// <param name="SubjectId">人格 id（关联键）。</param>
/// <param name="Title">卡片标题（如「10201 · 浮士德 · LCB」）。</param>
/// <param name="Subtitle">副标题（风格 token，如 <c>LCB</c>）。</param>
/// <param name="Summary">资源构成摘要（如「静态数据 12 · 音频 8 · 图像 3」）。</param>
/// <param name="PortraitLink">选中的封面链接（无可用立绘时为 null）。</param>
/// <param name="PortraitAsset">封面资源（能直接渲染时为非 null；否则卡片退化为首字母块）。</param>
/// <param name="LinkCount">该人格的关联资源总数。</param>
public sealed record PresetCard(
    string SubjectId,
    string Title,
    string Subtitle,
    string Summary,
    RelationLink? PortraitLink,
    AssetRecord? PortraitAsset,
    int LinkCount);

/// <summary>详情里的一行（一个具体资源 / 文本文件）。</summary>
/// <param name="Display">显示名（文件名 / 样本名 / 表名）。</param>
/// <param name="Detail">补充说明（编码 / 体积 / 键数）。</param>
/// <param name="RefKey">定位键（关联图里的口径）。</param>
/// <param name="Asset">能定位到项目资源时为非 null（可直接预览）。</param>
public sealed record PresetDetailRow(string Display, string? Detail, string RefKey, AssetRecord? Asset);

/// <summary>详情里的一个分组（按关联类别）。</summary>
/// <param name="Kind">关联类别。</param>
/// <param name="Title">中文标题（<see cref="RelationDisplayRules.KindLabel"/>）。</param>
/// <param name="Rows">该类别下的全部资源。</param>
public sealed record PresetDetailGroup(RelationKind Kind, string Title, IReadOnlyList<PresetDetailRow> Rows);

/// <summary>
/// 预设（人格）卡片流与详情的<b>展示层服务</b>：把关联图 + 项目资源索引整理成 UI 直接绑定的卡片 / 分组。
///
/// <para><b>为什么单独一层</b>：关联图给的是「人格 → 定位键」，而 UI 要的是「能画出来的卡片」——
/// 中间差两件事：① 从多个图像链接里挑一张最像立绘的封面；
/// ② 把定位键还原成 <see cref="AssetRecord"/>（只有后者才能读字节做预览）。
/// 这两件事都是纯逻辑（无 IO），放在 Application 层可以被单测覆盖（App 没有测试工程）。</para>
///
/// <para><b>封面挑选规则</b>：按 <see cref="RelationDisplayRules.PortraitRank"/> 升序取第一个
/// 「能定位到项目资源」的图像链接；全部定位不到时退化为第一个图像链接（只显示名字）。
/// 排序稳定（并列时按定位键），因此同一份数据每次得到的封面一致。</para>
/// </summary>
public sealed class PersonaPresetService
{
    private readonly RelationQueryService _relations;

    public PersonaPresetService(RelationQueryService relations)
    {
        ArgumentNullException.ThrowIfNull(relations);
        _relations = relations;
    }

    /// <summary>关联图是否已建好（没建好时页面显示「启动扫描后自动分析」）。</summary>
    public bool IsReady => _relations.IsReady;

    /// <summary>全部预设对象（按人格 id 数字序）。</summary>
    public IReadOnlyList<RelationSubject> Subjects() => _relations.Subjects();

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
            // 同一容器路径可能有多条对象行（同一容器引用多个对象）：保留「更可渲染」的那条。
            // 判定顺序：有源文件 > 体积大；相同则先到先得（稳定）。
            if (!map.TryGetValue(key, out var existing)) { map[key] = asset; continue; }
            if (Renderable(asset) && !Renderable(existing)) map[key] = asset;
        }
        return map;
    }

    /// <summary>是否较可能被渲染（有源文件 + 有 Unity PathId）。</summary>
    private static bool Renderable(AssetRecord asset)
        => asset.UnityPathId.HasValue
           && !string.IsNullOrWhiteSpace(asset.SourcePath)
           && File.Exists(asset.SourcePath);

    /// <summary>构建卡片流。<paramref name="keyword"/> 非空时按 id / 标题过滤（大小写不敏感）。</summary>
    public IReadOnlyList<PresetCard> BuildCards(IReadOnlyList<AssetRecord> assets, string? keyword = null)
    {
        var index = IndexByContainerEntry(assets);
        var filter = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        var cards = new List<PresetCard>();
        foreach (var subject in Subjects())
        {
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
            var rank = RelationDisplayRules.PortraitRank(link.RefKey, asset?.Type ?? AssetType.Sprite);
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
        return new PresetCard(
            subject.SubjectId,
            $"{subject.SubjectId} · {subject.DisplayName}",
            subject.Subtitle,
            string.Join(" · ", composition),
            best,
            bestAsset,
            links.Count);
    }

    /// <summary>
    /// 某人格的详情分组（按类别，类别顺序固定：数据 → 文本 → 音频 → 图像 → 视频 → Spine …）。
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
                        return new PresetDetailRow(link.Display, link.Detail, link.RefKey, asset);
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
    /// 某条详情行该跳到哪个工作台（供「打开」按钮使用）：
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
    /// 「打开」按钮填进搜索框的关键词：文本给文件名、音频给样本名、其余给定位键
    /// （Unity 容器路径 / 表名）。见 <see cref="PresetDetailRow"/>。
    /// </summary>
    public static string SearchKeywordFor(PresetDetailRow row) => FileName(row.Display) is { Length: > 0 } name
        ? name
        : row.Display;

    private static string FileName(string value)
    {
        var index = value.Replace('\\', '/').LastIndexOf('/');
        return index < 0 ? value : value[(index + 1)..];
    }
}
