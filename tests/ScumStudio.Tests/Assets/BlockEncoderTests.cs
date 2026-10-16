using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Textures;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Assets;

/// <summary>
/// The block encoder the texture writer uses, checked through the decoder the studio already trusts
/// (<see cref="TextureDecoder.DecodeRaw"/>, AssetRipper's BC decoders).
/// </summary>
public sealed class BlockEncoderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("PF_DXT1", EPixelFormat.PF_DXT1)]
    [InlineData("PF_DXT5", EPixelFormat.PF_DXT5)]
    [InlineData("PF_BC7", EPixelFormat.PF_BC7)]
    public void SolidColoursComeBackWithinOne(string format, EPixelFormat decoded)
    {
        byte[][] colours = [[0, 0, 0, 255], [255, 255, 255, 255], [200, 30, 90, 255], [17, 140, 251, 255], [128, 128, 128, 255], [93, 61, 7, 255]];
        foreach (var colour in colours)
        {
            var rgba = Solid(8, 8, colour);
            var back = TextureDecoder.DecodeRaw(BlockEncoder.Encode(rgba, 8, 8, format), decoded, 8, 8);
            for (var i = 0; i < back.Length; i++)
            {
                var channel = i % 4;
                if (channel == 3 && format == "PF_DXT1")
                {
                    continue;
                }

                Assert.True(Math.Abs(back[i] - rgba[i]) <= 1, $"{format} colour ({string.Join(",", colour)}) channel {channel}: {back[i]}");
            }
        }
    }

    [Theory]
    [InlineData("PF_DXT1", EPixelFormat.PF_DXT1, 32)]
    [InlineData("PF_DXT5", EPixelFormat.PF_DXT5, 32)]
    [InlineData("PF_BC7", EPixelFormat.PF_BC7, 40)]
    public void ASmoothGradientKeepsItsQuality(string format, EPixelFormat decoded, double minimum)
    {
        const int size = 128;
        var rgba = Gradient(size);
        var back = TextureDecoder.DecodeRaw(BlockEncoder.Encode(rgba, size, size, format), decoded, size, size);
        var psnr = Psnr(rgba, back, 3);
        output.WriteLine($"{format}: {psnr:F1} dB");
        Assert.True(psnr >= minimum, $"{format}: {psnr:F1} dB < {minimum}");
    }

    [Theory]
    [InlineData("PF_DXT5", EPixelFormat.PF_DXT5)]
    [InlineData("PF_BC7", EPixelFormat.PF_BC7)]
    public void AlphaZeroAndOpaqueSurvive(string format, EPixelFormat decoded)
    {
        // An icon's edge: transparent black beside an opaque colour, in the same blocks.
        const int size = 16;
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var o = (y * size + x) * 4;
                var opaque = x + y >= size - 1;
                (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = opaque ? ((byte)220, (byte)180, (byte)40, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0);
            }
        }

        var back = TextureDecoder.DecodeRaw(BlockEncoder.Encode(rgba, size, size, format), decoded, size, size);
        for (var i = 3; i < back.Length; i += 4)
        {
            Assert.Equal(rgba[i], back[i]);
        }
    }

    [Fact]
    public void Bc5KeepsRedAndGreen()
    {
        const int size = 64;
        var rgba = Gradient(size);
        var back = TextureDecoder.DecodeRaw(BlockEncoder.Encode(rgba, size, size, "PF_BC5"), EPixelFormat.PF_BC5, size, size);
        for (var i = 0; i < back.Length; i += 4)
        {
            Assert.True(Math.Abs(back[i] - rgba[i]) <= 2 && Math.Abs(back[i + 1] - rgba[i + 1]) <= 2, $"texel {i / 4}: {back[i]},{back[i + 1]} vs {rgba[i]},{rgba[i + 1]}");
        }
    }

    [Fact]
    public void Bc4AndBgraRoundTrip()
    {
        var rgba = Gradient(16);
        var bc4 = TextureDecoder.DecodeRaw(BlockEncoder.Encode(rgba, 16, 16, "PF_BC4"), EPixelFormat.PF_BC4, 16, 16);
        Assert.All(Enumerable.Range(0, 256), t => Assert.InRange(bc4[t * 4] - rgba[t * 4], -2, 2));
        Assert.Equal(rgba, TextureDecoder.DecodeRaw(BlockEncoder.Encode(rgba, 16, 16, "PF_B8G8R8A8"), EPixelFormat.PF_B8G8R8A8, 16, 16));
    }

    [Fact]
    public void ASmallMipIsPaddedToABlock()
    {
        // A 2x2 mip as MipChain stores it (padded to 4x4 by repetition), and a 2x2 buffer padded by the encoder itself.
        var two = Solid(2, 2, [10, 200, 30, 255]);
        Assert.Equal(8, BlockEncoder.Encode(two, 2, 2, "PF_DXT1").Length);
        var padded = MipChain.Pad(two, 2, 2, 4);
        Assert.Equal((4, 4), (padded.Width, padded.Height));
        Assert.All(Enumerable.Range(0, 16), t => Assert.Equal(two.AsSpan(0, 4).ToArray(), padded.Rgba.AsSpan(t * 4, 4).ToArray()));

        var chain = MipChain.Build(Gradient(16), 16, 16, MipChain.FullCount(16, 16), srgb: true);
        Assert.Equal([16, 8, 4, 4, 4], chain.Select(m => m.Width));
    }

    [Fact]
    public void TheSameInputGivesTheSameBytes()
    {
        var rgba = Gradient(64);
        foreach (var format in BlockEncoder.Formats)
        {
            Assert.Equal(BlockEncoder.Encode(rgba, 64, 64, format), BlockEncoder.Encode(rgba, 64, 64, format));
        }
    }

    [Fact]
    public void AnSrgbMipAveragesInLinearLight()
    {
        // Black and white texels average to sRGB 188 (linear 0.5), not 128.
        var rgba = new byte[2 * 2 * 4];
        for (var t = 0; t < 4; t++)
        {
            var v = (byte)(t % 2 == 0 ? 0 : 255);
            (rgba[t * 4], rgba[t * 4 + 1], rgba[t * 4 + 2], rgba[t * 4 + 3]) = (v, v, v, 255);
        }

        var mip = MipChain.Build(rgba, 2, 2, 2, srgb: true, blockSize: 1)[1];
        Assert.InRange(mip.Rgba[0], 187, 189);
        Assert.InRange(MipChain.Build(rgba, 2, 2, 2, srgb: false, blockSize: 1)[1].Rgba[0], 127, 128);
    }

    private static byte[] Solid(int width, int height, byte[] colour)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            colour.CopyTo(rgba, i);
        }

        return rgba;
    }

    private static byte[] Gradient(int size)
    {
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var o = (y * size + x) * 4;
                rgba[o] = (byte)(x * 255 / (size - 1));
                rgba[o + 1] = (byte)(y * 255 / (size - 1));
                rgba[o + 2] = (byte)((x + y) * 255 / (2 * (size - 1)));
                rgba[o + 3] = 255;
            }
        }

        return rgba;
    }

    private static double Psnr(byte[] a, byte[] b, int channels)
    {
        double sum = 0;
        var n = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (i % 4 < channels)
            {
                sum += (a[i] - b[i]) * (a[i] - b[i]);
                n++;
            }
        }

        var mse = sum / n;
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
