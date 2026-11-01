using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DevTunnelToolkit.Core;

public sealed record ServiceProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public ProxyProtocol Protocol { get; init; }
    public string LocalAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 3140;
    public bool AutoStart { get; init; }
    public PublishedProxyPolicy Policy { get; init; } = new();
}

public sealed record ServiceStatus(string ServiceId, ConnectionState State, string? LastError = null,
    string? LocalAddress = null, int? Port = null, DateTimeOffset? OperationStartedAt = null,
    DateTimeOffset? OperationCompletedAt = null, string? Phase = null);

/// <summary>A binding is tied to one running generation, never a reusable address/port.</summary>
public interface IManagedServiceRegistry
{
    IManagedServiceBinding Acquire(string serviceId, int remotePort, HostPortKind kind);
}

public interface IManagedServiceBinding : IDisposable
{
    HostedPort Port { get; }
    PublishedProxyPolicy Policy { get; }
    bool Available { get; }
    Task<ManagedServiceConnection> ConnectAsync(CancellationToken cancellationToken);
}

public sealed class ManagedServiceConnection(TcpClient client, CancellationToken revoked, Action release) : IDisposable
{
    private int disposed;
    public TcpClient Client { get; } = client;
    public CancellationToken Revoked { get; } = revoked;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { Client.Dispose(); }
        finally { release(); }
    }
}

public static class ServiceValidation
{
    public static void Profile(ServiceProfile profile)
    {
        Validation.Id(profile.Id);
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 120 || profile.Name.Any(char.IsControl) ||
            !Enum.IsDefined(profile.Protocol) || profile.Port is < 0 or > 65535)
            throw new ToolkitException("SERVICE_PROFILE_INVALID");
        if (!IPAddress.TryParse(profile.LocalAddress, out var address) || !IPAddress.IsLoopback(address))
            throw new ToolkitException("LOOPBACK_REQUIRED");
        HostingValidation.Policy(profile.Policy);
    }

    public static void Bindings(HostProfile host, ServiceProfile[] services)
    {
        foreach (var binding in host.Ports.Where(p => p.Kind != HostPortKind.Service))
        {
            var service = services.FirstOrDefault(s => s.Id == binding.ServiceId) ?? throw new ToolkitException("SERVICE_NOT_FOUND");
            if (binding.Kind != Kind(service.Protocol)) throw new ToolkitException("SERVICE_PROTOCOL_MISMATCH");
        }
    }

    public static HostPortKind Kind(ProxyProtocol protocol) => protocol == ProxyProtocol.Http ? HostPortKind.HttpProxy : HostPortKind.Socks5Proxy;
}

public static class ServiceMigration
{
    /// <summary>One service per legacy proxy mapping. Stable IDs prevent duplication after restart.
    /// Only legacy records without a service reference are converted; nothing is deduplicated.</summary>
    public static ToolkitState Upgrade(ToolkitState state)
    {
        if (state.Hosts is null || state.Services is null) throw new ToolkitException("INVALID_SAVED_STATE");
        var services = state.Services.ToList();
        var changed = false;
        var hosts = state.Hosts.Select(host => host with
        {
            Ports = host.Ports.Select(port =>
            {
                if (port.Kind == HostPortKind.Service || port.ServiceId is not null) return port;
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"desktop-service-v1:{host.Id}:{port.RemotePort}")))[..32].ToLowerInvariant();
                if (services.Any(s => s.Id == id)) throw new ToolkitException("SERVICE_MIGRATION_CONFLICT");
                var label = host.Alias.Length > 0 ? host.Alias : host.CanonicalId;
                var name = $"{label} / {port.RemotePort}";
                services.Add(new ServiceProfile
                {
                    Id = id,
                    Name = name.Length > 120 ? name[..120] : name,
                    Protocol = port.Kind == HostPortKind.HttpProxy ? ProxyProtocol.Http : ProxyProtocol.Socks5,
                    LocalAddress = port.LocalAddress,
                    Port = port.LocalPort,
                    AutoStart = host.AutoStart,
                    Policy = host.Proxy
                });
                changed = true;
                return port with { ServiceId = id };
            }).ToArray()
        }).ToArray();
        return changed ? state with { Services = [.. services], Hosts = hosts } : state;
    }
}
