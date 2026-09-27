using System.Text.Json;

namespace Tauri.Plugin.DotNet.Tests;

[BridgeEvent("tick")]
public class TickEvent
{
    public int Count { get; set; }
    public string? Note { get; set; }
}

public class DerivedTickEvent : TickEvent { }

public class UnmarkedEvent
{
    public int Count { get; set; }
}

public class TypedEventTests
{
    private sealed class Capture : IEventSink
    {
        public List<(string? Label, JsonElement Message)> Sent { get; } = new();

        public void Send(string? windowLabel, string eventJson) =>
            Sent.Add((windowLabel, JsonDocument.Parse(eventJson).RootElement.Clone()));
    }

    private static (BridgeDispatcher Dispatcher, Capture Sink) Create()
    {
        var sink = new Capture();
        return (new BridgeDispatcher(sink), sink);
    }

    [Fact]
    public void Emit_takes_the_event_name_from_the_attribute_and_broadcasts()
    {
        var (dispatcher, sink) = Create();

        dispatcher.Emit(new TickEvent { Count = 3 });

        var (label, message) = Assert.Single(sink.Sent);
        Assert.Null(label);
        Assert.Equal("tick", message.GetProperty("event").GetString());
        Assert.Equal(3, message.GetProperty("data").GetProperty("count").GetInt32());
    }

    [Fact]
    public void EmitTo_takes_the_event_name_from_the_attribute_and_targets_one_window()
    {
        var (dispatcher, sink) = Create();

        dispatcher.EmitTo("child-1", new TickEvent { Count = 1, Note = "hi" });

        var (label, message) = Assert.Single(sink.Sent);
        Assert.Equal("child-1", label);
        Assert.Equal("tick", message.GetProperty("event").GetString());
        Assert.Equal("hi", message.GetProperty("data").GetProperty("note").GetString());
    }

    [Fact]
    public void A_derived_payload_class_inherits_the_event_name()
    {
        var (dispatcher, sink) = Create();

        dispatcher.Emit(new DerivedTickEvent { Count = 1 });

        Assert.Equal("tick", Assert.Single(sink.Sent).Message.GetProperty("event").GetString());
    }

    [Fact]
    public void A_payload_without_the_attribute_is_rejected_with_a_clear_message_and_nothing_is_sent()
    {
        var (dispatcher, sink) = Create();

        var ex = Assert.Throws<InvalidOperationException>(() => dispatcher.Emit(new UnmarkedEvent()));

        Assert.Contains("UnmarkedEvent", ex.Message);
        Assert.Contains("[BridgeEvent", ex.Message);
        Assert.Empty(sink.Sent);
        Assert.Throws<InvalidOperationException>(() => dispatcher.EmitTo("main", new UnmarkedEvent()));
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public void EmitTo_still_requires_a_window_label()
    {
        var (dispatcher, _) = Create();

        Assert.Throws<ArgumentException>(() => dispatcher.EmitTo("", new TickEvent()));
    }

    [Fact]
    public void Events_emitted_by_name_still_work_and_are_not_taken_for_typed_payloads()
    {
        var (dispatcher, sink) = Create();

        dispatcher.Emit("adhoc");                                   // a string must stay an event name
        dispatcher.Emit("adhoc", new { value = 1 });
        dispatcher.EmitTo("main", "adhoc");                         // ... in EmitTo as well
        dispatcher.EmitTo("main", "adhoc", new { value = 2 });

        Assert.Equal(4, sink.Sent.Count);
        Assert.All(sink.Sent, s => Assert.Equal("adhoc", s.Message.GetProperty("event").GetString()));
        Assert.False(sink.Sent[0].Message.TryGetProperty("data", out _));
        Assert.Equal(2, sink.Sent[3].Message.GetProperty("data").GetProperty("value").GetInt32());
    }

    [Fact]
    public void Typed_events_without_an_event_sink_are_dropped_without_error()
    {
        var dispatcher = new BridgeDispatcher();

        dispatcher.Emit(new TickEvent());
        dispatcher.EmitTo("main", new TickEvent());
    }
}
