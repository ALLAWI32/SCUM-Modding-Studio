using System.Buffers.Binary;

namespace ScumStudio.Assets.Textures;

/// <summary>
/// Encodes RGBA8 pixels into the block formats SCUM cooks (pure C#, deterministic, no native code): BC1 (<c>PF_DXT1</c>,
/// 4-colour mode), BC3 (<c>PF_DXT5</c>), BC4, BC5 (red and green: normal maps), BC7 (mode 6: one subset, RGBA endpoints with
/// p-bits, 4-bit indices) and raw <c>PF_B8G8R8A8</c>. Endpoints come from the principal axis of each 4x4 block and are refined
/// by least squares; sizes that are not a multiple of 4 are padded with the edge pixels. Block rows run in parallel.
/// </summary>
public static class BlockEncoder
{
    private static readonly int[] Bc7Weights = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];
    private static readonly (byte Hi, byte Lo)[] Match5 = SingleColourTable(5);
    private static readonly (byte Hi, byte Lo)[] Match6 = SingleColourTable(6);

    /// <summary>The pixel formats <see cref="Encode"/> writes.</summary>
    public static IReadOnlyList<string> Formats { get; } = ["PF_DXT1", "PF_DXT5", "PF_BC4", "PF_BC5", "PF_BC7", "PF_B8G8R8A8"];

    /// <summary>True when <paramref name="pixelFormat"/> is one of <see cref="Formats"/>.</summary>
    public static bool IsSupported(string pixelFormat) => Formats.Contains(pixelFormat);

    /// <summary>Bytes of one encoded mip (blocks of the size padded to 4, or 4 bytes per pixel for <c>PF_B8G8R8A8</c>).</summary>
    public static int EncodedSize(string pixelFormat, int width, int height) => pixelFormat switch
    {
        "PF_B8G8R8A8" => width * height * 4,
        "PF_DXT1" or "PF_BC4" => ((width + 3) / 4) * ((height + 3) / 4) * 8,
        "PF_DXT5" or "PF_BC5" or "PF_BC7" => ((width + 3) / 4) * ((height + 3) / 4) * 16,
        _ => throw new NotSupportedException($"Pixel format {pixelFormat} cannot be encoded."),
    };

    /// <summary>Encodes <paramref name="rgba"/> (<paramref name="width"/> x <paramref name="height"/>, R G B A bytes) as <paramref name="pixelFormat"/>.</summary>
    /// <exception cref="NotSupportedException">The format is not one of <see cref="Formats"/>.</exception>
    public static byte[] Encode(byte[] rgba, int width, int height, string pixelFormat)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || rgba.Length < (long)width * height * 4)
        {
            throw new ArgumentException($"{width}x{height} needs {(long)width * height * 4} RGBA bytes; {rgba.Length} given.", nameof(rgba));
        }

        var output = new byte[EncodedSize(pixelFormat, width, height)];
        if (pixelFormat == "PF_B8G8R8A8")
        {
            for (var i = 0; i < output.Length; i += 4)
            {
                output[i] = rgba[i + 2];
                output[i + 1] = rgba[i + 1];
                output[i + 2] = rgba[i];
                output[i + 3] = rgba[i + 3];
            }

            return output;
        }

        var blocksX = (width + 3) / 4;
        var blockBytes = pixelFormat is "PF_DXT1" or "PF_BC4" ? 8 : 16;
        Parallel.For(0, (height + 3) / 4, by =>
        {
            Span<byte> block = stackalloc byte[64];
            for (var bx = 0; bx < blocksX; bx++)
            {
                for (var j = 0; j < 4; j++)
                {
                    var y = Math.Min(by * 4 + j, height - 1);
                    for (var i = 0; i < 4; i++)
                    {
                        var x = Math.Min(bx * 4 + i, width - 1);
                        rgba.AsSpan((y * width + x) * 4, 4).CopyTo(block.Slice(((j * 4) + i) * 4));
                    }
                }

                var dst = output.AsSpan((by * blocksX + bx) * blockBytes, blockBytes);
                switch (pixelFormat)
                {
                    case "PF_DXT1":
                        Bc1(block, dst);
                        break;
                    case "PF_DXT5":
                        Bc4(block, 3, dst[..8]);
                        Bc1(block, dst[8..]);
                        break;
                    case "PF_BC4":
                        Bc4(block, 0, dst);
                        break;
                    case "PF_BC5":
                        Bc4(block, 0, dst[..8]);
                        Bc4(block, 1, dst[8..]);
                        break;
                    default:
                        Bc7(block, dst);
                        break;
                }
            }
        });
        return output;
    }

    // ---- BC1: two RGB565 endpoints (c0 > c1: four colours), 2-bit indices

    private static void Bc1(ReadOnlySpan<byte> block, Span<byte> dst)
    {
        Span<float> px = stackalloc float[48];
        var solid = true;
        for (var i = 0; i < 16; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                px[i * 3 + c] = block[i * 4 + c];
                solid &= block[i * 4 + c] == block[c];
            }
        }

        if (solid)
        {
            Bc1Solid(block[0], block[1], block[2], dst);
            return;
        }

        Span<float> axis = stackalloc float[3];
        Span<float> mean = stackalloc float[3];
        PrincipalAxis(px, 3, mean, axis);
        float lo = float.MaxValue, hi = float.MinValue;
        for (var i = 0; i < 16; i++)
        {
            var t = Dot(px.Slice(i * 3, 3), mean, axis);
            lo = Math.Min(lo, t);
            hi = Math.Max(hi, t);
        }

        Span<float> e0 = stackalloc float[3];
        Span<float> e1 = stackalloc float[3];
        for (var c = 0; c < 3; c++)
        {
            e0[c] = mean[c] + axis[c] * hi;
            e1[c] = mean[c] + axis[c] * lo;
        }

        Span<byte> idx = stackalloc byte[16];
        Span<byte> bestIdx = stackalloc byte[16];
        var (c0, c1) = (Pack565(e0), Pack565(e1));
        var best = Bc1Fit(px, c0, c1, bestIdx);
        var (b0, b1) = (c0, c1);
        Span<float> weights = stackalloc float[16];
        for (var iteration = 0; iteration < 3; iteration++)
        {
            for (var i = 0; i < 16; i++)
            {
                weights[i] = bestIdx[i] switch { 0 => 0f, 1 => 1f, 2 => 1f / 3f, _ => 2f / 3f };
            }

            if (!LeastSquares(px, 3, weights, e0, e1))
            {
                break;
            }

            (c0, c1) = (Pack565(e0), Pack565(e1));
            var error = Bc1Fit(px, c0, c1, idx);
            if (error >= best)
            {
                break;
            }

            best = error;
            (b0, b1) = (c0, c1);
            idx.CopyTo(bestIdx);
        }

        WriteBc1(b0, b1, bestIdx, dst);
    }

    /// <summary>Squared error of the block on the palette of (c0, c1) and the nearest index of every pixel.</summary>
    private static float Bc1Fit(ReadOnlySpan<float> px, ushort c0, ushort c1, Span<byte> idx)
    {
        Span<float> pal = stackalloc float[12];
        Unpack565(c0, pal[..3]);
        Unpack565(c1, pal.Slice(3, 3));
        for (var c = 0; c < 3; c++)
        {
            pal[6 + c] = (2 * pal[c] + pal[3 + c]) / 3f;
            pal[9 + c] = (pal[c] + 2 * pal[3 + c]) / 3f;
        }

        var total = 0f;
        for (var i = 0; i < 16; i++)
        {
            var bestError = float.MaxValue;
            for (var k = 0; k < 4; k++)
            {
                var e = Sq(px[i * 3] - pal[k * 3]) + Sq(px[i * 3 + 1] - pal[k * 3 + 1]) + Sq(px[i * 3 + 2] - pal[k * 3 + 2]);
                if (e < bestError)
                {
                    bestError = e;
                    idx[i] = (byte)k;
                }
            }

            total += bestError;
        }

        return total;
    }

    /// <summary>A solid colour: per channel the endpoint pair whose 2/3 mix is nearest (stb_dxt's single-colour match).</summary>
    private static void Bc1Solid(byte r, byte g, byte b, Span<byte> dst)
    {
        var c0 = (ushort)((Match5[r].Hi << 11) | (Match6[g].Hi << 5) | Match5[b].Hi);
        var c1 = (ushort)((Match5[r].Lo << 11) | (Match6[g].Lo << 5) | Match5[b].Lo);
        Span<byte> idx = stackalloc byte[16];
        idx.Fill(2);
        WriteBc1(c0, c1, idx, dst);
    }

    /// <summary>Writes a BC1 block in four-colour mode: c0 &gt; c1 (endpoints swapped, or nudged apart when equal).</summary>
    private static void WriteBc1(ushort c0, ushort c1, Span<byte> idx, Span<byte> dst)
    {
        if (c0 < c1)
        {
            (c0, c1) = (c1, c0);
            for (var i = 0; i < 16; i++)
            {
                idx[i] = idx[i] switch { 0 => 1, 1 => 0, 2 => 3, _ => 2 };
            }
        }
        else if (c0 == c1)
        {
            // One colour: index 0 (c0) everywhere, c1 one step below so the block stays in four-colour mode.
            byte all = 0;
            if (c1 > 0)
            {
                c1--;
            }
            else
            {
                c0 = 1;
                all = 1;
            }

            idx.Fill(all);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(dst, c0);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[2..], c1);
        uint bits = 0;
        for (var i = 0; i < 16; i++)
        {
            bits |= (uint)idx[i] << (2 * i);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], bits);
    }

    private static (byte Hi, byte Lo)[] SingleColourTable(int bits)
    {
        var table = new (byte, byte)[256];
        var levels = 1 << bits;
        for (var v = 0; v < 256; v++)
        {
            var best = float.MaxValue;
            for (var hi = 0; hi < levels; hi++)
            {
                for (var lo = 0; lo < levels; lo++)
                {
                    var e = Math.Abs((2f * Expand(hi, bits) + Expand(lo, bits)) / 3f - v);
                    if (e < best)
                    {
                        best = e;
                        table[v] = ((byte)hi, (byte)lo);
                    }
                }
            }
        }

        return table;
    }

    private static int Expand(int value, int bits) => bits == 5 ? (value << 3) | (value >> 2) : (value << 2) | (value >> 4);

    private static ushort Pack565(ReadOnlySpan<float> c)
    {
        var r = (int)Math.Clamp(MathF.Round(c[0] * 31f / 255f), 0, 31);
        var g = (int)Math.Clamp(MathF.Round(c[1] * 63f / 255f), 0, 63);
        var b = (int)Math.Clamp(MathF.Round(c[2] * 31f / 255f), 0, 31);
        return (ushort)((r << 11) | (g << 5) | b);
    }

    private static void Unpack565(ushort c, Span<float> rgb)
    {
        rgb[0] = Expand(c >> 11, 5);
        rgb[1] = Expand((c >> 5) & 63, 6);
        rgb[2] = Expand(c & 31, 5);
    }

    // ---- BC4: one channel, two 8-bit endpoints (a0 > a1: eight values), 3-bit indices

    private static void Bc4(ReadOnlySpan<byte> block, int channel, Span<byte> dst)
    {
        Span<float> v = stackalloc float[16];
        float lo = 255, hi = 0;
        for (var i = 0; i < 16; i++)
        {
            v[i] = block[i * 4 + channel];
            lo = Math.Min(lo, v[i]);
            hi = Math.Max(hi, v[i]);
        }

        dst.Clear();
        if (hi == lo)
        {
            dst[0] = dst[1] = (byte)hi; // a0 <= a1: index 0 is a0
            return;
        }

        Span<byte> idx = stackalloc byte[16];
        Span<byte> bestIdx = stackalloc byte[16];
        var (a0, a1) = ((int)hi, (int)lo);
        var best = Bc4Fit(v, a0, a1, bestIdx);
        // Inset by the error the extremes can afford: a gradient fits its eight steps better.
        for (var inset = 1; inset <= 4 && hi - lo > 2 * inset + 1; inset++)
        {
            var error = Bc4Fit(v, (int)hi - inset, (int)lo + inset, idx);
            if (error < best)
            {
                best = error;
                (a0, a1) = ((int)hi - inset, (int)lo + inset);
                idx.CopyTo(bestIdx);
            }
        }

        dst[0] = (byte)a0;
        dst[1] = (byte)a1;
        ulong bits = 0;
        for (var i = 0; i < 16; i++)
        {
            bits |= (ulong)bestIdx[i] << (3 * i);
        }

        for (var k = 0; k < 6; k++)
        {
            dst[2 + k] = (byte)(bits >> (8 * k));
        }
    }

    private static float Bc4Fit(ReadOnlySpan<float> v, int a0, int a1, Span<byte> idx)
    {
        Span<float> pal = stackalloc float[8];
        pal[0] = a0;
        pal[1] = a1;
        for (var k = 2; k < 8; k++)
        {
            pal[k] = ((8 - k) * a0 + (k - 1) * a1) / 7;
        }

        var total = 0f;
        for (var i = 0; i < 16; i++)
        {
            var bestError = float.MaxValue;
            for (var k = 0; k < 8; k++)
            {
                var e = Sq(v[i] - pal[k]);
                if (e < bestError)
                {
                    bestError = e;
                    idx[i] = (byte)k;
                }
            }

            total += bestError;
        }

        return total;
    }

    // ---- BC7 mode 6: one subset, RGBA 7.7.7.7 endpoints + one p-bit each, 4-bit indices

    private static void Bc7(ReadOnlySpan<byte> block, Span<byte> dst)
    {
        Span<float> px = stackalloc float[64];
        for (var i = 0; i < 64; i++)
        {
            px[i] = block[i];
        }

        Span<float> axis = stackalloc float[4];
        Span<float> mean = stackalloc float[4];
        Span<float> e0 = stackalloc float[4];
        Span<float> e1 = stackalloc float[4];
        PrincipalAxis(px, 4, mean, axis);
        float lo = float.MaxValue, hi = float.MinValue;
        for (var i = 0; i < 16; i++)
        {
            var t = Dot(px.Slice(i * 4, 4), mean, axis);
            lo = Math.Min(lo, t);
            hi = Math.Max(hi, t);
        }

        for (var c = 0; c < 4; c++)
        {
            e0[c] = Math.Clamp(mean[c] + axis[c] * lo, 0, 255);
            e1[c] = Math.Clamp(mean[c] + axis[c] * hi, 0, 255);
        }

        Span<byte> q = stackalloc byte[8];
        Span<byte> best = stackalloc byte[8];
        Span<byte> idx = stackalloc byte[16];
        Span<byte> bestIdx = stackalloc byte[16];
        Bc7Quantize(e0, q[..4]);
        Bc7Quantize(e1, q[4..]);
        var bestError = Bc7Fit(px, q, bestIdx);
        q.CopyTo(best);
        Span<float> weights = stackalloc float[16];
        for (var iteration = 0; iteration < 3 && bestError > 0; iteration++)
        {
            for (var i = 0; i < 16; i++)
            {
                weights[i] = Bc7Weights[bestIdx[i]] / 64f;
            }

            if (!LeastSquares(px, 4, weights, e0, e1))
            {
                break;
            }

            for (var c = 0; c < 4; c++)
            {
                e0[c] = Math.Clamp(e0[c], 0, 255);
                e1[c] = Math.Clamp(e1[c], 0, 255);
            }

            Bc7Quantize(e0, q[..4]);
            Bc7Quantize(e1, q[4..]);
            var error = Bc7Fit(px, q, idx);
            if (error >= bestError)
            {
                break;
            }

            bestError = error;
            q.CopyTo(best);
            idx.CopyTo(bestIdx);
        }

        WriteBc7Mode6(best, bestIdx, dst);
    }

    /// <summary>
    /// An endpoint as four 8-bit values sharing one p-bit (the lowest bit): the p-bit with the smaller error, except that a
    /// fully transparent or fully opaque alpha decides it, so alpha 0 and 255 survive.
    /// </summary>
    private static void Bc7Quantize(ReadOnlySpan<float> e, Span<byte> dst)
    {
        var forced = e[3] >= 254.5f ? 1 : e[3] <= 0.5f ? 0 : -1;
        var bestError = float.MaxValue;
        Span<byte> v = stackalloc byte[4];
        for (var p = 0; p < 2; p++)
        {
            if (forced >= 0 && p != forced)
            {
                continue;
            }

            var error = 0f;
            for (var c = 0; c < 4; c++)
            {
                var q7 = (int)Math.Clamp(MathF.Round((e[c] - p) / 2f), 0, 127);
                v[c] = (byte)((q7 << 1) | p);
                error += Sq(v[c] - e[c]);
            }

            if (error < bestError)
            {
                bestError = error;
                v.CopyTo(dst);
            }
        }
    }

    private static float Bc7Fit(ReadOnlySpan<float> px, ReadOnlySpan<byte> q, Span<byte> idx)
    {
        Span<float> pal = stackalloc float[64];
        for (var k = 0; k < 16; k++)
        {
            for (var c = 0; c < 4; c++)
            {
                pal[k * 4 + c] = (((64 - Bc7Weights[k]) * q[c]) + (Bc7Weights[k] * q[4 + c]) + 32) >> 6;
            }
        }

        var total = 0f;
        for (var i = 0; i < 16; i++)
        {
            var bestError = float.MaxValue;
            for (var k = 0; k < 16; k++)
            {
                var e = Sq(px[i * 4] - pal[k * 4]) + Sq(px[i * 4 + 1] - pal[k * 4 + 1]) + Sq(px[i * 4 + 2] - pal[k * 4 + 2]) + Sq(px[i * 4 + 3] - pal[k * 4 + 3]);
                if (e < bestError)
                {
                    bestError = e;
                    idx[i] = (byte)k;
                }
            }

            total += bestError;
        }

        return total;
    }

    private static void WriteBc7Mode6(Span<byte> q, Span<byte> idx, Span<byte> dst)
    {
        if (idx[0] >= 8)
        {
            // The anchor (pixel 0) stores 3 bits: its index must be below 8, so the endpoints are swapped.
            for (var c = 0; c < 4; c++)
            {
                (q[c], q[4 + c]) = (q[4 + c], q[c]);
            }

            for (var i = 0; i < 16; i++)
            {
                idx[i] = (byte)(15 - idx[i]);
            }
        }

        UInt128 bits = 0;
        var at = 0;
        void Put(int value, int count)
        {
            bits |= (UInt128)(uint)value << at;
            at += count;
        }

        Put(1 << 6, 7);
        for (var c = 0; c < 4; c++)
        {
            Put(q[c] >> 1, 7);
            Put(q[4 + c] >> 1, 7);
        }

        Put(q[0] & 1, 1);
        Put(q[4] & 1, 1);
        Put(idx[0], 3);
        for (var i = 1; i < 16; i++)
        {
            Put(idx[i], 4);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(dst, (ulong)bits);
        BinaryPrimitives.WriteUInt64LittleEndian(dst[8..], (ulong)(bits >> 64));
    }

    // ---- shared: principal axis and least-squares endpoints

    /// <summary>Mean and unit principal axis of 16 points of <paramref name="dims"/> channels (power iteration).</summary>
    private static void PrincipalAxis(ReadOnlySpan<float> px, int dims, Span<float> mean, Span<float> axis)
    {
        mean.Clear();
        for (var i = 0; i < 16; i++)
        {
            for (var c = 0; c < dims; c++)
            {
                mean[c] += px[i * dims + c] / 16f;
            }
        }

        Span<float> cov = stackalloc float[16];
        cov.Clear();
        for (var i = 0; i < 16; i++)
        {
            for (var a = 0; a < dims; a++)
            {
                for (var b = 0; b < dims; b++)
                {
                    cov[a * 4 + b] += (px[i * dims + a] - mean[a]) * (px[i * dims + b] - mean[b]);
                }
            }
        }

        for (var c = 0; c < dims; c++)
        {
            axis[c] = 1f;
        }

        Span<float> next = stackalloc float[4];
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var norm = 0f;
            for (var a = 0; a < dims; a++)
            {
                next[a] = 0;
                for (var b = 0; b < dims; b++)
                {
                    next[a] += cov[a * 4 + b] * axis[b];
                }

                norm += next[a] * next[a];
            }

            if (norm < 1e-12f)
            {
                break;
            }

            norm = MathF.Sqrt(norm);
            for (var a = 0; a < dims; a++)
            {
                axis[a] = next[a] / norm;
            }
        }

        var length = 0f;
        for (var c = 0; c < dims; c++)
        {
            length += axis[c] * axis[c];
        }

        length = MathF.Sqrt(length);
        for (var c = 0; c < dims; c++)
        {
            axis[c] = length > 0 ? axis[c] / length : 0;
        }
    }

    /// <summary>
    /// The endpoints that best fit the pixels for fixed interpolation weights (weight 0 = <paramref name="e0"/>, 1 =
    /// <paramref name="e1"/>); false when the weights cannot separate them.
    /// </summary>
    private static bool LeastSquares(ReadOnlySpan<float> px, int dims, ReadOnlySpan<float> weights, Span<float> e0, Span<float> e1)
    {
        float aa = 0, ab = 0, bb = 0;
        Span<float> ax = stackalloc float[4];
        Span<float> bx = stackalloc float[4];
        ax.Clear();
        bx.Clear();
        for (var i = 0; i < 16; i++)
        {
            var t = weights[i];
            var s = 1 - t;
            aa += s * s;
            ab += s * t;
            bb += t * t;
            for (var c = 0; c < dims; c++)
            {
                ax[c] += s * px[i * dims + c];
                bx[c] += t * px[i * dims + c];
            }
        }

        var det = aa * bb - ab * ab;
        if (MathF.Abs(det) < 1e-6f)
        {
            return false;
        }

        for (var c = 0; c < dims; c++)
        {
            e0[c] = Math.Clamp((ax[c] * bb - bx[c] * ab) / det, 0, 255);
            e1[c] = Math.Clamp((bx[c] * aa - ax[c] * ab) / det, 0, 255);
        }

        return true;
    }

    private static float Dot(ReadOnlySpan<float> p, ReadOnlySpan<float> mean, ReadOnlySpan<float> axis)
    {
        var t = 0f;
        for (var c = 0; c < axis.Length; c++)
        {
            t += (p[c] - mean[c]) * axis[c];
        }

        return t;
    }

    private static float Sq(float v) => v * v;
}
