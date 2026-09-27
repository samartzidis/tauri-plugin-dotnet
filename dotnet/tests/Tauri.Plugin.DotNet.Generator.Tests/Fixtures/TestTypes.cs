using System.Text.Json.Serialization;

namespace Tauri.Plugin.DotNet.Generator.Tests.Fixtures;

// ==================== Services ====================

[BridgeService]
public class BasicService
{
    public string Greet(string name) => $"Hello, {name}!";
    public int Add(int a, int b) => a + b;
    public void DoNothing() { }
}

[BridgeService(Name = "CustomApi")]
public class CustomNamedService
{
    public string Echo(string msg) => msg;
}

[BridgeService]
public class AsyncService
{
    public Task<string> GetAsync(string name) => Task.FromResult(name);
    public ValueTask<int> GetValueAsync(int x) => new(x);
    public Task DoWorkAsync() => Task.CompletedTask;
}

[BridgeService]
public class IgnoredMethodService
{
    public string Visible() => "visible";

    [BridgeIgnore]
    public string Hidden() => "hidden";
}

[BridgeService]
public class CancellationService
{
    public Task<string> SlowMethod(int seconds, CancellationToken ct = default)
        => Task.FromResult("done");

    public Task<string> MultiParam(string name, int count, CancellationToken ct = default)
        => Task.FromResult(name);
}

/// <summary>Stand-in for Tauri.Plugin.DotNet.CallContext; generator filters params by name "CallContext".</summary>
public class CallContext
{
    public object? Window => null;
}

[BridgeService]
public class CallContextService
{
    public string GetTitle(CallContext ctx) => "Title";
    public string GetTitleWithArg(CallContext ctx, string name) => name;
}

[BridgeService]
public class EmptyService
{
}

[BridgeService]
public class AllIgnoredService
{
    [BridgeIgnore]
    public string Hidden1() => "";
    [BridgeIgnore]
    public void Hidden2() { }
}

[BridgeService]
public class ModelService
{
    public SimpleModel GetModel() => new();
    public DerivedModel GetDerived() => new();
    public NullableModel GetNullable() => new();
    public JsonCustomModel GetJsonCustom() => new();
    public List<SimpleModel> GetModels() => new();
    public Dictionary<string, int> GetDict() => new();
    public ModelWithEnum GetWithEnum() => new();
    public byte[] EchoBytes(byte[] data) => data;
    public EmptyModel GetEmptyModel() => new();
    public EdgeCaseEnum GetEdgeCaseEnum() => EdgeCaseEnum.Zero;
    public TypeWithOptionalFromContext GetTypeWithOptionalContext() => new();
}

[BridgeService]
public class GenericService
{
    public Page<SimpleModel> GetPage() => new();
    public Task<Page<SimpleModel>> GetPageAsync() => Task.FromResult(new Page<SimpleModel>());
    public Pair<string, int> GetPair(Page<string> page) => new();
    public Envelope<int> GetEnvelope() => new();
    public SimpleModelPage GetSimplePage() => new();
    public Node GetNode() => new();
}

[BridgeService]
public class LargeNumberService
{
    public LargeNumberModel GetModel() => new();
    public AllStringsModel GetAllStrings() => new();
}

[BridgeService]
public class WireTypeService
{
    public HashSet<string> GetTags() => new();
    public TimeSpan GetDuration() => default;
    public DateOnly GetDay() => default;
    public TimeOnly GetTime() => default;
    public Uri GetLink() => new("https://example.com");
    public System.Text.Json.JsonElement GetJson() => default;
    public KeyValuePair<string, int> GetEntry() => default;
    public (int, string) GetPair() => default;
    public (string, int) Swap((int, string) pair) => (pair.Item2, pair.Item1);
    public List<(int, string)> GetPairs() => new();
    public (SimpleModel, int) GetModelPair() => default;
    public List<int?> GetMaybeNumbers() => new();
    public WireModel GetModel() => new();
}

/// <summary>Properties of the types that travel as something other than their C# shape.</summary>
public class WireModel
{
    public HashSet<int> Ids { get; set; } = new();
    public TimeSpan Elapsed { get; set; }
    public DateOnly? Day { get; set; }
    public Uri? Link { get; set; }
    public System.Text.Json.JsonElement Extra { get; set; }
    public (int, SimpleModel) Pair { get; set; }
    public List<KeyValuePair<string, int>> Entries { get; set; } = new();
    public (int, int, int, int, int, int, int, int) TooBig { get; set; }
}

/// <summary>
/// Overloads and names that differ only in case. Deliberately NOT marked [BridgeService]: the whole-assembly tests would
/// fail on it, so the tests build it directly with ServiceDiscovery.BuildService.
/// </summary>
public class OverloadedFixture
{
    public string Foo(string a) => a;
    public string Foo(string a, int b) => a;
    public string Baz() => "";
    public string baz() => "";
    public string Single() => "";

    // One overload is hidden, so there is only one exposed method named Hidden
    [BridgeIgnore] public string Hidden(int x) => "";
    public string Hidden() => "";
}

/// <summary>An overload hidden with [BridgeIgnore] leaves one exposed method per name, which is fine.</summary>
public class IgnoredOverloadFixture
{
    [BridgeIgnore] public string Hidden(int x) => "";
    public string Hidden() => "";
    public string Other() => "";
}

/// <summary>Not marked with [BridgeFrontend], for the same reason as <see cref="OverloadedFixture"/>.</summary>
public interface IOverloadedFrontend
{
    Task<string> Ask(string question);
    Task<string> Ask(string question, int retries);
}

[BridgeService]
public class ConverterService
{
    public ConverterModel GetModel() => new();
    public StringLevel GetLevel() => StringLevel.Low;
    public void SetLevel(StringLevel level) { }
    public Money GetMoney() => new();
    public List<Money> GetMoneys() => new();
    public StringPerms GetPerms() => StringPerms.Read;
}

/// <summary>Not marked with [BridgeService] — should be ignored.</summary>
public class NotAService
{
    public string NotExposed() => "";
}

// ==================== Models ====================

public class SimpleModel
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

public class NullableModel
{
    public string Required { get; set; } = "";
    public string? Optional { get; set; }
    public int? NullableInt { get; set; }
}

public class JsonCustomModel
{
    [JsonPropertyName("custom_name")]
    public string CustomName { get; set; } = "";

    [JsonIgnore]
    public string Secret { get; set; } = "";

    public int Visible { get; set; }
}

public class BaseModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class DerivedModel : BaseModel
{
    public string Extra { get; set; } = "";
}

public class ModelWithCollections
{
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, int> Scores { get; set; } = new();
    public int[] Numbers { get; set; } = Array.Empty<int>();
}

// ---- Generic models ----

public class Page<T>
{
    public List<T> Items { get; set; } = new();
    public int Total { get; set; }
    public T? Latest { get; set; }
}

public class Pair<TKey, TValue>
{
    public TKey Key { get; set; } = default!;
    public TValue Value { get; set; } = default!;
}

/// <summary>A generic that uses other generics with its own parameter.</summary>
public class Envelope<T>
{
    public Page<T> Inner { get; set; } = new();
    public Pair<string, List<T>> Extra { get; set; } = new();
}

/// <summary>A generic base class with a concrete argument.</summary>
public class SimpleModelPage : Page<SimpleModel>
{
    public string Title { get; set; } = "";
}

public class Tree<T>
{
    public List<T> Children { get; set; } = new();
}

/// <summary>Names itself in its own base type; the generator must not recurse forever.</summary>
public class Node : Tree<Node>
{
    public string Label { get; set; } = "";
}

// ---- Converters ----

/// <summary>Numeric unless a property says otherwise.</summary>
public enum Shade { Light, Dark }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StringLevel { Low, High }

[JsonConverter(typeof(JsonStringEnumConverter<StringGeneric>))]
public enum StringGeneric { A, B }

[Flags, JsonConverter(typeof(JsonStringEnumConverter))]
public enum StringPerms { None = 0, Read = 1, Write = 2 }

[Flags]
public enum PlainFlags { None = 0, Left = 1, Right = 2 }

public class MoneyConverter : JsonConverter<Money>
{
    public override Money Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) => new();
    public override void Write(System.Text.Json.Utf8JsonWriter writer, Money value, System.Text.Json.JsonSerializerOptions options) => writer.WriteStringValue($"{value.Amount} {value.Currency}");
}

/// <summary>Written as a string such as "12.5 EUR" by its converter, not as an object.</summary>
[JsonConverter(typeof(MoneyConverter))]
public class Money
{
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
}

public class SecondsConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) => TimeSpan.FromSeconds(reader.GetDouble());
    public override void Write(System.Text.Json.Utf8JsonWriter writer, TimeSpan value, System.Text.Json.JsonSerializerOptions options) => writer.WriteNumberValue(value.TotalSeconds);
}

public class ConverterModel
{
    /// <summary>JsonStringEnumConverter on the property: a union of the member names.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Shade Tone { get; set; }

    /// <summary>The same enum without the converter stays numeric.</summary>
    public Shade Plain { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Shade? MaybeTone { get; set; }

    /// <summary>The enum type is a string enum by itself: its name, whatever the property says.</summary>
    public StringLevel Level { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public StringLevel LevelAgain { get; set; }

    public StringGeneric Generic { get; set; }

    public StringPerms Perms { get; set; }

    /// <summary>A [Flags] enum with the converter on the property: no union can say "Left, Right".</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PlainFlags Sides { get; set; }

    public Money Price { get; set; } = new();
    public List<Money> Prices { get; set; } = new();
    public Dictionary<string, Money> ByName { get; set; } = new();

    /// <summary>A custom converter on the property.</summary>
    [JsonConverter(typeof(SecondsConverter))]
    public TimeSpan Timeout { get; set; }

    /// <summary>JsonStringEnumConverter means nothing on an int (System.Text.Json refuses it), so it is ignored.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public int NotAnEnum { get; set; }
}

// ---- Numbers written as strings ----

/// <summary>Properties that keep their digits exact through JavaScript by travelling as JSON strings.</summary>
public class LargeNumberModel
{
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public long Id { get; set; }

    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public decimal Amount { get; set; }

    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? Optional { get; set; }

    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public List<long> Ids { get; set; } = new();

    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public Dictionary<string, long> Totals { get; set; } = new();

    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public Page<long> Nested { get; set; } = new();

    /// <summary>No attribute: stays a number.</summary>
    public long Plain { get; set; }

    /// <summary>Only reading from strings is allowed; the value is still written as a number.</summary>
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long ReadsStrings { get; set; }
}

/// <summary>The attribute on the type applies to all its properties, unless a property says otherwise.</summary>
[JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
public class AllStringsModel
{
    public long A { get; set; }
    public int B { get; set; }
    public string Name { get; set; } = "";

    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public long StillNumber { get; set; }
}

public enum TestEnum
{
    None = 0,
    First = 1,
    Second = 2
}

public enum EdgeCaseEnum
{
    Zero = 0,
    Negative = -1,
    SameAsZero = 0
}

public class EmptyModel
{
}

/// <summary>Used to assert type-level nullable context is detected (NullableContextAttribute on type).</summary>
public class TypeWithOptionalFromContext
{
    public string? OptionalFromContext { get; set; }
}

public class ModelWithEnum
{
    public TestEnum Status { get; set; }
}

// ==================== Events ====================

[BridgeEvent("test_event")]
public class TestEvent
{
    public int Count { get; set; }
    public string Message { get; set; } = "";
}

[BridgeEvent("another")]
public class AnotherEvent
{
    public string Data { get; set; } = "";
}

/// <summary>Not marked with [BridgeEvent] — should be ignored.</summary>
public class NotAnEvent
{
    public string Whatever { get; set; } = "";
}

// ==================== Nullable annotations ====================

[BridgeService]
public class NullableService
{
    public string? Find(string? key, string name) => key;
    public Task<string?> FindAsync(string name) => Task.FromResult<string?>(null);
    public Task<string> Required(string name) => Task.FromResult(name);
    public int? Count(int? limit) => limit;
    public Task DoIt(string? note) => Task.CompletedTask;
    public SimpleModel? FindModel(SimpleModel? seed) => seed;
}

// ==================== Frontend interfaces ====================

public class ShellChoice
{
    public string Label { get; set; } = "";
}

[BridgeFrontend]
public interface IShell
{
    Task<string?> PickFile(string title, string? filter);
    Task Notify(string text, CancellationToken cancellationToken);
    Task<ShellChoice> Choose(string question, List<string> options);
}

[BridgeFrontend(Name = "Windowing")]
public interface IWindowing
{
    Task<int> Count();
}

/// <summary>Not marked with [BridgeFrontend] — should be ignored.</summary>
public interface INotAFrontend
{
    Task Ping();
}
