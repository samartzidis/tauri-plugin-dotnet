namespace Tauri.Plugin.DotNet.Generator;

/// <summary>A problem in the user's backend that stops generation. Reported as a build error, without a stack trace.</summary>
sealed class BindingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Warnings and errors in the format MSBuild's Exec task recognises (<c>warning CODE: text</c>), so they show up as
/// warnings and errors of the backend's build. Each warning is printed once per run.
/// </summary>
static class Diagnostics
{
    /// <summary>A converter the generator cannot see through: the type is written as <c>unknown</c>.</summary>
    internal const string CustomConverter = "TAURIDOTNET010";

    /// <summary>Two methods of a service or frontend share a name.</summary>
    internal const string OverloadedMethod = "TAURIDOTNET011";

    // Per thread, so tests that run in parallel do not see each other's warnings
    [ThreadStatic] private static List<string>? t_captured;
    [ThreadStatic] private static HashSet<string>? t_seen;

    internal static void Warning(string code, string message)
    {
        var line = $"[{CodeEmitter.ToolName}] warning {code}: {message}";
        t_seen ??= new HashSet<string>();
        if (!t_seen.Add(line)) return;

        if (t_captured != null) t_captured.Add(line);
        else Console.WriteLine(line);
    }

    /// <summary>Starts a new run: warnings that were already printed are printed again.</summary>
    internal static void Reset() => t_seen = null;

    /// <summary>Collects the warnings of the current thread instead of printing them (for tests).</summary>
    internal static IDisposable Capture(out List<string> warnings)
    {
        var previous = t_captured;
        var previousSeen = t_seen;
        t_captured = warnings = new List<string>();
        t_seen = null;
        return new Restore(() => { t_captured = previous; t_seen = previousSeen; });
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
