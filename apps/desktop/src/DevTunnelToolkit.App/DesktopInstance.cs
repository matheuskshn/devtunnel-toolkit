using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using DevTunnelToolkit.Platform;

namespace DevTunnelToolkit.App;

/// <summary>One UI per private data-directory scope. A second launch requests focus, never starts another agent.</summary>
public sealed class DesktopInstance : IDisposable
{
    private readonly LocalPaths paths;
    private readonly FileStream? heldLock;
    private readonly CancellationTokenSource stop = new();
    private Task? server;
    private string SocketPath => Path.Combine(paths.DataDirectory, "desktop.sock");
    private string PipeName => paths.PipeName + "-desktop";
    public bool IsPrimary => heldLock is not null;

    public DesktopInstance(string? directory)
    {
        paths = new(directory); paths.Initialize();
        var lockPath = Path.Combine(paths.DataDirectory, "desktop.lock");
        LocalPaths.EnsurePrivateFile(lockPath);
        try
        {
            heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) heldLock.Lock(0, 1);
        }
        catch (IOException) { heldLock?.Dispose(); heldLock = null; }
    }

    public async Task<bool> ActivateAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            if (OperatingSystem.IsWindows())
            {
                await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(timeout.Token); await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
            }
            else
            {
                LocalPaths.RejectLinks(SocketPath);
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), timeout.Token);
                await socket.SendAsync(new byte[] { 1 }, SocketFlags.None, timeout.Token);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { return false; }
    }

    public void Listen(Action activate) => server ??= RunAsync(activate);
    private async Task RunAsync(Action activate)
    {
        Socket? listener = null;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                LocalPaths.RejectLinks(SocketPath);
                if (File.Exists(SocketPath)) File.Delete(SocketPath);
                listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
                File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); listener.Listen(4);
            }
            while (!stop.IsCancellationRequested)
            {
                Stream stream;
                if (OperatingSystem.IsWindows())
                {
                    var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    try { await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false); stream = pipe; }
                    catch { pipe.Dispose(); throw; }
                }
                else stream = new NetworkStream(await listener!.AcceptAsync(stop.Token).ConfigureAwait(false), ownsSocket: true);
                await using (stream)
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(1));
                    try { var command = new byte[1]; if (await stream.ReadAsync(command, timeout.Token).ConfigureAwait(false) == 1 && command[0] == 1) activate(); }
                    catch (Exception e) when (e is IOException or OperationCanceledException) { }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { listener?.Dispose(); if (!OperatingSystem.IsWindows() && File.Exists(SocketPath)) File.Delete(SocketPath); }
    }

    public static bool HasTrayHost()
    {
        if (OperatingSystem.IsWindows()) return true;
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/gdbus")) return false;
        try
        {
            var start = new ProcessStartInfo("/usr/bin/gdbus") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "call", "--session", "--dest", "org.freedesktop.DBus", "--object-path", "/org/freedesktop/DBus", "--method", "org.freedesktop.DBus.NameHasOwner", "org.kde.StatusNotifierWatcher" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return false;
            if (!process.WaitForExit(1000)) { process.Kill(); return false; }
            return process.ExitCode == 0 && process.StandardOutput.ReadToEnd().Contains("true", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        stop.Cancel();
        try { server?.GetAwaiter().GetResult(); } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
        heldLock?.Dispose(); stop.Dispose();
    }
}
