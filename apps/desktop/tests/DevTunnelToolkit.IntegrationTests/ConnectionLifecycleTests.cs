using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DevTunnelToolkit.Application;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Persistence;
using DevTunnelToolkit.Platform;
using Xunit;

namespace DevTunnelToolkit.IntegrationTests;

public sealed class ConnectionLifecycleTests
{
    [Fact]
    public async Task UnavailableStartupStatusDoesNotPreventTunnelManagement()
    {
        var service = new ControlledTunnelService();
        await using var fixture = await LifecycleFixture.CreateAsync(service, startup: new UnavailableStartup());
        Assert.Contains(fixture.Runtime.Snapshot().Logs, l => l.Code == "STARTUP_STATUS_UNAVAILABLE");
        var profile = await fixture.SaveTunnelAsync(await fixture.SaveAccountAsync(AccountProvider.GitHub));
        await fixture.CommandAsync("tunnel.connect", new { id = profile.Id });
        var connected = await WaitForAsync(fixture, profile.Id, c => c.State == ConnectionState.Connected);
        await AssertEchoAsync(Assert.Single(connected.Ports!).LocalPort);
    }

    [Fact]
    public async Task CompletingDeviceLoginPreservesAConcurrentProfileRename()
    {
        var authentication = new DeferredAuthentication();
        await using var fixture = await LifecycleFixture.CreateAsync(new ControlledTunnelService(), authentication);
        var account = await fixture.SaveAccountAsync(AccountProvider.GitHub);
        await fixture.CommandAsync("account.login", new { id = account.Id });
        await authentication.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.CommandAsync("account.save", account with { DisplayName = "Renamed during login" });
        authentication.Complete.TrySetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (fixture.Runtime.Snapshot().Logins.Single(l => l.AccountId == account.Id).State != "Completed")
            await Task.Delay(10, timeout.Token);

        var updated = Assert.Single(fixture.Runtime.Snapshot().Accounts);
        Assert.Equal("Renamed during login", updated.DisplayName);
        Assert.Equal("fixture-user", updated.Username);
        Assert.Equal("github:fixture-user", updated.StableIdentityId);
        Assert.Equal("Authenticated", updated.Status);
        Assert.NotNull(updated.AuthenticatedAt);
        Assert.Equal("Renamed during login", Assert.Single((await fixture.ReadPersistedAsync()).Accounts).DisplayName);
    }

    [Fact]
    public async Task StartupOverTheConnectionLimitPreservesTheAgentAndOtherProfiles()
    {
        var service = new ControlledTunnelService();
        await using var fixture = await LifecycleFixture.CreateAsync(service);
        var account = await fixture.SaveAccountAsync(AccountProvider.GitHub);
        await fixture.CommandAsync("settings.save", new AppSettings { MaxConnections = 1 });
        var first = await fixture.SaveTunnelAsync(account, autoConnect: true, tunnelName: "lifecycle-first");
        var excess = await fixture.SaveTunnelAsync(account, autoConnect: true, tunnelName: "lifecycle-second");

        await fixture.RestartAsync();
        var connected = await WaitForAsync(fixture, first.Id, c => c.State == ConnectionState.Connected);
        var rejected = await WaitForAsync(fixture, excess.Id, c => c.State == ConnectionState.Failed);
        Assert.Equal("CONNECTION_LIMIT_REACHED", rejected.ErrorCode);
        Assert.Equal(0, service.Attempts(excess.Id));
        Assert.Equal(2, fixture.Runtime.Snapshot().Tunnels.Length);
        await AssertEchoAsync(Assert.Single(connected.Ports!).LocalPort);
    }

    [Fact]
    public async Task AutoDiscoveryCannotPersistMoreThanTheMappingLimit()
    {
        var service = new ControlledTunnelService();
        await using var fixture = await LifecycleFixture.CreateAsync(service);
        var account = await fixture.SaveAccountAsync(AccountProvider.GitHub);
        var disabled = Enumerable.Range(40001, 100).Select(port => new PortMapping { RemotePort = port, Enabled = false, Mode = MappingMode.Disabled }).ToArray();
        var profile = await fixture.SaveTunnelAsync(account, mappings: disabled);
        await fixture.CommandAsync("tunnel.connect", new { id = profile.Id });
        var rejected = await WaitForAsync(fixture, profile.Id, c => c.ErrorCode == "DUPLICATE_OR_EXCESSIVE_MAPPINGS");
        Assert.NotEqual(ConnectionState.Connected, rejected.State);
        await fixture.CommandAsync("tunnel.stop", new { id = profile.Id });

        var stored = Assert.Single((await fixture.ReadPersistedAsync()).Tunnels);
        Assert.Equal(100, stored.Mappings.Length);
        Assert.DoesNotContain(stored.Mappings, m => m.RemotePort == 3140);
        Validation.Tunnel(stored);
        Assert.Equal(100, Assert.Single(fixture.Runtime.Snapshot().Tunnels).Mappings.Length);
    }

    [Fact]
    public async Task TransientFailuresUseBoundedBackoffAndEventuallyReconnect()
    {
        var service = new ControlledTunnelService { ConnectFailure = (_, attempt) => attempt <= 2 ? new IOException("simulated transport interruption") : null };
        await using var fixture = await LifecycleFixture.CreateAsync(service);
        var account = await fixture.SaveAccountAsync(AccountProvider.GitHub);
        var profile = await fixture.SaveTunnelAsync(account);
        await fixture.CommandAsync("tunnel.connect", new { id = profile.Id });

        var first = await WaitForAsync(fixture, profile.Id, c => c.State == ConnectionState.Reconnecting && c.RetryCount == 1);
        Assert.Equal("CONNECTION_FAILED", first.ErrorCode);
        Assert.NotNull(first.NextRetryAt);
        Assert.NotNull(first.OperationStartedAt);
        Assert.Null(first.OperationCompletedAt);
        var firstFailure = fixture.Runtime.Snapshot().Logs.Last(l => l.ProfileId == profile.Id && l.Code == "CONNECTION_FAILED" && l.Timestamp <= first.NextRetryAt);
        Assert.InRange((first.NextRetryAt!.Value - firstFailure.Timestamp).TotalSeconds, 0.79, 1.4);
        var second = await WaitForAsync(fixture, profile.Id, c => c.State == ConnectionState.Reconnecting && c.RetryCount == 2);
        Assert.NotNull(second.NextRetryAt);
        Assert.True(second.NextRetryAt > first.NextRetryAt);
        var secondFailure = fixture.Runtime.Snapshot().Logs.Last(l => l.ProfileId == profile.Id && l.Code == "CONNECTION_FAILED" && l.Timestamp <= second.NextRetryAt);
        Assert.InRange((second.NextRetryAt!.Value - secondFailure.Timestamp).TotalSeconds, 1.59, 2.6);
        var connected = await WaitForAsync(fixture, profile.Id, c => c.State == ConnectionState.Connected);
        Assert.Equal(2, connected.RetryCount);
        Assert.Equal(3, service.Attempts(profile.Id));
        Assert.Null(connected.NextRetryAt);
        Assert.Equal(first.OperationStartedAt, second.OperationStartedAt);
        Assert.Equal(first.OperationStartedAt, connected.OperationStartedAt);
        Assert.NotNull(connected.OperationCompletedAt);
        Assert.True(connected.OperationCompletedAt >= connected.OperationStartedAt);
        await AssertEchoAsync(Assert.Single(connected.Ports!).LocalPort);
    }

    [Fact]
    public async Task RelayTransportDisconnectionReconnectsAndReusesItsLocalPort()
    {
        using var remotePort = new PortReservation();
        var localPort = AvailablePort();
        var service = new ControlledTunnelService { RemotePort = remotePort.Port };
        await using var fixture = await LifecycleFixture.CreateAsync(service);
        var profile = await fixture.SaveTunnelAsync(await fixture.SaveAccountAsync(AccountProvider.GitHub), mappings: [new() { RemotePort = remotePort.Port, Mode = MappingMode.Automatic, RangeStart = localPort, RangeEnd = localPort }]);
        await fixture.CommandAsync("tunnel.connect", new { id = profile.Id });
        var initial = await WaitForAsync(fixture, profile.Id, c => c.State == ConnectionState.Connected);
        var previous = service.Latest(profile.Id);
        previous.Emit(ConnectionState.Stopped);

        var retry = await WaitForAsync(fixture, profile.Id, c => c.State == ConnectionState.Reconnecting);
        Assert.Equal("RELAY_DISCONNECTED", retry.ErrorCode);
        Assert.Equal(1, retry.RetryCount);
        var restored = await WaitForAsync(fixture, profile.Id, c => c.State == ConnectionState.Connected);
        Assert.Equal(2, service.Attempts(profile.Id));
        Assert.NotSame(previous, service.Latest(profile.Id));
        Assert.True(previous.Disposed);
        Assert.Equal(Assert.Single(initial.Ports!).LocalPort, Assert.Single(restored.Ports!).LocalPort);
        await AssertEchoAsync(restored.Ports![0].LocalPort);
    }

    [Fact]
    public async Task ExpiredIdentityDuringDiscoveryIsTerminalWithoutRetry()
    {
        var service = new ControlledTunnelService { DiscoveryFailure = _ => new ToolkitException("AUTHENTICATION_EXPIRED") };
        await using var fixture = await LifecycleFixture.CreateAsync(service);
        var profile = await fixture.SaveTunnelAsync(await fixture.SaveAccountAsync(AccountProvider.Microsoft));
        await fixture.CommandAsync("tunnel.connect", new { id = profile.Id });

        var failure = await WaitForAsync(fixture, profile.Id, c => c.State == ConnectionState.AuthenticationRequired);
        Assert.Equal("AUTHENTICATION_EXPIRED", failure.ErrorCode);
        Assert.Equal(0, failure.RetryCount);
        Assert.Null(failure.NextRetryAt);
        await Task.Delay(1350);
        Assert.Equal(1, service.Discoveries(profile.Id));
        Assert.Equal(0, service.Attempts(profile.Id));
        Assert.Equal(ConnectionState.AuthenticationRequired, fixture.Status(profile.Id).State);
    }

    [Fact]
    public async Task AuthenticationRequiredFromAnActiveRelayIsTerminalWithoutRetry()
    {
        var service = new ControlledTunnelService();
        await using var fixture = await LifecycleFixture.CreateAsync(service);
        var profile = await fixture.SaveTunnelAsync(await fixture.SaveAccountAsync(AccountProvider.Microsoft));
        await fixture.CommandAsync("tunnel.connect", new { id = profile.Id });
        await WaitForAsync(fixture, profile.Id, c => c.State == ConnectionState.Connected);
        service.Latest(profile.Id).Emit(ConnectionState.AuthenticationRequired);

        var interrupted = await WaitForAsync(fixture, profile.Id, c => c.State is ConnectionState.AuthenticationRequired or ConnectionState.Reconnecting or ConnectionState.Failed);
        Assert.Equal(ConnectionState.AuthenticationRequired, interrupted.State);
        Assert.Equal(0, interrupted.RetryCount);
        Assert.Null(interrupted.NextRetryAt);
        await Task.Delay(1350);
        Assert.Equal(1, service.Attempts(profile.Id));
    }

    [Fact]
    public async Task ManualPortConflictIsTerminalAndDoesNotDisturbTheExistingListener()
    {
        using var occupied = new PortReservation();
        var service = new ControlledTunnelService();
        await using var fixture = await LifecycleFixture.CreateAsync(service);
        var account = await fixture.SaveAccountAsync(AccountProvider.GitHub);
        var profile = await fixture.SaveTunnelAsync(account, mappings: [new() { RemotePort = 3140, LocalPort = occupied.Port, Mode = MappingMode.Manual }]);
        await fixture.CommandAsync("tunnel.connect", new { id = profile.Id });

        var failure = await WaitForAsync(fixture, profile.Id, c => c.State == ConnectionState.Failed);
        Assert.Equal("LOCAL_PORT_UNAVAILABLE", failure.ErrorCode);
        Assert.Equal(0, failure.RetryCount);
        Assert.Null(failure.NextRetryAt);
        Assert.True(service.Latest(profile.Id).Disposed);
        await Task.Delay(1350);
        Assert.Equal(1, service.Attempts(profile.Id));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, occupied.Port);
        Assert.True(client.Connected);
        Assert.Null(Assert.Single(fixture.Runtime.Snapshot().Tunnels).Mappings[0].LastEffectivePort);
    }

    [Fact]
    public async Task FailureOfOneAccountDoesNotInterruptTheOtherAccountTraffic()
    {
        var service = new ControlledTunnelService { DiscoveryFailure = account => account.Provider == AccountProvider.Microsoft ? new ToolkitException("AUTHENTICATION_EXPIRED") : null };
        await using var fixture = await LifecycleFixture.CreateAsync(service);
        var failed = await fixture.SaveTunnelAsync(await fixture.SaveAccountAsync(AccountProvider.Microsoft));
        var healthy = await fixture.SaveTunnelAsync(await fixture.SaveAccountAsync(AccountProvider.GitHub));
        await fixture.CommandAsync("tunnel.connect", new { id = healthy.Id });
        var connected = await WaitForAsync(fixture, healthy.Id, c => c.State == ConnectionState.Connected);
        await fixture.CommandAsync("tunnel.connect", new { id = failed.Id });
        await WaitForAsync(fixture, failed.Id, c => c.State == ConnectionState.AuthenticationRequired);

        await AssertEchoAsync(Assert.Single(connected.Ports!).LocalPort);
        Assert.Equal(ConnectionState.Connected, fixture.Status(healthy.Id).State);
        Assert.False(service.Latest(healthy.Id).Disposed);
        Assert.Equal(1, service.Attempts(healthy.Id));
        Assert.Equal(2, fixture.Runtime.Snapshot().Accounts.Length);
        await fixture.CommandAsync("tunnel.stop", new { id = failed.Id });
        await AssertEchoAsync(connected.Ports![0].LocalPort);
    }

    [Fact]
    public async Task AgentRestartReloadsProfilesAndTheirPreviouslyEffectivePortFromSqlite()
    {
        using var remotePort = new PortReservation();
        var service = new ControlledTunnelService { RemotePort = remotePort.Port };
        var localPort = AvailablePort();
        await using var fixture = await LifecycleFixture.CreateAsync(service);
        var account = await fixture.SaveAccountAsync(AccountProvider.GitHub);
        var profile = await fixture.SaveTunnelAsync(account, autoConnect: true, mappings: [new() { RemotePort = remotePort.Port, Mode = MappingMode.Automatic, RangeStart = localPort, RangeEnd = localPort }]);
        await fixture.CommandAsync("tunnel.connect", new { id = profile.Id });
        var before = await WaitForAsync(fixture, profile.Id, c => c.State == ConnectionState.Connected);
        var port = Assert.Single(before.Ports!).LocalPort;
        Assert.NotEqual(remotePort.Port, port);
        var saved = Assert.Single(fixture.Runtime.Snapshot().Tunnels);
        Assert.Equal(port, Assert.Single(saved.Mappings).LastEffectivePort);

        await fixture.RestartAsync();
        var after = await WaitForAsync(fixture, profile.Id, c => c.State == ConnectionState.Connected);
        var restored = Assert.Single(fixture.Runtime.Snapshot().Tunnels);
        Assert.Equal(saved.Id, restored.Id);
        Assert.Equal(saved.AccountId, restored.AccountId);
        Assert.Equal(saved.CanonicalId, restored.CanonicalId);
        Assert.True(restored.AutoConnect);
        Assert.Equal(port, Assert.Single(restored.Mappings).LastEffectivePort);
        Assert.Equal(port, Assert.Single(after.Ports!).LocalPort);
        Assert.Equal(account.Id, Assert.Single(fixture.Runtime.Snapshot().Accounts).Id);
        Assert.Equal(2, service.Attempts(profile.Id));
        await AssertEchoAsync(port);
    }

    private static async Task<ConnectionStatus> WaitForAsync(LifecycleFixture fixture, string id, Func<ConnectionStatus, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var current = fixture.Status(id);
            if (predicate(current)) return current;
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task AssertEchoAsync(int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var sent = Encoding.UTF8.GetBytes("lifecycle-echo");
        await client.GetStream().WriteAsync(sent, timeout.Token);
        var received = new byte[sent.Length];
        await client.GetStream().ReadExactlyAsync(received, timeout.Token);
        Assert.Equal(sent, received);
    }

    private static int AvailablePort()
    {
        using var reservation = new PortReservation();
        return reservation.Port;
    }

    private sealed class LifecycleFixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "dtk-lifecycle-" + Guid.NewGuid().ToString("N"));
        private readonly ControlledTunnelService service;
        private readonly IAuthenticationService authentication;
        private readonly IUserStartupManager? startup;
        private SqliteStateStore store = null!;
        public AgentRuntime Runtime { get; private set; } = null!;

        private LifecycleFixture(ControlledTunnelService service, IAuthenticationService? authentication, IUserStartupManager? startup)
        {
            this.service = service;
            this.authentication = authentication ?? new LocalAuthentication();
            this.startup = startup;
        }

        public static async Task<LifecycleFixture> CreateAsync(ControlledTunnelService service, IAuthenticationService? authentication = null, IUserStartupManager? startup = null)
        {
            var fixture = new LifecycleFixture(service, authentication, startup);
            await fixture.StartAsync();
            return fixture;
        }

        private async Task StartAsync()
        {
            store = new(new LocalPaths(directory));
            Runtime = new(store, new EmptyVault(), authentication, service, new("Test", true, startup?.Supported == true, false, false), startup);
            await Runtime.InitializeAsync();
        }

        public async Task RestartAsync()
        {
            await Runtime.DisposeAsync();
            await store.DisposeAsync();
            await StartAsync();
        }

        public Task<object> CommandAsync(string operation, object payload) => Runtime.DispatchAsync(operation, JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), default);
        public ConnectionStatus Status(string id) => Runtime.Snapshot().Connections.Single(c => c.ProfileId == id);
        public async Task<AccountProfile> SaveAccountAsync(AccountProvider provider) => (AccountProfile)await CommandAsync("account.save", new AccountProfile { DisplayName = "Lifecycle fixture", Provider = provider });
        public Task<ToolkitState> ReadPersistedAsync() => store.LoadAsync();
        public async Task<TunnelProfile> SaveTunnelAsync(AccountProfile account, bool autoConnect = false, PortMapping[]? mappings = null, string? tunnelName = null) =>
            (TunnelProfile)await CommandAsync("tunnel.save", new TunnelProfile
            {
                AccountId = account.Id,
                TunnelId = tunnelName ?? "lifecycle-fixture",
                ClusterId = "usw2",
                AutoConnect = autoConnect,
                Mappings = mappings ?? [],
                Reconnect = new() { Enabled = true, InitialDelaySeconds = 1, MaxDelaySeconds = 2, MaxRetries = 4 }
            });

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await store.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    private sealed class ControlledTunnelService : ITunnelService
    {
        private readonly ConcurrentDictionary<string, int> attempts = new();
        private readonly ConcurrentDictionary<string, int> discoveries = new();
        private readonly ConcurrentDictionary<string, ControlledRelay> relays = new();
        public int RemotePort { get; init; } = 3140;
        public Func<AccountProfile, Exception?>? DiscoveryFailure { get; init; }
        public Func<TunnelProfile, int, Exception?>? ConnectFailure { get; init; }
        public int Attempts(string id) => attempts.GetValueOrDefault(id);
        public int Discoveries(string id) => discoveries.GetValueOrDefault(id);
        public ControlledRelay Latest(string id) => relays[id];
        public Task<DiscoveredTunnel[]> DiscoverAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult<DiscoveredTunnel[]>([]);
        public Task<DiscoveredTunnel> GetAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken)
        {
            discoveries.AddOrUpdate(profile.Id, 1, (_, count) => count + 1);
            if (DiscoveryFailure?.Invoke(account) is { } failure) throw failure;
            return Task.FromResult(new DiscoveredTunnel(profile.TunnelId, profile.ClusterId, "Lifecycle fixture", [new(RemotePort)]));
        }
        public Task<IRelayConnection> ConnectAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken)
        {
            var count = attempts.AddOrUpdate(profile.Id, 1, (_, previous) => previous + 1);
            if (ConnectFailure?.Invoke(profile, count) is { } failure) throw failure;
            var relay = new ControlledRelay();
            relays[profile.Id] = relay;
            return Task.FromResult<IRelayConnection>(relay);
        }
    }

    private sealed class ControlledRelay : IRelayConnection
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly ConcurrentBag<Task> clients = [];
        public event Action<ConnectionState>? StateChanged;
        public bool Disposed { get; private set; }
        public void Emit(ConnectionState state) => StateChanged?.Invoke(state);
        public async Task<Stream> OpenStreamAsync(int remotePort, CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var peer = new TcpClient();
            try
            {
                var opening = peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint, cancellationToken);
                var accepted = await listener.AcceptTcpClientAsync(cancellationToken);
                await opening;
                clients.Add(EchoAsync(accepted, lifetime.Token));
                return peer.GetStream();
            }
            catch { peer.Dispose(); throw; }
            finally { listener.Stop(); }
        }

        private static async Task EchoAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[1024];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                        await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
            }
        }

        public Task RefreshPortsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            await lifetime.CancelAsync();
            await Task.WhenAll(clients);
            lifetime.Dispose();
        }
    }

    private sealed class EmptyVault : ICredentialVault
    {
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task WriteAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class LocalAuthentication : IAuthenticationService
    {
        public Task<AccountProfile> LoginAsync(AccountProfile account, Action<LoginChallenge> onChallenge, CancellationToken cancellationToken) => Task.FromResult(account);
        public Task<AccountProfile> ValidateAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult(account);
        public Task LogoutAsync(AccountProfile account, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> GetAuthorizationAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult("local-test-credential");
    }

    private sealed class DeferredAuthentication : IAuthenticationService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AccountProfile> LoginAsync(AccountProfile account, Action<LoginChallenge> onChallenge, CancellationToken cancellationToken)
        {
            onChallenge(new("https://example.invalid/device", "TEST-CODE", DateTimeOffset.UtcNow.AddMinutes(2)));
            Started.TrySetResult();
            await Complete.Task.WaitAsync(cancellationToken);
            return account with { Username = "fixture-user", StableIdentityId = "github:fixture-user", AuthenticatedAt = DateTimeOffset.UtcNow, Status = "Authenticated" };
        }
        public Task<AccountProfile> ValidateAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult(account);
        public Task LogoutAsync(AccountProfile account, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> GetAuthorizationAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult("local-test-credential");
    }

    private sealed class UnavailableStartup : IUserStartupManager
    {
        public bool Supported => true;
        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) => Task.FromException<bool>(new IOException("simulated startup service unavailable"));
        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class PortReservation : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public int Port { get; }
        public PortReservation() { listener.Server.ExclusiveAddressUse = true; listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        public void Dispose() => listener.Stop();
    }
}
