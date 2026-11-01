using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DevTunnelToolkit.Core;
using Microsoft.Win32;

namespace DevTunnelToolkit.Platform;

/// <summary>Opt-in startup for one per-user agent and data directory. Never starts or stops an existing agent.</summary>
public sealed class UserStartupManager : IUserStartupManager
{
    private const string OwnershipHeader = "# Managed by DevTunnel Toolkit Desktop";
    private readonly IStartupBackend backend;
    private readonly string executablePath;
    private readonly string dataDirectory;
    private readonly string registrationName;
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool Supported { get; }

    public UserStartupManager(string? executablePath, string dataDirectory)
        : this(executablePath, dataDirectory, CreateBackend()) { }

    internal UserStartupManager(string? executablePath, string dataDirectory, IStartupBackend backend)
    {
        this.backend = backend;
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        this.executablePath = executablePath is null ? "" : Path.GetFullPath(executablePath);
        var filename = Path.GetFileName(this.executablePath);
        var expected = backend.Kind == StartupKind.Windows ? "DevTunnelToolkit.Agent.exe" : "DevTunnelToolkit.Agent";
        var scopePath = backend.Kind == StartupKind.Windows ? this.dataDirectory.ToUpperInvariant() : this.dataDirectory;
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopePath)))[..16].ToLowerInvariant();
        registrationName = "devtunnel-toolkit-agent-" + scope;
        Supported = backend.Available && filename == expected && File.Exists(this.executablePath) &&
            !this.executablePath.Any(char.IsControl) && !this.dataDirectory.Any(char.IsControl);
    }

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        if (!Supported) return false;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await backend.ReadAsync(registrationName, cancellationToken).ConfigureAwait(false);
            return entry is { Owned: true } && await backend.IsEnabledAsync(registrationName, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!Supported) throw new ToolkitException("USER_STARTUP_UNAVAILABLE");
        LocalPaths.RejectLinks(executablePath);
        LocalPaths.RejectLinks(dataDirectory);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await backend.ReadAsync(registrationName, cancellationToken).ConfigureAwait(false);
            if (previous is { Owned: false }) throw new ToolkitException("STARTUP_ENTRY_CONFLICT");
            if (!enabled && previous is null) return;
            var wasEnabled = previous is not null && await backend.IsEnabledAsync(registrationName, cancellationToken).ConfigureAwait(false);
            var desired = BuildContent();
            if (enabled && wasEnabled && previous?.Content == desired) return;
            try
            {
                if (enabled)
                {
                    await backend.WriteAsync(registrationName, desired, cancellationToken).ConfigureAwait(false);
                    await backend.SetEnabledAsync(registrationName, true, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await backend.SetEnabledAsync(registrationName, false, cancellationToken).ConfigureAwait(false);
                    await backend.DeleteAsync(registrationName, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try
                {
                    if (previous is not null)
                    {
                        await backend.WriteAsync(registrationName, previous.Content, rollback.Token).ConfigureAwait(false);
                        await backend.SetEnabledAsync(registrationName, wasEnabled, rollback.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        try { await backend.SetEnabledAsync(registrationName, false, rollback.Token).ConfigureAwait(false); }
                        finally { await backend.DeleteAsync(registrationName, rollback.Token).ConfigureAwait(false); }
                    }
                }
                catch { throw new ToolkitException("STARTUP_ROLLBACK_FAILED"); }
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private string BuildContent()
    {
        if (backend.Kind == StartupKind.Windows)
        {
            var command = QuoteWindows(executablePath) + " --data-dir " + QuoteWindows(dataDirectory);
            if (command.Length > 260) throw new ToolkitException("STARTUP_COMMAND_TOO_LONG");
            return command;
        }
        return $"{OwnershipHeader}\n# Scope: {registrationName}\n[Unit]\nDescription=DevTunnel Toolkit user agent\n\n" +
            $"[Service]\nType=simple\nExecStart={QuoteSystemd(executablePath)} --data-dir {QuoteSystemd(dataDirectory)}\n" +
            "Restart=on-failure\nRestartSec=5\nNoNewPrivileges=yes\n\n[Install]\nWantedBy=default.target\n";
    }

    internal static string QuoteWindows(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); }
            else { result.Append('\\', slashes); result.Append(character); }
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    internal static string QuoteSystemd(string argument) => "\"" + argument
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("%", "%%", StringComparison.Ordinal)
        .Replace("$", "$$", StringComparison.Ordinal) + "\"";

    private static IStartupBackend CreateBackend()
    {
        if (OperatingSystem.IsWindows()) return new WindowsStartupBackend();
        return new LinuxStartupBackend();
    }

    internal enum StartupKind { Windows, Linux }
    internal sealed record StartupEntry(string Content, bool Owned);
    internal interface IStartupBackend
    {
        StartupKind Kind { get; }
        bool Available { get; }
        Task<StartupEntry?> ReadAsync(string name, CancellationToken cancellationToken);
        Task WriteAsync(string name, string content, CancellationToken cancellationToken);
        Task DeleteAsync(string name, CancellationToken cancellationToken);
        Task SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken);
        Task<bool> IsEnabledAsync(string name, CancellationToken cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsStartupBackend : IStartupBackend
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string OwnershipKey = @"Software\DevTunnelToolkit\Startup";
        public StartupKind Kind => StartupKind.Windows;
        public bool Available => true;
        public Task<StartupEntry?> ReadAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            using var ownership = Registry.CurrentUser.OpenSubKey(OwnershipKey);
            var value = run?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return Task.FromResult(value is null ? null : new StartupEntry(value.ToString() ?? "", value is string content && ownership?.GetValue(name) as string == content));
        }
        public Task WriteAsync(string name, string content, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var ownership = Registry.CurrentUser.CreateSubKey(OwnershipKey, true);
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, true);
            ownership.SetValue(name, content, RegistryValueKind.String);
            run.SetValue(name, content, RegistryValueKind.String);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, true);
            using var ownership = Registry.CurrentUser.OpenSubKey(OwnershipKey, true);
            run?.DeleteValue(name, false);
            ownership?.DeleteValue(name, false);
            return Task.CompletedTask;
        }
        public Task SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task<bool> IsEnabledAsync(string name, CancellationToken cancellationToken) => await ReadAsync(name, cancellationToken).ConfigureAwait(false) is not null;
    }

    private sealed class LinuxStartupBackend : IStartupBackend
    {
        private readonly string? systemctl;
        private readonly string unitDirectory;
        public StartupKind Kind => StartupKind.Linux;
        public bool Available { get; }
        public LinuxStartupBackend()
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(config) || !Path.IsPathFullyQualified(config))
                config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            unitDirectory = Path.Combine(config, "systemd", "user");
            systemctl = new[] { "/usr/bin/systemctl", "/bin/systemctl" }.FirstOrDefault(File.Exists);
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            Available = OperatingSystem.IsLinux() && systemctl is not null && !string.IsNullOrWhiteSpace(runtime) &&
                File.Exists(Path.Combine(runtime, "systemd", "private"));
        }
        private string UnitPath(string name)
        {
            var path = Path.Combine(unitDirectory, name + ".service");
            LocalPaths.RejectLinks(path);
            return path;
        }
        public async Task<StartupEntry?> ReadAsync(string name, CancellationToken cancellationToken)
        {
            var path = UnitPath(name);
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 16384) return new StartupEntry("", false);
            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return new StartupEntry(content, content.StartsWith($"{OwnershipHeader}\n# Scope: {name}\n", StringComparison.Ordinal));
        }
        public async Task WriteAsync(string name, string content, CancellationToken cancellationToken)
        {
            var path = UnitPath(name);
            Directory.CreateDirectory(unitDirectory);
            await LocalPaths.WritePrivateAsync(path, Encoding.UTF8.GetBytes(content), cancellationToken).ConfigureAwait(false);
        }
        public async Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(UnitPath(name));
            await RunAsync(["daemon-reload"], cancellationToken).ConfigureAwait(false);
        }
        public async Task SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken)
        {
            await RunAsync(["daemon-reload"], cancellationToken).ConfigureAwait(false);
            await RunAsync([enabled ? "enable" : "disable", "--", name + ".service"], cancellationToken).ConfigureAwait(false);
        }
        public async Task<bool> IsEnabledAsync(string name, CancellationToken cancellationToken) =>
            await RunAsync(["is-enabled", "--quiet", "--", name + ".service"], cancellationToken, true).ConfigureAwait(false) == 0;

        private async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken, bool allowDisabled = false)
        {
            if (systemctl is null) throw new ToolkitException("USER_STARTUP_UNAVAILABLE");
            var info = new ProcessStartInfo(systemctl) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--user");
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new ToolkitException("USER_STARTUP_FAILED");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
                if (process.ExitCode != 0 && (!allowDisabled || process.ExitCode is not (1 or 3 or 4)))
                    throw new ToolkitException("USER_STARTUP_FAILED");
                return process.ExitCode;
            }
            finally { if (!process.HasExited) process.Kill(true); }
        }
    }
}
