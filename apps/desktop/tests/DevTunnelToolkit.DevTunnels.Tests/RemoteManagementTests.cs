using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DevTunnelToolkit.Core;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;
using Xunit;

namespace DevTunnelToolkit.DevTunnels.Tests;

public sealed class RemoteManagementTests
{
    [Fact]
    public async Task SdkContractAcceptsSyntheticTarget()
    {
        using var fixture = new Fixture();
        await using var client = new TunnelManagementClient(new ProductInfoHeaderValue("Test", "1.0"), () => Task.FromResult<AuthenticationHeaderValue?>(new("Bearer", "fictional")),
            new Uri("https://global.rel.tunnels.api.visualstudio.com/"), fixture);
        await client.GetTunnelAsync(new() { TunnelId = Target.TunnelId, ClusterId = Target.ClusterId }, null, default);
    }
    private static readonly RemoteTunnelTarget Target = new("sample-tunnel", "use2");
    private static readonly AccountProfile Account = new() { Provider = AccountProvider.Microsoft, DisplayName = "Test", StableIdentityId = "microsoft:fictional" };
    private static readonly JsonSerializerOptions Json = TunnelContracts.JsonOptions;

    [Fact]
    public async Task ReadReturnsOnlyMetadataNeverServerTokens()
    {
        var fixture = new Fixture();
        fixture.Tunnel.AccessTokens = new Dictionary<string, string> { ["manage"] = "fictional-sensitive-access-token" };
        fixture.Tunnel.Expiration = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await fixture.Service.GetAsync(Account, Target, default);
        Assert.Equal(Target, result.Target);
        Assert.Equal(2030, result.ExpiresAt?.Year);
        Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(result));
        Assert.Contains("includePorts=true", Assert.Single(fixture.Requests).Uri);
    }

    [Fact]
    public async Task UpdatePreservesUnchangedServerAccessAndPortConfiguration()
    {
        var fixture = new Fixture();
        fixture.Tunnel.AccessControl = new([UnknownEntry()]);
        fixture.Tunnel.Ports = [new() { PortNumber = 8443, Protocol = "https", Description = "Preserve" }];
        var result = await fixture.Service.UpdateAsync(Account, Target, new() { Name = "sample-alias", Description = "Updated", Labels = ["test"], ExpirationSeconds = 7200 }, default);
        Assert.Equal("Updated", result.Description);
        Assert.Equal("future-provider", Assert.Single(result.Access).Provider);
        Assert.Equal(7200, result.ExpirationSeconds);
        Assert.Equal("Preserve", Assert.Single(result.Ports).Description);
        Assert.DoesNotContain("accessTokens", fixture.Requests.Single(item => item.Method == HttpMethod.Put).Body!);
    }

    [Theory]
    [InlineData("", "REMOTE_CONFIRMATION_REQUIRED")]
    [InlineData("another-tunnel.use2", "REMOTE_CONFIRMATION_REQUIRED")]
    [InlineData("sample-tunnel.use2 ", "REMOTE_CONFIRMATION_REQUIRED")]
    public async Task DestructiveActionsRequireExactTargetBeforeAnyNetwork(string confirmation, string code)
    {
        var fixture = new Fixture();
        var error = await Assert.ThrowsAsync<ToolkitException>(() => fixture.Service.DeleteAsync(Account, Target, confirmation, default));
        Assert.Equal(code, error.Code);
        Assert.Empty(fixture.Requests);
        await Assert.ThrowsAsync<ToolkitException>(() => fixture.Service.DeletePortAsync(Account, Target, 3140, confirmation, default));
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task DeletionReadsAndValidatesCanonicalTargetBeforeDelete()
    {
        var fixture = new Fixture();
        fixture.Tunnel.TunnelId = "wrong-target";
        var error = await Assert.ThrowsAsync<ToolkitException>(() => fixture.Service.DeleteAsync(Account, Target, Target.CanonicalId, default));
        Assert.Equal("REMOTE_TARGET_MISMATCH", error.Code);
        Assert.DoesNotContain(fixture.Requests, item => item.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task IdsCannotBeRenamedThroughMetadataUpdate()
    {
        var fixture = new Fixture();
        var error = await Assert.ThrowsAsync<ToolkitException>(() => fixture.Service.UpdateAsync(Account, Target, new() { TunnelId = "other-tunnel" }, default));
        Assert.Equal("REMOTE_ID_IMMUTABLE", error.Code);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task AccessAdditionPreservesUnknownRulesAndRemovalRejectsInheritedAndStaleEntries()
    {
        var fixture = new Fixture();
        var inherited = UnknownEntry(); inherited.IsInherited = true;
        fixture.Tunnel.AccessControl = new([inherited]);
        var result = await fixture.Service.AddAccessAsync(Account, Target, null, new() { Type = "Anonymous", Scopes = ["connect"] }, default);
        Assert.Equal(2, result.Length);
        Assert.Equal("future-provider", result[0].Provider);
        Assert.True(result[0].IsInherited);
        Assert.Equal("REMOTE_ACCESS_INHERITED", (await Assert.ThrowsAsync<ToolkitException>(() => fixture.Service.RemoveAccessAsync(Account, Target, null, result[0].EntryId, Target.CanonicalId, default))).Code);
        Assert.Equal("REMOTE_ACCESS_CHANGED_REFRESH_REQUIRED", (await Assert.ThrowsAsync<ToolkitException>(() => fixture.Service.RemoveAccessAsync(Account, Target, null, new string('0', 64), Target.CanonicalId, default))).Code);
        var remaining = await fixture.Service.RemoveAccessAsync(Account, Target, null, result[1].EntryId, Target.CanonicalId, default);
        Assert.True(Assert.Single(remaining).IsInherited);
    }

    [Fact]
    public async Task ResetDropsOnlyExplicitEntriesAndRetainsInheritedPolicies()
    {
        var fixture = new Fixture();
        var inherited = UnknownEntry(); inherited.IsInherited = true;
        fixture.Tunnel.AccessControl = new([inherited, new() { Type = TunnelAccessControlEntryType.Anonymous, Scopes = ["connect"] }]);
        var result = await fixture.Service.ResetAccessAsync(Account, Target, null, Target.CanonicalId, default);
        Assert.True(Assert.Single(result).IsInherited);
    }

    [Theory]
    [InlineData("manage")]
    [InlineData("host")]
    [InlineData("manage:ports")]
    public void AnonymousCannotGrantAdministrationOrHost(string scope)
    {
        var error = Assert.Throws<ToolkitException>(() => RemoteTunnelManagement.ToEntry(new() { Type = "Anonymous", Scopes = [scope] }, false));
        Assert.Equal("REMOTE_ANONYMOUS_CONNECT_ONLY", error.Code);
    }

    [Fact]
    public async Task PortUpdateRetainsAccessAndChecksCreateVersusEdit()
    {
        var fixture = new Fixture();
        fixture.Port = new() { PortNumber = 3140, Protocol = "auto", Name = "proxy-port", AccessControl = new([UnknownEntry()]) };
        var updated = await fixture.Service.SavePortAsync(Account, Target, new(3140, "tcp", "Proxy"), false, default);
        Assert.Equal("future-provider", Assert.Single(updated.Access).Provider);
        Assert.Equal("proxy-port", fixture.Port.Name);
        Assert.Equal("REMOTE_PORT_EXISTS", (await Assert.ThrowsAsync<ToolkitException>(() => fixture.Service.SavePortAsync(Account, Target, new(3140), true, default))).Code);
    }

    [Fact]
    public async Task PortAccessChangesDoNotModifyTunnelAcl()
    {
        var fixture = new Fixture();
        fixture.Tunnel.AccessControl = new([UnknownEntry()]);
        fixture.Port = new() { PortNumber = 3140, Protocol = "tcp" };
        var result = await fixture.Service.AddAccessAsync(Account, Target, 3140, new() { Type = "Anonymous" }, default);
        Assert.Equal("Anonymous", Assert.Single(result).Type);
        Assert.Equal("future-provider", Assert.Single(fixture.Tunnel.AccessControl.Entries).Provider);
        Assert.All(fixture.Requests.Where(item => item.Method == HttpMethod.Put), request => Assert.Contains("/ports/3140", request.Uri));
    }

    [Fact]
    public async Task TokenIssuanceUsesCombinedScopedTokenAndReportsObservedExpirationOnly()
    {
        var fixture = new Fixture();
        var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"exp\":{expires}}}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = header + "." + payload + ".fictional";
        fixture.Tunnel.AccessTokens = new Dictionary<string, string> { ["connect inspect"] = token };
        var result = await fixture.Service.IssueTokenAsync(Account, Target, new(["connect", "inspect"]), default);
        Assert.Equal(token, result.Token);
        Assert.Equal(expires, result.ExpiresAt?.ToUnixTimeSeconds());
        Assert.Equal("TokenClaim", result.ExpirationSource);
        Assert.Contains("tokenScopes=connect", Assert.Single(fixture.Requests).Uri);
        Assert.Null(RemoteTunnelManagement.TokenExpiration("opaque-token-without-expiration"));
    }

    [Fact]
    public async Task MissingRequestedTokenFailsClosedAndAccountTokensCannotManage()
    {
        var fixture = new Fixture();
        Assert.Equal("REMOTE_TOKEN_SCOPE_DENIED", (await Assert.ThrowsAsync<ToolkitException>(() => fixture.Service.IssueTokenAsync(Account, Target, new(["host"]), default))).Code);
        var previous = fixture.Requests.Count;
        Assert.Equal("REMOTE_ACCOUNT_REQUIRED", (await Assert.ThrowsAsync<ToolkitException>(() => fixture.Service.GetAsync(Account with { Provider = AccountProvider.TunnelToken }, Target, default))).Code);
        Assert.Equal(previous, fixture.Requests.Count);
    }

    [Theory]
    [InlineData(401, "AUTHENTICATION_REQUIRED")]
    [InlineData(403, "REMOTE_ACCESS_DENIED")]
    [InlineData(409, "REMOTE_CONFLICT_REFRESH_REQUIRED")]
    [InlineData(429, "REMOTE_RATE_LIMITED")]
    [InlineData(500, "REMOTE_SERVICE_UNAVAILABLE")]
    public async Task RemoteErrorsNeverReturnRawProviderBodies(int status, string code)
    {
        var fixture = new Fixture { Status = (HttpStatusCode)status };
        var error = await Assert.ThrowsAsync<ToolkitException>(() => fixture.Service.GetAsync(Account, Target, default));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("private-provider-data", error.ToString());
    }

    [Fact]
    public void CliCreateUsesFixedArgumentsAndNeverAnonymousOrImplicitTarget()
    {
        var arguments = RemoteTunnelManagement.CreateArguments(new() { TunnelId = "sample-tunnel", ClusterId = "use2", Description = "literal ; $(not-a-command)", Labels = ["sample"], ExpirationSeconds = 7200 });
        Assert.Equal(new[] { "create", "sample-tunnel.use2", "--description=literal ; $(not-a-command)", "--labels", "sample", "--expiration", "2h", "--json" }, arguments);
        Assert.DoesNotContain("--allow-anonymous", arguments);
        Assert.Throws<ToolkitException>(() => RemoteTunnelManagement.CreateArguments(new() { Labels = ["--allow-anonymous"] }));
        Assert.Throws<ToolkitException>(() => RemoteTunnelManagement.CreateArguments(new() { ExpirationSeconds = 3601 }));
        Assert.Equal(new[] { "create", "--description=--allow-anonymous", "--json" }, RemoteTunnelManagement.CreateArguments(new() { Description = "--allow-anonymous" }));
    }

    [Fact]
    public async Task CliManagementUsesOnlyVerifiedScopedTokenForSdk()
    {
        var runner = new FakeCli();
        var fixture = new Fixture(runner);
        var profile = Account with { Provider = AccountProvider.GitHubCli, StableIdentityId = "github:1234" };
        await fixture.Service.GetAsync(profile, Target, default);
        Assert.Equal(new[] { "user show --json", "token sample-tunnel.use2 --scopes manage --json", "user show --json" }, runner.Commands);
        Assert.StartsWith("tunnel ", Assert.Single(fixture.Requests).Authorization);
    }

    [Fact]
    public async Task CliIdentityChangePreventsAnySdkRequest()
    {
        var runner = new FakeCli { Changed = true };
        var fixture = new Fixture(runner);
        var profile = Account with { Provider = AccountProvider.GitHubCli, StableIdentityId = "github:1234" };
        Assert.Equal("CLI_ACCOUNT_CHANGED", (await Assert.ThrowsAsync<ToolkitException>(() => fixture.Service.GetAsync(profile, Target, default))).Code);
        Assert.Empty(fixture.Requests);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task DiagnosticsReportRealElapsedApiCheckWithoutClaimingRelayTraffic()
    {
        var fixture = new Fixture();
        var result = await fixture.Service.DiagnoseAsync(Account, Target, default);
        Assert.True(result.Success); Assert.Equal("ManagementApi", result.Check); Assert.True(result.ElapsedMilliseconds >= 0);
    }

    [Fact]
    public async Task NativeCreateIsPrivateAndDeleteTargetsExactlyTheReadResource()
    {
        var fixture = new Fixture();
        var result = await fixture.Service.CreateAsync(Account, new() { TunnelId = Target.TunnelId, ClusterId = Target.ClusterId, ExpirationSeconds = 86400 }, default);
        Assert.Equal(Target, result.Target); Assert.Empty(result.Access);
        await fixture.Service.DeleteAsync(Account, Target, Target.CanonicalId, default);
        Assert.Contains("/tunnels/sample-tunnel", fixture.Requests.Last().Uri);
        Assert.Equal(HttpMethod.Delete, fixture.Requests.Last().Method);
        Assert.DoesNotContain(fixture.Requests, request => request.Uri.Contains("allow-anonymous", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PortCreateDeleteAndMissingUpdateAreDistinctOperations()
    {
        var fixture = new Fixture();
        Assert.Equal("REMOTE_PORT_NOT_FOUND", (await Assert.ThrowsAsync<ToolkitException>(() => fixture.Service.SavePortAsync(Account, Target, new(8080), false, default))).Code);
        var result = await fixture.Service.SavePortAsync(Account, Target, new(8080, "http"), true, default);
        Assert.Equal(8080, result.PortNumber);
        await fixture.Service.DeletePortAsync(Account, Target, 8080, Target.CanonicalId, default);
        Assert.EndsWith("/ports/8080?api-version=2023-09-27-preview", fixture.Requests.Last().Uri);
    }

    [Fact]
    public void CliListAcceptsBothShapesDeduplicatesAndNeverReturnsTokens()
    {
        using var json = JsonDocument.Parse("""
            {"tunnels":[{"tunnelId":"sample.use2","description":"Example","accessTokens":{"manage":"fictional-sensitive-token"}},{"tunnel":{"tunnelId":"sample","clusterId":"use2"}}]}
            """);
        var list = RemoteTunnelManagement.ParseList(json.RootElement);
        Assert.Equal("sample.use2", Assert.Single(list).Target.CanonicalId);
        Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(list));
        Assert.Throws<ToolkitException>(() => RemoteTunnelManagement.ParseList(JsonSerializer.SerializeToElement(new { other = Array.Empty<object>() })));
    }

    [Fact]
    public async Task WebForwardingOptionsAreExplicitAndPreserveOtherServerOptions()
    {
        var fixture = new Fixture();
        fixture.Tunnel.Options = new() { IsInspectionEnabled = true, OriginHeader = "https://previous.example", RequestTimeoutSeconds = 100 };
        var options = new RemoteWebOptions(HostHeader: "service.example:8080", PreserveHostHeader: false, RequestTimeoutSeconds: 42);
        var result = await fixture.Service.UpdateAsync(Account, Target, new() { WebOptions = options }, default);
        Assert.Equal(42, result.WebOptions?.RequestTimeoutSeconds);
        Assert.Equal("https://previous.example", result.WebOptions?.OriginHeader);
        Assert.True(fixture.Tunnel.Options?.IsInspectionEnabled);
        var arguments = RemoteTunnelManagement.CreateArguments(new() { WebOptions = new(PreserveHostHeader: true, OriginHeader: "https://app.example", RequestTimeoutSeconds: 0) });
        Assert.Equal(new[] { "create", "--host-header=unchanged", "--origin-header=https://app.example", "--request-timeout", "0", "--json" }, arguments);
    }

    [Theory]
    [InlineData("valid.example\r\nX-Injected: true", null, null)]
    [InlineData("user@example.org", null, null)]
    [InlineData(null, "https://user:password@example.org", null)]
    [InlineData(null, "https://example.org/path", null)]
    [InlineData(null, null, -1)]
    [InlineData(null, null, 3601)]
    public void WebForwardingRejectsUnsafeHeadersAndUnboundedTimeouts(string? host, string? origin, int? timeout)
    {
        Assert.Throws<ToolkitException>(() => RemoteTunnelManagement.CreateArguments(new() { WebOptions = new(host, OriginHeader: origin, RequestTimeoutSeconds: timeout) }));
    }

    [Theory]
    [InlineData("create", null)]
    [InlineData("host", 3140)]
    [InlineData("manage:ports", 3140)]
    [InlineData("connect manage", null)]
    public async Task TokensRejectInvalidResourceScopesBeforeAnyNetwork(string scope, int? port)
    {
        var fixture = new Fixture();
        var error = await Assert.ThrowsAsync<ToolkitException>(() => fixture.Service.IssueTokenAsync(Account, Target, new([scope], port), default));
        Assert.Equal("REMOTE_SCOPE_INVALID", error.Code); Assert.Empty(fixture.Requests);
    }

    [Fact]
    public void TokenResultDoesNotRevealSecretWhenAccidentallyStringified()
        => Assert.DoesNotContain("sensitive-example", new RemoteTokenResult("sensitive-example", ["connect"], null, "NotReported").ToString());

    [Fact]
    public async Task AccessInsertionHonorsExplicitOrderingAndRejectsOutOfBounds()
    {
        var fixture = new Fixture(); fixture.Tunnel.AccessControl = new([UnknownEntry()]);
        var result = await fixture.Service.AddAccessAsync(Account, Target, null, new() { Type = "Anonymous", InsertIndex = 0 }, default);
        Assert.Equal("Anonymous", result[0].Type); Assert.Equal("future-provider", result[1].Provider);
        Assert.Equal("REMOTE_ACCESS_INDEX_INVALID", (await Assert.ThrowsAsync<ToolkitException>(() => fixture.Service.AddAccessAsync(Account, Target, null,
            new() { Type = "Users", Provider = "github", Subjects = ["12345"], InsertIndex = 3 }, default))).Code);
    }

    [Theory]
    [InlineData("aad")]
    [InlineData("entra")]
    [InlineData("microsoft")]
    public void MicrosoftProviderAliasesUseOfficialIdentifier(string provider)
        => Assert.Equal(TunnelAccessControlEntry.Providers.Microsoft, RemoteTunnelManagement.ToEntry(new() { Provider = provider, Subjects = ["fictional-user-id"] }, false).Provider);

    private static TunnelAccessControlEntry UnknownEntry() => new()
    {
        Type = TunnelAccessControlEntryType.Users,
        Provider = "future-provider",
        Subjects = ["stable-subject"],
        Scopes = ["connect"],
        IsDeny = true
    };

    private sealed record Request(HttpMethod Method, string Uri, string? Body, string? Authorization);

    private sealed class Fixture : DelegatingHandler
    {
        public Tunnel Tunnel { get; set; } = new() { TunnelId = Target.TunnelId, ClusterId = Target.ClusterId, Description = "Fixture", Ports = [] };
        public TunnelPort? Port { get; set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public List<Request> Requests { get; } = [];
        public RemoteTunnelManagement Service { get; }
        public Fixture(FakeCli? cli = null)
        {
            InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false };
            Service = new(new FakeAuth(), new(cli ?? new()), authorize => new(new ProductInfoHeaderValue("Test", "1.0"), authorize,
                new Uri("https://global.rel.tunnels.api.visualstudio.com/"), this));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
            if (Status != HttpStatusCode.OK) return new(Status) { Content = new StringContent("{\"title\":\"private-provider-data\"}", Encoding.UTF8, "application/problem+json") };
            var isPort = request.RequestUri.AbsolutePath.Contains("/ports/", StringComparison.Ordinal);
            if (request.Method == HttpMethod.Put)
            {
                if (isPort)
                {
                    var update = JsonSerializer.Deserialize<TunnelPort>(body!, Json)!;
                    update.Name ??= Port?.Name;
                    update.AccessControl = MergeInherited(update.AccessControl, Port?.AccessControl);
                    Port = update;
                }
                else
                {
                    var update = JsonSerializer.Deserialize<Tunnel>(body!, Json)!;
                    update.TunnelId ??= Tunnel.TunnelId; update.ClusterId ??= Tunnel.ClusterId;
                    update.Ports ??= Tunnel.Ports;
                    update.AccessControl = MergeInherited(update.AccessControl, Tunnel.AccessControl);
                    Tunnel = update;
                }
            }
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);
            if (isPort && Port is null) return new(HttpStatusCode.NotFound);
            var response = isPort ? JsonSerializer.Serialize(Port, Json) : JsonSerializer.Serialize(Tunnel, Json);
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
        private static TunnelAccessControl? MergeInherited(TunnelAccessControl? updated, TunnelAccessControl? previous)
        {
            if (updated is null) return previous;
            // The actual SDK omits inherited rules on writes; the service reattaches policies.
            return new((previous?.Entries ?? []).Where(entry => entry.IsInherited).Concat(updated.Entries.Where(entry => !entry.IsInherited)));
        }
        protected override void Dispose(bool disposing) { } // fixtures span several isolated client lifetimes
    }

    private sealed class FakeAuth : IAuthenticationService
    {
        public Task<string> GetAuthorizationAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult("Bearer fictional-account-token");
        public Task<AccountProfile> LoginAsync(AccountProfile account, Action<LoginChallenge> onChallenge, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AccountProfile> ValidateAsync(AccountProfile account, CancellationToken cancellationToken) => Task.FromResult(account);
        public Task LogoutAsync(AccountProfile account, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCli : IOfficialCli
    {
        public bool Changed { get; init; }
        public List<string> Commands { get; } = [];
        public Task<string> RunAsync(string[] arguments, Action<string>? output, CancellationToken cancellationToken)
        {
            Commands.Add(string.Join(' ', arguments));
            return Task.FromResult(arguments[0] == "user"
                ? "{\"status\":\"Logged in\",\"user\":{\"provider\":\"github\",\"username\":\"sample\",\"id\":\"" + (Changed ? "9999" : "1234") + "\"}}"
                : "{\"token\":\"fictional-management-token\"}");
        }
    }
}
