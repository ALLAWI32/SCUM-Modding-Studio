using System.Numerics;
using ScumStudio.Assets.Landscape;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Tests.Assets;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// pheidian: "is there a way to see the actual high poly landscape when you close in? To me it shows only the lowest LOD"
/// (whole-island and cell views load the ground at every fourth sample). <see cref="TerrainDetailRings"/> draws the
/// components near the camera from every sample (<see cref="LandscapeExtractor.BuildMesh"/>), without cracks at their
/// borders, within a vertex budget, and puts them back as the camera leaves.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class TerrainDetailRingsTests
{
    private const float Quad = 150f;

    /// <summary>A bumpy component of <paramref name="size"/> quads whose corner is at <paramref name="originX"/> (cm).</summary>
    private static LandscapeSurface Bumpy(int size, float originX = 0f, float originY = 0f) =>
        LandscapeExtractorTests.Ramp(size, Quad, new Vector2(originX, originY), (x, y) =>
        {
            var (wx, wy) = (originX + (x * Quad), originY + (y * Quad));
            return 1000f + (300f * MathF.Sin(wx * 0.013f)) + (200f * MathF.Cos(wy * 0.021f)) + (((int)(wx / Quad) * 7 + (int)(wy / Quad) * 13) % 5 * 40f);
        });

    private static Vector3 Vertex(MeshData mesh, int i) => new(mesh.Positions[i * 3], mesh.Positions[(i * 3) + 1], mesh.Positions[(i * 3) + 2]);

    [Fact]
    public void TheFullMeshHoldsEverySampleAndItsBorderFollowsTheLoadedStep()
    {
        const int size = 32;
        var surface = Bumpy(size);
        var loaded = LandscapeExtractor.BuildMesh(surface, 4);
        var full = LandscapeExtractor.BuildMesh(surface, 1, edgeStep: 4);
        Assert.Equal(9 * 9, loaded.VertexCount);
        Assert.Equal(33 * 33, full.VertexCount);
        Assert.Equal(loaded.Uv0.Max(), full.Uv0.Max());

        // Inside: every sample at its own height. On the border: on the loaded mesh's border line (no crack).
        var n = size + 1;
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var v = Vertex(full, (y * n) + x);
                Assert.Equal(surface.WorldPosition(x, y).X, v.X, 3);
                Assert.Equal(surface.WorldPosition(x, y).Y, v.Y, 3);
                var border = x == 0 || y == 0 || x == size || y == size;
                if (!border)
                {
                    Assert.Equal(surface.HeightAt(x, y), v.Z);
                    continue;
                }

                var q = y == 0 || y == size ? x : y;
                var (a, b) = (q / 4 * 4, Math.Min((q / 4 * 4) + 4, size));
                var (ha, hb) = y == 0 || y == size ? (surface.HeightAt(a, y), surface.HeightAt(b, y)) : (surface.HeightAt(x, a), surface.HeightAt(x, b));
                var expected = a == b ? ha : ha + ((hb - ha) * (q - a) / (b - a));
                Assert.Equal(expected, v.Z, 2);
            }
        }

        // The same mesh as the loader's at the loaded step.
        var again = LandscapeExtractor.BuildMesh(surface, 4, edgeStep: 4);
        Assert.Equal(loaded.Positions, again.Positions);
        Assert.Equal(loaded.Indices, again.Indices);
        Assert.Equal(loaded.Normals, again.Normals);
    }

    [Fact]
    public void TheShortLastSegmentOfTheBorderIsInterpolatedToo()
    {
        // 10 quads at step 4: the loaded border has vertices at 0, 4, 8 and 10.
        var surface = Bumpy(10);
        var full = LandscapeExtractor.BuildMesh(surface, 1, edgeStep: 4);
        var nine = Vertex(full, 9);
        Assert.Equal((surface.HeightAt(8, 0) + surface.HeightAt(10, 0)) * 0.5f, nine.Z, 2);
        Assert.Equal(surface.HeightAt(10, 0), Vertex(full, 10).Z); // the corner as it is
    }

    [Fact]
    public void TheLoadedStepIsReadFromTheMesh()
    {
        var surface = Bumpy(254);
        foreach (var step in new[] { 1, 2, 4, 16 })
        {
            var terrain = new PreparedTerrain("C", "L", LandscapeExtractor.BuildMesh(surface, step, computeNormals: false)) { Surface = surface };
            Assert.Equal(step, TerrainDetailRings.LoadedStep(terrain));
        }

        Assert.Null(TerrainDetailRings.LoadedStep(new PreparedTerrain("C", "L", LandscapeExtractor.BuildMesh(surface, 4))));
    }

    [Fact]
    public void TheDistanceIsToTheGroundOfTheComponent()
    {
        var surface = Bumpy(32); // 48 m square at the origin
        var top = surface.MaxHeightCm;
        Assert.Equal(1000f, TerrainDetailRings.DistanceCm(surface, new Vector3(2400f, 2400f, top + 1000f)), 1);
        Assert.Equal(10_000f, TerrainDetailRings.DistanceCm(surface, new Vector3(4800f + 10_000f, 2400f, top)), 1);
        Assert.Equal(0f, TerrainDetailRings.DistanceCm(surface, new Vector3(100f, 100f, (top + surface.MinHeightCm) * 0.5f)));
    }

    [GlFact]
    public void NearComponentsAreDrawnFromEverySampleWithinTheBudgetAndPutBack()
    {
        using var gl = GlHarness.Create(64, 64);
        const int size = 32; // 48 m components in a row along X
        var parts = Enumerable.Range(0, 5).Select(i =>
        {
            var surface = Bumpy(size, i * size * Quad);
            return new PreparedTerrain($"C_{i}", "Landscape_Test", LandscapeExtractor.BuildMesh(surface, 4)) { Surface = surface };
        }).ToList();
        var prepared = new PreparedLevelScene([], [], new Dictionary<string, PreparedMeshAsset>(), new Dictionary<string, ScumStudio.Assets.Textures.TextureImage>(), [], parts, [], TimeSpan.Zero);
        using var level = LevelSceneUploader.Upload(gl.Renderer, prepared);
        var ids = level.TerrainParts.Select(p => p.Handle.Id).ToList();
        var bounds = ids.Select(id => gl.Renderer.Meshes[id].Bounds).ToList();
        int Verts(int i) => gl.Renderer.Meshes[ids[i]].VertexCount;

        // Above the middle of the first component, 5 m over its highest sample: 0 m and 24.5 m away (full detail), 72 m
        // (every second sample), 120 m and 168 m (as loaded).
        var eye = UeToGl.Point(new FVector(2400f, 2400f, parts.Max(p => p.Surface!.MaxHeightCm) + 500f));
        var rings = new TerrainDetailRings { Settings = new TerrainDetailSettings(3_000f, 8_000f, 1_000_000) };
        Settle(rings, gl, level, eye);
        Assert.Equal(new[] { 33 * 33, 33 * 33, 17 * 17, 9 * 9, 9 * 9 }, Enumerable.Range(0, 5).Select(Verts).ToArray());
        Assert.Equal(3, rings.FinerCount); // two full, one half
        Assert.Equal((2 * 33 * 33) + (17 * 17), rings.FinerVertices);
        Assert.Equal(bounds, ids.Select(id => gl.Renderer.Meshes[id].Bounds).ToList()); // culled by the same bounds
        var camera = new FlyCamera();
        camera.SetClipRange(10f, 100_000f);
        camera.Orbit(eye, 0f, -60f, 1000f);
        gl.Renderer.Render(gl.Target, level.Scene, camera); // draws the new data

        // A budget for one full component and one half: the nearest first, the next one at the coarser ring.
        rings.Settings = new TerrainDetailSettings(3_000f, 8_000f, (33 * 33) + (17 * 17));
        Settle(rings, gl, level, eye);
        Assert.Equal(new[] { 33 * 33, 17 * 17, 9 * 9, 9 * 9, 9 * 9 }, Enumerable.Range(0, 5).Select(Verts).ToArray());

        // Off (or the camera gone): every component as loaded again.
        rings.Settings = TerrainDetailSettings.Off;
        Settle(rings, gl, level, eye);
        Assert.All(Enumerable.Range(0, 5), i => Assert.Equal(9 * 9, Verts(i)));
        Assert.Equal(0, rings.FinerCount);
        gl.Renderer.Render(gl.Target, level.Scene, camera);
    }

    [GlFact]
    public void AComponentKeepsItsRingALittlePastTheLine()
    {
        using var gl = GlHarness.Create(32, 32);
        var surface = Bumpy(32);
        var prepared = new PreparedLevelScene([], [], new Dictionary<string, PreparedMeshAsset>(), new Dictionary<string, ScumStudio.Assets.Textures.TextureImage>(), [],
            [new PreparedTerrain("C_0", "Landscape_Test", LandscapeExtractor.BuildMesh(surface, 4)) { Surface = surface }], [], TimeSpan.Zero);
        using var level = LevelSceneUploader.Upload(gl.Renderer, prepared);
        var id = level.TerrainParts[0].Handle.Id;
        var rings = new TerrainDetailRings { Settings = new TerrainDetailSettings(3_000f, 0f, 1_000_000) };
        var top = surface.MaxHeightCm;

        Settle(rings, gl, level, UeToGl.Point(new FVector(4800f + 2_900f, 2400f, top))); // 29 m off: full detail
        Assert.Equal(33 * 33, gl.Renderer.Meshes[id].VertexCount);
        Settle(rings, gl, level, UeToGl.Point(new FVector(4800f + 3_300f, 2400f, top))); // 33 m: still (hysteresis)
        Assert.Equal(33 * 33, gl.Renderer.Meshes[id].VertexCount);
        Settle(rings, gl, level, UeToGl.Point(new FVector(4800f + 3_600f, 2400f, top))); // 36 m: back as loaded
        Assert.Equal(9 * 9, gl.Renderer.Meshes[id].VertexCount);
    }

    private static void Settle(TerrainDetailRings rings, GlHarness gl, LevelScene level, Vector3 eye)
    {
        for (var i = 0; rings.Update(gl.Renderer, level, eye, 1000.0); i++)
        {
            Assert.True(i < 100_000, "the rings never settled");
            Thread.Sleep(1); // the worker builds the vertex data
        }
    }
}
