using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Networking;

namespace DevTunnelToolkit.Application;

public sealed partial class AgentRuntime(IStateStore store, ICredentialVault vault, IAuthenticationService authentication, ITunnelService tunnels, PlatformCapabilities capabilities, IUserStartupManager? startup = null,
    IRemoteTunnelManagementService? remoteManagement = null, IHostingService? hosting = null, ManagedServiceRegistry? serviceRegistry = null) : IAsyncDisposable
{
    private sealed record RunningOperation(CancellationTokenSource Cancellation, Task Completion);
    private readonly SemaphoreSlim mutation = new(1, 1);
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, RunningOperation> running = [];
    private readonly Dictionary<string, RunningOperation> loggingIn = [];
    private readonly ConcurrentDictionary<string, ConnectionStatus> connections = new();
    private readonly ConcurrentDictionary<string, LoginStatus> logins = new();
    private readonly Queue<LogEntry> recentLogs = new();
    private readonly Channel<LogEntry> logQueue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private ToolkitState state = ToolkitState.Empty;
    private Task logWriter = Task.CompletedTask;
    private long logSequence;
    private LocalProxyService? proxy;
    private string? proxyError;
    private PacService? pac;
    private string? pacError;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        state = await store.LoadAsync(cancellationToken);
        var migrated = ServiceMigration.Upgrade(state);
        if (!ReferenceEquals(migrated, state)) await store.SaveAsync(migrated, cancellationToken);
        state = migrated;
        services = serviceRegistry ?? new ManagedServiceRegistry(new PublishedProxyFactory(RecordHostedProxyEvent));
        foreach (var entry in await store.ReadLogsAsync(200, cancellationToken)) { recentLogs.Enqueue(entry); logSequence = Math.Max(logSequence, entry.Sequence); }
        await store.PruneLogsAsync(state.Settings.LogRetentionDays, cancellationToken);
        logWriter = WriteLogsAsync();
        if (startup?.Supported == true)
        {
            try { state = state with { Settings = state.Settings with { StartWithUserSession = await startup.IsEnabledAsync(cancellationToken) } }; }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                Log("Warning", "STARTUP_STATUS_UNAVAILABLE", "Startup status could not be checked. Tunnel management remains available.");
            }
        }
        if (state.Settings.Proxy.Enabled)
        {
            var service = CreateProxy();
            try { service.Start(state.Settings.Proxy); proxy = service; }
            catch (ToolkitException error) { await service.DisposeAsync(); proxyError = error.Code; Log("Warning", error.Code, "Local proxy could not start."); }
        }
        if (state.Settings.Pac.Enabled)
        {
            var service = new PacService(Snapshot);
            try { service.Start(state.Settings.Pac.Port); pac = service; }
            catch (ToolkitException error) { await service.DisposeAsync(); pacError = error.Code; Log("Warning", error.Code, "Local PAC server could not start. Tunnel profiles remain available."); }
        }
        foreach (var profile in state.Tunnels) connections[profile.Id] = new(profile.Id, ConnectionState.Stopped);
        foreach (var profile in state.Hosts) hostStatuses[profile.Id] = new(profile.Id, ConnectionState.Stopped);
        foreach (var profile in state.Services) serviceStatuses[profile.Id] = new(profile.Id, ConnectionState.Stopped);
        Log("Information", "AGENT_STARTED", "Agent started.");
        foreach (var profile in state.Services.Where(s => s.AutoStart))
        {
            try { await StartServiceAsync(profile, cancellationToken); }
            catch (ToolkitException error) { SetServiceStatus(new(profile.Id, ConnectionState.Failed, error.Code)); }
        }
        foreach (var profile in state.Tunnels.Where(t => t.AutoConnect))
        {
            try { StartConnection(profile.Id); }
            catch (ToolkitException error)
            {
                SetStatus(new(profile.Id, ConnectionState.Failed, ErrorCode: error.Code));
                Log("Warning", error.Code, "Automatic connection could not start. Other profiles remain available.", profile.Id);
            }
        }
        foreach (var profile in state.Hosts.Where(h => h.AutoStart))
        {
            try { StartHosting(profile.Id); }
            catch (ToolkitException error) { SetHostStatus(new(profile.Id, ConnectionState.Failed, error.Code)); }
        }
    }

    public AgentSnapshot Snapshot()
    {
        lock (sync) return new(state.Accounts, state.Tunnels, [.. connections.Values.OrderBy(c => c.ProfileId)], [.. logins.Values], state.Settings, capabilities, [.. recentLogs], pacError, proxyError, proxy?.ActiveConnections ?? 0)
        {
            Hosts = state.Hosts,
            Services = state.Services,
            ServiceStatuses = [.. serviceStatuses.Values.OrderBy(s => s.ServiceId)],
            HostStatuses = [.. hostStatuses.Values.OrderBy(h => h.ProfileId)],
            DiagnosticEcho = CurrentEcho
        };
    }

    public async Task<object> DispatchAsync(string operation, JsonElement payload, CancellationToken cancellationToken)
    {
        if (operation == "status") return Snapshot();
        if (operation.StartsWith("remote.", StringComparison.Ordinal)) return await DispatchRemoteAsync(operation, payload, cancellationToken);
        if (operation.StartsWith("host.", StringComparison.Ordinal)) return await DispatchHostingAsync(operation, payload, cancellationToken);
        if (operation.StartsWith("service.", StringComparison.Ordinal)) return await DispatchServicesAsync(operation, payload, cancellationToken);
        if (operation.StartsWith("diagnostic.", StringComparison.Ordinal)) return await DispatchDiagnosticAsync(operation, payload, cancellationToken);
        if (operation == "proxy.test")
            return ProxyRouting.Decide(Snapshot(), payload.GetProperty("host").GetString() ?? "", payload.GetProperty("port").GetInt32());
        if (operation == "proxy.probe")
        {
            var result = await ProxyProbe.RunAsync(Snapshot(), payload.GetProperty("host").GetString() ?? "", payload.GetProperty("port").GetInt32(), payload.GetProperty("https").GetBoolean(), cancellationToken,
                payload.TryGetProperty("upstreamOnly", out var upstreamOnly) && upstreamOnly.GetBoolean());
            Log(result.Succeeded ? "Information" : "Warning", result.Code,
                $"Access probe via {(result.ThroughLocalProxy ? "local" : "upstream")} proxy completed in {result.ElapsedMilliseconds} ms. HTTP status: {result.HttpStatus?.ToString() ?? "none"}. No response content recorded.", result.Route.ProfileId);
            return result;
        }
        if (operation == "pac.preview")
        {
            var settings = Read<AppSettings>(payload);
            Validation.Settings(settings);
            return PacService.Generate(Snapshot() with { Settings = settings });
        }
        if (operation == "logs") return await store.ReadLogsAsync(payload.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 200, cancellationToken);
        if (operation == "account.discover")
        {
            var account = Account(Id(payload));
            var result = await tunnels.DiscoverAsync(account, cancellationToken);
            Log("Information", "DISCOVERY_COMPLETED", "Owned tunnels discovered.");
            return result;
        }
        if (operation == "tunnel.refresh")
        {
            var profile = Profile(Id(payload));
            return await tunnels.GetAsync(Account(profile.AccountId), profile, cancellationToken);
        }
        await mutation.WaitAsync(cancellationToken);
        try
        {
            switch (operation)
            {
                case "account.save":
                    {
                        var input = Read<AccountProfile>(payload);
                        Validation.Account(input);
                        var previous = Snapshot().Accounts.FirstOrDefault(a => a.Id == input.Id);
                        if (previous is not null && (previous.CliLoginMode != input.CliLoginMode || previous.ClientId != input.ClientId || previous.Scope != input.Scope || previous.Provider != input.Provider || previous.TenantId != input.TenantId)) RequireNoLogin(input.Id);
                        if (previous is null && Snapshot().Accounts.Length >= 50) throw new ToolkitException("ACCOUNT_LIMIT_REACHED");
                        // Authentication metadata comes exclusively from the identity provider.
                        var account = previous is null ? new AccountProfile { Id = input.Id, Provider = input.Provider, CliLoginMode = input.CliLoginMode, DisplayName = input.DisplayName, ClientId = input.ClientId, TenantId = input.TenantId, Scope = input.Scope } : previous with { DisplayName = input.DisplayName, CliLoginMode = input.CliLoginMode, ClientId = input.ClientId, Scope = input.Scope };
                        if (previous is not null && (previous.Provider != input.Provider || previous.TenantId != input.TenantId)) throw new ToolkitException("ACCOUNT_IDENTITY_IMMUTABLE");
                        if (previous is not null && (previous.ClientId != account.ClientId || previous.Scope != account.Scope))
                        {
                            await StopAccountAsync(account.Id);
                            await authentication.LogoutAsync(previous, cancellationToken);
                            account = account with { AuthenticatedAt = null, ExpiresAt = null, Status = "AuthenticationRequired" };
                        }
                        Validation.Account(account);
                        await SaveAsync(state with { Accounts = [.. state.Accounts.Where(a => a.Id != account.Id), account] }, cancellationToken);
                        lock (sync)
                            if (!loggingIn.TryGetValue(account.Id, out var pending) || pending.Completion.IsCompleted) logins.TryRemove(account.Id, out _);
                        Log("Information", "ACCOUNT_SAVED", "Account configuration saved.");
                        return account;
                    }
                case "account.login":
                    {
                        var id = Id(payload);
                        var account = Account(id);
                        RequireNoLogin(id);
                        await StopAccountAsync(id);
                        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        var attempt = new LoginStatus(id, "Starting", RequestedMode: account.Provider is AccountProvider.MicrosoftCli or AccountProvider.GitHubCli ? account.CliLoginMode : null, StartedAt: DateTimeOffset.UtcNow);
                        logins[id] = attempt;
                        lock (sync) loggingIn[id] = new(source, Task.Run(() => LoginAsync(account, attempt, source.Token)));
                        return logins[id];
                    }
                case "account.cancel":
                    await CancelLoginAsync(Id(payload));
                    return true;
                case "account.validate":
                    {
                        var account = Account(Id(payload));
                        lock (sync)
                            if (loggingIn.TryGetValue(account.Id, out var login) && !login.Completion.IsCompleted) throw new ToolkitException("LOGIN_IN_PROGRESS");
                        var validated = await authentication.ValidateAsync(account, cancellationToken);
                        await SaveAccountAsync(validated, cancellationToken);
                        return validated;
                    }
                case "account.import-token":
                    {
                        var account = Account(Id(payload));
                        if (account.Provider != AccountProvider.TunnelToken) throw new ToolkitException("TOKEN_ACCOUNT_REQUIRED");
                        var token = payload.GetProperty("token").GetString() ?? "";
                        if (token.Length is < 20 or > 32768 || token.Any(char.IsControl)) throw new ToolkitException("INVALID_ACCESS_TOKEN");
                        await StopAccountAsync(account.Id);
                        await vault.WriteAsync($"account.{account.Id}.token", token, cancellationToken);
                        await SaveAccountAsync(account with { AuthenticatedAt = DateTimeOffset.UtcNow, Status = "TokenStored" }, cancellationToken);
                        Log("Information", "TOKEN_IMPORTED", "Tunnel credential stored in the protected vault.");
                        return true;
                    }
                case "account.remove":
                    {
                        var account = Account(Id(payload));
                        if (state.Tunnels.Any(t => t.AccountId == account.Id)) throw new ToolkitException("ACCOUNT_HAS_TUNNELS");
                        if (state.Hosts.Any(h => h.AccountId == account.Id)) throw new ToolkitException("ACCOUNT_HAS_HOSTS");
                        await CancelLoginAsync(account.Id);
                        await authentication.LogoutAsync(account, cancellationToken);
                        await SaveAsync(state with { Accounts = [.. state.Accounts.Where(a => a.Id != account.Id)] }, cancellationToken);
                        logins.TryRemove(account.Id, out _);
                        Log("Information", "ACCOUNT_REMOVED", "Local account removed.");
                        return true;
                    }
                case "tunnel.save":
                    {
                        var profile = Read<TunnelProfile>(payload);
                        Validation.Tunnel(profile);
                        _ = Account(profile.AccountId);
                        if (state.Tunnels.Any(t => t.Id != profile.Id && t.AccountId == profile.AccountId && t.CanonicalId == profile.CanonicalId)) throw new ToolkitException("TUNNEL_ALREADY_ADDED");
                        if (!state.Tunnels.Any(t => t.Id == profile.Id) && state.Tunnels.Length >= 200) throw new ToolkitException("TUNNEL_LIMIT_REACHED");
                        await StopConnectionAsync(profile.Id);
                        await SaveAsync(state with { Tunnels = [.. state.Tunnels.Where(t => t.Id != profile.Id), profile] }, cancellationToken);
                        connections[profile.Id] = new(profile.Id, ConnectionState.Stopped);
                        Log("Information", "TUNNEL_SAVED", "Tunnel profile saved.", profile.Id);
                        return profile;
                    }
                case "tunnel.remove":
                    {
                        var id = Profile(Id(payload)).Id;
                        if (state.Settings.Proxy.Rules.Any(r => r.Action == RouteAction.Tunnel && r.ProfileId == id)) throw new ToolkitException("TUNNEL_HAS_PROXY_RULES");
                        if (state.Settings.Pac.Rules.Any(r => r.ProfileId == id)) throw new ToolkitException("TUNNEL_HAS_PAC_RULES");
                        await StopConnectionAsync(id);
                        await SaveAsync(state with { Tunnels = [.. state.Tunnels.Where(t => t.Id != id)] }, cancellationToken);
                        connections.TryRemove(id, out _);
                        Log("Information", "TUNNEL_REMOVED", "Local profile removed; remote resource preserved.", id);
                        return true;
                    }
                case "tunnel.connect":
                    StartConnection(Id(payload));
                    return true;
                case "tunnel.stop":
                    await StopConnectionAsync(Profile(Id(payload)).Id);
                    return true;
                case "tunnel.reconnect":
                    await StopConnectionAsync(Profile(Id(payload)).Id);
                    StartConnection(Id(payload));
                    return true;
                case "connections.stop":
                    foreach (var id in state.Tunnels.Select(t => t.Id).ToArray()) await StopConnectionAsync(id);
                    return true;
                case "proxy.start":
                case "proxy.stop":
                    {
                        var settings = state.Settings with { Proxy = state.Settings.Proxy with { Enabled = operation == "proxy.start" } };
                        if (!settings.Proxy.Enabled && settings.Pac.Mode == PacMode.LocalProxy)
                            settings = settings with { Pac = settings.Pac with { Enabled = false } };
                        Validation.Settings(settings);
                        await ApplySettingsAsync(settings, cancellationToken);
                        return true;
                    }
                case "settings.save":
                    {
                        var settings = Read<AppSettings>(payload);
                        Validation.Settings(settings);
                        await ApplySettingsAsync(settings, cancellationToken);
                        await store.PruneLogsAsync(settings.LogRetentionDays, cancellationToken);
                        Log("Information", "SETTINGS_SAVED", "Application settings saved.");
                        return settings;
                    }
                default: throw new ToolkitException("UNKNOWN_OPERATION");
            }
        }
        finally { mutation.Release(); }
    }

    private async Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (serviceStatuses.Values.Any(s => s.State == ConnectionState.Connected &&
            (settings.Proxy.Enabled && s.Port == settings.Proxy.Port || settings.Pac.Enabled && s.Port == settings.Pac.Port)))
            throw new ToolkitException("SERVICE_PORT_CONFLICT");
        lock (sync)
        {
            if (state.Hosts.Any(h => hostingOperations.TryGetValue(h.Id, out var runningHost) && !runningHost.Completion.IsCompleted &&
                h.Ports.Any(p => p.Kind == HostPortKind.Service &&
                    (settings.Proxy.Enabled && p.LocalPort == settings.Proxy.Port || settings.Pac.Enabled && p.LocalPort == settings.Pac.Port))))
                throw new ToolkitException("HOST_LOCAL_SERVICE_CONFLICT");
        }
        foreach (var rule in settings.Pac.Rules)
            if (!state.Tunnels.Any(t => t.Id == rule.ProfileId)) throw new ToolkitException("PAC_TUNNEL_NOT_FOUND");
        foreach (var rule in settings.Proxy.Rules.Where(r => r.Action == RouteAction.Tunnel))
            if (!state.Tunnels.Any(t => t.Id == rule.ProfileId)) throw new ToolkitException("PROXY_TUNNEL_NOT_FOUND");
        var previous = state.Settings;
        var changeStartup = settings.StartWithUserSession != previous.StartWithUserSession;
        if (changeStartup && startup?.Supported != true) throw new ToolkitException("STARTUP_UNAVAILABLE");
        LocalProxyService? nextProxy = null;
        var replaceProxy = settings.Proxy.Enabled && (proxy is null || settings.Proxy.Port != previous.Proxy.Port || settings.Proxy.ListenAddress != previous.Proxy.ListenAddress);
        if (settings.Proxy.Enabled && proxy is not null && !replaceProxy &&
            (settings.Proxy.MaxClients != previous.Proxy.MaxClients || settings.Proxy.ConnectTimeoutSeconds != previous.Proxy.ConnectTimeoutSeconds || settings.Proxy.IdleTimeoutSeconds != previous.Proxy.IdleTimeoutSeconds))
            throw new ToolkitException("PROXY_RESTART_REQUIRED");
        PacService? next = null;
        var replacePac = settings.Pac.Enabled && (pac is null || settings.Pac.Port != previous.Pac.Port);
        try
        {
            if (replaceProxy) { nextProxy = CreateProxy(); nextProxy.Start(settings.Proxy); }
            if (replacePac) { next = new PacService(Snapshot); next.Start(settings.Pac.Port); }
            if (changeStartup) await startup!.SetEnabledAsync(settings.StartWithUserSession, cancellationToken);
            await SaveAsync(state with { Settings = settings }, cancellationToken);
        }
        catch
        {
            if (nextProxy is not null) await nextProxy.DisposeAsync();
            if (next is not null) await next.DisposeAsync();
            if (changeStartup)
            {
                try { await startup!.SetEnabledAsync(previous.StartWithUserSession, CancellationToken.None); }
                catch { Log("Error", "STARTUP_ROLLBACK_FAILED", "Startup registration needs review using the platform configuration utility."); }
            }
            throw;
        }
        if (replacePac || !settings.Pac.Enabled)
        {
            var old = pac;
            pac = next;
            if (old is not null) await old.DisposeAsync();
        }
        if (replaceProxy || !settings.Proxy.Enabled)
        {
            var old = proxy;
            proxy = nextProxy;
            if (old is not null) await old.DisposeAsync();
        }
        proxyError = null;
        pacError = null;
    }

    private LocalProxyService CreateProxy() => new(Snapshot, (code, profile) =>
        Log(code.Contains("FALLBACK", StringComparison.Ordinal) || code.Contains("BLOCK", StringComparison.Ordinal) ? "Warning" : "Information",
            code, "Local proxy routing decision. Request URLs, headers and bodies are not recorded.", profile));

    private void StartConnection(string id)
    {
        var profile = Profile(id);
        id = profile.Id;
        var account = Account(profile.AccountId);
        lock (sync)
        {
            if (running.TryGetValue(id, out var prior) && !prior.Completion.IsCompleted) return;
            if (running.Values.Count(r => !r.Completion.IsCompleted) >= state.Settings.MaxConnections) throw new ToolkitException("CONNECTION_LIMIT_REACHED");
            prior?.Cancellation.Dispose();
            var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            connections[id] = new(id, ConnectionState.Connecting, OperationStartedAt: DateTimeOffset.UtcNow, Phase: "queued");
            running[id] = new(source, Task.Run(() => RunConnectionAsync(account, profile, source.Token)));
        }
    }

    private async Task RunConnectionAsync(AccountProfile account, TunnelProfile profile, CancellationToken cancellationToken)
    {
        var retries = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    SetStatus(new(profile.Id, ConnectionState.Discovering, RetryCount: retries));
                    var remote = await tunnels.GetAsync(account, profile, cancellationToken);
                    await UpdateAuthenticationObservationAsync(account.Id, false, cancellationToken);
                    var requested = ResolveMappings(profile, remote);
                    if (requested.Length == 0) throw new ToolkitException("NO_PORTS_SELECTED");
                    SetStatus(new(profile.Id, ConnectionState.Connecting, RetryCount: retries, Phase: "relay"));
                    await using var relay = await tunnels.ConnectAsync(account, profile, cancellationToken);
                    var disconnected = new TaskCompletionSource<ConnectionState>(TaskCreationOptions.RunContinuationsAsynchronously);
                    relay.StateChanged += stateValue =>
                    {
                        if (stateValue is ConnectionState.Stopped or ConnectionState.Failed or ConnectionState.AuthenticationRequired) disconnected.TrySetResult(stateValue);
                        else if (stateValue is ConnectionState.Reconnecting or ConnectionState.Connected && connections.TryGetValue(profile.Id, out var old))
                            SetStatus(old with { State = stateValue });
                    };
                    SetStatus(new(profile.Id, ConnectionState.Connecting, RetryCount: retries, Phase: "mapping"));
                    await using var mapping = await MappedConnection.StartAsync(relay, requested, cancellationToken);
                    profile = await SaveEffectiveMappingsAsync(profile, requested, mapping.Ports, cancellationToken);
                    var connectedAt = DateTimeOffset.UtcNow;
                    SetStatus(new(profile.Id, ConnectionState.Connected, connectedAt, retries, Ports: [.. mapping.Ports], TunnelExpiresAt: remote.ExpiresAt));
                    Log("Information", "TUNNEL_CONNECTED", "Tunnel connected and local listeners ready.", profile.Id);
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var interval = TimeSpan.FromSeconds(Snapshot().Settings.DiscoveryIntervalSeconds);
                        await Task.WhenAny(Task.Delay(interval, cancellationToken), disconnected.Task);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (disconnected.Task.IsCompleted)
                            throw new ToolkitException(await disconnected.Task == ConnectionState.AuthenticationRequired ? "AUTHENTICATION_REQUIRED" : "RELAY_DISCONNECTED");
                        await relay.RefreshPortsAsync(cancellationToken);
                        var refreshed = await tunnels.GetAsync(account, profile, cancellationToken);
                        await UpdateAuthenticationObservationAsync(account.Id, false, cancellationToken);
                        if (connections.TryGetValue(profile.Id, out var live))
                            SetStatus(live with { TunnelExpiresAt = refreshed.ExpiresAt });
                        if (!requested.Select(p => p.RemotePort).Order().SequenceEqual(ResolveMappings(profile, refreshed).Select(p => p.RemotePort).Order()))
                            throw new ToolkitException("REMOTE_PORTS_CHANGED");
                        if (DateTimeOffset.UtcNow - connectedAt > TimeSpan.FromMinutes(1)) retries = 0;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    var code = ErrorCode(error);
                    var authRequired = code is "AUTH_REQUIRED" or "AUTHENTICATION_REQUIRED" or "AUTHENTICATION_EXPIRED" or "LOGIN_TOKEN_EXPIRED" or "AUTH_CLIENT_ID_REQUIRED" or "ACCESS_TOKEN_EXPIRED" or "CLI_ACCOUNT_CHANGED";
                    if (authRequired) await UpdateAuthenticationObservationAsync(account.Id, true, cancellationToken);
                    Log("Warning", code, "Connection interrupted. Check the connection status for the next action.", profile.Id);
                    if (authRequired || !profile.Reconnect.Enabled || retries >= profile.Reconnect.MaxRetries || code is "PORT_IN_USE" or "LOCAL_PORT_UNAVAILABLE" or "PORT_RANGE_EXHAUSTED" or "NO_PORTS_SELECTED" or "DUPLICATE_OR_EXCESSIVE_MAPPINGS" or "REMOTE_PORT_NOT_FOUND" or "TUNNEL_ACCESS_DENIED")
                    {
                        SetStatus(new(profile.Id, authRequired ? ConnectionState.AuthenticationRequired : ConnectionState.Failed, RetryCount: retries, ErrorCode: code));
                        return;
                    }
                    retries++;
                    var delay = Math.Min(profile.Reconnect.MaxDelaySeconds, profile.Reconnect.InitialDelaySeconds * Math.Pow(2, Math.Min(retries - 1, 16)));
                    delay = Math.Min(profile.Reconnect.MaxDelaySeconds, delay * (0.8 + Random.Shared.NextDouble() * 0.4));
                    var next = DateTimeOffset.UtcNow.AddSeconds(delay);
                    SetStatus(new(profile.Id, ConnectionState.Reconnecting, RetryCount: retries, NextRetryAt: next, ErrorCode: code));
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            SetStatus(new(profile.Id, ConnectionState.Failed, RetryCount: retries, ErrorCode: ErrorCode(error)));
            Log("Error", "CONNECTION_WORKER_FAILED", "Connection worker stopped. Other profiles remain available.", profile.Id);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested) SetStatus(new(profile.Id, ConnectionState.Stopped));
        }
    }

    public static PortMapping[] ResolveMappings(TunnelProfile profile, DiscoveredTunnel remote)
    {
        var ports = remote.Ports.Select(p => p.PortNumber).ToHashSet();
        var selected = profile.Mappings.Where(m => m.Enabled && m.Mode != MappingMode.Disabled && (!m.AutoDiscovered || ports.Contains(m.RemotePort))).ToList();
        if (selected.Any(m => !ports.Contains(m.RemotePort))) throw new ToolkitException("REMOTE_PORT_NOT_FOUND");
        if (profile.AutoDiscoverPorts)
            selected.AddRange(ports.Where(p => !profile.Mappings.Any(m => m.RemotePort == p)).Order().Select(p => new PortMapping { RemotePort = p, AutoDiscovered = true }));
        if (selected.Count > 100) throw new ToolkitException("DUPLICATE_OR_EXCESSIVE_MAPPINGS");
        foreach (var mapping in selected) Validation.Mapping(mapping);
        return [.. selected];
    }

    private async Task<TunnelProfile> SaveEffectiveMappingsAsync(TunnelProfile profile, PortMapping[] requested, IReadOnlyList<EffectiveMapping> effective, CancellationToken cancellationToken)
    {
        // Stop cancels this token before awaiting the worker while holding mutation.
        // The cancellable acquisition therefore cannot deadlock with stop/edit/remove.
        await mutation.WaitAsync(cancellationToken);
        try
        {
            var current = Profile(profile.Id);
            var mappings = current.Mappings.Where(m => !m.AutoDiscovered || requested.Any(p => p.RemotePort == m.RemotePort)).ToDictionary(m => m.RemotePort);
            foreach (var item in requested)
            {
                var port = effective.First(p => p.RemotePort == item.RemotePort);
                mappings[item.RemotePort] = item with { LastEffectivePort = port.LocalPort };
            }
            var updated = current with { Mappings = [.. mappings.Values.OrderBy(m => m.RemotePort)] };
            Validation.Tunnel(updated);
            if (!current.Mappings.SequenceEqual(updated.Mappings))
                await SaveAsync(state with { Tunnels = [.. state.Tunnels.Select(t => t.Id == updated.Id ? updated : t)] }, cancellationToken);
            return updated;
        }
        finally { mutation.Release(); }
    }

    public async Task<object> LoginFromDesktopAsync(string id, Func<DesktopLoginRequest, CancellationToken, Task> authenticate, CancellationToken cancellationToken)
    {
        Validation.Id(id);
        Task operation;
        await mutation.WaitAsync(cancellationToken);
        try
        {
            var account = Account(id);
            if (authentication is not IDesktopAuthenticationService || account.Provider != AccountProvider.MicrosoftCli || account.CliLoginMode != CliLoginMode.Browser)
                throw new ToolkitException("DESKTOP_LOGIN_UNSUPPORTED");
            RequireNoLogin(id);
            await StopAccountAsync(id);
            var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            var attempt = new LoginStatus(id, "Starting", RequestedMode: account.CliLoginMode, StartedAt: DateTimeOffset.UtcNow);
            logins[id] = attempt;
            operation = Task.Run(() => LoginAsync(account, attempt, source.Token, authenticate));
            lock (sync) loggingIn[id] = new(source, operation);
        }
        finally { mutation.Release(); }
        await operation;
        return logins.TryGetValue(id, out var status) ? status : new LoginStatus(id, "Cancelled");
    }

    private async Task LoginAsync(AccountProfile account, LoginStatus attempt, CancellationToken cancellationToken, Func<DesktopLoginRequest, CancellationToken, Task>? desktopLogin = null)
    {
        try
        {
            var updated = desktopLogin is null
                ? await authentication.LoginAsync(account, challenge => logins[account.Id] = attempt with { State = "AwaitingUser", Challenge = challenge }, cancellationToken)
                : await ((IDesktopAuthenticationService)authentication).LoginFromDesktopAsync(account, desktopLogin, cancellationToken);
            // Saving uses its own short gate; cancellation prevents a removed account from returning.
            await mutation.WaitAsync(cancellationToken);
            try
            {
                var current = Account(account.Id);
                await SaveAccountAsync(current with
                {
                    StableIdentityId = updated.StableIdentityId,
                    Username = updated.Username,
                    TenantId = updated.TenantId,
                    AuthenticatedAt = updated.AuthenticatedAt,
                    ExpiresAt = updated.ExpiresAt,
                    LastValidatedAt = updated.LastValidatedAt,
                    Status = updated.Status
                }, cancellationToken);
                logins[account.Id] = attempt with { State = "Completed" };
                Log("Information", "LOGIN_COMPLETED", "Identity authenticated and credentials protected.");
            }
            finally { mutation.Release(); }
        }
        catch (OperationCanceledException) { logins[account.Id] = attempt with { State = "Cancelled" }; }
        catch (Exception error)
        {
            logins[account.Id] = attempt with { State = "Failed", ErrorCode = ErrorCode(error) };
            Log("Warning", "LOGIN_FAILED", $"Authentication did not complete ({ErrorCode(error)}).");
        }
    }

    private async Task UpdateAuthenticationObservationAsync(string accountId, bool needsLogin, CancellationToken cancellationToken)
    {
        await mutation.WaitAsync(cancellationToken);
        try
        {
            var current = Account(accountId);
            var updated = needsLogin ? current with { Status = "AuthenticationRequired" } : current with
            {
                Status = current.Provider == AccountProvider.TunnelToken ? "Configured" : "Authenticated",
                LastValidatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = authentication is IAuthenticationMetadataProvider metadata ? metadata.GetObservedExpiration(accountId) : current.ExpiresAt
            };
            if (updated != current) await SaveAccountAsync(updated, cancellationToken);
        }
        finally { mutation.Release(); }
    }

    private void RequireNoLogin(string id)
    {
        lock (sync)
            if (loggingIn.TryGetValue(id, out var login) && !login.Completion.IsCompleted) throw new ToolkitException("LOGIN_IN_PROGRESS");
    }

    private async Task CancelLoginAsync(string id)
    {
        RunningOperation? operation;
        lock (sync) { loggingIn.Remove(id, out operation); }
        if (operation is null) return;
        await operation.Cancellation.CancelAsync();
        await operation.Completion;
        operation.Cancellation.Dispose();
        if (logins.TryGetValue(id, out var attempt)) logins[id] = attempt with { State = "Cancelled", Challenge = null, ErrorCode = null };
    }

    private async Task StopAccountAsync(string id)
    {
        await CancelLoginAsync(id);
        foreach (var profile in Snapshot().Tunnels.Where(t => t.AccountId == id)) await StopConnectionAsync(profile.Id);
        foreach (var profile in Snapshot().Hosts.Where(h => h.AccountId == id)) await StopHostingAsync(profile.Id);
    }

    private async Task StopConnectionAsync(string id)
    {
        RunningOperation? operation;
        lock (sync) { running.Remove(id, out operation); }
        if (operation is not null)
        {
            SetStatus(new(id, ConnectionState.Stopping));
            await operation.Cancellation.CancelAsync();
            await operation.Completion;
            operation.Cancellation.Dispose();
        }
        SetStatus(new(id, ConnectionState.Stopped));
    }

    private AccountProfile Account(string id) => Snapshot().Accounts.FirstOrDefault(a => a.Id == id) ?? throw new ToolkitException("ACCOUNT_NOT_FOUND");
    private TunnelProfile Profile(string id)
    {
        var profiles = Snapshot().Tunnels;
        var exact = profiles.FirstOrDefault(t => t.Id == id);
        if (exact is not null) return exact;
        var matches = profiles.Where(t => t.CanonicalId == id || t.Alias.Length > 0 && t.Alias == id).ToArray();
        return matches.Length switch { 1 => matches[0], 0 => throw new ToolkitException("TUNNEL_NOT_FOUND"), _ => throw new ToolkitException("TUNNEL_REFERENCE_AMBIGUOUS") };
    }
    private static string Id(JsonElement payload) => payload.GetProperty("id").GetString() ?? throw new ToolkitException("INVALID_ID");
    private static T Read<T>(JsonElement payload) => payload.Deserialize<T>(Json) ?? throw new ToolkitException("INVALID_REQUEST");
    private async Task SaveAccountAsync(AccountProfile account, CancellationToken cancellationToken) => await SaveAsync(state with { Accounts = [.. state.Accounts.Where(a => a.Id != account.Id), account] }, cancellationToken);
    private async Task SaveAsync(ToolkitState next, CancellationToken cancellationToken)
    {
        await store.SaveAsync(next, cancellationToken);
        lock (sync) state = next;
    }
    private void SetStatus(ConnectionStatus value)
    {
        var now = DateTimeOffset.UtcNow;
        static bool Pending(ConnectionState state) => state is ConnectionState.Authenticating or ConnectionState.Discovering or
            ConnectionState.Connecting or ConnectionState.Reconnecting or ConnectionState.Stopping;
        connections.AddOrUpdate(value.ProfileId,
            _ => value with { OperationStartedAt = now, OperationCompletedAt = Pending(value.State) ? null : now },
            (_, previous) =>
            {
                // Keep the clock through discovery, relay, mapping and retries, including UI restarts.
                var starts = Pending(value.State) && (!Pending(previous.State) || value.State == ConnectionState.Stopping && previous.State != value.State);
                return value with
                {
                    OperationStartedAt = starts ? now : previous.OperationStartedAt ?? now,
                    OperationCompletedAt = Pending(value.State) ? null : previous.State == value.State ? previous.OperationCompletedAt ?? now : now
                };
            });
    }
    private static string ErrorCode(Exception error) => error is ToolkitException toolkit ? toolkit.Code : error is OperationCanceledException ? "OPERATION_TIMEOUT" : "CONNECTION_FAILED";

    private void Log(string level, string code, string message, string? profileId = null)
    {
        lock (sync)
        {
            var entry = new LogEntry(++logSequence, DateTimeOffset.UtcNow, level, code, message, profileId);
            recentLogs.Enqueue(entry);
            while (recentLogs.Count > 200) recentLogs.Dequeue();
            logQueue.Writer.TryWrite(entry);
        }
    }
    private async Task WriteLogsAsync()
    {
        var count = 0;
        var reportedFailure = false;
        await foreach (var entry in logQueue.Reader.ReadAllAsync())
        {
            try
            {
                await store.AppendLogAsync(entry);
                if (++count % 100 == 0) await store.PruneLogsAsync(Snapshot().Settings.LogRetentionDays);
                reportedFailure = false;
            }
            catch
            {
                if (reportedFailure) continue;
                reportedFailure = true;
                lock (sync)
                {
                    recentLogs.Enqueue(new(++logSequence, DateTimeOffset.UtcNow, "Error", "LOG_PERSISTENCE_FAILED", "Some events could not be persisted. Check data-directory availability."));
                    while (recentLogs.Count > 200) recentLogs.Dequeue();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        RunningOperation[] operations;
        lock (sync) operations = [.. running.Values, .. loggingIn.Values, .. hostingOperations.Values];
        await Task.WhenAll(operations.Select(o => o.Completion));
        await StopEchoAsync();
        if (proxy is not null) await proxy.DisposeAsync();
        if (pac is not null) await pac.DisposeAsync();
        if (services is not null) await services.DisposeAsync();
        foreach (var operation in operations) operation.Cancellation.Dispose();
        logQueue.Writer.TryComplete();
        await logWriter;
        lifetime.Dispose();
        mutation.Dispose();
    }
}
