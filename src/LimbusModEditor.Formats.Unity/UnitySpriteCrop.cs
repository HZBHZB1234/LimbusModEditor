using LimbusModEditor.Editing.Images;

namespace LimbusModEditor.Formats.Unity;

/// <summary>裁剪区域（图像坐标系：原点左上，单位像素）。</summary>
public readonly record struct UnityCropRect(int X, int Y, int Width, int Height);

/// <summary>
/// Sprite 裁剪坐标换算（plan-01 第 1 步）：Unity 的 <c>m_Rect</c> /
/// <c>m_RD.textureRect</c> 以纹理<b>左下角</b>为原点，而图像解码结果是左上原点。
/// 真实样本实测（banner_MirrorDungeon7_en）：<c>m_Rect</c> 697×314 是 Sprite
/// 逻辑尺寸，<c>m_RD.textureRect</c> 621.85×181.85 @(39.08,71.08) 才是图集内的
/// 实际像素区域，因此优先用 textureRect、缺失时回退 m_Rect。
/// 越界仅按「浮点四舍五入」容差钳制（不猜内容）；完全不相交则 fail fast。
/// </summary>
public static class UnitySpriteCrop
{
    /// <summary>把 Unity 左下原点的 Sprite 区域换算为左上原点的裁剪区域。</summary>
    public static UnityCropRect Resolve(UnitySpriteRect rect, int textureWidth, int textureHeight)
    {
        if (textureWidth <= 0 || textureHeight <= 0)
            throw new InvalidDataException($"纹理尺寸非法（{textureWidth}×{textureHeight}），无法裁剪 Sprite。");
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new InvalidDataException($"Sprite 区域尺寸非法（{rect.Width}×{rect.Height}），无法裁剪。");

        var x = (int)MathF.Round(rect.X);
        var width = (int)MathF.Round(rect.Width);
        var height = (int)MathF.Round(rect.Height);
        var y = textureHeight - (int)MathF.Round(rect.Y) - height; // 左下原点 → 左上原点
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"Sprite 区域四舍五入后尺寸为 {width}×{height}，无法裁剪。");

        // 先做交集判断：完全在纹理之外就是数据有问题，不猜。
        if (x >= textureWidth || y >= textureHeight || x + width <= 0 || y + height <= 0)
            throw new InvalidDataException(
                $"Sprite 区域 {x},{y} {width}×{height} 与纹理 {textureWidth}×{textureHeight} 完全不相交（原点约定：Unity 左下）。");

        // 浮点舍入可能让区域边缘超出 1~2 像素：按纹理边界钳制（确定性，不是猜测）。
        var clampedX = Math.Clamp(x, 0, textureWidth - 1);
        var clampedY = Math.Clamp(y, 0, textureHeight - 1);
        var clampedWidth = Math.Min(width, textureWidth - clampedX);
        var clampedHeight = Math.Min(height, textureHeight - clampedY);
        return new UnityCropRect(clampedX, clampedY, clampedWidth, clampedHeight);
    }
}
