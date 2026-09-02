using Avalonia;
using Avalonia.Media;

namespace EntKube.Installer.Gui.ViewModels;

public enum LogKind
{
    Step,
    Detail,
    Warning,
}

/// <summary>
/// One line of install output, carrying its own colour.
///
/// The brush is a property rather than a XAML converter because there are three cases and one
/// consumer; a converter would be more machinery for the same result.
/// </summary>
public sealed class LogLineView(string text, LogKind kind)
{
    public string Text { get; } = text;

    public LogKind Kind { get; } = kind;

    public IBrush Brush => Kind switch
    {
        // Chosen to stay legible against both the light and the dark Fluent background, since the
        // window follows the system theme and a fixed dark grey would vanish on one of them.
        LogKind.Warning => new SolidColorBrush(Color.FromRgb(0xC2, 0x62, 0x2D)),
        LogKind.Detail => new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),

        // Ordinary steps take the theme's own text colour, so they read as normal body text in
        // whichever variant is active.
        _ => ThemeForeground,
    };

    private static IBrush ThemeForeground
    {
        get
        {
            if (Application.Current is { } app
                && app.TryGetResource("SystemControlForegroundBaseHighBrush", app.ActualThemeVariant, out object? found)
                && found is IBrush brush)
            {
                return brush;
            }

            // Only reached if the theme has not loaded — during a design-time preview, for instance.
            // Mid-grey is the safe answer because it is readable in either variant.
            return new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
        }
    }
}
