using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tauri.Plugin.DotNet;

/// <summary>
/// Writes a tuple, <c>(int, string)</c> or <c>Tuple&lt;int, string&gt;</c>, as a JSON array <c>[1,"a"]</c>, which the
/// generated TypeScript calls <c>[number, string]</c>. Without this, System.Text.Json drops the items of a
/// <see cref="ValueTuple"/> (they are fields, and the bridge's options do not include fields) and writes a
/// <see cref="Tuple"/> as <c>{"item1":1,"item2":"a"}</c>. Tuples of more than seven items are refused, not dropped silently.
/// </summary>
internal sealed class TupleJsonConverterFactory : JsonConverterFactory
{
    private const int MaxItems = 7;

    public override bool CanConvert(Type typeToConvert) => IsTuple(typeToConvert);

    private static bool IsTuple(Type type) =>
        type.IsGenericType &&
        type.GetGenericTypeDefinition() is { Namespace: "System" } definition &&
        (definition.Name.StartsWith("ValueTuple`", StringComparison.Ordinal) || definition.Name.StartsWith("Tuple`", StringComparison.Ordinal));

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var itemTypes = typeToConvert.GetGenericArguments();
        if (itemTypes.Length > MaxItems)
            throw new NotSupportedException(
                $"A tuple of {itemTypes.Length} items ({typeToConvert.Name}) cannot be sent over the bridge: only tuples of up to {MaxItems} items are supported. Use a class or record instead.");

        return (JsonConverter)Activator.CreateInstance(typeof(TupleConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class TupleConverter<T> : JsonConverter<T>
    {
        private readonly Type[] _itemTypes = typeof(T).GetGenericArguments();
        private readonly Func<T, object?>[] _getters;

        public TupleConverter()
        {
            // A ValueTuple keeps its items in fields Item1..ItemN, a Tuple in properties of the same names
            _getters = _itemTypes.Select((_, i) =>
            {
                var name = $"Item{i + 1}";
                if (typeof(T).GetField(name) is { } field)
                    return (Func<T, object?>)(tuple => field.GetValue(tuple));
                var property = typeof(T).GetProperty(name)!;
                return tuple => property.GetValue(tuple);
            }).ToArray();
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            for (var i = 0; i < _itemTypes.Length; i++)
                JsonSerializer.Serialize(writer, _getters[i](value), _itemTypes[i], options);
            writer.WriteEndArray();
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException($"Expected a JSON array of {_itemTypes.Length} item(s) for the tuple {typeToConvert.Name}.");

            var items = new object?[_itemTypes.Length];
            for (var i = 0; i < items.Length; i++)
            {
                if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
                    throw new JsonException($"The tuple {typeToConvert.Name} needs {items.Length} item(s), but the JSON array has {i}.");

                items[i] = JsonSerializer.Deserialize(ref reader, _itemTypes[i], options);
                if (items[i] is null && _itemTypes[i].IsValueType && Nullable.GetUnderlyingType(_itemTypes[i]) is null)
                    throw new JsonException($"Item {i + 1} of the tuple {typeToConvert.Name} cannot be null.");
            }

            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException($"The tuple {typeToConvert.Name} needs {items.Length} item(s), but the JSON array has more.");

            return (T)Activator.CreateInstance(typeof(T), items)!;
        }
    }
}
