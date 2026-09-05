using LimbusModEditor.Editing.Images;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LimbusModEditor.Domain.Tests;

public class ImageAtlasTests
{
    [Fact]
    public void GridSplitAndRepackPreserveDimensions()
    {
        using var source = new Image<Rgba32>(8, 4, new Rgba32(255, 0, 0, 255));
        using var input = new MemoryStream(); source.SaveAsPng(input);
        var service = new ImageAtlasService();
        var split = service.SplitGrid(input.ToArray(), 2, 2);
        var repacked = service.Repack(split.Layout, split.Regions);
        using var result = Image.Load<Rgba32>(repacked);
        Assert.Equal(8, result.Width);
        Assert.Equal(4, result.Height);
    }
}
