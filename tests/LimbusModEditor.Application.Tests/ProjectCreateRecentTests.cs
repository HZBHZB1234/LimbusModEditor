using System.Text.Json;
using LimbusModEditor.Application.AppConfig;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Ipc;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using static LimbusModEditor.Application.Ipc.IpcJson;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// project.create / project.recent 的 IPC 层契约测试。
///
/// <para>覆盖三件事：<b>新建即落盘且置为当前项目</b>、<b>同名冲突报中文错误
/// 且不覆盖</b>、<b>最近项目读的是共享配置里真有文件的条目（没有就空列表 + 中文说明）</b>。</para>
/// </summary>
public sealed class ProjectCreateRecentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipc-proj-" + Guid.NewGuid().ToString("N"));
    private readonly AppEnvironment _environment;
    private readonly IpcGateway _gateway;

    public ProjectCreateRecentTests()
    {
        Directory.CreateDirectory(_root);
        _environment = new AppEnvironment(_root);
        var catalog = new AssetCatalog(
            new UnityCacheSqliteIndexStore(Path.Combine(_root, "index.db")),
            EmptyAssetStateSource.Instance);
        _gateway = new IpcGateway(catalog, new ProjectState(),
            new SpineDataGateway(Path.Combine(_root, "spine")),
            new BankIndexService(new BankIndexStore(_root)),
            new LangTextWorkbenchService(),
            new StaticIndexService(new StaticTableIndexStore(_root)),
            environment: _environment);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { /* 测试结束，尽力清理 */ }
    }

    private async Task<IpcResponse> Call(string id, string method, object payload)
    {
        var json = await _gateway.HandleRequestAsync(IpcRequest.Create(id, method, SerializePayload(payload)).ToJson());
        return IpcResponse.FromJson(json);
    }

    private static T Payload<T>(IpcResponse response) where T : class
        => response.Payload!.Value.Deserialize<T>(IpcJson.Options)!;

    /// <summary>新建：项目文件真的落在盘上，且当前项目被置为它（用 project.save 反证）。</summary>
    [Fact]
    public async Task Create_writes_the_project_file_and_makes_it_current()
    {
        var dir = Path.Combine(_root, "mods", "甲");
        var response = await Call("req-proj-1", "project.create", new ProjectCreateRequest(dir, "测试模组"));
        Assert.True(response.Ok, response.Error?.Message);

        var created = Payload<ProjectCreateResponse>(response);
        Assert.Equal("测试模组", created.Name);
        Assert.Equal(Path.Combine(dir, "测试模组.lmeproj"), created.Path);
        Assert.True(File.Exists(created.Path));
        // CreateAsync 会在项目根建 sources/workspace/… 子目录
        Assert.True(Directory.Exists(Path.Combine(dir, "sources")));

        // 置为当前项目：project.save 不再报「请先打开项目」
        var save = await Call("req-proj-1-save", "project.save", new ProjectSaveRequest(created.Path));
        Assert.True(save.Ok, save.Error?.Message);
    }

    /// <summary>同名再建一次：中文冲突错误，且原文件字节不动（不覆盖）。</summary>
    [Fact]
    public async Task Create_refuses_to_overwrite_an_existing_project()
    {
        var dir = Path.Combine(_root, "mods", "乙");
        var first = await Call("req-proj-2", "project.create", new ProjectCreateRequest(dir, "同名"));
        Assert.True(first.Ok, first.Error?.Message);
        var file = Payload<ProjectCreateResponse>(first).Path;
        var before = await File.ReadAllBytesAsync(file);

        var second = await Call("req-proj-3", "project.create", new ProjectCreateRequest(dir, "同名"));

        Assert.False(second.Ok);
        Assert.Equal(IpcErrorCode.InvalidQuery, second.Error!.Code);
        Assert.Contains("同名项目已存在", second.Error!.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
    }

    /// <summary>最近项目：新建过的项目出现在列表里，且文件确实存在。</summary>
    [Fact]
    public async Task Recent_lists_the_project_that_was_just_created()
    {
        await Call("req-proj-4", "project.create",
            new ProjectCreateRequest(Path.Combine(_root, "mods", "丙"), "最近测试"));

        var response = await Call("req-proj-5", "project.recent", new { });
        Assert.True(response.Ok, response.Error?.Message);

        var recent = Payload<ProjectRecentResponse>(response);
        Assert.Contains(recent.Projects, p => p.Name == "最近测试");
        Assert.All(recent.Projects, p => Assert.True(File.Exists(p.Path)));
        Assert.Contains("最近项目", recent.Info);
    }

    /// <summary>一条记录都没有 → 空列表 + 中文说明（不编造记录）。</summary>
    [Fact]
    public async Task Recent_with_nothing_recorded_returns_an_empty_list_and_a_chinese_note()
    {
        var response = await Call("req-proj-6", "project.recent", new { });

        Assert.True(response.Ok, response.Error?.Message);
        var recent = Payload<ProjectRecentResponse>(response);
        Assert.Empty(recent.Projects);
        Assert.Contains("还没有最近项目", recent.Info);
    }
}
