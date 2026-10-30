using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App;

public sealed partial class MainWindow
{
    private string? _remoteSelectedAccountId;
    private string _remoteSearch = "";
    private readonly HashSet<string> _remoteSelection = [];

    private AccountProfile[] ManagementAccounts(AgentSnapshot snapshot) => snapshot.Accounts.Where(a => a.Provider != AccountProvider.TunnelToken).ToArray();

    private Control RemotePage(AgentSnapshot snapshot)
    {
        var page = PageStack();
        page.Children.Add(Paragraph("Recursos no servidor Dev Tunnels. Alterações afetam outros dispositivos. Remover um perfil local ou parar sua hospedagem são ações independentes."));
        var accounts = ManagementAccounts(snapshot);
        if (accounts.Length == 0) { page.Children.Add(Card(Paragraph("Adicione e autentique uma conta em Contas para gerenciar recursos remotos. Tokens de conexão não concedem administração da conta."))); return page; }
        if (!accounts.Any(a => a.Id == _remoteSelectedAccountId)) _remoteSelectedAccountId = accounts[0].Id;
        var controls = new StackPanel { Spacing = 12 };
        var choice = new ComboBox { ItemsSource = accounts.Select(a => $"{a.DisplayName} · {ProviderLabel(a.Provider)}").ToArray(), SelectedIndex = Array.FindIndex(accounts, a => a.Id == _remoteSelectedAccountId), HorizontalAlignment = HorizontalAlignment.Stretch };
        controls.Children.Add(Labeled("Conta de gerenciamento", choice));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(ActionButton("Consultar servidor", async () => { if (_remoteSelectedAccountId is { } id) await _model.LoadRemoteTunnelsAsync(id); }, true));
        actions.Children.Add(ActionButton("Clusters disponíveis", async () => { if (_remoteSelectedAccountId is { } id) await RemoteClustersAsync(id); }));
        actions.Children.Add(RemoteCancellationControl());
        var delete = ActionButton("Excluir selecionados no servidor", async () =>
        {
            if (_remoteSelectedAccountId is not { } accountId || _model.RemoteAccountId != accountId) return;
            var targets = _model.RemoteTunnels.Where(t => _remoteSelection.Contains(t.Target.CanonicalId)).Select(t => t.Target).OrderBy(t => t.CanonicalId).ToArray();
            if (targets.Length == 0) return;
            if (targets.Length > 25) throw new ToolkitException("REMOTE_BATCH_INVALID");
            var ids = targets.Select(t => t.CanonicalId).ToArray();
            if (!await ConfirmRemoteAsync("Excluir túneis selecionados", accountId, ids,
                "Todos os túneis listados serão excluídos no servidor. Clientes e hosts podem perder acesso. A operação não é atômica: em uma falha, atualize a lista antes de repetir.")) return;
            var results = await _model.DeleteRemoteBatchAsync(accountId, targets, ids);
            _remoteSelection.Clear();
            await _model.LoadRemoteTunnelsAsync(accountId);
            if (results is not null)
            {
                var (resultDialog, resultForm, _) = Dialog("Resultado das exclusões", 740);
                foreach (var result in results) resultForm.Children.Add(KeyValue(result.CanonicalId,
                    result.Succeeded ? "Excluído no servidor" : ToolkitViewModel.FriendlyError(new ToolkitException(result.ErrorCode ?? "REMOTE_DELETE_FAILED"))));
                await resultDialog.ShowDialog(this);
            }
        });
        actions.Children.Add(delete); controls.Children.Add(actions);
        var search = Field(controls, "Buscar túneis remotos", _remoteSearch, "Filtra o resultado já consultado por ID, nome, descrição ou etiqueta.");
        var rows = new StackPanel { Spacing = 10 };
        void Render()
        {
            rows.Children.Clear();
            delete.IsEnabled = _remoteSelection.Count > 0;
            if (_model.RemoteAccountId != _remoteSelectedAccountId)
            { rows.Children.Add(Paragraph("Selecione Consultar servidor para carregar os recursos desta conta.")); return; }
            _remoteSelection.IntersectWith(_model.RemoteTunnels.Select(t => t.Target.CanonicalId));
            delete.IsEnabled = _remoteSelection.Count > 0;
            var matches = _model.RemoteTunnels.Where(t => string.Join(" ", t.Target.CanonicalId, t.Name, t.Description, string.Join(" ", t.Labels)).Contains(_remoteSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var tunnel in matches)
            {
                var row = new Grid { ColumnDefinitions = new("Auto,*,Auto") };
                var selected = new CheckBox { IsChecked = _remoteSelection.Contains(tunnel.Target.CanonicalId), Margin = new(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(selected, $"Selecionar {tunnel.Target.CanonicalId}");
                selected.IsCheckedChanged += (_, _) => { if (selected.IsChecked == true) _remoteSelection.Add(tunnel.Target.CanonicalId); else _remoteSelection.Remove(tunnel.Target.CanonicalId); delete.IsEnabled = _remoteSelection.Count > 0; };
                row.Children.Add(selected);
                var details = new StackPanel { Spacing = 5 };
                details.Children.Add(Text(tunnel.Target.CanonicalId, 14, FontWeight.SemiBold));
                details.Children.Add(Paragraph($"{tunnel.Name ?? "Sem nome remoto"} · {tunnel.Ports.Length} portas · {RemoteHostLabel(snapshot, _remoteSelectedAccountId!, tunnel)}"));
                details.Children.Add(Paragraph($"Expira em: {RemoteDate(tunnel.ExpiresAt)}"));
                Grid.SetColumn(details, 1); row.Children.Add(details);
                var accountId = _remoteSelectedAccountId!;
                var manage = ActionButton("Gerenciar", () => RemoteDetailsAsync(accountId, tunnel.Target)); Grid.SetColumn(manage, 2); row.Children.Add(manage);
                rows.Children.Add(Card(row, 14));
            }
            if (matches.Length == 0) rows.Children.Add(Paragraph("Nenhum túnel corresponde ao filtro nesta consulta."));
        }
        search.TextChanged += (_, _) => { _remoteSearch = search.Text ?? ""; Render(); };
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedIndex < 0) return;
            _remoteSelectedAccountId = accounts[choice.SelectedIndex].Id; _remoteSelection.Clear(); Render();
        };
        page.Children.Add(Card(controls)); page.Children.Add(rows); Render();
        return page;
    }

    private async Task NewRemoteTunnelAsync(Window? owner = null, string? accountId = null, ManagedTunnel? existing = null)
    {
        var accounts = ManagementAccounts(_model.Snapshot!);
        if (accounts.Length == 0) { _model.ReportFailure(new ToolkitException("AUTHENTICATION_REQUIRED")); return; }
        var (dialog, form, footer) = RemoteDialog(existing is null ? "Criar túnel remoto" : "Editar túnel remoto", 720);
        var account = new ComboBox { ItemsSource = accounts.Select(a => a.DisplayName).ToArray(), SelectedIndex = Math.Max(0, Array.FindIndex(accounts, a => a.Id == (accountId ?? _remoteSelectedAccountId))), IsEnabled = existing is null, HorizontalAlignment = HorizontalAlignment.Stretch };
        form.Children.Add(Labeled("Conta proprietária", account));
        var id = Field(form, "Tunnel ID remoto", existing?.Target.TunnelId, "Na criação, vazio permite ao servidor gerar. Um ID existente não pode ser renomeado."); id.IsReadOnly = existing is not null;
        var cluster = Field(form, "Cluster", existing?.Target.ClusterId, "Vazio na criação usa a seleção do serviço. Ex.: use2."); cluster.IsReadOnly = existing is not null;
        var name = Field(form, "Nome remoto (opcional)", existing?.Name, "Nome aceito pelo serviço, independente do apelido local.");
        var description = Field(form, "Descrição", existing?.Description); description.MaxLength = 2048;
        var labels = Field(form, "Etiquetas", string.Join(", ", existing?.Labels ?? []), "Separe por vírgula ou quebra de linha."); labels.AcceptsReturn = true;
        var expiration = NumberField("Validade por inatividade (horas, 0 = padrão)", (existing?.ExpirationSeconds ?? 0) / 3600, 0, 720); form.Children.Add(expiration.Panel);
        form.Children.Add(Paragraph("A expiração absoluta é informada pelo serviço. Atividade e políticas do servidor podem influenciar a validade. O túnel será privado; permissões adicionais são configuradas em Acesso."));
        var web = RemoteWebEditor(form, existing?.WebOptions);
        footer.Children.Add(ActionButton(existing is null ? "Criar no servidor" : "Salvar no servidor", async () =>
        {
            var value = (int)(expiration.Input.Value ?? 0);
            var saved = await _model.SaveRemoteTunnelAsync(accounts[account.SelectedIndex].Id, new RemoteTunnelSpec
            {
                TunnelId = EmptyToNull(id.Text),
                ClusterId = EmptyToNull(cluster.Text),
                Name = EmptyToNull(name.Text),
                Description = description.Text?.Trim() ?? "",
                Labels = SplitRemoteValues(labels.Text),
                ExpirationSeconds = value == 0 ? null : value * 3600,
                WebOptions = web()
            }, existing?.Target);
            if (saved is not null) { _remoteSelectedAccountId = accounts[account.SelectedIndex].Id; dialog.Close(); }
        }, true));
        await dialog.ShowDialog(owner ?? this);
    }

    private async Task RemoteDetailsAsync(string accountId, RemoteTunnelTarget target)
    {
        var current = await _model.GetRemoteTunnelAsync(accountId, target);
        if (current is null) return;
        var (dialog, form, footer) = RemoteDialog(target.CanonicalId, 880);
        var tabs = new TabControl(); form.Children.Add(tabs);
        async Task Reload()
        {
            if (await _model.GetRemoteTunnelAsync(accountId, target) is { } updated) { current = updated; Render(); }
        }
        void Render()
        {
            var selected = tabs.SelectedIndex;
            var general = new StackPanel { Spacing = 12, Margin = new(0, 16) };
            general.Children.Add(KeyValue("Conta", _model.Snapshot?.Accounts.FirstOrDefault(a => a.Id == accountId)?.DisplayName ?? accountId));
            general.Children.Add(KeyValue("Tunnel ID", current.Target.CanonicalId));
            general.Children.Add(KeyValue("Nome remoto", current.Name ?? "Não configurado"));
            general.Children.Add(KeyValue("Descrição", current.Description ?? ""));
            general.Children.Add(KeyValue("Etiquetas", string.Join(", ", current.Labels)));
            general.Children.Add(KeyValue("Criado em", RemoteDate(current.CreatedAt)));
            general.Children.Add(KeyValue("Expira em", RemoteDate(current.ExpiresAt)));
            general.Children.Add(KeyValue("Validade por inatividade", current.ExpirationSeconds is { } seconds ? $"{seconds}s" : "Não informada"));
            general.Children.Add(KeyValue("Local de publicação", RemoteHostLabel(_model.Snapshot!, accountId, current)));
            general.Children.Add(Paragraph("O estado identifica a publicação ativa, não o computador que criou o recurso. Outro host ativo não informa automaticamente seu nome. Atualize esta consulta antes de iniciar uma publicação."));
            general.Children.Add(GuardRemoteMutation(ActionButton("Editar propriedades remotas", async () => { await NewRemoteTunnelAsync(dialog, accountId, current); await Reload(); }), accountId, target));
            if (RemoteInUse(accountId, target)) general.Children.Add(Paragraph("Este agente está usando o recurso. Pare suas conexões clientes e hospedagens antes de editar propriedades, portas ou permissões. O serviço continua independente em outros dispositivos."));
            general.Children.Add(Paragraph("Conectar cria um perfil de cliente neste computador. Hospedar publica serviços desta máquina. Nenhuma dessas ações transfere o controle de outro host ou executor."));
            general.Children.Add(ActionButton("Configurar cliente local", async () =>
            {
                dialog.Close();
                var local = _model.Snapshot?.Tunnels.FirstOrDefault(t => t.AccountId == accountId && t.CanonicalId == target.CanonicalId);
                await EditTunnelAsync(local ?? new TunnelProfile
                {
                    AccountId = accountId,
                    TunnelId = target.TunnelId,
                    ClusterId = target.ClusterId,
                    Mappings = current.Ports.Select(p => new PortMapping { RemotePort = p.PortNumber, LocalPort = p.PortNumber }).ToArray()
                });
            }));
            general.Children.Add(ActionButton("Configurar hospedagem local", async () => { dialog.Close(); await EditHostAsync(new HostProfile { AccountId = accountId, TunnelId = target.TunnelId, ClusterId = target.ClusterId }); }));
            var ports = new StackPanel { Spacing = 12, Margin = new(0, 16) };
            ports.Children.Add(GuardRemoteMutation(ActionButton("Criar porta remota", async () => { await EditRemotePortAsync(dialog, accountId, target); await Reload(); }, true), accountId, target));
            foreach (var port in current.Ports)
            {
                var row = new StackPanel { Spacing = 8 };
                row.Children.Add(Text($"{port.PortNumber} / {port.Protocol ?? "auto"}", 14, FontWeight.SemiBold));
                row.Children.Add(Paragraph(port.Description ?? "Sem descrição"));
                var actions = new WrapPanel();
                actions.Children.Add(GuardRemoteMutation(ActionButton("Editar porta", async () => { await EditRemotePortAsync(dialog, accountId, target, port); await Reload(); }), accountId, target));
                actions.Children.Add(ActionButton("Acesso da porta", async () => { await RemoteAccessAsync(dialog, accountId, target, port.PortNumber); await Reload(); }));
                actions.Children.Add(GuardRemoteMutation(ActionButton("Excluir porta no servidor", async () =>
                {
                    if (!await ConfirmRemoteAsync("Excluir porta remota", accountId, [target.CanonicalId], $"Excluir a porta {port.PortNumber}. Clientes que usam esta porta serão afetados.", dialog)) return;
                    if (await _model.DeleteRemotePortAsync(accountId, target, port.PortNumber, target.CanonicalId)) await Reload();
                }), accountId, target));
                row.Children.Add(actions); ports.Children.Add(Card(row, 12));
            }
            if (current.Ports.Length == 0) ports.Children.Add(Paragraph("Este túnel não possui portas remotas."));
            var access = new StackPanel { Spacing = 12, Margin = new(0, 16) };
            access.Children.Add(Paragraph("A conta do host não identifica automaticamente cada consumidor do proxy. Conceda apenas o escopo necessário. Permissões herdadas devem ser editadas no recurso de origem."));
            access.Children.Add(KeyValue("Regras no túnel", current.Access.Length.ToString()));
            access.Children.Add(ActionButton("Gerenciar acesso do túnel", async () => { await RemoteAccessAsync(dialog, accountId, target, null); await Reload(); }));
            access.Children.Add(ActionButton("Emitir token com escopo", () => RemoteTokenAsync(dialog, accountId, target, current.Ports)));
            var diagnostics = new StackPanel { Spacing = 12, Margin = new(0, 16) };
            diagnostics.Children.Add(Paragraph("Consulta autenticada ao plano de gerenciamento. O resultado não é um ping ICMP nem prova conectividade até um serviço publicado. Para validar tráfego, conecte e use os testes do proxy local."));
            var diagnostic = Paragraph("Nenhum teste executado nesta tela."); diagnostics.Children.Add(diagnostic);
            diagnostics.Children.Add(ActionButton("Testar consulta ao serviço", async () =>
            {
                var result = await _model.DiagnoseRemoteAsync(accountId, target);
                diagnostic.Text = result is null ? _model.Message : $"{(result.Success ? "Consulta concluída" : "Consulta falhou")} em {result.ElapsedMilliseconds} ms" +
                    (result.ErrorCode is null ? "." : $". {ToolkitViewModel.FriendlyError(new ToolkitException(result.ErrorCode))}");
            }));
            var clients = new StackPanel { Spacing = 12, Margin = new(0, 16) };
            clients.Children.Add(Paragraph("Perfis clientes neste dispositivo, não um inventário de todas as conexões remotas. Atualizar recurso renova os dados desta visão. O progresso de cada operação é acompanhado ao vivo."));
            foreach (var client in _model.Snapshot?.Tunnels.Where(t => t.AccountId == accountId && t.CanonicalId == target.CanonicalId) ?? [])
            {
                clients.Children.Add(KeyValue(client.Alias.Length == 0 ? client.Id : client.Alias, StateLabel(_model.Snapshot!.Connections.FirstOrDefault(c => c.ProfileId == client.Id)?.State ?? ConnectionState.Stopped)));
                clients.Children.Add(new ActivityPanel(_model, () => _model.TunnelActivity(client.Id)));
            }
            clients.Children.Add(ActionButton("Abrir conexões locais", () => { dialog.Close(); NavigateTo("Túneis"); return Task.CompletedTask; }));
            var hosting = new StackPanel { Spacing = 12, Margin = new(0, 16) };
            hosting.Children.Add(Paragraph("Vincule serviços de Serviços ou portas TCP locais a este túnel. Salvar vínculos não inicia serviços nem transfere a publicação de outro host. Um túnel tem um único host ativo no fluxo atual."));
            foreach (var host in _model.Snapshot?.Hosts.Where(h => h.AccountId == accountId && h.CanonicalId == target.CanonicalId) ?? [])
            {
                hosting.Children.Add(KeyValue(host.Alias.Length == 0 ? host.Id : host.Alias, StateLabel(_model.Snapshot!.HostStatuses.FirstOrDefault(s => s.ProfileId == host.Id)?.State ?? ConnectionState.Stopped)));
                hosting.Children.Add(new ActivityPanel(_model, () => _model.HostActivity(host.Id)));
                hosting.Children.Add(ActionButton("Editar vínculos", () => EditHostAsync(host, dialog)));
            }
            if (!(_model.Snapshot?.Hosts.Any(h => h.AccountId == accountId && h.CanonicalId == target.CanonicalId) ?? false))
            {
                var create = ActionButton("Vincular serviços ou portas deste Desktop", () => EditHostAsync(new HostProfile { AccountId = accountId, TunnelId = target.TunnelId, ClusterId = target.ClusterId, Ports = [new() { Kind = HostPortKind.Service, RemotePort = 8080, LocalPort = 8080 }] }, dialog));
                hosting.Children.Add(create);
                if (current.HostOnline == true) hosting.Children.Add(Paragraph("Outro host está ativo. Você pode preparar o perfil local, mas deverá parar o host original antes de publicar neste Desktop. Para apenas acessar suas portas, use Conexões."));
            }
            hosting.Children.Add(ActionButton("Abrir publicações neste Desktop", () => { dialog.Close(); NavigateTo("Hospedagem"); return Task.CompletedTask; }));
            var events = new StackPanel { Spacing = 10, Margin = new(0, 16) };
            events.Children.Add(Paragraph("Eventos deste agente para este recurso. Não inclui histórico de outros dispositivos. Atualize o recurso para renovar a lista."));
            var related = (_model.Snapshot?.Tunnels.Where(t => t.AccountId == accountId && t.CanonicalId == target.CanonicalId).Select(t => t.Id) ?? [])
                .Concat(_model.Snapshot?.Hosts.Where(h => h.AccountId == accountId && h.CanonicalId == target.CanonicalId).Select(h => h.Id) ?? []).Append(target.CanonicalId).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in _model.Snapshot?.Logs.Where(l => l.ProfileId is not null && related.Contains(l.ProfileId)).TakeLast(100).Reverse() ?? []) events.Children.Add(LogRow(entry));
            tabs.ItemsSource = new[] { new TabItem { Header = "Geral", Content = general }, new TabItem { Header = "Portas remotas", Content = ports }, new TabItem { Header = "Acesso e tokens", Content = access }, new TabItem { Header = "Conexões", Content = clients }, new TabItem { Header = "Publicação e vínculos", Content = hosting }, new TabItem { Header = "Eventos", Content = events }, new TabItem { Header = "Diagnóstico", Content = diagnostics } };
            foreach (var tab in tabs.ItemsSource.Cast<TabItem>()) { tab.FontSize = 13; tab.Padding = new(10, 8); }
            tabs.SelectedIndex = Math.Max(0, selected);
        }
        Render();
        footer.Children.Add(ActionButton("Atualizar recurso", Reload));
        footer.Children.Add(GuardRemoteMutation(ActionButton("Excluir no servidor", async () =>
        {
            if (!await ConfirmRemoteAsync("Excluir túnel remoto", accountId, [target.CanonicalId], "Exclusão permanente do recurso no servidor, incluindo portas e permissões. Clientes e hosts podem perder acesso. Perfis locais são preservados.", dialog)) return;
            if (await _model.DeleteRemoteTunnelAsync(accountId, target, target.CanonicalId)) dialog.Close();
        }), accountId, target));
        await dialog.ShowDialog(this);
    }

    private async Task EditRemotePortAsync(Window owner, string accountId, RemoteTunnelTarget target, ManagedPort? existing = null)
    {
        var (dialog, form, footer) = RemoteDialog(existing is null ? "Criar porta remota" : "Editar porta remota", 620);
        form.Children.Add(Paragraph($"Recurso: {target.CanonicalId}. Esta configuração é salva no servidor, não é um mapeamento local."));
        var port = NumberField("Porta remota", existing?.PortNumber ?? 3140, 1, 65535); port.Input.IsEnabled = existing is null; form.Children.Add(port.Panel);
        var protocol = Choice(form, "Protocolo", ["auto", "tcp", "http", "https", "ssh"], existing?.Protocol ?? "auto");
        var description = Field(form, "Descrição da porta", existing?.Description);
        var labels = Field(form, "Etiquetas da porta", string.Join(", ", existing?.Labels ?? []));
        var web = RemoteWebEditor(form, existing?.WebOptions);
        footer.Children.Add(ActionButton("Salvar porta no servidor", async () =>
        {
            if (await _model.SaveRemotePortAsync(accountId, target, new((int)(port.Input.Value ?? 3140), (string?)protocol.SelectedItem ?? "auto", description.Text?.Trim() ?? "", SplitRemoteValues(labels.Text), web()), existing is null)) dialog.Close();
        }, true));
        await dialog.ShowDialog(owner);
    }

    private async Task RemoteAccessAsync(Window owner, string accountId, RemoteTunnelTarget target, int? portNumber)
    {
        var (dialog, form, footer) = RemoteDialog(portNumber is { } p ? $"Acesso da porta {p}" : "Acesso do túnel", 800);
        form.Children.Add(Paragraph($"Recurso: {target.CanonicalId}. Entradas herdadas são somente leitura nesta tela."));
        var rows = new StackPanel { Spacing = 12 }; form.Children.Add(rows);
        async Task Reload()
        {
            var resource = await _model.GetRemoteTunnelAsync(accountId, target);
            if (resource is null) return;
            var entries = portNumber is { } number ? resource.Ports.FirstOrDefault(p => p.PortNumber == number)?.Access ?? [] : resource.Access;
            rows.Children.Clear();
            foreach (var entry in entries)
            {
                var card = new StackPanel { Spacing = 8 };
                card.Children.Add(Text($"{(entry.IsDeny ? "Negar" : "Permitir")} · {entry.Type} · {entry.Provider ?? "padrão"}", 14, FontWeight.SemiBold));
                card.Children.Add(Paragraph($"Identidades: {string.Join(", ", entry.Subjects)}\nEscopos: {string.Join(", ", entry.Scopes)}\nExpira: {RemoteDate(entry.ExpiresAt)}{(entry.IsInverse ? "\nRegra inversa" : "")}{(entry.IsInherited ? "\nHerdada" : "")}"));
                if (!entry.IsInherited)
                    card.Children.Add(ActionButton("Remover regra remota", async () =>
                    {
                        if (!await ConfirmRemoteAsync("Remover regra de acesso", accountId, [target.CanonicalId], "Remover exatamente esta regra pode bloquear clientes ou permitir acesso antes negado. Revise o tipo e os escopos selecionados.", dialog)) return;
                        if (await _model.DeleteRemoteAccessAsync(accountId, target, portNumber, entry.EntryId, target.CanonicalId)) await Reload();
                    }));
                rows.Children.Add(Card(card, 12));
            }
            if (entries.Length == 0) rows.Children.Add(Paragraph("Nenhuma regra explícita retornada. O proprietário mantém o acesso definido pelo serviço."));
        }
        footer.Children.Add(ActionButton("Adicionar regra", async () => { await AddRemoteAccessAsync(dialog, accountId, target, portNumber); await Reload(); }, true));
        footer.Children.Add(ActionButton("Restaurar acesso", async () =>
        {
            if (!await ConfirmRemoteAsync("Restaurar permissões", accountId, [target.CanonicalId], "Remove as regras explícitas deste recurso. Não altera regras herdadas. Clientes que dependem destas permissões podem perder acesso.", dialog)) return;
            if (await _model.ResetRemoteAccessAsync(accountId, target, portNumber, target.CanonicalId)) await Reload();
        }));
        await Reload(); await dialog.ShowDialog(owner);
    }

    private async Task AddRemoteAccessAsync(Window owner, string accountId, RemoteTunnelTarget target, int? portNumber)
    {
        var (dialog, form, footer) = RemoteDialog("Adicionar permissão remota", 700);
        var type = Choice(form, "Tipo de identidade", ["Users", "Groups", "Organizations", "Repositories", "Anonymous", "PublicKeys", "IPAddressRanges"], "Users");
        var provider = Field(form, "Provedor de identidade", "", "Identificador aceito pelo serviço: microsoft, github, ssh, ipv4, ipv6 ou service-tag, conforme o tipo.");
        var organization = Field(form, "Organização (opcional)", "");
        var subjects = Field(form, "Identidades", "", "IDs aceitos pelo provedor. Separe por vírgula ou quebra de linha."); subjects.AcceptsReturn = true;
        var scopes = RemoteScopes(form, ["connect"], portNumber is not null);
        var deny = Toggle(form, "Negar estes escopos", false);
        var inverse = Toggle(form, "Aplicar a quem não corresponde às identidades (inversa)", false);
        var expiration = Field(form, "Expira em (opcional, ISO 8601)", "", $"Data futura, no máximo 30 dias. Ex.: {DateTimeOffset.UtcNow.AddDays(7):yyyy-MM-ddTHH:mm:ssZ}. Vazio não informa expiração na regra.");
        var position = NumberField("Posição da regra (0 = primeira, vazio = final)", 0, 0, 100);
        position.Input.Value = null; form.Children.Add(position.Panel);
        var anonymous = Toggle(form, "Confirmo que acesso anônimo pode expor o recurso sem login", false);
        anonymous.IsVisible = false;
        type.SelectionChanged += (_, _) =>
        {
            anonymous.IsVisible = type.SelectedItem as string == "Anonymous"; subjects.IsEnabled = provider.IsEnabled = !anonymous.IsVisible;
            foreach (var scope in scopes.Where(s => s.Content as string != "connect"))
            { scope.IsEnabled = !anonymous.IsVisible; if (!scope.IsEnabled) scope.IsChecked = false; }
        };
        footer.Children.Add(ActionButton("Adicionar no servidor", async () =>
        {
            if (type.SelectedItem as string == "Anonymous" && anonymous.IsChecked != true) throw new ToolkitException("REMOTE_ANONYMOUS_CONFIRMATION_REQUIRED");
            DateTimeOffset? expiry = null;
            if (!string.IsNullOrWhiteSpace(expiration.Text))
            {
                if (!DateTimeOffset.TryParse(expiration.Text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)) throw new ToolkitException("INVALID_REMOTE_EXPIRATION");
                expiry = parsed;
            }
            if (await _model.SaveRemoteAccessAsync(accountId, target, portNumber, new RemoteAccessEntry
            {
                Type = type.SelectedItem as string ?? "Users",
                Provider = type.SelectedItem as string == "Anonymous" ? null : EmptyToNull(provider.Text),
                Organization = EmptyToNull(organization.Text),
                Subjects = type.SelectedItem as string == "Anonymous" ? [] : SplitRemoteValues(subjects.Text),
                Scopes = scopes.Where(s => s.IsChecked == true).Select(s => (string)s.Content!).ToArray(),
                IsDeny = deny.IsChecked == true,
                IsInverse = inverse.IsChecked == true,
                ExpiresAt = expiry,
                InsertIndex = position.Input.Value is { } index ? (int)index : null
            })) dialog.Close();
        }, true));
        await dialog.ShowDialog(owner);
    }

    private async Task RemoteTokenAsync(Window owner, string accountId, RemoteTunnelTarget target, ManagedPort[] ports)
    {
        var (dialog, form, footer) = RemoteDialog("Emitir token de acesso", 710);
        var open = true;
        form.Children.Add(Paragraph("Segredo sensível. Não será salvo nos perfis nem nos logs. Quem possuir o token terá os escopos selecionados até a validade informada pelo serviço. A área de transferência pode ser lida por outros aplicativos."));
        var portOptions = new[] { "Túnel inteiro" }.Concat(ports.Select(p => p.PortNumber.ToString())).ToArray();
        var port = Choice(form, "Escopo do recurso", portOptions, portOptions[0]);
        var scopes = RemoteScopes(form, ["connect"]);
        port.SelectionChanged += (_, _) =>
        {
            foreach (var scope in scopes.Where(s => s.Content as string is "host" or "manage:ports"))
            { scope.IsEnabled = port.SelectedIndex == 0; if (!scope.IsEnabled) scope.IsChecked = false; }
        };
        var token = Field(form, "Token emitido", ""); token.IsReadOnly = true; token.PasswordChar = '●'; token.MaxLength = 65536;
        var expiry = Paragraph("A validade será exibida após a emissão."); form.Children.Add(expiry);
        var reveal = Toggle(form, "Mostrar token nesta tela", false);
        reveal.IsCheckedChanged += (_, _) => token.PasswordChar = reveal.IsChecked == true ? '\0' : '●';
        var copy = ActionButton("Copiar token", async () =>
        {
            if (Clipboard is { } clipboard && !string.IsNullOrEmpty(token.Text))
                await _model.RunAsync(_ => clipboard.SetTextAsync(token.Text), "Token copiado para a área de transferência. Proteja este segredo.", "Copiando o token solicitado…", refreshAfter: false);
        });
        copy.IsEnabled = false; footer.Children.Add(copy);
        footer.Children.Add(ActionButton("Emitir token", async () =>
        {
            token.Text = ""; copy.IsEnabled = false;
            var result = await _model.IssueRemoteTokenAsync(accountId, target, new(scopes.Where(s => s.IsChecked == true).Select(s => (string)s.Content!).ToArray(), port.SelectedIndex <= 0 ? null : ports[port.SelectedIndex - 1].PortNumber));
            if (result is null || !open) return;
            token.Text = result.Token; copy.IsEnabled = true;
            expiry.Text = $"Expira em: {RemoteDate(result.ExpiresAt)}. Fonte da validade: {result.ExpirationSource}. Escopos: {string.Join(", ", result.Scopes)}.";
        }, true));
        dialog.Closed += (_, _) => { open = false; token.Text = ""; };
        await dialog.ShowDialog(owner);
    }

    private static CheckBox[] RemoteScopes(StackPanel form, string[] selected, bool port = false)
    {
        var panel = new WrapPanel();
        var boxes = (port ? new[] { "connect", "inspect", "manage" } : ["connect", "host", "inspect", "manage", "manage:ports"]).Select(scope => new CheckBox { Content = scope, IsChecked = selected.Contains(scope), Margin = new(0, 0, 16, 0) }).ToArray();
        foreach (var box in boxes) panel.Children.Add(box);
        form.Children.Add(Labeled("Escopos permitidos", panel)); return boxes;
    }

    private static Func<RemoteWebOptions?> RemoteWebEditor(StackPanel form, RemoteWebOptions? existing)
    {
        var enabled = Toggle(form, "Alterar parâmetros HTTP avançados", false);
        var advanced = new StackPanel { Spacing = 10, IsVisible = false };
        enabled.IsCheckedChanged += (_, _) => advanced.IsVisible = enabled.IsChecked == true;
        advanced.Children.Add(Paragraph("Somente para encaminhamento HTTP do serviço. Não altera o proxy HTTP/CONNECT publicado. Desmarcado preserva a configuração remota atual."));
        var modes = new[] { "Padrão do serviço", "Preservar original", "Definir valor" };
        var hostMode = Choice(advanced, "Tratamento do cabeçalho Host", modes, existing?.PreserveHostHeader == true ? modes[1] : !string.IsNullOrEmpty(existing?.HostHeader) ? modes[2] : modes[0]);
        var host = Field(advanced, "Valor do cabeçalho Host", existing?.HostHeader, "Nome de host, sem protocolo, caminho ou credenciais.");
        var originMode = Choice(advanced, "Tratamento do cabeçalho Origin", modes, existing?.PreserveOriginHeader == true ? modes[1] : !string.IsNullOrEmpty(existing?.OriginHeader) ? modes[2] : modes[0]);
        var origin = Field(advanced, "Valor do cabeçalho Origin", existing?.OriginHeader, "Origem HTTP(S), sem caminho, credenciais ou parâmetros. Ex.: https://example.org.");
        var changeTimeout = Toggle(advanced, "Definir timeout HTTP do serviço", existing?.RequestTimeoutSeconds is not null);
        var timeout = NumberField("Timeout HTTP (segundos, 0 = desativado)", existing?.RequestTimeoutSeconds ?? 60, 0, 3600); advanced.Children.Add(timeout.Panel);
        void Update()
        { host.IsEnabled = hostMode.SelectedIndex == 2; origin.IsEnabled = originMode.SelectedIndex == 2; timeout.Input.IsEnabled = changeTimeout.IsChecked == true; }
        hostMode.SelectionChanged += (_, _) => Update(); originMode.SelectionChanged += (_, _) => Update(); changeTimeout.IsCheckedChanged += (_, _) => Update(); Update();
        form.Children.Add(advanced);
        return () => enabled.IsChecked != true ? null : new RemoteWebOptions(
            hostMode.SelectedIndex == 2 ? host.Text?.Trim() ?? "" : "", hostMode.SelectedIndex == 1,
            originMode.SelectedIndex == 2 ? origin.Text?.Trim() ?? "" : "", originMode.SelectedIndex == 1,
            changeTimeout.IsChecked == true ? (int)(timeout.Input.Value ?? 0) : null);
    }

    private async Task RemoteClustersAsync(string accountId)
    {
        var clusters = await _model.LoadRemoteClustersAsync(accountId);
        if (clusters is null) return;
        var (dialog, form, _) = RemoteDialog("Clusters Dev Tunnels", 700);
        foreach (var cluster in clusters) form.Children.Add(KeyValue(cluster.ClusterId, $"{cluster.Location ?? "Localização não informada"}\n{cluster.ServiceUri}"));
        if (clusters.Length == 0) form.Children.Add(Paragraph("O serviço não retornou clusters."));
        await dialog.ShowDialog(this);
    }

    private (Window Window, StackPanel Form, StackPanel Footer) RemoteDialog(string title, double width)
    {
        var elements = Dialog(title, width);
        var cancel = ActionButton("Cancelar espera", () => { _model.CancelRemoteOperation(); return Task.CompletedTask; });
        void Changed(object? sender, EventArgs args) => cancel.IsVisible = _model.CanCancelRemoteOperation;
        Changed(null, EventArgs.Empty); _model.StateChanged += Changed;
        elements.Window.Closed += (_, _) => _model.StateChanged -= Changed;
        elements.Footer.Children.Add(cancel);
        return elements;
    }

    private Button RemoteCancellationControl()
    {
        var cancel = ActionButton("Cancelar espera remota", () => { _model.CancelRemoteOperation(); return Task.CompletedTask; });
        void Changed(object? sender, EventArgs args) => cancel.IsVisible = _model.CanCancelRemoteOperation;
        Changed(null, EventArgs.Empty);
        cancel.AttachedToVisualTree += (_, _) => { _model.StateChanged += Changed; Changed(null, EventArgs.Empty); };
        cancel.DetachedFromVisualTree += (_, _) => _model.StateChanged -= Changed;
        return cancel;
    }

    private async Task<bool> ConfirmRemoteAsync(string title, string accountId, string[] canonicalIds, string impact, Window? owner = null)
    {
        var (dialog, form, footer) = Dialog(title, 740);
        form.Children.Add(Paragraph($"Conta: {_model.Snapshot?.Accounts.FirstOrDefault(a => a.Id == accountId)?.DisplayName ?? accountId}\n{impact}"));
        form.Children.Add(Paragraph(string.Join("\n", canonicalIds)));
        var input = Field(form, "Confirme os identificadores", "", "Digite os identificadores completos acima, na mesma ordem, um por linha. Nenhum outro recurso será selecionado."); input.AcceptsReturn = true; input.MinHeight = 90;
        bool Matches() => (input.Text ?? "").Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).SequenceEqual(canonicalIds, StringComparer.Ordinal);
        var confirm = ActionButton("Confirmar alteração remota", () => { if (Matches()) dialog.Close(true); return Task.CompletedTask; }, true);
        confirm.IsEnabled = false;
        input.TextChanged += (_, _) => confirm.IsEnabled = Matches();
        footer.Children.Add(confirm); return await dialog.ShowDialog<bool>(owner ?? this);
    }

    private static string[] SplitRemoteValues(string? value) => (value ?? "").Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
    private static string RemoteDate(DateTimeOffset? value) => value?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz") ?? "Não informado pelo serviço";

    private bool RemoteInUse(string accountId, RemoteTunnelTarget target)
    {
        var state = _model.Snapshot;
        return state is not null && (state.Tunnels.Any(t => t.AccountId == accountId && t.CanonicalId == target.CanonicalId && state.Connections.Any(c => c.ProfileId == t.Id && (c.State == ConnectionState.Connected || ToolkitViewModel.IsConnectionPending(c.State)))) ||
            state.Hosts.Any(h => h.AccountId == accountId && h.CanonicalId == target.CanonicalId && state.HostStatuses.Any(s => s.ProfileId == h.Id && (s.State == ConnectionState.Connected || ToolkitViewModel.IsConnectionPending(s.State)))));
    }

    private Button GuardRemoteMutation(Button button, string accountId, RemoteTunnelTarget target)
    {
        button.IsEnabled = !RemoteInUse(accountId, target);
        if (!button.IsEnabled) ToolTip.SetTip(button, "Pare primeiro os clientes e a hospedagem local deste recurso.");
        return button;
    }
}
