using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace ScumStudio.App.Services;

/// <summary>An accent colour offered by Settings &gt; Appearance.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Hex"><c>#RRGGBB</c>.</param>
public sealed record AccentOption(string Name, string Hex);

/// <summary>The accent colours and the code that applies one to the running application.</summary>
public static class AccentPalette
{
    /// <summary>Default accent: blaze orange, the same colour as the viewport's selection highlight.</summary>
    public const string DefaultHex = "#E87B2F";

    /// <summary>The accent of releases before the FIELD MANUAL design; a saved value is migrated to <see cref="DefaultHex"/>.</summary>
    public const string LegacyDefaultHex = "#4C8DFF";

    /// <summary>
    /// Offered accents: the field colours first, then bright ones (owner: "more colours, let me choose"); any other colour
    /// comes from the picker next to them. All are light enough for dark text (<c>OnAccentBrush</c>) on accent buttons.
    /// </summary>
    public static IReadOnlyList<AccentOption> Options { get; } =
    [
        new("Blaze", DefaultHex),
        new("Signal", "#E8C547"),
        new("Khaki", "#C8B98C"),
        new("Olive", "#9DB35E"),
        new("Teal", "#4FB3A2"),
        new("Blue", "#6C9BDB"),
        new("Slate", "#9AA3A8"),
        new("Red", "#EF5B5B"),
        new("Coral", "#F2876B"),
        new("Gold", "#F5B83D"),
        new("Lime", "#B6DB4A"),
        new("Mint", "#5FD99A"),
        new("Cyan", "#38D3D3"),
        new("Sky", "#5BB8F5"),
        new("Violet", "#A98BEA"),
        new("Pink", "#EE82B8"),
    ];

    /// <summary><c>#RRGGBB</c> of <paramref name="color"/> (alpha dropped).</summary>
    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Parses <paramref name="hex"/>, falling back to <see cref="DefaultHex"/>.</summary>
    public static Color ParseOrDefault(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var color) ? color : Color.Parse(DefaultHex);

    /// <summary>
    /// Replaces the accent brushes/colours of <paramref name="application"/> and the Fluent dark palette's accent.
    /// Views use DynamicResource, so the change is immediate.
    /// </summary>
    public static void Apply(Application? application, string? hex)
    {
        if (application is null)
        {
            return;
        }

        if (!application.CheckAccess())
        {
            // Resources belong to the UI thread; callers on other threads (tools, tests) are marshalled.
            Dispatcher.UIThread.Post(() => Apply(application, hex));
            return;
        }

        var color = ParseOrDefault(hex);
        var resources = application.Resources;
        var soft = WithAlpha(color, 0x2E);
        var softer = WithAlpha(color, 0x3D);
        var strong = Lighten(color, 0.22);
        resources["AccentColor"] = color;
        resources["AccentSoftColor"] = soft;
        resources["AccentBrush"] = new SolidColorBrush(color);
        resources["AccentSoftBrush"] = new SolidColorBrush(soft);
        resources["AccentStrongBrush"] = new SolidColorBrush(strong);
        resources["TreeViewItemBackgroundSelected"] = new SolidColorBrush(soft);
        resources["TreeViewItemBackgroundSelectedPointerOver"] = new SolidColorBrush(softer);
        resources["SystemControlHighlightListAccentLowBrush"] = new SolidColorBrush(soft);
        resources["SystemControlHighlightListAccentMediumBrush"] = new SolidColorBrush(softer);
        resources["ToggleButtonBackgroundChecked"] = new SolidColorBrush(soft);
        resources["ToggleButtonBackgroundCheckedPointerOver"] = new SolidColorBrush(softer);
        resources["ToggleButtonForegroundChecked"] = new SolidColorBrush(strong);
        resources["ToggleButtonForegroundCheckedPointerOver"] = new SolidColorBrush(strong);

        foreach (var style in application.Styles)
        {
            if (style is FluentTheme fluent && fluent.Palettes.TryGetValue(ThemeVariant.Dark, out var palette))
            {
                palette.Accent = color;
            }
        }
    }

    /// <summary>Maps a saved accent to the current palette (the old default blue becomes Blaze).</summary>
    public static string Migrate(string? hex) =>
        string.IsNullOrWhiteSpace(hex) || string.Equals(hex.Trim(), LegacyDefaultHex, StringComparison.OrdinalIgnoreCase)
            ? DefaultHex
            : hex;

    /// <summary>Returns <paramref name="color"/> with alpha <paramref name="alpha"/>.</summary>
    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>Mixes <paramref name="color"/> towards white by <paramref name="amount"/> (0-1).</summary>
    public static Color Lighten(Color color, double amount)
    {
        byte Mix(byte c) => (byte)Math.Clamp(Math.Round(c + ((255 - c) * amount)), 0, 255);
        return Color.FromRgb(Mix(color.R), Mix(color.G), Mix(color.B));
    }
}
