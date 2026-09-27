using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Tauri.Plugin.DotNet.SidecarHost;

namespace Tauri.Plugin.DotNet.SidecarHost.Tests;

/// <summary>
/// Spawns the real, published <c>Tauri.Plugin.DotNet.SidecarHost.dll</c> as a child process against
/// the real <c>SidecarFixtureBackend</c> build output, acting as the Rust plugin would: this test IS
/// the pipe server. This is the actual proof that <see cref="BackendAssemblyResolver"/> resolves the
/// backend's own copy of <c>Tauri.Plugin.DotNet.dll</c> correctly (the tool's published folder, under
/// <c>sidecar-tool\</c>, deliberately carries none of its own - see the test project's
/// <c>PublishSidecarHostForTests</c> target), not just that the code compiles.
/// </summary>
/// <remarks>Grouped in one collection so these do not spawn several `dotnet` processes at once.</remarks>
[Collection("SidecarHost process")]
public class SidecarProcessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static string ToolPath => Path.Combine(AppContext.BaseDirectory, "sidecar-tool", "Tauri.Plugin.DotNet.SidecarHost.dll");
    private static string BackendPath => Path.Combine(AppContext.BaseDirectory, "SidecarFixtureBackend.dll");

    [Fact]
    public async Task A_call_round_trips_through_the_real_process_and_it_shuts_down_cleanly()
    {
        var pipeName = "tdn-sidecar-test-" + Guid.NewGuid();
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(Timeout);

        using var process = StartSidecar(pipeName, BackendPath);
        await server.WaitForConnectionAsync(cts.Token);

        Assert.IsType<ReadyEnvelope>(await ReadEnvelopeAsync(server, cts.Token));

        const string callId = "c1";
        var requestJson = """{"callId":"c1","method":"EchoService.Echo","args":["hello"]}""";
        await WriteEnvelopeAsync(server, new CallEnvelope(callId, "main", requestJson), cts.Token);

        var response = Assert.IsType<ResponseEnvelope>(await ReadEnvelopeAsync(server, cts.Token));
        Assert.Equal(callId, response.CallId);
        Assert.Contains("\"result\":\"hello\"", response.Json);

        await WriteEnvelopeAsync(server, new ShutdownEnvelope(5000), cts.Token);
        Assert.True(process.WaitForExit((int)Timeout.TotalMilliseconds), "the sidecar did not exit after a shutdown request");
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task A_missing_backend_reports_an_error_envelope_and_exits_nonzero()
    {
        var pipeName = "tdn-sidecar-test-" + Guid.NewGuid();
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(Timeout);

        using var process = StartSidecar(pipeName, Path.Combine(AppContext.BaseDirectory, "NoSuchBackend.dll"));
        await server.WaitForConnectionAsync(cts.Token);

        var error = Assert.IsType<ErrorEnvelope>(await ReadEnvelopeAsync(server, cts.Token));
        Assert.NotEmpty(error.Message);

        Assert.True(process.WaitForExit((int)Timeout.TotalMilliseconds));
        Assert.NotEqual(0, process.ExitCode);
    }

    [Fact]
    public async Task Cancelling_an_unknown_call_id_is_ignored_and_the_process_keeps_running()
    {
        var pipeName = "tdn-sidecar-test-" + Guid.NewGuid();
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(Timeout);

        using var process = StartSidecar(pipeName, BackendPath);
        await server.WaitForConnectionAsync(cts.Token);
        Assert.IsType<ReadyEnvelope>(await ReadEnvelopeAsync(server, cts.Token));

        await WriteEnvelopeAsync(server, new CancelEnvelope("no-such-call"), cts.Token);

        // Prove the process is still alive and serving by completing a real call afterward.
        var requestJson = """{"callId":"c2","method":"EchoService.Echo","args":["still alive"]}""";
        await WriteEnvelopeAsync(server, new CallEnvelope("c2", "main", requestJson), cts.Token);
        var response = Assert.IsType<ResponseEnvelope>(await ReadEnvelopeAsync(server, cts.Token));
        Assert.Contains("still alive", response.Json);

        await WriteEnvelopeAsync(server, new ShutdownEnvelope(5000), cts.Token);
        Assert.True(process.WaitForExit((int)Timeout.TotalMilliseconds));
    }

    private static Process StartSidecar(string pipeName, string backendPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(ToolPath);
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--backend");
        startInfo.ArgumentList.Add(backendPath);

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the sidecar process.");
    }

    private static async Task WriteEnvelopeAsync(Stream stream, Envelope envelope, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(envelope, Envelope.JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task<Envelope> ReadEnvelopeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var buffer = new byte[length];
        await ReadExactAsync(stream, buffer, cancellationToken);
        return JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(buffer), Envelope.JsonOptions)
            ?? throw new JsonException("Deserialized to null.");
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
                throw new IOException("The sidecar process ended the connection early.");
            offset += read;
        }
    }
}
