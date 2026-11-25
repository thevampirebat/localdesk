using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace LocalDesk;

internal static class DeskPalette
{
    static readonly Dictionary<string, SolidColorBrush> Brushes = [];
    static bool light;

    public static IBrush Get(string hex)
    {
        if (!Brushes.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush(Adapt(Color.Parse(hex)));
            Brushes.Add(hex, brush);
        }
        return brush;
    }

    public static void Apply(ThemeVariant theme)
    {
        var useLight = theme == ThemeVariant.Light;
        if (useLight == light) return;
        light = useLight;
        foreach (var (hex, brush) in Brushes) brush.Color = Adapt(Color.Parse(hex));
    }

    static Color Adapt(Color color)
    {
        if (!light) return color;
        var hsl = color.ToHsl();
        var lightness = hsl.L < 0.32 ? 1 - hsl.L * 0.47 : (1 - hsl.L) * 0.61;
        return new HslColor(hsl.A, hsl.H, hsl.S * 0.72, lightness).ToRgb();
    }
}

public sealed partial class MainWindow
{
    int ChatTextSize => Math.Clamp(preferences.ChatTextSize, 13, 24);

    void ApplyAppearance()
    {
        RequestedThemeVariant = preferences.Theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        if (Application.Current is { } app) app.RequestedThemeVariant = RequestedThemeVariant;
        DeskPalette.Apply(ActualThemeVariant);
        input.FontSize = ChatTextSize;
    }
}
