using System.Runtime.InteropServices;
using DevTunnelToolkit.Application;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.DevTunnels;
using DevTunnelToolkit.Ipc;
using DevTunnelToolkit.Persistence;
using DevTunnelToolkit.Platform;

namespace DevTunnelToolkit.Agent;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help")) { Console.WriteLine("DevTunnel Toolkit Agent\nUsage: DevTunnelToolkit.Agent [--data-dir ABSOLUTE_PATH]\nStartup: DevTunnelToolkit.Agent --configure-startup enable|disable [--data-dir ABSOLUTE_PATH]\nCredentials: OS vault; optional TOOLKIT_CREDENTIAL_KEY for headless AES-256-GCM."); return 0; }
        if (args.Contains("--version")) { Console.WriteLine(typeof(Program).Assembly.GetName().Version); return 0; }
        try
        {
            bool? configureStartup = null;
            if (args.Length >= 2 && args[0] == "--configure-startup")
            {
                configureStartup = args[1] switch { "enable" => true, "disable" => false, _ => throw new ToolkitException("INVALID_ARGUMENTS") };
                args = args[2..];
            }
            if (args.Length != 0 && (args.Length != 2 || args[0] != "--data-dir" || !Path.IsPathFullyQualified(args[1]))) throw new ToolkitException("INVALID_ARGUMENTS");
            var paths = new LocalPaths(args.Length == 2 ? args[1] : null);
            var startup = new UserStartupManager(Environment.ProcessPath, paths.DataDirectory);
            if (configureStartup is { } enable)
            {
                await startup.SetEnabledAsync(enable);
                Console.WriteLine(enable ? "User startup enabled. The agent is not started by this command." : "User startup disabled. Data and running connections are preserved.");
                return 0;
            }
            paths.Initialize();
            var lockPath = Path.Combine(paths.DataDirectory, "agent.lock");
            LocalPaths.EnsurePrivateFile(lockPath);
            FileStream processLock;
            try
            {
                processLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) processLock.Lock(0, 1);
                else throw new ToolkitException("UNSUPPORTED_PLATFORM");
            }
            catch (IOException) { throw new ToolkitException("AGENT_ALREADY_RUNNING"); }
            await using var heldLock = processLock;
            using var vault = new CredentialVault(paths);
            await using var store = new SqliteStateStore(paths);
            var cli = new CliAuthenticationService();
            using var authentication = new HybridAuthenticationService(new NativeAuthenticationService(vault), cli);
            ITunnelService service = new CliTunnelService(new DevTunnelService(authentication), cli);
            var platform = new PlatformCapabilities(OperatingSystem.IsWindows() ? "Windows" : "Linux", vault.Available, startup.Supported, false, false, "TCP loopback and opt-in local PAC are available. System proxy, LAN/WSL exposure and firewall changes are not implemented.");
            AgentRuntime? activeRuntime = null;
            var publishedProxies = new PublishedProxyFactory((code, id) => activeRuntime?.RecordHostedProxyEvent(code, id));
            var managedServices = new ManagedServiceRegistry(publishedProxies);
            await using var runtime = new AgentRuntime(store, vault, authentication, service, platform, startup,
                new RemoteTunnelManagement(authentication, cli), new TunnelHostingService(authentication, cli, managedServices), managedServices);
            activeRuntime = runtime;
            using var lifetime = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
            using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; lifetime.Cancel(); });
            await runtime.InitializeAsync(lifetime.Token);
            Console.WriteLine("DevTunnel Toolkit Agent ready. IPC is restricted to the current OS user.");
            await new ToolkitServer(paths, runtime.DispatchAsync, runtime.Snapshot, lifetime.Cancel, runtime.LoginFromDesktopAsync).RunAsync(lifetime.Token);
            return 0;
        }
        catch (ToolkitException error) { Console.Error.WriteLine(error.Code); return 1; }
        catch (OperationCanceledException) { return 0; }
        catch { Console.Error.WriteLine("AGENT_START_FAILED"); return 1; }
    }
}
