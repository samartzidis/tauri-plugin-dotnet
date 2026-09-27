using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tauri.Plugin.DotNet.Tests;

[BridgeService]
public class TestService
{
    public string Greet(string name) => $"Hello, {name}!";

    public int Add(int a, int b) => a + b;

    public Person MakePerson(string firstName) => new() { FirstName = firstName, Nickname = null };

    public Task<string> GreetAsync(string name) => Task.FromResult($"Async {name}");

    public ValueTask<int> ValueAsync(int n) => new(n * 2);

    public async Task DoAsync() => await Task.Yield();

    public void DoVoid() { }

    public string WhoAmI(CallContext ctx) => ctx.WindowLabel ?? "none";

    public string Optional(string a, string b = "default") => $"{a}-{b}";

    /// <summary>Completed once <see cref="Slow"/> is running, i.e. its call is registered as in flight.</summary>
    public TaskCompletionSource SlowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<string> Slow(CancellationToken ct)
    {
        SlowStarted.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return "unreachable";
    }

    public string Throws() => throw new InvalidOperationException("boom");

    [BridgeIgnore]
    public string Hidden() => "hidden";
}

public class Person
{
    public string FirstName { get; set; } = "";
    public string? Nickname { get; set; }
}

public class Account
{
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public long Id { get; set; }

    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public decimal Balance { get; set; }

    public long Plain { get; set; }
}

[BridgeService]
public class LedgerService
{
    public Account Echo(Account account) => account;
}

/// <summary>Overloads and names that differ only in case: none of these can be told apart by a call.</summary>
[BridgeService]
public class OverloadedService
{
    public string Foo(string a) => "one";
    public string Foo(string a, int b) => "two";
    public string Baz() => "Baz";
    public string baz() => "baz";
    public string Single() => "single";

    [BridgeIgnore] public string Hidden(int x) => "hidden";
    public string Hidden() => "visible";
}

/// <summary>An overload hidden with [BridgeIgnore] leaves one exposed method per name.</summary>
[BridgeService]
public class IgnoredOverloadService
{
    [BridgeIgnore] public string Hidden(int x) => "hidden";
    public string Hidden() => "visible";
}

[BridgeService(Name = "Renamed")]
public class OriginalName
{
    public string Ping() => "pong";
}

public class BridgeDispatcherTests
{
    private static BridgeDispatcher CreateDispatcher()
    {
        var dispatcher = new BridgeDispatcher();
        dispatcher.RegisterService(new TestService());
        return dispatcher;
    }

    private static BridgeRequest Request(string method, string callId = "c1", params object[] args) => new()
    {
        CallId = callId,
        Method = method,
        Args = args.Select(a => JsonSerializer.SerializeToElement(a)).ToArray()
    };

    // --- dispatch ---------------------------------------------------------

    [Fact]
    public async Task Invokes_sync_method_and_returns_result()
    {
        var response = await CreateDispatcher().InvokeAsync(Request("TestService.Greet", "c1", "World"));

        Assert.Equal("c1", response.CallId);
        Assert.Null(response.Error);
        Assert.Equal("Hello, World!", response.Result);
    }

    [Fact]
    public async Task Binds_multiple_arguments_positionally()
    {
        var response = await CreateDispatcher().InvokeAsync(Request("TestService.Add", "c1", 2, 3));

        Assert.Equal(5, response.Result);
    }

    [Fact]
    public async Task Method_names_are_case_insensitive_but_service_names_are_not()
    {
        var dispatcher = CreateDispatcher();

        Assert.Equal("Hello, x!", (await dispatcher.InvokeAsync(Request("TestService.greet", "c1", "x"))).Result);
        Assert.NotNull((await dispatcher.InvokeAsync(Request("testservice.Greet", "c2", "x"))).Error);
    }

    [Fact]
    public async Task Awaits_Task_of_T()
    {
        var response = await CreateDispatcher().InvokeAsync(Request("TestService.GreetAsync", "c1", "you"));

        Assert.Equal("Async you", response.Result);
    }

    [Fact]
    public async Task Awaits_ValueTask_of_T()
    {
        var response = await CreateDispatcher().InvokeAsync(Request("TestService.ValueAsync", "c1", 21));

        Assert.Equal(42, response.Result);
    }

    [Theory]
    [InlineData("TestService.DoAsync")]
    [InlineData("TestService.DoVoid")]
    public async Task Methods_without_a_result_produce_no_result(string method)
    {
        var dispatcher = CreateDispatcher();

        var response = await dispatcher.InvokeAsync(Request(method));
        var json = await dispatcher.InvokeJsonAsync(JsonSerializer.Serialize(Request(method)));

        Assert.Null(response.Result);
        Assert.Null(response.Error);
        Assert.DoesNotContain("result", json); // in particular no `{}` leaked from Task<VoidTaskResult>
        Assert.DoesNotContain("error", json);
    }

    [Fact]
    public async Task Serializes_results_camel_case_and_omits_nulls()
    {
        var dispatcher = CreateDispatcher();

        var json = await dispatcher.InvokeJsonAsync("""{"callId":"c1","method":"TestService.MakePerson","args":["Ada"]}""");

        Assert.Equal("""{"callId":"c1","result":{"firstName":"Ada"}}""", json);
    }

    [Fact]
    public async Task Numbers_marked_WriteAsString_travel_as_strings_both_ways_without_losing_digits()
    {
        var dispatcher = new BridgeDispatcher();
        dispatcher.RegisterService(new LedgerService());

        // 2^53 + 1 is the first integer a JavaScript number cannot hold; the decimal has more than 15 digits.
        var json = await dispatcher.InvokeJsonAsync(
            """{"callId":"c1","method":"LedgerService.Echo","args":[{"id":"9007199254740993","balance":"12345678901234567.89","plain":9007199254740993}]}""");

        Assert.Equal(
            """{"callId":"c1","result":{"id":"9007199254740993","balance":"12345678901234567.89","plain":9007199254740993}}""",
            json);
    }

    [Fact]
    public void Refuses_a_service_with_overloads_or_names_that_differ_only_in_case()
    {
        var dispatcher = new BridgeDispatcher();

        var ex = Assert.Throws<InvalidOperationException>(() => dispatcher.RegisterService(new OverloadedService()));

        Assert.Contains("Service 'OverloadedService' (OverloadedService)", ex.Message);
        Assert.Contains("Foo (2 methods)", ex.Message);
        Assert.Contains("Baz/baz (2 methods)", ex.Message);
        Assert.Contains("[BridgeIgnore]", ex.Message);
        // A unique name is no problem, and neither is a name whose other overload is [BridgeIgnore]
        Assert.DoesNotContain("Single", ex.Message);
        Assert.DoesNotContain("Hidden", ex.Message);
    }

    [Fact]
    public async Task A_refused_service_is_not_registered_at_all()
    {
        var dispatcher = new BridgeDispatcher();
        Assert.Throws<InvalidOperationException>(() => dispatcher.RegisterService(new OverloadedService()));

        var json = await dispatcher.InvokeJsonAsync("""{"callId":"c1","method":"OverloadedService.Single","args":[]}""");

        Assert.Contains("\"error\"", json);
        Assert.DoesNotContain("single", json);
    }

    [Fact]
    public async Task Allows_an_overload_hidden_with_BridgeIgnore()
    {
        var dispatcher = new BridgeDispatcher();
        dispatcher.RegisterService(new IgnoredOverloadService());

        var json = await dispatcher.InvokeJsonAsync("""{"callId":"c1","method":"IgnoredOverloadService.Hidden","args":[]}""");

        Assert.Equal("""{"callId":"c1","result":"visible"}""", json);
    }

    [Fact]
    public async Task Uses_service_name_override()
    {
        var dispatcher = new BridgeDispatcher().RegisterService(new OriginalName());

        Assert.Equal("pong", (await dispatcher.InvokeAsync(Request("Renamed.Ping"))).Result);
        Assert.NotNull((await dispatcher.InvokeAsync(Request("OriginalName.Ping", "c2"))).Error);
    }

    // --- injection and defaults ------------------------------------------

    [Fact]
    public async Task Injects_call_context_with_window_label()
    {
        var dispatcher = CreateDispatcher();

        Assert.Equal("main", (await dispatcher.InvokeAsync(Request("TestService.WhoAmI"), "main")).Result);
        Assert.Equal("none", (await dispatcher.InvokeAsync(Request("TestService.WhoAmI", "c2"))).Result);
    }

    [Fact]
    public async Task Uses_default_value_for_missing_optional_argument()
    {
        var dispatcher = CreateDispatcher();

        Assert.Equal("a-default", (await dispatcher.InvokeAsync(Request("TestService.Optional", "c1", "a"))).Result);
        Assert.Equal("a-b", (await dispatcher.InvokeAsync(Request("TestService.Optional", "c2", "a", "b"))).Result);
    }

    [Fact]
    public async Task Reports_missing_required_argument()
    {
        var response = await CreateDispatcher().InvokeAsync(Request("TestService.Greet"));

        Assert.Contains("Missing required argument 'name'", response.Error!.Message);
    }

    // --- errors -----------------------------------------------------------

    [Theory]
    [InlineData("NoDot", "Invalid method format")]
    [InlineData("Missing.Greet", "Service 'Missing' not found")]
    [InlineData("TestService.Nope", "Method 'Nope' not found")]
    [InlineData("TestService.Hidden", "Method 'Hidden' not found")]
    public async Task Reports_unresolvable_methods(string method, string expectedMessage)
    {
        var response = await CreateDispatcher().InvokeAsync(Request(method));

        Assert.Contains(expectedMessage, response.Error!.Message);
    }

    [Fact]
    public async Task Unwraps_exceptions_thrown_by_the_service()
    {
        var response = await CreateDispatcher().InvokeAsync(Request("TestService.Throws"));

        Assert.Equal("boom", response.Error!.Message);
        Assert.Equal("InvalidOperationException", response.Error.Type);
    }

    [Fact]
    public async Task Rejects_a_request_without_call_id()
    {
        var response = await CreateDispatcher().InvokeAsync(Request("TestService.Greet", "", "x"));

        Assert.Contains("callId", response.Error!.Message);
    }

    [Fact]
    public async Task Malformed_json_yields_an_error_response_not_an_exception()
    {
        var json = await CreateDispatcher().InvokeJsonAsync("{ not json");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("JsonException", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    // --- cancellation -----------------------------------------------------

    [Fact]
    public async Task Cancel_triggers_the_injected_cancellation_token()
    {
        var service = new TestService();
        var dispatcher = new BridgeDispatcher().RegisterService(service);

        var call = dispatcher.InvokeAsync(Request("TestService.Slow", "slow-1"));
        await service.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(dispatcher.Cancel("slow-1"));

        var response = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("OperationCanceledException", response.Error!.Type);
        Assert.Equal("slow-1", response.CallId);
    }

    [Fact]
    public async Task Completed_calls_are_no_longer_cancellable()
    {
        var dispatcher = CreateDispatcher();

        await dispatcher.InvokeAsync(Request("TestService.Greet", "done-1", "x"));

        Assert.False(dispatcher.Cancel("done-1"));
    }

    [Fact]
    public void Cancel_of_unknown_call_returns_false()
    {
        Assert.False(CreateDispatcher().Cancel("nope"));
    }

    [Fact]
    public async Task Rejects_a_call_id_that_is_already_in_flight()
    {
        var service = new TestService();
        var dispatcher = new BridgeDispatcher().RegisterService(service);

        var first = dispatcher.InvokeAsync(Request("TestService.Slow", "dup"));
        await service.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await dispatcher.InvokeAsync(Request("TestService.Greet", "dup", "x"));
        Assert.Contains("already in flight", second.Error!.Message);

        dispatcher.Cancel("dup");
        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // --- events -----------------------------------------------------------

    private sealed class RecordingSink : IEventSink
    {
        public List<(string? Label, string Json)> Sent { get; } = [];
        public void Send(string? windowLabel, string eventJson) => Sent.Add((windowLabel, eventJson));
    }

    [Fact]
    public void Emit_broadcasts_a_camel_case_event_message()
    {
        var sink = new RecordingSink();
        var dispatcher = new BridgeDispatcher(sink);

        dispatcher.Emit("progress", new { PercentDone = 50 });

        var (label, json) = Assert.Single(sink.Sent);
        Assert.Null(label);
        Assert.Equal("""{"event":"progress","data":{"percentDone":50}}""", json);
    }

    [Fact]
    public void EmitTo_targets_one_window()
    {
        var sink = new RecordingSink();
        var dispatcher = new BridgeDispatcher(sink);

        dispatcher.EmitTo("settings", "closed");

        var (label, json) = Assert.Single(sink.Sent);
        Assert.Equal("settings", label);
        Assert.Equal("""{"event":"closed"}""", json);
    }

    [Fact]
    public void Emit_without_a_sink_is_dropped_silently()
    {
        var dispatcher = new BridgeDispatcher();

        dispatcher.Emit("x");
        dispatcher.EmitTo("main", "x");
    }

    [Fact]
    public void EmitTo_requires_a_window_label()
    {
        Assert.Throws<ArgumentException>(() => new BridgeDispatcher().EmitTo("", "x"));
    }
}
