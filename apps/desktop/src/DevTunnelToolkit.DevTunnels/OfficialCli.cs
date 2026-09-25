using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.DevTunnels;

internal interface IOfficialCli
{
    Task<string> RunAsync(string[] arguments, Action<string>? onOutput, CancellationToken cancellationToken);
}

/// <summary>Runs the official CLI without a shell. Raw output never crosses IPC or enters logs.</summary>
internal sealed class OfficialCli : IOfficialCli
{
    private readonly string executable;
    private readonly bool desktopOwnsWindow;

    public OfficialCli(string? executable = null, bool desktopOwnsWindow = false)
    {
        this.desktopOwnsWindow = desktopOwnsWindow;
        var configured = executable ?? Environment.GetEnvironmentVariable("DEVTUNNEL_TOOLKIT_CLI_PATH");
        if (configured is not null && (!Path.IsPathFullyQualified(configured) || !File.Exists(configured)))
            throw new ToolkitException("DEVTUNNEL_CLI_PATH_INVALID");
        this.executable = configured ?? (OperatingSystem.IsWindows() ? "devtunnel.exe" : "devtunnel");
    }

    public async Task<string> RunAsync(string[] arguments, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        var start = CreateStartInfo(executable, arguments, OperatingSystem.IsWindows());
        // The CLI uses the OS keychain. It has no reason to inherit unrelated API tokens.
        foreach (var key in new[] { "GH_TOKEN", "GITHUB_TOKEN", "VALE_DEVOPS_TOKEN", "GITLAB_DOTCOM_TOKEN" }) start.Environment.Remove(key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(arguments.Contains("login") ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(60));
        var cancelledInWindow = 0;
        try
        {
            using var authenticationWindow = desktopOwnsWindow ? null : OpenAuthenticationWindow(start, arguments, () =>
            {
                Interlocked.Exchange(ref cancelledInWindow, 1);
                timeout.Cancel();
            });
            timeout.Token.ThrowIfCancellationRequested();
            CliProcess child;
            try { child = CliProcess.Start(start, killOnParentExit: desktopOwnsWindow); }
            catch (System.ComponentModel.Win32Exception error)
            {
                throw new ToolkitException(error.NativeErrorCode is 1260 or 4551 ? "EXECUTION_BLOCKED_BY_POLICY" : "DEVTUNNEL_CLI_UNAVAILABLE");
            }
            using var ownedChild = child;
            var process = child.Process;
            var stdout = ReadBoundedAsync(child.Output, 1024 * 1024, onOutput, timeout.Token);
            var stderr = ReadBoundedAsync(child.Error, 64 * 1024, null, timeout.Token);
            try
            {
                var exited = process.WaitForExitAsync(timeout.Token);
                var first = await Task.WhenAny(exited, stdout, stderr);
                await first; // Fail promptly if a stream exceeds its bound or a callback fails.
                await Task.WhenAll(exited, stdout, stderr);
                var output = await stdout;
                var errors = await stderr;
                if (process.ExitCode != 0) throw Classify(output + errors, arguments.Contains("--use-integrated-windows-auth"));
                return output;
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && Volatile.Read(ref cancelledInWindow) == 0) { throw new ToolkitException("DEVTUNNEL_CLI_TIMEOUT"); }
    }

    private static IDisposable? OpenAuthenticationWindow(ProcessStartInfo start, string[] arguments, Action cancel)
    {
        if (!OperatingSystem.IsWindows() || start.CreateNoWindow || !arguments.Contains("--entra")) return null;
        try { return WindowsAuthenticationWindow.Show(cancel); }
        catch (System.ComponentModel.Win32Exception) { throw new ToolkitException("AUTH_WINDOW_UNAVAILABLE"); }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string[] arguments, bool isWindows)
    {
        // Windows CLI browser login uses WAM. A hidden real console lets the CLI
        // find the visible authentication window in its parent agent process.
        // Preserve redirected streams and argument isolation; allow the native login
        // window only for an explicit interactive login, never for background work.
        var interactive = isWindows && arguments.Length >= 2 && arguments[0] == "user" && arguments[1] == "login" && arguments.Contains("--use-browser-auth");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = !interactive,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken);
            if (count == 0) return result.ToString();
            if (result.Length + count > limit) throw new ToolkitException("DEVTUNNEL_CLI_OUTPUT_LIMIT");
            var chunk = new string(buffer, 0, count);
            result.Append(chunk);
            onOutput?.Invoke(chunk);
        }
    }

    internal static ToolkitException Classify(string output, bool integrated = false)
    {
        // Export only allowlisted categories and a numeric Entra code. Never put
        // CLI output, usernames, tenant IDs, URLs or tokens in exceptions/logs/IPC.
        var entra = Regex.Match(output, @"\bAADSTS(\d{4,12})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (entra.Success) return new("AUTH_PROVIDER_AADSTS" + entra.Groups[1].Value);
        if (output.Contains("window handle must be configured", StringComparison.OrdinalIgnoreCase)) return new("AUTH_WINDOW_UNAVAILABLE");
        if (output.Contains("integrated_windows_auth_not_supported_managed_user", StringComparison.OrdinalIgnoreCase)) return new("WINDOWS_AUTH_ACCOUNT_UNSUPPORTED");
        if (Regex.IsMatch(output, "unknown_user|failed to get user name|could not identify logged in user", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return new("WINDOWS_AUTH_IDENTITY_UNAVAILABLE");
        if (Regex.IsMatch(output, "Silent login with Windows account failed|interaction_required|MsalUiRequiredException", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return new("AUTH_INTERACTION_REQUIRED");
        if (Regex.IsMatch(output, "application control|blocked by.*policy|0x800711c7", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return new("EXECUTION_BLOCKED_BY_POLICY");
        if (Regex.IsMatch(output, "(?:login token|token|login).*expired", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return new("AUTHENTICATION_EXPIRED");
        if (Regex.IsMatch(output, "not logged in|unauthorized|sign.in required|AADSTS", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return new("AUTHENTICATION_REQUIRED");
        if (Regex.IsMatch(output, "tunnel not found", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return new("TUNNEL_NOT_FOUND");
        return new(integrated ? "WINDOWS_AUTH_FAILED" : "DEVTUNNEL_CLI_FAILED");
    }
}

internal static class OfficialCliJson
{
    public static JsonDocument Parse(string output)
    {
        // First-run license text precedes JSON. Only a JSON document on a new line is accepted.
        var lines = output.Split('\n');
        var offset = 0;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                try { return JsonDocument.Parse(output[offset..]); }
                catch (JsonException) { throw new ToolkitException("DEVTUNNEL_CLI_JSON_INVALID"); }
            }
            offset += line.Length + 1;
        }
        throw new ToolkitException("DEVTUNNEL_CLI_JSON_REQUIRED");
    }

    public static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
