using System.Text.Json;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.Application;

public sealed partial class AgentRuntime
{
    private async Task<object> DispatchRemoteAsync(string operation, JsonElement payload, CancellationToken cancellationToken)
    {
        var service = remoteManagement ?? throw new ToolkitException("REMOTE_MANAGEMENT_UNAVAILABLE");
        var accountId = payload.GetProperty("accountId").GetString() ?? "";
        Validation.Id(accountId);
        var readOnly = operation is "remote.list" or "remote.get" or "remote.clusters" or "remote.diagnostics";
        if (!readOnly) await mutation.WaitAsync(cancellationToken);
        AccountProfile? auditAccount = null;
        RemoteTunnelTarget? auditTarget = null;
        var writeAttempted = false;
        try
        {
            var account = Account(accountId);
            auditAccount = account;
            RequireNoLogin(accountId);
            if (operation == "remote.list") return await service.ListAsync(account, cancellationToken);
            if (operation == "remote.clusters") return await service.ListClustersAsync(account, cancellationToken);
            if (operation == "remote.create")
            {
                writeAttempted = true;
                var created = await service.CreateAsync(account, Read<RemoteTunnelSpec>(payload.GetProperty("input")), cancellationToken);
                AuditRemote(operation, account, created.Target);
                return created;
            }
            if (operation == "remote.delete-batch")
            {
                var targets = Read<RemoteTunnelTarget[]>(payload.GetProperty("targets"));
                var confirmations = Read<string[]>(payload.GetProperty("confirmations"));
                if (targets.Length is < 1 or > 25 || targets.Length != confirmations.Length ||
                    targets.Select(t => t.CanonicalId).Distinct().Count() != targets.Length)
                    throw new ToolkitException("REMOTE_BATCH_INVALID");
                // Validate the entire selection before issuing the first destructive request.
                for (var i = 0; i < targets.Length; i++)
                {
                    ValidateRemoteTarget(account, targets[i]);
                    ConfirmRemote(targets[i], confirmations[i]);
                    RequireRemoteIdle(account.Id, targets[i]);
                }
                var results = new List<RemoteBatchResult>();
                foreach (var target in targets)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        results.Add(new(target.CanonicalId, false, "NOT_ATTEMPTED"));
                        continue;
                    }
                    try
                    {
                        await service.DeleteAsync(account, target, target.CanonicalId, cancellationToken);
                        results.Add(new(target.CanonicalId, true)); AuditRemote("remote.delete", account, target);
                    }
                    catch (Exception error)
                    {
                        var code = error is OperationCanceledException ? "REMOTE_OUTCOME_UNKNOWN" : ErrorCode(error);
                        results.Add(new(target.CanonicalId, false, code));
                        AuditRemote("remote.delete", account, target, false);
                    }
                }
                return results.ToArray();
            }
            var selected = Read<RemoteTunnelTarget>(payload.GetProperty("target"));
            ValidateRemoteTarget(account, selected);
            auditTarget = selected;
            if (operation == "remote.get") return await service.GetAsync(account, selected, cancellationToken);
            if (operation == "remote.diagnostics") return await service.DiagnoseAsync(account, selected, cancellationToken);
            var port = payload.TryGetProperty("portNumber", out var number) && number.ValueKind != JsonValueKind.Null ? number.GetInt32() : (int?)null;
            if (port is < 1 or > 65535) throw new ToolkitException("INVALID_PORT");
            var confirmation = payload.TryGetProperty("confirmation", out var text) ? text.GetString() ?? "" : "";
            if (operation is "remote.delete" or "remote.port.delete" or "remote.access.delete" or "remote.access.reset")
                ConfirmRemote(selected, confirmation);
            // Policy and port changes are explicit. Stop this agent's dependent
            // host/client first, avoiding confusing partially applied live state.
            if (operation is "remote.update" or "remote.delete" or "remote.port.save" or "remote.port.delete" or
                "remote.access.save" or "remote.access.delete" or "remote.access.reset")
                RequireRemoteIdle(account.Id, selected);
            writeAttempted = true;
            object result = operation switch
            {
                "remote.update" => await service.UpdateAsync(account, selected, Read<RemoteTunnelSpec>(payload.GetProperty("input")), cancellationToken),
                "remote.delete" => await DeleteRemoteAsync(service, account, selected, confirmation, cancellationToken),
                "remote.port.save" => await service.SavePortAsync(account, selected, Read<RemotePortSpec>(payload.GetProperty("input")), payload.GetProperty("create").GetBoolean(), cancellationToken),
                "remote.port.delete" => await DeleteRemotePortAsync(service, account, selected, port ?? throw new ToolkitException("INVALID_PORT"), confirmation, cancellationToken),
                "remote.access.save" => await service.AddAccessAsync(account, selected, port, Read<RemoteAccessEntry>(payload.GetProperty("input")), cancellationToken),
                "remote.access.delete" => await service.RemoveAccessAsync(account, selected, port, payload.GetProperty("entryId").GetString() ?? "", confirmation, cancellationToken),
                "remote.access.reset" => await service.ResetAccessAsync(account, selected, port, confirmation, cancellationToken),
                "remote.token" => await service.IssueTokenAsync(account, selected, Read<RemoteTokenRequest>(payload.GetProperty("input")), cancellationToken),
                _ => throw new ToolkitException("UNKNOWN_OPERATION")
            };
            AuditRemote(operation, account, selected);
            return result;
        }
        catch (Exception error) when (auditAccount is not null && operation is "remote.create" or "remote.update" or "remote.delete" or
            "remote.port.save" or "remote.port.delete" or "remote.access.save" or "remote.access.delete" or "remote.access.reset" or "remote.token")
        {
            var outcome = writeAttempted && error is OperationCanceledException ? "REMOTE_OUTCOME_UNKNOWN" : ErrorCode(error);
            Log("Warning", "REMOTE_OPERATION_FAILED",
                $"Operation {operation}; account {auditAccount!.Id}; result {outcome}. No credentials or request content recorded.", auditTarget?.CanonicalId);
            throw;
        }
        finally { if (!readOnly) mutation.Release(); }
    }

    private static void ValidateRemoteTarget(AccountProfile account, RemoteTunnelTarget target)
    {
        if (target is null) throw new ToolkitException("INVALID_TUNNEL_ID");
        Validation.Tunnel(new() { AccountId = account.Id, TunnelId = target.TunnelId, ClusterId = target.ClusterId });
    }

    private static void ConfirmRemote(RemoteTunnelTarget target, string confirmation)
    {
        if (!string.Equals(target.CanonicalId, confirmation, StringComparison.Ordinal))
            throw new ToolkitException("REMOTE_CONFIRMATION_REQUIRED");
    }

    private void RequireRemoteIdle(string accountId, RemoteTunnelTarget target)
    {
        lock (sync)
        {
            if (state.Tunnels.Any(t => t.CanonicalId == target.CanonicalId &&
                running.TryGetValue(t.Id, out var task) && !task.Completion.IsCompleted) ||
                state.Hosts.Any(h => h.CanonicalId == target.CanonicalId &&
                hostingOperations.TryGetValue(h.Id, out var task) && !task.Completion.IsCompleted))
                throw new ToolkitException("REMOTE_RESOURCE_IN_USE");
        }
    }

    private void AuditRemote(string operation, AccountProfile account, RemoteTunnelTarget target, bool success = true) =>
        Log(success ? "Information" : "Warning", success ? "REMOTE_OPERATION_COMPLETED" : "REMOTE_OPERATION_FAILED",
            $"Operation {operation}; account {account.Id}; tunnel {target.CanonicalId}. No credentials or request content recorded.", target.CanonicalId);

    private static async Task<bool> DeleteRemoteAsync(IRemoteTunnelManagementService service, AccountProfile account, RemoteTunnelTarget target, string confirmation, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(account, target, confirmation, cancellationToken);
        return true;
    }

    private static async Task<bool> DeleteRemotePortAsync(IRemoteTunnelManagementService service, AccountProfile account, RemoteTunnelTarget target, int port, string confirmation, CancellationToken cancellationToken)
    {
        await service.DeletePortAsync(account, target, port, confirmation, cancellationToken);
        return true;
    }
}
