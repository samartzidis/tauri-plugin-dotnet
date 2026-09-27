using System.Reflection;

namespace Tauri.Plugin.DotNet.Generator;

static class ServiceDiscovery
{
    internal static List<ServiceDef> DiscoverServices(Assembly assembly)
    {
        var services = new List<ServiceDef>();

        foreach (var type in assembly.GetExportedTypes())
        {
            var attr = type.CustomAttributes
                .FirstOrDefault(a => a.AttributeType.Name == "BridgeServiceAttribute");
            if (attr == null) continue;

            // Get optional Name property from the attribute
            var nameArg = attr.NamedArguments
                .FirstOrDefault(a => a.MemberName == "Name");
            var serviceName = nameArg.TypedValue.Value as string ?? type.Name;

            var service = BuildService(type, serviceName);
            services.Add(service);
            Console.WriteLine($"[{CodeEmitter.ToolName}] Found service: {serviceName} ({service.Methods.Count} methods)");
        }

        return services;
    }

    /// <summary>The exposed methods of a service class. Fails when two of them share a name, see <see cref="RequireUniqueNames"/>.</summary>
    internal static ServiceDef BuildService(Type type, string serviceName)
    {
        var methods = new List<MethodDef>();
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName)
                continue;
            if (method.CustomAttributes.Any(a => a.AttributeType.Name == "BridgeIgnoreAttribute"))
                continue;

            methods.Add(BuildMethod(method));
        }

        RequireUniqueNames($"Service '{serviceName}' ({type.Name})", methods, canIgnore: true);
        return new ServiceDef(serviceName, methods);
    }

    /// <summary>
    /// The bridge routes a call by service and method name, and a name can only be one method: the dispatcher registers the
    /// methods of a service by name, ignoring case, and keeps only the last of several. Rather than generate functions
    /// that cannot all work (and duplicate names TypeScript rejects), fail the build. Must match
    /// <c>BridgeDispatcher.RegisterService</c> and <c>FrontendProxy.Validate</c> in the library.
    /// </summary>
    internal static void RequireUniqueNames(string owner, IEnumerable<MethodDef> methods, bool canIgnore)
    {
        var shared = methods
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{string.Join("/", g.Select(m => m.Name).Distinct())} ({g.Count()} methods)")
            .ToList();
        if (shared.Count == 0) return;

        var fix = canIgnore ? "rename all but one, or mark them [BridgeIgnore]" : "rename all but one";
        throw new BindingException(Diagnostics.OverloadedMethod,
            $"{owner} has methods that share a name: {string.Join("; ", shared)}. " +
            $"The bridge routes calls by method name (ignoring case), so each method needs its own name; {fix}.");
    }

    /// <summary>
    /// Discover interfaces marked with [BridgeFrontend]: the calls .NET makes to the frontend.
    /// </summary>
    internal static List<FrontendDef> DiscoverFrontends(Assembly assembly)
    {
        var frontends = new List<FrontendDef>();

        foreach (var type in assembly.GetExportedTypes())
        {
            if (!type.IsInterface) continue;

            var attr = type.CustomAttributes
                .FirstOrDefault(a => a.AttributeType.Name == "BridgeFrontendAttribute");
            if (attr == null) continue;

            var nameArg = attr.NamedArguments
                .FirstOrDefault(a => a.MemberName == "Name");
            var name = nameArg.TypedValue.Value as string ?? StringHelpers.DefaultFrontendName(type.Name);

            var frontend = BuildFrontend(type, name);
            frontends.Add(frontend);
            Console.WriteLine($"[{CodeEmitter.ToolName}] Found frontend: {name} ({frontend.Methods.Count} methods)");
        }

        return frontends;
    }

    /// <summary>The methods of a [BridgeFrontend] interface. The runtime refuses overloads there too (<c>GetFrontend</c>).</summary>
    internal static FrontendDef BuildFrontend(Type type, string name)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(BuildMethod)
            .ToList();

        RequireUniqueNames($"Frontend interface '{type.Name}'", methods, canIgnore: false);
        return new FrontendDef(name, type.Name, methods);
    }

    private static MethodDef BuildMethod(MethodInfo method)
    {
        var returnType = TypeMapper.UnwrapTaskType(method.ReturnType);
        var isAsync = TypeMapper.IsTaskType(method.ReturnType);

        // Filter out injected parameters: CallContext and CancellationToken are
        // handled by the bridge and must not appear in the TS signature.
        var parameters = method.GetParameters()
            .Where(p => p.ParameterType.Name != "CallContext"
                && p.ParameterType.FullName != typeof(CancellationToken).FullName)
            .Select(p => new ParamDef(
                p.Name ?? $"arg{p.Position}",
                p.ParameterType,
                NullabilityReader.IsParameterNullable(p, method)))
            .ToList();

        return new MethodDef(method.Name, parameters, returnType, isAsync, NullabilityReader.IsReturnNullable(method));
    }

    /// <summary>
    /// Discover types marked with [BridgeEvent("eventName")].
    /// Each type represents an event payload with a named event.
    /// </summary>
    internal static List<EventDef> DiscoverEvents(Assembly assembly)
    {
        var events = new List<EventDef>();

        foreach (var type in assembly.GetExportedTypes())
        {
            var attr = type.CustomAttributes
                .FirstOrDefault(a => a.AttributeType.Name == "BridgeEventAttribute");
            if (attr == null) continue;

            // The event name is the first constructor argument
            var eventName = attr.ConstructorArguments.FirstOrDefault().Value as string;
            if (string.IsNullOrEmpty(eventName)) continue;

            events.Add(new EventDef(eventName, type));
            Console.WriteLine($"[{CodeEmitter.ToolName}] Found event: '{eventName}' → {type.Name}");
        }

        return events;
    }
}
