using System.Diagnostics;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Landscape;
using ScumStudio.Core.Geometry;
using ScumStudio.Pak;
using ScumStudio.Rendering.Resources;
using ScumStudio.Tests.Level;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// The ground near the camera on the real island (<c>SCUM_PAKS</c>): how far the ground whole-island and cell views load
/// (every fourth sample) is from the real one, and that <see cref="TerrainDetailRings"/>' full mesh holds every sample and
/// meets a neighbour drawn as loaded without a crack.
/// </summary>
public sealed class TerrainDetailRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int LoadedStep = 4; // MapPageViewModel: world mode and cells load the landscape at step 4

    private static AssetCatalog Open() => AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    [PaksTheory]
    [InlineData("Landscape_B_4_2b")] // the B_4 village meadows and roads
    [InlineData("Landscape_A_0_1c")] // the north-west coast and hills
    [InlineData("Landscape_C_2_3")] // inland forest
    public void TheLoadedGroundIsCoarseAndTheFullMeshIsTheGamesGround(string tile)
    {
        using var catalog = Open();
        var components = LandscapeExtractor.Extract(catalog, MapSlice.MapsPath + tile, new LandscapeExtractOptions { Step = LoadedStep })
            .SelectMany(p => p.Components).ToList();
        Assert.Equal(4, components.Count);

        var off = new List<float>();
        var buildMs = new List<double>();
        foreach (var component in components)
        {
            var surface = component.Surface!;
            var size = surface.ComponentSizeQuads;
            var coarse = Grid(size, LoadedStep);

            // The loaded ground (straight between every fourth sample) against every sample of the game's heightmap.
            for (var y = 0; y <= size; y++)
            {
                for (var x = 0; x <= size; x++)
                {
                    off.Add(MathF.Abs(Coarse(surface, coarse, x, y) - surface.HeightAt(x, y)));
                }
            }

            // The full mesh: every inner sample exactly; built and packed on a worker in a few milliseconds.
            var clock = Stopwatch.StartNew();
            var full = LandscapeExtractor.BuildMesh(surface, 1, LoadedStep);
            var packed = PreparedMesh.From(full);
            buildMs.Add(clock.Elapsed.TotalMilliseconds);
            Assert.Equal((size + 1) * (size + 1), full.VertexCount);
            Assert.Equal(full.VertexCount * PreparedMesh.FloatsPerVertex, packed.Vertices.Length);
            for (var y = 1; y < size; y++)
            {
                for (var x = 1; x < size; x++)
                {
                    Assert.Equal(surface.HeightAt(x, y), full.Positions[(((y * (size + 1)) + x) * 3) + 2]);
                }
            }

            // The loaded mesh is what the loader made (same vertices, same border).
            var loaded = LandscapeExtractor.BuildMesh(surface, LoadedStep, LoadedStep);
            Assert.Equal(component.Mesh.Positions, loaded.Positions);
            var centre = surface.WorldPosition(size / 2, size / 2);
            output.WriteLine($"{tile} {component.Name}: centre ({centre.X:0}, {centre.Y:0}, {centre.Z:0}) cm");
        }

        off.Sort();
        var mean = off.Average();
        var p95 = off[(int)(off.Count * 0.95)];
        var max = off[^1];
        var over25 = off.Count(d => d > 25f) / (double)off.Count;
        output.WriteLine($"{tile}: {off.Count:N0} samples; loaded ground (every {LoadedStep}th sample) off the real ground by mean {mean:0.0} cm, 95% within {p95:0.0} cm, worst {max:0.0} cm; {over25:P1} of the ground more than 25 cm off");
        output.WriteLine($"{tile}: full-detail component built and packed in {buildMs.Average():0.0} ms on average (worst {buildMs.Max():0.0} ms)");
        Assert.True(max > 0f, "the loaded ground matched every sample: nothing to draw finer");
    }

    [PaksFact]
    public void AFullComponentMeetsItsNeighbourDrawnAsLoadedWithoutACrack()
    {
        using var catalog = Open();
        var components = LandscapeExtractor.Extract(catalog, MapSlice.MapsPath + "Landscape_B_4_2b", new LandscapeExtractOptions { Step = LoadedStep })
            .SelectMany(p => p.Components).ToList();
        var pairs = 0;
        foreach (var left in components)
        {
            foreach (var right in components.Where(c => c.SectionBase == (left.SectionBase.X + left.ComponentSizeQuads, left.SectionBase.Y)))
            {
                // Left drawn from every sample, right as loaded: the shared edge (left x = size, right x = 0).
                var size = left.ComponentSizeQuads;
                var full = LandscapeExtractor.BuildMesh(left.Surface!, 1, LoadedStep);
                var coarse = Grid(size, LoadedStep);
                var worst = 0f;
                for (var y = 0; y <= size; y++)
                {
                    var i = ((y * (size + 1)) + size) * 3;
                    var (x0, y0, z0) = (full.Positions[i], full.Positions[i + 1], full.Positions[i + 2]);
                    var drawn = Coarse(right.Surface!, coarse, 0, y);
                    var at = right.Surface!.WorldPosition(0, y);
                    Assert.True(MathF.Abs(x0 - at.X) < 0.05f && MathF.Abs(y0 - at.Y) < 0.05f, $"row {y}: ({x0}, {y0}) vs ({at.X}, {at.Y})");
                    worst = MathF.Max(worst, MathF.Abs(z0 - drawn));
                }

                output.WriteLine($"{left.Name} (full) | {right.Name} (loaded): shared edge apart by at most {worst:0.000} cm");
                Assert.True(worst < 0.05f, $"crack of {worst} cm");
                pairs++;
            }
        }

        Assert.True(pairs >= 2, $"{pairs} neighbour pairs");
    }

    /// <summary>The quads a mesh of <paramref name="step"/> puts vertices on (the last one clamped to the edge).</summary>
    private static int[] Grid(int size, int step) =>
        Enumerable.Range(0, LandscapeExtractor.VertsPerSide(size, step)).Select(v => LandscapeExtractor.VertexQuad(v, step, size)).ToArray();

    /// <summary>Height a grid mesh with vertices at quads <paramref name="grid"/> draws at sample (x, y), on its triangles (diagonal (x+1, y)-(x, y+1)).</summary>
    private static float Coarse(LandscapeSurface surface, int[] grid, int x, int y)
    {
        var i = Cell(grid, x);
        var j = Cell(grid, y);
        var (qa, qb, qc, qd) = (grid[i], grid[i + 1], grid[j], grid[j + 1]);
        var fx = (x - qa) / (float)(qb - qa);
        var fy = (y - qc) / (float)(qd - qc);
        var a = surface.HeightAt(qa, qc);
        var b = surface.HeightAt(qb, qc);
        var c = surface.HeightAt(qa, qd);
        var d = surface.HeightAt(qb, qd);
        return fx + fy <= 1f ? a + (fx * (b - a)) + (fy * (c - a)) : d + ((1f - fx) * (c - d)) + ((1f - fy) * (b - d));

        static int Cell(int[] grid, int q)
        {
            for (var k = 0; k < grid.Length - 2; k++)
            {
                if (q < grid[k + 1])
                {
                    return k;
                }
            }

            return grid.Length - 2;
        }
    }
}
