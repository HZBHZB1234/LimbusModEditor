using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using LimbusModEditor.Formats.Unity;

namespace LimbusModEditor.Domain.Tests;

public sealed class SpriteMetadataEditTests
{
    [Fact]
    public void StoresAndReadsMetadataDeterministically()
    {
        var project = new ModProject();
        var asset = new AssetRecord { LogicalPath = "bundle/1.213", Type = AssetType.Sprite };
        project.Assets.Add(asset);
        var service = new SpriteMetadataEditService();
        var value = new UnitySpriteMetadata(new UnitySpriteRect(1, 2, 32, 40), new UnitySpriteVector2(.5f, .25f), new UnitySpriteBorder(1, 2, 3, 4), 100);
        service.Set(project, asset, value);
        Assert.Equal(value, service.ReadStored(asset));
        Assert.Equal(AssetEditState.Modified, asset.EditState);
        Assert.Single(project.Edits);
    }

    [Fact]
    public void RejectsInvalidPixelsPerUnit()
    {
        var project = new ModProject();
        var asset = new AssetRecord { LogicalPath = "bundle/1.213", Type = AssetType.Sprite };
        var service = new SpriteMetadataEditService();
        Assert.Throws<ArgumentException>(() => service.Set(project, asset,
            new UnitySpriteMetadata(new UnitySpriteRect(0, 0, 1, 1), new UnitySpriteVector2(0, 0), new UnitySpriteBorder(), 0)));
    }
}
