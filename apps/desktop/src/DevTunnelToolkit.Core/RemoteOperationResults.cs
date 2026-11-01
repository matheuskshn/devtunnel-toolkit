namespace DevTunnelToolkit.Core;

public sealed record RemoteBatchResult(string CanonicalId, bool Succeeded, string? ErrorCode = null);
public sealed record HostPreparationResult(string CanonicalId, int[] CreatedPorts, int[] ExistingPorts);
public sealed record EchoStatus(bool Running, int Port = 0);
public sealed record EchoProbeResult(bool Succeeded, long ElapsedMilliseconds, string? ErrorCode = null);
