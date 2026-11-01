using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App;

public sealed partial class ToolkitViewModel
{
    public Task<bool> SaveServiceAsync(ServiceProfile profile) => RunAsync(async token =>
    {
        ServiceValidation.Profile(profile);
        await gateway.CallAsync<ServiceProfile>("service.save", profile, token);
    }, "Serviço salvo. Inicie-o quando desejar; nenhum túnel remoto foi alterado.", "Validando e salvando o serviço e suas regras de acesso…", profile.Id);

    public Task<bool> ServiceActionAsync(string operation, string id) => RunAsync(async token =>
    {
        if (operation is not ("service.start" or "service.stop" or "service.remove")) throw new ToolkitException("INVALID_OPERATION");
        await gateway.CallAsync<bool>(operation, new { id }, token);
    }, operation switch { "service.start" => "Solicitação aceita. Acompanhe o estado do serviço.", "service.stop" => "Serviço encerrado. Reinicie as publicações afetadas após iniciar o serviço novamente.", _ => "Serviço removido deste dispositivo. Recursos remotos preservados." },
        operation switch { "service.start" => "Iniciando listener local e aplicando a política do serviço…", "service.stop" => "Encerrando clientes do serviço e revogando seus vínculos ativos…", _ => "Removendo a configuração do serviço…" }, id);

    public OperationActivity? ServiceActivity(string id)
    {
        if (IsBusy && OperationTargetId == id) return ForegroundActivity;
        if (!IsConnected || Snapshot?.ServiceStatuses.FirstOrDefault(s => s.ServiceId == id) is not { } state) return null;
        var description = state.State switch
        {
            ConnectionState.Connected => "Listener pronto para receber conexões. A publicação é configurada separadamente em DevTunnel.",
            ConnectionState.Stopped => "Serviço parado. Recursos remotos preservados.",
            ConnectionState.Stopping => "Encerrando clientes locais e revogando os vínculos publicados…",
            ConnectionState.Failed or ConnectionState.Degraded => FriendlyError(new ToolkitException(state.LastError ?? "SERVICE_START_FAILED")),
            _ => "Preparando o listener local e as regras de acesso…"
        };
        return new(description, state.OperationStartedAt, IsConnectionPending(state.State), FinishedAt: state.OperationCompletedAt);
    }
}
