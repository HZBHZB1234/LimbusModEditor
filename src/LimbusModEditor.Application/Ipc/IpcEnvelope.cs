using System.Text.Json;
using System.Text.Json.Serialization;

namespace LimbusModEditor.Application.Ipc;

/// <summary>
/// IPC 消息信封（WEB-IPC-CONTRACT §1 冻结）：请求/响应/事件三向。
/// 所有消息都是 JSON，外层信封统一为 {id, kind, method, payload}。
/// 传输层（PostWebMessageAsJson / WebMessageReceived）在 App 宿主侧，本类无 WPF 依赖。
/// </summary>
public static class IpcContractVersion
{
    public const string V1 = "v1";
}

/// <summary>错误码（闭集合，新增只能追加，WEB-IPC-CONTRACT §3.1）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IpcErrorCode
{
    Cancelled,
    NotFound,
    InvalidQuery,
    IoError,
    Unsupported,
    Internal
}

/// <summary>请求消息（前端 → 宿主）。kind 固定为 "request"。</summary>
public sealed record IpcRequest(
    string Id,
    string Kind,
    string Method,
    JsonElement Payload)
{
    public string ToJson() => JsonSerializer.Serialize(this, IpcJson.Options);

    public static IpcRequest FromJson(string json) =>
        JsonSerializer.Deserialize<IpcRequest>(json, IpcJson.Options)
        ?? throw new ArgumentException("无法解析 IPC 请求。", nameof(json));

    public static IpcRequest Create(string id, string method, JsonElement payload) =>
        new(id, "request", method, payload);
}

/// <summary>响应消息（宿主 → 前端，id 与请求对应）。kind 固定为 "response"。</summary>
public sealed record IpcResponse(
    string Id,
    string Kind,
    bool Ok,
    JsonElement? Payload,
    IpcError? Error)
{
    public string ToJson() => JsonSerializer.Serialize(this, IpcJson.Options);

    public static IpcResponse FromJson(string json) =>
        JsonSerializer.Deserialize<IpcResponse>(json, IpcJson.Options)
        ?? throw new ArgumentException("无法解析 IPC 响应。", nameof(json));

    public static IpcResponse Success(string id, object payload) =>
        new(id, "response", true, IpcJson.SerializePayload(payload), null);

    public static IpcResponse Failure(string id, IpcErrorCode code, string message) =>
        new(id, "response", false, null, new IpcError(code, message));
}

/// <summary>事件消息（宿主 → 前端推送，无 id，可多次）。kind 固定为 "event"。</summary>
public sealed record IpcEvent(
    string Kind,
    string Method,
    JsonElement Payload)
{
    public string ToJson() => JsonSerializer.Serialize(this, IpcJson.Options);

    public static IpcEvent FromJson(string json) =>
        JsonSerializer.Deserialize<IpcEvent>(json, IpcJson.Options)
        ?? throw new ArgumentException("无法解析 IPC 事件。", nameof(json));

    public static IpcEvent Create(string method, JsonElement payload) =>
        new("event", method, payload);
}

/// <summary>错误信息（ok:false 时附带）。</summary>
public sealed record IpcError(
    IpcErrorCode Code,
    string Message);

/// <summary>取消请求载荷（WEB-IPC-CONTRACT §3.2）。</summary>
public sealed record CancelPayload(string OperationId);

/// <summary>进度事件载荷（WEB-IPC-CONTRACT §4）。</summary>
public sealed record ProgressPayload(
    string OperationId,
    string Phase,
    int Current,
    int Total,
    string Message);

/// <summary>session.hello 事件载荷（WEB-IPC-CONTRACT §7）。</summary>
public sealed record SessionHelloPayload(
    string ContractVersion,
    string AppVersion,
    string RuntimeVersion);

internal static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static JsonElement SerializePayload(object payload)
    {
        var json = JsonSerializer.Serialize(payload, Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
