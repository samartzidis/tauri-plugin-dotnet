using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Tauri.Plugin.DotNet.Generator;

static class ModelCollector
{
    internal static void CollectModels(Type type, Dictionary<string, TypeDef> models, Assembly sourceAssembly)
    {
        // Unwrap nullables, arrays, generics
        if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == typeof(Nullable<>).FullName)
        {
            CollectModels(type.GetGenericArguments()[0], models, sourceAssembly);
            return;
        }

        if (type.IsArray)
        {
            CollectModels(type.GetElementType()!, models, sourceAssembly);
            return;
        }

        if (type.IsGenericType)
        {
            // A user-defined generic (Page<Person>) is one model, collected through its definition; framework
            // generics (List<T>, Task<T>, ...) are skipped by AddModel and only contribute their arguments.
            AddModel(type.IsGenericTypeDefinition ? type : type.GetGenericTypeDefinition(), models, sourceAssembly);
            if (!type.IsGenericTypeDefinition)
                foreach (var ga in type.GetGenericArguments())
                    CollectModels(ga, models, sourceAssembly);
            return;
        }

        AddModel(type, models, sourceAssembly);
    }

    // Types being added right now. A type that names itself in its own base type (class Node : Base<Node>) would
    // otherwise recurse forever: its model is only stored after the base type has been collected.
    [ThreadStatic]
    private static HashSet<string>? _adding;

    /// <summary>Adds a class, struct, enum or generic definition to the models, then everything its properties refer to.</summary>
    private static void AddModel(Type type, Dictionary<string, TypeDef> models, Assembly sourceAssembly)
    {
        // A generic parameter (the T in Page<T>) has no full name
        if (type.FullName == null) return;

        _adding ??= new HashSet<string>();
        if (!_adding.Add(type.FullName)) return;
        try
        {
            AddModelCore(type, models, sourceAssembly);
        }
        finally
        {
            _adding.Remove(type.FullName);
        }
    }

    private static void AddModelCore(Type type, Dictionary<string, TypeDef> models, Assembly sourceAssembly)
    {
        // Skip primitives and system types
        if (type.FullName == null) return;
        if (type.FullName.StartsWith("System.")) return;
        if (type.IsPrimitive) return;
        if (type.IsEnum)
        {
            var enumConverter = JsonConverters.Classify(type.CustomAttributes);
            if (enumConverter == ConverterKind.Custom)
            {
                WarnCustomConverter(type.FullName);
                return;
            }

            // Collect enum. Under JsonStringEnumConverter the values are the member names, not the numbers.
            if (!models.ContainsKey(type.FullName))
            {
                var stringEnum = enumConverter == ConverterKind.StringEnum;
                var enumValues = new List<EnumValueDef>();
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    enumValues.Add(new EnumValueDef(field.Name, stringEnum ? field.Name : field.GetRawConstantValue()));
                }
                models[type.FullName] = new TypeDef(type.Name, type.FullName, TypeDefKind.Enum, null, enumValues,
                    StringEnum: stringEnum, IsFlags: stringEnum && JsonConverters.IsFlags(type));
            }
            return;
        }

        // Only collect types from user assemblies (app + project refs), not framework
        if (IsFrameworkAssembly(type.Assembly)) return;

        // A custom converter decides the JSON of the type, so its properties say nothing about it: it is not a model
        // (uses of it are typed unknown by TypeMapper)
        if (JsonConverters.Classify(type.CustomAttributes) == ConverterKind.Custom)
        {
            WarnCustomConverter(type.FullName);
            return;
        }

        if (models.ContainsKey(type.FullName)) return;

        // Detect base class (if it's a user type, not System.Object etc.)
        Type? modelBase = null;
        var baseType = type.BaseType;
        if (baseType != null &&
            baseType.FullName != null &&
            !baseType.FullName.StartsWith("System.") &&
            !baseType.IsPrimitive &&
            !IsFrameworkAssembly(baseType.Assembly))
        {
            // Ensure the base type is also collected as a model
            CollectModels(baseType, models, sourceAssembly);
            modelBase = baseType;
        }

        // Collect only properties declared on this type (not inherited).
        // Base class properties are emitted via the `extends` clause in TS.
        var properties = new List<PropertyDef>();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            // Skip properties marked with [JsonIgnore]
            if (prop.CustomAttributes.Any(a => a.AttributeType.Name == nameof(JsonIgnoreAttribute)))
                continue;

            var isNullableRef = IsNullableReferenceProperty(prop, type);
            var jsonName = GetJsonPropertyName(prop);
            var numbersAsString = WritesNumbersAsString(prop, type);

            // A converter on the property itself. JsonStringEnumConverter only means something on an enum (elsewhere
            // System.Text.Json refuses it at run time), so on anything else it is not treated as a converter here.
            var converter = JsonConverters.Classify(prop.CustomAttributes);
            if (converter == ConverterKind.StringEnum && !JsonConverters.UnwrapNullable(prop.PropertyType).IsEnum)
                converter = ConverterKind.None;
            if (converter == ConverterKind.Custom)
                WarnCustomConverter($"{type.Name}.{prop.Name}", isProperty: true);

            properties.Add(new PropertyDef(prop.Name, prop.PropertyType, isNullableRef, jsonName, numbersAsString, converter));
        }

        // A generic definition (Page<T>) becomes `interface Page<T>`; its instantiations refer to it by FullName
        var genericParameters = type.IsGenericTypeDefinition
            ? type.GetGenericArguments().Select(a => a.Name).ToList()
            : null;

        models[type.FullName] = new TypeDef(StringHelpers.StripGenericArity(type.Name), type.FullName,
            TypeDefKind.Interface, properties, null, null, genericParameters);

        // Recurse into property types, except where the property's own converter replaces the type: a custom converter
        // makes the property unknown, and a string-enum converter on an enum that is not a string enum by itself
        // makes it a union of its names, so neither needs the type as a model of its own
        foreach (var prop in properties)
        {
            if (prop.Converter == ConverterKind.Custom) continue;
            if (prop.Converter == ConverterKind.StringEnum && !JsonConverters.IsStringEnum(JsonConverters.UnwrapNullable(prop.Type))) continue;

            CollectModels(prop.Type, models, sourceAssembly);
        }

        // Name the base type as TypeScript will (a generic base such as Page<int> becomes "Page<number>"). This
        // happens last, once this model is registered, so a base that names this type (Node : Base<Node>) maps.
        if (modelBase != null)
            models[type.FullName] = models[type.FullName] with { BaseTypeName = TypeMapper.MapTypeToTS(modelBase, models) };
    }

    private static void WarnCustomConverter(string name, bool isProperty = false) =>
        Diagnostics.Warning(Diagnostics.CustomConverter,
            $"{(isProperty ? "Property" : "Type")} '{name}' has a custom [JsonConverter], so the generator cannot know the JSON it writes; " +
            "it is typed as unknown in TypeScript. (JsonStringEnumConverter is the one converter it understands.)");

    private static bool IsFrameworkAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (string.IsNullOrEmpty(name)) return true;
        return name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("netstandard", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detect NRT (Nullable Reference Type) annotations on a property.
    /// The compiler emits [Nullable(byte[])] or [NullableContext(byte)] attributes.
    /// A value of 2 means nullable, 1 means non-nullable, 0 means oblivious.
    /// </summary>
    internal static bool IsNullableReferenceProperty(PropertyInfo prop, Type declaringType)
    {
        // If the property type is a value type, NRT doesn't apply
        if (prop.PropertyType.IsValueType) return false;

        // Check for [Nullable] attribute on the property itself
        var nullableAttr = prop.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.Name == nameof(NullableAttribute) &&
                                  a.AttributeType.Namespace == typeof(NullableAttribute).Namespace);

        if (nullableAttr != null)
        {
            var ctorArg = nullableAttr.ConstructorArguments.FirstOrDefault();
            if (ctorArg.Value is byte b) return b == 2;
            if (ctorArg.Value is IReadOnlyCollection<CustomAttributeTypedArgument> bytes)
                return bytes.FirstOrDefault().Value is byte first && first == 2;
        }

        // Fall back to [NullableContext] on the declaring type
        var contextAttr = declaringType.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.Name == nameof(NullableContextAttribute) &&
                                  a.AttributeType.Namespace == typeof(NullableContextAttribute).Namespace);

        if (contextAttr != null)
        {
            var ctorArg = contextAttr.ConstructorArguments.FirstOrDefault();
            if (ctorArg.Value is byte b) return b == 2;
        }

        return false;
    }

    /// <summary>
    /// Whether System.Text.Json writes this property's numbers as JSON strings: <c>[JsonNumberHandling]</c> with the
    /// <c>WriteAsString</c> flag on the property, or else on its declaring type. This is how a <c>long</c>, <c>ulong</c> or
    /// <c>decimal</c> is kept exact on its way through JavaScript, whose numbers are only exact up to 2^53.
    /// </summary>
    internal static bool WritesNumbersAsString(PropertyInfo prop, Type declaringType)
    {
        const int WriteAsString = 2; // JsonNumberHandling.WriteAsString

        foreach (var attributes in new[] { prop.CustomAttributes, declaringType.CustomAttributes })
        {
            var attr = attributes.FirstOrDefault(a => a.AttributeType.Name == nameof(JsonNumberHandlingAttribute));
            if (attr == null) continue;

            // The property's own attribute decides, even when it switches the type's setting off
            var arg = attr.ConstructorArguments.FirstOrDefault();
            return arg.Value != null && (Convert.ToInt32(arg.Value) & WriteAsString) != 0;
        }

        return false;
    }

    /// <summary>
    /// Read the [JsonPropertyName("name")] attribute value if present.
    /// Returns null if the attribute is not applied.
    /// </summary>
    internal static string? GetJsonPropertyName(PropertyInfo prop)
    {
        var attr = prop.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.Name == nameof(JsonPropertyNameAttribute));
        if (attr == null) return null;

        var ctorArg = attr.ConstructorArguments.FirstOrDefault();
        return ctorArg.Value as string;
    }
}
