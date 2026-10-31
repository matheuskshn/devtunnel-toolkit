using System.Net;
using System.Net.Sockets;
using DevTunnelToolkit.Core;
using Xunit;

namespace DevTunnelToolkit.Networking.Tests;

public sealed class DataPathFailureTests
{
    [Fact]
    public async Task RepeatedChannelFailuresSignalRecoveryInsteadOfSilentlyKeepingConnectedStatus()
    {
        var relay = new FailingRelay();
        await using var mapping = await MappedConnection.StartAsync(relay, [new PortMapping { RemotePort = 3140 }]);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mapping.DataPathFailed += () => failed.TrySetResult();
        var port = Assert.Single(mapping.Ports).LocalPort;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], timeout.Token));
            if (attempt < 2) Assert.False(failed.Task.IsCompleted);
        }
        await failed.Task.WaitAsync(timeout.Token);
        Assert.Equal(3, relay.Attempts);
    }

    private sealed class FailingRelay : IRelayConnection
    {
        public int Attempts { get; private set; }
        public event Action<ConnectionState>? StateChanged { add { } remove { } }
        public Task<Stream> OpenStreamAsync(int remotePort, CancellationToken cancellationToken)
        { Attempts++; return Task.FromException<Stream>(new IOException("synthetic channel failure")); }
        public Task RefreshPortsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
