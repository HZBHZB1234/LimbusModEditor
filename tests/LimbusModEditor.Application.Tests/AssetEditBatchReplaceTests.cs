using System.Text.Json;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Ipc;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;
using static LimbusModEditor.Application.Ipc.IpcJson;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// <b>asset.edit.batchReplace</b> 的入口契约测试：服务端早就有「按文件名从目录
/// 批量替换」的能力（AssetEditService.BatchReplaceFromDirectoryAsync），此前没有
/// IPC 入口，Web 端只能一个一个替换。
///
/// <para>批量替换只登记、不做写回：源目录只读，文件被复制进项目 edits/assets；
/// 匹配不到的一律计入 skipped 并给中文原因，不猜、不动。</para>
/// </summary>
public sealed class AssetEditBatchReplaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipc-batch-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectState _state = new();
    private readonly IpcGateway _gateway;

    public AssetEditBatchReplaceTests()
    {
        Directory.CreateDirectory(_root);
        var catalog = new AssetCatalog(
            new UnityCacheSqliteIndexStore(Path.Combine(_root, "index.db")),
            EmptyAssetStateSource.Instance);
        _gateway = new IpcGateway(catalog, _state,
            new SpineDataGateway(Path.Combine(_root, "spine")),
            new BankIndexService(new BankIndexStore(_root)),
            new LangTextWorkbenchService(),
            new StaticIndexService(new StaticTableIndexStore(_root)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { /* 测试结束，尽力清理 */ }
    }

    private async Task<IpcResponse> Call(object payload)
        => IpcResponse.FromJson(await _gateway.HandleRequestAsync(
            IpcRequest.Create("req-batch-" + Guid.NewGuid().ToString("N"), "asset.edit.batchReplace",
                SerializePayload(payload)).ToJson()));

    private async Task<ModProject> OpenProject(string name)
    {
        var dir = Path.Combine(_root, "proj-" + name);
        var project = await new ProjectService().CreateAsync(dir, name);
        _state.SetProject(project, Path.Combine(dir, name + ".lmeproj"));
        return project;
    }

    [Fact]
    public async Task Without_a_project_or_directory_reports_a_chinese_reason()
    {
        var noProject = await Call(new AssetEditBatchReplaceRequest(_root));
        Assert.False(noProject.Ok);
        Assert.Equal(IpcErrorCode.InvalidQuery, noProject.Error!.Code);
        Assert.Contains("请先打开项目", noProject.Error!.Message);

        await OpenProject("无目录");
        var missing = await Call(new AssetEditBatchReplaceRequest(Path.Combine(_root, "不存在的目录")));
        Assert.False(missing.Ok);
        Assert.Contains("替换目录不存在", missing.Error!.Message);
    }

    /// <summary>目录里两个文件命中、一个没有对应资源：逐条给结果，匹配不到的不动。</summary>
    [Fact]
    public async Task Registers_matched_files_and_reports_the_unmatched()
    {
        var project = await OpenProject("批量");
        project.Assets.Add(new AssetRecord { LogicalPath = "acct/bundle/A.png", Type = AssetType.Texture });
        project.Assets.Add(new AssetRecord { LogicalPath = "acct/bundle/B.png", Type = AssetType.Texture });

        var folder = Path.Combine(_root, "source");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "A.png"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "B.png"), [4, 5, 6]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "C.png"), [7]); // 项目里没有同名资源

        var response = await Call(new AssetEditBatchReplaceRequest(folder));
        Assert.True(response.Ok, response.Error?.Message);

        var result = response.Payload!.Value.Deserialize<AssetEditBatchReplaceResponse>(IpcJson.Options)!;
        Assert.Equal(2, result.Replaced);
        Assert.All(result.Items, item => Assert.True(File.Exists(item.ReplacementPath)));
        Assert.Contains(result.Items, x => x.LogicalPath == "acct/bundle/A.png");
        Assert.Contains("C.png", string.Join("|", result.Warnings));
        Assert.Equal(2, project.Assets.Count(AssetEditService.HasEdits));
    }

    /// <summary>namePattern 只放行命中的文件名，其余计入 skipped 并给中文原因。</summary>
    [Fact]
    public async Task Honours_the_name_pattern()
    {
        var project = await OpenProject("通配符");
        project.Assets.Add(new AssetRecord { LogicalPath = "acct/bundle/A.png", Type = AssetType.Texture });
        project.Assets.Add(new AssetRecord { LogicalPath = "acct/bundle/B.txt", Type = AssetType.Text });

        var folder = Path.Combine(_root, "source-pattern");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "A.png"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "B.txt"), [2]);

        var response = await Call(new AssetEditBatchReplaceRequest(folder, "*.png"));
        Assert.True(response.Ok, response.Error?.Message);

        var result = response.Payload!.Value.Deserialize<AssetEditBatchReplaceResponse>(IpcJson.Options)!;
        Assert.Equal(1, result.Replaced);
        Assert.Single(result.Items);
        Assert.Equal("acct/bundle/A.png", result.Items[0].LogicalPath);
        Assert.Equal(1, result.Skipped); // B.txt 被通配符挡掉
        Assert.Contains("*.png", string.Join("|", result.Warnings));
        Assert.False(AssetEditService.HasEdits(project.Assets.First(x => x.LogicalPath.EndsWith("B.txt"))));
    }
}
