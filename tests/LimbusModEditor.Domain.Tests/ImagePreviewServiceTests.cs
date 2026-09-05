using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using LimbusModEditor.Editing.Images;

namespace LimbusModEditor.Domain.Tests;

public class ImagePreviewServiceTests
{
    [Fact]
    public void CreatesThumbnailAndReportsDimensions()
    {
        using var image = new Image<Rgba32>(8, 4, new Rgba32(255, 0, 0, 200));
        using var source = new MemoryStream();
        image.SaveAsPng(source);
        var result = new ImagePreviewService().CreatePreview(source.ToArray(), 4, 4);
        Assert.Equal(8, result.Width);
        Assert.Equal(4, result.Height);
        Assert.Equal("PNG", result.Format);
        Assert.True(result.HasAlpha);
        Assert.NotEmpty(result.ThumbnailPng);
    }
}
