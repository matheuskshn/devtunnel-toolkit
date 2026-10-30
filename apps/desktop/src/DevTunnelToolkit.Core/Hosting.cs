using System.Net;
using System.Text.Json.Serialization;

namespace DevTunnelToolkit.Core;

[JsonConverter(typeof(JsonStringEnumConverter<HostPortKind>))]
public enum HostPortKind { Service, HttpProxy, Socks5Proxy }

[JsonConverter(typeof(JsonStringEnumConverter<PublishedProxyAction>))]
public enum PublishedProxyAction { Allow, Deny }

public sealed record ProxyPortRange
{
    public int Start { get; init; }
    public int End { get; init; }
}

public sealed record HostPortMapping
{
    public int RemotePort { get; init; } = 3140;
    public HostPortKind Kind { get; init; } = HostPortKind.HttpProxy;
    public string LocalAddress { get; init; } = "127.0.0.1";
    public int LocalPort { get; init; }
    public string? ServiceId { get; init; }
}

public sealed record PublishedProxyRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; init; } = true;
    public PublishedProxyAction Action { get; init; } = PublishedProxyAction.Allow;
    public DestinationKind Kind { get; init; }
    public string Destination { get; init; } = "";
    public bool IncludeSubdomains { get; init; } = true;
    public int[] Ports { get; init; } = [];
    public ProxyPortRange[] PortRanges { get; init; } = [];
    public bool AllowPrivateAddresses { get; init; }
    public bool AllowLoopback { get; init; }
}

public sealed record PublishedProxyPolicy
{
    public PublishedProxyRule[] Rules { get; init; } = [];
    // Empty port filters preserve the legacy unrestricted-port policy. Destinations
    // still require an explicit allow rule unless the public-only fallback is enabled.
    public int[] AllowedPorts { get; init; } = [];
    public ProxyPortRange[] AllowedPortRanges { get; init; } = [];
    public int[] ConnectPorts { get; init; } = [];
    public ProxyPortRange[] ConnectPortRanges { get; init; } = [];
    public bool AllowAllDestinations { get; init; }
    public int MaxClients { get; init; } = 128;
    public int ConnectTimeoutSeconds { get; init; } = 10;
    public int IdleTimeoutSeconds { get; init; } = 120;
}

public sealed record HostProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string AccountId { get; init; } = "";
    public string TunnelId { get; init; } = "";
    public string ClusterId { get; init; } = "";
    public string Alias { get; init; } = "";
    public bool AutoStart { get; init; }
    public ReconnectPolicy Reconnect { get; init; } = new();
    public HostPortMapping[] Ports { get; init; } = [];
    public PublishedProxyPolicy Proxy { get; init; } = new();
    public string CanonicalId => $"{TunnelId}.{ClusterId}";
}

public sealed record HostedPort(int RemotePort, HostPortKind Kind, string LocalAddress, int LocalPort, string? ServiceId = null);
public sealed record HostStatus(string ProfileId, ConnectionState State, string? LastError = null,
    HostedPort[]? Ports = null, DateTimeOffset? OperationStartedAt = null, DateTimeOffset? OperationCompletedAt = null,
    string? Phase = null, int RetryCount = 0, DateTimeOffset? NextRetryAt = null,
    DateTimeOffset? ExpiresAt = null, DateTimeOffset? HostTokenExpiresAt = null);

public interface IHostingService
{
    Task<IHostedTunnel> StartAsync(AccountProfile account, HostProfile profile, Action<HostStatus> progress, CancellationToken cancellationToken);
}

public interface IHostedTunnel : IAsyncDisposable
{
    event Action<ConnectionState>? StateChanged;
    ConnectionState State => ConnectionState.Connected;
    HostedPort[] Ports { get; }
    DateTimeOffset? ExpiresAt { get; }
    DateTimeOffset? HostTokenExpiresAt { get; }
    Task RefreshPortsAsync(CancellationToken cancellationToken);
}

public interface IPublishedProxyFactory
{
    Task<IPublishedProxy> StartAsync(HostProfile profile, CancellationToken cancellationToken);
}

public interface IPublishedProxy : IAsyncDisposable
{
    HostedPort[] Ports { get; }
}

public static class HostingValidation
{
    public static void Profile(HostProfile profile)
    {
        Validation.Tunnel(new TunnelProfile
        {
            Id = profile.Id,
            AccountId = profile.AccountId,
            TunnelId = profile.TunnelId,
            ClusterId = profile.ClusterId,
            Alias = profile.Alias,
            Reconnect = profile.Reconnect
        });
        if (profile.Ports is null || profile.Ports.Length is < 1 or > 100 ||
            profile.Ports.Select(p => p.RemotePort).Distinct().Count() != profile.Ports.Length)
            throw new ToolkitException("HOST_PORTS_INVALID");
        foreach (var port in profile.Ports)
        {
            if (!Enum.IsDefined(port.Kind) || port.RemotePort is < 1 or > 65535 || port.LocalPort is < 0 or > 65535 ||
                port.Kind == HostPortKind.Service && (port.LocalPort == 0 || port.ServiceId is not null))
                throw new ToolkitException("HOST_PORT_INVALID");
            if (port.ServiceId is not null) Validation.Id(port.ServiceId);
            if (!IPAddress.TryParse(port.LocalAddress, out var address) || !IPAddress.IsLoopback(address))
                throw new ToolkitException("LOOPBACK_REQUIRED");
        }
        Policy(profile.Proxy);
    }

    public static void Policy(PublishedProxyPolicy policy)
    {
        if (policy is null || policy.Rules is null || policy.Rules.Length > 100 || policy.MaxClients is < 1 or > 1024 ||
            policy.ConnectTimeoutSeconds is < 1 or > 60 || policy.IdleTimeoutSeconds is < 10 or > 3600)
            throw new ToolkitException("HOST_PROXY_POLICY_INVALID");
        PortFilter(policy.AllowedPorts, policy.AllowedPortRanges);
        PortFilter(policy.ConnectPorts, policy.ConnectPortRanges);
        var destinations = 0;
        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in policy.Rules)
        {
            if (rule is null) throw new ToolkitException("HOST_PROXY_RULE_INVALID");
            Validation.Id(rule.Id);
            PortFilter(rule.Ports, rule.PortRanges);
            if (!ruleIds.Add(rule.Id) || !Enum.IsDefined(rule.Action) ||
                !Enum.IsDefined(rule.Kind) || rule.AllowLoopback && rule.Kind == DestinationKind.Domain)
                throw new ToolkitException("HOST_PROXY_RULE_INVALID");
            var items = ProxyRouting.Destinations(rule.Destination);
            if ((destinations += items.Length) > 1000) throw new ToolkitException("HOST_PROXY_RULE_INVALID");
            foreach (var destination in items)
            {
                if (rule.Kind == DestinationKind.Domain && (ProxyRouting.NormalizeHost(destination) != destination.ToLowerInvariant() || IPAddress.TryParse(destination, out _)) ||
                    rule.Kind == DestinationKind.Ip && !IPAddress.TryParse(destination, out _) ||
                    rule.Kind == DestinationKind.Cidr && !IPNetwork.TryParse(destination, out _))
                    throw new ToolkitException("HOST_PROXY_RULE_INVALID");
            }
        }
    }

    private static void PortFilter(int[] ports, ProxyPortRange[] ranges)
    {
        if (ports is null || ranges is null || ports.Length > 100 || ranges.Length > 100 ||
            ports.Any(p => p is < 1 or > 65535) ||
            ranges.Any(r => r is null || r.Start is < 1 or > 65535 || r.End < r.Start || r.End > 65535))
            throw new ToolkitException("HOST_PROXY_PORT_FILTER_INVALID");
    }
}
