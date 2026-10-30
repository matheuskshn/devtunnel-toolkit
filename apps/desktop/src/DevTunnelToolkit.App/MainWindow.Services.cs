using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App;

public sealed partial class MainWindow
{
    private int _devTunnelTab;

    private Control DevTunnelPage(AgentSnapshot snapshot)
    {
        var page = PageStack();
        var navigation = new WrapPanel();
        foreach (var (label, index) in new[] { ("Túneis da conta", 0), ("Publicações neste Desktop", 1) })
        {
            var button = ActionButton(label, () => { _devTunnelTab = index; RefreshView(); return Task.CompletedTask; }, _devTunnelTab == index);
            navigation.Children.Add(button);
        }
        page.Children.Add(navigation);
        page.Children.Add(_devTunnelTab == 1 ? HostingPage(snapshot) : RemotePage(snapshot));
        return page;
    }

    internal static string RemoteHostLabel(AgentSnapshot snapshot, string accountId, ManagedTunnel tunnel)
    {
        var local = snapshot.Hosts.Any(h => h.CanonicalId == tunnel.Target.CanonicalId &&
            snapshot.HostStatuses.Any(s => s.ProfileId == h.Id && s.State is ConnectionState.Connected or ConnectionState.Degraded));
        if (local) return "Neste Desktop";
        return tunnel.HostOnline switch { true => "Outro host ativo", false => "Sem host conectado", null => "Estado desconhecido" };
    }

    private Control ServicesPage(AgentSnapshot snapshot)
    {
        var page = PageStack();
        page.Children.Add(Card(Paragraph("Serviços funcionam neste computador sem login e sem túnel. Para publicá-los, crie um vínculo em DevTunnel. O proxy pessoal em Proxy e PAC continua separado. HTTP/CONNECT e SOCKS5 suportam TCP, sem interceptação TLS.")));
        foreach (var service in snapshot.Services)
        {
            var state = snapshot.ServiceStatuses.FirstOrDefault(s => s.ServiceId == service.Id);
            var active = state is not null && (state.State is ConnectionState.Connected or ConnectionState.Degraded || ToolkitViewModel.IsConnectionPending(state.State));
            var publications = snapshot.Hosts.Where(h => h.Ports.Any(p => p.ServiceId == service.Id)).ToArray();
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(Text(service.Name, 17, FontWeight.SemiBold));
            content.Children.Add(KeyValue("Protocolo", service.Protocol == ProxyProtocol.Http ? "HTTP / CONNECT" : "SOCKS5 TCP CONNECT"));
            content.Children.Add(KeyValue("Estado", state?.State == ConnectionState.Connected ? "Em execução" : StateLabel(state?.State ?? ConnectionState.Stopped)));
            content.Children.Add(KeyValue("Listener local", state?.Port is { } port ? $"{state.LocalAddress}:{port}" : service.Port == 0 ? $"{service.LocalAddress} · porta automática ao iniciar" : $"{service.LocalAddress}:{service.Port}"));
            content.Children.Add(KeyValue("Política padrão", service.Policy.AllowAllDestinations ? "Permitir destinos públicos sem regra correspondente" : "Bloquear destinos sem regra correspondente"));
            content.Children.Add(KeyValue("Regras de acesso", service.Policy.Rules.Length.ToString()));
            content.Children.Add(KeyValue("Publicações vinculadas", publications.Length == 0 ? "Nenhuma" : string.Join(", ", publications.Select(p => p.CanonicalId))));
            content.Children.Add(new ActivityPanel(_model, () => _model.ServiceActivity(service.Id)));
            var actions = new WrapPanel();
            var start = ActionButton(active ? "Parar serviço" : "Iniciar serviço", async () =>
            {
                if (active && !await ConfirmStopServiceAsync(service)) return;
                await _model.ServiceActionAsync(active ? "service.stop" : "service.start", service.Id);
            }, !active);
            start.IsEnabled = !ToolkitViewModel.IsConnectionPending(state?.State ?? ConnectionState.Stopped); actions.Children.Add(start);
            var edit = ActionButton("Editar serviço", () => EditServiceAsync(service)); edit.IsEnabled = !active; actions.Children.Add(edit);
            actions.Children.Add(ActionButton("Ver logs do serviço", () => ServiceLogsAsync(service)));
            var remove = ActionButton("Remover serviço", async () =>
            {
                if (await ConfirmAsync("Remover serviço", $"Remover a configuração de {service.Name}? O serviço precisa estar parado e sem vínculos. Nenhum túnel remoto será excluído."))
                    await _model.ServiceActionAsync("service.remove", service.Id);
            });
            remove.IsEnabled = !active && publications.Length == 0; actions.Children.Add(remove);
            if (active) content.Children.Add(Paragraph("Pare o serviço antes de editar sua configuração. Isso interrompe seus clientes locais e os vínculos publicados."));
            if (publications.Length > 0) content.Children.Add(Paragraph("Para remover, desvincule primeiro este serviço das publicações em DevTunnel."));
            content.Children.Add(actions); page.Children.Add(Card(content));
        }
        if (snapshot.Services.Length == 0) page.Children.Add(Section("Seus serviços locais", "Crie um proxy HTTP/CONNECT ou SOCKS5 com uma política própria.", Paragraph("Use Novo serviço. Por padrão o listener fica em loopback e destinos sem regra são bloqueados. Iniciar o proxy não cria nem altera túneis remotos.")));
        return page;
    }

    private async Task<bool> ConfirmStopServiceAsync(ServiceProfile service)
    {
        var affected = _model.Snapshot?.Hosts.Where(h => h.Ports.Any(p => p.ServiceId == service.Id)).Select(h => h.CanonicalId).Distinct().ToArray() ?? [];
        return await ConfirmAsync("Parar serviço", "As conexões deste proxy serão encerradas. " + (affected.Length == 0 ? "Não há publicações vinculadas." :
            $"Publicações afetadas: {string.Join(", ", affected)}. Outros serviços continuam disponíveis. Depois de iniciar este serviço novamente, reinicie as publicações afetadas para restabelecer os vínculos. Não há fallback direto automático."));
    }

    private async Task ServiceLogsAsync(ServiceProfile service)
    {
        var (dialog, form, footer) = Dialog($"Logs do serviço · {service.Name}", 860);
        form.Children.Add(Paragraph("Eventos deste listener. O login do proprietário do túnel não identifica automaticamente cada cliente do proxy."));
        var rows = new StackPanel { Spacing = 8 }; form.Children.Add(rows);
        void Render()
        {
            rows.Children.Clear();
            var logs = _model.Snapshot?.Logs.Where(l => l.ProfileId == service.Id).TakeLast(100).Reverse().ToArray() ?? [];
            foreach (var entry in logs) rows.Children.Add(LogRow(entry));
            if (logs.Length == 0) rows.Children.Add(Paragraph("Nenhum evento deste serviço no histórico local disponível."));
        }
        Render(); footer.Children.Add(ActionButton("Atualizar logs", async () => { await _model.RefreshAsync(); Render(); }));
        await dialog.ShowDialog(this);
    }

    private async Task EditServiceAsync(ServiceProfile? profile = null)
    {
        var existing = profile ?? new ServiceProfile { Name = "Novo proxy" };
        var (dialog, form, footer) = Dialog(profile is null ? "Novo serviço" : "Editar serviço", 860, () => _model.ServiceActivity(existing.Id));
        var tabs = new TabControl(); form.Children.Add(tabs);
        var listener = new StackPanel { Spacing = 12, Margin = new(0, 16) };
        listener.Children.Add(Paragraph("O serviço é independente do DevTunnel. Configure o endereço nos aplicativos ou vincule-o a uma porta de túnel em DevTunnel."));
        var name = Field(listener, "Nome do serviço", existing.Name);
        var protocol = Choice(listener, "Protocolo do serviço", ["HTTP / CONNECT", "SOCKS5 TCP CONNECT"], existing.Protocol == ProxyProtocol.Http ? "HTTP / CONNECT" : "SOCKS5 TCP CONNECT");
        var address = Choice(listener, "Endereço de escuta", ["127.0.0.1", "::1"], existing.LocalAddress);
        var port = NumberField("Porta local do serviço (0 = automática)", existing.Port, 0, 65535); listener.Children.Add(port.Panel);
        protocol.SelectionChanged += (_, _) => { if (profile is null && port.Input.Value is 3140 or 3180) port.Input.Value = protocol.SelectedIndex == 0 ? 3140 : 3180; };
        var auto = Toggle(listener, "Iniciar serviço automaticamente com o agente", existing.AutoStart);
        listener.Children.Add(Paragraph("A porta local recebe clientes do proxy. Ela é diferente das portas de destino permitidas e da porta publicada no túnel. SOCKS5 suporta somente TCP CONNECT, sem UDP e BIND."));
        var access = new StackPanel { Spacing = 12, Margin = new(0, 16) };
        access.Children.Add(Paragraph("A primeira regra habilitada correspondente decide permitir ou bloquear. HTTPS usa domínio/IP e porta, nunca o caminho. Privados e loopback exigem autorização explícita; metadados e link-local permanecem bloqueados."));
        var all = Toggle(access, "Permitir destinos públicos sem regra correspondente", existing.Policy.AllowAllDestinations);
        var generalPorts = Field(access, "Portas de destino permitidas", FormatServicePorts(existing.Policy.AllowedPorts, existing.Policy.AllowedPortRanges), "Ex.: 80, 443, 8000-8090. Vazio não restringe portas; as regras de destino continuam obrigatórias.");
        var connectPorts = Field(access, "Portas para CONNECT e SOCKS5 TCP", FormatServicePorts(existing.Policy.ConnectPorts, existing.Policy.ConnectPortRanges), "Limite adicional para HTTP CONNECT e SOCKS5. Ex.: 443, 8443. Vazio não adiciona restrição.");
        var rules = new List<(Control View, Func<PublishedProxyRule> Read)>();
        var ruleRows = new StackPanel { Spacing = 12 }; access.Children.Add(ruleRows);
        void RenderRules() { ruleRows.Children.Clear(); foreach (var entry in rules) ruleRows.Children.Add(entry.View); }
        void AddRule(PublishedProxyRule rule)
        {
            var panel = new StackPanel { Spacing = 10 };
            var enabled = Toggle(panel, "Regra habilitada", rule.Enabled);
            var action = Choice(panel, "Ação da regra", ["Permitir", "Bloquear"], rule.Action == PublishedProxyAction.Allow ? "Permitir" : "Bloquear");
            var kind = Choice(panel, "Tipo de destino", ["Domínio", "IP", "Rede CIDR"], new[] { "Domínio", "IP", "Rede CIDR" }[(int)rule.Kind]);
            var destination = Field(panel, "Destinos", rule.Destination, "Separe por vírgula ou quebra de linha. Sem protocolo, caminho ou *. Todos compartilham ação e portas."); destination.AcceptsReturn = true; destination.MinHeight = 76;
            var subdomains = Toggle(panel, "Incluir subdomínios", rule.IncludeSubdomains);
            var ports = Field(panel, "Portas desta regra", FormatServicePorts(rule.Ports, rule.PortRanges), "Ex.: 80,443,8000-8090. Vazio corresponde a qualquer porta, respeitando os limites gerais.");
            var privateAddresses = Toggle(panel, "Autorizar endereços privados nesta regra", rule.AllowPrivateAddresses);
            var loopback = Toggle(panel, "Autorizar loopback nesta regra (IP ou CIDR)", rule.AllowLoopback);
            void Update() { subdomains.IsEnabled = kind.SelectedIndex == 0; privateAddresses.IsEnabled = action.SelectedIndex == 0; loopback.IsEnabled = kind.SelectedIndex != 0 && action.SelectedIndex == 0; }
            kind.SelectionChanged += (_, _) => Update(); action.SelectionChanged += (_, _) => Update(); Update();
            var view = Card(panel, 14);
            void Move(int offset)
            {
                var index = rules.FindIndex(r => ReferenceEquals(r.View, view)); var target = index + offset;
                if (index < 0 || target < 0 || target >= rules.Count) return;
                (rules[index], rules[target]) = (rules[target], rules[index]); RenderRules();
            }
            var controls = new WrapPanel();
            controls.Children.Add(ActionButton("Subir regra", () => { Move(-1); return Task.CompletedTask; }));
            controls.Children.Add(ActionButton("Descer regra", () => { Move(1); return Task.CompletedTask; }));
            controls.Children.Add(ActionButton("Remover regra", () => { rules.RemoveAll(r => ReferenceEquals(r.View, view)); RenderRules(); return Task.CompletedTask; }));
            panel.Children.Add(controls);
            rules.Add((view, () =>
            {
                var parsed = ParseServicePorts(ports.Text);
                return rule with { Enabled = enabled.IsChecked == true, Action = (PublishedProxyAction)action.SelectedIndex, Kind = (DestinationKind)kind.SelectedIndex, Destination = destination.Text?.Trim().ToLowerInvariant() ?? "", IncludeSubdomains = subdomains.IsChecked == true, Ports = parsed.Ports, PortRanges = parsed.Ranges, AllowPrivateAddresses = privateAddresses.IsEnabled && privateAddresses.IsChecked == true, AllowLoopback = loopback.IsEnabled && loopback.IsChecked == true };
            }
            )); RenderRules();
        }
        foreach (var rule in existing.Policy.Rules) AddRule(rule);
        access.Children.Add(ActionButton("Adicionar regra de acesso", () => { AddRule(new()); return Task.CompletedTask; }));
        var limits = new StackPanel { Spacing = 12, Margin = new(0, 16) };
        var clients = NumberField("Limite de clientes", existing.Policy.MaxClients, 1, 1024); limits.Children.Add(clients.Panel);
        var connect = NumberField("Timeout de conexão (s)", existing.Policy.ConnectTimeoutSeconds, 1, 60); limits.Children.Add(connect.Panel);
        var idle = NumberField("Timeout sem tráfego (s)", existing.Policy.IdleTimeoutSeconds, 10, 3600); limits.Children.Add(idle.Panel);
        tabs.ItemsSource = new[] { new TabItem { Header = "Serviço", Content = listener }, new TabItem { Header = "Regras de acesso", Content = access }, new TabItem { Header = "Limites", Content = limits } };
        tabs.SelectedIndex = 0;
        footer.Children.Add(ActionButton("Salvar serviço", async () =>
        {
            var allowed = ParseServicePorts(generalPorts.Text); var connects = ParseServicePorts(connectPorts.Text);
            var candidate = existing with
            {
                Name = name.Text?.Trim() ?? "",
                Protocol = protocol.SelectedIndex == 0 ? ProxyProtocol.Http : ProxyProtocol.Socks5,
                LocalAddress = address.SelectedItem as string ?? "127.0.0.1",
                Port = (int)(port.Input.Value ?? 0),
                AutoStart = auto.IsChecked == true,
                Policy = new() { Rules = rules.Select(r => r.Read()).ToArray(), AllowAllDestinations = all.IsChecked == true, AllowedPorts = allowed.Ports, AllowedPortRanges = allowed.Ranges, ConnectPorts = connects.Ports, ConnectPortRanges = connects.Ranges, MaxClients = (int)(clients.Input.Value ?? 128), ConnectTimeoutSeconds = (int)(connect.Input.Value ?? 10), IdleTimeoutSeconds = (int)(idle.Input.Value ?? 120) }
            };
            if (await _model.SaveServiceAsync(candidate)) dialog.Close();
        }, true));
        await dialog.ShowDialog(this);
    }

    internal static (int[] Ports, ProxyPortRange[] Ranges) ParseServicePorts(string? text)
    {
        var ports = new List<int>(); var ranges = new List<ProxyPortRange>();
        foreach (var part in SplitRemoteValues(text))
        {
            var bounds = part.Split('-');
            if (bounds.Length == 1 && int.TryParse(bounds[0], out var port) && port is >= 1 and <= 65535) ports.Add(port);
            else if (bounds.Length == 2 && int.TryParse(bounds[0], out var start) && int.TryParse(bounds[1], out var end) && start >= 1 && end <= 65535 && start <= end) ranges.Add(new() { Start = start, End = end });
            else throw new ToolkitException("INVALID_PORT");
        }
        return (ports.Distinct().ToArray(), ranges.Distinct().ToArray());
    }

    private static string FormatServicePorts(int[] ports, ProxyPortRange[] ranges) => string.Join(", ", ports.Select(p => p.ToString()).Concat(ranges.Select(r => $"{r.Start}-{r.End}")));
}
