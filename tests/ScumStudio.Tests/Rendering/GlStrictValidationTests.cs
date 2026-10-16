using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.Gl;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Rendering.Targets;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// D03 (AMD cards crash as areas load; RTX cards do not): every path the Map draws while areas stream in, run on a debug
/// context with the driver's debug output at every severity. A debug context makes the driver check each call and report
/// undefined behaviour it would otherwise let through, which is what one vendor tolerates and another can crash on. The
/// renderer must leave no error, undefined behaviour or portability message.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class GlStrictValidationTests(ITestOutputHelper output)
{
    [GlFact]
    public void EveryPathTheMapDrawsWhileAreasStreamLeavesTheDriverNothingToReport()
    {
        using var context = OffscreenGlContext.Create(64, 64, debug: true);
        using var capture = GlDebugOutput.Capture(context.Gl);
        using var renderer = new SceneRenderer(context);
        output.WriteLine(renderer.Info.ToString());
        StreamAndDraw(renderer, frames: 4);
        Report(capture);
    }

    [GlFact]
    public void EverySamplerOfEveryDrawFindsACompleteTextureOfItsKind()
    {
        // Read back before each draw, so it holds on any GPU: a shadow sampler on a unit with no depth texture (shadows off:
        // the frames of Performance quality and of the studio-lit previews) is undefined behaviour the debug context reports.
        using var gl = GlHarness.Create(64, 64);
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        var draws = 0;
        gl.Renderer.BeforeDraw = () =>
        {
            draws++;
            problems.UnionWith(GlDrawValidator.Check(gl.Context.Gl));
        };
        StreamAndDraw(gl.Renderer, frames: 2);
        output.WriteLine($"{draws} draws checked");
        problems.ToList().ForEach(output.WriteLine);
        Assert.True(draws > 50, $"{draws} draws");
        Assert.Empty(problems);
    }

    /// <summary>
    /// What the Map does as the camera flies into a dense area, on <paramref name="renderer"/>: textures of every upload path
    /// (BC1, BC3, BC5 and BC7 chains of odd sizes down to 1x1, RGBA8 of an odd width), masked leaves with alpha to coverage,
    /// normal maps and gloss, ground with its close-up layers, the sea over its copy of the scene, glass, sun shadows, sky,
    /// grid and overlay, a backdrop and a level drawn into one 4-sample target, picks, a texture sharpened in place, meshes
    /// and textures freed between frames (the GPU cache's trim), a resize, and the copy to the host's framebuffer.
    /// </summary>
    internal static void StreamAndDraw(SceneRenderer renderer, int frames)
    {
        var textures = new List<GpuTexture>();
        GpuTexture Keep(GpuTexture texture)
        {
            textures.Add(texture);
            return texture;
        }

        var bc1 = Keep(Compressed(renderer, CompressedFormat.Bc1, 125, 75, srgb: true));
        var bc3 = Keep(Compressed(renderer, CompressedFormat.Bc3, 64, 32, srgb: true));
        var bc5 = Keep(Compressed(renderer, CompressedFormat.Bc5, 30, 2, srgb: false));
        var bc7 = Keep(Compressed(renderer, CompressedFormat.Bc7, 6, 6, srgb: true));
        var odd = Keep(renderer.CreateTexture(3, 5, Enumerable.Repeat((byte)200, 3 * 5 * 4).ToArray()));
        var (leafRgba, _) = FoliageAntiAliasingTests.LeafTexture(40);
        var leaf = Keep(renderer.CreateTexture(256, 256, leafRgba));
        var weights = Keep(renderer.CreateTexture(5, 5, Enumerable.Repeat((byte)64, 5 * 5 * 4).ToArray(), srgb: false, TextureWrap.ClampToEdge));
        var nhr = Keep(renderer.CreateTexture(4, 4, Enumerable.Repeat((byte)128, 4 * 4 * 4).ToArray(), srgb: false));

        var backdrop = new Scene();
        var level = new Scene();
        var ground = renderer.AddMesh(PreparedMesh.From(PrimitiveMeshes.Plane(20_000f), MeshSpace.Gl), bc1);
        renderer.SetMeshTerrainDetail(ground, new TerrainDetailTextures(weights, [bc1, odd, null, bc7], new Vector4(0.01f, 0.02f, 0f, 0.005f),
            [new Vector3(0.3f), new Vector3(0.4f), Vector3.Zero, new Vector3(0.2f)], [nhr, null, null, null]));
        backdrop.Add(ground, Matrix4x4.CreateTranslation(0f, -10f, 0f), 0, "ground");
        var sea = renderer.AddMesh(PrimitiveMeshes.Plane(40_000f), MeshSpace.Gl);
        renderer.Meshes[sea.Id].Water = true;
        backdrop.Add(sea, Matrix4x4.CreateTranslation(0f, -5f, 0f), 0, "sea").Tint = new Vector4(0.035f, 0.12f, 0.17f, 0.75f);

        var box = renderer.AddMesh(PreparedMesh.From(PrimitiveMeshes.Cube(200f), MeshSpace.Gl), bc3);
        renderer.Meshes[box.Id].NormalMap = bc5;
        renderer.Meshes[box.Id].Roughness = new Vector2(0.3f, 0.8f);
        var card = renderer.AddMesh(PreparedMesh.From(PrimitiveMeshes.Plane(300f), MeshSpace.Gl), leaf, null, new Dictionary<string, float> { [string.Empty] = 0.33f });
        var glass = renderer.AddMesh(PrimitiveMeshes.Cube(150f), MeshSpace.Gl);
        var icon = renderer.AddMesh(PreparedMesh.From(PrimitiveMeshes.Plane(100f), MeshSpace.Gl), odd, billboard: true, shimmer: true);
        var id = 1u;
        for (var x = -3; x <= 3; x++)
        {
            for (var z = -3; z <= 3; z++)
            {
                var node = level.Add((x + z) % 2 == 0 ? box : card, Matrix4x4.CreateTranslation(x * 600f, 150f, z * 600f), id++, "piece");
                node.Selected = x == 0;
                node.Surface = z == 0 ? new Vector2(0.8f, 0.5f) : Vector2.Zero;
            }
        }

        level.Add(glass, Matrix4x4.CreateTranslation(0f, 400f, 0f), id++, "glass").Tint = new Vector4(0.6f, 0.8f, 1f, 0.4f);
        level.Add(icon, Matrix4x4.CreateTranslation(300f, 500f, 300f), id++, "icon").Tint = new Vector4(1f, 1f, 1f, 0.8f);

        renderer.Settings = new RenderSettings
        {
            Sky = true,
            Exposure = 1f,
            Shadows = true,
            ShadowDistance = 6000f,
            ShowGrid = true,
            FogDensity = 0.00002f,
            FogHeightFalloff = 0.0001f,
            BackfaceCulling = true,
            LightDirection = new Vector3(0.3f, -0.8f, 0.2f),
        };
        renderer.Overlay.Add(new OverlayLine(new Vector3(-500f, 100f, 0f), new Vector3(500f, 300f, 0f), new Vector4(1f, 0.5f, 0f, 1f), 3f, Next: new Vector3(800f, 300f, 400f)));
        renderer.OverlayTriangles.Add(new OverlayTriangle(Vector3.Zero, new Vector3(100f, 0f, 0f), new Vector3(0f, 100f, 0f), new Vector4(0f, 1f, 0f, 0.5f)));

        var camera = new FlyCamera { FieldOfView = 70f };
        camera.SetClipRange(10f, 3_000_000f);
        using var target = renderer.CreateTarget(96, 64, samples: 4);
        using var single = renderer.CreateTarget(40, 30);
        var gl = renderer.Context.Gl;
        var host = gl.GenFramebuffer();
        var hostColor = gl.GenTexture();
        gl.BindTexture(Silk.NET.OpenGL.TextureTarget.Texture2D, hostColor);
        gl.TexStorage2D(Silk.NET.OpenGL.TextureTarget.Texture2D, 1, Silk.NET.OpenGL.SizedInternalFormat.Rgba8, 120, 80);
        gl.BindTexture(Silk.NET.OpenGL.TextureTarget.Texture2D, 0);
        gl.BindFramebuffer(Silk.NET.OpenGL.FramebufferTarget.Framebuffer, host);
        gl.FramebufferTexture2D(Silk.NET.OpenGL.FramebufferTarget.Framebuffer, Silk.NET.OpenGL.FramebufferAttachment.ColorAttachment0, Silk.NET.OpenGL.TextureTarget.Texture2D, hostColor, 0);
        gl.BindFramebuffer(Silk.NET.OpenGL.FramebufferTarget.Framebuffer, 0);
        try
        {
            for (var frame = 0; frame < frames; frame++)
            {
                camera.Orbit(Vector3.Zero, 30f + (frame * 40f), -25f, 2500f);
                renderer.Time = frame * 0.3f;
                renderer.Render(target, backdrop, camera);
                renderer.Render(target, level, camera, clear: false);
                _ = renderer.Pick(target, level, camera, target.Width / 2, target.Height / 2, radius: 3);
                target.BlitTo(host, 120, 80);
                renderer.Render(single, level, camera);
                _ = single.ReadColorRgba();

                // Streaming: a level's meshes go, others come, a texture is sharpened in place, the target follows the window.
                var extra = renderer.AddMesh(PreparedMesh.From(PrimitiveMeshes.Cube(80f), MeshSpace.Gl), bc7);
                var node = level.Add(extra, Matrix4x4.CreateTranslation(0f, 800f, -frame * 100f), id++, "streamed");
                renderer.Render(target, level, camera, clear: false);
                level.Root.Remove(node);
                renderer.RemoveMesh(extra);
                bc1.TakeOver(Compressed(renderer, CompressedFormat.Bc1, 250, 150, srgb: true));
                target.Resize(96 + (frame * 8), 64 + (frame % 2));
                renderer.Settings = renderer.Settings with { Shadows = frame % 2 == 1, AnimateWater = frame % 3 != 0 }; // off in frame 1, 3, …
            }

            renderer.ResetState();
        }
        finally
        {
            gl.DeleteFramebuffer(host);
            gl.DeleteTexture(hostColor);
            textures.ForEach(t => t.Dispose());
        }
    }

    /// <summary>A full chain of <paramref name="format"/> mips from <paramref name="width"/> x <paramref name="height"/> down to 1x1, every block zero.</summary>
    internal static GpuTexture Compressed(SceneRenderer renderer, CompressedFormat format, int width, int height, bool srgb)
    {
        var mips = new List<byte[]>();
        for (var level = 0; ; level++)
        {
            var (w, h) = (Math.Max(1, width >> level), Math.Max(1, height >> level));
            mips.Add(new byte[GpuTexture.CompressedSize(format, w, h)]);
            if (w == 1 && h == 1)
            {
                break;
            }
        }

        return renderer.CreateCompressedTexture(format, width, height, mips, srgb) ?? throw new InvalidOperationException($"{format} refused");
    }

    private void Report(GlDebugCapture capture)
    {
        var messages = capture.Messages;
        output.WriteLine($"{messages.Count} driver messages, {capture.Problems.Count} problems");
        foreach (var group in messages.GroupBy(m => (m.Type, m.Severity, m.Id)).OrderByDescending(g => g.First().IsProblem))
        {
            output.WriteLine($"{group.Count(),4}x {group.First()}");
        }

        Assert.True(capture.Problems.Count == 0, string.Join(Environment.NewLine, capture.Problems.Distinct().Take(20)));
    }
}
