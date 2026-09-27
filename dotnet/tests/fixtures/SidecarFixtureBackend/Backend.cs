using Tauri.Plugin.DotNet;

namespace SidecarFixtureBackend;

/// <summary>A minimal real backend, built and loaded exactly like a consumer's would be, used to
/// exercise <c>Tauri.Plugin.DotNet.SidecarHost</c> end to end (see <c>SidecarProcessTests</c>).</summary>
public sealed class Backend : IBridgeBackend
{
    public void Configure(BridgeDispatcher dispatcher) => dispatcher.RegisterService(new EchoService());
}

[BridgeService]
public sealed class EchoService
{
    public Task<string> Echo(string text) => Task.FromResult(text);
}
