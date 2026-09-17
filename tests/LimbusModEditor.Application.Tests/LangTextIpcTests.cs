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
/// lang.fileEntries / lang.applyPatch 的 IPC 层契约测试。
///
/// <para>覆盖两件事：<b>前提缺失给中文错误</b>（没开项目不能改文本），以及
/// 前端历史名 <c>text.*</c> 与 <c>lang.*</c> 指向同一处理器（不再「未知方法」）。</para>
/// </summary>
public sealed class LangTextIpcTests
{
    private static IpcGateway Gateway()
    {
        var catalog = new AssetCatalog(
            new UnityCacheSqliteIndexStore(Path.Combine(Path.GetTempPath(), "ipc-lang-" + Guid.NewGuid().ToString("N") + ".db")),
            EmptyAssetStateSource.Instance);
        return new IpcGateway(catalog, new ProjectState(),
            new SpineDataGateway(Path.Combine(Path.GetTempPath(), "ipc-lang-spine-" + Guid.NewGuid().ToString("N"))),
            new BankIndexService(new BankIndexStore(Path.GetTempPath())),
            new LangTextWorkbenchService(),
            new StaticIndexService(new StaticTableIndexStore(Path.GetTempPath())));
    }

    [Fact]
    public async Task Apply_patch_without_a_project_reports_a_chinese_reason()
    {
        var gateway = Gateway();

        var json = await gateway.HandleRequestAsync(IpcRequest.Create("req-lang-1", "lang.applyPatch",
            SerializePayload(new LangApplyPatchRequest("StoryData/S1.json",
                [new LangKeyEditDto("greeting", "再见")]))).ToJson());
        var response = IpcResponse.FromJson(json);

        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCode.InvalidQuery, response.Error!.Code);
        Assert.Contains("请先打开项目", response.Error!.Message);
    }

    [Fact]
    public async Task Apply_patch_with_an_empty_edit_list_reports_a_chinese_reason()
    {
        var gateway = Gateway();

        var json = await gateway.HandleRequestAsync(IpcRequest.Create("req-lang-2", "lang.applyPatch",
            SerializePayload(new LangApplyPatchRequest("StoryData/S1.json", []))).ToJson());
        var response = IpcResponse.FromJson(json);

        Assert.False(response.Ok);
        Assert.Contains("没有要应用的改动", response.Error!.Message);
    }

    [Theory]
    [InlineData("lang.fileEntries")]
    [InlineData("text.fileEntries")]
    public async Task File_entries_rejects_a_missing_path_under_both_method_names(string method)
    {
        var gateway = Gateway();

        var json = await gateway.HandleRequestAsync(IpcRequest.Create("req-lang-3-" + method, method,
            SerializePayload(new LangFileEntriesRequest(" "))).ToJson());
        var response = IpcResponse.FromJson(json);

        // 「未知方法」会走 Internal；这里必须是 InvalidQuery，说明别名确实派发到了同一个处理器。
        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCode.InvalidQuery, response.Error!.Code);
        Assert.Contains("缺少文件路径", response.Error!.Message);
    }
}
