using AssetRipper.TextureDecoder.Bc;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;

namespace ScumStudio.Assets.Textures;

/// <summary>
/// Decodes cooked <see cref="UTexture2D"/> mips to RGBA8.
/// </summary>
/// <remarks>
/// BC1 (DXT1), BC2 (DXT3), BC3 (DXT5), BC4, BC5 and BC7 are decoded with the fully managed AssetRipper decoders (no native
/// libraries, identical on Windows and Linux); <c>PF_B8G8R8A8</c>, <c>PF_R8G8B8A8</c>, <c>PF_G8</c> and <c>PF_A8</c> are converted
/// directly. Other formats fall back to CUE4Parse-Conversion's decoder. Mips smaller than a 4x4 block are stored padded to a
/// whole block; they are decoded at the padded size and cropped.
/// </remarks>
public static class TextureDecoder
{
    /// <summary>Pixel formats decoded without CUE4Parse-Conversion's fallback.</summary>
    public static IReadOnlyList<EPixelFormat> NativeFormats { get; } =
    [
        EPixelFormat.PF_DXT1, EPixelFormat.PF_DXT3, EPixelFormat.PF_DXT5, EPixelFormat.PF_BC4, EPixelFormat.PF_BC5, EPixelFormat.PF_BC7,
        EPixelFormat.PF_B8G8R8A8, EPixelFormat.PF_R8G8B8A8, EPixelFormat.PF_G8, EPixelFormat.PF_A8,
    ];

    /// <summary>Describes a texture (format and mip chain) without decoding.</summary>
    public static TextureInfo Describe(UTexture2D texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var mips = texture.PlatformData.Mips
            .Select((m, i) => new TextureMipInfo(i, m.SizeX, m.SizeY, m.BulkData?.Data is { Length: > 0 }))
            .ToList();
        var first = mips.FirstOrDefault();
        return new TextureInfo(texture.Name, first?.Width ?? texture.PlatformData.SizeX, first?.Height ?? texture.PlatformData.SizeY,
            texture.Format.ToString(), texture.CompressionSettings.ToString(), texture.SRGB, mips);
    }

    /// <summary>
    /// Decodes the largest available mip whose width and height are both at most <paramref name="maxSize"/>
    /// (0 or negative = the largest available mip). When every mip is larger, the smallest available one is used.
    /// </summary>
    /// <exception cref="InvalidDataException">The texture has no mip with data.</exception>
    /// <exception cref="NotSupportedException">The pixel format cannot be decoded.</exception>
    public static TextureImage Decode(UTexture2D texture, int maxSize = 0)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var mips = texture.PlatformData.Mips;
        var index = SelectMip(mips.Select(m => (m.SizeX, m.SizeY, m.BulkData?.Data is { Length: > 0 })).ToList(), maxSize);
        if (index < 0)
        {
            throw new InvalidDataException($"{texture.Name} has no mip with data (missing .ubulk?).");
        }

        var mip = mips[index];
        var rgba = DecodeMip(texture, mip);
        var first = mips.Length > 0 ? mips[0] : mip;
        return new TextureImage(texture.Name, mip.SizeX, mip.SizeY, rgba, texture.Format.ToString(), index,
            first.SizeX, first.SizeY, texture.SRGB, texture.IsNormalMap);
    }

    /// <summary>
    /// For the GPU: a BC1/2/3/5/7 texture keeps its cooked blocks (<see cref="TextureImage.CompressedMips"/>, from the mip
    /// <see cref="Decode"/> would pick down to the smallest with data, <see cref="TextureImage.Rgba"/> empty): no CPU decode
    /// and a quarter to an eighth of the memory. Other formats are decoded as <see cref="Decode"/> does.
    /// </summary>
    public static TextureImage DecodeForGpu(UTexture2D texture, int maxSize = 0)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.Format is not (EPixelFormat.PF_DXT1 or EPixelFormat.PF_DXT3 or EPixelFormat.PF_DXT5 or EPixelFormat.PF_BC5 or EPixelFormat.PF_BC7))
        {
            return Decode(texture, maxSize);
        }

        var mips = texture.PlatformData.Mips;
        var index = SelectMip(mips.Select(m => (m.SizeX, m.SizeY, m.BulkData?.Data is { Length: > 0 })).ToList(), maxSize);
        if (index < 0)
        {
            throw new InvalidDataException($"{texture.Name} has no mip with data (missing .ubulk?).");
        }

        var blocks = new List<byte[]>();
        for (var i = index; i < mips.Length && mips[i].BulkData?.Data is { Length: > 0 } data; i++)
        {
            blocks.Add(data);
        }

        var first = mips[0];
        return new TextureImage(texture.Name, mips[index].SizeX, mips[index].SizeY, [], texture.Format.ToString(), index,
            first.SizeX, first.SizeY, texture.SRGB, texture.IsNormalMap) { CompressedMips = blocks };
    }

    /// <summary>
    /// Picks the mip to decode: the largest mip with data whose sizes are both &lt;= <paramref name="maxSize"/>
    /// (any size when <paramref name="maxSize"/> &lt;= 0); falls back to the smallest mip with data; -1 when none has data.
    /// Mips are ordered largest first.
    /// </summary>
    public static int SelectMip(IReadOnlyList<(int Width, int Height, bool HasData)> mips, int maxSize)
    {
        var smallest = -1;
        for (var i = 0; i < mips.Count; i++)
        {
            var (w, h, has) = mips[i];
            if (!has)
            {
                continue;
            }

            if (maxSize <= 0 || (w <= maxSize && h <= maxSize))
            {
                return i;
            }

            smallest = i;
        }

        return smallest;
    }

    /// <summary>Decodes raw block/pixel data of one mip into RGBA8.</summary>
    /// <param name="data">Cooked mip payload.</param>
    /// <param name="format">Pixel format.</param>
    /// <param name="width">Mip width in pixels.</param>
    /// <param name="height">Mip height in pixels.</param>
    /// <param name="isNormalMap">Reconstruct blue (Z) for two-channel (BC5) normal maps.</param>
    /// <exception cref="NotSupportedException">The format is not one of <see cref="NativeFormats"/>.</exception>
    public static byte[] DecodeRaw(ReadOnlySpan<byte> data, EPixelFormat format, int width, int height, bool isNormalMap = false)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Texture size must be positive.");
        }

        var pw = (width + 3) & ~3;
        var ph = (height + 3) & ~3;
        byte[] bgra;
        switch (format)
        {
            case EPixelFormat.PF_DXT1:
                RequireBlocks(data, pw, ph, 8, format);
                Bc1.Decompress(data, pw, ph, out bgra);
                break;
            case EPixelFormat.PF_DXT3:
                RequireBlocks(data, pw, ph, 16, format);
                Bc2.Decompress(data, pw, ph, out bgra);
                break;
            case EPixelFormat.PF_DXT5:
                RequireBlocks(data, pw, ph, 16, format);
                Bc3.Decompress(data, pw, ph, out bgra);
                break;
            case EPixelFormat.PF_BC4:
                RequireBlocks(data, pw, ph, 8, format);
                Bc4.Decompress(data, pw, ph, out bgra);
                break;
            case EPixelFormat.PF_BC5:
                RequireBlocks(data, pw, ph, 16, format);
                Bc5.Decompress(data, pw, ph, out bgra);
                break;
            case EPixelFormat.PF_BC7:
                RequireBlocks(data, pw, ph, 16, format);
                Bc7.Decompress(data, pw, ph, out bgra);
                break;
            case EPixelFormat.PF_B8G8R8A8:
                return SwapRedBlue(Crop(data, width, height, 4, width * 4));
            case EPixelFormat.PF_R8G8B8A8:
                return Crop(data, width, height, 4, width * 4);
            case EPixelFormat.PF_G8:
                return ExpandSingle(Crop(data, width, height, 1, width), grey: true);
            case EPixelFormat.PF_A8:
                return ExpandSingle(Crop(data, width, height, 1, width), grey: false);
            default:
                throw new NotSupportedException($"Pixel format {format} is not decoded natively.");
        }

        var rgba = SwapRedBlue(bgra);
        if (pw != width || ph != height)
        {
            rgba = Crop(rgba, width, height, 4, pw * 4);
        }

        if (format == EPixelFormat.PF_BC5)
        {
            // BC5 stores X in red and Y in green; the decoder leaves blue at 0.
            for (var i = 0; i < rgba.Length; i += 4)
            {
                rgba[i + 2] = isNormalMap ? ReconstructZ(rgba[i], rgba[i + 1]) : (byte)0;
                rgba[i + 3] = 255;
            }
        }
        else if (format == EPixelFormat.PF_BC4)
        {
            // Single channel: replicate red to grey for display.
            for (var i = 0; i < rgba.Length; i += 4)
            {
                rgba[i + 1] = rgba[i];
                rgba[i + 2] = rgba[i];
                rgba[i + 3] = 255;
            }
        }

        return rgba;
    }

    private static byte[] DecodeMip(UTexture2D texture, FTexture2DMipMap mip)
    {
        var data = mip.BulkData.Data ?? throw new InvalidDataException($"{texture.Name} mip {mip.SizeX}x{mip.SizeY} has no data.");
        if (NativeFormats.Contains(texture.Format))
        {
            return DecodeRaw(data, texture.Format, mip.SizeX, mip.SizeY, texture.IsNormalMap);
        }

        try
        {
            CUE4Parse_Conversion.Textures.TextureDecoder.DecodeTexture(mip, mip.SizeX, mip.SizeY, Math.Max(mip.SizeZ, 1), texture.Format,
                texture.IsNormalMap, ETexturePlatform.DesktopMobile, out var pixels, out var colorType);
            return ConvertSkia(pixels, colorType, mip.SizeX, mip.SizeY);
        }
        catch (Exception ex) when (ex is NotImplementedException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new NotSupportedException($"Pixel format {texture.Format} of {texture.Name} cannot be decoded: {ex.Message}", ex);
        }
    }

    private static byte[] ConvertSkia(byte[] pixels, SKColorType type, int width, int height)
    {
        var n = width * height;
        switch (type)
        {
            case SKColorType.Rgba8888:
                return pixels.Length >= n * 4 ? pixels[..(n * 4)] : throw new InvalidDataException("Decoded buffer too small.");
            case SKColorType.Bgra8888:
                return SwapRedBlue(pixels.Length >= n * 4 ? pixels[..(n * 4)] : throw new InvalidDataException("Decoded buffer too small."));
            case SKColorType.Rgb888x:
            {
                var result = pixels.Length >= n * 4 ? pixels[..(n * 4)] : throw new InvalidDataException("Decoded buffer too small.");
                for (var i = 3; i < result.Length; i += 4)
                {
                    result[i] = 255;
                }

                return result;
            }

            case SKColorType.Gray8:
                return ExpandSingle(pixels[..n], grey: true);
            default:
                throw new NotSupportedException($"Decoded colour type {type} is not supported.");
        }
    }

    private static void RequireBlocks(ReadOnlySpan<byte> data, int paddedWidth, int paddedHeight, int blockBytes, EPixelFormat format)
    {
        var needed = (long)(paddedWidth / 4) * (paddedHeight / 4) * blockBytes;
        if (data.Length < needed)
        {
            throw new InvalidDataException($"{format} mip {paddedWidth}x{paddedHeight} needs {needed} bytes but has {data.Length}.");
        }
    }

    private static byte[] Crop(ReadOnlySpan<byte> src, int width, int height, int bytesPerPixel, int srcStride)
    {
        var rowBytes = width * bytesPerPixel;
        if (src.Length < (long)srcStride * (height - 1) + rowBytes)
        {
            throw new InvalidDataException($"Pixel buffer too small for {width}x{height}.");
        }

        var dst = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            src.Slice(y * srcStride, rowBytes).CopyTo(dst.AsSpan(y * rowBytes));
        }

        return dst;
    }

    /// <summary>Swaps the first and third byte of every 4-byte pixel in place (BGRA to RGBA and back).</summary>
    private static byte[] SwapRedBlue(byte[] data)
    {
        for (var i = 0; i + 3 < data.Length; i += 4)
        {
            (data[i], data[i + 2]) = (data[i + 2], data[i]);
        }

        return data;
    }

    private static byte[] ExpandSingle(byte[] values, bool grey)
    {
        var dst = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (grey)
            {
                dst[i * 4] = v;
                dst[i * 4 + 1] = v;
                dst[i * 4 + 2] = v;
                dst[i * 4 + 3] = 255;
            }
            else
            {
                dst[i * 4] = 255;
                dst[i * 4 + 1] = 255;
                dst[i * 4 + 2] = 255;
                dst[i * 4 + 3] = v;
            }
        }

        return dst;
    }

    /// <summary>Reconstructs the Z byte of a unit normal from its X and Y bytes (unsigned, 0..255 = -1..1).</summary>
    internal static byte ReconstructZ(byte x, byte y)
    {
        var nx = x / 127.5f - 1f;
        var ny = y / 127.5f - 1f;
        var nz = MathF.Sqrt(MathF.Max(0f, 1f - nx * nx - ny * ny));
        return (byte)Math.Clamp((int)MathF.Round((nz + 1f) * 127.5f), 0, 255);
    }
}
