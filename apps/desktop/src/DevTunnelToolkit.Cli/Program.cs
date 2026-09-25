using System.Text.Json;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Ipc;

namespace DevTunnelToolkit.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(WireProtocol.Json) { WriteIndented = true };
    public static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            var arguments = args.ToList();
            var dataDirectory = TakeOption(arguments, "--data-dir");
            var json = arguments.Remove("--json");
            var client = new ToolkitClient(dataDirectory);
            if (arguments.Count == 0 || arguments.Contains("--help")) { Help(); return 0; }
            if (arguments[0] == "agent")
            {
                RequireCount(arguments, 2);
                if (arguments[1] != "stop") throw new ToolkitException("INVALID_ARGUMENTS");
                await client.CallAsync<bool>("agent.shutdown", cancellationToken: stop.Token);
                Console.WriteLine("Agent shutdown requested. Profiles and credentials are preserved.");
                return 0;
            }
            if (arguments[0] == "proxy")
            {
                if (arguments.Count is 5 or 6 && arguments[1] == "probe" && int.TryParse(arguments[3], out var probePort) && arguments[4] is "http" or "https" && (arguments.Count == 5 || arguments[5] == "--upstream"))
                    Print(await client.CallAsync<ProxyProbeResult>("proxy.probe", new { host = arguments[2], port = probePort, https = arguments[4] == "https", upstreamOnly = arguments.Count == 6 }, stop.Token), true);
                else if (arguments.Count == 4 && arguments[1] == "test" && int.TryParse(arguments[3], out var port))
                    Print(await client.CallAsync<RouteDecision>("proxy.test", new { host = arguments[2], port }, stop.Token), true);
                else if (arguments.Count == 2 && arguments[1] is "start" or "stop")
                    Print(await client.CallAsync<bool>("proxy." + arguments[1], cancellationToken: stop.Token), true);
                else throw new ToolkitException("INVALID_ARGUMENTS");
                return 0;
            }
            if (arguments[0] == "watch")
            {
                RequireCount(arguments, 1);
                await foreach (var update in client.WatchAsync(stop.Token)) Print(update, true);
                return 0;
            }
            if (arguments[0] is "status" or "logs")
            {
                RequireCount(arguments, 1);
                if (arguments[0] == "logs") Print(await client.CallAsync<LogEntry[]>("logs", new { limit = 200 }, stop.Token), true);
                else Print(await client.CallAsync<AgentSnapshot>("status", cancellationToken: stop.Token), json);
                return 0;
            }
            if (arguments[0] == "settings")
            {
                RequireCount(arguments, 3);
                if (arguments[1] != "import") throw new ToolkitException("INVALID_ARGUMENTS");
                var settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(arguments[2], stop.Token), WireProtocol.Json) ?? throw new ToolkitException("INVALID_SETTINGS");
                Print(await client.CallAsync<AppSettings>("settings.save", settings, stop.Token), true);
                return 0;
            }
            if (arguments[0] == "account")
            {
                if (arguments.Count < 2) throw new ToolkitException("INVALID_ARGUMENTS");
                if (arguments[1] == "add")
                {
                    var cliLoginMode = TakeOption(arguments, "--login-mode");
                    var clientId = TakeOption(arguments, "--client-id");
                    var tenant = TakeOption(arguments, "--tenant");
                    var scope = TakeOption(arguments, "--scope");
                    RequireCount(arguments, 4);
                    var provider = arguments[2].ToLowerInvariant() switch
                    {
                        "ms" or "entra" or "aad" or "windows" or "integrated-windows" => AccountProvider.MicrosoftCli,
                        "gh" => AccountProvider.GitHubCli,
                        _ when Enum.TryParse<AccountProvider>(arguments[2], true, out var parsed) && Enum.IsDefined(parsed) => parsed,
                        _ => throw new ToolkitException("INVALID_PROVIDER")
                    };
                    var mode = cliLoginMode?.ToLowerInvariant() switch
                    {
                        "device" or "device-code" => CliLoginMode.DeviceCode,
                        "browser" => CliLoginMode.Browser,
                        "windows" or "integrated-windows" => CliLoginMode.WindowsIntegrated,
                        null => arguments[2].ToLowerInvariant() is "windows" or "integrated-windows" ? CliLoginMode.WindowsIntegrated : CliLoginMode.DeviceCode,
                        _ => throw new ToolkitException("INVALID_CLI_LOGIN_MODE")
                    };
                    var profile = new AccountProfile { Provider = provider, CliLoginMode = mode, DisplayName = arguments[3], ClientId = clientId, TenantId = tenant, Scope = scope };
                    Validation.Account(profile);
                    Print(await client.CallAsync<AccountProfile>("account.save", profile, stop.Token), true);
                    return 0;
                }
                if (arguments[1] == "list") { RequireCount(arguments, 2); Print((await client.CallAsync<AgentSnapshot>("status", cancellationToken: stop.Token)).Accounts, true); return 0; }
                var stdin = arguments.Remove("--token-stdin");
                RequireCount(arguments, 3);
                var action = arguments[1];
                var id = arguments[2];
                if (action == "login")
                {
                    await client.CallAsync<LoginStatus>("account.login", new { id }, stop.Token);
                    string? displayed = null;
                    try
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            var state = (await client.CallAsync<AgentSnapshot>("status", cancellationToken: stop.Token)).Logins.FirstOrDefault(l => l.AccountId == id);
                            if (state?.Challenge is { } challenge && displayed != challenge.UserCode)
                            {
                                displayed = challenge.UserCode;
                                Console.WriteLine($"Open {challenge.VerificationUri} and enter {challenge.UserCode}. Expires {(challenge.ExpiresAt is { } expires ? expires.ToString("u") : "not reported by provider")}");
                            }
                            if (state?.State == "Completed") { Console.WriteLine("Authenticated."); return 0; }
                            if (state?.State is "Failed" or "Cancelled") throw new ToolkitException(state.ErrorCode ?? "LOGIN_CANCELLED");
                            await Task.Delay(1000, stop.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        await client.CallAsync<bool>("account.cancel", new { id });
                        throw;
                    }
                    return 1;
                }
                if (action == "import-token")
                {
                    if (!stdin || !Console.IsInputRedirected) throw new ToolkitException("TOKEN_STDIN_REQUIRED");
                    var tokenBuffer = new char[32770];
                    var length = 0;
                    while (length < tokenBuffer.Length)
                    {
                        var count = await Console.In.ReadAsync(tokenBuffer.AsMemory(length), stop.Token);
                        if (count == 0) break;
                        length += count;
                    }
                    if (length == tokenBuffer.Length) throw new ToolkitException("INVALID_ACCESS_TOKEN");
                    var token = new string(tokenBuffer, 0, length).Trim();
                    Array.Clear(tokenBuffer);
                    await client.CallAsync<bool>("account.import-token", new { id, token }, stop.Token);
                    Console.WriteLine("Credential stored.");
                    return 0;
                }
                if (action is not ("validate" or "remove" or "discover" or "cancel")) throw new ToolkitException("INVALID_ARGUMENTS");
                Print(await client.CallAsync<JsonElement>("account." + action, new { id }, stop.Token), true);
                return 0;
            }
            if (arguments[0] == "tunnel")
            {
                if (arguments.Count < 2) throw new ToolkitException("INVALID_ARGUMENTS");
                if (arguments[1] == "list") { RequireCount(arguments, 2); Print((await client.CallAsync<AgentSnapshot>("status", cancellationToken: stop.Token)).Tunnels, true); return 0; }
                if (arguments[1] == "add")
                {
                    var local = TakeOption(arguments, "--local-port");
                    var remote = TakeOption(arguments, "--remote-port");
                    var alias = TakeOption(arguments, "--alias");
                    var autoConnect = arguments.Remove("--auto-connect");
                    RequireCount(arguments, 4);
                    var canonical = arguments[3].Split('.');
                    if (canonical.Length != 2) throw new ToolkitException("CANONICAL_TUNNEL_ID_REQUIRED");
                    var mappings = Array.Empty<PortMapping>();
                    if (local is not null || remote is not null)
                    {
                        if (!int.TryParse(local, out var localPort) || !int.TryParse(remote, out var remotePort)) throw new ToolkitException("INVALID_PORT");
                        mappings = [new PortMapping { RemotePort = remotePort, LocalPort = localPort, Mode = MappingMode.Manual }];
                    }
                    var profile = new TunnelProfile { AccountId = arguments[2], TunnelId = canonical[0], ClusterId = canonical[1], Alias = alias ?? "", AutoConnect = autoConnect, AutoDiscoverPorts = mappings.Length == 0, Mappings = mappings };
                    Print(await client.CallAsync<TunnelProfile>("tunnel.save", profile, stop.Token), true);
                    return 0;
                }
                if (arguments[1] == "import")
                {
                    RequireCount(arguments, 3);
                    var profile = JsonSerializer.Deserialize<TunnelProfile>(await File.ReadAllTextAsync(arguments[2], stop.Token), WireProtocol.Json) ?? throw new ToolkitException("INVALID_PROFILE");
                    Print(await client.CallAsync<TunnelProfile>("tunnel.save", profile, stop.Token), true);
                    return 0;
                }
                RequireCount(arguments, 3);
                if (arguments[1] is not ("connect" or "stop" or "reconnect" or "remove" or "refresh")) throw new ToolkitException("INVALID_ARGUMENTS");
                Print(await client.CallAsync<JsonElement>("tunnel." + arguments[1], new { id = arguments[2] }, stop.Token), true);
                return 0;
            }
            throw new ToolkitException("INVALID_ARGUMENTS");
        }
        catch (ToolkitException error) { Console.Error.WriteLine(error.Code); return 1; }
        catch (OperationCanceledException) { Console.Error.WriteLine("CANCELLED"); return 130; }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException) { Console.Error.WriteLine("INVALID_INPUT"); return 2; }
    }

    private static string? TakeOption(List<string> args, string name)
    {
        var position = args.IndexOf(name);
        if (position < 0) return null;
        if (position + 1 >= args.Count) throw new ToolkitException("MISSING_OPTION_VALUE");
        var value = args[position + 1];
        args.RemoveRange(position, 2);
        return value;
    }
    private static void RequireCount(List<string> args, int expected) { if (args.Count != expected) throw new ToolkitException("INVALID_ARGUMENTS"); }
    private static void Print<T>(T value, bool json)
    {
        if (!json && value is AgentSnapshot state)
        {
            Console.WriteLine($"DevTunnel Toolkit | {state.Capabilities.OperatingSystem} | {state.Accounts.Length} accounts | {state.Tunnels.Length} tunnels");
            foreach (var profile in state.Tunnels)
            {
                var connection = state.Connections.FirstOrDefault(c => c.ProfileId == profile.Id);
                Console.WriteLine($"{profile.CanonicalId,-50} {connection?.State ?? ConnectionState.Stopped} {connection?.ErrorCode}");
                foreach (var port in connection?.Ports ?? []) Console.WriteLine($"  {port.LocalAddress}:{port.LocalPort} -> remote {port.RemotePort}");
            }
        }
        else Console.WriteLine(JsonSerializer.Serialize(value, Json));
    }
    private static void Help() => Console.WriteLine("""
        DevTunnel Toolkit
        --data-dir PATH  Use the same data directory as the running agent.
        status [--json] | watch | logs
        agent stop
        proxy start|stop
        proxy test HOST PORT
        proxy probe HOST PORT http|https [--upstream]
        account add MicrosoftCli|GitHubCli|GitHub|Microsoft|TunnelToken NAME [--client-id ID] [--tenant ID] [--scope SCOPE]
        Aliases: ms, entra and aad = MicrosoftCli; gh = GitHubCli.
        account list
        CLI login options: --login-mode browser|device-code|windows
        Provider aliases: entra|ms|aad, gh, windows|integrated-windows
        account login|cancel|validate|discover|remove ACCOUNT_ID
        account import-token ACCOUNT_ID --token-stdin
        tunnel add ACCOUNT_ID TUNNEL.CLUSTER [--remote-port PORT --local-port PORT] [--alias NAME] [--auto-connect]
        tunnel list
        tunnel connect|stop|reconnect|refresh|remove TUNNEL_OR_PROFILE_ID
        tunnel import FILE.json
        settings import FILE.json
        Removing a local profile never deletes the remote tunnel.
        """);
}
