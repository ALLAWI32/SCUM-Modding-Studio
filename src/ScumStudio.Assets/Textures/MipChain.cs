namespace ScumStudio.Assets.Textures;

/// <summary>One mip of a chain: its RGBA pixels at the size it is stored (padded to the block size when smaller).</summary>
/// <param name="Width">Stored width.</param>
/// <param name="Height">Stored height.</param>
/// <param name="Rgba">Pixels, <c>Width * Height * 4</c> bytes.</param>
public sealed record MipImage(int Width, int Height, byte[] Rgba);

/// <summary>
/// Builds a texture's mip chain as the cook does (port of <c>mip_images</c> in the owner's <c>build_textures.py</c>): each mip
/// is a 2x2 box filter of the one above, averaged in linear light for sRGB colour, renormalised for normal maps, optionally
/// weighted by a mask (white texels count <see cref="MaskWeight"/> + 1 times, so a painted body keeps its colour in the
/// small mips instead of fading into the atlas around it); mips smaller than the block are stored padded by repetition.
/// </summary>
public static class MipChain
{
    /// <summary>Extra weight of a white mask texel (the owner's <c>BODY_WEIGHT</c>).</summary>
    public const float MaskWeight = 15f;

    private static readonly float[] ToLinear = Enumerable.Range(0, 256).Select(v =>
    {
        var c = v / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }).ToArray();

    /// <summary>Mips of a full chain: log2 of the larger edge, plus one.</summary>
    public static int FullCount(int width, int height) => (int)Math.Log2(Math.Max(Math.Max(width, height), 1)) + 1;

    /// <summary>The logical size of mip <paramref name="index"/>.</summary>
    public static (int Width, int Height) MipSize(int width, int height, int index) => (Math.Max(width >> index, 1), Math.Max(height >> index, 1));

    /// <summary>Builds <paramref name="count"/> mips of <paramref name="rgba"/>.</summary>
    /// <param name="rgba">Mip 0, <paramref name="width"/> x <paramref name="height"/> RGBA bytes.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="count">Mips to build (mip 0 included).</param>
    /// <param name="srgb">Average in linear light (colour textures).</param>
    /// <param name="normalMap">Average directions and renormalise them (red/green/blue = X/Y/Z).</param>
    /// <param name="blockSize">Smallest stored edge (4 for block formats, 1 for raw pixels).</param>
    /// <param name="weightMask">Optional mask, one byte per mip-0 texel (white = heavier).</param>
    public static IReadOnlyList<MipImage> Build(byte[] rgba, int width, int height, int count, bool srgb, bool normalMap = false, int blockSize = 4,
        byte[]? weightMask = null)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.Length < (long)width * height * 4)
        {
            throw new ArgumentException($"{width}x{height} needs {(long)width * height * 4} RGBA bytes; {rgba.Length} given.", nameof(rgba));
        }

        var pixels = new float[width * height * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            var v = rgba[i];
            pixels[i] = (i & 3) == 3 ? v / 255f : normalMap ? v / 127.5f - 1f : srgb ? ToLinear[v] : v / 255f;
        }

        var weights = weightMask is null ? null : weightMask.Take(width * height).Select(m => 1f + MaskWeight * m / 255f).ToArray();
        var (w, h) = (width, height);
        var mips = new List<MipImage>(count);
        for (var level = 0; level < count; level++)
        {
            if (level > 0)
            {
                (pixels, weights, w, h) = Downsample(pixels, weights, w, h);
            }

            mips.Add(Pad(ToBytes(pixels, srgb, normalMap), w, h, blockSize));
        }

        return mips;
    }

    /// <summary>Pads a mip smaller than <paramref name="blockSize"/> to it by repeating its pixels (nearest).</summary>
    public static MipImage Pad(byte[] rgba, int width, int height, int blockSize) =>
        PadTo(new MipImage(width, height, rgba), Math.Max(width, blockSize), Math.Max(height, blockSize));

    /// <summary><paramref name="image"/> at <paramref name="pw"/> x <paramref name="ph"/> by nearest pixel (a template's stored mip size).</summary>
    public static MipImage PadTo(MipImage image, int pw, int ph)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (rgba, width, height) = (image.Rgba, image.Width, image.Height);
        if (pw == width && ph == height)
        {
            return image;
        }

        var padded = new byte[pw * ph * 4];
        for (var y = 0; y < ph; y++)
        {
            for (var x = 0; x < pw; x++)
            {
                rgba.AsSpan(((y * height / ph) * width + x * width / pw) * 4, 4).CopyTo(padded.AsSpan((y * pw + x) * 4));
            }
        }

        return new MipImage(pw, ph, padded);
    }

    private static (float[] Pixels, float[]? Weights, int Width, int Height) Downsample(float[] src, float[]? weights, int w, int h)
    {
        var (nw, nh) = (Math.Max(w / 2, 1), Math.Max(h / 2, 1));
        var dst = new float[nw * nh * 4];
        var dstWeights = weights is null ? null : new float[nw * nh];
        var (fx, fy) = (w / nw, h / nh);
        for (var y = 0; y < nh; y++)
        {
            for (var x = 0; x < nw; x++)
            {
                var sum = 0f;
                var o = (y * nw + x) * 4;
                for (var dy = 0; dy < fy; dy++)
                {
                    for (var dx = 0; dx < fx; dx++)
                    {
                        var s = (y * fy + dy) * w + x * fx + dx;
                        var weight = weights?[s] ?? 1f;
                        sum += weight;
                        for (var c = 0; c < 4; c++)
                        {
                            dst[o + c] += src[s * 4 + c] * weight;
                        }
                    }
                }

                for (var c = 0; c < 4; c++)
                {
                    dst[o + c] /= sum;
                }

                if (dstWeights is not null)
                {
                    dstWeights[y * nw + x] = sum / (fx * fy);
                }
            }
        }

        return (dst, dstWeights, nw, nh);
    }

    private static byte[] ToBytes(float[] pixels, bool srgb, bool normalMap)
    {
        var rgba = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (normalMap)
            {
                var (x, y, z) = (pixels[i], pixels[i + 1], pixels[i + 2]);
                var length = MathF.Sqrt(x * x + y * y + z * z);
                (x, y, z) = length > 1e-6f ? (x / length, y / length, z / length) : (0f, 0f, 1f);
                rgba[i] = Byte((x + 1f) * 127.5f);
                rgba[i + 1] = Byte((y + 1f) * 127.5f);
                rgba[i + 2] = Byte((z + 1f) * 127.5f);
            }
            else
            {
                for (var c = 0; c < 3; c++)
                {
                    var v = pixels[i + c];
                    rgba[i + c] = Byte((srgb ? (v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f) : v) * 255f);
                }
            }

            rgba[i + 3] = Byte(pixels[i + 3] * 255f);
        }

        return rgba;
    }

    private static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
}
