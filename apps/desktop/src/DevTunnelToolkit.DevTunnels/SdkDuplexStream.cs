using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.DevTunnels;

/// <summary>Preserves the SDK SSH channel's independent write EOF and read direction.</summary>
internal sealed class SdkDuplexStream(Stream stream) : Stream, IHalfCloseStream
{
    private int writesCompleted;
    public override bool CanRead => stream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => writesCompleted == 0 && stream.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public async Task CompleteWritesAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref writesCompleted, 1, 0) == 0)
        {
            // Both SshStream and SecureStream delegate a zero-length byte[] write to SSH EOF.
            // Use this explicit overload because generic Stream memory overloads may elide empty writes.
            await stream.WriteAsync(Array.Empty<byte>(), 0, 0, cancellationToken).ConfigureAwait(false);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => stream.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => stream.ReadAsync(buffer, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count)
    {
        ThrowIfWritesCompleted();
        stream.Write(buffer, offset, count);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ThrowIfWritesCompleted();
        return stream.WriteAsync(buffer, offset, count, cancellationToken);
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfWritesCompleted();
        return stream.WriteAsync(buffer, cancellationToken);
    }
    private void ThrowIfWritesCompleted()
    {
        if (writesCompleted != 0) throw new IOException("Writes have already completed.");
    }
    public override void Flush() => stream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => stream.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) stream.Dispose();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
