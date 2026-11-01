using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DevTunnelToolkit.App;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App.Tests;

public sealed class RemoteManagementUiTests
{
    private static ManagedTunnel Tunnel(string id = "fixture-remote") => new(new(id, "use2"), "Fixture", "Synthetic resource", [], null, null, 86400, false, [new(3140, "tcp", "Proxy", [], [])], []);
    private static Button Button(Control root, string name) => root.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
    private static TextBox Input(Control root, string name) => root.GetVisualDescendants().OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == name);
    private static void Click(Control root, string name) { Button(root, name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }

    [Fact]
    public async Task CancellingRemoteWaitStopsUiWaitAndDoesNotPromiseRollback()
    {
        var gateway = new FakeGateway();
        gateway.Handler = async (op, _, token) =>
        {
            if (op != "remote.list") return gateway.State;
            await Task.Delay(Timeout.Infinite, token); return Array.Empty<ManagedTunnel>();
        };
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync();
        var pending = model.LoadRemoteTunnelsAsync(gateway.State.Accounts[0].Id);
        Assert.True(model.IsBusy); Assert.True(model.CanCancelRemoteOperation);
        model.CancelRemoteOperation(); Assert.False(await pending);
        Assert.False(model.IsBusy); Assert.False(model.CanCancelRemoteOperation);
        Assert.Contains("pode continuar", model.Message);
        Assert.Empty(model.RemoteTunnels);
    }

    [Fact]
    public async Task BatchPreservesFailedResourcesAndTypedConfirmations()
    {
        var first = Tunnel(); var second = Tunnel("fixture-other");
        var gateway = new FakeGateway();
        gateway.Handler = (op, _, _) => Task.FromResult<object>(op switch
        {
            "remote.list" => new[] { first, second },
            "remote.delete-batch" => new[] { new RemoteBatchResult(first.Target.CanonicalId, true), new RemoteBatchResult(second.Target.CanonicalId, false, "REMOTE_ACCESS_DENIED") },
            _ => gateway.State
        });
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync();
        var account = gateway.State.Accounts[0].Id;
        Assert.True(await model.LoadRemoteTunnelsAsync(account));
        var results = await model.DeleteRemoteBatchAsync(account, [first.Target, second.Target], [first.Target.CanonicalId, second.Target.CanonicalId]);
        Assert.Equal(2, results!.Length); Assert.False(results[1].Succeeded);
        Assert.Equal(second.Target, Assert.Single(model.RemoteTunnels).Target);
        var payload = JsonSerializer.SerializeToElement(gateway.Calls.Single(c => c.Operation == "remote.delete-batch").Payload);
        Assert.Equal(account, payload.GetProperty("accountId").GetString());
        Assert.Equal(second.Target.CanonicalId, payload.GetProperty("confirmations")[1].GetString());
        Assert.DoesNotContain(gateway.Calls, c => c.Operation == "tunnel.remove");
    }

    [Fact]
    public async Task TokenIsReturnedOnlyToRequestingCallerAndEchoStopUsesTypedResult()
    {
        const string secret = "synthetic-secret-only-in-dialog";
        var gateway = new FakeGateway();
        gateway.Handler = (op, _, _) => Task.FromResult<object>(op switch
        {
            "remote.token" => new RemoteTokenResult(secret, ["connect"], DateTimeOffset.UtcNow.AddHours(1), "service"),
            "diagnostic.echo.stop" => new EchoStatus(false),
            _ => gateway.State
        });
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync();
        var result = await model.IssueRemoteTokenAsync(gateway.State.Accounts[0].Id, Tunnel().Target, new(["connect"], 3140));
        Assert.Equal(secret, result!.Token); Assert.Empty(model.RemoteTunnels);
        Assert.DoesNotContain(secret, model.Message);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(model.Snapshot));
        Assert.True(await model.SetEchoAsync(false));
    }

    [AvaloniaFact]
    public async Task RemoteDeleteRequiresExactIdAndKeepsLocalProfiles()
    {
        var remote = Tunnel(); var gateway = RemoteGateway(remote);
        var local = new TunnelProfile { AccountId = gateway.State.Accounts[0].Id, TunnelId = remote.Target.TunnelId, ClusterId = remote.Target.ClusterId };
        gateway.State = gateway.State with { Tunnels = [local] };
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync();
        await model.LoadRemoteTunnelsAsync(gateway.State.Accounts[0].Id);
        var window = new MainWindow(model); window.Show(); window.NavigateTo("Túneis remotos"); window.UpdateLayout();
        Capture(window, "desktop-remote-management");
        Click(window, "Gerenciar"); var details = Assert.Single(window.OwnedWindows); details.UpdateLayout();
        Capture(details, "desktop-remote-details");
        Click(details, "Excluir no servidor"); var confirm = Assert.Single(details.OwnedWindows); confirm.UpdateLayout();
        Assert.False(Button(confirm, "Confirmar alteração remota").IsEnabled);
        Input(confirm, "Confirme os identificadores").Text = remote.Target.TunnelId;
        Dispatcher.UIThread.RunJobs();
        Assert.False(Button(confirm, "Confirmar alteração remota").IsEnabled);
        Input(confirm, "Confirme os identificadores").Text = remote.Target.CanonicalId;
        Dispatcher.UIThread.RunJobs();
        Assert.True(Button(confirm, "Confirmar alteração remota").IsEnabled);
        Click(confirm, "Confirmar alteração remota");
        Assert.Single(gateway.Calls, c => c.Operation == "remote.delete");
        Assert.DoesNotContain(gateway.Calls, c => c.Operation == "tunnel.remove");
        Assert.Equal(local.Id, Assert.Single(model.Snapshot!.Tunnels).Id);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TokenDialogMasksAndClearsSecretOnClose()
    {
        var remote = Tunnel(); var gateway = RemoteGateway(remote);
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync(); await model.LoadRemoteTunnelsAsync(gateway.State.Accounts[0].Id);
        var window = new MainWindow(model); window.Show(); window.NavigateTo("Túneis remotos"); window.UpdateLayout();
        Click(window, "Gerenciar"); var details = Assert.Single(window.OwnedWindows); details.UpdateLayout();
        details.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2; details.UpdateLayout();
        Click(details, "Emitir token com escopo"); var dialog = Assert.Single(details.OwnedWindows); dialog.UpdateLayout();
        Assert.NotNull(dialog.Clipboard); await dialog.Clipboard.SetTextAsync("fixture-clipboard");
        Click(dialog, "Emitir token"); var token = Input(dialog, "Token emitido");
        Assert.Equal('●', token.PasswordChar); Assert.Equal("synthetic-ui-only-secret", token.Text);
        Assert.Equal("fixture-clipboard", await dialog.Clipboard.TryGetTextAsync());
        Assert.DoesNotContain(token.Text!, model.Message); Assert.DoesNotContain(token.Text!, JsonSerializer.Serialize(model.Snapshot));
        Click(dialog, "Copiar token"); Assert.Equal(token.Text, await dialog.Clipboard.TryGetTextAsync());
        dialog.Close(); Assert.Equal("", token.Text);
        details.Close(); window.Close();
    }

    [AvaloniaFact]
    public async Task ClosingTokenDialogBeforeServerRespondsDoesNotRetainTheLateSecret()
    {
        var remote = Tunnel(); var gateway = RemoteGateway(remote);
        var original = gateway.Handler!;
        var complete = new TaskCompletionSource<object>();
        gateway.Handler = (op, payload, cancellation) => op == "remote.token" ? complete.Task : original(op, payload, cancellation);
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync(); await model.LoadRemoteTunnelsAsync(gateway.State.Accounts[0].Id);
        var window = new MainWindow(model); window.Show(); window.NavigateTo("Túneis remotos"); window.UpdateLayout();
        Click(window, "Gerenciar"); var details = Assert.Single(window.OwnedWindows); details.UpdateLayout();
        details.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2; details.UpdateLayout();
        Click(details, "Emitir token com escopo"); var dialog = Assert.Single(details.OwnedWindows); dialog.UpdateLayout();
        var input = Input(dialog, "Token emitido");
        Click(dialog, "Emitir token"); Assert.True(model.IsBusy); dialog.Close();
        complete.SetResult(new RemoteTokenResult("synthetic-late-secret", ["connect"], null, "not-reported"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", input.Text); Assert.DoesNotContain("synthetic-late-secret", model.Message);
        Assert.DoesNotContain("synthetic-late-secret", JsonSerializer.Serialize(model.Snapshot));
        details.Close(); window.Close();
    }

    [AvaloniaFact]
    public async Task HostedOperationPersistsAfterCommandAndDoesNotJitterOnUnchangedSnapshot()
    {
        var gateway = new FakeGateway();
        var profile = new HostProfile { AccountId = gateway.State.Accounts[0].Id, TunnelId = "fixture-host", ClusterId = "use2", Ports = [new()] };
        gateway.State = gateway.State with { Hosts = [profile], HostStatuses = [new(profile.Id, ConnectionState.Connecting, OperationStartedAt: DateTimeOffset.UtcNow.AddSeconds(-20), Phase: "host-relay")] };
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync();
        var window = new MainWindow(model); window.Show(); window.NavigateTo("Hospedagem"); window.UpdateLayout();
        Capture(window, "desktop-hosting-progress");
        Assert.True(model.WorkspaceActivity!.Running);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Conectando a hospedagem ao relay") == true);
        var button = Button(window, "Cancelar publicação"); await model.RefreshAsync();
        Assert.Same(button, Button(window, "Cancelar publicação"));
        gateway.State = gateway.State with { HostStatuses = [new(profile.Id, ConnectionState.Connected, OperationCompletedAt: DateTimeOffset.UtcNow, Phase: "host-ready")] };
        await model.RefreshAsync(); window.UpdateLayout();
        Assert.False(model.HostActivity(profile.Id)!.Running);
        Assert.NotNull(Button(window, "Parar publicação"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task NewHostBindsExistingServiceWithoutCreatingProxyOrRemoteMutation()
    {
        var gateway = new FakeGateway();
        var service = new ServiceProfile { Name = "Fixture HTTP", Port = 3140 };
        gateway.State = gateway.State with { Services = [service] };
        gateway.Handler = (op, payload, _) => Task.FromResult(op == "host.save" ? payload! : gateway.State);
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync();
        var window = new MainWindow(model); window.Show(); window.NavigateTo("Hospedagem"); window.UpdateLayout();
        Click(window, "Nova publicação"); var dialog = Assert.Single(window.OwnedWindows); dialog.UpdateLayout();
        Capture(dialog, "desktop-hosting-editor");
        Input(dialog, "Tunnel ID para hospedar").Text = "fixture-host.use2";
        Click(dialog, "Salvar perfil");
        var profile = Assert.IsType<HostProfile>(gateway.Calls.Single(c => c.Operation == "host.save").Payload);
        Assert.Equal(HostPortKind.HttpProxy, Assert.Single(profile.Ports).Kind);
        Assert.Equal(3140, profile.Ports[0].RemotePort); Assert.Equal(3140, profile.Ports[0].LocalPort);
        Assert.Equal(service.Id, profile.Ports[0].ServiceId);
        Assert.Empty(profile.Proxy.Rules); Assert.False(profile.AutoStart);
        Assert.DoesNotContain(gateway.Calls, c => c.Operation.StartsWith("remote.") || c.Operation.StartsWith("service.") || c.Operation is "host.prepare" or "host.start");
        window.Close();
    }

    [AvaloniaFact]
    public async Task RemoteEditsAreDisabledWhileThisAgentUsesTheResource()
    {
        var remote = Tunnel(); var gateway = RemoteGateway(remote);
        var profile = new TunnelProfile { AccountId = gateway.State.Accounts[0].Id, TunnelId = remote.Target.TunnelId, ClusterId = remote.Target.ClusterId };
        gateway.State = gateway.State with { Tunnels = [profile], Connections = [new(profile.Id, ConnectionState.Connected)] };
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync(); await model.LoadRemoteTunnelsAsync(gateway.State.Accounts[0].Id);
        var window = new MainWindow(model); window.Show(); window.NavigateTo("Túneis remotos"); window.UpdateLayout();
        Click(window, "Gerenciar"); var dialog = Assert.Single(window.OwnedWindows); dialog.UpdateLayout();
        Assert.False(Button(dialog, "Editar propriedades remotas").IsEnabled);
        Assert.False(Button(dialog, "Excluir no servidor").IsEnabled);
        Assert.Equal(7, dialog.GetVisualDescendants().OfType<TabControl>().Single().ItemCount);
        Assert.DoesNotContain(gateway.Calls, c => c.Operation is "remote.update" or "remote.delete");
        dialog.Close(); window.Close();
    }

    [AvaloniaFact]
    public async Task CancellingActiveHostSaveKeepsFormAndDoesNotStopPublication()
    {
        var gateway = new FakeGateway();
        var profile = new HostProfile { AccountId = gateway.State.Accounts[0].Id, TunnelId = "fixture-host", ClusterId = "use2", Ports = [new()] };
        gateway.State = gateway.State with { Hosts = [profile], HostStatuses = [new(profile.Id, ConnectionState.Connected)] };
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync();
        var window = new MainWindow(model); window.Show(); window.NavigateTo("Hospedagem"); window.UpdateLayout();
        Click(window, "Editar vínculos"); var editor = Assert.Single(window.OwnedWindows); editor.UpdateLayout();
        Input(editor, "Apelido local da publicação").Text = "Unsaved fixture alias";
        Click(editor, "Salvar perfil"); var confirmation = Assert.Single(editor.OwnedWindows); confirmation.UpdateLayout();
        Assert.Equal("Salvar hospedagem ativa", confirmation.Title);
        Click(confirmation, "Cancelar");
        Assert.Empty(editor.OwnedWindows); Assert.Contains(editor, window.OwnedWindows);
        Assert.Equal("Unsaved fixture alias", Input(editor, "Apelido local da publicação").Text);
        Assert.DoesNotContain(gateway.Calls, c => c.Operation is "host.save" or "host.stop" or "host.restart");
        editor.Close(); window.Close();
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("TOOLKIT_UI_SCREENSHOT_DIR") is not { } directory || !Path.IsPathFullyQualified(directory)) return;
        Directory.CreateDirectory(directory); window.UpdateLayout();
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static FakeGateway RemoteGateway(ManagedTunnel remote)
    {
        var gateway = new FakeGateway();
        gateway.Handler = (op, _, _) => Task.FromResult<object>(op switch
        {
            "remote.list" => new[] { remote },
            "remote.get" => remote,
            "remote.delete" => true,
            "remote.token" => new RemoteTokenResult("synthetic-ui-only-secret", ["connect"], null, "not-reported"),
            _ => gateway.State
        });
        return gateway;
    }
}
