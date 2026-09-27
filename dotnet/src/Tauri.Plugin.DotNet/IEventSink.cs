namespace Tauri.Plugin.DotNet;

/// <summary>
/// Delivers .NET-to-frontend events. Implemented by the host adapter that connects the
/// dispatcher to the Tauri plugin (in-process or sidecar).
/// </summary>
public interface IEventSink
{
    /// <summary>
    /// Sends one event message to the frontend.
    /// </summary>
    /// <param name="windowLabel">Target webview label, or null to broadcast to all webviews.</param>
    /// <param name="eventJson">The serialized <c>{ "event": name, "data": payload }</c> message.</param>
    void Send(string? windowLabel, string eventJson);
}
