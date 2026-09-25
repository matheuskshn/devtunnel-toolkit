using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DevTunnelToolkit.App;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App.Tests;

public sealed class ProxyUiTests
{
    [AvaloniaFact]
    public async Task ProxyEditorSavesDestinationAndDefaultWithoutSystemProxyChanges()
    {
        var gateway = new FakeGateway();
        using var vm = new ToolkitViewModel(gateway, action => Dispatcher.UIThread.Post(action));
        await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show(); window.NavigateTo("Proxy e PAC");
        Click(window, "Editar proxy"); var editor = Assert.Single(window.OwnedWindows);
        Click(editor, "Adicionar regra de proxy"); editor.UpdateLayout();
        Find<TextBox>(editor, "Nome da regra").Text = "Public fixture";
        Find<TextBox>(editor, "Destinos").Text = "example.org, example.net\napi.example.org";
        Find<ComboBox>(editor, "Quando nenhuma regra corresponder").SelectedIndex = 1;
        Find<NumericUpDown>(editor, "Porta do destino (0 = qualquer)").Value = 443;
        Click(editor, "Salvar proxy");
        var settings = Assert.IsType<AppSettings>(Assert.Single(gateway.Calls, c => c.Operation == "settings.save").Payload);
        Assert.Equal(RouteAction.Block, settings.Proxy.DefaultAction);
        Assert.Equal("example.org, example.net\napi.example.org", Assert.Single(settings.Proxy.Rules).Destination);
        Assert.Equal(443, settings.Proxy.Rules[0].DestinationPort);
        Assert.False(settings.Proxy.Enabled);
        Assert.Empty(window.OwnedWindows); window.Close();
    }

    [AvaloniaFact]
    public async Task ManualPacAndTrayPreferenceRoundTripFromEditor()
    {
        var gateway = new FakeGateway();
        using var vm = new ToolkitViewModel(gateway, action => Dispatcher.UIThread.Post(action));
        await vm.RefreshAsync();
        var window = new MainWindow(vm); window.Show(); window.NavigateTo("Proxy e PAC");
        Click(window, "Editar PAC"); var editor = Assert.Single(window.OwnedWindows);
        Find<ComboBox>(editor, "Modo do PAC").SelectedItem = "Manual";
        var script = editor.GetVisualDescendants().OfType<TextBox>().Single(t => t.AcceptsReturn);
        const string manual = "function FindProxyForURL(url, host) { return 'DIRECT'; }";
        script.Text = manual;
        editor.GetVisualDescendants().OfType<CheckBox>().Single(t => Equals(t.Content, "Ao fechar a janela, manter na bandeja")).IsChecked = false;
        Click(editor, "Salvar");
        var settings = Assert.IsType<AppSettings>(Assert.Single(gateway.Calls, c => c.Operation == "settings.save").Payload);
        Assert.Equal(PacMode.Manual, settings.Pac.Mode); Assert.Equal(manual, settings.Pac.ManualScript);
        Assert.False(settings.CloseToTray); Assert.Empty(window.OwnedWindows); window.Close();
    }

    [Fact]
    public async Task ASecondDesktopActivatesTheExistingInstanceAndLockIsReleased()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dtk-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var primary = new DesktopInstance(directory))
            {
                Assert.True(primary.IsPrimary);
                var focus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                primary.Listen(() => focus.TrySetResult());
                using var secondary = new DesktopInstance(directory);
                Assert.False(secondary.IsPrimary); Assert.True(await secondary.ActivateAsync());
                await focus.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            using var replacement = new DesktopInstance(directory);
            Assert.True(replacement.IsPrimary);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().First(t => AutomationProperties.GetName(t) == name);
    private static void Click(Window window, string text) => window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, text)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
