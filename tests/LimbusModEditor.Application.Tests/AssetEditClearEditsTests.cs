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
/// <b>asset.edit.clearEdits</b> 的入口契约测试：AssetEditService.ClearEdits 早就能
/// 撤销一个资源的编辑，但① 没有 IPC 入口；② 只清标记、不删
/// <see cref="ModProject.Edits"/> 里的记录，而这份清单正是 ProjectBuildService
/// 「还有 X 处改动」的计数来源，会留下指向已删文件的幽灵条目。
/// </summary>
public sealed class AssetEditClearEditsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipc-clear-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectState _state = new();
    private readonly IpcGateway _gateway;

    public AssetEditClearEditsTests()
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

    private async Task<IpcResponse> Call(string assetId)
        => IpcResponse.FromJson(await _gateway.HandleRequestAsync(
            IpcRequest.Create("req-clear-" + Guid.NewGuid().ToString("N"), "asset.edit.clearEdits",
                SerializePayload(new AssetEditClearEditsRequest(assetId))).ToJson()));

    private async Task<ModProject> OpenProject(string name)
    {
        var dir = Path.Combine(_root, "proj-" + name);
        var project = await new ProjectService().CreateAsync(dir, name);
        _state.SetProject(project, Path.Combine(dir, name + ".lmeproj"));
        return project;
    }

    [Fact]
    public async Task Without_a_project_or_unknown_asset_reports_a_chinese_reason()
    {
        var noProject = await Call("x");
        Assert.False(noProject.Ok);
        Assert.Equal(IpcErrorCode.InvalidQuery, noProject.Error!.Code);
        Assert.Contains("请先打开项目", noProject.Error!.Message);

        await OpenProject("撤销");
        var unknown = await Call(Guid.NewGuid().ToString());
        Assert.False(unknown.Ok);
        Assert.Equal(IpcErrorCode.NotFound, unknown.Error!.Code);
        Assert.Contains("未找到资源", unknown.Error!.Message);
    }

    /// <summary>撤销一个已替换的资源：编辑标记清掉，项目编辑清单里的记录同步移除
    /// （否则「还有 X 处改动」会留下指向已删文件的幽灵计数）。</summary>
    [Fact]
    public async Task Drops_the_markers_and_the_project_edit_records()
    {
        var project = await OpenProject("撤销已替换");
        var asset = new AssetRecord { LogicalPath = "acct/bundle/C.png", Type = AssetType.Texture, Size = 4096 };
        project.Assets.Add(asset);

        var input = Path.Combine(_root, "replacement.png");
        await File.WriteAllBytesAsync(input, [1, 2, 3, 4]);
        var projectDir = Path.GetDirectoryName(_state.ProjectFile!)!;
        await new AssetEditService().ReplaceFromFileAsync(project, asset, input, projectDir);
        Assert.True(AssetEditService.HasEdits(asset));
        Assert.Single(project.Edits);

        var response = await Call(asset.AssetId.ToString());
        Assert.True(response.Ok, response.Error?.Message);

        var result = response.Payload!.Value.Deserialize<AssetEditClearEditsResponse>(IpcJson.Options)!;
        Assert.True(result.Ok);
        Assert.Equal(1, result.ClearedCount);
        Assert.Equal(1, result.RemovedEditOperations);
        Assert.Equal(0, result.RemainingEdits);
        Assert.False(AssetEditService.HasEdits(asset));
        Assert.Equal(AssetEditState.Unchanged, asset.EditState);
        Assert.Empty(project.Edits);
        Assert.Contains("已撤销", result.Info);
    }

    /// <summary>没有编辑的资源：清一次也不报错，清单计数不被动；逻辑路径也能当 assetId 用。</summary>
    [Fact]
    public async Task On_an_untouched_asset_is_a_no_op_with_a_chinese_reason()
    {
        var project = await OpenProject("撤销未改");
        var asset = new AssetRecord { LogicalPath = "acct/bundle/D.png", Type = AssetType.Texture };
        project.Assets.Add(asset);

        var response = await Call(asset.LogicalPath);
        Assert.True(response.Ok, response.Error?.Message);

        var result = response.Payload!.Value.Deserialize<AssetEditClearEditsResponse>(IpcJson.Options)!;
        Assert.Equal(0, result.ClearedCount);
        Assert.Equal(0, result.RemovedEditOperations);
        Assert.Contains("没有可撤销", result.Info);
    }
}
