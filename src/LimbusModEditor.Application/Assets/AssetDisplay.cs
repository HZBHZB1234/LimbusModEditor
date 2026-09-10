using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Assets;

/// <summary>资源列表的排序策略（UI 下拉框直连）。默认按名称。</summary>
public enum AssetSortKind
{
    /// <summary>按显示名自然排序（数字段按数值比较，icon2 排在 icon10 前）。</summary>
    Name,
    /// <summary>按大小降序（大文件在前）。</summary>
    SizeDescending,
    /// <summary>按大小升序。</summary>
    SizeAscending,
    /// <summary>按类型分组（类型中文名排序），组内按名称。</summary>
    Type,
    /// <summary>已修改的资源排最前，其余按名称。</summary>
    ModifiedFirst,
}

/// <summary>面向用户的资源显示层：把技术性的 LogicalPath
/// （<c>&lt;缓存外层键&gt;/&lt;内层键&gt;/&lt;CAB 名&gt;/&lt;pathId&gt;.&lt;typeId&gt;</c>）
/// 换算成「容器路径 + 友好名称」，让用户像浏览普通文件夹一样看游戏资源，
/// 不再暴露缓存键、Path ID 之类的实现细节。
/// <para>规则（按序判定）：</para>
/// <list type="number">
/// <item>扫描索引的缓存引用资源（metadata <c>reference=true</c> / 存在
/// <c>cacheOuter</c>）：显示路径取 metadata <c>containerEntry</c> ——
/// Unity AssetBundle 主对象 m_Container 表里的游戏内真实资源路径
/// （如 <c>assets/assetbundle/...</c>）；未命中容器表则归入「未命名资源」。</item>
/// <item>导入的旧式资源：显示路径取 LogicalPath 本身；结尾若恰好是
/// <c>pathId.typeId</c> 形态则剥掉该段（它只是内部编号）。</item>
/// </list>
/// 叶子名取显示路径最后一段；取不出可读名（空/纯编号）时用「类型中文 + 编号」
/// 兜底，保证同名资源在列表里仍可互相区分。</summary>
public static class AssetDisplay
{
    private static readonly char[] Separators = ['/'];

    /// <summary>缓存引用形态判定：由扫描服务写入的标记。</summary>
    public static bool IsCacheReference(AssetRecord asset)
        => asset.Metadata.TryGetValue("reference", out var reference) && reference == "true"
           || asset.Metadata.ContainsKey("cacheOuter");

    /// <summary>m_Container 容器条目（游戏内资源路径），未命中返回空串。</summary>
    public static string ContainerEntryPath(AssetRecord asset)
        => asset.Metadata.TryGetValue("containerEntry", out var path) ? path.Trim().TrimEnd('/') : string.Empty;

    private static string ContainerEntry(AssetRecord asset) => ContainerEntryPath(asset);

    /// <summary>文件夹路径（容器化，不含缓存键，不含叶子名）。容器缺失时
    /// 归入「未命名资源」；容器条目本身是根级单段路径时返回空串（叶子直接
    /// 挂根层，见 <see cref="TreePath"/>）。</summary>
    public static string DisplayFolder(AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (IsCacheReference(asset))
        {
            var container = ContainerEntry(asset);
            if (container.Length == 0) return UnnamedFolder;
            return container.Contains('/') ? container[..container.LastIndexOf('/')] : string.Empty;
        }
        // 导入的旧式资源：LogicalPath 即可读路径；剥掉结尾的 pathId.typeId 叶子。
        var path = asset.LogicalPath.Trim().TrimEnd('/');
        if (LooksLikeTechnicalLeaf(path)) path = path[..path.LastIndexOf('/')];
        return path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
    }

    /// <summary>叶子名：m_Container 路径最后一段；读不出名字（空 / 纯编号）时
    /// 用「类型中文 + #编号」兜底（编号仅在无真实名字时出现，用于互相区分）。</summary>
    public static string DisplayName(AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var name = LeafCandidate(asset);
        return LooksLikeTechnicalLeaf(name) || name.Length == 0 ? FallbackName(asset) : name;
    }

    /// <summary>树/搜索共用的完整显示路径 = 文件夹 + 叶子名。
    /// 恰好是「像文件夹一样逐段展开」的那条路径。</summary>
    public static string TreePath(AssetRecord asset)
    {
        var folder = DisplayFolder(asset);
        var name = DisplayName(asset);
        return folder.Length == 0 ? name : $"{folder}/{name}";
    }

    /// <summary>用户友好的资源路径（用于提示/详情），等价 TreePath。</summary>
    public static string DisplayPath(AssetRecord asset) => TreePath(asset);

    private static string LeafCandidate(AssetRecord asset)
    {
        if (IsCacheReference(asset))
        {
            var container = ContainerEntry(asset);
            if (container.Length == 0) return string.Empty;
            return container.Contains('/') ? container[(container.LastIndexOf('/') + 1)..] : container;
        }
        var path = asset.LogicalPath.Trim().TrimEnd('/');
        if (LooksLikeTechnicalLeaf(path)) path = path[..path.LastIndexOf('/')];
        return path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
    }

    /// <summary>「123.45」形态的纯内部编号段（pathId.typeId）。</summary>
    private static bool LooksLikeTechnicalLeaf(string segment)
    {
        if (segment.Length == 0) return false;
        if (segment.Contains('/')) segment = segment[(segment.LastIndexOf('/') + 1)..];
        var seenDot = false;
        foreach (var c in segment)
        {
            if (c == '.')
            {
                if (seenDot) return false;
                seenDot = true;
            }
            else if (!char.IsAsciiDigit(c)) return false;
        }
        return seenDot;
    }

    private static string FallbackName(AssetRecord asset)
    {
        var label = TypeLabel(asset.Type);
        return asset.UnityPathId is { } id ? $"{label} #{id}" : label;
    }

    public const string UnnamedFolder = "未命名资源";

    /// <summary>类型中文标签（UI 列表 / 筛选下拉共用）。</summary>
    public static string TypeLabel(AssetType type) => type switch
    {
        AssetType.Texture => "纹理",
        AssetType.Sprite => "精灵图",
        AssetType.Audio => "音频",
        AssetType.Text => "文本",
        AssetType.Json => "JSON",
        AssetType.MonoBehaviour => "脚本数据",
        AssetType.MonoScript => "脚本",
        AssetType.ScriptableObject => "脚本数据",
        AssetType.Mesh => "网格",
        AssetType.Animation => "动画",
        AssetType.Font => "字体",
        AssetType.Binary => "二进制",
        AssetType.GameObject => "游戏对象",
        AssetType.Component => "组件",
        AssetType.Material => "材质",
        AssetType.Shader => "着色器",
        AssetType.Video => "视频",
        AssetType.SpriteAtlas => "图集",
        _ => "未知类型",
    };

    /// <summary>编辑状态中文标签（UI 列表 / 筛选下拉共用）。</summary>
    public static string StateLabel(AssetEditState state) => state switch
    {
        AssetEditState.Unchanged => "未修改",
        AssetEditState.Modified => "已修改",
        AssetEditState.Added => "新增",
        AssetEditState.Deleted => "已删除",
        AssetEditState.Conflict => "冲突",
        AssetEditState.Invalid => "无效",
        _ => state.ToString(),
    };

    /// <summary>自然名称比较：段内数字按数值比（icon2 &lt; icon10），
    /// 其余按当前文化不区分大小写。</summary>
    public static int CompareNames(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var ia = 0;
        var ib = 0;
        while (ia < a.Length && ib < b.Length)
        {
            var digitA = char.IsAsciiDigit(a[ia]);
            var digitB = char.IsAsciiDigit(b[ib]);
            if (digitA && digitB)
            {
                var startA = ia;
                var startB = ib;
                while (ia < a.Length && char.IsAsciiDigit(a[ia])) ia++;
                while (ib < b.Length && char.IsAsciiDigit(b[ib])) ib++;
                var runA = a[startA..ia].TrimStart('0');
                var runB = b[startB..ib].TrimStart('0');
                var byLength = runA.Length.CompareTo(runB.Length);
                if (byLength != 0) return byLength;
                var byValue = string.CompareOrdinal(runA, runB);
                if (byValue != 0) return byValue;
            }
            else
            {
                var byChar = char.ToUpperInvariant(a[ia]).CompareTo(char.ToUpperInvariant(b[ib]));
                if (byChar != 0) return byChar;
                ia++;
                ib++;
            }
        }
        return (a.Length - ia).CompareTo(b.Length - ib);
    }

    /// <summary><see cref="CompareNames"/> 的比较器实例（树 / 列表排序共用）。</summary>
    public static IComparer<string> ComparerInstance { get; } = Comparer<string>.Create(CompareNames);

    /// <summary>显示路径分段（TreePath 的目录段）。</summary>
    public static string[] SplitTreePath(string path)
        => string.IsNullOrEmpty(path) ? [] : path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
}
