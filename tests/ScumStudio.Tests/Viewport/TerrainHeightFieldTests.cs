using System.Numerics;
using ScumStudio.Assets.Landscape;
using ScumStudio.Core.Mathematics;
using ScumStudio.Tests.Assets;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>Ground queries on synthetic ramps: height, normal, layers, read-out text and ray casts.</summary>
public sealed class TerrainHeightFieldTests
{
    // Two 10-quad components of 100 cm side by side along X (0..1000, 1000..2000), z = 20 * x_cm / 100 + 500 (11.3° slope).
    private static TerrainHeightField Field(out LandscapeSurface left, out LandscapeSurface right)
    {
        left = LandscapeExtractorTests.Ramp(10, 100f, Vector2.Zero, (x, _) => 20f * x + 500f);
        right = LandscapeExtractorTests.Ramp(10, 100f, new Vector2(1000f, 0f), (x, _) => 20f * (x + 10) + 500f);
        var layers = TerrainAlbedoBakerTests.Layers(left, ("Forest_Ground", i => (byte)(i % 11 < 5 ? 255 : 0)), ("Gravel", i => (byte)(i % 11 < 5 ? 0 : 255)));
        return new TerrainHeightField([(left, layers), (right, null)]);
    }

    private static float Analytic(float x) => 20f * x / 100f + 500f;

    [Fact]
    public void SampleHeight_FollowsTheRamp_AcrossComponents_AndIsNullOutside()
    {
        var field = Field(out _, out _);
        Assert.Equal(2, field.ComponentCount);
        // 1000.1 would sit inside the shared-edge tolerance (1e-3 quad = 1 mm) of the left component, which clamps it to 1000.
        foreach (var x in new[] { 0f, 123.4f, 999.9f, 1000f, 1001f, 1777f, 2000f })
        {
            Assert.Equal(Analytic(x), field.SampleHeight(x, 432.1f)!.Value, 2);
        }

        Assert.Null(field.SampleHeight(-1f, 500f));
        Assert.Null(field.SampleHeight(500f, 1000.5f));
        Assert.Equal(new Vector3(0f, 0f, 500f), field.Min);
        Assert.Equal(new Vector3(2000f, 1000f, 900f), field.Max);
    }

    [Fact]
    public void Normal_MatchesTheSlope()
    {
        var field = Field(out _, out _);
        var expected = Vector3.Normalize(new Vector3(-0.2f, 0f, 1f));
        Assert.True(Vector3.Distance(expected, field.Normal(1500f, 300f)!.Value) < 1e-4f);
        Assert.Null(field.Normal(5000f, 0f));
    }

    [Fact]
    public void SampleLayers_ReturnsWeightsStrongestFirst_AndDescribeFormatsThem()
    {
        var field = Field(out _, out _);
        // Sample (2, 0) is index 2 → Forest_Ground; sample (6, 0) → Gravel; between (4.5) both blend.
        Assert.Equal("Forest_Ground", Assert.Single(field.SampleLayers(200f, 0f)).Name);
        Assert.Equal("Gravel", Assert.Single(field.SampleLayers(600f, 0f)).Name);
        var mixed = field.SampleLayers(450f, 0f);
        Assert.Equal(2, mixed.Count);
        Assert.Equal(0.5f, mixed[0].Weight, 3);
        Assert.Empty(field.SampleLayers(1500f, 0f)); // right component has no layer data
        Assert.Equal("Ground: Forest_Ground 100% · Z 5.4 m", field.Describe(200f, 0f));
        Assert.Equal("Ground: (no layer data) · Z 8.0 m", field.Describe(1500f, 0f));
        Assert.Null(field.Describe(-10f, 0f));
    }

    [Fact]
    public void Raycast_HitsTheRampWithinOneCentimetre()
    {
        var field = Field(out _, out _);
        var origin = new Vector3(100f, 500f, 3000f);
        var direction = Vector3.Normalize(new Vector3(1f, 0.1f, -2f));
        var hit = field.Raycast(origin, direction)!.Value;
        // Analytic: origin.z + d.z t = 0.2 (origin.x + d.x t) + 500 (the hit lands in the right-hand component).
        var t = (3000f - 500f - 0.2f * origin.X) / ((0.2f * direction.X) - direction.Z);
        var expected = origin + direction * t;
        Assert.True(Vector3.Distance(expected, hit.Position) < 1f, $"{expected} vs {hit.Position}");
        Assert.InRange(hit.Distance, t - 2f, t + 2f);
        Assert.True(hit.Position.X > 1000f);
        Assert.True(Vector3.Distance(Vector3.Normalize(new Vector3(-0.2f, 0f, 1f)), hit.Normal) < 1e-3f);
        Assert.Equal("Ramp", hit.Component);
        Assert.Equal(hit.Position.Z, field.SampleHeight(hit.Position.X, hit.Position.Y)!.Value, 3);
    }

    [Fact]
    public void Raycast_VerticalMissesAndBelowGround()
    {
        var field = Field(out _, out _);
        var down = field.Raycast(new Vector3(1500f, 200f, 5000f), -Vector3.UnitZ)!.Value;
        Assert.Equal(Analytic(1500f), down.Position.Z, 2);
        Assert.Equal(5000f - Analytic(1500f), down.Distance, 1);
        Assert.Null(field.Raycast(new Vector3(1500f, 200f, 5000f), Vector3.UnitZ));
        Assert.Null(field.Raycast(new Vector3(5000f, 200f, 5000f), -Vector3.UnitZ));
        Assert.Null(field.Raycast(new Vector3(500f, 500f, 3000f), new Vector3(0f, 1f, 0f)));
        Assert.Null(field.Raycast(new Vector3(500f, 500f, 0f), Vector3.Normalize(new Vector3(1f, 0f, 0.01f))));
        Assert.Null(field.Raycast(new Vector3(100f, 500f, 3000f), Vector3.Normalize(new Vector3(1f, 0.1f, -0.9f)), maxDistance: 10f));
        Assert.Null(new TerrainHeightField([]).Raycast(Vector3.Zero, -Vector3.UnitZ));
    }

    [Fact]
    public void RaycastGl_ConvertsFromRendererSpace()
    {
        var field = Field(out _, out _);
        var originUe = new FVector(700f, 300f, 4000f);
        var hit = field.RaycastGl(UeToGl.Point(originUe), UeToGl.Direction(new FVector(0f, 0f, -1f)))!.Value;
        Assert.Equal(Analytic(700f), hit.Position.Z, 2);
        Assert.Equal(700f, hit.Position.X, 2);
        Assert.Equal(300f, hit.Position.Y, 2);
    }
}
