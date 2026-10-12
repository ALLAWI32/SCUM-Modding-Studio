using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ScumStudio.App.Services;

/// <summary>A shared mod's picture: the large preview and the gallery card's thumbnail, both JPEG.</summary>
/// <param name="Preview">At most <see cref="SharePicture.PreviewWidth"/> x <see cref="SharePicture.PreviewHeight"/>, at most <see cref="SharePicture.PreviewMaxBytes"/>.</param>
/// <param name="Thumb">Exactly <see cref="SharePicture.ThumbWidth"/> x <see cref="SharePicture.ThumbHeight"/> (cover crop), at most <see cref="SharePicture.ThumbMaxBytes"/>.</param>
/// <param name="Width">The preview's width.</param>
/// <param name="Height">The preview's height.</param>
public sealed record SharePictures(byte[] Preview, byte[] Thumb, int Width, int Height);

/// <summary>
/// Turns a Map capture or an imported image into <see cref="SharePictures"/> small enough to upload: the quality steps
/// down (and then the size) until each JPEG fits its limit. CPU work: call it off the UI thread.
/// </summary>
public static class SharePicture
{
    /// <summary>Largest preview width.</summary>
    public const int PreviewWidth = 1920;

    /// <summary>Largest preview height.</summary>
    public const int PreviewHeight = 1080;

    /// <summary>Largest preview file.</summary>
    public const int PreviewMaxBytes = 800 * 1024;

    /// <summary>Thumbnail width.</summary>
    public const int ThumbWidth = 480;

    /// <summary>Thumbnail height.</summary>
    public const int ThumbHeight = 270;

    /// <summary>Largest thumbnail file.</summary>
    public const int ThumbMaxBytes = 120 * 1024;

    /// <summary>Images with more pixels than this are refused before decoding (a 16k x 12k photo is the most anyone needs).</summary>
    public const long MaxSourcePixels = 200_000_000;

    /// <summary>The file types the image picker offers.</summary>
    public static IReadOnlyList<string> FilePatterns { get; } = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"];

    /// <summary>Pictures from top-down RGBA8 pixels (<see cref="IViewportCapture"/>).</summary>
    public static SharePictures FromRgba(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || rgba.Length != width * height * 4)
        {
            throw new ArgumentException($"Expected {width}x{height} RGBA8 pixels, got {rgba.Length} bytes.", nameof(rgba));
        }

        using var image = Image.LoadPixelData<Rgba32>(rgba, width, height);
        return Encode(image);
    }

    /// <summary>
    /// Pictures from a PNG, JPEG, BMP, GIF or WebP file, scaled down keeping its shape.
    /// </summary>
    /// <exception cref="InvalidDataException">Not an image this reads, a damaged one, or a huge one.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static SharePictures FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            var info = Image.Identify(path);
            if ((long)info.Width * info.Height > MaxSourcePixels)
            {
                throw new InvalidDataException($"{info.Width} x {info.Height} pixels is too large.");
            }

            // JPEG decodes straight to the smaller size; the others are decoded and then scaled.
            var options = new DecoderOptions { TargetSize = new Size(PreviewWidth, PreviewHeight) };
            using var image = Image.Load<Rgba32>(options, path);
            image.Mutate(x => x.AutoOrient());
            return Encode(image);
        }
        catch (ImageFormatException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    /// <summary>The preview size of a <paramref name="width"/> x <paramref name="height"/> image: scaled down (never up) into 1920 x 1080.</summary>
    public static (int Width, int Height) FitPreview(int width, int height)
    {
        var scale = Math.Min(1.0, Math.Min((double)PreviewWidth / width, (double)PreviewHeight / height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static SharePictures Encode(Image<Rgba32> source)
    {
        // JPEG has no alpha: a transparent PNG gets a dark backdrop instead of whatever colour its clear pixels hold.
        source.Mutate(x => x.BackgroundColor(Color.Black));
        var (w, h) = FitPreview(source.Width, source.Height);
        byte[] preview;
        while (true)
        {
            using var sized = source.Clone(x => x.Resize(w, h, KnownResamplers.Lanczos3));
            if (Jpeg(sized, PreviewMaxBytes, 85) is { } fits)
            {
                preview = fits;
                break;
            }

            // Even the lowest quality is too large (a noisy picture): smaller, then try again.
            (w, h) = (Math.Max(1, w * 4 / 5), Math.Max(1, h * 4 / 5));
        }

        using var thumb = source.Clone(x => x.Resize(new ResizeOptions
        {
            Size = new Size(ThumbWidth, ThumbHeight),
            Mode = ResizeMode.Crop,
            Sampler = KnownResamplers.Lanczos3,
        }));
        if (Jpeg(thumb, ThumbMaxBytes, 80) is not { } small)
        {
            // Only pure noise gets here; softened, it fits.
            thumb.Mutate(x => x.GaussianBlur(1.5f));
            small = Jpeg(thumb, ThumbMaxBytes, 60) ?? Jpeg(thumb, int.MaxValue, 30)!;
        }

        return new SharePictures(preview, small, w, h);
    }

    /// <summary>The image as JPEG at the highest quality from <paramref name="quality"/> down to 30 that fits <paramref name="maxBytes"/>; null when none does.</summary>
    private static byte[]? Jpeg(Image<Rgba32> image, int maxBytes, int quality)
    {
        for (var q = quality; q >= 30; q -= 10)
        {
            using var stream = new MemoryStream();
            image.SaveAsJpeg(stream, new JpegEncoder { Quality = q, ColorType = JpegEncodingColor.YCbCrRatio420 });
            if (stream.Length <= maxBytes)
            {
                return stream.ToArray();
            }
        }

        return null;
    }
}
