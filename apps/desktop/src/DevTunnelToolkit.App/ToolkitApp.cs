using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace DevTunnelToolkit.App;

public sealed class ToolkitApp : Avalonia.Application
{
    private readonly Dictionary<string, Bitmap> _brandImages = [];
    private readonly Dictionary<string, WindowIcon> _brandWindowIcons = [];

    public override void Initialize()
    {
        foreach (var variant in new[] { "", "-dark", "-contrast" })
        {
            using var image = AssetLoader.Open(new Uri($"avares://DevTunnelToolkit.App/Assets/logo-icon{variant}.png"));
            _brandImages[variant] = new Bitmap(image);
            _brandWindowIcons[variant] = new WindowIcon(_brandImages[variant]);
        }
        Styles.Add(new FluentTheme());
        Styles.Add(new Style(x => x.OfType<Window>())
        {
            Setters = { new Setter(TemplatedControl.FontFamilyProperty, new FontFamily("Inter, Segoe UI, Noto Sans, sans-serif")), new Setter(TemplatedControl.FontSizeProperty, 13d) }
        });
        Styles.Add(new Style(x => x.OfType<Button>())
        {
            Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)), new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 8)) }
        });
        ActualThemeVariantChanged += (_, _) => UpdateColors();
        if (PlatformSettings is { } platform) platform.ColorValuesChanged += (_, _) => UpdateColors();
        ApplyTheme("System");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            var tray = new DesktopTray(window, desktop);
            Program.Instance?.Listen(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => tray.Open()));
            var hideOnFirstOpen = Program.StartInTray;
            window.Opened += (_, _) =>
            {
                if (hideOnFirstOpen && tray.Available) Avalonia.Threading.Dispatcher.UIThread.Post(window.Hide);
                hideOnFirstOpen = false;
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private bool _highContrast;

    public void ApplyTheme(string theme)
    {
        _highContrast = theme == "HighContrast";
        RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" or "HighContrast" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        UpdateColors();
    }

    private void UpdateColors()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var highContrast = _highContrast || PlatformSettings?.GetColorValues().ContrastPreference == ColorContrastPreference.High;
        var palette = highContrast
            ? new[] { "#000000", "#000000", "#000000", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFF00", "#000000", "#FFFFFF" }
            : dark
                ? new[] { "#091421", "#101F30", "#0C1928", "#DEEBFA", "#9CACBF", "#293E53", "#36D5E8", "#07222C", "#76DFC0" }
                : new[] { "#F1F5FA", "#FFFFFF", "#E9EFF7", "#102D47", "#536B80", "#D4DFEB", "#006CC6", "#FFFFFF", "#137763" };
        var keys = new[] { "Canvas", "Surface", "Subtle", "Text", "Muted", "Stroke", "Accent", "AccentText", "Success" };
        for (var i = 0; i < keys.Length; i++) Resources[keys[i]] = new SolidColorBrush(Color.Parse(palette[i]));
        Resources["BrandIcon"] = _brandImages[highContrast ? "-contrast" : dark ? "-dark" : ""];
        Resources["BrandWindowIcon"] = _brandWindowIcons[highContrast ? "-contrast" : dark ? "-dark" : ""];
    }
}
