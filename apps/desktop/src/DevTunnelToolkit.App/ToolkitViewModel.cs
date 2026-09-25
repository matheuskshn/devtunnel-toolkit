using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.DevTunnels;
using DevTunnelToolkit.Ipc;

namespace DevTunnelToolkit.App;

public interface IToolkitGateway
{
    Task<T> CallAsync<T>(string operation, object? payload = null, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentSnapshot> WatchAsync(CancellationToken cancellationToken = default);
}

public interface IDesktopLoginGateway
{
    Task<LoginStatus> LoginFromDesktopAsync(string id, CancellationToken cancellationToken);
}

public sealed class ToolkitGateway(string? dataDirectory = null) : IToolkitGateway, IDesktopLoginGateway
{
    private readonly ToolkitClient _client = new(dataDirectory);
    public Task<LoginStatus> LoginFromDesktopAsync(string id, CancellationToken cancellationToken)
        => _client.LoginFromDesktopAsync(id, CliAuthenticationService.RunDesktopLoginAsync, cancellationToken);
    public Task<T> CallAsync<T>(string operation, object? payload = null, CancellationToken cancellationToken = default)
        => _client.CallAsync<T>(operation, payload, cancellationToken);
    public IAsyncEnumerable<AgentSnapshot> WatchAsync(CancellationToken cancellationToken = default)
        => _client.WatchAsync(cancellationToken);
}

public sealed partial class ToolkitViewModel(IToolkitGateway gateway, Action<Action>? dispatch = null,
    Func<CancellationToken, Task>? ensureAgent = null, bool? isWindows = null) : INotifyPropertyChanged, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<Action> _dispatch = dispatch ?? (action => action());
    private bool _isConnected;
    private bool _requiresAgentUpdate;
    private bool _isBusy;
    private bool _isStartingAgent;
    private string _message = "Conectando ao agente local…";
    private AgentSnapshot? _snapshot;
    private Task? _watch;
    private bool _disposed;
    private readonly Dictionary<string, CancellationTokenSource> _desktopLogins = [];
    private Task? _start;

    public bool IsDesktopLoginActive(string id) => _desktopLogins.ContainsKey(id);
    public void CancelDesktopLogins()
    {
        foreach (var source in _desktopLogins.Values.ToArray()) source.Cancel();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? StateChanged;
    public AgentSnapshot? Snapshot { get => _snapshot; private set => Set(ref _snapshot, value); }
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
    public bool RequiresAgentUpdate { get => _requiresAgentUpdate; private set => Set(ref _requiresAgentUpdate, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public DateTimeOffset? OperationStartedAt { get; private set; }
    public bool IsStartingAgent { get => _isStartingAgent; private set => Set(ref _isStartingAgent, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string? DiscoveryAccountId { get; private set; }
    public DiscoveredTunnel[] DiscoveredTunnels { get; private set; } = [];

    public Task StartAsync() => _start ??= StartCoreAsync();

    private async Task StartCoreAsync()
    {
        if (ensureAgent is not null)
        {
            Message = "Iniciando agente local…";
            _agentStartedAt = DateTimeOffset.UtcNow;
            IsStartingAgent = true; StateChanged?.Invoke(this, EventArgs.Empty);
            try { await ensureAgent(_lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error) { Disconnected(error); _watch ??= WatchLoopAsync(); return; }
            finally { IsStartingAgent = false; StateChanged?.Invoke(this, EventArgs.Empty); }
        }
        await RefreshAsync();
        _watch ??= WatchLoopAsync();
    }

    public Task<bool> RetryAgentAsync() => RunAsync(async token =>
    {
        if (ensureAgent is null) throw new ToolkitException("AGENT_EXECUTABLE_NOT_FOUND");
        IsStartingAgent = true; StateChanged?.Invoke(this, EventArgs.Empty);
        try { await ensureAgent(token); }
        finally { IsStartingAgent = false; StateChanged?.Invoke(this, EventArgs.Empty); }
    }, "Agente conectado.", "Verificando e iniciando o agente local…");

    public async Task RefreshAsync()
    {
        try
        {
            Accept(await gateway.CallAsync<AgentSnapshot>("status", cancellationToken: _lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Disconnected(ex); }
    }

    public async Task<bool> RunAsync(Func<CancellationToken, Task> operation, string success, string activity = "Processando solicitação no agente…", string? targetId = null, bool refreshAfter = true)
    {
        if (IsBusy) return false;
        OperationStartedAt = DateTimeOffset.UtcNow;
        OperationTargetId = targetId;
        _operationDescription = activity;
        _lastActivity = null;
        Message = activity;
        IsBusy = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await operation(_lifetime.Token);
            Message = success;
            if (refreshAfter) await RefreshAsync();
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (ex is ToolkitException { Code: "IPC_VERSION_MISMATCH" }) Disconnected(ex);
            else Message = FriendlyError(ex);
            return false;
        }
        finally
        {
            _lastActivity = new(Message, OperationStartedAt, false, FinishedAt: DateTimeOffset.UtcNow);
            OperationStartedAt = null; IsBusy = false; StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task<bool> ControlAsync(string operation) => RunAsync(async token =>
    {
        if (operation is not ("proxy.start" or "proxy.stop" or "connections.stop")) throw new ToolkitException("INVALID_OPERATION");
        await gateway.CallAsync<bool>(operation, cancellationToken: token);
    }, "Operação concluída.", operation switch
    {
        "proxy.start" => "Iniciando o proxy local…",
        "proxy.stop" => "Encerrando o proxy e suas conexões…",
        _ => "Encerrando conexões…"
    });

    public Task<bool> ShutdownAgentAsync() => RunAsync(async token =>
    {
        try { await gateway.CallAsync<bool>("agent.shutdown", cancellationToken: token); }
        catch (ToolkitException e) when (e.Code == "AGENT_UNAVAILABLE") { }
    }, "Agente encerrado.", "Encerrando túneis, proxy e agente local…", refreshAfter: false);

    public async Task<RouteDecision?> TestRouteAsync(string host, int port)
    {
        RouteDecision? result = null;
        await RunAsync(async token => result = await gateway.CallAsync<RouteDecision>("proxy.test", new { host, port }, token),
            "Regra avaliada. Nenhum acesso ao destino foi realizado.", "Avaliando as regras de roteamento…", "proxy");
        return result;
    }
    public async Task<ProxyProbeResult?> ProbeAsync(string host, int port, bool https, bool upstreamOnly = false)
    {
        ProxyProbeResult? result = null;
        await RunAsync(async token => result = await gateway.CallAsync<ProxyProbeResult>("proxy.probe", new { host, port, https, upstreamOnly }, token),
            "Teste de acesso concluído. Consulte o resultado no painel do proxy.", upstreamOnly ? "Testando diretamente o proxy remoto pelo túnel. Aguardando resposta HTTP/TLS…" : "Testando acesso pelo proxy local. Aguardando resposta HTTP/TLS…");
        return result;
    }
    public async Task<string?> PreviewPacAsync(AppSettings settings)
    {
        string? result = null;
        await RunAsync(async token => result = await gateway.CallAsync<string>("pac.preview", settings, token),
            "PAC gerado.", "Gerando a configuração PAC com os mapeamentos disponíveis…", "pac");
        return result;
    }

    public Task<bool> SaveAccountAsync(AccountProfile account) => RunAsync(async token =>
    {
        Validation.Account(account);
        await gateway.CallAsync<AccountProfile>("account.save", account, token);
    }, "Conta salva.", "Validando e salvando a conta…", account.Id);

    public Task<bool> SaveTunnelAsync(TunnelProfile tunnel) => RunAsync(async token =>
    {
        Validation.Tunnel(tunnel);
        await gateway.CallAsync<TunnelProfile>("tunnel.save", tunnel, token);
    }, "Perfil salvo. Reconecte o túnel para aplicar mudanças nos mapeamentos.", "Validando e salvando o perfil…", tunnel.Id);

    public Task<bool> SaveSettingsAsync(AppSettings settings) => RunAsync(async token =>
    {
        Validation.Settings(settings);
        await gateway.CallAsync<AppSettings>("settings.save", settings, token);
    }, "Configurações salvas.", "Salvando e aplicando configurações…");

    public Task<bool> ActionAsync(string operation, string id)
    {
        if (operation == "account.cancel" && _desktopLogins.TryGetValue(id, out var source))
        {
            source.Cancel(); return Task.FromResult(true);
        }
        var account = Snapshot?.Accounts.FirstOrDefault(a => a.Id == id);
        if (operation == "account.login" && (isWindows ?? OperatingSystem.IsWindows()) &&
            account is { Provider: AccountProvider.MicrosoftCli, CliLoginMode: CliLoginMode.Browser } && gateway is IDesktopLoginGateway desktop)
        {
            if (_desktopLogins.ContainsKey(id)) return Task.FromResult(false);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _desktopLogins[id] = cancellation;
            _loginObservedAt[id] = DateTimeOffset.UtcNow;
            Message = "Conclua o login na janela Microsoft. Você pode cancelar aqui.";
            StateChanged?.Invoke(this, EventArgs.Empty);
            _ = CompleteDesktopLoginAsync(desktop, id, cancellation);
            return Task.FromResult(true);
        }
        return RunAsync(async token =>
    {
        Validation.Id(id);
        switch (operation)
        {
            case "account.login": await gateway.CallAsync<LoginStatus>(operation, new { id }, token); break;
            case "account.validate": await gateway.CallAsync<AccountProfile>(operation, new { id }, token); break;
            case "tunnel.refresh": await gateway.CallAsync<DiscoveredTunnel>(operation, new { id }, token); break;
            case "account.cancel":
            case "account.remove":
            case "tunnel.remove":
            case "tunnel.connect":
            case "tunnel.stop":
            case "tunnel.reconnect":
                await gateway.CallAsync<bool>(operation, new { id }, token); break;
            default: throw new ToolkitException("INVALID_OPERATION");
        }
    }, operation switch
    {
        "tunnel.connect" or "tunnel.reconnect" => "Solicitação aceita. Acompanhe as etapas no cartão do túnel.",
        "tunnel.stop" => "Túnel parado. Portas locais liberadas.",
        "account.login" => "Login iniciado. Conclua a autenticação no provedor.",
        "account.cancel" => "Login cancelado.",
        "account.validate" => "Validação da conta concluída.",
        "tunnel.refresh" => "Portas remotas atualizadas.",
        _ => "Operação concluída."
    }, operation switch
    {
        "account.login" => "Iniciando autenticação no provedor…",
        "account.validate" => "Validando autenticação da conta…",
        "tunnel.refresh" => "Consultando as portas do túnel no provedor…",
        "tunnel.connect" => "Solicitando conexão ao túnel…",
        "tunnel.reconnect" => "Solicitando reconexão ao túnel…",
        "tunnel.stop" => "Encerrando conexão com o túnel…",
        _ => "Aplicando operação no agente…"
    }, id);
    }

    private async Task CompleteDesktopLoginAsync(IDesktopLoginGateway desktop, string id, CancellationTokenSource cancellation)
    {
        try
        {
            var result = await desktop.LoginFromDesktopAsync(id, cancellation.Token);
            Message = result.State == "Completed" ? "Autenticação concluída." : result.State == "Cancelled" ? "Login cancelado." : FriendlyError(new ToolkitException(result.ErrorCode ?? "DESKTOP_LOGIN_FAILED"));
        }
        catch (OperationCanceledException) { Message = "Login cancelado."; }
        catch (Exception error) { Message = FriendlyError(error); }
        finally
        {
            _lastActivity = new(Message, _loginObservedAt.TryGetValue(id, out var started) ? started : null, false, FinishedAt: DateTimeOffset.UtcNow);
            _desktopLogins.Remove(id); cancellation.Dispose();
            if (!_disposed) { await RefreshAsync(); StateChanged?.Invoke(this, EventArgs.Empty); }
        }
    }

    public Task<bool> DiscoverAsync(string accountId) => RunAsync(async token =>
    {
        Validation.Id(accountId);
        var result = await gateway.CallAsync<DiscoveredTunnel[]>("account.discover", new { id = accountId }, token);
        DiscoveryAccountId = accountId;
        DiscoveredTunnels = result;
    }, "Descoberta concluída.", "Consultando túneis e portas no provedor. Aguarde…", accountId);

    public static bool IsSafeLoginUri(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
            uri.Host is "github.com" or "microsoft.com" or "www.microsoft.com" or "login.microsoft.com" or "login.microsoftonline.com";
    }

    public static string FriendlyError(Exception error) => error switch
    {
        OperationCanceledException or TimeoutException => "Tempo de espera esgotado. Consulte o estado atual antes de tentar novamente: uma operação já aceita pode continuar no agente.",
        ToolkitException e => e.Code switch
        {
            "INVALID_TUNNEL_ID" => "Informe o Tunnel ID e o cluster válidos, como tunnel-demo e use2.",
            "INVALID_PORT" or "INVALID_LOCAL_PORT" => "As portas devem estar entre 1 e 65535. O modo manual exige uma porta local.",
            "INVALID_PORT_RANGE" => "A faixa deve estar entre 1024 e 65535, com até 10001 portas.",
            "LOOPBACK_REQUIRED" => "Use um endereço local: 127.0.0.1 ou ::1.",
            "LOCAL_PROXY_START_FAILED" => "O proxy local não iniciou. Verifique se o endereço e a porta estão disponíveis.",
            "LOCAL_PROXY_NOT_RUNNING" => "Inicie o proxy local antes de testar o acesso.",
            "PROXY_UPSTREAM_NOT_AVAILABLE" => "O destino precisa corresponder a uma regra de túnel com mapeamento conectado para testar o proxy remoto.",
            "LOCAL_SERVICE_PORT_CONFLICT" => "O proxy local e o servidor PAC precisam usar portas diferentes.",
            "PAC_REQUIRES_PROXY" => "Ative o proxy local para servir um PAC que encaminha para ele.",
            "INVALID_MANUAL_PAC" => "O PAC manual deve conter texto, sem caracteres de controle e com até 64 Ki caracteres.",
            "INVALID_PROXY_DESTINATIONS" => "Informe de 1 a 100 destinos por regra, separados por vírgula ou quebra de linha. Limite total: 1000 destinos.",
            "INVALID_CLI_LOGIN_MODE" => "Windows integrado requer o provedor Entra / Microsoft pelo CLI oficial.",
            "WINDOWS_AUTH_UNAVAILABLE" => "O login integrado só pode ser iniciado por um Agent em Windows. Use navegador ou código de dispositivo neste sistema.",
            "WINDOWS_AUTH_FAILED" => "O CLI não concluiu o login integrado. Verifique a sessão Windows e as políticas da organização; se exigirem interação/MFA, selecione navegador ou código de dispositivo.",
            "AUTH_INTERACTION_REQUIRED" => "O CLI informou que o login silencioso falhou e pediu login interativo. Edite esta conta e selecione Navegador. Isso não indica, por si só, bloqueio por política corporativa.",
            "WINDOWS_AUTH_ACCOUNT_UNSUPPORTED" => "A conta não suporta o fluxo integrado usado pelo CLI. Edite esta conta e selecione Navegador.",
            "WINDOWS_AUTH_IDENTITY_UNAVAILABLE" => "O CLI não conseguiu identificar uma conta Windows compatível com login integrado. Uma conta de trabalho apenas registrada pode não ser suficiente. Use Navegador.",
            "AUTH_WINDOW_UNAVAILABLE" => "O login Microsoft não encontrou uma janela de referência (WAM). O aplicativo interrompeu a troca automática para código de dispositivo. Tente novamente numa sessão Windows interativa ou escolha Código de dispositivo explicitamente.",
            "AUTH_FLOW_CHANGED" => "O CLI tentou mudar para código de dispositivo. A tentativa foi interrompida para respeitar sua escolha. Edite a conta se desejar usar esse fluxo.",
            "LOGIN_IN_PROGRESS" => "Já existe uma autenticação em andamento. Conclua ou cancele o login antes de editar a conta ou iniciar outra tentativa.",
            "EXECUTION_BLOCKED_BY_POLICY" => "O Windows bloqueou a execução pela política de segurança. Solicite autorização ao administrador; o aplicativo não tenta contornar esse bloqueio.",
            _ when e.Code.StartsWith("AUTH_PROVIDER_AADSTS", StringComparison.Ordinal) && e.Code[20..] is { Length: >= 4 and <= 12 } digits && digits.All(char.IsAsciiDigit) => $"O Microsoft Entra retornou AADSTS{e.Code[20..]}. Consulte esse código com o administrador. Nenhum token ou dado da conta é exposto neste diagnóstico.",
            "INVALID_PROXY_DESTINATION" => "Informe domínio sem protocolo/caminho, IP ou rede CIDR. Para subdomínios, use a opção do formulário.",
            "INVALID_PROXY_SETTINGS" or "INVALID_PROXY_RULE" => "Revise as regras, portas e limites do proxy local.",
            "PROXY_RESTART_REQUIRED" => "Pare o proxy antes de alterar limites e timeouts; depois, inicie-o novamente.",
            "TUNNEL_HAS_PROXY_RULES" => "Remova as regras de proxy que usam este túnel antes de excluí-lo.",
            "INVALID_SETTINGS" => "Revise os limites das configurações.",
            "AUTHENTICATION_REQUIRED" or "AUTHENTICATION_EXPIRED" => "Autentique novamente esta conta para continuar.",
            "CLI_ACCOUNT_CHANGED" => "O CLI oficial está autenticado com outra identidade. Autentique o perfil correto antes de continuar.",
            "DEVTUNNEL_CLI_UNAVAILABLE" => "Instale o CLI oficial devtunnel e disponibilize-o no PATH do agente.",
            "ACCOUNT_IN_USE" or "ACCOUNT_HAS_TUNNELS" => "Remova os perfis de túnel associados antes de remover a conta.",
            "VAULT_UNAVAILABLE" or "CREDENTIAL_VAULT_UNAVAILABLE" => "O cofre de credenciais não está disponível. Configure o cofre antes de autenticar.",
            "LOCAL_PORT_UNAVAILABLE" => "A porta local está ocupada. Escolha outra porta ou use o mapeamento automático.",
            "AUTH_CLIENT_ID_REQUIRED" => "Informe o Client ID de sua aplicação registrada no provedor, com device flow habilitado.",
            "ACCOUNT_IDENTITY_CHANGED" => "A conta autenticada é diferente deste perfil. Use a identidade original ou adicione outra conta.",
            "INVALID_PAC_DOMAIN" => "Use um domínio sem protocolo, caminho ou curinga, como example.org. Não repita o mesmo domínio.",
            "INVALID_PAC_SETTINGS" => "Revise a porta do PAC (1024 a 65535) e o limite de 100 regras.",
            "INVALID_PAC_RULE" => "Cada regra PAC precisa de um túnel e uma porta remota válidos.",
            "IPC_VERSION_MISMATCH" => "A interface e o agente estão em versões incompatíveis. Encerre o agente anterior e inicie o agente deste pacote. Suas contas e configurações serão preservadas.",
            "AGENT_ALREADY_RUNNING" => "Já existe um agente em execução para este usuário. Verifique se ele pertence ao mesmo pacote da interface.",
            "AGENT_EXECUTABLE_NOT_FOUND" => "O agente não foi encontrado neste pacote. Execute DevTunnelToolkit.Agent separadamente ou instale o pacote completo.",
            "AGENT_START_FAILED" or "AGENT_START_TIMEOUT" => "Não foi possível iniciar o agente local. Verifique o pacote e tente novamente. Agentes existentes não foram encerrados.",
            "DESKTOP_LOGIN_UNSUPPORTED" => "Este fluxo precisa de um Desktop e agente atualizados em Windows. Use Código de dispositivo para autenticar sem a interface.",
            "DUPLICATE_OR_EXCESSIVE_MAPPINGS" => "Cada porta remota pode ter um único mapeamento. Limite: 100.",
            "INVALID_RECONNECT_POLICY" => "Revise os intervalos e o limite de tentativas de reconexão.",
            _ when e.Code.Length is > 0 and <= 80 && e.Code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') => $"Não foi possível concluir a operação ({e.Code}).",
            _ => "Não foi possível concluir a operação. Consulte os eventos do agente."
        },
        IOException or System.Net.Sockets.SocketException => "Agente indisponível. Inicie o agente local e tente novamente.",
        _ => "Não foi possível concluir a operação. Consulte os eventos do agente."
    };

    private async Task WatchLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await foreach (var snapshot in gateway.WatchAsync(_lifetime.Token).ConfigureAwait(false))
                    _dispatch(() => Accept(snapshot));
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            catch (Exception ex) { _dispatch(() => Disconnected(ex)); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Accept(AgentSnapshot snapshot)
    {
        var first = !IsConnected;
        Snapshot = snapshot;
        ObserveActivities(snapshot);
        RequiresAgentUpdate = false;
        IsConnected = true;
        if (first) Message = "Agente conectado. As conexões continuam ativas ao fechar esta janela.";
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Disconnected(Exception error)
    {
        IsConnected = false;
        RequiresAgentUpdate = error is ToolkitException { Code: "IPC_VERSION_MISMATCH" };
        Message = FriendlyError(error);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelDesktopLogins();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
