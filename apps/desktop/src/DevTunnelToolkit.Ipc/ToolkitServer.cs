using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Platform;

namespace DevTunnelToolkit.Ipc;

public sealed class ToolkitServer(LocalPaths paths, Func<string, JsonElement, CancellationToken, Task<object>> dispatch, Func<AgentSnapshot> snapshot, Action? requestShutdown = null,
    Func<string, Func<DesktopLoginRequest, CancellationToken, Task>, CancellationToken, Task<object>>? desktopLogin = null)
{
    private readonly SemaphoreSlim slots = new(32, 32);
    private readonly ConcurrentDictionary<long, Task> clients = new();
    private long sequence;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        paths.Initialize();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Socket? listener = null;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                LocalPaths.RejectLinks(paths.SocketPath);
                if (File.Exists(paths.SocketPath)) File.Delete(paths.SocketPath); // Agent singleton lock is held by the caller.
                listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(paths.SocketPath));
                File.SetUnixFileMode(paths.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                listener.Listen(32);
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                await slots.WaitAsync(cancellationToken);
                Stream stream;
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        var pipe = new NamedPipeServerStream(paths.PipeName, PipeDirection.InOut, 32, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        try { await pipe.WaitForConnectionAsync(cancellationToken); stream = pipe; }
                        catch { await pipe.DisposeAsync(); throw; }
                    }
                    else stream = new NetworkStream(await listener!.AcceptAsync(cancellationToken), ownsSocket: true);
                }
                catch { slots.Release(); throw; }
                var id = Interlocked.Increment(ref sequence);
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                clients[id] = completed.Task;
                _ = HandleAndReleaseAsync(id, stream, completed, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            listener?.Dispose();
            await lifetime.CancelAsync();
            await Task.WhenAll(clients.Values);
            if (!OperatingSystem.IsWindows() && File.Exists(paths.SocketPath)) File.Delete(paths.SocketPath);
            slots.Dispose();
        }
    }

    private async Task HandleAndReleaseAsync(long id, Stream stream, TaskCompletionSource completed, CancellationToken cancellationToken)
    {
        try { await HandleAsync(stream, cancellationToken); }
        catch { /* Transport disposal failures are isolated to this client too. */ }
        finally
        {
            // Completion includes slot cleanup. Shutdown must not dispose the semaphore while
            // an untracked continuation can still release it.
            slots.Release();
            completed.TrySetResult();
            clients.TryRemove(id, out _);
        }
    }

    private async Task HandleAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using (stream)
        using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var request = await WireProtocol.ReadAsync<AgentRequest>(stream, requestTimeout.Token);
                if (request.Version != WireProtocol.Version) throw new ToolkitException("IPC_VERSION_MISMATCH");
                if (request.Operation == "account.login.desktop")
                {
                    if (desktopLogin is null) throw new ToolkitException("DESKTOP_LOGIN_UNSUPPORTED");
                    requestTimeout.CancelAfter(TimeSpan.FromMinutes(15));
                    await HandleDesktopLoginAsync(stream, request.Payload.GetProperty("id").GetString() ?? "", requestTimeout.Token);
                    return;
                }
                if (request.Operation == "watch")
                {
                    // Authorized state snapshots, no replay of another client's challenges or credentials.
                    using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));
                    do
                    {
                        using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        writeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                        await WireProtocol.WriteAsync(stream, new AgentReply(true, JsonSerializer.SerializeToElement(snapshot(), WireProtocol.Json)), writeTimeout.Token);
                    } while (await ticker.WaitForNextTickAsync(cancellationToken));
                    return;
                }
                if (request.Operation == "agent.shutdown")
                {
                    if (requestShutdown is null) throw new ToolkitException("SHUTDOWN_UNAVAILABLE");
                    await WireProtocol.WriteAsync(stream, new AgentReply(true, JsonSerializer.SerializeToElement(true)), requestTimeout.Token);
                    requestShutdown();
                    return;
                }
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(65));
                var result = await dispatch(request.Operation, request.Payload, requestTimeout.Token);
                await WireProtocol.WriteAsync(stream, new AgentReply(true, JsonSerializer.SerializeToElement(result, WireProtocol.Json)), requestTimeout.Token);
            }
            catch (Exception error) when (error is ToolkitException or JsonException or ArgumentException)
            {
                using var errorTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                errorTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                try { await WireProtocol.WriteAsync(stream, new AgentReply(false, ErrorCode: error is ToolkitException te ? te.Code : "INVALID_REQUEST"), errorTimeout.Token); }
                catch (Exception failed) when (failed is IOException or SocketException or OperationCanceledException) { }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
            catch
            {
                using var errorTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                errorTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                try { await WireProtocol.WriteAsync(stream, new AgentReply(false, ErrorCode: "OPERATION_FAILED"), errorTimeout.Token); }
                catch { /* Disconnected clients must not take down the agent. */ }
            }
        }
    }

    private async Task HandleDesktopLoginAsync(Stream stream, string accountId, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // One connection owns this attempt. EOF cancels it, including while waiting
        // for the shared CLI gate. No global completion nonce or credential payload.
        var completion = WireProtocol.ReadAsync<DesktopLoginCompletion>(stream, lifetime.Token);
        Task<object>? operation = null;
        try
        {
            operation = desktopLogin!(accountId, async (request, token) =>
            {
                await WireProtocol.WriteAsync(stream, new AgentReply(true, JsonSerializer.SerializeToElement(request, WireProtocol.Json), Stage: "desktop-login-ready"), token);
                DesktopLoginCompletion response;
                try { response = await completion.WaitAsync(token); }
                catch (Exception error) when (error is IOException or SocketException) { throw new OperationCanceledException("Desktop disconnected", error, token); }
                if (response.Success && (response.Cancelled || response.ErrorCode is not null)) throw new ToolkitException("IPC_INVALID_RESPONSE");
                if (response.Cancelled) throw new OperationCanceledException(token);
                if (!response.Success) throw new ToolkitException(WireProtocol.SafeDesktopLoginError(response.ErrorCode));
            }, lifetime.Token);
            if (await Task.WhenAny(operation, completion) == completion)
            {
                try { await completion; }
                catch { await lifetime.CancelAsync(); throw; }
            }
            var result = await operation;
            await WireProtocol.WriteAsync(stream, new AgentReply(true, JsonSerializer.SerializeToElement(result, WireProtocol.Json), Stage: "desktop-login-result"), lifetime.Token);
        }
        finally
        {
            await lifetime.CancelAsync();
            if (operation is not null) { try { await operation; } catch { } }
            try { await completion; } catch { }
        }
    }
}
