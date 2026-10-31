using System.Diagnostics;
using System.Net.Http.Headers;
using DevTunnelToolkit.Core;
using Microsoft.DevTunnels.Connections;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;
using Microsoft.DevTunnels.Ssh;
using SdkConnectionStatus = Microsoft.DevTunnels.Connections.ConnectionStatus;

namespace DevTunnelToolkit.DevTunnels;

public sealed class DevTunnelService(IAuthenticationService authentication) : ITunnelService
{
    internal static ConnectionState ClassifyStatus(ConnectionStatusChangedEventArgs args)
    {
        if (args.Status == SdkConnectionStatus.Connected) return ConnectionState.Connected;
        if (args.Status != SdkConnectionStatus.Disconnected) return ConnectionState.Reconnecting;
        var error = args.DisconnectException;
        for (var depth = 0; error is not null && depth < 8; depth++, error = error.InnerException)
        {
            if (error is UnauthorizedAccessException ||
                error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } ||
                error is ToolkitException { Code: "AUTHENTICATION_REQUIRED" or "AUTHENTICATION_EXPIRED" or "AUTH_CLIENT_ID_REQUIRED" or "ACCESS_TOKEN_EXPIRED" })
                return ConnectionState.AuthenticationRequired;
        }
        return ConnectionState.Failed;
    }

    public async Task<DiscoveredTunnel[]> DiscoverAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        if (account.Provider == AccountProvider.TunnelToken)
            throw new ToolkitException("DISCOVERY_REQUIRES_ACCOUNT", "Tokens de túnel permitem adicionar um Tunnel ID conhecido, mas não listar a conta.");
        await using var management = CreateManagement(account, cancellationToken);
        try
        {
            var tunnels = await management.ListTunnelsAsync(clusterId: null, domain: null, options: new() { IncludePorts = true }, ownedTunnelsOnly: true, cancellation: cancellationToken);
            return tunnels.Select(ToDiscovered).ToArray();
        }
        catch (UnauthorizedAccessException) { throw new ToolkitException("AUTHENTICATION_REQUIRED"); }
    }

    public async Task<DiscoveredTunnel> GetAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken)
    {
        await using var management = CreateManagement(account, cancellationToken);
        return ToDiscovered(await GetTunnelAsync(management, account, profile, cancellationToken));
    }

    public async Task<IRelayConnection> ConnectAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken)
    {
        // A connection keeps its own account-bound management client for token renewal.
        var management = CreateManagement(account, CancellationToken.None);
        var trace = new TraceSource("DevTunnelToolkit.Relay", SourceLevels.Off);
        trace.Listeners.Clear();
        var client = new TunnelRelayTunnelClient(management, trace) { AcceptLocalConnectionsForForwardedPorts = false };
        try
        {
            var tunnel = await GetTunnelAsync(management, account, profile, cancellationToken);
            if (account.Provider == AccountProvider.TunnelToken)
            {
                var authorization = await authentication.GetAuthorizationAsync(account, cancellationToken);
                tunnel.AccessTokens ??= new Dictionary<string, string>();
                tunnel.AccessTokens[TunnelAccessScopes.Connect] = AuthenticationHeaderValue.Parse(authorization).Parameter!;
            }
            await client.ConnectAsync(tunnel, new TunnelConnectionOptions
            {
                EnableRetry = false,
                EnableReconnect = false,
                KeepAliveIntervalInSeconds = 20
            }, cancellation: cancellationToken);
            return new RelayConnection(client, management, trace);
        }
        catch
        {
            await client.DisposeAsync();
            await management.DisposeAsync();
            trace.Close();
            throw;
        }
    }

    private TunnelManagementClient CreateManagement(AccountProfile account, CancellationToken cancellationToken)
    {
        var client = new TunnelManagementClient(new ProductInfoHeaderValue("DevTunnelToolkit", "1.0"), async () =>
            AuthenticationHeaderValue.Parse(await authentication.GetAuthorizationAsync(account, cancellationToken)),
            tunnelServiceUri: null, httpHandler: null);
        // Service-side custom event reporting is optional and disabled for this application.
        client.EnableEventsReporting = false;
        return client;
    }

    private static async Task<Tunnel> GetTunnelAsync(ITunnelManagementClient management, AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.TunnelId) || string.IsNullOrWhiteSpace(profile.ClusterId))
            throw new ToolkitException("TUNNEL_ID_REQUIRED");
        try
        {
            return await management.GetTunnelAsync(new() { TunnelId = profile.TunnelId, ClusterId = profile.ClusterId }, new()
            {
                IncludePorts = true,
                TokenScopes = account.Provider == AccountProvider.TunnelToken ? null : [TunnelAccessScopes.Connect]
            }, cancellationToken) ?? throw new ToolkitException("TUNNEL_NOT_FOUND");
        }
        catch (UnauthorizedAccessException) { throw new ToolkitException("TUNNEL_ACCESS_DENIED"); }
    }

    private static DiscoveredTunnel ToDiscovered(Tunnel tunnel) => new(
        tunnel.TunnelId ?? "", tunnel.ClusterId ?? "", tunnel.Name,
        tunnel.Ports?.Select(port => new RemotePort(port.PortNumber, port.Protocol, port.Description)).ToArray() ?? [],
        tunnel.Expiration is { } expiry ? new DateTimeOffset(DateTime.SpecifyKind(expiry, DateTimeKind.Utc)) : null,
        tunnel.Status?.HostConnectionCount is { } hosts ? hosts.Current > 0 : null);

    private sealed class RelayConnection : IRelayConnection
    {
        private readonly TunnelRelayTunnelClient client;
        private readonly TunnelManagementClient management;
        private readonly TraceSource trace;
        public ConnectionState State => client.ConnectionStatus switch
        {
            SdkConnectionStatus.Connected => ConnectionState.Connected,
            SdkConnectionStatus.Disconnected => ConnectionState.Failed,
            _ => ConnectionState.Reconnecting
        };
        public event Action<ConnectionState>? StateChanged;

        public RelayConnection(TunnelRelayTunnelClient client, TunnelManagementClient management, TraceSource trace)
        {
            this.client = client;
            this.management = management;
            this.trace = trace;
            client.ConnectionStatusChanged += OnStatusChanged;
        }

        private void OnStatusChanged(object? sender, ConnectionStatusChangedEventArgs args) => StateChanged?.Invoke(ClassifyStatus(args));

        public async Task<Stream> OpenStreamAsync(int remotePort, CancellationToken cancellationToken)
        {
            if (remotePort is < 1 or > 65535) throw new ToolkitException("PORT_INVALID");
            await client.WaitForForwardedPortAsync(remotePort, cancellationToken);
            var stream = await client.ConnectToForwardedPortAsync(remotePort, cancellationToken) ?? throw new ToolkitException("REMOTE_PORT_UNAVAILABLE");
            return stream is SshStream or SecureStream ? new SdkDuplexStream(stream) : stream;
        }

        public Task RefreshPortsAsync(CancellationToken cancellationToken) => client.RefreshPortsAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            client.ConnectionStatusChanged -= OnStatusChanged;
            await client.DisposeAsync();
            await management.DisposeAsync();
            trace.Close();
        }
    }
}
