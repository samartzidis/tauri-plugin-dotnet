using System.Reflection;

namespace Tauri.Plugin.DotNet.Hosting.Sidecar;

/// <summary>
/// Entry point for the generated <c>dotnet watch</c> runner project (see <c>src/sidecar.rs</c>'s
/// wrapper generation): connects back to the Rust plugin over a local pipe named on the command
/// line, loads the backend - referenced at compile time by the generated project, not by path or by
/// bytes - and serves calls, cancellations and events until the connection ends.
/// </summary>
/// <remarks>
/// Unlike the older path-based sidecar-runner tool, the backend here is an ordinary compile-time
/// <c>ProjectReference</c> of the generated wrapper project, so <see cref="Assembly.Load(string)"/> by
/// simple name resolves it (and its own dependencies) through the wrapper's normal
/// <c>deps.json</c>-driven probing - no <c>Assembly.LoadFrom</c>, no custom
/// <see cref="AssemblyLoadContext"/> resolver, and none of the JIT-ordering care that dynamic loading
/// by path needed.
/// </remarks>
public static class SidecarRunner
{
    /// <summary>
    /// Parses <c>--pipe &lt;name&gt;</c> from <paramref name="args"/>, connects, and runs until the
    /// pipe closes. Returns a process exit code (0 on a clean shutdown, 1 if the backend failed to load).
    /// </summary>
    public static async Task<int> RunAsync(string[] args, string backendAssemblyName)
    {
        string? pipeName = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pipe" && i + 1 < args.Length)
                pipeName = args[++i];
        }

        if (string.IsNullOrEmpty(pipeName))
        {
            Console.Error.WriteLine("Missing required --pipe <name> argument.");
            return 1;
        }

        await using var pipe = await SidecarPipe.ConnectAsync(pipeName, CancellationToken.None).ConfigureAwait(false);

        BridgeDispatcher dispatcher;
        IBridgeBackend backend;
        try
        {
            var assembly = Assembly.Load(backendAssemblyName);
            backend = BackendDiscovery.CreateBackend(assembly);
            dispatcher = NativeHost.CreateDispatcher(backend, new PipeEventSink(pipe));
        }
        catch (Exception ex)
        {
            pipe.Enqueue(new ErrorEnvelope(ex.Message, ex.GetType().Name));
            return 1;
        }

        pipe.Enqueue(new ReadyEnvelope());
        await RunLoopAsync(pipe, dispatcher, backend).ConfigureAwait(false);
        return 0;
    }

    private static async Task RunLoopAsync(SidecarPipe pipe, BridgeDispatcher dispatcher, IBridgeBackend backend)
    {
        await foreach (var envelope in pipe.ReadAllAsync().ConfigureAwait(false))
        {
            switch (envelope)
            {
                case CallEnvelope call:
                    // Fire-and-forget, like NativeHost.Call: a slow service must not block the next
                    // frame from being read.
                    _ = HandleCallAsync(pipe, dispatcher, call);
                    break;
                case CancelEnvelope cancel:
                    dispatcher.Cancel(cancel.CallId);
                    break;
                case ShutdownEnvelope shutdown:
                    await ShutdownAsync(dispatcher, backend, TimeSpan.FromMilliseconds(shutdown.TimeoutMs)).ConfigureAwait(false);
                    return;
                default:
                    // Ready/Error/Response are only ever sent by this program, never received.
                    break;
            }
        }
    }

    private static async Task HandleCallAsync(SidecarPipe pipe, BridgeDispatcher dispatcher, CallEnvelope call)
    {
        var responseJson = await dispatcher.InvokeJsonAsync(call.Json, call.WindowLabel).ConfigureAwait(false);
        pipe.Enqueue(new ResponseEnvelope(call.CallId, responseJson));
    }

    /// <summary>Mirrors <see cref="NativeHost.ShutdownAsync"/>: stop taking calls, dispose services, then the backend's own hook.</summary>
    private static async Task ShutdownAsync(BridgeDispatcher dispatcher, IBridgeBackend backend, TimeSpan timeout)
    {
        using var limit = new CancellationTokenSource(timeout);
        try
        {
            await dispatcher.ShutdownAsync(limit.Token).ConfigureAwait(false);
            await backend.ShutdownAsync(limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch
        {
            // Best effort on the way out - the process exits right after this either way.
        }
    }
}
