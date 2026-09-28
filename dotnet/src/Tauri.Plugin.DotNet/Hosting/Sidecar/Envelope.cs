using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tauri.Plugin.DotNet.Hosting.Sidecar;

/// <summary>
/// One message on the sidecar's local socket. Mirrors the Rust plugin's <c>Envelope</c>
/// (<c>src/sidecar.rs</c>) exactly - both sides must agree on the "kind" discriminator and the
/// camelCase field names. The <c>Json</c> fields carry the existing, unchanged bridge wire format
/// (<see cref="BridgeRequest"/>/<see cref="BridgeResponse"/>/event JSON) as an opaque string; only
/// this envelope is new.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ReadyEnvelope), "ready")]
[JsonDerivedType(typeof(ErrorEnvelope), "error")]
[JsonDerivedType(typeof(CallEnvelope), "call")]
[JsonDerivedType(typeof(ResponseEnvelope), "response")]
[JsonDerivedType(typeof(CancelEnvelope), "cancel")]
[JsonDerivedType(typeof(EventEnvelope), "event")]
[JsonDerivedType(typeof(ShutdownEnvelope), "shutdown")]
internal abstract record Envelope
{
    /// <summary>Shared by every (de)serialization of an <see cref="Envelope"/> on this side of the pipe.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <summary>Sent once the backend has loaded and the dispatcher is ready to take calls.</summary>
internal sealed record ReadyEnvelope : Envelope;

/// <summary>Sent instead of <see cref="ReadyEnvelope"/> when the backend failed to load; the sidecar exits right after.</summary>
internal sealed record ErrorEnvelope(string Message, string Type) : Envelope;

/// <summary>One call to dispatch. <see cref="Json"/> is the verbatim <c>BridgeRequest</c> JSON.</summary>
internal sealed record CallEnvelope(string CallId, string WindowLabel, string Json) : Envelope;

/// <summary><see cref="Json"/> is the verbatim <c>BridgeResponse</c> JSON for the call named by <see cref="CallId"/>.</summary>
internal sealed record ResponseEnvelope(string CallId, string Json) : Envelope;

/// <summary>Requests cancellation of the call named by <see cref="CallId"/>.</summary>
internal sealed record CancelEnvelope(string CallId) : Envelope;

/// <summary><see cref="Json"/> is the verbatim <c>{ "event", "data" }</c> JSON pushed to the frontend.</summary>
internal sealed record EventEnvelope(string? WindowLabel, string Json) : Envelope;

/// <summary>Tells the sidecar to stop taking calls, dispose its services and exit.</summary>
internal sealed record ShutdownEnvelope(int TimeoutMs) : Envelope;
