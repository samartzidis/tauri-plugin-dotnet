using Tauri.Plugin.DotNet.Generator;

namespace Tauri.Plugin.DotNet.Generator.Tests;

public class TypeMappingTests
{
    private static string Map(Type type, Dictionary<string, TypeDef>? models = null)
        => TypeMapper.MapTypeToTS(type, models ?? new Dictionary<string, TypeDef>());

    #region Primitives

    [Fact]
    public void Maps_Void_To_Void()
        => Assert.Equal("void", Map(typeof(void)));

    [Fact]
    public void Maps_String_To_String()
        => Assert.Equal("string", Map(typeof(string)));

    [Fact]
    public void Maps_Boolean_To_Boolean()
        => Assert.Equal("boolean", Map(typeof(bool)));

    [Theory]
    [MemberData(nameof(NumericTypeData))]
    public void Maps_NumericTypes_To_Number(Type type)
        => Assert.Equal("number", Map(type));

    public static IEnumerable<object[]> NumericTypeData => new[]
    {
        new object[] { typeof(byte) },
        new object[] { typeof(sbyte) },
        new object[] { typeof(short) },
        new object[] { typeof(ushort) },
        new object[] { typeof(int) },
        new object[] { typeof(uint) },
        new object[] { typeof(long) },
        new object[] { typeof(ulong) },
        new object[] { typeof(float) },
        new object[] { typeof(double) },
        new object[] { typeof(decimal) },
    };

    [Fact]
    public void Maps_DateTime_To_String()
        => Assert.Equal("string", Map(typeof(DateTime)));

    [Fact]
    public void Maps_DateTimeOffset_To_String()
        => Assert.Equal("string", Map(typeof(DateTimeOffset)));

    [Fact]
    public void Maps_Guid_To_String()
        => Assert.Equal("string", Map(typeof(Guid)));

    [Fact]
    public void Maps_Object_To_Unknown()
        => Assert.Equal("unknown", Map(typeof(object)));

    #endregion

    #region Arrays

    [Fact]
    public void Maps_ByteArray_To_String()
        => Assert.Equal("string", Map(typeof(byte[])));

    [Fact]
    public void Maps_IntArray_To_NumberArray()
        => Assert.Equal("number[]", Map(typeof(int[])));

    [Fact]
    public void Maps_StringArray_To_StringArray()
        => Assert.Equal("string[]", Map(typeof(string[])));

    #endregion

    #region Nullable

    [Fact]
    public void Maps_NullableInt_To_NumberOrNull()
        => Assert.Equal("number | null", Map(typeof(int?)));

    [Fact]
    public void Maps_NullableBool_To_BooleanOrNull()
        => Assert.Equal("boolean | null", Map(typeof(bool?)));

    [Fact]
    public void Maps_NullableDateTime_To_StringOrNull()
        => Assert.Equal("string | null", Map(typeof(DateTime?)));

    #endregion

    #region Collections

    [Fact]
    public void Maps_ListOfString_To_StringArray()
        => Assert.Equal("string[]", Map(typeof(List<string>)));

    [Fact]
    public void Maps_ListOfInt_To_NumberArray()
        => Assert.Equal("number[]", Map(typeof(List<int>)));

    [Fact]
    public void Maps_IEnumerableOfString_To_StringArray()
        => Assert.Equal("string[]", Map(typeof(IEnumerable<string>)));

    [Fact]
    public void Maps_IReadOnlyListOfInt_To_NumberArray()
        => Assert.Equal("number[]", Map(typeof(IReadOnlyList<int>)));

    [Fact]
    public void Maps_DictionaryStringInt_To_Record()
        => Assert.Equal("Record<string, number>", Map(typeof(Dictionary<string, int>)));

    [Fact]
    public void Maps_IDictionaryStringString_To_Record()
        => Assert.Equal("Record<string, string>", Map(typeof(IDictionary<string, string>)));

    [Fact]
    public void Maps_IReadOnlyDictionaryStringInt_To_Record()
        => Assert.Equal("Record<string, number>", Map(typeof(IReadOnlyDictionary<string, int>)));

    [Fact]
    public void Maps_ICollectionOfString_To_StringArray()
        => Assert.Equal("string[]", Map(typeof(ICollection<string>)));

    [Fact]
    public void Maps_IReadOnlyCollectionOfInt_To_NumberArray()
        => Assert.Equal("number[]", Map(typeof(IReadOnlyCollection<int>)));

    #endregion

    #region Async Unwrapping

    [Fact]
    public void Maps_NonGenericTask_To_Void()
        => Assert.Equal("void", Map(typeof(Task)));

    [Fact]
    public void Maps_TaskOfString_To_String()
        => Assert.Equal("string", Map(typeof(Task<string>)));

    [Fact]
    public void Maps_TaskOfInt_To_Number()
        => Assert.Equal("number", Map(typeof(Task<int>)));

    [Fact]
    public void Maps_ValueTaskOfString_To_String()
        => Assert.Equal("string", Map(typeof(ValueTask<string>)));

    [Fact]
    public void Maps_ValueTaskOfInt_To_Number()
        => Assert.Equal("number", Map(typeof(ValueTask<int>)));

    #endregion

    #region Model / Enum References

    [Fact]
    public void Maps_KnownModel_To_TypeName()
    {
        var models = new Dictionary<string, TypeDef>
        {
            ["Tauri.Plugin.DotNet.Generator.Tests.Fixtures.SimpleModel"] = new TypeDef(
                "SimpleModel", "Tauri.Plugin.DotNet.Generator.Tests.Fixtures.SimpleModel",
                TypeDefKind.Interface,
                new List<PropertyDef>(), null)
        };

        Assert.Equal("SimpleModel", Map(typeof(Fixtures.SimpleModel), models));
    }

    [Fact]
    public void Maps_Enum_To_TypeName()
    {
        Assert.Equal("TestEnum", Map(typeof(Fixtures.TestEnum)));
    }

    [Fact]
    public void Maps_UnknownReferenceType_To_Unknown()
    {
        // A type not in models and not a known system type
        Assert.Equal("unknown", Map(typeof(System.Text.StringBuilder)));
    }

    #endregion

    #region IsTaskType / UnwrapTaskType

    [Fact]
    public void IsTaskType_Task_ReturnsTrue()
        => Assert.True(TypeMapper.IsTaskType(typeof(Task)));

    [Fact]
    public void IsTaskType_TaskOfT_ReturnsTrue()
        => Assert.True(TypeMapper.IsTaskType(typeof(Task<string>)));

    [Fact]
    public void IsTaskType_ValueTask_ReturnsTrue()
        => Assert.True(TypeMapper.IsTaskType(typeof(ValueTask)));

    [Fact]
    public void IsTaskType_ValueTaskOfT_ReturnsTrue()
        => Assert.True(TypeMapper.IsTaskType(typeof(ValueTask<int>)));

    [Fact]
    public void IsTaskType_String_ReturnsFalse()
        => Assert.False(TypeMapper.IsTaskType(typeof(string)));

    [Fact]
    public void UnwrapTaskType_TaskOfString_ReturnsString()
        => Assert.Equal(typeof(string), TypeMapper.UnwrapTaskType(typeof(Task<string>)));

    [Fact]
    public void UnwrapTaskType_ValueTaskOfInt_ReturnsInt()
        => Assert.Equal(typeof(int), TypeMapper.UnwrapTaskType(typeof(ValueTask<int>)));

    [Fact]
    public void UnwrapTaskType_NonTask_ReturnsSameType()
        => Assert.Equal(typeof(string), TypeMapper.UnwrapTaskType(typeof(string)));

    #endregion

    #region Converters

    private static string MapWith(Type type, ConverterKind converter)
        => TypeMapper.MapTypeToTS(type, new Dictionary<string, TypeDef>(), numbersAsString: false, converter);

    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(string))]
    [InlineData(typeof(Fixtures.SimpleModel))]
    [InlineData(typeof(List<int>))]
    public void CustomConverterOnAProperty_MakesItUnknown(Type type)
        => Assert.Equal("unknown", MapWith(type, ConverterKind.Custom));

    [Fact]
    public void StringEnumConverterOnAProperty_IsAUnionOfTheMemberNames()
        => Assert.Equal("\"Light\" | \"Dark\"", MapWith(typeof(Fixtures.Shade), ConverterKind.StringEnum));

    [Fact]
    public void StringEnumConverterOnANullableProperty_IsAUnionOrNull()
        => Assert.Equal("\"Light\" | \"Dark\" | null", MapWith(typeof(Fixtures.Shade?), ConverterKind.StringEnum));

    [Fact]
    public void StringEnumConverterOnAFlagsEnumProperty_IsString()
        => Assert.Equal("string", MapWith(typeof(Fixtures.PlainFlags), ConverterKind.StringEnum));

    [Fact]
    public void EnumWithoutConverter_KeepsItsName()
    {
        Assert.Equal("Shade", Map(typeof(Fixtures.Shade)));
        Assert.Equal("Shade", MapWith(typeof(Fixtures.Shade), ConverterKind.None));
    }

    [Fact]
    public void EnumWithTheConverterOnItsType_KeepsItsName_EvenWithTheConverterOnTheProperty()
    {
        Assert.Equal("StringLevel", Map(typeof(Fixtures.StringLevel)));
        Assert.Equal("StringLevel", MapWith(typeof(Fixtures.StringLevel), ConverterKind.StringEnum));
        Assert.Equal("StringGeneric", Map(typeof(Fixtures.StringGeneric)));
    }

    [Fact]
    public void TypeWithACustomConverter_IsUnknownWhereverItIsUsed()
    {
        Assert.Equal("unknown", Map(typeof(Fixtures.Money)));
        Assert.Equal("unknown[]", Map(typeof(List<Fixtures.Money>)));
        Assert.Equal("unknown[]", Map(typeof(Fixtures.Money[])));
        Assert.Equal("Record<string, unknown>", Map(typeof(Dictionary<string, Fixtures.Money>)));
    }

    [Theory]
    [InlineData(typeof(Fixtures.StringLevel), "StringEnum")]
    [InlineData(typeof(Fixtures.StringGeneric), "StringEnum")]
    [InlineData(typeof(Fixtures.StringPerms), "StringEnum")]
    [InlineData(typeof(Fixtures.Money), "Custom")]
    [InlineData(typeof(Fixtures.Shade), "None")]
    [InlineData(typeof(Fixtures.SimpleModel), "None")]
    public void JsonConverters_ClassifyTheConverterOfAType(Type type, string expected)
        => Assert.Equal(Enum.Parse<ConverterKind>(expected), JsonConverters.Classify(type.CustomAttributes));

    #endregion

    #region Sets, time types, JSON, key/value pairs and tuples

    [Fact]
    public void Maps_Sets_To_Arrays()
    {
        Assert.Equal("number[]", Map(typeof(HashSet<int>)));
        Assert.Equal("string[]", Map(typeof(SortedSet<string>)));
        Assert.Equal("number[]", Map(typeof(ISet<int>)));
        Assert.Equal("number[]", Map(typeof(IReadOnlySet<int>)));
    }

    [Theory]
    [InlineData(typeof(TimeSpan))]
    [InlineData(typeof(DateOnly))]
    [InlineData(typeof(TimeOnly))]
    [InlineData(typeof(Uri))]
    public void Maps_TimeAndUriTypes_To_String(Type type)
        => Assert.Equal("string", Map(type));

    [Fact]
    public void Maps_NullableTimeType_To_NullableString()
        => Assert.Equal("string | null", Map(typeof(DateOnly?)));

    [Fact]
    public void Maps_JsonTypes_To_Unknown_Or_Their_Shape()
    {
        Assert.Equal("unknown", Map(typeof(System.Text.Json.JsonElement)));
        Assert.Equal("unknown", Map(typeof(System.Text.Json.Nodes.JsonNode)));
        Assert.Equal("unknown", Map(typeof(System.Text.Json.Nodes.JsonValue)));
        Assert.Equal("Record<string, unknown>", Map(typeof(System.Text.Json.Nodes.JsonObject)));
        Assert.Equal("unknown[]", Map(typeof(System.Text.Json.Nodes.JsonArray)));
    }

    [Fact]
    public void Maps_KeyValuePair_To_KeyValueObject()
    {
        Assert.Equal("{ key: string; value: number }", Map(typeof(KeyValuePair<string, int>)));
        Assert.Equal("{ key: string; value: number }[]", Map(typeof(List<KeyValuePair<string, int>>)));
    }

    [Fact]
    public void Maps_ValueTuple_To_TypeScriptTuple()
    {
        Assert.Equal("[number, string]", Map(typeof((int, string))));
        Assert.Equal("[number, string, boolean]", Map(typeof((int, string, bool))));
        Assert.Equal("[number]", Map(typeof(ValueTuple<int>)));
    }

    [Fact]
    public void Maps_TupleClass_To_TypeScriptTuple()
        => Assert.Equal("[number, string]", Map(typeof(Tuple<int, string>)));

    [Fact]
    public void Maps_Tuples_Recursively_And_Inside_Collections()
    {
        Assert.Equal("[[number, number], string]", Map(typeof(((int, int), string))));
        Assert.Equal("[number, string][]", Map(typeof(List<(int, string)>)));
        Assert.Equal("Record<string, [number, number]>", Map(typeof(Dictionary<string, (int, int)>)));
        Assert.Equal("[number | null, string]", Map(typeof((int?, string))));
        Assert.Equal("[number, string] | null", Map(typeof((int, string)?)));
    }

    [Fact]
    public void Maps_SevenItemTuple_ButNotEightItem()
    {
        Assert.Equal("[number, number, number, number, number, number, number]", Map(typeof((int, int, int, int, int, int, int))));
        // The runtime refuses more than seven items, so there is no honest type for it
        Assert.Equal("unknown", Map(typeof((int, int, int, int, int, int, int, int))));
    }

    [Fact]
    public void Maps_UserModelsInsideTuplesAndPairs()
    {
        var models = new Dictionary<string, TypeDef>
        {
            [typeof(Fixtures.SimpleModel).FullName!] = new TypeDef("SimpleModel", typeof(Fixtures.SimpleModel).FullName!,
                TypeDefKind.Interface, new List<PropertyDef>(), null),
        };

        Assert.Equal("[SimpleModel, number]", Map(typeof((Fixtures.SimpleModel, int)), models));
        Assert.Equal("{ key: string; value: SimpleModel }", Map(typeof(KeyValuePair<string, Fixtures.SimpleModel>), models));
    }

    [Fact]
    public void Maps_ArrayOfNullable_WithParentheses()
    {
        // "number | null[]" would mean number, or an array of null
        Assert.Equal("(number | null)[]", Map(typeof(List<int?>)));
        Assert.Equal("(number | null)[]", Map(typeof(int?[])));
        Assert.Equal("(string | null)[]", Map(typeof(HashSet<DateOnly?>)));
        Assert.Equal("number[]", Map(typeof(List<int>)));
    }

    #endregion

    #region Numbers written as strings

    [Fact]
    public void NumbersAsString_ReachesSetElements_ButNotTuplesOrPairs()
    {
        Assert.Equal("string[]", MapAsString(typeof(HashSet<long>)));
        // A tuple is written by a converter and a pair by its own properties, so the property's setting does not apply
        Assert.Equal("[number, number]", MapAsString(typeof((long, long))));
        Assert.Equal("{ key: string; value: number }", MapAsString(typeof(KeyValuePair<string, long>)));
    }

    private static string MapAsString(Type type, Dictionary<string, TypeDef>? models = null)
        => TypeMapper.MapTypeToTS(type, models ?? new Dictionary<string, TypeDef>(), numbersAsString: true);

    [Theory]
    [MemberData(nameof(NumericTypeData))]
    public void NumbersAsString_MapsEveryNumericType_ToString(Type type)
        => Assert.Equal("string", MapAsString(type));

    [Fact]
    public void NumbersAsString_NullableNumber_IsNullableString()
        => Assert.Equal("string | null", MapAsString(typeof(long?)));

    [Fact]
    public void NumbersAsString_CollectionElements_AreStrings()
    {
        Assert.Equal("string[]", MapAsString(typeof(long[])));
        Assert.Equal("string[]", MapAsString(typeof(List<decimal>)));
        Assert.Equal("string[]", MapAsString(typeof(IEnumerable<ulong>)));
    }

    [Fact]
    public void NumbersAsString_DictionaryValues_AreStrings_KeysAreNot()
        => Assert.Equal("Record<string, string>", MapAsString(typeof(Dictionary<string, long>)));

    [Fact]
    public void NumbersAsString_LeavesOtherTypesAlone()
    {
        Assert.Equal("boolean", MapAsString(typeof(bool)));
        Assert.Equal("string", MapAsString(typeof(string)));
        Assert.Equal("string", MapAsString(typeof(Guid)));
        Assert.Equal("unknown", MapAsString(typeof(object)));
    }

    [Fact]
    public void NumbersAsString_DoesNotReachIntoUserGenericArguments()
    {
        // Page<long>'s own properties decide how its numbers travel, not the property that holds it
        var models = new Dictionary<string, TypeDef>
        {
            [typeof(Fixtures.Page<>).FullName!] = new TypeDef("Page", typeof(Fixtures.Page<>).FullName!,
                TypeDefKind.Interface, new List<PropertyDef>(), null, null, new List<string> { "T" }),
        };
        Assert.Equal("Page<number>", MapAsString(typeof(Fixtures.Page<long>), models));
    }

    [Fact]
    public void NumbersAsString_IsOffByDefault()
    {
        Assert.Equal("number", Map(typeof(long)));
        Assert.Equal("number[]", Map(typeof(List<long>)));
    }

    #endregion

    #region User-defined generics

    private static Dictionary<string, TypeDef> GenericModels() => new()
    {
        [typeof(Fixtures.Page<>).FullName!] = new TypeDef("Page", typeof(Fixtures.Page<>).FullName!,
            TypeDefKind.Interface, new List<PropertyDef>(), null, null, new List<string> { "T" }),
        [typeof(Fixtures.Pair<,>).FullName!] = new TypeDef("Pair", typeof(Fixtures.Pair<,>).FullName!,
            TypeDefKind.Interface, new List<PropertyDef>(), null, null, new List<string> { "TKey", "TValue" }),
        [typeof(Fixtures.SimpleModel).FullName!] = new TypeDef("SimpleModel", typeof(Fixtures.SimpleModel).FullName!,
            TypeDefKind.Interface, new List<PropertyDef>(), null),
    };

    [Fact]
    public void Maps_UserGeneric_ToInterfaceWithArguments()
        => Assert.Equal("Page<string>", Map(typeof(Fixtures.Page<string>), GenericModels()));

    [Fact]
    public void Maps_UserGeneric_WithModelArgument()
        => Assert.Equal("Page<SimpleModel>", Map(typeof(Fixtures.Page<Fixtures.SimpleModel>), GenericModels()));

    [Fact]
    public void Maps_UserGeneric_WithTwoArguments()
        => Assert.Equal("Pair<string, number>", Map(typeof(Fixtures.Pair<string, int>), GenericModels()));

    [Fact]
    public void Maps_UserGeneric_MapsArgumentsRecursively()
        => Assert.Equal("Pair<string[], Page<number>>",
            Map(typeof(Fixtures.Pair<List<string>, Fixtures.Page<int>>), GenericModels()));

    [Fact]
    public void Maps_UserGeneric_InsideCollectionsAndTasks()
    {
        var models = GenericModels();
        Assert.Equal("Page<string>[]", Map(typeof(List<Fixtures.Page<string>>), models));
        Assert.Equal("Record<string, Page<number>>", Map(typeof(Dictionary<string, Fixtures.Page<int>>), models));
        Assert.Equal("Page<string>", Map(typeof(Task<Fixtures.Page<string>>), models));
    }

    [Fact]
    public void Maps_GenericParameter_ToItsName()
    {
        var t = typeof(Fixtures.Pair<,>).GetGenericArguments()[1];
        Assert.Equal("TValue", Map(t));
    }

    [Fact]
    public void Maps_ListOfGenericParameter_ToArrayOfItsName()
    {
        var items = typeof(Fixtures.Page<>).GetProperty("Items")!.PropertyType;
        Assert.Equal("T[]", Map(items));
    }

    [Fact]
    public void Maps_UnregisteredUserGeneric_ToUnknown()
        => Assert.Equal("unknown", Map(typeof(Fixtures.Page<string>)));

    #endregion
}
