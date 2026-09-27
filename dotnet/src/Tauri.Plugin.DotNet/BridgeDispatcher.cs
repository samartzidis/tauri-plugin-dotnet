using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tauri.Plugin.DotNet;

/// <summary>
/// Transport-agnostic core of the bridge: reflection-based dispatch of frontend calls to
/// registered service instances, cancellation, and .NET-to-frontend events.
/// The host adapter feeds it requests through <see cref="InvokeAsync"/> / <see cref="InvokeJsonAsync"/>
/// and receives events through <see cref="EventSink"/>.
/// </summary>
public sealed partial class BridgeDispatcher
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new TupleJsonConverterFactory() }
    };

    private readonly Dictionary<string, RegisteredService> _services = new();
    private readonly ILogger<BridgeDispatcher> _logger;

    /// <summary>Tracks in-flight calls so they can be cancelled. Key = callId.</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlightCalls = new();

    /// <param name="eventSink">Where events are delivered. Can also be assigned later via <see cref="EventSink"/>.</param>
    /// <param name="logger">Optional logger. Pass <c>null</c> to disable logging.</param>
    public BridgeDispatcher(IEventSink? eventSink = null, ILogger<BridgeDispatcher>? logger = null)
    {
        EventSink = eventSink;
        _logger = logger ?? NullLogger<BridgeDispatcher>.Instance;
        RegisterFrontendReplies();
    }

    /// <summary>
    /// Where <see cref="Emit"/> and <see cref="EmitTo"/> deliver events. Events emitted while
    /// this is null are dropped.
    /// </summary>
    public IEventSink? EventSink { get; set; }

    /// <summary>
    /// Register a service instance. All public instance methods declared on T
    /// become callable from JS as "ServiceName.MethodName". Register all services
    /// before the host starts dispatching calls.
    /// </summary>
    public BridgeDispatcher RegisterService<T>(T instance) where T : class
    {
        var type = typeof(T);
        var attr = type.GetCustomAttribute<BridgeServiceAttribute>();
        var serviceName = attr?.Name ?? type.Name;

        var exposed = new List<MethodInfo>();
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName) continue; // skip property accessors, event accessors
            if (method.GetCustomAttribute<BridgeIgnoreAttribute>() != null) continue;
            exposed.Add(method);
        }

        // A call names its method, and a name can only be one method. Two methods with the same name (overloads, or names
        // that differ only in case, which the lookup ignores) would silently leave only the last reachable, so refuse them.
        // Must match ServiceDiscovery.RequireUniqueNames in the generator.
        var shared = exposed
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{string.Join("/", g.Select(m => m.Name).Distinct())} ({g.Count()} methods)")
            .ToList();
        if (shared.Count > 0)
            throw new InvalidOperationException(
                $"Service '{serviceName}' ({type.Name}) has methods that share a name: {string.Join("; ", shared)}. " +
                "The bridge routes calls by method name (ignoring case), so each method needs its own name; rename all but one, or mark them [BridgeIgnore].");

        var methods = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var method in exposed)
            methods[method.Name] = method;

        _services[serviceName] = new RegisteredService(instance, methods);
        lock (_serviceInstances)
            _serviceInstances.Add(instance);
        _logger.LogInformation("Registered service '{ServiceName}' with {MethodCount} method(s)", serviceName, methods.Count);
        return this;
    }

    /// <summary>
    /// Handles a serialized <see cref="BridgeRequest"/> and returns a serialized <see cref="BridgeResponse"/>.
    /// Never throws: malformed requests and failing calls come back as an error response.
    /// This is the entry point host adapters use, since both hosting models exchange strings.
    /// </summary>
    /// <param name="requestJson">The request JSON.</param>
    /// <param name="windowLabel">Label of the calling webview, surfaced to services as <see cref="CallContext.WindowLabel"/>.</param>
    public async Task<string> InvokeJsonAsync(string requestJson, string? windowLabel = null)
    {
        BridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(requestJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            return SerializeResponse(ErrorResponse("", $"Malformed request: {ex.Message}", nameof(JsonException)));
        }

        if (request == null)
            return SerializeResponse(ErrorResponse("", "Request is empty.", nameof(ArgumentException)));

        var response = await InvokeAsync(request, windowLabel).ConfigureAwait(false);
        return SerializeResponse(response);
    }

    /// <summary>
    /// Dispatches a call to the registered service and awaits its result.
    /// Never throws: failures (including cancellation) are reported in <see cref="BridgeResponse.Error"/>.
    /// </summary>
    public async Task<BridgeResponse> InvokeAsync(BridgeRequest request, string? windowLabel = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_shuttingDown)
            return StoppedResponse(request.CallId);

        try
        {
            if (string.IsNullOrEmpty(request.CallId))
                throw new ArgumentException("Request is missing 'callId'.");

            var (service, method) = Resolve(request.Method);

            using var cts = new CancellationTokenSource();
            if (!_inFlightCalls.TryAdd(request.CallId, cts))
                throw new InvalidOperationException($"Call id '{request.CallId}' is already in flight.");

            try
            {
                // Shutdown cancels the calls it sees in flight. A call that registered just after it looked must not run,
                // and one that registered before is seen: the flag is set first, then the calls are listed.
                if (_shuttingDown)
                    return StoppedResponse(request.CallId);

                var args = BindArguments(request, method, new CallContext(windowLabel), cts.Token);
                var rawResult = method.Invoke(service.Instance, args);
                var result = await UnwrapAsyncResult(rawResult, method.ReturnType).ConfigureAwait(false);

                return new BridgeResponse { CallId = request.CallId, Result = result };
            }
            finally
            {
                _inFlightCalls.TryRemove(request.CallId, out _);
            }
        }
        catch (Exception ex)
        {
            return ToErrorResponse(request, ex);
        }
    }

    /// <summary>
    /// Cancels an in-flight call, triggering the <see cref="CancellationToken"/> injected into the service method.
    /// </summary>
    /// <returns>true if the call was in flight; false if it was unknown or already completed.</returns>
    public bool Cancel(string callId)
    {
        if (!_inFlightCalls.TryGetValue(callId, out var cts))
            return false;

        _logger.LogDebug("Received cancel request for callId '{CallId}'", callId);
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false; // the call completed between lookup and cancel
        }
    }

    // The by-name overloads below are separate methods, not one with an optional `data`
    // parameter: with a default argument, Emit("name") would bind to the generic Emit<string>
    // overload (an exact generic match beats a method that needs a default), and a name would be
    // taken for a typed payload.

    /// <summary>
    /// Emit an event with no payload, by name, to the frontend of all webviews. Fire-and-forget;
    /// safe to call from any thread. Prefer the typed <see cref="Emit{TEvent}"/>.
    /// </summary>
    /// <param name="eventName">The event name (matches the JS <c>events.on(name, cb)</c> subscription).</param>
    public void Emit(string eventName) => SendEvent(null, eventName, null);

    /// <summary>
    /// Emit an event by name, with a payload, to the frontend of all webviews.
    /// Prefer the typed <see cref="Emit{TEvent}"/>.
    /// </summary>
    /// <param name="eventName">The event name (matches the JS <c>events.on(name, cb)</c> subscription).</param>
    /// <param name="data">Payload, serialized to JSON with camelCase naming.</param>
    public void Emit(string eventName, object? data) => SendEvent(null, eventName, data);

    /// <summary>
    /// Emit an event with no payload, by name, to a single webview identified by its Tauri label
    /// (for example <see cref="CallContext.WindowLabel"/>).
    /// </summary>
    public void EmitTo(string windowLabel, string eventName)
    {
        ArgumentException.ThrowIfNullOrEmpty(windowLabel);
        SendEvent(windowLabel, eventName, null);
    }

    /// <summary>
    /// Emit an event by name, with a payload, to a single webview identified by its Tauri label.
    /// Prefer the typed <see cref="EmitTo{TEvent}"/>.
    /// </summary>
    public void EmitTo(string windowLabel, string eventName, object? data)
    {
        ArgumentException.ThrowIfNullOrEmpty(windowLabel);
        SendEvent(windowLabel, eventName, data);
    }

    /// <summary>
    /// Emit a typed event to the frontend of all webviews. The event name comes from the
    /// <see cref="BridgeEventAttribute"/> on <typeparamref name="TEvent"/>, so the name is written
    /// once, on the payload class, and the generated TypeScript <c>on…</c> helper is the same event.
    /// </summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="TEvent"/> is not marked <see cref="BridgeEventAttribute"/>.</exception>
    public void Emit<TEvent>(TEvent payload) where TEvent : class =>
        SendEvent(null, EventNameOf(typeof(TEvent)), payload);

    /// <summary>
    /// Emit a typed event to a single webview (see <see cref="Emit{TEvent}"/>).
    /// </summary>
    public void EmitTo<TEvent>(string windowLabel, TEvent payload) where TEvent : class
    {
        ArgumentException.ThrowIfNullOrEmpty(windowLabel);
        SendEvent(windowLabel, EventNameOf(typeof(TEvent)), payload);
    }

    private static readonly ConcurrentDictionary<Type, string> EventNames = new();

    private static string EventNameOf(Type type) =>
        EventNames.GetOrAdd(type, static t =>
            t.GetCustomAttribute<BridgeEventAttribute>()?.Name
            ?? throw new InvalidOperationException(
                $"'{t.Name}' is not marked [BridgeEvent(\"name\")], so its event name is unknown. " +
                "Mark the class, or emit it by name with Emit(string, object?)."));

    private void SendEvent(string? windowLabel, string eventName, object? data)
    {
        var sink = EventSink;
        if (sink == null)
        {
            _logger.LogDebug("Dropping event '{EventName}': no event sink is attached", eventName);
            return;
        }

        var json = JsonSerializer.Serialize(new BridgeEventMessage { Event = eventName, Data = data }, JsonOptions);
        sink.Send(windowLabel, json);
    }

    /// <summary>Parses "ServiceName.MethodName" and looks both up.</summary>
    private (RegisteredService Service, MethodInfo Method) Resolve(string fullName)
    {
        var dotIndex = fullName.IndexOf('.');
        if (dotIndex < 0)
            throw new ArgumentException($"Invalid method format: '{fullName}'. Expected 'ServiceName.MethodName'.");

        var serviceName = fullName[..dotIndex];
        var methodName = fullName[(dotIndex + 1)..];

        if (!_services.TryGetValue(serviceName, out var service))
            throw new InvalidOperationException($"Service '{serviceName}' not found. Registered services: {string.Join(", ", _services.Keys)}");

        if (!service.Methods.TryGetValue(methodName, out var method))
            throw new InvalidOperationException($"Method '{methodName}' not found on service '{serviceName}'.");

        return (service, method);
    }

    /// <summary>
    /// Builds the argument array from the JSON args, injecting <see cref="CallContext"/> and
    /// <see cref="CancellationToken"/> where declared (they are never supplied from JS).
    /// </summary>
    private static object?[] BindArguments(BridgeRequest request, MethodInfo method, CallContext callContext, CancellationToken cancellationToken)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        var jsonArgIndex = 0;

        for (var i = 0; i < parameters.Length; i++)
        {
            var paramType = parameters[i].ParameterType;
            if (paramType == typeof(CallContext))
            {
                args[i] = callContext;
            }
            else if (paramType == typeof(CancellationToken))
            {
                args[i] = cancellationToken;
            }
            else if (request.Args != null && jsonArgIndex < request.Args.Length)
            {
                args[i] = request.Args[jsonArgIndex].Deserialize(paramType, JsonOptions);
                jsonArgIndex++;
            }
            else if (parameters[i].HasDefaultValue)
            {
                args[i] = parameters[i].DefaultValue;
            }
            else
            {
                throw new ArgumentException(
                    $"Missing required argument '{parameters[i].Name}' (index {i}) for method '{request.Method}'.");
            }
        }

        return args;
    }

    /// <summary>
    /// Awaits Task, Task&lt;T&gt;, ValueTask and ValueTask&lt;T&gt; return values and extracts the result.
    /// Synchronous return values pass through unchanged. The declared return type decides whether
    /// there is a result, because the runtime type of an <c>async Task</c> result is a
    /// <c>Task&lt;VoidTaskResult&gt;</c> subclass that would otherwise leak a bogus value.
    /// </summary>
    private static async Task<object?> UnwrapAsyncResult(object? rawResult, Type declaredReturnType)
    {
        switch (rawResult)
        {
            case null:
                return null;

            case Task task:
                await task.ConfigureAwait(false);
                return IsGenericOf(declaredReturnType, typeof(Task<>))
                    ? declaredReturnType.GetProperty(nameof(Task<int>.Result))!.GetValue(task)
                    : null;

            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                return null;
        }

        // ValueTask<T> is a struct with no shared base type, so go through AsTask().
        if (IsGenericOf(declaredReturnType, typeof(ValueTask<>)))
        {
            var task = (Task)declaredReturnType.GetMethod(nameof(ValueTask<int>.AsTask))!.Invoke(rawResult, null)!;
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty(nameof(Task<int>.Result))!.GetValue(task);
        }

        return rawResult;
    }

    private static bool IsGenericOf(Type type, Type genericTypeDefinition) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == genericTypeDefinition;

    private BridgeResponse ToErrorResponse(BridgeRequest request, Exception ex)
    {
        // Reflection wraps exceptions thrown by the service method
        var error = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

        if (error is OperationCanceledException)
        {
            _logger.LogDebug("Call '{Method}' ({CallId}) was cancelled", request.Method, request.CallId);
            return ErrorResponse(request.CallId, $"Call '{request.Method}' was cancelled.", nameof(OperationCanceledException));
        }

        _logger.LogError(error, "Error handling '{Method}'", request.Method);
        return ErrorResponse(request.CallId, error.Message, error.GetType().Name);
    }

    private static BridgeResponse ErrorResponse(string callId, string message, string type) => new()
    {
        CallId = callId,
        Error = new BridgeError { Message = message, Type = type }
    };

    private string SerializeResponse(BridgeResponse response)
    {
        try
        {
            return JsonSerializer.Serialize(response, JsonOptions);
        }
        catch (Exception ex)
        {
            // e.g. the result graph is not serializable; still answer the call
            _logger.LogError(ex, "Failed to serialize the response for call '{CallId}'", response.CallId);
            return JsonSerializer.Serialize(ErrorResponse(response.CallId, $"Failed to serialize the result: {ex.Message}", ex.GetType().Name), JsonOptions);
        }
    }

    private sealed record RegisteredService(object Instance, Dictionary<string, MethodInfo> Methods);
}
