using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Application.Relations;

/// <summary>关联分析里与「游戏内路径命名约定」相关的纯规则（无 IO，可直接单测）。</summary>
public static class RelationDisplayRules
{
    /// <summary>Spine 骨骼资源的路径判据（真实数据里 Spine 资源分散在六处）：
    /// ① <c>Prefab/SpineIllustPrefab/&lt;id&gt;_gacksung.prefab</c>（人格立绘立牌）；
    /// ② <c>Story/StandingModel/*.psb</c>（立绘 PSB，内含 Skeleton 与图集切片）；
    /// ③ <c>Story/CG/.../*_SkeletonData.asset</c>（Spine SkeletonDataAsset）；
    /// ④ <c>Story/Spine/**</c>（剧情 Spine 专用目录）；
    /// ⑤ <c>Story/CG/.../StorySpine_*</c>（CG Spine 目录，如 StorySpine_Sinclair）；
    /// ⑥ <c>Prefab/SD/**.prefab</c>（<b>战斗用</b> Spine：敌人 / 异想体 / 人格 / E.G.O 的
    /// <c>*Appearance.prefab</c>，如 <c>SD/Personality/10103_Yisang_SwordGroupAppearance.prefab</c>）。
    ///
    /// <para><b>第 ⑥ 条是本轮补上的</b>：这批 prefab 的<b>路径名里没有任何 Spine 字样</b>，
    /// 旧判据因此把它们全部挡在取数之前（实测抽样 24 个「未绑定」候选，0 个进得了引用链）。
    /// 但它们的引用链里<b>确实挂着真骨架</b> —— 用生产 resolver 直连实测：
    /// <c>SD/Personality</c> 12 个里 3 个、<c>SD/EGO</c> 16 个里 9 个能取到
    /// 「骨架 JSON + 图集 + 纹理页」（骨架名如 <c>01_yisang</c> / <c>Yisang_blade_idle</c>）。
    /// 不进引用链就永远看不到它们，所以判据必须覆盖 SD 目录。</para>
    ///
    /// <para><b>仍然不猜内容</b>：这里只放宽「<b>哪些路径值得试一试</b>」，
    /// 到底是不是 Spine 一律由 <c>SpinePrefabChainResolver</c> <b>按正文</b>判定
    /// （骨架必须同时含 <c>"skeleton"</c>+<c>"bones"</c> 且能 JSON 解析，图集必须是
    /// 页名 + <c>size:</c> 指令头）。路径放行 ≠ 认定它是 Spine；判定失败就如实报
    /// 「引用链里没有骨架与图集」，不造假数据。</para>
    ///
    /// 单一判据来源：SpineData 网关与关联图分类均委托此方法，不另立实现。</summary>
    public static bool IsSpinePath(string? containerEntry)
    {
        if (string.IsNullOrEmpty(containerEntry)) return false;
        return containerEntry.Contains("/SpineIllustPrefab/", StringComparison.OrdinalIgnoreCase)
            || containerEntry.EndsWith(".psb", StringComparison.OrdinalIgnoreCase)
            || containerEntry.Contains("SkeletonData", StringComparison.OrdinalIgnoreCase)
            || containerEntry.Contains("/Story/Spine/", StringComparison.OrdinalIgnoreCase)
            || containerEntry.Contains("StorySpine", StringComparison.OrdinalIgnoreCase)
            || IsSdAppearancePrefab(containerEntry);
    }

    /// <summary>
    /// 战斗用 Spine 的挂点：<c>Prefab/SD/**.prefab</c>（<c>SD</c> = Spine Data 目录）。
    /// 与第 ① 条一样是「立绘/出场」类 prefab，只是服务于战斗而非维基立绘展示。
    /// </summary>
    public static bool IsSdAppearancePrefab(string? containerEntry)
    {
        if (string.IsNullOrEmpty(containerEntry)) return false;
        if (!containerEntry.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return false;
        return containerEntry.Contains("/Prefab/SD/", StringComparison.OrdinalIgnoreCase)
            || containerEntry.StartsWith("Prefab/SD/", StringComparison.OrdinalIgnoreCase)
            || containerEntry.StartsWith("Assets/Prefab/SD/", StringComparison.OrdinalIgnoreCase)
            || containerEntry.Contains("/Resources_moved/Prefab/SD/", StringComparison.OrdinalIgnoreCase);
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
