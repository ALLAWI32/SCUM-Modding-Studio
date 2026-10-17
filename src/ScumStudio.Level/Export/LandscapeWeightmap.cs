using System.Buffers.Binary;
using System.Text;
using ScumStudio.Formats.Packages;

namespace ScumStudio.Level.Export;

/// <summary>
/// A cooked landscape weightmap (<c>UTexture2D</c>, <c>PF_B8G8R8A8</c>, <c>TEXTUREGROUP_Terrain_Weightmap</c>) as SCUM's island
/// tiles carry them (read 2026-10-16): after the tagged properties come the object guid flag, the two strip flags,
/// <c>bCooked</c>, then one platform data block (pixel format name, absolute skip offset, size, slices, the format string,
/// the first mip and the mip count) and per mip <c>bCooked</c>, the bulk data header (flags, element count, size on disk,
/// offset) and its size. The two largest mips live in the <c>.ubulk</c> (offset inside it, <c>BULKDATA_NoOffsetFixUp</c>);
/// the others follow their header inline in the <c>.uexp</c>.
/// <para>Texel (x, y) of a mip is 4 bytes at <c>(y * width + x) * 4</c> in B, G, R, A order; a layer's
/// <c>WeightmapTextureChannel</c> 0..3 is R, G, B, A (<see cref="ByteOfChannel"/>).</para>
/// </summary>
public static class LandscapeWeightmap
{
    private const uint BulkCompressed = 0x2 | 0x200;
    private const uint BulkInline = 0x40;
    private const uint BulkSeparateFile = 0x100;
    private const uint BulkSize64 = 0x2000;
    private const uint BulkNoOffsetFixUp = 0x10000;

    /// <summary>One mip of a weightmap and where its bytes are.</summary>
    /// <param name="Width">Texels per row.</param>
    /// <param name="Height">Rows.</param>
    /// <param name="InUBulk">True when the bytes are in the package's <c>.ubulk</c>; false when inline in the <c>.uexp</c>.</param>
    /// <param name="Offset">Offset of the first byte in the <c>.ubulk</c> or the <c>.uexp</c>.</param>
    public readonly record struct Mip(int Width, int Height, bool InUBulk, int Offset)
    {
        /// <summary>Bytes of the mip (4 per texel).</summary>
        public int Length => Width * Height * 4;
    }

    /// <summary>A weightmap texture export and its mip chain (mip 0 first, down to 1 x 1).</summary>
    public sealed record Texture(int Export, int Width, int Height, IReadOnlyList<Mip> Mips);

    /// <summary>The byte of a texel (B, G, R, A order) that holds weightmap channel <paramref name="channel"/> (0..3 = R, G, B, A).</summary>
    public static int ByteOfChannel(int channel) => channel switch
    {
        0 => 2,
        1 => 1,
        2 => 0,
        3 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), "Channel must be 0..3 (R, G, B, A)."),
    };

    /// <summary>
    /// The weightmap texture export <paramref name="export"/> of <paramref name="package"/> (whose <c>.ubulk</c> has
    /// <paramref name="ubulkLength"/> bytes), or null when it is not laid out as the island's weightmaps are (another pixel
    /// format, compressed or 64-bit bulk data, a mip chain that is not complete, or bytes out of range).
    /// </summary>
    public static Texture? Read(CookedPackage package, int export, long ubulkLength)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.GetExportClassName(export) != "Texture2D")
        {
            return null;
        }

        var block = package.ReadProperties(export);
        var (exportOffset, exportLength) = package.GetExportRange(export);
        var payload = package.UExp.AsSpan(exportOffset, exportLength);
        var at = block.EndOffset;
        try
        {
            var hasGuid = Int(payload, ref at);
            if (hasGuid is not (0 or 1))
            {
                return null;
            }

            at += (hasGuid * 16) + 4; // the guid, then the UTexture and UTexture2D strip flags
            if (Int(payload, ref at) != 1)
            {
                return null; // not cooked
            }

            var formatName = Int(payload, ref at);
            at += 4;
            if ((uint)formatName >= (uint)package.Names.Count || package.Names[formatName] != "PF_B8G8R8A8")
            {
                return null;
            }

            at += 8; // the skip offset (absolute) past this platform data
            var width = Int(payload, ref at);
            var height = Int(payload, ref at);
            var packed = Int(payload, ref at);
            var formatLength = Int(payload, ref at);
            if (packed != 1 || formatLength != 12 || Encoding.ASCII.GetString(payload.Slice(at, 11)) != "PF_B8G8R8A8")
            {
                return null;
            }

            at += formatLength;
            var firstMip = Int(payload, ref at);
            var count = Int(payload, ref at);
            if (firstMip != 0 || width < 1 || height < 1 || count != 1 + (int)Math.Log2(Math.Max(width, height)))
            {
                return null;
            }

            var mips = new List<Mip>(count);
            for (var m = 0; m < count; m++)
            {
                var (w, h) = (Math.Max(1, width >> m), Math.Max(1, height >> m));
                if (Int(payload, ref at) != 1)
                {
                    return null;
                }

                var flags = (uint)Int(payload, ref at);
                var elements = Int(payload, ref at);
                var onDisk = Int(payload, ref at);
                var offset = BinaryPrimitives.ReadInt64LittleEndian(payload[at..]);
                at += 8;
                if ((flags & (BulkCompressed | BulkSize64)) != 0 || elements != w * h * 4 || onDisk != elements)
                {
                    return null;
                }

                Mip mip;
                if ((flags & BulkInline) != 0 && (flags & BulkSeparateFile) == 0)
                {
                    mip = new Mip(w, h, false, exportOffset + at);
                    at += onDisk;
                }
                else if ((flags & (BulkSeparateFile | BulkNoOffsetFixUp)) == (BulkSeparateFile | BulkNoOffsetFixUp) && offset >= 0 && offset + onDisk <= ubulkLength)
                {
                    mip = new Mip(w, h, true, (int)offset);
                }
                else
                {
                    return null;
                }

                if (Int(payload, ref at) != w || Int(payload, ref at) != h || Int(payload, ref at) != 1 || at > payload.Length)
                {
                    return null;
                }

                mips.Add(mip);
            }

            return new Texture(export, width, height, mips);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The bytes of <paramref name="mip"/> in <paramref name="uexp"/> or <paramref name="ubulk"/>.</summary>
    public static Span<byte> Bytes(Mip mip, byte[] uexp, byte[]? ubulk)
    {
        ArgumentNullException.ThrowIfNull(uexp);
        return mip.InUBulk
            ? (ubulk ?? throw new InvalidDataException("the weightmap's large mips are in a .ubulk this package does not have")).AsSpan(mip.Offset, mip.Length)
            : uexp.AsSpan(mip.Offset, mip.Length);
    }

    /// <summary>
    /// The mip-0 texels of component sample (<paramref name="x"/>, <paramref name="y"/>): one, or two (four at a corner)
    /// where the sample lies on the edge shared by two subsections, which each keep their own copy of it.
    /// </summary>
    public static IEnumerable<(int X, int Y)> TexelsOf(int x, int y, int subsectionQuads, int numSubsections)
    {
        foreach (var tx in Texels(x, subsectionQuads, numSubsections))
        {
            foreach (var ty in Texels(y, subsectionQuads, numSubsections))
            {
                yield return (tx, ty);
            }
        }
    }

    private static IEnumerable<int> Texels(int q, int subsectionQuads, int numSubsections)
    {
        for (var s = 0; s < numSubsections; s++)
        {
            var local = q - (s * subsectionQuads);
            if (local >= 0 && local <= subsectionQuads)
            {
                yield return (s * (subsectionQuads + 1)) + local;
            }
        }
    }

    /// <summary>
    /// Rebuilds byte <paramref name="channelByte"/> of mips 1.. of <paramref name="mips"/> (B, G, R, A texels; mip 0 first)
    /// from mip 0 by the rule SCUM's own weightmaps follow (measured 2026-10-16 on the island's 172 visibility weightmaps):
    /// while a mip texel still comes from one subsection (<c>UpdateMipsTempl</c>'s layout) it is the bilinear blend of the
    /// previous mip at the same place in that subsection, in single precision, rounded once; the mips below average 2 x 2
    /// texels of the previous one, truncated. Rebuilt from mip 0 this way, the visibility channel of every mip of all 172 is
    /// byte for byte the game's; in the paint channels mips 4 and down are too, and 46, 19 and 2 bytes of mips 1 to 3 (of
    /// about 11 million) differ by 1 (the game's mips were made from finer weights than the 8-bit mip 0 it ships with).
    /// </summary>
    public static void BuildMips(IReadOnlyList<byte[]> mips, int width, int subsectionQuads, int numSubsections, int channelByte)
    {
        ArgumentNullException.ThrowIfNull(mips);
        var maxWholeSubsectionMip = (int)Math.Log2(subsectionQuads + 1) - 1;
        for (var mip = 1; mip < mips.Count; mip++)
        {
            var (size, prevSize) = (Math.Max(1, width >> mip), Math.Max(1, width >> (mip - 1)));
            var (dst, src) = (mips[mip], mips[mip - 1]);
            if (mip <= maxWholeSubsectionMip)
            {
                var quads = ((subsectionQuads + 1) >> mip) - 1;
                var prevQuads = ((subsectionQuads + 1) >> (mip - 1)) - 1;
                var inv = 1.0f / quads;
                for (var sy = 0; sy < numSubsections; sy++)
                {
                    for (var sx = 0; sx < numSubsections; sx++)
                    {
                        for (var vy = 0; vy <= quads; vy++)
                        {
                            for (var vx = 0; vx <= quads; vx++)
                            {
                                var px = prevQuads * (float)vx * inv;
                                var py = prevQuads * (float)vy * inv;
                                var (fx, fy) = ((int)MathF.Floor(px), (int)MathF.Floor(py));
                                var (tx, ty) = (((prevQuads + 1) * sx) + fx, ((prevQuads + 1) * sy) + fy);
                                int At(int dx, int dy)
                                {
                                    var i = (((ty + dy) * prevSize) + tx + dx) * 4;
                                    return i < src.Length ? src[i + channelByte] : 0;
                                }

                                var (fracX, fracY) = (px - fx, py - fy);
                                float a00 = At(0, 0), a10 = At(1, 0), a01 = At(0, 1), a11 = At(1, 1);
                                var top = a00 + (fracX * (a10 - a00));
                                var value = top + (fracY * (a01 + (fracX * (a11 - a01)) - top));
                                dst[((((quads + 1) * sy) + vy) * size + ((quads + 1) * sx) + vx) * 4 + channelByte] = (byte)MathF.Floor(value + 0.5f);
                            }
                        }
                    }
                }

                continue;
            }

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var sum = src[((((2 * y) * prevSize) + (2 * x)) * 4) + channelByte] + src[((((2 * y) * prevSize) + (2 * x) + 1) * 4) + channelByte]
                        + src[((((2 * y) + 1) * prevSize + (2 * x)) * 4) + channelByte] + src[((((2 * y) + 1) * prevSize + (2 * x) + 1) * 4) + channelByte];
                    dst[((y * size) + x) * 4 + channelByte] = (byte)(sum >> 2);
                }
            }
        }
    }


    private static int Int(ReadOnlySpan<byte> data, ref int at)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
        at += 4;
        return value;
    }
}
