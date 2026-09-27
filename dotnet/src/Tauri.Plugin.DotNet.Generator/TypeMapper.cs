namespace Tauri.Plugin.DotNet.Generator;

static class TypeMapper
{
    // No typeof(void) in C#; use constant for return type void
    private const string VoidFullName = "System.Void";

    internal static readonly HashSet<string> TaskTypeNames = new()
    {
        typeof(Task).FullName!,
        typeof(ValueTask).FullName!
    };

    internal static readonly HashSet<string> GenericTaskTypeNames = new()
    {
        typeof(Task<>).FullName!,
        typeof(ValueTask<>).FullName!
    };

    /// <param name="numbersAsString">
    /// The numbers directly inside this type (a number, a nullable one, or the elements of an array, list, set or dictionary)
    /// are written as JSON strings, so they map to <c>string</c>. It does not reach into other models, tuples, key/value
    /// pairs or the arguments of a user-defined generic: their own properties or converters decide.
    /// </param>
    /// <param name="converter">
    /// What a <c>[JsonConverter]</c> on the property this type belongs to does. A custom converter makes the whole property
    /// <c>unknown</c>; <c>JsonStringEnumConverter</c> makes an enum a union of its member names (see also the converter on
    /// the enum type itself, which is read from the type).
    /// </param>
    internal static string MapTypeToTS(Type type, Dictionary<string, TypeDef> models, bool numbersAsString = false,
        ConverterKind converter = ConverterKind.None)
    {
        // The T in a generic model (Page<T>) keeps its name in the TypeScript interface
        if (type.IsGenericParameter)
            return type.Name;

        // The converter on a property decides what its JSON is, and the generator cannot see that
        if (converter == ConverterKind.Custom)
            return "unknown";

        // Handle void
        if (type.FullName == VoidFullName)
            return "void";

        // Handle Task / Task<T> / ValueTask / ValueTask<T>
        if (IsTaskType(type))
        {
            if (type.IsGenericType)
            {
                var inner = UnwrapTaskType(type);
                return MapTypeToTS(inner, models);
            }
            // Non-generic Task or ValueTask → void
            return "void";
        }

        // Handle Nullable<T>
        if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == typeof(Nullable<>).FullName)
        {
            var inner = type.GetGenericArguments()[0];
            return $"{MapTypeToTS(inner, models, numbersAsString, converter)} | null";
        }

        // Numbers are written as JSON strings under [JsonNumberHandling(WriteAsString)]
        var number = numbersAsString ? "string" : "number";

        // Primitives (type-safe full names)
        var mapped = type.FullName switch
        {
            _ when type.FullName == typeof(string).FullName => "string",
            _ when type.FullName == typeof(bool).FullName => "boolean",
            _ when type.FullName == typeof(byte).FullName || type.FullName == typeof(sbyte).FullName => number,
            _ when type.FullName == typeof(short).FullName || type.FullName == typeof(ushort).FullName => number,
            _ when type.FullName == typeof(int).FullName || type.FullName == typeof(uint).FullName => number,
            _ when type.FullName == typeof(long).FullName || type.FullName == typeof(ulong).FullName => number,
            _ when type.FullName == typeof(float).FullName || type.FullName == typeof(double).FullName || type.FullName == typeof(decimal).FullName => number,
            _ when type.FullName == typeof(DateTime).FullName || type.FullName == typeof(DateTimeOffset).FullName => "string",
            _ when type.FullName == typeof(TimeSpan).FullName => "string",   // "1.02:03:04"
            _ when type.FullName == typeof(DateOnly).FullName => "string",   // "2024-01-31"
            _ when type.FullName == typeof(TimeOnly).FullName => "string",   // "13:45:30"
            _ when type.FullName == typeof(Uri).FullName => "string",
            _ when type.FullName == typeof(Guid).FullName => "string",
            _ when type.FullName == typeof(object).FullName => "unknown",
            // Any JSON at all: unknown is the honest type, and stating it keeps it from being an accident of the fallback
            _ when type.FullName == typeof(System.Text.Json.JsonElement).FullName => "unknown",
            _ when type.FullName == typeof(System.Text.Json.Nodes.JsonNode).FullName => "unknown",
            _ when type.FullName == typeof(System.Text.Json.Nodes.JsonValue).FullName => "unknown",
            _ when type.FullName == typeof(System.Text.Json.Nodes.JsonObject).FullName => "Record<string, unknown>",
            _ when type.FullName == typeof(System.Text.Json.Nodes.JsonArray).FullName => "unknown[]",
            _ => null
        };
        if (mapped != null) return mapped;

        // byte[] is special: System.Text.Json serializes it as a base64 string
        if (type.IsArray && type.GetElementType()?.FullName == typeof(byte).FullName)
        {
            return "string";
        }

        // A type with a custom [JsonConverter] writes whatever its converter writes (an enum with a custom converter too)
        if (!type.IsArray &&
            JsonConverters.Classify((type.IsGenericType ? type.GetGenericTypeDefinition() : type).CustomAttributes) == ConverterKind.Custom)
            return "unknown";

        // Arrays (non-byte)
        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            return ArrayOf(MapTypeToTS(elementType, models, numbersAsString));
        }

        // List<T>, IList<T>, IEnumerable<T>, ICollection<T>
        if (type.IsGenericType)
        {
            var genDef = type.GetGenericTypeDefinition().FullName;
            var genArgs = type.GetGenericArguments();

            if (genDef is {} listLike && (
                listLike == typeof(List<>).FullName ||
                listLike == typeof(IList<>).FullName ||
                listLike == typeof(IEnumerable<>).FullName ||
                listLike == typeof(ICollection<>).FullName ||
                listLike == typeof(IReadOnlyList<>).FullName ||
                listLike == typeof(IReadOnlyCollection<>).FullName ||
                // Sets are JSON arrays too (a set read from JSON must be a concrete or ISet type: IReadOnlySet<T> cannot be read)
                listLike == typeof(HashSet<>).FullName ||
                listLike == typeof(SortedSet<>).FullName ||
                listLike == typeof(ISet<>).FullName ||
                listLike == typeof(IReadOnlySet<>).FullName))
            {
                return ArrayOf(MapTypeToTS(genArgs[0], models, numbersAsString));
            }

            // KeyValuePair<K,V> is an object with key and value (Dictionary entries are not: they are Record<K, V>)
            if (genDef == typeof(KeyValuePair<,>).FullName)
                return $"{{ key: {MapTypeToTS(genArgs[0], models)}; value: {MapTypeToTS(genArgs[1], models)} }}";

            // Tuples are JSON arrays, (int, string) is [number, string]; the runtime refuses more than 7 items
            if (IsTuple(type.GetGenericTypeDefinition()))
                return genArgs.Length > MaxTupleItems
                    ? "unknown"
                    : $"[{string.Join(", ", genArgs.Select(a => MapTypeToTS(a, models)))}]";

            // Dictionary<K,V>
            if (genDef is {} dictLike && (
                dictLike == typeof(Dictionary<,>).FullName ||
                dictLike == typeof(IDictionary<,>).FullName ||
                dictLike == typeof(IReadOnlyDictionary<,>).FullName))
            {
                var keyTs = MapTypeToTS(genArgs[0], models);
                var valTs = MapTypeToTS(genArgs[1], models, numbersAsString);
                return $"Record<{keyTs}, {valTs}>";
            }

            // A user-defined generic (Page<Person>): the interface of its definition, with the arguments applied
            if (models.TryGetValue(genDef ?? "", out var definition))
            {
                var args = string.Join(", ", genArgs.Select(a => MapTypeToTS(a, models)));
                return $"{definition.Name}<{args}>";
            }
        }

        // Enums
        if (type.IsEnum)
        {
            // JsonStringEnumConverter on the property (the enum type is not a string enum itself): the member names.
            // A [Flags] enum is written as names joined with ", ", which no union of names can express.
            if (converter == ConverterKind.StringEnum && !JsonConverters.IsStringEnum(type))
            {
                var names = JsonConverters.MemberNames(type);
                return JsonConverters.IsFlags(type) || names.Count == 0
                    ? "string"
                    : string.Join(" | ", names.Select(n => $"\"{n}\""));
            }

            return type.Name;
        }

        // Known model type
        if (models.ContainsKey(type.FullName ?? type.Name))
        {
            return type.Name;
        }

        // Fallback
        return "unknown";
    }

    /// <summary>The most items a tuple can have to be sent over the bridge (see TupleJsonConverterFactory in the library).</summary>
    private const int MaxTupleItems = 7;

    /// <summary>System.ValueTuple&lt;...&gt; or System.Tuple&lt;...&gt;. Matches the runtime's rule, which is by name too.</summary>
    private static bool IsTuple(Type genericDefinition) =>
        genericDefinition.Namespace == "System" &&
        (genericDefinition.Name.StartsWith("ValueTuple`", StringComparison.Ordinal) ||
         genericDefinition.Name.StartsWith("Tuple`", StringComparison.Ordinal));

    /// <summary>"T[]", or "(A | null)[]" for a nullable element: "A | null[]" would mean something else.</summary>
    private static string ArrayOf(string element) =>
        element.EndsWith(" | null", StringComparison.Ordinal) ? $"({element})[]" : $"{element}[]";

    internal static bool IsTaskType(Type type)
    {
        if (type.FullName != null && TaskTypeNames.Contains(type.FullName)) return true;
        if (type.IsGenericType && type.GetGenericTypeDefinition().FullName is { } gn && GenericTaskTypeNames.Contains(gn)) return true;
        return false;
    }

    internal static Type UnwrapTaskType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition().FullName is { } gn && GenericTaskTypeNames.Contains(gn))
            return type.GetGenericArguments()[0];

        // Task / ValueTask with no result => void-equivalent
        if (type.FullName != null && TaskTypeNames.Contains(type.FullName))
        {
            return type; // caller checks IsTaskType
        }

        return type;
    }
}
