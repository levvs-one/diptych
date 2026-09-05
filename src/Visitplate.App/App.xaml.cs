using System.ComponentModel;
using System.Windows;

namespace Visitplate.App;

public partial class App : Application
{
    private readonly Dictionary<string, object> normalColors = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        ApplyContrast();
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemParametersChanged;
        base.OnExit(e);
    }

    private void SystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.InvokeAsync(ApplyContrast);
    }

    private void ApplyContrast()
    {
        var contrastColors = new Dictionary<string, object>
        {
            ["CanvasBrush"] = SystemColors.WindowBrush,
            ["PaperBrush"] = SystemColors.WindowBrush,
            ["InkBrush"] = SystemColors.WindowTextBrush,
            ["MutedBrush"] = SystemColors.WindowTextBrush,
            ["RuleBrush"] = SystemColors.WindowTextBrush,
            ["AccentBrush"] = SystemColors.HighlightBrush,
            ["SelectionBrush"] = SystemColors.HighlightBrush,
            ["SelectionInkBrush"] = SystemColors.HighlightTextBrush,
            ["OnAccentBrush"] = SystemColors.HighlightTextBrush
        };
        foreach (var (key, value) in contrastColors)
        {
            normalColors.TryAdd(key, Resources[key]);
            Resources[key] = SystemParameters.HighContrast ? value : normalColors[key];
        }
    }
}
