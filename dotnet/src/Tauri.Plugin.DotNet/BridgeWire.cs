using System.Text.Json;

namespace Tauri.Plugin.DotNet;

/// <summary>A call from the frontend: <c>{ callId, method: "Service.Method", args: [...] }</c>.</summary>
public sealed class BridgeRequest
{
    public string CallId { get; set; } = "";
    public string Method { get; set; } = "";
    public JsonElement[]? Args { get; set; }
}

/// <summary>
/// The outcome of a call. Exactly one of <see cref="Result"/> or <see cref="Error"/> is meaningful;
/// a successful call with no return value has neither.
/// </summary>
public sealed class BridgeResponse
{
    public string CallId { get; set; } = "";
    public object? Result { get; set; }
    public BridgeError? Error { get; set; }
}

public sealed class BridgeError
{
    public string Message { get; set; } = "";

    /// <summary>The exception type name; the frontend exposes it as <c>Error.name</c>.</summary>
    public string Type { get; set; } = "";
}

internal sealed class BridgeEventMessage
{
    public string Event { get; set; } = "";
    public object? Data { get; set; }
}
