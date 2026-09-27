using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Tauri.Plugin.DotNet.SidecarHost;

/// <summary>
/// The framed connection to the Rust plugin: a 4-byte little-endian length prefix followed by UTF-8
/// JSON, matching <c>src/sidecar.rs</c>'s <c>write_frame</c>/<c>read_frame</c> exactly. A single
/// background writer serializes every outgoing message onto the stream, so call responses and events
/// (sent from different call sites, potentially concurrently) never interleave mid-frame.
/// </summary>
/// <remarks>
/// Uses <see cref="NamedPipeClientStream"/>, which is a real Win32 named pipe on Windows - the same
/// primitive the Rust side's local socket uses there, so the two interoperate directly. On
/// Linux/macOS, .NET's named pipes and the `interprocess` crate's namespaced sockets are not
/// guaranteed to use the same underlying naming scheme; this has only been exercised on Windows so far.
/// </remarks>
internal sealed class SidecarPipe : IAsyncDisposable
{
    private readonly NamedPipeClientStream _stream;
    private readonly Channel<Envelope> _outbox = Channel.CreateUnbounded<Envelope>();
    private readonly Task _writerTask;

    private SidecarPipe(NamedPipeClientStream stream)
    {
        _stream = stream;
        _writerTask = Task.Run(RunWriterAsync);
    }

    public static async Task<SidecarPipe> ConnectAsync(string pipeName, CancellationToken cancellationToken)
    {
        var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return new SidecarPipe(stream);
    }

    /// <summary>Queues a message to be framed and written; returns immediately. Used by both the main
    /// loop (responses, the handshake) and <see cref="PipeEventSink"/> (events), which may run concurrently.</summary>
    public void Enqueue(Envelope envelope) => _outbox.Writer.TryWrite(envelope);

    /// <summary>Reads messages until the connection ends.</summary>
    public async IAsyncEnumerable<Envelope> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var envelope = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (envelope is null)
                yield break;
            yield return envelope;
        }
    }

    private async Task RunWriterAsync()
    {
        await foreach (var envelope in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var json = JsonSerializer.Serialize(envelope, Envelope.JsonOptions);
            await WriteFrameAsync(json).ConfigureAwait(false);
        }
    }

    private async Task WriteFrameAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
        await _stream.WriteAsync(header).ConfigureAwait(false);
        await _stream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private async Task<Envelope?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(header, cancellationToken).ConfigureAwait(false))
            return null;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var buffer = new byte[length];
        if (!await ReadExactAsync(buffer, cancellationToken).ConfigureAwait(false))
            return null;

        var json = Encoding.UTF8.GetString(buffer);
        return JsonSerializer.Deserialize<Envelope>(json, Envelope.JsonOptions);
    }

    /// <returns><see langword="false"/> when the stream ended cleanly before any byte of this frame arrived.</returns>
    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return offset == 0 ? false : throw new IOException("The sidecar connection ended mid-frame.");
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _outbox.Writer.TryComplete();
        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch
        {
            // Best effort: the connection may already be gone.
        }
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
