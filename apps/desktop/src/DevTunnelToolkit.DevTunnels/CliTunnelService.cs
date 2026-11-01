using System.Text.Json;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.DevTunnels;

/// <summary>The CLI obtains a tunnel-scoped token; the official SDK keeps relay and port mapping.</summary>
public sealed class CliTunnelService : ITunnelService
{
    private readonly ITunnelService native;
    private readonly CliAuthenticationService cli;
    private readonly Func<string, ITunnelService> scopedService;

    public CliTunnelService(ITunnelService native, CliAuthenticationService cli)
        : this(native, cli, token => new DevTunnelService(new ScopedTokenAuthentication(token))) { }

    internal CliTunnelService(ITunnelService native, CliAuthenticationService cli, Func<string, ITunnelService> scopedService)
    {
        this.native = native;
        this.cli = cli;
        this.scopedService = scopedService;
    }

    private static bool IsCli(AccountProfile account) => account.Provider is AccountProvider.MicrosoftCli or AccountProvider.GitHubCli;

    public async Task<DiscoveredTunnel[]> DiscoverAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        if (!IsCli(account)) return await native.DiscoverAsync(account, cancellationToken);
        using var json = OfficialCliJson.Parse(await cli.RunAsAsync(account, ["list", "--limit", "100", "--json"], cancellationToken));
        var keys = ParseTunnelKeys(json.RootElement);
        var results = new List<DiscoveredTunnel>(keys.Length);
        foreach (var (tunnelId, clusterId) in keys)
        {
            var profile = new TunnelProfile { AccountId = account.Id, TunnelId = tunnelId, ClusterId = clusterId };
            results.Add(await GetAsync(account, profile, cancellationToken));
        }
        return [.. results];
    }

    public async Task<DiscoveredTunnel> GetAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken)
    {
        if (!IsCli(account)) return await native.GetAsync(account, profile, cancellationToken);
        var token = await IssueConnectTokenAsync(account, profile, cancellationToken);
        return await scopedService(token).GetAsync(TokenAccount(profile), profile, cancellationToken);
    }

    public async Task<IRelayConnection> ConnectAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken)
    {
        if (!IsCli(account)) return await native.ConnectAsync(account, profile, cancellationToken);
        var token = await IssueConnectTokenAsync(account, profile, cancellationToken);
        return await scopedService(token).ConnectAsync(TokenAccount(profile), profile, cancellationToken);
    }

    private async Task<string> IssueConnectTokenAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken)
    {
        Validation.Tunnel(profile);
        if (profile.AccountId != account.Id) throw new ToolkitException("TUNNEL_ACCOUNT_MISMATCH");
        using var json = OfficialCliJson.Parse(await cli.RunAsAsync(account, ["token", profile.CanonicalId, "--scopes", "connect", "--json"], cancellationToken));
        var token = OfficialCliJson.String(json.RootElement, "token") ?? OfficialCliJson.String(json.RootElement, "accessToken");
        if (token is null || token.Length is < 20 or > 32768 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new ToolkitException("CLI_TOKEN_SCHEMA_UNSUPPORTED");
        return token;
    }

    private static AccountProfile TokenAccount(TunnelProfile profile) => new() { Id = profile.Id, Provider = AccountProvider.TunnelToken };

    internal static (string TunnelId, string ClusterId)[] ParseTunnelKeys(JsonElement value)
    {
        var list = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("tunnels", out var tunnels) ? tunnels : value;
        if (list.ValueKind != JsonValueKind.Array) throw new ToolkitException("CLI_TUNNEL_LIST_SCHEMA_UNSUPPORTED");
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, string)>();
        foreach (var entry in list.EnumerateArray())
        {
            var tunnel = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("tunnel", out var nested) ? nested : entry;
            var id = OfficialCliJson.String(tunnel, "tunnelId") ?? OfficialCliJson.String(tunnel, "id");
            var cluster = OfficialCliJson.String(tunnel, "clusterId");
            if (id is null) throw new ToolkitException("CLI_TUNNEL_LIST_SCHEMA_UNSUPPORTED");
            var parts = id.Split('.');
            if (parts.Length == 2 && (cluster is null || cluster == parts[1])) { id = parts[0]; cluster = parts[1]; }
            if (cluster is null) throw new ToolkitException("CLI_TUNNEL_LIST_SCHEMA_UNSUPPORTED");
            var profile = new TunnelProfile { AccountId = new string('0', 32), TunnelId = id, ClusterId = cluster };
            try { Validation.Tunnel(profile); }
            catch (ToolkitException) { throw new ToolkitException("CLI_TUNNEL_LIST_SCHEMA_UNSUPPORTED"); }
            if (found.Add(profile.CanonicalId)) result.Add((id, cluster));
        }
        if (result.Count > 100) throw new ToolkitException("CLI_TUNNEL_LIST_SCHEMA_UNSUPPORTED");
        return [.. result];
    }

    private sealed class ScopedTokenAuthentication(string token) : IAuthenticationService
    {
        public Task<string> GetAuthorizationAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult("tunnel " + token);
        public Task<AccountProfile> LoginAsync(AccountProfile account, Action<LoginChallenge> onChallenge, CancellationToken cancellationToken) => throw new ToolkitException("AUTH_PROVIDER_UNSUPPORTED");
        public Task<AccountProfile> ValidateAsync(AccountProfile account, CancellationToken cancellationToken) => throw new ToolkitException("AUTH_PROVIDER_UNSUPPORTED");
        public Task LogoutAsync(AccountProfile account, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
