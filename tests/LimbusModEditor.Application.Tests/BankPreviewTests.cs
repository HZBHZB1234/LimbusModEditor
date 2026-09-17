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
/// bank.preview 的 IPC 层契约测试。
///
/// <para>覆盖三件事：<b>缺参数给中文错误</b>、<b>解不出地址时是 Unsupported + 中文原因
/// （不是空地址、不是 Internal）</b>。真实解码链路（FSB → FMOD → WAV）由
/// WikiMediaResolver 承担，它的音频分支已在维基语音试听里验证。</para>
/// </summary>
public sealed class BankPreviewTests
{
    private static IpcGateway Gateway()
    {
        var catalog = new AssetCatalog(
            new UnityCacheSqliteIndexStore(Path.Combine(Path.GetTempPath(), "ipc-bank-" + Guid.NewGuid().ToString("N") + ".db")),
            EmptyAssetStateSource.Instance);
        return new IpcGateway(catalog, new ProjectState(),
            new SpineDataGateway(Path.Combine(Path.GetTempPath(), "ipc-bank-spine-" + Guid.NewGuid().ToString("N"))),
            new BankIndexService(new BankIndexStore(Path.GetTempPath())),
            new LangTextWorkbenchService(),
            new StaticIndexService(new StaticTableIndexStore(Path.GetTempPath())));
    }

    [Fact]
    public async Task Preview_without_a_bank_reports_a_chinese_reason()
    {
        var gateway = Gateway();

        var json = await gateway.HandleRequestAsync(IpcRequest.Create("req-bank-1", "bank.preview",
            SerializePayload(new BankPreviewRequest(" ", "sample-01"))).ToJson());
        var response = IpcResponse.FromJson(json);

        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCode.InvalidQuery, response.Error!.Code);
        Assert.Contains("缺少 bank", response.Error!.Message);
    }

    [Fact]
    public async Task Preview_without_a_sample_name_reports_a_chinese_reason()
    {
        var gateway = Gateway();

        var json = await gateway.HandleRequestAsync(IpcRequest.Create("req-bank-2", "bank.preview",
            SerializePayload(new BankPreviewRequest("some.bank", " "))).ToJson());
        var response = IpcResponse.FromJson(json);

        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCode.InvalidQuery, response.Error!.Code);
        Assert.Contains("缺少样本名", response.Error!.Message);
    }

    /// <summary>索引里没有这条样本（或 FMOD 不可用）→ Unsupported + 中文原因，
    /// 不给空地址、不抛 Internal。</summary>
    [Fact]
    public async Task Preview_of_an_unknown_sample_fails_unsupported_with_a_chinese_reason()
    {
        var gateway = Gateway();

        var json = await gateway.HandleRequestAsync(IpcRequest.Create("req-bank-3", "bank.preview",
            SerializePayload(new BankPreviewRequest("missing.bank", "no-such-sample"))).ToJson());
        var response = IpcResponse.FromJson(json);

        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCode.Unsupported, response.Error!.Code);
        Assert.Contains("可播放地址", response.Error!.Message);
    }
}
