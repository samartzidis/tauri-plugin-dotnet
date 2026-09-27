using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tauri.Plugin.DotNet.Hosting;

namespace Tauri.Plugin.DotNet.Tests;

public class BackendLoggingTests
{
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    private sealed class LoggingBackend(ILoggerFactory? loggerFactory) : IBridgeBackend
    {
        public ILoggerFactory? LoggerFactory { get; } = loggerFactory;
        public bool ConfigureSawLogger { get; private set; }

        public void Configure(BridgeDispatcher dispatcher)
        {
            ConfigureSawLogger = LoggerFactory != null;
            dispatcher.RegisterService(new TestService());
        }
    }

    // Deliberately does not override LoggerFactory: it must default to "no logging".
    private sealed class SilentBackend : IBridgeBackend
    {
        public void Configure(BridgeDispatcher dispatcher) => dispatcher.RegisterService(new TestService());
    }

    private sealed class NoEvents : IEventSink
    {
        public void Send(string? windowLabel, string eventJson) { }
    }

    [Fact]
    public void A_backend_without_a_logger_factory_gets_no_logging()
    {
        Assert.Null(((IBridgeBackend)new SilentBackend()).LoggerFactory);

        var dispatcher = NativeHost.CreateDispatcher(new SilentBackend(), new NoEvents());

        Assert.NotNull(dispatcher);
    }

    [Fact]
    public void The_backends_logger_factory_receives_the_dispatchers_log_output()
    {
        var factory = new CapturingLoggerFactory();

        NativeHost.CreateDispatcher(new LoggingBackend(factory), new NoEvents());

        var entry = Assert.Single(factory.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Registered service 'TestService'", entry.Message);
    }

    [Fact]
    public async Task Exceptions_thrown_by_service_methods_are_logged_as_errors()
    {
        var factory = new CapturingLoggerFactory();
        var dispatcher = NativeHost.CreateDispatcher(new LoggingBackend(factory), new NoEvents());

        var response = await dispatcher.InvokeAsync(new BridgeRequest { CallId = "1", Method = "TestService.Throws" });

        Assert.NotNull(response.Error);
        Assert.Contains(factory.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Error handling 'TestService.Throws'"));
    }

    [Fact]
    public void The_factory_is_available_before_Configure_runs()
    {
        var backend = new LoggingBackend(new CapturingLoggerFactory());

        NativeHost.CreateDispatcher(backend, new NoEvents());

        Assert.True(backend.ConfigureSawLogger);
    }
}
