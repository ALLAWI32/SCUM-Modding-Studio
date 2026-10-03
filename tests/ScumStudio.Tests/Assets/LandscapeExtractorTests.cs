using System.Numerics;
using ScumStudio.Assets.Landscape;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.Assets;

/// <summary>
/// Landscape mesh building: the vertex-step crack fix (every step reaches the component edge), texel/UV mapping, packed
/// normals and <see cref="LandscapeSurface"/> queries. Pure tests plus slice tests on <c>Landscape_A_0_4d</c>.
/// </summary>
public sealed class LandscapeExtractorTests
{
    private const string TileA04d = MapSlice.MapsPath + "Landscape_A_0_4d";

    [Theory]
    [InlineData(254, 1, 255)]
    [InlineData(254, 2, 128)]
    [InlineData(254, 3, 86)]
    [InlineData(254, 4, 65)]
    [InlineData(254, 8, 33)]
    [InlineData(63, 4, 17)]
    [InlineData(7, 100, 2)]
    public void VertsPerSide_IsCeilingPlusOne(int sizeQuads, int step, int expected)
    {
        var verts = LandscapeExtractor.VertsPerSide(sizeQuads, step);
        Assert.Equal(expected, verts);
        // The last vertex always lands exactly on the component edge, the one before it strictly inside.
        Assert.Equal(sizeQuads, LandscapeExtractor.VertexQuad(verts - 1, step, sizeQuads));
        Assert.True(LandscapeExtractor.VertexQuad(verts - 2, step, sizeQuads) < sizeQuads);
    }

    [Fact]
    public void VertexCount_For254QuadsAtStep4_Is65Squared()
    {
        var verts = LandscapeExtractor.VertsPerSide(254, 4);
        Assert.Equal(65 * 65, verts * verts);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(126, 126)]
    [InlineData(127, 127)]
    [InlineData(128, 129)]
    [InlineData(200, 201)]
    [InlineData(254, 255)]
    public void TexelIndex_HonoursSubsectionBorders(int quad, int texel) =>
        Assert.Equal(texel, LandscapeExtractor.TexelIndex(quad, 127, 2));

    [Fact]
    public void TexelIndex_SingleSubsection_IsIdentity()
    {
        for (var q = 0; q <= 63; q++)
        {
            Assert.Equal(q, LandscapeExtractor.TexelIndex(q, 63, 1));
        }
    }

    [Fact]
    public void AlbedoTexture_SizeAndCoordinates_LineUpWithSamples()
    {
        Assert.Equal(256, LandscapeExtractor.AlbedoTextureSize(254));
        Assert.Equal(64, LandscapeExtractor.AlbedoTextureSize(63));
        Assert.Equal(128, LandscapeExtractor.AlbedoTextureSize(127));
        Assert.Equal(0.5f / 256f, LandscapeExtractor.TextureCoordinate(0, 256));
        Assert.Equal(254.5f / 256f, LandscapeExtractor.TextureCoordinate(254, 256));
    }

    [Fact]
    public void PackedNormal_DecodesUpAndTilted_RejectsGarbage()
    {
        Assert.True(LandscapeExtractor.TryDecodePackedNormal(128, 128, out var up));
        Assert.True(up.Z > 0.9999f);
        Assert.True(LandscapeExtractor.TryDecodePackedNormal(255, 128, out var tilted));
        Assert.True(tilted.X > 0.99f && MathF.Abs(tilted.Length() - 1f) < 1e-5f);
        Assert.False(LandscapeExtractor.TryDecodePackedNormal(0, 0, out _));
    }

    [Fact]
    public void Surface_SamplesPlaneExactly_AndMapsWorldToQuads()
    {
        // 4 quads, 150 cm each, origin (1000, 2000); heights z = 10 * qx + 5 * qy + 100 (world cm).
        var surface = Ramp(4, 150f, new Vector2(1000f, 2000f), (x, y) => 10f * x + 5f * y + 100f);
        Assert.Equal(5, surface.SampleCount);
        Assert.Equal(150f, surface.QuadSizeCm, 3);
        Assert.Equal(new Vector2(1000f, 2000f), surface.WorldMinXY);
        Assert.Equal(new Vector2(1600f, 2600f), surface.WorldMaxXY);

        Assert.True(surface.TryGetQuadCoordinates(1000f + 1.5f * 150f, 2000f + 2.25f * 150f, out var qx, out var qy));
        Assert.Equal(1.5f, qx, 4);
        Assert.Equal(2.25f, qy, 4);
        Assert.Equal(10f * 1.5f + 5f * 2.25f + 100f, surface.SampleHeight(qx, qy), 3);
        Assert.False(surface.TryGetQuadCoordinates(999f, 2100f, out _, out _));

        // A plane: every normal equals the analytic one (heights per quad of 150 cm).
        var expected = Vector3.Normalize(new Vector3(-10f / 150f, -5f / 150f, 1f));
        Assert.True(Vector3.Distance(expected, surface.NormalAt(2, 2)) < 1e-4f);
        Assert.True(Vector3.Distance(expected, surface.SampleNormal(0.3f, 3.7f)) < 1e-4f);
        Assert.Equal(new Vector3(1000f + 300f, 2000f + 450f, 10f * 2f + 5f * 3f + 100f), surface.WorldPosition(2, 3));
    }

    [Fact]
    public void Surface_InterpolatesOnTheLandscapeTriangles()
    {
        // One raised corner (x+1, y+1) only affects the second triangle (b, c, d) of the quad.
        var surface = Ramp(1, 100f, Vector2.Zero, (x, y) => x == 1 && y == 1 ? 100f : 0f);
        Assert.Equal(0f, surface.SampleHeight(0.25f, 0.25f), 4);
        Assert.Equal(0f, surface.SampleHeight(0.5f, 0.5f), 4); // on the b-c diagonal
        Assert.Equal(50f, surface.SampleHeight(0.75f, 0.75f), 3);
    }

    [MapSliceFact]
    public void Slice_EveryStepReachesTheComponentEdge()
    {
        using var catalog = MapSlice.Open();
        foreach (var step in new[] { 1, 2, 3, 4, 8 })
        {
            var proxies = LandscapeExtractor.Extract(catalog, TileA04d, new LandscapeExtractOptions { Step = step, KeepSurface = false });
            var components = proxies.SelectMany(p => p.Components).ToList();
            Assert.Equal(4, components.Count);
            foreach (var c in components)
            {
                var verts = LandscapeExtractor.VertsPerSide(c.ComponentSizeQuads, step);
                Assert.Equal(verts * verts, c.Mesh.VertexCount);
                // 254 quads of 150 cm: the mesh spans the whole component (the old bug stopped at quad 252 for step 4).
                var b = c.Mesh.Bounds;
                Assert.InRange(b.Max.X - b.Min.X, 254 * 150f - 1f, 254 * 150f + 1f);
                Assert.InRange(b.Max.Y - b.Min.Y, 254 * 150f - 1f, 254 * 150f + 1f);
                // UVs address texel centres of a 256-texel texture: first sample 0.5/256, last 254.5/256.
                Assert.Equal(0.5f / 256f, c.Mesh.Uv0.Min(), 5);
                Assert.Equal(254.5f / 256f, c.Mesh.Uv0.Max(), 5);
            }
        }
    }

    [MapSliceFact]
    public void Slice_NeighbouringComponents_ShareEdgeVerticesAndNormals()
    {
        using var catalog = MapSlice.Open();
        foreach (var step in new[] { 1, 4 })
        {
            var components = LandscapeExtractor.Extract(catalog, TileA04d, new LandscapeExtractOptions { Step = step })
                .SelectMany(p => p.Components).ToDictionary(c => c.Name);
            var left = components["LandscapeComponent_44"];
            var right = components["LandscapeComponent_35"];
            Assert.Equal((-508, 1524), left.SectionBase);
            Assert.Equal((-254, 1524), right.SectionBase);
            var verts = LandscapeExtractor.VertsPerSide(254, step);
            for (var v = 0; v < verts; v++)
            {
                var a = Vertex(left.Mesh.Positions, (v * verts) + verts - 1); // +X edge of 44
                var b = Vertex(right.Mesh.Positions, v * verts);            // -X edge of 35
                Assert.True(Vector3.Distance(a, b) < 0.05f, $"step {step} row {v}: {a} vs {b}");
                var na = Vertex(left.Mesh.Normals, (v * verts) + verts - 1);
                var nb = Vertex(right.Mesh.Normals, v * verts);
                Assert.True(Vector3.Distance(na, nb) < 1e-3f, $"step {step} row {v}: normal {na} vs {nb}");
            }
        }
    }

    [MapSliceFact]
    public void Slice_PackedNormals_MatchTheHeights()
    {
        using var catalog = MapSlice.Open();
        var component = LandscapeExtractor.Extract(catalog, TileA04d).SelectMany(p => p.Components).First();
        var surface = component.Surface!;
        Assert.True(surface.HasPackedNormals);
        Assert.Equal(255, surface.SampleCount);
        double sum = 0;
        var count = 0;
        for (var y = 2; y < 253; y += 3)
        {
            for (var x = 2; x < 253; x += 3)
            {
                var dot = Math.Clamp(Vector3.Dot(surface.NormalAt(x, y), surface.FiniteDifferenceNormal(x, y)), -1f, 1f);
                sum += Math.Acos(dot) * 180.0 / Math.PI;
                count++;
            }
        }

        // Measured by the research probe: 0.7 - 1.0 degrees mean difference on A_0_4d.
        Assert.InRange(sum / count, 0.0, 2.0);
        Assert.InRange(component.MinHeightCm, surface.MinHeightCm - 0.01f, surface.MaxHeightCm);
    }

    internal static LandscapeSurface Ramp(int sizeQuads, float quadCm, Vector2 origin, Func<int, int, float> height)
    {
        var n = sizeQuads + 1;
        var heights = new float[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                heights[y * n + x] = height(x, y);
            }
        }

        var toWorld = Matrix4x4.CreateScale(quadCm, quadCm, 1f) * Matrix4x4.CreateTranslation(origin.X, origin.Y, 0f);
        return new LandscapeSurface("Ramp", (0, 0), sizeQuads, toWorld, heights);
    }

    private static Vector3 Vertex(float[] data, int index) => new(data[index * 3], data[index * 3 + 1], data[index * 3 + 2]);
}
