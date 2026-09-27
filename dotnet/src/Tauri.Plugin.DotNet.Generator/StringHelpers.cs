using System.Text;

namespace Tauri.Plugin.DotNet.Generator;

static class StringHelpers
{
    internal static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (char.IsLower(name[0])) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>The name of a generic type without its arity suffix: "Page`1" becomes "Page".</summary>
    internal static string StripGenericArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    internal static string FormatEnumValue(object? value)
    {
        if (value is string s) return $"\"{s}\"";
        return value?.ToString() ?? "0";
    }

    /// <summary>
    /// Convert a camelCase or snake_case event name to PascalCase for function naming.
    /// E.g. "progress" → "Progress", "task_completed" → "TaskCompleted"
    /// </summary>
    internal static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        var sb = new StringBuilder();
        bool capitalizeNext = true;
        foreach (var ch in name)
        {
            if (ch == '_' || ch == '-')
            {
                capitalizeNext = true;
                continue;
            }
            sb.Append(capitalizeNext ? char.ToUpperInvariant(ch) : ch);
            capitalizeNext = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// The wire name of a [BridgeFrontend] interface without an explicit name: the interface name
    /// without a leading "I" that is followed by an uppercase letter ("IFrontend" becomes "Frontend").
    /// Must match the rule in the Tauri.Plugin.DotNet library.
    /// </summary>
    internal static string DefaultFrontendName(string interfaceName) =>
        interfaceName.Length > 1 && interfaceName[0] == 'I' && char.IsUpper(interfaceName[1]) ? interfaceName[1..] : interfaceName;

    /// <summary>
    /// Deletes any .ts files in the output directory that were not generated
    /// in this run. This removes stale bindings from renamed or deleted services.
    /// </summary>
    internal static void CleanupStaleFiles(string outputDir, HashSet<string> generatedFiles)
    {
        foreach (var file in Directory.GetFiles(outputDir, "*.ts"))
        {
            var fullPath = Path.GetFullPath(file);
            if (!generatedFiles.Contains(fullPath))
            {
                File.Delete(fullPath);
                Console.WriteLine($"[{CodeEmitter.ToolName}] Deleted stale file: {file}");
            }
        }
    }
}
