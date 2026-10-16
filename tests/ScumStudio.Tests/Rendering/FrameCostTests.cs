using System.Diagnostics;
using System.Numerics;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Rendering.Targets;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// Owner, 2026-10-12: "the game is very smooth; our app is not". The Map's steady frame (island backdrop, then the detailed
/// cells, overlay, blit) allocated on every frame and scanned every instance of every visible batch; picks under the brush
/// prepared the whole view; the overlay was drawn twice. These pin the steady frame's cost.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class FrameCostTests(ITestOutputHelper output)
{
    [GlFact]
    public void SteadyFramesAllocateNothing()
    {
        using var gl = GlHarness.Create(320, 240);
        using var target = gl.Renderer.CreateTarget(320, 240, samples: 4);
        var (backdrop, detail, camera) = MapFrame(gl.Renderer);
        AddOverlay(gl.Renderer, camera, lines: 50, triangles: 20);
        // Two Settings objects, as the Map's perf view alternates: each scene keeps its own effective settings.
        var dusk = new RenderSettings { Sky = true, TimeOfDay = 18.5f, Shadows = true, ShadowDistance = 20_000f, ShowGrid = false };
        var noon = dusk with { TimeOfDay = 12f, Exposure = 1.1f };

        var frame = 0;
        void Frame()
        {
            gl.Renderer.Time = frame++ * 0.016f;
            gl.Renderer.Settings = dusk;
            gl.Renderer.Render(target, backdrop, camera);
            gl.Renderer.Settings = noon;
            gl.Renderer.Render(target, detail, camera, clear: false);
            target.BlitTo(0, target.Width, target.Height);
        }

        for (var i = 0; i < 20; i++)
        {
            Frame();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++)
        {
            Frame();
        }

        var still = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"still camera: {still / 200.0:N0} bytes per frame ({gl.Renderer.LastStats.Instances:N0} instances drawn)");
        Assert.InRange(gl.Renderer.LastStats.Instances, 2000, 40_000);

        // A moving camera: one culling frustum per frame is left (Frustum.FromViewProjection's six planes, ~120 bytes).
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++)
        {
            camera.Position += new Vector3(1f, 0f, 0f);
            Frame();
        }

        var moving = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"moving camera: {moving / 200.0:N0} bytes per frame");
        // What is left is a few small objects per frame (128 bytes still, 248 moving, measured; base: 54,976): far below
        // anything the GC notices at 60 fps. The audit's 50+ KB per frame of lists, records and uniform names is gone.
        Assert.InRange(still / 200.0, 0, 256);
        Assert.InRange(moving / 200.0, 0, 512);
    }

    [GlFact]
    public void OverlayIsDrawnOncePerFrame()
    {
        // Audit F425: the Map renders the backdrop and then the detailed scene; each Render drew the overlay, so the gizmo,
        // the brush circle and the selection lines were blended twice (and drawn twice per frame).
        using var gl = GlHarness.Create(64, 64);
        gl.UseUnlit();
        var camera = Camera();
        gl.Renderer.OverlayTriangles.Add(CoveringTriangle(camera, new Vector4(1f, 0f, 0f, 0.5f)));
        gl.Renderer.Render(gl.Target, new Scene(), camera);
        gl.Renderer.Render(gl.Target, new Scene(), camera, clear: false);
        var red = Centre(gl.Target);
        output.WriteLine($"overlay at alpha 0.5 over black: {red} (one blend ≈ 128, two ≈ 191)");
        Assert.InRange(red, 124, 132);
    }

    [GlFact]
    public void ScreenshotAndThumbnailCallersStillGetTheOverlay()
    {
        using var gl = GlHarness.Create(64, 64);
        gl.UseUnlit();
        var camera = Camera();
        gl.Renderer.OverlayTriangles.Add(CoveringTriangle(camera, new Vector4(1f, 0f, 0f, 0.5f)));

        // One Render read straight back (CLI renders, thumbnails, snapshots): the overlay is there, and only once when read twice.
        gl.Renderer.Render(gl.Target, new Scene(), camera);
        Assert.InRange(Centre(gl.Target), 124, 132);
        Assert.InRange(Centre(gl.Target), 124, 132);

        // A render into another target in between neither draws this target's overlay there nor loses it here.
        using var other = gl.Renderer.CreateTarget(64, 64);
        gl.Renderer.Render(gl.Target, new Scene(), camera);
        var gizmo = gl.Renderer.OverlayTriangles[0];
        gl.Renderer.OverlayTriangles.Clear();
        gl.Renderer.Render(other, new Scene(), camera);
        Assert.True(Centre(other) < 8, "the other target was rendered without an overlay");
        gl.Renderer.OverlayTriangles.Add(gizmo);
        Assert.InRange(Centre(gl.Target), 124, 132);

        // A multisampled target resolved by the host (ColorTexture) shows it as well.
        using var smooth = gl.Renderer.CreateTarget(64, 64, samples: 4);
        gl.Renderer.Render(smooth, new Scene(), camera);
        Assert.NotEqual(0u, smooth.ColorTexture);
        Assert.InRange(Centre(smooth), 124, 132);
    }

    [GlFact]
    public void PickPreparesOnlyTheClustersUnderTheCursor()
    {
        // Audit F433: a pick prepared the whole view (every cluster classified, every draw written), every frame the brush is
        // over the Map. 60k instances on a grid, all in view; a pick at the centre may prepare only what is under it.
        const int size = 400;
        using var gl = GlHarness.Create(size, size);
        gl.Renderer.Settings = new RenderSettings { CullPixelSize = 0f, ShowGrid = false, LightDirection = new Vector3(0.3f, -1f, 0.2f) };
        var cube = gl.Renderer.AddMesh(PrimitiveMeshes.Cube(150f), MeshSpace.Gl);
        var scene = new Scene();
        const int side = 245;
        for (var i = 0; i < side * side; i++)
        {
            scene.Add(cube, Matrix4x4.CreateTranslation(((i % side) - (side / 2)) * 200f, 0f, ((i / side) - (side / 2)) * 200f), (uint)(i + 1));
        }

        var camera = new FlyCamera { FieldOfView = 60f };
        camera.SetClipRange(100f, 200_000f);
        camera.Orbit(Vector3.Zero, 0f, -89.9f, 45_000f);
        var stats = gl.Renderer.Render(gl.Target, scene, camera);
        Assert.Equal(side * side, stats.Instances);

        var hit = gl.Renderer.Pick(gl.Target, scene, camera, size / 2, size / 2);
        var picked = gl.Renderer.LastPickStats;
        output.WriteLine($"pick prepared {picked.Instances:N0} instances in {picked.Draws} draws ({100.0 * picked.Instances / stats.Instances:0.###} % of the {stats.Instances:N0} drawn)");
        Assert.NotNull(hit);
        Assert.InRange(picked.Instances, 1, stats.Instances / 100);

        // Whatever is under any pixel is what the whole-view ID buffer has there.
        var full = gl.Renderer.RenderPickBuffer(gl.Target, scene, camera);
        var (codes, _) = gl.Target.ReadPickRegion(0, 0, size, size);
        var random = new Random(433);
        for (var n = 0; n < 40; n++)
        {
            var (x, y) = n == 0 ? (size / 2, size / 2) : (random.Next(size), random.Next(size));
            uint? expected = full.TryResolvePickCode(codes[(y * size) + x], out _, out var node) ? node!.SelectableId : null;
            Assert.Equal(expected, gl.Renderer.Pick(gl.Target, scene, camera, x, y)?.InstanceId);
        }

        // A brush-sized pick (radius 6) stays local too.
        gl.Renderer.Pick(gl.Target, scene, camera, 37, 351, radius: 6);
        Assert.InRange(gl.Renderer.LastPickStats.Instances, 1, stats.Instances / 100);
    }

    [GlFact]
    public void PrepareFrameBenchmark()
    {
        // Not a pass/fail rule: the CPU cost of a frame of 200k instances and of a pick, before and after.
        using var gl = GlHarness.Create(640, 360);
        gl.Renderer.Settings = new RenderSettings { ShowGrid = false, CullPixelSize = 0f };
        var cube = gl.Renderer.AddMesh(PrimitiveMeshes.Cube(150f), MeshSpace.Gl);
        var glass = gl.Renderer.AddMesh(PrimitiveMeshes.Cube(150f), MeshSpace.Gl);
        var scene = new Scene();
        const int side = 448;
        for (var i = 0; i < side * side; i++)
        {
            var node = scene.Add(i % 50 == 0 ? glass : cube, Matrix4x4.CreateTranslation(((i % side) - (side / 2)) * 300f, 0f, ((i / side) - (side / 2)) * 300f), (uint)(i + 1));
            if (i % 50 == 0)
            {
                node.Tint = new Vector4(0.6f, 0.8f, 1f, 0.4f);
            }
        }

        var camera = new FlyCamera { FieldOfView = 60f };
        camera.SetClipRange(10f, 300_000f);
        camera.Orbit(Vector3.Zero, 30f, -20f, 40_000f);
        for (var i = 0; i < 5; i++)
        {
            gl.Renderer.Render(gl.Target, scene, camera);
        }

        var clock = Stopwatch.StartNew();
        const int frames = 30;
        for (var i = 0; i < frames; i++)
        {
            gl.Renderer.Render(gl.Target, scene, camera);
        }

        var frameMs = clock.Elapsed.TotalMilliseconds / frames;
        clock.Restart();
        for (var i = 0; i < frames; i++)
        {
            gl.Renderer.Pick(gl.Target, scene, camera, 320, 180, radius: 4);
        }

        var pickMs = clock.Elapsed.TotalMilliseconds / frames;
        output.WriteLine($"{side * side:N0} instances, {gl.Renderer.LastStats.Instances:N0} drawn: {frameMs:0.00} ms per Render (CPU), {pickMs:0.00} ms per pick");
        Assert.True(gl.Renderer.LastStats.Instances > 0);
    }

    /// <summary>The Map's frame: a terrain backdrop with close-up layers and a sea, then 40k detailed instances with glass.</summary>
    private static (Scene Backdrop, Scene Detail, FlyCamera Camera) MapFrame(SceneRenderer renderer)
    {
        var backdrop = new Scene { Environment = new SceneEnvironment { FogDensity = 0.00002f } };
        var ground = renderer.AddMesh(PrimitiveMeshes.Plane(120_000f), MeshSpace.Gl, 1f, Solid(renderer, 90, 110, 70));
        renderer.SetMeshTerrainDetail(ground, new TerrainDetailTextures(Solid(renderer, 255, 0, 0), [Solid(renderer, 80, 90, 60), Solid(renderer, 120, 110, 90), null, null],
            new Vector4(1f / 600f), [new Vector3(0.3f), new Vector3(0.4f), Vector3.Zero, Vector3.Zero], [Solid(renderer, 128, 128, 255), null, null, null]));
        backdrop.Add(ground, Matrix4x4.CreateTranslation(0f, -10f, 0f), 0, "terrain");
        var sea = renderer.AddMesh(PrimitiveMeshes.Plane(400_000f), MeshSpace.Gl);
        renderer.Meshes[sea.Id].Water = true;
        backdrop.Add(sea, Matrix4x4.CreateTranslation(0f, -200f, 0f), 0, "sea").Tint = new Vector4(0.035f, 0.12f, 0.17f, 0.75f);

        var detail = new Scene();
        var cube = renderer.AddMesh(PrimitiveMeshes.Cube(400f), MeshSpace.Gl);
        var glass = renderer.AddMesh(PrimitiveMeshes.Cube(400f), MeshSpace.Gl);
        const int side = 200;
        for (var i = 0; i < side * side; i++)
        {
            var isGlass = i % 40 == 0;
            var node = detail.Add(isGlass ? glass : cube, Matrix4x4.CreateTranslation(((i % side) - (side / 2)) * 600f, 200f, ((i / side) - (side / 2)) * 600f), (uint)(i + 1));
            if (isGlass)
            {
                node.Tint = new Vector4(0.6f, 0.8f, 1f, 0.4f);
            }
        }

        var camera = new FlyCamera { FieldOfView = 60f };
        camera.SetClipRange(10f, 300_000f);
        camera.Orbit(Vector3.Zero, 30f, -25f, 30_000f);
        return (backdrop, detail, camera);
    }

    private static GpuTexture Solid(SceneRenderer renderer, byte r, byte g, byte b) => renderer.CreateTexture(1, 1, [r, g, b, 255]);

    private static void AddOverlay(SceneRenderer renderer, FlyCamera camera, int lines, int triangles)
    {
        for (var i = 0; i < lines; i++)
        {
            var a = new Vector3(i * 100f, 0f, 0f);
            renderer.Overlay.Add(new OverlayLine(a, a + new Vector3(0f, 500f, 300f), new Vector4(1f, 0.8f, 0f, 1f), 2f, a - Vector3.UnitX, null));
        }

        for (var i = 0; i < triangles; i++)
        {
            var a = new Vector3(0f, i * 50f, 0f);
            renderer.OverlayTriangles.Add(new OverlayTriangle(a, a + (camera.Right * 200f), a + (camera.Up * 200f), new Vector4(0f, 0.5f, 1f, 0.8f)));
        }
    }

    private static FlyCamera Camera()
    {
        var camera = new FlyCamera { FieldOfView = 60f };
        camera.SetClipRange(10f, 10_000f);
        camera.Orbit(Vector3.Zero, 0f, 0f, 1000f);
        return camera;
    }

    /// <summary>A triangle facing the camera that covers the middle of the view.</summary>
    private static OverlayTriangle CoveringTriangle(FlyCamera camera, Vector4 color) =>
        new(-(camera.Right + camera.Up) * 2000f, (camera.Right * 4000f) - (camera.Up * 2000f), (camera.Up * 4000f) - (camera.Right * 2000f), color);

    private static int Centre(RenderTarget target)
    {
        var rgba = target.ReadColorRgba();
        return rgba[(((target.Height / 2) * target.Width) + (target.Width / 2)) * 4];
    }
}
