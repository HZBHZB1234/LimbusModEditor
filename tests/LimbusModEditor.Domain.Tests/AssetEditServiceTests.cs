using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

public class AssetEditServiceTests
{
    [Fact]
    public async Task ReplacementIsStoredAndRecordedAsEdit()
    {
        var root = Path.Combine(Path.GetTempPath(), "lme-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "replacement.bin");
        await File.WriteAllBytesAsync(input, [4, 5, 6]);
        var project = new ModProject();
        var asset = new AssetRecord { LogicalPath = "acct/bundle/1.28", Type = AssetType.Texture, OriginalHash = "old" };
        project.Assets.Add(asset);
        try
        {
            var result = await new AssetEditService().ReplaceFromFileAsync(project, asset.AssetId, input, root);
            Assert.True(File.Exists(result.StoredPath));
            Assert.Equal(AssetEditState.Modified, asset.EditState);
            Assert.Single(project.Edits);
            Assert.Equal(result.Hash, project.Edits[0].AfterHash);
        }
        finally { Directory.Delete(root, true); }
    }
}
