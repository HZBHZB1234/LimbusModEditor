using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

public class ImageAtlasEditServiceTests
{
    [Fact]
    public async Task SplitAndRepackRoundTripThroughProjectWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "lme-atlas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "atlas.png");
        using (var image = new Image<Rgba32>(8, 8, new Rgba32(20, 40, 60, 255))) await image.SaveAsPngAsync(source);
        var project = new ModProject();
        var asset = new AssetRecord { LogicalPath = "atlas", Type = AssetType.Texture };
        project.Assets.Add(asset);
        try
        {
            var service = new ImageAtlasEditService();
            var split = await service.SplitAsync(project, asset.AssetId, source, root, 2, 2);
            Assert.Equal(4, split.Layout.Regions.Count);
            var replacement = await service.RepackAsync(project, asset.AssetId, root);
            Assert.True(File.Exists(replacement.StoredPath));
            Assert.Equal(AssetEditState.Modified, asset.EditState);
        }
        finally { Directory.Delete(root, true); }
    }
}
