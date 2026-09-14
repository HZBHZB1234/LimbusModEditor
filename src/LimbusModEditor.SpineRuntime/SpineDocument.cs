using Spine;

namespace LimbusModEditor.SpineRuntime;

/// <summary>
/// 一个已解析、可渲染的 Spine 骨架文档。
/// <para>
/// 由 <see cref="TryCreate"/> 从“骨架 JSON 文本 + 图集文本 + 图集页字节来源”构造，
/// 内部持有 <see cref="Atlas"/>、<see cref="SkeletonData"/>、<see cref="Skeleton"/> 以及
/// <see cref="AnimationState"/>。解析失败的细节见 <see cref="SpineLoadResult"/>。
/// </para>
/// </summary>
public sealed class SpineDocument : IDisposable
{
    private readonly Atlas _atlas;
    private readonly SkiaTextureLoader _loader;
    private readonly Skeleton _skeleton;
    private readonly AnimationState _state;
    private TrackEntry _currentEntry;

    /// <summary>骨架元数据：动画名列表。</summary>
    public IReadOnlyList<string> AnimationNames { get; }

    /// <summary>骨架元数据：皮肤名列表（含 default）。</summary>
    public IReadOnlyList<string> Skins { get; }

    /// <summary>骨架元数据：骨骼名列表（父在前）。</summary>
    public IReadOnlyList<string> Bones { get; }

    /// <summary>骨架元数据：插槽名列表（即 setup pose 绘制顺序）。</summary>
    public IReadOnlyList<string> Slots { get; }

    /// <summary>当前激活的皮肤名（可能为 null）。</summary>
    public string? CurrentSkinName => _skeleton.Skin?.Name;

    internal Skeleton Skeleton => _skeleton;
    internal AnimationState AnimationState => _state;

    private SpineDocument(Atlas atlas, SkiaTextureLoader loader, SkeletonData data, Skeleton skeleton, AnimationState state)
    {
        _atlas = atlas;
        _loader = loader;
        _skeleton = skeleton;
        _state = state;

        AnimationNames = data.Animations.Items.Take(data.Animations.Count).Select(a => a.Name).ToArray();
        Skins = data.Skins.Items.Take(data.Skins.Count).Select(s => s.Name).ToArray();
        Bones = data.Bones.Items.Take(data.Bones.Count).Select(b => b.name).ToArray();
        Slots = data.Slots.Items.Take(data.Slots.Count).Select(s => s.name).ToArray();
    }

    /// <summary>
    /// 从骨架 JSON 文本与图集文本构造文档。图集页字节通过 <paramref name="textureSource"/> 提供。
    /// 任何解析错误都会被捕获并包装成失败的 <see cref="SpineLoadResult"/>（含中文原因）。
    /// </summary>
    public static SpineLoadResult TryCreate(string skeletonJsonText, string atlasText, ISpineTextureSource textureSource)
    {
        if (string.IsNullOrWhiteSpace(skeletonJsonText))
            return SpineLoadResult.Fail("骨架 JSON 文本为空，无法解析。");
        if (string.IsNullOrWhiteSpace(atlasText))
            return SpineLoadResult.Fail("图集文本为空，无法解析。");
        if (textureSource == null)
            return SpineLoadResult.Fail("未提供图集页纹理来源（ISpineTextureSource）。");

        try
        {
            var loader = new SkiaTextureLoader(textureSource);
            Atlas atlas;
            try
            {
                // imagesDir 传空：TextureLoader 内部只用 page.name 去查纹理源。
                atlas = new Atlas(new StringReader(atlasText), string.Empty, loader);
            }
            catch (Exception ex)
            {
                return SpineLoadResult.Fail("图集文本解析失败：" + ex.Message);
            }

            SkeletonData data;
            try
            {
                var json = new SkeletonJson(atlas);
                data = json.ReadSkeletonData(new StringReader(skeletonJsonText));
            }
            catch (Exception ex)
            {
                return SpineLoadResult.Fail("骨架 JSON 解析失败：" + ex.Message);
            }

            var skeleton = new Skeleton(data);
            // 让默认皮肤成为“当前皮肤”，使 CurrentSkinName 立即可用（Skeleton 构造器不会自动设置）。
            if (data.DefaultSkin != null) skeleton.SetSkin(data.DefaultSkin);
            var stateData = new AnimationStateData(data);
            var state = new AnimationState(stateData);

            var doc = new SpineDocument(atlas, loader, data, skeleton, state);
            return SpineLoadResult.Ok(doc, loader.MissingPages.ToArray());
        }
        catch (Exception ex)
        {
            // 兜底：任何未预期错误都不让异常穿过边界。
            return SpineLoadResult.Fail("构建 Spine 文档时发生意外错误：" + ex.Message);
        }
    }

    /// <summary>指定某个动画在轨道 0 上播放（loop 指定是否循环）。返回该轨道条目以便后续定位时间。</summary>
    public TrackEntry? SetAnimation(string animationName, bool loop = true)
    {
        if (_skeleton.Data.FindAnimation(animationName) == null)
            return null;
        _currentEntry = _state.SetAnimation(0, animationName, loop);
        return _currentEntry;
    }

    /// <summary>清除所有轨道动画，回到 setup pose。</summary>
    public void ClearAnimations()
    {
        _state.ClearTracks();
        _currentEntry = null;
    }

    /// <summary>
    /// 按“时间（秒，绝对轨道时间）”求值当前姿态：重设到 setup pose，把轨道条目定位到该时间，
    /// 应用动画并更新世界变换。供 <see cref="SpineFrameRenderer.RenderFrame"/> 在给定时间出图使用。
    /// 若尚未设置动画，则仅渲染 setup pose。
    /// </summary>
    internal void ApplyTime(double timeSeconds)
    {
        _skeleton.SetToSetupPose();
        if (_currentEntry != null)
            _currentEntry.TrackTime = (float)timeSeconds;
        _state.Apply(_skeleton);
        _skeleton.UpdateWorldTransform();
    }

    /// <summary>
    /// 增量推进（用于播放）：按 deltaSeconds 更新动画状态并应用、更新世界变换。
    /// </summary>
    public void Step(double deltaSeconds)
    {
        _state.Update((float)deltaSeconds);
        _state.Apply(_skeleton);
        _skeleton.UpdateWorldTransform();
    }

    /// <summary>动画时长（秒）；找不到动画时返回 0。</summary>
    public double AnimationDuration(string animationName)
    {
        var anim = _skeleton.Data.FindAnimation(animationName);
        return anim == null ? 0 : anim.Duration;
    }

    /// <summary>当前姿态下所有 region/mesh 附件的 AABB（骨架坐标系）。</summary>
    internal void GetBounds(out float x, out float y, out float width, out float height, ref float[] vertexBuffer)
    {
        float[] vb = vertexBuffer ?? new float[8];
        _skeleton.GetBounds(out x, out y, out width, out height, ref vb);
        vertexBuffer = vb;
    }

    public void Dispose()
    {
        try { _atlas.Dispose(); } catch { /* 忽略卸载阶段的异常 */ }
        _state.ClearTracks();
    }
}
