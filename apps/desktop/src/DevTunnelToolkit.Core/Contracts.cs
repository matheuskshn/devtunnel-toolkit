namespace DevTunnelToolkit.Core;

public interface ICredentialVault
{
    Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default);
    Task WriteAsync(string key, string value, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public interface IAuthenticationService
{
    Task<AccountProfile> LoginAsync(AccountProfile account, Action<LoginChallenge> onChallenge, CancellationToken cancellationToken);
    Task<AccountProfile> ValidateAsync(AccountProfile account, CancellationToken cancellationToken);
    Task LogoutAsync(AccountProfile account, CancellationToken cancellationToken);
    Task<string> GetAuthorizationAsync(AccountProfile account, CancellationToken cancellationToken);
}

/// <summary>Non-secret access-token expiry observed during authentication or acquisition, without network I/O.</summary>
public interface IAuthenticationMetadataProvider
{
    DateTimeOffset? GetObservedExpiration(string accountId);
}

public sealed record DesktopLoginRequest(AccountProvider Provider, CliLoginMode Mode);

/// <summary>The UI runs a fixed interactive login; the agent independently verifies the CLI identity.</summary>
public interface IDesktopAuthenticationService
{
    Task<AccountProfile> LoginFromDesktopAsync(AccountProfile account, Func<DesktopLoginRequest, CancellationToken, Task> authenticate, CancellationToken cancellationToken);
}

public interface ITunnelService
{
    Task<DiscoveredTunnel[]> DiscoverAsync(AccountProfile account, CancellationToken cancellationToken);
    Task<DiscoveredTunnel> GetAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken);
    Task<IRelayConnection> ConnectAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken);
}

public interface IRelayConnection : IAsyncDisposable
{
    event Action<ConnectionState>? StateChanged;
    Task<Stream> OpenStreamAsync(int remotePort, CancellationToken cancellationToken);
    Task RefreshPortsAsync(CancellationToken cancellationToken);
}

public interface IHalfCloseStream
{
    Task CompleteWritesAsync(CancellationToken cancellationToken = default);
}

public interface IUserStartupManager
{
    bool Supported { get; }
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

public interface IStateStore : IAsyncDisposable
{
    Task<ToolkitState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ToolkitState state, CancellationToken cancellationToken = default);
    Task AppendLogAsync(LogEntry entry, CancellationToken cancellationToken = default);
    Task<LogEntry[]> ReadLogsAsync(int limit, CancellationToken cancellationToken = default);
    Task PruneLogsAsync(int retentionDays, CancellationToken cancellationToken = default);
}
