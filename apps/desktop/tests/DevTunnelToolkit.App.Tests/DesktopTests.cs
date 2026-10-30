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

[assembly: AvaloniaTestApplication(typeof(DevTunnelToolkit.App.Tests.TestAppBuilder))]

namespace DevTunnelToolkit.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ToolkitApp>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class DesktopTests
{
    [AvaloniaFact]
    public async Task IncompatibleAgentShowsRecoveryInsteadOfStartingAnotherAgent()
    {
        var gateway = new FakeGateway { Error = new ToolkitException("IPC_VERSION_MISMATCH") };
        using var vm = new ToolkitViewModel(gateway, action => Dispatcher.UIThread.Post(action));
        await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Atualize o agente local");
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Iniciar agente local"));
        Assert.NotNull(window.Icon);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DefaultMicrosoftCliAccountDoesNotRequireClientId()
    {
        var gateway = new FakeGateway();
        using var vm = new ToolkitViewModel(gateway, action => Dispatcher.UIThread.Post(action));
        await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show();
        window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == "Abrir Contas").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Adicionar conta")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var editor = Assert.Single(window.OwnedWindows);
        Assert.NotNull(window.Icon);
        Assert.NotNull(editor.Icon);
        Assert.Equal("Entra / Microsoft - CLI oficial", editor.GetVisualDescendants().OfType<ComboBox>().First(b => AutomationProperties.GetName(b) == "Modo de autenticação").SelectedItem);
        editor.GetVisualDescendants().OfType<TextBox>().First(b => AutomationProperties.GetName(b) == "Nome do perfil").Text = "CLI fixture";
        editor.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Salvar")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var account = Assert.IsType<AccountProfile>(Assert.Single(gateway.Calls, c => c.Operation == "account.save").Payload);
        Assert.Equal(AccountProvider.MicrosoftCli, account.Provider);
        Assert.Null(account.ClientId);
        Assert.Empty(window.OwnedWindows);
        window.Close();
    }

    [AvaloniaFact]
    public async Task GitHubAccountCanUseTheOfficialDeviceClientWithoutCustomRegistration()
    {
        var gateway = new FakeGateway();
        using var vm = new ToolkitViewModel(gateway, action => Dispatcher.UIThread.Post(action));
        await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show();
        window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == "Abrir Contas").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Adicionar conta")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var editor = Assert.Single(window.OwnedWindows);
        editor.GetVisualDescendants().OfType<TextBox>().First(b => AutomationProperties.GetName(b) == "Nome do perfil").Text = "Personal fixture";
        editor.GetVisualDescendants().OfType<ComboBox>().First(b => AutomationProperties.GetName(b) == "Modo de autenticação").SelectedItem = "GitHub - conta isolada";
        editor.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Salvar")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var account = Assert.IsType<AccountProfile>(Assert.Single(gateway.Calls, c => c.Operation == "account.save").Payload);
        Assert.Equal(AccountProvider.GitHub, account.Provider);
        Assert.Null(account.ClientId);
        Assert.Empty(window.OwnedWindows);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PacFailureShowsAnAlertWithoutHidingActiveTunnels()
    {
        var gateway = new FakeGateway();
        var profile = new TunnelProfile { AccountId = gateway.State.Accounts[0].Id, TunnelId = "desktop-demo", ClusterId = "use2" };
        gateway.State = gateway.State with { Tunnels = [profile], Connections = [new(profile.Id, ConnectionState.Connected)], PacErrorCode = "LOCAL_PORT_UNAVAILABLE" };
        using var vm = new ToolkitViewModel(gateway, action => Dispatcher.UIThread.Post(action));
        await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show();
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
        Assert.Contains(texts, t => t.StartsWith("PAC indisponível:", StringComparison.Ordinal));
        Assert.Contains("desktop-demo.use2", texts); Assert.Contains("Conectado", texts);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PacEditorSavesRuleUsingTunnelProfileAndRemotePort()
    {
        var gateway = new FakeGateway();
        var profile = new TunnelProfile { AccountId = gateway.State.Accounts[0].Id, TunnelId = "desktop-demo", ClusterId = "use2", Mappings = [new() { RemotePort = 3180, LocalPort = 13180 }] };
        gateway.State = gateway.State with { Tunnels = [profile] };
        using var vm = new ToolkitViewModel(gateway, action => Dispatcher.UIThread.Post(action));
        await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show();
        window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == "Abrir Configurações").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Editar configurações")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var editor = Assert.Single(window.OwnedWindows);
        editor.GetVisualDescendants().OfType<CheckBox>().First(b => Equals(b.Content, "Disponibilizar PAC neste dispositivo")).IsChecked = true;
        editor.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Adicionar regra PAC")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        editor.UpdateLayout();
        editor.GetVisualDescendants().OfType<TextBox>().First(b => AutomationProperties.GetName(b) == "Domínio da regra").Text = "example.org";
        editor.GetVisualDescendants().OfType<ComboBox>().First(b => AutomationProperties.GetName(b) == "Tipo de proxy").SelectedIndex = 1;
        editor.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Salvar")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var settings = Assert.IsType<AppSettings>(Assert.Single(gateway.Calls, c => c.Operation == "settings.save").Payload);
        Assert.True(settings.Pac.Enabled);
        var rule = Assert.Single(settings.Pac.Rules);
        Assert.Equal(profile.Id, rule.ProfileId); Assert.Equal(3180, rule.RemotePort); Assert.Equal(ProxyProtocol.Socks5, rule.Protocol);
        Assert.Equal("example.org", rule.Domain);
        Assert.Empty(window.OwnedWindows);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DeviceLoginCanBeCancelledForTheCorrectIdentity()
    {
        var gateway = new FakeGateway();
        var account = gateway.State.Accounts[0];
        gateway.State = gateway.State with { Logins = [new(account.Id, "Pending", new("https://microsoft.com/devicelogin", "TEST-CODE", DateTimeOffset.UtcNow.AddMinutes(10)))] };
        using var vm = new ToolkitViewModel(gateway, action => Dispatcher.UIThread.Post(action));
        await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show();
        window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == "Abrir Contas").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text == "TEST-CODE");
        window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Cancelar login")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var call = Assert.Single(gateway.Calls, c => c.Operation == "account.cancel");
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(call.Payload);
        Assert.Equal(account.Id, payload.GetProperty("id").GetString());
        window.Close();
    }

    [AvaloniaFact]
    public async Task TwoProviderProfilesKeepTheirEffectivePortsDistinct()
    {
        var gateway = new FakeGateway();
        var microsoft = gateway.State.Accounts[0] with { DisplayName = "Microsoft workspace", Username = "microsoft-user" };
        var github = new AccountProfile { DisplayName = "GitHub workspace", Username = "github-user", Provider = AccountProvider.GitHub, Status = "Authenticated", ClientId = "test-client" };
        var first = new TunnelProfile { AccountId = microsoft.Id, TunnelId = "desktop-demo", ClusterId = "use2", Alias = "HTTP proxy" };
        var second = new TunnelProfile { AccountId = github.Id, TunnelId = "desktop-tools", ClusterId = "euw", Alias = "Development services" };
        gateway.State = gateway.State with
        {
            Accounts = [microsoft, github],
            Tunnels = [first, second],
            Connections = [new(first.Id, ConnectionState.Connected, Ports: [new(3140, "127.0.0.1", 13140)]), new(second.Id, ConnectionState.Connected, Ports: [new(3140, "127.0.0.1", 23140), new(3180, "127.0.0.1", 23180)])],
            Logs = [new(1, DateTimeOffset.UtcNow, "Information", "TUNNEL_CONNECTED", "Tunnel connected and local listeners ready.", first.Id)]
        };
        using var vm = new ToolkitViewModel(gateway, action => Dispatcher.UIThread.Post(action));
        await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show();
        var text = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
        Assert.Contains(text, t => t.Contains("127.0.0.1:13140"));
        Assert.Contains(text, t => t.Contains("127.0.0.1:23140"));
        if (Environment.GetEnvironmentVariable("TOOLKIT_UI_SCREENSHOT_DIR") is { } directory && Path.IsPathFullyQualified(directory))
        {
            Directory.CreateDirectory(directory);
            foreach (var theme in new[] { "Light", "Dark", "HighContrast" })
            {
                (Avalonia.Application.Current as ToolkitApp)!.ApplyTheme(theme);
                window.CaptureRenderedFrame()?.Save(Path.Combine(directory, $"desktop-active-{theme.ToLowerInvariant()}.png"), PngBitmapEncoderOptions.Default);
            }
        }
        window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == "Abrir Contas").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var accounts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
        Assert.Contains("microsoft-user", accounts); Assert.Contains("github-user", accounts);
        window.Close();
    }

    [AvaloniaFact]
    public async Task MappingEditorSavesManualPortsThroughTheAgentContract()
    {
        var gateway = new FakeGateway();
        var profile = new TunnelProfile { AccountId = gateway.State.Accounts[0].Id, TunnelId = "desktop-demo", ClusterId = "use2", Mappings = [new() { RemotePort = 3140, LocalPort = 3140 }] };
        gateway.State = gateway.State with { Tunnels = [profile], Connections = [new(profile.Id, ConnectionState.Connected, Ports: [new(3140, "127.0.0.1", 3140)])] };
        using var vm = new ToolkitViewModel(gateway, action => Dispatcher.UIThread.Post(action));
        await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show();
        window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Editar")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var editor = Assert.Single(window.OwnedWindows);
        var mode = editor.GetVisualDescendants().OfType<ComboBox>().First(c => AutomationProperties.GetName(c) == "Modo");
        mode.SelectedIndex = (int)MappingMode.Manual;
        var port = editor.GetVisualDescendants().OfType<NumericUpDown>().First(c => AutomationProperties.GetName(c) == "Porta local (0 = auto)");
        port.Value = 13140;
        editor.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Salvar perfil")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var saved = Assert.IsType<TunnelProfile>(gateway.Calls.First(c => c.Operation == "tunnel.save").Payload);
        Assert.Equal(MappingMode.Manual, saved.Mappings[0].Mode);
        Assert.Equal(13140, saved.Mappings[0].LocalPort);
        Assert.Equal(profile.Id, saved.Id);
        Assert.Empty(window.OwnedWindows);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SidebarNavigatesAllRealPages()
    {
        using var vm = new ToolkitViewModel(new FakeGateway(), action => Dispatcher.UIThread.Post(action));
        await vm.RefreshAsync();
        var window = new MainWindow(vm);
        window.Show();
        foreach (var page in new[] { "Contas", "Conexões", "Serviços", "DevTunnel", "Portas", "Proxy e PAC", "Logs", "Configurações", "Dashboard" })
        {
            var button = window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == $"Abrir {page}");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.IsVisible);
            Assert.NotEmpty(window.GetVisualDescendants().OfType<TextBlock>());
        }
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "Visão geral");
        if (Environment.GetEnvironmentVariable("TOOLKIT_UI_SCREENSHOT_DIR") is { } directory && Path.IsPathFullyQualified(directory))
        {
            Directory.CreateDirectory(directory);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(directory, "desktop-light.png"), PngBitmapEncoderOptions.Default);
            (Avalonia.Application.Current as ToolkitApp)!.ApplyTheme("Dark");
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(directory, "desktop-dark.png"), PngBitmapEncoderOptions.Default);
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task AccountEditorHasNoCredentialInput()
    {
        using var vm = new ToolkitViewModel(new FakeGateway(), action => Dispatcher.UIThread.Post(action));
        await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show();
        var accounts = window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == "Abrir Contas");
        accounts.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var add = window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Adicionar conta"));
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var editor = Assert.Single(window.OwnedWindows);
        Assert.Equal("Adicionar conta", editor.Title);
        Assert.DoesNotContain(editor.GetVisualDescendants().OfType<TextBox>(), t => t.PasswordChar != default);
        var cancel = editor.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Cancelar"));
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Close();
    }
}
