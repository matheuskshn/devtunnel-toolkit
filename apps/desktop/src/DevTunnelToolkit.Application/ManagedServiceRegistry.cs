using System.Net;
using System.Net.Sockets;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.Application;

/// <summary>Owns listeners independently of any account or relay. Publication leases pin a
/// generation, and are revoked before its listener can be reused by another process.</summary>
public sealed class ManagedServiceRegistry(IPublishedProxyFactory factory) : IManagedServiceRegistry, IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = [];
    private readonly SemaphoreSlim mutation = new(1, 1);

    public bool IsRunning(string id) { lock (sync) return entries.ContainsKey(id); }
    public Guid? Generation(string id) { lock (sync) return entries.TryGetValue(id, out var entry) ? entry.Generation : null; }

    public HostedPort? Endpoint(string id)
    {
        lock (sync) return entries.TryGetValue(id, out var entry) ? entry.Port : null;
    }

    public async Task<HostedPort> StartAsync(ServiceProfile profile, CancellationToken cancellationToken)
    {
        ServiceValidation.Profile(profile);
        await mutation.WaitAsync(cancellationToken);
        try
        {
            lock (sync) if (entries.TryGetValue(profile.Id, out var current)) return current.Port;
            // The existing proxy engine is only a listener factory. Its synthetic resource
            // identifiers never leave this process and are never sent to Dev Tunnels.
            var proxy = await factory.StartAsync(new HostProfile
            {
                Id = profile.Id,
                AccountId = profile.Id,
                TunnelId = "local-service",
                ClusterId = "use2",
                Proxy = profile.Policy,
                Ports = [new() { RemotePort = 1, Kind = ServiceValidation.Kind(profile.Protocol), LocalAddress = profile.LocalAddress, LocalPort = profile.Port }]
            }, cancellationToken);
            try
            {
                var endpoint = proxy.Ports.Single() with { ServiceId = profile.Id };
                lock (sync) entries.Add(profile.Id, new(proxy, endpoint, profile.Policy));
                return endpoint;
            }
            catch { await proxy.DisposeAsync(); throw; }
        }
        finally { mutation.Release(); }
    }

    public async Task StopAsync(string id)
    {
        await mutation.WaitAsync();
        try
        {
            Entry? entry;
            lock (sync) entries.Remove(id, out entry);
            if (entry is not null) await entry.StopAsync();
        }
        finally { mutation.Release(); }
    }

    public IManagedServiceBinding Acquire(string serviceId, int remotePort, HostPortKind kind)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(serviceId, out var entry)) throw new ToolkitException("SERVICE_NOT_RUNNING");
            if (entry.Port.Kind != kind) throw new ToolkitException("SERVICE_PROTOCOL_MISMATCH");
            return new Binding(entry, entry.Port with { RemotePort = remotePort });
        }
    }

    public async ValueTask DisposeAsync()
    {
        string[] ids;
        lock (sync) ids = [.. entries.Keys];
        foreach (var id in ids) await StopAsync(id);
        mutation.Dispose();
    }

    private sealed class Entry(IPublishedProxy proxy, HostedPort port, PublishedProxyPolicy policy)
    {
        private readonly object sync = new();
        private readonly CancellationTokenSource revoked = new();
        private bool available = true;
        public Guid Generation { get; } = Guid.NewGuid();
        public HostedPort Port { get; } = port;
        public PublishedProxyPolicy Policy { get; } = policy;
        public bool Available { get { lock (sync) return available; } }

        public async Task<ManagedServiceConnection> ConnectAsync(CancellationToken binding, CancellationToken cancellationToken)
        {
            TcpClient client;
            CancellationTokenSource lifetime;
            CancellationTokenSource opening;
            CancellationTokenRegistration registration;
            ValueTask connecting;
            lock (sync)
            {
                if (!available || binding.IsCancellationRequested) throw new ToolkitException("SERVICE_BINDING_REVOKED");
                client = new(IPAddress.Parse(Port.LocalAddress).AddressFamily);
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(revoked.Token, binding);
                opening = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
                registration = lifetime.Token.Register(client.Dispose);
                try { connecting = client.ConnectAsync(IPAddress.Parse(Port.LocalAddress), Port.LocalPort, opening.Token); }
                catch
                {
                    client.Dispose(); registration.Dispose(); opening.Dispose(); lifetime.Dispose(); throw;
                }
            }
            try
            {
                await connecting;
                lifetime.Token.ThrowIfCancellationRequested();
                return new(client, lifetime.Token, () => { registration.Dispose(); lifetime.Dispose(); });
            }
            catch
            {
                client.Dispose(); registration.Dispose(); lifetime.Dispose(); throw;
            }
            finally { opening.Dispose(); }
        }

        public async Task StopAsync()
        {
            lock (sync) available = false;
            // Await all revocation callbacks before releasing the listening socket. This
            // closes pending and established bridges before a port can be reused.
            await revoked.CancelAsync();
            try { await proxy.DisposeAsync(); }
            finally { revoked.Dispose(); }
        }
    }

    private sealed class Binding(Entry entry, HostedPort port) : IManagedServiceBinding
    {
        private readonly CancellationTokenSource revoked = new();
        private int disposed;
        public HostedPort Port { get; } = port;
        public PublishedProxyPolicy Policy => entry.Policy;
        public bool Available => Volatile.Read(ref disposed) == 0 && entry.Available;
        public Task<ManagedServiceConnection> ConnectAsync(CancellationToken cancellationToken) =>
            Available ? entry.ConnectAsync(revoked.Token, cancellationToken) : throw new ToolkitException("SERVICE_BINDING_REVOKED");
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            revoked.Cancel(); revoked.Dispose();
        }
    }
}
