using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ScumStudio.Assets.Textures;

namespace ScumStudio.App.Converters;

/// <summary>
/// Converts a decoded <see cref="TextureImage"/> (RGBA8, as view models hold it) to an Avalonia bitmap for an
/// <c>Image</c>. One bitmap is kept per image, so list rows that are recycled do not copy the pixels again.
/// </summary>
public sealed class TextureImageConverter : IValueConverter
{
    private static readonly ConditionalWeakTable<TextureImage, Bitmap> Cache = new();

    /// <summary>Shared instance for <c>{x:Static}</c>.</summary>
    public static TextureImageConverter Instance { get; } = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TextureImage { Width: > 0, Height: > 0 } image ? Cache.GetValue(image, ToBitmap) : null;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Copies the RGBA pixels into a new unpremultiplied bitmap.</summary>
    public static Bitmap ToBitmap(TextureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var buffer = bitmap.Lock();
        var row = image.Width * 4;
        for (var y = 0; y < image.Height; y++)
        {
            Marshal.Copy(image.Rgba, y * row, buffer.Address + (y * buffer.RowBytes), row);
        }

        return bitmap;
    }
}
