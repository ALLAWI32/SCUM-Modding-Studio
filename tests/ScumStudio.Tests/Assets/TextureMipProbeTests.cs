using System.Reflection;
using ScumStudio.Assets.Textures;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Assets;

/// <summary>
/// Audit F270/F312/F318: choosing the mip to keep looked at the data of every mip, so a 4096² texture kept at 512 read and
/// inflated its 4096, 2048 and 1024 mips (about 21 MB of large-object garbage for a 0.35 MB chain) on every streaming step.
/// Sizes come from the header; data is looked at only for the mip that is kept and, on a miss, the next ones.
/// </summary>
public sealed class TextureMipProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void ProbesOnlyTheKeptChain()
    {
        var sizes = Enumerable.Range(0, 13).Select(i => (4096 >> i, 4096 >> i)).ToArray();
        var probed = new List<int>();
        var index = TextureDecoder.SelectMip(sizes.Length, i => sizes[i], i => { probed.Add(i); return true; }, 512);
        output.WriteLine($"probed mips {string.Join(", ", probed)}");
        Assert.Equal(3, index);
        Assert.All(probed, i => Assert.True(i >= 3, $"mip {i} ({sizes[i].Item1}²) is larger than the 512 kept and was read"));

        // Every mip larger than asked: the smallest one with data is kept, looked at from the smallest upwards.
        probed.Clear();
        Assert.Equal(2, TextureDecoder.SelectMip(3, i => sizes[i], i => { probed.Add(i); return true; }, 512));
        Assert.Equal([2], probed);
    }

    [Fact]
    public void SameMipAsBefore()
    {
        var random = new Random(270);
        int[] maxSizes = [0, -1, 1, 2, 3, 64, 100, 512, 1024, 4096, 100_000];
        for (var run = 0; run < 500; run++)
        {
            // A chain from a random (possibly non-square) top size down to 1x1, or cut short; random mips without data.
            var width = 1 << random.Next(0, 13);
            var height = 1 << random.Next(0, 13);
            var count = 1 + random.Next(0, 1 + (int)Math.Log2(Math.Max(width, height)));
            var mips = Enumerable.Range(0, count).Select(i => (Math.Max(1, width >> i), Math.Max(1, height >> i), random.Next(3) > 0)).ToArray();
            var maxSize = maxSizes[random.Next(maxSizes.Length)];
            var expected = TextureDecoder.SelectMip(mips, maxSize);
            var actual = TextureDecoder.SelectMip(count, i => (mips[i].Item1, mips[i].Item2), i => mips[i].Item3, maxSize);
            Assert.True(expected == actual, $"run {run}: {string.Join(" ", mips.Select(m => $"{m.Item1}x{m.Item2}{(m.Item3 ? "" : "-")}"))} max {maxSize}: {actual}, not {expected}");
        }
    }

    [Fact]
    public void Cue4ParseBulkDataIsLazy()
    {
        // The premise of the saving: CUE4Parse 1.2.2 reads a mip's payload when Data is first read, not when the package
        // loads. If this ever fails, looking at fewer mips no longer saves the reads (they happen at load anyway).
        var type = typeof(CUE4Parse.UE4.Assets.Objects.FByteBulkData);
        var lazyLoad = type.GetField("LazyLoad", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var data = type.GetField("_data", BindingFlags.NonPublic | BindingFlags.Instance);
        output.WriteLine($"LazyLoad: {lazyLoad?.FieldType} = {lazyLoad?.GetValue(null)}; _data: {data?.FieldType}");
        Assert.NotNull(lazyLoad);
        Assert.Equal(true, lazyLoad!.GetValue(null));
        Assert.NotNull(data);
        Assert.Equal(typeof(Lazy<byte[]>), data!.FieldType);
    }
}
