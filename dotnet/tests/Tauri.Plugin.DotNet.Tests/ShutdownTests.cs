using System.Collections.Concurrent;
using System.Text.Json;

namespace Tauri.Plugin.DotNet.Tests;

/// <summary>What happens when the app exits: the dispatcher stops taking calls, cancels the ones in flight and disposes the services.</summary>
public class ShutdownTests
{
    /// <summary>What the services below did, in order.</summary>
    private sealed class Log
    {
        private readonly ConcurrentQueue<string> _entries = new();
        public void Add(string entry) => _entries.Enqueue(entry);
        public string[] Entries => _entries.ToArray();
    }

    [BridgeService]
    private sealed class Alpha(Log log) : IDisposable
    {
        public string Ping() => "alpha";
        public void Dispose() => log.Add("dispose:Alpha");
    }

    [BridgeService]
    private sealed class Beta(Log log) : IAsyncDisposable
    {
        public string Ping() => "beta";

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            log.Add("dispose:Beta");
        }
    }

    /// <summary>Not disposable at all.</summary>
    [BridgeService]
    private sealed class Gamma
    {
        public string Ping() => "gamma";
    }

    [BridgeService]
    private sealed class Faulty(Log log) : IDisposable
    {
        public string Ping() => "faulty";

        public void Dispose()
        {
            log.Add("dispose:Faulty");
            throw new InvalidOperationException("boom");
        }
    }

    [BridgeService]
    private sealed class Both(Log log) : IDisposable, IAsyncDisposable
    {
        public string Ping() => "both";
        public void Dispose() => log.Add("Dispose");

        public ValueTask DisposeAsync()
        {
            log.Add("DisposeAsync");
            return ValueTask.CompletedTask;
        }
    }

    [BridgeService]
    private sealed class Hangs : IAsyncDisposable
    {
        public string Ping() => "hangs";
        public ValueTask DisposeAsync() => new(Task.Delay(Timeout.Infinite));
    }

    [BridgeService]
    private sealed class Slow(Log log) : IDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> Work(CancellationToken cancellationToken)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return "unreachable";
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(50); // a call takes a moment to wind down
                log.Add("call-cancelled");
                throw;
            }
        }

        public void Dispose() => log.Add("dispose:Slow");
    }

    /// <summary>A call that does not look at its cancellation token.</summary>
    [BridgeService]
    private sealed class Stubborn(Log log, TaskCompletionSource release) : IDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> Work()
        {
            Started.SetResult();
            await release.Task;
            return "done";
        }

        public void Dispose() => log.Add("dispose:Stubborn");
    }

    private sealed class SilentSink : IEventSink
    {
        public void Send(string? windowLabel, string eventJson) { }
    }

    private static string Ping(string service) => $$"""{"callId":"c-{{Guid.NewGuid():N}}","method":"{{service}}.Ping","args":[]}""";

    // --- disposing --------------------------------------------------------

    [Fact]
    public async Task Disposes_the_services_in_the_reverse_of_the_order_they_were_registered()
    {
        var log = new Log();
        var dispatcher = new BridgeDispatcher()
            .RegisterService(new Alpha(log))
            .RegisterService(new Beta(log))
            .RegisterService(new Gamma());

        await dispatcher.ShutdownAsync();

        // Gamma has nothing to dispose; Beta was registered after Alpha, so it goes first
        Assert.Equal(new[] { "dispose:Beta", "dispose:Alpha" }, log.Entries);
    }

    [Fact]
    public async Task A_service_that_throws_while_being_disposed_does_not_keep_the_others_from_it()
    {
        var log = new Log();
        var dispatcher = new BridgeDispatcher()
            .RegisterService(new Alpha(log))
            .RegisterService(new Faulty(log))
            .RegisterService(new Beta(log));

        await dispatcher.ShutdownAsync(); // does not throw

        Assert.Equal(new[] { "dispose:Beta", "dispose:Faulty", "dispose:Alpha" }, log.Entries);
    }

    [Fact]
    public async Task Uses_DisposeAsync_and_not_Dispose_for_a_service_that_has_both()
    {
        var log = new Log();
        var dispatcher = new BridgeDispatcher().RegisterService(new Both(log));

        await dispatcher.ShutdownAsync();

        Assert.Equal(new[] { "DisposeAsync" }, log.Entries);
    }

    [Fact]
    public async Task Disposes_an_instance_registered_twice_once()
    {
        var log = new Log();
        var alpha = new Alpha(log);
        var dispatcher = new BridgeDispatcher().RegisterService(alpha).RegisterService(alpha);

        await dispatcher.ShutdownAsync();

        Assert.Equal(new[] { "dispose:Alpha" }, log.Entries);
    }

    [Fact]
    public async Task Shutdown_is_idempotent_and_the_second_call_returns_the_first_task()
    {
        var log = new Log();
        var dispatcher = new BridgeDispatcher().RegisterService(new Alpha(log));

        var first = dispatcher.ShutdownAsync();
        var second = dispatcher.ShutdownAsync();
        await Task.WhenAll(first, second);

        Assert.Same(first, second);
        Assert.Equal(new[] { "dispose:Alpha" }, log.Entries);
    }

    [Fact]
    public async Task Shutdown_of_a_dispatcher_with_no_services_completes()
        => await new BridgeDispatcher().ShutdownAsync();

    // --- calls ------------------------------------------------------------

    [Fact]
    public async Task Rejects_calls_that_arrive_after_shutdown_began()
    {
        var dispatcher = new BridgeDispatcher().RegisterService(new Alpha(new Log()));
        Assert.Contains("alpha", await dispatcher.InvokeJsonAsync(Ping("Alpha")));

        await dispatcher.ShutdownAsync();

        using var response = JsonDocument.Parse(await dispatcher.InvokeJsonAsync(Ping("Alpha")));
        var error = response.RootElement.GetProperty("error");
        Assert.Equal("HostStopped", error.GetProperty("type").GetString());
        Assert.Contains("shutting down", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Cancels_calls_in_flight_and_waits_for_them_before_disposing_their_service()
    {
        var log = new Log();
        var slow = new Slow(log);
        var dispatcher = new BridgeDispatcher().RegisterService(slow);
        var call = dispatcher.InvokeJsonAsync("""{"callId":"c1","method":"Slow.Work","args":[]}""");
        await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await dispatcher.ShutdownAsync();

        // The call saw its token fire and finished winding down before the service was disposed
        Assert.Equal(new[] { "call-cancelled", "dispose:Slow" }, log.Entries);
        using var response = JsonDocument.Parse(await call);
        Assert.Equal("OperationCanceledException", response.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Disposes_the_services_anyway_when_a_call_ignores_its_cancellation_token()
    {
        var log = new Log();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stubborn = new Stubborn(log, release);
        var dispatcher = new BridgeDispatcher().RegisterService(stubborn);
        dispatcher.DrainTimeout = TimeSpan.FromMilliseconds(100);
        var call = dispatcher.InvokeJsonAsync("""{"callId":"c1","method":"Stubborn.Work","args":[]}""");
        await stubborn.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await dispatcher.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // The process is ending: flushing the service matters more than waiting for a call that will not stop
        Assert.Equal(new[] { "dispose:Stubborn" }, log.Entries);
        release.SetResult();
        await call;
    }

    // --- time limit -------------------------------------------------------

    [Fact]
    public async Task Is_cancelled_when_the_time_runs_out_during_a_disposal_that_never_ends()
    {
        var dispatcher = new BridgeDispatcher().RegisterService(new Hangs());
        using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.ShutdownAsync(limit.Token));
    }

    // --- frontend calls ---------------------------------------------------

    [Fact]
    public async Task Frontend_calls_still_waiting_for_an_answer_are_abandoned()
    {
        var dispatcher = new BridgeDispatcher(new SilentSink());
        var pending = dispatcher.GetFrontend<IFrontend>("main").Notify("hello"); // never answered

        await dispatcher.ShutdownAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("shutting down", ex.Message);
    }

    [Fact]
    public async Task A_frontend_call_made_after_shutdown_began_fails_at_once()
    {
        var dispatcher = new BridgeDispatcher(new SilentSink());
        var frontend = dispatcher.GetFrontend<IFrontend>("main");
        await dispatcher.ShutdownAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => frontend.Notify("late").WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("shutting down", ex.Message);
    }

    // --- the backend hook -------------------------------------------------

    private sealed class DefaultBackend : IBridgeBackend
    {
        public void Configure(BridgeDispatcher dispatcher) { }
    }

    [Fact]
    public async Task The_backend_hook_does_nothing_by_default()
        => await ((IBridgeBackend)new DefaultBackend()).ShutdownAsync(CancellationToken.None);
}
