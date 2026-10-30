namespace DevTunnelToolkit.Core;

public sealed record RemoteTunnelTarget(string TunnelId, string ClusterId)
{
    public string CanonicalId => $"{TunnelId}.{ClusterId}";
}

public sealed record RemoteTunnelSpec
{
    public string? TunnelId { get; init; }
    public string? ClusterId { get; init; }
    public string? Name { get; init; }
    public string Description { get; init; } = "";
    public string[] Labels { get; init; } = [];
    public int? ExpirationSeconds { get; init; }
    public RemoteWebOptions? WebOptions { get; init; }
}

public sealed record ManagedTunnel(RemoteTunnelTarget Target, string? Name, string? Description,
    string[] Labels, DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt, int? ExpirationSeconds,
    bool? HostOnline, ManagedPort[] Ports, RemoteAccessEntry[] Access, RemoteWebOptions? WebOptions = null);

public sealed record RemotePortSpec(int PortNumber, string Protocol = "auto", string Description = "", string[]? Labels = null, RemoteWebOptions? WebOptions = null);
public sealed record ManagedPort(int PortNumber, string? Protocol, string? Description, string[] Labels, RemoteAccessEntry[] Access, RemoteWebOptions? WebOptions = null);

public sealed record RemoteWebOptions(string? HostHeader = null, bool? PreserveHostHeader = null,
    string? OriginHeader = null, bool? PreserveOriginHeader = null, int? RequestTimeoutSeconds = null);

// EntryId is a digest of the complete server entry, not a server-side numeric index.
// A stale or inherited entry cannot be removed accidentally after a refresh.
public sealed record RemoteAccessEntry
{
    public string EntryId { get; init; } = "";
    public string Type { get; init; } = "Users";
    public string? Provider { get; init; }
    public string? Organization { get; init; }
    public string[] Subjects { get; init; } = [];
    public string[] Scopes { get; init; } = ["connect"];
    public bool IsDeny { get; init; }
    public bool IsInverse { get; init; }
    public bool IsInherited { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public int? InsertIndex { get; init; }
}

public sealed record RemoteTokenRequest(string[] Scopes, int? PortNumber = null);
// Secret: return only to the requesting local UI. Never persist in snapshots, state or logs.
public sealed record RemoteTokenResult(string Token, string[] Scopes, DateTimeOffset? ExpiresAt, string ExpirationSource)
{
    public override string ToString() => $"RemoteTokenResult {{ Token = [redacted], ExpiresAt = {ExpiresAt:O}, ExpirationSource = {ExpirationSource} }}";
}
public sealed record RemoteCluster(string ClusterId, string? Location, string? ServiceUri);
public sealed record RemoteDiagnostic(string Check, bool Success, long ElapsedMilliseconds, string? ErrorCode = null);

public interface IRemoteTunnelManagementService
{
    Task<ManagedTunnel[]> ListAsync(AccountProfile account, CancellationToken cancellationToken);
    Task<ManagedTunnel> GetAsync(AccountProfile account, RemoteTunnelTarget target, CancellationToken cancellationToken);
    Task<ManagedTunnel> CreateAsync(AccountProfile account, RemoteTunnelSpec spec, CancellationToken cancellationToken);
    Task<ManagedTunnel> UpdateAsync(AccountProfile account, RemoteTunnelTarget target, RemoteTunnelSpec spec, CancellationToken cancellationToken);
    Task DeleteAsync(AccountProfile account, RemoteTunnelTarget target, string confirmation, CancellationToken cancellationToken);
    Task<ManagedPort> SavePortAsync(AccountProfile account, RemoteTunnelTarget target, RemotePortSpec port, bool create, CancellationToken cancellationToken);
    Task DeletePortAsync(AccountProfile account, RemoteTunnelTarget target, int portNumber, string confirmation, CancellationToken cancellationToken);
    Task<RemoteAccessEntry[]> AddAccessAsync(AccountProfile account, RemoteTunnelTarget target, int? portNumber, RemoteAccessEntry entry, CancellationToken cancellationToken);
    Task<RemoteAccessEntry[]> RemoveAccessAsync(AccountProfile account, RemoteTunnelTarget target, int? portNumber, string entryId, string confirmation, CancellationToken cancellationToken);
    Task<RemoteAccessEntry[]> ResetAccessAsync(AccountProfile account, RemoteTunnelTarget target, int? portNumber, string confirmation, CancellationToken cancellationToken);
    Task<RemoteTokenResult> IssueTokenAsync(AccountProfile account, RemoteTunnelTarget target, RemoteTokenRequest request, CancellationToken cancellationToken);
    Task<RemoteCluster[]> ListClustersAsync(AccountProfile account, CancellationToken cancellationToken);
    Task<RemoteDiagnostic> DiagnoseAsync(AccountProfile account, RemoteTunnelTarget target, CancellationToken cancellationToken);
}
