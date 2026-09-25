using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DevTunnelToolkit.App;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App.Tests;

public sealed class CliLoginUiTests
{
    [AvaloniaTheory]
    [InlineData(CliLoginMode.Browser)]
    [InlineData(CliLoginMode.DeviceCode)]
    [InlineData(CliLoginMode.WindowsIntegrated)]
    public async Task MicrosoftCliModeCanBeSavedWithoutApplicationRegistration(CliLoginMode mode)
    {
        var gateway = new FakeGateway();
        using var vm = new ToolkitViewModel(gateway, action => Dispatcher.UIThread.Post(action));
        await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show(); window.NavigateTo("Contas");
        window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Adicionar conta")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var editor = Assert.Single(window.OwnedWindows);
        var flow = editor.GetVisualDescendants().OfType<ComboBox>().First(c => AutomationProperties.GetName(c) == "Fluxo de login do CLI");
        Assert.Equal((int)CliLoginMode.Browser, flow.SelectedIndex);
        flow.SelectedIndex = (int)mode;
        editor.GetVisualDescendants().OfType<TextBox>().First(c => AutomationProperties.GetName(c) == "Nome do perfil").Text = "Fixture account";
        editor.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Salvar")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var saved = Assert.IsType<AccountProfile>(Assert.Single(gateway.Calls, c => c.Operation == "account.save").Payload);
        Assert.Equal(AccountProvider.MicrosoftCli, saved.Provider); Assert.Equal(mode, saved.CliLoginMode);
        Assert.Null(saved.ClientId); Assert.Empty(window.OwnedWindows); window.Close();
    }

    [AvaloniaFact]
    public async Task BrowserLoginWithoutDeviceChallengeStillOffersCancellation()
    {
        var gateway = new FakeGateway();
        var account = gateway.State.Accounts[0] with { Provider = AccountProvider.MicrosoftCli, CliLoginMode = CliLoginMode.Browser };
        gateway.State = gateway.State with { Accounts = [account], Logins = [new(account.Id, "Starting")] };
        using var vm = new ToolkitViewModel(gateway, action => Dispatcher.UIThread.Post(action)); await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show(); window.NavigateTo("Contas");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("janela Microsoft") == true);
        foreach (var label in new[] { "Editar", "Autenticar", "Validar", "Remover" })
            Assert.False(window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, label)).IsEnabled);
        window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Cancelar login")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(gateway.Calls, c => c.Operation == "account.cancel"); window.Close();
    }

    [Theory]
    [InlineData("AUTH_PROVIDER_AADSTS50076", "AADSTS50076")]
    [InlineData("AUTH_INTERACTION_REQUIRED", "login interativo")]
    [InlineData("AUTH_WINDOW_UNAVAILABLE", "janela de referência")]
    [InlineData("AUTH_FLOW_CHANGED", "tentativa foi interrompida")]
    [InlineData("LOGIN_IN_PROGRESS", "Conclua ou cancele")]
    public void SafeDiagnosticsAreActionable(string code, string message)
        => Assert.Contains(message, ToolkitViewModel.FriendlyError(new ToolkitException(code)));
}
