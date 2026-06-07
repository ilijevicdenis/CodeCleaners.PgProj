using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PgProj.Lsp.Protocol;

/// <summary>
/// The canonical serializer for the LSP/JSON-RPC wire. LSP property names are already camelCase, so we
/// pin the policy explicitly (rather than rely on a default) and omit nulls so optional members drop off
/// the wire. Kept separate from the Core <c>JsonContract</c> options because the LSP wire is its own
/// (external) protocol surface — the two must be free to evolve independently.
/// </summary>
public static class LspJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(JsonNode? node) =>
        node is null ? default : node.Deserialize<T>(Options);
}

/// <summary>
/// A single JSON-RPC 2.0 message, parsed loosely so one shape covers requests (have <c>id</c> + <c>method</c>),
/// responses (have <c>id</c> + <c>result</c>/<c>error</c>) and notifications (have <c>method</c>, no <c>id</c>).
/// <see cref="Params"/> is left as a <see cref="JsonNode"/> so a handler decodes it into the concrete LSP DTO
/// only when it knows the method — the transport stays method-agnostic.
/// </summary>
public sealed class JsonRpcMessage
{
    public string JsonRpc { get; init; } = "2.0";

    /// <summary>Request/response correlation id. Null for a notification. Number or string on the wire.</summary>
    public JsonNode? Id { get; init; }

    /// <summary>The method name for a request/notification; null for a response.</summary>
    public string? Method { get; init; }

    /// <summary>The params payload (object or array), undecoded; null when the message carries none.</summary>
    public JsonNode? Params { get; init; }

    public JsonNode? Result { get; init; }
    public JsonRpcError? Error { get; init; }

    /// <summary>True for a result-bearing response (so a null result still serializes as <c>"result":null</c>,
    /// which the JSON-RPC spec requires — a response must carry exactly one of <c>result</c>/<c>error</c>).</summary>
    public bool IsResponse { get; init; }

    public bool IsRequest => Method is not null && Id is not null;
    public bool IsNotification => Method is not null && Id is null;

    public static JsonRpcMessage Request(JsonNode id, string method, object? @params = null) => new()
    {
        Id = id,
        Method = method,
        Params = @params is null ? null : JsonSerializer.SerializeToNode(@params, LspJson.Options),
    };

    public static JsonRpcMessage Notification(string method, object? @params = null) => new()
    {
        Method = method,
        Params = @params is null ? null : JsonSerializer.SerializeToNode(@params, LspJson.Options),
    };

    public static JsonRpcMessage ResultFor(JsonNode? id, object? result) => new()
    {
        Id = id,
        IsResponse = true,
        Result = result is null ? null : JsonSerializer.SerializeToNode(result, LspJson.Options),
    };

    public static JsonRpcMessage ErrorFor(JsonNode? id, int code, string message) => new()
    {
        Id = id,
        Error = new JsonRpcError { Code = code, Message = message },
    };

    /// <summary>Serialize this message to its on-the-wire JSON object (camelCase, omit-null).</summary>
    public string ToJson()
    {
        var obj = new JsonObject { ["jsonrpc"] = "2.0" };
        if (Id is not null) obj["id"] = Id.DeepClone();
        if (Method is not null) obj["method"] = Method;
        if (Params is not null) obj["params"] = Params.DeepClone();
        if (Result is not null) obj["result"] = Result.DeepClone();
        else if (IsResponse && Error is null) obj["result"] = null; // explicit JSON null per the spec
        if (Error is not null) obj["error"] = JsonSerializer.SerializeToNode(Error, LspJson.Options);
        return obj.ToJsonString(LspJson.Options);
    }

    /// <summary>Parse one on-the-wire JSON object into a loose message (no method dispatch yet).</summary>
    public static JsonRpcMessage FromJson(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject
                   ?? throw new JsonException("JSON-RPC payload was not a JSON object.");
        return new JsonRpcMessage
        {
            JsonRpc = node["jsonrpc"]?.GetValue<string>() ?? "2.0",
            Id = node["id"]?.DeepClone(),
            Method = node["method"]?.GetValue<string>(),
            Params = node["params"]?.DeepClone(),
            Result = node["result"]?.DeepClone(),
            Error = node["error"] is { } e ? e.Deserialize<JsonRpcError>(LspJson.Options) : null,
        };
    }
}

/// <summary>A JSON-RPC 2.0 error object. <see cref="LspErrorCodes"/> defines the codes we emit.</summary>
public sealed class JsonRpcError
{
    public int Code { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>The JSON-RPC / LSP error codes this server uses.</summary>
public static class LspErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int ServerNotInitialized = -32002;
}
