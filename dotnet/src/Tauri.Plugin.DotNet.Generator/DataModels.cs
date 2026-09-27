namespace Tauri.Plugin.DotNet.Generator;

record ServiceDef(string Name, List<MethodDef> Methods);
record MethodDef(string Name, List<ParamDef> Parameters, Type ReturnType, bool IsAsync, bool ReturnIsNullable = false);
record ParamDef(string Name, Type Type, bool IsNullable = false);
record EventDef(string Name, Type PayloadType);

/// <summary>A [BridgeFrontend] interface: calls .NET makes to the frontend. <c>Name</c> is the wire name, <c>InterfaceName</c> the C# type name.</summary>
record FrontendDef(string Name, string InterfaceName, List<MethodDef> Methods);

enum TypeDefKind { Interface, Enum }
/// <summary>
/// A model type. <c>BaseTypeName</c> is the TypeScript form of the base type ("Base", or "Base&lt;number&gt;" for a
/// generic base). <c>GenericParameters</c> lists the type parameters of a generic definition ("T"); for a generic
/// type <c>FullName</c> is the definition's, and every instantiation maps to that one interface.
/// </summary>
/// <remarks>
/// <c>StringEnum</c>: an enum written by JsonStringEnumConverter, so its values are the member names. <c>IsFlags</c>: a
/// [Flags] enum of that kind, which is written as the names joined with ", " (for example "Read, Write") and so is
/// only a <c>string</c> in TypeScript.
/// </remarks>
record TypeDef(string Name, string FullName, TypeDefKind Kind, List<PropertyDef>? Properties, List<EnumValueDef>? EnumValues, string? BaseTypeName = null, List<string>? GenericParameters = null, bool StringEnum = false, bool IsFlags = false);
/// <summary>
/// <c>NumbersAsString</c>: the property carries <c>[JsonNumberHandling(WriteAsString)]</c> (or its declaring type does), so its numbers travel as JSON strings.
/// <c>Converter</c>: what a <c>[JsonConverter]</c> on the property itself does (a converter on the property's type is read from that type).
/// </summary>
record PropertyDef(string Name, Type Type, bool IsNullableRef = false, string? JsonName = null, bool NumbersAsString = false, ConverterKind Converter = ConverterKind.None);
record EnumValueDef(string Name, object? Value);
