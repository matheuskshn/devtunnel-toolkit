using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using DevTunnelToolkit.Core;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace DevTunnelToolkit.Application;

public sealed class PublishedProxyFactory(Action<string, string?> report) : IPublishedProxyFactory
{
    public async Task<IPublishedProxy> StartAsync(HostProfile profile, CancellationToken cancellationToken)
    {
        var service = new PublishedProxyService(profile, report);
        try { service.Start(); cancellationToken.ThrowIfCancellationRequested(); return service; }
        catch { await service.DisposeAsync(); throw; }
    }
}

/// <summary>Dedicated loopback-only publication. HTTP uses Titanium, with every destination
/// going through the same deny-by-default SOCKS policy and pinned-IP connector. No TLS interception.</summary>
public sealed class PublishedProxyService : IPublishedProxy
{
    // Independent services must not chain recursively through one another or reach
    // another service's internal HTTP/SOCKS policy adapter.
    private readonly HashSet<int> registeredPorts = [];
    public static bool IsReservedLocalPort(int port) => LocalServiceEndpoints.IsManagedPort(port);
    private readonly HostProfile profile;
    private readonly Action<string, string?> report;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolve;
    private readonly Func<IPAddress, int, CancellationToken, Task<TcpClient>> connect;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim clients;
    private readonly SemaphoreSlim internalClients;
    private readonly List<TcpListener> listeners = [];
    private readonly HashSet<TcpListener> internalListeners = [];
    private readonly Dictionary<TcpListener, int> httpUpstreams = [];
    private readonly List<Task> acceptTasks = [];
    private readonly List<ProxyServer> httpServers = [];
    private readonly ConcurrentDictionary<int, Task> connections = new();
    private readonly HashSet<int> forbiddenPorts = [];
    private int nextConnection;
    private bool started;
    public HostedPort[] Ports { get; private set; } = [];

    public PublishedProxyService(HostProfile profile, Action<string, string?> report,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<IPAddress, int, CancellationToken, Task<TcpClient>>? connect = null)
    {
        HostingValidation.Profile(profile);
        this.profile = profile;
        this.report = report;
        this.resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        this.connect = connect ?? ConnectPinnedAsync;
        clients = new SemaphoreSlim(profile.Proxy.MaxClients);
        internalClients = new SemaphoreSlim(profile.Proxy.MaxClients);
    }

    public void Start()
    {
        if (started) throw new ToolkitException("HOST_PROXY_ALREADY_STARTED");
        started = true;
        var bound = new List<HostedPort>();
        var proxyPorts = profile.Ports.Where(p => p.Kind != HostPortKind.Service).ToArray();
        // This private upstream also enforces the policy for HTTP requests, preventing DNS rebinding
        // between authorization and connect. The HTTP library never connects to the target by name.
        var internalSocks = proxyPorts.Any(p => p.Kind == HostPortKind.HttpProxy) ? StartSocks("127.0.0.1", 0, internalOnly: true) : 0;
        foreach (var port in proxyPorts)
        {
            var local = port.Kind == HostPortKind.Socks5Proxy ? StartSocks(port.LocalAddress, port.LocalPort) : StartHttp(port.LocalAddress, port.LocalPort, internalSocks);
            bound.Add(new(port.RemotePort, port.Kind, port.LocalAddress, local, port.ServiceId));
        }
        Ports = [.. bound];
        // A service exposed alongside a proxy must not be reachable recursively through that proxy.
        foreach (var port in profile.Ports.Where(p => p.LocalPort != 0)) forbiddenPorts.Add(port.LocalPort);
        foreach (var listener in listeners) acceptTasks.Add(AcceptAsync(listener));
    }

    private int StartSocks(string address, int port, bool internalOnly = false)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var listener = new TcpListener(IPAddress.Parse(address), port);
            listener.Start(profile.Proxy.MaxClients);
            var effective = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (!RegisterPort(effective))
            {
                listener.Stop();
                if (port != 0) throw new ToolkitException("SERVICE_BINDING_REQUIRED");
                continue;
            }
            listeners.Add(listener);
            if (internalOnly) internalListeners.Add(listener);
            forbiddenPorts.Add(effective);
            return effective;
        }
        throw new ToolkitException("SERVICE_PORT_CONFLICT");
    }

    private bool RegisterPort(int port)
    {
        if (registeredPorts.Contains(port)) return true;
        if (!LocalServiceEndpoints.TryRegisterManagedPort(port)) return false;
        registeredPorts.Add(port);
        return true;
    }

    private int StartHttp(string address, int port, int socksPort)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var server = new ProxyServer(false, false, false)
            {
                ForwardToUpstreamGateway = false,
                EnableWinAuth = false,
                EnableConnectionPool = false,
                NetworkFailureRetryAttempts = 0,
                EnableHttp2 = false,
                MaxConcurrentClientConnections = profile.Proxy.MaxClients,
                ClientHeaderTimeoutSeconds = 10,
                ConnectTimeOutSeconds = profile.Proxy.ConnectTimeoutSeconds,
                ResponseHeaderTimeoutSeconds = profile.Proxy.IdleTimeoutSeconds,
                IdleReadTimeoutSeconds = profile.Proxy.IdleTimeoutSeconds,
                IdleWriteTimeoutSeconds = profile.Proxy.IdleTimeoutSeconds
            };
            server.Logging.Enabled = false;
            server.GetCustomUpStreamProxyFunc = _ => Task.FromResult<IExternalProxy?>(new ExternalProxy("127.0.0.1", socksPort)
            {
                ProxyType = ExternalProxyType.Socks5,
                ProxyDnsRequests = true,
                BypassLocalhost = false,
                UseDefaultCredentials = false
            });
            var endpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
            // CONNECT is handled by the outer listener with its stricter port filter.
            // Never accept a second CONNECT on an existing plain HTTP keep-alive stream.
            endpoint.BeforeTunnelConnectRequest += (_, args) =>
            {
                args.DenyConnect = true;
                report("HOST_PROXY_DENIED", profile.Id);
                return Task.CompletedTask;
            };
            server.BeforeRequest += (_, args) =>
            {
                if (args.HttpClient.Request.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    args.GenericResponse("CONNECT requires a new proxy connection.", HttpStatusCode.Forbidden);
                    report("HOST_PROXY_DENIED", profile.Id);
                }
                return Task.CompletedTask;
            };
            server.AddEndPoint(endpoint);
            try { server.Start(changeSystemProxySettings: false); }
            catch { server.Dispose(); throw new ToolkitException("HOST_PROXY_START_FAILED"); }
            if (!RegisterPort(endpoint.Port))
            {
                try { server.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
                finally { server.Dispose(); }
                continue;
            }
            httpServers.Add(server);
            forbiddenPorts.Add(endpoint.Port);
            // Titanium peeks TLS after CONNECT even with decryption off. Handle CONNECT directly
            // so SSH/server-first protocols and small payloads never wait for a TLS ClientHello.
            var effective = StartSocks(address, port);
            httpUpstreams.Add(listeners[^1], endpoint.Port);
            return effective;
        }
        throw new ToolkitException("SERVICE_PORT_CONFLICT");
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                var gate = internalListeners.Contains(listener) ? internalClients : clients;
                if (!gate.Wait(0)) { client.Dispose(); report("HOST_PROXY_LIMIT", profile.Id); continue; }
                var id = Interlocked.Increment(ref nextConnection);
                var task = httpUpstreams.TryGetValue(listener, out var upstream) ? HandleHttpAsync(client, upstream, gate) :
                    HandleAsync(client, gate, isConnect: !internalListeners.Contains(listener));
                connections[id] = task;
                _ = task.ContinueWith(_ => connections.TryRemove(id, out var ignored), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient client, SemaphoreSlim gate, bool isConnect)
    {
        using (client)
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            handshake.CancelAfter(TimeSpan.FromSeconds(profile.Proxy.ConnectTimeoutSeconds));
            var stream = client.GetStream();
            var negotiated = false;
            var established = false;
            try
            {
                var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, handshake.Token);
                if (greeting[0] != 5 || greeting[1] == 0) return;
                var methods = new byte[greeting[1]]; await stream.ReadExactlyAsync(methods, handshake.Token);
                if (!methods.Contains((byte)0)) { await stream.WriteAsync(new byte[] { 5, 255 }, handshake.Token); return; }
                await stream.WriteAsync(new byte[] { 5, 0 }, handshake.Token);
                negotiated = true;
                var header = new byte[4]; await stream.ReadExactlyAsync(header, handshake.Token);
                if (header[0] != 5 || header[2] != 0 || header[1] != 1)
                { await ReplyAsync(stream, 7, handshake.Token); return; } // TCP CONNECT only, never BIND or UDP.
                int size;
                if (header[3] == 3)
                {
                    var count = new byte[1]; await stream.ReadExactlyAsync(count, handshake.Token); size = count[0];
                    if (size == 0) { await ReplyAsync(stream, 8, handshake.Token); return; }
                }
                else size = header[3] switch { 1 => 4, 4 => 16, _ => 0 };
                if (size == 0) { await ReplyAsync(stream, 8, handshake.Token); return; }
                var destination = new byte[size + 2]; await stream.ReadExactlyAsync(destination, handshake.Token);
                var host = header[3] == 3 ? Encoding.ASCII.GetString(destination, 0, size) : new IPAddress(destination.AsSpan(0, size)).ToString();
                var port = destination[size] * 256 + destination[size + 1];
                var addresses = await ResolveAllowedAsync(host, port, isConnect, handshake.Token);
                using var upstream = await ConnectAllowedAsync(addresses, port, handshake.Token);
                await ReplyAsync(stream, 0, handshake.Token);
                established = true;
                report("HOST_PROXY_CONNECTED", profile.Id);
                await RelayAsync(client, upstream, profile.Proxy.IdleTimeoutSeconds, stop.Token);
            }
            catch (ToolkitException)
            {
                report("HOST_PROXY_DENIED", profile.Id);
                try { await ReplyAsync(stream, 2, handshake.Token); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                report(stop.IsCancellationRequested ? "HOST_PROXY_STOPPED" : "HOST_PROXY_CONNECTION_FAILED", profile.Id);
                if (negotiated && !established)
                    try { await ReplyAsync(stream, 4, handshake.Token); }
                    catch (Exception sendError) when (sendError is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            finally { gate.Release(); }
        }
    }

    private async Task HandleHttpAsync(TcpClient client, int httpPort, SemaphoreSlim gate)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(profile.Proxy.ConnectTimeoutSeconds));
            var stream = client.GetStream();
            var established = false;
            try
            {
                var header = new List<byte>(); var next = new byte[1];
                while (true)
                {
                    if (header.Count >= 16384) throw new ToolkitException("HOST_PROXY_REQUEST_INVALID");
                    await stream.ReadExactlyAsync(next, timeout.Token); header.Add(next[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                var text = Encoding.ASCII.GetString(header.ToArray());
                var lines = text.Split("\r\n", StringSplitOptions.None);
                var request = lines[0].Split(' ');
                if (request.Length != 3 || request[2] is not ("HTTP/1.1" or "HTTP/1.0")) throw new ToolkitException("HOST_PROXY_REQUEST_INVALID");
                IPAddress[] addresses = [IPAddress.Loopback];
                var destinationPort = httpPort;
                if (request[0] == "CONNECT")
                {
                    if (request[1].Any(c => char.IsControl(c) || c is '/' or '?' or '#' or '@' or '\\') ||
                        !Uri.TryCreate("http://" + request[1], UriKind.Absolute, out var target) ||
                        request[1].LastIndexOf(':') < 1 || !int.TryParse(request[1][(request[1].LastIndexOf(':') + 1)..], out var port) ||
                        lines.Skip(1).Any(l => l.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) ||
                            l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && l[15..].Trim() != "0"))
                        throw new ToolkitException("HOST_PROXY_REQUEST_INVALID");
                    addresses = await ResolveAllowedAsync(target.IdnHost, port, isConnect: true, timeout.Token);
                    destinationPort = port;
                }
                using var upstream = await ConnectAllowedAsync(addresses, destinationPort, timeout.Token);
                if (request[0] == "CONNECT")
                {
                    await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), timeout.Token);
                    established = true; report("HOST_PROXY_CONNECTED", profile.Id);
                }
                else
                {
                    await upstream.GetStream().WriteAsync(header.ToArray(), timeout.Token);
                    established = true;
                }
                await RelayAsync(client, upstream, profile.Proxy.IdleTimeoutSeconds, stop.Token);
            }
            catch (ToolkitException)
            {
                report("HOST_PROXY_DENIED", profile.Id);
                if (!established) await HttpFailureAsync(stream, "403 Forbidden", timeout.Token);
            }
            catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                report(stop.IsCancellationRequested ? "HOST_PROXY_STOPPED" : "HOST_PROXY_CONNECTION_FAILED", profile.Id);
                if (!established) await HttpFailureAsync(stream, "502 Bad Gateway", timeout.Token);
            }
            finally { gate.Release(); }
        }
    }

    private static async Task HttpFailureAsync(Stream stream, string status, CancellationToken token)
    {
        try { await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token); }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task<IPAddress[]> ResolveAllowedAsync(string host, int port, bool isConnect, CancellationToken cancellationToken)
    {
        host = ProxyRouting.NormalizeHost(host);
        var policy = profile.Proxy;
        if (port is < 1 or > 65535 || !PortMatches(port, policy.AllowedPorts, policy.AllowedPortRanges) ||
            isConnect && !PortMatches(port, policy.ConnectPorts, policy.ConnectPortRanges))
            throw new ToolkitException("HOST_PROXY_DESTINATION_DENIED");
        var rule = policy.Rules.FirstOrDefault(r => r.Enabled && PortMatches(port, r.Ports, r.PortRanges) &&
            ProxyRouting.Matches(new ProxyRoute { Kind = r.Kind, Destination = r.Destination, IncludeSubdomains = r.IncludeSubdomains }, host));
        if (rule?.Action == PublishedProxyAction.Deny || rule is null && !policy.AllowAllDestinations)
            throw new ToolkitException("HOST_PROXY_DESTINATION_DENIED");
        // A public-only default must never inherit private/loopback permissions from
        // a later or unrelated rule. The first matching rule owns the entire decision.
        rule ??= new PublishedProxyRule { Kind = DestinationKind.Domain };
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await resolve(host, cancellationToken);
        if (addresses.Length is < 1 or > 64 || addresses.Any(a => !AddressAllowed(a, rule)))
            throw new ToolkitException("HOST_PROXY_DESTINATION_DENIED");
        addresses = addresses.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Distinct().ToArray();
        if (forbiddenPorts.Contains(port) || IsReservedLocalPort(port))
        {
            // A remote server may legitimately use the same port. Only the published host's
            // own loopback/interface addresses can recurse into one of its reserved listeners.
            IPAddress[] local;
            try
            {
                local = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address.IsIPv4MappedToIPv6 ? a.Address.MapToIPv4() : a.Address).ToArray();
            }
            catch (Exception) { throw new ToolkitException("HOST_PROXY_DESTINATION_DENIED"); }
            if (addresses.Any(a => IPAddress.IsLoopback(a) || local.Contains(a))) throw new ToolkitException("HOST_PROXY_DESTINATION_DENIED");
        }
        return addresses;
    }

    private static bool PortMatches(int port, int[] ports, ProxyPortRange[] ranges) =>
        ports.Length == 0 && ranges.Length == 0 || ports.Contains(port) || ranges.Any(r => port >= r.Start && port <= r.End);

    private async Task<TcpClient> ConnectAllowedAsync(IPAddress[] addresses, int port, CancellationToken cancellationToken)
    {
        // Only already-authorized, pinned IPs reach here. Each attempt happens before any HTTP
        // success response or application bytes; never re-resolve DNS or replay a request.
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Reserve time for later addresses if the first family has a black-holed route.
            // The caller's single handshake deadline still bounds the whole sequence.
            attempt.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(100, profile.Proxy.ConnectTimeoutSeconds * 1000 / addresses.Length)));
            try { return await connect(address, port, attempt.Token); }
            catch (SocketException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new SocketException((int)SocketError.HostUnreachable);
    }

    private static async Task<TcpClient> ConnectPinnedAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient(address.AddressFamily);
        try { await client.ConnectAsync(address, port, cancellationToken); return client; }
        catch { client.Dispose(); throw; }
    }

    private static bool AddressAllowed(IPAddress address, PublishedProxyRule rule)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            // Link-local (including metadata), unspecified, multicast and service infrastructure are never proxy destinations.
            if (bytes[0] == 0 || bytes[0] >= 224 || bytes[0] == 169 && bytes[1] == 254 ||
                address.Equals(IPAddress.Parse("168.63.129.16")) || address.Equals(IPAddress.Parse("100.100.100.200"))) return false;
            if (IPAddress.IsLoopback(address)) return rule.AllowLoopback && rule.Kind != DestinationKind.Domain;
            var privateAddress = bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
            return !privateAddress || rule.AllowPrivateAddresses;
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast || address.IsIPv6SiteLocal || address.IsIPv6Teredo || address.Equals(IPAddress.Parse("fd00:ec2::254"))) return false;
        if (IPAddress.IsLoopback(address)) return rule.AllowLoopback && rule.Kind != DestinationKind.Domain;
        var ipv6 = address.GetAddressBytes();
        if ((ipv6[0] & 0xfe) == 0xfc) return rule.AllowPrivateAddresses;
        // Only global-unicast IPv6, excluding 6to4. NAT64/compatible ranges must not translate
        // an apparently public IPv6 destination into forbidden IPv4 metadata or loopback.
        return (ipv6[0] & 0xe0) == 0x20 && !(ipv6[0] == 0x20 && ipv6[1] == 0x02);
    }

    private static ValueTask ReplyAsync(Stream stream, byte result, CancellationToken token) =>
        stream.WriteAsync(new byte[] { 5, result, 0, 1, 0, 0, 0, 0, 0, 0 }, token);

    private static async Task RelayAsync(TcpClient left, TcpClient right, int idleSeconds, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var lastActivity = Environment.TickCount64;
        async Task Copy(TcpClient source, TcpClient destination)
        {
            var buffer = new byte[32768];
            while (true)
            {
                var read = await source.GetStream().ReadAsync(buffer, lifetime.Token);
                if (read == 0) { destination.Client.Shutdown(SocketShutdown.Send); return; }
                await destination.GetStream().WriteAsync(buffer.AsMemory(0, read), lifetime.Token);
                Interlocked.Exchange(ref lastActivity, Environment.TickCount64);
            }
        }
        async Task WatchIdle()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
                if (Environment.TickCount64 - Interlocked.Read(ref lastActivity) >= idleSeconds * 1000L) { lifetime.Cancel(); return; }
        }
        var idle = WatchIdle();
        var first = Copy(left, right); var second = Copy(right, left);
        try
        {
            var completed = await Task.WhenAny(first, second);
            if (completed.IsFaulted || completed.IsCanceled) lifetime.Cancel();
            await Task.WhenAll(first, second);
        }
        finally
        {
            lifetime.Cancel();
            try { await idle; } catch (OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await stop.CancelAsync();
            foreach (var listener in listeners) listener.Stop();
            await Task.WhenAll(acceptTasks);
            foreach (var server in httpServers)
            {
                try { await server.StopAsync(TimeSpan.FromSeconds(3)); } finally { server.Dispose(); }
            }
            await Task.WhenAll(connections.Values);
            clients.Dispose(); internalClients.Dispose(); stop.Dispose();
        }
        finally
        {
            foreach (var port in registeredPorts) LocalServiceEndpoints.UnregisterManagedPort(port);
            registeredPorts.Clear();
        }
    }
}
