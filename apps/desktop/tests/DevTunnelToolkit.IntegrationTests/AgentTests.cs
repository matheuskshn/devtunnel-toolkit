using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevTunnelToolkit.Application;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Ipc;
using DevTunnelToolkit.Persistence;
using DevTunnelToolkit.Platform;
using Xunit;

namespace DevTunnelToolkit.IntegrationTests;

public sealed class AgentTests
{
    [Theory]
    [InlineData(AccountProvider.MicrosoftCli)]
    [InlineData(AccountProvider.GitHubCli)]
    public async Task CliBackedAccountWithoutClientIdRoundTripsThroughRealIpc(AccountProvider provider)
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = await fixture.Client.CallAsync<AccountProfile>("account.save",
            new AccountProfile { DisplayName = "CLI fixture", Provider = provider });
        Assert.Equal(provider, account.Provider);
        Assert.Null(account.ClientId);
        var saved = Assert.Single((await fixture.Client.CallAsync<AgentSnapshot>("status")).Accounts);
        Assert.Equal(account.Id, saved.Id);
        Assert.Equal(provider, saved.Provider);
    }

    [Theory]
    [InlineData(CliLoginMode.DeviceCode)]
    [InlineData(CliLoginMode.Browser)]
    [InlineData(CliLoginMode.WindowsIntegrated)]
    public async Task CliLoginModeRoundTripsAndCanBeEditedWithoutIdentityChanges(CliLoginMode mode)
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = await fixture.Client.CallAsync<AccountProfile>("account.save", new AccountProfile { Provider = AccountProvider.MicrosoftCli, CliLoginMode = mode });
        Assert.Equal(mode, (await fixture.Client.CallAsync<AgentSnapshot>("status")).Accounts.Single().CliLoginMode);
        var updated = await fixture.Client.CallAsync<AccountProfile>("account.save", account with { CliLoginMode = CliLoginMode.Browser });
        Assert.Equal(CliLoginMode.Browser, updated.CliLoginMode);
        Assert.Equal(account.Id, updated.Id);
        Assert.Equal(account.Provider, updated.Provider);
    }

    [Fact]
    public async Task CompleteIpcJourneyConnectsAndStopsByCanonicalId()
    {
        await using var fixture = await Fixture.CreateAsync();
        var client = fixture.Client;
        var account = await client.CallAsync<AccountProfile>("account.save", new AccountProfile { DisplayName = "Fixture", Provider = AccountProvider.GitHub });
        var discovered = await client.CallAsync<DiscoveredTunnel[]>("account.discover", new { id = account.Id });
        Assert.Single(discovered);
        var profile = await client.CallAsync<TunnelProfile>("tunnel.save", new TunnelProfile { AccountId = account.Id, TunnelId = "fixture-tunnel", ClusterId = "usw2", Alias = "fixture", Reconnect = new() { Enabled = false } });
        await client.CallAsync<bool>("tunnel.connect", new { id = profile.CanonicalId });
        var connected = await WaitForStateAsync(client, profile.Id, ConnectionState.Connected);
        var port = Assert.Single(connected.Ports!);
        Assert.Equal(port.LocalPort, Assert.Single((await client.CallAsync<AgentSnapshot>("status")).Tunnels.Single().Mappings).LastEffectivePort);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port.LocalPort, timeout.Token);
            await tcp.GetStream().WriteAsync("hello-through-agent"u8.ToArray(), timeout.Token);
            var response = new byte[19];
            await tcp.GetStream().ReadExactlyAsync(response, timeout.Token);
            Assert.Equal("hello-through-agent", Encoding.UTF8.GetString(response));
        }
        await client.CallAsync<bool>("tunnel.stop", new { id = "fixture" });
        await WaitForStateAsync(client, profile.Id, ConnectionState.Stopped);
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, port.LocalPort));
        await client.CallAsync<bool>("tunnel.remove", new { id = profile.CanonicalId });
        Assert.Empty((await client.CallAsync<AgentSnapshot>("status")).Tunnels);
    }

    [Fact]
    public async Task TwoTunnelsAndDuplicateConnectRemainIsolated()
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = await fixture.Client.CallAsync<AccountProfile>("account.save", new AccountProfile { Provider = AccountProvider.GitHub });
        var first = await fixture.Client.CallAsync<TunnelProfile>("tunnel.save", new TunnelProfile { AccountId = account.Id, TunnelId = "first-tunnel", ClusterId = "usw2" });
        var second = await fixture.Client.CallAsync<TunnelProfile>("tunnel.save", new TunnelProfile { AccountId = account.Id, TunnelId = "second-tunnel", ClusterId = "usw2" });
        await fixture.Client.CallAsync<bool>("tunnel.connect", new { id = first.CanonicalId });
        await fixture.Client.CallAsync<bool>("tunnel.connect", new { id = first.Id });
        await fixture.Client.CallAsync<bool>("tunnel.connect", new { id = second.CanonicalId });
        var one = await WaitForStateAsync(fixture.Client, first.Id, ConnectionState.Connected);
        var two = await WaitForStateAsync(fixture.Client, second.Id, ConnectionState.Connected);
        Assert.NotEqual(one.Ports![0].LocalPort, two.Ports![0].LocalPort);
        Assert.Equal(2, fixture.Service.Connections);
        await fixture.Client.CallAsync<bool>("tunnel.stop", new { id = first.CanonicalId });
        Assert.Equal(ConnectionState.Connected, (await fixture.Client.CallAsync<AgentSnapshot>("status")).Connections.Single(c => c.ProfileId == second.Id).State);
    }

    [Fact]
    public async Task LoginCancellationDoesNotDeadlockMutationOrExposeCredential()
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = await fixture.Client.CallAsync<AccountProfile>("account.save", new AccountProfile { Provider = AccountProvider.GitHub });
        await fixture.Client.CallAsync<LoginStatus>("account.login", new { id = account.Id });
        var exception = await Assert.ThrowsAsync<ToolkitException>(() => fixture.Client.CallAsync<AccountProfile>("account.validate", new { id = account.Id }));
        Assert.Equal("LOGIN_IN_PROGRESS", exception.Code);
        await fixture.Client.CallAsync<bool>("account.cancel", new { id = account.Id });
        var snapshot = await fixture.Client.CallAsync<AgentSnapshot>("status");
        Assert.Equal("Cancelled", Assert.Single(snapshot.Logins).State);
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(snapshot));
        await fixture.Client.CallAsync<bool>("account.remove", new { id = account.Id });
        Assert.Empty((await fixture.Client.CallAsync<AgentSnapshot>("status")).Accounts);
    }

    [Fact]
    public async Task AccountsCannotBeRemovedWhileProfilesDependOnThem()
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = await fixture.Client.CallAsync<AccountProfile>("account.save", new AccountProfile());
        await fixture.Client.CallAsync<TunnelProfile>("tunnel.save", new TunnelProfile { AccountId = account.Id, TunnelId = "test-tunnel", ClusterId = "usw2" });
        var exception = await Assert.ThrowsAsync<ToolkitException>(() => fixture.Client.CallAsync<bool>("account.remove", new { id = account.Id }));
        Assert.Equal("ACCOUNT_HAS_TUNNELS", exception.Code);
    }

    [Fact]
    public async Task SubscriptionAndInvalidFrameDoNotInterruptHealthyClients()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscription = fixture.Client.WatchAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        await Assert.ThrowsAsync<ToolkitException>(() => fixture.Client.CallAsync<bool>("execute-shell", new { command = "ignored" }));
        var snapshot = await fixture.Client.CallAsync<AgentSnapshot>("status");
        Assert.Empty(snapshot.Tunnels);
        using var frame = new MemoryStream([0x7f, 0xff, 0xff, 0xff]);
        Assert.Equal("IPC_INVALID_FRAME", (await Assert.ThrowsAsync<ToolkitException>(() => WireProtocol.ReadAsync<AgentRequest>(frame, default))).Code);
    }

    [Fact]
    public void DisabledDiscoveredPortIsNotAutomaticallyPublishedAgain()
    {
        var profile = new TunnelProfile { Mappings = [new() { RemotePort = 3140, Mode = MappingMode.Disabled }] };
        Assert.Equal(3180, Assert.Single(AgentRuntime.ResolveMappings(profile, new("test-tunnel", "usw2", null, [new(3140), new(3180)]))).RemotePort);
    }

    [Fact]
    public void AutoDiscoveredPortsCanDisappearWithoutBreakingTheProfile()
    {
        var profile = new TunnelProfile { Mappings = [new() { RemotePort = 3140, AutoDiscovered = true, LastEffectivePort = 19001 }] };
        Assert.Equal(3180, Assert.Single(AgentRuntime.ResolveMappings(profile, new("test-tunnel", "usw2", null, [new(3180)]))).RemotePort);
    }

    [Fact]
    public async Task PacServerUsesEffectiveMappingAndProtectsReferencedTunnel()
    {
        await using var fixture = await Fixture.CreateAsync();
        var client = fixture.Client;
        var account = await client.CallAsync<AccountProfile>("account.save", new AccountProfile { Provider = AccountProvider.GitHub });
        var profile = await client.CallAsync<TunnelProfile>("tunnel.save", new TunnelProfile { AccountId = account.Id, TunnelId = "pac-tunnel", ClusterId = "usw2" });
        await client.CallAsync<bool>("tunnel.connect", new { id = profile.Id });
        var connection = await WaitForStateAsync(client, profile.Id, ConnectionState.Connected);
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var settings = new AppSettings { Pac = new() { Enabled = true, Port = port, Rules = [new() { Domain = "example.invalid", ProfileId = profile.Id, RemotePort = 3140 }] } };
        await client.CallAsync<AppSettings>("settings.save", settings);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var content = await http.GetStringAsync($"http://127.0.0.1:{port}/proxy.pac");
        Assert.Contains($"PROXY 127.0.0.1:{connection.Ports![0].LocalPort}", content);
        Assert.Contains(".example.invalid", content);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{port}/other")).StatusCode);
        using var rebinding = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/proxy.pac");
        rebinding.Headers.Host = $"untrusted.invalid:{port}";
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(rebinding)).StatusCode);
        Assert.Equal("TUNNEL_HAS_PAC_RULES", (await Assert.ThrowsAsync<ToolkitException>(() => client.CallAsync<bool>("tunnel.remove", new { id = profile.Id }))).Code);
        await client.CallAsync<bool>("tunnel.stop", new { id = profile.Id });
        Assert.Contains("PROXY 127.0.0.1:1", await http.GetStringAsync($"http://127.0.0.1:{port}/proxy.pac"));
        await client.CallAsync<AppSettings>("settings.save", settings with { Pac = settings.Pac with { Enabled = false, Rules = [] } });
        await client.CallAsync<bool>("tunnel.remove", new { id = profile.Id });
    }

    [Theory]
    [InlineData("example.invalid\";alert(1);//")]
    [InlineData("*.example.invalid")]
    [InlineData("example.invalid/route")]
    [InlineData("example.invalid.")]
    public void PacRejectsUntrustedScriptCharacters(string domain)
    {
        Assert.Equal("INVALID_PAC_DOMAIN", Assert.Throws<ToolkitException>(() => Validation.Pac(new() { Rules = [new() { Domain = domain, ProfileId = Guid.NewGuid().ToString("N"), RemotePort = 3140 }] })).Code);
    }

    [Fact]
    public async Task ProxyAndManualPacPersistAndStoppingPreservesProfiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var client = fixture.Client;
        var account = await client.CallAsync<AccountProfile>("account.save", new AccountProfile { Provider = AccountProvider.GitHub });
        var profile = await client.CallAsync<TunnelProfile>("tunnel.save", new TunnelProfile { AccountId = account.Id, TunnelId = "route-fixture", ClusterId = "usw2" });
        var rule = new ProxyRoute { Destination = "example.org", ProfileId = profile.Id, DirectFallback = true };
        using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var settings = new AppSettings { Proxy = new() { Enabled = true, Port = port, Rules = [rule] }, Pac = new() { Mode = PacMode.Manual, ManualScript = "function FindProxyForURL(url, host) { return 'DIRECT'; }" } };
        await client.CallAsync<AppSettings>("settings.save", settings);
        Assert.Equal(RouteAction.Direct, (await client.CallAsync<RouteDecision>("proxy.test", new { host = "example.org", port = 443 })).Action);
        Assert.Equal(settings.Pac.ManualScript, await client.CallAsync<string>("pac.preview", settings));
        Assert.Equal("TUNNEL_HAS_PROXY_RULES", (await Assert.ThrowsAsync<ToolkitException>(() => client.CallAsync<bool>("tunnel.remove", new { id = profile.Id }))).Code);
        var changedLimits = settings with { Proxy = settings.Proxy with { MaxClients = 2 } };
        Assert.Equal("PROXY_RESTART_REQUIRED", (await Assert.ThrowsAsync<ToolkitException>(() => client.CallAsync<AppSettings>("settings.save", changedLimits))).Code);
        Assert.Equal(128, (await client.CallAsync<AgentSnapshot>("status")).Settings.Proxy.MaxClients);
        await client.CallAsync<bool>("proxy.stop");
        await client.CallAsync<bool>("connections.stop");
        var snapshot = await client.CallAsync<AgentSnapshot>("status");
        Assert.False(snapshot.Settings.Proxy.Enabled); Assert.Single(snapshot.Tunnels); Assert.Single(snapshot.Settings.Proxy.Rules);
        using var released = new TcpListener(IPAddress.Loopback, port); released.Start();
    }

    [Fact]
    public async Task ProxyPortConflictRollsBackSettings()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var busy = new TcpListener(IPAddress.Loopback, 0); busy.Start();
        var settings = new AppSettings { Proxy = new() { Enabled = true, Port = ((IPEndPoint)busy.LocalEndpoint).Port } };
        Assert.Equal("LOCAL_PROXY_START_FAILED", (await Assert.ThrowsAsync<ToolkitException>(() => fixture.Client.CallAsync<AppSettings>("settings.save", settings))).Code);
        Assert.False((await fixture.Client.CallAsync<AgentSnapshot>("status")).Settings.Proxy.Enabled);
    }

    [Fact]
    public async Task GracefulShutdownAcknowledgesBeforeStoppingIpc()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.True(await fixture.Client.CallAsync<bool>("agent.shutdown"));
    }

    private static async Task<ConnectionStatus> WaitForStateAsync(ToolkitClient client, string id, ConnectionState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var status = (await client.CallAsync<AgentSnapshot>("status", cancellationToken: timeout.Token)).Connections.Single(c => c.ProfileId == id);
            if (status.State == expected) return status;
            if (status.State == ConnectionState.Failed) Assert.Fail(status.ErrorCode);
            await Task.Delay(25, timeout.Token);
        }
    }

    [Fact]
    public async Task PendingLoginCannotBeRelabeledOrReplacedAndKeepsItsMode()
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = await fixture.Client.CallAsync<AccountProfile>("account.save", new AccountProfile { Provider = AccountProvider.MicrosoftCli, CliLoginMode = CliLoginMode.Browser });
        var attempt = await fixture.Client.CallAsync<LoginStatus>("account.login", new { id = account.Id });
        Assert.Equal(CliLoginMode.Browser, attempt.RequestedMode); Assert.NotNull(attempt.StartedAt);
        var edit = await Assert.ThrowsAsync<ToolkitException>(() => fixture.Client.CallAsync<AccountProfile>("account.save", account with { CliLoginMode = CliLoginMode.WindowsIntegrated }));
        Assert.Equal("LOGIN_IN_PROGRESS", edit.Code);
        var duplicate = await Assert.ThrowsAsync<ToolkitException>(() => fixture.Client.CallAsync<LoginStatus>("account.login", new { id = account.Id }));
        Assert.Equal("LOGIN_IN_PROGRESS", duplicate.Code);
        await fixture.Client.CallAsync<bool>("account.cancel", new { id = account.Id });
        var cancelled = Assert.Single((await fixture.Client.CallAsync<AgentSnapshot>("status")).Logins);
        Assert.Equal("Cancelled", cancelled.State); Assert.Equal(attempt.StartedAt, cancelled.StartedAt);
        Assert.Equal(CliLoginMode.Browser, cancelled.RequestedMode); Assert.Null(cancelled.Challenge);
        await fixture.Client.CallAsync<AccountProfile>("account.save", account with { CliLoginMode = CliLoginMode.WindowsIntegrated });
        Assert.Empty((await fixture.Client.CallAsync<AgentSnapshot>("status")).Logins);
    }

    [Fact]
    public async Task DesktopLoginUsesBoundedRequestAndAgentPersistence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = await fixture.Client.CallAsync<AccountProfile>("account.save", new AccountProfile { Provider = AccountProvider.MicrosoftCli, CliLoginMode = CliLoginMode.Browser });
        var status = await fixture.Client.LoginFromDesktopAsync(account.Id, (request, _) =>
        {
            Assert.Equal(new DesktopLoginRequest(AccountProvider.MicrosoftCli, CliLoginMode.Browser), request);
            return Task.CompletedTask;
        });
        Assert.Equal("Completed", status.State);
        var snapshot = await fixture.Client.CallAsync<AgentSnapshot>("status");
        Assert.Equal("Authenticated", Assert.Single(snapshot.Accounts).Status);
        Assert.Equal("fixture-identity", snapshot.Accounts[0].StableIdentityId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DesktopDisconnectAndAgentCancellationReleaseLoginAndKeepAgentAlive(bool closeDesktop)
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = await fixture.Client.CallAsync<AccountProfile>("account.save", new AccountProfile { Provider = AccountProvider.MicrosoftCli, CliLoginMode = CliLoginMode.Browser });
        using var close = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = fixture.Client.LoginFromDesktopAsync(account.Id, async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped.TrySetResult(); }
        }, close.Token);
        await started.Task.WaitAsync(timeout.Token);
        var editing = await Assert.ThrowsAsync<ToolkitException>(() => fixture.Client.CallAsync<AccountProfile>("account.save", account with { CliLoginMode = CliLoginMode.DeviceCode }));
        Assert.Equal("LOGIN_IN_PROGRESS", editing.Code);
        if (closeDesktop)
        {
            await close.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        }
        else
        {
            await fixture.Client.CallAsync<bool>("account.cancel", new { id = account.Id });
            Assert.Equal("Cancelled", (await login.WaitAsync(timeout.Token)).State);
        }
        await stopped.Task.WaitAsync(timeout.Token);
        AgentSnapshot snapshot;
        do
        {
            snapshot = await fixture.Client.CallAsync<AgentSnapshot>("status", cancellationToken: timeout.Token);
            if (snapshot.Logins.Single().State == "Cancelled") break;
            await Task.Delay(20, timeout.Token);
        } while (true);
        Assert.Null(snapshot.Accounts[0].StableIdentityId);
        await fixture.Client.CallAsync<AccountProfile>("account.save", account with { CliLoginMode = CliLoginMode.DeviceCode });
    }

    [Fact]
    public async Task DesktopErrorCannotExposeRawOutputInAgentState()
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = await fixture.Client.CallAsync<AccountProfile>("account.save", new AccountProfile { Provider = AccountProvider.MicrosoftCli, CliLoginMode = CliLoginMode.Browser });
        var result = await fixture.Client.LoginFromDesktopAsync(account.Id, (_, _) => throw new ToolkitException("fictional-secret-private-output"));
        Assert.Equal("Failed", result.State); Assert.Equal("DESKTOP_LOGIN_FAILED", result.ErrorCode);
        Assert.DoesNotContain("fictional-secret", JsonSerializer.Serialize(await fixture.Client.CallAsync<AgentSnapshot>("status")));
    }

    [Fact]
    public async Task DesktopUnsupportedAccountCannotStartCli()
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = await fixture.Client.CallAsync<AccountProfile>("account.save", new AccountProfile { Provider = AccountProvider.GitHub });
        var error = await Assert.ThrowsAsync<ToolkitException>(() => fixture.Client.LoginFromDesktopAsync(account.Id, (_, _) => throw new InvalidOperationException("Must not run")));
        Assert.Equal("DESKTOP_LOGIN_UNSUPPORTED", error.Code);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "dtk-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource stop = new();
        private SqliteStateStore store = null!;
        private CredentialVault vault = null!;
        private AgentRuntime runtime = null!;
        private Task server = Task.CompletedTask;
        public ToolkitClient Client { get; private set; } = null!;
        public EchoTunnelService Service { get; } = new();

        public static async Task<Fixture> CreateAsync()
        {
            var instance = new Fixture();
            var paths = new LocalPaths(instance.directory);
            instance.store = new(paths);
            instance.vault = new(paths, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            instance.runtime = new(instance.store, instance.vault, new FakeAuth(), instance.Service, new("Test", true, false, false, false));
            await instance.runtime.InitializeAsync();
            instance.server = new ToolkitServer(paths, instance.runtime.DispatchAsync, instance.runtime.Snapshot, instance.stop.Cancel, instance.runtime.LoginFromDesktopAsync).RunAsync(instance.stop.Token);
            instance.Client = new(instance.directory);
            await instance.Client.CallAsync<AgentSnapshot>("status");
            return instance;
        }
        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await server;
            await runtime.DisposeAsync();
            await store.DisposeAsync();
            vault.Dispose();
            stop.Dispose();
            Directory.Delete(directory, true);
        }
    }

    private sealed class FakeAuth : IAuthenticationService, IDesktopAuthenticationService
    {
        public async Task<AccountProfile> LoginFromDesktopAsync(AccountProfile account, Func<DesktopLoginRequest, CancellationToken, Task> authenticate, CancellationToken cancellationToken)
        {
            await authenticate(new(account.Provider, account.CliLoginMode), cancellationToken);
            return account with { Status = "Authenticated", StableIdentityId = "fixture-identity", AuthenticatedAt = DateTimeOffset.UtcNow };
        }
        public async Task<AccountProfile> LoginAsync(AccountProfile account, Action<LoginChallenge> onChallenge, CancellationToken cancellationToken)
        {
            onChallenge(new("https://example.invalid/device", "TEST-CODE", DateTimeOffset.UtcNow.AddMinutes(1)));
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return account;
        }
        public Task<AccountProfile> ValidateAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult(account);
        public Task LogoutAsync(AccountProfile account, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> GetAuthorizationAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult("fixture-secret");
    }

    private sealed class EchoTunnelService : ITunnelService
    {
        public int Connections;
        public Task<DiscoveredTunnel[]> DiscoverAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult<DiscoveredTunnel[]>([new("fixture-tunnel", "usw2", null, [new(3140)])]);
        public Task<DiscoveredTunnel> GetAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken) => Task.FromResult(new DiscoveredTunnel(profile.TunnelId, profile.ClusterId, null, [new(3140)]));
        public Task<IRelayConnection> ConnectAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Connections);
            return Task.FromResult<IRelayConnection>(new EchoRelay());
        }
    }

    private sealed class EchoRelay : IRelayConnection
    {
        public event Action<ConnectionState>? StateChanged { add { } remove { } }
        public async Task<Stream> OpenStreamAsync(int remotePort, CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var peer = new TcpClient();
            var connected = peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint, cancellationToken);
            var remote = await listener.AcceptTcpClientAsync(cancellationToken);
            await connected;
            listener.Stop();
            _ = Task.Run(async () =>
            {
                using (remote)
                {
                    try
                    {
                        var stream = remote.GetStream();
                        var buffer = new byte[4096];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0) await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException) { }
                }
            }, CancellationToken.None);
            return peer.GetStream();
        }
        public Task RefreshPortsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
