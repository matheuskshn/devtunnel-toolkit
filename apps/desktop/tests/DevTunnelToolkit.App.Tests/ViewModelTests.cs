using System.Runtime.CompilerServices;
using DevTunnelToolkit.App;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App.Tests;

public sealed class ViewModelTests
{
    [Theory]
    [InlineData("proxy.test")]
    [InlineData("pac.preview")]
    [InlineData("agent.shutdown")]
    public async Task AuxiliaryOperationsReportLoadingAndReleaseItOnTimeout(string operation)
    {
        var gateway = new FakeGateway();
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Handler = (op, _, _) => op == operation ? completion.Task : Task.FromResult<object>(gateway.State);
        using var vm = new ToolkitViewModel(gateway); await vm.RefreshAsync();
        Task pending = operation switch
        {
            "proxy.test" => vm.TestRouteAsync("example.org", 443),
            "pac.preview" => vm.PreviewPacAsync(new()),
            _ => vm.ShutdownAgentAsync()
        };
        Assert.True(vm.IsBusy); Assert.True(vm.WorkspaceActivity!.Running);
        completion.SetException(new OperationCanceledException("private-fixture-detail"));
        await pending;
        Assert.False(vm.IsBusy); Assert.False(vm.WorkspaceActivity!.Running);
        Assert.Contains("Tempo de espera esgotado", vm.Message);
        Assert.DoesNotContain("private-fixture-detail", vm.Message);
    }

    [Fact]
    public async Task AccountLoadingIsScopedAndLoginClockSurvivesAnotherSnapshot()
    {
        var gateway = new FakeGateway();
        var account = gateway.State.Accounts[0];
        var start = DateTimeOffset.UtcNow.AddSeconds(-40);
        gateway.State = gateway.State with { Logins = [new(account.Id, "Pending", new("https://github.com/login/device", "TEST-CODE", null), StartedAt: start)] };
        using var vm = new ToolkitViewModel(gateway); await vm.RefreshAsync();
        Assert.True(vm.AccountActivity(account.Id)!.Running);
        Assert.Null(vm.AccountActivity("other-account"));
        await vm.RefreshAsync(); Assert.Equal(start, vm.AccountActivity(account.Id)!.StartedAt);
        gateway.State = gateway.State with { Logins = [new(account.Id, "Cancelled", StartedAt: start)] };
        await vm.RefreshAsync(); Assert.Null(vm.AccountActivity(account.Id)); Assert.Null(vm.WorkspaceActivity);
    }

    [Fact]
    public async Task SlowDiscoveryReportsActivityAndRejectsDuplicateOperation()
    {
        var gateway = new FakeGateway();
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Handler = (op, _, _) => op == "account.discover" ? completion.Task : Task.FromResult<object>(gateway.State);
        using var vm = new ToolkitViewModel(gateway);
        await vm.RefreshAsync();
        var pending = vm.DiscoverAsync(gateway.State.Accounts[0].Id);
        Assert.True(vm.IsBusy); Assert.NotNull(vm.OperationStartedAt);
        Assert.Contains("Consultando túneis", vm.Message);
        Assert.False(await vm.DiscoverAsync(gateway.State.Accounts[0].Id));
        completion.SetResult(Array.Empty<DiscoveredTunnel>());
        Assert.True(await pending); Assert.False(vm.IsBusy); Assert.Null(vm.OperationStartedAt);
        Assert.Single(gateway.Calls, call => call.Operation == "account.discover");
        Assert.Equal("Descoberta concluída.", vm.Message);
    }

    [Fact]
    public async Task FailedOperationAlwaysClearsProgressWithoutExposingRawError()
    {
        using var vm = new ToolkitViewModel(new FakeGateway());
        Assert.False(await vm.RunAsync(_ => throw new IOException("private-detail"), "Success", "Carregando…"));
        Assert.False(vm.IsBusy); Assert.Null(vm.OperationStartedAt); Assert.DoesNotContain("private-detail", vm.Message);
    }
    [Fact]
    public async Task OpeningDesktopStartsAgentOnceBeforeWatching()
    {
        var gateway = new FakeGateway();
        var starts = 0;
        using var vm = new ToolkitViewModel(gateway, ensureAgent: _ =>
        {
            Assert.Empty(gateway.Calls); starts++; return Task.CompletedTask;
        });
        await vm.StartAsync(); await vm.StartAsync();
        Assert.Equal(1, starts); Assert.True(vm.IsConnected);
    }

    [Fact]
    public async Task StartupReusesCompatibleAgentAndDoesNotReplaceIncompatiblePeer()
    {
        var gateway = new FakeGateway();
        var launcher = new AgentLauncher(gateway);
        await launcher.EnsureStartedAsync(TestContext.Current.CancellationToken);
        Assert.Equal("status", Assert.Single(gateway.Calls).Operation);
        gateway.Error = new ToolkitException("IPC_VERSION_MISMATCH");
        var error = await Assert.ThrowsAsync<ToolkitException>(() => launcher.EnsureStartedAsync(TestContext.Current.CancellationToken));
        Assert.Equal("IPC_VERSION_MISMATCH", error.Code);
    }

    [Fact]
    public async Task DesktopLoginDoesNotBlockCancelAndClosingDoesNotShutdownAgent()
    {
        var gateway = new FakeGateway();
        var account = new AccountProfile { Provider = AccountProvider.MicrosoftCli, CliLoginMode = CliLoginMode.Browser };
        gateway.State = gateway.State with { Accounts = [account] };
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.DesktopLogin = async (id, token) =>
        {
            Assert.Equal(account.Id, id);
            try { await Task.Delay(Timeout.Infinite, token); return new(id, "Completed"); }
            finally { stopped.TrySetResult(); }
        };
        using var vm = new ToolkitViewModel(gateway, isWindows: true);
        await vm.RefreshAsync();
        Assert.True(await vm.ActionAsync("account.login", account.Id));
        Assert.True(vm.IsDesktopLoginActive(account.Id)); Assert.False(vm.IsBusy);
        Assert.False(await vm.ActionAsync("account.login", account.Id));
        vm.CancelDesktopLogins();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(gateway.Calls, call => call.Operation is "agent.shutdown" or "account.login" or "connections.stop");
    }

    [Theory]
    [InlineData("https://github.com/login/device", true)]
    [InlineData("https://microsoft.com/devicelogin", true)]
    [InlineData("https://login.microsoftonline.com/common/oauth2/deviceauth", true)]
    [InlineData("http://github.com/login/device", false)]
    [InlineData("file:///tmp/anything", false)]
    [InlineData("https://github.com.attacker.example/", false)]
    [InlineData("https://github.com@attacker.example/", false)]
    [InlineData("https://github.com:444/login/device", false)]
    [InlineData("javascript:alert(1)", false)]
    public void LoginLinksOnlyLaunchTrustedHttpsOrigins(string uri, bool expected)
        => Assert.Equal(expected, ToolkitViewModel.IsSafeLoginUri(uri));

    [Fact]
    public async Task SaveMappingUsesIpcAndPreservesOpaqueProfileId()
    {
        var gateway = new FakeGateway();
        using var vm = new ToolkitViewModel(gateway);
        var profile = new TunnelProfile { AccountId = gateway.State.Accounts[0].Id, TunnelId = "desktop-demo", ClusterId = "use2", Mappings = [new() { RemotePort = 3140, LocalPort = 13140, Mode = MappingMode.Manual }] };
        Assert.True(await vm.SaveTunnelAsync(profile));
        var saved = Assert.IsType<TunnelProfile>(gateway.Calls.First(c => c.Operation == "tunnel.save").Payload);
        Assert.Equal(profile.Id, saved.Id);
        Assert.Equal(13140, saved.Mappings[0].LocalPort);
        Assert.True(vm.IsConnected);
    }

    [Fact]
    public async Task InvalidExposureIsRejectedBeforeIpc()
    {
        var gateway = new FakeGateway();
        using var vm = new ToolkitViewModel(gateway);
        var tunnel = new TunnelProfile { AccountId = gateway.State.Accounts[0].Id, TunnelId = "desktop-demo", ClusterId = "use2", Mappings = [new() { RemotePort = 3140, LocalAddress = "0.0.0.0" }] };
        Assert.False(await vm.SaveTunnelAsync(tunnel));
        Assert.Empty(gateway.Calls);
        Assert.Contains("127.0.0.1", vm.Message);
    }

    [Fact]
    public async Task DiscoveryIsBoundToTheSelectedAccount()
    {
        var gateway = new FakeGateway();
        using var vm = new ToolkitViewModel(gateway);
        var id = gateway.State.Accounts[0].Id;
        Assert.True(await vm.DiscoverAsync(id));
        Assert.Equal(id, vm.DiscoveryAccountId);
        Assert.Equal("desktop-demo.use2", Assert.Single(vm.DiscoveredTunnels).CanonicalId);
    }

    [Fact]
    public async Task ArbitraryOperationsNeverReachAgent()
    {
        var gateway = new FakeGateway();
        using var vm = new ToolkitViewModel(gateway);
        Assert.False(await vm.ActionAsync("shell.execute", gateway.State.Accounts[0].Id));
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task IncompatibleAgentIsActionableAndRecoversAfterReplacement()
    {
        var gateway = new FakeGateway { Error = new ToolkitException("IPC_VERSION_MISMATCH") };
        using var vm = new ToolkitViewModel(gateway);
        await vm.RefreshAsync();
        Assert.False(vm.IsConnected);
        Assert.True(vm.RequiresAgentUpdate);
        Assert.Contains("versões incompatíveis", vm.Message);
        Assert.Contains("contas e configurações serão preservadas", vm.Message);
        gateway.Error = null;
        await vm.RefreshAsync();
        Assert.True(vm.IsConnected);
        Assert.False(vm.RequiresAgentUpdate);
    }

    [Fact]
    public async Task AgentVersionChangeDuringSaveDisconnectsTheWorkspace()
    {
        var gateway = new FakeGateway();
        using var vm = new ToolkitViewModel(gateway);
        await vm.RefreshAsync();
        gateway.Error = new ToolkitException("IPC_VERSION_MISMATCH");
        Assert.False(await vm.SaveAccountAsync(new() { Provider = AccountProvider.MicrosoftCli }));
        Assert.False(vm.IsConnected);
        Assert.True(vm.RequiresAgentUpdate);
    }

    [Fact]
    public async Task AgentFailureKeepsSecretsOutOfUi()
    {
        var gateway = new FakeGateway { Error = new InvalidOperationException("secret-token-value") };
        using var vm = new ToolkitViewModel(gateway);
        await vm.RefreshAsync();
        Assert.False(vm.IsConnected);
        Assert.DoesNotContain("secret-token-value", vm.Message);
    }
}

internal sealed class FakeGateway : IToolkitGateway, IDesktopLoginGateway
{
    public Func<string, object?, CancellationToken, Task<object>>? Handler { get; set; }
    public Func<string, CancellationToken, Task<LoginStatus>>? DesktopLogin { get; set; }
    public Task<LoginStatus> LoginFromDesktopAsync(string id, CancellationToken cancellationToken)
        => DesktopLogin?.Invoke(id, cancellationToken) ?? throw new ToolkitException("DESKTOP_LOGIN_UNSUPPORTED");
    public Exception? Error { get; set; }
    public System.Collections.Concurrent.ConcurrentQueue<(string Operation, object? Payload)> Calls { get; } = new();
    public AgentSnapshot State { get; set; } = new(
        [new AccountProfile { DisplayName = "Demo account", Provider = AccountProvider.Microsoft, Status = "Authenticated", ClientId = "test-client" }],
        [], [], [], new AppSettings(), new PlatformCapabilities("Test", true, false, false, false), []);

    public async Task<T> CallAsync<T>(string operation, object? payload = null, CancellationToken cancellationToken = default)
    {
        if (Error is not null) throw Error;
        Calls.Enqueue((operation, payload));
        if (Handler is not null) return (T)await Handler(operation, payload, cancellationToken);
        object response = operation switch
        {
            "status" => State,
            "account.save" => payload!,
            "settings.save" => payload!,
            "tunnel.save" => payload!,
            "account.discover" => new[] { new DiscoveredTunnel("desktop-demo", "use2", "Demo", [new RemotePort(3140)]) },
            _ => true
        };
        return (T)response;
    }

    public async IAsyncEnumerable<AgentSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Error is not null) throw Error;
        yield return State;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
