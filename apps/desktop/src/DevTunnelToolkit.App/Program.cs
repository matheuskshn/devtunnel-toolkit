using Avalonia;

namespace DevTunnelToolkit.App;

public static class Program
{
    public static string? DataDirectory { get; private set; }
    public static bool StartInTray { get; private set; }
    public static DesktopInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        StartInTray = args.Contains("--tray");
        args = args.Where(a => a != "--tray").ToArray();
        if (args.Length > 0)
        {
            if (args.Length != 2 || args[0] != "--data-dir" || !Path.IsPathFullyQualified(args[1]))
                return 2;
            DataDirectory = Path.GetFullPath(args[1]);
        }
        using var instance = new DesktopInstance(DataDirectory);
        Instance = instance;
        if (!instance.IsPrimary) return instance.ActivateAsync().GetAwaiter().GetResult() ? 0 : 1;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ToolkitApp>().UsePlatformDetect();
}
