using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using DevTunnelToolkit.Core;
using Microsoft.DevTunnels.Connections;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Tcp.Events;

namespace DevTunnelToolkit.DevTunnels;

/// <summary>Hosts only explicitly selected ports. Tunnel resources, ports and ACLs are never created
/// or overwritten here; the SDK manages only the endpoint required by the active hosting session.</summary>
public sealed class TunnelHostingService(IAuthenticationService authentication, CliAuthenticationService cli,
    IManagedServiceRegistry services) : IHostingService
{
    public async Task<IHostedTunnel> StartAsync(AccountProfile account, HostProfile profile, Action<HostStatus> progress, CancellationToken cancellationToken)
    {
        HostingValidation.Profile(profile);
        if (account.Id != profile.AccountId) throw new ToolkitException("TUNNEL_ACCOUNT_MISMATCH");
        var started = DateTimeOffset.UtcNow;
        progress(new(profile.Id, ConnectionState.Connecting, OperationStartedAt: started, Phase: "host-preflight"));
        var isCli = account.Provider is AccountProvider.MicrosoftCli or AccountProvider.GitHubCli;
        string? hostToken = null;
        var management = new TunnelManagementClient(new ProductInfoHeaderValue("DevTunnelToolkit", "1.0"), async () =>
            isCli ? new AuthenticationHeaderValue("tunnel", hostToken) :
                AuthenticationHeaderValue.Parse(await authentication.GetAuthorizationAsync(account, cancellationToken)),
            tunnelServiceUri: null, httpHandler: null)
        { EnableEventsReporting = false };
        var trace = new TraceSource("DevTunnelToolkit.Host", SourceLevels.Off);
        trace.Listeners.Clear();
        var host = new MappedTunnelRelayHost(management, trace) { ForwardConnectionsToLocalPorts = false };
        var bindings = new List<IManagedServiceBinding>();
        HostedTunnel? result = null;
        try
        {
            // Pin service generations before the first network await. Stop/restart while
            // authentication or remote preflight runs cannot silently replace a binding.
            foreach (var port in profile.Ports.Where(p => p.Kind != HostPortKind.Service))
                bindings.Add(services.Acquire(port.ServiceId ?? throw new ToolkitException("SERVICE_NOT_FOUND"), port.RemotePort, port.Kind));
            if (isCli) hostToken = await IssueTokenAsync(account, profile, cancellationToken);
            async Task<Tunnel> ReadAsync(CancellationToken token)
            {
                Tunnel tunnel;
                try
                {
                    tunnel = await management.GetTunnelAsync(new() { TunnelId = profile.TunnelId, ClusterId = profile.ClusterId },
                        new() { IncludePorts = true, IncludeAccessControl = true, TokenScopes = isCli || account.Provider == AccountProvider.TunnelToken ? null : [TunnelAccessScopes.Host] }, token)
                        ?? throw new ToolkitException("TUNNEL_NOT_FOUND");
                }
                catch (Exception error) when (error is not ToolkitException and not OperationCanceledException)
                { throw SafeError(error); }
                ValidateRemote(profile, tunnel);
                return tunnel;
            }
            var tunnel = await ReadAsync(cancellationToken);
            // Connecting a second SDK host can evict the first. Never silently take over another executor.
            if (tunnel.Status?.HostConnectionCount?.Current > 0) throw new ToolkitException("HOST_ALREADY_HOSTED");
            if (isCli || account.Provider == AccountProvider.TunnelToken)
            {
                hostToken ??= AuthenticationHeaderValue.Parse(await authentication.GetAuthorizationAsync(account, cancellationToken)).Parameter;
                tunnel.AccessTokens ??= new Dictionary<string, string>();
                tunnel.AccessTokens[TunnelAccessScopes.Host] = hostToken ?? throw new ToolkitException("HOST_TOKEN_REQUIRED");
            }
            else hostToken = tunnel.AccessTokens is { } tokens && tokens.TryGetValue(TunnelAccessScopes.Host, out var initialToken) ? initialToken : null;
            progress(new(profile.Id, ConnectionState.Connecting, OperationStartedAt: started, Phase: "host-bindings"));
            if (bindings.Any(b => !b.Available)) throw new ToolkitException("SERVICE_BINDING_REVOKED");
            var ports = profile.Ports.Where(p => p.Kind == HostPortKind.Service)
                .Select(p => new HostedPort(p.RemotePort, p.Kind, p.LocalAddress, p.LocalPort)).Concat(bindings.Select(b => b.Port)).ToArray();
            result = new HostedTunnel(profile, host, management, trace, bindings.ToArray(), ports, ReadAsync)
            {
                ExpiresAt = tunnel.Expiration is { } expiry ? new(DateTime.SpecifyKind(expiry, DateTimeKind.Utc)) : null,
                HostTokenExpiresAt = TokenExpiry(hostToken)
            };
            host.RefreshingTunnelAccessToken += (_, args) => args.TunnelAccessTokenTask = RenewAsync(args.Cancellation);
            async Task<string?> RenewAsync(CancellationToken token)
            {
                if (isCli) hostToken = await IssueTokenAsync(account, profile, token);
                var refreshed = await ReadAsync(token); // Recheck port existence and proxy privacy before renewal.
                if (!isCli)
                    hostToken = account.Provider == AccountProvider.TunnelToken
                        ? AuthenticationHeaderValue.Parse(await authentication.GetAuthorizationAsync(account, token)).Parameter
                        : refreshed.AccessTokens is { } tokens && tokens.TryGetValue(TunnelAccessScopes.Host, out var renewedToken) ? renewedToken : null;
                result.HostTokenExpiresAt = TokenExpiry(hostToken);
                return hostToken;
            }
            progress(new(profile.Id, ConnectionState.Connecting, Ports: ports, OperationStartedAt: started, Phase: "host-relay"));
            await host.ConnectAsync(tunnel, new TunnelConnectionOptions { EnableRetry = false, EnableReconnect = false, KeepAliveIntervalInSeconds = 20 }, cancellationToken);
            progress(new(profile.Id, ConnectionState.Connected, Ports: ports, OperationStartedAt: started,
                OperationCompletedAt: DateTimeOffset.UtcNow, Phase: "host-ready", ExpiresAt: result.ExpiresAt, HostTokenExpiresAt: result.HostTokenExpiresAt));
            return result;
        }
        catch (Exception error)
        {
            try
            {
                if (result is not null) await result.DisposeAsync();
                else
                {
                    try { await host.DisposeAsync(); }
                    finally
                    {
                        try { foreach (var binding in bindings) binding.Dispose(); }
                        finally { await management.DisposeAsync(); trace.Close(); }
                    }
                }
            }
            catch (Exception) { /* The original startup failure remains the actionable, sanitized result. */ }
            if (error is ToolkitException or OperationCanceledException) throw;
            throw SafeError(error);
        }
    }

    internal static ToolkitException SafeError(Exception error)
    {
        for (var depth = 0; error is not null && depth < 12; depth++, error = error.InnerException!)
        {
            if (error is ToolkitException toolkit) return toolkit;
            if (error is UnauthorizedAccessException) return new("AUTHENTICATION_REQUIRED");
            if (error is HttpRequestException http)
                return new(http.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "AUTHENTICATION_REQUIRED",
                    HttpStatusCode.Forbidden => "TUNNEL_ACCESS_DENIED",
                    HttpStatusCode.NotFound => "TUNNEL_NOT_FOUND",
                    HttpStatusCode.TooManyRequests => "REMOTE_RATE_LIMITED",
                    _ => "HOST_RELAY_UNAVAILABLE"
                });
        }
        return new("HOST_RELAY_UNAVAILABLE");
    }

    private async Task<string> IssueTokenAsync(AccountProfile account, HostProfile profile, CancellationToken token)
    {
        using var json = OfficialCliJson.Parse(await cli.RunAsAsync(account, ["token", profile.CanonicalId, "--scopes", "host", "--json"], token));
        var value = OfficialCliJson.String(json.RootElement, "token") ?? OfficialCliJson.String(json.RootElement, "accessToken");
        if (value is null || value.Length is < 20 or > 32768 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
            throw new ToolkitException("CLI_TOKEN_SCHEMA_UNSUPPORTED");
        return value;
    }

    internal static void ValidateRemote(HostProfile profile, Tunnel tunnel)
    {
        if (tunnel.TunnelId != profile.TunnelId || tunnel.ClusterId != profile.ClusterId) throw new ToolkitException("HOST_REMOTE_ID_MISMATCH");
        foreach (var port in profile.Ports)
        {
            var remote = tunnel.Ports?.FirstOrDefault(p => p.PortNumber == port.RemotePort) ?? throw new ToolkitException("HOST_REMOTE_PORT_MISSING");
            if (port.Kind != HostPortKind.Service && (Anonymous(tunnel.AccessControl) || Anonymous(remote.AccessControl)))
                throw new ToolkitException("HOST_PROXY_REQUIRES_PRIVATE_TUNNEL");
        }
        static bool Anonymous(TunnelAccessControl? access) => access?.Entries?.Any(e => e.Type == TunnelAccessControlEntryType.Anonymous &&
            !e.IsDeny && (e.Scopes is null || e.Scopes.Length == 0 || e.Scopes.Contains(TunnelAccessScopes.Connect))) == true;
    }

    internal static DateTimeOffset? TokenExpiry(string? token)
    {
        // Display-only metadata from a scoped token. It is never trusted for authorization decisions.
        if (token is null || token.Length > 32768) return null;
        try
        {
            var parts = token.Split('.'); if (parts.Length != 3) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            return json.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentOutOfRangeException or InvalidOperationException) { return null; }
    }

    internal sealed class HostedTunnel : IHostedTunnel
    {
        private readonly HostProfile profile;
        private readonly TunnelRelayTunnelHost host;
        private readonly TunnelManagementClient management;
        private readonly TraceSource trace;
        private readonly IManagedServiceBinding[] bindings;
        private readonly HostedPort[] ports;
        private readonly Func<CancellationToken, Task<Tunnel>> read;
        private readonly CancellationTokenSource stop = new();
        private readonly SemaphoreSlim slots;
        private readonly ConcurrentDictionary<int, Task> bridges = new();
        private readonly object lifecycle = new();
        private readonly Dictionary<int, Task<Stream?>> openings = [];
        private Task? disposeTask;
        private bool closing;
        private int nextBridge;
        public event Action<ConnectionState>? StateChanged;
        public HostedPort[] Ports => ports.Where(p => p.ServiceId is null || bindings.Any(b => b.Port.RemotePort == p.RemotePort && b.Available)).ToArray();
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? HostTokenExpiresAt { get; set; }
        public ConnectionState State => DevTunnelService.ClassifyStatus(new(Microsoft.DevTunnels.Connections.ConnectionStatus.Connected,
            host.ConnectionStatus, host.DisconnectException));

        public HostedTunnel(HostProfile profile, TunnelRelayTunnelHost host, TunnelManagementClient management, TraceSource trace,
            IManagedServiceBinding[] bindings, HostedPort[] ports, Func<CancellationToken, Task<Tunnel>> read)
        {
            this.profile = profile; this.host = host; this.management = management; this.trace = trace;
            this.bindings = bindings; this.ports = ports; this.read = read;
            // Managed engines own per-service limits. The relay has a separate bounded
            // transport ceiling, not the obsolete embedded proxy policy's default 128.
            slots = new(bindings.Length > 0 ? 1024 : profile.Proxy.MaxClients);
            host.ForwardedPortConnecting += Connecting;
            host.ConnectionStatusChanged += StatusChanged;
        }

        private void StatusChanged(object? sender, ConnectionStatusChangedEventArgs args) => StateChanged?.Invoke(DevTunnelService.ClassifyStatus(args));

        private void Connecting(object? sender, ForwardedPortConnectingEventArgs args)
        {
            lock (lifecycle)
            {
                if (closing) { args.Stream.Dispose(); args.TransformTask = Task.FromResult<Stream?>(null); return; }
                // The relay may already have wrapped this channel in SecureStream for E2E encryption.
                // Never replace that stream with the underlying (encrypted) SSH bytes.
                var transformed = args.TransformTask ?? Task.FromResult<Stream?>(args.Stream);
                var id = Interlocked.Increment(ref nextBridge);
                var task = OpenOrCloseAsync(args, transformed);
                openings[id] = task;
                args.TransformTask = task;
                _ = task.ContinueWith(_ => { lock (lifecycle) openings.Remove(id); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        private async Task<Stream?> OpenOrCloseAsync(ForwardedPortConnectingEventArgs args, Task<Stream?> transformed)
        {
            var stream = await OpenAsync(args, transformed);
            // V1 doesn't await TransformTask. Rejection must also explicitly close its channel.
            if (stream is null) args.Stream.Dispose();
            return stream;
        }

        private async Task<Stream?> OpenAsync(ForwardedPortConnectingEventArgs args, Task<Stream?> transformed)
        {
            var port = Ports.FirstOrDefault(p => p.RemotePort == args.Port);
            if (port?.Kind == HostPortKind.Service && LocalServiceEndpoints.IsManagedPort(port.LocalPort)) return null;
            if (port is null || stop.IsCancellationRequested || !slots.Wait(0)) return null;
            var binding = bindings.FirstOrDefault(b => b.Port.RemotePort == port.RemotePort);
            var policy = binding?.Policy ?? profile.Proxy;
            TcpClient? client = null;
            ManagedServiceConnection? managed = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, args.Cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(policy.ConnectTimeoutSeconds));
                var stream = await transformed.WaitAsync(timeout.Token);
                if (stream is null) { slots.Release(); return null; }
                if (port.Kind != HostPortKind.Service) await read(timeout.Token); // External ACL edits are checked per new connection.
                if (port.ServiceId is not null)
                {
                    managed = await (binding ?? throw new ToolkitException("SERVICE_BINDING_REVOKED")).ConnectAsync(timeout.Token);
                    client = managed.Client;
                }
                else
                {
                    client = new TcpClient(IPAddress.Parse(port.LocalAddress).AddressFamily);
                    await client.ConnectAsync(IPAddress.Parse(port.LocalAddress), port.LocalPort, timeout.Token);
                }
                var id = Interlocked.Increment(ref nextBridge);
                // args.Cancellation belongs to the opening event, not the lifetime of the channel.
                var task = BridgeAsync(stream, client, managed, policy.IdleTimeoutSeconds);
                bridges[id] = task;
                _ = task.ContinueWith(_ => bridges.TryRemove(id, out var ignored), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return stream;
            }
            catch { managed?.Dispose(); client?.Dispose(); slots.Release(); return null; }
        }

        private async Task BridgeAsync(Stream remote, TcpClient local, ManagedServiceConnection? managed, int idleTimeoutSeconds)
        {
            using (managed)
            using (local)
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, managed?.Revoked ?? CancellationToken.None))
            await using (var stream = new SdkDuplexStream(remote))
            {
                var activity = Environment.TickCount64;
                async Task Pump(Stream source, Stream target, bool toLocal)
                {
                    var buffer = new byte[32768];
                    while (true)
                    {
                        var count = await source.ReadAsync(buffer, lifetime.Token);
                        if (count == 0)
                        {
                            if (toLocal) local.Client.Shutdown(SocketShutdown.Send);
                            else await stream.CompleteWritesAsync(lifetime.Token);
                            return;
                        }
                        await target.WriteAsync(buffer.AsMemory(0, count), lifetime.Token);
                        Interlocked.Exchange(ref activity, Environment.TickCount64);
                    }
                }
                async Task Idle()
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                    while (await timer.WaitForNextTickAsync(lifetime.Token))
                        if (Environment.TickCount64 - Interlocked.Read(ref activity) >= idleTimeoutSeconds * 1000L) { lifetime.Cancel(); return; }
                }
                var idle = Idle();
                try
                {
                    var first = Pump(stream, local.GetStream(), true); var second = Pump(local.GetStream(), stream, false);
                    var completed = await Task.WhenAny(first, second);
                    if (completed.IsFaulted || completed.IsCanceled) lifetime.Cancel();
                    await Task.WhenAll(first, second);
                    if (remote is SecureStream secure && !secure.IsClosed)
                    {
                        // Disposing a SecureStream immediately after sending EOF also disposes the
                        // peer's inner SSH session, which can truncate data it has not consumed yet.
                        // Let the peer close after draining; the idle timer/host stop still bounds this wait.
                        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        EventHandler<SshSessionClosedEventArgs> onClosed = (_, _) => closed.TrySetResult();
                        secure.Closed += onClosed;
                        try { if (!secure.IsClosed) await closed.Task.WaitAsync(lifetime.Token); }
                        finally { secure.Closed -= onClosed; }
                    }
                }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                finally
                {
                    lifetime.Cancel(); slots.Release();
                    try { await idle; } catch (OperationCanceledException) { }
                }
            }
        }

        public async Task RefreshPortsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var tunnel = await read(cancellationToken);
                ExpiresAt = tunnel.Expiration is { } expiry ? new(DateTime.SpecifyKind(expiry, DateTimeKind.Utc)) : null;
                await host.RefreshPortsAsync(cancellationToken);
            }
            catch (Exception error) when (error is not ToolkitException and not OperationCanceledException) { throw SafeError(error); }
        }

        public ValueTask DisposeAsync()
        {
            lock (lifecycle)
            {
                closing = true;
                return new(disposeTask ??= Task.Run(DisposeCoreAsync));
            }
        }

        private async Task DisposeCoreAsync()
        {
            host.ForwardedPortConnecting -= Connecting;
            host.ConnectionStatusChanged -= StatusChanged;
            await stop.CancelAsync();
            Task<Stream?>[] pending;
            lock (lifecycle) pending = openings.Values.ToArray();
            try
            {
                try { await host.DisposeAsync(); }
                finally
                {
                    await Task.WhenAll(pending);
                    await Task.WhenAll(bridges.Values);
                }
            }
            finally
            {
                try { foreach (var binding in bindings) binding.Dispose(); }
                finally
                {
                    try { await management.DisposeAsync(); }
                    finally { trace.Close(); slots.Dispose(); stop.Dispose(); }
                }
            }
        }
    }
}
