using System.Text.Json.Serialization;
using Tauri.Plugin.DotNet;

namespace SampleApp.Backend.Services;

/// <summary>
/// Event payload for progress updates. Demonstrates typed events
/// via the [BridgeEvent] attribute.
/// </summary>
[BridgeEvent("progress")]
public class ProgressEvent
{
    public int Percent { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// Event payload when the child window button is pressed. Parent window subscribes to demonstrate cross-window communication.
/// </summary>
[BridgeEvent("childButtonPressed")]
public class ChildButtonPressedEvent
{
    public string Message { get; set; } = "";
    public DateTime Time { get; set; }
}

/// <summary>
/// Event payload sent only to the window that made the call (see <c>BackendService.EchoToCaller</c>).
/// </summary>
[BridgeEvent("echoToCaller")]
public class EchoToCallerEvent
{
    public string Text { get; set; } = "";
    public string Window { get; set; } = "";
}

/// <summary>
/// What C# can ask a window to do: the reverse of a service. The frontend implements it (the
/// generated <c>implementFrontend</c>) and C# calls it through
/// <c>dispatcher.GetFrontend&lt;IFrontend&gt;(windowLabel)</c>, awaiting the answer.
/// </summary>
[BridgeFrontend]
public interface IFrontend
{
    /// <summary>Shows the native file dialog in the window. Returns the chosen path, or null if it was cancelled.</summary>
    Task<string?> PickFile(string title, CancellationToken cancellationToken);
}

/// <summary>What C# did with the chosen file; no path means the dialog was cancelled.</summary>
public class FilePickResult
{
    public string? Path { get; set; }
    public long? SizeBytes { get; set; }
}

/// <summary>
/// A model representing a person, used to demonstrate
/// complex type serialization across the bridge.
/// </summary>
public class Person
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public string? Email { get; set; }

    /// <summary>Custom JSON name via [JsonPropertyName].</summary>
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";

    /// <summary>Internal field excluded from JS via [JsonIgnore].</summary>
    [JsonIgnore]
    public string InternalNotes { get; set; } = "";
}

/// <summary>
/// A model extending Person to demonstrate model inheritance.
/// In TypeScript this should generate: interface Employee extends Person { ... }
/// </summary>
public class Employee : Person
{
    public string Department { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>
/// Sample service demonstrating various method signatures
/// that get exposed to the JS frontend via the bridge.
/// </summary>
[BridgeService]
public class BackendService
{
    private readonly BridgeDispatcher _dispatcher;

    public BackendService(BridgeDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Demonstrates C# calling the frontend: it asks the calling window, through the typed
    /// <see cref="IFrontend"/>, to show a file dialog, awaits the answer, and reads the file. The
    /// cancellation token also cancels the wait for the dialog (for example on a JS timeout).
    /// </summary>
    public async Task<FilePickResult> PickFileFromCSharp(CallContext context, CancellationToken cancellationToken)
    {
        var window = context.WindowLabel ?? throw new InvalidOperationException("The calling window is unknown.");

        var path = await _dispatcher.GetFrontend<IFrontend>(window).PickFile("Picked from C#", cancellationToken);

        return new FilePickResult
        {
            Path = path,
            SizeBytes = path is null ? null : new FileInfo(path).Length,
        };
    }

    /// <summary>
    /// Notify the parent (and any other attached windows) that the child window button was pressed.
    /// Used to demonstrate child-to-parent communication via bridge emit.
    /// </summary>
    public void NotifyParent(string message)
    {
        _dispatcher.Emit(new ChildButtonPressedEvent
        {
            Message = message,
            Time = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Demonstrates a targeted event: only the window that made this call receives it.
    /// <see cref="CallContext"/> is injected by the bridge and does not appear in the TypeScript signature.
    /// </summary>
    public void EchoToCaller(CallContext context, string text)
    {
        var window = context.WindowLabel ?? throw new InvalidOperationException("The calling window is unknown.");
        _dispatcher.EmitTo(window, new EchoToCallerEvent { Text = text, Window = window });
    }

    /// <summary>
    /// Simple string-to-string method.
    /// </summary>
    public string Greet(string name)
    {
        return $"Hello, {name}! Greetings from .NET.";
    }

    /// <summary>
    /// Returns a model object to demonstrate complex type serialization.
    /// </summary>
    public Person GetPerson(string name, int age)
    {
        return new Person
        {
            Name = name,
            Age = age,
            Email = $"{name.ToLowerInvariant()}@example.com",
            DisplayName = $"{name} (age {age})"
        };
    }

    /// <summary>
    /// Demonstrates multiple numeric parameters.
    /// </summary>
    public int Add(int a, int b)
    {
        return a + b;
    }

    /// <summary>
    /// Demonstrates async methods (Task&lt;T&gt;).
    /// </summary>
    public async Task<string> GetGreetingAsync(string name)
    {
        await Task.Delay(500); // Simulate async work
        return $"Async hello, {name}! This took 500ms on the .NET side.";
    }

    /// <summary>
    /// Returns a list of Person objects.
    /// </summary>
    public List<Person> GetPeople()
    {
        return new List<Person>
        {
            new() { Name = "Alice", Age = 30, Email = "alice@example.com" },
            new() { Name = "Bob", Age = 25 },
            new() { Name = "Charlie", Age = 35, Email = "charlie@example.com" }
        };
    }

    /// <summary>
    /// Returns an Employee to demonstrate model inheritance.
    /// </summary>
    public Employee GetEmployee(string name, string department)
    {
        return new Employee
        {
            Name = name,
            Age = 28,
            Department = department,
            Title = "Engineer",
            DisplayName = name
        };
    }

    /// <summary>
    /// Demonstrates byte[] round-trip. System.Text.Json serializes byte[]
    /// as a base64 string, so JS receives/sends a string.
    /// </summary>
    public byte[] EchoBytes(byte[] data)
    {
        // Reverse the bytes to prove we processed them
        var reversed = new byte[data.Length];
        Array.Copy(data, reversed, data.Length);
        Array.Reverse(reversed);
        return reversed;
    }

    /// <summary>
    /// Demonstrates ValueTask&lt;T&gt; return type support.
    /// </summary>
    public async ValueTask<string> GetValueTaskGreeting(string name)
    {
        await Task.Delay(200);
        return $"ValueTask hello, {name}! Returned via ValueTask<string>.";
    }

    /// <summary>
    /// A deliberately slow method for testing call timeouts and cancellation.
    /// Accepts an optional CancellationToken which is auto-injected by the bridge
    /// when a cancel message arrives from JS.
    /// </summary>
    public async Task<string> SlowMethod(int seconds, CancellationToken cancellationToken = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        return $"Completed after {seconds} second(s).";
    }

    /// <summary>
    /// Demonstrates .NET → JS event push.
    /// Runs a simulated task that emits progress events every 500ms.
    /// </summary>
    public async Task<string> RunWithProgress(int steps, CancellationToken cancellationToken = default)
    {
        for (int i = 1; i <= steps; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(500, cancellationToken);

            var percent = (int)((double)i / steps * 100);
            _dispatcher.Emit(new ProgressEvent
            {
                Percent = percent,
                Message = $"Step {i}/{steps} complete"
            });
        }
        return $"All {steps} steps completed!";
    }

    /// <summary>
    /// Internal helper that should NOT be exposed to JS.
    /// Demonstrates the [BridgeIgnore] attribute.
    /// </summary>
    [BridgeIgnore]
    public string InternalHelper()
    {
        return "This method is not callable from JS.";
    }
}
