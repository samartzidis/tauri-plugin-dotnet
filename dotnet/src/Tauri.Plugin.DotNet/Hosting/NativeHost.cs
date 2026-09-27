using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Tauri.Plugin.DotNet.Hosting;

/// <summary>
/// The native entry points the Tauri plugin's in-process host binds to through hostfxr.
/// Strings cross the boundary as UTF-8 pointer + byte length and are copied on both sides, so
/// neither side ever frees the other's memory. Nothing here may throw: an exception escaping an
/// <see cref="UnmanagedCallersOnlyAttribute"/> method terminates the process.
/// </summary>
public static class NativeHost
{
    /// <summary>
    /// What <see cref="GetVersion"/> returns when the version cannot be packed into an int (a component above
    /// <see cref="MaxVersionComponent"/>, or no version at all). The crate reports it as an unreadable version.
    /// </summary>
    internal const int UnknownVersion = -1;

    /// <summary>The largest major, minor or patch number <see cref="PackVersion"/> can carry: 10 bits each, 30 in all.</summary>
    internal const int MaxVersionComponent = 1023;

    /// <summary>What <see cref="Shutdown"/> returns: everything was stopped and disposed.</summary>
    internal const int ShutdownCompleted = 0;

    /// <summary>What <see cref="Shutdown"/> returns: the time ran out before it was done.</summary>
    internal const int ShutdownTimedOut = 1;

    /// <summary>What <see cref="Shutdown"/> returns: it failed (details are in the backend's log).</summary>
    internal const int ShutdownFailed = 2;

    // Time given, on top of the timeout, to a shutdown that is stuck in code that ignores its cancellation token
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromMilliseconds(250);

    private static BridgeDispatcher? s_dispatcher;
    private static IBridgeBackend? s_backend;
    private static int s_shutdownStarted;

    /// <summary>
    /// The version of this package, packed as <c>major &lt;&lt; 20 | minor &lt;&lt; 10 | patch</c> (see <see cref="PackVersion"/>). The
    /// first entry point the plugin calls. The crate compares the major and minor number with its own and refuses to start
    /// the backend when they differ, so a crate and a package that do not belong together fail with a clear message instead
    /// of an obscure one; the patch number is free. That makes the version the interface: a change to the entry points, their
    /// signatures or the JSON both sides exchange needs a new minor version (while the major version is 0, as semver says).
    /// </summary>
    [UnmanagedCallersOnly]
    public static int GetVersion() => PackVersion(typeof(NativeHost).Assembly.GetName().Version);

    /// <summary>Packs a version into one int the way <see cref="GetVersion"/> reports it: 10 bits each for major, minor and patch.</summary>
    /// <returns>The packed version, or <see cref="UnknownVersion"/> when a number does not fit.</returns>
    internal static int PackVersion(Version? version)
    {
        if (version is null) return UnknownVersion;

        // An assembly version without a third number reports it as -1: that is patch 0
        var (major, minor, patch) = (version.Major, version.Minor, Math.Max(version.Build, 0));
        if (major is < 0 or > MaxVersionComponent || minor is < 0 or > MaxVersionComponent || patch > MaxVersionComponent)
            return UnknownVersion;

        return (major << 20) | (minor << 10) | patch;
    }

    /// <summary>
    /// Loads the backend and configures it. Returns 0 on success; otherwise the number of bytes
    /// of an error message written to <paramref name="error"/>.
    /// </summary>
    /// <param name="assemblyPath">Path of the backend assembly.</param>
    /// <param name="emit">Native callback that delivers events to the frontend.</param>
    /// <param name="emitContext">Opaque value passed back to <paramref name="emit"/>.</param>
    [UnmanagedCallersOnly]
    public static unsafe int Initialize(
        byte* assemblyPath, int assemblyPathLength,
        delegate* unmanaged<nint, byte*, int, byte*, int, void> emit, nint emitContext,
        byte* error, int errorCapacity)
    {
        try
        {
            var path = ReadText(assemblyPath, assemblyPathLength);

            // The plugin loaded the backend through this same load context, so this returns the
            // already-loaded assembly instead of loading a second copy.
            var assembly = CurrentContext().LoadFromAssemblyPath(path);

            return Start(assembly, emit, emitContext);
        }
        catch (Exception ex)
        {
            return WriteError($"{ex.GetType().Name}: {ex.Message}", error, errorCapacity);
        }
    }

    /// <summary>
    /// The same as <see cref="Initialize"/> for a backend the plugin embedded in the executable:
    /// its assemblies were already loaded from memory, so the backend is found by its assembly
    /// name (for example <c>MyApp.Backend</c>) instead of being loaded from a path.
    /// </summary>
    [UnmanagedCallersOnly]
    public static unsafe int InitializeEmbedded(
        byte* assemblyName, int assemblyNameLength,
        delegate* unmanaged<nint, byte*, int, byte*, int, void> emit, nint emitContext,
        byte* error, int errorCapacity)
    {
        try
        {
            var assembly = FindLoadedAssembly(ReadText(assemblyName, assemblyNameLength));

            return Start(assembly, emit, emitContext);
        }
        catch (Exception ex)
        {
            return WriteError($"{ex.GetType().Name}: {ex.Message}", error, errorCapacity);
        }
    }

    private static unsafe int Start(Assembly assembly, delegate* unmanaged<nint, byte*, int, byte*, int, void> emit, nint emitContext)
    {
        var backend = BackendDiscovery.CreateBackend(assembly);
        s_dispatcher = CreateDispatcher(backend, new NativeEventSink((nint)emit, emitContext));
        s_backend = backend;
        return 0;
    }

    private static AssemblyLoadContext CurrentContext() =>
        AssemblyLoadContext.GetLoadContext(typeof(NativeHost).Assembly) ?? AssemblyLoadContext.Default;

    /// <summary>Finds an assembly the plugin already loaded from memory, by its simple name.</summary>
    internal static Assembly FindLoadedAssembly(string name) =>
        CurrentContext().Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"The backend assembly '{name}' is not loaded. The embedded bundle must contain it, and the bundle's backend name must match its assembly name.");

    /// <summary>Creates the dispatcher with the backend's logger, then lets the backend register its services.</summary>
    internal static BridgeDispatcher CreateDispatcher(IBridgeBackend backend, IEventSink eventSink)
    {
        var dispatcher = new BridgeDispatcher(eventSink, backend.LoggerFactory?.CreateLogger<BridgeDispatcher>());
        backend.Configure(dispatcher);
        return dispatcher;
    }

    /// <summary>
    /// Starts a call and returns immediately. <paramref name="done"/> is invoked exactly once,
    /// from a thread-pool thread, with the serialized response.
    /// </summary>
    /// <param name="context">Opaque value passed back to <paramref name="done"/>.</param>
    [UnmanagedCallersOnly]
    public static unsafe void Call(
        byte* windowLabel, int windowLabelLength,
        byte* request, int requestLength,
        nint context, delegate* unmanaged<nint, byte*, int, void> done)
    {
        string? label;
        string requestJson;
        try
        {
            label = windowLabelLength > 0 ? ReadText(windowLabel, windowLabelLength) : null;
            requestJson = ReadText(request, requestLength);
        }
        catch (Exception ex)
        {
            Complete((nint)done, context, ErrorJson(ex));
            return;
        }

        // Off the calling thread, so a service that blocks before its first await cannot stall the caller.
        var doneAddress = (nint)done;
        _ = Task.Run(() => RunCallAsync(label, requestJson, context, doneAddress));
    }

    /// <summary>Requests cancellation of an in-flight call. Unknown call ids are ignored.</summary>
    [UnmanagedCallersOnly]
    public static unsafe void Cancel(byte* callId, int callIdLength)
    {
        try
        {
            s_dispatcher?.Cancel(ReadText(callId, callIdLength));
        }
        catch
        {
            // A cancel that cannot be delivered has nothing to report to.
        }
    }

    /// <summary>
    /// Stops the backend for the end of the app: the dispatcher rejects new calls, cancels the ones in flight and disposes the
    /// services, then the backend's own <see cref="IBridgeBackend.ShutdownAsync"/> runs. Blocks until that is done or
    /// <paramref name="timeoutMilliseconds"/> has passed, whichever comes first, and never throws. Safe to call more than once.
    /// </summary>
    /// <returns><c>0</c> when it completed, <c>1</c> when the time ran out, <c>2</c> when it failed.</returns>
    [UnmanagedCallersOnly]
    public static int Shutdown(int timeoutMilliseconds)
    {
        try
        {
            var timeout = TimeSpan.FromMilliseconds(Math.Max(timeoutMilliseconds, 0));

            // Run on a pool thread: a Dispose that blocks and never returns cannot hold the caller past the limit, since
            // the caller only waits for that thread for as long as it may
            var work = Task.Run(() => ShutdownAsync(timeout));
            return work.Wait(timeout + ShutdownGrace) ? work.Result : ShutdownTimedOut;
        }
        catch
        {
            return ShutdownFailed;
        }
    }

    internal static async Task<int> ShutdownAsync(TimeSpan timeout)
    {
        var dispatcher = s_dispatcher;
        if (dispatcher == null || Interlocked.Exchange(ref s_shutdownStarted, 1) == 1)
            return ShutdownCompleted;

        var backend = s_backend;
        var log = backend?.LoggerFactory?.CreateLogger(typeof(NativeHost).FullName!);

        using var limit = new CancellationTokenSource(timeout);
        try
        {
            await dispatcher.ShutdownAsync(limit.Token).ConfigureAwait(false);
            if (backend != null)
                await backend.ShutdownAsync(limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
            return ShutdownCompleted;
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            log?.LogWarning("Shutdown did not finish within {Milliseconds} ms", (int)timeout.TotalMilliseconds);
            return ShutdownTimedOut;
        }
        catch (Exception ex)
        {
            log?.LogError(ex, "Shutdown failed");
            return ShutdownFailed;
        }
    }

    /// <summary>Test hook: installs a dispatcher without going through <see cref="Initialize"/>.</summary>
    internal static void UseDispatcher(BridgeDispatcher? dispatcher)
    {
        s_dispatcher = dispatcher;
        s_shutdownStarted = 0;
    }

    /// <summary>Test hook: installs the backend whose <see cref="IBridgeBackend.ShutdownAsync"/> <see cref="Shutdown"/> calls.</summary>
    internal static void UseBackend(IBridgeBackend? backend) => s_backend = backend;

    // Async methods cannot be unsafe or take pointers, so the callback travels as an address.
    private static async Task RunCallAsync(string? windowLabel, string requestJson, nint context, nint done)
    {
        string response;
        try
        {
            var dispatcher = s_dispatcher
                ?? throw new InvalidOperationException("The .NET backend has not been initialized.");
            response = await dispatcher.InvokeJsonAsync(requestJson, windowLabel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            response = ErrorJson(ex);
        }

        Complete(done, context, response);
    }

    private static unsafe void Complete(nint done, nint context, string responseJson)
    {
        var bytes = Encoding.UTF8.GetBytes(responseJson);
        fixed (byte* p = bytes)
        {
            ((delegate* unmanaged<nint, byte*, int, void>)done)(context, p, bytes.Length);
        }
    }

    private static string ErrorJson(Exception ex) =>
        JsonSerializer.Serialize(new BridgeResponse
        {
            Error = new BridgeError { Message = ex.Message, Type = ex.GetType().Name }
        }, BridgeDispatcher.JsonOptions);

    private static unsafe string ReadText(byte* text, int length) =>
        length <= 0 ? "" : Encoding.UTF8.GetString(text, length);

    /// <summary>Writes as much of <paramref name="message"/> as fits; returns the bytes written (at least 1).</summary>
    private static unsafe int WriteError(string message, byte* buffer, int capacity)
    {
        var bytes = Encoding.UTF8.GetBytes(message.Length == 0 ? "unknown error" : message);
        var count = Math.Min(bytes.Length, Math.Max(capacity, 0));
        for (var i = 0; i < count; i++)
            buffer[i] = bytes[i];
        return Math.Max(count, 1);
    }
}

/// <summary>Sends events to the frontend through the native callback given to <see cref="NativeHost.Initialize"/>.</summary>
internal sealed unsafe class NativeEventSink(nint emit, nint context) : IEventSink
{
    public void Send(string? windowLabel, string eventJson)
    {
        var label = windowLabel is null ? [] : Encoding.UTF8.GetBytes(windowLabel);
        var json = Encoding.UTF8.GetBytes(eventJson);
        fixed (byte* l = label)
        fixed (byte* j = json)
        {
            ((delegate* unmanaged<nint, byte*, int, byte*, int, void>)emit)(context, l, label.Length, j, json.Length);
        }
    }
}
