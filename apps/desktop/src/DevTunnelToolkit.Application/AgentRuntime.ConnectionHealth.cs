using DevTunnelToolkit.Core;
using DevTunnelToolkit.Networking;

namespace DevTunnelToolkit.Application;

public sealed partial class AgentRuntime
{
    private static string ConnectionFailureStage(Exception error, string fallback) =>
        error.Data["CliStage"] is string stage && stage is "identity" or "scoped_token" or "list" or "command" or "discovery" ? stage : fallback;

    private static bool IsTransientMetadataFailure(Exception error) => error switch
    {
        ToolkitException e => e.Code is "DEVTUNNEL_CLI_FAILED" or "DEVTUNNEL_CLI_TIMEOUT" or "CONNECTION_FAILED" or
            "CLI_DNS_FAILED" or "CLI_NETWORK_FAILED" or "CLI_SERVICE_UNAVAILABLE" or "CLI_RATE_LIMITED",
        OperationCanceledException or TimeoutException or IOException or System.Net.Http.HttpRequestException { StatusCode: null or >= System.Net.HttpStatusCode.InternalServerError } => true,
        _ => false
    };

    private async Task RefreshConnectionMetadataAsync(AccountProfile account, TunnelProfile profile, PortMapping[] requested,
        IRelayConnection relay, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var stage = "port_metadata";
        try
        {
            await relay.RefreshPortsAsync(deadline.Token);
            stage = "tunnel_metadata";
            var remote = await tunnels.GetAsync(account, profile, deadline.Token);
            if (!requested.Select(p => p.RemotePort).Order().SequenceEqual(ResolveMappings(profile, remote).Select(p => p.RemotePort).Order()))
                throw new ToolkitException("REMOTE_PORTS_CHANGED");
            await UpdateAuthenticationObservationAsync(account.Id, false, deadline.Token);
            if (connections.TryGetValue(profile.Id, out var live))
                SetStatus(live with { TunnelExpiresAt = remote.ExpiresAt, MetadataErrorCode = null, MetadataVerifiedAt = DateTimeOffset.UtcNow });
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && IsTransientMetadataFailure(error))
        {
            var code = error is OperationCanceledException or TimeoutException ? "TUNNEL_METADATA_TIMEOUT" : ErrorCode(error);
            if (connections.TryGetValue(profile.Id, out var live)) SetStatus(live with { MetadataErrorCode = code });
            Log("Warning", "TUNNEL_METADATA_STALE", $"Metadata refresh failed during {ConnectionFailureStage(error, stage)} ({code}); relay and local listeners preserved.", profile.Id);
        }
    }

    private (int RemotePort, ProxyProtocol Protocol)[] ProxyHealthTargets(TunnelProfile profile)
    {
        var snapshot = Snapshot();
        return snapshot.Settings.Proxy.Rules.Where(r => snapshot.Settings.Proxy.Enabled && r.Enabled && r.Action == RouteAction.Tunnel && r.ProfileId == profile.Id)
            .Select(r => (r.RemotePort, r.Protocol))
            .Concat(snapshot.Settings.Pac.Rules.Where(r => snapshot.Settings.Pac.Enabled && snapshot.Settings.Pac.Mode == PacMode.Rules && r.ProfileId == profile.Id).Select(r => (r.RemotePort, r.Protocol)))
            .Where(target => snapshot.Connections.Any(c => c.ProfileId == profile.Id && c.Ports?.Any(p => p.RemotePort == target.RemotePort) == true))
            .Distinct().ToArray();
    }

    private async Task<string?> CheckProxyChannelsAsync(TunnelProfile profile, MappedConnection mapping, CancellationToken cancellationToken)
    {
        var targets = ProxyHealthTargets(profile);
        if (mapping.AtCapacity) return "TUNNEL_CLIENT_LIMIT_REACHED";
        foreach (var (port, protocol) in targets)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try { await TunnelProxyHealth.CheckAsync(mapping.Ports.Single(p => p.RemotePort == port), protocol, deadline.Token); }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is IOException or OperationCanceledException or ToolkitException or System.Net.Sockets.SocketException)
            {
                return error is ToolkitException { Code: "TUNNEL_PROXY_PROTOCOL_INVALID" } ? "TUNNEL_PROXY_PROTOCOL_INVALID" : "TUNNEL_PROXY_UNRESPONSIVE";
            }
        }
        if (targets.Length > 0 && connections.TryGetValue(profile.Id, out var live))
            SetStatus(live with { ProxyVerifiedAt = DateTimeOffset.UtcNow });
        return null;
    }
}
