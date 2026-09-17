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
/// config.autoDetect 的 IPC 层契约测试。
///
/// <para>本机的探测结果不可控（有的机器装了游戏，有的没装），所以钉住的是
/// <b>不变量</b>：所有非 null 的目录必须真的存在（不返回编出来的路径），
/// 并且 info 一定给中文说明。探不到的项必须是 null。</para>
/// </summary>
public sealed class ConfigAutoDetectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipc-detect-" + Guid.NewGuid().ToString("N"));

    public ConfigAutoDetectTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { /* 尽力清理 */ }
    }

    [Fact]
    public async Task Detect_returns_only_directories_that_really_exist()
    {
        var environment = new AppEnvironment(_root);
        var catalog = new AssetCatalog(
            new UnityCacheSqliteIndexStore(Path.Combine(_root, "index.db")),
            EmptyAssetStateSource.Instance);
        var gateway = new IpcGateway(catalog, new ProjectState(),
            new SpineDataGateway(Path.Combine(_root, "spine")),
            new BankIndexService(new BankIndexStore(_root)),
            new LangTextWorkbenchService(),
            new StaticIndexService(new StaticTableIndexStore(_root)),
            environment: environment);

        var json = await gateway.HandleRequestAsync(IpcRequest.Create("req-detect-1", "config.autoDetect",
            SerializePayload(new { })).ToJson());
        var response = IpcResponse.FromJson(json);
        Assert.True(response.Ok, response.Error?.Message);

        // 显式带上 IPC 的序列化选项（camelCase 命名策略），别依赖默认大小写规则
        var detected = response.Payload!.Value.Deserialize<ConfigAutoDetectResponse>(IpcJson.Options)!;
        foreach (var directory in new[] { detected.GameDirectory, detected.UnityCacheDirectory, detected.ModDirectory })
        {
            if (directory is null) continue;
            Assert.True(Directory.Exists(directory), $"探测给了不存在的目录：{directory}");
        }
        if (detected.FmodLibraryDirectory is { } fmod) Assert.True(Directory.Exists(fmod), fmod);
        Assert.False(string.IsNullOrWhiteSpace(detected.Info));
    }
}
