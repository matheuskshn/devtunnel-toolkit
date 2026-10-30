using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Claims;
using DevTunnelToolkit.Core;
using Microsoft.DevTunnels.Connections;
using Microsoft.DevTunnels.Connections.Messages;
using Microsoft.DevTunnels.Management;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Tcp.Events;
using Xunit;

namespace DevTunnelToolkit.DevTunnels.Tests;

public sealed class HostingTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V2AdapterPreservesSdkStreamAndEncryptedConfirmation(bool encrypted)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var clientSocket = new TcpClient();
        var connecting = clientSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token).AsTask();
        using var serverSocket = await listener.AcceptTcpClientAsync(timeout.Token); await connecting;
        var trace = new TraceSource("SyntheticHostingTransport", SourceLevels.Off); trace.Listeners.Clear();
        await using var management = new TunnelManagementClient(new ProductInfoHeaderValue("SyntheticHostingTest", "1.0"), tunnelServiceUri: null, httpHandler: null);
        await using var host = new MappedTunnelRelayHost(management, trace) { ForwardConnectionsToLocalPorts = false };
        using var server = new SshServerSession(new SshSessionConfiguration(useSecurity: false), trace);
        using var client = new SshClientSession(new SshSessionConfiguration(useSecurity: false), trace);
        var accepted = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ChannelMessage>? openingResult = null;
        var confirmation = new PortRelayConnectResponseMessage { IsE2EEncryptionEnabled = encrypted };
        host.ForwardedPortConnecting += (_, args) =>
        {
            Assert.Equal(3140, args.Port);
            var transformed = args.TransformTask;
            args.TransformTask = CaptureAsync();
            async Task<Stream?> CaptureAsync()
            {
                var stream = transformed is not null ? await transformed : args.Stream;
                Assert.NotNull(stream);
                Assert.Equal(encrypted, stream is SecureStream);
                accepted.TrySetResult(stream); return stream;
            }
        };
        server.ChannelOpening += (_, args) =>
        {
            args.OpeningTask = Task.FromResult<ChannelMessage>(confirmation);
            host.OpenMappedChannel(args); openingResult = args.OpeningTask;
        };
        await Task.WhenAll(server.ConnectAsync(serverSocket.GetStream(), timeout.Token), client.ConnectAsync(clientSocket.GetStream(), timeout.Token));
        using var channel = await client.OpenChannelAsync(new PortRelayConnectRequestMessage
        { ChannelType = "forwarded-tcpip", Host = "127.0.0.1", Port = 3140, IsE2EEncryptionRequested = encrypted }, null, timeout.Token);
        await using var raw = new SshStream(channel);
        await using var remote = await accepted.Task.WaitAsync(timeout.Token);
        Assert.Same(confirmation, await openingResult!);
        if (encrypted)
        {
            await using var secured = new SecureStream(raw, new SshClientCredentials("synthetic-client"), trace: trace);
            secured.Authenticating += (_, args) => args.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
            await secured.ConnectAsync(timeout.Token);
            await ExchangeAsync(secured, remote, timeout.Token);
        }
        else await ExchangeAsync(raw, remote, timeout.Token);
        trace.Close();
    }

    [Fact]
    public async Task V2AdapterRejectsUnmappedPortByClosingChannel()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var clientSocket = new TcpClient();
        var connecting = clientSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token).AsTask();
        using var serverSocket = await listener.AcceptTcpClientAsync(timeout.Token); await connecting;
        var trace = new TraceSource("SyntheticHostingRejection", SourceLevels.Off); trace.Listeners.Clear();
        await using var management = new TunnelManagementClient(new ProductInfoHeaderValue("SyntheticHostingTest", "1.0"), tunnelServiceUri: null, httpHandler: null);
        await using var host = new MappedTunnelRelayHost(management, trace) { ForwardConnectionsToLocalPorts = false };
        using var server = new SshServerSession(new SshSessionConfiguration(useSecurity: false), trace);
        using var client = new SshClientSession(new SshSessionConfiguration(useSecurity: false), trace);
        host.ForwardedPortConnecting += (_, args) => args.TransformTask = Task.FromResult<Stream?>(null);
        server.ChannelOpening += (_, args) => host.OpenMappedChannel(args);
        await Task.WhenAll(server.ConnectAsync(serverSocket.GetStream(), timeout.Token), client.ConnectAsync(clientSocket.GetStream(), timeout.Token));
        await Assert.ThrowsAsync<SshChannelException>(async () => await client.OpenChannelAsync(new PortRelayConnectRequestMessage
        { ChannelType = "forwarded-tcpip", Host = "127.0.0.1", Port = 9999 }, null, timeout.Token));
        Assert.False(timeout.IsCancellationRequested); trace.Close();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MappedHostBridgesToLoopbackAndPreservesResponseAfterClientEof(bool encrypted, bool legacy)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var response = new byte[512 * 1024]; Random.Shared.NextBytes(response);
        var serving = Task.Run(async () =>
        {
            using var connection = await origin.AcceptTcpClientAsync(timeout.Token);
            using var request = new MemoryStream(); await connection.GetStream().CopyToAsync(request, timeout.Token);
            Assert.Equal(new byte[] { 1, 2, 3 }, request.ToArray());
            await connection.GetStream().WriteAsync(response, timeout.Token);
            connection.Client.Shutdown(SocketShutdown.Send);
        });
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var clientSocket = new TcpClient();
        var connecting = clientSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token).AsTask();
        using var serverSocket = await listener.AcceptTcpClientAsync(timeout.Token); await connecting;
        var trace = new TraceSource("SyntheticHostedBridge", SourceLevels.Off); trace.Listeners.Clear();
        var management = new TunnelManagementClient(new ProductInfoHeaderValue("SyntheticHostingTest", "1.0"), tunnelServiceUri: null, httpHandler: null);
        var host = new MappedTunnelRelayHost(management, trace) { ForwardConnectionsToLocalPorts = false };
        var profile = new HostProfile
        {
            AccountId = new string('a', 32),
            TunnelId = "synthetic-host",
            ClusterId = "use2",
            Ports = [new() { Kind = HostPortKind.Service, RemotePort = 3140, LocalPort = originPort }]
        };
        await using var hosted = new TunnelHostingService.HostedTunnel(profile, host, management, trace, [],
            [new(3140, HostPortKind.Service, "127.0.0.1", originPort)], _ => throw new InvalidOperationException("Services do not query remote ACLs during local bridging."));
        using var server = new SshServerSession(new SshSessionConfiguration(useSecurity: false), trace);
        using var client = new SshClientSession(new SshSessionConfiguration(useSecurity: false), trace);
        server.ChannelOpening += (_, args) =>
        {
            args.OpeningTask = Task.FromResult<ChannelMessage>(new PortRelayConnectResponseMessage { IsE2EEncryptionEnabled = encrypted });
            if (legacy)
            {
                // Invoke the actual pinned SDK's V1 notification over this real local SSH channel.
                // Reflection is confined to this compatibility test, never application code.
                var handler = typeof(TunnelRelayTunnelHost).GetMethod("OnSshChannelOpening", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var request = args.Request.ConvertTo<PortForwardChannelOpenMessage>();
                handler.Invoke(host, [server, new Microsoft.DevTunnels.Ssh.Events.SshChannelOpeningEventArgs(request, args.Channel, true)]);
            }
            else host.OpenMappedChannel(args);
        };
        await Task.WhenAll(server.ConnectAsync(serverSocket.GetStream(), timeout.Token), client.ConnectAsync(clientSocket.GetStream(), timeout.Token));
        using var channel = await client.OpenChannelAsync(new PortRelayConnectRequestMessage
        { ChannelType = "forwarded-tcpip", Host = "127.0.0.1", Port = 3140, IsE2EEncryptionRequested = encrypted }, null, timeout.Token);
        await using var raw = new SshStream(channel);
        Stream stream = raw;
        if (encrypted)
        {
            var secure = new SecureStream(raw, new SshClientCredentials("synthetic-client"), trace: trace);
            secure.Authenticating += (_, args) => args.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
            await secure.ConnectAsync(timeout.Token); stream = secure;
        }
        await using var duplex = new SdkDuplexStream(stream);
        await duplex.WriteAsync(new byte[] { 1, 2, 3 }, timeout.Token);
        await duplex.CompleteWritesAsync(timeout.Token);
        using var received = new MemoryStream(); await duplex.CopyToAsync(received, timeout.Token);
        Assert.Equal(response, received.ToArray()); await serving;
    }

    private sealed class EmptyProxy : IPublishedProxy
    {
        public HostedPort[] Ports => [];
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task StoppingHostCancelsAndDrainsPendingAclChecksBeforeDisposingSlots()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var clientSocket = new TcpClient();
        var connecting = clientSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token).AsTask();
        using var serverSocket = await listener.AcceptTcpClientAsync(timeout.Token); await connecting;
        var trace = new TraceSource("SyntheticHostedCancellation", SourceLevels.Off); trace.Listeners.Clear();
        var management = new TunnelManagementClient(new ProductInfoHeaderValue("SyntheticHostingTest", "1.0"), tunnelServiceUri: null, httpHandler: null);
        var host = new MappedTunnelRelayHost(management, trace) { ForwardConnectionsToLocalPorts = false };
        var profile = new HostProfile
        {
            AccountId = new string('a', 32),
            TunnelId = "synthetic-host",
            ClusterId = "use2",
            Ports = [new() { Kind = HostPortKind.HttpProxy, RemotePort = 3140 }]
        };
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        await using var hosted = new TunnelHostingService.HostedTunnel(profile, host, management, trace, [],
            [new(3140, HostPortKind.HttpProxy, "127.0.0.1", 65000)], async ct =>
            {
                reading.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { cancelled = true; throw; }
                throw new InvalidOperationException();
            });
        using var server = new SshServerSession(new SshSessionConfiguration(useSecurity: false), trace);
        using var client = new SshClientSession(new SshSessionConfiguration(useSecurity: false), trace);
        server.ChannelOpening += (_, args) => host.OpenMappedChannel(args);
        await Task.WhenAll(server.ConnectAsync(serverSocket.GetStream(), timeout.Token), client.ConnectAsync(clientSocket.GetStream(), timeout.Token));
        var opening = client.OpenChannelAsync(new PortRelayConnectRequestMessage
        { ChannelType = "forwarded-tcpip", Host = "127.0.0.1", Port = 3140 }, null, timeout.Token);
        await reading.Task.WaitAsync(timeout.Token);
        await hosted.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        Assert.True(cancelled);
        await Assert.ThrowsAsync<SshChannelException>(async () => await opening);
        await hosted.DisposeAsync(); // Idempotent after a start/stop race.
    }

    private static async Task ExchangeAsync(Stream left, Stream right, CancellationToken token)
    {
        await left.WriteAsync(new byte[] { 1, 2, 3, 4 }, token);
        var payload = new byte[4]; await right.ReadExactlyAsync(payload, token);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, payload);
        await right.WriteAsync(new byte[] { 5, 6, 7, 8 }, token);
        await left.ReadExactlyAsync(payload, token); Assert.Equal(new byte[] { 5, 6, 7, 8 }, payload);
    }
}
