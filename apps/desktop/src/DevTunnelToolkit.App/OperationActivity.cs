using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App;

public sealed record OperationActivity(string Description, DateTimeOffset? StartedAt, bool Running,
    DateTimeOffset? NextAttemptAt = null, DateTimeOffset? FinishedAt = null)
{
    public string Timing(DateTimeOffset now)
    {
        if (StartedAt is not { } start) return Running ? "Aguardando atualização do agente…" : "";
        var seconds = Math.Max(0, (int)((FinishedAt ?? now) - start).TotalSeconds);
        var duration = seconds < 60 ? $"{seconds}s" : $"{seconds / 60}min {seconds % 60:00}s";
        if (!Running) return $"Duração: {duration}.";
        if (NextAttemptAt is { } next)
            return $"Em andamento há {duration}. Próxima tentativa em {Math.Max(0, (int)Math.Ceiling((next - now).TotalSeconds))}s.";
        return $"Em andamento há {duration}." + (seconds >= 15 ? " Ainda aguardando; o tempo restante não é informado." : "");
    }
}

public sealed partial class ToolkitViewModel
{
    private DateTimeOffset? _agentStartedAt;
    private string _operationDescription = "";
    private OperationActivity? _lastActivity;
    private readonly Dictionary<string, DateTimeOffset> _connectionObservedAt = [];
    private readonly Dictionary<string, DateTimeOffset> _loginObservedAt = [];
    public string? OperationTargetId { get; private set; }
    public OperationActivity? ForegroundActivity => IsBusy ? new(_operationDescription, OperationStartedAt, true) : _lastActivity;

    public static bool IsConnectionPending(ConnectionState state) => state is ConnectionState.Authenticating or
        ConnectionState.Discovering or ConnectionState.Connecting or ConnectionState.Reconnecting or ConnectionState.Stopping;

    private void ObserveActivities(AgentSnapshot snapshot)
    {
        var pending = snapshot.Connections.Where(c => IsConnectionPending(c.State)).Select(c => c.ProfileId).ToHashSet();
        foreach (var id in _connectionObservedAt.Keys.Where(id => !pending.Contains(id)).ToArray()) _connectionObservedAt.Remove(id);
        foreach (var id in pending) _connectionObservedAt.TryAdd(id, DateTimeOffset.UtcNow);
        var loggingIn = snapshot.Logins.Where(l => l.State is "Starting" or "Pending" or "AwaitingUser").Select(l => l.AccountId).Concat(_desktopLogins.Keys).ToHashSet();
        foreach (var id in _loginObservedAt.Keys.Where(id => !loggingIn.Contains(id)).ToArray()) _loginObservedAt.Remove(id);
        foreach (var id in loggingIn) _loginObservedAt.TryAdd(id, DateTimeOffset.UtcNow);
    }

    public OperationActivity? TunnelActivity(string id)
    {
        if (IsBusy && OperationTargetId == id) return ForegroundActivity;
        if (!IsConnected) return null;
        var state = Snapshot?.Connections.FirstOrDefault(c => c.ProfileId == id);
        if (state is null) return null;
        var description = state.State switch
        {
            ConnectionState.Authenticating => "Validando a autenticação para acessar o túnel…",
            ConnectionState.Discovering => "Consultando o túnel, permissões e portas remotas…",
            ConnectionState.Connecting when state.Phase == "queued" => "Solicitação aceita. Preparando a conexão…",
            ConnectionState.Connecting when state.Phase == "mapping" => "Relay estabelecido. Preparando as portas locais…",
            ConnectionState.Connecting => "Estabelecendo a conexão com o relay do túnel…",
            ConnectionState.Reconnecting when state.NextRetryAt is not null => $"Aguardando a tentativa {state.RetryCount} de reconexão…",
            ConnectionState.Reconnecting => "Restabelecendo a conexão com o relay…",
            ConnectionState.Stopping => "Encerrando o relay e liberando as portas locais…",
            ConnectionState.Connected => "Conexão estabelecida. Portas locais prontas.",
            ConnectionState.Stopped => "Conexão encerrada. Portas locais liberadas.",
            ConnectionState.AuthenticationRequired => "A conexão precisa de autenticação. Abra Contas para entrar novamente.",
            ConnectionState.Failed or ConnectionState.Degraded => $"Conexão não disponível. {FriendlyError(new ToolkitException(state.ErrorCode ?? "CONNECTION_FAILED"))}",
            _ => ""
        };
        var running = IsConnectionPending(state.State);
        var started = state.OperationStartedAt ?? (_connectionObservedAt.TryGetValue(id, out var observed) ? observed : (DateTimeOffset?)null);
        return new(description, started, running, state.NextRetryAt, state.OperationCompletedAt);
    }

    public OperationActivity? AccountActivity(string id)
    {
        if (IsBusy && OperationTargetId == id) return ForegroundActivity;
        if (!IsConnected) return null;
        var login = Snapshot?.Logins.FirstOrDefault(l => l.AccountId == id);
        if (IsDesktopLoginActive(id) || login?.State is "Starting" or "Pending" or "AwaitingUser")
        {
            var mode = login?.RequestedMode ?? Snapshot?.Accounts.FirstOrDefault(a => a.Id == id)?.CliLoginMode;
            var description = login?.Challenge is not null ? "Aguardando você concluir o login no provedor. Use o código exibido abaixo." :
                IsDesktopLoginActive(id) || mode == CliLoginMode.Browser ? "Conclua o login na janela Microsoft ou no navegador. Você pode cancelar abaixo." :
                mode == CliLoginMode.WindowsIntegrated ? "Autenticando com a sessão Windows do agente…" : "Aguardando o provedor iniciar a autenticação…";
            return new(description, login?.StartedAt ?? (_loginObservedAt.TryGetValue(id, out var observed) ? observed : null), true);
        }
        return OperationTargetId == id ? ForegroundActivity : null;
    }

    public OperationActivity? WorkspaceActivity
    {
        get
        {
            if (IsBusy) return ForegroundActivity;
            if (IsStartingAgent) return new("Verificando e iniciando o agente local…", _agentStartedAt, true);
            if (!IsConnected) return new(Message, null, false);
            var activities = new List<OperationActivity>();
            foreach (var tunnel in Snapshot?.Tunnels ?? [])
                if (TunnelActivity(tunnel.Id) is { Running: true } activity) activities.Add(activity with { Description = $"{tunnel.CanonicalId}: {activity.Description}" });
            foreach (var account in Snapshot?.Accounts ?? [])
                if (AccountActivity(account.Id) is { Running: true } activity) activities.Add(activity with { Description = $"{account.DisplayName}: {activity.Description}" });
            foreach (var host in Snapshot?.Hosts ?? [])
                if (HostActivity(host.Id) is { Running: true } activity) activities.Add(activity with { Description = $"{host.CanonicalId}: {activity.Description}" });
            foreach (var service in Snapshot?.Services ?? [])
                if (ServiceActivity(service.Id) is { Running: true } activity) activities.Add(activity with { Description = $"{service.Name}: {activity.Description}" });
            if (activities.Count == 0) return ForegroundActivity;
            var first = activities.OrderBy(a => a.StartedAt).First();
            return activities.Count == 1 ? first : first with { Description = $"{activities.Count} operações em andamento. {first.Description}" };
        }
    }

    public void ReportActivity(string description)
    {
        _operationDescription = description; Message = description; StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ReportFailure(Exception error)
    {
        Message = FriendlyError(error); _lastActivity = new(Message, null, false, FinishedAt: DateTimeOffset.UtcNow); StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
