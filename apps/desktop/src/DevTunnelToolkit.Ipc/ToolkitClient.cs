using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Platform;

namespace DevTunnelToolkit.Ipc;

public sealed class ToolkitClient(string? dataDirectory = null)
{
    private readonly LocalPaths paths = new(dataDirectory);

    public async Task<LoginStatus> LoginFromDesktopAsync(string id, Func<DesktopLoginRequest, CancellationToken, Task> authenticate, CancellationToken cancellationToken = default)
    {
        Validation.Id(id);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromMinutes(15));
        await using var stream = await ConnectAsync(lifetime.Token);
        await WireProtocol.WriteAsync(stream, new AgentRequest(WireProtocol.Version, "account.login.desktop", JsonSerializer.SerializeToElement(new { id })), lifetime.Token);
        var ready = await WireProtocol.ReadAsync<AgentReply>(stream, lifetime.Token);
        if (!ready.Success) throw new ToolkitException(ready.ErrorCode ?? "DESKTOP_LOGIN_FAILED");
        if (ready.Stage == "desktop-login-result")
            return ready.Result?.Deserialize<LoginStatus>(WireProtocol.Json) ?? throw new ToolkitException("IPC_INVALID_RESPONSE");
        if (ready.Stage != "desktop-login-ready") throw new ToolkitException("IPC_INVALID_RESPONSE");
        var request = ready.Result?.Deserialize<DesktopLoginRequest>(WireProtocol.Json) ?? throw new ToolkitException("IPC_INVALID_RESPONSE");
        var final = WireProtocol.ReadAsync<AgentReply>(stream, lifetime.Token);
        Task login;
        try { login = authenticate(request, lifetime.Token); }
        catch (Exception error) { login = Task.FromException(error); }
        try
        {
            if (await Task.WhenAny(login, final) == login)
            {
                DesktopLoginCompletion completion;
                try { await login; completion = new(true); }
                catch (OperationCanceledException) { completion = new(false, Cancelled: true); }
                catch (Exception error) { completion = new(false, ErrorCode: WireProtocol.SafeDesktopLoginError((error as ToolkitException)?.Code)); }
                await WireProtocol.WriteAsync(stream, completion, lifetime.Token);
            }
            var response = await final;
            if (!response.Success) throw new ToolkitException(response.ErrorCode ?? "DESKTOP_LOGIN_FAILED");
            if (response.Stage != "desktop-login-result") throw new ToolkitException("IPC_INVALID_RESPONSE");
            return response.Result?.Deserialize<LoginStatus>(WireProtocol.Json) ?? throw new ToolkitException("IPC_INVALID_RESPONSE");
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await login; } catch { /* Only the structured completion can cross IPC. */ }
            try { await final; } catch { /* Observe the disconnected/cancelled read. */ }
        }
    }

    public async Task<T> CallAsync<T>(string operation, object? payload = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        await using var stream = await ConnectAsync(timeout.Token);
        await WireProtocol.WriteAsync(stream, new AgentRequest(WireProtocol.Version, operation, JsonSerializer.SerializeToElement(payload, WireProtocol.Json)), timeout.Token);
        var response = await WireProtocol.ReadAsync<AgentReply>(stream, timeout.Token);
        if (!response.Success) throw new ToolkitException(response.ErrorCode ?? "AGENT_ERROR", response.Message);
        return response.Result!.Value.Deserialize<T>(WireProtocol.Json) ?? throw new ToolkitException("IPC_INVALID_RESPONSE");
    }

    public async IAsyncEnumerable<AgentSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = await ConnectAsync(cancellationToken);
        await WireProtocol.WriteAsync(stream, new AgentRequest(WireProtocol.Version, "watch", JsonSerializer.SerializeToElement<object?>(null)), cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            var response = await WireProtocol.ReadAsync<AgentReply>(stream, cancellationToken);
            if (!response.Success) throw new ToolkitException(response.ErrorCode ?? "AGENT_ERROR");
            yield return response.Result!.Value.Deserialize<AgentSnapshot>(WireProtocol.Json) ?? throw new ToolkitException("IPC_INVALID_RESPONSE");
        }
    }

    private async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", paths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(timeout.Token); return pipe; }
            catch { await pipe.DisposeAsync(); throw new ToolkitException("AGENT_UNAVAILABLE"); }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            LocalPaths.RejectLinks(paths.SocketPath);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(paths.SocketPath), timeout.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw new ToolkitException("AGENT_UNAVAILABLE"); }
    }
}
