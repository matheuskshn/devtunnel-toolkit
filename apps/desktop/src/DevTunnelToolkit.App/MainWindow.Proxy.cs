using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App;

public sealed partial class MainWindow
{
    private TextBlock? _proxyClients;
    public ToolkitViewModel Model => _model;
    public void NavigateTo(string page) { _currentPage = page; RefreshView(); }

    public async Task<bool> ConfirmShutdownAsync()
    {
        Show(); Activate();
        var count = _model.Snapshot?.Connections.Count(c => c.State is not (ConnectionState.Stopped or ConnectionState.Failed)) ?? 0;
        if (!await ConfirmAsync("Encerrar DevTunnel Toolkit", $"Encerrar o Agent, {count} túnel(is), o proxy local e esta interface? Aplicativos configurados para esse proxy perderão o acesso por ele. As contas serão preservadas.")) return false;
        return await _model.ShutdownAgentAsync();
    }

    private Control ProxyPage(AgentSnapshot snapshot)
    {
        var stack = PageStack();
        var proxy = snapshot.Settings.Proxy;
        var status = new StackPanel { Spacing = 12 };
        status.Children.Add(KeyValue("Proxy HTTP / CONNECT", !proxy.Enabled ? "Desativado" : snapshot.ProxyErrorCode is { } error ? ToolkitViewModel.FriendlyError(new ToolkitException(error)) : $"{proxy.ListenAddress}:{proxy.Port}"));
        _proxyClients = Text(snapshot.ProxyActiveConnections.ToString());
        var clients = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        clients.Children.Add(Text("Clientes conectados ao proxy", color: "Muted")); clients.Children.Add(_proxyClients);
        status.Children.Add(clients);
        status.Children.Add(Paragraph("Clientes conectados indica conexões locais aceitas, não confirma acesso ao destino. O serviço é HTTP / CONNECT, mesmo quando a regra usa um proxy remoto SOCKS5."));
        status.Children.Add(KeyValue("Sem regra correspondente", proxy.DefaultAction == RouteAction.Direct ? "Conexão direta" : "Bloqueado"));
        status.Children.Add(Paragraph("Configure este endereço como proxy fixo nos aplicativos. O Toolkit não altera o proxy do sistema, não instala certificados e não descriptografa HTTPS."));
        status.Children.Add(ActionButton(proxy.Enabled ? "Parar proxy local" : "Iniciar proxy local", async () => await _model.ControlAsync(proxy.Enabled ? "proxy.stop" : "proxy.start"), true));
        var rows = new StackPanel { Spacing = 8 };
        foreach (var (rule, index) in proxy.Rules.Select((r, i) => (r, i)))
            rows.Children.Add(KeyValue($"{index + 1}. {rule.Name}", $"{(rule.Enabled ? "Ativa" : "Inativa")} · {rule.Destination}:{(rule.DestinationPort == 0 ? "*" : rule.DestinationPort)} → {rule.Action} · fallback {(rule.DirectFallback ? "direto" : "não")}"));
        if (proxy.Rules.Length == 0) rows.Children.Add(Paragraph("Sem regras. Use Editar proxy para cadastrar destinos e a ação padrão."));
        stack.Children.Add(Section("Proxy local", "O serviço permanece ativo enquanto o Agent estiver em execução.", status));
        stack.Children.Add(Section("Regras de roteamento", "A primeira regra correspondente vence. IP/CIDR se aplica a destinos informados como IP, sem resolver DNS local para classificar domínios.", rows));
        var test = new StackPanel { Spacing = 10 };
        var host = Field(test, "Destino para testar", "example.org");
        var port = NumberField("Porta do destino", 443, 1, 65535); test.Children.Add(port.Panel);
        var transport = Choice(test, "Protocolo do teste de acesso", new[] { "HTTPS", "HTTP" }, "HTTPS");
        var result = Paragraph("Testar regra apenas simula a rota. Verificar acesso envia HEAD / pelo proxy local, sem seguir redirecionamentos, enviar credenciais ou baixar o conteúdo."); test.Children.Add(result);
        test.Children.Add(ActionButton("Testar regra", async () =>
        {
            var route = await _model.TestRouteAsync(host.Text?.Trim() ?? "", (int)(port.Input.Value ?? 443));
            if (route is null) { result.Text = _model.Message; return; }
            var name = proxy.Rules.FirstOrDefault(r => r.Id == route.RuleId)?.Name ?? "Padrão";
            result.Text = $"{name}: {DescribeRoute(route)} · {route.Reason ?? "regra aplicada"}";
        }));
        async Task CheckAccess(bool upstreamOnly)
        {
            result.Text = "Verificando a rota, conectando pelo proxy e aguardando resposta…";
            var probe = await _model.ProbeAsync(host.Text?.Trim() ?? "", (int)(port.Input.Value ?? 443), transport.SelectedIndex == 0, upstreamOnly);
            result.Text = probe is null ? _model.Message : $"{(probe.ThroughLocalProxy ? "Pelo proxy local" : "Direto no proxy remoto pelo túnel, sem fallback direto")}\n{DescribeRoute(probe.Route)}\n{ProbeDescription(probe)} · {probe.ElapsedMilliseconds} ms";
        }
        test.Children.Add(ActionButton("Verificar acesso", () => CheckAccess(false)));
        test.Children.Add(ActionButton("Verificar proxy remoto", () => CheckAccess(true)));
        stack.Children.Add(Section("Diagnóstico do destino", "HTTPS é roteado somente por domínio/IP e porta. O teste usa a rede e pode usar conexão direta quando sua regra permite fallback.", test));
        var pac = new StackPanel { Spacing = 10 };
        pac.Children.Add(KeyValue("Modo", snapshot.Settings.Pac.Mode.ToString()));
        pac.Children.Add(KeyValue("URL", snapshot.Settings.Pac.Enabled ? $"http://127.0.0.1:{snapshot.Settings.Pac.Port}/proxy.pac" : "Desativado"));
        pac.Children.Add(ActionButton("Editar PAC", EditSettingsAsync));
        pac.Children.Add(ActionButton("Visualizar / exportar PAC", async () =>
        {
            var script = await _model.PreviewPacAsync(snapshot.Settings);
            if (script is null) return;
            var (dialog, form, footer) = Dialog("PAC gerado", 780);
            form.Children.Add(new TextBox { Text = script, IsReadOnly = true, AcceptsReturn = true, MinHeight = 350, FontFamily = new FontFamily("monospace") });
            footer.Children.Add(ActionButton("Exportar .pac", async () =>
            {
                var file = await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Exportar PAC", SuggestedFileName = "proxy.pac", DefaultExtension = "pac" });
                if (file is null) return;
                using (file)
                    await _model.RunAsync(async token =>
                    {
                        await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(script), token);
                    }, "Arquivo PAC exportado.", "Gravando o arquivo PAC…", refreshAfter: false);
            }));
            await dialog.ShowDialog(this);
        }));
        stack.Children.Add(Section("Proxy automático / PAC", "O modo Proxy local centraliza as decisões no Agent. O modo Manual serve seu script sem executá-lo no Agent.", pac));
        return stack;
    }

    private static string DescribeRoute(RouteDecision route) => route.Action switch
    {
        RouteAction.Tunnel => $"Rota prevista: túnel · {route.Protocol} em {route.Upstream?.LocalAddress}:{route.Upstream?.LocalPort} · fallback direto {(route.DirectFallback ? "permitido" : "desativado")}",
        RouteAction.Block => "Bloqueado pela regra local",
        _ => "Rota prevista: conexão direta"
    };

    private static string ProbeDescription(ProxyProbeResult probe)
    {
        var status = probe.HttpStatus is { } code ? $"HTTP {code}. " : "";
        return status + (probe.Code switch
        {
            "PROXY_PROBE_HTTP_RESPONSE" => "Resposta recebida. O teste não valida o login ou o conteúdo do aplicativo.",
            "PROXY_PROBE_ACCESS_DENIED" => "Acesso negado. Revise a autenticação e as permissões no destino/proxy remoto.",
            "PROXY_UPSTREAM_AUTH_REQUIRED" => "O proxy remoto exige autenticação (407).",
            "PROXY_PROBE_GATEWAY_ERROR" => "O proxy/destino retornou falha de gateway ou indisponibilidade.",
            "PROXY_PROBE_TLS_FAILED" => "A negociação TLS falhou ou o canal foi interrompido. Compare com o teste do proxy remoto e verifique protocolo e certificados. A validação TLS permanece ativa.",
            "PROXY_PROBE_CONNECT_FAILED" => "O proxy recusou ou não concluiu o CONNECT. Revise as portas e o protocolo HTTP/SOCKS5 da regra.",
            "PROXY_PROBE_TIMEOUT" => "Tempo de espera esgotado antes de receber uma resposta. Revise rota, túnel e porta de destino.",
            "PROXY_PROBE_DNS_FAILED" => "Não foi possível resolver o endereço.",
            "PROXY_ROUTE_BLOCKED" or "TUNNEL_UNAVAILABLE" or "LOCAL_PROXY_LOOP" => "A política local bloqueou a conexão. Revise a regra e a disponibilidade do túnel.",
            _ => "Falha de conexão ou resposta inválida. Revise a porta, o protocolo e os logs."
        });
    }

    private async Task EditProxyAsync()
    {
        if (_model.Snapshot is not { } snapshot) return;
        var settings = snapshot.Settings.Proxy;
        var (dialog, form, footer) = Dialog("Proxy local e roteamento", 850);
        var enabled = Toggle(form, "Ativar proxy local", settings.Enabled);
        var address = Choice(form, "Endereço do proxy local", new[] { "127.0.0.1", "::1" }, settings.ListenAddress);
        var port = NumberField("Porta do proxy local", settings.Port, 1024, 65535); form.Children.Add(port.Panel);
        var defaults = Choice(form, "Quando nenhuma regra corresponder", new[] { "Direto", "Bloquear" }, settings.DefaultAction == RouteAction.Direct ? "Direto" : "Bloquear");
        var connect = NumberField("Timeout de conexão (s)", settings.ConnectTimeoutSeconds, 1, 60); form.Children.Add(connect.Panel);
        var idle = NumberField("Timeout sem tráfego (s)", settings.IdleTimeoutSeconds, 10, 3600); form.Children.Add(idle.Panel);
        var limit = NumberField("Máximo de clientes do proxy", settings.MaxClients, 1, 1024); form.Children.Add(limit.Panel);
        form.Children.Add(Paragraph("Pare o proxy antes de alterar timeouts e limites. Regras e rota padrão podem ser alteradas enquanto ele está ativo e valem para novas requisições/conexões."));
        form.Children.Add(Paragraph("Fallback direto é opcional por regra e pode enviar DNS/tráfego pela rede normal. Não migra conexões existentes e não contorna respostas 403/407."));
        var editors = new List<(ProxyRoute Original, Func<ProxyRoute> Read, Control View)>();
        var list = new StackPanel { Spacing = 14 }; form.Children.Add(list);
        void Render() { list.Children.Clear(); foreach (var item in editors) list.Children.Add(item.View); }
        void AddRule(ProxyRoute rule)
        {
            var panel = new StackPanel { Spacing = 10 };
            var name = Field(panel, "Nome da regra", rule.Name);
            var active = Toggle(panel, "Regra ativa", rule.Enabled);
            var kind = Choice(panel, "Tipo de destino", new[] { "Domínio", "IP", "Rede CIDR" }, new[] { "Domínio", "IP", "Rede CIDR" }[(int)rule.Kind]);
            var destination = Field(panel, "Destinos", rule.Destination, "Separe por vírgula ou quebra de linha. Até 100 destinos do tipo selecionado por regra. Ex.: example.org, example.net. Mesma ação, porta e túnel para todos. Sem protocolo, caminho ou *.");
            destination.AcceptsReturn = true; destination.MinHeight = 90; destination.MaxLength = 32768;
            destination.TextWrapping = TextWrapping.Wrap;
            var subdomains = Toggle(panel, "Incluir subdomínios", rule.IncludeSubdomains);
            var targetPort = NumberField("Porta do destino (0 = qualquer)", rule.DestinationPort, 0, 65535); panel.Children.Add(targetPort.Panel);
            var action = Choice(panel, "Ação da regra", new[] { "Direto", "Túnel", "Bloquear" }, new[] { "Direto", "Túnel", "Bloquear" }[(int)rule.Action]);
            var profiles = snapshot.Tunnels;
            var profile = new ComboBox { ItemsSource = profiles.Select(t => t.CanonicalId).ToArray(), SelectedIndex = Math.Max(0, Array.FindIndex(profiles, t => t.Id == rule.ProfileId)), HorizontalAlignment = HorizontalAlignment.Stretch };
            panel.Children.Add(Labeled("Túnel do proxy remoto", profile));
            var remote = NumberField("Porta do proxy remoto", rule.RemotePort, 1, 65535); panel.Children.Add(remote.Panel);
            var protocol = Choice(panel, "Protocolo do proxy remoto", new[] { "HTTP", "SOCKS5" }, rule.Protocol == ProxyProtocol.Http ? "HTTP" : "SOCKS5");
            var fallback = Toggle(panel, "Tentar direto se o túnel estiver indisponível", rule.DirectFallback);
            void Update()
            {
                var tunnel = action.SelectedIndex == (int)RouteAction.Tunnel;
                profile.IsEnabled = remote.Input.IsEnabled = protocol.IsEnabled = fallback.IsEnabled = tunnel;
                subdomains.IsEnabled = kind.SelectedIndex == (int)DestinationKind.Domain;
            }
            action.SelectionChanged += (_, _) => Update(); kind.SelectionChanged += (_, _) => Update(); Update();
            ProxyRoute Read() => rule with
            {
                Name = name.Text?.Trim() ?? "",
                Enabled = active.IsChecked == true,
                Kind = (DestinationKind)kind.SelectedIndex,
                Destination = destination.Text?.Trim().ToLowerInvariant() ?? "",
                IncludeSubdomains = subdomains.IsChecked == true,
                DestinationPort = (int)(targetPort.Input.Value ?? 0),
                Action = (RouteAction)action.SelectedIndex,
                ProfileId = profile.SelectedIndex >= 0 && profile.SelectedIndex < profiles.Length ? profiles[profile.SelectedIndex].Id : "",
                RemotePort = (int)(remote.Input.Value ?? 3140),
                Protocol = (ProxyProtocol)protocol.SelectedIndex,
                DirectFallback = fallback.IsChecked == true
            };
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            void Move(int offset)
            {
                var index = editors.FindIndex(x => x.Original.Id == rule.Id); var target = index + offset;
                if (target < 0 || target >= editors.Count) return;
                (editors[index], editors[target]) = (editors[target], editors[index]); Render();
            }
            controls.Children.Add(ActionButton("Subir regra", () => { Move(-1); return Task.CompletedTask; }));
            controls.Children.Add(ActionButton("Descer regra", () => { Move(1); return Task.CompletedTask; }));
            controls.Children.Add(ActionButton("Remover regra", () => { editors.RemoveAll(x => x.Original.Id == rule.Id); Render(); return Task.CompletedTask; }));
            panel.Children.Add(controls); editors.Add((rule, Read, Card(panel, 14))); Render();
        }
        foreach (var rule in settings.Rules) AddRule(rule);
        form.Children.Add(ActionButton("Adicionar regra de proxy", () => { AddRule(new() { Action = RouteAction.Direct }); return Task.CompletedTask; }));
        var error = Paragraph(""); form.Children.Add(error);
        footer.Children.Add(ActionButton("Salvar proxy", async () =>
        {
            var updated = settings with { Enabled = enabled.IsChecked == true, ListenAddress = (string)address.SelectedItem!, Port = (int)(port.Input.Value ?? 8888), DefaultAction = defaults.SelectedIndex == 0 ? RouteAction.Direct : RouteAction.Block, ConnectTimeoutSeconds = (int)(connect.Input.Value ?? 10), IdleTimeoutSeconds = (int)(idle.Input.Value ?? 120), MaxClients = (int)(limit.Input.Value ?? 128), Rules = editors.Select(x => x.Read()).ToArray() };
            var next = snapshot.Settings with { Proxy = updated };
            if (!updated.Enabled && next.Pac.Mode == PacMode.LocalProxy) next = next with { Pac = next.Pac with { Enabled = false } };
            if (await _model.SaveSettingsAsync(next)) dialog.Close(); else error.Text = _model.Message;
        }, true));
        await dialog.ShowDialog(this);
    }

    private async Task<string?> ReadPacFileAsync(Window owner)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importar PAC manual",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Proxy Auto-Config") { Patterns = ["*.pac", "*.js", "*.txt"] }]
        });
        if (files.Count == 0) return null;
        string? result = null;
        using (files[0])
            await _model.RunAsync(async token =>
            {
                await using var stream = await files[0].OpenReadAsync();
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
                var buffer = new char[65537]; var length = await reader.ReadBlockAsync(buffer.AsMemory(), token);
                if (length > 65536) throw new ToolkitException("INVALID_MANUAL_PAC");
                result = new string(buffer, 0, length);
            }, "PAC importado. Salve as configurações para aplicar.", "Lendo e validando o arquivo PAC…", refreshAfter: false);
        return result;
    }
}
