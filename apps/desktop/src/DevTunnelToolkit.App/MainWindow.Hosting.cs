using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App;

public sealed partial class MainWindow
{
    private Control HostingPage(AgentSnapshot snapshot)
    {
        var page = PageStack();
        page.Children.Add(Card(Paragraph("Publicar vincula serviços ou portas deste computador a um túnel remoto. Inicie os proxies primeiro em Serviços. Parar a publicação não encerra os serviços. Um túnel com outro host ativo não pode ser assumido automaticamente por este Desktop.")));
        foreach (var profile in snapshot.Hosts)
        {
            var status = snapshot.HostStatuses.FirstOrDefault(s => s.ProfileId == profile.Id);
            var pending = status is not null && ToolkitViewModel.IsConnectionPending(status.State);
            var active = pending || status?.State is ConnectionState.Connected or ConnectionState.Degraded;
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(Text(profile.CanonicalId, 16, FontWeight.SemiBold));
            content.Children.Add(Paragraph($"{(profile.Alias.Length == 0 ? "" : profile.Alias + " · ")}{snapshot.Accounts.FirstOrDefault(a => a.Id == profile.AccountId)?.DisplayName ?? "Conta indisponível"}"));
            content.Children.Add(KeyValue("Publicação neste Desktop", StateLabel(status?.State ?? ConnectionState.Stopped)));
            foreach (var port in profile.Ports)
            {
                var actual = status?.Ports?.FirstOrDefault(p => p.RemotePort == port.RemotePort);
                var service = snapshot.Services.FirstOrDefault(s => s.Id == port.ServiceId);
                var serviceState = snapshot.ServiceStatuses.FirstOrDefault(s => s.ServiceId == port.ServiceId);
                var localPort = serviceState?.Port ?? service?.Port ?? port.LocalPort;
                var localAddress = serviceState?.LocalAddress ?? service?.LocalAddress ?? port.LocalAddress;
                var address = actual is not null ? $"{actual.LocalAddress}:{actual.LocalPort}" : localPort == 0 ? $"{localAddress} · porta automática ao iniciar" : $"{localAddress}:{localPort}";
                content.Children.Add(KeyValue($"Porta publicada {port.RemotePort}", service is not null ? $"{service.Name} · {address}" : $"TCP local · {address}"));
            }
            content.Children.Add(KeyValue("Recurso remoto expira", RemoteDate(status?.ExpiresAt)));
            content.Children.Add(KeyValue("Token de host expira", RemoteDate(status?.HostTokenExpiresAt)));
            content.Children.Add(new ActivityPanel(_model, () => _model.HostActivity(profile.Id)));
            var actions = new WrapPanel();
            var startStop = ActionButton(active ? pending ? "Cancelar publicação" : "Parar publicação" : "Iniciar publicação", async () => await _model.HostActionAsync(active ? "host.stop" : "host.start", profile.Id), !active);
            startStop.IsEnabled = status?.State != ConnectionState.Stopping; actions.Children.Add(startStop);
            var restart = ActionButton("Reiniciar publicação", async () =>
            {
                if (await ConfirmAsync("Reiniciar publicação", "Interromper a publicação deste computador e iniciá-la novamente? Conexões existentes podem cair; serviços e recurso remoto serão preservados. Serviços vinculados precisam estar em execução."))
                    await _model.HostActionAsync("host.restart", profile.Id);
            });
            restart.IsEnabled = !pending; actions.Children.Add(restart);
            actions.Children.Add(ActionButton("Editar vínculos", () => EditHostAsync(profile)));
            actions.Children.Add(ActionButton("Gerenciar recurso remoto", () => RemoteDetailsAsync(profile.AccountId, new(profile.TunnelId, profile.ClusterId))));
            content.Children.Add(actions); page.Children.Add(Card(content));
        }
        if (snapshot.Hosts.Length == 0) page.Children.Add(Section("Publique serviços ou portas locais", "Vincule um serviço gerenciado ou um endpoint TCP existente.",
            Paragraph("Use Nova publicação. Escolha um túnel da sua conta, adicione os vínculos, prepare as portas e inicie. Para acessar um túnel publicado em outro host, use Conexões.")));
        page.Children.Add(HostingDiagnostics(snapshot));
        return page;
    }

    private Control HostingDiagnostics(AgentSnapshot snapshot)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(Paragraph("Fixture TCP do Toolkit para testar ida e volta. Não é o protocolo echo/ping do CLI oficial. Nenhum túnel é criado ou alterado automaticamente. A fixture não expõe arquivos nem executa comandos."));
        content.Children.Add(KeyValue("Fixture neste computador", snapshot.DiagnosticEcho.Running ? $"127.0.0.1:{snapshot.DiagnosticEcho.Port}" : "Parada"));
        var port = NumberField("Porta local da fixture (0 = automática)", 0, 0, 65535); content.Children.Add(port.Panel);
        content.Children.Add(ActionButton(snapshot.DiagnosticEcho.Running ? "Parar fixture TCP" : "Iniciar fixture TCP", async () => await _model.SetEchoAsync(!snapshot.DiagnosticEcho.Running, (int)(port.Input.Value ?? 0))));
        content.Children.Add(Paragraph("Para medir o relay: inicie uma fixture no host, publique sua porta como serviço TCP, conecte um perfil cliente ao mesmo túnel e selecione abaixo a porta remota mapeada. O teste envia somente um desafio aleatório e confere o retorno."));
        var profiles = snapshot.Tunnels.Where(t => snapshot.Connections.Any(c => c.ProfileId == t.Id && c.State == ConnectionState.Connected)).ToArray();
        if (profiles.Length > 0)
        {
            var profile = Choice(content, "Perfil cliente conectado para diagnóstico", profiles.Select(t => t.CanonicalId).ToArray(), profiles[0].CanonicalId);
            var remote = NumberField("Porta remota da fixture", 8080, 1, 65535); content.Children.Add(remote.Panel);
            var result = Paragraph("Nenhum teste TCP realizado nesta tela."); content.Children.Add(result);
            content.Children.Add(ActionButton("Medir ida e volta TCP", async () =>
            {
                var probe = await _model.PingEchoAsync(profiles[profile.SelectedIndex].Id, (int)(remote.Input.Value ?? 0));
                result.Text = probe is null ? _model.Message : $"{(probe.Succeeded ? "Desafio validado" : "Teste falhou")} · {probe.ElapsedMilliseconds} ms. " +
                    (probe.ErrorCode is null ? "" : ToolkitViewModel.FriendlyError(new ToolkitException(probe.ErrorCode)));
            }));
        }
        else content.Children.Add(Paragraph("Conecte um perfil cliente em Conexões para habilitar a medição pelo mapeamento."));
        return Section("Diagnóstico TCP controlado", "Serviço local temporário e teste pelo mapeamento conectado.", content);
    }

    private async Task EditHostAsync(HostProfile? profile = null, Window? owner = null)
    {
        if (_model.Snapshot is not { } snapshot) return;
        var accounts = ManagementAccounts(snapshot);
        if (accounts.Length == 0) { _model.ReportFailure(new ToolkitException("AUTHENTICATION_REQUIRED")); return; }
        var firstService = snapshot.Services.FirstOrDefault();
        var existing = profile ?? new HostProfile { AccountId = _remoteSelectedAccountId ?? accounts[0].Id, Ports = firstService is null ? [new HostPortMapping { Kind = HostPortKind.Service, RemotePort = 8080, LocalPort = 8080 }] : [new HostPortMapping { Kind = firstService.Protocol == ProxyProtocol.Http ? HostPortKind.HttpProxy : HostPortKind.Socks5Proxy, ServiceId = firstService.Id, RemotePort = firstService.Port == 0 ? 3140 : firstService.Port, LocalAddress = firstService.LocalAddress, LocalPort = firstService.Port }] };
        var (dialog, form, footer) = Dialog("Publicar serviços por um túnel", 860, () => _model.HostActivity(existing.Id));
        var saved = snapshot.Hosts.Any(h => h.Id == existing.Id);
        var account = new ComboBox { ItemsSource = accounts.Select(a => a.DisplayName).ToArray(), SelectedIndex = Math.Max(0, Array.FindIndex(accounts, a => a.Id == existing.AccountId)), HorizontalAlignment = HorizontalAlignment.Stretch };
        form.Children.Add(Labeled("Conta do host", account));
        var canonical = Field(form, "Tunnel ID para hospedar", existing.TunnelId.Length > 0 ? existing.CanonicalId : "", "Identificador completo de um recurso que esta conta pode hospedar. Ex.: tunnel-demo.use2.");
        var found = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Consulte os túneis da conta" };
        form.Children.Add(Labeled("Selecionar recurso remoto", found));
        ManagedTunnel[] discovered = [];
        form.Children.Add(ActionButton("Consultar túneis desta conta", async () =>
        {
            if (await _model.LoadRemoteTunnelsAsync(accounts[account.SelectedIndex].Id))
            { discovered = _model.RemoteTunnels; found.ItemsSource = discovered.Select(t => t.Target.CanonicalId).ToArray(); }
        }));
        form.Children.Add(RemoteCancellationControl());
        found.SelectionChanged += (_, _) => { if (found.SelectedIndex >= 0 && found.SelectedIndex < discovered.Length) canonical.Text = discovered[found.SelectedIndex].Target.CanonicalId; };
        account.SelectionChanged += (_, _) => { found.ItemsSource = Array.Empty<string>(); discovered = []; };
        form.Children.Add(Paragraph("Crie novos recursos em DevTunnel > Túneis da conta. Salvar os vínculos não altera o servidor. Um host ativo em outro computador impede iniciar esta publicação. Não é possível dividir o mesmo túnel entre Hub e Desktop simultaneamente."));
        var alias = Field(form, "Apelido local da publicação", existing.Alias);
        var auto = Toggle(form, "Publicar automaticamente ao iniciar o agente", existing.AutoStart);
        var reconnect = Toggle(form, "Reconectar a publicação após interrupções", existing.Reconnect.Enabled);
        var initial = NumberField("Intervalo inicial de reconexão (s)", existing.Reconnect.InitialDelaySeconds, 1, 300); form.Children.Add(initial.Panel);
        var maximum = NumberField("Intervalo máximo de reconexão (s)", existing.Reconnect.MaxDelaySeconds, 1, 3600); form.Children.Add(maximum.Panel);
        var retries = NumberField("Tentativas máximas de reconexão", existing.Reconnect.MaxRetries, 0, 1000); form.Children.Add(retries.Panel);
        form.Children.Add(Text("Portas e vínculos", 17, FontWeight.SemiBold));
        form.Children.Add(Paragraph("Escolha um serviço criado em Serviços ou uma porta TCP existente em loopback. A porta publicada no túnel pode ser diferente da porta local. A política do proxy é editada somente no serviço, e compartilhada entre todos os seus vínculos."));
        var ports = new List<(Control View, Func<HostPortMapping> Read)>();
        var portRows = new StackPanel { Spacing = 12 }; form.Children.Add(portRows);
        void AddPort(HostPortMapping mapping)
        {
            var panel = new StackPanel { Spacing = 10 };
            var kind = Choice(panel, "Tipo de vínculo", ["Serviço gerenciado", "Porta TCP local"], mapping.ServiceId is not null ? "Serviço gerenciado" : "Porta TCP local");
            var remote = NumberField("Porta publicada no túnel", mapping.RemotePort, 1, 65535); panel.Children.Add(remote.Panel);
            var services = snapshot.Services;
            var service = new ComboBox { ItemsSource = services.Select(s => $"{s.Name} · {(s.Protocol == ProxyProtocol.Http ? "HTTP/CONNECT" : "SOCKS5")}").ToArray(), SelectedIndex = services.Length == 0 ? -1 : Math.Max(0, Array.FindIndex(services, s => s.Id == mapping.ServiceId)), HorizontalAlignment = HorizontalAlignment.Stretch };
            var serviceField = Labeled("Serviço gerenciado", service); panel.Children.Add(serviceField);
            var serviceState = Paragraph(""); panel.Children.Add(serviceState);
            var local = NumberField("Porta TCP existente neste computador", mapping.LocalPort == 0 ? 8080 : mapping.LocalPort, 1, 65535); panel.Children.Add(local.Panel);
            var address = Choice(panel, "Endereço local do serviço", ["127.0.0.1", "::1"], mapping.LocalAddress);
            void UpdateBinding()
            {
                var managed = kind.SelectedIndex == 0; serviceField.IsVisible = managed; serviceState.IsVisible = managed; local.Panel.IsVisible = !managed; address.IsEnabled = !managed;
                if (service.SelectedIndex >= 0 && service.SelectedIndex < services.Length)
                {
                    var selectedService = services[service.SelectedIndex]; var state = snapshot.ServiceStatuses.FirstOrDefault(s => s.ServiceId == selectedService.Id);
                    serviceState.Text = $"{selectedService.LocalAddress}:{(state?.Port ?? selectedService.Port)} · {(state?.State == ConnectionState.Connected ? "Em execução" : "Inicie este serviço antes de publicar")}";
                }
                else serviceState.Text = "Nenhum serviço disponível. Crie um proxy em Serviços ou escolha Porta TCP local.";
            }
            kind.SelectionChanged += (_, _) => UpdateBinding(); service.SelectionChanged += (_, _) => UpdateBinding(); UpdateBinding();
            var view = Card(panel, 14);
            panel.Children.Add(ActionButton("Remover publicação", () => { portRows.Children.Remove(view); ports.RemoveAll(p => ReferenceEquals(p.View, view)); return Task.CompletedTask; }));
            ports.Add((view, () =>
            {
                if (kind.SelectedIndex == 1) return new HostPortMapping { Kind = HostPortKind.Service, RemotePort = (int)(remote.Input.Value ?? 0), LocalPort = (int)(local.Input.Value ?? 0), LocalAddress = address.SelectedItem as string ?? "127.0.0.1" };
                if (service.SelectedIndex < 0 || service.SelectedIndex >= services.Length) throw new ToolkitException("SERVICE_NOT_FOUND");
                var selectedService = services[service.SelectedIndex];
                return new HostPortMapping { Kind = selectedService.Protocol == ProxyProtocol.Http ? HostPortKind.HttpProxy : HostPortKind.Socks5Proxy, ServiceId = selectedService.Id, RemotePort = (int)(remote.Input.Value ?? 0), LocalPort = selectedService.Port, LocalAddress = selectedService.LocalAddress };
            }
            ));
            portRows.Children.Add(view);
        }
        foreach (var mapping in existing.Ports) AddPort(mapping);
        var addPorts = new WrapPanel();
        var addService = ActionButton("Vincular serviço", () => { if (firstService is not null) AddPort(new() { Kind = firstService.Protocol == ProxyProtocol.Http ? HostPortKind.HttpProxy : HostPortKind.Socks5Proxy, ServiceId = firstService.Id, RemotePort = firstService.Port == 0 ? 3140 : firstService.Port }); return Task.CompletedTask; }); addService.IsEnabled = firstService is not null; addPorts.Children.Add(addService);
        addPorts.Children.Add(ActionButton("Vincular porta TCP", () => { AddPort(new() { Kind = HostPortKind.Service, RemotePort = 8080, LocalPort = 8080 }); return Task.CompletedTask; }));
        form.Children.Add(addPorts);
        form.Children.Add(Paragraph("Os acessos são associados ao perfil/listener. O login do host não prova a identidade de cada cliente. Use túneis separados e permissões remotas restritas quando precisar separar consumidores. Salvar encerra a hospedagem ativa neste computador. Depois, você precisará iniciá-la novamente; não há reinício automático."));
        HostProfile Read()
        {
            var value = canonical.Text?.Trim() ?? ""; var index = value.LastIndexOf('.');
            if (index <= 0 || index == value.Length - 1) throw new ToolkitException("INVALID_TUNNEL_ID");
            var candidate = existing with
            {
                AccountId = accounts[account.SelectedIndex].Id,
                TunnelId = value[..index],
                ClusterId = value[(index + 1)..],
                Alias = alias.Text?.Trim() ?? "",
                AutoStart = auto.IsChecked == true,
                Reconnect = new() { Enabled = reconnect.IsChecked == true, InitialDelaySeconds = (int)(initial.Input.Value ?? 2), MaxDelaySeconds = (int)(maximum.Input.Value ?? 60), MaxRetries = (int)(retries.Input.Value ?? 10) },
                Ports = ports.Select(p => p.Read()).ToArray()
            };
            HostingValidation.Profile(candidate); return candidate;
        }
        async Task<bool> SaveCheckedAsync(HostProfile candidate)
        {
            var status = _model.Snapshot?.HostStatuses.FirstOrDefault(s => s.ProfileId == candidate.Id);
            if (status is not null && (status.State is ConnectionState.Connected or ConnectionState.Degraded || ToolkitViewModel.IsConnectionPending(status.State)) &&
                !await ConfirmAsync("Salvar hospedagem ativa", "Salvar encerra a publicação deste computador e suas conexões. Você precisará iniciar a hospedagem novamente depois de salvar. O recurso remoto será preservado. Deseja continuar?", dialog)) return false;
            return await _model.SaveHostAsync(candidate);
        }
        footer.Children.Add(ActionButton("Salvar perfil", async () => { if (await SaveCheckedAsync(Read())) dialog.Close(); }, true));
        var operations = new WrapPanel();
        operations.Children.Add(ActionButton("Salvar e preparar portas remotas", async () =>
        {
            var candidate = Read();
            if (!await ConfirmRemoteAsync("Preparar portas para hospedagem", candidate.AccountId, [candidate.CanonicalId], $"Salvar este perfil e criar somente as portas remotas ausentes: {string.Join(", ", candidate.Ports.Select(p => p.RemotePort))}. Portas existentes não serão alteradas. O túnel precisa existir.", dialog)) return;
            if (await SaveCheckedAsync(candidate)) await _model.HostActionAsync("host.prepare", candidate.Id, candidate.CanonicalId);
        }));
        if (saved)
        {
            operations.Children.Add(ActionButton("Parar hospedagem local", async () => await _model.HostActionAsync("host.stop", existing.Id)));
            operations.Children.Add(ActionButton("Remover perfil de hospedagem", async () =>
            {
                if (!await ConfirmAsync("Remover hospedagem local", "Remove somente este perfil e encerra a publicação neste computador. O túnel, portas e permissões no servidor serão preservados.", dialog)) return;
                if (await _model.HostActionAsync("host.remove", existing.Id)) dialog.Close();
            }));
        }
        form.Children.Add(operations);
        await dialog.ShowDialog(owner ?? this);
    }

}
