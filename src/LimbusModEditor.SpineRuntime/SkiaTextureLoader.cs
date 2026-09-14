using SkiaSharp;
using Spine;

namespace LimbusModEditor.SpineRuntime;

/// <summary>
/// 把图集页解码为 Skia 位图并挂到 <see cref="AtlasPage.rendererObject"/> 上的 Spine 纹理加载器。
/// <para>
/// 这里统一把所有页解码成<b>预乘 alpha（premultiplied）</b>的 <see cref="SKImage"/>：
/// <list type="bullet">
///   <item><description>atlas 标 <c>pma:true</c> 时，PNG 的 RGB 在打包阶段已被乘以 alpha，仅重新标记为 Premul；</description></item>
///   <item><description>否则按直 alpha 解码后，通过绘制到预乘画布转换成 Premul。</description></item>
/// </list>
/// 渲染时画布与贴图都工作在 Premul 空间，配合 SrcOver/Plus 混合即可得到正确的合成结果。
/// </para>
/// </summary>
internal sealed class SkiaTextureLoader : TextureLoader
{
    private readonly ISpineTextureSource _source;

    /// <summary>本次加载中缺失（拿不到字节 / 解码失败）的图集页名，供上层给出中文提示。</summary>
    public readonly List<string> MissingPages = new();

    public SkiaTextureLoader(ISpineTextureSource source) => _source = source;

    public void Load(AtlasPage page, string path)
    {
        string name = page.name;
        byte[]? bytes = _source.GetPageBytes(name);
        if (bytes == null || bytes.Length == 0)
        {
            MissingPages.Add(name);
            page.rendererObject = null;
            return;
        }

        try
        {
            using var original = SKBitmap.Decode(bytes);
            if (original == null)
            {
                // 解码失败（非 PNG / 损坏）——记为缺失页，不让异常穿过边界。
                MissingPages.Add(name);
                page.rendererObject = null;
                return;
            }

            page.rendererObject = ToPremultipliedImage(original, page.pma);
            if (page.width == 0) page.width = original.Width;
            if (page.height == 0) page.height = original.Height;
        }
        catch (Exception ex)
        {
            // 任何意外错误都收敛成“缺失页”，绝不在加载路径上抛异常。
            MissingPages.Add(name + "：" + ex.Message);
            page.rendererObject = null;
        }
    }

    public void Unload(object texture)
    {
        if (texture is SKImage img) img.Dispose();
    }

    private static SKImage ToPremultipliedImage(SKBitmap original, bool pma)
    {
        // 统一产出 Premul 的 SKImage：
        //  * pma:true  —— PNG 的 RGB 在打包阶段已被乘以 alpha，仅需把原始字节原样拷入
        //                Premul 标记的位图（不做任何换算，避免二次预乘）。
        //  * 否则      —— 直 alpha，逐个像素把 rgb 乘以 a 预乘。
        var info = new SKImageInfo(original.Width, original.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var premul = new SKBitmap(info);
        int pixels = original.Width * original.Height;

        unsafe
        {
            byte* src = (byte*)original.GetPixels().ToPointer();
            byte* dst = (byte*)premul.GetPixels().ToPointer();
            if (pma)
            {
                // 原始字节即预乘结果，直接按像素拷贝（不重算）。
                Buffer.MemoryCopy(src, dst, pixels * 4L, pixels * 4L);
            }
            else
            {
                for (int i = 0; i < pixels; i++)
                {
                    byte r = src[0], g = src[1], b = src[2], a = src[3];
                    dst[0] = (byte)(r * a / 255);
                    dst[1] = (byte)(g * a / 255);
                    dst[2] = (byte)(b * a / 255);
                    dst[3] = a;
                    src += 4;
                    dst += 4;
                }
            }
        }

        return SKImage.FromBitmap(premul);
    }
}
