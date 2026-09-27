using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Tauri.Plugin.DotNet.SidecarHost;

/// <summary>
/// Makes sure the only copy of <c>Tauri.Plugin.DotNet.dll</c> (and any other managed or native
/// dependency the backend ships) this process ever loads is the backend folder's own copy - not a
/// second one this program's own output folder might otherwise supply.
/// </summary>
/// <remarks>
/// Without this, the CLR could end up with two distinct copies of <c>Tauri.Plugin.DotNet.dll</c>
/// loaded (this program's own, from its build output, and the backend's, copied next to it by
/// <c>CopyLocalLockFileAssemblies</c>). The backend's <see cref="IBridgeBackend"/> implementation,
/// built against the backend's copy, would then not be assignable to the parameter type that
/// <see cref="Hosting.BackendDiscovery.CreateBackend(Assembly)"/> expects, resolved against a
/// different copy - failing with a confusing cast/type-mismatch error instead of doing anything
/// useful. The project reference to <c>Tauri.Plugin.DotNet</c> is compile-time only
/// (<c>Private=false</c>, <c>ExcludeAssets=runtime</c>) precisely so this resolver is the only source
/// of that assembly at run time. Install this before touching any type from the backend or from
/// <c>Tauri.Plugin.DotNet</c>.
/// </remarks>
/// <param name="backendPath">
/// The backend dll's path, as given on the command line - a private copy the Rust side made
/// (<c>src/shadow.rs</c>) before spawning this process, not the original build output. Resolving
/// dependencies by path (<see cref="AssemblyLoadContext.LoadFromAssemblyPath"/>) is therefore safe:
/// only the copy is held open, never the original that `dotnet build` needs to replace. The same
/// applies to <see cref="NativeLibrary.Load(string)"/> below: a native dependency
/// (<c>runtimes/&lt;rid&gt;/native/</c>, resolved from the backend's own <c>.deps.json</c> via
/// <see cref="AssemblyDependencyResolver.ResolveUnmanagedDllToPath"/>) is loaded from the same
/// disposable copy, never the original.
/// </param>
internal static class BackendAssemblyResolver
{
    public static void Install(string backendPath)
    {
        var resolver = new AssemblyDependencyResolver(backendPath);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var resolvedPath = resolver.ResolveAssemblyToPath(name);
            return resolvedPath is null ? null : context.LoadFromAssemblyPath(resolvedPath);
        };
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
        {
            var resolvedPath = resolver.ResolveUnmanagedDllToPath(name);
            return resolvedPath is null ? IntPtr.Zero : NativeLibrary.Load(resolvedPath);
        };
    }
}
