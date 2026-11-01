using System.Text.Json;
using DevTunnelToolkit.Core;
using Xunit;

namespace DevTunnelToolkit.DevTunnels.Tests;

public sealed class CliAuthenticationTests
{
    private const string MicrosoftIdentity = """
        {"status":"Logged in","user":{"provider":"microsoft","username":"sample@example.invalid","objectId":"11111111-1111-1111-1111-111111111111","tenantId":"22222222-2222-2222-2222-222222222222"}}
        """;

    [Fact]
    public async Task DesktopCompletionIsIndependentlyValidatedAndCannotChangeIdentity()
    {
        var runner = new FakeCli(MicrosoftIdentity);
        var service = new CliAuthenticationService(runner, true);
        var account = new AccountProfile { Provider = AccountProvider.MicrosoftCli, CliLoginMode = CliLoginMode.Browser };
        var result = await service.LoginFromDesktopAsync(account, (request, _) =>
        {
            Assert.Equal(new DesktopLoginRequest(AccountProvider.MicrosoftCli, CliLoginMode.Browser), request);
            Assert.Empty(runner.Commands); return Task.CompletedTask;
        }, default);
        Assert.Equal("user show --json", Assert.Single(runner.Commands));
        Assert.NotNull(result.AuthenticatedAt); Assert.Equal("Authenticated", result.Status);
        var mismatch = new CliAuthenticationService(new FakeCli(MicrosoftIdentity), true);
        var error = await Assert.ThrowsAsync<ToolkitException>(() => mismatch.LoginFromDesktopAsync(account with { StableIdentityId = "another-identity" }, (_, _) => Task.CompletedTask, default));
        Assert.Equal("CLI_ACCOUNT_CHANGED", error.Code);
    }

    [Fact]
    public async Task DesktopLoginHoldsTheSharedCliGateUntilValidation()
    {
        var runner = new FakeCli(MicrosoftIdentity, MicrosoftIdentity);
        var service = new CliAuthenticationService(runner, true);
        var account = new AccountProfile { Provider = AccountProvider.MicrosoftCli, CliLoginMode = CliLoginMode.Browser };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = service.LoginFromDesktopAsync(account, (_, token) => release.Task.WaitAsync(token), default);
        var validation = service.ValidateAsync(account, default);
        Assert.Empty(runner.Commands); Assert.False(validation.IsCompleted);
        release.SetResult(); await login; await validation;
        Assert.Equal(2, runner.Commands.Count);
    }

    [Theory]
    [InlineData(false, AccountProvider.MicrosoftCli, CliLoginMode.Browser)]
    [InlineData(true, AccountProvider.GitHubCli, CliLoginMode.Browser)]
    [InlineData(true, AccountProvider.MicrosoftCli, CliLoginMode.DeviceCode)]
    public async Task UnsupportedDesktopRequestsNeverRun(bool windows, AccountProvider provider, CliLoginMode mode)
    {
        var service = new CliAuthenticationService(new FakeCli(), windows);
        await Assert.ThrowsAsync<ToolkitException>(() => service.LoginFromDesktopAsync(new() { Provider = provider, CliLoginMode = mode }, (_, _) => throw new InvalidOperationException(), default));
    }

    [Fact]
    public async Task MicrosoftCliLoginPublishesOnlyDeviceChallengeAndStableIdentity()
    {
        var runner = new FakeCli(
            "To sign in, use a web browser to open the page https://login.microsoft.com/device and enter the code ABC123456 to authenticate.\n",
            MicrosoftIdentity);
        var service = new CliAuthenticationService(runner);
        LoginChallenge? challenge = null;
        var account = await service.LoginAsync(new AccountProfile { Provider = AccountProvider.MicrosoftCli }, value => challenge = value, default);
        Assert.Equal("https://login.microsoft.com/device", challenge?.VerificationUri);
        Assert.Equal("ABC123456", challenge?.UserCode);
        Assert.Null(challenge?.ExpiresAt);
        Assert.Equal("sample@example.invalid", account.Username);
        Assert.Equal("microsoft:22222222-2222-2222-2222-222222222222:11111111-1111-1111-1111-111111111111", account.StableIdentityId);
        Assert.Null(account.ExpiresAt);
        Assert.DoesNotContain("ABC123456", JsonSerializer.Serialize(account));
        Assert.Equal("user login --entra --use-device-code-auth --json", runner.Commands[0]);
    }

    [Fact]
    public async Task ChangedCliIdentityCannotIssueTokenForSavedProfile()
    {
        var other = """
            {"status":"Logged in","user":{"provider":"microsoft","username":"other@example.invalid","objectId":"99999999-1111-1111-1111-111111111111","tenantId":"22222222-2222-2222-2222-222222222222"}}
            """;
        var runner = new FakeCli(other);
        var account = new AccountProfile { Provider = AccountProvider.MicrosoftCli, StableIdentityId = "microsoft:22222222-2222-2222-2222-222222222222:11111111-1111-1111-1111-111111111111" };
        var error = await Assert.ThrowsAsync<ToolkitException>(() => new CliAuthenticationService(runner).RunAsAsync(account, ["token", "example.use2", "--scopes", "connect", "--json"], default));
        Assert.Equal("CLI_ACCOUNT_CHANGED", error.Code);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task SharedCliSessionIsCheckedBeforeAndAfterScopedOperation()
    {
        var runner = new FakeCli(MicrosoftIdentity, "{\"token\":\"fictional-scoped-token\"}", MicrosoftIdentity);
        var account = new AccountProfile { Provider = AccountProvider.MicrosoftCli, StableIdentityId = "microsoft:22222222-2222-2222-2222-222222222222:11111111-1111-1111-1111-111111111111" };
        var response = await new CliAuthenticationService(runner).RunAsAsync(account, ["token", "example.use2", "--scopes", "connect", "--json"], default);
        Assert.Contains("fictional-scoped-token", response);
        Assert.Equal(new[] { "user show --json", "token example.use2 --scopes connect --json", "user show --json" }, runner.Commands);
    }

    [Fact]
    public void CliJsonAndTunnelListAcceptBannerAndKnownShapesButRejectUnknownSchema()
    {
        using var json = OfficialCliJson.Parse("Welcome to dev tunnels!\n\n{\"tunnels\":[{\"tunnelId\":\"example\",\"clusterId\":\"use2\"},{\"tunnel\":{\"tunnelId\":\"sample.use2\"}}]}");
        Assert.Equal(new[] { ("example", "use2"), ("sample", "use2") }, CliTunnelService.ParseTunnelKeys(json.RootElement));
        using var invalid = JsonDocument.Parse("{\"items\":[]}");
        Assert.Equal("CLI_TUNNEL_LIST_SCHEMA_UNSUPPORTED", Assert.Throws<ToolkitException>(() => CliTunnelService.ParseTunnelKeys(invalid.RootElement)).Code);
    }

    [Fact]
    public async Task DiscoveryUsesCliForIdentityAndScopedTokenButSdkAdapterForTunnelDetails()
    {
        const string token = "fictional-connect-token-123456789";
        var runner = new FakeCli(
            MicrosoftIdentity,
            "{\"tunnels\":[{\"tunnelId\":\"example\",\"clusterId\":\"use2\"}]}",
            MicrosoftIdentity,
            MicrosoftIdentity,
            "{\"token\":\"" + token + "\"}",
            MicrosoftIdentity);
        var auth = new CliAuthenticationService(runner);
        var native = new FakeTunnelService();
        var scoped = new FakeTunnelService();
        var service = new CliTunnelService(native, auth, issued =>
        {
            Assert.Equal(token, issued);
            return scoped;
        });
        var account = new AccountProfile { Provider = AccountProvider.MicrosoftCli, StableIdentityId = "microsoft:22222222-2222-2222-2222-222222222222:11111111-1111-1111-1111-111111111111" };
        var found = await service.DiscoverAsync(account, default);
        Assert.Single(found);
        Assert.Equal("example.use2", found[0].CanonicalId);
        Assert.Equal(3140, found[0].Ports[0].PortNumber);
        Assert.Equal(0, native.Calls);
        Assert.Equal(1, scoped.Calls);
        Assert.Equal(AccountProvider.TunnelToken, scoped.LastAccount?.Provider);
        Assert.Equal("token example.use2 --scopes connect --json", runner.Commands[4]);
    }

    [Fact]
    public async Task ForgettingCliProfileDoesNotLogOutSharedOsSession()
    {
        var runner = new FakeCli();
        await new CliAuthenticationService(runner).LogoutAsync(new AccountProfile { Provider = AccountProvider.GitHubCli }, default);
        Assert.Empty(runner.Commands);
    }

    [Theory]
    [InlineData(CliLoginMode.DeviceCode, "--use-device-code-auth")]
    [InlineData(CliLoginMode.Browser, "--use-browser-auth")]
    [InlineData(CliLoginMode.WindowsIntegrated, "--use-integrated-windows-auth")]
    public async Task MicrosoftModesUseOfficialFlagsAndPreserveIdentity(CliLoginMode mode, string flag)
    {
        var runner = new FakeCli("Logged in", MicrosoftIdentity);
        var service = new CliAuthenticationService(runner, isWindows: true);
        var account = await service.LoginAsync(new AccountProfile { Provider = AccountProvider.MicrosoftCli, CliLoginMode = mode }, _ => Assert.Fail("No challenge expected"), default);
        Assert.Equal($"user login --entra {flag} --json", runner.Commands[0]);
        Assert.Equal(mode, account.CliLoginMode);
        Assert.NotNull(account.StableIdentityId);
    }

    [Fact]
    public async Task IntegratedWindowsFailsBeforeInvokingCliOnLinux()
    {
        var runner = new FakeCli();
        var service = new CliAuthenticationService(runner, isWindows: false);
        var error = await Assert.ThrowsAsync<ToolkitException>(() => service.LoginAsync(new AccountProfile { Provider = AccountProvider.MicrosoftCli, CliLoginMode = CliLoginMode.WindowsIntegrated }, _ => { }, default));
        Assert.Equal("WINDOWS_AUTH_UNAVAILABLE", error.Code); Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task IntegratedWindowsCannotBeUsedForGitHubOrSilentlyFallBack()
    {
        var runner = new FakeCli();
        var service = new CliAuthenticationService(runner, isWindows: true);
        var error = await Assert.ThrowsAsync<ToolkitException>(() => service.LoginAsync(new AccountProfile { Provider = AccountProvider.GitHubCli, CliLoginMode = CliLoginMode.WindowsIntegrated }, _ => { }, default));
        Assert.Equal("INVALID_CLI_LOGIN_MODE", error.Code); Assert.Empty(runner.Commands);
        var failing = new FailedCli();
        await Assert.ThrowsAsync<ToolkitException>(() => new CliAuthenticationService(failing, true).LoginAsync(new AccountProfile { Provider = AccountProvider.MicrosoftCli, CliLoginMode = CliLoginMode.WindowsIntegrated }, _ => { }, default));
        Assert.Equal(1, failing.Attempts);
    }

    [Theory]
    [InlineData(CliLoginMode.Browser, "", "AUTH_FLOW_CHANGED")]
    [InlineData(CliLoginMode.WindowsIntegrated, "", "AUTH_FLOW_CHANGED")]
    [InlineData(CliLoginMode.Browser, "A window handle must be configured.\nFalling back to device code authentication.\n", "AUTH_WINDOW_UNAVAILABLE")]
    public async Task CliDeviceFallbackDoesNotReplaceSelectedFlow(CliLoginMode mode, string prefix, string expected)
    {
        var runner = new FakeCli(prefix + "To sign in, use a web browser to open the page https://login.microsoft.com/device and enter the code ABC123456 to authenticate.\n");
        var error = await Assert.ThrowsAsync<ToolkitException>(() => new CliAuthenticationService(runner, true).LoginAsync(
            new AccountProfile { Provider = AccountProvider.MicrosoftCli, CliLoginMode = mode }, _ => Assert.Fail("Must not publish fallback challenge"), default));
        Assert.Equal(expected, error.Code); Assert.Single(runner.Commands);
        Assert.DoesNotContain("ABC123456", error.ToString());
    }

    [Fact]
    public async Task GitHubBrowserCanRetainItsProviderDeviceChallenge()
    {
        var runner = new FakeCli("To sign in, use a web browser to open the page https://github.com/login/device and enter the code ABCD-1234 to authenticate.\n",
            """{"status":"Logged in","user":{"provider":"github","username":"fixture","id":"12345"}}""");
        LoginChallenge? challenge = null;
        var account = await new CliAuthenticationService(runner, true).LoginAsync(
            new AccountProfile { Provider = AccountProvider.GitHubCli, CliLoginMode = CliLoginMode.Browser }, value => challenge = value, default);
        Assert.NotNull(challenge); Assert.Equal("github:12345", account.StableIdentityId);
    }

    private sealed class FailedCli : IOfficialCli
    {
        public int Attempts;
        public Task<string> RunAsync(string[] args, Action<string>? output, CancellationToken token)
        {
            Attempts++;
            throw new ToolkitException("WINDOWS_AUTH_FAILED");
        }
    }

    private sealed class FakeCli(params string[] responses) : IOfficialCli
    {
        private readonly Queue<string> responses = new(responses);
        public List<string> Commands { get; } = [];
        public Task<string> RunAsync(string[] arguments, Action<string>? onOutput, CancellationToken cancellationToken)
        {
            Commands.Add(string.Join(' ', arguments));
            var response = responses.Dequeue();
            onOutput?.Invoke(response[..(response.Length / 2)]);
            onOutput?.Invoke(response[(response.Length / 2)..]);
            return Task.FromResult(response);
        }
    }

    private sealed class FakeTunnelService : ITunnelService
    {
        public int Calls { get; private set; }
        public AccountProfile? LastAccount { get; private set; }
        public Task<DiscoveredTunnel[]> DiscoverAsync(AccountProfile account, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected native discovery");
        public Task<DiscoveredTunnel> GetAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken)
        {
            Calls++;
            LastAccount = account;
            return Task.FromResult(new DiscoveredTunnel(profile.TunnelId, profile.ClusterId, null, [new RemotePort(3140)]));
        }
        public Task<IRelayConnection> ConnectAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken) => throw new InvalidOperationException("Connection is not part of this test");
    }
}
