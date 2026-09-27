namespace Tauri.Plugin.DotNet;

/// <summary>
/// Call-scoped context passed to bridge service methods when declared as a parameter.
/// It identifies the webview that issued the current call.
/// </summary>
/// <example>
/// <code>
/// public string Where(CallContext ctx) => ctx.WindowLabel ?? "unknown";
/// </code>
/// </example>
public sealed class CallContext
{
    /// <summary>
    /// The Tauri label of the webview that issued the current call, or null if unknown.
    /// Pass it to <see cref="BridgeDispatcher.EmitTo"/> to reply to that webview only.
    /// </summary>
    public string? WindowLabel { get; }

    internal CallContext(string? windowLabel)
    {
        WindowLabel = windowLabel;
    }
}
