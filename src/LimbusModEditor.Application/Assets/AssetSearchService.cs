using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

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
    bool? HasContainerEntry = null);

public sealed class AssetSearchService
{
    public IReadOnlyList<AssetRecord> Search(ModProject project, AssetSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(project);
        // 快照后交给重载：调用方可以先在 UI 线程取快照，再把过滤排序放到
        // 后台线程，避免大项目（全缓存扫描 40 万级）冻结界面。
        return Search(project.Assets.ToArray(), query);
    }

    public IReadOnlyList<AssetRecord> Search(IEnumerable<AssetRecord> assets, AssetSearchQuery query)
    {
        if (query.MinSize is < 0) throw new ArgumentException("MinSize 不能为负。", nameof(query));
        if (query.MaxSize is < 0) throw new ArgumentException("MaxSize 不能为负（负值曾经静默关闭上限）。", nameof(query));
        var text = query.Text?.Trim();
        return assets.Where(asset =>
            (string.IsNullOrEmpty(text) || MatchesText(asset, text)) &&
            (query.Type is null || asset.Type == query.Type) &&
            (query.State is null || asset.EditState == query.State) &&
            (string.IsNullOrEmpty(query.Container) || asset.ContainerPath?.Contains(query.Container, StringComparison.OrdinalIgnoreCase) == true) &&
            (query.UnityPathId is null || asset.UnityPathId == query.UnityPathId) &&
            (query.UnityTypeId is null || asset.UnityTypeId == query.UnityTypeId) &&
            (query.MinSize is null || asset.Size >= query.MinSize) &&
            (query.MaxSize is null || asset.Size <= query.MaxSize) &&
            (query.HasReplacement is null || HasUsableReplacement(asset) == query.HasReplacement) &&
            (query.HasContainerEntry is null || HasContainerEntry(asset) == query.HasContainerEntry))
            .OrderBy(x => x, CreateComparer(query.Sort)).ToArray();
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
