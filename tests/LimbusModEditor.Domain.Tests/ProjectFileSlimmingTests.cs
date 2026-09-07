using System.Text.Json;
using System.Text.Json.Serialization;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

/// <summary>阶段 C 项目瘦身：纯引用资产不入项目文件（可由扫描索引重建），
/// 实体化/导入资产保留；旧格式项目文件里的引用资产加载时照常还原。</summary>
public class ProjectFileSlimmingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-slim-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    private static AssetRecord ReferenceAsset(string path) => new()
    {
        LogicalPath = path,
        Type = AssetType.Texture,
        Size = 100,
        Metadata = { ["reference"] = "true" }
    };

    private static AssetRecord MaterializedAsset(string path) => new()
    {
        LogicalPath = path,
        Type = AssetType.Texture,
        Size = 200,
        Metadata = { ["materialized"] = "true" }
    };

    [Fact]
    public async Task Save_skips_reference_assets_but_keeps_materialized_ones()
    {
        Directory.CreateDirectory(_root);
        var project = new ModProject { Name = "Slim" };
        project.Assets.Add(ReferenceAsset("outer/inner/a/1.28"));
        project.Assets.Add(MaterializedAsset("outer/inner/b/2.28"));
        project.Assets.Add(ReferenceAsset("outer/inner/c/3.28"));
        var file = Path.Combine(_root, "Slim.lmeproj");

        await new ProjectService().SaveAsync(project, file);
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("outer/inner/b/2.28", text);
        Assert.DoesNotContain("outer/inner/a/1.28", text);
        Assert.DoesNotContain("outer/inner/c/3.28", text);
        Assert.True(new FileInfo(file).Length < 4096, "引用资产不应撑大项目文件");

        var loaded = await new ProjectService().LoadAsync(file);
        var asset = Assert.Single(loaded.Assets);
        Assert.Equal("outer/inner/b/2.28", asset.LogicalPath);
        Assert.False(asset.Metadata.ContainsKey("reference"));
    }

    [Fact]
    public async Task Load_still_reads_reference_assets_from_legacy_project_files()
    {
        Directory.CreateDirectory(_root);
        var project = new ModProject { Name = "Legacy" };
        project.Assets.Add(ReferenceAsset("outer/inner/a/1.28"));
        project.Assets.Add(MaterializedAsset("outer/inner/b/2.28"));
        // 用不含瘦身的序列化选项模拟旧版编辑器写出的项目文件。
        var legacyOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var file = Path.Combine(_root, "Legacy.lmeproj");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(project, legacyOptions));

        var loaded = await new ProjectService().LoadAsync(file);
        Assert.Equal(2, loaded.Assets.Count);
        Assert.Equal("true", loaded.Assets[0].Metadata["reference"]);
    }
}
