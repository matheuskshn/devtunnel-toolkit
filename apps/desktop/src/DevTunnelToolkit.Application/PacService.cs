using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.Application;

/// <summary>Opt-in loopback-only PAC delivery. Never changes operating system proxy settings.</summary>
public sealed class PacService(Func<AgentSnapshot> snapshot) : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private TcpListener? listener;
    private readonly SemaphoreSlim clients = new(16);
    private readonly List<Task> workers = [];
    private Task accept = Task.CompletedTask;
    private int boundPort;

    public void Start(int port)
    {
        listener = new(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        try
        {
            listener.Start(16);
            boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        catch (SocketException) { listener.Stop(); throw new ToolkitException("PAC_PORT_UNAVAILABLE"); }
        accept = AcceptAsync();
    }

    public static string Generate(AgentSnapshot state)
    {
        Validation.Pac(state.Settings.Pac);
        if (state.Settings.Pac.Mode == PacMode.Manual) return state.Settings.Pac.ManualScript;
        if (state.Settings.Pac.Mode == PacMode.LocalProxy)
        {
            var settings = state.Settings.Proxy;
            var address = settings.ListenAddress.Contains(':') ? $"[{settings.ListenAddress}]" : settings.ListenAddress;
            return $"function FindProxyForURL(url, host) {{ return {JsonSerializer.Serialize($"PROXY {address}:{settings.Port}")}; }}\n";
        }
        var script = new StringBuilder("function FindProxyForURL(url, host) {\n  host = host.toLowerCase();\n");
        // More-specific domains win independently from UI ordering.
        foreach (var rule in state.Settings.Pac.Rules.OrderByDescending(r => r.Domain.Length))
        {
            var domain = JsonSerializer.Serialize(rule.Domain.ToLowerInvariant());
            script.Append("  if (host === ").Append(domain);
            if (rule.IncludeSubdomains)
                script.Append(" || dnsDomainIs(host, ").Append(JsonSerializer.Serialize("." + rule.Domain.ToLowerInvariant())).Append(')');
            var mapping = state.Connections.Where(c => c.ProfileId == rule.ProfileId && c.State == ConnectionState.Connected).SelectMany(c => c.Ports ?? []).FirstOrDefault(p => p.RemotePort == rule.RemotePort);
            // DIRECT is an explicit per-rule choice; preserved rules remain fail-closed.
            var proxy = mapping is null ? "PROXY 127.0.0.1:1" : $"{(rule.Protocol == ProxyProtocol.Http ? "PROXY" : "SOCKS5")} {(mapping.LocalAddress.Contains(':') ? $"[{mapping.LocalAddress}]" : mapping.LocalAddress)}:{mapping.LocalPort}";
            if (rule.DirectFallback) proxy = mapping is null ? "DIRECT" : proxy + "; DIRECT";
            script.Append(") return ").Append(JsonSerializer.Serialize(proxy)).Append(";\n");
        }
        return script.Append("  return \"DIRECT\";\n}\n").ToString();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await clients.WaitAsync(stop.Token);
                TcpClient client;
                try { client = await listener!.AcceptTcpClientAsync(stop.Token); }
                catch { clients.Release(); throw; }
                workers.RemoveAll(t => t.IsCompleted);
                workers.Add(ServeAsync(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
                    if (read == 0) return;
                    length += read;
                    if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) break;
                }
                var request = Encoding.ASCII.GetString(buffer, 0, length);
                var isGet = request.StartsWith("GET /proxy.pac HTTP/1.1\r\n", StringComparison.Ordinal) || request.StartsWith("GET /proxy.pac HTTP/1.0\r\n", StringComparison.Ordinal);
                var headerEnd = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                var hosts = (headerEnd < 0 ? request : request[..headerEnd]).Split("\r\n").Where(h => h.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).Select(h => h[5..].Trim()).ToArray();
                var safeHost = hosts.Length == 1 && (hosts[0] == $"127.0.0.1:{boundPort}" || hosts[0].Equals($"localhost:{boundPort}", StringComparison.OrdinalIgnoreCase));
                var valid = isGet && headerEnd >= 0 && safeHost;
                var body = Encoding.UTF8.GetBytes(valid ? Generate(snapshot()) : "Not found\n");
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "404 Not Found")}\r\nContent-Type: {(valid ? "application/x-ns-proxy-autoconfig" : "text/plain")}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nCross-Origin-Resource-Policy: same-origin\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, timeout.Token);
                await stream.WriteAsync(body, timeout.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ToolkitException) { }
            finally { clients.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener?.Stop();
        await accept;
        await Task.WhenAll(workers);
        clients.Dispose();
        stop.Dispose();
    }
}
