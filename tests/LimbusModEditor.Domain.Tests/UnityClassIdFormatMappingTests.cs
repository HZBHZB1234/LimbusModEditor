using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// UnityClassId 映射回归：class id 表来自真实缓存全量扫描
/// （Unity 6000.3，1473 bundle / 127 万对象，见 artifacts/type-distribution-full.csv）。
/// 断言：真实出现过的每一个 class id 都不再落到 Unknown，且各归其位。
/// </summary>
public class UnityClassIdFormatMappingTests
{
    /// <summary>全量扫描实测出现的 class id → 期望映射（数值表覆盖）。</summary>
    public static TheoryData<int, AssetType> RealClassIdData() => new()
    {
        { 1, AssetType.GameObject },
        { 4, AssetType.Component },          // Transform
        { 20, AssetType.Component },         // Camera
        { 21, AssetType.Material },          // Material
        { 23, AssetType.Component },         // MeshRenderer
        { 28, AssetType.Texture },           // Texture2D
        { 33, AssetType.Component },         // MeshFilter
        { 43, AssetType.Mesh },              // Mesh
        { 48, AssetType.Shader },            // Shader
        { 49, AssetType.Text },              // TextAsset
        { 50, AssetType.Component },         // Rigidbody2D
        { 54, AssetType.Component },         // Rigidbody
        { 64, AssetType.Component },         // MeshCollider
        { 65, AssetType.Component },         // BoxCollider
        { 74, AssetType.Animation },         // AnimationClip
        { 81, AssetType.Component },         // AudioListener
        { 83, AssetType.Audio },             // AudioClip
        { 84, AssetType.Texture },           // RenderTexture
        { 86, AssetType.Texture },           // CustomRenderTexture
        { 89, AssetType.Texture },           // Cubemap
        { 91, AssetType.Animation },         // AnimatorController
        { 95, AssetType.Component },         // Animator
        { 96, AssetType.Component },         // TrailRenderer
        { 108, AssetType.Component },        // Light
        { 111, AssetType.Component },        // Animation（组件）
        { 114, AssetType.MonoBehaviour },    // MonoBehaviour
        { 115, AssetType.MonoScript },       // MonoScript
        { 120, AssetType.Component },        // LineRenderer
        { 128, AssetType.Font },             // Font
        { 135, AssetType.Component },        // SphereCollider
        { 137, AssetType.Component },        // SkinnedMeshRenderer
        { 142, AssetType.Binary },           // AssetBundle（容器对象）
        { 198, AssetType.Component },        // ParticleSystem
        { 199, AssetType.Component },        // ParticleSystemRenderer
        { 210, AssetType.Component },        // SortingGroup
        { 212, AssetType.Component },        // SpriteRenderer
        { 213, AssetType.Sprite },           // Sprite
        { 215, AssetType.Component },        // ReflectionProbe
        { 221, AssetType.Animation },        // AnimatorOverrideController
        { 222, AssetType.Component },        // CanvasRenderer
        { 223, AssetType.Component },        // Canvas
        { 224, AssetType.Component },        // RectTransform
        { 225, AssetType.Component },        // CanvasGroup
        { 233, AssetType.Component },        // HingeJoint2D
        { 320, AssetType.Component },        // PlayableDirector
        { 328, AssetType.Component },        // VideoPlayer
        { 329, AssetType.Video },            // VideoClip
        { 331, AssetType.Component },        // SpriteMask
        { 687078895, AssetType.SpriteAtlas },      // ref-type SpriteAtlas
        { 850595691, AssetType.GameObject },       // ref-type LightingSettings
        { 1183024399, AssetType.Component },       // ref-type LookAtConstraint
    };

    [Theory]
    [MemberData(nameof(RealClassIdData))]
    public void Real_cache_class_ids_map_away_from_unknown(int typeId, AssetType expected)
    {
        var mapped = UnityClassId.Map(typeId);
        Assert.Equal(expected, mapped);
        Assert.NotEqual(AssetType.Unknown, mapped);
    }

    [Fact]
    public void Unmapped_class_stays_unknown()
        => Assert.Equal(AssetType.Unknown, UnityClassId.Map(999));

    [Fact]
    public void TryMap_reports_unknown_for_unmapped()
    {
        Assert.True(UnityClassId.TryMap(329, out var video));
        Assert.Equal(AssetType.Video, video);
        Assert.False(UnityClassId.TryMap(999, out _));
    }

    [Theory]
    [InlineData(999, "SpriteAtlas", AssetType.SpriteAtlas)]   // ref-type 未来换 id：按类名兜底
    [InlineData(999, "VideoClip", AssetType.Video)]
    [InlineData(999, "material", AssetType.Material)]          // 大小写不敏感
    [InlineData(999, "Transform", AssetType.Component)]
    [InlineData(999, "AnimatorController", AssetType.Animation)]
    [InlineData(999, "RenderTexture", AssetType.Texture)]
    [InlineData(999, "  Shader  ", AssetType.Shader)]          // 容忍空白
    [InlineData(999, "SpriteAtlasV2", AssetType.Unknown)]      // 不认识的类名不猜
    [InlineData(687078895, null, AssetType.SpriteAtlas)]       // 数值表优先
    [InlineData(999, null, AssetType.Unknown)]
    public void Name_fallback_maps_known_class_names(int typeId, string? typeName, AssetType expected)
        => Assert.Equal(expected, UnityClassId.Map(typeId, typeName));

    [Fact]
    public void ByName_rejects_blank_names()
    {
        Assert.Throws<ArgumentException>(() => UnityClassId.ByName(string.Empty));
        Assert.Throws<ArgumentException>(() => UnityClassId.ByName("   "));
    }
}
