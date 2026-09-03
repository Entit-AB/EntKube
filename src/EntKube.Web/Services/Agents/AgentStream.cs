using System.Buffers;
using System.IO.Pipelines;
using EntKube.Agents.Protocol;

namespace EntKube.Web.Services.Agents;

/// <summary>
/// One multiplexed TCP stream carried over an agent link, presented to callers as
/// an ordinary <see cref="Stream"/> so it can be handed straight to
/// <c>SocketsHttpHandler.ConnectCallback</c>.
///
/// Reads are fed by the link's receive loop through a <see cref="Pipe"/>, which
/// gives real backpressure: if a caller stops reading, the pipe fills, the
/// receive loop stalls on that stream and stops pulling more of it off the link
/// rather than buffering without limit.
///
/// Writes are split into protocol-sized frames and serialised by the link.
/// </summary>
public sealed class AgentStream : Stream
{
    private readonly Pipe pipe;
    private readonly Func<uint, ReadOnlyMemory<byte>, CancellationToken, Task> sendData;
    private readonly Func<uint, Task> sendClose;
    private readonly Action onDisposed;
    private bool disposed;

    internal AgentStream(
        uint streamId,
        Func<uint, ReadOnlyMemory<byte>, CancellationToken, Task> sendData,
        Func<uint, Task> sendClose,
        Action onDisposed)
    {
        StreamId = streamId;
        this.sendData = sendData;
        this.sendClose = sendClose;
        this.onDisposed = onDisposed;

        // Pause the producer well before memory becomes a problem; 1 MB in flight
        // per stream is generous for the API traffic this carries.
        pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 1024 * 1024,
            resumeWriterThreshold: 256 * 1024,
            useSynchronizationContext: false));
    }

    /// <summary>Identifier this stream carries on the link.</summary>
    public uint StreamId { get; }

    /// <summary>Called by the receive loop when a Data frame arrives for this stream.</summary>
    internal async ValueTask FeedAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (disposed) return;

        FlushResult result = await pipe.Writer.WriteAsync(data, ct);

        if (result.IsCompleted)
        {
            // Reader is gone — nothing more will be consumed.
            await pipe.Writer.CompleteAsync();
        }
    }

    /// <summary>Called by the receive loop when the far end closes this stream.</summary>
    internal void CompleteFeed(string? reason = null)
        => pipe.Writer.Complete(reason is null ? null : new IOException(reason));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ReadResult result = await pipe.Reader.ReadAsync(ct);

        if (result.Buffer.IsEmpty && result.IsCompleted)
        {
            pipe.Reader.AdvanceTo(result.Buffer.End);
            return 0;
        }

        int count = (int)Math.Min(buffer.Length, result.Buffer.Length);
        ReadOnlySequence<byte> slice = result.Buffer.Slice(0, count);
        slice.CopyTo(buffer.Span[..count]);
        pipe.Reader.AdvanceTo(slice.End);
        return count;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => await ReadAsync(buffer.AsMemory(offset, count), ct);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        // The link caps frame size, so a large write becomes several frames.
        int offset = 0;

        while (offset < buffer.Length)
        {
            int chunk = Math.Min(AgentProtocol.MaxPayloadSize, buffer.Length - offset);
            await sendData(StreamId, buffer.Slice(offset, chunk), ct);
            offset += chunk;
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => await WriteAsync(buffer.AsMemory(offset, count), ct);

    public override async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;

        try
        {
            await sendClose(StreamId);
        }
        catch
        {
            // The link may already be gone; closing a stream on a dead link is not
            // an error worth surfacing to the caller.
        }

        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
        onDisposed();

        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            // Synchronous disposal cannot await the Close frame; fire and forget it
            // so the far end still learns the stream is finished.
            _ = DisposeAsync().AsTask();
        }

        base.Dispose(disposing);
    }

    // ── Stream plumbing that does not apply to a network stream ──

    public override bool CanRead => !disposed;
    public override bool CanWrite => !disposed;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
}
