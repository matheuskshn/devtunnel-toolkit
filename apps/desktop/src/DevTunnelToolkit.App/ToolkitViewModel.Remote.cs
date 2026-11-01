using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App;

public sealed partial class ToolkitViewModel
{
    private CancellationTokenSource? _remoteOperation;
    public string? RemoteAccountId { get; private set; }
    public ManagedTunnel[] RemoteTunnels { get; private set; } = [];
    public bool CanCancelRemoteOperation => _remoteOperation is not null;
    public void CancelRemoteOperation() => _remoteOperation?.Cancel();

    private async Task<T?> RemoteCallAsync<T>(string operation, object payload, string activity, string success,
        Func<T, CancellationToken, Task>? accept = null) where T : class
    {
        T? result = null;
        await RunAsync(async lifetime =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _remoteOperation = cancellation;
            StateChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                result = await gateway.CallAsync<T>(operation, payload, cancellation.Token);
                if (accept is not null) await accept(result, cancellation.Token);
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                result = null;
                throw new ToolkitException("REMOTE_WAIT_CANCELLED");
            }
            finally { _remoteOperation = null; }
        }, success, activity, "remote", refreshAfter: false);
        return result;
    }

    public async Task<bool> LoadRemoteTunnelsAsync(string accountId)
    {
        var result = await RemoteCallAsync<ManagedTunnel[]>("remote.list", new { accountId },
            "Consultando os túneis salvos no servidor para esta conta…", "Túneis remotos atualizados.", (items, _) =>
            {
                RemoteAccountId = accountId; RemoteTunnels = items; return Task.CompletedTask;
            });
        return result is not null;
    }

    private void AcceptRemote(string accountId, ManagedTunnel tunnel)
    {
        if (RemoteAccountId != accountId) { RemoteAccountId = accountId; RemoteTunnels = []; }
        RemoteTunnels = RemoteTunnels.Where(t => t.Target != tunnel.Target).Append(tunnel).OrderBy(t => t.Target.CanonicalId).ToArray();
    }

    public Task<ManagedTunnel?> GetRemoteTunnelAsync(string accountId, RemoteTunnelTarget target) =>
        RemoteCallAsync<ManagedTunnel>("remote.get", new { accountId, target }, "Consultando propriedades, portas e permissões no servidor…",
            "Recurso remoto consultado.", (item, _) => { AcceptRemote(accountId, item); return Task.CompletedTask; });

    public Task<ManagedTunnel?> SaveRemoteTunnelAsync(string accountId, RemoteTunnelSpec input, RemoteTunnelTarget? target = null) =>
        RemoteCallAsync<ManagedTunnel>(target is null ? "remote.create" : "remote.update", new { accountId, target, input },
            target is null ? "Criando um túnel privado no servidor…" : "Atualizando as propriedades do túnel no servidor…",
            "Túnel salvo no servidor.", (item, _) => { AcceptRemote(accountId, item); return Task.CompletedTask; });

    private async Task<bool> RemoteWriteAsync(string operation, object payload, string activity, string success,
        Func<CancellationToken, Task>? accept = null)
    {
        return await RunAsync(async lifetime =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _remoteOperation = cancellation; StateChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                await gateway.CallAsync<bool>(operation, payload, cancellation.Token);
                if (accept is not null) await accept(cancellation.Token);
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { throw new ToolkitException("REMOTE_WAIT_CANCELLED"); }
            finally { _remoteOperation = null; }
        }, success, activity, "remote", refreshAfter: false);
    }

    public Task<bool> DeleteRemoteTunnelAsync(string accountId, RemoteTunnelTarget target, string confirmation) =>
        RemoteWriteAsync("remote.delete", new { accountId, target, confirmation }, "Excluindo o recurso remoto. As conexões serão afetadas…",
            "Túnel excluído no servidor. Perfis locais não foram removidos.", _ =>
            {
                if (RemoteAccountId == accountId) RemoteTunnels = RemoteTunnels.Where(t => t.Target != target).ToArray();
                return Task.CompletedTask;
            });

    public Task<RemoteBatchResult[]?> DeleteRemoteBatchAsync(string accountId, RemoteTunnelTarget[] targets, string[] confirmations) =>
        RemoteCallAsync<RemoteBatchResult[]>("remote.delete-batch", new { accountId, targets, confirmations }, "Excluindo somente os túneis remotos confirmados…",
            "Exclusão em lote processada. Confira o resultado individual de cada recurso.", (results, _) =>
            {
                var removed = results.Where(r => r.Succeeded).Select(r => r.CanonicalId).ToHashSet(StringComparer.Ordinal);
                if (RemoteAccountId == accountId) RemoteTunnels = RemoteTunnels.Where(t => !removed.Contains(t.Target.CanonicalId)).ToArray();
                return Task.CompletedTask;
            });

    public async Task<bool> SaveRemotePortAsync(string accountId, RemoteTunnelTarget target, RemotePortSpec input, bool create)
    {
        return await RemoteCallAsync<ManagedPort>("remote.port.save", new { accountId, target, input, create },
            "Salvando a porta no recurso remoto…", "Porta salva no servidor.") is not null;
    }
    public Task<bool> DeleteRemotePortAsync(string accountId, RemoteTunnelTarget target, int portNumber, string confirmation) =>
        RemoteWriteAsync("remote.port.delete", new { accountId, target, portNumber, confirmation }, "Excluindo a porta remota…", "Porta excluída no servidor.");
    public async Task<bool> SaveRemoteAccessAsync(string accountId, RemoteTunnelTarget target, int? portNumber, RemoteAccessEntry input) =>
        await RemoteCallAsync<RemoteAccessEntry[]>("remote.access.save", new { accountId, target, portNumber, input },
            "Atualizando as permissões do recurso remoto…", "Regra de acesso adicionada.") is not null;
    public async Task<bool> DeleteRemoteAccessAsync(string accountId, RemoteTunnelTarget target, int? portNumber, string entryId, string confirmation) =>
        await RemoteCallAsync<RemoteAccessEntry[]>("remote.access.delete", new { accountId, target, portNumber, entryId, confirmation },
            "Removendo a regra de acesso selecionada…", "Regra removida no servidor.") is not null;
    public async Task<bool> ResetRemoteAccessAsync(string accountId, RemoteTunnelTarget target, int? portNumber, string confirmation) =>
        await RemoteCallAsync<RemoteAccessEntry[]>("remote.access.reset", new { accountId, target, portNumber, confirmation },
            "Restaurando o acesso privado do recurso…", "Permissões explícitas removidas. Revise as permissões herdadas.") is not null;
    public Task<RemoteTokenResult?> IssueRemoteTokenAsync(string accountId, RemoteTunnelTarget target, RemoteTokenRequest input) =>
        RemoteCallAsync<RemoteTokenResult>("remote.token", new { accountId, target, input }, "Solicitando um token com os escopos selecionados…",
            "Token emitido. Ele aparece somente no diálogo atual e não é salvo no histórico.");
    public Task<RemoteCluster[]?> LoadRemoteClustersAsync(string accountId) =>
        RemoteCallAsync<RemoteCluster[]>("remote.clusters", new { accountId }, "Consultando os clusters do serviço Dev Tunnels…", "Clusters consultados.");
    public Task<RemoteDiagnostic?> DiagnoseRemoteAsync(string accountId, RemoteTunnelTarget target) =>
        RemoteCallAsync<RemoteDiagnostic>("remote.diagnostics", new { accountId, target }, "Medindo a consulta autenticada ao serviço de gerenciamento…",
            "Diagnóstico concluído. Esta consulta não verifica tráfego pelo relay.");

    public Task<bool> SaveHostAsync(HostProfile profile) => RunAsync(async token =>
    {
        HostingValidation.Profile(profile);
        await gateway.CallAsync<HostProfile>("host.save", profile, token);
    }, "Perfil de hospedagem salvo. As portas remotas não foram alteradas.",
        "Validando e salvando a hospedagem neste dispositivo…", profile.Id);

    public Task<bool> HostActionAsync(string operation, string id, string? confirmation = null) => RunAsync(async token =>
    {
        if (operation is not ("host.prepare" or "host.start" or "host.stop" or "host.restart" or "host.remove")) throw new ToolkitException("INVALID_OPERATION");
        if (operation == "host.prepare") await gateway.CallAsync<HostPreparationResult>(operation, new { id, confirmation }, token);
        else await gateway.CallAsync<bool>(operation, new { id, confirmation }, token);
    }, operation switch
    {
        "host.start" or "host.restart" => "Solicitação aceita. Acompanhe as etapas da hospedagem.",
        "host.prepare" => "Portas remotas preparadas. Inicie a hospedagem quando desejar.",
        "host.remove" => "Perfil removido deste dispositivo. O recurso remoto foi preservado.",
        _ => "Publicação encerrada. Serviços locais e túnel remoto preservados."
    }, operation switch
    {
        "host.prepare" => "Criando somente as portas remotas ausentes, sem alterar as existentes…",
        "host.remove" => "Removendo o perfil de hospedagem local…",
        "host.stop" => "Encerrando a publicação e preservando os serviços locais…",
        _ => "Solicitando publicação dos serviços pelo relay…"
    }, id);

    public OperationActivity? HostActivity(string id)
    {
        if (IsBusy && OperationTargetId == id) return ForegroundActivity;
        if (!IsConnected || Snapshot?.HostStatuses.FirstOrDefault(h => h.ProfileId == id) is not { } status) return null;
        var description = status.State switch
        {
            ConnectionState.Connected => "Hospedagem pronta. Serviços publicados pelo relay.",
            ConnectionState.Stopped => "Hospedagem encerrada. Recurso remoto preservado.",
            ConnectionState.Stopping => "Encerrando a publicação; os serviços locais permanecem ativos…",
            ConnectionState.Reconnecting => "Aguardando restabelecer a hospedagem…",
            ConnectionState.AuthenticationRequired => "Autentique novamente a conta do host em Contas.",
            ConnectionState.Failed or ConnectionState.Degraded => FriendlyError(new ToolkitException(status.LastError ?? "HOST_START_FAILED")),
            _ => status.Phase switch
            {
                "host-preflight" => "Verificando a conta, o túnel e as permissões de hospedagem…",
                "host-listeners" => "Validando os serviços e vinculando as portas locais…",
                "host-relay" => "Conectando a hospedagem ao relay Dev Tunnels…",
                _ => "Solicitação aceita. Preparando a hospedagem…"
            }
        };
        return new(description, status.OperationStartedAt, IsConnectionPending(status.State), status.NextRetryAt, status.OperationCompletedAt);
    }

    public Task<bool> SetEchoAsync(bool running, int port = 0) => RunAsync(async token =>
    {
        if (running) await gateway.CallAsync<EchoStatus>("diagnostic.echo.start", new { port }, token);
        else await gateway.CallAsync<EchoStatus>("diagnostic.echo.stop", cancellationToken: token);
    }, running ? "Fixture TCP local iniciada. Nenhum túnel remoto foi criado." : "Fixture TCP local encerrada.",
        running ? "Iniciando o serviço local de diagnóstico TCP…" : "Encerrando o serviço de diagnóstico…");

    public async Task<EchoProbeResult?> PingEchoAsync(string id, int portNumber)
    {
        EchoProbeResult? result = null;
        await RunAsync(async token => result = await gateway.CallAsync<EchoProbeResult>("diagnostic.ping", new { id, portNumber }, token),
            "Teste TCP concluído. Consulte o resultado do diagnóstico.", "Enviando um desafio TCP pelo mapeamento conectado e aguardando o retorno…", id);
        return result;
    }
}
