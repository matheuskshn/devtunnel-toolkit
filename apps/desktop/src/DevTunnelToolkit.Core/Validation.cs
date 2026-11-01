using System.Net;
using System.Text.RegularExpressions;

namespace DevTunnelToolkit.Core;

public static partial class Validation
{
    [GeneratedRegex("^[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex InternalIdPattern();
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,47}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex TunnelPattern();
    [GeneratedRegex("^(([a-z]{3,4}[0-9]{1,3})|asse|aue|brs|euw|use)$", RegexOptions.CultureInvariant)]
    private static partial Regex ClusterPattern();

    public static void Id(string value)
    {
        if (value is null || !InternalIdPattern().IsMatch(value)) throw new ToolkitException("INVALID_ID");
    }

    public static void Account(AccountProfile account)
    {
        Id(account.Id);
        Text(account.DisplayName, 120);
        if (!Enum.IsDefined(account.Provider)) throw new ToolkitException("INVALID_PROVIDER");
        if (!Enum.IsDefined(account.CliLoginMode) ||
            account.Provider is not (AccountProvider.MicrosoftCli or AccountProvider.GitHubCli) && account.CliLoginMode != CliLoginMode.DeviceCode ||
            account.CliLoginMode == CliLoginMode.WindowsIntegrated && account.Provider != AccountProvider.MicrosoftCli)
            throw new ToolkitException("INVALID_CLI_LOGIN_MODE");
        if (account.ClientId is { } client) Text(client, 128);
        if (account.Scope is { } scope) Text(scope, 500);
        if (account.TenantId is { } tenant && tenant != "organizations" && tenant != "common" && tenant != "consumers" && !Guid.TryParse(tenant, out _))
            throw new ToolkitException("INVALID_TENANT");
    }

    public static void Tunnel(TunnelProfile profile)
    {
        Id(profile.Id);
        Id(profile.AccountId);
        if (!TunnelPattern().IsMatch(profile.TunnelId) || !ClusterPattern().IsMatch(profile.ClusterId)) throw new ToolkitException("INVALID_TUNNEL_ID");
        Text(profile.Alias, 120);
        if (profile.Mappings.Length > 100 || profile.Mappings.Select(m => m.RemotePort).Distinct().Count() != profile.Mappings.Length)
            throw new ToolkitException("DUPLICATE_OR_EXCESSIVE_MAPPINGS");
        foreach (var mapping in profile.Mappings) Mapping(mapping);
        if (profile.Reconnect.InitialDelaySeconds is < 1 or > 300 || profile.Reconnect.MaxDelaySeconds < profile.Reconnect.InitialDelaySeconds || profile.Reconnect.MaxDelaySeconds > 3600 || profile.Reconnect.MaxRetries is < 0 or > 1000)
            throw new ToolkitException("INVALID_RECONNECT_POLICY");
    }

    public static void Mapping(PortMapping mapping)
    {
        if (mapping.RemotePort is < 1 or > 65535 || !Enum.IsDefined(mapping.Mode)) throw new ToolkitException("INVALID_PORT");
        if (!IPAddress.TryParse(mapping.LocalAddress, out var address) || !IPAddress.IsLoopback(address)) throw new ToolkitException("LOOPBACK_REQUIRED");
        if (mapping.LocalPort is < 0 or > 65535 || mapping.Mode == MappingMode.Manual && mapping.LocalPort == 0) throw new ToolkitException("INVALID_LOCAL_PORT");
        if (mapping.RangeStart is < 1024 or > 65535 || mapping.RangeEnd < mapping.RangeStart || mapping.RangeEnd > 65535 || mapping.RangeEnd - mapping.RangeStart > 10000)
            throw new ToolkitException("INVALID_PORT_RANGE");
        if (mapping.LastEffectivePort is < 1 or > 65535) throw new ToolkitException("INVALID_LOCAL_PORT");
    }

    public static void Settings(AppSettings settings)
    {
        if (settings.Theme is not ("System" or "Light" or "Dark" or "HighContrast") || settings.LogRetentionDays is < 1 or > 90 || settings.MaxConnections is < 1 or > 100 || settings.DiscoveryIntervalSeconds is < 10 or > 3600)
            throw new ToolkitException("INVALID_SETTINGS");
        Pac(settings.Pac);
        Proxy(settings.Proxy);
        if (settings.Proxy.Enabled && settings.Pac.Enabled && settings.Proxy.Port == settings.Pac.Port)
            throw new ToolkitException("LOCAL_SERVICE_PORT_CONFLICT");
        if (settings.Pac.Enabled && settings.Pac.Mode == PacMode.LocalProxy && !settings.Proxy.Enabled)
            throw new ToolkitException("PAC_REQUIRES_PROXY");
    }

    public static void Pac(PacSettings settings)
    {
        if (!Enum.IsDefined(settings.Mode) || settings.ManualScript is null || settings.ManualScript.Length > 65536 ||
            settings.ManualScript.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) ||
            settings.Mode == PacMode.Manual && string.IsNullOrWhiteSpace(settings.ManualScript)) throw new ToolkitException("INVALID_MANUAL_PAC");
        if (settings.Port is < 1024 or > 65535 || settings.Rules.Length > 100) throw new ToolkitException("INVALID_PAC_SETTINGS");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in settings.Rules)
        {
            Id(rule.ProfileId);
            if (rule.RemotePort is < 1 or > 65535 || !Enum.IsDefined(rule.Protocol)) throw new ToolkitException("INVALID_PAC_RULE");
            if (rule.Domain.Length is < 1 or > 253 || rule.Domain != rule.Domain.TrimEnd('.') || !rule.Domain.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ||
                rule.Domain.Split('.').Any(label => label.Length is < 1 or > 63 || label.StartsWith('-') || label.EndsWith('-')) || !seen.Add(rule.Domain))
                throw new ToolkitException("INVALID_PAC_DOMAIN");
        }
    }

    public static void Proxy(LocalProxySettings settings)
    {
        if (!IPAddress.TryParse(settings.ListenAddress, out var listen) || !IPAddress.IsLoopback(listen))
            throw new ToolkitException("LOOPBACK_REQUIRED");
        if (settings.Port is < 1024 or > 65535 || settings.ConnectTimeoutSeconds is < 1 or > 60 ||
            settings.IdleTimeoutSeconds is < 10 or > 3600 || settings.MaxClients is < 1 or > 1024 ||
            settings.DefaultAction is not (RouteAction.Direct or RouteAction.Block) ||
            settings.Rules.Length > 100 || settings.Rules.Select(r => r.Id).Distinct().Count() != settings.Rules.Length)
            throw new ToolkitException("INVALID_PROXY_SETTINGS");
        var destinationCount = 0;
        foreach (var rule in settings.Rules)
        {
            Id(rule.Id); Text(rule.Name, 120);
            if (!Enum.IsDefined(rule.Kind) || !Enum.IsDefined(rule.Action) || !Enum.IsDefined(rule.Protocol) ||
                rule.DestinationPort is < 0 or > 65535 || rule.Destination is null)
                throw new ToolkitException("INVALID_PROXY_RULE");
            var destinations = ProxyRouting.Destinations(rule.Destination);
            if ((destinationCount += destinations.Length) > 1000) throw new ToolkitException("INVALID_PROXY_DESTINATIONS");
            foreach (var destination in destinations)
                switch (rule.Kind)
                {
                    case DestinationKind.Domain:
                        if (ProxyRouting.NormalizeHost(destination) != destination.ToLowerInvariant() ||
                            IPAddress.TryParse(destination, out _)) throw new ToolkitException("INVALID_PROXY_DESTINATION");
                        break;
                    case DestinationKind.Ip:
                        if (!IPAddress.TryParse(destination, out _)) throw new ToolkitException("INVALID_PROXY_DESTINATION");
                        break;
                    case DestinationKind.Cidr:
                        if (!IPNetwork.TryParse(destination, out _)) throw new ToolkitException("INVALID_PROXY_DESTINATION");
                        break;
                }
            if (rule.Action == RouteAction.Tunnel)
            {
                Id(rule.ProfileId);
                if (rule.RemotePort is < 1 or > 65535) throw new ToolkitException("INVALID_PROXY_RULE");
            }
        }
    }

    public static void Text(string value, int max)
    {
        if (value is null || value.Length > max || value.Any(char.IsControl)) throw new ToolkitException("INVALID_TEXT");
    }
}
