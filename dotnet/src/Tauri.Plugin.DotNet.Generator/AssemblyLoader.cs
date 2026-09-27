using System.Reflection;

namespace Tauri.Plugin.DotNet.Generator;

static class AssemblyLoader
{
    internal static MetadataLoadContext CreateLoadContext(string assemblyPath)
    {
        var assemblyDir = Path.GetDirectoryName(assemblyPath)!;
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        // When the target assembly is a self-contained publish, its directory also
        // contains runtime DLLs (e.g. System.Private.CoreLib.dll). We must
        // deduplicate by file name, preferring the generator's own runtime copies
        // so MetadataLoadContext doesn't see the same assembly twice.
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Runtime DLLs first (baseline)
        foreach (var dll in Directory.GetFiles(runtimeDir, "*.dll"))
            byName[Path.GetFileName(dll)] = dll;

        // Assembly directory DLLs — only add if not already present from runtime
        foreach (var dll in Directory.GetFiles(assemblyDir, "*.dll"))
        {
            var name = Path.GetFileName(dll);
            byName.TryAdd(name, dll);
        }

        var resolver = new PathAssemblyResolver(byName.Values);
        var coreAssemblyName = Path.GetFileNameWithoutExtension(typeof(object).Assembly.Location);
        return new MetadataLoadContext(resolver, coreAssemblyName);
    }
}
