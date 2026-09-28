using Tauri.Plugin.DotNet;

namespace SidecarFixtureBackend;

/// <summary>A minimal real backend, built and referenced exactly like a consumer's would be, used to
/// exercise the dev-only sidecar host end to end (see <c>sidecar::tests::real_process_*</c> in
/// <c>src/sidecar.rs</c>).</summary>
public sealed class Backend : IBridgeBackend
{
    public void Configure(BridgeDispatcher dispatcher) => dispatcher.RegisterService(new EchoService());
}

[BridgeService]
public sealed class EchoService
{
    public Task<string> Echo(string text) => Task.FromResult(text);
}
