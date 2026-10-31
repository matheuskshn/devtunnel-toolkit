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

public sealed class ConnectionActionsTests
{
    [AvaloniaFact]
    public async Task ConnectionCardReconnectsAndValidatesItsOwnAccountWithoutNavigation()
    {
        var gateway = new FakeGateway();
        var account = gateway.State.Accounts[0];
        var profile = new TunnelProfile { AccountId = account.Id, TunnelId = "fixture", ClusterId = "use2" };
        gateway.State = gateway.State with { Tunnels = [profile], Connections = [new(profile.Id, ConnectionState.Connected, Ports: [new(3140, "127.0.0.1", 13140)])] };
        gateway.Handler = (op, _, _) => Task.FromResult<object>(op switch { "status" => gateway.State, "account.validate" => account, _ => true });
        using var vm = new ToolkitViewModel(gateway, action => Dispatcher.UIThread.Post(action));
        await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show(); window.NavigateTo("Conexões"); window.UpdateLayout();
        foreach (var label in new[] { "Reconectar", "Validar conta" })
        {
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, label));
            Assert.True(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        }
        Assert.Single(gateway.Calls, c => c.Operation == "tunnel.reconnect");
        Assert.Single(gateway.Calls, c => c.Operation == "account.validate");
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Autenticar conta"));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Adicionar conta"));
        Capture(window, "desktop-connection-actions");
        window.Close();
    }

    [AvaloniaFact]
    public async Task PeriodicProxySuccessDoesNotReplaceConnectionButtonsOrTheFocusedSearch()
    {
        var gateway = new FakeGateway();
        var account = gateway.State.Accounts[0];
        var profile = new TunnelProfile { AccountId = account.Id, TunnelId = "fixture", ClusterId = "use2" };
        var status = new ConnectionStatus(profile.Id, ConnectionState.Connected) { ProxyVerifiedAt = DateTimeOffset.UtcNow };
        gateway.State = gateway.State with { Tunnels = [profile], Connections = [status] };
        using var vm = new ToolkitViewModel(gateway); await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show(); window.NavigateTo("Conexões"); window.UpdateLayout();
        var reconnect = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Reconectar"));
        var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Buscar túneis");
        search.Focus(); search.Text = "fixture"; search.CaretIndex = 3; window.UpdateLayout();
        reconnect = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Reconectar"));
        gateway.State = gateway.State with { Connections = [status with { ProxyVerifiedAt = DateTimeOffset.UtcNow.AddSeconds(15) }] };
        await vm.RefreshAsync(); window.UpdateLayout();
        Assert.Same(reconnect, window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Reconectar")));
        Assert.Same(search, window.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Buscar túneis"));
        Assert.Equal(3, search.CaretIndex); Assert.True(search.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public async Task BrowserAuthenticationUsesTheConnectionAccountAndCanBeCancelledInline()
    {
        var gateway = new FakeGateway();
        var account = gateway.State.Accounts[0] with { Provider = AccountProvider.MicrosoftCli, CliLoginMode = CliLoginMode.Browser };
        var profile = new TunnelProfile { AccountId = account.Id, TunnelId = "fixture", ClusterId = "use2" };
        gateway.State = gateway.State with { Accounts = [account], Tunnels = [profile] };
        string? requestedAccount = null;
        gateway.DesktopLogin = async (id, token) => { requestedAccount = id; await Task.Delay(Timeout.Infinite, token); return new(id, "Completed"); };
        using var vm = new ToolkitViewModel(gateway, isWindows: true); await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show(); window.NavigateTo("Conexões"); window.UpdateLayout();
        window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Autenticar conta")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        Assert.Equal(account.Id, requestedAccount);
        Assert.True(vm.IsDesktopLoginActive(account.Id));
        window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Cancelar login")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (vm.IsDesktopLoginActive(account.Id)) await Task.Delay(10, timeout.Token);
        Assert.Contains("cancelado", vm.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(gateway.Calls, c => c.Operation == "account.login");
        window.Close();
    }

    [AvaloniaFact]
    public async Task DeviceChallengeAndCancellationStayInTheConnectionCard()
    {
        var gateway = new FakeGateway();
        var account = gateway.State.Accounts[0];
        var profile = new TunnelProfile { AccountId = account.Id, TunnelId = "fixture", ClusterId = "use2" };
        gateway.State = gateway.State with { Tunnels = [profile], Logins = [new(account.Id, "Pending", new("https://github.com/login/device", "FIXTURE-CODE", null))] };
        using var vm = new ToolkitViewModel(gateway); await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show(); window.NavigateTo("Conexões"); window.UpdateLayout();
        Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text == "FIXTURE-CODE");
        Capture(window, "desktop-connection-device-login");
        foreach (var label in new[] { "Autenticar conta", "Validar conta" })
            Assert.False(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, label)).IsEnabled);
        window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Cancelar login")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(gateway.Calls, c => c.Operation == "account.cancel");
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "Abrir Conexões");
        window.Close();
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("TOOLKIT_UI_SCREENSHOT_DIR") is not { } directory || !Path.IsPathFullyQualified(directory)) return;
        Directory.CreateDirectory(directory); window.UpdateLayout();
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
