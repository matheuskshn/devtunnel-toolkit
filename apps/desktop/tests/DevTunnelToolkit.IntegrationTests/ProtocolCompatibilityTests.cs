using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Ipc;
using DevTunnelToolkit.Platform;
using Xunit;

namespace DevTunnelToolkit.IntegrationTests;

public sealed class ProtocolCompatibilityTests
{
    [Theory]
    [InlineData("account.save", 1)]
    [InlineData("watch", 1)]
    [InlineData("agent.shutdown", 2)]
    [InlineData("settings.save", 2)]
    [InlineData("settings.save", 3)]
    [InlineData("account.save", 3)]
    [InlineData("account.login.desktop", 4)]
    [InlineData("watch", 4)]
    public async Task LegacyRequestsAreRejectedBeforeDispatchOrSnapshot(string operation, int version)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dtk-v2-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var paths = new LocalPaths(directory);
        var dispatched = false;
        var snapshotRead = false;
        var server = new ToolkitServer(paths, (_, _, _) =>
        {
            dispatched = true;
            throw new InvalidOperationException("An incompatible request must not be dispatched");
        }, () =>
        {
            snapshotRead = true;
            throw new InvalidOperationException("An incompatible watcher must not receive state");
        }).RunAsync(timeout.Token);
        try
        {
            await using var stream = await ConnectAsync(paths, timeout.Token);
            var payload = JsonSerializer.SerializeToElement(new { provider = "UnknownProvider" });
            await WireProtocol.WriteAsync(stream, new AgentRequest(version, operation, payload), timeout.Token);
            var reply = await WireProtocol.ReadAsync<AgentReply>(stream, timeout.Token);
            Assert.False(reply.Success);
            Assert.Equal("IPC_VERSION_MISMATCH", reply.ErrorCode);
            Assert.False(dispatched);
            Assert.False(snapshotRead);
        }
        finally
        {
            await timeout.CancelAsync();
            await server;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DesktopLoginUsesANewWireVersion() => Assert.Equal(5, WireProtocol.Version);

    private static async Task<Stream> ConnectAsync(LocalPaths paths, CancellationToken cancellation)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", paths.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(cancellation); return pipe; }
            catch { await pipe.DisposeAsync(); throw; }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(paths.SocketPath), cancellation);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
