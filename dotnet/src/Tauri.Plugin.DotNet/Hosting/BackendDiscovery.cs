using System.Reflection;

namespace Tauri.Plugin.DotNet.Hosting;

/// <summary>Finds and creates the <see cref="IBridgeBackend"/> of a backend assembly.</summary>
internal static class BackendDiscovery
{
    public static IBridgeBackend CreateBackend(Assembly assembly) =>
        CreateBackend(GetTypes(assembly), assembly.GetName().Name ?? assembly.FullName ?? "the assembly");

    public static IBridgeBackend CreateBackend(IEnumerable<Type> candidates, string assemblyName)
    {
        var backends = candidates
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IBridgeBackend).IsAssignableFrom(t))
            .ToList();

        switch (backends.Count)
        {
            case 0:
                throw new InvalidOperationException(
                    $"'{assemblyName}' contains no class implementing {nameof(IBridgeBackend)}.");
            case > 1:
                throw new InvalidOperationException(
                    $"'{assemblyName}' contains more than one class implementing {nameof(IBridgeBackend)}: " +
                    string.Join(", ", backends.Select(t => t.FullName)) + ". Keep exactly one.");
        }

        var type = backends[0];
        if (type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes) is not { } ctor)
            throw new InvalidOperationException($"'{type.FullName}' needs a parameterless constructor.");

        return (IBridgeBackend)ctor.Invoke(null);
    }

    private static IEnumerable<Type> GetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Types whose dependencies are missing are skipped; a real backend type failing to
            // load shows up as "no backend found" plus this detail.
            var detail = string.Join("; ", ex.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct());
            throw new InvalidOperationException($"Failed to load the types of '{assembly.GetName().Name}': {detail}", ex);
        }
    }
}
