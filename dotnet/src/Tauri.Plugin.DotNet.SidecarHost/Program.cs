using System.Reflection;
using Tauri.Plugin.DotNet.Hosting;

namespace Tauri.Plugin.DotNet.SidecarHost;

/// <summary>
/// Runs one .NET backend as a dev-only sidecar process for <c>tauri-plugin-dotnet</c>: connects back
/// to the Rust plugin over a local socket named on the command line, loads the backend the same way
/// the in-process host does (<see cref="NativeHost.CreateDispatcher"/>), and serves calls, cancellations
/// and events over that connection until told to shut down. See <c>src/sidecar.rs</c> for the Rust side
/// of this protocol.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        // Connect first: everything up to here only touches types in this assembly (CliOptions,
        // SidecarPipe), so it is safe regardless of whether the backend path is even valid.
        await using var pipe = await SidecarPipe.ConnectAsync(options.PipeName, CancellationToken.None).ConfigureAwait(false);

        try
        {
            // Must happen before any type from the backend or from Tauri.Plugin.DotNet is touched - and
            // critically, before the JIT even prepares a method that mentions one, since preparing a
            // method resolves the types of ALL of its locals up front regardless of which branch runs.
            // RunAsync (below) declares locals typed BridgeDispatcher/IBridgeBackend, so it must not be
            // JITted, and is therefore not even called, until this succeeds; Main itself must stay free
            // of any such reference so this failure can still be reported over the now-open pipe instead
            // of crashing before a connection exists (AssemblyDependencyResolver throws for a bad path).
            BackendAssemblyResolver.Install(options.BackendPath);
        }
        catch (Exception ex)
        {
            // `await using` flushes this over the pipe (and only then disposes it) before returning.
            pipe.Enqueue(new ErrorEnvelope(ex.Message, ex.GetType().Name));
            return 1;
        }

        return await RunAsync(pipe, options).ConfigureAwait(false);
    }

    private static async Task<int> RunAsync(SidecarPipe pipe, CliOptions options)
    {
        BridgeDispatcher dispatcher;
        IBridgeBackend backend;
        try
        {
            // options.BackendPath is a private copy the Rust side made (src/shadow.rs) before
            // spawning this process, not the original build output - so loading it by path, same as
            // NativeHost does for the in-process host, is safe: nothing needs to overwrite THIS file
            // while it is open, only the original, untouched by this process. Loading by path also
            // keeps Assembly.Location meaningful, which is what lets a native dependency resolve at
            // all (see BackendAssemblyResolver) and lets a debugger's source paths bind normally.
            var assembly = Assembly.LoadFrom(options.BackendPath);
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
