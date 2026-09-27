using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace Tauri.Plugin.DotNet;

// .NET-to-frontend calls: C# awaits a typed proxy method, the frontend implements it.
public sealed partial class BridgeDispatcher
{
    /// <summary>Event that carries a request to the frontend. Reserved: the runtime handles it, not application code.</summary>
    internal const string FrontendEventName = "$frontend";

    /// <summary>Service the frontend calls to answer a request. Reserved.</summary>
    internal const string FrontendReplyService = "$frontend";

    private static readonly TimeSpan DefaultFrontendTimeout = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, PendingFrontendCall> _pendingFrontendCalls = new();

    /// <summary>
    /// Returns a proxy for <typeparamref name="T"/>, a public interface marked
    /// <see cref="BridgeFrontendAttribute"/>. Each method call is sent to the webview
    /// <paramref name="windowLabel"/>, whose frontend must have registered an implementation
    /// (the generated <c>implement…</c> function); the awaited result is what it returned.
    /// </summary>
    /// <remarks>
    /// A failure in the frontend throws <see cref="FrontendException"/>. If the frontend does not
    /// answer within <paramref name="timeout"/> (two minutes by default) a <see cref="TimeoutException"/>
    /// is thrown; pass <see cref="Timeout.InfiniteTimeSpan"/> to wait forever. A
    /// <see cref="CancellationToken"/> parameter on an interface method cancels the wait and is not sent.
    /// The frontend must have registered its implementation before the call: a request that arrives
    /// earlier is not replayed.
    /// </remarks>
    public T GetFrontend<T>(string windowLabel, TimeSpan? timeout = null) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(windowLabel);
        if (timeout is { } t && t <= TimeSpan.Zero && t != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive or Timeout.InfiniteTimeSpan.");

        var serviceName = FrontendProxy.Validate(typeof(T));
        var proxy = DispatchProxy.Create<T, FrontendProxy>();
        ((FrontendProxy)(object)proxy).Attach(this, windowLabel, serviceName, timeout ?? DefaultFrontendTimeout);
        return proxy;
    }

    /// <summary>Sends the request and waits for the frontend's raw JSON answer (null when it answered with nothing).</summary>
    internal async Task<JsonElement?> CallFrontendAsync(
        string windowLabel, string service, string method, object?[] args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (EventSink == null)
            throw new InvalidOperationException($"Cannot call the frontend method '{service}.{method}': no event sink is attached.");
        if (_shuttingDown)
            throw new InvalidOperationException($"Cannot call the frontend method '{service}.{method}': {ShuttingDownMessage}");

        var id = Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingFrontendCalls[id] = new PendingFrontendCall(windowLabel, answer);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
            limit.CancelAfter(timeout);
        using var onLimit = limit.Token.Register(() => answer.TrySetCanceled(limit.Token));

        try
        {
            SendEvent(windowLabel, FrontendEventName, new FrontendRequestMessage { Id = id, Service = service, Method = method, Args = args });
            return await answer.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Window '{windowLabel}' did not answer '{service}.{method}' within {timeout.TotalSeconds:0.##} s.");
        }
        finally
        {
            _pendingFrontendCalls.TryRemove(id, out _);
        }
    }

    /// <summary>Completes a pending call with the answer the frontend sent. Answers for calls that already ended are ignored.</summary>
    internal void CompleteFrontendCall(string id, string? fromWindow, object? result, string error, string errorType)
    {
        if (!_pendingFrontendCalls.TryGetValue(id, out var pending))
            return;

        // Only the window that was asked may answer; ids are unguessable but windows still must not answer for each other.
        if (pending.WindowLabel != fromWindow)
            throw new InvalidOperationException($"Frontend call '{id}' was not made to window '{fromWindow}'.");

        if (!string.IsNullOrEmpty(error))
            pending.Answer.TrySetException(new FrontendException(error, errorType));
        else
            pending.Answer.TrySetResult(result is JsonElement { ValueKind: not JsonValueKind.Null } element ? element : null);
    }

    private void RegisterFrontendReplies()
    {
        // Added directly (not through RegisterService) so it is neither logged nor visible to the generator.
        var replies = new FrontendReplies(this);
        var methods = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(FrontendReplies.Reply)] = typeof(FrontendReplies).GetMethod(nameof(FrontendReplies.Reply))!
        };
        _services[FrontendReplyService] = new RegisteredService(replies, methods);
    }

    private sealed record PendingFrontendCall(string WindowLabel, TaskCompletionSource<JsonElement?> Answer);

    private sealed class FrontendRequestMessage
    {
        public string Id { get; set; } = "";
        public string Service { get; set; } = "";
        public string Method { get; set; } = "";
        public object?[] Args { get; set; } = [];
    }
}

/// <summary>The built-in service the frontend runtime calls to answer a .NET-to-frontend request.</summary>
internal sealed class FrontendReplies(BridgeDispatcher dispatcher)
{
    /// <summary>
    /// <paramref name="error"/> is empty on success; on failure it carries the message and
    /// <paramref name="errorType"/> the JavaScript error's name.
    /// </summary>
    public void Reply(CallContext context, string id, object? result, string error, string errorType) =>
        dispatcher.CompleteFrontendCall(id, context.WindowLabel, result, error, errorType);
}

/// <summary>A .NET-to-frontend call failed inside the frontend implementation.</summary>
public sealed class FrontendException : Exception
{
    public FrontendException(string message, string errorType) : base(message) => ErrorType = errorType;

    /// <summary>The name of the JavaScript error that was thrown, for example <c>TypeError</c>.</summary>
    public string ErrorType { get; }
}

/// <summary>Turns calls on a <see cref="BridgeFrontendAttribute"/> interface into frontend requests.</summary>
internal class FrontendProxy : DispatchProxy
{
    private static readonly MethodInfo s_convert = typeof(FrontendProxy).GetMethod(nameof(Convert), BindingFlags.Static | BindingFlags.NonPublic)!;

    private BridgeDispatcher _dispatcher = null!;
    private string _window = "";
    private string _service = "";
    private TimeSpan _timeout;

    internal void Attach(BridgeDispatcher dispatcher, string window, string service, TimeSpan timeout)
    {
        _dispatcher = dispatcher;
        _window = window;
        _service = service;
        _timeout = timeout;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        var parameters = targetMethod.GetParameters();
        var wireArgs = new List<object?>();
        var cancellationToken = CancellationToken.None;
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType == typeof(CancellationToken))
                cancellationToken = (CancellationToken)args![i]!;
            else
                wireArgs.Add(args![i]);
        }

        var raw = _dispatcher.CallFrontendAsync(_window, _service, targetMethod.Name, wireArgs.ToArray(), _timeout, cancellationToken);

        return targetMethod.ReturnType == typeof(Task)
            ? Discard(raw)
            : s_convert.MakeGenericMethod(targetMethod.ReturnType.GetGenericArguments()[0]).Invoke(null, [raw]);
    }

    private static async Task Discard(Task<JsonElement?> raw) => await raw.ConfigureAwait(false);

    private static async Task<T?> Convert<T>(Task<JsonElement?> raw)
    {
        var element = await raw.ConfigureAwait(false);
        return element is { } value ? value.Deserialize<T>(BridgeDispatcher.JsonOptions) : default;
    }

    /// <summary>Checks that <paramref name="type"/> can be proxied and returns its wire name.</summary>
    internal static string Validate(Type type)
    {
        var problems = new List<string>();

        if (!type.IsInterface)
            problems.Add("it is not an interface");
        else
        {
            if (!(type.IsPublic || type.IsNestedPublic))
                problems.Add("it is not public");
            if (type.GetInterfaces().Length > 0)
                problems.Add("it inherits other interfaces");
            if (type.GetProperties().Length > 0 || type.GetEvents().Length > 0)
                problems.Add("it has properties or events (only methods are supported)");

            foreach (var method in type.GetMethods().Where(m => !m.IsSpecialName))
            {
                var returnsTask = method.ReturnType == typeof(Task) ||
                    (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>));
                if (!returnsTask)
                    problems.Add($"{method.Name} must return Task or Task<T>");
                if (method.IsGenericMethodDefinition)
                    problems.Add($"{method.Name} must not be generic");
                if (method.GetParameters().Any(p => p.ParameterType.IsByRef))
                    problems.Add($"{method.Name} must not have ref or out parameters");
            }

            foreach (var overloaded in type.GetMethods().GroupBy(m => m.Name).Where(g => g.Count() > 1))
                problems.Add($"{overloaded.Key} is overloaded (names must be unique)");
        }

        var attribute = type.GetCustomAttribute<BridgeFrontendAttribute>();
        if (attribute == null)
            problems.Add("it is not marked [BridgeFrontend]");

        if (problems.Count > 0)
            throw new InvalidOperationException($"'{type.Name}' cannot be used as a frontend interface: {string.Join("; ", problems)}.");

        return attribute!.Name ?? DefaultName(type.Name);
    }

    /// <summary>The interface name without a leading <c>I</c>: <c>IFrontend</c> becomes <c>Frontend</c>.</summary>
    internal static string DefaultName(string interfaceName) =>
        interfaceName.Length > 1 && interfaceName[0] == 'I' && char.IsUpper(interfaceName[1]) ? interfaceName[1..] : interfaceName;
}
