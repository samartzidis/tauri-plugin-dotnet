using Microsoft.Extensions.Logging;

namespace Tauri.Plugin.DotNet;

/// <summary>
/// The entry point of a backend hosted in-process by the Tauri plugin. The backend assembly must
/// contain exactly one public or internal class implementing this interface, with a parameterless
/// constructor; the host creates it once at startup and calls <see cref="Configure"/>.
/// </summary>
/// <example>
/// <code>
/// public sealed class Backend : IBridgeBackend
/// {
///     public void Configure(BridgeDispatcher dispatcher) =>
///         dispatcher.RegisterService(new GreetService());
/// }
/// </code>
/// </example>
public interface IBridgeBackend
{
    /// <summary>
    /// Where the bridge sends its own log output: registered services, cancelled calls and
    /// exceptions thrown by service methods. Read once, before <see cref="Configure"/>, so a
    /// backend can also use the same factory for its services. <see langword="null"/> (the default)
    /// turns bridge logging off. The host never disposes the factory.
    /// </summary>
    ILoggerFactory? LoggerFactory => null;

    /// <summary>
    /// Registers the backend's services. The dispatcher is already connected to the frontend, so
    /// services can keep it to <see cref="BridgeDispatcher.Emit"/> events.
    /// </summary>
    void Configure(BridgeDispatcher dispatcher);

    /// <summary>
    /// Called once when the app is exiting, after the dispatcher has stopped taking calls, cancelled the ones in flight and
    /// disposed the registered services (see <see cref="BridgeDispatcher.ShutdownAsync"/>). Use it to release what the backend
    /// created itself and no service owns, such as a database connection shared by several services. The default does nothing.
    /// </summary>
    /// <remarks>
    /// The process ends right after, so keep it short: the host waits a few seconds at most (the plugin's shutdown timeout) and
    /// then goes on. It runs only on a normal exit, not after a crash or a forced kill.
    /// </remarks>
    /// <param name="cancellationToken">Fires when the host stops waiting.</param>
    Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
