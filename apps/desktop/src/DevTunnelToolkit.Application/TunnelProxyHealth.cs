using System.Net;
using System.Net.Sockets;
using System.Text;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.Application;

/// <summary>Checks only explicitly configured proxy ports. Never contacts a destination or sends credentials.</summary>
public static class TunnelProxyHealth
{
    public static async Task CheckAsync(IRelayConnection relay, int port, ProxyProtocol protocol, CancellationToken cancellationToken)
    {
        await using var stream = await relay.OpenStreamAsync(port, cancellationToken);
        await CheckStreamAsync(stream, protocol, cancellationToken);
    }

    public static async Task CheckAsync(EffectiveMapping mapping, ProxyProtocol protocol, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(mapping.LocalAddress, out var address) || !IPAddress.IsLoopback(address))
            throw new ToolkitException("LOOPBACK_REQUIRED");
        using var client = new TcpClient(address.AddressFamily);
        await client.ConnectAsync(address, mapping.LocalPort, cancellationToken);
        await CheckStreamAsync(client.GetStream(), protocol, cancellationToken);
    }

    private static async Task CheckStreamAsync(Stream stream, ProxyProtocol protocol, CancellationToken cancellationToken)
    {
        if (protocol == ProxyProtocol.Socks5)
        {
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken);
            var reply = new byte[2];
            await stream.ReadExactlyAsync(reply, cancellationToken);
            if (reply[0] != 5 || reply[1] != 0) throw new ToolkitException("TUNNEL_PROXY_PROTOCOL_INVALID");
            return;
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes("OPTIONS * HTTP/1.1\r\nHost: proxy.invalid\r\nConnection: close\r\n\r\n"), cancellationToken);
        var header = new byte[4096];
        var count = 0;
        var terminated = false;
        while (count < header.Length)
        {
            var read = await stream.ReadAsync(header.AsMemory(count, 1), cancellationToken);
            if (read == 0) throw new IOException("Proxy closed before its response.");
            count += read;
            if (count >= 2 && header[count - 2] == '\r' && header[count - 1] == '\n') { terminated = true; break; }
        }
        var line = Encoding.ASCII.GetString(header, 0, count).Split(' ');
        if (!terminated || line.Length < 2 || line[0] is not ("HTTP/1.0" or "HTTP/1.1") || line[1].Length != 3 || !int.TryParse(line[1], out var status) || status is < 100 or > 599)
            throw new ToolkitException("TUNNEL_PROXY_PROTOCOL_INVALID");
        // Any valid response proves this proxy channel answered, including a policy denial.
        // This does not certify destination reachability or its TLS certificate.
    }
}
