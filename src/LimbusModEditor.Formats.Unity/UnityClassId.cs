using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.Formats.Unity;

/// <summary>
/// Maps Unity serialized class IDs to the editor's neutral asset types.
/// The table follows Unity's canonical class-ID reference (verified against
/// AssetsTools.NET's AssetClassID enum): 115 is MonoScript, 213 is Sprite and
/// there is no dedicated ScriptableObject class ID.
/// <para>覆盖范围来自真实缓存扫描（Unity 6000.3，1473 bundle / 127 万对象）：
/// 除既有 10 类外，补全了实际出现的全部类 —— 场景组件（Transform/Renderer/
/// Collider/Light/Camera/ParticleSystem/Animator/Canvas 等）归入
/// <see cref="AssetType.Component"/>，Material/Shader/VideoClip/SpriteAtlas 各有
/// 专属类型。其余未覆盖的类才落到 <see cref="AssetType.Unknown"/>。</para>
/// <para>Unity 2022+ 的 ref-type 伪 class id（如 SpriteAtlas = 687078895、
/// LightingSettings = 850595691、LookAtConstraint = 1183024399）由类型表给出，
/// 同一游戏版本内稳定，直接进数值表；另提供按类型表类名的兜底映射
/// （<see cref="Map(int, string?)"/>），防未来版本换 id。</para>
/// </summary>
public static class UnityClassId
{
    public const int GameObject = 1;
    public const int Transform = 4;
    public const int Camera = 20;
    public const int Material = 21;
    public const int MeshRenderer = 23;
    public const int Texture2D = 28;
    public const int MeshFilter = 33;
    public const int Mesh = 43;
    public const int Shader = 48;
    /// <summary>Unity TextAsset（m_Name + m_Script）。真实缓存里存在（如 spine
    /// 骨架 JSON），此前未映射导致文本预览无法分派。</summary>
    public const int TextAsset = 49;
    public const int Rigidbody2D = 50;
    public const int Rigidbody = 54;
    public const int MeshCollider = 64;
    public const int BoxCollider = 65;
    public const int AnimationClip = 74;
    public const int AudioListener = 81;
    public const int AudioClip = 83;
    public const int RenderTexture = 84;
    public const int CustomRenderTexture = 86;
    public const int Cubemap = 89;
    public const int AnimatorController = 91;
    public const int Animator = 95;
    public const int TrailRenderer = 96;
    public const int Light = 108;
    /// <summary>Legacy Animation 组件（区别于 AnimationClip 74）。</summary>
    public const int Animation = 111;
    public const int MonoBehaviour = 114;
    public const int MonoScript = 115;
    public const int LineRenderer = 120;
    public const int Font = 128;
    public const int SphereCollider = 135;
    public const int SkinnedMeshRenderer = 137;
    public const int AssetBundle = 142;
    public const int ParticleSystem = 198;
    public const int ParticleSystemRenderer = 199;
    public const int SortingGroup = 210;
    public const int SpriteRenderer = 212;
    public const int Sprite = 213;
    public const int ReflectionProbe = 215;
    public const int AnimatorOverrideController = 221;
    public const int CanvasRenderer = 222;
    public const int Canvas = 223;
    public const int RectTransform = 224;
    public const int CanvasGroup = 225;
    public const int HingeJoint2D = 233;
    public const int PlayableDirector = 320;
    public const int VideoPlayer = 328;
    public const int VideoClip = 329;
    public const int SpriteMask = 331;
    /// <summary>Unity 2022+ ref-type 伪 class id：SpriteAtlas（类型表类名 SpriteAtlas）。</summary>
    public const int SpriteAtlasRefType = 687078895;
    /// <summary>Unity 2022+ ref-type 伪 class id：LightingSettings（类型表类名 LightingSettings）。</summary>
    public const int LightingSettingsRefType = 850595691;
    /// <summary>Unity 2022+ ref-type 伪 class id：LookAtConstraint（类型表类名 LookAtConstraint）。</summary>
    public const int LookAtConstraintRefType = 1183024399;

    public static bool TryMap(int typeId, out AssetType type)
    {
        type = Map(typeId);
        return type != AssetType.Unknown;
    }

    /// <summary>按 class id 映射（数值表全覆盖真实缓存出现过的类）。</summary>
    public static AssetType Map(int typeId) => typeId switch
    {
        GameObject => AssetType.GameObject,
        Texture2D or RenderTexture or CustomRenderTexture or Cubemap => AssetType.Texture,
        TextAsset => AssetType.Text,
        Mesh => AssetType.Mesh,
        AnimationClip or AnimatorController or AnimatorOverrideController => AssetType.Animation,
        AudioClip => AssetType.Audio,
        MonoBehaviour => AssetType.MonoBehaviour,
        MonoScript => AssetType.MonoScript,
        Font => AssetType.Font,
        AssetBundle => AssetType.Binary,
        Sprite => AssetType.Sprite,
        Material => AssetType.Material,
        Shader => AssetType.Shader,
        VideoClip => AssetType.Video,
        SpriteAtlasRefType => AssetType.SpriteAtlas,
        Transform or Camera or MeshRenderer or MeshFilter or Rigidbody2D or Rigidbody or
        MeshCollider or BoxCollider or AudioListener or Animator or TrailRenderer or Light or
        Animation or LineRenderer or SphereCollider or SkinnedMeshRenderer or ParticleSystem or
        ParticleSystemRenderer or SortingGroup or SpriteRenderer or ReflectionProbe or
        CanvasRenderer or Canvas or RectTransform or CanvasGroup or HingeJoint2D or
        PlayableDirector or VideoPlayer or SpriteMask or LookAtConstraintRefType => AssetType.Component,
        LightingSettingsRefType => AssetType.GameObject,
        _ => AssetType.Unknown
    };

    /// <summary>按 class id 优先、类型表类名兜底的映射。真实类名来自
    /// SerializedFile 类型表（<c>TypeTreeType.TypeReference.ClassName</c>），
    /// 兜底覆盖未来版本可能变化的 ref-type id。</summary>
    public static AssetType Map(int typeId, string? typeName)
    {
        var mapped = Map(typeId);
        if (mapped != AssetType.Unknown || string.IsNullOrWhiteSpace(typeName)) return mapped;
        return ByName(typeName);
    }

    /// <summary>按类型表类名映射（大小写不敏感）。只认已支持的类名，
    /// 其余返回 Unknown（不猜）。</summary>
    public static AssetType ByName(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return typeName.Trim().ToLowerInvariant() switch
        {
            "gameobject" => AssetType.GameObject,
            "texture2d" or "rendertexture" or "customrendertexture" or "cubemap" => AssetType.Texture,
            "textasset" => AssetType.Text,
            "mesh" => AssetType.Mesh,
            "animationclip" or "animatorcontroller" or "animatoroverridecontroller" => AssetType.Animation,
            "audioclip" => AssetType.Audio,
            "monobehaviour" => AssetType.MonoBehaviour,
            "monoscript" => AssetType.MonoScript,
            "font" => AssetType.Font,
            "assetbundle" => AssetType.Binary,
            "sprite" => AssetType.Sprite,
            "material" => AssetType.Material,
            "shader" => AssetType.Shader,
            "videoclip" => AssetType.Video,
            "spriteatlas" => AssetType.SpriteAtlas,
            "transform" or "recttransform" or "camera" or "meshrenderer" or "meshfilter" or
            "rigidbody2d" or "rigidbody" or "meshcollider" or "boxcollider" or "spherecollider" or
            "audiolistener" or "animator" or "trailrenderer" or "light" or "animation" or
            "linerenderer" or "skinnedmeshrenderer" or "particlesystem" or "particlesystemrenderer" or
            "sortinggroup" or "spriterenderer" or "reflectionprobe" or "canvasrenderer" or
            "canvas" or "canvasgroup" or "hingejoint2d" or "playabledirector" or "videoplayer" or
            "spritemask" or "lookatconstraint" => AssetType.Component,
            "lightingsettings" => AssetType.GameObject,
            _ => AssetType.Unknown
        };
    }
}
