using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

/// <summary>回归：资源工作台列表与目录树同时在真实缓存（1275623 个资源）上空白。
///
/// 根因不在搜索服务，而在资源工作台筛选行的「（全部类型）」/「（全部状态）」
/// 下拉项：它们承载的是枚举的 0 值 <see cref="AssetType.Unknown"/> /
/// <see cref="AssetEditState.Unchanged"/>，页面直接把它当筛选值下发，于是查询
/// 变成「只要未知类型 + 只要未修改」——全部资源（含容器内资源）被过滤光，
/// 列表与目录树一起空白。
///
/// 约定（本文件是它的守门测试）：**枚举的 0 值就是筛选行的「全部」哨兵**，
/// 下拉框选中索引 0 时必须映射为 null（该维度不过滤），绝不下发 0 值本身。
/// 页面把哨兵约定记为「首项 = 枚举 0 值」注释；这里逐条钉住它：新增枚举值不许
/// 插到 0 值前面、标签不许重复（重复会让「全部类型」与真实类型撞名，用户选中
/// 后仍然空列表）。</summary>
public class AssetFilterComboSentinelTests
{
    /// <summary>把枚举展开成筛选行下拉项：与资源工作台
    /// <c>AssetsWorkbenchPage.RefreshAssetList</c> 的构建方式一致
    /// （首项 Label 用「（全部…）」标注）。标签规则必须与页面保持同步。</summary>
    private static IReadOnlyList<(TEnum? Value, string Label)> ComboItems<TEnum>(
        Func<TEnum, string> label, Func<TEnum, bool> isAll, string allLabel) where TEnum : struct, Enum
        => Enum.GetValues<TEnum>().Select(v => ((TEnum?)v, isAll(v) ? allLabel : label(v))).ToList();

    /// <summary>把下拉项映射成查询值：镜像页面
    /// <c>AssetsWorkbenchPage.SelectedFilterValue</c> 的约定。</summary>
    private static TEnum? SelectedValue<TEnum>(IReadOnlyList<(TEnum? Value, string Label)> items, int selectedIndex)
        where TEnum : struct, Enum
        => selectedIndex is > 0 && selectedIndex < items.Count ? items[selectedIndex].Value : null;

    private static IReadOnlyList<(AssetType? Value, string Label)> TypeItems()
        => ComboItems<AssetType>(AssetDisplay.TypeLabel, t => t == AssetType.Unknown, "（全部类型）");

    private static IReadOnlyList<(AssetEditState? Value, string Label)> StateItems()
        => ComboItems<AssetEditState>(AssetDisplay.StateLabel, s => s == AssetEditState.Unchanged, "（全部状态）");

    [Fact]
    public void Type_combo_first_item_is_the_unknown_sentinel()
    {
        var items = TypeItems();
        Assert.Equal(AssetType.Unknown, items[0].Value);
        Assert.Equal("（全部类型）", items[0].Label);
        Assert.Equal(Enum.GetValues<AssetType>().Length, items.Count);
        // 后加的枚举值（Component/Material/Shader/Video/SpriteAtlas）不许插到 0 值
        // 之前——那会让「（全部类型）」不再是首项、哨兵约定失效。
        Assert.Equal(0, (int)AssetType.Unknown);
        foreach (var type in Enum.GetValues<AssetType>().Where(t => t != AssetType.Unknown))
            Assert.Contains(items, x => x.Value == type);
    }

    [Fact]
    public void State_combo_first_item_is_the_unchanged_sentinel_and_labels_stay_unique()
    {
        var items = StateItems();
        Assert.Equal(AssetEditState.Unchanged, items[0].Value);
        Assert.Equal("（全部状态）", items[0].Label);
        Assert.Equal(Enum.GetValues<AssetEditState>().Length, items.Count);
        var labels = items.Skip(1).Select(x => x.Label).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    /// <summary>真实缓存里的资源：容器内资源（有 m_Container 路径）、容器外支撑
    /// 对象、以及一条 Unknown + Unchanged 的资源（0 值筛选一旦下发就会把前面
    /// 两条正常资源全部滤掉——正是本次空白事故的形态）。</summary>
    private static ModProject SampleProject()
    {
        var project = new ModProject();
        project.Assets.Add(new AssetRecord
        {
            LogicalPath = "outer/inner/CAB-a/1.28",
            Type = AssetType.Texture,
            Size = 2048,
            UnityPathId = 1,
            UnityTypeId = 28,
            Metadata = { ["reference"] = "true", ["containerEntry"] = "Assets/Sprites/icon.png" },
        });
        project.Assets.Add(new AssetRecord
        {
            LogicalPath = "outer/inner/CAB-a/2.1",
            Type = AssetType.GameObject,
            Size = 512,
            UnityPathId = 2,
            UnityTypeId = 1,
            Metadata = { ["reference"] = "true" },
        });
        project.Assets.Add(new AssetRecord
        {
            LogicalPath = "outer/inner/CAB-a/3.0",
            Type = AssetType.Unknown,
            Size = 0,
            UnityPathId = 3,
            UnityTypeId = 0,
            Metadata = { ["reference"] = "true" },
        });
        return project;
    }

    [Fact]
    public void Default_combo_state_keeps_container_assets_visible()
    {
        var project = SampleProject();
        var service = new AssetSearchService();
        var type = SelectedValue(TypeItems(), selectedIndex: 0);
        var state = SelectedValue(StateItems(), selectedIndex: 0);

        // 页面默认：两个下拉都停在「（全部…）」→ 该维度不过滤。
        Assert.Null(type);
        Assert.Null(state);
        var visible = service.Search(project, new AssetSearchQuery(Type: type, State: state, HasContainerEntry: true));
        Assert.Single(visible); // 容器内那条
        Assert.Equal("Assets/Sprites/icon.png", AssetDisplay.DisplayPath(visible[0]));
        Assert.Single(AssetTreeBuilder.BuildRoots(visible)); // 目录树根层同样不为空
    }

    [Fact]
    public void Passing_the_sentinel_value_itself_empties_the_list_and_tree()
    {
        var project = SampleProject();
        var service = new AssetSearchService();
        // 事故形态：把「全部」项承载的 0 值当筛选值下发。
        var broken = service.Search(project, new AssetSearchQuery(
            Type: AssetType.Unknown, State: AssetEditState.Unchanged, HasContainerEntry: true));
        Assert.Empty(broken);
        Assert.Empty(AssetTreeBuilder.BuildRoots(broken));

        // 任选一个真实项仍然照常过滤（哨兵只影响索引 0）。
        var type = SelectedValue(TypeItems(), selectedIndex: (int)AssetType.Texture);
        Assert.Equal(AssetType.Texture, type);
        Assert.Single(service.Search(project, new AssetSearchQuery(Type: type)));
    }
}
