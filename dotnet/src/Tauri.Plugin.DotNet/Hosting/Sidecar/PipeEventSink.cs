namespace Tauri.Plugin.DotNet.Hosting.Sidecar;

/// <summary>Forwards .NET-to-frontend events to the Rust plugin over the sidecar pipe.</summary>
internal sealed class PipeEventSink(SidecarPipe pipe) : IEventSink
{
    public void Send(string? windowLabel, string eventJson) => pipe.Enqueue(new EventEnvelope(windowLabel, eventJson));
}
