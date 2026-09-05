using System.Text;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

public sealed class TextAssetEditTests
{
    [Fact]
    public async Task JsonDocumentIsFormattedAndRecordedAsReplacement()
    {
        var root = Directory.CreateTempSubdirectory("lme-text-");
        try
        {
            var source = Path.Combine(root.FullName, "data.json");
            await File.WriteAllTextAsync(source, "{\"value\":1}", Encoding.UTF8);
            var project = new ModProject();
            var asset = new AssetRecord { LogicalPath = "data.json", SourcePath = source, Type = AssetType.Json };
            project.Assets.Add(asset);
            var service = new TextAssetEditService(new AssetEditService());
            var document = await service.OpenAsync(project, asset.AssetId);
            document = document with { Text = "{\"value\":2}" };
            var result = await service.SaveAsync(project, document, root.FullName);
            Assert.True(File.Exists(result.StoredPath));
            Assert.Contains("\"value\": 2", await File.ReadAllTextAsync(result.StoredPath));
            Assert.Equal(AssetEditState.Modified, asset.EditState);
            Assert.Single(project.Edits);
        }
        finally { root.Delete(true); }
    }
}
