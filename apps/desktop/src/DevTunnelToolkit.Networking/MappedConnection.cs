using System.Net;
using System.Net.Sockets;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.Networking;

/// <summary>
/// Owns local loopback listeners and their client streams, but never owns the relay.
/// Binding a complete mapping set is transactional: a failed bind releases all earlier binds.
/// </summary>
public sealed class MappedConnection : IAsyncDisposable
{
    private readonly IRelayConnection relay;
    private readonly MappedConnectionOptions options;
    private readonly CancellationTokenSource lifetime;
    private readonly List<(TcpListener Listener, int RemotePort)> listeners;
    private readonly Dictionary<long, (TcpClient Client, Task Completion)> clients = [];
    private readonly object sync = new();
    private readonly Task[] acceptTasks;
    private readonly CancellationTokenRegistration stopRegistration;
    private long nextClientId;
    private Task? disposeTask;
    private int failedOpenings;
    public event Action? DataPathFailed;

    private MappedConnection(IRelayConnection relay, List<(TcpListener Listener, int RemotePort)> listeners,
        MappedConnectionOptions options, CancellationToken cancellationToken)
    {
        this.relay = relay;
        this.listeners = listeners;
        this.options = options;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Ports = Array.AsReadOnly(listeners.Select(item =>
        {
            var endpoint = (IPEndPoint)item.Listener.LocalEndpoint;
            return new EffectiveMapping(item.RemotePort, endpoint.Address.ToString(), endpoint.Port);
        }).ToArray());
        stopRegistration = lifetime.Token.Register(StopListeners);
        acceptTasks = listeners.Select(AcceptAsync).ToArray();
    }

    public IReadOnlyList<EffectiveMapping> Ports { get; }
    public bool AtCapacity { get { lock (sync) return clients.Count >= options.MaxConcurrentClients; } }

    public int ActiveClientCount
    {
        get { lock (sync) return clients.Count; }
    }

    public static Task<MappedConnection> StartAsync(IRelayConnection relay,
        IReadOnlyList<PortMapping> mappings, CancellationToken cancellationToken = default) =>
        StartAsync(relay, mappings, new MappedConnectionOptions(), cancellationToken);

    public static Task<MappedConnection> StartAsync(IRelayConnection relay,
        IReadOnlyList<PortMapping> mappings, MappedConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (mappings.Count > 100 || mappings.Select(mapping => mapping.RemotePort).Distinct().Count() != mappings.Count)
            throw new ToolkitException("DUPLICATE_OR_EXCESSIVE_MAPPINGS");
        foreach (var mapping in mappings) Validation.Mapping(mapping);

        List<(TcpListener Listener, int RemotePort)> listeners = [];
        try
        {
            foreach (var mapping in mappings.Where(mapping => mapping.Enabled && mapping.Mode != MappingMode.Disabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                listeners.Add((Bind(mapping, options.ListenBacklog, cancellationToken), mapping.RemotePort));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new MappedConnection(relay, listeners, options, cancellationToken));
        }
        catch
        {
            foreach (var (listener, _) in listeners) listener.Stop();
            throw;
        }
    }

    private static TcpListener Bind(PortMapping mapping, int backlog, CancellationToken cancellationToken)
    {
        var address = IPAddress.Parse(mapping.LocalAddress);
        foreach (var candidate in Candidates(mapping).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listener = new TcpListener(address, candidate);
            // Checking a free port before binding would leave a race. The successful bind
            // itself reserves the port, including between mappings in this transaction.
            listener.Server.ExclusiveAddressUse = true;
            try
            {
                listener.Start(backlog);
                return listener;
            }
            catch (SocketException exception) when (exception.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                listener.Stop();
            }
            catch
            {
                listener.Stop();
                throw;
            }
        }
        throw new ToolkitException(mapping.Mode is MappingMode.Automatic or MappingMode.ReservedRange ? "PORT_RANGE_EXHAUSTED" : "LOCAL_PORT_UNAVAILABLE");
    }

    private static IEnumerable<int> Candidates(PortMapping mapping)
    {
        if (mapping.Mode == MappingMode.Manual)
        {
            yield return mapping.LocalPort;
            yield break;
        }
        if (mapping.Mode == MappingMode.SamePort)
        {
            yield return mapping.RemotePort;
            yield break;
        }
        if (mapping.LastEffectivePort is { } previous &&
            (mapping.Mode == MappingMode.Automatic || previous >= mapping.RangeStart && previous <= mapping.RangeEnd))
            yield return previous;
        if (mapping.Mode == MappingMode.Automatic) yield return mapping.RemotePort;
        for (var port = mapping.RangeStart; port <= mapping.RangeEnd; port++) yield return port;
    }

    private async Task AcceptAsync((TcpListener Listener, int RemotePort) mapping)
    {
        var cancellationToken = lifetime.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await mapping.Listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                // Do not enqueue unbounded tasks waiting for a semaphore. Refuse excess
                // clients immediately while keeping all existing connections intact.
                lock (sync)
                {
                    if (cancellationToken.IsCancellationRequested || clients.Count >= options.MaxConcurrentClients)
                    {
                        client.Dispose();
                        continue;
                    }
                    var id = Interlocked.Increment(ref nextClientId);
                    var completion = ForwardAsync(client, mapping.RemotePort, cancellationToken);
                    clients.Add(id, (client, completion));
                    _ = completion.ContinueWith(completed =>
                    {
                        // Observe unexpected adapter faults even when the client has
                        // already gone away and the relay itself remains healthy.
                        _ = completed.Exception;
                        lock (sync) clients.Remove(id);
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ForwardAsync(TcpClient client, int remotePort, CancellationToken cancellationToken)
    {
        using (client)
        using (var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        using (var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            try
            {
                opening.CancelAfter(options.OpenStreamTimeout);
                Stream opened;
                try
                {
                    opened = await relay.OpenStreamAsync(remotePort, opening.Token).ConfigureAwait(false);
                    Interlocked.Exchange(ref failedOpenings, 0);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    if (Interlocked.Increment(ref failedOpenings) == 3) DataPathFailed?.Invoke();
                    throw;
                }
                await using var remote = opened;
                var local = client.GetStream();
                using var closeOnCancellation = connection.Token.Register(() =>
                {
                    client.Dispose();
                    remote.Dispose();
                });
                var outgoing = CopyToRemoteAsync(local, remote, connection.Token);
                var incoming = CopyToLocalAsync(remote, local, client, connection.Token);
                try
                {
                    // EOF in one direction is not the end of the other direction.
                    // Protocols commonly send a complete response after client half-close.
                    var first = await Task.WhenAny(outgoing, incoming).ConfigureAwait(false);
                    await first.ConfigureAwait(false);
                    connection.CancelAfter(options.DrainTimeout);
                    await Task.WhenAll(outgoing, incoming).ConfigureAwait(false);
                }
                finally
                {
                    await connection.CancelAsync().ConfigureAwait(false);
                    try { await Task.WhenAll(outgoing, incoming).ConfigureAwait(false); }
                    catch (Exception exception) when (IsConnectionException(exception)) { }
                }
            }
            catch (Exception exception) when (IsConnectionException(exception))
            {
                // Client/remote failures are contained to this stream. The relay owner
                // supervises relay lifecycle independently from transient TCP clients.
            }
        }
    }

    private static async Task CopyToRemoteAsync(Stream local, Stream remote, CancellationToken cancellationToken)
    {
        await local.CopyToAsync(remote, 65536, cancellationToken).ConfigureAwait(false);
        await remote.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (remote is IHalfCloseStream halfClose)
            await halfClose.CompleteWritesAsync(cancellationToken).ConfigureAwait(false);
        // An ordinary Stream has no write-half-close API. Keep its read side alive
        // for the configured drain interval instead of disposing it on local EOF.
    }

    private static async Task CopyToLocalAsync(Stream remote, Stream local, TcpClient client, CancellationToken cancellationToken)
    {
        await remote.CopyToAsync(local, 65536, cancellationToken).ConfigureAwait(false);
        await local.FlushAsync(cancellationToken).ConfigureAwait(false);
        try { client.Client.Shutdown(SocketShutdown.Send); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static bool IsConnectionException(Exception exception) =>
        exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException or ToolkitException;

    private void StopListeners()
    {
        foreach (var (listener, _) in listeners) listener.Stop();
        lock (sync)
        {
            foreach (var client in clients.Values) client.Client.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        // Cancellation callbacks also take sync. Run disposal after releasing the
        // monitor, including when cancellation and the first awaits complete inline.
        lock (sync) return new ValueTask(disposeTask ??= Task.Run(DisposeCoreAsync));
    }

    private async Task DisposeCoreAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(acceptTasks).ConfigureAwait(false);
        Task[] pending;
        lock (sync) pending = clients.Values.Select(client => client.Completion).ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        finally
        {
            stopRegistration.Dispose();
            lifetime.Dispose();
            lock (sync) clients.Clear();
        }
    }
}
