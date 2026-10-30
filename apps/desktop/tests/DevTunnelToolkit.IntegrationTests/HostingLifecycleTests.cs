using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DevTunnelToolkit.Application;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Ipc;
using DevTunnelToolkit.Persistence;
using DevTunnelToolkit.Platform;
using Xunit;

namespace DevTunnelToolkit.IntegrationTests;

public sealed class HostingLifecycleTests
{
    [Fact]
    public async Task AutoStartLoadsPersistedProfileButNeverPersistsConnectionStatus()
    {
        await using var fixture = await Fixture.CreateAsync();
        var profile = await fixture.SaveHostAsync(autoStart: true);
        Assert.Equal(0, fixture.Hosting.Starts);
        Assert.Equal(ConnectionState.Stopped, (await fixture.SnapshotAsync()).HostStatuses.Single().State);
        await fixture.RestartAsync();
        var online = await fixture.WaitStateAsync(profile.Id, ConnectionState.Connected);
        Assert.Equal(1, fixture.Hosting.Starts);
        Assert.Equal(profile.Id, Assert.Single((await fixture.Store.LoadAsync()).Hosts).Id);
        Assert.NotNull(online.OperationCompletedAt);
        await fixture.RestartAsync();
        await fixture.WaitStateAsync(profile.Id, ConnectionState.Connected);
        Assert.Equal(2, fixture.Hosting.Starts);
        Assert.Equal(1, fixture.Hosting.Disposed);
        Assert.Equal(profile.Id, Assert.Single((await fixture.SnapshotAsync()).Hosts).Id);
    }

    [Fact]
    public async Task TransientOpenFailureRetriesAndKeepsOperationStartTime()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Hosting.Errors.Enqueue("HOST_NETWORK_TRANSIENT");
        var profile = await fixture.SaveHostAsync();
        await fixture.Client.CallAsync<bool>("host.start", new { id = profile.Id });
        var retry = await fixture.WaitStateAsync(profile.Id, ConnectionState.Reconnecting);
        Assert.Equal(1, retry.RetryCount); Assert.NotNull(retry.NextRetryAt);
        Assert.NotNull(retry.OperationStartedAt); Assert.Null(retry.OperationCompletedAt);
        var online = await fixture.WaitStateAsync(profile.Id, ConnectionState.Connected);
        Assert.Equal(2, fixture.Hosting.Starts); Assert.Equal(1, online.RetryCount);
        Assert.Equal(retry.OperationStartedAt, online.OperationStartedAt);
        Assert.NotNull(online.OperationCompletedAt); Assert.Null(online.NextRetryAt);
        Assert.Empty((await fixture.SnapshotAsync()).Tunnels);
    }

    [Fact]
    public async Task CancellationDuringBackoffPreventsAnotherStartAndAllowsExplicitRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Hosting.Errors.Enqueue("HOST_NETWORK_TRANSIENT");
        var profile = await fixture.SaveHostAsync(delaySeconds: 3);
        await fixture.Client.CallAsync<bool>("host.start", new { id = profile.Id });
        await fixture.WaitStateAsync(profile.Id, ConnectionState.Reconnecting);
        await fixture.Client.CallAsync<bool>("host.stop", new { id = profile.Id });
        var stopped = await fixture.WaitStateAsync(profile.Id, ConnectionState.Stopped);
        Assert.Null(stopped.NextRetryAt); Assert.NotNull(stopped.OperationCompletedAt);
        Assert.Equal(1, fixture.Hosting.Starts);
        await fixture.Client.CallAsync<bool>("host.restart", new { id = profile.Id });
        await fixture.WaitStateAsync(profile.Id, ConnectionState.Connected);
        Assert.Equal(2, fixture.Hosting.Starts);
    }

    [Theory]
    [InlineData("AUTHENTICATION_REQUIRED", ConnectionState.AuthenticationRequired)]
    [InlineData("AUTHENTICATION_EXPIRED", ConnectionState.AuthenticationRequired)]
    [InlineData("CLI_ACCOUNT_CHANGED", ConnectionState.AuthenticationRequired)]
    [InlineData("HOST_REMOTE_PORT_MISSING", ConnectionState.Failed)]
    [InlineData("HOST_PROXY_REQUIRES_PRIVATE_TUNNEL", ConnectionState.Failed)]
    [InlineData("TUNNEL_NOT_FOUND", ConnectionState.Failed)]
    public async Task PermanentFailureDoesNotRetryOrCreateNewResource(string code, ConnectionState expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Hosting.Errors.Enqueue(code);
        var profile = await fixture.SaveHostAsync();
        await fixture.Client.CallAsync<bool>("host.start", new { id = profile.Id });
        var failed = await fixture.WaitStateAsync(profile.Id, expected);
        Assert.Equal(code, failed.LastError); Assert.Equal(0, failed.RetryCount);
        Assert.Null(failed.NextRetryAt); Assert.Equal(1, fixture.Hosting.Starts);
        Assert.Equal(profile.CanonicalId, Assert.Single((await fixture.SnapshotAsync()).Hosts).CanonicalId);
    }

    [Fact]
    public async Task CredentialInvalidationLogsOutAndStopsHostingWithoutRemovingProfile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var profile = await fixture.SaveHostAsync();
        await fixture.Client.CallAsync<bool>("host.start", new { id = profile.Id });
        await fixture.WaitStateAsync(profile.Id, ConnectionState.Connected);
        await fixture.Client.CallAsync<AccountProfile>("account.save", fixture.Account with { Scope = "changed-scope" });
        await fixture.WaitStateAsync(profile.Id, ConnectionState.Stopped);
        Assert.Equal(1, fixture.Authentication.Logouts); Assert.Equal(1, fixture.Hosting.Disposed);
        var snapshot = await fixture.SnapshotAsync();
        Assert.Equal(profile.CanonicalId, Assert.Single(snapshot.Hosts).CanonicalId);
        Assert.Equal("AuthenticationRequired", Assert.Single(snapshot.Accounts).Status);
        Assert.Equal(1, fixture.Hosting.Starts);
    }

    [Fact]
    public async Task NewLoginStopsPreviousHostAndDoesNotRestartItImplicitly()
    {
        await using var fixture = await Fixture.CreateAsync();
        var profile = await fixture.SaveHostAsync();
        await fixture.Client.CallAsync<bool>("host.start", new { id = profile.Id });
        await fixture.WaitStateAsync(profile.Id, ConnectionState.Connected);
        await fixture.Client.CallAsync<LoginStatus>("account.login", new { id = fixture.Account.Id });
        await fixture.WaitStateAsync(profile.Id, ConnectionState.Stopped);
        Assert.Equal(1, fixture.Hosting.Disposed);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Volatile.Read(ref fixture.Authentication.Logins) == 0) await Task.Delay(10, timeout.Token);
        Assert.Equal(1, fixture.Hosting.Starts);
        Assert.Equal(profile.Id, Assert.Single((await fixture.Store.LoadAsync()).Hosts).Id);
    }

    [Fact]
    public async Task RuntimeDisposalDuringOpenCancelsAndClosesItsTemporaryListener()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Hosting.BindWhileOpening = true;
        var profile = await fixture.SaveHostAsync();
        await fixture.Client.CallAsync<bool>("host.start", new { id = profile.Id });
        var port = await fixture.Hosting.OpeningPort.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var probe = new TcpClient()) await probe.ConnectAsync(IPAddress.Loopback, port);
        await fixture.StopSessionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, fixture.Hosting.Cancelled);
        using var closed = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => closed.ConnectAsync(IPAddress.Loopback, port));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "dtk-host-lifecycle-" + Guid.NewGuid().ToString("N"));
        private CancellationTokenSource? serverLifetime;
        private Task server = Task.CompletedTask;
        private AgentRuntime? runtime;
        public SqliteStateStore Store { get; private set; } = null!;
        public ToolkitClient Client { get; private set; } = null!;
        public AccountProfile Account { get; private set; } = null!;
        public FakeHosting Hosting { get; } = new();
        public FakeAuthentication Authentication { get; } = new();
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(); await fixture.StartSessionAsync();
            fixture.Account = await fixture.Client.CallAsync<AccountProfile>("account.save", new AccountProfile { Provider = AccountProvider.GitHub, DisplayName = "Lifecycle fixture" });
            return fixture;
        }
        private async Task StartSessionAsync()
        {
            var paths = new LocalPaths(directory); Store = new(paths);
            runtime = new(Store, new EmptyVault(), Authentication, new NoRemoteTunnels(), new("Test", true, false, false, false), hosting: Hosting);
            await runtime.InitializeAsync();
            serverLifetime = new();
            server = new ToolkitServer(paths, runtime.DispatchAsync, runtime.Snapshot, serverLifetime.Cancel).RunAsync(serverLifetime.Token);
            Client = new(directory);
        }
        public async Task RestartAsync() { await StopSessionAsync(); await StartSessionAsync(); }
        public async Task StopSessionAsync()
        {
            if (runtime is null) return;
            await serverLifetime!.CancelAsync(); await server;
            await runtime.DisposeAsync(); runtime = null;
            await Store.DisposeAsync(); serverLifetime.Dispose(); serverLifetime = null;
        }
        public Task<AgentSnapshot> SnapshotAsync() => Client.CallAsync<AgentSnapshot>("status");
        public async Task<HostProfile> SaveHostAsync(bool autoStart = false, int delaySeconds = 1)
        {
            var service = await Client.CallAsync<ServiceProfile>("service.save", new ServiceProfile { Name = "Lifecycle proxy", Port = 0, AutoStart = autoStart });
            await Client.CallAsync<bool>("service.start", new { id = service.Id });
            return await Client.CallAsync<HostProfile>("host.save", new HostProfile
            {
                AccountId = Account.Id,
                TunnelId = "lifecycle-fixture",
                ClusterId = "use2",
                AutoStart = autoStart,
                Ports = [new() { RemotePort = 3140, Kind = HostPortKind.HttpProxy, ServiceId = service.Id }],
                Reconnect = new() { InitialDelaySeconds = delaySeconds, MaxDelaySeconds = delaySeconds, MaxRetries = 2 }
            });
        }
        public async Task<HostStatus> WaitStateAsync(string id, ConnectionState expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var snapshot = await Client.CallAsync<AgentSnapshot>("status", cancellationToken: timeout.Token);
                var status = snapshot.HostStatuses.Single(s => s.ProfileId == id);
                if (status.State == expected) return status;
                if (status.State is ConnectionState.Failed or ConnectionState.AuthenticationRequired) Assert.Fail($"Unexpected host state: {status.State} ({status.LastError})");
                await Task.Delay(10, timeout.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            await StopSessionAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeHosting : IHostingService
    {
        public int Starts;
        public int Disposed;
        public int Cancelled;
        public bool BindWhileOpening;
        public ConcurrentQueue<string> Errors { get; } = new();
        public TaskCompletionSource<int> OpeningPort { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IHostedTunnel> StartAsync(AccountProfile account, HostProfile profile, Action<HostStatus> progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Starts);
            progress(new(profile.Id, ConnectionState.Connecting, Phase: "host-relay"));
            if (Errors.TryDequeue(out var code)) throw new ToolkitException(code);
            if (BindWhileOpening)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
                OpeningPort.TrySetResult(((IPEndPoint)listener.LocalEndpoint).Port);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); throw; }
                finally { listener.Stop(); }
            }
            return new Live(this, profile);
        }
        private sealed class Live(FakeHosting owner, HostProfile profile) : IHostedTunnel
        {
            private int disposed;
            public event Action<ConnectionState>? StateChanged { add { } remove { } }
            public HostedPort[] Ports => profile.Ports.Select(p => new HostedPort(p.RemotePort, p.Kind, p.LocalAddress, 20000 + p.RemotePort)).ToArray();
            public DateTimeOffset? ExpiresAt => null;
            public DateTimeOffset? HostTokenExpiresAt => null;
            public Task RefreshPortsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Increment(ref owner.Disposed);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeAuthentication : IAuthenticationService
    {
        public int Logins;
        public int Logouts;
        public Task<AccountProfile> LoginAsync(AccountProfile account, Action<LoginChallenge> onChallenge, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Logins);
            return Task.FromResult(account with { AuthenticatedAt = DateTimeOffset.UtcNow, Status = "Authenticated" });
        }
        public Task<AccountProfile> ValidateAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult(account);
        public Task<string> GetAuthorizationAsync(AccountProfile account, CancellationToken cancellationToken) => throw new NotSupportedException("No real provider is used by lifecycle tests.");
        public Task LogoutAsync(AccountProfile account, CancellationToken cancellationToken) { Interlocked.Increment(ref Logouts); return Task.CompletedTask; }
    }

    private sealed class NoRemoteTunnels : ITunnelService
    {
        public Task<DiscoveredTunnel[]> DiscoverAsync(AccountProfile account, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DiscoveredTunnel> GetAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IRelayConnection> ConnectAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EmptyVault : ICredentialVault
    {
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task WriteAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
