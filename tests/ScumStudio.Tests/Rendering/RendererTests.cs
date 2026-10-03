using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Rendering.Snapshots;
using ScumStudio.Rendering.Targets;

namespace ScumStudio.Tests.Rendering;

/// <summary>A context + renderer + target for one test (GL objects are bound to the creating thread).</summary>
internal sealed class GlHarness : IDisposable
{
    private GlHarness(OffscreenGlContext context, int width, int height)
    {
        Context = context;
        Renderer = new SceneRenderer(context);
        Target = Renderer.CreateTarget(width, height);
    }

    public OffscreenGlContext Context { get; }

    public SceneRenderer Renderer { get; }

    public RenderTarget Target { get; }

    public static GlHarness Create(int width = 320, int height = 240) =>
        new(OffscreenGlContext.Create(width, height), width, height);

    /// <summary>Unlit settings: ambient 1, no key light, no grid, so output = sRGB(albedo * tint).</summary>
    public void UseUnlit(bool reverseZ = true) =>
        Renderer.Settings = new RenderSettings
        {
            SkyColor = Vector3.One,
            GroundColor = Vector3.One,
            LightColor = Vector3.Zero,
            ShowGrid = false,
            ClearColor = Vector4.Zero,
            ReverseZ = reverseZ,
        };

    public byte[] Pixel(byte[] rgba, int x, int y)
    {
        var o = ((y * Target.Width) + x) * 4;
        return rgba[o..(o + 4)];
    }

    public void Dispose()
    {
        Target.Dispose();
        Renderer.Dispose();
        Context.Dispose();
    }
}

[Collection(GlCollection.Name)]
public sealed class RendererTests
{
    [GlFact]
    public void TwoScenesDrawnEveryFrameKeepTheirBatches()
    {
        // The Map draws the island backdrop and then the detailed cells each frame; with one shared batch cache every
        // frame rebuilt both and re-uploaded their instances (84 ms instead of 3).
        using var gl = GlHarness.Create();
        var quad = MeshData.Create("quad", [0, 0, 0, 100, 0, 0, 0, 100, 0], [0u, 1u, 2u], [0, 0, 1, 0, 0, 1, 0, 0, 1], [0, 0, 1, 0, 0, 1]);
        var backdrop = new Scene();
        var detail = new Scene();
        backdrop.Add(gl.Renderer.AddMesh(quad), Matrix4x4.Identity, 1, "a");
        detail.Add(gl.Renderer.AddMesh(quad), Matrix4x4.CreateTranslation(200, 0, 0), 2, "b");
        var camera = new FlyCamera();
        camera.Frame(new BoundingBox(new Vector3(-100), new Vector3(400)), 4f / 3f, -135f, -30f);

        for (var frame = 0; frame < 3; frame++)
        {
            gl.Renderer.Render(gl.Target, backdrop, camera);
            gl.Renderer.Render(gl.Target, detail, camera, clear: false);
        }

        Assert.Equal(2, gl.Renderer.BatchUploads);
    }

    [GlFact]
    public void ContextReportsGl43Core()
    {
        using var context = OffscreenGlContext.Create(64, 32);
        Assert.True(context.Info.IsAtLeast(4, 3), context.Info.ToString());
        Assert.Equal((64, 32), context.Size);
        context.Resize(100, 50);
        Assert.Equal((100, 50), context.Size);
        Assert.False(string.IsNullOrWhiteSpace(context.Info.Renderer));
    }

    [GlFact]
    public async Task TestSceneRendersNonBlackPng()
    {
        // GL work stays on this thread (Silk resolves entry points lazily from the current context); the PNG is
        // written after the context is gone, where an await may resume on another thread.
        var rgba = RenderTestScene(640, 360);
        Assert.Equal(640 * 360 * 4, rgba.Length);
        var nonBlack = 0;
        var distinct = new HashSet<int>();
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i] + rgba[i + 1] + rgba[i + 2] > 60)
            {
                nonBlack++;
            }

            distinct.Add((rgba[i] << 16) | (rgba[i + 1] << 8) | rgba[i + 2]);
        }

        Assert.True(nonBlack > rgba.Length / 4 / 4, $"only {nonBlack} bright pixels");
        Assert.True(distinct.Count > 50, $"only {distinct.Count} distinct colours; shading looks flat");

        var path = Path.Combine(Path.GetTempPath(), $"scumstudio-render-{Guid.NewGuid():N}.png");
        try
        {
            await ImageExport.SavePngAsync(rgba, 640, 360, path);
            var (back, w, h) = await ImageExport.LoadRgbaAsync(path);
            Assert.Equal((640, 360), (w, h));
            Assert.Equal(rgba, back);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] RenderTestScene(int width, int height)
    {
        using var gl = GlHarness.Create(width, height);
        var cube = gl.Renderer.AddMesh(PrimitiveMeshes.Cube(100f), MeshSpace.Gl);
        var grid = CubeGridScene.Build(cube, 48, 48);
        var camera = new FlyCamera();
        grid.FrameCamera(camera);

        var stats = gl.Renderer.Render(gl.Target, grid.Scene, camera);
        Assert.Equal(1, stats.Batches);
        Assert.True(stats.Instances > 100, $"only {stats.Instances} instances drawn");
        Assert.True(stats.Culled > 0, "the framed view should cull part of a 48x48 grid");
        Assert.Equal(grid.Count, stats.Instances + stats.Culled);
        Assert.Equal((long)stats.Instances * 12, stats.Triangles);
        return gl.Target.ReadColorRgba();
    }

    [GlFact]
    public void PickingReturnsTheInstanceUnderAKnownPixel()
    {
        using var gl = GlHarness.Create(400, 300);
        var cube = gl.Renderer.AddMesh(PrimitiveMeshes.Cube(100f), MeshSpace.Gl);
        var other = gl.Renderer.AddMesh(PrimitiveMeshes.Cube(60f), MeshSpace.Gl);
        var scene = new Scene();
        // A row of cubes in front of the camera, alternating meshes, ids 101..105.
        for (var i = 0; i < 5; i++)
        {
            scene.Add(i % 2 == 0 ? cube : other, Matrix4x4.CreateTranslation(1000f, 0f, (i - 2) * 250f), (uint)(101 + i));
        }

        var unpickable = scene.Add(cube, Matrix4x4.CreateTranslation(1000f, 300f, 0f), 0);
        var camera = new FlyCamera { Position = Vector3.Zero };
        camera.SetClipRange(10f, 10_000f);

        foreach (var reverseZ in new[] { true, false })
        {
            gl.Renderer.Settings = new RenderSettings { ReverseZ = reverseZ };
            for (var i = 0; i < 5; i++)
            {
                var centre = new Vector3(1000f, 0f, (i - 2) * 250f);
                var (px, py) = ToPixel(camera, centre, gl.Target);
                var hit = gl.Renderer.Pick(gl.Target, scene, camera, px, py);
                Assert.NotNull(hit);
                Assert.Equal((uint)(101 + i), hit!.InstanceId);
                Assert.Equal(i % 2 == 0 ? cube.Id : other.Id, hit.MeshId);
                Assert.Equal((uint)(101 + i), hit.Node.SelectableId);
                // The hit lies on the camera-facing side (x = 1000 - half size) of that cube.
                var half = i % 2 == 0 ? 50f : 30f;
                Assert.InRange(hit.WorldPosition.X, 1000f - half - 2f, 1000f - half + 2f);
                Assert.InRange(hit.WorldPosition.Z, centre.Z - half - 2f, centre.Z + half + 2f);
            }

            // Background and non-pickable nodes return null.
            Assert.Null(gl.Renderer.Pick(gl.Target, scene, camera, 2, 2));
            var (ux, uy) = ToPixel(camera, unpickable.WorldTransform.Translation, gl.Target);
            Assert.Null(gl.Renderer.Pick(gl.Target, scene, camera, ux, uy));
            Assert.Null(gl.Renderer.Pick(gl.Target, scene, camera, -1, 5));
        }
    }

    [GlFact]
    public void AClickBetweenAFencesBarsTakesTheFenceNotTheBridgeBehind()
    {
        using var gl = GlHarness.Create(400, 300);
        var bar = gl.Renderer.AddMesh(PrimitiveMeshes.Cube(4f), MeshSpace.Gl);
        var deck = gl.Renderer.AddMesh(PrimitiveMeshes.Cube(100f), MeshSpace.Gl);
        var scene = new Scene();
        // A thin bar in front of a big deck: a click a few pixels beside the bar lands on the deck behind it.
        scene.Add(bar, Matrix4x4.CreateScale(1f, 40f, 1f) * Matrix4x4.CreateTranslation(500f, 0f, 0f), 7u);
        scene.Add(deck, Matrix4x4.CreateScale(1f, 10f, 10f) * Matrix4x4.CreateTranslation(2000f, 0f, 0f), 9u);
        var camera = new FlyCamera { Position = Vector3.Zero };
        camera.SetClipRange(10f, 10_000f);
        var (px, py) = ToPixel(camera, new Vector3(500f, 0f, 0f), gl.Target);

        Assert.Equal(9u, gl.Renderer.Pick(gl.Target, scene, camera, px + 3, py)!.InstanceId); // one pixel only: the deck
        Assert.Equal(7u, gl.Renderer.Pick(gl.Target, scene, camera, px + 3, py, radius: 4)!.InstanceId); // around it: the bar, nearer
        Assert.Equal(9u, gl.Renderer.Pick(gl.Target, scene, camera, px + 40, py, radius: 4)!.InstanceId); // well clear of it: the deck
    }

    [GlFact]
    public void TestScenePickAtCentreFindsTheCentreCube()
    {
        using var gl = GlHarness.Create(640, 360);
        var cube = gl.Renderer.AddMesh(PrimitiveMeshes.Cube(100f), MeshSpace.Gl);
        var grid = CubeGridScene.Build(cube, 64, 64);
        var camera = new FlyCamera();
        grid.FrameCamera(camera);
        var hit = gl.Renderer.Pick(gl.Target, grid.Scene, camera, 320, 180);
        Assert.NotNull(hit);
        var (c, r) = grid.CenterCell;
        Assert.Equal(grid.IdAt(c, r), hit!.InstanceId);
        Assert.Equal(cube.Id, hit.MeshId);
    }

    [GlFact]
    public void CullingSkipsInstancesBehindTheCamera()
    {
        using var gl = GlHarness.Create(200, 150);
        gl.UseUnlit();
        var cube = gl.Renderer.AddMesh(PrimitiveMeshes.Cube(100f), MeshSpace.Gl);
        var scene = new Scene();
        for (var i = 0; i < 20; i++)
        {
            scene.Add(cube, Matrix4x4.CreateTranslation(-1000f - (i * 150f), 0f, 0f), (uint)(i + 1));
        }

        var camera = new FlyCamera { Position = Vector3.Zero, Yaw = 0f };
        var stats = gl.Renderer.Render(gl.Target, scene, camera);
        Assert.Equal(0, stats.Instances);
        Assert.Equal(20, stats.Culled);
        Assert.Equal(0, stats.Batches);
        Assert.All(gl.Target.ReadColorRgba(), b => Assert.Equal(0, b));

        camera.Yaw = 180f;
        stats = gl.Renderer.Render(gl.Target, scene, camera);
        Assert.Equal(20, stats.Instances);
        Assert.Equal(0, stats.Culled);
        Assert.Contains(gl.Target.ReadColorRgba(), b => b != 0);
    }

    [GlFact]
    public void TexturesAreSampledSrgbCorrectAndTinted()
    {
        using var gl = GlHarness.Create(64, 64);
        gl.UseUnlit();
        var camera = new FlyCamera { Position = new Vector3(0f, 500f, 0f), Pitch = -90f };
        camera.SetClipRange(1f, 2000f);

        // Mid-grey sRGB 128 sampled (decoded to linear 0.216) and re-encoded must come back as 128.
        using var grey = gl.Renderer.CreateTexture(1, 1, [128, 128, 128, 255]);
        var plane = gl.Renderer.AddMesh(PrimitiveMeshes.Plane(10_000f), MeshSpace.Gl, texture: grey);
        var scene = new Scene();
        var node = scene.Add(plane, Matrix4x4.Identity, 1);
        gl.Renderer.Render(gl.Target, scene, camera);
        var px = gl.Pixel(gl.Target.ReadColorRgba(), 32, 32);
        Assert.InRange(px[0], 126, 130);
        Assert.InRange(px[1], 126, 130);
        Assert.Equal(255, px[3]);

        // Tint multiplies in linear space: red tint keeps only the red channel.
        node.Tint = new Vector4(1f, 0f, 0f, 1f);
        gl.Renderer.Render(gl.Target, scene, camera);
        px = gl.Pixel(gl.Target.ReadColorRgba(), 32, 32);
        Assert.InRange(px[0], 126, 130);
        Assert.Equal(0, px[1]);
        Assert.Equal(0, px[2]);
    }

    [GlFact]
    public void SelectionHighlightChangesTheColour()
    {
        using var gl = GlHarness.Create(128, 128);
        var cube = gl.Renderer.AddMesh(PrimitiveMeshes.Cube(100f), MeshSpace.Gl);
        var scene = new Scene();
        var node = scene.Add(cube, Matrix4x4.CreateTranslation(400f, 0f, 0f), 1);
        node.Tint = new Vector4(0.2f, 0.2f, 0.8f, 1f);
        var camera = new FlyCamera();
        gl.Renderer.Render(gl.Target, scene, camera);
        var normal = gl.Pixel(gl.Target.ReadColorRgba(), 64, 64);
        scene.SetSelection([1u]);
        gl.Renderer.Render(gl.Target, scene, camera);
        var selected = gl.Pixel(gl.Target.ReadColorRgba(), 64, 64);
        Assert.True(selected[0] > normal[0] + 20, $"selected red {selected[0]} vs {normal[0]}");
    }

    [GlFact]
    public void TargetsResizeAndDepthOrdersOverlappingInstances()
    {
        using var gl = GlHarness.Create(100, 100);
        gl.UseUnlit();
        var cube = gl.Renderer.AddMesh(PrimitiveMeshes.Cube(100f), MeshSpace.Gl);
        var scene = new Scene();
        var far = scene.Add(cube, Matrix4x4.CreateScale(4f) * Matrix4x4.CreateTranslation(3000f, 0f, 0f), 2);
        far.Tint = new Vector4(0f, 1f, 0f, 1f);
        var near = scene.Add(cube, Matrix4x4.CreateTranslation(1000f, 0f, 0f), 1);
        near.Tint = new Vector4(1f, 0f, 0f, 1f);
        var camera = new FlyCamera();

        Assert.True(gl.Target.Resize(300, 200));
        Assert.False(gl.Target.Resize(300, 200));
        foreach (var reverseZ in new[] { true, false })
        {
            gl.UseUnlit(reverseZ);
            gl.Renderer.Render(gl.Target, scene, camera);
            var rgba = gl.Target.ReadColorRgba();
            Assert.Equal(300 * 200 * 4, rgba.Length);
            var centre = gl.Pixel(rgba, 150, 100);
            Assert.Equal(255, centre[0]); // the near red cube wins the depth test (drawn after the far one)
            Assert.Equal(0, centre[1]);
            Assert.Equal(1u, gl.Renderer.Pick(gl.Target, scene, camera, 150, 100)!.InstanceId);
        }
    }

    [GlFact]
    public void LodsSwitchByDistanceAndSectionsDrawWithTheirOwnTextures()
    {
        using var gl = GlHarness.Create(240, 240);
        gl.UseUnlit();
        using var red = gl.Renderer.CreateTexture(1, 1, [255, 0, 0, 255]);
        using var blue = gl.Renderer.CreateTexture(1, 1, [0, 0, 255, 255]);
        // LOD 0: the cube, faces +X/-X/+Y in material A (red), -Y/+Z/-Z in material B (blue). LOD 1: a single quad in A.
        var cube = PrimitiveMeshes.Cube(100f);
        var lod0 = cube with { Sections = [new MeshSection("A", 0, 18), new MeshSection("B", 18, 18)] };
        var lod1 = MeshData.Create("Quad", [0f, -50f, -50f, 0f, 50f, -50f, 0f, 50f, 50f, 0f, -50f, 50f], [0u, 1u, 2u, 0u, 2u, 3u], sections: [new MeshSection("A", 0, 6)]);
        var mesh = gl.Renderer.AddMesh(PreparedMesh.FromLods([lod0, lod1], [1f, 0.3f], MeshSpace.Gl), null,
            new Dictionary<string, GpuTexture> { ["A"] = red, ["B"] = blue });
        var scene = new Scene();
        var node = scene.Add(mesh, Matrix4x4.CreateTranslation(1000f, 0f, 0f), 1);
        var camera = new FlyCamera { Position = Vector3.Zero };
        camera.SetClipRange(10f, 10_000f);

        // 10 m away the cube covers 0.16 of the view height: below the 0.3 threshold, so LOD 1 (2 triangles, one section).
        var stats = gl.Renderer.Render(gl.Target, scene, camera);
        Assert.Equal(2, stats.Triangles);
        Assert.Equal(1, stats.Batches);
        Assert.Equal(1u, gl.Renderer.Pick(gl.Target, scene, camera, 120, 120)!.InstanceId);

        // 1.5 m away it fills the view: LOD 0, one draw per section, both textures visible from a diagonal view.
        node.LocalTransform = Matrix4x4.Identity;
        camera.Orbit(Vector3.Zero, 45f, -30f, 500f);
        stats = gl.Renderer.Render(gl.Target, scene, camera);
        Assert.Equal(12, stats.Triangles);
        Assert.Equal(2, stats.Batches);
        var rgba = gl.Target.ReadColorRgba();
        var reds = 0;
        var blues = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            reds += rgba[i] == 255 && rgba[i + 1] == 0 && rgba[i + 2] == 0 ? 1 : 0;
            blues += rgba[i] == 0 && rgba[i + 1] == 0 && rgba[i + 2] == 255 ? 1 : 0;
        }

        Assert.True(reds > 500 && blues > 500, $"{reds} red, {blues} blue pixels");
        Assert.Equal(1, stats.Instances);

        // A dragged node is patched in place and re-uploaded: it draws where it moved (out of view, then back).
        node.LocalTransform = Matrix4x4.CreateTranslation(0f, 0f, -100_000f);
        gl.Renderer.Render(gl.Target, scene, camera);
        Assert.Equal(0, CountColoured(gl.Target.ReadColorRgba()));
        node.LocalTransform = Matrix4x4.Identity;
        gl.Renderer.Render(gl.Target, scene, camera);
        Assert.Equal(reds + blues, CountColoured(gl.Target.ReadColorRgba()));

        static int CountColoured(byte[] rgba)
        {
            var n = 0;
            for (var i = 0; i < rgba.Length; i += 4)
            {
                n += rgba[i] == 255 || rgba[i + 2] == 255 ? 1 : 0;
            }

            return n;
        }
    }

    [GlFact]
    public void MipmappedTexturesUseAnisotropicFiltering()
    {
        using var gl = GlHarness.Create(16, 16);
        var api = gl.Context.Gl;
        var supported = api.GetFloat((Silk.NET.OpenGL.GetPName)0x84FF);
        if (api.GetError() != Silk.NET.OpenGL.GLEnum.NoError || supported < 2f)
        {
            return; // driver without GL_EXT_texture_filter_anisotropic
        }

        using var texture = gl.Renderer.CreateTexture(64, 64, PrimitiveMeshes.Checkerboard(64, 8, (255, 0, 0), (0, 0, 255)));
        api.GetSamplerParameter(texture.Sampler, (Silk.NET.OpenGL.GLEnum)0x84FE, out float level);
        Assert.Equal(MathF.Min(supported, GpuTexture.MaxAnisotropy), level);
        texture.Bind(0);
        Assert.Equal((int)texture.Sampler, api.GetInteger(Silk.NET.OpenGL.GetPName.SamplerBinding));
        using var solid = gl.Renderer.CreateTexture(1, 1, [1, 2, 3, 255]);
        api.GetSamplerParameter(solid.Sampler, Silk.NET.OpenGL.GLEnum.TextureMinFilter, out int minFilter);
        Assert.Equal((int)Silk.NET.OpenGL.TextureMinFilter.LinearMipmapLinear, minFilter);
    }

    [GlFact]
    public void MeshSnapshotRendersAnUnrealSpaceMesh()
    {
        using var context = OffscreenGlContext.Create(160, 90);
        // A UE-space box standing on Z = 0 (Z up), 200 x 100 x 50 cm.
        var box = PrimitiveMeshes.Cube(1f);
        var positions = box.Positions.Select((v, i) => (i % 3) switch { 0 => v * 200f, 1 => v * 100f, _ => (v + 0.5f) * 50f }).ToArray();
        var ueMesh = ScumStudio.Core.Geometry.MeshData.Create("Box", positions, box.Indices, box.Normals, box.Uv0);
        var texture = PrimitiveMeshes.Checkerboard(16, 4, (255, 0, 0), (0, 0, 255));
        var result = MeshSnapshot.Render(context, ueMesh, MeshSpace.Unreal, new SnapshotOptions { Width = 160, Height = 90 }, (16, 16, texture, true));
        Assert.Equal(1, result.Stats.Instances);
        // UE Z (height 0..50) became GL Y.
        RenderAssert.Near(new Vector3(-100f, 0f, -50f), result.Bounds.Min, 1e-3f);
        RenderAssert.Near(new Vector3(100f, 50f, 50f), result.Bounds.Max, 1e-3f);
        var centre = ((45 * 160) + 80) * 4;
        var px = result.Rgba.AsSpan(centre, 4).ToArray();
        Assert.True(px[0] > 40 || px[2] > 40, $"centre pixel {string.Join(',', px)} should show the red/blue checker");
        Assert.True(px[1] < Math.Max(px[0], px[2]), "checker has no green");
    }

    private static (int X, int Y) ToPixel(FlyCamera camera, Vector3 world, RenderTarget target)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), camera.GetViewProjection(target.AspectRatio));
        var ndc = new Vector2(clip.X, clip.Y) / clip.W;
        return ((int)((ndc.X + 1f) * 0.5f * target.Width), (int)((1f - ndc.Y) * 0.5f * target.Height));
    }
}
