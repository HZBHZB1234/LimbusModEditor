using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using Microsoft.Data.Sqlite;

namespace LimbusModEditor.Application.Relations;

/// <summary>一条「关联资源」板块的行（视图直接用）。</summary>
/// <param name="SubjectId">对象 id（人格 id）。</param>
/// <param name="Title">标题（如「10201 · 浮士德 · LCB」）。</param>
/// <param name="Summary">各类关联的分组摘要（如「图像 3 · 音频 12 · 文本 2」）。</param>
public sealed record RelationSubjectRow(string SubjectId, string Title, string Summary);

/// <summary>「关联资源」板块的一次查询结果。</summary>
/// <param name="Rows">命中的对象行（可为空）。</param>
/// <param name="Info">中文说明（供预览面板显示；也解释「为什么没有行」）。</param>
public sealed record RelationSubjectLookup(IReadOnlyList<RelationSubjectRow> Rows, string Info)
{
    public static readonly RelationSubjectLookup Empty = new([], "此资源没有关联到任何预设对象。");
}

/// <summary>
/// 一条「资源 → 对象」命中：<b>对象身份 + 命中的那条引用的形态</b>（IPC relation.describe 的一行）。
///
/// <para>为什么不是 <see cref="RelationSubjectRow"/>：后者只给「标题 + 各类条数摘要」，
/// 回答不了「这条引用是图像还是音频、引用的文件叫什么」——而资源工作台的反查面板
/// 正好要按「引用的 kind / display」分行（一个资源可能以多种形态属于同一个对象）。</para>
/// </summary>
public sealed record RelationSubjectHit(
    string SubjectId,
    string DisplayName,
    string Category,
    string CategoryLabel,
    string Kind,
    string KindLabel,
    string Display,
    string? Detail,
    string Subtitle,
    string Character);

/// <summary>一次反查结果；<paramref name="Info"/> 解释「为什么没有命中」。</summary>
public sealed record RelationSubjectHits(IReadOnlyList<RelationSubjectHit> Hits, string Info);

/// <summary>
/// 关联图的<b>查询门面</b>：把 <see cref="RelationStore"/> 的原始行整理成 UI 能直接绑定的结果。
///
/// <para>两个方向都覆盖：</para>
/// <list type="bullet">
/// <item><b>资源 → 对象</b>（<see cref="FindSubjectsForAsset"/>）：资源预览的「关联资源」板块，
/// 用来回答「我现在看的这张图 / 这条语音，是哪个角色的？」——靠
/// <c>subjects_by_ref</c> 反向索引做 O(1) 查。</item>
/// <item><b>对象 → 资源</b>（<see cref="Links"/> / <see cref="CountByKind"/>）：预设卡片流页面
/// 点进人格后列它的文本 / 数据 / 音频 / 图像 / 动画 / Spine。</item>
/// </list>
///
/// <para>库不存在或还没分析时返回空结果 + 中文原因（<b>绝不抛异常</b>）：
/// 关联图是派生旁路，缺了只是「少一块信息」，不该把资源预览搞崩。</para>
/// </summary>
public sealed class RelationQueryService
{
    private readonly RelationStore _store;

    /// <param name="store">关联索引库（<c>new RelationStore(env.CacheDirectory)</c>）。</param>
    public RelationQueryService(RelationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>关联图是否已建好（没建好时 UI 显示「启动扫描后自动分析」）。</summary>
    public bool IsReady
    {
        get
        {
            try
            {
                return _store.Exists && _store.ReadSubjectCount() > 0;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return false;
            }
        }
    }

    /// <summary>全部预设对象（按 id 数字序）。</summary>
    public IReadOnlyList<RelationSubject> Subjects() => _store.ReadSubjects();

    /// <summary>某对象的全部关联资源。</summary>
    public IReadOnlyList<RelationLink> Links(string subjectId) => _store.ReadLinks(subjectId);

    /// <summary>某对象某一类别的关联资源。</summary>
    public IReadOnlyList<RelationLink> Links(string subjectId, RelationKind kind) => _store.ReadLinks(subjectId, kind);

    /// <summary>某对象各类别的关联条数（卡片 / 分组标题的摘要用）。</summary>
    public IReadOnlyDictionary<RelationKind, int> CountByKind(string subjectId)
    {
        var counts = new Dictionary<RelationKind, int>();
        foreach (var link in _store.ReadLinks(subjectId))
            counts[link.Kind] = counts.GetValueOrDefault(link.Kind) + 1;
        return counts;
    }

    /// <summary>
    /// 反查：引用这批定位键的对象（资源预览的「关联资源」板块）。
    /// <paramref name="refKeys"/> 是候选键（同一个资源在不同通道里口径不同，见
    /// <see cref="RefKeysForAsset"/>）。
    /// </summary>
    public IReadOnlyList<RelationSubject> FindSubjectsByRefKeys(IEnumerable<string> refKeys)
    {
        ArgumentNullException.ThrowIfNull(refKeys);
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in refKeys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            foreach (var id in _store.ReadSubjectIdsByRef(key)) ids.Add(id);
        }
        if (ids.Count == 0) return [];
        var byId = new Dictionary<string, RelationSubject>(StringComparer.Ordinal);
        foreach (var subject in _store.ReadSubjects()) byId[subject.SubjectId] = subject;
        return ids.Where(byId.ContainsKey).Select(x => byId[x]).ToArray();
    }

    /// <summary>一个 Unity 资源的候选定位键（容器路径 = 关联图里资源侧的口径）。</summary>
    public static IReadOnlyList<string> RefKeysForAsset(AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var keys = new List<string>(2);
        var container = AssetDisplay.ContainerEntryPath(asset);
        if (!string.IsNullOrWhiteSpace(container)) keys.Add(container);
        return keys;
    }

    /// <summary>
    /// 一次查完「这个资源属于哪些对象」，并给出中文说明（含「为什么没有」）。
    /// </summary>
    public RelationSubjectLookup DescribeSubjectsForAsset(AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!IsReady) return new RelationSubjectLookup([], "关联图还没建立（启动扫描会自动分析四个索引库）。");

        var refKeys = RefKeysForAsset(asset);
        if (refKeys.Count == 0)
            return new RelationSubjectLookup([], "此资源没有游戏内容器路径，无法用关联图定位（关联键是容器路径 / 相对路径）。");

        var subjects = FindSubjectsByRefKeys(refKeys);
        if (subjects.Count == 0)
            return new RelationSubjectLookup([], "此资源没有关联到任何预设对象（它不属于任何已登记的人格资源）。");

        var rows = new List<RelationSubjectRow>(subjects.Count);
        foreach (var subject in subjects)
        {
            // 摘要给「这个人格一共有哪些资源」的构成（最多列 4 类），让用户一眼看出
            // 这个资源在整体里的位置，而不是只知道「命中了」。
            var composition = CountByKind(subject.SubjectId)
                .OrderByDescending(x => x.Value)
                .Take(4)
                .Select(x => $"{RelationDisplayRules.KindLabel(x.Key)} {x.Value}");
            rows.Add(new RelationSubjectRow(
                subject.SubjectId,
                $"{subject.SubjectId} · {subject.DisplayName}",
                string.Join(" · ", composition)));
        }
        return new RelationSubjectLookup(rows, $"该资源被 {rows.Count} 个预设对象引用（点「查看」在资源列表里搜索该人格 id）。");
    }

    /// <summary>
    /// IPC <c>relation.describe</c> 的取数：一个资源 → 引用了它的对象 + 每条引用的形态。
    ///
    /// <para>与 <see cref="DescribeSubjectsForAsset"/> 的分工：那是给 WPF 预览面板的
    /// 「标题 + 摘要」行；这条给 Web 反查面板的<b>结构化行</b>（前端要按 kind/display 分行、要拿
    /// 对象 id 当维基页面 id）。库没建好 / 资源没有容器路径 / 没命中都返回<b>空列表 + 中文原因</b>，
    /// 绝不抛异常、绝不编造。</para>
    /// </summary>
    public RelationSubjectHits DescribeHitsForAsset(AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!IsReady) return new RelationSubjectHits([], "关联图还没建立（启动扫描会自动分析四个索引库）。");

        var refKeys = RefKeysForAsset(asset);
        if (refKeys.Count == 0)
            return new RelationSubjectHits([], "此资源没有游戏内容器路径，无法用关联图定位（关联键是容器路径 / 相对路径）。");

        var subjects = FindSubjectsByRefKeys(refKeys);
        if (subjects.Count == 0)
            return new RelationSubjectHits([], "此资源没有关联到任何预设对象（它不属于任何已登记的对象资源）。");

        var keys = new HashSet<string>(refKeys, StringComparer.OrdinalIgnoreCase);
        var hits = new List<RelationSubjectHit>();
        foreach (var subject in subjects)
        {
            var categoryLabel = string.IsNullOrWhiteSpace(subject.CategoryLabel)
                ? RelationCategories.Label(subject.SubjectKind)
                : subject.CategoryLabel;
            foreach (var link in Links(subject.SubjectId).Where(x => keys.Contains(x.RefKey)))
            {
                hits.Add(new RelationSubjectHit(
                    subject.SubjectId,
                    subject.DisplayName,
                    subject.SubjectKind,
                    categoryLabel,
                    link.Kind.ToString(),
                    RelationDisplayRules.KindLabel(link.Kind),
                    link.Display,
                    link.Detail,
                    subject.Subtitle,
                    subject.Character));
            }
        }

        return new RelationSubjectHits(hits,
            $"该资源被 {hits.Count} 条关联引用命中（来自 {subjects.Count} 个对象）。");
    }
}
