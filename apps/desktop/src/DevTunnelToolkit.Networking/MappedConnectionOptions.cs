namespace DevTunnelToolkit.Networking;

/// <summary>Bounds the resources consumed by one connected tunnel.</summary>
public sealed record MappedConnectionOptions
{
    public int MaxConcurrentClients { get; init; } = 128;
    public int ListenBacklog { get; init; } = 64;
    public TimeSpan OpenStreamTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (MaxConcurrentClients is < 1 or > 4096 || ListenBacklog is < 1 or > 4096 ||
            OpenStreamTimeout <= TimeSpan.Zero || OpenStreamTimeout > TimeSpan.FromMinutes(5) ||
            DrainTimeout <= TimeSpan.Zero || DrainTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(MappedConnectionOptions));
        }
    }
}
