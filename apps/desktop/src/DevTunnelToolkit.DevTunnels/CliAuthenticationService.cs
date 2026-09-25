using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.DevTunnels;

internal sealed record CliIdentity(string Provider, string Username, string StableId, string? TenantId);

/// <summary>
/// The official CLI owns one shared OS-keychain login. Identity is checked before and after
/// every scoped operation; no undocumented CLI credential cache is read by this application.
/// </summary>
public sealed class CliAuthenticationService : IAuthenticationService, IAuthenticationMetadataProvider, IDesktopAuthenticationService
{
    private readonly IOfficialCli cli;
    private readonly bool isWindows;
    private readonly SemaphoreSlim gate = new(1, 1);

    public CliAuthenticationService() : this(new OfficialCli()) { }
    internal CliAuthenticationService(IOfficialCli cli, bool? isWindows = null)
    {
        this.cli = cli;
        this.isWindows = isWindows ?? OperatingSystem.IsWindows();
    }

    internal static string[] LoginArguments(AccountProfile account, bool isWindows)
    {
        Validation.Account(account);
        if (account.CliLoginMode == CliLoginMode.WindowsIntegrated && !isWindows)
            throw new ToolkitException("WINDOWS_AUTH_UNAVAILABLE");
        var method = account.CliLoginMode switch
        {
            CliLoginMode.Browser => "--use-browser-auth",
            CliLoginMode.WindowsIntegrated => "--use-integrated-windows-auth",
            _ => "--use-device-code-auth"
        };
        return ["user", "login", account.Provider == AccountProvider.GitHubCli ? "--github" : "--entra", method, "--json"];
    }

    public DateTimeOffset? GetObservedExpiration(string accountId) => null;

    public Task<AccountProfile> LoginAsync(AccountProfile account, Action<LoginChallenge> onChallenge, CancellationToken cancellationToken)
        => LoginCoreAsync(account, token => RunLoginCommandAsync(account, onChallenge, cli, isWindows, token), cancellationToken);

    public Task<AccountProfile> LoginFromDesktopAsync(AccountProfile account, Func<DesktopLoginRequest, CancellationToken, Task> authenticate, CancellationToken cancellationToken)
    {
        if (!isWindows || account.Provider != AccountProvider.MicrosoftCli || account.CliLoginMode != CliLoginMode.Browser)
            throw new ToolkitException("DESKTOP_LOGIN_UNSUPPORTED");
        return LoginCoreAsync(account, token => authenticate(new(account.Provider, account.CliLoginMode), token), cancellationToken);
    }

    public static Task RunDesktopLoginAsync(DesktopLoginRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || request.Provider != AccountProvider.MicrosoftCli || request.Mode != CliLoginMode.Browser)
            throw new ToolkitException("DESKTOP_LOGIN_UNSUPPORTED");
        var account = new AccountProfile { DisplayName = "Desktop login", Provider = request.Provider, CliLoginMode = request.Mode };
        return RunLoginCommandAsync(account, _ => throw new ToolkitException("AUTH_FLOW_CHANGED"), new OfficialCli(desktopOwnsWindow: true), true, cancellationToken);
    }

    private static async Task RunLoginCommandAsync(AccountProfile account, Action<LoginChallenge> onChallenge, IOfficialCli runner, bool windows, CancellationToken cancellationToken)
    {
        var arguments = LoginArguments(account, windows);
        var output = new StringBuilder();
        var announced = false;
        await runner.RunAsync(arguments, chunk =>
        {
            // The CLI emits a human-readable challenge even with --json. Never forward
            // other CLI output: it may contain identity information or credentials.
            if (announced || output.Length > 8192) return;
            output.Append(chunk);
            var match = Regex.Match(output.ToString(), @"(https://(?:login\.microsoft\.com/device|github\.com/login/device))[^\r\n]*?enter the code\s+([A-Z0-9-]{6,15})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return;
            // Do not silently turn a requested interactive/silent login into
            // device code. The user must explicitly select that flow.
            // GitHub's browser flow can legitimately carry a device challenge.
            if (account.Provider == AccountProvider.MicrosoftCli && account.CliLoginMode != CliLoginMode.DeviceCode)
                throw new ToolkitException(output.ToString().Contains("window handle must be configured", StringComparison.OrdinalIgnoreCase)
                    ? "AUTH_WINDOW_UNAVAILABLE" : "AUTH_FLOW_CHANGED");
            announced = true;
            onChallenge(new(match.Groups[1].Value, match.Groups[2].Value, null));
        }, cancellationToken);
    }

    private async Task<AccountProfile> LoginCoreAsync(AccountProfile account, Func<CancellationToken, Task> authenticate, CancellationToken cancellationToken)
    {
        RequireCliAccount(account);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await authenticate(cancellationToken);
            var identity = await CurrentIdentityAsync(cancellationToken);
            EnsureExpected(account, identity);
            var now = DateTimeOffset.UtcNow;
            return account with
            {
                Username = identity.Username,
                StableIdentityId = identity.StableId,
                TenantId = identity.TenantId,
                AuthenticatedAt = now,
                ExpiresAt = null,
                LastValidatedAt = now,
                Status = "Authenticated"
            };
        }
        finally { gate.Release(); }
    }

    public async Task<AccountProfile> ValidateAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        RequireCliAccount(account);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var identity = await CurrentIdentityAsync(cancellationToken);
            EnsureExpected(account, identity);
            return account with
            {
                Username = identity.Username,
                StableIdentityId = identity.StableId,
                TenantId = identity.TenantId,
                LastValidatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = null,
                Status = "Authenticated"
            };
        }
        finally { gate.Release(); }
    }

    public Task LogoutAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        // Never log out the shared official CLI when forgetting an application profile.
        // Other tools may be using that same OS-keychain entry.
        return Task.CompletedTask;
    }

    public Task<string> GetAuthorizationAsync(AccountProfile account, CancellationToken cancellationToken)
        => throw new ToolkitException("CLI_ACCOUNT_TOKEN_NOT_EXPORTABLE");

    public async Task<string> RunAsAsync(AccountProfile account, string[] arguments, CancellationToken cancellationToken)
    {
        RequireCliAccount(account);
        if (account.StableIdentityId is null) throw new ToolkitException("AUTHENTICATION_REQUIRED");
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureExpected(account, await CurrentIdentityAsync(cancellationToken));
            var output = await cli.RunAsync(arguments, null, cancellationToken);
            EnsureExpected(account, await CurrentIdentityAsync(cancellationToken));
            return output;
        }
        finally { gate.Release(); }
    }

    private async Task<CliIdentity> CurrentIdentityAsync(CancellationToken cancellationToken)
    {
        using var json = OfficialCliJson.Parse(await cli.RunAsync(["user", "show", "--json"], null, cancellationToken));
        return ParseIdentity(json.RootElement);
    }

    internal static CliIdentity ParseIdentity(JsonElement value)
    {
        var status = OfficialCliJson.String(value, "status");
        if (status is not null && status.Contains("expired", StringComparison.OrdinalIgnoreCase)) throw new ToolkitException("AUTHENTICATION_EXPIRED");
        if (string.Equals(status, "Not logged in", StringComparison.OrdinalIgnoreCase)) throw new ToolkitException("AUTHENTICATION_REQUIRED");
        var user = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("user", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : value;
        var provider = (OfficialCliJson.String(user, "provider") ?? OfficialCliJson.String(value, "provider") ?? "").ToLowerInvariant();
        if (provider == "entra") provider = "microsoft";
        if (provider is not ("github" or "microsoft")) throw new ToolkitException("CLI_IDENTITY_SCHEMA_UNSUPPORTED");
        var username = OfficialCliJson.String(user, "username") ?? OfficialCliJson.String(user, "userName") ?? OfficialCliJson.String(user, "login");
        var id = OfficialCliJson.String(user, "objectId") ?? OfficialCliJson.String(user, "userId") ?? OfficialCliJson.String(user, "id");
        if (id is null && user.ValueKind == JsonValueKind.Object && user.TryGetProperty("id", out var numericId) && numericId.ValueKind == JsonValueKind.Number) id = numericId.ToString();
        var tenant = OfficialCliJson.String(user, "tenantId");
        if (string.IsNullOrWhiteSpace(username) || username.Length > 320 || username.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl) ||
            tenant is { Length: > 0 } && !Guid.TryParse(tenant, out _))
            throw new ToolkitException("CLI_IDENTITY_SCHEMA_UNSUPPORTED");
        return new(provider, username, $"{provider}:{(tenant is null ? "" : tenant + ":")}{id}", tenant);
    }

    private static void RequireCliAccount(AccountProfile account)
    {
        Validation.Id(account.Id);
        if (account.Provider is not (AccountProvider.MicrosoftCli or AccountProvider.GitHubCli)) throw new ToolkitException("AUTH_PROVIDER_UNSUPPORTED");
    }

    private static void EnsureExpected(AccountProfile account, CliIdentity identity)
    {
        var expectedProvider = account.Provider == AccountProvider.GitHubCli ? "github" : "microsoft";
        if (identity.Provider != expectedProvider) throw new ToolkitException("CLI_ACCOUNT_CHANGED");
        if (account.StableIdentityId is not null && account.StableIdentityId != identity.StableId)
            throw new ToolkitException("CLI_ACCOUNT_CHANGED");
    }
}

public sealed class HybridAuthenticationService(NativeAuthenticationService native, CliAuthenticationService cli) : IAuthenticationService, IAuthenticationMetadataProvider, IDesktopAuthenticationService, IDisposable
{
    public Task<AccountProfile> LoginFromDesktopAsync(AccountProfile account, Func<DesktopLoginRequest, CancellationToken, Task> authenticate, CancellationToken cancellationToken)
        => cli.LoginFromDesktopAsync(account, authenticate, cancellationToken);
    private static bool IsCli(AccountProfile account) => account.Provider is AccountProvider.MicrosoftCli or AccountProvider.GitHubCli;
    public DateTimeOffset? GetObservedExpiration(string accountId) => native.GetObservedExpiration(accountId);
    public Task<AccountProfile> LoginAsync(AccountProfile account, Action<LoginChallenge> onChallenge, CancellationToken cancellationToken)
        => IsCli(account) ? cli.LoginAsync(account, onChallenge, cancellationToken) : native.LoginAsync(account, onChallenge, cancellationToken);
    public Task<AccountProfile> ValidateAsync(AccountProfile account, CancellationToken cancellationToken)
        => IsCli(account) ? cli.ValidateAsync(account, cancellationToken) : native.ValidateAsync(account, cancellationToken);
    public Task LogoutAsync(AccountProfile account, CancellationToken cancellationToken)
        => IsCli(account) ? cli.LogoutAsync(account, cancellationToken) : native.LogoutAsync(account, cancellationToken);
    public Task<string> GetAuthorizationAsync(AccountProfile account, CancellationToken cancellationToken)
        => IsCli(account) ? cli.GetAuthorizationAsync(account, cancellationToken) : native.GetAuthorizationAsync(account, cancellationToken);
    public void Dispose() => native.Dispose();
}
