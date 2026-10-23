using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ScumStudio.Viewport.Decals;

/// <summary>Why a picture cannot be a sticker.</summary>
public enum StickerProblem
{
    /// <summary>Not a picture the studio reads (a PSD, an AVIF or HEIC photo renamed <c>.png</c> …).</summary>
    NotAPicture,

    /// <summary>A picture whose data is broken or cut short.</summary>
    Damaged,

    /// <summary>More pixels on a side than <see cref="StickerPictures.MaxInputSide"/>.</summary>
    TooLarge,

    /// <summary>Every pixel fully transparent: nothing would show.</summary>
    Empty,
}

/// <summary>A picture that cannot be a sticker, with what is wrong (<see cref="StickerPictures.Save"/>).</summary>
public sealed class StickerPictureException : Exception
{
    /// <summary>Creates the exception.</summary>
    public StickerPictureException(StickerProblem problem, string message, int width = 0, int height = 0, Exception? inner = null)
        : base(message, inner)
    {
        Problem = problem;
        Width = width;
        Height = height;
    }

    /// <summary>What is wrong.</summary>
    public StickerProblem Problem { get; }

    /// <summary>The picture's width, when known.</summary>
    public int Width { get; }

    /// <summary>The picture's height, when known.</summary>
    public int Height { get; }
}

/// <summary>What <see cref="StickerPictures.Save"/> wrote.</summary>
/// <param name="Width">Width of the saved sticker.</param>
/// <param name="Height">Height of the saved sticker.</param>
/// <param name="SourceWidth">Width of the user's picture.</param>
/// <param name="SourceHeight">Height of the user's picture.</param>
/// <param name="Format">The picture's format as read (PNG, JPEG, WEBP …).</param>
public sealed record StickerImport(int Width, int Height, int SourceWidth, int SourceHeight, string Format);

/// <summary>
/// The user's picture as the project keeps a sticker (owner, 2026-10-19: "the PNG stickers sometimes do not work when we
/// put them"): any picture ImageSharp reads (PNG of every colour type and bit depth, palette, grey, interlaced, with a colour
/// profile or gamma; JPEG baseline, progressive or CMYK; WebP, BMP, GIF, TGA, TIFF), turned upright by its EXIF
/// orientation, at most <see cref="MaxSide"/> pixels on its long side, saved as an 8-bit RGBA PNG. A sticker never ships
/// as a texture of its own: Apply decals bakes it into the clone's colour texture (the Rager's <c>T_Rager_Outer_D</c>,
/// 2048 x 2048 DXT1, 12 mips) and colour mask, so its own size, format and mips need not match anything of the game. What
/// cannot work says what is wrong (<see cref="StickerPictureException"/>).
/// </summary>
public static class StickerPictures
{
    /// <summary>Longest side of a saved sticker: the size of the textures it is baked into (2048 on the game's cars).</summary>
    public const int MaxSide = 2048;

    /// <summary>
    /// Longest side of a picture the studio reads at all: 8192 (256 MB of pixels while it is made smaller; tested, about
    /// 0.6 s). A bigger one would take up to 1 GB and freeze the page while it is read; the message says to make it smaller.
    /// </summary>
    public const int MaxInputSide = 8192;

    /// <summary>Reads <paramref name="source"/> and saves it as the sticker <paramref name="destination"/> (an 8-bit RGBA PNG).</summary>
    /// <exception cref="StickerPictureException">The file is not a picture, is damaged, too large or fully transparent.</exception>
    /// <exception cref="IOException">The file cannot be read or the sticker written.</exception>
    public static StickerImport Save(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var name = Path.GetFileName(source);
        ImageInfo info;
        try
        {
            info = Image.Identify(source);
        }
        catch (UnknownImageFormatException ex)
        {
            throw new StickerPictureException(StickerProblem.NotAPicture, $"{name} is not a picture the studio can read.", inner: ex);
        }
        catch (InvalidImageContentException ex)
        {
            throw new StickerPictureException(StickerProblem.Damaged, $"{name} is damaged: {ex.Message}", inner: ex);
        }

        if (info.Width > MaxInputSide || info.Height > MaxInputSide || info.Width < 1 || info.Height < 1)
        {
            throw new StickerPictureException(StickerProblem.TooLarge, $"{name} is {info.Width} x {info.Height} pixels.", info.Width, info.Height);
        }

        Image<Rgba32> image;
        try
        {
            // Decoded whole (DecoderOptions.TargetSize would also make a small picture big) and made smaller below when it is big.
            image = Image.Load<Rgba32>(source);
        }
        catch (UnknownImageFormatException ex)
        {
            throw new StickerPictureException(StickerProblem.NotAPicture, $"{name} is not a picture the studio can read.", inner: ex);
        }
        catch (Exception ex) when (ex is InvalidImageContentException or ImageFormatException or NotSupportedException)
        {
            throw new StickerPictureException(StickerProblem.Damaged, $"{name} could not be read: {ex.Message}", info.Width, info.Height, ex);
        }

        using (image)
        {
            image.Mutate(x => x.AutoOrient());
            var longSide = Math.Max(image.Width, image.Height);
            if (longSide > MaxSide)
            {
                image.Mutate(x => x.Resize(Math.Max(1, image.Width * MaxSide / longSide), Math.Max(1, image.Height * MaxSide / longSide)));
            }

            if (!AnyVisible(image))
            {
                throw new StickerPictureException(StickerProblem.Empty, $"Every pixel of {name} is fully transparent.", info.Width, info.Height);
            }

            // Saved without the picture's colour profile, gamma or EXIF: the pixels as they look, nothing that could turn them again.
            image.Metadata.ExifProfile = null;
            image.Metadata.IccProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.GetPngMetadata().Gamma = 0;
            var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            image.SaveAsPng(destination, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 });
            return new StickerImport(image.Width, image.Height, info.Width, info.Height, info.Metadata.DecodedImageFormat?.Name ?? "?");
        }
    }

    private static bool AnyVisible(Image<Rgba32> image)
    {
        var visible = false;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height && !visible; y++)
            {
                foreach (var p in rows.GetRowSpan(y))
                {
                    if (p.A > 0)
                    {
                        visible = true;
                        break;
                    }
                }
            }
        });
        return visible;
    }
}
