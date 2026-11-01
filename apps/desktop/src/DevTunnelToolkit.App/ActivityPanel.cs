using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace DevTunnelToolkit.App;

// Update text and clocks in place: snapshots and timer ticks must not replace
// buttons, lose focus, change scroll position or reset an indeterminate animation.
internal sealed class ActivityPanel : StackPanel
{
    private readonly ToolkitViewModel _model;
    private readonly Func<OperationActivity?> _activity;
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 3 };
    private readonly TextBlock _description = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _timing = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ActivityPanel(ToolkitViewModel model, Func<OperationActivity?> activity)
    {
        _model = model; _activity = activity; Spacing = 6; Margin = new Thickness(0, 8, 0, 4);
        AutomationProperties.SetName(_progress, "Operação em andamento");
        Children.Add(_progress); Children.Add(_description); Children.Add(_timing);
        _timer.Tick += (_, _) => Update();
        AttachedToVisualTree += (_, _) => { _model.StateChanged += Changed; Update(); };
        DetachedFromVisualTree += (_, _) => { _model.StateChanged -= Changed; _timer.Stop(); };
        Update();
    }

    private void Changed(object? sender, EventArgs e) => Update();
    private void Update()
    {
        var activity = _activity();
        IsVisible = activity is not null;
        _progress.IsVisible = activity?.Running == true;
        _description.Text = activity?.Description ?? "";
        _timing.Text = activity?.Timing(DateTimeOffset.UtcNow) ?? "";
        _timing.IsVisible = _timing.Text.Length > 0;
        if (activity?.Running == true && VisualRoot is not null) _timer.Start(); else _timer.Stop();
    }
}
