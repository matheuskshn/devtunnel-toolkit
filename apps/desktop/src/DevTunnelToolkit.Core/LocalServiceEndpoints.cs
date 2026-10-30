namespace DevTunnelToolkit.Core;

/// <summary>Process-local reservations keep raw TCP publication separate from managed
/// proxy listeners, including their private protocol adapters. No remote state is changed.</summary>
public static class LocalServiceEndpoints
{
    private static readonly object Sync = new();
    private static readonly Dictionary<int, int> Managed = [];
    private static readonly Dictionary<int, int> External = [];

    public static bool IsManagedPort(int port)
    {
        lock (Sync) return Managed.ContainsKey(port);
    }

    public static bool TryRegisterManagedPort(int port)
    {
        if (port is < 1 or > 65535) throw new ToolkitException("INVALID_PORT");
        lock (Sync)
        {
            if (External.ContainsKey(port)) return false;
            Managed[port] = Managed.GetValueOrDefault(port) + 1;
            return true;
        }
    }

    public static void UnregisterManagedPort(int port)
    {
        lock (Sync) Release(Managed, port);
    }

    public static IDisposable ReserveExternalPorts(IEnumerable<int> ports)
    {
        var unique = ports.Distinct().ToArray();
        if (unique.Any(port => port is < 1 or > 65535)) throw new ToolkitException("INVALID_PORT");
        lock (Sync)
        {
            if (unique.Any(Managed.ContainsKey)) throw new ToolkitException("SERVICE_BINDING_REQUIRED");
            foreach (var port in unique) External[port] = External.GetValueOrDefault(port) + 1;
        }
        return new Reservation(unique);
    }

    private static void Release(Dictionary<int, int> entries, int port)
    {
        if (!entries.TryGetValue(port, out var count)) return;
        if (count == 1) entries.Remove(port);
        else entries[port] = count - 1;
    }

    private sealed class Reservation(int[] ports) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (Sync) foreach (var port in ports) Release(External, port);
        }
    }
}
