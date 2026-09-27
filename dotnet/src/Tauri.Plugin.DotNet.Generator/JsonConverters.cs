using System.Reflection;
using System.Text.Json.Serialization;

namespace Tauri.Plugin.DotNet.Generator;

/// <summary>
/// What a <c>[JsonConverter]</c> does to the JSON of a type or property, as far as the generator can tell.
/// </summary>
enum ConverterKind
{
    /// <summary>No converter: the JSON follows the C# shape.</summary>
    None,

    /// <summary>The parameterless <c>JsonStringEnumConverter</c>: an enum is written as the name of its member, as declared.</summary>
    StringEnum,

    /// <summary>Any other converter: it decides the JSON, and the generator cannot know what that is.</summary>
    Custom,
}

static class JsonConverters
{
    private static readonly string ConverterAttribute = typeof(JsonConverterAttribute).FullName!;
    private static readonly string StringEnumConverter = typeof(JsonStringEnumConverter).FullName!;
    private static readonly string GenericStringEnumConverter = typeof(JsonStringEnumConverter<>).FullName!;

    /// <summary>Classifies the <c>[JsonConverter]</c> among the attributes of a type or property.</summary>
    internal static ConverterKind Classify(IEnumerable<CustomAttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (!IsConverterAttribute(attribute.AttributeType)) continue;

            // [JsonConverter(typeof(X))]. A derived attribute has no type argument and picks its converter in code.
            if (attribute.ConstructorArguments.FirstOrDefault().Value is not Type converter)
                return ConverterKind.Custom;

            return IsStringEnumConverter(converter) ? ConverterKind.StringEnum : ConverterKind.Custom;
        }

        return ConverterKind.None;
    }

    private static bool IsConverterAttribute(Type type)
    {
        for (var t = type; t != null; t = t.BaseType)
            if (t.FullName == ConverterAttribute)
                return true;
        return false;
    }

    // The attribute cannot pass constructor arguments, so this is the converter without a naming policy:
    // names as declared, and numbers still accepted when reading.
    private static bool IsStringEnumConverter(Type converter) =>
        converter.FullName == StringEnumConverter ||
        (converter.IsGenericType && converter.GetGenericTypeDefinition().FullName == GenericStringEnumConverter);

    internal static bool IsStringEnum(Type type) => Classify(type.CustomAttributes) == ConverterKind.StringEnum;

    internal static bool IsFlags(Type type) => type.CustomAttributes.Any(a => a.AttributeType.FullName == typeof(FlagsAttribute).FullName);

    /// <summary>The type inside a <c>Nullable&lt;T&gt;</c>, or the type itself.</summary>
    internal static Type UnwrapNullable(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition().FullName == typeof(Nullable<>).FullName
            ? type.GetGenericArguments()[0]
            : type;

    /// <summary>The names of an enum's members, as JsonStringEnumConverter writes them.</summary>
    internal static List<string> MemberNames(Type enumType) =>
        enumType.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name).ToList();
}
