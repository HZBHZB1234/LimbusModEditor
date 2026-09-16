using System.Text;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Tests;

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

    [Fact]
    public async Task BundleAssetIsRefusedInsteadOfReadingTheContainerAsText()
    {
        // 回归（2026-09 卡死事故）：bundle 内对象的 SourcePath 是**整个 AssetBundle 容器**
        // （<外层键>/<内层键>/__data）。以前 OpenAsync 会把容器字节当正文读，
        // 2.26 MB 二进制塞进 WPF 文本框实测排版 26.5 秒 → 界面被 Windows 判「未响应」后强杀
        // （用户报的「预览 static-data 后软件崩溃」，实为挂起，所以没有 crash 日志）。
        var root = Directory.CreateTempSubdirectory("lme-bundle-");
        try
        {
            var container = Path.Combine(root.FullName, "__data");
            var payload = new byte[64];
            Encoding.ASCII.GetBytes("UnityFS").CopyTo(payload, 0);
            payload[32] = 0;
            await File.WriteAllBytesAsync(container, payload);

            var project = new ModProject();
            var asset = new AssetRecord
            {
                LogicalPath = "outer/inner/CAB-x/1.49",
                SourcePath = container,
                ContainerPath = "CAB-x",
                Bundle = "62d6e466f528b73cf836882c2a786cc2",
                UnityPathId = 1,
                UnityTypeId = 49,
                Type = AssetType.Text,
                Metadata = { ["unityBundle"] = "true" },
            };
            project.Assets.Add(asset);

            Assert.False(TextAssetEditService.CanEditText(asset));
            var service = new TextAssetEditService(new AssetEditService());
            await Assert.ThrowsAsync<NotSupportedException>(() => service.OpenAsync(project, asset.AssetId));
            Assert.Equal(AssetEditState.Unchanged, asset.EditState);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task LooseFileWithBinaryContentIsRefused()
    {
        // 第二道防线：即使来源是松散文件，只要内容看起来是二进制（含 NUL），
        // 也不能塞进 WPF 文本框。
        var root = Directory.CreateTempSubdirectory("lme-binary-");
        try
        {
            var source = Path.Combine(root.FullName, "weird.json");
            await File.WriteAllBytesAsync(source, new byte[] { 0x7B, 0x00, 0x7D, 0x00 });
            var project = new ModProject();
            var asset = new AssetRecord { LogicalPath = "weird.json", SourcePath = source, Type = AssetType.Json };
            project.Assets.Add(asset);
            var service = new TextAssetEditService(new AssetEditService());
            await Assert.ThrowsAsync<NotSupportedException>(() => service.OpenAsync(project, asset.AssetId));
        }
        finally { root.Delete(true); }
    }
}
