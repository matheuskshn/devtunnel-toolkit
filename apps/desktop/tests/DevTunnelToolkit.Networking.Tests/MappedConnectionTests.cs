using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DevTunnelToolkit.Core;
using Xunit;

namespace DevTunnelToolkit.Networking.Tests;

public sealed class MappedConnectionTests
{
    [Fact]
    public async Task ManualMappingPreservesLargeResponseAfterClientHalfClose()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var relay = new LoopbackRelay();
        await using var mapped = await MappedConnection.StartAsync(relay, [Manual(3140, FreePort())], timeout.Token);
        var payload = new byte[512 * 1024];
        Random.Shared.NextBytes(payload);

        var actual = await RoundTripAsync(mapped.Ports[0].LocalPort, payload, timeout.Token);

        Assert.Equal(payload, actual);
        Assert.Contains(3140, relay.RemotePorts);
        Assert.False(relay.Disposed);
    }

    [Fact]
    public async Task AutomaticReusesPreviousPortBeforeRemotePort()
    {
        await using var relay = new LoopbackRelay();
        var previous = FreePort();
        using var remoteReservation = Listen();
        var remote = Port(remoteReservation);
        await using var mapped = await MappedConnection.StartAsync(relay,
            [new PortMapping { RemotePort = remote, LastEffectivePort = previous }]);

        Assert.Equal(previous, Assert.Single(mapped.Ports).LocalPort);
    }

    [Fact]
    public async Task AutomaticFallsBackToRangeWithoutDisturbingOccupiedListeners()
    {
        await using var relay = new LoopbackRelay();
        using var previous = Listen();
        using var remote = Listen();
        var fallback = FreePort();
        await using var mapped = await MappedConnection.StartAsync(relay,
            [new PortMapping
            {
                RemotePort = Port(remote), LastEffectivePort = Port(previous),
                RangeStart = fallback, RangeEnd = fallback
            }]);

        Assert.Equal(fallback, Assert.Single(mapped.Ports).LocalPort);
        Assert.True(previous.Server.IsBound);
        Assert.True(remote.Server.IsBound);
    }

    [Fact]
    public async Task ReservedRangeNeverReusesAnOutOfRangePort()
    {
        await using var relay = new LoopbackRelay();
        using var previous = Listen();
        var rangePort = FreePort();
        await using var mapped = await MappedConnection.StartAsync(relay,
            [new PortMapping
            {
                RemotePort = Port(previous), LastEffectivePort = Port(previous), Mode = MappingMode.ReservedRange,
                RangeStart = rangePort, RangeEnd = rangePort
            }]);

        Assert.Equal(rangePort, Assert.Single(mapped.Ports).LocalPort);
    }

    [Fact]
    public async Task SamePortUsesRemotePortAndManualCollisionFails()
    {
        await using var relay = new LoopbackRelay();
        var remote = FreePort();
        await using var mapped = await MappedConnection.StartAsync(relay,
            [new PortMapping { RemotePort = remote, Mode = MappingMode.SamePort }]);
        Assert.Equal(remote, Assert.Single(mapped.Ports).LocalPort);

        var failure = await Assert.ThrowsAsync<ToolkitException>(() =>
            MappedConnection.StartAsync(relay, [Manual(3180, remote)]));
        Assert.Equal("LOCAL_PORT_UNAVAILABLE", failure.Code);
    }

    [Fact]
    public async Task FailedSecondBindRollsBackFirstBind()
    {
        await using var relay = new LoopbackRelay();
        using var occupied = Listen();
        var firstPort = FreePort();

        var failure = await Assert.ThrowsAsync<ToolkitException>(() =>
            MappedConnection.StartAsync(relay, [Manual(3140, firstPort), Manual(3180, Port(occupied))]));

        Assert.Equal("LOCAL_PORT_UNAVAILABLE", failure.Code);
        using var rebound = Listen(firstPort);
        Assert.True(rebound.Server.IsBound);
        Assert.True(occupied.Server.IsBound);
    }

    [Fact]
    public async Task OccupiedReservedRangeIsReportedClearly()
    {
        await using var relay = new LoopbackRelay();
        using var occupied = Listen();
        var port = Port(occupied);

        var failure = await Assert.ThrowsAsync<ToolkitException>(() =>
            MappedConnection.StartAsync(relay,
                [new PortMapping { RemotePort = 3140, Mode = MappingMode.ReservedRange, RangeStart = port, RangeEnd = port }]));

        Assert.Equal("PORT_RANGE_EXHAUSTED", failure.Code);
    }

    [Fact]
    public async Task DisabledMappingsDoNotBind()
    {
        await using var relay = new LoopbackRelay();
        using var occupied = Listen();
        await using var mapped = await MappedConnection.StartAsync(relay,
            [Manual(3140, Port(occupied)) with { Enabled = false }, new PortMapping { RemotePort = 3180, Mode = MappingMode.Disabled }]);

        Assert.Empty(mapped.Ports);
        Assert.Equal(0, mapped.ActiveClientCount);
    }

    [Fact]
    public async Task MultipleMappingsAndClientsAreIndependent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var relay = new LoopbackRelay();
        using var first = Listen();
        using var second = Listen();
        var firstPort = Port(first);
        var secondPort = Port(second);
        first.Stop();
        second.Stop();
        await using var mapped = await MappedConnection.StartAsync(relay,
            [Manual(3140, firstPort), Manual(3180, secondPort)], timeout.Token);

        await Task.WhenAll(Enumerable.Range(0, 24).Select(async index =>
        {
            byte[] request = [(byte)index, 42, 101];
            var response = await RoundTripAsync(mapped.Ports[index % 2].LocalPort, request, timeout.Token);
            Assert.Equal(request, response);
        }));

        Assert.Contains(3140, relay.RemotePorts);
        Assert.Contains(3180, relay.RemotePorts);
        await EventuallyAsync(() => mapped.ActiveClientCount == 0, timeout.Token);
    }

    [Fact]
    public async Task ClientLimitRefusesExcessClientsWithoutOpeningExtraRelayStreams()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var relay = new LoopbackRelay();
        await using var mapped = await MappedConnection.StartAsync(relay, [Manual(3140, FreePort())],
            new MappedConnectionOptions { MaxConcurrentClients = 1 }, timeout.Token);
        using var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, mapped.Ports[0].LocalPort, timeout.Token);
        await EventuallyAsync(() => relay.RemotePorts.Count == 1, timeout.Token);
        using var second = new TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, mapped.Ports[0].LocalPort, timeout.Token);

        Assert.Equal(0, await second.GetStream().ReadAsync(new byte[1], timeout.Token));
        Assert.Single(relay.RemotePorts);
        Assert.Equal(1, mapped.ActiveClientCount);
    }

    [Fact]
    public async Task TwoTunnelsCanShareRemotePortAndStoppingOnePreservesTheOther()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var firstRelay = new LoopbackRelay();
        await using var secondRelay = new LoopbackRelay();
        await using var first = await MappedConnection.StartAsync(firstRelay, [Manual(3140, FreePort())], timeout.Token);
        await using var second = await MappedConnection.StartAsync(secondRelay, [Manual(3140, FreePort())], timeout.Token);

        Assert.NotEqual(first.Ports[0].LocalPort, second.Ports[0].LocalPort);
        Assert.Equal(new byte[] { 1, 2 }, await RoundTripAsync(first.Ports[0].LocalPort, [1, 2], timeout.Token));
        await first.DisposeAsync();
        Assert.Equal(new byte[] { 3, 4 }, await RoundTripAsync(second.Ports[0].LocalPort, [3, 4], timeout.Token));
        Assert.False(firstRelay.Disposed);
        Assert.False(secondRelay.Disposed);
    }

    [Fact]
    public async Task Ipv6LoopbackIsSupportedWithoutWildcardExposure()
    {
        if (!Socket.OSSupportsIPv6) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var relay = new LoopbackRelay();
        using var reservation = new TcpListener(IPAddress.IPv6Loopback, 0);
        reservation.Start();
        var port = Port(reservation);
        reservation.Stop();
        await using var mapped = await MappedConnection.StartAsync(relay,
            [Manual(3140, port) with { LocalAddress = "::1" }], timeout.Token);
        using var client = new TcpClient(AddressFamily.InterNetworkV6);
        await client.ConnectAsync(IPAddress.IPv6Loopback, port, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 42 }, timeout.Token);
        client.Client.Shutdown(SocketShutdown.Send);
        using var response = new MemoryStream();
        await stream.CopyToAsync(response, timeout.Token);

        Assert.Equal("::1", mapped.Ports[0].LocalAddress);
        Assert.Equal(new byte[] { 42 }, response.ToArray());
    }

    [Fact]
    public async Task CancellationReleasesPortAndActiveClientsWithoutDisposingRelay()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var lifetime = new CancellationTokenSource();
        await using var relay = new LoopbackRelay();
        var port = FreePort();
        await using var mapped = await MappedConnection.StartAsync(relay, [Manual(3140, port)], lifetime.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await EventuallyAsync(() => mapped.ActiveClientCount == 1, timeout.Token);

        await lifetime.CancelAsync();
        await mapped.DisposeAsync();
        await mapped.DisposeAsync();

        using var rebound = Listen(port);
        Assert.True(rebound.Server.IsBound);
        Assert.False(relay.Disposed);
        Assert.Equal(0, mapped.ActiveClientCount);
    }

    [Fact]
    public async Task CancellationDuringRelayOpenCompletesDisposal()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var relay = new WaitingRelay();
        var mapped = await MappedConnection.StartAsync(relay, [Manual(3140, FreePort())]);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, mapped.Ports[0].LocalPort, timeout.Token);
        await relay.Started.Task.WaitAsync(timeout.Token);

        await mapped.DisposeAsync().AsTask().WaitAsync(timeout.Token);

        Assert.Equal(0, mapped.ActiveClientCount);
    }

    [Fact]
    public async Task ConcurrentExternalCancellationAndDisposeNeverHoldTheCleanupLock()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var relay = new LoopbackRelay();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            using var lifetime = new CancellationTokenSource();
            var mapped = await MappedConnection.StartAsync(relay, [Manual(3140, FreePort())], lifetime.Token);
            var cancelling = lifetime.CancelAsync();
            var disposing = mapped.DisposeAsync().AsTask();
            await Task.WhenAll(cancelling, disposing).WaitAsync(timeout.Token);
            Assert.Equal(0, mapped.ActiveClientCount);
        }
    }

    [Fact]
    public async Task OpenTimeoutReleasesClientSlot()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var relay = new WaitingRelay();
        await using var mapped = await MappedConnection.StartAsync(relay, [Manual(3140, FreePort())],
            new MappedConnectionOptions { OpenStreamTimeout = TimeSpan.FromMilliseconds(100) });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, mapped.Ports[0].LocalPort, timeout.Token);
        await relay.Started.Task.WaitAsync(timeout.Token);

        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], timeout.Token));
        await EventuallyAsync(() => mapped.ActiveClientCount == 0, timeout.Token);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.0.2.1")]
    public async Task PublicBindingsAreRejected(string address)
    {
        await using var relay = new LoopbackRelay();
        var error = await Assert.ThrowsAsync<ToolkitException>(() =>
            MappedConnection.StartAsync(relay, [Manual(3140, FreePort()) with { LocalAddress = address }]));
        Assert.Equal("LOOPBACK_REQUIRED", error.Code);
    }

    [Fact]
    public async Task AlreadyCancelledStartDoesNotBind()
    {
        await using var relay = new LoopbackRelay();
        var port = FreePort();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MappedConnection.StartAsync(relay, [Manual(3140, port)], cancelled.Token));
        using var rebound = Listen(port);
        Assert.True(rebound.Server.IsBound);
    }

    private static PortMapping Manual(int remote, int local) => new() { RemotePort = remote, LocalPort = local, Mode = MappingMode.Manual };

    private static TcpListener Listen(int port = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        return listener;
    }

    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    private static int FreePort()
    {
        using var listener = Listen();
        return Port(listener);
    }

    private static async Task<byte[]> RoundTripAsync(int localPort, byte[] payload, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, localPort, cancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync(payload, cancellationToken);
        client.Client.Shutdown(SocketShutdown.Send);
        using var response = new MemoryStream();
        await stream.CopyToAsync(response, cancellationToken);
        return response.ToArray();
    }

    private static async Task EventuallyAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate()) await Task.Delay(10, cancellationToken);
    }

    /// <summary>A real loopback server returns its response only after receiving request EOF.</summary>
    private sealed class LoopbackRelay : IRelayConnection
    {
        private readonly TcpListener listener = Listen();
        private readonly CancellationTokenSource lifetime = new();
        private readonly ConcurrentBag<Task> clients = [];
        private readonly Task accept;
        public ConcurrentBag<int> RemotePorts { get; } = [];
        public bool Disposed { get; private set; }
        public event Action<ConnectionState>? StateChanged { add { } remove { } }

        public LoopbackRelay() => accept = AcceptAsync();

        public async Task<Stream> OpenStreamAsync(int remotePort, CancellationToken cancellationToken)
        {
            RemotePorts.Add(remotePort);
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, Port(listener), cancellationToken);
                return new HalfCloseNetworkStream(client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public Task RefreshPortsAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private async Task AcceptAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    clients.Add(EchoAfterEofAsync(client));
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (SocketException) when (lifetime.IsCancellationRequested) { }
        }

        private async Task EchoAfterEofAsync(TcpClient client)
        {
            using (client)
            using (var request = new MemoryStream())
            {
                try
                {
                    await client.GetStream().CopyToAsync(request, lifetime.Token);
                    await Task.Delay(25, lifetime.Token);
                    await client.GetStream().WriteAsync(request.ToArray(), lifetime.Token);
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            await lifetime.CancelAsync();
            listener.Stop();
            await accept;
            await Task.WhenAll(clients);
            lifetime.Dispose();
        }
    }

    private sealed class WaitingRelay : IRelayConnection
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<ConnectionState>? StateChanged { add { } remove { } }
        public async Task<Stream> OpenStreamAsync(int remotePort, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The operation must be cancelled.");
        }
        public Task RefreshPortsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HalfCloseNetworkStream(TcpClient client) : Stream, IHalfCloseStream
    {
        private readonly NetworkStream stream = client.GetStream();
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => stream.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => stream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => stream.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => stream.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => stream.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public Task CompleteWritesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            client.Client.Shutdown(SocketShutdown.Send);
            return Task.CompletedTask;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) client.Dispose();
            base.Dispose(disposing);
        }
    }
}
