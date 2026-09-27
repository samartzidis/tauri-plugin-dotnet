using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Tauri.Plugin.DotNet.Hosting;

namespace Tauri.Plugin.DotNet.Tests;

/// <summary>
/// Exercises the real native entry points by invoking them through their own function pointers,
/// exactly as the Rust host does, with test callbacks standing in for Rust. They share static state
/// (the installed dispatcher and the completion table), so they must not run in parallel.
/// </summary>
[Collection("NativeHost")]
public class NativeHostTests : IDisposable
{
    private static readonly ConcurrentDictionary<nint, TaskCompletionSource<string>> Completions = new();
    private static readonly ConcurrentQueue<(string? Label, string Json)> Events = new();
    private static long s_nextContext;

    [UnmanagedCallersOnly]
    private static unsafe void OnDone(nint context, byte* response, int length)
    {
        var text = Encoding.UTF8.GetString(response, length);
        if (Completions.TryRemove(context, out var completion))
            completion.SetResult(text);
    }

    [UnmanagedCallersOnly]
    private static unsafe void OnEmit(nint context, byte* label, int labelLength, byte* json, int jsonLength) =>
        Events.Enqueue((labelLength > 0 ? Encoding.UTF8.GetString(label, labelLength) : null, Encoding.UTF8.GetString(json, jsonLength)));

    public NativeHostTests() => Events.Clear();

    public void Dispose()
    {
        NativeHost.UseDispatcher(null);
        NativeHost.UseBackend(null);
    }

    /// <summary>Calls <c>NativeHost.Call</c> through its function pointer; the returned task is the native completion callback.</summary>
    private static unsafe Task<string> CallNative(string request, string? windowLabel = null)
    {
        var context = (nint)Interlocked.Increment(ref s_nextContext);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Completions[context] = completion;

        var label = windowLabel is null ? [] : Encoding.UTF8.GetBytes(windowLabel);
        var body = Encoding.UTF8.GetBytes(request);
        delegate* unmanaged<byte*, int, byte*, int, nint, delegate* unmanaged<nint, byte*, int, void>, void> call = &NativeHost.Call;
        fixed (byte* l = label)
        fixed (byte* b = body)
        {
            call(l, label.Length, b, body.Length, context, &OnDone);
        }

        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static unsafe void CancelNative(string callId)
    {
        var bytes = Encoding.UTF8.GetBytes(callId);
        delegate* unmanaged<byte*, int, void> cancel = &NativeHost.Cancel;
        fixed (byte* p = bytes)
        {
            cancel(p, bytes.Length);
        }
    }

    private static string Request(string method, string callId, params object[] args) =>
        JsonSerializer.Serialize(new { callId, method, args });

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // --- Call -------------------------------------------------------------

    [Fact]
    public async Task Call_delivers_the_response_through_the_completion_callback()
    {
        NativeHost.UseDispatcher(new BridgeDispatcher().RegisterService(new TestService()));

        var response = Parse(await CallNative(Request("TestService.Greet", "c1", "native")));

        Assert.Equal("c1", response.GetProperty("callId").GetString());
        Assert.Equal("Hello, native!", response.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Call_passes_the_window_label_to_the_service()
    {
        NativeHost.UseDispatcher(new BridgeDispatcher().RegisterService(new TestService()));

        var withLabel = Parse(await CallNative(Request("TestService.WhoAmI", "c1"), "settings"));
        var withoutLabel = Parse(await CallNative(Request("TestService.WhoAmI", "c2")));

        Assert.Equal("settings", withLabel.GetProperty("result").GetString());
        Assert.Equal("none", withoutLabel.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Call_round_trips_non_ascii_text()
    {
        NativeHost.UseDispatcher(new BridgeDispatcher().RegisterService(new TestService()));

        var response = Parse(await CallNative(Request("TestService.Greet", "c1", "Γειά σου 世界 🎉")));

        Assert.Equal("Hello, Γειά σου 世界 🎉!", response.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Call_reports_service_errors_in_the_response()
    {
        NativeHost.UseDispatcher(new BridgeDispatcher().RegisterService(new TestService()));

        var error = Parse(await CallNative(Request("TestService.Throws", "c1"))).GetProperty("error");

        Assert.Equal("boom", error.GetProperty("message").GetString());
        Assert.Equal("InvalidOperationException", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Call_before_the_backend_is_initialized_completes_with_an_error()
    {
        NativeHost.UseDispatcher(null);

        var error = Parse(await CallNative(Request("TestService.Greet", "c1", "x"))).GetProperty("error");

        Assert.Contains("has not been initialized", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Call_returns_before_a_slow_service_completes_and_cancel_reaches_it()
    {
        var service = new TestService();
        NativeHost.UseDispatcher(new BridgeDispatcher().RegisterService(service));

        var pending = CallNative(Request("TestService.Slow", "slow-1")); // returned: the call is only started
        await service.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);

        CancelNative("slow-1");

        var error = Parse(await pending).GetProperty("error");
        Assert.Equal("OperationCanceledException", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Concurrent_calls_each_complete_with_their_own_response()
    {
        NativeHost.UseDispatcher(new BridgeDispatcher().RegisterService(new TestService()));

        var calls = Enumerable.Range(0, 50).Select(i => CallNative(Request("TestService.Add", $"c{i}", i, 1000))).ToArray();
        var responses = await Task.WhenAll(calls);

        for (var i = 0; i < calls.Length; i++)
        {
            var response = Parse(responses[i]);
            Assert.Equal($"c{i}", response.GetProperty("callId").GetString());
            Assert.Equal(i + 1000, response.GetProperty("result").GetInt32());
        }
    }

    [Fact]
    public void Cancel_of_an_unknown_call_is_ignored()
    {
        NativeHost.UseDispatcher(new BridgeDispatcher().RegisterService(new TestService()));

        CancelNative("no-such-call");
        NativeHost.UseDispatcher(null);
        CancelNative("still-nothing");
    }

    // --- Initialize -------------------------------------------------------

    private static unsafe (int Result, string Message) InitializeNative(string assemblyPath, int errorCapacity = 512)
    {
        var path = Encoding.UTF8.GetBytes(assemblyPath);
        var error = new byte[errorCapacity];
        delegate* unmanaged<byte*, int, delegate* unmanaged<nint, byte*, int, byte*, int, void>, nint, byte*, int, int> initialize = &NativeHost.Initialize;
        fixed (byte* p = path)
        fixed (byte* e = error)
        {
            var result = initialize(p, path.Length, &OnEmit, 0, e, error.Length);
            return (result, Encoding.UTF8.GetString(error, 0, Math.Min(result, error.Length)));
        }
    }

    [Fact]
    public void Initialize_reports_a_missing_assembly_as_an_error_message()
    {
        var (result, message) = InitializeNative(Path.Combine(Path.GetTempPath(), "no-such-backend-" + Guid.NewGuid() + ".dll"));

        Assert.True(result > 0);
        Assert.Contains("no-such-backend", message);
    }

    [Fact]
    public void Initialize_reports_backend_discovery_errors()
    {
        // The test assembly deliberately contains several IBridgeBackend implementations.
        var (result, message) = InitializeNative(typeof(NativeHostTests).Assembly.Location);

        Assert.True(result > 0);
        Assert.Contains("more than one class implementing IBridgeBackend", message);
    }

    [Fact]
    public void Initialize_truncates_long_errors_to_the_buffer()
    {
        var (result, message) = InitializeNative(Path.Combine(Path.GetTempPath(), new string('x', 400) + ".dll"), errorCapacity: 40);

        Assert.Equal(40, result);
        Assert.Equal(40, Encoding.UTF8.GetByteCount(message));
    }

    // --- InitializeEmbedded (backend found by name among already loaded assemblies) ---

    private static unsafe (int Result, string Message) InitializeEmbeddedNative(string assemblyName, int errorCapacity = 512)
    {
        var name = Encoding.UTF8.GetBytes(assemblyName);
        var error = new byte[errorCapacity];
        delegate* unmanaged<byte*, int, delegate* unmanaged<nint, byte*, int, byte*, int, void>, nint, byte*, int, int> initialize = &NativeHost.InitializeEmbedded;
        fixed (byte* p = name)
        fixed (byte* e = error)
        {
            var result = initialize(p, name.Length, &OnEmit, 0, e, error.Length);
            return (result, Encoding.UTF8.GetString(error, 0, Math.Min(result, error.Length)));
        }
    }

    [Fact]
    public void InitializeEmbedded_reports_an_assembly_that_is_not_loaded()
    {
        var (result, message) = InitializeEmbeddedNative("No.Such.Backend");

        Assert.True(result > 0);
        Assert.Contains("'No.Such.Backend' is not loaded", message);
    }

    [Fact]
    public void InitializeEmbedded_finds_the_assembly_by_name_ignoring_case_and_reports_discovery_errors()
    {
        // The test assembly deliberately contains several IBridgeBackend implementations.
        var (result, message) = InitializeEmbeddedNative("tauri.plugin.dotnet.TESTS");

        Assert.True(result > 0);
        Assert.Contains("more than one class implementing IBridgeBackend", message);
    }

    [Fact]
    public async Task InitializeEmbedded_starts_a_backend_that_was_loaded_from_memory()
    {
        NativeHost.UseDispatcher(null);
        var assemblyName = "EmbeddedTestBackend" + Guid.NewGuid().ToString("N");
        DefineAssemblyWithOneBackend(assemblyName);

        var (result, message) = InitializeEmbeddedNative(assemblyName);

        Assert.True(result == 0, message);
        var response = Parse(await CallNative(Request("Missing.Method", "e1")));
        // The dispatcher is installed: the call reaches it and fails there, not with "not initialized".
        Assert.DoesNotContain("has not been initialized", response.GetProperty("error").GetProperty("message").GetString());
        Assert.Contains("Service 'Missing' not found", response.GetProperty("error").GetProperty("message").GetString());
    }

    /// <summary>An in-memory assembly with exactly one <see cref="IBridgeBackend"/>, like an embedded backend.</summary>
    private static void DefineAssemblyWithOneBackend(string assemblyName)
    {
        var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new System.Reflection.AssemblyName(assemblyName), System.Reflection.Emit.AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule(assemblyName).DefineType(
            "OnlyBackend", System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Class, typeof(object), [typeof(IBridgeBackend)]);

        type.DefineDefaultConstructor(System.Reflection.MethodAttributes.Public);

        var configure = type.DefineMethod(
            nameof(IBridgeBackend.Configure),
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Virtual | System.Reflection.MethodAttributes.Final | System.Reflection.MethodAttributes.HideBySig | System.Reflection.MethodAttributes.NewSlot,
            typeof(void),
            [typeof(BridgeDispatcher)]);
        configure.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ret);
        type.DefineMethodOverride(configure, typeof(IBridgeBackend).GetMethod(nameof(IBridgeBackend.Configure))!);

        type.CreateType();
    }

    [Fact]
    public async Task A_failed_initialize_leaves_the_host_uninitialized()
    {
        NativeHost.UseDispatcher(null);

        InitializeNative(Path.Combine(Path.GetTempPath(), "no-such-backend.dll"));

        var response = Parse(await CallNative(Request("TestService.Greet", "c1", "x")));
        Assert.Contains("has not been initialized", response.GetProperty("error").GetProperty("message").GetString());
    }

    // --- Events -----------------------------------------------------------

    [Fact]
    public unsafe void Events_reach_the_native_callback_with_and_without_a_window_label()
    {
        var sink = new NativeEventSink((nint)(delegate* unmanaged<nint, byte*, int, byte*, int, void>)&OnEmit, 0);
        var dispatcher = new BridgeDispatcher(sink);

        dispatcher.Emit("progress", new { Percent = 50 });
        dispatcher.EmitTo("settings", "closed");

        Assert.Equal(2, Events.Count);
        Events.TryDequeue(out var broadcast);
        Events.TryDequeue(out var targeted);
        Assert.Null(broadcast.Label);
        Assert.Equal("""{"event":"progress","data":{"percent":50}}""", broadcast.Json);
        Assert.Equal("settings", targeted.Label);
        Assert.Equal("""{"event":"closed"}""", targeted.Json);
    }

    [Fact]
    public unsafe void Events_round_trip_non_ascii_text()
    {
        var sink = new NativeEventSink((nint)(delegate* unmanaged<nint, byte*, int, byte*, int, void>)&OnEmit, 0);

        new BridgeDispatcher(sink).EmitTo("παράθυρο", "msg", new { Text = "世界 🎉" });

        Events.TryDequeue(out var evt);
        Assert.Equal("παράθυρο", evt.Label);
        Assert.Equal("世界 🎉", JsonDocument.Parse(evt.Json).RootElement.GetProperty("data").GetProperty("text").GetString());
    }

    // --- GetVersion / Shutdown ---------------------------------------------

    private static unsafe int GetVersionNative()
    {
        delegate* unmanaged<int> version = &NativeHost.GetVersion;
        return version();
    }

    private static unsafe int ShutdownNative(int timeoutMilliseconds)
    {
        delegate* unmanaged<int, int> shutdown = &NativeHost.Shutdown;
        return shutdown(timeoutMilliseconds);
    }

    /// <summary>Records what the services and the backend hook did, in order.</summary>
    private sealed class Steps
    {
        private readonly ConcurrentQueue<string> _steps = new();
        public void Add(string step) => _steps.Enqueue(step);
        public string[] All => _steps.ToArray();
    }

    [BridgeService]
    private sealed class Closable(Steps steps) : IDisposable
    {
        public string Ping() => "pong";
        public void Dispose() => steps.Add("service disposed");
    }

    /// <summary>A service whose Dispose blocks, and cannot be interrupted, until released.</summary>
    [BridgeService]
    private sealed class Stuck(ManualResetEventSlim release) : IDisposable
    {
        public string Ping() => "pong";
        public void Dispose() => release.Wait();
    }

    private sealed class HookBackend(Steps steps, bool throws = false) : IBridgeBackend
    {
        public void Configure(BridgeDispatcher dispatcher) { }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            steps.Add("backend hook");
            return throws ? throw new InvalidOperationException("hook failed") : Task.CompletedTask;
        }
    }

    [Fact]
    public void GetVersion_returns_the_packages_own_version_packed_for_the_plugin_crate()
    {
        var own = typeof(NativeHost).Assembly.GetName().Version!;

        var packed = GetVersionNative();

        Assert.Equal(own.Major, packed >> 20);
        Assert.Equal(own.Minor, (packed >> 10) & 1023);
        Assert.Equal(Math.Max(own.Build, 0), packed & 1023);
    }

    [Theory]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("0.2.7", 0, 2, 7)]
    [InlineData("1.23.456", 1, 23, 456)]
    [InlineData("1023.1023.1023", 1023, 1023, 1023)]
    public void PackVersion_keeps_major_minor_and_patch_apart(string version, int major, int minor, int patch)
    {
        var packed = NativeHost.PackVersion(Version.Parse(version));

        Assert.Equal((major, minor, patch), (packed >> 20, (packed >> 10) & 1023, packed & 1023));
        Assert.True(packed >= 0);
    }

    [Fact]
    public void PackVersion_treats_a_missing_patch_number_as_zero_and_ignores_the_fourth_number()
    {
        Assert.Equal(NativeHost.PackVersion(new Version(0, 3, 0)), NativeHost.PackVersion(new Version(0, 3)));
        Assert.Equal(NativeHost.PackVersion(new Version(0, 3, 5)), NativeHost.PackVersion(new Version(0, 3, 5, 99)));
    }

    [Fact]
    public void PackVersion_reports_what_does_not_fit_or_is_missing_as_unknown()
    {
        Assert.Equal(NativeHost.UnknownVersion, NativeHost.PackVersion(null));
        Assert.Equal(NativeHost.UnknownVersion, NativeHost.PackVersion(new Version(1024, 0, 0)));
        Assert.Equal(NativeHost.UnknownVersion, NativeHost.PackVersion(new Version(0, 1024, 0)));
        Assert.Equal(NativeHost.UnknownVersion, NativeHost.PackVersion(new Version(0, 0, 1024)));
    }

    [Fact]
    public void The_crate_and_the_package_have_the_same_major_and_minor_version()
    {
        // The crate refuses a package whose major or minor differs, so a release that ships them apart would not start.
        // Cargo.toml and the package's csproj are where each side takes its version from.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Cargo.toml")))
            root = root.Parent;
        Assert.True(root != null, "Cargo.toml was not found above the test assembly");

        var cargo = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(Path.Combine(root!.FullName, "Cargo.toml")), @"(?m)^version\s*=\s*""([^""]+)""");
        var csproj = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(Path.Combine(root.FullName, "dotnet", "src", "Tauri.Plugin.DotNet", "Tauri.Plugin.DotNet.csproj")),
            @"<Version>([^<]+)</Version>");
        Assert.True(cargo.Success && csproj.Success, "could not read a version from Cargo.toml or the csproj");

        static (string Major, string Minor) MajorMinor(string version)
        {
            var parts = version.Split('.', '-', '+');
            return (parts[0], parts[1]);
        }

        Assert.Equal(MajorMinor(cargo.Groups[1].Value), MajorMinor(csproj.Groups[1].Value));
    }

    [Fact]
    public void Shutdown_disposes_the_services_and_then_runs_the_backend_hook()
    {
        var steps = new Steps();
        NativeHost.UseDispatcher(new BridgeDispatcher().RegisterService(new Closable(steps)));
        NativeHost.UseBackend(new HookBackend(steps));

        var result = ShutdownNative(5000);

        Assert.Equal(NativeHost.ShutdownCompleted, result);
        Assert.Equal(new[] { "service disposed", "backend hook" }, steps.All);
    }

    [Fact]
    public async Task Calls_after_Shutdown_are_rejected_with_HostStopped()
    {
        NativeHost.UseDispatcher(new BridgeDispatcher().RegisterService(new Closable(new Steps())));
        ShutdownNative(5000);

        var response = Parse(await CallNative(Request("Closable.Ping", "c1")));

        Assert.Equal("HostStopped", response.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public void A_second_Shutdown_does_nothing()
    {
        var steps = new Steps();
        NativeHost.UseDispatcher(new BridgeDispatcher().RegisterService(new Closable(steps)));
        NativeHost.UseBackend(new HookBackend(steps));

        Assert.Equal(NativeHost.ShutdownCompleted, ShutdownNative(5000));
        Assert.Equal(NativeHost.ShutdownCompleted, ShutdownNative(5000));

        Assert.Equal(new[] { "service disposed", "backend hook" }, steps.All);
    }

    [Fact]
    public void Shutdown_before_the_backend_started_is_harmless()
        => Assert.Equal(NativeHost.ShutdownCompleted, ShutdownNative(1000));

    [Fact]
    public void Shutdown_reports_a_failing_backend_hook()
    {
        var steps = new Steps();
        NativeHost.UseDispatcher(new BridgeDispatcher().RegisterService(new Closable(steps)));
        NativeHost.UseBackend(new HookBackend(steps, throws: true));

        Assert.Equal(NativeHost.ShutdownFailed, ShutdownNative(5000));
        // The services were disposed before the hook failed
        Assert.Equal(new[] { "service disposed", "backend hook" }, steps.All);
    }

    [Fact]
    public void Shutdown_gives_up_at_the_limit_even_when_a_Dispose_cannot_be_interrupted()
    {
        using var release = new ManualResetEventSlim();
        NativeHost.UseDispatcher(new BridgeDispatcher().RegisterService(new Stuck(release)));
        var timer = System.Diagnostics.Stopwatch.StartNew();

        int result;
        try
        {
            result = ShutdownNative(300);
        }
        finally
        {
            release.Set(); // let the blocked pool thread go
        }

        Assert.Equal(NativeHost.ShutdownTimedOut, result);
        // The limit plus the grace period, not forever
        Assert.InRange(timer.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
    }
}
