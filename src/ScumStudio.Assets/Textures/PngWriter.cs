using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ScumStudio.Assets.Textures;

/// <summary>Writes <see cref="TextureImage"/>s (or raw RGBA8 buffers) as PNG files with ImageSharp.</summary>
public static class PngWriter
{
    /// <summary>Encodes an RGBA8 buffer as PNG into <paramref name="stream"/>.</summary>
    public static async Task WriteAsync(Stream stream, byte[] rgba, int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.Length < (long)width * height * 4)
        {
            throw new ArgumentException($"RGBA buffer holds {rgba.Length} bytes; {width}x{height} needs {(long)width * height * 4}.", nameof(rgba));
        }

        using var image = Image.LoadPixelData<Rgba32>(rgba.AsSpan(0, width * height * 4), width, height);
        await image.SaveAsync(stream, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Saves <paramref name="image"/> as a PNG file (parent folders are created).</summary>
    public static async Task SaveAsync(TextureImage image, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await WriteAsync(stream, image.Rgba, image.Width, image.Height, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="SaveAsync(TextureImage, string, CancellationToken)"/>.</summary>
    public static void Save(TextureImage image, string path) => SaveAsync(image, path).GetAwaiter().GetResult();
}
