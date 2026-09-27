using Tauri.Plugin.DotNet.Generator;
using Tauri.Plugin.DotNet.Generator.Tests.Fixtures;
using Tauri.Plugin.DotNet.Generator.Tests.Helpers;

namespace Tauri.Plugin.DotNet.Generator.Tests;

/// <summary>[BridgeFrontend] interfaces (calls .NET makes to the frontend) and nullable-annotation handling.</summary>
public class FrontendTests : IClassFixture<AssemblyHelper>, IDisposable
{
    private readonly AssemblyHelper _helper;
    private readonly string _outputDir = Path.Combine(Path.GetTempPath(), $"BindingGenFrontend_{Guid.NewGuid():N}");

    public FrontendTests(AssemblyHelper helper) => _helper = helper;

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, true);
    }

    #region Discovery

    [Fact]
    public void DiscoverFrontends_FindsMarkedInterfacesOnly()
    {
        var frontends = ServiceDiscovery.DiscoverFrontends(_helper.TestAssembly);

        Assert.Contains(frontends, f => f.InterfaceName == "IShell");
        Assert.Contains(frontends, f => f.InterfaceName == "IWindowing");
        Assert.DoesNotContain(frontends, f => f.InterfaceName == "INotAFrontend");
    }

    [Fact]
    public void DiscoverFrontends_DefaultNameDropsTheLeadingI_AndTheAttributeCanOverrideIt()
    {
        var frontends = ServiceDiscovery.DiscoverFrontends(_helper.TestAssembly);

        Assert.Equal("Shell", frontends.Single(f => f.InterfaceName == "IShell").Name);
        Assert.Equal("Windowing", frontends.Single(f => f.InterfaceName == "IWindowing").Name);
    }

    [Theory]
    [InlineData("IFrontend", "Frontend")]
    [InlineData("Frontend", "Frontend")]
    [InlineData("Item", "Item")]
    [InlineData("I", "I")]
    [InlineData("IOError", "OError")]
    public void DefaultFrontendName_MatchesTheLibraryRule(string interfaceName, string expected) =>
        Assert.Equal(expected, StringHelpers.DefaultFrontendName(interfaceName));

    [Fact]
    public void DiscoverFrontends_ReadsMethodsAndStripsCancellationTokens()
    {
        var shell = ServiceDiscovery.DiscoverFrontends(_helper.TestAssembly).Single(f => f.InterfaceName == "IShell");

        Assert.Equal(new[] { "PickFile", "Notify", "Choose" }, shell.Methods.Select(m => m.Name));
        Assert.Equal(new[] { "text" }, shell.Methods.Single(m => m.Name == "Notify").Parameters.Select(p => p.Name));
    }

    [Fact]
    public void DiscoverFrontends_ReadsNullableAnnotationsOfParametersAndResults()
    {
        var shell = ServiceDiscovery.DiscoverFrontends(_helper.TestAssembly).Single(f => f.InterfaceName == "IShell");
        var pickFile = shell.Methods.Single(m => m.Name == "PickFile");

        Assert.True(pickFile.ReturnIsNullable);
        Assert.False(pickFile.Parameters.Single(p => p.Name == "title").IsNullable);
        Assert.True(pickFile.Parameters.Single(p => p.Name == "filter").IsNullable);
        Assert.False(shell.Methods.Single(m => m.Name == "Choose").ReturnIsNullable);
    }

    [Fact]
    public void DiscoverServices_ReadsNullableAnnotationsOfServiceMethodsToo()
    {
        var service = ServiceDiscovery.DiscoverServices(_helper.TestAssembly).Single(s => s.Name == "NullableService");

        var find = service.Methods.Single(m => m.Name == "Find");
        Assert.True(find.ReturnIsNullable);
        Assert.True(find.Parameters.Single(p => p.Name == "key").IsNullable);
        Assert.False(find.Parameters.Single(p => p.Name == "name").IsNullable);

        Assert.True(service.Methods.Single(m => m.Name == "FindAsync").ReturnIsNullable);
        Assert.False(service.Methods.Single(m => m.Name == "Required").ReturnIsNullable);
        Assert.True(service.Methods.Single(m => m.Name == "DoIt").Parameters.Single().IsNullable);
        Assert.True(service.Methods.Single(m => m.Name == "FindModel").ReturnIsNullable);
    }

    [Fact]
    public void ValueTypesAreNeverReportedAsNullableReferences()
    {
        // int? is handled by the type mapper (Nullable<T>), not by the NRT flag.
        var service = ServiceDiscovery.DiscoverServices(_helper.TestAssembly).Single(s => s.Name == "NullableService");

        var count = service.Methods.Single(m => m.Name == "Count");
        Assert.False(count.ReturnIsNullable);
        Assert.False(count.Parameters.Single().IsNullable);
    }

    #endregion

    #region Code generation

    private FrontendDef Shell() => ServiceDiscovery.DiscoverFrontends(_helper.TestAssembly).Single(f => f.InterfaceName == "IShell");

    [Fact]
    public void GenerateFrontendsFile_EmitsATypedInterfaceWithNullsAndPromises()
    {
        var models = new Dictionary<string, TypeDef>();
        ModelCollector.CollectModels(_helper.LoadType(typeof(ShellChoice)), models, _helper.TestAssembly);

        var code = CodeEmitter.GenerateFrontendsFile(new List<FrontendDef> { Shell() }, models);

        Assert.StartsWith(CodeEmitter.GeneratedHeader, code);
        Assert.Contains("import { registerFrontend } from \"./runtime\";", code);
        Assert.Contains("import type { ShellChoice } from \"./models\";", code);
        Assert.Contains("export interface IShell {", code);
        Assert.Contains("PickFile(title: string, filter: string | null): Promise<string | null>;", code);
        Assert.Contains("Notify(text: string): Promise<void>;", code);
        Assert.Contains("Choose(question: string, options: string[]): Promise<ShellChoice>;", code);
    }

    [Fact]
    public void GenerateFrontendsFile_EmitsAnImplementFunctionNamedAfterTheWireName()
    {
        var frontends = ServiceDiscovery.DiscoverFrontends(_helper.TestAssembly);

        var code = CodeEmitter.GenerateFrontendsFile(frontends, new Dictionary<string, TypeDef>());

        Assert.Contains("export function implementShell(implementation: IShell): () => void {", code);
        Assert.Contains("return registerFrontend(\"Shell\", implementation);", code);
        Assert.Contains("export function implementWindowing(implementation: IWindowing): () => void {", code);
        Assert.Contains("return registerFrontend(\"Windowing\", implementation);", code);
    }

    [Fact]
    public void GenerateFrontendsFile_OmitsTheModelImportWhenNoModelIsUsed()
    {
        var windowing = ServiceDiscovery.DiscoverFrontends(_helper.TestAssembly).Single(f => f.InterfaceName == "IWindowing");

        var code = CodeEmitter.GenerateFrontendsFile(new List<FrontendDef> { windowing }, new Dictionary<string, TypeDef>());

        Assert.DoesNotContain("./models", code);
    }

    [Fact]
    public void GenerateIndexFile_ExportsTheFrontendsFileOnlyWhenThereAreFrontends()
    {
        var none = CodeEmitter.GenerateIndexFile(new(), new(), new Dictionary<string, TypeDef>());
        var some = CodeEmitter.GenerateIndexFile(new(), new(), new Dictionary<string, TypeDef>(), new List<FrontendDef> { Shell() });

        Assert.DoesNotContain("frontends", none);
        Assert.Contains("export * from \"./frontends\";", some);
    }

    [Fact]
    public void GenerateServiceFile_EmitsNullForNullableParametersAndResults()
    {
        var service = ServiceDiscovery.DiscoverServices(_helper.TestAssembly).Single(s => s.Name == "NullableService");

        var code = CodeEmitter.GenerateServiceFile(service, new Dictionary<string, TypeDef>());

        Assert.Contains("export function Find(key: string | null, name: string): Promise<string | null> {", code);
        Assert.Contains("return call<string | null>(\"NullableService.Find\", key, name);", code);
        Assert.Contains("export function FindAsync(name: string): Promise<string | null> {", code);
        Assert.Contains("export function Required(name: string): Promise<string> {", code);
        Assert.Contains("export function DoIt(note: string | null): Promise<void> {", code);
        // int? already maps to "number | null"; it must not be doubled
        Assert.Contains("export function Count(limit: number | null): Promise<number | null> {", code);
    }

    #endregion

    #region Whole program

    [Fact]
    public void Main_GeneratesFrontendsFileAndExportsItFromTheIndex()
    {
        var result = Program.Main(new[] { typeof(IShell).Assembly.Location, _outputDir });

        Assert.Equal(0, result);
        var frontends = File.ReadAllText(Path.Combine(_outputDir, "frontends.ts"));
        Assert.Contains("export interface IShell", frontends);
        Assert.Contains("export interface IWindowing", frontends);
        Assert.Contains("export * from \"./frontends\";", File.ReadAllText(Path.Combine(_outputDir, "index.ts")));
    }

    [Fact]
    public void Main_CollectsModelsUsedOnlyByFrontendInterfaces()
    {
        Program.Main(new[] { typeof(IShell).Assembly.Location, _outputDir });

        // ShellChoice is referenced by IShell.Choose and by nothing else
        Assert.Contains("export interface ShellChoice", File.ReadAllText(Path.Combine(_outputDir, "models.ts")));
    }

    #endregion
}
