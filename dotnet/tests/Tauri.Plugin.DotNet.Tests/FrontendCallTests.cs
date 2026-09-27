using System.Text.Json;

namespace Tauri.Plugin.DotNet.Tests;

[BridgeFrontend]
public interface IFrontend
{
    Task<string?> PickFile(string title);
    Task Notify(string text);
    Task<Person> GetPerson(string name);
    Task<int> Slow(CancellationToken cancellationToken);
}

[BridgeFrontend(Name = "Custom")]
public interface IRenamedFrontend
{
    Task Ping();
}

public class FrontendCallTests
{
    /// <summary>What the pretend frontend sees of one request.</summary>
    private sealed record Request(string? Window, string Id, string Service, string Method, JsonElement[] Args);

    /// <summary>
    /// Stands in for the webview: records each <c>$frontend</c> event and answers it through the
    /// dispatcher's real reply path, the way the frontend runtime does.
    /// </summary>
    private sealed class FakeFrontend : IEventSink
    {
        public BridgeDispatcher Dispatcher { get; set; } = null!;
        public List<Request> Requests { get; } = new();

        /// <summary>Returns the answer, or null to never answer.</summary>
        public Func<Request, (object? Result, string Error, string ErrorType)?> Handler { get; set; } = _ => (null, "", "");

        /// <summary>Window the answer claims to come from; null uses the window that was asked.</summary>
        public string? ReplyFrom { get; set; }

        public string? LastReplyResponse { get; private set; }

        public void Send(string? windowLabel, string eventJson)
        {
            using var doc = JsonDocument.Parse(eventJson);
            if (doc.RootElement.GetProperty("event").GetString() != "$frontend")
                return;

            var data = doc.RootElement.GetProperty("data");
            var request = new Request(
                windowLabel,
                data.GetProperty("id").GetString()!,
                data.GetProperty("service").GetString()!,
                data.GetProperty("method").GetString()!,
                data.GetProperty("args").EnumerateArray().Select(a => a.Clone()).ToArray());
            lock (Requests) Requests.Add(request);

            if (Handler(request) is not { } answer)
                return;

            var reply = JsonSerializer.Serialize(new
            {
                callId = Guid.NewGuid().ToString("N"),
                method = "$frontend.Reply",
                args = new object?[] { request.Id, answer.Result, answer.Error, answer.ErrorType },
            });
            // Answers arrive later on another thread, like a real frontend's.
            _ = Task.Run(async () =>
            {
                await Task.Yield();
                LastReplyResponse = await Dispatcher.InvokeJsonAsync(reply, ReplyFrom ?? windowLabel);
            });
        }
    }

    private static (BridgeDispatcher Dispatcher, FakeFrontend Frontend) Create()
    {
        var frontend = new FakeFrontend();
        var dispatcher = new BridgeDispatcher(frontend);
        frontend.Dispatcher = dispatcher;
        return (dispatcher, frontend);
    }

    [Fact]
    public async Task A_string_answer_comes_back_typed_and_the_request_carries_the_arguments()
    {
        var (dispatcher, frontend) = Create();
        frontend.Handler = _ => ("C:/a.txt", "", "");

        var path = await dispatcher.GetFrontend<IFrontend>("main").PickFile("Choose");

        Assert.Equal("C:/a.txt", path);
        var request = Assert.Single(frontend.Requests);
        Assert.Equal("main", request.Window);
        Assert.Equal("Frontend", request.Service);
        Assert.Equal("PickFile", request.Method);
        Assert.Equal("Choose", Assert.Single(request.Args).GetString());
    }

    [Fact]
    public async Task A_null_answer_gives_null()
    {
        var (dispatcher, frontend) = Create();
        frontend.Handler = _ => (null, "", "");

        Assert.Null(await dispatcher.GetFrontend<IFrontend>("main").PickFile("Choose"));
    }

    [Fact]
    public async Task A_model_answer_is_deserialized_with_the_bridge_json_rules()
    {
        var (dispatcher, frontend) = Create();
        frontend.Handler = _ => (new { firstName = "Ada", nickname = (string?)null }, "", "");

        var person = await dispatcher.GetFrontend<IFrontend>("main").GetPerson("Ada");

        Assert.Equal("Ada", person.FirstName);
        Assert.Null(person.Nickname);
    }

    [Fact]
    public async Task A_method_returning_Task_completes_without_a_value()
    {
        var (dispatcher, frontend) = Create();

        await dispatcher.GetFrontend<IFrontend>("main").Notify("hello");

        Assert.Equal("Notify", Assert.Single(frontend.Requests).Method);
    }

    [Fact]
    public async Task The_wire_name_can_be_overridden_with_the_attribute()
    {
        var (dispatcher, frontend) = Create();

        await dispatcher.GetFrontend<IRenamedFrontend>("main").Ping();

        Assert.Equal("Custom", Assert.Single(frontend.Requests).Service);
    }

    [Fact]
    public async Task A_failure_in_the_frontend_throws_FrontendException_with_the_js_error_name()
    {
        var (dispatcher, frontend) = Create();
        frontend.Handler = _ => (null, "the dialog blew up", "TypeError");

        var ex = await Assert.ThrowsAsync<FrontendException>(() => dispatcher.GetFrontend<IFrontend>("main").PickFile("x"));

        Assert.Equal("the dialog blew up", ex.Message);
        Assert.Equal("TypeError", ex.ErrorType);
    }

    [Fact]
    public async Task Requests_are_sent_to_the_window_that_was_asked()
    {
        var (dispatcher, frontend) = Create();

        await dispatcher.GetFrontend<IFrontend>("child-1").Notify("x");

        Assert.Equal("child-1", Assert.Single(frontend.Requests).Window);
    }

    [Fact]
    public async Task No_answer_times_out_instead_of_hanging()
    {
        var (dispatcher, frontend) = Create();
        frontend.Handler = _ => null;

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => dispatcher.GetFrontend<IFrontend>("main", TimeSpan.FromMilliseconds(100)).PickFile("x"));

        Assert.Contains("did not answer 'Frontend.PickFile'", ex.Message);
    }

    [Fact]
    public async Task An_answer_after_the_timeout_is_ignored()
    {
        var (dispatcher, frontend) = Create();
        frontend.Handler = _ => null;
        await Assert.ThrowsAsync<TimeoutException>(
            () => dispatcher.GetFrontend<IFrontend>("main", TimeSpan.FromMilliseconds(50)).PickFile("x"));
        var late = frontend.Requests.Single();

        var response = await dispatcher.InvokeJsonAsync(
            JsonSerializer.Serialize(new { callId = "late", method = "$frontend.Reply", args = new object?[] { late.Id, "x", "", "" } }), "main");

        Assert.DoesNotContain("error", response);
    }

    [Fact]
    public async Task Cancelling_the_token_cancels_the_wait_and_is_not_sent_to_the_frontend()
    {
        var (dispatcher, frontend) = Create();
        frontend.Handler = _ => null;
        using var cts = new CancellationTokenSource();

        var call = dispatcher.GetFrontend<IFrontend>("main").Slow(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Empty(Assert.Single(frontend.Requests).Args);
    }

    [Fact]
    public async Task Concurrent_calls_are_matched_to_their_own_answers()
    {
        var (dispatcher, frontend) = Create();
        frontend.Handler = request => (request.Args[0].GetString() + "!", "", "");
        var ui = dispatcher.GetFrontend<IFrontend>("main");

        var answers = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => ui.PickFile($"title-{i}")));

        Assert.Equal(Enumerable.Range(0, 50).Select(i => $"title-{i}!"), answers);
    }

    [Fact]
    public async Task Another_window_cannot_answer_for_the_window_that_was_asked()
    {
        var (dispatcher, frontend) = Create();
        frontend.ReplyFrom = "other";
        var ui = dispatcher.GetFrontend<IFrontend>("main", TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<TimeoutException>(() => ui.PickFile("x"));

        Assert.Contains("was not made to window", frontend.LastReplyResponse);
        Assert.Contains("other", frontend.LastReplyResponse);
    }

    [Fact]
    public async Task Calling_without_an_event_sink_fails_at_once()
    {
        var dispatcher = new BridgeDispatcher();

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.GetFrontend<IFrontend>("main").Notify("x"));
    }

    [Fact]
    public async Task The_reply_service_exists_on_every_dispatcher_and_ignores_unknown_ids()
    {
        var dispatcher = new BridgeDispatcher();

        var response = await dispatcher.InvokeJsonAsync("""{"callId":"1","method":"$frontend.Reply","args":["nope",null,"",""]}""");

        Assert.DoesNotContain("not found", response);
        Assert.DoesNotContain("error", response);
    }

    public interface IUnmarked { Task Ping(); }

    [BridgeFrontend] public interface IBadReturn { string Get(); }
    [BridgeFrontend] public interface IOverloaded { Task A(); Task A(int x); }
    [BridgeFrontend] public interface IWithProperty { string Name { get; } Task A(); }
    [BridgeFrontend] public interface IGenericMethod { Task A<T>(T value); }
    [BridgeFrontend] public interface IRefParameter { Task A(ref int x); }
    [BridgeFrontend] public interface IDerived : IFrontend { }
    [BridgeFrontend] internal interface IInternal { Task A(); }
    [BridgeFrontend] public interface IAsyncOnly { ValueTask A(); }

    [Theory]
    [InlineData(typeof(IUnmarked), "not marked [BridgeFrontend]")]
    [InlineData(typeof(IBadReturn), "Get must return Task or Task<T>")]
    [InlineData(typeof(IOverloaded), "A is overloaded")]
    [InlineData(typeof(IWithProperty), "properties or events")]
    [InlineData(typeof(IGenericMethod), "A must not be generic")]
    [InlineData(typeof(IRefParameter), "ref or out")]
    [InlineData(typeof(IDerived), "inherits other interfaces")]
    [InlineData(typeof(IInternal), "not public")]
    [InlineData(typeof(IAsyncOnly), "A must return Task or Task<T>")]
    public void Interfaces_that_cannot_be_proxied_are_rejected_with_the_reason(Type type, string reason)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FrontendProxy.Validate(type));

        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void A_class_is_rejected()
    {
        var (dispatcher, _) = Create();

        var ex = Assert.Throws<InvalidOperationException>(() => dispatcher.GetFrontend<TestService>("main"));
        Assert.Contains("not an interface", ex.Message);
    }

    [Theory]
    [InlineData("IFrontend", "Frontend")]
    [InlineData("Frontend", "Frontend")]
    [InlineData("Item", "Item")]
    [InlineData("I", "I")]
    [InlineData("IOError", "OError")]
    public void The_default_wire_name_drops_a_leading_I_only_before_an_uppercase_letter(string interfaceName, string expected) =>
        Assert.Equal(expected, FrontendProxy.DefaultName(interfaceName));

    [Fact]
    public void A_non_positive_timeout_is_rejected()
    {
        var (dispatcher, _) = Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => dispatcher.GetFrontend<IFrontend>("main", TimeSpan.Zero));
        Assert.NotNull(dispatcher.GetFrontend<IFrontend>("main", Timeout.InfiniteTimeSpan));
    }
}
