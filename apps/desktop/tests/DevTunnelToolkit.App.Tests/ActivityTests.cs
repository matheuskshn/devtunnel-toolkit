using System.Text.Json;
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

public sealed class ActivityTests
{
    [AvaloniaFact]
    public async Task ConnectionFeedbackOutlivesCommandAndSurvivesNavigation()
    {
        var gateway = new FakeGateway();
        var tunnel = new TunnelProfile { AccountId = gateway.State.Accounts[0].Id, TunnelId = "fixture-tunnel", ClusterId = "use2" };
        gateway.State = gateway.State with { Tunnels = [tunnel] };
        var start = DateTimeOffset.UtcNow.AddSeconds(-20);
        gateway.Handler = (op, _, _) =>
        {
            if (op == "tunnel.connect")
            {
                gateway.State = gateway.State with { Connections = [new(tunnel.Id, ConnectionState.Discovering, OperationStartedAt: start)] };
                return Task.FromResult<object>(true);
            }
            if (op == "tunnel.stop")
            {
                gateway.State = gateway.State with { Connections = [new(tunnel.Id, ConnectionState.Stopped, OperationStartedAt: start, OperationCompletedAt: DateTimeOffset.UtcNow)] };
                return Task.FromResult<object>(true);
            }
            return Task.FromResult<object>(gateway.State);
        };
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync();
        var window = new MainWindow(model); window.Show();
        Find(window, "Conectar").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(model.IsBusy); // The command returned; the actual connection did not.
        Assert.True(model.WorkspaceActivity!.Running);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Consultando o túnel") == true);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("20s") == true);
        Assert.Contains(window.GetVisualDescendants().OfType<ProgressBar>(), p => p.IsVisible && p.IsIndeterminate);
        var cancel = Find(window, "Cancelar conexão");
        await model.RefreshAsync(); Assert.Same(cancel, Find(window, "Cancelar conexão"));
        window.NavigateTo("Portas");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Consultando o túnel") == true);
        window.NavigateTo("Túneis"); window.UpdateLayout(); Capture(window, "desktop-connection-discovery");
        Find(window, "Cancelar conexão").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Assert.False(model.TunnelActivity(tunnel.Id)!.Running);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Conexão encerrada. Portas locais liberadas.");
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReconnectCountdownAndMappingStageRemainVisibleUntilReady()
    {
        var gateway = new FakeGateway();
        var tunnel = new TunnelProfile { AccountId = gateway.State.Accounts[0].Id, TunnelId = "fixture-tunnel", ClusterId = "use2" };
        var start = DateTimeOffset.UtcNow.AddSeconds(-30);
        gateway.State = gateway.State with { Tunnels = [tunnel], Connections = [new(tunnel.Id, ConnectionState.Reconnecting, RetryCount: 2, NextRetryAt: DateTimeOffset.UtcNow.AddSeconds(10), OperationStartedAt: start)] };
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync();
        var window = new MainWindow(model); window.Show();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Próxima tentativa em") == true);
        Capture(window, "desktop-connection-retry");
        gateway.State = gateway.State with { Connections = [new(tunnel.Id, ConnectionState.Connecting, OperationStartedAt: start, Phase: "mapping")] };
        await model.RefreshAsync();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Preparando as portas locais") == true);
        gateway.State = gateway.State with { Connections = [new(tunnel.Id, ConnectionState.Connected, OperationStartedAt: start, OperationCompletedAt: DateTimeOffset.UtcNow)] };
        await model.RefreshAsync();
        Assert.False(model.TunnelActivity(tunnel.Id)!.Running);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Duração:") == true);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SlowSaveShowsDescriptionAndElapsedInsideTheDialog()
    {
        var gateway = new FakeGateway();
        var completion = new TaskCompletionSource<object>();
        gateway.Handler = (op, _, _) => op == "account.save" ? completion.Task : Task.FromResult<object>(gateway.State);
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync();
        var window = new MainWindow(model); window.Show(); window.NavigateTo("Contas");
        Find(window, "Adicionar conta").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var dialog = Assert.Single(window.OwnedWindows);
        dialog.GetVisualDescendants().OfType<TextBox>().First(t => AutomationProperties.GetName(t) == "Nome do perfil").Text = "Fixture";
        dialog.UpdateLayout();
        var save = Find(dialog, "Salvar");
        var buttonWidth = save.Bounds.Width;
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); dialog.UpdateLayout();
        Assert.Equal(buttonWidth, save.Bounds.Width);
        Assert.False(save.IsEnabled);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Validando e salvando a conta…");
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("Em andamento há") == true);
        Capture(dialog, "desktop-dialog-saving");
        completion.SetException(new IOException("private-fixture-detail")); Dispatcher.UIThread.RunJobs();
        Assert.True(save.IsEnabled); Assert.NotEmpty(window.OwnedWindows);
        Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("private-fixture-detail") == true);
        dialog.Close(); window.Close();
    }

    [Fact]
    public void ClocksNeverInventPercentagesOrNegativeTime()
    {
        var start = DateTimeOffset.UtcNow;
        Assert.Contains("0s", new OperationActivity("Waiting", start, true).Timing(start.AddSeconds(-2)));
        Assert.Contains("1min 05s", new OperationActivity("Waiting", start, true).Timing(start.AddSeconds(65)));
        Assert.Contains("Próxima tentativa em 0s", new OperationActivity("Retry", start, true, start).Timing(start.AddSeconds(2)));
        Assert.Equal("Duração: 4s.", new OperationActivity("Done", start, false, FinishedAt: start.AddSeconds(4)).Timing(start.AddHours(1)));
    }

    [AvaloniaFact]
    public async Task ProxyDiagnosticsKeepInputAndResultWhileCountersAndLogsChange()
    {
        var gateway = new FakeGateway();
        var completion = new TaskCompletionSource<object>();
        gateway.Handler = (op, _, _) => op == "proxy.probe" ? completion.Task : Task.FromResult<object>(gateway.State);
        using var model = new ToolkitViewModel(gateway); await model.RefreshAsync();
        var window = new MainWindow(model); window.Show(); window.NavigateTo("Proxy e PAC");
        var host = window.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Destino para testar");
        host.Text = "example.net";
        var button = Find(window, "Verificar acesso"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(button.IsEnabled);
        Assert.IsType<Grid>(button.Content);
        gateway.State = gateway.State with { ProxyActiveConnections = 2, Logs = [new(1, DateTimeOffset.UtcNow, "Warning", "PROXY_PROBE_CONNECT_FAILED", "Fixture")] };
        await model.RefreshAsync();
        Assert.Equal("example.net", host.Text); Assert.Same(button, Find(window, "Verificar acesso"));
        completion.SetResult(new ProxyProbeResult(new("example.net", 443, RouteAction.Direct, null), false, "PROXY_PROBE_CONNECT_FAILED", 503, 400));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("HTTP 503") == true);
        Assert.Same(button, Find(window, "Verificar acesso")); Assert.True(button.IsEnabled);
        Capture(window, "desktop-proxy-diagnostics");
        window.Close();
    }
    [AvaloniaFact]
    public async Task UnchangedSnapshotsAndUnrelatedLogsPreserveAccountButtons()
    {
        var gateway = new FakeGateway();
        using var model = new ToolkitViewModel(gateway);
        await model.RefreshAsync();
        var window = new MainWindow(model); window.Show(); window.NavigateTo("Contas");
        var before = Find(window, "Descobrir túneis");
        // IPC deserializes fresh arrays each time. Reference equality is insufficient.
        gateway.State = JsonSerializer.Deserialize<AgentSnapshot>(JsonSerializer.Serialize(gateway.State))!;
        await model.RefreshAsync();
        Assert.Same(before, Find(window, "Descobrir túneis"));
        gateway.State = gateway.State with { ProxyActiveConnections = 3, Logs = [new(1, DateTimeOffset.UtcNow, "Information", "PROXY_ROUTE_DIRECT", "Fixture")] };
        await model.RefreshAsync();
        Assert.Same(before, Find(window, "Descobrir túneis"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task DiscoveryRemainsVisibleAndDisabledDuringSlowProviderResponse()
    {
        var gateway = new FakeGateway();
        var completion = new TaskCompletionSource<object>();
        gateway.Handler = (op, _, _) => op == "account.discover" ? completion.Task : Task.FromResult<object>(gateway.State);
        using var model = new ToolkitViewModel(gateway);
        await model.RefreshAsync();
        var window = new MainWindow(model); window.Show(); window.NavigateTo("Túneis");
        var button = Find(window, "Descobrir túneis");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(button.IsEnabled);
        Assert.Contains(window.GetVisualDescendants().OfType<ProgressBar>(), p => p.IsVisible && p.IsIndeterminate);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Consultando túneis") == true);
        Capture(window, "desktop-discovery-loading");
        await model.RefreshAsync();
        Assert.Same(button, Find(window, "Descobrir túneis")); Assert.False(button.IsEnabled);
        completion.SetResult(new[] { new DiscoveredTunnel("fixture-tunnel", "use2", "Fixture", []) });
        Dispatcher.UIThread.RunJobs();
        Assert.False(model.IsBusy);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("fixture-tunnel") == true);
        window.Close();
    }

    [AvaloniaFact]
    public async Task NavigationDoesNotMoveContentWhenSelectionChanges()
    {
        using var model = new ToolkitViewModel(new FakeGateway()); await model.RefreshAsync();
        var window = new MainWindow(model); window.Show();
        var accounts = Find(window, "Abrir Contas");
        var border = accounts.BorderThickness;
        window.NavigateTo("Contas");
        Assert.Equal(border, accounts.BorderThickness);
        window.Close();
    }

    private static Button Find(Window window, string name) => window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == name);

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("TOOLKIT_UI_SCREENSHOT_DIR") is not { } directory || !Path.IsPathFullyQualified(directory)) return;
        Directory.CreateDirectory(directory); window.UpdateLayout();
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
