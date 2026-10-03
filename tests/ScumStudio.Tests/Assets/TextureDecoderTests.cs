using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Textures;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ScumStudio.Tests.Assets;

public sealed class TextureDecoderTests
{
    // BC1 block: color0 = RGB565 red (0xF800), color1 = blue (0x001F), all 2-bit indices 0 -> every texel is color0.
    private static readonly byte[] RedBc1Block = [0x00, 0xF8, 0x1F, 0x00, 0, 0, 0, 0];

    [Fact]
    public void Bc1_SolidBlock_DecodesToRgba()
    {
        var rgba = TextureDecoder.DecodeRaw(RedBc1Block, EPixelFormat.PF_DXT1, 4, 4);
        Assert.Equal(64, rgba.Length);
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal([255, 0, 0, 255], rgba.AsSpan(i * 4, 4).ToArray());
        }
    }

    [Fact]
    public void Bc1_MipSmallerThanBlock_IsCropped()
    {
        var rgba = TextureDecoder.DecodeRaw(RedBc1Block, EPixelFormat.PF_DXT1, 2, 1);
        Assert.Equal(8, rgba.Length);
        Assert.Equal([255, 0, 0, 255, 255, 0, 0, 255], rgba);
    }

    [Fact]
    public void Bc1_TooLittleData_Throws() =>
        Assert.Throws<InvalidDataException>(() => TextureDecoder.DecodeRaw(RedBc1Block, EPixelFormat.PF_DXT1, 8, 8));

    [Fact]
    public void Bc5_NormalMap_ReconstructsBlue()
    {
        // Two BC4 channel blocks with both endpoints 128 -> X = Y = 128 (a flat normal).
        byte[] block = [128, 128, 0, 0, 0, 0, 0, 0, 128, 128, 0, 0, 0, 0, 0, 0];
        var normal = TextureDecoder.DecodeRaw(block, EPixelFormat.PF_BC5, 4, 4, isNormalMap: true);
        Assert.Equal(128, normal[0]);
        Assert.Equal(128, normal[1]);
        Assert.True(normal[2] >= 254, $"blue {normal[2]}");
        Assert.Equal(255, normal[3]);

        var raw = TextureDecoder.DecodeRaw(block, EPixelFormat.PF_BC5, 4, 4, isNormalMap: false);
        Assert.Equal(0, raw[2]);
    }

    [Fact]
    public void Bc4_ReplicatesRedToGrey()
    {
        byte[] block = [200, 200, 0, 0, 0, 0, 0, 0];
        var rgba = TextureDecoder.DecodeRaw(block, EPixelFormat.PF_BC4, 4, 4);
        Assert.Equal([200, 200, 200, 255], rgba.AsSpan(0, 4).ToArray());
    }

    [Fact]
    public void Uncompressed_Formats_AreConverted()
    {
        byte[] bgra = [10, 20, 30, 40, 50, 60, 70, 80];
        Assert.Equal([30, 20, 10, 40, 70, 60, 50, 80], TextureDecoder.DecodeRaw(bgra, EPixelFormat.PF_B8G8R8A8, 2, 1));
        Assert.Equal([10, 20, 30, 40, 50, 60, 70, 80], TextureDecoder.DecodeRaw(bgra, EPixelFormat.PF_R8G8B8A8, 2, 1));
        Assert.Equal([7, 7, 7, 255, 9, 9, 9, 255], TextureDecoder.DecodeRaw(new byte[] { 7, 9 }, EPixelFormat.PF_G8, 2, 1));
        Assert.Throws<NotSupportedException>(() => TextureDecoder.DecodeRaw(bgra, EPixelFormat.PF_ASTC_4x4, 4, 4));
    }

    [Fact]
    public void SelectMip_PicksLargestWithinLimit()
    {
        var mips = new List<(int, int, bool)> { (2048, 1024, false), (1024, 512, true), (512, 256, true), (256, 128, true) };
        Assert.Equal(1, TextureDecoder.SelectMip(mips, 0));
        Assert.Equal(1, TextureDecoder.SelectMip(mips, 1024));
        Assert.Equal(2, TextureDecoder.SelectMip(mips, 1000));
        Assert.Equal(3, TextureDecoder.SelectMip(mips, 100));
        Assert.Equal(-1, TextureDecoder.SelectMip([(4, 4, false)], 0));
    }

    [Fact]
    public async Task PngWriter_RoundTripsPixels()
    {
        var image = new TextureImage("T_Test", 2, 1, [255, 0, 0, 255, 0, 128, 255, 64], "PF_B8G8R8A8", 0, 2, 1, true, false);
        var path = Path.Combine(Path.GetTempPath(), "scumstudio-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            await PngWriter.SaveAsync(image, path);
            using var png = Image.Load<Rgba32>(path);
            Assert.Equal(2, png.Width);
            Assert.Equal(1, png.Height);
            Assert.Equal(new Rgba32(255, 0, 0, 255), png[0, 0]);
            Assert.Equal(new Rgba32(0, 128, 255, 64), png[1, 0]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
