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

    [Fact]
    public async Task ClearEditsRemovesMarkersRestoresStateAndDeletesStoredFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "lme-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "replacement.png");
        await File.WriteAllBytesAsync(input, [1, 2, 3]);
        var project = new ModProject();
        var asset = new AssetRecord { LogicalPath = "acct/bundle/7.28", Type = AssetType.Texture, Size = 12345 };
        project.Assets.Add(asset);
        try
        {
            var service = new AssetEditService();
            var result = await service.ReplaceFromFileAsync(project, asset.AssetId, input, root);
            asset.Metadata["unityFieldEdits"] = "[]";
            asset.Metadata["spriteMetadata"] = "{}";
            Assert.True(AssetEditService.HasEdits(asset));

            var cleared = service.ClearEdits(project, asset.AssetId, root);

            Assert.True(cleared);
            Assert.False(AssetEditService.HasEdits(asset));
            Assert.Equal(AssetEditState.Unchanged, asset.EditState);
            Assert.Null(asset.ModifiedHash);
            Assert.Equal(12345, asset.Size); // originalSize 还原替换前显示大小
            Assert.False(File.Exists(result.StoredPath)); // 项目 edits/assets 内的暂存文件被删除
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ClearEditsKeepsExternalReplacementFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "lme-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var external = Path.Combine(Path.GetTempPath(), "lme-external-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(external, [9]);
        var project = new ModProject();
        var asset = new AssetRecord { LogicalPath = "acct/bundle/8.28", Type = AssetType.Texture };
        project.Assets.Add(asset);
        try
        {
            // 直接登记一个项目外的替换文件（模拟手工编辑的元数据）。
            asset.Metadata["replacementPath"] = external;
            var cleared = new AssetEditService().ClearEdits(project, asset.AssetId, root);
            Assert.True(cleared);
            Assert.True(File.Exists(external)); // 项目外的文件不删除
            Assert.False(AssetEditService.HasEdits(asset));
        }
        finally
        {
            Directory.Delete(root, true);
            File.Delete(external);
        }
    }

    [Fact]
    public void ClearEditsWithoutEditsReturnsFalse()
    {
        var project = new ModProject();
        var asset = new AssetRecord { LogicalPath = "acct/bundle/9.28" };
        project.Assets.Add(asset);
        Assert.False(new AssetEditService().ClearEdits(project, asset.AssetId));
        Assert.Throws<KeyNotFoundException>(() => new AssetEditService().ClearEdits(project, Guid.NewGuid()));
    }

    [Fact]
    public async Task SecondReplacementKeepsFirstOriginalSize()
    {
        var root = Path.Combine(Path.GetTempPath(), "lme-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, "a.png");
        var second = Path.Combine(root, "b.png");
        await File.WriteAllBytesAsync(first, [1]);
        await File.WriteAllBytesAsync(second, [2, 2]);
        var project = new ModProject();
        var asset = new AssetRecord { LogicalPath = "acct/bundle/10.28", Type = AssetType.Texture, Size = 777 };
        project.Assets.Add(asset);
        try
        {
            var service = new AssetEditService();
            await service.ReplaceFromFileAsync(project, asset.AssetId, first, root);
            await service.ReplaceFromFileAsync(project, asset.AssetId, second, root);
            Assert.Equal("777", asset.Metadata["originalSize"]); // 重复替换不覆盖最早的原始大小
        }
        finally { Directory.Delete(root, true); }
    }
}
