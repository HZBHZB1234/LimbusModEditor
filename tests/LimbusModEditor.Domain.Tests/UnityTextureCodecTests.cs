using LimbusModEditor.Editing.Images;
using SixLabors.ImageSharp;

namespace LimbusModEditor.Domain.Tests;

public class UnityTextureCodecTests
{
    [Fact]
    public void Rgba32RoundTripsThroughPng()
    {
        var codec = new UnityTextureCodec();
        var source = new UnityTextureInfo(2, 1, UnityTexturePixelFormat.Rgba32, [255, 0, 0, 255, 0, 20, 40, 128]);
        var png = codec.ToPng(source);
        var decoded = codec.FromPng(png);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.Equal(source.Format, decoded.Format);
        Assert.Equal(source.PixelData, decoded.PixelData);
    }

    [Fact]
    public void Bgra32SwapsRedAndBlueChannels()
    {
        var codec = new UnityTextureCodec();
        var image = codec.ToImage(new UnityTextureInfo(1, 1, UnityTexturePixelFormat.Bgra32, [10, 20, 30, 255]));
        Assert.Equal((byte)30, image[0, 0].R);
        Assert.Equal((byte)10, image[0, 0].B);
    }

    [Fact]
    public void Dxt1EncodesAndDecodesNonMultipleOfFourDimensions()
    {
        var codec = new UnityTextureCodec();
        using var source = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(5, 3);
        source.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < source.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < source.Width; x++) row[x] = new(240, 20, 30, 255);
            }
        });
        using var pngStream = new MemoryStream(); source.SaveAsPng(pngStream);
        var encoded = codec.FromPng(pngStream.ToArray(), UnityTexturePixelFormat.Dxt1);
        Assert.Equal(2 * 1 * 8, encoded.PixelData.Length);
        using var decoded = codec.ToImage(encoded);
        Assert.Equal(5, decoded.Width); Assert.Equal(3, decoded.Height);
        Assert.InRange(decoded[2, 1].R, (byte)220, (byte)255);
        Assert.InRange(decoded[2, 1].G, (byte)0, (byte)45);
    }

    [Fact]
    public void Dxt5PreservesAlphaGradientApproximately()
    {
        var codec = new UnityTextureCodec();
        using var source = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(4, 4);
        source.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < 4; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < 4; x++) row[x] = new(20, 80, 200, (byte)(x * 85));
            }
        });
        using var pngStream = new MemoryStream(); source.SaveAsPng(pngStream);
        var encoded = codec.FromPng(pngStream.ToArray(), UnityTexturePixelFormat.Dxt5);
        Assert.Equal(16, encoded.PixelData.Length);
        using var decoded = codec.ToImage(encoded);
        Assert.InRange(decoded[0, 0].A, (byte)0, (byte)20);
        Assert.InRange(decoded[3, 0].A, (byte)235, (byte)255);
    }

    // ── 行序约定（资源工作台图像上下颠倒的回归门）──────────────────────

    [Fact]
    public void Png_decode_flips_the_bottom_up_unity_payload()
    {
        // 2×2 RGBA32。Unity 负载第 0 行 = 图像最下面一行（蓝），第 1 行 = 最上面一行（红）。
        var codec = new UnityTextureCodec();
        var source = new UnityTextureInfo(2, 2, UnityTexturePixelFormat.Rgba32,
        [
            0, 0, 255, 255, 0, 0, 255, 255, // 负载第 0 行（左下原点 → 图像底部）
            255, 0, 0, 255, 255, 0, 0, 255, // 负载第 1 行（图像顶部）
        ]);

        var png = codec.ToPng(source);
        using var image = Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(png);
        Assert.Equal((byte)255, image[0, 0].R); // PNG 第一行 = 红（= 负载最后一行）
        Assert.Equal((byte)0, image[0, 0].B);
        Assert.Equal((byte)255, image[0, 1].B); // PNG 最后一行 = 蓝（= 负载第一行）
        Assert.Equal((byte)0, image[0, 1].R);
    }

    [Fact]
    public void Png_encode_flips_back_so_the_round_trip_is_byte_identical()
    {
        var codec = new UnityTextureCodec();
        var source = new UnityTextureInfo(3, 2, UnityTexturePixelFormat.Rgba32,
        [
            0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, // 底部行（负载第 0 行）
            255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, // 顶部行（负载第 1 行）
        ]);

        // 读出来 → 写回去：像素逐字节不变（否则「换图」会把整张图上下颠倒）。
        var decoded = codec.FromPng(codec.ToPng(source));
        Assert.Equal(source.PixelData, decoded.PixelData);
    }

    [Fact]
    public void Cropped_preview_uses_top_left_coordinates_after_the_flip()
    {
        // 4×4 RGBA32：负载第 0~1 行（图像下半部）蓝，第 2~3 行（图像上半部）红。
        var width = 4;
        var height = 4;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var bottomHalf = y < 2; // 负载行序：前两行是图像下半部
                pixels[offset] = (byte)(bottomHalf ? 0 : 255);
                pixels[offset + 1] = 0;
                pixels[offset + 2] = (byte)(bottomHalf ? 255 : 0);
                pixels[offset + 3] = 255;
            }

        // 裁剪「图像顶部」2 行（y=0 是左上原点）→ 必须拿到红色；
        // 这与 UnitySpriteCrop 的「Unity 左下原点 → 图像左上原点」换算配套。
        var png = new UnityTextureCodec().ToPngCropped(
            new UnityTextureInfo(width, height, UnityTexturePixelFormat.Rgba32, pixels), 0, 0, 4, 2);
        using var image = Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(png);
        Assert.Equal((byte)255, image[0, 0].R);
        Assert.Equal((byte)0, image[0, 0].B);
    }

    [Fact]
    public void Dxt_payload_starts_at_the_bottom_block_row()
    {
        // 4×8：图像上半（行 0~3）红、下半（行 4~7）蓝 —— 两个 4×4 块各自纯色，
        // 这样色端点不会被「同一块里混两色」摊成紫/黑，方向才可判定。
        // DXT 负载按自下而上的块行写入，所以负载的第一个色块必须是蓝色。
        var codec = new UnityTextureCodec();
        using var source = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(4, 8);
        source.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < 8; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < 4; x++) row[x] = y < 4 ? new(255, 0, 0, 255) : new(0, 0, 255, 255);
            }
        });
        using var pngStream = new MemoryStream(); source.SaveAsPng(pngStream);

        var encoded = codec.FromPng(pngStream.ToArray(), UnityTexturePixelFormat.Dxt1);
        Assert.Equal(2 * 8, encoded.PixelData.Length); // 4×8 → 1×2 块 → 每块 8 字节

        var firstBlock = (ushort)(encoded.PixelData[0] | (encoded.PixelData[1] << 8));
        Assert.Equal(31, firstBlock & 31);          // 蓝分量满
        Assert.Equal(0, (firstBlock >> 11) & 31);  // 红分量为 0
        var secondBlock = (ushort)(encoded.PixelData[8] | (encoded.PixelData[9] << 8));
        Assert.Equal(31, (secondBlock >> 11) & 31); // 第二块是图像上半（红）
        Assert.Equal(0, secondBlock & 31);

        // 解码后上下位置回到图像坐标：第一行红、最后一行蓝。
        using var decoded = codec.ToImage(encoded);
        Assert.True(decoded[0, 0].R > decoded[0, 0].B);
        Assert.True(decoded[0, 7].B > decoded[0, 7].R);
    }
}
