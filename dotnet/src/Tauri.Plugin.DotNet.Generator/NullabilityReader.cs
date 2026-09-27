using System.Reflection;

namespace Tauri.Plugin.DotNet.Generator;

/// <summary>
/// Reads C# nullable-reference-type annotations for method parameters and return values.
/// The compiler records them as <c>[Nullable]</c> on the parameter and <c>[NullableContext]</c> on
/// the method or an enclosing type: 2 means nullable, 1 non-nullable, 0 (or no attribute) oblivious.
/// Only the outermost type is read, which is what decides whether TypeScript needs <c>| null</c>.
/// </summary>
static class NullabilityReader
{
    private const string NullableAttribute = "NullableAttribute";
    private const string NullableContextAttribute = "NullableContextAttribute";
    private const string CompilerServices = "System.Runtime.CompilerServices";
    private const byte AnnotatedNullable = 2;

    /// <summary>Is the parameter declared as a nullable reference type (for example <c>string?</c>)?</summary>
    internal static bool IsParameterNullable(ParameterInfo parameter, MethodBase method) =>
        IsNullable(parameter.CustomAttributes, method, parameter.ParameterType, 0);

    /// <summary>
    /// Is the value a method returns declared nullable? For <c>Task&lt;string?&gt;</c> that is the
    /// task's result, whose flag follows the flag of the task type itself.
    /// </summary>
    internal static bool IsReturnNullable(MethodInfo method)
    {
        var returnType = method.ReturnType;
        var isGenericTask = returnType.IsGenericType && TypeMapper.IsTaskType(returnType);
        return IsNullable(
            method.ReturnParameter.CustomAttributes,
            method,
            isGenericTask ? returnType.GetGenericArguments()[0] : returnType,
            isGenericTask ? 1 : 0);
    }

    private static bool IsNullable(IEnumerable<CustomAttributeData> attributes, MethodBase method, Type type, int flagIndex)
    {
        // Value types cannot be null; Nullable<T> is mapped to "T | null" by the type mapper itself.
        if (type.IsValueType || type.FullName == "System.Void")
            return false;

        if (FindFlags(attributes, NullableAttribute) is { } flags)
            return FlagAt(flags, flagIndex) == AnnotatedNullable;

        if (FindFlags(method.CustomAttributes, NullableContextAttribute) is { } methodContext)
            return FlagAt(methodContext, 0) == AnnotatedNullable;

        for (var declaring = method.DeclaringType; declaring != null; declaring = declaring.DeclaringType)
        {
            if (FindFlags(declaring.CustomAttributes, NullableContextAttribute) is { } typeContext)
                return FlagAt(typeContext, 0) == AnnotatedNullable;
        }

        return false;
    }

    private static CustomAttributeTypedArgument? FindFlags(IEnumerable<CustomAttributeData> attributes, string attributeName)
    {
        var attribute = attributes.FirstOrDefault(a => a.AttributeType.Name == attributeName && a.AttributeType.Namespace == CompilerServices);
        return attribute is { ConstructorArguments.Count: > 0 } ? attribute.ConstructorArguments[0] : null;
    }

    /// <summary>The attribute holds one byte for the whole type, or one per type in the tree.</summary>
    private static byte FlagAt(CustomAttributeTypedArgument argument, int index) => argument.Value switch
    {
        byte single => single,
        IReadOnlyCollection<CustomAttributeTypedArgument> tree when index < tree.Count && tree.ElementAt(index).Value is byte flag => flag,
        _ => 0,
    };
}
