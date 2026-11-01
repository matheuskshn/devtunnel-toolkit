using System.Net;
using System.Text;
using DevTunnelToolkit.Core;
using Microsoft.DevTunnels.Contracts;
using Xunit;

namespace DevTunnelToolkit.DevTunnels.Tests;

public sealed class HostingTests
{
    private static HostProfile Profile => new()
    {
        AccountId = new string('a', 32),
        TunnelId = "synthetic-host",
        ClusterId = "use2",
        Ports = [new() { Kind = HostPortKind.HttpProxy, RemotePort = 3140 }]
    };
    private static Tunnel Remote => new() { TunnelId = "synthetic-host", ClusterId = "use2", Ports = [new() { PortNumber = 3140 }] };

    [Fact]
    public void HostingNeverCreatesMissingRemotePortOrChangesIdentity()
    {
        TunnelHostingService.ValidateRemote(Profile, Remote);
        var remote = Remote; remote.Ports = [];
        Assert.Equal("HOST_REMOTE_PORT_MISSING", Assert.Throws<ToolkitException>(() => TunnelHostingService.ValidateRemote(Profile, remote)).Code);
        remote = Remote; remote.TunnelId = "other-synthetic";
        Assert.Equal("HOST_REMOTE_ID_MISMATCH", Assert.Throws<ToolkitException>(() => TunnelHostingService.ValidateRemote(Profile, remote)).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicProxyIsRefusedAtTunnelOrPortLevel(bool port)
    {
        var access = new TunnelAccessControl([new() { Type = TunnelAccessControlEntryType.Anonymous, Scopes = [TunnelAccessScopes.Connect] }]);
        var remote = Remote;
        if (port) remote.Ports![0].AccessControl = access; else remote.AccessControl = access;
        Assert.Equal("HOST_PROXY_REQUIRES_PRIVATE_TUNNEL", Assert.Throws<ToolkitException>(() => TunnelHostingService.ValidateRemote(Profile, remote)).Code);
    }

    [Fact]
    public void TokenExpirationIsMetadataOnlyAndOpaqueTokensStayUnknown()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":2000000000}"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2000000000), TunnelHostingService.TokenExpiry("header." + payload + ".signature"));
        Assert.Null(TunnelHostingService.TokenExpiry("opaque-token"));
        Assert.Null(TunnelHostingService.TokenExpiry("a.invalid.c"));
        Assert.Null(TunnelHostingService.TokenExpiry("a." + Convert.ToBase64String("{\"exp\":9223372036854775807}"u8.ToArray()) + ".c"));
    }

    [Theory]
    [InlineData(401, "AUTHENTICATION_REQUIRED")]
    [InlineData(403, "TUNNEL_ACCESS_DENIED")]
    [InlineData(404, "TUNNEL_NOT_FOUND")]
    [InlineData(429, "REMOTE_RATE_LIMITED")]
    [InlineData(503, "HOST_RELAY_UNAVAILABLE")]
    public void HostProviderErrorsAreClassifiedWithoutLeakingBody(int status, string expected)
    {
        var error = TunnelHostingService.SafeError(new InvalidOperationException("synthetic sensitive body",
            new HttpRequestException("synthetic token value", null, (HttpStatusCode)status)));
        Assert.Equal(expected, error.Code);
        Assert.DoesNotContain("synthetic", error.Message);
        Assert.Null(error.InnerException);
    }
}
