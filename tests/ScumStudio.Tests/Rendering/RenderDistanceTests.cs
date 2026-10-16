using System.Diagnostics;
using System.Numerics;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// The 'Render distance' setting: the island backdrop draws the game's far models out to that many sectors around the
/// camera, the terrain always; and the GPU timer of the frame readout never waits for the card.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class RenderDistanceTests
{
    private const float SectorCm = 300_000f;
    private readonly ITestOutputHelper _output;

    public RenderDistanceTests(ITestOutputHelper output) => _output = output;

    [GlFact]
    public void FarViewReachesTheChosenSectorsAndTheTerrainStays()
    {
        using var gl = GlHarness.Create(64, 64);
        gl.UseUnlit();
        var detail = gl.Renderer.Settings with { CullPixelSize = 0f };

        // Looking along +X: a landscape tile 20 km away (no draw distance) and two far models (+inf, as DrawDistanceOf gives
        // them) at 2 km and 7 km, each its own mesh as the game's merged far models are.
        var scene = new Scene();
        scene.Add(gl.Renderer.AddMesh(PrimitiveMeshes.Cube(200_000f), MeshSpace.Gl), Matrix4x4.CreateTranslation(2_000_000f, 0f, 0f), 1, "Landscape_D_4_1/C_0").MaxDrawDistance = 0f;
        scene.Add(gl.Renderer.AddMesh(PrimitiveMeshes.Cube(20_000f), MeshSpace.Gl), Matrix4x4.CreateTranslation(200_000f, 0f, 0f), 2, "Distant_A_1").MaxDrawDistance = float.PositiveInfinity;
        scene.Add(gl.Renderer.AddMesh(PrimitiveMeshes.Cube(20_000f), MeshSpace.Gl), Matrix4x4.CreateTranslation(700_000f, 0f, 0f), 3, "Distant_A_3").MaxDrawDistance = float.PositiveInfinity;
        var camera = new FlyCamera { Position = Vector3.Zero, Yaw = 0f, Pitch = 0f };
        camera.SetClipRange(10f, 3_000_000f);

        gl.Renderer.Settings = FrameBudget.Backdrop(detail, 2, SectorCm);
        var two = gl.Renderer.Render(gl.Target, scene, camera);
        gl.Renderer.Settings = FrameBudget.Backdrop(detail, 4, SectorCm);
        var four = gl.Renderer.Render(gl.Target, scene, camera);
        _output.WriteLine($"2 sectors of 3 km: {two.Instances} drawn, {two.Culled} culled; 4 sectors: {four.Instances} drawn, {four.Culled} culled");

        Assert.Equal((2, 1), (two.Instances, two.Culled));
        Assert.Equal((3, 0), (four.Instances, four.Culled));
        Assert.Equal(1, FrameBudget.Backdrop(detail, 0, SectorCm).ObjectDrawDistance / SectorCm);
        Assert.Equal(4, FrameBudget.Backdrop(detail, 9, SectorCm).ObjectDrawDistance / SectorCm);
    }

    [GlFact]
    public void GpuTimerReadsBackWithoutWaiting()
    {
        using var gl = GlHarness.Create(256, 256);
        gl.UseUnlit();
        var grid = CubeGridScene.Build(gl.Renderer.AddMesh(PrimitiveMeshes.Cube(100f), MeshSpace.Gl), 32, 32);
        var camera = new FlyCamera { Position = new Vector3(0f, 2000f, 4000f), Pitch = -25f, Yaw = -90f };
        using var timer = new GpuFrameTimer(gl.Context.Gl);
        var worst = 0.0;
        for (var frame = 0; frame < 30; frame++)
        {
            var clock = Stopwatch.StartNew();
            timer.Begin();
            gl.Renderer.Render(gl.Target, grid.Scene, camera);
            timer.End();
            worst = Math.Max(worst, clock.Elapsed.TotalMilliseconds);
            gl.Context.Gl.Finish(); // the next frame's Begin finds the result ready
        }

        _output.WriteLine($"GPU {timer.LastMs:0.000} ms a frame; slowest Begin+Render+End on the CPU {worst:0.0} ms");
        Assert.True(double.IsFinite(timer.LastMs) && timer.LastMs > 0, $"GPU time {timer.LastMs}");
    }
}
