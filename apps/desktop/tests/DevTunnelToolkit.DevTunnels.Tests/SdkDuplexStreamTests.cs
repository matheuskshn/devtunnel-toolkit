using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Xunit;

namespace DevTunnelToolkit.DevTunnels.Tests;

public sealed class SdkDuplexStreamTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSdkStreamsPreserveResponseAfterClientEof(bool encryptedStream)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var cancellation = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var tcpClient = new TcpClient();
        var connecting = tcpClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellation).AsTask();
        using var tcpServer = await listener.AcceptTcpClientAsync(cancellation);
        await connecting;
        listener.Stop();
        using var key = SshAlgorithms.PublicKey.ECDsaSha2Nistp384.GenerateKeyPair();
        var trace = new TraceSource("LocalSdkTest", SourceLevels.Off);
        trace.Listeners.Clear();
        try
        {
            if (encryptedStream)
            {
                using var server = new SecureStream(tcpServer.GetStream(), new SshServerCredentials(key), trace: trace);
                using var client = new SecureStream(tcpClient.GetStream(), new SshClientCredentials("sample-user"), trace: trace);
                server.Authenticating += (_, args) => args.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
                client.Authenticating += (_, args) => args.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
                await Task.WhenAll(server.ConnectAsync(cancellation), client.ConnectAsync(cancellation));
                await ExchangeAfterEofAsync(client, server, cancellation);
            }
            else
            {
                using var server = new SshServerSession(new SshSessionConfiguration(), trace: trace);
                using var client = new SshClientSession(new SshSessionConfiguration(), trace);
                server.Credentials = new SshServerCredentials(key);
                server.Authenticating += (_, args) => args.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
                client.Authenticating += (_, args) => args.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
                await Task.WhenAll(server.ConnectAsync(tcpServer.GetStream(), cancellation), client.ConnectAsync(tcpClient.GetStream(), cancellation));
                Assert.True(await client.AuthenticateAsync(new SshClientCredentials("sample-user"), cancellation));
                var acceptingChannel = server.AcceptChannelAsync(cancellation: cancellation);
                using var clientChannel = await client.OpenChannelAsync(cancellation);
                using var serverChannel = await acceptingChannel;
                using var clientStream = new SshStream(clientChannel);
                using var serverStream = new SshStream(serverChannel);
                await ExchangeAfterEofAsync(clientStream, serverStream, cancellation);
            }
        }
        finally { trace.Close(); }
    }

    private static async Task ExchangeAfterEofAsync(Stream client, Stream server, CancellationToken cancellationToken)
    {
        await using var duplex = new SdkDuplexStream(client);
        var response = new byte[512 * 1024];
        Random.Shared.NextBytes(response);
        var serving = Task.Run(async () =>
        {
            using var request = new MemoryStream();
            await server.CopyToAsync(request, cancellationToken);
            Assert.Equal(new byte[] { 1, 2, 3 }, request.ToArray());
            await server.WriteAsync(response, 0, response.Length, cancellationToken);
            await server.WriteAsync(Array.Empty<byte>(), 0, 0, cancellationToken);
        }, cancellationToken);
        await duplex.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
        await duplex.CompleteWritesAsync(cancellationToken);
        using var received = new MemoryStream();
        await duplex.CopyToAsync(received, cancellationToken);
        await serving;
        Assert.Equal(response, received.ToArray());
    }

    [Fact]
    public async Task CompletingWritesSendsOneSshEofAndPreservesReads()
    {
        using var inner = new EofStream();
        await using var stream = new SdkDuplexStream(inner);
        await stream.CompleteWritesAsync();
        await stream.CompleteWritesAsync();
        Assert.Equal(1, inner.EofCount);
        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.Equal(42, stream.ReadByte());
        await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync(new byte[] { 1 }, 0, 1));
    }

    private sealed class EofStream : MemoryStream
    {
        public int EofCount { get; private set; }
        public EofStream() : base([42]) { }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (count == 0) { EofCount++; return Task.CompletedTask; }
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }
}
