using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ScumStudio.Rendering.Imaging;

/// <summary>Writes read-back frames (<see cref="Targets.RenderTarget.ReadColorRgba"/>) to image files.</summary>
public static class ImageExport
{
    /// <summary>Saves top-down RGBA8 pixels as a PNG file (parent directories are created).</summary>
    public static async Task SavePngAsync(byte[] rgba, int width, int height, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (width <= 0 || height <= 0 || rgba.Length != width * height * 4)
        {
            throw new ArgumentException($"Expected {width}x{height} RGBA8 pixels ({(long)width * height * 4} bytes), got {rgba.Length} bytes.", nameof(rgba));
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var image = Image.LoadPixelData<Rgba32>(rgba, width, height);
        await image.SaveAsPngAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads a PNG (or any ImageSharp-supported file) as top-down RGBA8 pixels.</summary>
    public static async Task<(byte[] Rgba, int Width, int Height)> LoadRgbaAsync(string path, CancellationToken cancellationToken = default)
    {
        using var image = await Image.LoadAsync<Rgba32>(path, cancellationToken).ConfigureAwait(false);
        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);
        return (pixels, image.Width, image.Height);
    }
}
