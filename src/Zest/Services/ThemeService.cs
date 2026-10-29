using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Zest.Services;

/// <summary>A color theme from Lemon's res/values-night/lemon_colors.xml; surface tones are derived from it.</summary>
public sealed record ThemePalette(
    string Name,
    Color Primary,
    Color OnPrimary,
    Color PrimaryContainer,
    Color Secondary,
    Color Tertiary,
    Color Background,
    Color OnSurface,
    Color OnSurfaceVariant,
    Color Outline,
    Color Accent);

public static class ThemeService
{
    private static ThemePalette P(string name, string primary, string onPrimary, string primaryContainer, string secondary,
        string tertiary, string background, string onSurface, string onSurfaceVariant, string outline, string? accent = null) =>
        new(name, Color.Parse(primary), Color.Parse(onPrimary), Color.Parse(primaryContainer), Color.Parse(secondary),
            Color.Parse(tertiary), Color.Parse(background), Color.Parse(onSurface), Color.Parse(onSurfaceVariant),
            Color.Parse(outline), Color.Parse(accent ?? tertiary));

    public static IReadOnlyList<ThemePalette> Themes { get; } =
    [
        // Lemon's redesigned interface (lemon_modern.xml); outline chosen so the derived outline lands on #5A544D.
        P("Lemon", "#FFD43B", "#1A1814", "#4A3D00", "#D9CDAE", "#FF8A78", "#0F0E0C", "#F5F0E6", "#BDB5A8", "#978D82", "#8BC34A"),
        P("Blue", "#B7C4FF", "#002681", "#0039B5", "#C2C5DD", "#E4BADA", "#1B1B1F", "#E4E1E6", "#C6C5D0", "#90909A"),
        P("Cyan", "#B7FFF7", "#007D81", "#00A0B5", "#C2DDDB", "#BAE1E4", "#1B1F1E", "#E1E6E5", "#C5D0CF", "#909A98"),
        P("Green", "#D0FFB7", "#368100", "#1BB500", "#CDDDC2", "#CAE4BA", "#1C1F1B", "#E2E6E1", "#C9D0C5", "#959A90"),
        P("Yellow", "#FFEDB7", "#817F00", "#B5A300", "#DDDDC2", "#E0E4BA", "#1F1F1B", "#E6E6E1", "#D0D0C5", "#9A9990"),
        P("Orange", "#FFCBB7", "#812500", "#B55400", "#DDCDC2", "#E4CCBA", "#1F1D1B", "#E6E3E1", "#D0CAC5", "#9A9490"),
        P("Red", "#FFB7B7", "#810000", "#B50000", "#DDC2C2", "#E4BABA", "#1F1B1B", "#E6E1E1", "#D0C5C5", "#9A9090"),
        P("Pink", "#FFB7C5", "#81001E", "#B50033", "#DDC2C7", "#E4BAC4", "#1F1B1C", "#E6E1E2", "#D0C5C8", "#9A9093"),
        P("Violet", "#DAB7FF", "#350068", "#5A00B5", "#D2C2DD", "#DACCE4", "#1F1B1F", "#E6E4E6", "#D0C6D0", "#9A909A"),
        P("Gray", "#B7B7B7", "#2B2B2B", "#424242", "#C2C2C2", "#DADADA", "#1B1B1B", "#E4E4E4", "#C6C6C6", "#909090"),
    ];

    public static ThemePalette Find(string name) =>
        Themes.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];

    /// <summary>Replaces the app's color resources; everything bound with DynamicResource restyles immediately.</summary>
    public static void Apply(string name)
    {
        var app = Application.Current!;
        var t = Find(name);
        var bg = t.Background;

        var colors = new Dictionary<string, Color>
        {
            ["Background"] = bg,
            ["Surface"] = Mix(bg, t.OnSurface, 0.02),
            ["ContainerLow"] = Mix(bg, t.OnSurface, 0.04),
            ["Container"] = Mix(bg, t.OnSurface, 0.07),
            ["ContainerHigh"] = Mix(bg, t.OnSurface, 0.10),
            ["ContainerHighest"] = Mix(bg, t.OnSurface, 0.14),
            ["OnSurface"] = t.OnSurface,
            ["OnSurfaceVariant"] = t.OnSurfaceVariant,
            ["Outline"] = Mix(bg, t.Outline, 0.55),
            ["OutlineVariant"] = Mix(bg, t.Outline, 0.30),
            ["Primary"] = t.Primary,
            ["PrimaryHover"] = Mix(t.Primary, Colors.White, 0.35),
            ["OnPrimary"] = t.OnPrimary,
            ["PrimaryContainer"] = t.PrimaryContainer,
            ["Secondary"] = t.Secondary,
            ["Tertiary"] = t.Tertiary,
            ["Scrim"] = WithAlpha(bg, 0xD9),
            ["SearchFill"] = WithAlpha(Mix(bg, t.OnSurface, 0.02), 0x80),
        };
        foreach (var (key, color) in colors)
        {
            app.Resources[key] = color;
            app.Resources[key + "Brush"] = new SolidColorBrush(color);
        }

        // Lemon's GradientBorderCardView: primary into the accent (leaf green for the Lemon theme).
        app.Resources["CardGlowBrush"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(t.Primary, 0), new GradientStop(t.Accent, 1) },
        };
        app.Resources["GlowTopLeftBrush"] = Glow(t.Accent, 0, 0);
        app.Resources["GlowBottomRightBrush"] = Glow(t.Primary, 1, 1);

        // Fluent's accent (focus rings, checkboxes, text selection) follows the theme's primary.
        if (app.Resources.ThemeDictionaries.TryGetValue(ThemeVariant.Dark, out var dark) && dark is IResourceDictionary d)
        {
            d["SystemAccentColor"] = t.Primary;
            d["SystemAccentColorDark1"] = Mix(t.Primary, Colors.Black, 0.1);
            d["SystemAccentColorLight1"] = Mix(t.Primary, Colors.White, 0.3);
        }
    }

    // lemon_background_gradient: a faint radial glow in one corner.
    private static RadialGradientBrush Glow(Color color, double x, double y) => new()
    {
        Center = new RelativePoint(x, y, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(x, y, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.6, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.8, RelativeUnit.Relative),
        GradientStops = { new GradientStop(WithAlpha(color, 0x16), 0), new GradientStop(WithAlpha(color, 0), 1) },
    };

    public static Color Mix(Color a, Color b, double amount) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * amount),
        (byte)(a.R + (b.R - a.R) * amount),
        (byte)(a.G + (b.G - a.G) * amount),
        (byte)(a.B + (b.B - a.B) * amount));

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
}
