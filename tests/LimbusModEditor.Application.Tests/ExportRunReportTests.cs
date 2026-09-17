using System.Text.Json;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Ipc;
using LimbusModEditor.Application.Projects;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using static LimbusModEditor.Application.Ipc.IpcJson;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// export.run 的 IPC 层契约测试：<b>逐条 written / skipped 清单</b>。
///
/// <para>计划只算一次（ModExportPlanService.Plan），结果直接取 ModPackExportResult，
/// 同一槽位的「计划」与「结果」并成一行返回，不重复计算。</para>
/// </summary>
public sealed class ExportRunReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipc-export-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectState _state = new();
    private readonly IpcGateway _gateway;

    public ExportRunReportTests()
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

    private async Task<IpcResponse> Run(string targetDirectory)
        => IpcResponse.FromJson(await _gateway.HandleRequestAsync(
            IpcRequest.Create("req-export-" + Guid.NewGuid().ToString("N"), "export.run",
                SerializePayload(new ExportRunRequest(targetDirectory))).ToJson()));

    private async Task OpenProject(string name)
    {
        var dir = Path.Combine(_root, "proj-" + name);
        var project = await new ProjectService().CreateAsync(dir, name);
        _state.SetProject(project, Path.Combine(dir, name + ".lmeproj"));
    }

    [Fact]
    public async Task Run_without_a_project_reports_a_chinese_reason()
    {
        var response = await Run(Path.Combine(_root, "out"));

        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCode.InvalidQuery, response.Error!.Code);
        Assert.Contains("请先打开项目", response.Error!.Message);
    }

    /// <summary>没有任何修改：8 个槽位全部列出，每一条都带中文跳过原因（不是空清单）。</summary>
    [Fact]
    public async Task Run_lists_every_skipped_slot_with_a_chinese_reason()
    {
        await OpenProject("无改动");

        var response = await Run(Path.Combine(_root, "out-empty"));
        Assert.True(response.Ok, response.Error?.Message);

        var result = response.Payload!.Value.Deserialize<ExportRunResponse>(IpcJson.Options)!;
        Assert.NotEmpty(result.Items);
        Assert.All(result.Items, item =>
        {
            Assert.False(item.Written);
            Assert.NotEmpty(item.SkippedReasons);
        });
        Assert.Equal(result.Items.Count, result.Skipped.Count);
        Assert.Equal(result.Items.Count, result.Slots);
        Assert.Equal(0, result.WrittenSlots);
        Assert.Equal(0, result.WrittenFileCount);
        Assert.Contains("没有写出任何产物", result.Info);
    }

    /// <summary>有一条静态表修改：该槽位真的写出产物（路径可点开），其余槽位照旧给原因。</summary>
    [Fact]
    public async Task Run_reports_the_written_artifact_and_keeps_the_skipped_reasons()
    {
        await OpenProject("有改动");
        var entry = new StaticTableEntry("Assets/Resources_moved/static/test", "test", "TestClass",
            "test.json", "test.serialized", 12345, 64, true);
        _state.StaticEdits!.Set(entry.Key, entry, "{\"a\":1}", "{\"a\":2}");

        var response = await Run(Path.Combine(_root, "out-static"));
        Assert.True(response.Ok, response.Error?.Message);

        var result = response.Payload!.Value.Deserialize<ExportRunResponse>(IpcJson.Options)!;
        var written = result.Items.Single(item => item.Written);
        Assert.Equal(1, result.WrittenSlots);
        Assert.NotEmpty(written.OutputPaths);
        Assert.All(written.OutputPaths, path => Assert.True(File.Exists(path)));
        Assert.Empty(written.SkippedReasons);
        Assert.Equal(1, result.WrittenFileCount);
        // 其余槽位仍然逐条给原因，不会因为有一个写出就丢掉跳过清单
        Assert.Equal(result.Items.Count - 1, result.Skipped.Count);
        Assert.Contains("已写出", result.Info);
    }
}
