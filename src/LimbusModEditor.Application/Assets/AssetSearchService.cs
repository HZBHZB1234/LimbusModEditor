using System.Diagnostics;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Diagnostics;
using LimbusModEditor.Domain.Projects;
using NLog;

namespace LimbusModEditor.Application.Assets;

public sealed record AssetSearchQuery(
    string? Text = null,
    AssetType? Type = null,
    AssetEditState? State = null,
    string? Container = null,
    long? UnityPathId = null,
    int? UnityTypeId = null,
    long? MinSize = null,
    long? MaxSize = null,
    bool? HasReplacement = null,
    AssetSortKind Sort = AssetSortKind.Name,
    bool? HasContainerEntry = null,
    /// <summary>plan-08：是否显示静态数据 bundle（static_s1_0_assets_all_*）里的
    /// 资源。默认 false —— 资源工作台默认视图不出现这些资源，由静态数据工作台
    /// 专门编辑；旧调用不传该参数即保持「默认隐藏」。</summary>
    bool ShowStaticTables = false);

public sealed class AssetSearchService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public IReadOnlyList<AssetRecord> Search(ModProject project, AssetSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(project);
        Log.Debug("资源搜索：对项目快照执行（项目内 {0:N0} 条），文本「{1}」，类型 {2}，容器「{3}」，显示静态数据表={4}。",
            project.Assets.Count, query.Text ?? "-", query.Type?.ToString() ?? "-", query.Container ?? "-", query.ShowStaticTables);
        // 快照后交给重载：调用方可以先在 UI 线程取快照，再把过滤排序放到
        // 后台线程，避免大项目（全缓存扫描 40 万级）冻结界面。
        return Search(project.Assets.ToArray(), query);
    }

    public IReadOnlyList<AssetRecord> Search(IEnumerable<AssetRecord> assets, AssetSearchQuery query)
    {
        if (query.MinSize is < 0) throw new ArgumentException("MinSize 不能为负。", nameof(query));
        if (query.MaxSize is < 0) throw new ArgumentException("MaxSize 不能为负（负值曾经静默关闭上限）。", nameof(query));
        var text = query.Text?.Trim();
        using var scope = Log.Scope("资源搜索");
        Log.Info("搜索开始：文本「{0}」，类型 {1}，状态 {2}，容器「{3}」，UnityPathId {4}，UnityTypeId {5}，大小 [{6}..{7}]，有替换 {8}，有容器条目 {9}，排序 {10}，显示静态数据表 {11}",
            text ?? "-", query.Type?.ToString() ?? "-", query.State?.ToString() ?? "-", query.Container ?? "-",
            query.UnityPathId?.ToString() ?? "-", query.UnityTypeId?.ToString() ?? "-",
            query.MinSize?.ToString() ?? "-", query.MaxSize?.ToString() ?? "-",
            query.HasReplacement?.ToString() ?? "-", query.HasContainerEntry?.ToString() ?? "-",
            query.Sort, query.ShowStaticTables);
        var startTimestamp = Stopwatch.GetTimestamp();
        var matched = assets.Where(asset =>
            (string.IsNullOrEmpty(text) || MatchesText(asset, text)) &&
            (query.Type is null || asset.Type == query.Type) &&
            (query.State is null || asset.EditState == query.State) &&
            (string.IsNullOrEmpty(query.Container) || asset.ContainerPath?.Contains(query.Container, StringComparison.OrdinalIgnoreCase) == true) &&
            (query.UnityPathId is null || asset.UnityPathId == query.UnityPathId) &&
            (query.UnityTypeId is null || asset.UnityTypeId == query.UnityTypeId) &&
            (query.MinSize is null || asset.Size >= query.MinSize) &&
            (query.MaxSize is null || asset.Size <= query.MaxSize) &&
            (query.HasReplacement is null || HasUsableReplacement(asset) == query.HasReplacement) &&
            (query.HasContainerEntry is null || HasContainerEntry(asset) == query.HasContainerEntry) &&
            // plan-08：静态数据 bundle 的资源默认不出现在资源工作台（由静态数据
            // 工作台专门编辑）；勾选「显示静态数据表」后照常出现。
            (query.ShowStaticTables || !IsStaticBundleAsset(asset)))
            .OrderBy(x => x, CreateComparer(query.Sort)).ToArray();
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        Log.Info("搜索完成：命中 {0:N0} 条，耗时 {1:0.#} ms（排序 {2}，显示静态数据表={3}）。",
            matched.Length, elapsed.TotalMilliseconds, query.Sort, query.ShowStaticTables);
        return matched;
    }

    /// <summary>是否为静态数据 bundle（static_s1_0_assets_all_*）内的资源。
    ///
    /// <para><b>两道判据</b>：① 扫描时按 catalog 写入的元数据标记；
    /// ② 兜底看 bundle 文件名 / 内层键（<see cref="StaticBundleLocator.LooksLikeStaticBundle"/>）。
    /// 只用 ① 是不够的 —— 标记是「catalog 可用且内层键命中」的产物，catalog 缺失 /
    /// 版本不符 / 旧索引缺列时就为假，于是资源工作台又把 static-data 的表全列出来。
    /// bundle 名是不依赖 catalog 的权威事实，因此兜底不会误判。</para></summary>
    private static bool IsStaticBundleAsset(AssetRecord asset)
    {
        var hasMetadata = asset.Metadata.TryGetValue(UnityCacheScanService.StaticBundleMetadataKey, out var flag);
        var metadataHit = hasMetadata && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
        // 逐资源判定（真实数据 127 万条）：只有 Trace 打开时才留痕。
        if (Log.IsTraceEnabled)
            Log.Trace("静态判定（元数据标记）：键 {0} 存在={1}，值「{2}」→ {3}；输入 bundle「{4}」，SourcePath「{5}」",
                UnityCacheScanService.StaticBundleMetadataKey, hasMetadata, flag ?? "-",
                metadataHit ? "命中" : "未命中", asset.Bundle ?? "-", asset.SourcePath ?? "-");
        if (metadataHit) return true;
        var innerKey = asset.Metadata.GetValueOrDefault("cacheInner");
        var byBundleName = StaticBundleLocator.LooksLikeStaticBundle(asset.Bundle);
        var byInnerKey = !byBundleName && StaticBundleLocator.LooksLikeStaticBundle(innerKey);
        // 兜底生效（元数据标记未命中）意味着扫描时的 catalog 判定缺失：这条必须留痕。
        if (byBundleName || byInnerKey)
            Log.Warn("静态判定：元数据标记未命中，由 {0} 兜底判定为静态数据（bundle「{1}」，cacheInner「{2}」），资源「{3}」——catalog 可能缺失/版本不符/旧索引缺列。",
                byBundleName ? "bundle 名" : "内层键", asset.Bundle ?? "-", innerKey ?? "-", AssetDisplay.DisplayPath(asset));
        // 第三道判据（不依赖 catalog / bundle 名）：资源自身的游戏内容器路径。
        // 缓存目录名只是内层内容哈希，游戏更新换键后旧静态 bundle 会以裸哈希目录
        // 留在缓存里，前两道判据同时失效（真实数据：62d6e466… 旧版本残留）。
        var containerEntry = AssetDisplay.ContainerEntryPath(asset);
        var byContainerPath = StaticBundleLocator.LooksLikeStaticTablePath(containerEntry);
        if (byContainerPath && !byBundleName && !byInnerKey)
            Log.Warn("静态判定：元数据与 bundle 名均未命中，由资源路径兜底判定为静态数据（容器路径「{0}」，bundle「{1}」）——缓存里存在旧版本静态 bundle。",
                containerEntry, asset.Bundle ?? "-");
        else if (!byBundleName && !byInnerKey && !byContainerPath && Log.IsTraceEnabled)
            Log.Trace("静态判定结论：非静态（bundle 名「{0}」、内层键「{1}」、容器路径「{2}」均不匹配），资源「{3}」。",
                asset.Bundle ?? "-", innerKey ?? "-", containerEntry, AssetDisplay.DisplayPath(asset));
        return byBundleName || byInnerKey || byContainerPath;
    }

    /// <summary>对象是否在 m_Container 表里有游戏内资源路径（文件管理器视图
    /// 的「看得见的文件」；容器外的支撑对象默认隐藏以减少技术噪音）。
    /// 导入的旧式资源不算支撑对象（LogicalPath 就是它的名字），始终视为可见。</summary>
    private static bool HasContainerEntry(AssetRecord asset)
        => !AssetDisplay.IsCacheReference(asset) || !string.IsNullOrWhiteSpace(AssetDisplay.ContainerEntryPath(asset));

    /// <summary>文本匹配覆盖：显示路径（容器路径 + 友好名）、LogicalPath、
    /// 源文件路径 —— 用户按游戏内资源名搜索时不需要知道缓存键。</summary>
    private static bool MatchesText(AssetRecord asset, string text)
        => AssetDisplay.DisplayPath(asset).Contains(text, StringComparison.OrdinalIgnoreCase) ||
           asset.LogicalPath.Contains(text, StringComparison.OrdinalIgnoreCase) ||
           asset.SourcePath?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    private static IComparer<AssetRecord> CreateComparer(AssetSortKind sort) => sort switch
    {
        AssetSortKind.SizeDescending => Comparer<AssetRecord>.Create((a, b) =>
        {
            var bySize = b.Size.CompareTo(a.Size);
            return bySize != 0 ? bySize : CompareByDisplayPath(a, b);
        }),
        AssetSortKind.SizeAscending => Comparer<AssetRecord>.Create((a, b) =>
        {
            var bySize = a.Size.CompareTo(b.Size);
            return bySize != 0 ? bySize : CompareByDisplayPath(a, b);
        }),
        AssetSortKind.Type => Comparer<AssetRecord>.Create((a, b) =>
        {
            var byType = string.Compare(AssetDisplay.TypeLabel(a.Type), AssetDisplay.TypeLabel(b.Type), StringComparison.CurrentCulture);
            return byType != 0 ? byType : CompareByDisplayPath(a, b);
        }),
        AssetSortKind.ModifiedFirst => Comparer<AssetRecord>.Create((a, b) =>
        {
            var modifiedA = a.EditState is not AssetEditState.Unchanged ? 0 : 1;
            var modifiedB = b.EditState is not AssetEditState.Unchanged ? 0 : 1;
            var byModified = modifiedA.CompareTo(modifiedB);
            return byModified != 0 ? byModified : CompareByDisplayPath(a, b);
        }),
        _ => Comparer<AssetRecord>.Create(CompareByDisplayPath),
    };

    private static int CompareByDisplayPath(AssetRecord a, AssetRecord b)
    {
        var pathA = AssetDisplay.SplitTreePath(AssetDisplay.DisplayPath(a));
        var pathB = AssetDisplay.SplitTreePath(AssetDisplay.DisplayPath(b));
        for (var i = 0; i < Math.Min(pathA.Length, pathB.Length); i++)
        {
            var bySegment = AssetDisplay.CompareNames(pathA[i], pathB[i]);
            if (bySegment != 0) return bySegment;
        }
        var byDepth = pathA.Length.CompareTo(pathB.Length);
        return byDepth != 0 ? byDepth : string.Compare(a.LogicalPath, b.LogicalPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An asset counts as "replaced" only when its registered
    /// replacement file still exists; a vanished replacement cannot be
    /// materialized by a build and must not hide behind the filter.</summary>
    private static bool HasUsableReplacement(AssetRecord asset)
        => asset.Metadata.TryGetValue("replacementPath", out var path) && File.Exists(path);
}
