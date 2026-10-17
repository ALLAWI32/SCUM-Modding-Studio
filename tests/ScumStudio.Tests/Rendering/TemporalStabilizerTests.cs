using System.Numerics;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Rendering.Targets;
using Xunit;
using Xunit.Abstractions;
using static ScumStudio.Tests.Rendering.FoliageAntiAliasingTests;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// Owner: trees and bushes "shimmer" while the Map's camera moves, even with 4 samples and alpha to coverage. The
/// <see cref="TemporalStabilizer"/> blends each frame with the last one where the camera saw it: a slow pan over a forest of
/// masked leaf cards flickers less (<see cref="FrameFlicker"/>), a view at rest is drawn exactly as without it, and moving
/// edges and overlays do not trail. With <c>SCUMSTUDIO_SHOTS</c> set to a folder the pans are saved there as PNG frames.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class TemporalStabilizerTests(ITestOutputHelper output)
{
    private const int Size = 320;

    [GlFact]
    public void ASlowPanOverAForestFlickersLess()
    {
        using var gl = GlHarness.Create(Size, Size);
        using var target = gl.Renderer.CreateTarget(Size, Size, samples: 4); // the Map's target: 4 samples, alpha to coverage
        using var stabilizer = new TemporalStabilizer(gl.Renderer);
        var (scene, camera, region) = Forest(gl);
        var pixelCm = 2f * 900f * MathF.Tan(30f * MathF.PI / 180f) / Size;
        var start = camera.Position;
        List<byte[]> Pan(TemporalStabilizer? steady)
        {
            var frames = new List<byte[]>();
            steady?.Reset();
            for (var frame = 0; frame < 30; frame++)
            {
                camera.Position = start + (camera.Right * (pixelCm * 0.37f * frame)); // 0.37 px a frame: a slow, steady pan
                gl.Renderer.Render(target, scene, camera);
                steady?.Apply(target, camera);
                frames.Add(target.ReadColorRgba());
            }

            camera.Position = start;
            return frames;
        }

        var before = Pan(null);
        var after = Pan(stabilizer);
        var (x0, y0, x1, y1) = region;
        var flickerBefore = FrameFlicker.Measure(before, Size, Size, x0, y0, x1, y1);
        var flickerAfter = FrameFlicker.Measure(after, Size, Size, x0, y0, x1, y1);
        // What the frames try to show: the last frame's camera drawn 4 times larger with 4 samples and averaged down (64
        // samples a pixel). A steadied frame must be no further from it than the plain one: smoother, not blurred.
        camera.Position = start + (camera.Right * (pixelCm * 0.37f * 29));
        using var large = gl.Renderer.CreateTarget(Size * 4, Size * 4, samples: 4);
        gl.Renderer.Render(large, scene, camera);
        var truth = Downsample(large.ReadColorRgba(), 4);
        camera.Position = start;
        var (errorBefore, errorAfter) = (Error(before[^1], truth, region), Error(after[^1], truth, region));
        var (detailBefore, detailAfter, detailTruth) = (Detail(before[^1], region), Detail(after[^1], region), Detail(truth, region));
        output.WriteLine($"slow pan over 64 masked cards, {target.Samples} samples: flicker {flickerBefore:F5} without, {flickerAfter:F5} steadied ({flickerAfter / flickerBefore:P0}); "
            + $"off the 64-sample picture by {errorBefore:F4} without, {errorAfter:F4} steadied; detail (mean neighbour step) {detailBefore:F4} without, {detailAfter:F4} steadied, {detailTruth:F4} in the 64-sample picture");
        Save("forest-truth", [truth]);
        foreach (var feedback in new[] { 0.5f, 0.6f, 0.7f, 0.8f, 0.88f })
        {
            stabilizer.Feedback = feedback; // how the history's share trades steadiness for detail (printed, for choosing the default)
            var frames = Pan(stabilizer);
            output.WriteLine($"  feedback {feedback:0.00}: flicker {FrameFlicker.Measure(frames, Size, Size, x0, y0, x1, y1) / flickerBefore:P0} of the plain pan, detail {Detail(frames[^1], region) / detailBefore:P0} of the plain frame, off the 64-sample picture by {Error(frames[^1], truth, region):F4}");
        }

        stabilizer.Feedback = TemporalStabilizer.DefaultFeedback;
        Save("forest-pan-before", before);
        Save("forest-pan-after", after);
        Assert.True(flickerBefore > 0.001, $"the anti-aliased pan should still flicker a little ({flickerBefore:F5})");
        Assert.True(flickerAfter <= flickerBefore * 0.55, $"steadied {flickerAfter:F5} vs {flickerBefore:F5}");
        Assert.True(errorAfter <= errorBefore, $"the steadied frame is further from the 64-sample picture ({errorAfter:F4}) than the plain one ({errorBefore:F4})");
        Assert.True(detailAfter >= detailBefore * 0.6, $"the steadied forest is blurred: detail {detailAfter:F4}, the plain frame {detailBefore:F4}");
    }

    [GlFact]
    public void AViewAtRestIsDrawnExactly()
    {
        using var gl = GlHarness.Create(Size, Size);
        using var target = gl.Renderer.CreateTarget(Size, Size, samples: 4);
        using var stabilizer = new TemporalStabilizer(gl.Renderer);
        var (scene, camera, _) = Forest(gl);
        for (var frame = 0; frame < 12; frame++)
        {
            camera.Position += camera.Right * 3f;
            gl.Renderer.Render(target, scene, camera);
            stabilizer.Apply(target, camera);
        }

        var frames = 0;
        do
        {
            gl.Renderer.Render(target, scene, camera);
            stabilizer.Apply(target, camera);
            frames++;
        }
        while (stabilizer.Settling && frames < 20);

        var steadied = target.ReadColorRgba();
        gl.Renderer.Render(target, scene, camera);
        var plain = target.ReadColorRgba();
        var worst = steadied.Select((b, i) => Math.Abs(b - plain[i])).Max();
        output.WriteLine($"settled after {frames} still frames (last feedback {stabilizer.LastFeedback}); largest difference from the plain frame {worst}/255");
        Assert.Equal(TemporalStabilizer.SettleFrames, frames);
        Assert.True(worst <= 1, $"a view at rest differs from the plain frame by {worst}/255");
    }

    [GlFact]
    public void MovingEdgesAndOverlaysDoNotTrail()
    {
        // Cubes with hard edges on the sky and an overlay line, the camera sliding fast (several pixels a frame): the
        // steadied frame stays the frame drawn, edges included, and the overlay is drawn after it, sharp.
        foreach (var samples in new[] { 1, 4 })
        {
            using var gl = GlHarness.Create(Size, Size);
            using var target = gl.Renderer.CreateTarget(Size, Size, samples);
            using var stabilizer = new TemporalStabilizer(gl.Renderer);
            var grid = CubeGridScene.Build(gl.Renderer.AddMesh(PrimitiveMeshes.Cube(100f), MeshSpace.Gl), 6, 6);
            var camera = new FlyCamera { FieldOfView = 60f };
            grid.FrameCamera(camera);
            gl.Renderer.Settings = new RenderSettings { LightDirection = new Vector3(0.3f, -1f, 0.2f), ShowGrid = false, ClearColor = new Vector4(0.7f, 0.8f, 0.9f, 1f) };
            var (sum, worstMean, worstLine, n) = (0.0, 0.0, 0, 0);
            for (var frame = 0; frame < 16; frame++)
            {
                camera.Position += camera.Right * 25f;
                gl.Renderer.Overlay.Clear();
                gl.Renderer.Overlay.Add(new OverlayLine(camera.Position + (camera.Forward * 500f) - (camera.Right * 100f), camera.Position + (camera.Forward * 500f) + (camera.Right * 100f), new Vector4(1f, 0.5f, 0f, 1f), 3f));
                gl.Renderer.Render(target, grid.Scene, camera);
                stabilizer.Apply(target, camera);
                var steadied = target.ReadColorRgba();
                gl.Renderer.Render(target, grid.Scene, camera);
                var plain = target.ReadColorRgba();
                var mean = steadied.Select((b, i) => (double)Math.Abs(b - plain[i])).Sum() / steadied.Length;
                (sum, worstMean, n) = (sum + mean, Math.Max(worstMean, mean), n + 1);
                var (lx, ly) = (Size / 2, Size / 2);
                worstLine = Math.Max(worstLine, Enumerable.Range(0, 3).Max(c => Math.Abs(steadied[(((ly * Size) + lx) * 4) + c] - plain[(((ly * Size) + lx) * 4) + c])));
            }

            output.WriteLine($"{target.Samples} sample(s): steadied vs plain frame of the same camera, mean |difference| {sum / n:F3}/255 (worst frame {worstMean:F3}); overlay line centre off by {worstLine}/255");
            Assert.True(worstMean < 1.5, $"{samples} sample(s): moving edges trail ({worstMean:F3}/255 on average in a frame)");
            Assert.True(worstLine <= 2, $"{samples} sample(s): the overlay line is not drawn sharp over the steadied frame ({worstLine}/255)");
        }
    }

    [Fact]
    public void ReprojectionOfAStillCameraIsTheIdentity()
    {
        var camera = new FlyCamera { Position = new Vector3(450_000f, 3_000f, -440_000f), Yaw = 37f, Pitch = -12f }; // island coordinates
        camera.SetClipRange(10f, 3_000_000f);
        var view = new TemporalStabilizer.View(camera.Position, camera.Forward, camera.Up, camera.GetReverseZProjection(16f / 9f));
        var m = TemporalStabilizer.Reprojection(view, view);
        foreach (var ndc in new[] { new Vector4(0.3f, -0.7f, 0.001f, 1f), new Vector4(-0.9f, 0.9f, 0.5f, 1f), new Vector4(0f, 0f, 0.00001f, 1f) })
        {
            var back = Vector4.Transform(ndc, m);
            var (x, y) = (back.X / back.W, back.Y / back.W);
            Assert.True(MathF.Abs(x - ndc.X) < 1e-4f && MathF.Abs(y - ndc.Y) < 1e-4f, $"{ndc} came back as ({x}, {y})");
        }

        // A step right moves what is ahead to the left on screen.
        var moved = view with { Position = view.Position + (camera.Right * 100f) };
        var shifted = Vector4.Transform(new Vector4(0f, 0f, 0.01f, 1f), TemporalStabilizer.Reprojection(moved, view));
        Assert.True(shifted.X / shifted.W > 0f, "a point ahead was further right a frame before the camera stepped right");
    }

    /// <summary>64 masked leaf cards of 1 m at 9 m (leaf strokes a fraction of a pixel), lit by day, and the screen rectangle they fill.</summary>
    private static (Scene Scene, FlyCamera Camera, (int X0, int Y0, int X1, int Y1) Region) Forest(GlHarness gl)
    {
        var (rgba, _) = LeafTexture(strokes: 700);
        var leaves = gl.Renderer.CreateTexture(256, 256, rgba);
        var camera = LookingAtOrigin(900f);
        var card = Masked(gl, FacingQuad(camera, Vector3.Zero, 100f), leaves);
        var scene = new Scene();
        for (var i = 0; i < 64; i++)
        {
            var offset = (camera.Right * (((i % 8) - 3.5f) * 110f)) + (camera.Up * (((i / 8) - 3.5f) * 110f));
            scene.Add(card, Matrix4x4.CreateTranslation(offset), (uint)(i + 1));
        }

        gl.Renderer.Settings = new RenderSettings
        {
            SkyColor = new Vector3(0.5f),
            GroundColor = new Vector3(0.3f),
            LightColor = Vector3.One,
            LightDirection = new Vector3(0.3f, -1f, 0.2f),
            ShowGrid = false,
            ClearColor = new Vector4(0.7f, 0.8f, 0.9f, 1f),
        };
        var (x0, y0) = ToPixel(camera, (-camera.Right + camera.Up) * 400f, gl.Target);
        var (x1, y1) = ToPixel(camera, (camera.Right - camera.Up) * 400f, gl.Target);
        return (scene, camera, (x0, y0, x1, y1));
    }

    /// <summary>The mean luminance step between horizontal neighbours inside <paramref name="region"/>: how much detail a frame keeps.</summary>
    private static double Detail(byte[] image, (int X0, int Y0, int X1, int Y1) region)
    {
        var (sum, n) = (0.0, 0);
        for (var y = region.Y0; y < region.Y1; y++)
        {
            for (var x = region.X0; x < region.X1 - 1; x++)
            {
                var o = ((y * Size) + x) * 4;
                sum += Math.Abs(FrameFlicker.Luminance(image, o + 4) - FrameFlicker.Luminance(image, o));
                n++;
            }
        }

        return sum / Math.Max(1, n);
    }

    /// <summary>An RGBA8 picture <paramref name="factor"/> times <see cref="Size"/> square averaged down to <see cref="Size"/>.</summary>
    private static byte[] Downsample(byte[] large, int factor)
    {
        var small = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var sum = 0;
                    for (var dy = 0; dy < factor; dy++)
                    {
                        for (var dx = 0; dx < factor; dx++)
                        {
                            sum += large[((((y * factor) + dy) * Size * factor) + (x * factor) + dx) * 4 + c];
                        }
                    }

                    small[(((y * Size) + x) * 4) + c] = (byte)((sum + (factor * factor / 2)) / (factor * factor));
                }
            }
        }

        return small;
    }

    /// <summary>The mean luminance difference between two frames inside <paramref name="region"/>.</summary>
    private static double Error(byte[] image, byte[] truth, (int X0, int Y0, int X1, int Y1) region)
    {
        var (sum, n) = (0.0, 0);
        for (var y = region.Y0; y < region.Y1; y++)
        {
            for (var x = region.X0; x < region.X1; x++)
            {
                var o = ((y * Size) + x) * 4;
                sum += Math.Abs(FrameFlicker.Luminance(image, o) - FrameFlicker.Luminance(truth, o));
                n++;
            }
        }

        return sum / Math.Max(1, n);
    }

    private void Save(string name, List<byte[]> frames)
    {
        if (Environment.GetEnvironmentVariable("SCUMSTUDIO_SHOTS") is not { Length: > 0 } folder)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        for (var i = 0; i < frames.Count; i += 6)
        {
            ImageExport.SavePngAsync(frames[i], Size, Size, Path.Combine(folder, $"{name}-{i:00}.png")).GetAwaiter().GetResult();
        }

        output.WriteLine($"saved {name} frames to {folder}");
    }
}
