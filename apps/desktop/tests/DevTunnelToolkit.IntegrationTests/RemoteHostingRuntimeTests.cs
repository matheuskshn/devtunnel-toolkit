using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using DevTunnelToolkit.Application;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Ipc;
using DevTunnelToolkit.Persistence;
using DevTunnelToolkit.Platform;
using Xunit;

namespace DevTunnelToolkit.IntegrationTests;

public sealed class RemoteHostingRuntimeTests
{
    [Fact]
    public async Task RemoteDeleteRequiresExplicitTargetAndPreservesLocalProfile()
    {
        await using var f = await Fixture.CreateAsync();
        var profile = await f.Client.CallAsync<TunnelProfile>("tunnel.save", new TunnelProfile { AccountId = f.Account.Id, TunnelId = f.Target.TunnelId, ClusterId = f.Target.ClusterId });
        var error = await Assert.ThrowsAsync<ToolkitException>(() => f.Client.CallAsync<bool>("remote.delete", new { accountId = f.Account.Id, target = f.Target, confirmation = "wrong" }));
        Assert.Equal("REMOTE_CONFIRMATION_REQUIRED", error.Code); Assert.Empty(f.Remote.Deleted);
        await f.Client.CallAsync<bool>("remote.delete", new { accountId = f.Account.Id, target = f.Target, confirmation = f.Target.CanonicalId });
        Assert.Equal(f.Target.CanonicalId, Assert.Single(f.Remote.Deleted));
        Assert.Equal(profile.Id, Assert.Single((await f.Client.CallAsync<AgentSnapshot>("status")).Tunnels).Id);
        await f.Client.CallAsync<bool>("tunnel.remove", new { id = profile.Id });
        Assert.Single(f.Remote.Deleted);
    }

    [Fact]
    public async Task BatchChecksEveryConfirmationBeforeDeletingAndReportsPartialFailures()
    {
        await using var f = await Fixture.CreateAsync();
        var targets = new[] { f.Target, new RemoteTunnelTarget("second-fixture", "use2") };
        await Assert.ThrowsAsync<ToolkitException>(() => f.Client.CallAsync<RemoteBatchResult[]>("remote.delete-batch",
            new { accountId = f.Account.Id, targets, confirmations = new[] { f.Target.CanonicalId, "wrong" } }));
        Assert.Empty(f.Remote.Deleted);
        f.Remote.FailDelete = targets[1].CanonicalId;
        var results = await f.Client.CallAsync<RemoteBatchResult[]>("remote.delete-batch",
            new { accountId = f.Account.Id, targets, confirmations = targets.Select(t => t.CanonicalId).ToArray() });
        Assert.True(results[0].Succeeded); Assert.False(results[1].Succeeded);
        Assert.Equal("TUNNEL_ACCESS_DENIED", results[1].ErrorCode);
    }

    [Fact]
    public async Task IssuedTokenIsReturnedOnlyToRequesterAndNeverSavedInSnapshotsOrLogs()
    {
        await using var f = await Fixture.CreateAsync();
        var token = await f.Client.CallAsync<RemoteTokenResult>("remote.token",
            new { accountId = f.Account.Id, target = f.Target, input = new RemoteTokenRequest(["connect"]) });
        Assert.Equal("opaque-fixture-secret", token.Token);
        var status = await f.Client.CallAsync<AgentSnapshot>("status");
        Assert.DoesNotContain(token.Token, JsonSerializer.Serialize(status));
        Assert.DoesNotContain(token.Token, JsonSerializer.Serialize(await f.Store.LoadAsync()));
        Assert.DoesNotContain(token.Token, JsonSerializer.Serialize(await f.Client.CallAsync<LogEntry[]>("logs", new { limit = 200 })));
    }

    [Fact]
    public async Task HostPreparationCreatesOnlyMissingPortsAndStartingDoesNotModifyRemoteResources()
    {
        await using var f = await Fixture.CreateAsync();
        var host = await f.SaveHostAsync();
        await Assert.ThrowsAsync<ToolkitException>(() => f.Client.CallAsync<HostPreparationResult>("host.prepare", new { id = host.Id, confirmation = "wrong" }));
        Assert.Empty(f.Remote.CreatedPorts);
        var result = await f.Client.CallAsync<HostPreparationResult>("host.prepare", new { id = host.Id, confirmation = host.CanonicalId });
        Assert.Equal([3180], result.CreatedPorts); Assert.Equal([3140], result.ExistingPorts);
        Assert.Equal([3180], f.Remote.CreatedPorts);
        await f.Client.CallAsync<bool>("host.start", new { id = host.Id });
        await f.WaitAsync(host.Id, ConnectionState.Connected);
        Assert.Equal([3180], f.Remote.CreatedPorts);
        var blocked = await Assert.ThrowsAsync<ToolkitException>(() => f.Client.CallAsync<bool>("remote.delete", new { accountId = f.Account.Id, target = f.Target, confirmation = host.CanonicalId }));
        Assert.Equal("REMOTE_RESOURCE_IN_USE", blocked.Code); Assert.Empty(f.Remote.Deleted);
        await f.Client.CallAsync<bool>("host.stop", new { id = host.Id });
        await f.WaitAsync(host.Id, ConnectionState.Stopped); Assert.Equal(1, f.Hosting.Disposed);
        await f.Client.CallAsync<bool>("host.remove", new { id = host.Id });
        Assert.Empty((await f.Client.CallAsync<AgentSnapshot>("status")).Hosts); Assert.Empty(f.Remote.Deleted);
    }

    [Fact]
    public async Task QueuedHostHasProgressAndCanBeCancelledWithoutOrphanWorker()
    {
        await using var f = await Fixture.CreateAsync();
        f.Hosting.WaitForCancellation = true;
        var host = await f.SaveHostAsync();
        await f.Client.CallAsync<bool>("host.start", new { id = host.Id });
        await f.Hosting.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var status = (await f.Client.CallAsync<AgentSnapshot>("status")).HostStatuses.Single();
        Assert.NotNull(status.OperationStartedAt); Assert.Null(status.OperationCompletedAt);
        Assert.Equal(ConnectionState.Connecting, status.State);
        await f.Client.CallAsync<bool>("host.stop", new { id = host.Id });
        var stopped = await f.WaitAsync(host.Id, ConnectionState.Stopped);
        Assert.NotNull(stopped.OperationCompletedAt);
        Assert.Equal(1, f.Hosting.Cancelled);
    }

    [Fact]
    public async Task AccountCannotBeRemovedWhileHostProfileDependsOnItAndProfilePersists()
    {
        await using var f = await Fixture.CreateAsync();
        var host = await f.SaveHostAsync();
        var blocked = await Assert.ThrowsAsync<ToolkitException>(() => f.Client.CallAsync<bool>("account.remove", new { id = f.Account.Id }));
        Assert.Equal("ACCOUNT_HAS_HOSTS", blocked.Code);
        var persisted = Assert.Single((await f.Store.LoadAsync()).Hosts);
        Assert.Equal(host.Id, persisted.Id); Assert.Equal(host.CanonicalId, persisted.CanonicalId);
        Assert.Equal(2, persisted.Ports.Length);
        Assert.Equal(ConnectionState.Stopped, (await f.Client.CallAsync<AgentSnapshot>("status")).HostStatuses.Single().State);
        Assert.Equal(0, f.Hosting.Starts);
    }

    [Fact]
    public async Task DuplicateHostsAndInvalidRulesFailBeforeAnyNetworkStart()
    {
        await using var f = await Fixture.CreateAsync();
        var host = await f.SaveHostAsync();
        await Assert.ThrowsAsync<ToolkitException>(() => f.Client.CallAsync<HostProfile>("host.save", host with { Id = Guid.NewGuid().ToString("N") }));
        await Assert.ThrowsAsync<ToolkitException>(() => f.Client.CallAsync<HostProfile>("host.save", host with { Proxy = new() { Rules = [new() { Destination = "example.org", Ports = [0] }] } }));
        Assert.Equal(0, f.Hosting.Starts);
    }

    [Fact]
    public async Task ResourceInUseIsProtectedAcrossAccounts()
    {
        await using var f = await Fixture.CreateAsync();
        var host = await f.SaveHostAsync();
        await f.Client.CallAsync<bool>("host.start", new { id = host.Id });
        await f.WaitAsync(host.Id, ConnectionState.Connected);
        var second = await f.Client.CallAsync<AccountProfile>("account.save", new AccountProfile { DisplayName = "Other fixture", Provider = AccountProvider.GitHub });
        var error = await Assert.ThrowsAsync<ToolkitException>(() => f.Client.CallAsync<bool>("remote.delete",
            new { accountId = second.Id, target = f.Target, confirmation = f.Target.CanonicalId }));
        Assert.Equal("REMOTE_RESOURCE_IN_USE", error.Code); Assert.Empty(f.Remote.Deleted);
        error = await Assert.ThrowsAsync<ToolkitException>(() => f.Client.CallAsync<ManagedTunnel>("remote.update",
            new { accountId = second.Id, target = f.Target, input = new RemoteTunnelSpec { Description = "Changed" } }));
        Assert.Equal("REMOTE_RESOURCE_IN_USE", error.Code);
    }

    [Fact]
    public async Task PublishedServiceCannotBecomeThePersonalProxyThroughSettingsChange()
    {
        await using var f = await Fixture.CreateAsync();
        var host = await f.Client.CallAsync<HostProfile>("host.save", new HostProfile
        {
            AccountId = f.Account.Id,
            TunnelId = f.Target.TunnelId,
            ClusterId = f.Target.ClusterId,
            Ports = [new() { Kind = HostPortKind.Service, RemotePort = 8888, LocalPort = 18888 }]
        });
        await f.Client.CallAsync<bool>("host.start", new { id = host.Id });
        await f.WaitAsync(host.Id, ConnectionState.Connected);
        var settings = (await f.Client.CallAsync<AgentSnapshot>("status")).Settings;
        var error = await Assert.ThrowsAsync<ToolkitException>(() => f.Client.CallAsync<AppSettings>("settings.save",
            settings with { Proxy = settings.Proxy with { Enabled = true, Port = 18888 } }));
        Assert.Equal("HOST_LOCAL_SERVICE_CONFLICT", error.Code);
        Assert.False((await f.Client.CallAsync<AgentSnapshot>("status")).Settings.Proxy.Enabled);
    }

    [Fact]
    public async Task EchoDiagnosticRoundTripUsesConnectedMappingAndStopsCleanly()
    {
        await using var f = await Fixture.CreateAsync();
        var echo = await f.Client.CallAsync<EchoStatus>("diagnostic.echo.start", new { port = 0 });
        Assert.True(echo.Running);
        f.Tunnels.EchoPort = echo.Port;
        var profile = await f.Client.CallAsync<TunnelProfile>("tunnel.save", new TunnelProfile
        {
            AccountId = f.Account.Id,
            TunnelId = f.Target.TunnelId,
            ClusterId = f.Target.ClusterId,
            Mappings = [new() { RemotePort = 3140 }],
            Reconnect = new() { Enabled = false }
        });
        await f.Client.CallAsync<bool>("tunnel.connect", new { id = profile.Id });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!(await f.Client.CallAsync<AgentSnapshot>("status", cancellationToken: timeout.Token)).Connections.Any(c => c.State == ConnectionState.Connected))
            await Task.Delay(20, timeout.Token);
        var result = await f.Client.CallAsync<EchoProbeResult>("diagnostic.ping", new { id = profile.Id, portNumber = 3140 });
        Assert.True(result.Succeeded, result.ErrorCode);
        await f.Client.CallAsync<EchoStatus>("diagnostic.echo.stop");
        Assert.False((await f.Client.CallAsync<EchoStatus>("diagnostic.echo.status")).Running);
        await f.Client.CallAsync<bool>("tunnel.stop", new { id = profile.Id });
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "dtk-management-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource lifetime = new();
        private Task server = Task.CompletedTask;
        private CredentialVault vault = null!;
        private AgentRuntime runtime = null!;
        public SqliteStateStore Store { get; private set; } = null!;
        public ToolkitClient Client { get; private set; } = null!;
        public AccountProfile Account { get; private set; } = null!;
        public RemoteTunnelTarget Target { get; } = new("managed-fixture", "use2");
        public FakeRemote Remote { get; } = new();
        public FakeHosting Hosting { get; } = new();
        public FakeTunnels Tunnels { get; } = new();
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            var paths = new LocalPaths(f.directory);
            f.Store = new(paths); f.vault = new(paths, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            f.runtime = new(f.Store, f.vault, new FakeAuth(), f.Tunnels, new("Test", true, false, false, false), remoteManagement: f.Remote, hosting: f.Hosting);
            await f.runtime.InitializeAsync();
            f.server = new ToolkitServer(paths, f.runtime.DispatchAsync, f.runtime.Snapshot, f.lifetime.Cancel).RunAsync(f.lifetime.Token);
            f.Client = new(f.directory);
            f.Account = await f.Client.CallAsync<AccountProfile>("account.save", new AccountProfile { Provider = AccountProvider.GitHub, DisplayName = "Fixture account" });
            return f;
        }
        public async Task<HostProfile> SaveHostAsync()
        {
            var http = await Client.CallAsync<ServiceProfile>("service.save", new ServiceProfile { Name = "Fixture HTTP", Port = 0 });
            var socks = await Client.CallAsync<ServiceProfile>("service.save", new ServiceProfile { Name = "Fixture SOCKS", Protocol = ProxyProtocol.Socks5, Port = 0 });
            await Client.CallAsync<bool>("service.start", new { id = http.Id });
            await Client.CallAsync<bool>("service.start", new { id = socks.Id });
            return await Client.CallAsync<HostProfile>("host.save", new HostProfile
            {
                AccountId = Account.Id,
                TunnelId = Target.TunnelId,
                ClusterId = Target.ClusterId,
                Ports = [new() { ServiceId = http.Id }, new() { RemotePort = 3180, Kind = HostPortKind.Socks5Proxy, ServiceId = socks.Id }],
                Reconnect = new() { Enabled = false }
            });
        }
        public async Task<HostStatus> WaitAsync(string id, ConnectionState expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var status = (await Client.CallAsync<AgentSnapshot>("status", cancellationToken: timeout.Token)).HostStatuses.Single(h => h.ProfileId == id);
                if (status.State == expected) return status;
                if (status.State == ConnectionState.Failed) Assert.Fail(status.LastError);
                await Task.Delay(20, timeout.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync(); await server;
            await runtime.DisposeAsync(); await Store.DisposeAsync(); vault.Dispose(); lifetime.Dispose();
            Directory.Delete(directory, true);
        }
    }

    private sealed class FakeAuth : IAuthenticationService
    {
        public Task<AccountProfile> LoginAsync(AccountProfile account, Action<LoginChallenge> onChallenge, CancellationToken cancellationToken) => Task.FromResult(account);
        public Task<AccountProfile> ValidateAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult(account);
        public Task LogoutAsync(AccountProfile account, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> GetAuthorizationAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult("fixture");
    }

    private sealed class FakeTunnels : ITunnelService
    {
        public int EchoPort { get; set; }
        public Task<DiscoveredTunnel[]> DiscoverAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult<DiscoveredTunnel[]>([]);
        public Task<DiscoveredTunnel> GetAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken) => Task.FromResult(new DiscoveredTunnel(profile.TunnelId, profile.ClusterId, null, [new(3140)]));
        public Task<IRelayConnection> ConnectAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken) => Task.FromResult<IRelayConnection>(new Relay(EchoPort));
        private sealed class Relay(int port) : IRelayConnection
        {
            public event Action<ConnectionState>? StateChanged { add { } remove { } }
            public async Task<Stream> OpenStreamAsync(int remotePort, CancellationToken cancellationToken)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            public Task RefreshPortsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHosting : IHostingService
    {
        public int Starts;
        public int Disposed;
        public int Cancelled;
        public bool WaitForCancellation;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IHostedTunnel> StartAsync(AccountProfile account, HostProfile profile, Action<HostStatus> progress, CancellationToken cancellationToken)
        {
            Starts++;
            progress(new(profile.Id, ConnectionState.Connecting, Phase: "host-relay")); Started.TrySetResult();
            if (WaitForCancellation)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Cancelled++; throw; }
            }
            return new Live(this, profile);
        }
        private sealed class Live(FakeHosting owner, HostProfile profile) : IHostedTunnel
        {
            public event Action<ConnectionState>? StateChanged { add { } remove { } }
            public HostedPort[] Ports => profile.Ports.Select(p => new HostedPort(p.RemotePort, p.Kind, p.LocalAddress, 20000 + p.RemotePort)).ToArray();
            public DateTimeOffset? ExpiresAt => null;
            public DateTimeOffset? HostTokenExpiresAt => null;
            public Task RefreshPortsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() { owner.Disposed++; return ValueTask.CompletedTask; }
        }
    }

    private sealed class FakeRemote : IRemoteTunnelManagementService
    {
        public List<string> Deleted { get; } = [];
        public List<int> CreatedPorts { get; } = [];
        public string? FailDelete { get; set; }
        public Task<ManagedTunnel> GetAsync(AccountProfile account, RemoteTunnelTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(new ManagedTunnel(target, null, "", [], null, null, null, false, [new(3140, "http", "Keep existing", [], [])], []));
        public Task<ManagedTunnel[]> ListAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult<ManagedTunnel[]>([]);
        public Task<ManagedTunnel> CreateAsync(AccountProfile account, RemoteTunnelSpec spec, CancellationToken cancellationToken) => GetAsync(account, new(spec.TunnelId ?? "new-fixture", spec.ClusterId ?? "use2"), cancellationToken);
        public Task<ManagedTunnel> UpdateAsync(AccountProfile account, RemoteTunnelTarget target, RemoteTunnelSpec spec, CancellationToken cancellationToken) => GetAsync(account, target, cancellationToken);
        public Task DeleteAsync(AccountProfile account, RemoteTunnelTarget target, string confirmation, CancellationToken cancellationToken)
        {
            if (target.CanonicalId == FailDelete) throw new ToolkitException("TUNNEL_ACCESS_DENIED");
            Deleted.Add(target.CanonicalId); return Task.CompletedTask;
        }
        public Task<ManagedPort> SavePortAsync(AccountProfile account, RemoteTunnelTarget target, RemotePortSpec port, bool create, CancellationToken cancellationToken)
        {
            Assert.True(create); CreatedPorts.Add(port.PortNumber);
            return Task.FromResult(new ManagedPort(port.PortNumber, port.Protocol, port.Description, [], []));
        }
        public Task DeletePortAsync(AccountProfile account, RemoteTunnelTarget target, int portNumber, string confirmation, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<RemoteAccessEntry[]> AddAccessAsync(AccountProfile account, RemoteTunnelTarget target, int? portNumber, RemoteAccessEntry entry, CancellationToken cancellationToken) => Task.FromResult(new[] { entry });
        public Task<RemoteAccessEntry[]> RemoveAccessAsync(AccountProfile account, RemoteTunnelTarget target, int? portNumber, string entryId, string confirmation, CancellationToken cancellationToken) => Task.FromResult<RemoteAccessEntry[]>([]);
        public Task<RemoteAccessEntry[]> ResetAccessAsync(AccountProfile account, RemoteTunnelTarget target, int? portNumber, string confirmation, CancellationToken cancellationToken) => Task.FromResult<RemoteAccessEntry[]>([]);
        public Task<RemoteTokenResult> IssueTokenAsync(AccountProfile account, RemoteTunnelTarget target, RemoteTokenRequest request, CancellationToken cancellationToken) => Task.FromResult(new RemoteTokenResult("opaque-fixture-secret", request.Scopes, null, "NotReported"));
        public Task<RemoteCluster[]> ListClustersAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult<RemoteCluster[]>([]);
        public Task<RemoteDiagnostic> DiagnoseAsync(AccountProfile account, RemoteTunnelTarget target, CancellationToken cancellationToken) => Task.FromResult(new RemoteDiagnostic("ManagementApi", true, 1));
    }
}
