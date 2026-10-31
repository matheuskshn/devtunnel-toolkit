using System.Text;
using DevTunnelToolkit.Application;
using DevTunnelToolkit.Core;
using Xunit;

namespace DevTunnelToolkit.IntegrationTests;

public sealed class TunnelProxyHealthTests
{
    [Theory]
    [InlineData("HTTP/1.1 400 Bad Request\r\n")]
    [InlineData("HTTP/1.0 403 Forbidden\r\n")]
    public async Task ProxyPolicyResponsesProveTheChannelWithoutRequestingADestination(string response)
    {
        var stream = new ScriptedStream(Encoding.ASCII.GetBytes(response));
        var relay = new ScriptedRelay(stream);
        await TunnelProxyHealth.CheckAsync(relay, 3140, ProxyProtocol.Http, default);
        var sent = Encoding.ASCII.GetString(stream.Sent.ToArray());
        Assert.StartsWith("OPTIONS * HTTP/1.1\r\n", sent);
        Assert.DoesNotContain("Authorization", sent);
        Assert.DoesNotContain("CONNECT", sent);
    }

    [Fact]
    public async Task SocksCheckStopsAfterGreetingWithoutSendingATarget()
    {
        var stream = new ScriptedStream([5, 0]);
        await TunnelProxyHealth.CheckAsync(new ScriptedRelay(stream), 3180, ProxyProtocol.Socks5, default);
        Assert.Equal(new byte[] { 5, 1, 0 }, stream.Sent.ToArray());
    }

    [Fact]
    public async Task ConnectedButSilentProxyTimesOutAndDisposesTheChannel()
    {
        var stream = new ScriptedStream([], silent: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TunnelProxyHealth.CheckAsync(new ScriptedRelay(stream), 3140, ProxyProtocol.Http, timeout.Token));
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task AnEchoServiceIsNotMisreportedAsAHealthyHttpProxy()
    {
        var stream = new ScriptedStream(Encoding.ASCII.GetBytes("arbitrary non-proxy bytes\r\n"));
        var error = await Assert.ThrowsAsync<ToolkitException>(() => TunnelProxyHealth.CheckAsync(new ScriptedRelay(stream), 3140, ProxyProtocol.Http, default));
        Assert.Equal("TUNNEL_PROXY_PROTOCOL_INVALID", error.Code);
    }

    private sealed class ScriptedRelay(Stream stream) : IRelayConnection
    {
        public event Action<ConnectionState>? StateChanged { add { } remove { } }
        public Task<Stream> OpenStreamAsync(int remotePort, CancellationToken cancellationToken) => Task.FromResult(stream);
        public Task RefreshPortsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedStream(byte[] reply, bool silent = false) : Stream
    {
        private readonly MemoryStream incoming = new(reply);
        public MemoryStream Sent { get; } = new();
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => incoming.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (silent) await Task.Delay(Timeout.Infinite, cancellationToken);
            return await incoming.ReadAsync(buffer, cancellationToken);
        }
        public override void Write(byte[] buffer, int offset, int count) => Sent.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => Sent.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; incoming.Dispose(); base.Dispose(disposing); }
    }
}
