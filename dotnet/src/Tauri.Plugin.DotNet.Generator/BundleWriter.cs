using System.Reflection;
using System.Text;

namespace Tauri.Plugin.DotNet.Generator;

/// <summary>
/// Packs a built backend into one file (the "backend bundle") that the Tauri plugin embeds in the
/// app's executable with <c>include_bytes!</c>, so the backend needs no files next to the exe.
/// Used through the <c>bundle</c> command of this tool, which the <c>TauriDotNetEmbed</c> MSBuild
/// option runs after the build.
///
/// The format is read by the plugin's <c>src/bundle.rs</c>, which documents it. All integers are
/// little-endian; entries are written in a fixed order so the same build gives the same bytes.
/// </summary>
static class BundleWriter
{
    private static readonly byte[] Magic = "TDNBNDL1"u8.ToArray();

    private const byte KindRuntimeConfig = 1;
    private const byte KindAssembly = 2;
    private const byte KindSymbols = 3;

    /// <summary>The assembly with the native entry points; every bundle needs it.</summary>
    internal const string HostAssembly = "Tauri.Plugin.DotNet";

    internal sealed record BundleContents(
        string BackendName,
        byte[] RuntimeConfig,
        List<(string FileName, byte[] Data)> Assemblies,
        List<(string FileName, byte[] Data)> Symbols);

    /// <summary>
    /// Reads the build output next to the backend assembly: its runtime config, and every managed
    /// assembly (with its symbols, if any) in that folder. Anything that cannot be embedded is
    /// added to <paramref name="warnings"/>.
    /// </summary>
    internal static BundleContents Collect(string backendAssemblyPath, List<string> warnings)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(backendAssemblyPath))!;
        var backendName = AssemblyName.GetAssemblyName(backendAssemblyPath).Name
            ?? throw new InvalidOperationException($"'{backendAssemblyPath}' has no assembly name.");

        var runtimeConfigPath = Path.ChangeExtension(backendAssemblyPath, ".runtimeconfig.json");
        if (!File.Exists(runtimeConfigPath))
        {
            throw new InvalidOperationException(
                $"'{runtimeConfigPath}' is missing. Build the backend with " +
                "<EnableDynamicLoading>true</EnableDynamicLoading> and <GenerateRuntimeConfigurationFiles>true</GenerateRuntimeConfigurationFiles>.");
        }

        var assemblies = new List<(string FileName, byte[] Data)>();
        var symbols = new List<(string FileName, byte[] Data)>();

        foreach (var path in Directory.GetFiles(directory, "*.dll").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(path);
            if (!IsManagedAssembly(path))
            {
                warnings.Add($"{fileName} is not a managed assembly, so it is not embedded. Native libraries must be shipped as files next to the executable.");
                continue;
            }

            assemblies.Add((fileName, File.ReadAllBytes(path)));

            var symbolsPath = Path.ChangeExtension(path, ".pdb");
            if (File.Exists(symbolsPath))
                symbols.Add((Path.GetFileName(symbolsPath), File.ReadAllBytes(symbolsPath)));
        }

        foreach (var subdirectory in Directory.GetDirectories(directory))
        {
            if (Directory.EnumerateFiles(subdirectory, "*.dll", SearchOption.AllDirectories).Any())
            {
                warnings.Add(
                    $"The files in '{Path.GetFileName(subdirectory)}' are not embedded " +
                    "(satellite resource assemblies and native or runtime-specific libraries must be shipped next to the executable).");
            }
        }

        if (!assemblies.Any(a => a.FileName.Equals(HostAssembly + ".dll", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"'{directory}' does not contain {HostAssembly}.dll. Does the backend reference the {HostAssembly} package, and is <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies> set?");
        }

        return new BundleContents(backendName, File.ReadAllBytes(runtimeConfigPath), assemblies, symbols);
    }

    /// <summary>Serializes the contents; the bytes are the same for the same input.</summary>
    internal static byte[] Write(BundleContents contents)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            WriteText(writer, contents.BackendName);

            var assemblies = contents.Assemblies.OrderBy(a => a.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            var symbols = contents.Symbols.OrderBy(s => s.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            writer.Write((uint)(1 + assemblies.Count + symbols.Count));

            WriteEntry(writer, KindRuntimeConfig, "runtimeconfig.json", contents.RuntimeConfig);
            foreach (var (fileName, data) in assemblies)
                WriteEntry(writer, KindAssembly, fileName, data);
            foreach (var (fileName, data) in symbols)
                WriteEntry(writer, KindSymbols, fileName, data);
        }

        return stream.ToArray();
    }

    private static void WriteEntry(BinaryWriter writer, byte kind, string name, byte[] data)
    {
        if (data.LongLength > uint.MaxValue)
            throw new InvalidOperationException($"'{name}' is larger than 4 GiB and cannot be embedded.");

        writer.Write(kind);
        WriteText(writer, name);
        writer.Write((uint)data.Length);
        writer.Write(data);
    }

    private static void WriteText(BinaryWriter writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
        {
            return false;
        }
    }

    /// <summary>
    /// The <c>bundle &lt;backend-assembly&gt; &lt;output-file&gt;</c> command. The output file is
    /// left untouched when it already has the same content, so an unchanged backend does not make
    /// Cargo rebuild the app.
    /// </summary>
    internal static int Run(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine($"Usage: {CodeEmitter.ToolName} bundle <backend-assembly-path> <output-file>");
            return 1;
        }

        var assemblyPath = Path.GetFullPath(args[0]);
        var outputPath = Path.GetFullPath(args[1]);
        if (!File.Exists(assemblyPath))
        {
            Console.Error.WriteLine($"Assembly not found: {assemblyPath}");
            return 1;
        }

        try
        {
            var warnings = new List<string>();
            var contents = Collect(assemblyPath, warnings);
            var bytes = Write(contents);

            foreach (var warning in warnings)
                Console.WriteLine($"[{CodeEmitter.ToolName}] Warning: {warning}");

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var unchanged = File.Exists(outputPath) && File.ReadAllBytes(outputPath).AsSpan().SequenceEqual(bytes);
            if (!unchanged)
                File.WriteAllBytes(outputPath, bytes);

            Console.WriteLine(
                $"[{CodeEmitter.ToolName}] Backend bundle {(unchanged ? "up to date" : "written")}: {outputPath} " +
                $"({contents.Assemblies.Count} assembly(ies), {bytes.Length / 1024} KiB)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{CodeEmitter.ToolName}] Error: {ex.Message}");
            return 1;
        }
    }
}
