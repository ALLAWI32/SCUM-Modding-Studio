using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ScumStudio.App.Converters;

/// <summary>
/// Converts a <c>#RRGGBB</c> / <c>#AARRGGBB</c> string (view models expose colours as plain strings) to an immutable
/// brush. Unparsable or empty text gives <see cref="Fallback"/>.
/// </summary>
public sealed class HexBrushConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c>.</summary>
    public static HexBrushConverter Instance { get; } = new();

    /// <summary>Brush returned for text that is not a colour.</summary>
    public static IBrush Fallback { get; } = new ImmutableSolidColorBrush(Color.Parse("#8A8672"));

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && Color.TryParse(text, out var color)
            ? new ImmutableSolidColorBrush(color)
            : Fallback;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ISolidColorBrush brush ? brush.Color.ToString() : null;
}
