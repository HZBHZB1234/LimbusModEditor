using SkiaSharp;
using LimbusModEditor.SpineRuntime;

namespace LimbusModEditor.SpineRuntime.Tests;

/// <summary>
/// 测试用的 <see cref="ISpineTextureSource"/>：把页名映射到现画的 PNG 字节。
/// 可选地让某些页“缺失”（返回 null），用于验证缺页时的中文失败路径。
/// </summary>
internal sealed class TestTextureSource : ISpineTextureSource
{
    private readonly Dictionary<string, byte[]> _pages;
    private readonly HashSet<string> _missing;

    public TestTextureSource(Dictionary<string, byte[]> pages, HashSet<string> missing = null)
    {
        _pages = pages;
        _missing = missing ?? new HashSet<string>();
    }

    public byte[] GetPageBytes(string pageName)
    {
        if (_missing.Contains(pageName)) return null;
        return _pages.TryGetValue(pageName, out var bytes) ? bytes : null;
    }

    /// <summary>用纯色生成一个不透明的 PNG（作为图集页）。</summary>
    public static byte[] MakeSolidPng(int w, int h, SKColor color)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        bmp.Erase(color);
        using var img = SKImage.FromBitmap(bmp);
        using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
