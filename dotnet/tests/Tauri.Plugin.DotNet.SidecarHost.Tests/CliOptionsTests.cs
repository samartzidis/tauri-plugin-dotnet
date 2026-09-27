using Tauri.Plugin.DotNet.SidecarHost;

namespace Tauri.Plugin.DotNet.SidecarHost.Tests;

public class CliOptionsTests
{
    [Fact]
    public void Parses_both_required_arguments()
    {
        var options = CliOptions.Parse(["--pipe", "my-pipe", "--backend", "C:/app/Backend.dll"]);

        Assert.Equal("my-pipe", options.PipeName);
        Assert.Equal("C:/app/Backend.dll", options.BackendPath);
    }

    [Fact]
    public void Order_of_the_arguments_does_not_matter()
    {
        var options = CliOptions.Parse(["--backend", "C:/app/Backend.dll", "--pipe", "my-pipe"]);

        Assert.Equal("my-pipe", options.PipeName);
        Assert.Equal("C:/app/Backend.dll", options.BackendPath);
    }

    [Fact]
    public void A_missing_pipe_argument_throws() =>
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--backend", "C:/app/Backend.dll"]));

    [Fact]
    public void A_missing_backend_argument_throws() =>
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--pipe", "my-pipe"]));

    [Fact]
    public void A_flag_with_no_value_after_it_is_ignored_and_still_reports_missing() =>
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--pipe"]));
}
