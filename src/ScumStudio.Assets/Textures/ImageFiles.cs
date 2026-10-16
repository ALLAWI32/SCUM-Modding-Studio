using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ScumStudio.Assets.Textures;

/// <summary>Pictures the user brings (PNG, JPG): read to RGBA8 and fitted to a texture's size with ImageSharp.</summary>
public static class ImageFiles
{
    /// <summary>Smallest texture edge the studio writes.</summary>
    public const int MinSize = 4;

    /// <summary>Largest texture edge the studio writes.</summary>
    public const int MaxSize = 4096;

    /// <summary>Above this edge the game's vehicle texture group may load a smaller mip (TEXTUREGROUP_Vehicle).</summary>
    public const int GroupCapSize = 2048;

    /// <summary>The file extensions <see cref="Load(string)"/> reads (for a file picker).</summary>
    public const string Extensions = "png;jpg;jpeg";

    /// <summary>True for a power of two from <see cref="MinSize"/> to <see cref="MaxSize"/>.</summary>
    public static bool IsValidSize(int size) => size is >= MinSize and <= MaxSize && (size & (size - 1)) == 0;

    /// <summary>Reads a PNG or JPG as RGBA8 (top row first).</summary>
    /// <exception cref="InvalidDataException">The file is not a picture ImageSharp can read.</exception>
    public static TextureImage Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Load(stream, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Reads a PNG or JPG stream as RGBA8.</summary>
    /// <exception cref="InvalidDataException">The data is not a picture ImageSharp can read.</exception>
    public static TextureImage Load(Stream stream, string name)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using var image = Image.Load<Rgba32>(stream);
            var rgba = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(rgba);
            return new TextureImage(name, image.Width, image.Height, rgba, "PF_R8G8B8A8", 0, image.Width, image.Height, true, false);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    /// <summary>The pixels of <paramref name="image"/> at <paramref name="width"/> x <paramref name="height"/> (Lanczos; the same bytes when the size matches).</summary>
    public static byte[] Fit(TextureImage image, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width == width && image.Height == height)
        {
            return image.Rgba;
        }

        using var picture = Image.LoadPixelData<Rgba32>(image.Rgba.AsSpan(0, image.Width * image.Height * 4), image.Width, image.Height);
        picture.Mutate(x => x.Resize(width, height, KnownResamplers.Lanczos3));
        var rgba = new byte[width * height * 4];
        picture.CopyPixelDataTo(rgba);
        return rgba;
    }
}
