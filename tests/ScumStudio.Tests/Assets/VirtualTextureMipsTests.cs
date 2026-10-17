using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Textures;

namespace ScumStudio.Tests.Assets;

/// <summary>
/// Streaming virtual textures (the light-tower trailer, most far models): a mip is put together from the inside of its
/// tiles, which UE stores in Morton order with a border around each (<see cref="VirtualTextureMips"/>).
/// </summary>
public sealed class VirtualTextureMipsTests
{
    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 1u)]
    [InlineData(2u, 4u)]
    [InlineData(3u, 5u)]
    [InlineData(0xFFFFu, 0x55555555u)]
    public void MortonCodeSpreadsBitsToTheEvenPlaces(uint x, uint expected) => Assert.Equal(expected, VirtualTextureMips.MortonCode2(x));

    /// <summary>
    /// An 8x8 G8 texture in 4 px tiles with a 1 px border: mip 0 is 2x2 tiles stored (0,0) (1,0) (0,1) (1,1), mip 1 one tile.
    /// Every inner texel holds its own position in the mip; borders hold 0xEE and must never show.
    /// </summary>
    [Fact]
    public void TilesArePutTogetherWithoutTheirBorders()
    {
        const int tile = 4, border = 1, stored = tile + (2 * border);
        var chunk = new List<byte>();
        var offsets = new List<uint>();
        foreach (var (tx, ty, scale) in new[] { (0, 0, 1), (1, 0, 1), (0, 1, 1), (1, 1, 1), (0, 0, 2) })
        {
            offsets.Add((uint)chunk.Count);
            for (var y = 0; y < stored; y++)
            {
                for (var x = 0; x < stored; x++)
                {
                    var inner = x >= border && x < border + tile && y >= border && y < border + tile;
                    chunk.Add(inner ? Texel((tx * tile) + x - border, (ty * tile) + y - border, scale) : (byte)0xEE);
                }
            }
        }

        var layout = new VirtualTextureMips.Layout(8, 8, tile, border, 1, 4, [0, 4, 5, 5, 5], [.. offsets], [0, 5]);
        var data = chunk.ToArray();

        var mip0 = VirtualTextureMips.Assemble(layout, 0, EPixelFormat.PF_G8, _ => data);
        var mip1 = VirtualTextureMips.Assemble(layout, 1, EPixelFormat.PF_G8, _ => data);

        Assert.Equal(64, mip0.Length);
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                Assert.Equal(Texel(x, y, 1), mip0[(y * 8) + x]);
            }
        }

        Assert.Equal(16, mip1.Length);
        Assert.Equal(Texel(3, 2, 2), mip1[(2 * 4) + 3]);
        Assert.DoesNotContain((byte)0xEE, mip0);
        Assert.Equal([(0, 8, 8), (1, 4, 4)], VirtualTextureMips.Mips(layout).Select(m => (m.Index, m.Width, m.Height)).ToArray());
    }

    /// <summary>BC1 tiles of 8 px with a 4 px border (one block): the border block is skipped, a missing tile stays zero.</summary>
    [Fact]
    public void BlockTilesSkipTheirBorderBlockAndAMissingTileStaysEmpty()
    {
        // Stored tile: 16x16 px = 4x4 blocks of 8 bytes; inner blocks (1..2, 1..2) carry their tile number + 1.
        var chunk = new byte[2 * 4 * 4 * 8];
        for (var t = 0; t < 2; t++)
        {
            for (var by = 0; by < 4; by++)
            {
                for (var bx = 0; bx < 4; bx++)
                {
                    var inner = bx is 1 or 2 && by is 1 or 2;
                    Array.Fill(chunk, inner ? (byte)(t + 1) : (byte)0xEE, (t * 128) + (((by * 4) + bx) * 8), 8);
                }
            }
        }

        // 16x16 texture, 2x2 tiles: (0,0) and (1,0) stored, (0,1) missing, (1,1) out of the mip's tiles.
        var layout = new VirtualTextureMips.Layout(16, 16, 8, 4, 1, 1, [0, 3], [0, 128, uint.MaxValue], [0, 3]);
        var mip = VirtualTextureMips.Assemble(layout, 0, EPixelFormat.PF_DXT1, _ => chunk);

        Assert.Equal(4 * 4 * 8, mip.Length);
        byte BlockAt(int bx, int by) => mip[((by * 4) + bx) * 8];
        Assert.Equal(1, BlockAt(0, 0));
        Assert.Equal(1, BlockAt(1, 1));
        Assert.Equal(2, BlockAt(2, 0));
        Assert.Equal(2, BlockAt(3, 1));
        Assert.Equal(0, BlockAt(0, 2));
        Assert.Equal(0, BlockAt(3, 3));
        Assert.DoesNotContain((byte)0xEE, mip);
    }

    private static byte Texel(int x, int y, int scale) => (byte)((x * 16) + y + (scale * 100 % 7));
}
