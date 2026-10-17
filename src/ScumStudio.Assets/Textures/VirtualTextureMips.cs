using CUE4Parse.UE4.Assets.Exports.Texture;

namespace ScumStudio.Assets.Textures;

/// <summary>
/// The mips of a streaming virtual texture (<c>VirtualTextureStreaming</c>): UE 4.27 cooks it with no regular mips and keeps
/// its pixels in <c>FVirtualTextureBuiltData</c> instead, as tiles of <c>TileSize</c> pixels with a <c>TileBorderSize</c>
/// border on every side, each mip's tiles in Morton order (<c>TileIndexPerMip</c>), each tile's bytes at
/// <c>TileOffsetInChunk</c> in the chunk that holds it (<c>TileIndexPerChunk</c>). SCUM's <c>_VT</c> master shaders take
/// such textures (the light-tower trailer at the hangars, 512 of the 650 far-model colour slots): read as a texture without
/// mips they drew white. A mip is put together from the inside of its tiles in the cooked format (blocks are copied, not
/// decoded), so it decodes and uploads as a regular mip does.
/// </summary>
internal static class VirtualTextureMips
{
    /// <summary>Where a virtual texture's tiles are (the parts of <c>FVirtualTextureBuiltData</c> a mip needs), layer 0 only.</summary>
    internal sealed record Layout(int Width, int Height, int TileSize, int Border, int Layers, int MipCount,
        uint[] TileIndexPerMip, uint[] TileOffsetInChunk, uint[] TileIndexPerChunk)
    {
        public static Layout Of(FVirtualTextureBuiltData vt) => new((int)vt.Width, (int)vt.Height, (int)vt.TileSize, (int)vt.TileBorderSize,
            (int)vt.NumLayers, (int)vt.NumMips, vt.TileIndexPerMip ?? [], vt.TileOffsetInChunk ?? [], vt.TileIndexPerChunk ?? []);

        /// <summary>Size of mip <paramref name="mip"/> in pixels.</summary>
        public (int Width, int Height) Size(int mip) => (Math.Max(1, Width >> mip), Math.Max(1, Height >> mip));
    }

    /// <summary>True when <paramref name="texture"/> keeps its pixels as virtual texture tiles only (no regular mip with data).</summary>
    public static bool IsVirtual(UTexture2D texture) =>
        texture.PlatformData is { } platform && platform.Mips.Length == 0 && platform.VTData is { NumLayers: > 0, TileSize: > 0, Chunks.Length: > 0 };

    /// <summary>
    /// The mips that can be put together (largest first, index = the texture's mip index): every mip at least one tile wide
    /// and high, and mip 0 always. Smaller mips share one padded tile and are left out.
    /// </summary>
    public static List<(int Index, int Width, int Height)> Mips(Layout vt)
    {
        var mips = new List<(int, int, int)>();
        var count = Math.Min(vt.MipCount, vt.TileIndexPerMip.Length - 1);
        for (var m = 0; m < count; m++)
        {
            var (w, h) = vt.Size(m);
            if (m > 0 && (w < vt.TileSize || h < vt.TileSize))
            {
                break;
            }

            if (vt.TileIndexPerMip[m + 1] > vt.TileIndexPerMip[m])
            {
                mips.Add((m, w, h));
            }
        }

        return mips;
    }

    /// <summary>The mips of a cooked virtual texture (<see cref="Mips(Layout)"/>).</summary>
    public static List<(int Index, int Width, int Height)> Mips(FVirtualTextureBuiltData vt) => Mips(Layout.Of(vt));

    /// <summary>The cooked bytes of mip <paramref name="mip"/> of layer 0, laid out as a regular mip of that size in <paramref name="format"/>.</summary>
    /// <exception cref="NotSupportedException">The tiles are compressed with a codec other than raw GPU blocks, or the format has no known block size.</exception>
    public static byte[] Assemble(FVirtualTextureBuiltData vt, int mip, out EPixelFormat format)
    {
        format = vt.LayerTypes is { Length: > 0 } types ? types[0] : throw new InvalidDataException("The virtual texture names no pixel format.");
        if (vt.WidthInBlocks != 1 || vt.HeightInBlocks != 1)
        {
            throw new NotSupportedException($"Virtual textures of {vt.WidthInBlocks}x{vt.HeightInBlocks} blocks (UDIM) are not read.");
        }

        return Assemble(Layout.Of(vt), mip, format, chunk =>
        {
            if (vt.Chunks[chunk].CodecType is { Length: > 0 } codecs && codecs[0] != EVirtualTextureCodec.RawGPU)
            {
                throw new NotSupportedException($"Virtual texture tiles compressed as {codecs[0]} are not read.");
            }

            return vt.Chunks[chunk].BulkData?.Data ?? throw new InvalidDataException("A virtual texture chunk has no data (missing .ubulk?).");
        });
    }

    /// <summary>
    /// Mip <paramref name="mip"/> of <paramref name="vt"/> in <paramref name="format"/>: the inside of every tile (its border
    /// left out) copied block row by block row to where it sits in the mip; tiles the cook left out stay zero.
    /// <paramref name="chunkData"/> gives a chunk's bytes by index.
    /// </summary>
    internal static byte[] Assemble(Layout vt, int mip, EPixelFormat format, Func<int, byte[]> chunkData)
    {
        var (blockSize, blockBytes) = BlockOf(format);
        if (vt.TileSize % blockSize != 0 || vt.Border % blockSize != 0)
        {
            throw new NotSupportedException($"Virtual texture tiles of {vt.TileSize} px with a {vt.Border} px border do not align with {format} blocks.");
        }

        if (mip < 0 || mip + 1 >= vt.TileIndexPerMip.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(mip), $"The virtual texture has no mip {mip}.");
        }

        var (width, height) = vt.Size(mip);
        var outX = (width + blockSize - 1) / blockSize;
        var outY = (height + blockSize - 1) / blockSize;
        var tileBlocks = (vt.TileSize + (2 * vt.Border) + blockSize - 1) / blockSize; // the stored tile, border included
        var inner = vt.TileSize / blockSize;
        var skip = vt.Border / blockSize;
        var rowBytes = tileBlocks * blockBytes;
        var result = new byte[(long)outX * outY * blockBytes];
        var tilesX = (width + vt.TileSize - 1) / vt.TileSize;
        var tilesY = (height + vt.TileSize - 1) / vt.TileSize;
        var first = vt.TileIndexPerMip[mip];
        var end = vt.TileIndexPerMip[mip + 1];
        var chunks = new Dictionary<int, byte[]>();
        for (var ty = 0; ty < tilesY; ty++)
        {
            for (var tx = 0; tx < tilesX; tx++)
            {
                var index = first + ((MortonCode2((uint)tx) | (MortonCode2((uint)ty) << 1)) * (uint)Math.Max(1, vt.Layers));
                if (index >= end || index >= vt.TileOffsetInChunk.Length || vt.TileOffsetInChunk[index] == uint.MaxValue)
                {
                    continue;
                }

                var chunk = ChunkOf(vt, index);
                if (!chunks.TryGetValue(chunk, out var data))
                {
                    chunks[chunk] = data = chunkData(chunk);
                }

                var offset = (long)vt.TileOffsetInChunk[index];
                if (offset + ((long)rowBytes * tileBlocks) > data.Length)
                {
                    throw new InvalidDataException($"Virtual texture tile {index} runs past its chunk ({data.Length} bytes).");
                }

                var columns = Math.Min(inner, outX - (tx * inner));
                for (var row = 0; row < inner && (ty * inner) + row < outY; row++)
                {
                    var src = offset + ((long)(row + skip) * rowBytes) + ((long)skip * blockBytes);
                    var dst = ((((long)ty * inner) + row) * outX + ((long)tx * inner)) * blockBytes;
                    Array.Copy(data, src, result, dst, (long)columns * blockBytes);
                }
            }
        }

        return result;
    }

    private static int ChunkOf(Layout vt, uint tileIndex)
    {
        for (var c = 0; c + 1 < vt.TileIndexPerChunk.Length; c++)
        {
            if (tileIndex < vt.TileIndexPerChunk[c + 1])
            {
                return c;
            }
        }

        return Math.Max(0, vt.TileIndexPerChunk.Length - 2);
    }

    /// <summary>Block edge in pixels and bytes per block of the formats SCUM cooks its virtual textures in.</summary>
    internal static (int Size, int Bytes) BlockOf(EPixelFormat format) => format switch
    {
        EPixelFormat.PF_DXT1 or EPixelFormat.PF_BC4 => (4, 8),
        EPixelFormat.PF_DXT3 or EPixelFormat.PF_DXT5 or EPixelFormat.PF_BC5 or EPixelFormat.PF_BC7 => (4, 16),
        EPixelFormat.PF_B8G8R8A8 or EPixelFormat.PF_R8G8B8A8 => (1, 4),
        EPixelFormat.PF_G8 or EPixelFormat.PF_A8 => (1, 1),
        _ => throw new NotSupportedException($"Virtual texture tiles in {format} are not read."),
    };

    /// <summary>Spreads the low 16 bits of <paramref name="x"/> to the even bits (UE's <c>FMath::MortonCode2</c>).</summary>
    internal static uint MortonCode2(uint x)
    {
        x &= 0x0000ffff;
        x = (x ^ (x << 8)) & 0x00ff00ff;
        x = (x ^ (x << 4)) & 0x0f0f0f0f;
        x = (x ^ (x << 2)) & 0x33333333;
        x = (x ^ (x << 1)) & 0x55555555;
        return x;
    }
}
