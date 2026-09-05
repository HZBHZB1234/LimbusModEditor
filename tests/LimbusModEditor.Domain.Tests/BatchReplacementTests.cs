using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Domain.Tests;

/// <summary>P3.3: batch replacement matches folder files to assets by file
/// name and registers them through the reversible pipeline.</summary>
public class BatchReplacementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lme-batch-" + Guid.NewGuid().ToString("N"));

    public BatchReplacementTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string MakeProject()
    {
        var projectDir = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectDir);
        return projectDir;
    }

    private static ModProject MakeProjectModel()
    {
        var project = new ModProject();
        project.Assets.Add(new AssetRecord { LogicalPath = "images/logo.png", Type = AssetType.Texture });
        project.Assets.Add(new AssetRecord { LogicalPath = "images/icon.png", Type = AssetType.Texture, Metadata = { ["replacementPath"] = @"C:\already.png" } });
        project.Assets.Add(new AssetRecord { LogicalPath = "audio/bgm1", Type = AssetType.Audio });
        return project;
    }

    private string MakeFolder(params (string Name, bool WithContent)[] files)
    {
        var folder = Path.Combine(_root, "replacements-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        foreach (var (name, withContent) in files)
            File.WriteAllText(Path.Combine(folder, name), withContent ? "data" : "data");
        return folder;
    }

    [Fact]
    public async Task Matches_by_file_name_and_registers_replacement()
    {
        var projectDir = MakeProject();
        var project = MakeProjectModel();
        var folder = MakeFolder(("logo.png", true), ("bgm1", true), ("icon.png", true), ("unknown.txt", true));
        var report = await new AssetEditService().BatchReplaceFromDirectoryAsync(project, folder, projectDir);
        Assert.Equal(2, report.Matched);
        Assert.Contains("unknown.txt", report.FilesWithoutAsset);
        Assert.Equal(1, report.SkippedAlreadyReplaced);
        var logo = project.Assets.Single(x => x.LogicalPath == "images/logo.png");
        Assert.True(logo.Metadata.ContainsKey("replacementPath"));
        Assert.True(File.Exists(logo.Metadata["replacementPath"]));
    }

    [Fact]
    public async Task Only_unreplaced_flag_skips_existing_replacements()
    {
        var projectDir = MakeProject();
        var project = MakeProjectModel();
        var folder = MakeFolder(("icon.png", true));
        var service = new AssetEditService();
        var report = await service.BatchReplaceFromDirectoryAsync(project, folder, projectDir, onlyUnreplaced: true);
        Assert.Equal(0, report.Matched);
        Assert.Equal(1, report.SkippedAlreadyReplaced);

        var forced = await service.BatchReplaceFromDirectoryAsync(project, folder, projectDir, onlyUnreplaced: false);
        Assert.Equal(1, forced.Matched);
    }

    [Fact]
    public async Task Missing_folder_throws()
    {
        var projectDir = MakeProject();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
            await new AssetEditService().BatchReplaceFromDirectoryAsync(MakeProjectModel(), Path.Combine(_root, "missing"), projectDir));
    }

    [Fact]
    public void Describe_reports_counts()
    {
        var report = new BatchReplacementReport(3, ["a.png"], ["x", "y"], 1);
        Assert.Contains("3 个替换", report.Describe());
        Assert.Contains("2 个资源没有提供文件", report.Describe());
    }
}
