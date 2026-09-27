using SampleApp.Backend.Services;
using Microsoft.Extensions.Logging;
using Tauri.Plugin.DotNet;

namespace SampleApp.Backend;

/// <summary>
/// The entry point the plugin's in-process host finds in this assembly: it registers the services
/// once at startup. The dispatcher is already wired to the frontend, so services can emit events.
/// </summary>
public sealed class Backend : IBridgeBackend
{
    // The bridge logs registered services, cancelled calls and exceptions thrown by service methods
    // here. Output appears in the console the app was started from (debug builds keep one).
    public ILoggerFactory? LoggerFactory { get; } =
        Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder
            .AddSimpleConsole(options => options.SingleLine = true)
            .SetMinimumLevel(LogLevel.Debug));

    public void Configure(BridgeDispatcher dispatcher) =>
        dispatcher.RegisterService(new BackendService(dispatcher));
}
