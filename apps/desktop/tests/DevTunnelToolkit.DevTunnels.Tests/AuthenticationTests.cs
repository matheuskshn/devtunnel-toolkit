using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using DevTunnelToolkit.Core;
using Microsoft.DevTunnels.Contracts;
using Xunit;

namespace DevTunnelToolkit.DevTunnels.Tests;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task MicrosoftRequiresAnExplicitPublicClientRegistration()
    {
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("Network must not be called"));
        using var service = new NativeAuthenticationService(new MemoryVault(), new HttpClient(handler));
        var failure = await Assert.ThrowsAsync<ToolkitException>(() => service.LoginAsync(
            new() { Provider = AccountProvider.Microsoft }, _ => throw new InvalidOperationException("No challenge expected"), default));
        Assert.Equal("AUTH_CLIENT_ID_REQUIRED", failure.Code);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GitHubDeviceLoginUsesSdkAppAndOnlyPublishesChallengeAndIdentity()
    {
        var vault = new MemoryVault();
        var account = new AccountProfile { Provider = AccountProvider.GitHub };
        LoginChallenge? challenge = null;
        var handler = new ScriptedHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/login/device/code")
            {
                Assert.Contains(Uri.EscapeDataString(TunnelServiceProperties.Production.GitHubAppClientId), await request.Content!.ReadAsStringAsync());
                return Json(new { device_code = "private-device-code", user_code = "ABCD-1234", expires_in = 60, interval = 1, verification_uri = "https://malicious.example" });
            }
            if (request.RequestUri.AbsolutePath == "/login/oauth/access_token")
                return Json(new { access_token = "fictional-access-value", refresh_token = "fictional-refresh-value", expires_in = 28800, refresh_token_expires_in = 86400 });
            Assert.Equal("https://api.github.com/user", request.RequestUri.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fictional-access-value", request.Headers.Authorization.Parameter);
            return Json(new { id = 42, login = "sample-user" });
        });
        using var service = new NativeAuthenticationService(vault, new HttpClient(handler));
        var result = await service.LoginAsync(account, value => challenge = value, default);
        Assert.NotNull(challenge);
        Assert.Equal("https://github.com/login/device", challenge.VerificationUri);
        Assert.Equal("ABCD-1234", challenge.UserCode);
        Assert.Equal("sample-user", result.Username);
        Assert.Equal("42", result.StableIdentityId);
        Assert.NotNull(result.ExpiresAt);
        Assert.Equal(result.ExpiresAt, service.GetObservedExpiration(account.Id));
        Assert.DoesNotContain("fictional-access-value", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("private-device-code", JsonSerializer.Serialize(challenge));
        Assert.Equal("github fictional-access-value", await service.GetAuthorizationAsync(result, default));
        Assert.Equal(3, handler.RequestCount);
        Assert.Single(vault.Data);
    }

    [Fact]
    public async Task RefreshIsSerializedPerAccountAndLogoutPreservesOtherAccounts()
    {
        var vault = new MemoryVault();
        var first = new AccountProfile { Provider = AccountProvider.GitHub };
        var second = new AccountProfile { Provider = AccountProvider.GitHub };
        await vault.WriteAsync($"account.{first.Id}.github", Credential("old-value", DateTimeOffset.UtcNow.AddMinutes(-1)));
        await vault.WriteAsync($"account.{second.Id}.github", Credential("second-value", DateTimeOffset.UtcNow.AddHours(3)));
        var handler = new ScriptedHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("grant_type=refresh_token", body);
            Assert.DoesNotContain("client_secret", body);
            return Json(new { access_token = "renewed-value", refresh_token = "rotated-refresh", expires_in = 28800, refresh_token_expires_in = 86400 });
        });
        using var service = new NativeAuthenticationService(vault, new HttpClient(handler));
        Assert.Null(service.GetObservedExpiration(first.Id));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.GetAuthorizationAsync(first, default)));
        Assert.All(results, result => Assert.Equal("github renewed-value", result));
        Assert.Equal(1, handler.RequestCount);
        Assert.True(service.GetObservedExpiration(first.Id) > DateTimeOffset.UtcNow.AddHours(7));
        await service.LogoutAsync(first, default);
        Assert.Null(service.GetObservedExpiration(first.Id));
        Assert.Equal("github second-value", await service.GetAuthorizationAsync(second, default));
        var missing = await Assert.ThrowsAsync<ToolkitException>(() => service.GetAuthorizationAsync(first, default));
        Assert.Equal("AUTHENTICATION_REQUIRED", missing.Code);
        Assert.Single(vault.Data);
    }

    [Fact]
    public async Task ProviderErrorsDoNotEchoSensitiveResponseContent()
    {
        using var service = new NativeAuthenticationService(new MemoryVault(), new HttpClient(new ScriptedHandler(_ => Task.FromResult(Json(new
        {
            error = "invalid_grant",
            error_description = "fictional-sensitive-response"
        })))));
        var account = new AccountProfile { Provider = AccountProvider.GitHub };
        var failure = await Assert.ThrowsAsync<ToolkitException>(() => service.LoginAsync(account, _ => { }, default));
        Assert.DoesNotContain("fictional-sensitive-response", failure.ToString());
    }

    [Fact]
    public async Task GitHubReauthenticationCannotReplaceAnExistingIdentityOrCredential()
    {
        var vault = new MemoryVault();
        var account = new AccountProfile { Provider = AccountProvider.GitHub, StableIdentityId = "42" };
        var saved = Credential("original-account-token", DateTimeOffset.UtcNow.AddHours(1));
        await vault.WriteAsync($"account.{account.Id}.github", saved);
        var handler = new ScriptedHandler(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/login/device/code" => Json(new { device_code = "fixture-device", user_code = "TEST-1234", expires_in = 60, interval = 1 }),
            "/login/oauth/access_token" => Json(new { access_token = "different-account-token", expires_in = 3600 }),
            _ => Json(new { id = 84, login = "different-user" })
        }));
        using var service = new NativeAuthenticationService(vault, new HttpClient(handler));
        var rejected = await Assert.ThrowsAsync<ToolkitException>(() => service.LoginAsync(account, _ => { }, default));
        Assert.Equal("ACCOUNT_IDENTITY_CHANGED", rejected.Code);
        Assert.Equal(saved, await vault.ReadAsync($"account.{account.Id}.github"));
        Assert.Equal("github original-account-token", await service.GetAuthorizationAsync(account, default));
        Assert.DoesNotContain("different-account-token", JsonSerializer.Serialize(vault.Data));
    }

    [Fact]
    public async Task MicrosoftReauthenticationKeepsNewCacheInMemoryUntilIdentityMatches()
    {
        const string tenant = "11111111-2222-3333-4444-555555555555";
        const string clientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        const string originalId = "11111111-1111-1111-1111-111111111111";
        const string replacementId = "22222222-2222-2222-2222-222222222222";
        const string authority = "https://login.microsoftonline.com/" + tenant;
        const string scope = "api://33333333-3333-3333-3333-333333333333/.default";
        var identity = originalId;
        var vault = new MemoryVault();
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/discovery/instance", StringComparison.Ordinal))
                return Task.FromResult(Json(new
                {
                    tenant_discovery_endpoint = authority + "/v2.0/.well-known/openid-configuration",
                    metadata = new[] { new { preferred_network = "login.microsoftonline.com", preferred_cache = "login.windows.net", aliases = new[] { "login.microsoftonline.com", "login.windows.net", "login.microsoft.com" } } }
                }));
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
                return Task.FromResult(Json(new
                {
                    authorization_endpoint = authority + "/oauth2/v2.0/authorize",
                    token_endpoint = authority + "/oauth2/v2.0/token",
                    device_authorization_endpoint = authority + "/oauth2/v2.0/devicecode",
                    issuer = authority + "/v2.0",
                    jwks_uri = authority + "/discovery/v2.0/keys"
                }));
            if (path.EndsWith("/devicecode", StringComparison.Ordinal))
                return Task.FromResult(Json(new { device_code = "fixture-device", user_code = "TEST-1234", expires_in = 60, interval = 1, verification_uri = "https://microsoft.com/devicelogin", message = "Fixture challenge" }));
            Assert.EndsWith("/token", path, StringComparison.Ordinal);
            var idToken = Base64Url(new { alg = "none", typ = "JWT" }) + "." + Base64Url(new
            {
                aud = clientId,
                iss = authority + "/v2.0",
                tid = tenant,
                oid = identity,
                sub = identity,
                preferred_username = "sample-user@example.invalid",
                name = "Sample User",
                exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }) + ".fixture";
            return Task.FromResult(Json(new
            {
                token_type = "Bearer",
                scope,
                expires_in = 3600,
                ext_expires_in = 3600,
                access_token = "access-" + identity,
                refresh_token = "refresh-" + identity,
                id_token = idToken,
                client_info = Base64Url(new { uid = identity, utid = tenant })
            }));
        });
        using var service = new NativeAuthenticationService(vault, new HttpClient(handler));
        var account = new AccountProfile { Provider = AccountProvider.Microsoft, ClientId = clientId, TenantId = tenant, Scope = scope };
        var original = await service.LoginAsync(account, _ => { }, default);
        Assert.Equal(originalId + "." + tenant, original.StableIdentityId);
        Assert.Equal(original.ExpiresAt, service.GetObservedExpiration(account.Id));
        Assert.True(service.GetObservedExpiration(account.Id) > DateTimeOffset.UtcNow.AddMinutes(59));
        var cacheKey = $"account.{account.Id}.msal";
        var saved = await vault.ReadAsync(cacheKey);
        Assert.NotNull(saved);
        identity = replacementId;
        var rejected = await Assert.ThrowsAsync<ToolkitException>(() => service.LoginAsync(original, _ => { }, default));
        Assert.Equal("ACCOUNT_IDENTITY_CHANGED", rejected.Code);
        Assert.Equal(saved, await vault.ReadAsync(cacheKey));
        Assert.Equal(original.ExpiresAt, service.GetObservedExpiration(account.Id));
        Assert.Equal("Bearer access-" + originalId, await service.GetAuthorizationAsync(original, default));
        var calls = handler.RequestCount;
        // MSAL persists cached expiration with whole-second precision.
        Assert.Equal(original.ExpiresAt!.Value.ToUnixTimeSeconds(), ((IAuthenticationMetadataProvider)service).GetObservedExpiration(account.Id)!.Value.ToUnixTimeSeconds());
        Assert.Equal(calls, handler.RequestCount);
        Assert.DoesNotContain(replacementId, Encoding.UTF8.GetString(Convert.FromBase64String((await vault.ReadAsync(cacheKey))!)));
    }

    [Theory]
    [InlineData(AccountProvider.Microsoft, "user-one.tenant-one", "user-one.tenant-two")]
    [InlineData(AccountProvider.Microsoft, "user-one.tenant-one", "user-two.tenant-one")]
    [InlineData(AccountProvider.GitHub, "42", "84")]
    public void IdentityGuardUsesTheStableProviderIdentity(AccountProvider provider, string original, string replacement)
    {
        var profile = new AccountProfile { Provider = provider, StableIdentityId = original };
        NativeAuthenticationService.EnsureIdentityUnchanged(profile, original);
        Assert.Equal("ACCOUNT_IDENTITY_CHANGED", Assert.Throws<ToolkitException>(() => NativeAuthenticationService.EnsureIdentityUnchanged(profile, replacement)).Code);
        NativeAuthenticationService.EnsureIdentityUnchanged(profile with { StableIdentityId = null }, replacement);
    }

    [Fact]
    public async Task ExpiredTunnelTokenIsRejectedAndCannotDiscoverTheWholeAccount()
    {
        var vault = new MemoryVault();
        var account = new AccountProfile { Provider = AccountProvider.TunnelToken };
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { exp = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds() })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await vault.WriteAsync($"account.{account.Id}.token", "e30." + payload + ".test-signature");
        using var authentication = new NativeAuthenticationService(vault);
        var expired = await Assert.ThrowsAsync<ToolkitException>(() => authentication.GetAuthorizationAsync(account, default));
        Assert.Equal("AUTHENTICATION_EXPIRED", expired.Code);
        var discovery = await Assert.ThrowsAsync<ToolkitException>(() => new DevTunnelService(authentication).DiscoverAsync(account, default));
        Assert.Equal("DISCOVERY_REQUIRES_ACCOUNT", discovery.Code);
    }

    [Fact]
    public async Task MissingMicrosoftCacheDoesNotAdoptAccountsFromOtherApplications()
    {
        using var authentication = new NativeAuthenticationService(new MemoryVault());
        var account = new AccountProfile { Provider = AccountProvider.Microsoft, ClientId = "11111111-2222-3333-4444-555555555555" };
        var failure = await Assert.ThrowsAsync<ToolkitException>(() => authentication.GetAuthorizationAsync(account, default));
        Assert.Equal("AUTHENTICATION_REQUIRED", failure.Code);
    }

    private static string Credential(string access, DateTimeOffset expiration) => JsonSerializer.Serialize(new
    {
        clientId = "sample-client",
        accessToken = access,
        refreshToken = "sample-refresh",
        expiresAt = expiration,
        refreshExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
    });

    private static string Base64Url(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private sealed class ScriptedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        private int requestCount;
        public int RequestCount => requestCount;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            return handle(request);
        }
    }

    private sealed class MemoryVault : ICredentialVault
    {
        public ConcurrentDictionary<string, string> Data { get; } = new();
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Data.TryGetValue(key, out var value) ? value : null);
        public Task WriteAsync(string key, string value, CancellationToken cancellationToken = default) { Data[key] = value; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { Data.TryRemove(key, out _); return Task.CompletedTask; }
    }
}
