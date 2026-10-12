using System.Globalization;
using Avalonia.Data.Converters;
using ScumStudio.App.Localization;
using ScumStudio.Core.Settings;

namespace ScumStudio.App.Converters;

/// <summary>
/// A 3D quality preset's name in the UI language (the pickers showed the enum's English names in every language). Used in a
/// MultiBinding whose second value is <see cref="Loc.Language"/>, so an open picker follows a language switch.
/// </summary>
public sealed class RenderQualityTextConverter : IMultiValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c>.</summary>
    public static RenderQualityTextConverter Instance { get; } = new();

    /// <inheritdoc />
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count > 0 && values[0] is RenderQuality quality ? Text(quality) : string.Empty;

    /// <summary>The shown name of <paramref name="quality"/>.</summary>
    public static string Text(RenderQuality quality) => quality switch
    {
        RenderQuality.Performance => Loc.T("Map.Quality.Performance"),
        RenderQuality.High => Loc.T("Map.Quality.High"),
        RenderQuality.Ultra => Loc.T("Map.Quality.Ultra"),
        _ => Loc.T("Map.Quality.Balanced"),
    };
}
