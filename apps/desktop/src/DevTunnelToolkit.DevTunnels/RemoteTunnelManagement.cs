using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevTunnelToolkit.Core;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;

namespace DevTunnelToolkit.DevTunnels;

/// <summary>Account-bound remote administration. No implicit last-used tunnel or credential cache access.</summary>
public sealed class RemoteTunnelManagement : IRemoteTunnelManagementService
{
    private readonly IAuthenticationService authentication;
    private readonly CliAuthenticationService cli;
    private readonly Func<Func<Task<AuthenticationHeaderValue?>>, TunnelManagementClient> factory;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] ValidScopes = ["create", "manage", "manage:ports", "host", "inspect", "connect"];

    public RemoteTunnelManagement(IAuthenticationService authentication, CliAuthenticationService cli)
        : this(authentication, cli, authorize => new(new ProductInfoHeaderValue("DevTunnelToolkit", "1.0"), authorize,
            tunnelServiceUri: null, httpHandler: null))
    { }

    internal RemoteTunnelManagement(IAuthenticationService authentication, CliAuthenticationService cli,
        Func<Func<Task<AuthenticationHeaderValue?>>, TunnelManagementClient> factory)
    {
        this.authentication = authentication;
        this.cli = cli;
        this.factory = factory;
    }

    private static bool UsesCli(AccountProfile account) => account.Provider is AccountProvider.MicrosoftCli or AccountProvider.GitHubCli;
    private static void RequireAccount(AccountProfile account)
    {
        Validation.Account(account);
        if (account.Provider == AccountProvider.TunnelToken) throw new ToolkitException("REMOTE_ACCOUNT_REQUIRED");
        if (string.IsNullOrEmpty(account.StableIdentityId)) throw new ToolkitException("AUTHENTICATION_REQUIRED");
    }

    internal static void ValidateTarget(RemoteTunnelTarget target)
        => Validation.Tunnel(new() { AccountId = new string('0', 32), TunnelId = target.TunnelId, ClusterId = target.ClusterId });

    private static Tunnel Target(RemoteTunnelTarget target) => new() { TunnelId = target.TunnelId, ClusterId = target.ClusterId };
    private static TunnelRequestOptions ReadOptions() => new() { IncludePorts = true, IncludeAccessControl = true };

    private async Task<TunnelManagementClient> ClientAsync(AccountProfile account, RemoteTunnelTarget? target, CancellationToken cancellationToken)
    {
        RequireAccount(account);
        if (target is not null) ValidateTarget(target);
        Func<Task<AuthenticationHeaderValue?>> authorization;
        if (UsesCli(account) && target is not null)
        {
            // The CLI remains owner of the account login. Only a scoped token crosses into the SDK.
            using var result = OfficialCliJson.Parse(await cli.RunAsAsync(account,
                ["token", target.CanonicalId, "--scopes", "manage", "--json"], cancellationToken));
            var token = ReadToken(result.RootElement);
            authorization = () => Task.FromResult<AuthenticationHeaderValue?>(new("tunnel", token));
        }
        else if (UsesCli(account))
        {
            await cli.ValidateAsync(account, cancellationToken);
            authorization = () => Task.FromResult<AuthenticationHeaderValue?>(null); // public cluster metadata only
        }
        else authorization = async () => AuthenticationHeaderValue.Parse(await authentication.GetAuthorizationAsync(account, cancellationToken));
        var client = factory(authorization);
        client.EnableEventsReporting = false;
        return client;
    }

    public Task<ManagedTunnel[]> ListAsync(AccountProfile account, CancellationToken cancellationToken) => SafeAsync(async () =>
    {
        RequireAccount(account);
        if (UsesCli(account))
        {
            using var json = OfficialCliJson.Parse(await cli.RunAsAsync(account, ["list", "--limit", "1000", "--json"], cancellationToken));
            return ParseList(json.RootElement);
        }
        await using var client = await ClientAsync(account, null, cancellationToken);
        var tunnels = await client.ListTunnelsAsync(null, null, ReadOptions(), true, cancellationToken);
        if (tunnels.Length > 1000) throw new ToolkitException("REMOTE_LIST_LIMIT_REACHED");
        return tunnels.Select(MapTunnel).ToArray();
    });

    internal static ManagedTunnel[] ParseList(JsonElement value)
    {
        var list = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("tunnels", out var items) ? items : value;
        if (list.ValueKind != JsonValueKind.Array) throw new ToolkitException("CLI_TUNNEL_LIST_SCHEMA_UNSUPPORTED");
        if (list.GetArrayLength() >= 1000) throw new ToolkitException("REMOTE_LIST_LIMIT_REACHED");
        var result = new List<ManagedTunnel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in list.EnumerateArray())
        {
            var tunnel = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("tunnel", out var nested) ? nested : element;
            var key = CliTunnelService.ParseTunnelKeys(JsonSerializer.SerializeToElement(new[] { tunnel })).Single();
            var parsed = tunnel.Deserialize<Tunnel>(TunnelContracts.JsonOptions) ?? throw new ToolkitException("CLI_TUNNEL_LIST_SCHEMA_UNSUPPORTED");
            parsed.TunnelId = key.TunnelId; parsed.ClusterId = key.ClusterId;
            if (seen.Add($"{key.TunnelId}.{key.ClusterId}")) result.Add(MapTunnel(parsed));
        }
        // List results need not include every port/ACL. GetAsync refreshes the full detail before editing.
        return result.ToArray();
    }

    public Task<ManagedTunnel> GetAsync(AccountProfile account, RemoteTunnelTarget target, CancellationToken cancellationToken) => SafeAsync(async () =>
    {
        await using var client = await ClientAsync(account, target, cancellationToken);
        return MapTunnel(await ReadAsync(client, target, cancellationToken));
    });

    public Task<ManagedTunnel> CreateAsync(AccountProfile account, RemoteTunnelSpec spec, CancellationToken cancellationToken) => WriteAsync(async () =>
    {
        RequireAccount(account);
        ValidateSpec(spec, null);
        if (UsesCli(account))
        {
            using var json = OfficialCliJson.Parse(await cli.RunAsAsync(account, CreateArguments(spec), cancellationToken));
            var value = json.RootElement.TryGetProperty("tunnel", out var nested) ? nested : json.RootElement;
            var id = OfficialCliJson.String(value, "tunnelId") ?? OfficialCliJson.String(value, "id");
            var cluster = OfficialCliJson.String(value, "clusterId");
            if (id?.Split('.') is { Length: 2 } parts && (cluster is null || cluster == parts[1])) { id = parts[0]; cluster = parts[1]; }
            if (id is null || cluster is null) throw new ToolkitException("REMOTE_CREATED_REFRESH_REQUIRED");
            var target = new RemoteTunnelTarget(id, cluster);
            ValidateTarget(target);
            try
            {
                await using var client = await ClientAsync(account, target, cancellationToken);
                if (!string.IsNullOrEmpty(spec.Name))
                {
                    var created = Target(target);
                    created.Name = spec.Name;
                    await client.UpdateTunnelAsync(created, null, cancellationToken);
                }
                return MapTunnel(await ReadAsync(client, target, cancellationToken));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new ToolkitException("REMOTE_CREATED_REFRESH_REQUIRED"); }
        }
        await using var native = await ClientAsync(account, null, cancellationToken);
        return MapTunnel(await native.CreateTunnelAsync(new()
        {
            TunnelId = spec.TunnelId,
            ClusterId = spec.ClusterId,
            Name = spec.Name,
            Description = spec.Description,
            Labels = spec.Labels,
            CustomExpiration = (uint?)spec.ExpirationSeconds,
            AccessControl = new(), // Private owner-only by default.
            Options = ApplyWebOptions(null, spec.WebOptions)
        }, null, cancellationToken));
    }, cancellationToken);

    internal static string[] CreateArguments(RemoteTunnelSpec spec)
    {
        ValidateSpec(spec, null);
        var arguments = new List<string> { "create" };
        if (spec.TunnelId is { Length: > 0 } id) arguments.Add(spec.ClusterId is { Length: > 0 } cluster ? $"{id}.{cluster}" : id);
        else if (spec.ClusterId is { Length: > 0 }) throw new ToolkitException("REMOTE_CLUSTER_REQUIRES_ID");
        // Equals syntax prevents a literal value such as --allow-anonymous from being
        // interpreted as a CLI option, even though the process never uses a shell.
        if (spec.Description.Length > 0) arguments.Add("--description=" + spec.Description);
        if (spec.Labels.Length > 0) { arguments.Add("--labels"); arguments.AddRange(spec.Labels); }
        if (spec.ExpirationSeconds is { } expiration)
        {
            // The official CLI only accepts whole hours or days; never silently round the user's lifetime.
            if (expiration % 3600 != 0) throw new ToolkitException("REMOTE_EXPIRATION_WHOLE_HOURS_REQUIRED");
            arguments.AddRange(["--expiration", (expiration / 3600).ToString(CultureInfo.InvariantCulture) + "h"]);
        }
        if (spec.WebOptions is { } web)
        {
            if (web.PreserveHostHeader == true) arguments.Add("--host-header=unchanged");
            else if (web.HostHeader is { Length: > 0 } host) arguments.Add("--host-header=" + host);
            if (web.PreserveOriginHeader == true) arguments.Add("--origin-header=unchanged");
            else if (web.OriginHeader is { Length: > 0 } origin) arguments.Add("--origin-header=" + origin);
            if (web.RequestTimeoutSeconds is { } timeout) arguments.AddRange(["--request-timeout", timeout.ToString(CultureInfo.InvariantCulture)]);
        }
        arguments.Add("--json");
        return arguments.ToArray();
    }

    public Task<ManagedTunnel> UpdateAsync(AccountProfile account, RemoteTunnelTarget target, RemoteTunnelSpec spec, CancellationToken cancellationToken) => WriteAsync(async () =>
    {
        ValidateSpec(spec, target);
        await using var client = await ClientAsync(account, target, cancellationToken);
        var existing = await ReadAsync(client, target, cancellationToken);
        var tunnel = Target(target);
        tunnel.Name = spec.Name;
        tunnel.Description = spec.Description;
        tunnel.Labels = spec.Labels;
        if (spec.ExpirationSeconds is { } expiration) tunnel.CustomExpiration = (uint)expiration;
        tunnel.Options = spec.WebOptions is null ? null : ApplyWebOptions(existing.Options, spec.WebOptions);
        // Non-null fields are a patch. Do not resubmit unrelated ACL/ports/endpoints from
        // our read: another administrator may have changed them since the request began.
        await client.UpdateTunnelAsync(tunnel, null, cancellationToken);
        return MapTunnel(await ReadAsync(client, target, cancellationToken));
    }, cancellationToken);

    public Task DeleteAsync(AccountProfile account, RemoteTunnelTarget target, string confirmation, CancellationToken cancellationToken) => WriteAsync(async () =>
    {
        Confirm(target, confirmation);
        await using var client = await ClientAsync(account, target, cancellationToken);
        await ReadAsync(client, target, cancellationToken);
        if (!await client.DeleteTunnelAsync(Target(target), null, cancellationToken)) throw new ToolkitException("TUNNEL_NOT_FOUND");
        return true;
    }, cancellationToken);

    public Task<ManagedPort> SavePortAsync(AccountProfile account, RemoteTunnelTarget target, RemotePortSpec port, bool create, CancellationToken cancellationToken) => WriteAsync(async () =>
    {
        ValidatePort(port.PortNumber);
        Text(port.Description, 4000);
        Labels(port.Labels ?? []);
        ValidateWebOptions(port.WebOptions);
        if (port.Protocol is not ("auto" or "http" or "https" or "ssh" or "tcp")) throw new ToolkitException("REMOTE_PROTOCOL_INVALID");
        await using var client = await ClientAsync(account, target, cancellationToken);
        var tunnel = await ReadAsync(client, target, cancellationToken);
        var existing = await ReadPortAsync(client, tunnel, port.PortNumber, null, cancellationToken);
        if (create && existing is not null) throw new ToolkitException("REMOTE_PORT_EXISTS");
        if (!create && existing is null) throw new ToolkitException("REMOTE_PORT_NOT_FOUND");
        var value = new TunnelPort { PortNumber = (ushort)port.PortNumber };
        value.Protocol = port.Protocol; value.Description = port.Description; value.Labels = port.Labels ?? [];
        value.Options = port.WebOptions is null ? null : ApplyWebOptions(existing?.Options, port.WebOptions);
        var saved = create ? await client.CreateTunnelPortAsync(tunnel, value, null, cancellationToken)
            : await client.UpdateTunnelPortAsync(tunnel, value, null, cancellationToken);
        return MapPort(saved);
    }, cancellationToken);

    public Task DeletePortAsync(AccountProfile account, RemoteTunnelTarget target, int portNumber, string confirmation, CancellationToken cancellationToken) => WriteAsync(async () =>
    {
        ValidatePort(portNumber); Confirm(target, confirmation);
        await using var client = await ClientAsync(account, target, cancellationToken);
        var tunnel = await ReadAsync(client, target, cancellationToken);
        if (!await client.DeleteTunnelPortAsync(tunnel, (ushort)portNumber, null, cancellationToken)) throw new ToolkitException("REMOTE_PORT_NOT_FOUND");
        return true;
    }, cancellationToken);

    public Task<RemoteAccessEntry[]> AddAccessAsync(AccountProfile account, RemoteTunnelTarget target, int? portNumber, RemoteAccessEntry entry, CancellationToken cancellationToken)
    {
        var ace = ToEntry(entry, portNumber is not null);
        return ChangeAccessAsync(account, target, portNumber, entries =>
        {
            if (entries.Length >= 100) throw new ToolkitException("REMOTE_ACCESS_LIMIT_REACHED");
            if (entries.Any(item => EntryId(item) == EntryId(ace))) throw new ToolkitException("REMOTE_ACCESS_EXISTS");
            var index = entry.InsertIndex ?? entries.Length;
            if (index < 0 || index > entries.Length) throw new ToolkitException("REMOTE_ACCESS_INDEX_INVALID");
            return [.. entries.Take(index), ace, .. entries.Skip(index)];
        }, cancellationToken);
    }

    public Task<RemoteAccessEntry[]> RemoveAccessAsync(AccountProfile account, RemoteTunnelTarget target, int? portNumber, string entryId, string confirmation, CancellationToken cancellationToken)
    {
        Confirm(target, confirmation);
        if (entryId is null || entryId.Length != 64 || !entryId.All(char.IsAsciiHexDigit)) throw new ToolkitException("REMOTE_ACCESS_ID_INVALID");
        return ChangeAccessAsync(account, target, portNumber, entries =>
        {
            var matches = entries.Where(item => EntryId(item) == entryId).ToArray();
            if (matches.Length != 1) throw new ToolkitException("REMOTE_ACCESS_CHANGED_REFRESH_REQUIRED");
            if (matches[0].IsInherited) throw new ToolkitException("REMOTE_ACCESS_INHERITED");
            return entries.Where(item => EntryId(item) != entryId).ToArray();
        }, cancellationToken);
    }

    public Task<RemoteAccessEntry[]> ResetAccessAsync(AccountProfile account, RemoteTunnelTarget target, int? portNumber, string confirmation, CancellationToken cancellationToken)
    {
        Confirm(target, confirmation);
        return ChangeAccessAsync(account, target, portNumber, entries => entries.Where(entry => entry.IsInherited).ToArray(), cancellationToken);
    }

    private Task<RemoteAccessEntry[]> ChangeAccessAsync(AccountProfile account, RemoteTunnelTarget target, int? portNumber,
        Func<TunnelAccessControlEntry[], TunnelAccessControlEntry[]> change, CancellationToken cancellationToken) => WriteAsync(async () =>
    {
        if (portNumber is { } number) ValidatePort(number);
        await using var client = await ClientAsync(account, target, cancellationToken);
        var tunnel = await ReadAsync(client, target, cancellationToken);
        if (portNumber is { } port)
        {
            var existing = await ReadPortAsync(client, tunnel, port, ReadOptions(), cancellationToken)
                ?? throw new ToolkitException("REMOTE_PORT_NOT_FOUND");
            var updated = new TunnelPort { PortNumber = (ushort)port, AccessControl = new(change(existing.AccessControl?.Entries?.ToArray() ?? [])) };
            var result = await client.UpdateTunnelPortAsync(tunnel, updated, null, cancellationToken);
            return MapAccess(result.AccessControl);
        }
        var patch = Target(target);
        patch.AccessControl = new(change(tunnel.AccessControl?.Entries?.ToArray() ?? []));
        return MapAccess((await client.UpdateTunnelAsync(patch, null, cancellationToken)).AccessControl);
    }, cancellationToken);

    public Task<RemoteTokenResult> IssueTokenAsync(AccountProfile account, RemoteTunnelTarget target, RemoteTokenRequest request, CancellationToken cancellationToken) => SafeAsync(async () =>
    {
        ValidateScopes(request.Scopes, request.PortNumber is not null);
        if (request.PortNumber is { } number) ValidatePort(number);
        await using var client = await ClientAsync(account, target, cancellationToken);
        var scopes = string.Join(' ', request.Scopes);
        var options = new TunnelRequestOptions { TokenScopes = [scopes] };
        var tunnel = Target(target);
        var tokens = request.PortNumber is { } port
            ? (await ReadPortAsync(client, tunnel, port, options, cancellationToken))?.AccessTokens
            : (await client.GetTunnelAsync(tunnel, options, cancellationToken))?.AccessTokens;
        if (tokens is null || !tokens.TryGetValue(scopes, out var token)) throw new ToolkitException("REMOTE_TOKEN_SCOPE_DENIED");
        ValidateToken(token);
        var expires = TokenExpiration(token);
        return new RemoteTokenResult(token, request.Scopes.ToArray(), expires, expires is null ? "NotReported" : "TokenClaim");
    });

    public Task<RemoteCluster[]> ListClustersAsync(AccountProfile account, CancellationToken cancellationToken) => SafeAsync(async () =>
    {
        await using var client = await ClientAsync(account, null, cancellationToken);
        var clusters = await client.ListClustersAsync(cancellationToken);
        if (clusters.Length > 200) throw new ToolkitException("REMOTE_RESPONSE_INVALID");
        return clusters.Select(cluster => new RemoteCluster(cluster.ClusterId, cluster.AzureLocation, cluster.Uri)).ToArray();
    });

    public async Task<RemoteDiagnostic> DiagnoseAsync(AccountProfile account, RemoteTunnelTarget target, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try { await GetAsync(account, target, cancellationToken); return new("ManagementApi", true, timer.ElapsedMilliseconds); }
        catch (ToolkitException error) { return new("ManagementApi", false, timer.ElapsedMilliseconds, error.Code); }
    }

    private static async Task<Tunnel> ReadAsync(ITunnelManagementClient client, RemoteTunnelTarget target, CancellationToken cancellationToken)
    {
        var result = await client.GetTunnelAsync(Target(target), ReadOptions(), cancellationToken) ?? throw new ToolkitException("TUNNEL_NOT_FOUND");
        if (result.TunnelId != target.TunnelId || result.ClusterId != target.ClusterId) throw new ToolkitException("REMOTE_TARGET_MISMATCH");
        return result;
    }

    private static async Task<TunnelPort?> ReadPortAsync(ITunnelManagementClient client, Tunnel tunnel, int port,
        TunnelRequestOptions? options, CancellationToken cancellationToken)
    {
        try { return await client.GetTunnelPortAsync(tunnel, (ushort)port, options, cancellationToken); }
        catch (Exception error) when (ErrorCode(error) == "TUNNEL_NOT_FOUND") { return null; }
    }

    private async Task<T> WriteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try { return await SafeAsync(action); }
        finally { writeGate.Release(); }
    }

    private static async Task<T> SafeAsync<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (ToolkitException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw new ToolkitException(ErrorCode(error)); }
    }

    // The SDK wraps several HTTP failures in InvalidOperationException and even response
    // parsing errors in Exception. Never send those raw messages or inner bodies to IPC/logs.
    private static string ErrorCode(Exception error)
    {
        var fallback = error is UnauthorizedAccessException ? "REMOTE_ACCESS_DENIED" : error is ArgumentException ? "REMOTE_REQUEST_INVALID" : "REMOTE_SERVICE_UNAVAILABLE";
        for (var depth = 0; error is not null && depth < 8; depth++, error = error.InnerException!)
        {
            if (error is JsonException) return "REMOTE_RESPONSE_INVALID";
            if (error is HttpRequestException http) return http.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "AUTHENTICATION_REQUIRED",
                HttpStatusCode.Forbidden => "REMOTE_ACCESS_DENIED",
                HttpStatusCode.NotFound => "TUNNEL_NOT_FOUND",
                HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => "REMOTE_CONFLICT_REFRESH_REQUIRED",
                HttpStatusCode.TooManyRequests => "REMOTE_RATE_LIMITED",
                _ => "REMOTE_SERVICE_UNAVAILABLE"
            };
        }
        return fallback;
    }

    internal static void ValidateSpec(RemoteTunnelSpec spec, RemoteTunnelTarget? target)
    {
        if (target is not null)
        {
            ValidateTarget(target);
            if (spec.TunnelId is not null && spec.TunnelId != target.TunnelId || spec.ClusterId is not null && spec.ClusterId != target.ClusterId)
                throw new ToolkitException("REMOTE_ID_IMMUTABLE");
        }
        if (spec.TunnelId is { } id) ValidateTarget(new(id, spec.ClusterId ?? "use2"));
        if (spec.ClusterId is { } cluster) ValidateTarget(new(spec.TunnelId ?? "example", cluster));
        if (spec.Name is { Length: > 0 } name && !TunnelConstraints.TunnelNameRegex.IsMatch(name)) throw new ToolkitException("REMOTE_NAME_INVALID");
        Text(spec.Description, 4000); Labels(spec.Labels);
        if (spec.ExpirationSeconds is < 3600 or > 2592000) throw new ToolkitException("REMOTE_EXPIRATION_INVALID");
        ValidateWebOptions(spec.WebOptions);
    }

    private static void ValidateWebOptions(RemoteWebOptions? options)
    {
        if (options is null) return;
        if (options.RequestTimeoutSeconds is < 0 or > 3600) throw new ToolkitException("REMOTE_TIMEOUT_INVALID");
        if (options.HostHeader is { } host)
        {
            Text(host, 256);
            if (host.Length > 0 && (host.Any(char.IsWhiteSpace) || host.IndexOfAny(['/', '?', '#', '@']) >= 0 ||
                !Uri.TryCreate("http://" + host, UriKind.Absolute, out _))) throw new ToolkitException("REMOTE_HOST_HEADER_INVALID");
        }
        if (options.OriginHeader is { } origin)
        {
            Text(origin, 2048);
            if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
                uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)) throw new ToolkitException("REMOTE_ORIGIN_HEADER_INVALID");
        }
    }

    private static TunnelOptions? ApplyWebOptions(TunnelOptions? options, RemoteWebOptions? updates)
    {
        if (updates is null) return options;
        options ??= new();
        if (updates.HostHeader is { } host) options.HostHeader = host;
        if (updates.OriginHeader is { } origin) options.OriginHeader = origin;
        if (updates.PreserveHostHeader is { } keepHost) options.IsHostHeaderUnchanged = keepHost;
        if (updates.PreserveOriginHeader is { } keepOrigin) options.IsOriginHeaderUnchanged = keepOrigin;
        if (updates.RequestTimeoutSeconds is { } timeout) options.RequestTimeoutSeconds = timeout;
        return options;
    }

    private static RemoteWebOptions? MapWebOptions(TunnelOptions? options) => options is null ? null : new(options.HostHeader,
        options.IsHostHeaderUnchanged, options.OriginHeader, options.IsOriginHeaderUnchanged, options.RequestTimeoutSeconds);

    private static void Text(string? value, int maximum)
    {
        if (value is null || value.Length > maximum || value.Any(char.IsControl)) throw new ToolkitException("REMOTE_TEXT_INVALID");
    }

    private static void Labels(string[] values)
    {
        if (values is null || values.Length > 20 || values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new ToolkitException("REMOTE_LABELS_INVALID");
        foreach (var value in values)
            if (string.IsNullOrEmpty(value) || value.Length > 50 || !TunnelConstraints.LabelRegex.IsMatch(value) || value[0] == '-') throw new ToolkitException("REMOTE_LABELS_INVALID");
    }

    private static void ValidatePort(int port) { if (port is < 1 or > 65535) throw new ToolkitException("INVALID_PORT"); }
    private static void Confirm(RemoteTunnelTarget target, string confirmation)
    {
        ValidateTarget(target);
        if (!string.Equals(target.CanonicalId, confirmation, StringComparison.Ordinal)) throw new ToolkitException("REMOTE_CONFIRMATION_REQUIRED");
    }

    private static void ValidateScopes(string[] scopes, bool port)
    {
        if (scopes is null || scopes.Length is < 1 or > 6 || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Length ||
            scopes.Any(scope => !ValidScopes.Contains(scope, StringComparer.Ordinal) || scope == "create" || port && scope is "host" or "manage:ports"))
            throw new ToolkitException("REMOTE_SCOPE_INVALID");
    }

    internal static TunnelAccessControlEntry ToEntry(RemoteAccessEntry entry, bool port)
    {
        if (entry.InsertIndex is < 0 or > 100) throw new ToolkitException("REMOTE_ACCESS_INDEX_INVALID");
        if (!Enum.TryParse<TunnelAccessControlEntryType>(entry.Type, false, out var type) || type == TunnelAccessControlEntryType.None ||
            !Enum.IsDefined(type) || entry.IsInherited) throw new ToolkitException("REMOTE_ACCESS_INVALID");
        ValidateScopes(entry.Scopes, port);
        if (entry.Subjects is null || entry.Subjects.Length > 100) throw new ToolkitException("REMOTE_ACCESS_INVALID");
        foreach (var subject in entry.Subjects) { Text(subject, 512); if (subject.Length == 0) throw new ToolkitException("REMOTE_ACCESS_INVALID"); }
        if (entry.Provider is { } provider) Text(provider, 12);
        if (entry.Organization is { } organization) Text(organization, 256);
        if (type == TunnelAccessControlEntryType.Anonymous && (entry.Subjects.Length != 0 || entry.Provider is not null || entry.Scopes.Any(scope => scope != "connect")))
            throw new ToolkitException("REMOTE_ANONYMOUS_CONNECT_ONLY");
        if (type != TunnelAccessControlEntryType.Anonymous && (entry.Subjects.Length == 0 || string.IsNullOrEmpty(entry.Provider))) throw new ToolkitException("REMOTE_ACCESS_INVALID");
        if (entry.ExpiresAt is { } expires && (expires <= DateTimeOffset.UtcNow || expires > DateTimeOffset.UtcNow.AddDays(30))) throw new ToolkitException("REMOTE_EXPIRATION_INVALID");
        return new()
        {
            Type = type,
            Provider = entry.Provider is "aad" or "entra" ? TunnelAccessControlEntry.Providers.Microsoft : entry.Provider,
            Organization = entry.Organization,
            Subjects = entry.Subjects.ToArray(),
            Scopes = entry.Scopes.ToArray(),
            IsDeny = entry.IsDeny,
            IsInverse = entry.IsInverse,
            Expiration = entry.ExpiresAt?.UtcDateTime
        };
    }

    private static string EntryId(TunnelAccessControlEntry entry) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry, Json)));
    private static DateTimeOffset? Utc(DateTime? value) => value is { } date ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : null;
    private static RemoteAccessEntry[] MapAccess(TunnelAccessControl? access) => access?.Entries?.Select(entry => new RemoteAccessEntry
    {
        EntryId = EntryId(entry),
        Type = entry.Type.ToString(),
        Provider = entry.Provider,
        Organization = entry.Organization,
        Subjects = entry.Subjects?.ToArray() ?? [],
        Scopes = entry.Scopes?.ToArray() ?? [],
        IsDeny = entry.IsDeny,
        IsInverse = entry.IsInverse,
        IsInherited = entry.IsInherited,
        ExpiresAt = Utc(entry.Expiration)
    }).ToArray() ?? [];
    private static ManagedPort MapPort(TunnelPort port) => new(port.PortNumber, port.Protocol, port.Description, port.Labels?.ToArray() ?? [], MapAccess(port.AccessControl), MapWebOptions(port.Options));
    private static ManagedTunnel MapTunnel(Tunnel tunnel) => new(new(tunnel.TunnelId ?? "", tunnel.ClusterId ?? ""), tunnel.Name, tunnel.Description,
        tunnel.Labels?.ToArray() ?? [], Utc(tunnel.Created), Utc(tunnel.Expiration), checked((int?)tunnel.CustomExpiration),
        tunnel.Status?.HostConnectionCount is { } hosts ? hosts.Current > 0 : null, tunnel.Ports?.Select(MapPort).ToArray() ?? [], MapAccess(tunnel.AccessControl), MapWebOptions(tunnel.Options));
    private static string ReadToken(JsonElement value)
    {
        var token = OfficialCliJson.String(value, "token") ?? OfficialCliJson.String(value, "accessToken");
        ValidateToken(token);
        return token!;
    }
    private static void ValidateToken(string? token)
    {
        if (token is null || token.Length is < 20 or > 32768 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl)) throw new ToolkitException("CLI_TOKEN_SCHEMA_UNSUPPORTED");
    }
    internal static DateTimeOffset? TokenExpiration(string token)
    {
        try { return Utc(TunnelAccessTokenProperties.TryParse(token)?.Expiration); }
        catch (Exception error) when (error is FormatException or ArgumentException or JsonException) { return null; }
    }
}
