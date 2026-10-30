using System.Buffers.Binary;
using System.Text.Json;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.Ipc;

public sealed record AgentRequest(int Version, string Operation, JsonElement Payload);
public sealed record AgentReply(bool Success, JsonElement? Result = null, string? ErrorCode = null, string? Message = null, string? Stage = null);
public sealed record DesktopLoginCompletion(bool Success, bool Cancelled = false, string? ErrorCode = null);

public static class WireProtocol
{
    // v7 separates service lifecycle and publication bindings. Reject older clients
    // before they overwrite independent services or interpret a binding as a listener.
    // Reject incompatible peers before dispatching or silently dropping settings.
    public const int Version = 7;

    public static string SafeDesktopLoginError(string? code) => code switch
    {
        "AUTH_WINDOW_UNAVAILABLE" or "AUTH_FLOW_CHANGED" or "EXECUTION_BLOCKED_BY_POLICY" or
        "DEVTUNNEL_CLI_UNAVAILABLE" or "DEVTUNNEL_CLI_TIMEOUT" or "DEVTUNNEL_CLI_PATH_INVALID" => code,
        _ when code is not null && code.StartsWith("AUTH_PROVIDER_AADSTS", StringComparison.Ordinal) &&
            code[20..] is { Length: >= 4 and <= 12 } digits && digits.All(char.IsAsciiDigit) => code,
        _ => "DESKTOP_LOGIN_FAILED"
    };
    // Requests stay small; replies must accommodate the bounded maximum workspace
    // (200 tunnel profiles with 100 mappings each, plus current connection state).
    public const int MaxFrameBytes = 1024 * 1024;
    public const int MaxReplyFrameBytes = 16 * 1024 * 1024;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > FrameLimit<T>()) throw new ToolkitException("IPC_FRAME_TOO_LARGE");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size <= 0 || size > FrameLimit<T>()) throw new ToolkitException("IPC_INVALID_FRAME");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new ToolkitException("IPC_INVALID_MESSAGE");
    }

    private static int FrameLimit<T>() => typeof(T) == typeof(AgentReply) ? MaxReplyFrameBytes : MaxFrameBytes;
}
