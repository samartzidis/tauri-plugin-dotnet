using Tauri.Plugin.DotNet.Generator;
using Tauri.Plugin.DotNet.Generator.Tests.Fixtures;

namespace Tauri.Plugin.DotNet.Generator.Tests;

public class EndToEndTests : IDisposable
{
    private readonly string _outputDir;

    public EndToEndTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), $"BindingGenE2E_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, true);
    }

    #region Argument Validation

    [Fact]
    public void Main_MissingArgs_Returns1()
    {
        var result = Program.Main(Array.Empty<string>());
        Assert.Equal(1, result);
    }

    [Fact]
    public void Main_MissingAssembly_Returns1()
    {
        var result = Program.Main(new[] { "nonexistent.dll", _outputDir });
        Assert.Equal(1, result);
    }

    #endregion

    #region Full Pipeline

    [Fact]
    public void Main_WithTestAssembly_ReturnsZero()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        var result = Program.Main(new[] { assemblyPath, _outputDir });

        Assert.Equal(0, result);
    }

    [Fact]
    public void Main_WithTestAssembly_GeneratesServiceFiles()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        Assert.True(File.Exists(Path.Combine(_outputDir, "BasicService.ts")));
        Assert.True(File.Exists(Path.Combine(_outputDir, "AsyncService.ts")));
        Assert.True(File.Exists(Path.Combine(_outputDir, "ModelService.ts")));
        Assert.True(File.Exists(Path.Combine(_outputDir, "CancellationService.ts")));
        Assert.True(File.Exists(Path.Combine(_outputDir, "IgnoredMethodService.ts")));
        Assert.True(File.Exists(Path.Combine(_outputDir, "CallContextService.ts")));
    }

    [Fact]
    public void Main_WithTestAssembly_GeneratesCustomNamedService()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        // CustomNamedService has [BridgeService(Name = "CustomApi")]
        Assert.True(File.Exists(Path.Combine(_outputDir, "CustomApi.ts")));
        Assert.False(File.Exists(Path.Combine(_outputDir, "CustomNamedService.ts")));
    }

    [Fact]
    public void Main_WithTestAssembly_GeneratesModelsFile()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var modelsPath = Path.Combine(_outputDir, "models.ts");
        Assert.True(File.Exists(modelsPath));

        var content = File.ReadAllText(modelsPath);
        Assert.Contains("export interface SimpleModel", content);
        Assert.Contains("export interface DerivedModel extends BaseModel", content);
        Assert.Contains("export enum TestEnum", content);
    }

    [Fact]
    public void Main_WithTestAssembly_GeneratesGenericModelsAndSignatures()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var models = File.ReadAllText(Path.Combine(_outputDir, "models.ts"));
        Assert.Contains("export interface Page<T> {", models);
        Assert.Contains("  items: T[];", models);
        Assert.Contains("export interface Pair<TKey, TValue> {", models);
        Assert.Contains("  inner: Page<T>;", models);
        Assert.Contains("  extra: Pair<string, T[]>;", models);
        Assert.Contains("export interface SimpleModelPage extends Page<SimpleModel> {", models);
        Assert.Contains("export interface Node extends Tree<Node> {", models);
        // Instantiations are not interfaces of their own
        Assert.DoesNotContain("`", models);

        var service = File.ReadAllText(Path.Combine(_outputDir, "GenericService.ts"));
        Assert.Contains("export function GetPage(): Promise<Page<SimpleModel>>", service);
        Assert.Contains("export function GetPageAsync(): Promise<Page<SimpleModel>>", service);
        Assert.Contains("export function GetPair(page: Page<string>): Promise<Pair<string, number>>", service);
        Assert.Contains("export function GetEnvelope(): Promise<Envelope<number>>", service);
    }

    [Fact]
    public void Main_WithTestAssembly_GeneratesEventsFile()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var eventsPath = Path.Combine(_outputDir, "events.ts");
        Assert.True(File.Exists(eventsPath));

        var content = File.ReadAllText(eventsPath);
        Assert.Contains("onTestEvent", content);
        Assert.Contains("onAnother", content);
    }

    [Fact]
    public void Main_WithTestAssembly_GeneratesIndexFile()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var indexPath = Path.Combine(_outputDir, "index.ts");
        Assert.True(File.Exists(indexPath));

        var content = File.ReadAllText(indexPath);
        Assert.Contains("export * as BasicService from \"./BasicService\";", content);
        Assert.Contains("export * from \"./models\";", content);
        Assert.Contains("export * from \"./events\";", content);
    }

    [Fact]
    public void Main_WithTestAssembly_ServiceFileContent_HasCorrectSignatures()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var content = File.ReadAllText(Path.Combine(_outputDir, "BasicService.ts"));
        Assert.Contains("export function Greet(name: string): Promise<string>", content);
        Assert.Contains("export function Add(a: number, b: number): Promise<number>", content);
        Assert.Contains("export function DoNothing(): Promise<void>", content);
    }

    [Fact]
    public void Main_WithTestAssembly_ByteArrayMappedToString()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var content = File.ReadAllText(Path.Combine(_outputDir, "ModelService.ts"));
        // byte[] should be mapped to string (base64)
        Assert.Contains("EchoBytes(data: string): Promise<string>", content);
    }

    [Fact]
    public void Main_WithTestAssembly_CancellationTokenStrippedFromSignature()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var content = File.ReadAllText(Path.Combine(_outputDir, "CancellationService.ts"));
        // CancellationToken should be stripped — SlowMethod should only have 'seconds'
        Assert.Contains("SlowMethod(seconds: number)", content);
        Assert.DoesNotContain("CancellationToken", content);
        Assert.DoesNotContain("cancellationToken", content);
    }

    [Fact]
    public void Main_WithTestAssembly_IgnoredMethodExcluded()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var content = File.ReadAllText(Path.Combine(_outputDir, "IgnoredMethodService.ts"));
        Assert.Contains("export function Visible()", content);
        Assert.DoesNotContain("Hidden", content);
    }

    [Fact]
    public void Main_WithTestAssembly_CallContextStrippedFromSignature()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var content = File.ReadAllText(Path.Combine(_outputDir, "CallContextService.ts"));
        Assert.Contains("export function GetTitle(): Promise<string>", content);
        Assert.Contains("export function GetTitleWithArg(name: string): Promise<string>", content);
        Assert.DoesNotContain("ctx:", content);
        Assert.DoesNotContain("ctx)", content);
    }

    [Fact]
    public void Main_WithTestAssembly_JsonPropertyNameRespected()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var content = File.ReadAllText(Path.Combine(_outputDir, "models.ts"));
        // JsonCustomModel.CustomName has [JsonPropertyName("custom_name")]
        Assert.Contains("custom_name: string;", content);
    }

    [Fact]
    public void Main_WithTestAssembly_JsonIgnoreRespected()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var content = File.ReadAllText(Path.Combine(_outputDir, "models.ts"));
        // JsonCustomModel.Secret has [JsonIgnore], should not appear
        Assert.DoesNotContain("secret", content);
        Assert.DoesNotContain("Secret", content);
    }

    [Fact]
    public void Main_WithTestAssembly_CleansUpStaleFiles()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        // First run creates files
        Program.Main(new[] { assemblyPath, _outputDir });

        // Drop a fake stale file
        var staleFile = Path.Combine(_outputDir, "DeletedService.ts");
        File.WriteAllText(staleFile, "// stale");

        // Second run should clean it up
        Program.Main(new[] { assemblyPath, _outputDir });

        Assert.False(File.Exists(staleFile), "Stale file should be deleted on subsequent run");
    }

    [Fact]
    public void Main_WithTestAssembly_NumbersWrittenAsStringsAreTypedAsStrings()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var content = File.ReadAllText(Path.Combine(_outputDir, "models.ts"));
        var large = ModelBlock(content, "LargeNumberModel");
        Assert.Contains("  id: string;", large);
        Assert.Contains("  amount: string;", large);
        Assert.Contains("  optional?: string;", large);
        Assert.Contains("  ids: string[];", large);
        Assert.Contains("  totals: Record<string, string>;", large);
        Assert.Contains("  nested: Page<number>;", large);
        Assert.Contains("  plain: number;", large);
        Assert.Contains("  readsStrings: number;", large);

        var all = ModelBlock(content, "AllStringsModel");
        Assert.Contains("  a: string;", all);
        Assert.Contains("  b: string;", all);
        Assert.Contains("  name: string;", all);
        Assert.Contains("  stillNumber: number;", all);
    }

    [Fact]
    public void Main_WithTestAssembly_TypesSetsTimesJsonPairsAndTuples()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var service = File.ReadAllText(Path.Combine(_outputDir, "WireTypeService.ts"));
        Assert.Contains("export function GetTags(): Promise<string[]>", service);
        Assert.Contains("export function GetDuration(): Promise<string>", service);
        Assert.Contains("export function GetDay(): Promise<string>", service);
        Assert.Contains("export function GetTime(): Promise<string>", service);
        Assert.Contains("export function GetLink(): Promise<string>", service);
        Assert.Contains("export function GetJson(): Promise<unknown>", service);
        Assert.Contains("export function GetEntry(): Promise<{ key: string; value: number }>", service);
        Assert.Contains("export function GetPair(): Promise<[number, string]>", service);
        Assert.Contains("export function Swap(pair: [number, string]): Promise<[string, number]>", service);
        Assert.Contains("export function GetPairs(): Promise<[number, string][]>", service);
        Assert.Contains("export function GetModelPair(): Promise<[SimpleModel, number]>", service);
        Assert.Contains("export function GetMaybeNumbers(): Promise<(number | null)[]>", service);
        // A model that is only used inside a tuple is still imported
        Assert.Contains("import type { SimpleModel, WireModel } from \"./models\";", service);

        var models = ModelBlock(File.ReadAllText(Path.Combine(_outputDir, "models.ts")), "WireModel");
        Assert.Contains("  ids: number[];", models);
        Assert.Contains("  elapsed: string;", models);
        Assert.Contains("  day?: string;", models);
        Assert.Contains("  link?: string;", models);
        Assert.Contains("  extra: unknown;", models);
        Assert.Contains("  pair: [number, SimpleModel];", models);
        Assert.Contains("  entries: { key: string; value: number }[];", models);
        Assert.Contains("  tooBig: unknown;", models);
    }

    [Fact]
    public void Main_WithTestAssembly_TypesConvertedValuesAsTheConvertersWriteThem()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var models = File.ReadAllText(Path.Combine(_outputDir, "models.ts"));
        var block = ModelBlock(models, "ConverterModel");
        Assert.Contains("  tone: \"Light\" | \"Dark\";", block);
        Assert.Contains("  plain: Shade;", block);
        Assert.Contains("  maybeTone?: \"Light\" | \"Dark\";", block);
        Assert.Contains("  level: StringLevel;", block);
        Assert.Contains("  levelAgain: StringLevel;", block);
        Assert.Contains("  generic: StringGeneric;", block);
        Assert.Contains("  perms: StringPerms;", block);
        Assert.Contains("  sides: string;", block);
        Assert.Contains("  price: unknown;", block);
        Assert.Contains("  prices: unknown[];", block);
        Assert.Contains("  byName: Record<string, unknown>;", block);
        Assert.Contains("  timeout: unknown;", block);
        Assert.Contains("  notAnEnum: number;", block);

        // Enums: string where JsonStringEnumConverter is on the type, numeric otherwise
        Assert.Contains("export enum StringLevel {", models);
        Assert.Contains("  Low = \"Low\",", models);
        Assert.Contains("export type StringPerms = string;", models);
        Assert.Contains("  Light = 0,", models);
        // A type with a custom converter is not declared, and neither is an enum only used through a string-enum property
        Assert.DoesNotContain("export interface Money", models);
        Assert.DoesNotContain("PlainFlags", models);

        var service = File.ReadAllText(Path.Combine(_outputDir, "ConverterService.ts"));
        Assert.Contains("export function GetMoney(): Promise<unknown>", service);
        Assert.Contains("export function GetMoneys(): Promise<unknown[]>", service);
        Assert.Contains("export function GetLevel(): Promise<StringLevel>", service);
        Assert.Contains("export function SetLevel(level: StringLevel): Promise<void>", service);
    }

    private static string ModelBlock(string modelsTs, string name)
    {
        var start = modelsTs.IndexOf($"export interface {name}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} not found in models.ts");
        // The interface ends at the first closing brace that starts a line (a property type can contain braces)
        return modelsTs[start..modelsTs.IndexOf("\n}", start, StringComparison.Ordinal)];
    }

    [Fact]
    public void Main_WithTestAssembly_GeneratesEmptyAndAllIgnoredServiceFiles()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var emptyPath = Path.Combine(_outputDir, "EmptyService.ts");
        var allIgnoredPath = Path.Combine(_outputDir, "AllIgnoredService.ts");
        Assert.True(File.Exists(emptyPath));
        Assert.True(File.Exists(allIgnoredPath));

        var emptyContent = File.ReadAllText(emptyPath);
        Assert.Contains("// Auto-generated", emptyContent);
        Assert.Contains("import { call }", emptyContent);

        var allIgnoredContent = File.ReadAllText(allIgnoredPath);
        Assert.Contains("// Auto-generated", allIgnoredContent);
        Assert.DoesNotContain("Hidden1", allIgnoredContent);
        Assert.DoesNotContain("Hidden2", allIgnoredContent);
    }

    [Fact]
    public void Main_WithTestAssembly_GeneratesEmptyModelAndEdgeCaseEnum()
    {
        var assemblyPath = typeof(BasicService).Assembly.Location;

        Program.Main(new[] { assemblyPath, _outputDir });

        var content = File.ReadAllText(Path.Combine(_outputDir, "models.ts"));
        Assert.Contains("export interface EmptyModel {", content);
        Assert.Contains("export enum EdgeCaseEnum", content);
        Assert.Contains("Negative = -1", content);
        Assert.Contains("SameAsZero = 0", content);
    }

    #endregion
}
