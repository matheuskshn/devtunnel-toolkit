using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.App;

public sealed class DesktopTray : IDisposable
{
    private readonly MainWindow window;
    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly TrayIcon icon;
    private bool exiting;
    private bool shuttingDown;
    private string? menuState;
    public bool Available { get; }

    public DesktopTray(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        this.window = window; this.desktop = desktop;
        Available = DesktopInstance.HasTrayHost();
        icon = new TrayIcon { Icon = window.Icon, ToolTipText = "DevTunnel Toolkit", IsVisible = Available };
        TrayIcon.SetIcons(Avalonia.Application.Current!, new TrayIcons { icon });
        icon.Clicked += (_, _) => Open();
        window.Closing += OnClosing;
        window.Model.StateChanged += OnStateChanged;
        desktop.ShutdownRequested += (_, _) => exiting = true;
        desktop.Exit += (_, _) => Dispose();
        Update();
    }

    public void Open(string? page = null)
    {
        if (page is not null) window.NavigateTo(page);
        window.Show(); window.ShowInTaskbar = true;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (exiting || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) return;
        if (Available && (window.Model.Snapshot?.Settings.CloseToTray ?? true)) { e.Cancel = true; window.Hide(); }
    }

    private void OnStateChanged(object? sender, EventArgs e) => Update();
    private void Update()
    {
        var model = window.Model;
        var state = model.Snapshot;
        var fingerprint = $"{model.IsConnected}:{model.IsBusy}:{state?.Settings.Proxy.Enabled}:" +
            string.Join("|", state?.Tunnels.Select(t => $"{t.Id}:{t.CanonicalId}:{state.Connections.FirstOrDefault(c => c.ProfileId == t.Id)?.State}") ?? []);
        icon.Icon = window.Icon;
        if (fingerprint == menuState) return; // Do not rebuild an open native menu on every watch tick.
        menuState = fingerprint;
        var connected = state?.Connections.Count(c => c.State == ConnectionState.Connected) ?? 0;
        var pending = state?.Connections.Count(c => ToolkitViewModel.IsConnectionPending(c.State)) ?? 0;
        var attention = state?.Connections.Any(c => c.State is ConnectionState.Failed or ConnectionState.AuthenticationRequired or ConnectionState.Degraded) == true;
        icon.ToolTipText = !model.IsConnected ? "DevTunnel Toolkit - Agent indisponível" : $"DevTunnel Toolkit - {connected} conectado(s){(pending > 0 ? $" - {pending} em andamento" : "")}{(attention ? " - atenção" : "")}";
        icon.Icon = window.Icon;
        var menu = new NativeMenu();
        void Add(NativeMenu target, string title, Func<Task> action, bool enabled = true)
        {
            var item = new NativeMenuItem(title) { IsEnabled = enabled };
            item.Click += async (_, _) =>
            {
                try { await action(); }
                catch (Exception error) { Open("Logs"); await model.RunAsync(_ => Task.FromException(error), ""); }
            };
            target.Items.Add(item);
        }
        Add(menu, "Abrir DevTunnel Toolkit", () => { Open(); return Task.CompletedTask; });
        if (state is not null && model.IsConnected)
        {
            var tunnels = new NativeMenu();
            foreach (var profile in state.Tunnels)
            {
                var connection = state.Connections.FirstOrDefault(c => c.ProfileId == profile.Id)?.State ?? ConnectionState.Stopped;
                var working = ToolkitViewModel.IsConnectionPending(connection);
                var active = connection == ConnectionState.Connected || working;
                Add(tunnels, $"{(working ? "Cancelar conexão" : active ? "Desconectar" : "Conectar")} {profile.CanonicalId}", async () => await model.ActionAsync(active ? "tunnel.stop" : "tunnel.connect", profile.Id), !model.IsBusy && connection != ConnectionState.Stopping);
            }
            menu.Items.Add(new NativeMenuItem("Túneis") { Menu = tunnels, IsEnabled = state.Tunnels.Length > 0 });
            Add(menu, state.Settings.Proxy.Enabled ? "Parar proxy local" : "Iniciar proxy local", async () => await model.ControlAsync(state.Settings.Proxy.Enabled ? "proxy.stop" : "proxy.start"), !model.IsBusy);
            Add(menu, "Parar conexões", async () => await model.ControlAsync("connections.stop"), !model.IsBusy);
        }
        Add(menu, "Proxy e PAC", () => { Open("Proxy e PAC"); return Task.CompletedTask; });
        Add(menu, "Ver logs", () => { Open("Logs"); return Task.CompletedTask; });
        menu.Items.Add(new NativeMenuItemSeparator());
        Add(menu, "Encerrar tudo", async () =>
        {
            if (shuttingDown) return;
            shuttingDown = true;
            try
            {
                if (!await window.ConfirmShutdownAsync()) return;
                exiting = true; icon.IsVisible = false; desktop.Shutdown();
            }
            finally { shuttingDown = false; }
        }, !model.IsBusy);
        icon.Menu = menu;
    }

    public void Dispose()
    {
        exiting = true;
        window.Closing -= OnClosing; window.Model.StateChanged -= OnStateChanged;
        icon.Dispose();
    }
}
