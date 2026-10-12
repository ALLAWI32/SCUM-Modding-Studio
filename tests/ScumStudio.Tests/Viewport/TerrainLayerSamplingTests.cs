using System.Numerics;
using ScumStudio.Assets.Landscape;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// The paint layers are read on their own grid: a component whose heights come coarser than its weightmap (a surface read
/// with a vertex step) still answers which layer is painted where, so the brush's road and water rules hold there too.
/// </summary>
public sealed class TerrainLayerSamplingTests
{
    private const int Full = 16;

    [Fact]
    public void ACoarseSurfaceSamplesItsFullResolutionLayers()
    {
        var field = new TerrainHeightField([(Surface(4), Painted(5, 7))]);

        var at = field.SampleLayers(500f, 700f);
        Assert.Equal("Asphalt", Assert.Single(at).Name);
        Assert.Equal(1f, at[0].Weight, 3);
        Assert.Empty(field.SampleLayers(700f, 700f)); // two full quads away
        Assert.Empty(field.SampleLayers(500f, 900f));
        Assert.Equal(0.5f, Assert.Single(field.SampleLayers(550f, 700f)).Weight, 3); // half way to the next sample
    }

    [Fact]
    public void AFullResolutionSurfaceSamplesAsBefore()
    {
        var field = new TerrainHeightField([(Surface(Full), Painted(5, 7))]);

        Assert.Equal(1f, Assert.Single(field.SampleLayers(500f, 700f)).Weight, 3);
        Assert.Equal(0.5f, Assert.Single(field.SampleLayers(500f, 650f)).Weight, 3);
        Assert.Empty(field.SampleLayers(700f, 700f));
        Assert.Empty(field.SampleLayers(-100f, 700f)); // off the terrain
    }

    /// <summary>A flat 16 m component of <paramref name="quads"/> quads per side at the origin.</summary>
    private static LandscapeSurface Surface(int quads)
    {
        var size = Full * 100f / quads;
        return new LandscapeSurface("C", (0, 0), quads, Matrix4x4.CreateScale(size, size, 1f), new float[(quads + 1) * (quads + 1)]);
    }

    /// <summary>Full-resolution (1 m) layers with Asphalt painted on the one sample (<paramref name="x"/>, <paramref name="y"/>).</summary>
    private static LandscapeComponentLayers Painted(int x, int y)
    {
        const int N = Full + 1;
        var weights = new byte[N * N];
        weights[(y * N) + x] = 255;
        return new LandscapeComponentLayers("C", (0, 0), Full, [new LandscapeLayerWeights("Asphalt", null, true, weights)], null, [], 1, []);
    }
}
