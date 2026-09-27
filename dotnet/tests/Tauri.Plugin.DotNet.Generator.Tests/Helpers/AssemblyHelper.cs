using System.Reflection;
using Tauri.Plugin.DotNet.Generator;

namespace Tauri.Plugin.DotNet.Generator.Tests.Helpers;

/// <summary>
/// Shared test fixture that creates a MetadataLoadContext and loads the
/// test assembly for discovery/model-collection tests. Implements
/// IDisposable so xUnit's IClassFixture disposes the MLC after all
/// tests in a class complete.
/// </summary>
public sealed class AssemblyHelper : IDisposable
{
    public MetadataLoadContext Mlc { get; }
    public Assembly TestAssembly { get; }
    public string TestAssemblyPath { get; }

    public AssemblyHelper()
    {
        TestAssemblyPath = typeof(AssemblyHelper).Assembly.Location;
        Mlc = AssemblyLoader.CreateLoadContext(TestAssemblyPath);
        TestAssembly = Mlc.LoadFromAssemblyPath(TestAssemblyPath);
    }

    /// <summary>
    /// Load a type from the test assembly by its runtime System.Type.
    /// Returns the MLC-equivalent Type.
    /// </summary>
    public Type LoadType(Type runtimeType)
    {
        return TestAssembly.GetType(runtimeType.FullName!)
            ?? throw new InvalidOperationException(
                $"Cannot find type '{runtimeType.FullName}' in MLC-loaded test assembly.");
    }

    /// <summary>
    /// Load a type from the core (runtime) assembly by full name.
    /// Useful for system types like System.String, System.Int32, etc.
    /// </summary>
    public Type LoadCoreType(string fullName)
    {
        foreach (var asm in Mlc.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t != null) return t;
        }
        throw new InvalidOperationException($"Cannot find core type '{fullName}' in MLC.");
    }

    public void Dispose() => Mlc.Dispose();
}
