namespace Tauri.Plugin.DotNet.SidecarHost;

/// <summary>The command-line contract with the Rust plugin (<c>src/sidecar.rs</c>'s <c>spawn_and_handshake</c>).</summary>
internal sealed record CliOptions(string PipeName, string BackendPath)
{
    public static CliOptions Parse(string[] args)
    {
        string? pipeName = null;
        string? backendPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Length:
                    pipeName = args[++i];
                    break;
                case "--backend" when i + 1 < args.Length:
                    backendPath = args[++i];
                    break;
            }
        }

        if (string.IsNullOrEmpty(pipeName))
            throw new ArgumentException("Missing required --pipe <name> argument.");
        if (string.IsNullOrEmpty(backendPath))
            throw new ArgumentException("Missing required --backend <path> argument.");

        return new CliOptions(pipeName, backendPath);
    }
}
