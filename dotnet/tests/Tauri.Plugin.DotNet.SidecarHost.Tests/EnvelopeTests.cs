using System.Text.Json;
using Tauri.Plugin.DotNet.SidecarHost;

namespace Tauri.Plugin.DotNet.SidecarHost.Tests;

public class EnvelopeTests
{
    [Fact]
    public void Ready_round_trips_with_the_kind_discriminator_only()
    {
        var json = JsonSerializer.Serialize<Envelope>(new ReadyEnvelope(), Envelope.JsonOptions);

        Assert.Equal("""{"kind":"ready"}""", json);
        Assert.IsType<ReadyEnvelope>(JsonSerializer.Deserialize<Envelope>(json, Envelope.JsonOptions));
    }

    [Fact]
    public void Error_round_trips_with_camel_case_fields()
    {
        Envelope original = new ErrorEnvelope("boom", "InvalidOperationException");

        var json = JsonSerializer.Serialize(original, Envelope.JsonOptions);
        var result = Assert.IsType<ErrorEnvelope>(JsonSerializer.Deserialize<Envelope>(json, Envelope.JsonOptions));

        Assert.Contains("\"message\":\"boom\"", json);
        Assert.Contains("\"type\":\"InvalidOperationException\"", json);
        Assert.Equal(original, result);
    }

    [Fact]
    public void Call_round_trips_with_the_verbatim_wire_json_untouched()
    {
        Envelope original = new CallEnvelope("c1", "main", """{"callId":"c1","method":"Svc.Do","args":[]}""");

        var json = JsonSerializer.Serialize(original, Envelope.JsonOptions);
        var result = Assert.IsType<CallEnvelope>(JsonSerializer.Deserialize<Envelope>(json, Envelope.JsonOptions));

        Assert.Equal(original, result);
    }

    [Fact]
    public void Response_round_trips()
    {
        Envelope original = new ResponseEnvelope("c1", """{"callId":"c1","result":null}""");

        var json = JsonSerializer.Serialize(original, Envelope.JsonOptions);

        Assert.Equal(original, JsonSerializer.Deserialize<Envelope>(json, Envelope.JsonOptions));
    }

    [Fact]
    public void Cancel_round_trips()
    {
        Envelope original = new CancelEnvelope("c1");

        var json = JsonSerializer.Serialize(original, Envelope.JsonOptions);

        Assert.Equal(original, JsonSerializer.Deserialize<Envelope>(json, Envelope.JsonOptions));
    }

    [Fact]
    public void Event_round_trips_with_a_window_label()
    {
        Envelope original = new EventEnvelope("main", """{"event":"progress","data":{"percent":50}}""");

        var json = JsonSerializer.Serialize(original, Envelope.JsonOptions);

        Assert.Equal(original, JsonSerializer.Deserialize<Envelope>(json, Envelope.JsonOptions));
    }

    [Fact]
    public void Event_round_trips_with_no_window_label()
    {
        Envelope original = new EventEnvelope(null, """{"event":"closed"}""");

        var json = JsonSerializer.Serialize(original, Envelope.JsonOptions);
        var result = Assert.IsType<EventEnvelope>(JsonSerializer.Deserialize<Envelope>(json, Envelope.JsonOptions));

        Assert.Null(result.WindowLabel);
    }

    [Fact]
    public void Shutdown_round_trips_with_a_camel_case_timeout_field()
    {
        Envelope original = new ShutdownEnvelope(5000);

        var json = JsonSerializer.Serialize(original, Envelope.JsonOptions);

        Assert.Contains("\"timeoutMs\":5000", json);
        Assert.Equal(original, JsonSerializer.Deserialize<Envelope>(json, Envelope.JsonOptions));
    }
}
