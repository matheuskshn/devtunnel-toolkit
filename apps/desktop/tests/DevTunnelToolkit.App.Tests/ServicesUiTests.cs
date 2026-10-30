using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DevTunnelToolkit.App;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App.Tests;

public sealed class ServicesUiTests
{
    private static Button Button(Control root, string name) => root.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
    private static TextBox Input(Control root, string name) => root.GetVisualDescendants().OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == name);
    private static void Click(Control root, string name) { Button(root, name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); }
    private static ServiceProfile Service() => new() { Name = "Fixture HTTP", Port = 3140 };

    [AvaloniaFact]
    public async Task IndependentServiceEditorSavesOrderedRulesRangesAndNoRemoteWrites()
    {
        var gateway = new FakeGateway();
        gateway.Handler = (operation, payload, _) => Task.FromResult(operation == "service.save" ? payload! : gateway.State);
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync();
        var window = new MainWindow(model); window.Show(); window.NavigateTo("Serviços");
        Click(window, "Novo serviço"); var dialog = Assert.Single(window.OwnedWindows); dialog.UpdateLayout();
        Input(dialog, "Nome do serviço").Text = "Fixture proxy";
        Capture(dialog, "desktop-services-listener");
        var tabs = dialog.GetVisualDescendants().OfType<TabControl>().Single(); tabs.SelectedIndex = 1; dialog.UpdateLayout();
        Input(dialog, "Portas de destino permitidas").Text = "80,443,8000-8090";
        Input(dialog, "Portas para CONNECT e SOCKS5 TCP").Text = "443,8443-8444";
        Click(dialog, "Adicionar regra de acesso");
        Input(dialog, "Destinos").Text = "example.org, example.net\nexample.com";
        Input(dialog, "Portas desta regra").Text = "443,8000-8001";
        dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Ação da regra").SelectedIndex = 1;
        Capture(dialog, "desktop-services-policy");
        Click(dialog, "Salvar serviço");
        var saved = Assert.IsType<ServiceProfile>(gateway.Calls.Single(c => c.Operation == "service.save").Payload);
        Assert.Equal("Fixture proxy", saved.Name); Assert.Equal(ProxyProtocol.Http, saved.Protocol);
        Assert.Equal(new[] { 80, 443 }, saved.Policy.AllowedPorts);
        Assert.Equal(new ProxyPortRange { Start = 8000, End = 8090 }, Assert.Single(saved.Policy.AllowedPortRanges));
        Assert.Equal(new[] { 443 }, saved.Policy.ConnectPorts);
        var rule = Assert.Single(saved.Policy.Rules); Assert.Equal(PublishedProxyAction.Deny, rule.Action);
        Assert.Contains("example.net\nexample.com", rule.Destination); Assert.True(rule.Enabled);
        Assert.Equal(new ProxyPortRange { Start = 8000, End = 8001 }, Assert.Single(rule.PortRanges));
        Assert.False(saved.Policy.AllowAllDestinations); Assert.False(saved.AutoStart);
        Assert.DoesNotContain(gateway.Calls, c => c.Operation.StartsWith("host.") || c.Operation.StartsWith("remote."));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ServicesRemainStableAcrossSnapshotsAndShowElapsedBackgroundStart()
    {
        var gateway = new FakeGateway(); var service = Service();
        gateway.State = gateway.State with { Services = [service], ServiceStatuses = [new(service.Id, ConnectionState.Connecting, OperationStartedAt: DateTimeOffset.UtcNow.AddSeconds(-20))] };
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync();
        var window = new MainWindow(model); window.Show(); window.NavigateTo("Serviços"); window.UpdateLayout();
        var button = Button(window, "Parar serviço");
        Assert.False(button.IsEnabled); Assert.True(model.WorkspaceActivity!.Running);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("20s") == true);
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(); var offset = scroll.Offset;
        gateway.State = gateway.State with { Logs = [new(1, DateTimeOffset.UtcNow, "Information", "UNRELATED", "Synthetic unrelated event")] };
        await model.RefreshAsync(); window.UpdateLayout();
        Assert.Same(button, Button(window, "Parar serviço")); Assert.Equal(offset, scroll.Offset);
        Capture(window, "desktop-services-progress");
        gateway.State = gateway.State with { ServiceStatuses = [new(service.Id, ConnectionState.Connected, LocalAddress: "127.0.0.1", Port: 3140, OperationCompletedAt: DateTimeOffset.UtcNow)] };
        await model.RefreshAsync(); window.UpdateLayout();
        Assert.True(Button(window, "Parar serviço").IsEnabled); Assert.False(Button(window, "Editar serviço").IsEnabled);
        Assert.False(model.ServiceActivity(service.Id)!.Running);
        window.Close();
    }

    [AvaloniaFact]
    public async Task StopServiceRequiresImpactConfirmationAndDoesNotStopUnrelatedPublications()
    {
        var gateway = new FakeGateway(); var service = Service();
        var host = new HostProfile { AccountId = gateway.State.Accounts[0].Id, TunnelId = "fixture-host", ClusterId = "use2", Ports = [new() { ServiceId = service.Id }] };
        gateway.State = gateway.State with { Services = [service], ServiceStatuses = [new(service.Id, ConnectionState.Connected)], Hosts = [host] };
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync();
        var window = new MainWindow(model); window.Show(); window.NavigateTo("Serviços"); window.UpdateLayout();
        Assert.False(Button(window, "Remover serviço").IsEnabled);
        Click(window, "Parar serviço"); var confirm = Assert.Single(window.OwnedWindows); confirm.UpdateLayout();
        Assert.Contains(confirm.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains(host.CanonicalId) == true && t.Text.Contains("reinicie as publicações"));
        Click(confirm, "Cancelar"); Assert.DoesNotContain(gateway.Calls, c => c.Operation == "service.stop");
        Click(window, "Parar serviço"); confirm = Assert.Single(window.OwnedWindows); confirm.UpdateLayout(); Click(confirm, "Confirmar");
        Assert.Single(gateway.Calls, c => c.Operation == "service.stop");
        Assert.DoesNotContain(gateway.Calls, c => c.Operation.StartsWith("host.") || c.Operation.StartsWith("remote."));
        window.Close();
    }

    [AvaloniaFact]
    public async Task DevTunnelHostLabelsUseActiveStateNotStoredProfileAndPreserveFilterFocus()
    {
        var gateway = new FakeGateway(); var account = gateway.State.Accounts[0];
        // Another local account may be hosting a resource shared with the selected account.
        var host = new HostProfile { AccountId = "second-local-account", TunnelId = "fixture-own", ClusterId = "use2", Ports = [new() { Kind = HostPortKind.Service, LocalPort = 8080 }] };
        ManagedTunnel Remote(string id, bool? online) => new(new(id, "use2"), null, null, [], null, null, null, online, [], []);
        var remotes = new[] { Remote("fixture-own", true), Remote("fixture-other", true), Remote("fixture-offline", false), Remote("fixture-unknown", null) };
        gateway.State = gateway.State with { Hosts = [host], HostStatuses = [new(host.Id, ConnectionState.Stopped)] };
        gateway.Handler = (op, _, _) => Task.FromResult<object>(op == "remote.list" ? remotes : gateway.State);
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync(); await model.LoadRemoteTunnelsAsync(account.Id);
        var window = new MainWindow(model); window.Show(); window.NavigateTo("DevTunnel"); window.UpdateLayout();
        Assert.Equal(2, window.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Text?.Contains("Outro host ativo") == true));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Sem host conectado") == true);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Estado desconhecido") == true);
        var refresh = Button(window, "Consultar servidor"); var search = Input(window, "Buscar túneis remotos"); search.Focus();
        search.Text = "fixture-own"; search.CaretIndex = 5;
        gateway.State = gateway.State with { HostStatuses = [new(host.Id, ConnectionState.Connected)] };
        await model.RefreshAsync(); window.UpdateLayout();
        Assert.Same(refresh, Button(window, "Consultar servidor")); Assert.Same(search, Input(window, "Buscar túneis remotos")); Assert.Equal(5, search.CaretIndex);
        Button(window, "Consultar servidor").Focus(); await model.RefreshAsync(); window.UpdateLayout();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Neste Desktop") == true);
        Capture(window, "desktop-devtunnel-management");
        window.Close();
    }

    [AvaloniaFact]
    public async Task ServiceFormStaysOpenWithClearErrorForInvalidPortRange()
    {
        var gateway = new FakeGateway(); using var model = new ToolkitViewModel(gateway); await model.RefreshAsync();
        var window = new MainWindow(model); window.Show(); window.NavigateTo("Serviços"); Click(window, "Novo serviço");
        var dialog = Assert.Single(window.OwnedWindows); dialog.UpdateLayout();
        dialog.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1; dialog.UpdateLayout();
        Input(dialog, "Portas de destino permitidas").Text = "9000-8000"; Click(dialog, "Salvar serviço");
        Assert.Contains(dialog, window.OwnedWindows); Assert.DoesNotContain(gateway.Calls, c => c.Operation == "service.save");
        Assert.Contains("porta", model.Message, StringComparison.OrdinalIgnoreCase);
        dialog.Close(); window.Close();
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("TOOLKIT_UI_SCREENSHOT_DIR") is not { } directory || !Path.IsPathFullyQualified(directory)) return;
        Directory.CreateDirectory(directory); window.UpdateLayout(); window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
