using System.Runtime.InteropServices;
using DevTunnelToolkit.Core;
using Xunit;

namespace DevTunnelToolkit.DevTunnels.Tests;

public sealed class CliProcessTests
{
    [WindowsFact]
    public async Task DesktopOwnedJobTerminatesOnlyItsChildOnDispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        var start = OfficialCli.CreateStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/d", "/c", "ping -n 30 127.0.0.1 >nul"], true);
        start.CreateNoWindow = false;
        var child = CliProcess.Start(start, killOnParentExit: true);
        using var observer = System.Diagnostics.Process.GetProcessById(child.Process.Id);
        _ = observer.SafeHandle;
        child.Dispose();
        await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(observer.HasExited);
    }
    [WindowsFact]
    public void AuthenticationWindowIsVisibleAndClosesWithoutCancellingOnDispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cancellations = 0;
        var window = WindowsAuthenticationWindow.Show(() => Interlocked.Increment(ref cancellations));
        var handle = window.Handle;
        try { Assert.NotEqual(0, handle); Assert.True(IsWindowVisible(handle)); }
        finally { window.Dispose(); }
        Assert.False(IsWindow(handle));
        Assert.Equal(0, cancellations);
        window.Dispose();
    }

    [WindowsFact]
    public async Task ClosingAuthenticationWindowCancelsItsOwnLogin()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var window = WindowsAuthenticationWindow.Show(() => cancelled.TrySetResult());
        Assert.True(PostMessage(window.Handle, 0x0010, 0, 0));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [WindowsFact]
    public async Task WindowsNativeLauncherKeepsARealButHiddenConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Fixed local fixture. Inspect the child's own console, never user windows.
        const string script = """
            $ErrorActionPreference = 'Stop'
            Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            public static class ConsoleFixture {
                [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
                [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
                [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
                [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
            }
            '@
            $window = [ConsoleFixture]::GetConsoleWindow()
            if ($window -eq [IntPtr]::Zero -or -not [ConsoleFixture]::IsWindow($window)) { exit 10 }
            for ($i = 0; $i -lt 10; $i++) {
                $root = [ConsoleFixture]::GetAncestor($window, 3)
                if ($root -eq [IntPtr]::Zero -or -not [ConsoleFixture]::IsWindow($root)) { exit 12 }
                if ([ConsoleFixture]::IsWindowVisible($window) -or [ConsoleFixture]::IsWindowVisible($root)) { exit 11 }
                Start-Sleep -Milliseconds 100
            }
            Write-Output 'REAL_HIDDEN_CONSOLE_OK'
            """;
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var start = OfficialCli.CreateStartInfo(executable, ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], true);
        start.CreateNoWindow = false;
        start.WorkingDirectory = Environment.SystemDirectory;
        using var child = CliProcess.Start(start);
        try
        {
            var output = child.Output.ReadToEndAsync(); var error = child.Error.ReadToEndAsync();
            await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, child.Process.ExitCode);
            Assert.Contains("REAL_HIDDEN_CONSOLE_OK", await output.WaitAsync(TimeSpan.FromSeconds(5)));
            await error.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { if (!child.Process.HasExited) child.Process.Kill(entireProcessTree: true); }
    }

    [WindowsFact]
    public async Task WindowsNativeLauncherCapturesBothStreamsAndExitCode()
    {
        if (!OperatingSystem.IsWindows()) return;
        // A local, fixed console fixture: no authentication, network or user state.
        var start = OfficialCli.CreateStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/d", "/c", "echo fixture-out&echo fixture-error 1>&2&exit /b 7"], true);
        start.CreateNoWindow = false;
        start.WorkingDirectory = Environment.SystemDirectory;
        using var child = CliProcess.Start(start);
        try
        {
            var output = child.Output.ReadToEndAsync(); var error = child.Error.ReadToEndAsync();
            await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains("fixture-out", await output.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("fixture-error", await error.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(7, child.Process.ExitCode);
            Assert.Equal("\"a b\\\\\"", WindowsInteractiveCli.QuoteArgument("a b\\"));
            Assert.Equal("\"a\\\"b\"", WindowsInteractiveCli.QuoteArgument("a\"b"));
        }
        finally { if (!child.Process.HasExited) child.Process.Kill(entireProcessTree: true); }
    }

    [Theory]
    [InlineData(true, "--use-browser-auth", false)]
    [InlineData(false, "--use-browser-auth", true)]
    [InlineData(true, "--use-device-code-auth", true)]
    [InlineData(true, "--use-integrated-windows-auth", true)]
    public void OnlyExplicitWindowsBrowserLoginGetsAConsole(bool windows, string method, bool noWindow)
    {
        var arguments = new[] { "user", "login", "--entra", method, "--json" };
        var start = OfficialCli.CreateStartInfo("fixture", arguments, windows);
        Assert.Equal(noWindow, start.CreateNoWindow);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput); Assert.True(start.RedirectStandardError);
        Assert.Equal(arguments, start.ArgumentList);
        Assert.True(OfficialCli.CreateStartInfo("fixture", ["user", "show", "--json"], windows).CreateNoWindow);
    }

    [Theory]
    [InlineData("Silent login with Windows account failed; try interactive login instead.", "AUTH_INTERACTION_REQUIRED")]
    [InlineData("A window handle must be configured. See https://example.invalid", "AUTH_WINDOW_UNAVAILABLE")]
    [InlineData("integrated_windows_auth_not_supported_managed_user", "WINDOWS_AUTH_ACCOUNT_UNSUPPORTED")]
    [InlineData("unknown_user: failed to get user name", "WINDOWS_AUTH_IDENTITY_UNAVAILABLE")]
    [InlineData("MsalUiRequiredException", "AUTH_INTERACTION_REQUIRED")]
    [InlineData("AADSTS50076: sample@example.invalid token=fictional-secret", "AUTH_PROVIDER_AADSTS50076")]
    [InlineData("0x800711c7", "EXECUTION_BLOCKED_BY_POLICY")]
    [InlineData("unrecognized private details", "WINDOWS_AUTH_FAILED")]
    public void DiagnosticsPreserveOnlySafeCategories(string output, string expected)
    {
        var error = OfficialCli.Classify(output, integrated: true);
        Assert.Equal(expected, error.Code); Assert.Equal(expected, error.Message);
        Assert.DoesNotContain("example.invalid", error.ToString());
        Assert.DoesNotContain("fictional-secret", error.ToString());
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Native Windows console and handle inheritance test.";
    }
}
