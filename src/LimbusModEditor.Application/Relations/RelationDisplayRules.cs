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
    /// <b>不猜格式</b>：这里只做路径归类，能否解析由真身管线按真实文件判定。</summary>
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

    /// <summary>预览形态标签（<see cref="RelationLink.MediaKind"/>）——UI 按它选预览器。</summary>
    public static string MediaKindOf(RelationKind kind) => kind switch
    {
        RelationKind.Text => "text",
        RelationKind.StaticData => "static",
        RelationKind.Audio => "audio",
        RelationKind.Image => "image",
        RelationKind.Video => "video",
        RelationKind.Spine => "spine",
        RelationKind.Animation => "animation",
        RelationKind.Prefab => "prefab",
        RelationKind.Mesh => "mesh",
        _ => "other",
    };

    /// <summary>「立绘」候选的优先级（越小越优先）。卡片封面按这个顺序挑第一张
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

    /// <summary>立绘候选优先级（越小越优先）。**优先 <c>Texture</c> 记录**：
    /// 真实数据里同一条 <c>container_entry</c> 既有 Sprite 行也有 Texture 行，
    /// 而解码只能走 Texture 的 pathId（选到 Sprite 行会必然解码失败、封面留白）。</summary>
    public static int CoverCandidateRank(string? containerEntry, AssetType type)
    {
        var rank = PortraitRank(containerEntry, type);
        if (rank < 0) return -1;
        // Texture 记录比同路径的 Sprite 记录高 1 档（乘 2 后加偏移，保证不越过相邻的 PortraitRank 档）。
        return type == AssetType.Texture ? rank * 2 : rank * 2 + 1;
    }
}
