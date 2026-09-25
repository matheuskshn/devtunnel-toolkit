using System.Net;
using System.Text.Json.Serialization;

namespace DevTunnelToolkit.Core;

[JsonConverter(typeof(JsonStringEnumConverter<RouteAction>))]
public enum RouteAction { Direct, Tunnel, Block }
[JsonConverter(typeof(JsonStringEnumConverter<DestinationKind>))]
public enum DestinationKind { Domain, Ip, Cidr }

public sealed record ProxyRoute
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; init; } = true;
    public string Name { get; init; } = "";
    public DestinationKind Kind { get; init; }
    public string Destination { get; init; } = "";
    public bool IncludeSubdomains { get; init; } = true;
    public int DestinationPort { get; init; }
    public RouteAction Action { get; init; } = RouteAction.Tunnel;
    public string ProfileId { get; init; } = "";
    public int RemotePort { get; init; } = 3140;
    public ProxyProtocol Protocol { get; init; }
    public bool DirectFallback { get; init; }
}

public sealed record LocalProxySettings
{
    public bool Enabled { get; init; }
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 8888;
    public int ConnectTimeoutSeconds { get; init; } = 10;
    public int IdleTimeoutSeconds { get; init; } = 120;
    public int MaxClients { get; init; } = 128;
    public RouteAction DefaultAction { get; init; } = RouteAction.Direct;
    public ProxyRoute[] Rules { get; init; } = [];
}

public sealed record RouteDecision(string Host, int Port, RouteAction Action, string? RuleId,
    string? ProfileId = null, EffectiveMapping? Upstream = null, ProxyProtocol Protocol = ProxyProtocol.Http,
    bool DirectFallback = false, string? Reason = null);

public sealed record ProxyProbeResult(RouteDecision Route, bool Succeeded, string Code, int? HttpStatus, long ElapsedMilliseconds, bool ThroughLocalProxy = true);

public static class ProxyRouting
{
    public static string NormalizeHost(string host)
    {
        host = host.TrimEnd('.').Trim('[', ']');
        if (IPAddress.TryParse(host, out var address)) return address.MapToIPv6().IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
        if (host.Length is < 1 or > 253 || host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.')) ||
            host.Split('.').Any(label => label.Length is < 1 or > 63 || label.StartsWith('-') || label.EndsWith('-')))
            throw new ToolkitException("INVALID_PROXY_DESTINATION");
        return host.ToLowerInvariant();
    }

    public static RouteDecision Decide(AgentSnapshot state, string host, int port)
    {
        host = NormalizeHost(host);
        if (port is < 1 or > 65535) throw new ToolkitException("INVALID_PORT");
        // Reserve service ports to prevent aliases/rebinding from recursively reaching them.
        if (port == state.Settings.Proxy.Port || state.Settings.Pac.Enabled && port == state.Settings.Pac.Port)
            return new(host, port, RouteAction.Block, null, Reason: "LOCAL_PROXY_LOOP");
        // First matching rule wins. Never resolve a hostname merely to classify it:
        // doing so would leak tunnel-only DNS queries to the local network.
        foreach (var rule in state.Settings.Proxy.Rules.Where(r => r.Enabled))
        {
            if (rule.DestinationPort != 0 && rule.DestinationPort != port || !Matches(rule, host)) continue;
            if (rule.Action != RouteAction.Tunnel) return new(host, port, rule.Action, rule.Id);
            var mapping = state.Connections.Where(c => c.ProfileId == rule.ProfileId && c.State == ConnectionState.Connected)
                .SelectMany(c => c.Ports ?? []).FirstOrDefault(p => p.RemotePort == rule.RemotePort);
            if (mapping is not null && (!IPAddress.TryParse(mapping.LocalAddress, out var local) || !IPAddress.IsLoopback(local) ||
                mapping.LocalPort == state.Settings.Proxy.Port || state.Settings.Pac.Enabled && mapping.LocalPort == state.Settings.Pac.Port))
                return new(host, port, RouteAction.Block, rule.Id, rule.ProfileId, Reason: "LOCAL_PROXY_LOOP");
            return new(host, port, mapping is null ? rule.DirectFallback ? RouteAction.Direct : RouteAction.Block : RouteAction.Tunnel,
                rule.Id, rule.ProfileId, mapping, rule.Protocol, rule.DirectFallback,
                mapping is null ? rule.DirectFallback ? "TUNNEL_UNAVAILABLE_DIRECT" : "TUNNEL_UNAVAILABLE" : null);
        }
        return new(host, port, state.Settings.Proxy.DefaultAction, null);
    }

    public static string[] Destinations(string value)
    {
        if (value is null || value.Length > 32768) throw new ToolkitException("INVALID_PROXY_DESTINATIONS");
        var items = value.Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (items.Length is < 1 or > 100) throw new ToolkitException("INVALID_PROXY_DESTINATIONS");
        return items;
    }

    public static bool Matches(ProxyRoute rule, string host) => Destinations(rule.Destination).Any(destination => MatchesDestination(rule, destination, host));

    private static bool MatchesDestination(ProxyRoute rule, string destination, string host) => rule.Kind switch
    {
        DestinationKind.Domain => host.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            rule.IncludeSubdomains && host.EndsWith("." + destination, StringComparison.OrdinalIgnoreCase),
        DestinationKind.Ip => IPAddress.TryParse(host, out var address) && address.Equals(IPAddress.Parse(destination)),
        DestinationKind.Cidr => IPAddress.TryParse(host, out var address) && IPNetwork.Parse(destination).Contains(address),
        _ => false
    };
}
