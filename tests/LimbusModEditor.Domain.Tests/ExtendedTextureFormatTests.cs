using LimbusModEditor.Editing.Images;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LimbusModEditor.Domain.Tests;

/// <summary>P1.3 tests for the extended uncompressed texture formats and the
/// mipmap layout math, using hand-crafted payloads with known byte order.</summary>
public class ExtendedTextureFormatTests
{
    private static UnityTextureCodec Codec() => new();

    [Fact]
    public void Argb32_reads_bgra_byte_order()
    {
        // B,G,R,A = 10,20,30,255 → R=30, G=20, B=10
        var image = Codec().ToImage(new UnityTextureInfo(1, 1, UnityTexturePixelFormat.Argb32, [10, 20, 30, 255]));
        Assert.Equal((byte)30, image[0, 0].R);
        Assert.Equal((byte)20, image[0, 0].G);
        Assert.Equal((byte)10, image[0, 0].B);
        Assert.Equal((byte)255, image[0, 0].A);
    }

    [Fact]
    public void Rgb565_round_trips_with_expected_quantization()
    {
        var source = new UnityTextureInfo(1, 1, UnityTexturePixelFormat.Rgb565, [0x00, 0xF8]); // 0xF800 = pure red
        var image = Codec().ToImage(source);
        Assert.Equal((byte)255, image[0, 0].R);
        Assert.Equal((byte)0, image[0, 0].G);
        Assert.Equal((byte)0, image[0, 0].B);

        using var pngStream = new MemoryStream(); image.SaveAsPng(pngStream);
        var encoded = Codec().FromPng(pngStream.ToArray(), UnityTexturePixelFormat.Rgb565);
        Assert.Equal(source.PixelData, encoded.PixelData);
    }

    [Fact]
    public void Argb4444_and_Rgba4444_use_their_nibble_orders()
    {
        // ARGB4444 little-endian 0x0F89: A=0 (hi), R=15... wait: value 0x0F89 → A=0, R=15? compute: 0x0F89 >> 12 = 0 (A), (>>8)&15 = 15 (R), (>>4)&15 = 8 (G), &15 = 9 (B)
        var image = Codec().ToImage(new UnityTextureInfo(1, 1, UnityTexturePixelFormat.Argb4444, [0x89, 0x0F]));
        Assert.Equal((byte)(15 * 17), image[0, 0].R);
        Assert.Equal((byte)(8 * 17), image[0, 0].G);
        Assert.Equal((byte)(9 * 17), image[0, 0].B);
        Assert.Equal((byte)0, image[0, 0].A);

        // RGBA4444 0xF089: R=15, G=0, B=8, A=9
        var image2 = Codec().ToImage(new UnityTextureInfo(1, 1, UnityTexturePixelFormat.Rgba4444, [0x89, 0xF0]));
        Assert.Equal((byte)(15 * 17), image2[0, 0].R);
        Assert.Equal((byte)0, image2[0, 0].G);
        Assert.Equal((byte)(8 * 17), image2[0, 0].B);
        Assert.Equal((byte)(9 * 17), image2[0, 0].A);
    }

    [Fact]
    public void R16_and_Alpha8_render_grayscale()
    {
        // R16 = 0x8000 (32768) → ~128 gray
        var image = Codec().ToImage(new UnityTextureInfo(1, 1, UnityTexturePixelFormat.R16, [0x00, 0x80]));
        Assert.InRange(image[0, 0].R, (byte)127, (byte)129);
        Assert.Equal(image[0, 0].R, image[0, 0].G);

        var alpha = Codec().ToImage(new UnityTextureInfo(1, 1, UnityTexturePixelFormat.Alpha8, [200]));
        Assert.Equal((byte)200, alpha[0, 0].R);
        Assert.Equal((byte)255, alpha[0, 0].A);
    }

    [Fact]
    public void Bgr24_and_Rg16_follow_their_channel_orders()
    {
        var bgr = Codec().ToImage(new UnityTextureInfo(1, 1, UnityTexturePixelFormat.Bgr24, [10, 20, 30]));
        Assert.Equal((byte)30, bgr[0, 0].R);
        Assert.Equal((byte)10, bgr[0, 0].B);

        var rg = Codec().ToImage(new UnityTextureInfo(1, 1, UnityTexturePixelFormat.Rg16, [0x80, 0xFF]));
        Assert.Equal((byte)128, rg[0, 0].R);
        Assert.Equal((byte)255, rg[0, 0].G);
        Assert.Equal((byte)0, rg[0, 0].B);
    }

    [Fact]
    public void All_supported_formats_round_trip_through_png_within_lossiness()
    {
        var codec = Codec();
        using var source = new Image<Rgba32>(4, 4);
        source.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < 4; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < 4; x++) row[x] = new((byte)(x * 60), (byte)(y * 60), 128, (byte)(255 - x * 40));
            }
        });
        using var pngStream = new MemoryStream(); source.SaveAsPng(pngStream);
        var png = pngStream.ToArray();

        foreach (var format in new[]
                 {
                     UnityTexturePixelFormat.Rgba32, UnityTexturePixelFormat.Bgra32, UnityTexturePixelFormat.Argb32,
                     UnityTexturePixelFormat.Rgb24, UnityTexturePixelFormat.Bgr24,
                     UnityTexturePixelFormat.R8, UnityTexturePixelFormat.R16, UnityTexturePixelFormat.Rg16,
                     UnityTexturePixelFormat.Rgb565, UnityTexturePixelFormat.Argb4444, UnityTexturePixelFormat.Rgba4444
                 })
        {
            var encoded = codec.FromPng(png, format);
            using var decoded = codec.ToImage(encoded);
            // lossless formats must be exact; quantized ones at least keep hue direction
            if (format is UnityTexturePixelFormat.Rgba32 or UnityTexturePixelFormat.Bgra32 or UnityTexturePixelFormat.Argb32)
                Assert.True(ImagesEqual(source, decoded), $"{format} must round trip exactly");
            // R8/Alpha8 store the alpha channel, which decreases in this image
            if (format is not (UnityTexturePixelFormat.R8 or UnityTexturePixelFormat.Alpha8))
                Assert.True(decoded[3, 0].R >= decoded[0, 0].R, $"{format} must preserve red direction");
        }
    }

    private static bool ImagesEqual(Image<Rgba32> a, Image<Rgba32> b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (var y = 0; y < a.Height; y++) for (var x = 0; x < a.Width; x++)
            if (a[x, y] != b[x, y]) return false;
        return true;
    }

    [Fact]
    public void Mipmap_layout_slices_levels_correctly()
    {
        // 8×8 RGBA32 with 3 mip levels: 256 + 64 + 16 = 336 bytes
        var data = new byte[256 + 64 + 16];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);
        var texture = new UnityTextureInfo(8, 8, UnityTexturePixelFormat.Rgba32, data);

        Assert.Equal(3, UnityTextureMipmaps.EstimateMipCount(8, 8, UnityTexturePixelFormat.Rgba32, data.Length));
        var level2 = UnityTextureMipmaps.Slice(texture, 2);
        Assert.Equal(2, level2.Width);
        Assert.Equal(2, level2.Height);
        Assert.Equal(16, level2.PixelData.Length);
        Assert.Equal(data.Skip(320).Take(16), level2.PixelData);
    }

    [Fact]
    public void Mipmap_layout_supports_block_formats()
    {
        // 8×8 DXT1: level0 = 2×2 blocks × 8 = 32B; level1 = 1×1 block = 8B
        var data = new byte[40];
        var texture = new UnityTextureInfo(8, 8, UnityTexturePixelFormat.Dxt1, data);
        Assert.Equal(2, UnityTextureMipmaps.EstimateMipCount(8, 8, UnityTexturePixelFormat.Dxt1, data.Length));
        var level1 = UnityTextureMipmaps.Slice(texture, 1);
        Assert.Equal(4, level1.Width);
        Assert.Equal(4, level1.Height);
        Assert.Equal(8, level1.PixelData.Length);
    }

    [Fact]
    public void Catalog_marks_lossiness_and_layout_notes()
    {
        var dxt = TextureFormatCatalog.Describe(UnityTexturePixelFormat.Dxt1);
        Assert.False(dxt.Lossless);
        var rgba = TextureFormatCatalog.Describe(UnityTexturePixelFormat.Rgba32);
        Assert.True(rgba.Lossless);
        var argb32 = TextureFormatCatalog.Describe(UnityTexturePixelFormat.Argb32);
        Assert.Contains("B,G,R,A", argb32.Notes);
        Assert.Equal(14, TextureFormatCatalog.All().Count);
    }
}
