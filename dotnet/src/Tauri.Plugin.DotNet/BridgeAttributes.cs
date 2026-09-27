namespace Tauri.Plugin.DotNet;

/// <summary>
/// Marks a class as a bridge service whose public methods
/// will be callable from the JavaScript frontend.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class BridgeServiceAttribute : Attribute
{
    /// <summary>
    /// Optional override for the service name used in JS bindings.
    /// Defaults to the class name.
    /// </summary>
    public string? Name { get; set; }
}

/// <summary>
/// Excludes a public method from the bridge. Methods marked with this
/// attribute will not be callable from JS and will not appear in
/// generated TypeScript bindings.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class BridgeIgnoreAttribute : Attribute { }

/// <summary>
/// Declares a typed event that can be emitted from .NET to JS.
/// Apply to a class or record that represents the event payload.
/// The generator will create typed subscription helpers in TypeScript.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class BridgeEventAttribute : Attribute
{
    /// <summary>
    /// The event name used on the wire. This is the string passed to
    /// <c>dispatcher.Emit()</c> and <c>events.on()</c>.
    /// </summary>
    public string Name { get; }

    public BridgeEventAttribute(string name)
    {
        Name = name;
    }
}

/// <summary>
/// Declares a public interface of calls that .NET makes to the frontend, the reverse of a
/// <see cref="BridgeServiceAttribute"/> service. Every method must return <see cref="Task"/> or
/// <see cref="Task{TResult}"/>. The generator emits a matching TypeScript interface and an
/// <c>implement…</c> function for the frontend to register its implementation with; C# then gets
/// a typed proxy from <see cref="BridgeDispatcher.GetFrontend{T}"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
public class BridgeFrontendAttribute : Attribute
{
    /// <summary>
    /// Optional override for the name used on the wire and in the generated <c>implement…</c>
    /// function. Defaults to the interface name without a leading <c>I</c> (<c>IFrontend</c> becomes <c>Frontend</c>).
    /// </summary>
    public string? Name { get; set; }
}
