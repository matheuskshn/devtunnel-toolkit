using System.Collections.Concurrent;
using System.Text.Json;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.Application;

public sealed partial class AgentRuntime
{
    private readonly Dictionary<string, RunningOperation> hostingOperations = [];
    private readonly ConcurrentDictionary<string, HostStatus> hostStatuses = new();

    private async Task<object> DispatchHostingAsync(string operation, JsonElement payload, CancellationToken cancellationToken)
    {
        await mutation.WaitAsync(cancellationToken);
        try
        {
            if (operation == "host.save")
            {
                var profile = Read<HostProfile>(payload);
                HostingValidation.Profile(profile);
                ServiceValidation.Bindings(profile, state.Services);
                _ = Account(profile.AccountId);
                if (state.Hosts.Any(h => h.Id != profile.Id && h.AccountId == profile.AccountId && h.CanonicalId == profile.CanonicalId))
                    throw new ToolkitException("HOST_ALREADY_ADDED");
                if (!state.Hosts.Any(h => h.Id == profile.Id) && state.Hosts.Length >= 100)
                    throw new ToolkitException("HOST_LIMIT_REACHED");
                await StopHostingAsync(profile.Id);
                await SaveAsync(state with { Hosts = [.. state.Hosts.Where(h => h.Id != profile.Id), profile] }, cancellationToken);
                SetHostStatus(new(profile.Id, ConnectionState.Stopped));
                Log("Information", "HOST_PROFILE_SAVED", "Local hosting profile saved; remote resource unchanged.", profile.Id);
                return profile;
            }
            var host = Host(Id(payload));
            switch (operation)
            {
                case "host.start": StartHosting(host.Id); return true;
                case "host.restart": await StopHostingAsync(host.Id); StartHosting(host.Id); return true;
                case "host.stop": await StopHostingAsync(host.Id); return true;
                case "host.remove":
                    await StopHostingAsync(host.Id);
                    await SaveAsync(state with { Hosts = [.. state.Hosts.Where(h => h.Id != host.Id)] }, cancellationToken);
                    hostStatuses.TryRemove(host.Id, out _);
                    Log("Information", "HOST_PROFILE_REMOVED", "Local hosting profile removed; remote resource preserved.", host.Id);
                    return true;
                case "host.prepare":
                    {
                        var service = remoteManagement ?? throw new ToolkitException("REMOTE_MANAGEMENT_UNAVAILABLE");
                        var target = new RemoteTunnelTarget(host.TunnelId, host.ClusterId);
                        ConfirmRemote(target, payload.GetProperty("confirmation").GetString() ?? "");
                        RequireRemoteIdle(host.AccountId, target);
                        var account = Account(host.AccountId);
                        RequireNoLogin(account.Id);
                        var remote = await service.GetAsync(account, target, cancellationToken);
                        var created = new List<int>();
                        var existing = remote.Ports.Select(p => p.PortNumber).ToHashSet();
                        // Create only explicitly selected missing ports. Existing protocols,
                        // permissions and other ports are never overwritten or deleted.
                        foreach (var port in host.Ports.Where(p => !existing.Contains(p.RemotePort)))
                        {
                            try
                            {
                                await service.SavePortAsync(account, target, new(port.RemotePort, "auto", "Desktop hosted service"), true, cancellationToken);
                            }
                            catch
                            {
                                Log("Warning", "HOST_PREPARATION_INCOMPLETE", "Port preparation did not finish. Some remote ports may have been created; refresh before retrying.", target.CanonicalId);
                                throw new ToolkitException("HOST_PREPARATION_INCOMPLETE");
                            }
                            created.Add(port.RemotePort);
                            Log("Information", "HOST_PORT_CREATED", $"Remote port {port.RemotePort} created; existing ports preserved.", target.CanonicalId);
                        }
                        return new HostPreparationResult(host.CanonicalId, [.. created], [.. host.Ports.Where(p => existing.Contains(p.RemotePort)).Select(p => p.RemotePort)]);
                    }
                default: throw new ToolkitException("UNKNOWN_OPERATION");
            }
        }
        finally { mutation.Release(); }
    }

    private HostProfile Host(string reference)
    {
        var hosts = Snapshot().Hosts;
        var exact = hosts.FirstOrDefault(h => h.Id == reference);
        if (exact is not null) return exact;
        var matches = hosts.Where(h => h.CanonicalId == reference || h.Alias.Length > 0 && h.Alias == reference).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ToolkitException("HOST_NOT_FOUND"),
            _ => throw new ToolkitException("HOST_REFERENCE_AMBIGUOUS")
        };
    }

    private void StartHosting(string id)
    {
        if (hosting is null) throw new ToolkitException("HOSTING_UNAVAILABLE");
        var profile = Host(id);
        HostingValidation.Profile(profile);
        ServiceValidation.Bindings(profile, state.Services);
        foreach (var port in profile.Ports.Where(p => p.ServiceId is not null))
            if (!services.IsRunning(port.ServiceId!)) throw new ToolkitException("SERVICE_NOT_RUNNING");
        if (profile.Ports.Any(p => p.Kind == HostPortKind.Service &&
            (state.Services.Any(s => s.Port != 0 && s.Port == p.LocalPort) ||
             serviceStatuses.Values.Any(s => s.State == ConnectionState.Connected && s.Port == p.LocalPort))))
            throw new ToolkitException("SERVICE_BINDING_REQUIRED");
        var account = Account(profile.AccountId);
        RequireNoLogin(account.Id);
        var generations = profile.Ports.Where(p => p.ServiceId is not null).Select(p => p.ServiceId!).Distinct()
            .ToDictionary(id => id, id => services.Generation(id) ?? throw new ToolkitException("SERVICE_NOT_RUNNING"));
        if (profile.Ports.Any(p => p.Kind == HostPortKind.Service &&
            (state.Settings.Proxy.Enabled && p.LocalPort == state.Settings.Proxy.Port ||
             state.Settings.Pac.Enabled && p.LocalPort == state.Settings.Pac.Port)))
            throw new ToolkitException("HOST_LOCAL_SERVICE_CONFLICT");
        lock (sync)
        {
            if (hostingOperations.TryGetValue(profile.Id, out var old) && !old.Completion.IsCompleted) return;
            if (state.Hosts.Any(h => h.Id != profile.Id && h.CanonicalId == profile.CanonicalId &&
                hostingOperations.TryGetValue(h.Id, out var other) && !other.Completion.IsCompleted))
                throw new ToolkitException("HOST_ALREADY_RUNNING");
            if (hostingOperations.Values.Count(o => !o.Completion.IsCompleted) >= state.Settings.MaxConnections)
                throw new ToolkitException("HOST_LIMIT_REACHED");
            old?.Cancellation.Dispose();
            var externalPorts = LocalServiceEndpoints.ReserveExternalPorts(profile.Ports.Where(p => p.Kind == HostPortKind.Service).Select(p => p.LocalPort));
            var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            hostStatuses[profile.Id] = new(profile.Id, ConnectionState.Connecting, OperationStartedAt: DateTimeOffset.UtcNow, Phase: "queued");
            hostingOperations[profile.Id] = new(source, Task.Run(() => RunHostingAsync(account, profile, generations, externalPorts, source.Token)));
        }
    }

    private async Task RunHostingAsync(AccountProfile account, HostProfile profile, Dictionary<string, Guid> generations, IDisposable externalPorts, CancellationToken cancellationToken)
    {
        var retries = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (generations.Any(pair => services.Generation(pair.Key) != pair.Value)) throw new ToolkitException("SERVICE_BINDING_REVOKED");
                    SetHostStatus(new(profile.Id, ConnectionState.Discovering, Phase: "checking-remote", RetryCount: retries));
                    await using var host = await hosting!.StartAsync(account, profile, progress =>
                    {
                        if (!cancellationToken.IsCancellationRequested) SetHostStatus(progress with { RetryCount = retries });
                    }, cancellationToken);
                    var disconnected = new TaskCompletionSource<ConnectionState>(TaskCreationOptions.RunContinuationsAsynchronously);
                    host.StateChanged += value =>
                    {
                        if (value is ConnectionState.Failed or ConnectionState.Stopped or ConnectionState.AuthenticationRequired)
                            disconnected.TrySetResult(value);
                    };
                    if (host.State is ConnectionState.Failed or ConnectionState.Stopped or ConnectionState.AuthenticationRequired)
                        disconnected.TrySetResult(host.State);
                    if (disconnected.Task.IsCompleted)
                        throw new ToolkitException(await disconnected.Task == ConnectionState.AuthenticationRequired ? "AUTHENTICATION_REQUIRED" : "HOST_RELAY_DISCONNECTED");
                    SetHostStatus(new(profile.Id, ConnectionState.Connected, Ports: host.Ports, Phase: "ready", RetryCount: retries,
                        ExpiresAt: host.ExpiresAt, HostTokenExpiresAt: host.HostTokenExpiresAt));
                    Log("Information", "HOST_STARTED", "Hosting ready. Only selected ports are published.", profile.Id);
                    var connectedAt = DateTimeOffset.UtcNow;
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(15), cancellationToken), disconnected.Task);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (disconnected.Task.IsCompleted)
                            throw new ToolkitException(await disconnected.Task == ConnectionState.AuthenticationRequired ? "AUTHENTICATION_REQUIRED" : "HOST_RELAY_DISCONNECTED");
                        await host.RefreshPortsAsync(cancellationToken);
                        if (hostStatuses.TryGetValue(profile.Id, out var status))
                            SetHostStatus(status with
                            {
                                ExpiresAt = host.ExpiresAt,
                                HostTokenExpiresAt = host.HostTokenExpiresAt,
                                Ports = host.Ports,
                                State = host.Ports.Length == profile.Ports.Length ? ConnectionState.Connected : ConnectionState.Degraded,
                                LastError = host.Ports.Length == profile.Ports.Length ? null : "SERVICE_BINDING_REVOKED"
                            });
                        if (DateTimeOffset.UtcNow - connectedAt > TimeSpan.FromMinutes(1)) retries = 0;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    var code = ErrorCode(error);
                    var needsLogin = code is "AUTHENTICATION_REQUIRED" or "AUTHENTICATION_EXPIRED" or "ACCESS_TOKEN_EXPIRED" or "CLI_ACCOUNT_CHANGED";
                    var permanent = needsLogin || code is "HOST_REMOTE_PORT_MISSING" or "HOST_ANONYMOUS_ACCESS" or "HOST_ANONYMOUS_ACCESS_DENIED" or
                        "HOST_PROXY_REQUIRES_PRIVATE_TUNNEL" or "HOST_REMOTE_ID_MISMATCH" or "HOST_ALREADY_HOSTED" or
                        "HOST_PORT_IN_USE" or "HOST_LOCAL_SERVICE_CONFLICT" or "TUNNEL_ACCESS_DENIED" or "TUNNEL_NOT_FOUND" or
                        "SERVICE_NOT_RUNNING" or "SERVICE_NOT_FOUND" or "SERVICE_BINDING_REVOKED" or "SERVICE_PROTOCOL_MISMATCH";
                    Log("Warning", code, "Hosting interrupted; no new remote resource was created.", profile.Id);
                    if (permanent || !profile.Reconnect.Enabled || retries >= profile.Reconnect.MaxRetries)
                    {
                        SetHostStatus(new(profile.Id, needsLogin ? ConnectionState.AuthenticationRequired : ConnectionState.Failed, code, RetryCount: retries));
                        return;
                    }
                    retries++;
                    var delay = Math.Min(profile.Reconnect.MaxDelaySeconds, profile.Reconnect.InitialDelaySeconds * Math.Pow(2, Math.Min(retries - 1, 16)));
                    var next = DateTimeOffset.UtcNow.AddSeconds(delay);
                    SetHostStatus(new(profile.Id, ConnectionState.Reconnecting, code, Phase: "retry", RetryCount: retries, NextRetryAt: next));
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { SetHostStatus(new(profile.Id, ConnectionState.Failed, ErrorCode(error), RetryCount: retries)); }
        finally
        {
            externalPorts.Dispose();
            if (cancellationToken.IsCancellationRequested) SetHostStatus(new(profile.Id, ConnectionState.Stopped));
        }
    }

    private async Task StopHostingAsync(string id)
    {
        RunningOperation? runningHost;
        lock (sync) hostingOperations.Remove(id, out runningHost);
        if (runningHost is not null)
        {
            SetHostStatus(new(id, ConnectionState.Stopping, Phase: "stopping"));
            await runningHost.Cancellation.CancelAsync();
            await runningHost.Completion;
            runningHost.Cancellation.Dispose();
        }
        SetHostStatus(new(id, ConnectionState.Stopped));
    }

    private void SetHostStatus(HostStatus value)
    {
        var now = DateTimeOffset.UtcNow;
        static bool Pending(ConnectionState state) => state is ConnectionState.Authenticating or ConnectionState.Discovering or
            ConnectionState.Connecting or ConnectionState.Reconnecting or ConnectionState.Stopping;
        hostStatuses.AddOrUpdate(value.ProfileId, value with { OperationStartedAt = now, OperationCompletedAt = Pending(value.State) ? null : now },
            (_, previous) => value with
            {
                OperationStartedAt = Pending(value.State) && (!Pending(previous.State) || value.State == ConnectionState.Stopping && previous.State != value.State) ? now : previous.OperationStartedAt ?? now,
                OperationCompletedAt = Pending(value.State) ? null : previous.State == value.State ? previous.OperationCompletedAt ?? now : now
            });
    }

    public void RecordHostedProxyEvent(string code, string? profileId)
    {
        // Restrict plugin-provided diagnostics to a closed vocabulary. No destinations,
        // URLs, identities from remote clients, request bodies or credentials are logged.
        var safe = code switch
        {
            "HOST_PROXY_ALLOWED" or "HOST_PROXY_BLOCKED" or "HOST_PROXY_ERROR" or "HOST_PROXY_LIMIT" or
            "HOST_PROXY_CONNECTED" or "HOST_PROXY_DENIED" or "HOST_PROXY_CONNECTION_FAILED" or "HOST_PROXY_STOPPED" => code,
            _ => "HOST_PROXY_EVENT"
        };
        Log(safe is "HOST_PROXY_ALLOWED" or "HOST_PROXY_CONNECTED" or "HOST_PROXY_STOPPED" ? "Information" : "Warning", safe, "Published proxy policy event.", profileId);
    }
}
