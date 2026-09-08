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

    [Fact]
    public async Task PlainTextIsStoredVerbatim()
    {
        var root = Directory.CreateTempSubdirectory("lme-text-");
        try
        {
            var source = Path.Combine(root.FullName, "note.txt");
            await File.WriteAllTextAsync(source, "原始", new UTF8Encoding(false));
            var project = new ModProject();
            var asset = new AssetRecord { LogicalPath = "note.txt", SourcePath = source, Type = AssetType.Text };
            project.Assets.Add(asset);
            var service = new TextAssetEditService(new AssetEditService());
            var document = await service.OpenAsync(project, asset.AssetId);
            Assert.False(document.IsJson);
            document = document with { Text = "改过的 文本\n第二行" };
            var result = await service.SaveAsync(project, document, root.FullName);
            Assert.Equal("改过的 文本\n第二行", await File.ReadAllTextAsync(result.StoredPath, new UTF8Encoding(false)));
            Assert.Equal(AssetEditState.Modified, asset.EditState);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task InvalidJsonIsRejectedBeforeWriting()
    {
        // 窗口在保存前会先校验一次以保留对话框；服务层同样 fail fast，
        // 保证任何调用路径都不会把坏 JSON 写进项目。
        var root = Directory.CreateTempSubdirectory("lme-text-");
        try
        {
            var source = Path.Combine(root.FullName, "bad.json");
            await File.WriteAllTextAsync(source, "{\"value\":1}", Encoding.UTF8);
            var project = new ModProject();
            var asset = new AssetRecord { LogicalPath = "bad.json", SourcePath = source, Type = AssetType.Json };
            project.Assets.Add(asset);
            var service = new TextAssetEditService(new AssetEditService());
            var document = await service.OpenAsync(project, asset.AssetId);
            document = document with { Text = "{ broken" };
            await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(
                () => service.SaveAsync(project, document, root.FullName));
            Assert.Equal(AssetEditState.Unchanged, asset.EditState);
        }
        finally { root.Delete(true); }
    }
}
