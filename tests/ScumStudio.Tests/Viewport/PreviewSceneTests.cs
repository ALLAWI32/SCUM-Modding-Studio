using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ScumStudio.App.Controls;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Viewport;

/// <summary>The scale figure, the sunlit floor and the staged full-size textures of a <see cref="PreviewScene"/> on the GPU.</summary>
[Collection(GlCollection.Name)]
public sealed class PreviewSceneTests(ITestOutputHelper output)
{
    private const int Width = 320;
    private const int Height = 240;

    [GlFact]
    public void ScaleFigureIsTheGamesCharacterUnpickableAndCountedInTheBounds()
    {
        using var gl = GlHarness.Create(64, 64);
        using var scene = PreviewScene.Upload(gl.Renderer, Cube(100f));
        var parts = Drawn(scene);
        var before = scene.Bounds;
        Assert.Equal(100f, before.Size.Y, 2);

        // No character loaded (no game files yet): no figure at all, never a stand-in of boxes.
        scene.FigureOf = () => null;
        scene.ShowScale = true;
        Assert.Equal(parts, Drawn(scene));
        Assert.False(scene.FigureShown);

        // The character arrives: the scene says so, and the next ShowScale stands it there, textured.
        var character = ScaleFigure.Stand(ScaleFigureTests.Character(178f));
        scene.FigureOf = () => character;
        Assert.True(scene.FigureStale);
        scene.ShowScale = true;
        Assert.False(scene.FigureStale);
        var added = Assert.Single(Drawn(scene).Except(parts));
        Assert.Equal(0u, added.SelectableId);
        Assert.NotNull(gl.Renderer.Meshes[added.Mesh!.Id].Texture);
        Assert.Equal(before, scene.ModelBounds);
        Assert.Equal(before.Min.Y, scene.Bounds.Min.Y, 3); // it stands on the model's ground
        Assert.InRange(scene.Bounds.Size.Y, 177.5f, 178.5f);
        Assert.Single(scene.Model.Parts);

        scene.ShowScale = false;
        Assert.Equal(parts, Drawn(scene));
        Assert.Equal(before, scene.Bounds);

        // A 3 cm item gets none: next to a person it would be a speck.
        using var small = PreviewScene.Upload(gl.Renderer, Cube(3f));
        small.FigureOf = () => character;
        small.ShowScale = true;
        Assert.Single(Drawn(small));
    }

    [GlFact]
    public async Task SunCastsTheModelsShadowOnTheFloor()
    {
        using var gl = GlHarness.Create(Width, Height);
        using var scene = PreviewScene.Upload(gl.Renderer, Cube(100f));
        Assert.Single(Drawn(scene)); // studio light: the model alone, no floor
        scene.Ground = true;
        Assert.Equal(2, Drawn(scene).Count);

        var camera = new FlyCamera();
        camera.Orbit(Vector3.Zero, PreviewScene.DefaultViewYaw, -60f, 600f);
        var radius = scene.Bounds.Extent.Length();
        camera.FitClipRange(600f, radius);
        gl.Renderer.Settings = MeshPreview.SettingsFor(PreviewLight.Sun, gl.Renderer.Settings, radius, -Vector3.UnitY) with { ShowGrid = false };
        gl.Renderer.Render(gl.Target, scene.Scene, camera);
        var rgba = gl.Target.ReadColorRgba();

        // The floor where the sunlight through the cube's centre lands, and a spot beside the cube the sun reaches.
        var sun = Vector3.Normalize(RenderSettings.IslandSunDirection);
        var shaded = Brightness(rgba, camera, sun * (-50f / sun.Y));
        var lit = Brightness(rgba, camera, new Vector3(150f, -50f, 0f));
        Assert.True(lit > shaded + 25f, $"lit floor {lit:F0}, shadow {shaded:F0}");

        if (Environment.GetEnvironmentVariable("SCUMSTUDIO_SCREENSHOTS") is { Length: > 0 } folder && folder is not "0")
        {
            // The preview's own view: the person beside the cube, the metre grid on the floor.
            scene.ShowScale = true;
            camera.Orbit(scene.Bounds.Center, PreviewScene.DefaultViewYaw, -20f, 700f);
            camera.FitClipRange(700f, scene.Bounds.Extent.Length());
            gl.Renderer.Settings = gl.Renderer.Settings with { ShowGrid = true, GridHeight = scene.Bounds.Min.Y, GridCellSize = MeshPreview.GridCellFor(100f) };
            gl.Renderer.Render(gl.Target, scene.Scene, camera);
            Directory.CreateDirectory(folder);
            await ImageExport.SavePngAsync(gl.Target.ReadColorRgba(), Width, Height, Path.Combine(folder, "preview-sun-scale.png"));
        }
    }

    [GlFact]
    public void FullSizeTexturesLandOverStandInsAFewAFrame()
    {
        // Ten materials at 4096² (BC7 colour, BC5 normal map, whole mip chains): a vehicle's worth on the Vehicles tab.
        const int Materials = 10;
        using var gl = GlHarness.Create(64, 64);
        var vehicle = Vehicle(Materials, 4096);
        var full = vehicle.Textures.Values.Sum(t => t.CompressedMips!.Sum(m => (long)m.Length));

        var clock = Stopwatch.StartNew();
        using (var whole = PreviewScene.Upload(gl.Renderer, vehicle))
        {
            Show(gl, whole);
            Assert.Equal(full, Textures(gl, whole).Sum(t => t.EstimatedBytes));
        }

        var oneFrame = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        using var staged = PreviewScene.UploadStaged(gl.Renderer, vehicle);
        Show(gl, staged);
        var firstFrame = clock.Elapsed.TotalMilliseconds;
        Assert.Equal(2 * Materials, staged.PendingTextures);
        Assert.All(Textures(gl, staged), t => Assert.True(t.Width <= PreviewScene.StandInSize, $"{t.Width} px on the first frame"));
        var standIns = Textures(gl, staged).Sum(t => t.EstimatedBytes);
        Assert.True(standIns * 100 < full, $"{standIns} bytes of stand-ins");

        var (frames, slowest) = (0, 0.0);
        bool done;
        do
        {
            clock.Restart();
            done = staged.Stage(8.0);
            Show(gl, staged);
            (frames, slowest) = (frames + 1, Math.Max(slowest, clock.Elapsed.TotalMilliseconds));
        }
        while (!done);

        Assert.Equal(0, staged.PendingTextures);
        Assert.All(Textures(gl, staged), t => Assert.Equal(4096, t.Width));
        Assert.Equal(full, Textures(gl, staged).Sum(t => t.EstimatedBytes));
        output.WriteLine(FormattableString.Invariant(
            $"{2 * Materials} textures, {full / 1048576.0:F0} MB on the GPU: one frame {oneFrame:F0} ms; staged first frame {firstFrame:F1} ms ({standIns / 1024.0:F0} KB of stand-ins), then {frames} frames, slowest {slowest:F1} ms"));
    }

    [GlFact]
    public void APaintRestyleUploadsItsBakeAtOnce()
    {
        // Repaint brings a new baked RGBA texture at the paint size (2048²): Restyle sends it whole, not staged.
        using var gl = GlHarness.Create(64, 64);
        var vehicle = Vehicle(1, 4096);
        using var scene = PreviewScene.UploadStaged(gl.Renderer, vehicle);
        var bake = new TextureImage("Bake", 2048, 2048, new byte[2048 * 2048 * 4], "PF_B8G8R8A8", 0, 2048, 2048, true, false);
        var times = new List<double>();
        for (var run = 0; run < 3; run++)
        {
            var painted = vehicle with
            {
                Parts = [vehicle.Parts[0] with { TexturePath = "#paint" }],
                Textures = new Dictionary<string, TextureImage>(vehicle.Textures) { ["#paint"] = bake with { Rgba = new byte[bake.Rgba.Length] } },
            };
            var clock = Stopwatch.StartNew();
            Assert.True(scene.Restyle(painted));
            Show(gl, scene);
            times.Add(clock.Elapsed.TotalMilliseconds);
            Assert.Equal(2048, gl.Renderer.Meshes[Drawn(scene)[0].Mesh!.Id].Texture!.Width);
        }

        output.WriteLine(FormattableString.Invariant($"Restyle with a 2048² RGBA bake: {string.Join(", ", times.Select(t => t.ToString("F1", CultureInfo.InvariantCulture)))} ms"));
    }

    [GlFact]
    public void ARestyleWithAMovedPartKeepsTheMeshesAndMovesOnlyThatPart()
    {
        // Owner: "every part of the car like Blender: move it". Moving a door re-uploaded the whole car and lost the view.
        using var gl = GlHarness.Create(64, 64);
        var model = Vehicle(3, 64);
        using var scene = PreviewScene.Upload(gl.Renderer, model);
        var meshes = Drawn(scene).Select(n => n.Mesh!.Id).ToList();
        var matrices = Drawn(scene).Select(n => n.LocalTransform).ToList();
        var before = scene.ModelBounds;

        var moved = model with { Parts = [model.Parts[0], model.Parts[1] with { Transform = new FTransform(new FVector(0, 300, 0)) }, model.Parts[2]] };
        Assert.True(scene.Restyle(moved));
        Assert.Equal(meshes, Drawn(scene).Select(n => n.Mesh!.Id));
        Assert.Equal(matrices[0], Drawn(scene)[0].LocalTransform);
        Assert.Equal(matrices[2], Drawn(scene)[2].LocalTransform);
        Assert.Equal(300f, Drawn(scene)[1].LocalTransform.M43, 3); // UE Y is GL Z
        Assert.Equal(before.Max.Z + 300f, scene.ModelBounds.Max.Z, 2);
        Assert.Same(moved, scene.Model);
    }

    [GlFact]
    public void PartsMoveAndHideOneAtATimeWithTheirBounds()
    {
        using var gl = GlHarness.Create(64, 64);
        var model = Vehicle(2, 64);
        using var scene = PreviewScene.Upload(gl.Renderer, model);
        var nodes = Drawn(scene);
        Assert.Equal(1, scene.PartIndexOf(nodes[1]));
        Assert.Equal(-1, scene.PartIndexOf(null));

        Assert.True(scene.SetPartTransform(1, new FTransform(new FVector(500, 0, 0))));
        Assert.Equal(new FVector(500, 0, 0), scene.PartTransform(1)!.Value.Translation);
        Assert.Equal(550f, scene.ModelBounds.Max.X, 2);
        Assert.Equal(Matrix4x4.Identity, nodes[0].LocalTransform);

        Assert.True(scene.SetPartHidden(1, true));
        Assert.False(nodes[1].Visible);
        Assert.True(nodes[0].Visible);
        Assert.Equal(50f, scene.ModelBounds.Max.X, 2); // a hidden part leaves the bounds
        Assert.True(scene.SetPartHidden(1, false));
        Assert.Equal(550f, scene.ModelBounds.Max.X, 2);
        Assert.False(scene.SetPartHidden(2, true));
        Assert.False(scene.SetPartTransform(-1, FTransform.Identity));
    }

    [Fact]
    public void AStandInIsTheTexturesOwnLowerMips()
    {
        var chain = Chain(4096);
        var colour = new TextureImage("T_D", 4096, 2048, [], "PF_BC7", 0, 4096, 2048, true, false) { CompressedMips = chain };
        var standIn = PreviewScene.StandIn(colour)!;
        Assert.Equal((256, 128, 4), (standIn.Width, standIn.Height, standIn.MipIndex));
        Assert.Same(chain[4], standIn.CompressedMips![0]);

        // A chain cut above 256 px has no stand-in (it goes up whole); an RGBA image is sampled every n-th pixel.
        Assert.Null(PreviewScene.StandIn(colour with { CompressedMips = chain.Take(3).ToArray() }));
        var rgba = new byte[1024 * 1024 * 4];
        rgba[(((4 * 1024) + 8) * 4) + 1] = 200; // pixel (8, 4) is green: stand-in pixel (2, 1)
        var small = PreviewScene.StandIn(new TextureImage("T_Rgba", 1024, 1024, rgba, "PF_B8G8R8A8", 0, 1024, 1024, true, false))!;
        Assert.Equal((256, 256), (small.Width, small.Height));
        Assert.Equal(200, small.Rgba[((256 + 2) * 4) + 1]);
    }

    /// <summary><paramref name="materials"/> boxes, each with its own colour and normal map of <paramref name="size"/>² (one shared zeroed mip chain).</summary>
    private static PreviewModel Vehicle(int materials, int size)
    {
        var chain = Chain(size);
        var textures = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<PreviewPart>();
        for (var i = 0; i < materials; i++)
        {
            textures[$"T_{i}_D"] = new TextureImage($"T_{i}_D", size, size, [], "PF_BC7", 0, size, size, true, false) { CompressedMips = chain };
            textures[$"T_{i}_N"] = new TextureImage($"T_{i}_N", size, size, [], "PF_BC5", 0, size, size, false, true) { CompressedMips = chain };
            parts.Add(new PreviewPart($"Part{i}", PrimitiveMeshes.Cube(100f), $"T_{i}_D", FTransform.Identity) { NormalPath = $"T_{i}_N" });
        }

        return new PreviewModel("Vehicle", parts, textures);
    }

    /// <summary>A whole mip chain of 16-byte blocks (BC5/BC7) for a texture of <paramref name="size"/> px.</summary>
    private static byte[][] Chain(int size)
    {
        var mips = new List<byte[]>();
        for (var edge = size; edge >= 1; edge /= 2)
        {
            mips.Add(new byte[Math.Max(1, edge / 4) * Math.Max(1, edge / 4) * 16]);
        }

        return [.. mips];
    }

    /// <summary>Draws <paramref name="scene"/> and reads the frame back, so the GPU has finished with the uploads.</summary>
    private static void Show(GlHarness gl, PreviewScene scene)
    {
        var camera = new FlyCamera();
        camera.Orbit(Vector3.Zero, PreviewScene.DefaultViewYaw, -20f, 600f);
        camera.FitClipRange(600f, 200f);
        gl.Renderer.Render(gl.Target, scene.Scene, camera);
        _ = gl.Target.ReadColorRgba();
    }

    private static List<GpuTexture> Textures(GlHarness gl, PreviewScene scene) =>
        Drawn(scene).Select(n => gl.Renderer.Meshes[n.Mesh!.Id]).SelectMany(m => new[] { m.Texture, m.NormalMap }).OfType<GpuTexture>().Distinct().ToList();

    private static PreviewModel Cube(float size) =>
        new("Cube", [new PreviewPart("Cube", PrimitiveMeshes.Cube(size), null, FTransform.Identity, new Vector4(0.6f, 0.6f, 0.6f, 1f))],
            new Dictionary<string, TextureImage>());

    private static List<SceneNode> Drawn(PreviewScene scene) => scene.Scene.Nodes.Where(n => n.Mesh is not null).ToList();

    /// <summary>Mean sRGB luma of the 3×3 pixels around where <paramref name="world"/> (GL) lands in the frame.</summary>
    private static float Brightness(byte[] rgba, FlyCamera camera, Vector3 world)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), camera.GetViewProjection((float)Width / Height));
        var x = (int)(((clip.X / clip.W * 0.5f) + 0.5f) * Width);
        var y = (int)((0.5f - (clip.Y / clip.W * 0.5f)) * Height);
        Assert.InRange(x, 1, Width - 2);
        Assert.InRange(y, 1, Height - 2);
        var sum = 0f;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var o = (((y + dy) * Width) + x + dx) * 4;
                sum += (0.2126f * rgba[o]) + (0.7152f * rgba[o + 1]) + (0.0722f * rgba[o + 2]);
            }
        }

        return sum / 9f;
    }
}
