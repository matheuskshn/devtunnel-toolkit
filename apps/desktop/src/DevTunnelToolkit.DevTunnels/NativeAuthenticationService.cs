using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using DevTunnelToolkit.Core;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;
using Microsoft.Identity.Client;

namespace DevTunnelToolkit.DevTunnels;

/// <summary>Account-isolated provider authentication. The caller supplies a protected vault.</summary>
public sealed class NativeAuthenticationService : IAuthenticationService, IAuthenticationMetadataProvider, IDisposable
{
    private readonly ICredentialVault vault;
    private readonly HttpClient http;
    private readonly bool ownsHttpClient;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> accountLocks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> observedExpirations = new();
    private readonly TimeProvider time;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public NativeAuthenticationService(ICredentialVault vault) : this(vault, null) { }

    public NativeAuthenticationService(ICredentialVault vault, HttpClient? httpClient, TimeProvider? timeProvider = null)
    {
        this.vault = vault;
        http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        ownsHttpClient = httpClient is null;
        time = timeProvider ?? TimeProvider.System;
    }

    public DateTimeOffset? GetObservedExpiration(string accountId)
    {
        ValidateAccountId(accountId);
        return observedExpirations.TryGetValue(accountId, out var expiration) ? expiration : null;
    }

    private void ObserveExpiration(string accountId, DateTimeOffset? expiration)
    {
        if (expiration is { } value) observedExpirations[accountId] = value;
        else observedExpirations.TryRemove(accountId, out _);
    }

    public async Task<AccountProfile> LoginAsync(AccountProfile account, Action<LoginChallenge> onChallenge, CancellationToken cancellationToken)
    {
        ValidateAccountId(account.Id);
        var gate = accountLocks.GetOrAdd(account.Id, _ => new(1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return account.Provider switch
            {
                AccountProvider.GitHub => await LoginGitHubAsync(account, onChallenge, cancellationToken),
                AccountProvider.Microsoft => await LoginMicrosoftAsync(account, onChallenge, cancellationToken),
                AccountProvider.TunnelToken => await ValidateTunnelTokenAsync(account, cancellationToken),
                _ => throw new ToolkitException("AUTH_PROVIDER_UNSUPPORTED")
            };
        }
        catch (MsalException ex) { throw NormalizeMsalException(ex); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ToolkitException("AUTH_PROVIDER_TIMEOUT"); }
        catch (HttpRequestException) { throw new ToolkitException("AUTH_PROVIDER_UNAVAILABLE"); }
        finally { gate.Release(); }
    }

    public async Task<AccountProfile> ValidateAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        if (account.Provider == AccountProvider.TunnelToken)
            return await ValidateTunnelTokenAsync(account, cancellationToken);

        var authorization = await GetAuthorizationAsync(account, cancellationToken);
        await using var client = new TunnelManagementClient(
            new ProductInfoHeaderValue("DevTunnelToolkit", "1.0"),
            () => Task.FromResult<AuthenticationHeaderValue?>(AuthenticationHeaderValue.Parse(authorization)),
            tunnelServiceUri: null, httpHandler: null);
        try { await client.ListUserLimitsAsync(cancellationToken); }
        catch (UnauthorizedAccessException) { throw new ToolkitException("AUTHENTICATION_REQUIRED"); }
        return account with
        {
            Status = "Authenticated",
            LastValidatedAt = time.GetUtcNow(),
            ExpiresAt = observedExpirations.TryGetValue(account.Id, out var expiry) ? expiry : null
        };
    }

    public async Task LogoutAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        ValidateAccountId(account.Id);
        var gate = accountLocks.GetOrAdd(account.Id, _ => new(1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var suffix in new[] { "github", "msal", "token" })
                await vault.DeleteAsync($"account.{account.Id}.{suffix}", cancellationToken);
            observedExpirations.TryRemove(account.Id, out _);
        }
        finally { gate.Release(); }
    }

    public async Task<string> GetAuthorizationAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        ValidateAccountId(account.Id);
        var gate = accountLocks.GetOrAdd(account.Id, _ => new(1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            switch (account.Provider)
            {
                case AccountProvider.GitHub:
                    var credential = await ReadGitHubAsync(account, cancellationToken);
                    if (credential.ExpiresAt is { } expiry && expiry <= time.GetUtcNow().AddMinutes(2))
                        credential = await RefreshGitHubAsync(account, credential, cancellationToken);
                    ObserveExpiration(account.Id, credential.ExpiresAt);
                    return "github " + credential.AccessToken;
                case AccountProvider.Microsoft:
                    {
                        var app = await CreateMicrosoftApplicationAsync(account, cancellationToken);
                        var accounts = (await app.GetAccountsAsync()).ToArray();
                        var cachedAccount = accounts.SingleOrDefault(candidate =>
                            account.StableIdentityId is null || candidate.HomeAccountId.Identifier == account.StableIdentityId);
                        if (cachedAccount is null) throw new ToolkitException("AUTHENTICATION_REQUIRED");
                        using var silentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        silentCancellation.CancelAfter(TimeSpan.FromSeconds(45));
                        var result = await app.AcquireTokenSilent(GetMicrosoftScopes(account), cachedAccount).ExecuteAsync(silentCancellation.Token);
                        observedExpirations[account.Id] = result.ExpiresOn;
                        return "Bearer " + result.AccessToken;
                    }
                case AccountProvider.TunnelToken:
                    var token = await vault.ReadAsync($"account.{account.Id}.token", cancellationToken);
                    if (string.IsNullOrWhiteSpace(token)) throw new ToolkitException("AUTHENTICATION_REQUIRED");
                    ValidateTokenValue(token);
                    var tokenExpiration = GetTunnelTokenExpiration(token);
                    if (tokenExpiration <= time.GetUtcNow()) throw new ToolkitException("AUTHENTICATION_EXPIRED");
                    ObserveExpiration(account.Id, tokenExpiration);
                    return "tunnel " + token;
                default:
                    throw new ToolkitException("AUTH_PROVIDER_UNSUPPORTED");
            }
        }
        catch (MsalException ex) { throw NormalizeMsalException(ex); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ToolkitException("AUTH_PROVIDER_TIMEOUT"); }
        catch (HttpRequestException) { throw new ToolkitException("AUTH_PROVIDER_UNAVAILABLE"); }
        finally { gate.Release(); }
    }

    private async Task<AccountProfile> LoginGitHubAsync(AccountProfile account, Action<LoginChallenge> onChallenge, CancellationToken cancellationToken)
    {
        var clientId = account.ClientId ?? TunnelServiceProperties.Production.GitHubAppClientId;
        using var challenge = await PostGitHubAsync("https://github.com/login/device/code", new() { ["client_id"] = clientId }, cancellationToken);
        var data = challenge.RootElement;
        ThrowOAuthError(OptionalString(data, "error"));
        var deviceCode = RequiredString(data, "device_code");
        var userCode = RequiredString(data, "user_code");
        var expiry = time.GetUtcNow().AddSeconds(ReadSeconds(data, "expires_in", 900));
        var interval = ReadSeconds(data, "interval", 5);
        onChallenge(new("https://github.com/login/device", userCode, expiry));
        while (time.GetUtcNow() < expiry)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), time, cancellationToken);
            using var response = await PostGitHubAsync("https://github.com/login/oauth/access_token", new()
            {
                ["client_id"] = clientId,
                ["device_code"] = deviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            }, cancellationToken);
            var root = response.RootElement;
            var error = OptionalString(root, "error");
            if (error == "authorization_pending") continue;
            if (error == "slow_down") { interval = Math.Max(interval + 5, ReadSeconds(root, "interval", interval + 5)); continue; }
            ThrowOAuthError(error);
            var credential = ParseGitHubCredential(root, clientId);
            var identity = await ReadGitHubIdentityAsync(credential.AccessToken, cancellationToken);
            EnsureIdentityUnchanged(account, identity.Id);
            await vault.WriteAsync($"account.{account.Id}.github", JsonSerializer.Serialize(credential, JsonOptions), cancellationToken);
            ObserveExpiration(account.Id, credential.ExpiresAt);
            return account with
            {
                Username = identity.Login,
                DisplayName = string.IsNullOrWhiteSpace(account.DisplayName) ? identity.Login : account.DisplayName,
                StableIdentityId = identity.Id,
                AuthenticatedAt = time.GetUtcNow(),
                ExpiresAt = credential.ExpiresAt,
                LastValidatedAt = time.GetUtcNow(),
                Status = "Authenticated"
            };
        }
        throw new ToolkitException("AUTH_CHALLENGE_EXPIRED");
    }

    private async Task<GitHubCredential> RefreshGitHubAsync(AccountProfile account, GitHubCredential old, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(old.RefreshToken) || old.RefreshExpiresAt <= time.GetUtcNow())
            throw new ToolkitException("AUTHENTICATION_EXPIRED");
        using var response = await PostGitHubAsync("https://github.com/login/oauth/access_token", new()
        {
            ["client_id"] = old.ClientId,
            ["refresh_token"] = old.RefreshToken,
            ["grant_type"] = "refresh_token"
        }, cancellationToken);
        ThrowOAuthError(OptionalString(response.RootElement, "error"));
        var renewed = ParseGitHubCredential(response.RootElement, old.ClientId);
        await vault.WriteAsync($"account.{account.Id}.github", JsonSerializer.Serialize(renewed, JsonOptions), cancellationToken);
        return renewed;
    }

    private async Task<AccountProfile> LoginMicrosoftAsync(AccountProfile account, Action<LoginChallenge> onChallenge, CancellationToken cancellationToken)
    {
        string? pendingCache = null;
        var app = await CreateMicrosoftApplicationAsync(account, cancellationToken, value =>
        {
            pendingCache = value;
            return Task.CompletedTask;
        });
        var result = await app.AcquireTokenWithDeviceCode(GetMicrosoftScopes(account), code =>
        {
            onChallenge(new(code.VerificationUrl, code.UserCode, code.ExpiresOn));
            return Task.CompletedTask;
        }).ExecuteAsync(cancellationToken);
        EnsureIdentityUnchanged(account, result.Account.HomeAccountId.Identifier);
        if (pendingCache is not null)
            await vault.WriteAsync($"account.{account.Id}.msal", pendingCache, cancellationToken);
        ObserveExpiration(account.Id, result.ExpiresOn);
        return account with
        {
            Username = result.Account.Username,
            DisplayName = string.IsNullOrWhiteSpace(account.DisplayName) ? result.Account.Username : account.DisplayName,
            StableIdentityId = result.Account.HomeAccountId.Identifier,
            TenantId = result.TenantId,
            AuthenticatedAt = time.GetUtcNow(),
            ExpiresAt = result.ExpiresOn,
            LastValidatedAt = time.GetUtcNow(),
            Status = "Authenticated"
        };
    }

    private Task<IPublicClientApplication> CreateMicrosoftApplicationAsync(AccountProfile account, CancellationToken cancellationToken, Func<string, Task>? storePendingCache = null)
    {
        if (!Guid.TryParse(account.ClientId, out _))
            throw new ToolkitException("AUTH_CLIENT_ID_REQUIRED", "Configure o Client ID de uma aplicação pública Microsoft autorizada para Dev Tunnels.");
        var tenant = string.IsNullOrWhiteSpace(account.TenantId) ? "common" : account.TenantId;
        if (tenant != "common" && tenant != "organizations" && tenant != "consumers" && !Guid.TryParse(tenant, out _))
            throw new ToolkitException("AUTH_TENANT_INVALID");
        var app = PublicClientApplicationBuilder.Create(account.ClientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenant}")
            .WithRedirectUri("http://localhost")
            .WithHttpClientFactory(new MsalHttpClientFactory(http))
            .Build();
        var cacheKey = $"account.{account.Id}.msal";
        app.UserTokenCache.SetBeforeAccessAsync(async notification =>
        {
            var stored = await vault.ReadAsync(cacheKey, cancellationToken);
            if (stored is not null)
            {
                try { notification.TokenCache.DeserializeMsalV3(Convert.FromBase64String(stored), shouldClearExistingCache: true); }
                catch (FormatException) { throw new ToolkitException("AUTH_CACHE_INVALID"); }
            }
        });
        app.UserTokenCache.SetAfterAccessAsync(async notification =>
        {
            if (notification.HasStateChanged)
            {
                var bytes = notification.TokenCache.SerializeMsalV3();
                try
                {
                    var serialized = Convert.ToBase64String(bytes);
                    if (storePendingCache is null) await vault.WriteAsync(cacheKey, serialized, cancellationToken);
                    else await storePendingCache(serialized);
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            }
        });
        return Task.FromResult(app);
    }

    private static string[] GetMicrosoftScopes(AccountProfile account) =>
        [string.IsNullOrWhiteSpace(account.Scope) ? $"{TunnelServiceProperties.Production.ServiceAppId}/.default" : account.Scope];

    private async Task<AccountProfile> ValidateTunnelTokenAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        ValidateAccountId(account.Id);
        var token = await vault.ReadAsync($"account.{account.Id}.token", cancellationToken);
        if (string.IsNullOrWhiteSpace(token)) throw new ToolkitException("AUTHENTICATION_REQUIRED");
        ValidateTokenValue(token);
        var expiry = GetTunnelTokenExpiration(token);
        if (expiry <= time.GetUtcNow()) throw new ToolkitException("AUTHENTICATION_EXPIRED");
        ObserveExpiration(account.Id, expiry);
        // The credential is configured, but validity on the service is established only by GetTunnelAsync.
        return account with { ExpiresAt = expiry, Status = "Configured" };
    }

    private async Task<GitHubCredential> ReadGitHubAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        var raw = await vault.ReadAsync($"account.{account.Id}.github", cancellationToken);
        if (raw is null) throw new ToolkitException("AUTHENTICATION_REQUIRED");
        try
        {
            var credential = JsonSerializer.Deserialize<GitHubCredential>(raw, JsonOptions) ?? throw new ToolkitException("AUTH_CACHE_INVALID");
            ValidateTokenValue(credential.AccessToken);
            return credential;
        }
        catch (JsonException) { throw new ToolkitException("AUTH_CACHE_INVALID"); }
    }

    private async Task<(string Id, string Login)> ReadGitHubIdentityAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevTunnelToolkit", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new ToolkitException("AUTH_IDENTITY_UNAVAILABLE");
        using var document = await ReadJsonAsync(response, cancellationToken);
        if (!document.RootElement.TryGetProperty("id", out var id) || !id.TryGetInt64(out var number) || number <= 0)
            throw new ToolkitException("AUTH_IDENTITY_INVALID");
        return (number.ToString(System.Globalization.CultureInfo.InvariantCulture), RequiredString(document.RootElement, "login"));
    }

    private async Task<JsonDocument> PostGitHubAsync(string uri, Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(values) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevTunnelToolkit", "1.0"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new ToolkitException("AUTH_PROVIDER_UNAVAILABLE");
        return await ReadJsonAsync(response, cancellationToken);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await response.Content.LoadIntoBufferAsync(65_536, cancellationToken);
        try { return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken)); }
        catch (JsonException) { throw new ToolkitException("AUTH_RESPONSE_INVALID"); }
    }

    private GitHubCredential ParseGitHubCredential(JsonElement root, string clientId)
    {
        var accessToken = RequiredString(root, "access_token");
        ValidateTokenValue(accessToken);
        return new(clientId, accessToken, OptionalString(root, "refresh_token"),
            ReadExpiry(root, "expires_in"), ReadExpiry(root, "refresh_token_expires_in"));
    }

    private DateTimeOffset? ReadExpiry(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt64(out var seconds) && seconds > 0 && seconds <= 315_360_000
            ? time.GetUtcNow().AddSeconds(seconds) : null;

    private static int ReadSeconds(JsonElement root, string property, int fallback) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var seconds) && seconds > 0 && seconds <= 3600 ? seconds : fallback;

    private static string RequiredString(JsonElement root, string property) =>
        OptionalString(root, property) is { Length: > 0 } value ? value : throw new ToolkitException("AUTH_RESPONSE_INVALID");

    private static string? OptionalString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void ThrowOAuthError(string? error)
    {
        if (error is null) return;
        throw new ToolkitException(error switch
        {
            "access_denied" => "AUTH_ACCESS_DENIED",
            "expired_token" or "token_expired" => "AUTH_CHALLENGE_EXPIRED",
            "bad_refresh_token" or "invalid_grant" => "AUTHENTICATION_EXPIRED",
            "device_flow_disabled" => "AUTH_DEVICE_FLOW_DISABLED",
            _ => "AUTH_PROVIDER_ERROR"
        });
    }

    private static ToolkitException NormalizeMsalException(MsalException exception) =>
        new(exception is MsalUiRequiredException ? "AUTHENTICATION_REQUIRED" : "AUTH_PROVIDER_ERROR");

    private static void ValidateAccountId(string accountId)
    {
        if (accountId.Length != 32 || accountId.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ToolkitException("ACCOUNT_ID_INVALID");
    }

    internal static void EnsureIdentityUnchanged(AccountProfile account, string authenticatedIdentity)
    {
        if (!string.IsNullOrWhiteSpace(account.StableIdentityId) && !string.Equals(account.StableIdentityId, authenticatedIdentity, StringComparison.Ordinal))
            throw new ToolkitException("ACCOUNT_IDENTITY_CHANGED", "A conta autenticada é diferente deste perfil. Use a identidade original ou adicione outra conta.");
    }

    private static void ValidateTokenValue(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 32768 || token.Any(char.IsWhiteSpace))
            throw new ToolkitException("AUTH_TOKEN_INVALID");
    }

    private static DateTimeOffset? GetTunnelTokenExpiration(string token)
    {
        try { return TunnelAccessTokenProperties.TryParse(token)?.Expiration is { } expiry ? new DateTimeOffset(DateTime.SpecifyKind(expiry, DateTimeKind.Utc)) : null; }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException) { return null; }
    }

    public void Dispose()
    {
        if (ownsHttpClient) http.Dispose();
    }

    private sealed record GitHubCredential(string ClientId, string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, DateTimeOffset? RefreshExpiresAt)
    {
        public override string ToString() => "Protected GitHub credentials";
    }

    private sealed class MsalHttpClientFactory(HttpClient client) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => client;
    }
}
