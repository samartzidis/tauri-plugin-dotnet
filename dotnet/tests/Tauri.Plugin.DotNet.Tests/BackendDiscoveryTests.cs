using Tauri.Plugin.DotNet.Hosting;

namespace Tauri.Plugin.DotNet.Tests;

public class FirstBackend : IBridgeBackend
{
    public static int Configured;
    public void Configure(BridgeDispatcher dispatcher) => Configured++;
}

public class SecondBackend : IBridgeBackend
{
    public void Configure(BridgeDispatcher dispatcher) { }
}

internal sealed class InternalBackend : IBridgeBackend
{
    internal InternalBackend() { }
    public void Configure(BridgeDispatcher dispatcher) => dispatcher.RegisterService(new OriginalName());
}

public class BackendWithoutDefaultConstructor(string unused) : IBridgeBackend
{
    public string Unused { get; } = unused;
    public void Configure(BridgeDispatcher dispatcher) { }
}

public abstract class AbstractBackend : IBridgeBackend
{
    public abstract void Configure(BridgeDispatcher dispatcher);
}

public class BackendDiscoveryTests
{
    private static IBridgeBackend Create(params Type[] types) => BackendDiscovery.CreateBackend(types, "TestAssembly");

    [Fact]
    public void Creates_the_single_backend()
    {
        var backend = Create(typeof(FirstBackend), typeof(string), typeof(BridgeDispatcher));

        Assert.IsType<FirstBackend>(backend);
    }

    [Fact]
    public async Task Creates_a_non_public_backend_with_a_non_public_constructor()
    {
        var dispatcher = new BridgeDispatcher();

        Create(typeof(InternalBackend)).Configure(dispatcher);

        var response = await dispatcher.InvokeAsync(new BridgeRequest { CallId = "c", Method = "Renamed.Ping" });
        Assert.Equal("pong", response.Result);
    }

    [Fact]
    public void Ignores_abstract_classes_and_interfaces()
    {
        var backend = Create(typeof(AbstractBackend), typeof(IBridgeBackend), typeof(FirstBackend));

        Assert.IsType<FirstBackend>(backend);
    }

    [Fact]
    public void Fails_when_there_is_no_backend()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create(typeof(string), typeof(AbstractBackend)));

        Assert.Contains("no class implementing IBridgeBackend", ex.Message);
        Assert.Contains("TestAssembly", ex.Message);
    }

    [Fact]
    public void Fails_when_there_are_several_backends_and_names_them()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create(typeof(FirstBackend), typeof(SecondBackend)));

        Assert.Contains("more than one", ex.Message);
        Assert.Contains(nameof(FirstBackend), ex.Message);
        Assert.Contains(nameof(SecondBackend), ex.Message);
    }

    [Fact]
    public void Fails_when_the_backend_has_no_parameterless_constructor()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create(typeof(BackendWithoutDefaultConstructor)));

        Assert.Contains("parameterless constructor", ex.Message);
    }
}
