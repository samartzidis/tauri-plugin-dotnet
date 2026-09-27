using System.Reflection;
using Tauri.Plugin.DotNet.Generator;
using Tauri.Plugin.DotNet.Generator.Tests.Helpers;
using Tauri.Plugin.DotNet.Generator.Tests.Fixtures;

namespace Tauri.Plugin.DotNet.Generator.Tests;

public class ModelCollectionTests : IClassFixture<AssemblyHelper>
{
    private readonly AssemblyHelper _helper;

    public ModelCollectionTests(AssemblyHelper helper)
    {
        _helper = helper;
    }

    private Dictionary<string, TypeDef> Collect(Type fixtureType)
    {
        var mlcType = _helper.LoadType(fixtureType);
        var models = new Dictionary<string, TypeDef>();
        ModelCollector.CollectModels(mlcType, models, _helper.TestAssembly);
        return models;
    }

    #region Basic Collection

    [Fact]
    public void CollectsSimpleModel_WithProperties()
    {
        var models = Collect(typeof(SimpleModel));

        Assert.Single(models);
        var model = models.Values.First();
        Assert.Equal("SimpleModel", model.Name);
        Assert.Equal(TypeDefKind.Interface, model.Kind);
        Assert.NotNull(model.Properties);
        Assert.Equal(2, model.Properties!.Count);
        Assert.Contains(model.Properties, p => p.Name == "Name");
        Assert.Contains(model.Properties, p => p.Name == "Value");
    }

    [Fact]
    public void SkipsSystemTypes()
    {
        // System.String should not be collected as a model
        var stringType = _helper.LoadCoreType("System.String");
        var models = new Dictionary<string, TypeDef>();
        ModelCollector.CollectModels(stringType, models, _helper.TestAssembly);

        Assert.Empty(models);
    }

    [Fact]
    public void SkipsPrimitiveTypes()
    {
        var intType = _helper.LoadCoreType("System.Int32");
        var models = new Dictionary<string, TypeDef>();
        ModelCollector.CollectModels(intType, models, _helper.TestAssembly);

        Assert.Empty(models);
    }

    #endregion

    #region Enums

    [Fact]
    public void CollectsEnum_WithValues()
    {
        var models = Collect(typeof(TestEnum));

        Assert.Single(models);
        var model = models.Values.First();
        Assert.Equal("TestEnum", model.Name);
        Assert.Equal(TypeDefKind.Enum, model.Kind);
        Assert.NotNull(model.EnumValues);
        Assert.Equal(3, model.EnumValues!.Count);
        Assert.Contains(model.EnumValues, v => v.Name == "None");
        Assert.Contains(model.EnumValues, v => v.Name == "First");
        Assert.Contains(model.EnumValues, v => v.Name == "Second");
    }

    #endregion

    #region Unwrapping

    [Fact]
    public void UnwrapsNullableValueType_AndRecurses()
    {
        // Nullable<SimpleModel> isn't valid, but Nullable<TestEnum> would be
        // Actually test with a type that has Nullable<T> property
        var models = Collect(typeof(NullableModel));

        Assert.Single(models); // Only NullableModel itself
        var model = models.Values.First();
        Assert.Equal("NullableModel", model.Name);
        // int? should not add int as a model
    }

    [Fact]
    public void UnwrapsArray_AndCollectsElementType()
    {
        // Create an array type of SimpleModel and collect
        var simpleType = _helper.LoadType(typeof(SimpleModel));
        var arrayType = simpleType.MakeArrayType();
        var models = new Dictionary<string, TypeDef>();
        ModelCollector.CollectModels(arrayType, models, _helper.TestAssembly);

        // Should have collected SimpleModel from the array element
        Assert.Contains(models.Values, m => m.Name == "SimpleModel");
    }

    [Fact]
    public void UnwrapsGenericCollection_AndCollectsElementType()
    {
        // ModelService.GetModels returns List<SimpleModel>
        // Test via the service discovery path
        var services = ServiceDiscovery.DiscoverServices(_helper.TestAssembly);
        var svc = services.First(s => s.Name == "ModelService");
        var getModels = svc.Methods.First(m => m.Name == "GetModels");

        var models = new Dictionary<string, TypeDef>();
        ModelCollector.CollectModels(getModels.ReturnType, models, _helper.TestAssembly);

        Assert.Contains(models.Values, m => m.Name == "SimpleModel");
    }

    #endregion

    #region Inheritance

    [Fact]
    public void DetectsBaseClass_AndSetsBaseTypeName()
    {
        var models = Collect(typeof(DerivedModel));

        // Should have both BaseModel and DerivedModel
        Assert.Equal(2, models.Count);

        var derived = models.Values.First(m => m.Name == "DerivedModel");
        Assert.Equal("BaseModel", derived.BaseTypeName);

        var baseModel = models.Values.First(m => m.Name == "BaseModel");
        Assert.Null(baseModel.BaseTypeName);
    }

    [Fact]
    public void UsesDeclaredOnlyProperties_NoDuplication()
    {
        var models = Collect(typeof(DerivedModel));

        var derived = models.Values.First(m => m.Name == "DerivedModel");
        var baseModel = models.Values.First(m => m.Name == "BaseModel");

        // DerivedModel should only have its own properties (Extra), not inherited (Id, Name)
        Assert.Single(derived.Properties!);
        Assert.Equal("Extra", derived.Properties![0].Name);

        // BaseModel should have Id and Name
        Assert.Equal(2, baseModel.Properties!.Count);
        Assert.Contains(baseModel.Properties, p => p.Name == "Id");
        Assert.Contains(baseModel.Properties, p => p.Name == "Name");
    }

    #endregion

    #region JSON Attributes

    [Fact]
    public void SkipsJsonIgnoreProperties()
    {
        var models = Collect(typeof(JsonCustomModel));
        var model = models.Values.First();

        // Secret property has [JsonIgnore], should be excluded
        Assert.DoesNotContain(model.Properties!, p => p.Name == "Secret");
    }

    [Fact]
    public void ReadsJsonPropertyName()
    {
        var models = Collect(typeof(JsonCustomModel));
        var model = models.Values.First();

        var customProp = model.Properties!.First(p => p.Name == "CustomName");
        Assert.Equal("custom_name", customProp.JsonName);
    }

    [Fact]
    public void RegularProperties_HaveNullJsonName()
    {
        var models = Collect(typeof(JsonCustomModel));
        var model = models.Values.First();

        var visibleProp = model.Properties!.First(p => p.Name == "Visible");
        Assert.Null(visibleProp.JsonName);
    }

    #endregion

    #region Nullable Reference Types

    [Fact]
    public void DetectsNullableReferenceType()
    {
        var models = Collect(typeof(NullableModel));
        var model = models.Values.First();

        var required = model.Properties!.First(p => p.Name == "Required");
        Assert.False(required.IsNullableRef);

        var optional = model.Properties!.First(p => p.Name == "Optional");
        Assert.True(optional.IsNullableRef);
    }

    [Fact]
    public void NullableValueType_IsNotMarkedAsNullableRef()
    {
        var models = Collect(typeof(NullableModel));
        var model = models.Values.First();

        // int? is Nullable<int>, not an NRT — IsNullableRef should be false
        var nullableInt = model.Properties!.First(p => p.Name == "NullableInt");
        Assert.False(nullableInt.IsNullableRef);
    }

    [Fact]
    public void DetectsNullableReferenceType_FromTypeNullableContext()
    {
        var models = Collect(typeof(TypeWithOptionalFromContext));
        var model = models.Values.First();

        var optional = model.Properties!.First(p => p.Name == "OptionalFromContext");
        Assert.True(optional.IsNullableRef);
    }

    #endregion

    #region Edge cases

    [Fact]
    public void CollectsEmptyModel_WithNoProperties()
    {
        var models = Collect(typeof(EmptyModel));

        Assert.Single(models);
        var model = models.Values.First();
        Assert.Equal("EmptyModel", model.Name);
        Assert.NotNull(model.Properties);
        Assert.Empty(model.Properties!);
    }

    [Fact]
    public void CollectsEnum_WithNegativeAndDuplicateValues()
    {
        var models = Collect(typeof(EdgeCaseEnum));

        Assert.Single(models);
        var model = models.Values.First();
        Assert.Equal("EdgeCaseEnum", model.Name);
        Assert.Equal(TypeDefKind.Enum, model.Kind);
        Assert.NotNull(model.EnumValues);
        Assert.Equal(3, model.EnumValues!.Count);
        Assert.Contains(model.EnumValues, v => v.Name == "Zero" && (int)(v.Value ?? 0) == 0);
        Assert.Contains(model.EnumValues, v => v.Name == "Negative" && (int)(v.Value ?? 0) == -1);
        Assert.Contains(model.EnumValues, v => v.Name == "SameAsZero" && (int)(v.Value ?? 0) == 0);
    }

    #endregion

    #region Converters

    private static readonly string ConverterModelKey = typeof(ConverterModel).FullName!;

    private PropertyDef ConverterProperty(string name) =>
        Collect(typeof(ConverterModel))[ConverterModelKey].Properties!.First(p => p.Name == name);

    [Theory]
    [InlineData("Tone", "StringEnum")]
    [InlineData("MaybeTone", "StringEnum")]
    [InlineData("LevelAgain", "StringEnum")]
    [InlineData("Sides", "StringEnum")]
    [InlineData("Timeout", "Custom")]
    [InlineData("Plain", "None")]
    [InlineData("Level", "None")]   // the converter is on the enum type, not on the property
    [InlineData("Price", "None")]   // ditto for a class with a converter
    [InlineData("NotAnEnum", "None")] // JsonStringEnumConverter means nothing on an int
    public void PropertyConverters_AreRead(string property, string expected)
        => Assert.Equal(Enum.Parse<ConverterKind>(expected), ConverterProperty(property).Converter);

    [Fact]
    public void StringEnumType_IsCollectedWithItsNamesAsValues()
    {
        var models = Collect(typeof(ConverterModel));

        var level = models[typeof(StringLevel).FullName!];
        Assert.True(level.StringEnum);
        Assert.False(level.IsFlags);
        Assert.Equal(new object?[] { "Low", "High" }, level.EnumValues!.Select(v => v.Value));
        Assert.True(models[typeof(StringGeneric).FullName!].StringEnum); // JsonStringEnumConverter<T>
    }

    [Fact]
    public void PlainEnum_IsStillNumeric()
    {
        var shade = Collect(typeof(ConverterModel))[typeof(Shade).FullName!];

        Assert.False(shade.StringEnum);
        Assert.Equal(new object?[] { 0, 1 }, shade.EnumValues!.Select(v => v.Value));
    }

    [Fact]
    public void FlagsStringEnum_IsMarkedAsFlags()
    {
        var perms = Collect(typeof(ConverterModel))[typeof(StringPerms).FullName!];

        Assert.True(perms.StringEnum);
        Assert.True(perms.IsFlags);
    }

    [Fact]
    public void EnumUsedOnlyThroughAStringEnumProperty_IsNotCollected()
    {
        // ConverterModel.Sides is a PlainFlags with JsonStringEnumConverter and nothing else uses PlainFlags: the property is
        // a plain string, so the enum would only be an unused (and misleading numeric) declaration
        var models = Collect(typeof(ConverterModel));

        Assert.DoesNotContain(typeof(PlainFlags).FullName!, models.Keys);
        // Shade is also used without the converter (Plain), so it is collected
        Assert.Contains(typeof(Shade).FullName!, models.Keys);
    }

    [Fact]
    public void TypeWithACustomConverter_IsNotAModel_AndWarnsOnce()
    {
        using var _ = Diagnostics.Capture(out var warnings);

        var models = Collect(typeof(ConverterModel));

        Assert.DoesNotContain(typeof(Money).FullName!, models.Keys);
        // Money is used by three properties, but is reported once
        var money = Assert.Single(warnings, w => w.Contains("Type '") && w.Contains("Money"));
        Assert.Contains($"warning {Diagnostics.CustomConverter}:", money);
    }

    [Fact]
    public void CustomConverterOnAProperty_Warns()
    {
        using var _ = Diagnostics.Capture(out var warnings);

        Collect(typeof(ConverterModel));

        var timeout = Assert.Single(warnings, w => w.Contains("Property 'ConverterModel.Timeout'"));
        Assert.Contains($"warning {Diagnostics.CustomConverter}:", timeout);
    }

    [Fact]
    public void StringEnumConverters_DoNotWarn()
    {
        using var _ = Diagnostics.Capture(out var warnings);

        Collect(typeof(ConverterModel));

        Assert.DoesNotContain(warnings, w => w.Contains("Tone") || w.Contains("StringLevel") || w.Contains("Sides"));
    }

    #endregion

    #region Numbers written as strings

    private PropertyDef Property(Type model, string name) =>
        Collect(model)[model.FullName!].Properties!.First(p => p.Name == name);

    [Theory]
    [InlineData("Id")]
    [InlineData("Amount")]
    [InlineData("Optional")]
    [InlineData("Ids")]
    [InlineData("Totals")]
    public void DetectsWriteAsString_OnTheProperty(string name)
        => Assert.True(Property(typeof(LargeNumberModel), name).NumbersAsString);

    [Theory]
    [InlineData("Plain")]
    [InlineData("ReadsStrings")] // AllowReadingFromString alone does not change what is written
    public void WriteAsStringIsOffWithoutItsFlag(string name)
        => Assert.False(Property(typeof(LargeNumberModel), name).NumbersAsString);

    [Fact]
    public void TypeLevelAttribute_AppliesToItsProperties()
    {
        Assert.True(Property(typeof(AllStringsModel), "A").NumbersAsString);
        Assert.True(Property(typeof(AllStringsModel), "B").NumbersAsString);
    }

    [Fact]
    public void PropertyAttribute_OverridesTheTypeLevelOne()
        => Assert.False(Property(typeof(AllStringsModel), "StillNumber").NumbersAsString);

    [Fact]
    public void ModelsWithoutTheAttribute_AreUnaffected()
        => Assert.All(Collect(typeof(SimpleModel)).Values.SelectMany(m => m.Properties!), p => Assert.False(p.NumbersAsString));

    #endregion

    #region User-defined generics

    private static readonly string PageKey = typeof(Page<>).FullName!;
    private static readonly string PairKey = typeof(Pair<,>).FullName!;

    private MethodDef GenericMethod(string name) =>
        ServiceDiscovery.DiscoverServices(_helper.TestAssembly)
            .First(s => s.Name == "GenericService").Methods.First(m => m.Name == name);

    private Dictionary<string, TypeDef> CollectFromMethod(string name)
    {
        var method = GenericMethod(name);
        var models = new Dictionary<string, TypeDef>();
        ModelCollector.CollectModels(method.ReturnType, models, _helper.TestAssembly);
        foreach (var p in method.Parameters)
            ModelCollector.CollectModels(p.Type, models, _helper.TestAssembly);
        return models;
    }

    [Fact]
    public void CollectsGenericDefinition_WithItsParameters()
    {
        var models = CollectFromMethod("GetPage");

        var page = models[PageKey];
        Assert.Equal("Page", page.Name);
        Assert.Equal(new[] { "T" }, page.GenericParameters);
        Assert.Contains(page.Properties!, p => p.Name == "Items");
        Assert.Contains(page.Properties!, p => p.Name == "Total");
        // The argument is collected as a model of its own
        Assert.Contains(models.Values, m => m.Name == "SimpleModel");
    }

    [Fact]
    public void GenericDefinition_IsCollectedOnce_ForSeveralInstantiations()
    {
        // GetPair(Page<string>) uses Page<string>; GetPage returns Page<SimpleModel>
        var models = CollectFromMethod("GetPage");
        ModelCollector.CollectModels(GenericMethod("GetPair").Parameters[0].Type, models, _helper.TestAssembly);

        Assert.Single(models.Values, m => m.Name == "Page");
        Assert.DoesNotContain(models.Keys, k => k.Contains('['));
    }

    [Fact]
    public void CollectsGenericDefinition_WithTwoParameters()
    {
        var models = CollectFromMethod("GetPair");

        Assert.Equal(new[] { "TKey", "TValue" }, models[PairKey].GenericParameters);
    }

    [Fact]
    public void GenericParameterProperties_KeepTheirNullability()
    {
        var page = CollectFromMethod("GetPage")[PageKey];

        Assert.True(page.Properties!.First(p => p.Name == "Latest").IsNullableRef);
        Assert.False(page.Properties!.First(p => p.Name == "Total").IsNullableRef);
    }

    [Fact]
    public void CollectsGenericsUsedByGenerics()
    {
        var models = CollectFromMethod("GetEnvelope");

        Assert.Equal(new[] { "T" }, models[typeof(Envelope<>).FullName!].GenericParameters);
        Assert.True(models.ContainsKey(PageKey));
        Assert.True(models.ContainsKey(PairKey));
    }

    [Fact]
    public void GenericBaseClass_IsNamedWithItsArguments()
    {
        var models = CollectFromMethod("GetSimplePage");

        var derived = models[typeof(SimpleModelPage).FullName!];
        Assert.Equal("Page<SimpleModel>", derived.BaseTypeName);
        // Only the property declared on the derived class, the rest comes from `extends`
        Assert.Single(derived.Properties!);
        Assert.Equal("Title", derived.Properties![0].Name);
        Assert.True(models.ContainsKey(PageKey));
        Assert.Contains(models.Values, m => m.Name == "SimpleModel");
    }

    [Fact]
    public void BaseClassNamingItself_TerminatesAndIsNamed()
    {
        // class Node : Tree<Node>
        var models = CollectFromMethod("GetNode");

        Assert.Equal("Tree<Node>", models[typeof(Node).FullName!].BaseTypeName);
        Assert.Equal(new[] { "T" }, models[typeof(Tree<>).FullName!].GenericParameters);
    }

    [Fact]
    public void FrameworkGenerics_AreNotModels()
    {
        var models = Collect(typeof(ModelWithCollections));

        // List<string>, Dictionary<string, int>: only the model itself
        Assert.Single(models);
    }

    #endregion

    #region Deduplication

    [Fact]
    public void DoesNotDuplicateAlreadyCollectedModels()
    {
        var mlcType = _helper.LoadType(typeof(SimpleModel));
        var models = new Dictionary<string, TypeDef>();

        // Collect twice
        ModelCollector.CollectModels(mlcType, models, _helper.TestAssembly);
        ModelCollector.CollectModels(mlcType, models, _helper.TestAssembly);

        Assert.Single(models);
    }

    #endregion
}
