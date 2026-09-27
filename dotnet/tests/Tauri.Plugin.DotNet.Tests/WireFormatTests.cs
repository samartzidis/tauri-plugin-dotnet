using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tauri.Plugin.DotNet.Tests;

/// <summary>
/// The JSON the bridge writes and reads for the types the generator maps to TypeScript. The generator's mapping
/// (TypeMapper) is only right while these hold, so they are pinned here.
/// </summary>
public class WireFormatTests
{
    private static string Write<T>(T value) => JsonSerializer.Serialize(value, BridgeDispatcher.JsonOptions);

    private static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, BridgeDispatcher.JsonOptions);

    // --- types that are plain JSON already: the generator only had to name them ------------------------

    [Fact]
    public void Sets_are_arrays()
    {
        Assert.Equal("[1,2]", Write(new HashSet<int> { 1, 2 }));
        Assert.Equal("[1,2]", Write(new SortedSet<int> { 2, 1 }));
        Assert.Equal("[1]", Write((ISet<int>)new HashSet<int> { 1 }));
        Assert.Equal("[1]", Write((IReadOnlySet<int>)new HashSet<int> { 1 }));
        Assert.Equal(new[] { 1, 2 }, Read<HashSet<int>>("[1,2]")!.OrderBy(x => x));
    }

    [Fact]
    public void Time_and_uri_types_are_strings()
    {
        Assert.Equal("\"1.02:03:04\"", Write(new TimeSpan(1, 2, 3, 4)));
        Assert.Equal("\"2024-01-31\"", Write(new DateOnly(2024, 1, 31)));
        Assert.Equal("\"13:45:30\"", Write(new TimeOnly(13, 45, 30)));
        Assert.Equal("\"https://example.com/a?b=1\"", Write(new Uri("https://example.com/a?b=1")));

        Assert.Equal(new TimeSpan(1, 2, 3, 4), Read<TimeSpan>("\"1.02:03:04\""));
        Assert.Equal(new DateOnly(2024, 1, 31), Read<DateOnly>("\"2024-01-31\""));
        Assert.Equal(new TimeOnly(13, 45, 30), Read<TimeOnly>("\"13:45:30\""));
        Assert.Equal(new Uri("https://example.com/a?b=1"), Read<Uri>("\"https://example.com/a?b=1\""));
    }

    [Fact]
    public void JsonElement_passes_any_json_through()
    {
        var element = Read<JsonElement>("""{"a":[1,"x",null]}""");

        Assert.Equal("""{"a":[1,"x",null]}""", Write(element));
    }

    [Fact]
    public void KeyValuePair_is_an_object_with_key_and_value()
    {
        Assert.Equal("""{"key":"a","value":1}""", Write(new KeyValuePair<string, int>("a", 1)));
        Assert.Equal(new KeyValuePair<string, int>("a", 1), Read<KeyValuePair<string, int>>("""{"key":"a","value":1}"""));
    }

    // --- converters: what [JsonConverter] does to the wire, which the generator types (or gives up on) -------------------

    public enum Tone { Red, Green }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum Level { Low, High }

    [Flags, JsonConverter(typeof(JsonStringEnumConverter))]
    public enum Perms { None = 0, Read = 1, Write = 2 }

    public class MoneyConverter : JsonConverter<Money>
    {
        public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var parts = reader.GetString()!.Split(' ');
            return new Money { Amount = decimal.Parse(parts[0]), Currency = parts[1] };
        }

        public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
            => writer.WriteStringValue($"{value.Amount} {value.Currency}");
    }

    [JsonConverter(typeof(MoneyConverter))]
    public class Money
    {
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "";
    }

    public class Paint
    {
        [JsonConverter(typeof(JsonStringEnumConverter))] public Tone Chosen { get; set; }
        public Tone Plain { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))] public Tone? Maybe { get; set; }
        public Level Level { get; set; }
        public Perms Perms { get; set; }
        public Money Price { get; set; } = new();
    }

    [Fact]
    public void String_enum_converter_writes_the_member_name_where_it_is_applied_and_a_number_elsewhere()
    {
        var paint = new Paint { Chosen = Tone.Green, Plain = Tone.Green, Level = Level.High, Perms = Perms.Read | Perms.Write, Price = new() { Amount = 12.5m, Currency = "EUR" } };

        // Chosen: property-level converter. Plain: the same enum without one. Level: the converter is on the enum type.
        // Maybe is null and omitted. A [Flags] enum is the member names joined with ", ".
        Assert.Equal("""{"chosen":"Green","plain":1,"level":"High","perms":"Read, Write","price":"12.5 EUR"}""", Write(paint));
    }

    [Fact]
    public void A_nullable_enum_with_the_string_converter_is_the_name_or_omitted()
        => Assert.Equal("""{"chosen":"Red","plain":0,"maybe":"Green","level":"Low","perms":"None","price":"0 "}""",
            Write(new Paint { Maybe = Tone.Green }));

    [Fact]
    public void String_enums_are_read_from_names_and_still_from_numbers()
    {
        var byName = Read<Paint>("""{"chosen":"Green","level":"High","perms":"Read, Write","price":"3 USD"}""")!;
        Assert.Equal(Tone.Green, byName.Chosen);
        Assert.Equal(Level.High, byName.Level);
        Assert.Equal(Perms.Read | Perms.Write, byName.Perms);
        Assert.Equal((3m, "USD"), (byName.Price.Amount, byName.Price.Currency));

        // JsonStringEnumConverter without options allows integers when reading, so a number sent by mistake still works
        var byNumber = Read<Paint>("""{"chosen":1,"level":1,"perms":3}""")!;
        Assert.Equal(Tone.Green, byNumber.Chosen);
        Assert.Equal(Level.High, byNumber.Level);
        Assert.Equal(Perms.Read | Perms.Write, byNumber.Perms);
    }

    [Fact]
    public void A_custom_converter_decides_the_shape_of_its_type()
    {
        // A string, not the {"amount":..,"currency":..} its properties suggest: the generator cannot know that
        Assert.Equal("\"12.5 EUR\"", Write(new Money { Amount = 12.5m, Currency = "EUR" }));
        Assert.Equal("""["1 EUR","2 USD"]""", Write(new List<Money> { new() { Amount = 1, Currency = "EUR" }, new() { Amount = 2, Currency = "USD" } }));
    }

    // --- tuples: System.Text.Json would write {} (ValueTuple) or {"item1":..} (Tuple) -----------------

    [Fact]
    public void ValueTuples_are_arrays()
    {
        Assert.Equal("""[1,"a"]""", Write((1, "a")));
        Assert.Equal("""[1,"a",true]""", Write((1, "a", true)));
        Assert.Equal((1, "a"), Read<(int, string)>("""[1,"a"]"""));
        Assert.Equal((1, "a", true), Read<(int, string, bool)>("""[1,"a",true]"""));
    }

    [Fact]
    public void Tuple_classes_are_arrays_too()
    {
        Assert.Equal("""[1,"a"]""", Write(Tuple.Create(1, "a")));
        Assert.Equal(Tuple.Create(1, "a"), Read<Tuple<int, string>>("""[1,"a"]"""));
    }

    [Fact]
    public void Tuples_nest_and_sit_inside_collections_and_models()
    {
        Assert.Equal("""[[1,2],"x"]""", Write(((1, 2), "x")));
        Assert.Equal("""[[1,"a"],[2,"b"]]""", Write(new List<(int, string)> { (1, "a"), (2, "b") }));
        Assert.Equal("""{"k":[1,2]}""", Write(new Dictionary<string, (int, int)> { ["k"] = (1, 2) }));
        Assert.Equal("""{"pair":[1,"a"]}""", Write(new HasTuple { Pair = (1, "a") }));

        Assert.Equal((1, 2), Read<Dictionary<string, (int, int)>>("""{"k":[1,2]}""")!["k"]);
        Assert.Equal(("x", (1, 2)), Read<(string, (int, int))>("""["x",[1,2]]"""));
        Assert.Equal((3, "c"), Read<HasTuple>("""{"pair":[3,"c"]}""")!.Pair);
    }

    [Fact]
    public void Tuple_items_use_the_bridges_own_options()
    {
        // camelCase property names and omitted nulls apply inside a tuple, and a null item is kept as null
        Assert.Equal("""[{"firstName":"Ada"},null]""", Write((new Person { FirstName = "Ada" }, (string?)null)));
        Assert.Equal("Ada", Read<(Person, string?)>("""[{"FIRSTNAME":"Ada"},null]""").Item1.FirstName);
    }

    [Fact]
    public void Nullable_tuple_is_null_or_an_array()
    {
        Assert.Equal("null", Write((ValueTuple<int, string>?)null));
        Assert.Null(Read<(int, string)?>("null"));
        Assert.Equal((1, "a"), Read<(int, string)?>("""[1,"a"]"""));
    }

    [Theory]
    [InlineData("""{"item1":1,"item2":"a"}""")] // not an array
    [InlineData("""[1]""")]                       // too short
    [InlineData("""[1,"a","extra"]""")]           // too long
    [InlineData("""[null,"a"]""")]                // null for an int
    [InlineData("""["x","a"]""")]                 // wrong item type
    public void Malformed_tuples_are_rejected(string json)
        => Assert.Throws<JsonException>(() => Read<(int, string)>(json));

    [Fact]
    public void Tuples_of_more_than_seven_items_are_refused_not_dropped()
    {
        var tuple = (1, 2, 3, 4, 5, 6, 7, 8);

        var ex = Assert.Throws<NotSupportedException>(() => Write(tuple));
        Assert.Contains("up to 7", ex.Message);
    }

    // --- through the dispatcher: arguments in, results out ---------------------------------------------

    [Fact]
    public async Task Tuple_arguments_and_results_travel_as_arrays_through_a_service()
    {
        var dispatcher = new BridgeDispatcher();
        dispatcher.RegisterService(new TupleService());

        var json = await dispatcher.InvokeJsonAsync("""{"callId":"c1","method":"TupleService.Swap","args":[[1,"a"]]}""");

        Assert.Equal("""{"callId":"c1","result":["a",1]}""", json);
    }

    [Fact]
    public async Task A_malformed_tuple_argument_comes_back_as_an_error_response()
    {
        var dispatcher = new BridgeDispatcher();
        dispatcher.RegisterService(new TupleService());

        var json = await dispatcher.InvokeJsonAsync("""{"callId":"c1","method":"TupleService.Swap","args":[[1]]}""");

        Assert.Contains("\"error\"", json);
        Assert.Contains("needs 2 item(s)", json);
    }

    public class HasTuple
    {
        public (int, string) Pair { get; set; }
    }

    [BridgeService]
    public class TupleService
    {
        public (string, int) Swap((int, string) pair) => (pair.Item2, pair.Item1);
    }
}
