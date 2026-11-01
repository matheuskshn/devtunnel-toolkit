using System.Text.Json;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Ipc;

namespace DevTunnelToolkit.Cli;

internal static class RemoteCommands
{
    public static async Task<bool> TryExecuteAsync(ToolkitClient client, List<string> arguments, CancellationToken token)
    {
        if (arguments[0] is not ("remote" or "host" or "service" or "diagnostic")) return false;
        var args = arguments.ToList();
        var confirmation = Option(args, "--confirm");
        var portText = Option(args, "--port");
        int? port = portText is null ? null : int.TryParse(portText, out var parsedPort) ? parsedPort : throw new ToolkitException("INVALID_PORT");
        var reveal = args.Remove("--show-token");
        if (args.Count < 2 ||
            reveal && (args[0] != "remote" || args[1] != "token") ||
            port is not null && (args[0] != "remote" || args[1] is not ("access-add" or "access-delete" or "access-reset" or "token")) ||
            confirmation is not null && !((args[0] == "host" && args[1] == "prepare") ||
                (args[0] == "remote" && args[1] is "delete" or "port-delete" or "access-delete" or "access-reset")))
            throw new ToolkitException("INVALID_ARGUMENTS");
        object result;
        if (args[0] == "service")
        {
            if (args.Count == 2 && args[1] == "list")
            {
                var snapshot = await client.CallAsync<AgentSnapshot>("status", cancellationToken: token);
                result = new { snapshot.Services, snapshot.ServiceStatuses };
            }
            else if (args.Count == 3 && args[1] == "import")
                result = await client.CallAsync<ServiceProfile>("service.save", await ReadAsync<ServiceProfile>(args[2], token), token);
            else
            {
                Count(args, 3);
                if (args[1] is not ("start" or "stop" or "remove")) throw new ToolkitException("INVALID_ARGUMENTS");
                result = await client.CallAsync<JsonElement>("service." + args[1], new { id = args[2] }, token);
            }
        }
        else if (args[0] == "host")
        {
            if (args.Count == 2 && args[1] == "list") result = (await client.CallAsync<AgentSnapshot>("status", cancellationToken: token)).Hosts;
            else if (args.Count == 3 && args[1] == "import") result = await client.CallAsync<HostProfile>("host.save", await ReadAsync<HostProfile>(args[2], token), token);
            else
            {
                Count(args, 3);
                if (args[1] is not ("start" or "stop" or "restart" or "remove" or "prepare")) throw new ToolkitException("INVALID_ARGUMENTS");
                result = await client.CallAsync<JsonElement>("host." + args[1], new { id = args[2], confirmation }, token);
            }
        }
        else if (args[0] == "diagnostic")
        {
            if (args.Count is 2 or 3 && args[1] == "echo-start")
                result = await client.CallAsync<EchoStatus>("diagnostic.echo.start", new { port = args.Count == 3 && int.TryParse(args[2], out var selected) ? selected : args.Count == 2 ? 0 : throw new ToolkitException("INVALID_PORT") }, token);
            else if (args.Count == 2 && args[1] is "echo-stop" or "echo-status")
                result = await client.CallAsync<EchoStatus>("diagnostic.echo." + (args[1] == "echo-stop" ? "stop" : "status"), cancellationToken: token);
            else
            {
                Count(args, 4);
                if (args[1] != "ping" || !int.TryParse(args[3], out var remotePort)) throw new ToolkitException("INVALID_ARGUMENTS");
                result = await client.CallAsync<EchoProbeResult>("diagnostic.ping", new { id = args[2], portNumber = remotePort }, token);
            }
        }
        else
        {
            if (args.Count < 3) throw new ToolkitException("INVALID_ARGUMENTS");
            var accountId = args[2];
            if (args[1] is "list" or "clusters")
            {
                Count(args, 3);
                result = await client.CallAsync<JsonElement>("remote." + args[1], new { accountId }, token);
            }
            else if (args[1] == "create")
            {
                Count(args, 4);
                result = await client.CallAsync<ManagedTunnel>("remote.create", new { accountId, input = await ReadAsync<RemoteTunnelSpec>(args[3], token) }, token);
            }
            else if (args[1] == "delete-batch")
            {
                Count(args, 4);
                var selection = await ReadAsync<Selection>(args[3], token);
                result = await client.CallAsync<RemoteBatchResult[]>("remote.delete-batch", new { accountId, selection.Targets, selection.Confirmations }, token);
            }
            else
            {
                if (args.Count < 4) throw new ToolkitException("INVALID_ARGUMENTS");
                var parts = args[3].Split('.');
                if (parts.Length != 2) throw new ToolkitException("CANONICAL_TUNNEL_ID_REQUIRED");
                var target = new RemoteTunnelTarget(parts[0], parts[1]);
                switch (args[1])
                {
                    case "show":
                    case "diagnostics":
                        Count(args, 4);
                        result = await client.CallAsync<JsonElement>(args[1] == "show" ? "remote.get" : "remote.diagnostics", new { accountId, target }, token);
                        break;
                    case "delete":
                        Count(args, 4);
                        result = await client.CallAsync<bool>("remote.delete", new { accountId, target, confirmation }, token);
                        break;
                    case "update":
                        Count(args, 5);
                        result = await client.CallAsync<ManagedTunnel>("remote.update", new { accountId, target, input = await ReadAsync<RemoteTunnelSpec>(args[4], token) }, token);
                        break;
                    case "port-create":
                    case "port-update":
                        Count(args, 5);
                        result = await client.CallAsync<ManagedPort>("remote.port.save", new { accountId, target, input = await ReadAsync<RemotePortSpec>(args[4], token), create = args[1] == "port-create" }, token);
                        break;
                    case "port-delete":
                        Count(args, 5);
                        if (!int.TryParse(args[4], out var portNumber)) throw new ToolkitException("INVALID_PORT");
                        result = await client.CallAsync<bool>("remote.port.delete", new { accountId, target, portNumber, confirmation }, token);
                        break;
                    case "access-add":
                        Count(args, 5);
                        result = await client.CallAsync<RemoteAccessEntry[]>("remote.access.save", new { accountId, target, portNumber = port, input = await ReadAsync<RemoteAccessEntry>(args[4], token) }, token);
                        break;
                    case "access-delete":
                        Count(args, 5);
                        result = await client.CallAsync<RemoteAccessEntry[]>("remote.access.delete", new { accountId, target, portNumber = port, entryId = args[4], confirmation }, token);
                        break;
                    case "access-reset":
                        Count(args, 4);
                        result = await client.CallAsync<RemoteAccessEntry[]>("remote.access.reset", new { accountId, target, portNumber = port, confirmation }, token);
                        break;
                    case "token":
                        Count(args, 5);
                        var credential = await client.CallAsync<RemoteTokenResult>("remote.token",
                            new { accountId, target, input = new RemoteTokenRequest(args[4].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), port) }, token);
                        result = reveal ? credential : new { credential.Scopes, credential.ExpiresAt, credential.ExpirationSource, tokenHidden = true };
                        break;
                    default: throw new ToolkitException("INVALID_ARGUMENTS");
                }
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(WireProtocol.Json) { WriteIndented = true }));
        return true;
    }

    private sealed record Selection(RemoteTunnelTarget[] Targets, string[] Confirmations);
    private static async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        if (file.Length > WireProtocol.MaxFrameBytes) throw new ToolkitException("INPUT_TOO_LARGE");
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await file.ReadAsync(buffer, token)) != 0)
        {
            if (bounded.Length + count > WireProtocol.MaxFrameBytes) throw new ToolkitException("INPUT_TOO_LARGE");
            await bounded.WriteAsync(buffer.AsMemory(0, count), token);
        }
        bounded.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(bounded, WireProtocol.Json, token) ?? throw new ToolkitException("INVALID_INPUT");
    }
    private static void Count(List<string> args, int count) { if (args.Count != count) throw new ToolkitException("INVALID_ARGUMENTS"); }
    private static string? Option(List<string> args, string name)
    {
        var index = args.IndexOf(name);
        if (index < 0) return null;
        if (index + 1 >= args.Count) throw new ToolkitException("MISSING_OPTION_VALUE");
        var value = args[index + 1]; args.RemoveRange(index, 2); return value;
    }
}
