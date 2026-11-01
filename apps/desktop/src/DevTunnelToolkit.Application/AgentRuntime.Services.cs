using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.Application;

public sealed partial class AgentRuntime
{
    private readonly ConcurrentDictionary<string, ServiceStatus> serviceStatuses = new();
    private ManagedServiceRegistry services = null!;

    private async Task<object> DispatchServicesAsync(string operation, JsonElement payload, CancellationToken cancellationToken)
    {
        await mutation.WaitAsync(cancellationToken);
        try
        {
            if (operation == "service.save")
            {
                var profile = Read<ServiceProfile>(payload);
                ServiceValidation.Profile(profile);
                if (services.IsRunning(profile.Id)) throw new ToolkitException("SERVICE_RUNNING");
                if (!state.Services.Any(s => s.Id == profile.Id) && state.Services.Length >= 10000)
                    throw new ToolkitException("SERVICE_LIMIT_REACHED");
                var next = state.Services.Where(s => s.Id != profile.Id).Append(profile).ToArray();
                foreach (var host in state.Hosts) ServiceValidation.Bindings(host, next);
                await SaveAsync(state with { Services = next }, cancellationToken);
                SetServiceStatus(new(profile.Id, ConnectionState.Stopped));
                Log("Information", "SERVICE_SAVED", "Independent local service configuration saved.", profile.Id);
                return profile;
            }
            var id = Id(payload);
            var service = state.Services.FirstOrDefault(s => s.Id == id) ?? throw new ToolkitException("SERVICE_NOT_FOUND");
            switch (operation)
            {
                case "service.start": await StartServiceAsync(service, cancellationToken); return true;
                case "service.stop":
                    SetServiceStatus(new(id, ConnectionState.Stopping, Phase: "stopping"));
                    await services.StopAsync(id);
                    SetServiceStatus(new(id, ConnectionState.Stopped));
                    foreach (var host in state.Hosts.Where(h => h.Ports.Any(p => p.ServiceId == id)))
                        if (hostStatuses.TryGetValue(host.Id, out var status) && status.State is ConnectionState.Connected or ConnectionState.Degraded)
                            SetHostStatus(status with
                            {
                                State = ConnectionState.Degraded,
                                LastError = "SERVICE_BINDING_REVOKED",
                                Ports = status.Ports?.Where(p => p.ServiceId != id).ToArray(),
                                Phase = "service-stopped"
                            });
                    Log("Information", "SERVICE_STOPPED", "Service stopped. Its publication bindings were revoked; other services remain available.", id);
                    return true;
                case "service.remove":
                    if (state.Hosts.Any(h => h.Ports.Any(p => p.ServiceId == id))) throw new ToolkitException("SERVICE_IN_USE");
                    if (services.IsRunning(id)) throw new ToolkitException("SERVICE_RUNNING");
                    await SaveAsync(state with { Services = [.. state.Services.Where(s => s.Id != id)] }, cancellationToken);
                    serviceStatuses.TryRemove(id, out _);
                    Log("Information", "SERVICE_REMOVED", "Local service configuration removed.", id);
                    return true;
                default: throw new ToolkitException("UNKNOWN_OPERATION");
            }
        }
        finally { mutation.Release(); }
    }

    private async Task StartServiceAsync(ServiceProfile profile, CancellationToken cancellationToken)
    {
        if (services.IsRunning(profile.Id)) return;
        lock (sync)
        {
            if (profile.Port != 0 && state.Hosts.Any(h => hostingOperations.TryGetValue(h.Id, out var operation) && !operation.Completion.IsCompleted &&
                h.Ports.Any(p => p.Kind == HostPortKind.Service && p.LocalPort == profile.Port)))
                throw new ToolkitException("SERVICE_BINDING_REQUIRED");
        }
        if (profile.Port != 0 &&
            (state.Settings.Proxy.Enabled && profile.Port == state.Settings.Proxy.Port ||
             state.Settings.Pac.Enabled && profile.Port == state.Settings.Pac.Port ||
             connections.Values.Any(c => c.Ports?.Any(p => p.LocalPort == profile.Port) == true) ||
             serviceStatuses.Values.Any(s => s.ServiceId != profile.Id && s.State == ConnectionState.Connected && s.Port == profile.Port)))
            throw new ToolkitException("SERVICE_PORT_CONFLICT");
        SetServiceStatus(new(profile.Id, ConnectionState.Connecting, Phase: "starting-listener"));
        try
        {
            // Let the engine bind port zero directly, then atomically register its
            // reservation. A free-port probe would release the socket before use.
            var endpoint = await services.StartAsync(profile, cancellationToken);
            lock (sync)
            {
                if (state.Hosts.Any(h => hostingOperations.TryGetValue(h.Id, out var operation) && !operation.Completion.IsCompleted &&
                    h.Ports.Any(p => p.Kind == HostPortKind.Service && p.LocalPort == endpoint.LocalPort)))
                    throw new ToolkitException("SERVICE_BINDING_REQUIRED");
            }
            SetServiceStatus(new(profile.Id, ConnectionState.Connected, LocalAddress: endpoint.LocalAddress, Port: endpoint.LocalPort, Phase: "ready"));
            Log("Information", "SERVICE_STARTED", "Local service ready. Publishing requires an explicit DevTunnel binding.", profile.Id);
        }
        catch (Exception error)
        {
            await services.StopAsync(profile.Id);
            var code = error switch
            {
                ToolkitException toolkit => toolkit.Code,
                OperationCanceledException => "OPERATION_CANCELLED",
                SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied } => "SERVICE_PORT_CONFLICT",
                _ => "SERVICE_START_FAILED"
            };
            SetServiceStatus(new(profile.Id, ConnectionState.Failed, code));
            if (error is OperationCanceledException) throw;
            throw new ToolkitException(code);
        }
    }

    private void SetServiceStatus(ServiceStatus value)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = value.State is ConnectionState.Connecting or ConnectionState.Stopping;
        serviceStatuses.AddOrUpdate(value.ServiceId,
            value with { OperationStartedAt = now, OperationCompletedAt = pending ? null : now },
            (_, previous) => value with
            {
                OperationStartedAt = pending ? now : previous.OperationStartedAt ?? now,
                OperationCompletedAt = pending ? null : now
            });
    }
}
