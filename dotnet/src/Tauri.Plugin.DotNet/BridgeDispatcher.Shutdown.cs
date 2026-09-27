using Microsoft.Extensions.Logging;

namespace Tauri.Plugin.DotNet;

public sealed partial class BridgeDispatcher
{
    /// <summary>The error type of a response to a call that arrived after shutdown began. The Rust host uses the same name.</summary>
    internal const string StoppedErrorType = "HostStopped";

    private const string ShuttingDownMessage = "The backend is shutting down.";

    // The services in the order they were registered, for disposing them the other way round
    private readonly List<object> _serviceInstances = new();
    private readonly object _shutdownLock = new();
    private volatile bool _shuttingDown;
    private Task? _shutdown;

    /// <summary>
    /// How long shutdown waits for calls that were told to cancel before it disposes the services anyway. A call that
    /// honours its <see cref="CancellationToken"/> ends within milliseconds; one that does not must not keep a service
    /// from flushing its data on the way out. Settable for tests.
    /// </summary>
    internal TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Stops the dispatcher for the end of the app: new calls are rejected (error type <c>HostStopped</c>), calls in flight
    /// are cancelled through their <see cref="CancellationToken"/> and given a moment to finish, and then the registered
    /// services are disposed in the reverse of the order they were registered: <see cref="IAsyncDisposable"/> when a
    /// service has it, otherwise <see cref="IDisposable"/>. A service that throws while being disposed is logged and does
    /// not stop the others.
    /// </summary>
    /// <remarks>
    /// The host calls this when Tauri exits. The process ends right after, so this is the last chance to flush a database
    /// or a file. It only runs on a normal exit: a crash, a forced kill or power loss skips it. Calling it again returns the
    /// first call's task, so services are disposed once.
    /// </remarks>
    /// <param name="cancellationToken">Ends the wait. If it fires before shutdown is done, the task is cancelled and the services not yet disposed stay as they are.</param>
    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_shutdownLock)
            return _shutdown ??= ShutdownCoreAsync(cancellationToken);
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        _shuttingDown = true;
        _logger.LogInformation("Shutting down: {Calls} call(s) in flight, {Services} service(s) to dispose", _inFlightCalls.Count, _serviceInstances.Count);

        // A service waiting for a window's answer to a frontend call will not get one
        foreach (var pending in _pendingFrontendCalls.Values)
            pending.Answer.TrySetException(new InvalidOperationException($"The frontend call was abandoned: {ShuttingDownMessage}"));

        // Tell the running calls to stop, then give them a moment to finish before their services are disposed
        foreach (var call in _inFlightCalls.Values)
        {
            try { call.Cancel(); }
            catch (ObjectDisposedException) { /* it finished just now */ }
        }

        await DrainCallsAsync(cancellationToken).ConfigureAwait(false);
        await DisposeServicesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Shutdown complete");
    }

    private async Task DrainCallsAsync(CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(DrainTimeout);
        try
        {
            while (!_inFlightCalls.IsEmpty)
                await Task.Delay(10, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the grace period ran out: go on and dispose, the process is ending
            _logger.LogWarning("{Calls} call(s) did not finish within {Seconds:0.##} s of being cancelled; disposing the services anyway",
                _inFlightCalls.Count, DrainTimeout.TotalSeconds);
        }
    }

    private async Task DisposeServicesAsync(CancellationToken cancellationToken)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var i = _serviceInstances.Count - 1; i >= 0; i--)
        {
            var service = _serviceInstances[i];
            if (!seen.Add(service)) continue; // the same instance registered twice

            try
            {
                switch (service)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Shutdown ran out of time while disposing service '{Service}'", service.GetType().Name);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Disposing service '{Service}' failed", service.GetType().Name);
            }
        }
    }

    private static BridgeResponse StoppedResponse(string? callId) =>
        ErrorResponse(callId ?? "", ShuttingDownMessage, StoppedErrorType);
}
