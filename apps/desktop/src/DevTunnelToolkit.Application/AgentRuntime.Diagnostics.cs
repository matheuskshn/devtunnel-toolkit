using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.Application;

public sealed partial class AgentRuntime
{
    private TcpListener? echoListener;
    private CancellationTokenSource? echoLifetime;
    private Task echoTask = Task.CompletedTask;
    private EchoStatus echoStatus = new(false);
    private EchoStatus CurrentEcho => Volatile.Read(ref echoStatus);

    private async Task<object> DispatchDiagnosticAsync(string operation, JsonElement payload, CancellationToken cancellationToken)
    {
        if (operation == "diagnostic.echo.status") return CurrentEcho;
        if (operation == "diagnostic.ping")
        {
            var profile = Profile(Id(payload));
            var port = payload.GetProperty("portNumber").GetInt32();
            var mapped = Snapshot().Connections.Where(c => c.ProfileId == profile.Id && c.State == ConnectionState.Connected)
                .SelectMany(c => c.Ports ?? []).FirstOrDefault(p => p.RemotePort == port) ?? throw new ToolkitException("REMOTE_PORT_UNAVAILABLE");
            if (!IPAddress.TryParse(mapped.LocalAddress, out var address) || !IPAddress.IsLoopback(address))
                throw new ToolkitException("LOOPBACK_REQUIRED");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var elapsed = Stopwatch.StartNew();
            try
            {
                using var socket = new TcpClient(address.AddressFamily);
                await socket.ConnectAsync(address, mapped.LocalPort, timeout.Token);
                var challenge = RandomNumberGenerator.GetBytes(32);
                await socket.GetStream().WriteAsync(challenge, timeout.Token);
                var reply = new byte[challenge.Length];
                await socket.GetStream().ReadExactlyAsync(reply, timeout.Token);
                var equal = CryptographicOperations.FixedTimeEquals(challenge, reply);
                return new EchoProbeResult(equal, elapsed.ElapsedMilliseconds, equal ? null : "ECHO_RESPONSE_MISMATCH");
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
            {
                return new EchoProbeResult(false, elapsed.ElapsedMilliseconds, error is OperationCanceledException ? "ECHO_TIMEOUT" : "ECHO_CONNECTION_FAILED");
            }
        }
        await mutation.WaitAsync(cancellationToken);
        try
        {
            if (operation == "diagnostic.echo.stop") { await StopEchoAsync(); return new EchoStatus(false); }
            if (operation != "diagnostic.echo.start") throw new ToolkitException("UNKNOWN_OPERATION");
            if (echoListener is not null) return CurrentEcho;
            var port = payload.TryGetProperty("port", out var selected) ? selected.GetInt32() : 0;
            if (port != 0 && port is < 1024 or > 65535) throw new ToolkitException("INVALID_PORT");
            var listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Start(16); }
            catch (SocketException) { listener.Stop(); throw new ToolkitException("ECHO_PORT_IN_USE"); }
            echoLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            echoListener = listener;
            Volatile.Write(ref echoStatus, new(true, ((IPEndPoint)listener.LocalEndpoint).Port));
            echoTask = RunEchoAsync(listener, echoLifetime.Token);
            return CurrentEcho;
        }
        finally { mutation.Release(); }
    }

    private async Task RunEchoAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(16);
        var clients = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                if (!slots.Wait(0)) { client.Dispose(); continue; }
                clients.RemoveAll(t => t.IsCompleted);
                clients.Add(ServeEchoAsync(client, slots, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            Log("Warning", "ECHO_LISTENER_FAILED", "The local diagnostic listener stopped unexpectedly.");
        }
        finally
        {
            Volatile.Write(ref echoStatus, new(false));
            listener.Stop(); await Task.WhenAll(clients);
        }
    }

    private static async Task ServeEchoAsync(TcpClient client, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                // Bounded diagnostic exchange, not an unrestricted application listener.
                var bytes = new byte[32];
                await client.GetStream().ReadExactlyAsync(bytes, timeout.Token);
                await client.GetStream().WriteAsync(bytes, timeout.Token);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
            finally { slots.Release(); }
        }
    }

    private async Task StopEchoAsync()
    {
        if (echoLifetime is null) return;
        Volatile.Write(ref echoStatus, new(false));
        await echoLifetime.CancelAsync();
        echoListener?.Stop();
        await echoTask;
        echoListener = null;
        echoLifetime.Dispose(); echoLifetime = null;
    }
}
