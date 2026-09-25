using System.Text.Json.Serialization;

namespace DevTunnelToolkit.Core;

[JsonConverter(typeof(JsonStringEnumConverter<AccountProvider>))]
public enum AccountProvider { Microsoft, GitHub, TunnelToken, MicrosoftCli, GitHubCli }
[JsonConverter(typeof(JsonStringEnumConverter<MappingMode>))]
public enum MappingMode { SamePort, Automatic, Manual, ReservedRange, Disabled }
[JsonConverter(typeof(JsonStringEnumConverter<ConnectionState>))]
public enum ConnectionState { Stopped, Authenticating, Discovering, Connecting, Connected, Reconnecting, AuthenticationRequired, Degraded, Failed, Stopping }

[JsonConverter(typeof(JsonStringEnumConverter<CliLoginMode>))]
public enum CliLoginMode { DeviceCode, Browser, WindowsIntegrated }

public sealed record AccountProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public CliLoginMode CliLoginMode { get; init; } = CliLoginMode.DeviceCode;
    public AccountProvider Provider { get; init; }
    public string DisplayName { get; init; } = "";
    public string? StableIdentityId { get; init; }
    public string? Username { get; init; }
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? Scope { get; init; }
    public DateTimeOffset? AuthenticatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? LastValidatedAt { get; init; }
    public string Status { get; init; } = "AuthenticationRequired";
}

public sealed record PortMapping
{
    public bool AutoDiscovered { get; init; }
    public int RemotePort { get; init; }
    public string LocalAddress { get; init; } = "127.0.0.1";
    public int LocalPort { get; init; }
    public int? LastEffectivePort { get; init; }
    public MappingMode Mode { get; init; } = MappingMode.Automatic;
    public int RangeStart { get; init; } = 20000;
    public int RangeEnd { get; init; } = 21000;
    public bool Enabled { get; init; } = true;
}

public sealed record ReconnectPolicy
{
    public bool Enabled { get; init; } = true;
    public int InitialDelaySeconds { get; init; } = 2;
    public int MaxDelaySeconds { get; init; } = 60;
    public int MaxRetries { get; init; } = 10;
}

public sealed record TunnelProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string AccountId { get; init; } = "";
    public string TunnelId { get; init; } = "";
    public string ClusterId { get; init; } = "";
    public string Alias { get; init; } = "";
    public bool AutoConnect { get; init; }
    public bool AutoDiscoverPorts { get; init; } = true;
    public ReconnectPolicy Reconnect { get; init; } = new();
    public PortMapping[] Mappings { get; init; } = [];
    public string CanonicalId => $"{TunnelId}.{ClusterId}";
}

public sealed record RemotePort(int PortNumber, string? Protocol = null, string? Description = null);
public sealed record DiscoveredTunnel(string TunnelId, string ClusterId, string? Name, RemotePort[] Ports, DateTimeOffset? ExpiresAt = null, bool? HostOnline = null)
{
    public string CanonicalId => $"{TunnelId}.{ClusterId}";
}
public sealed record EffectiveMapping(int RemotePort, string LocalAddress, int LocalPort);
public sealed record ConnectionStatus(string ProfileId, ConnectionState State, DateTimeOffset? ConnectedAt = null, int RetryCount = 0, DateTimeOffset? NextRetryAt = null, string? ErrorCode = null, EffectiveMapping[]? Ports = null, DateTimeOffset? TunnelExpiresAt = null,
    DateTimeOffset? OperationStartedAt = null, DateTimeOffset? OperationCompletedAt = null, string? Phase = null);
public sealed record LoginChallenge(string VerificationUri, string UserCode, DateTimeOffset? ExpiresAt, string? Message = null);
public sealed record LoginStatus(string AccountId, string State, LoginChallenge? Challenge = null, string? ErrorCode = null,
    CliLoginMode? RequestedMode = null, DateTimeOffset? StartedAt = null);
public sealed record AppSettings
{
    public PacSettings Pac { get; init; } = new();
    public LocalProxySettings Proxy { get; init; } = new();
    public bool CloseToTray { get; init; } = true;
    public string Theme { get; init; } = "System";
    public int LogRetentionDays { get; init; } = 7;
    public int MaxConnections { get; init; } = 20;
    public bool StartWithUserSession { get; init; }
    public int DiscoveryIntervalSeconds { get; init; } = 60;
}
[JsonConverter(typeof(JsonStringEnumConverter<ProxyProtocol>))]
public enum ProxyProtocol { Http, Socks5 }
public sealed record PacRule
{
    public string Domain { get; init; } = "";
    public bool IncludeSubdomains { get; init; } = true;
    public string ProfileId { get; init; } = "";
    public int RemotePort { get; init; }
    public ProxyProtocol Protocol { get; init; }
    public bool DirectFallback { get; init; }
}
[JsonConverter(typeof(JsonStringEnumConverter<PacMode>))]
public enum PacMode { Rules, LocalProxy, Manual }
public sealed record PacSettings
{
    public bool Enabled { get; init; }
    public int Port { get; init; } = 7777;
    public PacRule[] Rules { get; init; } = [];
    public PacMode Mode { get; init; }
    public string ManualScript { get; init; } = "function FindProxyForURL(url, host) { return \"DIRECT\"; }";
}
public sealed record PlatformCapabilities(string OperatingSystem, bool CredentialVault, bool UserStartup, bool SystemProxy, bool WslExposure, string? Detail = null);
public sealed record LogEntry(long Sequence, DateTimeOffset Timestamp, string Level, string Code, string Message, string? ProfileId = null);
public sealed record AgentSnapshot(AccountProfile[] Accounts, TunnelProfile[] Tunnels, ConnectionStatus[] Connections, LoginStatus[] Logins, AppSettings Settings, PlatformCapabilities Capabilities, LogEntry[] Logs, string? PacErrorCode = null, string? ProxyErrorCode = null, int ProxyActiveConnections = 0);
public sealed record ToolkitState(AccountProfile[] Accounts, TunnelProfile[] Tunnels, AppSettings Settings)
{
    public static ToolkitState Empty => new([], [], new());
}

public sealed class ToolkitException(string code, string? message = null) : Exception(message ?? code)
{
    public string Code { get; } = code;
}
