using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DevTunnelToolkit.Application;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Persistence;
using DevTunnelToolkit.Platform;
using Xunit;

namespace DevTunnelToolkit.IntegrationTests;

public sealed class ServiceLifecycleTests
{
    [Fact]
    public async Task OccupiedAutoStartPortDoesNotAbortOtherServicesOrAgentStartup()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        await using var f = await Fixture.CreateAsync();
        var blocked = await f.SaveAsync(new ServiceProfile
        {
            Name = "Occupied fixture",
            Protocol = ProxyProtocol.Socks5,
            Port = ((IPEndPoint)occupied.LocalEndpoint).Port,
            AutoStart = true
        });
        var healthy = await f.SaveAsync(new ServiceProfile
        {
            Name = "Healthy fixture",
            Protocol = ProxyProtocol.Socks5,
            Port = 0,
            AutoStart = true
        });
        await f.RestartAsync();
        var statuses = f.Runtime.Snapshot().ServiceStatuses;
        var failure = Assert.Single(statuses, s => s.ServiceId == blocked.Id);
        Assert.Equal(ConnectionState.Failed, failure.State);
        Assert.Equal("SERVICE_PORT_CONFLICT", failure.LastError);
        var ready = Assert.Single(statuses, s => s.ServiceId == healthy.Id);
        Assert.Equal(ConnectionState.Connected, ready.State);
        await SocksHandshakeAsync(ready.Port!.Value);
    }

    [Fact]
    public async Task ServicesStartWithoutAccountsAndAutoStartAfterPersistenceReload()
    {
        await using var f = await Fixture.CreateAsync();
        var service = await f.SaveAsync(new ServiceProfile { Name = "Independent SOCKS", Protocol = ProxyProtocol.Socks5, Port = 0, AutoStart = true });
        Assert.Empty(f.Runtime.Snapshot().Accounts);
        await f.CallAsync("service.start", new { id = service.Id });
        var status = Assert.Single(f.Runtime.Snapshot().ServiceStatuses);
        Assert.Equal(ConnectionState.Connected, status.State); Assert.True(status.Port > 0);
        Assert.NotNull(status.OperationCompletedAt);
        await SocksHandshakeAsync(status.Port!.Value);
        await f.RestartAsync();
        status = Assert.Single(f.Runtime.Snapshot().ServiceStatuses);
        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Empty(f.Runtime.Snapshot().Accounts);
        await SocksHandshakeAsync(status.Port!.Value);
    }

    [Fact]
    public async Task PublishingRequiresRunningServiceAndStoppingPublicationPreservesService()
    {
        await using var f = await Fixture.CreateAsync();
        var service = await f.SaveAsync(new ServiceProfile { Name = "Independent proxy", Protocol = ProxyProtocol.Socks5, Port = 0 });
        var host = await f.HostAsync(service);
        Assert.Equal("SERVICE_NOT_RUNNING", (await Assert.ThrowsAsync<ToolkitException>(() => f.CallAsync("host.start", new { id = host.Id }))).Code);
        await f.CallAsync("service.start", new { id = service.Id });
        await f.CallAsync("host.start", new { id = host.Id });
        await f.WaitHostAsync(host.Id, ConnectionState.Connected);
        await f.CallAsync("host.stop", new { id = host.Id });
        Assert.Equal(ConnectionState.Connected, Assert.Single(f.Runtime.Snapshot().ServiceStatuses).State);
        await SocksHandshakeAsync(Assert.Single(f.Runtime.Snapshot().ServiceStatuses).Port!.Value);
        Assert.Equal("SERVICE_RUNNING", (await Assert.ThrowsAsync<ToolkitException>(() => f.SaveAsync(service with { Name = "Edited" }))).Code);
        Assert.Equal("SERVICE_IN_USE", (await Assert.ThrowsAsync<ToolkitException>(() => f.CallAsync("service.remove", new { id = service.Id }))).Code);
        await f.CallAsync("service.stop", new { id = service.Id });
        Assert.Equal("SERVICE_IN_USE", (await Assert.ThrowsAsync<ToolkitException>(() => f.CallAsync("service.remove", new { id = service.Id }))).Code);
        await f.CallAsync("host.remove", new { id = host.Id });
        await f.CallAsync("service.remove", new { id = service.Id });
        Assert.Empty(f.Runtime.Snapshot().Services);
    }

    [Fact]
    public async Task ServiceStopDegradesOnlyItsBindingsAndDoesNotRestartThemOnPortReuse()
    {
        await using var f = await Fixture.CreateAsync();
        var first = await f.SaveAsync(new ServiceProfile { Name = "First", Protocol = ProxyProtocol.Socks5, Port = 0 });
        var second = await f.SaveAsync(new ServiceProfile { Name = "Second", Protocol = ProxyProtocol.Socks5, Port = 0 });
        await f.CallAsync("service.start", new { id = first.Id });
        await f.CallAsync("service.start", new { id = second.Id });
        var host = await f.HostAsync(first, second);
        await f.CallAsync("host.start", new { id = host.Id });
        await f.WaitHostAsync(host.Id, ConnectionState.Connected);
        await f.CallAsync("service.stop", new { id = first.Id });
        var status = Assert.Single(f.Runtime.Snapshot().HostStatuses);
        Assert.Equal(ConnectionState.Degraded, status.State);
        Assert.Equal(second.Id, Assert.Single(status.Ports!).ServiceId);
        Assert.Equal(0, f.Hosting.Disposals);
        await f.CallAsync("service.start", new { id = first.Id });
        Assert.Equal(ConnectionState.Degraded, Assert.Single(f.Runtime.Snapshot().HostStatuses).State);
        Assert.Single(f.Hosting.Active!.Ports);
        await f.CallAsync("host.restart", new { id = host.Id });
        await f.WaitHostAsync(host.Id, ConnectionState.Connected);
        Assert.Equal(2, f.Hosting.Active!.Ports.Length);
    }

    [Fact]
    public async Task BindingGenerationCannotConnectToReusedListenerButOtherServiceSurvives()
    {
        await using var registry = new ManagedServiceRegistry(new PublishedProxyFactory((_, _) => { }));
        var first = new ServiceProfile { Name = "First", Protocol = ProxyProtocol.Socks5, Port = 0 };
        var second = new ServiceProfile { Name = "Second", Protocol = ProxyProtocol.Socks5, Port = 0 };
        var original = await registry.StartAsync(first, default);
        await registry.StartAsync(second, default);
        using var binding = registry.Acquire(first.Id, 3180, HostPortKind.Socks5Proxy);
        using var shared = registry.Acquire(first.Id, 3182, HostPortKind.Socks5Proxy);
        using (var released = registry.Acquire(first.Id, 3183, HostPortKind.Socks5Proxy)) { }
        Assert.True(binding.Available); Assert.True(shared.Available);
        using var unaffected = registry.Acquire(second.Id, 3181, HostPortKind.Socks5Proxy);
        using var connection = await binding.ConnectAsync(default);
        await registry.StopAsync(first.Id);
        Assert.True(connection.Revoked.IsCancellationRequested);
        Assert.False(binding.Available); Assert.True(unaffected.Available);
        Assert.False(shared.Available);
        using (var other = await unaffected.ConnectAsync(default)) await SocksHandshakeAsync(other.Client);
        using var replacement = new TcpListener(IPAddress.Loopback, original.LocalPort); replacement.Start();
        Assert.Equal("SERVICE_BINDING_REVOKED", (await Assert.ThrowsAsync<ToolkitException>(() => binding.ConnectAsync(default))).Code);
        Assert.False(replacement.Pending());
        replacement.Stop();
        await registry.StartAsync(first with { Port = original.LocalPort }, default);
        Assert.False(binding.Available);
        using var renewed = registry.Acquire(first.Id, 3180, HostPortKind.Socks5Proxy);
        using var ready = await renewed.ConnectAsync(default); await SocksHandshakeAsync(ready.Client);
    }

    [Fact]
    public async Task ServicesCannotBypassPrivacyThroughGenericTcpBindingsInEitherDirection()
    {
        await using var f = await Fixture.CreateAsync();
        var service = await f.SaveAsync(new ServiceProfile { Name = "Protected proxy", Protocol = ProxyProtocol.Socks5, Port = 0 });
        await f.CallAsync("service.start", new { id = service.Id });
        var endpoint = Assert.Single(f.Runtime.Snapshot().ServiceStatuses).Port!.Value;
        var host = await f.ExternalHostAsync(endpoint);
        Assert.Equal("SERVICE_BINDING_REQUIRED", (await Assert.ThrowsAsync<ToolkitException>(() => f.CallAsync("host.start", new { id = host.Id }))).Code);
        await f.CallAsync("service.stop", new { id = service.Id });
        await f.CallAsync("host.start", new { id = host.Id });
        await f.WaitHostAsync(host.Id, ConnectionState.Connected);
        service = await f.SaveAsync(service with { Port = endpoint });
        Assert.Equal("SERVICE_BINDING_REQUIRED", (await Assert.ThrowsAsync<ToolkitException>(() => f.CallAsync("service.start", new { id = service.Id }))).Code);
        await f.CallAsync("host.stop", new { id = host.Id });
        Assert.Equal("SERVICE_BINDING_REQUIRED", (await Assert.ThrowsAsync<ToolkitException>(() => f.CallAsync("host.start", new { id = host.Id }))).Code);
    }

    [Fact]
    public async Task PacAndPersonalProxyCannotReuseManagedServicePorts()
    {
        await using var f = await Fixture.CreateAsync();
        var service = await f.SaveAsync(new ServiceProfile { Name = "Managed", Port = 0 });
        await f.CallAsync("service.start", new { id = service.Id });
        var port = Assert.Single(f.Runtime.Snapshot().ServiceStatuses).Port!.Value;
        var settings = f.Runtime.Snapshot().Settings;
        Assert.Equal("SERVICE_PORT_CONFLICT", (await Assert.ThrowsAsync<ToolkitException>(() => f.CallAsync("settings.save", settings with { Pac = new() { Enabled = true, Port = port } }))).Code);
        Assert.Equal("SERVICE_PORT_CONFLICT", (await Assert.ThrowsAsync<ToolkitException>(() => f.CallAsync("settings.save", settings with { Proxy = new() { Enabled = true, Port = port } }))).Code);
        await f.CallAsync("service.stop", new { id = service.Id });
        await f.CallAsync("settings.save", settings with { Pac = new() { Enabled = true, Port = port } });
        await f.SaveAsync(service with { Port = port });
        Assert.Equal("SERVICE_PORT_CONFLICT", (await Assert.ThrowsAsync<ToolkitException>(() => f.CallAsync("service.start", new { id = service.Id }))).Code);
    }

    [Fact]
    public async Task BindingProtocolCannotDisagreeWithManagedService()
    {
        await using var f = await Fixture.CreateAsync();
        var service = await f.SaveAsync(new ServiceProfile { Name = "SOCKS", Protocol = ProxyProtocol.Socks5, Port = 0 });
        var host = await f.HostAsync(service);
        Assert.Equal("SERVICE_PROTOCOL_MISMATCH", (await Assert.ThrowsAsync<ToolkitException>(() => f.CallAsync("host.save", host with { Ports = [host.Ports[0] with { Kind = HostPortKind.HttpProxy }] }))).Code);
        Assert.Equal("SERVICE_PROTOCOL_MISMATCH", (await Assert.ThrowsAsync<ToolkitException>(() => f.SaveAsync(service with { Protocol = ProxyProtocol.Http }))).Code);
    }

    private static async Task SocksHandshakeAsync(int port)
    {
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port);
        await SocksHandshakeAsync(client);
    }

    private static async Task SocksHandshakeAsync(TcpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.GetStream().WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
        var response = new byte[2]; await client.GetStream().ReadExactlyAsync(response, timeout.Token);
        Assert.Equal(new byte[] { 5, 0 }, response);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "dtk-services-" + Guid.NewGuid().ToString("N"));
        private SqliteStateStore store = null!;
        public AgentRuntime Runtime { get; private set; } = null!;
        public FakeHosting Hosting { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync() { var fixture = new Fixture(); await fixture.StartAsync(); return fixture; }
        private async Task StartAsync()
        {
            store = new(new LocalPaths(directory));
            var registry = new ManagedServiceRegistry(new PublishedProxyFactory((_, _) => { }));
            Hosting = new(registry);
            Runtime = new(store, new NoVault(), new NoAuth(), new NoTunnels(), new("Test", true, false, false, false), hosting: Hosting, serviceRegistry: registry);
            await Runtime.InitializeAsync();
        }
        public async Task RestartAsync() { await Runtime.DisposeAsync(); await store.DisposeAsync(); await StartAsync(); }
        public Task<object> CallAsync(string operation, object payload) => Runtime.DispatchAsync(operation, JsonSerializer.SerializeToElement(payload, WireJson), default);
        private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
        public async Task<ServiceProfile> SaveAsync(ServiceProfile service) => (ServiceProfile)await CallAsync("service.save", service);
        private async Task<AccountProfile> AccountAsync() => Runtime.Snapshot().Accounts.FirstOrDefault() ??
            (AccountProfile)await CallAsync("account.save", new AccountProfile { DisplayName = "Service fixture", Provider = AccountProvider.GitHub });
        public async Task<HostProfile> HostAsync(params ServiceProfile[] services)
        {
            var account = await AccountAsync();
            return (HostProfile)await CallAsync("host.save", new HostProfile
            {
                AccountId = account.Id,
                TunnelId = "service-fixture",
                ClusterId = "use2",
                Ports = services.Select((s, index) => new HostPortMapping { RemotePort = 3180 + index, Kind = ServiceValidation.Kind(s.Protocol), ServiceId = s.Id }).ToArray()
            });
        }
        public async Task<HostProfile> ExternalHostAsync(int port)
        {
            var account = await AccountAsync();
            return (HostProfile)await CallAsync("host.save", new HostProfile
            {
                AccountId = account.Id,
                TunnelId = "external-fixture",
                ClusterId = "use2",
                Ports = [new() { RemotePort = 8080, Kind = HostPortKind.Service, LocalPort = port }]
            });
        }
        public async Task WaitHostAsync(string id, ConnectionState state)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!Runtime.Snapshot().HostStatuses.Any(s => s.ProfileId == id && s.State == state)) await Task.Delay(10, timeout.Token);
        }
        public async ValueTask DisposeAsync() { await Runtime.DisposeAsync(); await store.DisposeAsync(); Directory.Delete(directory, true); }
    }

    private sealed class FakeHosting(ManagedServiceRegistry registry) : IHostingService
    {
        public int Disposals { get; private set; }
        public Live? Active { get; private set; }
        public Task<IHostedTunnel> StartAsync(AccountProfile account, HostProfile profile, Action<HostStatus> progress, CancellationToken cancellationToken)
        {
            Active = new(this, profile, profile.Ports.Where(p => p.ServiceId is not null).Select(p => registry.Acquire(p.ServiceId!, p.RemotePort, p.Kind)).ToArray());
            return Task.FromResult<IHostedTunnel>(Active);
        }
        public sealed class Live(FakeHosting owner, HostProfile profile, IManagedServiceBinding[] bindings) : IHostedTunnel
        {
            public event Action<ConnectionState>? StateChanged { add { } remove { } }
            public HostedPort[] Ports => bindings.Where(b => b.Available).Select(b => b.Port).Concat(profile.Ports.Where(p => p.Kind == HostPortKind.Service).Select(p => new HostedPort(p.RemotePort, p.Kind, p.LocalAddress, p.LocalPort))).ToArray();
            public DateTimeOffset? ExpiresAt => null;
            public DateTimeOffset? HostTokenExpiresAt => null;
            public Task RefreshPortsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() { owner.Disposals++; foreach (var binding in bindings) binding.Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class NoAuth : IAuthenticationService
    {
        public Task<AccountProfile> LoginAsync(AccountProfile account, Action<LoginChallenge> challenge, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AccountProfile> ValidateAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult(account);
        public Task LogoutAsync(AccountProfile account, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> GetAuthorizationAsync(AccountProfile account, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class NoTunnels : ITunnelService
    {
        public Task<DiscoveredTunnel[]> DiscoverAsync(AccountProfile account, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DiscoveredTunnel> GetAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IRelayConnection> ConnectAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class NoVault : ICredentialVault
    {
        public bool Available => true;
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
