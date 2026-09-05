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
}
