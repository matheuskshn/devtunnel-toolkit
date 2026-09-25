using System.Net;
using DevTunnelToolkit.Core;
using Microsoft.DevTunnels.Connections;
using Xunit;
using ConnectionStatus = Microsoft.DevTunnels.Connections.ConnectionStatus;

namespace DevTunnelToolkit.DevTunnels.Tests;

public sealed class ConnectionStatusTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SdkDisconnectPreservesAuthenticationRequirement(int kind)
    {
        Exception error = kind switch
        {
            0 => new UnauthorizedAccessException(),
            1 => new HttpRequestException("synthetic", null, HttpStatusCode.Unauthorized),
            _ => new IOException("synthetic", new ToolkitException("AUTHENTICATION_EXPIRED"))
        };
        var args = new ConnectionStatusChangedEventArgs(ConnectionStatus.Connected, ConnectionStatus.Disconnected, error);
        Assert.Equal(ConnectionState.AuthenticationRequired, DevTunnelService.ClassifyStatus(args));
    }

    [Fact]
    public void TransportFailureRemainsEligibleForReconnect()
    {
        var args = new ConnectionStatusChangedEventArgs(ConnectionStatus.Connected, ConnectionStatus.Disconnected, new IOException("synthetic transport failure"));
        Assert.Equal(ConnectionState.Failed, DevTunnelService.ClassifyStatus(args));
        Assert.Equal(ConnectionState.Connected, DevTunnelService.ClassifyStatus(new(ConnectionStatus.Connecting, ConnectionStatus.Connected, null)));
    }
}
