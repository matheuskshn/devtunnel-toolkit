using System.Diagnostics;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App;

public sealed class AgentLauncher(IToolkitGateway gateway, string? dataDirectory = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (await IsReadyAsync(cancellationToken)) return;
            cancellationToken.ThrowIfCancellationRequested();
            var start = CreateStartInfo(AppContext.BaseDirectory, dataDirectory);
            if (!File.Exists(start.FileName)) throw new ToolkitException("AGENT_EXECUTABLE_NOT_FOUND");
            Process? process;
            try { process = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception error)
            {
                throw new ToolkitException(error.NativeErrorCode is 1260 or 4551 ? "EXECUTION_BLOCKED_BY_POLICY" : "AGENT_START_FAILED");
            }
            using (process)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                try
                {
                    while (!await IsReadyAsync(timeout.Token))
                    {
                        // Another starter may have won the agent's singleton lock.
                        // Keep probing while that winning process becomes ready.
                        if (process is null) throw new ToolkitException("AGENT_START_FAILED");
                        await Task.Delay(250, timeout.Token);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new ToolkitException(process?.HasExited == true ? "AGENT_START_FAILED" : "AGENT_START_TIMEOUT"); }
            }
        }
        finally { gate.Release(); }
    }

    private async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try { await gateway.CallAsync<AgentSnapshot>("status", cancellationToken: cancellationToken); return true; }
        catch (ToolkitException error) when (error.Code == "AGENT_UNAVAILABLE") { cancellationToken.ThrowIfCancellationRequested(); return false; }
    }

    internal static ProcessStartInfo CreateStartInfo(string directory, string? dataDirectory)
    {
        var name = OperatingSystem.IsWindows() ? "DevTunnelToolkit.Agent.exe" : "DevTunnelToolkit.Agent";
        var candidate = Path.Combine(directory, "agent", name);
        if (!File.Exists(candidate)) candidate = Path.Combine(directory, name);
        var start = new ProcessStartInfo(candidate) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(candidate)! };
        if (dataDirectory is not null) { start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(Path.GetFullPath(dataDirectory)); }
        return start;
    }
}
