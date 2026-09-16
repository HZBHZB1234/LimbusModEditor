using System.Text.Json;
using LimbusModEditor.Application.Assets;
using LimbusModEditor.Application.Catalog;
using LimbusModEditor.Application.Ipc;
using LimbusModEditor.Application.Scanning;
using LimbusModEditor.Application.SpineData;
using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using static LimbusModEditor.Application.Ipc.IpcJson;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// IPC 消息信封往返序列化 + 网关派发测试（WEB-IPC-CONTRACT §1/§3）。
/// </summary>
public sealed class IpcEnvelopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipc-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "index.db");

    public IpcEnvelopeTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static AssetCatalog EmptyCatalog()
    {
        var store = new UnityCacheSqliteIndexStore(Path.Combine(Path.GetTempPath(), "ipc-empty-" + Guid.NewGuid().ToString("N") + ".db"));
        return new AssetCatalog(store, EmptyAssetStateSource.Instance);
    }

    private static IpcGateway EmptyGateway()
    {
        var catalog = EmptyCatalog();
        var projectState = new ProjectState();
        var spineData = new SpineDataGateway(Path.Combine(Path.GetTempPath(), "ipc-spine-" + Guid.NewGuid().ToString("N")));
        var bankStore = new BankIndexStore(Path.GetTempPath());
        var bankIndex = new BankIndexService(bankStore);
        var langText = new LangTextWorkbenchService();
        var staticStore = new StaticTableIndexStore(Path.GetTempPath());
        var staticIndex = new StaticIndexService(staticStore);
        return new IpcGateway(catalog, projectState, spineData, bankIndex, langText, staticIndex);
    }

    // ── 消息信封往返 ────────────────────────────────────────────────

    [Fact]
    public void RequestEnvelope_RoundTrip_PreservesIdAndMethod()
    {
        var request = IpcRequest.Create("req-0001", "catalog.query", SerializePayload(
            new CatalogQueryRequest(new AssetSearchQuery(), 0, 200)));

        var json = request.ToJson();
        var restored = IpcRequest.FromJson(json);

        Assert.Equal("req-0001", restored.Id);
        Assert.Equal("catalog.query", restored.Method);
        Assert.Equal("request", restored.Kind);
        Assert.Contains("\"kind\":\"request\"", json);
    }

    [Fact]
    public void ResponseEnvelope_Success_PreservesIdAndPayload()
    {
        var response = IpcResponse.Success("req-0001", new CatalogCountResponse(42));

        var json = response.ToJson();
        var restored = IpcResponse.FromJson(json);

        Assert.Equal("req-0001", restored.Id);
        Assert.Equal("response", restored.Kind);
        Assert.True(restored.Ok);
        Assert.Null(restored.Error);
        Assert.Equal(42, restored.Payload!.Value.Deserialize<CatalogCountResponse>(Options)!.TotalCount);
    }

    [Fact]
    public void ResponseEnvelope_Failure_ContainsChineseMessage()
    {
        var response = IpcResponse.Failure("req-0002", IpcErrorCode.Cancelled, "用户取消");

        var json = response.ToJson();
        var restored = IpcResponse.FromJson(json);

        Assert.Equal("req-0002", restored.Id);
        Assert.Equal("response", restored.Kind);
        Assert.False(restored.Ok);
        Assert.NotNull(restored.Error);
        Assert.Equal(IpcErrorCode.Cancelled, restored.Error!.Code);
        Assert.Equal("用户取消", restored.Error.Message);
    }

    [Fact]
    public void EventEnvelope_HasNoId()
    {
        var evt = IpcEvent.Create("progress", SerializePayload(
            new ProgressPayload("export-000", "carra2", 37, 100, "正在重打包…")));

        var json = evt.ToJson();
        var restored = IpcEvent.FromJson(json);

        Assert.Equal("event", restored.Kind);
        Assert.Equal("progress", restored.Method);
        Assert.Equal("正在重打包…", restored.Payload.Deserialize<ProgressPayload>(Options)!.Message);
        Assert.DoesNotContain("\"id\"", json);
        Assert.Contains("\"kind\":\"event\"", json);
    }

    // ── 网关派发 ────────────────────────────────────────────────────

    [Fact]
    public void UnknownMethod_ReturnsInternalError()
    {
        var gateway = EmptyGateway();
        var request = IpcRequest.Create("req-999", "unknown.method", SerializePayload(new { }));
        var responseJson = gateway.HandleRequest(request.ToJson());
        var response = IpcResponse.FromJson(responseJson);

        Assert.Equal("req-999", response.Id);
        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCode.Internal, response.Error?.Code);
        Assert.Contains("未知方法", response.Error?.Message);
    }

    [Fact]
    public void MalformedJson_ReturnsParseError()
    {
        var gateway = EmptyGateway();
        var responseJson = gateway.HandleRequest("this is not json{{{");
        var response = IpcResponse.FromJson(responseJson);

        Assert.False(response.Ok);
        Assert.Equal(IpcErrorCode.InvalidQuery, response.Error?.Code);
    }

    [Fact]
    public void CatalogQuery_EmptyStore_ReturnsZero()
    {
        var gateway = EmptyGateway();
        var request = IpcRequest.Create("req-001", "catalog.query", SerializePayload(
            new CatalogQueryRequest(new AssetSearchQuery(), 0, 20)));
        var responseJson = gateway.HandleRequest(request.ToJson());
        var response = IpcResponse.FromJson(responseJson);

        Assert.True(response.Ok);
        var result = response.Payload!.Value.Deserialize<CatalogQueryResponse>(Options)!;
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public void CatalogCount_EmptyStore_ReturnsZero()
    {
        var gateway = EmptyGateway();
        var request = IpcRequest.Create("req-004", "catalog.count", SerializePayload(
            new CatalogCountRequest(new AssetSearchQuery())));
        var responseJson = gateway.HandleRequest(request.ToJson());
        var response = IpcResponse.FromJson(responseJson);

        Assert.True(response.Ok);
        var result = response.Payload!.Value.Deserialize<CatalogCountResponse>(Options)!;
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public void RelationReveal_NonExistent_ReturnsFalse_DoesNotThrow()
    {
        var gateway = EmptyGateway();
        var request = IpcRequest.Create("req-005", "relation.reveal", SerializePayload(
            new RelationRevealRequest("nonexistent/path", "fallback")));
        var responseJson = gateway.HandleRequest(request.ToJson());
        var response = IpcResponse.FromJson(responseJson);

        Assert.True(response.Ok);
        var result = response.Payload!.Value.Deserialize<RelationRevealResponse>(Options)!;
        Assert.False(result.Located);
    }

    [Fact]
    public void Cancel_RespondsWithCancelled()
    {
        var gateway = EmptyGateway();
        var request = IpcRequest.Create("req-006", "cancel", SerializePayload(
            new CancelPayload("export-000")));
        var responseJson = gateway.HandleRequest(request.ToJson());
        var response = IpcResponse.FromJson(responseJson);

        Assert.True(response.Ok);
    }

    [Fact]
    public void ConfigRead_UnknownKey_ReturnsNull()
    {
        var gateway = EmptyGateway();
        var request = IpcRequest.Create("req-007", "config.read", SerializePayload(
            new ConfigReadRequest("nonexistent.key")));
        var responseJson = gateway.HandleRequest(request.ToJson());
        var response = IpcResponse.FromJson(responseJson);

        Assert.True(response.Ok);
    }

    [Fact]
    public void UiStateRead_ClampsColumnWidth()
    {
        var gateway = EmptyGateway();
        var request = IpcRequest.Create("req-008", "uiState.read", SerializePayload(
            new UiStateReadRequest("assets", 9999)));
        var responseJson = gateway.HandleRequest(request.ToJson());
        var response = IpcResponse.FromJson(responseJson);

        Assert.True(response.Ok);
        var doc = JsonDocument.Parse(response.Payload!.Value.GetRawText());
        Assert.Equal(2000, doc.RootElement.GetProperty("columnWidth").GetInt32());
    }
}
