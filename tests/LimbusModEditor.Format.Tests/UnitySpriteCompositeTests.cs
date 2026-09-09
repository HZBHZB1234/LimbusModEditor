using System.Text;
using LimbusModEditor.Editing.Images;
using LimbusModEditor.Formats.Unity;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LimbusModEditor.Format.Tests;

/// <summary>plan-01 第 1 步的合成样本单测：Sprite 裁剪坐标换算（Unity 左下
/// 原点 → 图像左上原点）、像素裁剪正确性与越界 fail fast；TextAsset 正文
/// UTF-8 解码与非法字节的明确失败。</summary>
public class UnitySpriteCompositeTests
{
    [Fact]
    public void Resolve_converts_bottom_left_origin_to_top_left()
    {
        // 真实样本形态：纹理 697×314，textureRect 621.85×181.85 @(39.08, 71.08)
        var rect = new UnitySpriteRect(39.076122f, 71.07612f, 621.8478f, 181.84775f);
        var crop = UnitySpriteCrop.Resolve(rect, 697, 314);
        Assert.Equal(39, crop.X);
        Assert.Equal(314 - 71 - 182, crop.Y); // 61
        Assert.Equal(622, crop.Width);
        Assert.Equal(182, crop.Height);
    }

    [Fact]
    public void Resolve_keeps_full_texture_rect_at_origin()
    {
        var crop = UnitySpriteCrop.Resolve(new UnitySpriteRect(0, 0, 64, 32), 64, 32);
        Assert.Equal(new UnityCropRect(0, 0, 64, 32), crop);
    }

    [Fact]
    public void Resolve_clamps_float_rounding_overflow_to_texture_bounds()
    {
        // 舍入让区域超出右/下边界 1 像素：按纹理边界钳制（确定性），不报错。
        var crop = UnitySpriteCrop.Resolve(new UnitySpriteRect(0.4f, 0.6f, 64.2f, 32.4f), 64, 32);
        Assert.Equal(0, crop.X);
        Assert.Equal(0, crop.Y);
        Assert.Equal(64, crop.Width);
        Assert.Equal(32, crop.Height);
    }

    [Theory]
    [InlineData(0, 0, 0, 16)]      // 零宽
    [InlineData(0, 0, 16, 0)]      // 零高
    [InlineData(100, 0, 8, 8)]     // 完全在纹理右侧之外
    [InlineData(0, 100, 8, 8)]     // 完全在纹理下方之外
    public void Resolve_fails_fast_for_invalid_regions(float x, float y, float width, float height)
    {
        var rect = new UnitySpriteRect(x, y, width, height);
        Assert.Throws<InvalidDataException>(() => UnitySpriteCrop.Resolve(rect, 64, 64));
    }

    [Fact]
    public void ToPngCropped_crops_the_requested_pixels()
    {
        // 8×4 纹理：左半红、右半蓝；裁剪右半得到纯蓝 4×4。
        var width = 8;
        var height = 4;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var blue = x >= 4;
                pixels[offset] = (byte)(blue ? 0 : 255);
                pixels[offset + 1] = 0;
                pixels[offset + 2] = (byte)(blue ? 255 : 0);
                pixels[offset + 3] = 255;
            }
        var codec = new UnityTextureCodec();
        var png = codec.ToPngCropped(new UnityTextureInfo(width, height, UnityTexturePixelFormat.Rgba32, pixels), 4, 0, 4, 4);
        using var image = Image.Load<Rgba32>(png);
        Assert.Equal(4, image.Width);
        Assert.Equal(4, image.Height);
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = image[x, y];
                Assert.Equal(0, pixel.R);
                Assert.Equal(255, pixel.B);
                Assert.Equal(255, pixel.A);
            }
    }

    [Fact]
    public void ToPngCropped_rejects_out_of_bounds_regions()
    {
        var pixels = new byte[4 * 4 * 4];
        var texture = new UnityTextureInfo(4, 4, UnityTexturePixelFormat.Rgba32, pixels);
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnityTextureCodec().ToPngCropped(texture, 2, 2, 4, 4));
    }

    [Fact]
    public void TextAsset_decodes_utf8_and_reports_invalid_bytes()
    {
        var json = "{\"name\":\"镜面地牢\"}";
        var ok = new UnityTextAsset("CAB-test", 1, "static/dataClass/file", Encoding.UTF8.GetBytes(json));
        Assert.Equal(json, ok.TryDecodeUtf8());

        // 非 UTF-8 字节：明确返回 null（不猜编码），由调用方给出「十六进制预览」提示。
        var binary = new UnityTextAsset("CAB-test", 2, "binary", [0xFF, 0xFE, 0x00, 0x01]);
        Assert.Null(binary.TryDecodeUtf8());
    }
}
