using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App;

public sealed partial class MainWindow : Window
{
    private static readonly (AccountProvider Provider, string Label)[] ProviderModes =
    [
        (AccountProvider.MicrosoftCli, "Entra / Microsoft - CLI oficial"),
        (AccountProvider.GitHub, "GitHub - conta isolada"),
        (AccountProvider.GitHubCli, "GitHub - CLI oficial"),
        (AccountProvider.Microsoft, "Microsoft - app própria"),
        (AccountProvider.TunnelToken, "Token de túnel")
    ];

    private static string ProviderLabel(AccountProvider provider) => ProviderModes.First(mode => mode.Provider == provider).Label;
    private static AccountProvider SelectedProvider(ComboBox choice) => ProviderModes.First(mode => mode.Label == (string?)choice.SelectedItem).Provider;

    private readonly ToolkitViewModel _model;
    private readonly ContentControl _page = new();
    private readonly TextBlock _title = Text("Visão geral", 25, FontWeight.SemiBold);
    private readonly TextBlock _description = Text("Suas conexões, identidades e portas em um único lugar.");
    private readonly TextBlock _status = Text("Conectando…", 11);
    private readonly TextBlock _message = Text("Conectando ao agente local…", 12);
    private readonly TextBlock _elapsed = Text("", 11);
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 3, IsVisible = false };
    private readonly DispatcherTimer _activityTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _pacWarning = Text("", 12);
    private readonly Button _primary;
    private readonly Dictionary<string, Button> _navigation = [];
    private string _currentPage = "Dashboard";
    private string _theme = "";
    private string _logSearch = "";
    private string _logLevel = "Todos";
    private string _tunnelSearch = "";
    private string? _renderedPage;
    private string? _pageSignature;
    private string? _selectedNavigation;
    private int _pendingActions;

    public MainWindow() : this(CreateModel()) { }

    private static ToolkitViewModel CreateModel()
    {
        var gateway = new ToolkitGateway(Program.DataDirectory);
        var launcher = new AgentLauncher(gateway, Program.DataDirectory);
        return new ToolkitViewModel(gateway, action => Dispatcher.UIThread.Post(action), launcher.EnsureStartedAsync);
    }

    public MainWindow(ToolkitViewModel model)
    {
        _model = model;
        DataContext = model;
        Title = "DevTunnel Toolkit";
        Paint(this, IconProperty, "BrandWindowIcon");
        Width = 1280; Height = 830; MinWidth = 940; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Paint(this, BackgroundProperty, "Canvas");
        Paint(this, ForegroundProperty, "Text");
        var layout = new Grid { ColumnDefinitions = new("220,*") };
        layout.Children.Add(BuildSidebar());
        var main = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new(28, 26, 28, 16) };
        Grid.SetColumn(main, 1);
        var heading = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(0, 0, 0, 24) };
        var labels = new StackPanel { Spacing = 6 };
        labels.Children.Add(Text("CONNECTION WORKSPACE", 10, FontWeight.SemiBold, "Muted"));
        labels.Children.Add(_title);
        labels.Children.Add(_description);
        heading.Children.Add(labels);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(ActionButton("Atualizar", async () => await _model.RunAsync(_ => _model.RefreshAsync(), "Estado atualizado.", "Atualizando o estado do workspace…")));
        _primary = ActionButton("Novo túnel", NewForCurrentPageAsync, true);
        actions.Children.Add(_primary);
        Grid.SetColumn(actions, 1); heading.Children.Add(actions);
        main.Children.Add(heading);
        _pacWarning.TextWrapping = TextWrapping.Wrap;
        _pacWarning.Margin = new(0, 0, 0, 15);
        Grid.SetRow(_pacWarning, 1); main.Children.Add(_pacWarning);
        var scroll = new ScrollViewer { Content = _page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); main.Children.Add(scroll);
        _message.TextWrapping = TextWrapping.Wrap;
        Paint(_message, TextBlock.ForegroundProperty, "Muted");
        var activity = new StackPanel { Spacing = 5, Margin = new(0, 15, 0, 0) };
        AutomationProperties.SetName(_progress, "Operação em andamento");
        activity.Children.Add(_progress); activity.Children.Add(_message); activity.Children.Add(_elapsed);
        Grid.SetRow(activity, 3); main.Children.Add(activity);
        layout.Children.Add(main);
        Content = layout;
        model.StateChanged += (_, _) => RefreshView();
        model.PropertyChanged += (_, _) => UpdateActivity();
        _activityTimer.Tick += (_, _) => UpdateActivity();
        Opened += async (_, _) => await model.StartAsync();
        Closing += (_, _) => model.CancelDesktopLogins();
        Closed += (_, _) => { _activityTimer.Stop(); model.Dispose(); };
        RefreshView();
    }

    private Border BuildSidebar()
    {
        var grid = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new(18, 25, 18, 20) };
        var brand = new StackPanel { Spacing = 11, Margin = new(6, 0, 0, 31) };
        var brandImage = new Image { Width = 53, Height = 53, HorizontalAlignment = HorizontalAlignment.Left };
        Paint(brandImage, Image.SourceProperty, "BrandIcon"); brand.Children.Add(brandImage);
        brand.Children.Add(Text("DevTunnel", 23, FontWeight.Bold));
        brand.Children.Add(Text("TOOLKIT / DESKTOP", 10, FontWeight.SemiBold, "Accent"));
        grid.Children.Add(brand);
        var navigation = new StackPanel { Spacing = 7 };
        navigation.Children.Add(Text("WORKSPACE", 10, FontWeight.SemiBold, "Muted"));
        foreach (var name in new[] { "Dashboard", "Contas", "Conexões", "Serviços", "DevTunnel", "Portas", "Proxy e PAC", "Logs", "Configurações" })
        {
            var button = ActionButton(name, () => { _currentPage = name; RefreshView(); return Task.CompletedTask; });
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Margin = new(0, name == "Dashboard" ? 9 : 0, 0, 0);
            button.Padding = new(14, 11);
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11 };
            label.Children.Add(NavigationIcon(name));
            label.Children.Add(Text(name, 13));
            button.Content = label;
            AutomationProperties.SetName(button, $"Abrir {name}");
            _navigation[name] = button;
            navigation.Children.Add(button);
        }
        Grid.SetRow(navigation, 1); grid.Children.Add(navigation);
        var footer = new StackPanel { Spacing = 8 };
        footer.Children.Add(_status);
        footer.Children.Add(Text("Agente independente por usuário", 10, color: "Muted"));
        footer.Children.Add(Text(VersionLabel(), 10, color: "Muted"));
        Grid.SetRow(footer, 2); grid.Children.Add(footer);
        var border = new Border { Child = grid, BorderThickness = new(0, 0, 1, 0) };
        Paint(border, Border.BackgroundProperty, "Surface"); Paint(border, Border.BorderBrushProperty, "Stroke");
        return border;
    }

    private void RefreshView()
    {
        _message.Text = _model.Message;
        _status.Text = _model.IsStartingAgent ? "○  Iniciando agente…" : _model.IsConnected ? "●  Agente conectado" : "○  Agente indisponível";
        Paint(_status, TextBlock.ForegroundProperty, _model.IsConnected ? "Success" : "Muted");
        _primary.IsEnabled = _model.IsConnected && !_model.IsBusy && !_primary.Classes.Contains("working");
        if (_selectedNavigation != _currentPage)
        {
            _selectedNavigation = _currentPage;
            foreach (var (name, button) in _navigation)
            {
                Paint(button, BackgroundProperty, name == _currentPage ? "Subtle" : "Surface");
                Paint(button, ForegroundProperty, name == _currentPage ? "Accent" : "Text");
                Paint(button, BorderBrushProperty, name == _currentPage ? "Accent" : "Surface");
                // Keep identical geometry for selected, idle and hovered buttons.
                button.BorderThickness = new Thickness(3, 0, 0, 0);
            }
        }
        _title.Text = _currentPage switch { "Dashboard" => "Visão geral", "Portas" => "Portas e mapeamentos", _ => _currentPage };
        _description.Text = _currentPage switch
        {
            "Contas" => "Identidades isoladas. Autenticação individual por provedor.",
            "Conexões" => "Acesse túneis remotos neste computador, sem assumir sua publicação.",
            "DevTunnel" => "Gerencie os túneis da conta e publique serviços ou portas deste Desktop.",
            "Serviços" => "Proxies HTTP/CONNECT e SOCKS5 independentes, com portas e políticas próprias.",
            "Portas" => "Endereços locais, portas remotas e mapeamentos efetivos.",
            "Proxy e PAC" => "Roteamento por destino, proxy local e configuração automática.",
            "Logs" => "Eventos operacionais do agente, atualizados ao vivo.",
            "Configurações" => "Personalize a experiência e os limites deste dispositivo.",
            _ => "Suas conexões, identidades e portas em um único lugar."
        };
        if (!_primary.Classes.Contains("working"))
        {
            _primary.Content = _currentPage switch { "Contas" => "Adicionar conta", "DevTunnel" => _devTunnelTab == 1 ? "Nova publicação" : "Criar túnel remoto", "Serviços" => "Novo serviço", "Configurações" => "Editar configurações", "Proxy e PAC" => "Editar proxy", "Logs" => "Copiar eventos", _ => "Nova conexão" };
            AutomationProperties.SetName(_primary, _primary.Content?.ToString());
        }
        var snapshot = _model.Snapshot;
        _pacWarning.IsVisible = snapshot?.PacErrorCode is not null;
        if (snapshot?.PacErrorCode is { } pacError)
            _pacWarning.Text = $"PAC indisponível: {ToolkitViewModel.FriendlyError(new ToolkitException(pacError))} Revise a porta em Configurações. Os túneis continuam independentes.";
        if (snapshot is null || !_model.IsConnected)
        {
            _pageSignature = null; _renderedPage = null;
            var empty = new StackPanel { Spacing = 16 };
            if (_model.IsStartingAgent)
            {
                empty.Children.Add(Text("Iniciando agente local", 22, FontWeight.SemiBold));
                empty.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3 });
                empty.Children.Add(Paragraph("Verificando o agente deste workspace. Uma instância compatível será reutilizada automaticamente."));
            }
            else if (_model.RequiresAgentUpdate)
            {
                empty.Children.Add(Text("Atualize o agente local", 22, FontWeight.SemiBold));
                empty.Children.Add(Paragraph(_model.Message));
                empty.Children.Add(Paragraph("No Windows, encerre DevTunnelToolkit.Agent.exe no Gerenciador de Tarefas. Isso interrompe as conexões gerenciadas por ele. Depois, use Iniciar agente local nesta janela. Não encerre o CLI devtunnel."));
            }
            else
            {
                empty.Children.Add(Text("Inicie seu workspace", 22, FontWeight.SemiBold));
                empty.Children.Add(Paragraph("O Desktop inicia o agente automaticamente e mantém as conexões em segundo plano. Se a inicialização falhou, verifique o pacote e tente novamente."));
                empty.Children.Add(ActionButton("Iniciar agente local", LaunchAgentAsync, true));
                empty.Children.Add(Paragraph("Em desenvolvimento, execute DevTunnelToolkit.Agent separadamente. A interface se reconecta automaticamente."));
            }
            _page.Content = Card(empty);
            return;
        }
        if (_theme != snapshot.Settings.Theme)
        {
            _theme = snapshot.Settings.Theme;
            (Avalonia.Application.Current as ToolkitApp)?.ApplyTheme(_theme);
        }
        UpdateActivity();
        // Preserve control identity (hover, focus, scroll and pending button state).
        // Snapshots arrive each second; only visible state changes need rendering.
        if (_renderedPage == _currentPage && (_model.IsBusy || _pendingActions > 0)) return;
        // Do not replace a focused filter field while a streamed snapshot arrives.
        if (_renderedPage == _currentPage && _page.IsKeyboardFocusWithin && FocusManager?.GetFocusedElement() is TextBox) return;
        var signature = PageSignature(snapshot);
        if (_renderedPage == _currentPage && _pageSignature == signature) return;
        _renderedPage = _currentPage; _pageSignature = signature;
        _page.Content = _currentPage switch
        {
            "Contas" => AccountsPage(snapshot),
            "Conexões" => TunnelsPage(snapshot),
            "DevTunnel" => DevTunnelPage(snapshot),
            "Serviços" => ServicesPage(snapshot),
            "Portas" => PortsPage(snapshot),
            "Proxy e PAC" => ProxyPage(snapshot),
            "Logs" => LogsPage(snapshot),
            "Configurações" => SettingsPage(snapshot),
            _ => DashboardPage(snapshot)
        };
    }

    private string PageSignature(AgentSnapshot snapshot) => JsonSerializer.Serialize(_currentPage switch
    {
        "Contas" => (object)new { snapshot.Accounts, snapshot.Logins, snapshot.Tunnels, DesktopLogins = snapshot.Accounts.Select(a => _model.IsDesktopLoginActive(a.Id)), _model.DiscoveryAccountId, _model.DiscoveredTunnels },
        "Conexões" => new { snapshot.Accounts, snapshot.Tunnels, snapshot.Connections, _model.DiscoveryAccountId, _model.DiscoveredTunnels },
        "DevTunnel" => new { snapshot.Accounts, _model.RemoteAccountId, _model.RemoteTunnels, snapshot.Hosts, snapshot.HostStatuses, snapshot.Services, snapshot.ServiceStatuses, snapshot.DiagnosticEcho, snapshot.Tunnels, snapshot.Connections, _devTunnelTab },
        "Serviços" => new { snapshot.Services, snapshot.ServiceStatuses, snapshot.Hosts, snapshot.HostStatuses },
        "Portas" => new { snapshot.Tunnels, snapshot.Connections },
        "Proxy e PAC" => new { snapshot.Settings, snapshot.Tunnels, snapshot.ProxyErrorCode, snapshot.PacErrorCode },
        "Configurações" => new { snapshot.Settings, snapshot.Capabilities },
        "Logs" => new { snapshot.Logs },
        _ => new { snapshot.Accounts, snapshot.Tunnels, snapshot.Connections, Logs = snapshot.Logs.TakeLast(5) }
    });

    private void UpdateActivity()
    {
        var activity = _model.WorkspaceActivity;
        _message.Text = activity?.Description ?? _model.Message;
        _primary.IsEnabled = _model.IsConnected && !_model.IsBusy && !_primary.Classes.Contains("working");
        _progress.IsVisible = activity?.Running == true;
        _elapsed.Text = activity?.Timing(DateTimeOffset.UtcNow) ?? "";
        _elapsed.IsVisible = _elapsed.Text.Length > 0;
        if (activity?.Running == true)
        {
            if (!_activityTimer.IsEnabled) _activityTimer.Start();
        }
        else _activityTimer.Stop();
        if (_proxyClients is not null) _proxyClients.Text = (_model.Snapshot?.ProxyActiveConnections ?? 0).ToString();
    }

    private Control DashboardPage(AgentSnapshot snapshot)
    {
        var stack = PageStack();
        var metrics = new UniformGrid { Columns = 4 };
        metrics.Children.Add(Metric("TÚNEIS", snapshot.Tunnels.Length.ToString(), "Perfis neste dispositivo"));
        metrics.Children.Add(Metric("CONECTADOS", snapshot.Connections.Count(c => c.State == ConnectionState.Connected).ToString(), "Relays ativos"));
        metrics.Children.Add(Metric("CONTAS", snapshot.Accounts.Length.ToString(), "Identidades configuradas"));
        metrics.Children.Add(Metric("PORTAS LOCAIS", snapshot.Connections.Sum(c => c.Ports?.Length ?? 0).ToString(), "Mapeamentos efetivos"));
        stack.Children.Add(metrics);
        stack.Children.Add(Section("Conexões do workspace", "O Tunnel ID identifica a conexão. Seus perfis permanecem neste dispositivo.", TunnelRows(snapshot)));
        var recent = new StackPanel { Spacing = 7 };
        foreach (var entry in snapshot.Logs.TakeLast(5).Reverse()) recent.Children.Add(LogRow(entry));
        if (recent.Children.Count == 0) recent.Children.Add(Paragraph("Os eventos aparecem aqui após as primeiras operações."));
        stack.Children.Add(Section("Atividade recente", "Estado publicado pelo agente local.", recent));
        return stack;
    }

    private Control TunnelsPage(AgentSnapshot snapshot)
    {
        var stack = PageStack();
        var tunnels = new StackPanel { Spacing = 14 };
        var search = new TextBox { Text = _tunnelSearch, PlaceholderText = "Buscar Tunnel ID, apelido ou conta…" };
        AutomationProperties.SetName(search, "Buscar túneis");
        var result = new ContentControl();
        void FilterTunnels()
        {
            var source = _model.Snapshot ?? snapshot;
            result.Content = TunnelRows(source with
            {
                Tunnels = source.Tunnels.Where(t =>
                t.CanonicalId.Contains(_tunnelSearch, StringComparison.OrdinalIgnoreCase) ||
                t.Alias.Contains(_tunnelSearch, StringComparison.OrdinalIgnoreCase) ||
                (source.Accounts.FirstOrDefault(a => a.Id == t.AccountId)?.DisplayName.Contains(_tunnelSearch, StringComparison.OrdinalIgnoreCase) ?? false)).ToArray()
            });
        }
        search.TextChanged += (_, _) => { _tunnelSearch = search.Text ?? ""; FilterTunnels(); };
        FilterTunnels(); tunnels.Children.Add(search); tunnels.Children.Add(result);
        stack.Children.Add(Section("Seus túneis", "Conexões TCP privadas com mapeamentos independentes.", tunnels));
        if (snapshot.Accounts.Any(a => a.Provider != AccountProvider.TunnelToken))
        {
            var discover = new StackPanel { Spacing = 10 };
            foreach (var account in snapshot.Accounts.Where(a => a.Provider != AccountProvider.TunnelToken))
            {
                var row = new Grid { ColumnDefinitions = new("*,Auto") };
                row.Children.Add(Text($"{account.DisplayName} · {ProviderLabel(account.Provider)}", 13, FontWeight.Medium));
                var action = ActionButton("Descobrir túneis", async () => await _model.DiscoverAsync(account.Id));
                Grid.SetColumn(action, 1); row.Children.Add(action); discover.Children.Add(row);
                discover.Children.Add(new ActivityPanel(_model, () => _model.AccountActivity(account.Id)));
            }
            stack.Children.Add(Section("Descoberta remota", "Consulte os túneis acessíveis a cada conta.", discover));
        }
        if (_model.DiscoveryAccountId is not null) stack.Children.Add(DiscoveryResults(snapshot));
        return stack;
    }

    private Control TunnelRows(AgentSnapshot snapshot)
    {
        var rows = new StackPanel { Spacing = 10 };
        if (snapshot.Tunnels.Length == 0)
        {
            rows.Children.Add(Paragraph("Nenhum túnel configurado. Adicione uma conta, descubra seus túneis ou cadastre um Tunnel ID."));
            rows.Children.Add(ActionButton(snapshot.Accounts.Length == 0 ? "Adicionar primeira conta" : "Cadastrar túnel", async () =>
            { if (snapshot.Accounts.Length == 0) await EditAccountAsync(); else await EditTunnelAsync(); }, true));
        }
        foreach (var tunnel in snapshot.Tunnels)
        {
            var state = snapshot.Connections.FirstOrDefault(c => c.ProfileId == tunnel.Id);
            var row = new Grid { ColumnDefinitions = new("*,140,Auto"), RowDefinitions = new("Auto,Auto"), Margin = new(0, 5) };
            var identity = new StackPanel { Spacing = 5 };
            identity.Children.Add(Text(tunnel.CanonicalId, 14, FontWeight.SemiBold));
            identity.Children.Add(Text($"{tunnel.Alias} · {snapshot.Accounts.FirstOrDefault(a => a.Id == tunnel.AccountId)?.DisplayName ?? "Conta indisponível"}", 11, color: "Muted"));
            row.Children.Add(identity);
            var stateText = Text(StateLabel(state?.State ?? ConnectionState.Stopped), 12, FontWeight.SemiBold, state?.State == ConnectionState.Connected ? "Success" : "Muted");
            stateText.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(stateText, 1); row.Children.Add(stateText);
            var actions = new WrapPanel { Orientation = Orientation.Horizontal };
            var pending = state is not null && ToolkitViewModel.IsConnectionPending(state.State);
            var connected = state?.State == ConnectionState.Connected || pending;
            var connect = ActionButton(pending ? "Cancelar conexão" : connected ? "Parar" : "Conectar", async () => await _model.ActionAsync(connected ? "tunnel.stop" : "tunnel.connect", tunnel.Id), !connected);
            connect.IsEnabled = state?.State != ConnectionState.Stopping;
            actions.Children.Add(connect);
            actions.Children.Add(ActionButton("Editar", () => EditTunnelAsync(tunnel)));
            Grid.SetColumn(actions, 2); row.Children.Add(actions);
            var info = new StackPanel { Spacing = 4, Margin = new(0, 9, 0, 0) };
            info.Children.Add(new ActivityPanel(_model, () => _model.TunnelActivity(tunnel.Id)));
            info.Children.Add(Text(state?.Ports is { Length: > 0 } ports ? string.Join("  ·  ", ports.Select(p => $"{p.LocalAddress}:{p.LocalPort} → {p.RemotePort}")) : "Nenhuma porta local ativa", 11, color: "Muted"));
            if (state?.ErrorCode is { } code) info.Children.Add(Text($"Atenção: {code}", 11));
            if (state?.NextRetryAt is { } retry) info.Children.Add(Text($"Tentativa {state.RetryCount} · próxima {FormatDate(retry)}", 11, color: "Muted"));
            if (state?.TunnelExpiresAt is { } expires) info.Children.Add(Text($"Recurso remoto expira em {FormatDate(expires)}", 11, color: "Muted"));
            Grid.SetRow(info, 1); Grid.SetColumnSpan(info, 3); row.Children.Add(info);
            rows.Children.Add(Card(row, 14));
        }
        return rows;
    }

    private Control AccountsPage(AgentSnapshot snapshot)
    {
        var stack = PageStack();
        if (!snapshot.Capabilities.CredentialVault) stack.Children.Add(Card(Paragraph("Cofre do aplicativo indisponível para contas SDK e tokens importados. O modo CLI usa o cofre próprio do devtunnel, que também precisa estar disponível no sistema.")));
        if (snapshot.Accounts.Length == 0) stack.Children.Add(Section("Adicione uma identidade", "Microsoft corporativo e GitHub", Paragraph("Microsoft via CLI não pede Client ID. O CLI oficial usa uma sessão compartilhada do sistema; contas SDK mantêm credenciais isoladas.")));
        foreach (var account in snapshot.Accounts)
        {
            var content = new StackPanel { Spacing = 12 };
            var login = snapshot.Logins.FirstOrDefault(l => l.AccountId == account.Id);
            var loginActive = _model.IsDesktopLoginActive(account.Id) || login?.State is "Starting" or "Pending" or "AwaitingUser";
            content.Children.Add(Text($"{account.DisplayName} · {ProviderLabel(account.Provider)}", 17, FontWeight.SemiBold));
            content.Children.Add(Paragraph(account.Username ?? "Identidade ainda não autenticada"));
            content.Children.Add(KeyValue("Estado", AccountStatus(account.Status)));
            if (account.Provider is AccountProvider.MicrosoftCli or AccountProvider.GitHubCli)
                content.Children.Add(KeyValue("Fluxo de login", account.CliLoginMode switch { CliLoginMode.Browser => "Navegador", CliLoginMode.WindowsIntegrated => "Windows integrado", _ => "Código de dispositivo" }));
            if (login?.StartedAt is { } started)
            {
                content.Children.Add(KeyValue("Tentativa iniciada em", FormatDate(started)));
                if (login.RequestedMode is { } mode) content.Children.Add(KeyValue("Fluxo da tentativa", mode switch { CliLoginMode.Browser => "Navegador / janela Microsoft", CliLoginMode.WindowsIntegrated => "Windows integrado", _ => "Código de dispositivo" }));
            }
            content.Children.Add(KeyValue("Autenticada em", FormatDate(account.AuthenticatedAt)));
            content.Children.Add(KeyValue("Token de acesso expira em", account.ExpiresAt is { } expiry ? FormatDate(expiry) : "Não informado pelo provedor"));
            content.Children.Add(Text(account.Provider is AccountProvider.MicrosoftCli or AccountProvider.GitHubCli ? "O CLI não informa a validade exata. Alterar o login do CLI pode exigir autenticar novamente este perfil." : "A política da conta pode exigir novo login antes dessa data.", 11, color: "Muted"));
            content.Children.Add(KeyValue("Última validação", FormatDate(account.LastValidatedAt)));
            content.Children.Add(KeyValue("Túneis associados", snapshot.Tunnels.Count(t => t.AccountId == account.Id).ToString()));
            content.Children.Add(new ActivityPanel(_model, () => _model.AccountActivity(account.Id)));
            if (login?.Challenge is { } challenge)
            {
                var challengePanel = new StackPanel { Spacing = 9 };
                challengePanel.Children.Add(Text("Conclua o login no provedor", 14, FontWeight.SemiBold));
                challengePanel.Children.Add(new SelectableTextBlock { Text = challenge.UserCode, FontSize = 26, FontWeight = FontWeight.Bold });
                challengePanel.Children.Add(Text(challenge.VerificationUri, 12));
                challengePanel.Children.Add(Text(challenge.ExpiresAt is { } challengeExpiry ? $"Código válido até {FormatDate(challengeExpiry)}" : "Validade do código não informada pelo CLI", 11, color: "Muted"));
                var loginButtons = new WrapPanel();
                if (ToolkitViewModel.IsSafeLoginUri(challenge.VerificationUri)) loginButtons.Children.Add(ActionButton("Abrir navegador", () => OpenLoginAsync(challenge.VerificationUri), true));
                loginButtons.Children.Add(ActionButton("Copiar código", async () => { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(challenge.UserCode); }));
                loginButtons.Children.Add(ActionButton("Cancelar login", async () => await _model.ActionAsync("account.cancel", account.Id)));
                challengePanel.Children.Add(loginButtons);
                content.Children.Add(Card(challengePanel, 14));
            }
            else if (loginActive)
            {
                content.Children.Add(ActionButton("Cancelar login", async () => await _model.ActionAsync("account.cancel", account.Id)));
            }
            else if (login?.ErrorCode is { } error)
            {
                content.Children.Add(Paragraph($"Login: {ToolkitViewModel.FriendlyError(new ToolkitException(error))}"));
                content.Children.Add(Text($"Diagnóstico: {error}", 11, color: "Muted"));
            }
            var actions = new WrapPanel();
            if (account.Provider != AccountProvider.TunnelToken)
                actions.Children.Add(ActionButton("Autenticar", async () => await _model.ActionAsync("account.login", account.Id), true));
            actions.Children.Add(ActionButton("Validar", async () => await _model.ActionAsync("account.validate", account.Id)));
            if (account.Provider != AccountProvider.TunnelToken)
                actions.Children.Add(ActionButton("Descobrir túneis", async () => await _model.DiscoverAsync(account.Id)));
            actions.Children.Add(ActionButton("Editar", () => EditAccountAsync(account)));
            actions.Children.Add(ActionButton("Remover", async () => { if (await ConfirmAsync("Remover conta", $"Remover {account.DisplayName} e suas credenciais deste dispositivo? Primeiro remova os perfis de túnel associados.")) await _model.ActionAsync("account.remove", account.Id); }));
            // Cancellation remains available in the login panel. Do not allow an
            // edit or a second login to relabel/replace an in-flight attempt.
            foreach (var action in actions.Children.OfType<Button>()) action.IsEnabled = !loginActive;
            content.Children.Add(actions);
            stack.Children.Add(Card(content));
        }
        if (_model.DiscoveryAccountId is not null) stack.Children.Add(DiscoveryResults(snapshot));
        return stack;
    }

    private Control DiscoveryResults(AgentSnapshot snapshot)
    {
        var content = new StackPanel { Spacing = 10 };
        foreach (var tunnel in _model.DiscoveredTunnels)
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto") };
            var details = new StackPanel { Spacing = 5 };
            details.Children.Add(Text(tunnel.CanonicalId, 14, FontWeight.SemiBold));
            details.Children.Add(Text($"{tunnel.Name ?? "Sem nome"} · {tunnel.Ports.Length} portas · host {(tunnel.HostOnline is true ? "online" : tunnel.HostOnline is false ? "offline" : "desconhecido")}", 11, color: "Muted"));
            details.Children.Add(Text(string.Join(" · ", tunnel.Ports.Select(p => $"{p.PortNumber}/{p.Protocol ?? "TCP"}")), 11));
            row.Children.Add(details);
            var adopted = snapshot.Tunnels.Any(t => t.CanonicalId == tunnel.CanonicalId && t.AccountId == _model.DiscoveryAccountId);
            var add = ActionButton(adopted ? "Já adicionado" : "Configurar", () => EditTunnelAsync(new TunnelProfile
            {
                AccountId = _model.DiscoveryAccountId!,
                TunnelId = tunnel.TunnelId,
                ClusterId = tunnel.ClusterId,
                Alias = tunnel.Name ?? "",
                Mappings = tunnel.Ports.Select(p => new PortMapping { RemotePort = p.PortNumber, LocalPort = p.PortNumber }).ToArray()
            }), !adopted);
            add.IsEnabled = !adopted; Grid.SetColumn(add, 1); row.Children.Add(add); content.Children.Add(Card(row, 12));
        }
        if (_model.DiscoveredTunnels.Length == 0) content.Children.Add(Paragraph("Nenhum túnel retornado para esta identidade."));
        var owner = snapshot.Accounts.FirstOrDefault(a => a.Id == _model.DiscoveryAccountId)?.DisplayName ?? "Conta";
        return Section($"Resultado da descoberta · {owner}", "Configurar cria somente um perfil local.", content);
    }

    private Control PortsPage(AgentSnapshot snapshot)
    {
        var content = new StackPanel { Spacing = 12 };
        foreach (var tunnel in snapshot.Tunnels)
        {
            var ports = new StackPanel { Spacing = 9 };
            var effective = snapshot.Connections.FirstOrDefault(c => c.ProfileId == tunnel.Id)?.Ports ?? [];
            foreach (var mapping in tunnel.Mappings)
            {
                var actual = effective.FirstOrDefault(p => p.RemotePort == mapping.RemotePort);
                ports.Children.Add(KeyValue($"TCP {mapping.RemotePort}", $"{mapping.LocalAddress}:{(actual?.LocalPort.ToString() ?? "inativo")} · {ModeLabel(mapping.Mode)}"));
            }
            foreach (var mapping in effective.Where(p => tunnel.Mappings.All(m => m.RemotePort != p.RemotePort)))
                ports.Children.Add(KeyValue($"TCP {mapping.RemotePort}", $"{mapping.LocalAddress}:{mapping.LocalPort} · Descoberto automaticamente"));
            if (ports.Children.Count == 0) ports.Children.Add(Paragraph(tunnel.AutoDiscoverPorts ? "As portas serão descobertas ao conectar." : "Nenhum mapeamento configurado."));
            ports.Children.Add(new ActivityPanel(_model, () => _model.TunnelActivity(tunnel.Id)));
            ports.Children.Add(ActionButton("Editar mapeamentos", () => EditTunnelAsync(tunnel)));
            content.Children.Add(Section(tunnel.CanonicalId, "Conexões locais restritas ao loopback.", ports));
        }
        if (content.Children.Count == 0) content.Children.Add(Card(Paragraph("Cadastre um túnel para configurar seus mapeamentos de portas.")));
        return content;
    }

    private Control LogsPage(AgentSnapshot snapshot)
    {
        var content = new StackPanel { Spacing = 14 };
        var filters = new Grid { ColumnDefinitions = new("*,150,Auto") };
        var query = new TextBox { Text = _logSearch, PlaceholderText = "Buscar código, mensagem ou perfil…", Margin = new(0, 0, 10, 0) };
        AutomationProperties.SetName(query, "Buscar eventos");
        var level = new ComboBox { ItemsSource = new[] { "Todos", "Information", "Warning", "Error", "Debug" }, SelectedItem = _logLevel, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(level, "Severidade dos eventos");
        filters.Children.Add(query); Grid.SetColumn(level, 1); filters.Children.Add(level); content.Children.Add(filters);
        if (StorageProvider.CanSave)
        {
            var export = ActionButton("Exportar arquivo", ExportLogsAsync);
            export.Margin = new(10, 0, 0, 0); Grid.SetColumn(export, 2); filters.Children.Add(export);
        }
        var rows = new StackPanel { Spacing = 8 }; content.Children.Add(rows);
        void FilterLogs()
        {
            rows.Children.Clear();
            foreach (var entry in (_model.Snapshot ?? snapshot).Logs.Reverse().Where(l =>
                (_logLevel == "Todos" || l.Level.Equals(_logLevel, StringComparison.OrdinalIgnoreCase)) &&
                (l.Code.Contains(_logSearch, StringComparison.OrdinalIgnoreCase) || l.Message.Contains(_logSearch, StringComparison.OrdinalIgnoreCase) || (l.ProfileId?.Contains(_logSearch, StringComparison.OrdinalIgnoreCase) ?? false))))
                rows.Children.Add(LogRow(entry));
            if (rows.Children.Count == 0) rows.Children.Add(Paragraph("Nenhum evento corresponde ao filtro."));
        }
        query.TextChanged += (_, _) => { _logSearch = query.Text ?? ""; FilterLogs(); };
        level.SelectionChanged += (_, _) => { _logLevel = level.SelectedItem as string ?? "Todos"; FilterLogs(); };
        FilterLogs();
        return Section("Eventos recentes", $"{snapshot.Logs.Length} eventos. O agente remove segredos antes de publicar os logs.", content);
    }

    private Control SettingsPage(AgentSnapshot snapshot)
    {
        var stack = PageStack();
        var settings = new StackPanel { Spacing = 12 };
        settings.Children.Add(KeyValue("Tema", snapshot.Settings.Theme));
        settings.Children.Add(KeyValue("Fechar janela", snapshot.Settings.CloseToTray ? "Manter na bandeja, quando disponível" : "Fechar interface e manter Agent"));
        settings.Children.Add(ActionButton("Encerrar tudo", async () =>
        {
            if (await ConfirmShutdownAsync() && Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime) lifetime.Shutdown();
        }));
        settings.Children.Add(KeyValue("Retenção de logs", $"{snapshot.Settings.LogRetentionDays} dias"));
        settings.Children.Add(KeyValue("Conexões simultâneas", snapshot.Settings.MaxConnections.ToString()));
        settings.Children.Add(KeyValue("Intervalo de descoberta", $"{snapshot.Settings.DiscoveryIntervalSeconds} segundos"));
        settings.Children.Add(KeyValue("Iniciar com a sessão", snapshot.Settings.StartWithUserSession ? "Ativado" : "Desativado"));
        settings.Children.Add(KeyValue("PAC local", snapshot.Settings.Pac.Enabled ? $"http://127.0.0.1:{snapshot.Settings.Pac.Port}/proxy.pac" : "Desativado"));
        settings.Children.Add(KeyValue("Regras PAC", snapshot.Settings.Pac.Rules.Length.ToString()));
        if (snapshot.Settings.Pac.Enabled)
            settings.Children.Add(ActionButton("Copiar URL do PAC", async () => { if (Clipboard is { } clipboard) await clipboard.SetTextAsync($"http://127.0.0.1:{snapshot.Settings.Pac.Port}/proxy.pac"); }));
        stack.Children.Add(Section("Preferências locais", "Configurações persistidas por usuário.", settings));
        var capabilities = new StackPanel { Spacing = 12 };
        capabilities.Children.Add(KeyValue("Sistema operacional", snapshot.Capabilities.OperatingSystem));
        capabilities.Children.Add(KeyValue("Cofre de credenciais", Capability(snapshot.Capabilities.CredentialVault)));
        capabilities.Children.Add(KeyValue("Inicialização por usuário", Capability(snapshot.Capabilities.UserStartup)));
        capabilities.Children.Add(KeyValue("Aplicar proxy no sistema", Capability(snapshot.Capabilities.SystemProxy)));
        capabilities.Children.Add(KeyValue("Exposição para WSL", Capability(snapshot.Capabilities.WslExposure)));
        if (snapshot.Capabilities.Detail is { } detail) capabilities.Children.Add(Paragraph(detail));
        stack.Children.Add(Section("Capacidades deste dispositivo", "As integrações disponíveis dependem do sistema e da instalação.", capabilities));
        return stack;
    }

    private async Task NewForCurrentPageAsync()
    {
        switch (_currentPage)
        {
            case "Contas": await EditAccountAsync(); break;
            case "DevTunnel": if (_devTunnelTab == 1) await EditHostAsync(); else await NewRemoteTunnelAsync(); break;
            case "Serviços": await EditServiceAsync(); break;
            case "Configurações": await EditSettingsAsync(); break;
            case "Proxy e PAC": await EditProxyAsync(); break;
            case "Logs":
                if (Clipboard is { } clipboard && _model.Snapshot is { } snapshot)
                    await _model.RunAsync(_ => clipboard.SetTextAsync(string.Join(Environment.NewLine, snapshot.Logs.Select(l => $"{l.Timestamp:O} {l.Level} {l.Code} {l.Message}"))),
                        "Eventos copiados.", "Copiando eventos para a área de transferência…", refreshAfter: false);
                break;
            default: await EditTunnelAsync(); break;
        }
    }

    private async Task ExportLogsAsync()
    {
        if (_model.Snapshot is not { } snapshot) return;
        var selected = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Exportar eventos do DevTunnel Toolkit",
            SuggestedFileName = $"devtunnel-toolkit-logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log",
            DefaultExtension = "log",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Eventos em texto") { Patterns = ["*.log"] }]
        });
        if (selected is null) return;
        using (selected)
        {
            await _model.RunAsync(async token =>
            {
                await using var stream = await selected.OpenWriteAsync();
                stream.SetLength(0);
                await using var writer = new StreamWriter(stream);
                foreach (var entry in snapshot.Logs) await writer.WriteLineAsync($"{entry.Timestamp:O} {entry.Level} {entry.Code} {entry.Message}".AsMemory(), token);
            }, "Eventos exportados.", "Gravando o arquivo de eventos…", refreshAfter: false);
        }
    }

    private async Task EditAccountAsync(AccountProfile? account = null)
    {
        var existing = account ?? new AccountProfile { Provider = AccountProvider.MicrosoftCli, CliLoginMode = CliLoginMode.Browser };
        var (dialog, form, footer) = Dialog(account is null ? "Adicionar conta" : "Editar conta", 620);
        var name = Field(form, "Nome do perfil", existing.DisplayName, "Um nome local para reconhecer esta identidade.");
        var provider = Choice(form, "Modo de autenticação", existing.Provider == AccountProvider.TunnelToken ? [ProviderLabel(AccountProvider.TunnelToken)] : ProviderModes.Where(mode => mode.Provider != AccountProvider.TunnelToken).Select(mode => mode.Label).ToArray(), ProviderLabel(existing.Provider));
        provider.IsEnabled = account is null;
        var loginModes = new[] { "Código de dispositivo", "Navegador (Entra / Microsoft ou GitHub)", "Windows integrado (somente Windows)" };
        var cliLoginMode = Choice(form, "Fluxo de login do CLI", loginModes, loginModes[(int)existing.CliLoginMode]);
        var loginGuidance = Paragraph(""); form.Children.Add(loginGuidance);
        var guidance = Paragraph(""); form.Children.Add(guidance);
        var client = Field(form, "Application / Client ID", existing.ClientId, "Somente no modo Microsoft com registro próprio. GitHub SDK usa o identificador oficial.");
        var tenant = Field(form, "Tenant Microsoft", existing.Provider == AccountProvider.Microsoft ? existing.TenantId ?? "organizations" : "", "Somente no modo Microsoft com registro próprio.");
        var scope = Field(form, "Escopo (opcional)", existing.Scope, "Somente para autenticação SDK com registro próprio.");
        void UpdateMode()
        {
            var mode = SelectedProvider(provider);
            var sdk = mode is AccountProvider.Microsoft or AccountProvider.GitHub;
            var useCli = mode is AccountProvider.MicrosoftCli or AccountProvider.GitHubCli;
            ((Control)cliLoginMode.Parent!).IsVisible = loginGuidance.IsVisible = useCli;
            if (mode == AccountProvider.GitHubCli && cliLoginMode.SelectedIndex == (int)CliLoginMode.WindowsIntegrated) cliLoginMode.SelectedIndex = (int)CliLoginMode.DeviceCode;
            loginGuidance.Text = (CliLoginMode)cliLoginMode.SelectedIndex switch
            {
                CliLoginMode.Browser => "Abre o navegador ou a janela nativa Microsoft (WAM) pelo CLI oficial. Não exige Client ID próprio. Uma sessão já válida no CLI pode ser reutilizada.",
                CliLoginMode.WindowsIntegrated => "Login silencioso com a identidade Windows do Agent. Apenas registrar uma conta de trabalho no Windows não garante suporte: depende do ingresso no domínio/Entra, da conta e das políticas. Se houver interação/MFA, use Navegador. Não troca de fluxo automaticamente.",
                _ => "Mostra um código para concluir o login no navegador, inclusive em outro dispositivo."
            };
            ((Control)client.Parent!).IsVisible = sdk;
            ((Control)tenant.Parent!).IsVisible = mode == AccountProvider.Microsoft;
            ((Control)scope.Parent!).IsVisible = sdk;
            guidance.Text = mode switch
            {
                AccountProvider.MicrosoftCli or AccountProvider.GitHubCli => "Usa o CLI oficial instalado no dispositivo. Não requer Client ID próprio. O cofre do CLI é compartilhado com outros aplicativos; antes de cada operação verificamos a identidade selecionada. Trocar a conta ativa do CLI pode interromper novas conexões deste perfil.",
                AccountProvider.Microsoft => "Modo avançado: use seu Client ID público e MSAL para manter esta conta isolada no cofre do aplicativo.",
                AccountProvider.GitHub => "O SDK usa o fluxo oficial de device code com credencial isolada por conta.",
                _ => "Credencial de túnel fornecida separadamente e protegida pelo cofre local."
            };
        }
        provider.SelectionChanged += (_, _) => UpdateMode();
        cliLoginMode.SelectionChanged += (_, _) => UpdateMode(); UpdateMode();
        if (existing.Provider == AccountProvider.TunnelToken)
        {
            client.IsEnabled = false; tenant.IsEnabled = false; scope.IsEnabled = false;
            form.Children.Add(Paragraph("Este perfil usa uma credencial de túnel fornecida pela CLI e protegida pelo cofre local. A interface não expõe o token."));
        }
        var error = Paragraph(""); form.Children.Add(error);
        footer.Children.Add(ActionButton("Salvar", async () =>
        {
            var chosen = SelectedProvider(provider);
            var updated = existing with { DisplayName = name.Text?.Trim() ?? "", Provider = chosen, CliLoginMode = chosen is AccountProvider.MicrosoftCli or AccountProvider.GitHubCli ? (CliLoginMode)cliLoginMode.SelectedIndex : CliLoginMode.DeviceCode, ClientId = chosen is AccountProvider.Microsoft or AccountProvider.GitHub ? EmptyToNull(client.Text) : null, TenantId = chosen == AccountProvider.Microsoft ? EmptyToNull(tenant.Text) : existing.TenantId, Scope = chosen is AccountProvider.Microsoft or AccountProvider.GitHub ? EmptyToNull(scope.Text) : null };
            if (string.IsNullOrWhiteSpace(updated.DisplayName) || chosen == AccountProvider.Microsoft && updated.ClientId is null) { error.Text = "Informe o nome do perfil e, para Microsoft, o Client ID."; return; }
            if (await _model.SaveAccountAsync(updated)) dialog.Close(); else error.Text = _model.Message;
        }, true));
        await dialog.ShowDialog(this);
    }

    private async Task EditTunnelAsync(TunnelProfile? tunnel = null)
    {
        var snapshot = _model.Snapshot;
        if (snapshot is null) return;
        if (snapshot.Accounts.Length == 0) { await EditAccountAsync(); return; }
        var existing = tunnel ?? new TunnelProfile { AccountId = snapshot.Accounts[0].Id };
        var saved = snapshot.Tunnels.Any(t => t.Id == existing.Id);
        var (dialog, form, footer) = Dialog(saved ? $"Configurar {existing.CanonicalId}" : "Adicionar túnel", 770,
            () => saved ? _model.TunnelActivity(existing.Id) : null);
        var canonical = Field(form, "Tunnel ID", existing.TunnelId.Length > 0 ? existing.CanonicalId : "", "Identificador completo, incluindo o cluster: tunnel-demo.use2.");
        var alias = Field(form, "Apelido (opcional)", existing.Alias);
        var accounts = new ComboBox { ItemsSource = snapshot.Accounts.Select(a => $"{a.DisplayName} · {a.Provider}").ToArray(), SelectedIndex = Math.Max(0, Array.FindIndex(snapshot.Accounts, a => a.Id == existing.AccountId)), HorizontalAlignment = HorizontalAlignment.Stretch };
        form.Children.Add(Labeled("Conta", accounts));
        var autoConnect = Toggle(form, "Conectar automaticamente ao iniciar o agente", existing.AutoConnect);
        var discover = Toggle(form, "Descobrir e mapear portas remotas automaticamente", existing.AutoDiscoverPorts);
        var reconnect = Toggle(form, "Reconectar após interrupções", existing.Reconnect.Enabled);
        var delays = new Grid { ColumnDefinitions = new("*,*,*") };
        var initial = NumberField("Intervalo inicial (s)", existing.Reconnect.InitialDelaySeconds, 1, 300);
        var maximum = NumberField("Intervalo máximo (s)", existing.Reconnect.MaxDelaySeconds, 1, 3600);
        var attempts = NumberField("Tentativas máximas", existing.Reconnect.MaxRetries, 0, 1000);
        delays.Children.Add(initial.Panel); Grid.SetColumn(maximum.Panel, 1); delays.Children.Add(maximum.Panel); Grid.SetColumn(attempts.Panel, 2); delays.Children.Add(attempts.Panel); form.Children.Add(delays);
        form.Children.Add(Text("Mapeamentos TCP", 17, FontWeight.SemiBold));
        form.Children.Add(Paragraph("A porta efetiva aparece na lista após conectar. Uma porta desabilitada não cria listener local."));
        var mappingPanel = new StackPanel { Spacing = 10 };
        var mappingEditors = new List<MappingEditor>();
        void AddMapping(PortMapping mapping)
        {
            var editor = new MappingEditor(mapping);
            editor.Remove.Click += (_, _) => { mappingEditors.Remove(editor); mappingPanel.Children.Remove(editor.View); };
            mappingEditors.Add(editor); mappingPanel.Children.Add(editor.View);
        }
        foreach (var mapping in existing.Mappings) AddMapping(mapping);
        form.Children.Add(mappingPanel);
        form.Children.Add(ActionButton("Adicionar porta", () => { AddMapping(new PortMapping { RemotePort = 3140, LocalPort = 3140 }); return Task.CompletedTask; }));
        if (saved)
        {
            var operations = new WrapPanel();
            operations.Children.Add(ActionButton("Atualizar portas remotas", async () => { await _model.ActionAsync("tunnel.refresh", existing.Id); }));
            operations.Children.Add(ActionButton("Reconectar agora", async () => await _model.ActionAsync("tunnel.reconnect", existing.Id)));
            operations.Children.Add(ActionButton("Remover perfil local", async () =>
            {
                if (!await ConfirmAsync("Remover perfil", $"Parar {existing.CanonicalId} e remover o perfil deste dispositivo? O recurso remoto será preservado.", dialog)) return;
                if (await _model.ActionAsync("tunnel.remove", existing.Id)) dialog.Close();
            }));
            form.Children.Add(operations);
        }
        var error = Paragraph(""); form.Children.Add(error);
        footer.Children.Add(ActionButton("Salvar perfil", async () =>
        {
            var parts = (canonical.Text ?? "").Trim().Split('.');
            if (parts.Length != 2) { error.Text = "Informe Tunnel ID e cluster separados por ponto."; return; }
            var updated = existing with
            {
                TunnelId = parts[0],
                ClusterId = parts[1],
                Alias = alias.Text?.Trim() ?? "",
                AccountId = snapshot.Accounts[accounts.SelectedIndex].Id,
                AutoConnect = autoConnect.IsChecked == true,
                AutoDiscoverPorts = discover.IsChecked == true,
                Reconnect = new() { Enabled = reconnect.IsChecked == true, InitialDelaySeconds = (int)(initial.Input.Value ?? 2), MaxDelaySeconds = (int)(maximum.Input.Value ?? 60), MaxRetries = (int)(attempts.Input.Value ?? 10) },
                Mappings = mappingEditors.Select(m => m.Read()).ToArray()
            };
            if (await _model.SaveTunnelAsync(updated)) dialog.Close(); else error.Text = _model.Message;
        }, true));
        await dialog.ShowDialog(this);
    }

    private async Task EditSettingsAsync()
    {
        if (_model.Snapshot is not { } snapshot) return;
        var settings = snapshot.Settings;
        var (dialog, form, footer) = Dialog("Configurações do dispositivo", 580);
        var theme = Choice(form, "Tema", new[] { "System", "Light", "Dark", "HighContrast" }, settings.Theme);
        var retention = NumberField("Retenção de logs (dias)", settings.LogRetentionDays, 1, 90); form.Children.Add(retention.Panel);
        var connections = NumberField("Máximo de conexões simultâneas", settings.MaxConnections, 1, 100); form.Children.Add(connections.Panel);
        var discovery = NumberField("Intervalo de descoberta (segundos)", settings.DiscoveryIntervalSeconds, 10, 3600); form.Children.Add(discovery.Panel);
        var closeToTray = Toggle(form, "Ao fechar a janela, manter na bandeja", settings.CloseToTray);
        var startup = Toggle(form, "Iniciar o agente com minha sessão", settings.StartWithUserSession);
        startup.IsEnabled = snapshot.Capabilities.UserStartup;
        if (!snapshot.Capabilities.UserStartup) form.Children.Add(Paragraph("A inicialização automática não está disponível nesta instalação."));
        form.Children.Add(Text("Proxy automático / PAC", 17, FontWeight.SemiBold));
        form.Children.Add(Paragraph("Publique uma configuração PAC local para os aplicativos compatíveis. Copie a URL gerada e configure-a no navegador ou sistema. As regras usam as portas efetivas dos túneis."));
        var pacMode = Choice(form, "Modo do PAC", new[] { "Regras", "Proxy local", "Manual" }, settings.Pac.Mode switch { PacMode.LocalProxy => "Proxy local", PacMode.Manual => "Manual", _ => "Regras" });
        var manualPanel = new StackPanel { Spacing = 10 };
        form.Children.Add(manualPanel);
        var manual = new TextBox { Text = settings.Pac.ManualScript, AcceptsReturn = true, MinHeight = 150, MaxLength = 65536, FontFamily = new FontFamily("monospace") };
        manualPanel.Children.Add(Labeled("PAC manual", manual));
        manualPanel.Children.Add(Paragraph("O Agent apenas serve este JavaScript. Ele é executado pelos clientes PAC, nunca pelo Agent. O modo manual substitui as regras PAC e não altera as regras do proxy local."));
        manualPanel.Children.Add(ActionButton("Importar arquivo .pac", async () => manual.Text = await ReadPacFileAsync(dialog) ?? manual.Text));
        var pacEnabled = Toggle(form, "Disponibilizar PAC neste dispositivo", settings.Pac.Enabled);
        var pacPort = NumberField("Porta HTTP local do PAC", settings.Pac.Port, 1024, 65535); form.Children.Add(pacPort.Panel);
        var rulesPanel = new StackPanel { Spacing = 12 };
        var rules = new List<PacRuleEditor>();
        void AddRule(PacRule rule)
        {
            var editor = new PacRuleEditor(rule, snapshot);
            editor.Remove.Click += (_, _) => { rules.Remove(editor); rulesPanel.Children.Remove(editor.View); };
            rules.Add(editor); rulesPanel.Children.Add(editor.View);
        }
        foreach (var rule in settings.Pac.Rules) AddRule(rule);
        form.Children.Add(rulesPanel);
        var newRule = ActionButton("Adicionar regra PAC", () =>
        {
            var first = snapshot.Tunnels.First(t => PacRuleEditor.AvailablePorts(t, snapshot).Length > 0);
            AddRule(new PacRule { ProfileId = first.Id, RemotePort = PacRuleEditor.AvailablePorts(first, snapshot)[0] });
            return Task.CompletedTask;
        });
        newRule.IsEnabled = snapshot.Tunnels.Any(t => PacRuleEditor.AvailablePorts(t, snapshot).Length > 0);
        form.Children.Add(newRule);
        if (!newRule.IsEnabled) form.Children.Add(Paragraph("Configure ou conecte um túnel com portas para adicionar regras PAC."));
        void PacModeChanged()
        {
            manualPanel.IsVisible = pacMode.SelectedIndex == (int)PacMode.Manual;
            rulesPanel.IsVisible = newRule.IsVisible = pacMode.SelectedIndex == (int)PacMode.Rules;
        }
        pacMode.SelectionChanged += (_, _) => PacModeChanged(); PacModeChanged();
        var error = Paragraph(""); form.Children.Add(error);
        footer.Children.Add(ActionButton("Salvar", async () =>
        {
            var value = settings with
            {
                Theme = (string)theme.SelectedItem!,
                LogRetentionDays = (int)(retention.Input.Value ?? 7),
                MaxConnections = (int)(connections.Input.Value ?? 20),
                DiscoveryIntervalSeconds = (int)(discovery.Input.Value ?? 60),
                CloseToTray = closeToTray.IsChecked == true,
                StartWithUserSession = startup.IsChecked == true,
                Pac = new PacSettings { Enabled = pacEnabled.IsChecked == true, Port = (int)(pacPort.Input.Value ?? 7777), Rules = rules.Select(rule => rule.Read()).ToArray(), Mode = (PacMode)pacMode.SelectedIndex, ManualScript = manual.Text ?? "" }
            };
            if (await _model.SaveSettingsAsync(value)) dialog.Close(); else error.Text = _model.Message;
        }, true));
        await dialog.ShowDialog(this);
    }

    private async Task LaunchAgentAsync()
    {
        await _model.RetryAgentAsync();
    }

    private static Task OpenLoginAsync(string uri)
    {
        if (ToolkitViewModel.IsSafeLoginUri(uri)) using (Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })) { }
        return Task.CompletedTask;
    }

    private static Control NavigationIcon(string name)
    {
        var geometry = name switch
        {
            "Dashboard" => "M3,3 L10,3 10,10 3,10 Z M14,3 L21,3 21,10 14,10 Z M3,14 L10,14 10,21 3,21 Z M14,14 L21,14 21,21 14,21 Z",
            "Contas" => "M16,21 L16,19 C16,15 4,15 4,19 L4,21 M10,11 A4,4 0 1 0 10,3 A4,4 0 1 0 10,11 M18,4 C22,4 22,10 18,10 M19,15 C22,15 23,17 23,20",
            "Conexões" => "M5,21 L5,10 A7,7 0 0 1 19,10 L19,21 M9,21 L9,10 A3,3 0 0 1 15,10 L15,21 M1,14 L7,14 M4,11 L7,14 4,17 M17,14 L23,14 M20,11 L23,14 20,17",
            "DevTunnel" => "M5,17 C0,17 0,9 5,9 C5,1 17,1 18,9 C24,9 24,17 19,17 M7,13 L7,23 M4,20 L7,23 10,20 M17,23 L17,13 M14,16 L17,13 20,16",
            "Serviços" => "M3,3 L21,3 21,10 3,10 Z M3,14 L21,14 21,21 3,21 Z M6,6 L7,6 M6,17 L7,17 M15,6 L18,6 M15,17 L18,17",
            "Portas" => "M6,3 L6,9 M18,15 L18,21 M3,9 L9,9 9,15 3,15 Z M15,9 L21,9 21,15 15,15 Z M6,15 L6,18 18,18 M18,9 L18,6 6,6",
            "Proxy e PAC" => "M3,12 L9,12 M6,9 L9,12 6,15 M9,7 L15,7 15,17 9,17 Z M15,9 L21,5 M18,5 L21,5 21,8 M15,15 L21,19 M18,19 L21,19 21,16",
            "Logs" => "M5,3 L19,3 19,21 5,21 Z M8,7 L16,7 M8,11 L16,11 M8,15 L13,15",
            _ => "M4,5 L20,5 M4,12 L20,12 M4,19 L20,19 M8,2 L8,8 M16,9 L16,15 M10,16 L10,22"
        };
        var path = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(geometry), Width = 18, Height = 18, Stretch = Stretch.Uniform, StrokeThickness = 1.7, VerticalAlignment = VerticalAlignment.Center };
        Paint(path, Avalonia.Controls.Shapes.Shape.StrokeProperty, "Accent"); return path;
    }

    private (Window Window, StackPanel Form, StackPanel Footer) Dialog(string title, double width, Func<OperationActivity?>? activity = null)
    {
        var dialog = new Window { Title = title, Width = width, Height = 710, MinWidth = 480, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Paint(dialog, IconProperty, "BrandWindowIcon");
        Paint(dialog, BackgroundProperty, "Surface"); Paint(dialog, ForegroundProperty, "Text");
        var grid = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new(24) };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new(0, 0, 0, 20) };
        var logo = new Image { Width = 32, Height = 32 };
        Paint(logo, Image.SourceProperty, "BrandIcon"); heading.Children.Add(logo);
        var headingText = Text(title, 21, FontWeight.SemiBold); headingText.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(headingText); grid.Children.Add(heading);
        var form = new StackPanel { Spacing = 16, Margin = new(0, 0, 8, 0) };
        var scroll = new ScrollViewer { Content = form, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        footer.Children.Add(ActionButton("Cancelar", () => { dialog.Close(); return Task.CompletedTask; }));
        var openedAt = DateTimeOffset.UtcNow;
        var feedback = new ActivityPanel(_model, () => activity?.Invoke() ??
            (_model.ForegroundActivity is { } current && (current.Running || current.FinishedAt >= openedAt) ? current : null));
        Grid.SetRow(feedback, 2); grid.Children.Add(feedback);
        Grid.SetRow(footer, 3); grid.Children.Add(footer); dialog.Content = grid;
        return (dialog, form, footer);
    }

    private async Task<bool> ConfirmAsync(string title, string message, Window? owner = null)
    {
        var (dialog, form, footer) = Dialog(title, 540); dialog.Height = 320;
        form.Children.Add(Paragraph(message));
        footer.Children.Add(ActionButton("Confirmar", () => { dialog.Close(true); return Task.CompletedTask; }, true));
        return await dialog.ShowDialog<bool>(owner ?? this);
    }

    internal static string StateLabel(ConnectionState state) => state switch
    {
        ConnectionState.Stopped => "Parado",
        ConnectionState.Connected => "Conectado",
        ConnectionState.Connecting => "Conectando",
        ConnectionState.Reconnecting => "Reconectando",
        ConnectionState.AuthenticationRequired => "Autenticação necessária",
        ConnectionState.Authenticating => "Autenticando",
        ConnectionState.Discovering => "Descobrindo",
        ConnectionState.Degraded => "Degradado",
        ConnectionState.Stopping => "Parando",
        _ => "Falha"
    };
    internal static string ModeLabel(MappingMode mode) => mode switch { MappingMode.SamePort => "Mesma porta", MappingMode.Automatic => "Automático", MappingMode.Manual => "Manual", MappingMode.ReservedRange => "Faixa reservada", _ => "Desabilitado" };
    private static string AccountStatus(string status) => status switch { "Authenticated" or "Valid" => "Autenticada", "AuthenticationRequired" => "Autenticação necessária", "Expired" => "Expirada", _ => status };
    private static string VersionLabel()
    {
        var version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        var parts = version.Split('+', 2);
        var revision = parts.Length == 2 && parts[1].Length >= 7 && parts[1].Take(7).All(char.IsAsciiHexDigit) ? $" · {parts[1][..7]}" : "";
        return $"v{parts[0]}{revision}";
    }
    private static string Capability(bool value) => value ? "Disponível" : "Indisponível nesta instalação";
    private static string FormatDate(DateTimeOffset? value) => value?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "Ainda não realizada";
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static StackPanel PageStack() => new() { Spacing = 20 };
    internal static void Paint(AvaloniaObject target, AvaloniaProperty property, string key) => target.Bind(property, new DynamicResourceExtension(key));
    internal static TextBlock Text(string value, double size = 13, FontWeight? weight = null, string color = "Text")
    {
        var text = new TextBlock { Text = value, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        Paint(text, TextBlock.ForegroundProperty, color); return text;
    }
    private static TextBlock Paragraph(string value) { var text = Text(value, 12, color: "Muted"); text.TextWrapping = TextWrapping.Wrap; return text; }
    private static Border Card(Control content, double padding = 20)
    {
        var card = new Border { Child = content, Padding = new(padding), CornerRadius = new(9), BorderThickness = new(1) };
        Paint(card, Border.BackgroundProperty, "Surface"); Paint(card, Border.BorderBrushProperty, "Stroke"); return card;
    }
    private static Control Section(string title, string subtitle, Control content)
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(Text(title, 16, FontWeight.SemiBold)); stack.Children.Add(Paragraph(subtitle)); stack.Children.Add(content); return Card(stack);
    }
    private static Control Metric(string label, string value, string detail)
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(Text(label, 10, FontWeight.SemiBold, "Muted")); stack.Children.Add(Text(value, 31, FontWeight.SemiBold, "Accent")); stack.Children.Add(Text(detail, 10, color: "Muted"));
        var card = Card(stack, 17); card.Margin = new(0, 0, 10, 0); return card;
    }
    private static Control KeyValue(string key, string value)
    {
        var grid = new Grid { ColumnDefinitions = new("210,*") }; grid.Children.Add(Text(key, 12, color: "Muted"));
        var text = Text(value, 12); text.TextWrapping = TextWrapping.Wrap; Grid.SetColumn(text, 1); grid.Children.Add(text); return grid;
    }
    private static Control LogRow(LogEntry entry)
    {
        var grid = new Grid { ColumnDefinitions = new("130,70,*"), RowDefinitions = new("Auto,Auto"), Margin = new(0, 4) };
        grid.Children.Add(Text(entry.Timestamp.ToLocalTime().ToString("dd/MM HH:mm:ss"), 11, color: "Muted"));
        var level = Text(entry.Level switch { "Information" => "Info", "Warning" => "Aviso", "Error" => "Erro", _ => entry.Level }, 11, FontWeight.SemiBold); Grid.SetColumn(level, 1); grid.Children.Add(level);
        var code = Text(entry.Code, 12, FontWeight.Medium); Grid.SetColumn(code, 2); grid.Children.Add(code);
        var message = Paragraph(entry.Message); message.Margin = new(0, 5, 0, 0); Grid.SetRow(message, 1); Grid.SetColumn(message, 2); grid.Children.Add(message); return grid;
    }
    internal static Button ActionButton(string title, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = title, Margin = new(0, 0, 6, 0), MinHeight = 34 };
        AutomationProperties.SetName(button, title);
        if (primary) { Paint(button, BackgroundProperty, "Accent"); Paint(button, ForegroundProperty, "AccentText"); }
        button.Click += async (_, _) =>
        {
            if (button.Classes.Contains("working")) return;
            var main = TopLevel.GetTopLevel(button) as MainWindow;
            if (main is not null) main._pendingActions++;
            button.Classes.Add("working");
            button.IsEnabled = false;
            var original = button.Content;
            var originalWidth = button.Width;
            var originalHeight = button.Height;
            var showingProgress = false;
            try
            {
                var pending = action();
                if (!pending.IsCompleted)
                {
                    // An overlay keeps the same text/size, instead of making the
                    // button jump when its label is replaced with a longer message.
                    if (button.Bounds.Width > 0) button.Width = button.Bounds.Width;
                    if (button.Bounds.Height > 0) button.Height = button.Bounds.Height;
                    button.Content = null;
                    var busy = new Grid();
                    busy.Children.Add(new ContentControl { Content = original });
                    var progress = new ProgressBar { IsIndeterminate = true, MinWidth = 0, Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 0, -4) };
                    AutomationProperties.SetName(progress, $"{title}: em andamento");
                    busy.Children.Add(progress); button.Content = busy; showingProgress = true;
                }
                await pending;
            }
            catch (Exception error)
            {
                var top = TopLevel.GetTopLevel(button) as Window;
                while (top?.Owner is Window owner) top = owner;
                if (top is MainWindow ownerWindow) ownerWindow._model.ReportFailure(error);
            }
            finally
            {
                if (showingProgress)
                {
                    if (button.Content is Grid busy && busy.Children[0] is ContentControl content) content.Content = null;
                    button.Content = original;
                    button.Width = originalWidth; button.Height = originalHeight;
                }
                button.Classes.Remove("working"); button.IsEnabled = true;
                if (main is not null) { main._pendingActions--; main.RefreshView(); }
            }
        };
        return button;
    }
    internal static StackPanel Labeled(string label, Control input)
    {
        var panel = new StackPanel { Spacing = 7, Margin = new(0, 0, 8, 0) };
        panel.Children.Add(Text(label, 12, FontWeight.Medium)); panel.Children.Add(input); AutomationProperties.SetName(input, label); return panel;
    }
    private static TextBox Field(StackPanel form, string label, string? value, string? help = null)
    {
        var input = new TextBox { Text = value ?? "", HorizontalAlignment = HorizontalAlignment.Stretch };
        var field = Labeled(label, input); if (help is not null) field.Children.Add(Paragraph(help)); form.Children.Add(field); return input;
    }
    internal static ComboBox Choice(StackPanel form, string label, string[] options, string selected)
    {
        var input = new ComboBox { ItemsSource = options, SelectedItem = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
        form.Children.Add(Labeled(label, input)); return input;
    }
    private static CheckBox Toggle(StackPanel form, string label, bool value)
    {
        var input = new CheckBox { Content = label, IsChecked = value }; form.Children.Add(input); return input;
    }
    internal static (StackPanel Panel, NumericUpDown Input) NumberField(string label, int value, int min, int max)
    {
        var input = new NumericUpDown { Value = value, Minimum = min, Maximum = max, Increment = 1, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Stretch };
        return (Labeled(label, input), input);
    }
}

internal sealed class MappingEditor
{
    public Control View { get; }
    public Button Remove { get; }
    private readonly NumericUpDown _remote;
    private readonly NumericUpDown _local;
    private readonly NumericUpDown _rangeStart;
    private readonly NumericUpDown _rangeEnd;
    private readonly ComboBox _mode;
    private readonly ComboBox _address;
    private readonly CheckBox _enabled;
    private readonly PortMapping _original;

    public MappingEditor(PortMapping mapping)
    {
        _original = mapping;
        var panel = new StackPanel { Spacing = 9 };
        _enabled = new CheckBox { Content = "Ativar este mapeamento", IsChecked = mapping.Enabled };
        var numbers = new Grid { ColumnDefinitions = new("*,*,*") };
        var remote = MainWindow.NumberField("Porta remota", mapping.RemotePort, 1, 65535); _remote = remote.Input;
        var local = MainWindow.NumberField("Porta local (0 = auto)", mapping.LocalPort, 0, 65535); _local = local.Input;
        numbers.Children.Add(remote.Panel); Grid.SetColumn(local.Panel, 1); numbers.Children.Add(local.Panel);
        var modes = Enum.GetValues<MappingMode>();
        _mode = new ComboBox { ItemsSource = modes.Select(MainWindow.ModeLabel).ToArray(), SelectedIndex = Array.IndexOf(modes, mapping.Mode), HorizontalAlignment = HorizontalAlignment.Stretch };
        var mode = MainWindow.Labeled("Modo", _mode); Grid.SetColumn(mode, 2); numbers.Children.Add(mode); panel.Children.Add(numbers);
        var details = new Grid { ColumnDefinitions = new("*,*,*,Auto") };
        _address = new ComboBox { ItemsSource = new[] { "127.0.0.1", "::1" }, SelectedItem = mapping.LocalAddress, HorizontalAlignment = HorizontalAlignment.Stretch };
        details.Children.Add(MainWindow.Labeled("Endereço local", _address));
        var start = MainWindow.NumberField("Faixa inicial", mapping.RangeStart, 1024, 65535); _rangeStart = start.Input;
        var end = MainWindow.NumberField("Faixa final", mapping.RangeEnd, 1024, 65535); _rangeEnd = end.Input;
        Grid.SetColumn(start.Panel, 1); details.Children.Add(start.Panel); Grid.SetColumn(end.Panel, 2); details.Children.Add(end.Panel);
        Remove = new Button { Content = "Remover", VerticalAlignment = VerticalAlignment.Bottom }; Grid.SetColumn(Remove, 3); details.Children.Add(Remove);
        void UpdateMode() { var selected = modes[Math.Max(0, _mode.SelectedIndex)]; _local.IsEnabled = selected is MappingMode.Manual; _rangeStart.IsEnabled = _rangeEnd.IsEnabled = selected is MappingMode.ReservedRange or MappingMode.Automatic; _enabled.IsEnabled = selected != MappingMode.Disabled; }
        _mode.SelectionChanged += (_, _) => UpdateMode(); UpdateMode();
        panel.Children.Add(details);
        panel.Children.Add(_enabled);
        var border = new Border { Child = panel, Padding = new(14), BorderThickness = new(1), CornerRadius = new(7) };
        MainWindow.Paint(border, Border.BorderBrushProperty, "Stroke"); MainWindow.Paint(border, Border.BackgroundProperty, "Subtle"); View = border;
    }

    public PortMapping Read()
    {
        var mode = Enum.GetValues<MappingMode>()[Math.Max(0, _mode.SelectedIndex)];
        var edited = _original with { RemotePort = (int)(_remote.Value ?? 0), LocalPort = (int)(_local.Value ?? 0), LocalAddress = (string?)_address.SelectedItem ?? "127.0.0.1", Mode = mode, Enabled = _enabled.IsChecked == true && mode != MappingMode.Disabled, RangeStart = (int)(_rangeStart.Value ?? 20000), RangeEnd = (int)(_rangeEnd.Value ?? 21000) };
        return edited == _original ? edited : edited with { AutoDiscovered = false };
    }
}

internal sealed class PacRuleEditor
{
    public Control View { get; }
    public Button Remove { get; }
    private readonly TextBox _domain;
    private readonly CheckBox _subdomains;
    private readonly CheckBox _fallback;
    private readonly ComboBox _profile;
    private readonly ComboBox _remotePort;
    private readonly ComboBox _protocol;
    private readonly TunnelProfile[] _profiles;

    public PacRuleEditor(PacRule rule, AgentSnapshot snapshot)
    {
        _profiles = snapshot.Tunnels;
        var panel = new StackPanel { Spacing = 11 };
        _domain = new TextBox { Text = rule.Domain, PlaceholderText = "example.org" };
        panel.Children.Add(MainWindow.Labeled("Domínio da regra", _domain));
        _fallback = new CheckBox { Content = "Tentar conexão direta se o túnel estiver indisponível", IsChecked = rule.DirectFallback }; panel.Children.Add(_fallback);
        _subdomains = new CheckBox { Content = "Incluir subdomínios", IsChecked = rule.IncludeSubdomains }; panel.Children.Add(_subdomains);
        _profile = new ComboBox { ItemsSource = _profiles.Select(p => p.CanonicalId).ToArray(), SelectedIndex = Array.FindIndex(_profiles, p => p.Id == rule.ProfileId), HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(MainWindow.Labeled("Túnel que oferece o proxy", _profile));
        var grid = new Grid { ColumnDefinitions = new("*,*,Auto") };
        _remotePort = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.Children.Add(MainWindow.Labeled("Porta remota do proxy", _remotePort));
        _protocol = new ComboBox { ItemsSource = new[] { "HTTP CONNECT", "SOCKS5" }, SelectedIndex = rule.Protocol == ProxyProtocol.Socks5 ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var protocol = MainWindow.Labeled("Tipo de proxy", _protocol); Grid.SetColumn(protocol, 1); grid.Children.Add(protocol);
        Remove = new Button { Content = "Remover", VerticalAlignment = VerticalAlignment.Bottom }; Grid.SetColumn(Remove, 2); grid.Children.Add(Remove);
        void UpdatePorts()
        {
            var current = _remotePort.SelectedItem is int value ? value : rule.RemotePort;
            var ports = _profile.SelectedIndex >= 0 ? AvailablePorts(_profiles[_profile.SelectedIndex], snapshot) : [];
            _remotePort.ItemsSource = ports;
            _remotePort.SelectedItem = ports.Contains(current) ? current : ports.FirstOrDefault();
        }
        _profile.SelectionChanged += (_, _) => UpdatePorts(); UpdatePorts(); panel.Children.Add(grid);
        var border = new Border { Child = panel, Padding = new(14), BorderThickness = new(1), CornerRadius = new(7) };
        MainWindow.Paint(border, Border.BorderBrushProperty, "Stroke"); MainWindow.Paint(border, Border.BackgroundProperty, "Subtle"); View = border;
    }

    public static int[] AvailablePorts(TunnelProfile profile, AgentSnapshot snapshot) => profile.Mappings.Where(m => m.Enabled && m.Mode != MappingMode.Disabled).Select(m => m.RemotePort)
        .Concat(snapshot.Connections.FirstOrDefault(c => c.ProfileId == profile.Id)?.Ports?.Select(p => p.RemotePort) ?? []).Distinct().Order().ToArray();

    public PacRule Read() => new()
    {
        Domain = _domain.Text?.Trim() ?? "",
        IncludeSubdomains = _subdomains.IsChecked == true,
        DirectFallback = _fallback.IsChecked == true,
        ProfileId = _profile.SelectedIndex >= 0 ? _profiles[_profile.SelectedIndex].Id : "",
        RemotePort = _remotePort.SelectedItem is int port ? port : 0,
        Protocol = _protocol.SelectedIndex == 1 ? ProxyProtocol.Socks5 : ProxyProtocol.Http
    };
}
