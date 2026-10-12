using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Rendering.Targets;
using ScumStudio.Viewport;
using Xunit;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// Owner: "no shimmering/noise in the trees". Masked leaves on synthetic cards (strokes two texels wide on alpha 0, mipmapped
/// by the driver like UE's averaged mips): the ID and shadow passes cut them as the colour pass does, two-sided cards shade
/// without a line that moves with the camera, and the anti-aliased target keeps far canopies steady and as dense as near ones.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class FoliageAntiAliasingTests(ITestOutputHelper output)
{
    private const float Cutoff = 0.3333f;
    private const int LeafSize = 256;

    [GlFact]
    public void PickSeesThroughMaskedTexels()
    {
        // Audit F261: the ID pass drew the whole card, so a click between leaves (and the brush's ground hit under a tree)
        // took the tree instead of what is seen through it.
        using var gl = GlHarness.Create(200, 200);
        gl.UseUnlit();
        var halfOpaque = new byte[LeafSize * LeafSize * 4];
        for (var i = 0; i < LeafSize * LeafSize; i++)
        {
            halfOpaque[(i * 4) + 1] = 160;
            halfOpaque[(i * 4) + 3] = (byte)(i % LeafSize < LeafSize / 2 ? 255 : 0); // left half opaque
        }

        using var texture = gl.Renderer.CreateTexture(LeafSize, LeafSize, halfOpaque);
        var camera = LookingAtOrigin(1000f);
        var scene = new Scene();
        scene.Add(gl.Renderer.AddMesh(PrimitiveMeshes.Cube(3000f), MeshSpace.Gl), Matrix4x4.CreateTranslation(camera.Forward * 3000f), 2, "box");
        scene.Add(Masked(gl, FacingQuad(camera, Vector3.Zero, 600f), texture), Matrix4x4.Identity, 1, "leaves");

        var leaf = ToPixel(camera, -camera.Right * 150f, gl.Target);
        var gap = ToPixel(camera, camera.Right * 150f, gl.Target);
        Assert.Equal(1u, gl.Renderer.Pick(gl.Target, scene, camera, leaf.X, leaf.Y)?.InstanceId);
        Assert.Equal(2u, gl.Renderer.Pick(gl.Target, scene, camera, gap.X, gap.Y)?.InstanceId);
    }

    [GlFact]
    public void MaskedCastersShadeLikeTheyAreDrawn()
    {
        // Audit F424: the shadow pass cut leaves by the raw alpha of its own far mip, where leaves and gaps average below the
        // cutoff, while the colour pass raises that alpha with the mip level: a distant canopy was drawn but cast no shade.
        using var gl = GlHarness.Create(128, 128);
        var rgba = new byte[LeafSize * LeafSize * 4];
        for (var i = 0; i < LeafSize * LeafSize; i++)
        {
            (rgba[(i * 4) + 1], rgba[(i * 4) + 3]) = ((byte)110, (byte)((i / LeafSize) % 8 < 2 ? 255 : 0)); // 2 rows of 8: 25 % leaves
        }

        using var leaves = gl.Renderer.CreateTexture(LeafSize, LeafSize, rgba);
        var scene = new Scene();
        scene.Add(gl.Renderer.AddMesh(PrimitiveMeshes.Plane(40_000f), MeshSpace.Gl), Matrix4x4.Identity, 1, "ground");
        // A level 4 m card 3 m up; the 400 m shadow square has 19.5 cm texels, so the card spans 20 of them (mip ~3.6).
        scene.Add(Masked(gl, PrimitiveMeshes.Plane(400f), leaves), Matrix4x4.CreateTranslation(0f, 300f, 0f), 2, "canopy");
        var camera = new FlyCamera();
        camera.SetClipRange(10f, 10_000f);
        camera.Orbit(new Vector3(300f, 0f, 0f), 0f, -89f, 1500f); // straight down at the card's shadow (sun from 45° in -X)
        gl.Renderer.Settings = new RenderSettings
        {
            SkyColor = new Vector3(0.2f),
            GroundColor = new Vector3(0.2f),
            LightColor = Vector3.One,
            LightDirection = new Vector3(1f, -1f, 0f),
            ShowGrid = false,
            Shadows = true,
            ShadowDistance = 20_000f,
        };

        gl.Renderer.Render(gl.Target, scene, camera);
        var image = gl.Target.ReadColorRgba();
        var under = ToPixel(camera, new Vector3(300f, 0f, 0f), gl.Target);
        var open = ToPixel(camera, new Vector3(300f, 0f, 600f), gl.Target);
        var shaded = gl.Pixel(image, under.X, under.Y)[1];
        var lit = gl.Pixel(image, open.X, open.Y)[1];
        output.WriteLine($"ground under the card {shaded}, open ground {lit}");
        Assert.True(shaded < lit - 60, $"ground under the card {shaded} vs open ground {lit}");
    }

    [GlFact]
    public void TwoSidedLightingIsContinuousAcrossABentNormalCard()
    {
        // Leaf cards carry normals bent away from their face. The two-sided flip was decided per pixel by the direction to
        // the camera, so where that crossed the bent normal the light jumped along a line that moved with the camera.
        using var gl = GlHarness.Create(200, 200);
        var camera = LookingAtOrigin(300f);
        var bent = Vector3.Normalize((camera.Right * 0.94f) - (camera.Forward * 0.34f)); // 70° off the face normal, toward +right
        var scene = new Scene();
        var card = scene.Add(gl.Renderer.AddMesh(FacingQuad(camera, Vector3.Zero, 300f, bent), MeshSpace.Gl), Matrix4x4.Identity, 1, "card");
        card.Tint = new Vector4(0.5f, 0.5f, 0.5f, 1f);
        gl.Renderer.Settings = new RenderSettings
        {
            SkyColor = new Vector3(0.1f),
            GroundColor = new Vector3(0.1f),
            LightColor = Vector3.One,
            LightDirection = -bent,
            ShowGrid = false,
            ClearColor = Vector4.Zero,
        };

        gl.Renderer.Render(gl.Target, scene, camera);
        var image = gl.Target.ReadColorRgba();
        var (x0, y0) = ToPixel(camera, (-camera.Right + camera.Up) * 150f, gl.Target);
        var (x1, y1) = ToPixel(camera, (camera.Right - camera.Up) * 150f, gl.Target);
        var step = 0.0;
        var at = 0;
        for (var y = y0 + 3; y <= y1 - 3; y++)
        {
            for (var x = x0 + 3; x < x1 - 3; x++)
            {
                var d = Math.Abs(Luminance(gl.Pixel(image, x + 1, y)) - Luminance(gl.Pixel(image, x, y)));
                (step, at) = d > step ? (d, x) : (step, at);
            }
        }

        output.WriteLine($"card {x0}..{x1} x {y0}..{y1}: largest step between neighbours {step:F3} at column {at}");
        Assert.True(step < 0.1, $"the shading jumps by {step:F3} at column {at} inside the card");
    }

    [GlFact]
    public void TwoSidedCardsShadeTheirFrontAndBackAsInTheGame()
    {
        // Grass and canopy cards often carry normals that point up (or out of the tree), not along their face. UE shades the
        // front of a two-sided triangle (by its winding) with the normal as it is and the back with the normal turned around
        // (TwoSidedSign), whichever way the card leans; a mirrored instance keeps its front.
        using var gl = GlHarness.Create(200, 200);
        var camera = LookingAtOrigin(600f);
        var tilt = Quaternion.CreateFromAxisAngle(camera.Right, 50f * MathF.PI / 180f); // top toward the camera: the front leans down
        var (r, u) = (camera.Right * 150f, Vector3.Transform(camera.Up, tilt) * 150f);
        Vector3[] corners = [-r - u, r - u, r + u, -r + u];
        var card = UpNormalCard(corners, [0u, 1u, 2u, 0u, 2u, 3u]);
        var mirrored = UpNormalCard([.. corners.Select(c => c with { X = -c.X })], [0u, 2u, 1u, 0u, 3u, 2u]);
        gl.Renderer.Settings = new RenderSettings
        {
            SkyColor = new Vector3(0.1f),
            GroundColor = new Vector3(0.1f),
            LightColor = Vector3.One,
            LightDirection = -Vector3.UnitY,
            ShowGrid = false,
            ClearColor = Vector4.Zero,
        };

        double Centre(MeshData mesh, Matrix4x4 transform, FlyCamera from)
        {
            var scene = new Scene();
            scene.Add(gl.Renderer.AddMesh(mesh, MeshSpace.Gl), transform, 1).Tint = new Vector4(0.5f, 0.5f, 0.5f, 1f);
            gl.Renderer.Render(gl.Target, scene, from);
            var (x, y) = ToPixel(from, Vector3.Zero, gl.Target);
            return Luminance(gl.Pixel(gl.Target.ReadColorRgba(), x, y));
        }

        var behind = new FlyCamera { FieldOfView = 60f };
        behind.SetClipRange(10f, 20_000f);
        behind.Orbit(Vector3.Zero, 210f, -10f, 600f);
        var front = Centre(card, Matrix4x4.Identity, camera);
        var mirror = Centre(mirrored, Matrix4x4.CreateScale(-1f, 1f, 1f), camera);
        var back = Centre(card, Matrix4x4.Identity, behind);
        output.WriteLine($"up-normal card under the sun: front {front:F3}, mirrored instance {mirror:F3}, back {back:F3}");
        Assert.True(front > 0.6, $"the front keeps its up normal and the sun ({front:F3})");
        Assert.True(Math.Abs(mirror - front) < 0.02, $"a mirrored instance keeps its front ({mirror:F3} vs {front:F3})");
        Assert.True(back < 0.4, $"the back is shaded with the normal turned around ({back:F3})");
    }

    [GlFact]
    public void ShapesWoundAgainstTheirNormalsKeepTheirLitSide()
    {
        // The studio's spawn shapes are wound against their normals (built counter-clockwise in UE's axes, which the swap to
        // the GL world mirrors): taken by its winding alone, every face seen from outside would be a back face and turn dark.
        // They shade as their twin wound the other way.
        using var gl = GlHarness.Create(160, 160);
        var crate = SpawnMarkers.Asset(SpawnKind.Loot).Mesh;
        var rewound = MeshData.Create(crate.Name, crate.Positions, [.. crate.Indices.Select((_, i) => crate.Indices[i - (i % 3) + ((3 - (i % 3)) % 3)])], crate.Normals);
        gl.Renderer.Settings = Daylight();
        byte[] Frame(MeshData mesh)
        {
            var handle = gl.Renderer.AddMesh(mesh, MeshSpace.Unreal);
            var scene = new Scene();
            scene.Add(handle, Matrix4x4.Identity, 1);
            var camera = new FlyCamera { FieldOfView = 60f };
            camera.SetClipRange(1f, 10_000f);
            camera.Orbit(handle.Bounds.Center, 30f, -35f, handle.Bounds.Size.Length() * 1.5f);
            gl.Renderer.Render(gl.Target, scene, camera);
            return gl.Target.ReadColorRgba();
        }

        var (shape, twin) = (Frame(crate), Frame(rewound));
        var worst = shape.Select((b, i) => Math.Abs(b - twin[i])).Max();
        output.WriteLine($"spawn crate vs its rewound twin: largest difference {worst}/255");
        Assert.True(worst <= 2, $"the spawn crate shades unlike its twin ({worst}/255)");
    }

    [GlFact]
    public void AStaleGlErrorDoesNotTurnAntiAliasingOff()
    {
        // The viewport shares Avalonia's GL context: an error other code left there made the multisampled target fall back
        // to one sample for the whole session.
        using var gl = GlHarness.Create(32, 32);
        gl.Context.Gl.Enable((Silk.NET.OpenGL.EnableCap)0); // GL_INVALID_ENUM left behind
        using var target = gl.Renderer.CreateTarget(32, 32, samples: 4);
        Assert.Equal(4, target.Samples);
    }

    [GlFact]
    public void AntiAliasedFoliageIsStableUnderSubPixelCameraMoves()
    {
        // 64 cards of 1 m at 9 m, about 31 px each: the leaf strokes are a fraction of a pixel (mip ~3). The camera slides
        // an eighth of a pixel sideways per frame; the cut-out cards sparkle, the anti-aliased ones should hardly change.
        const int size = 320;
        using var gl = GlHarness.Create(size, size);
        using var smooth = gl.Renderer.CreateTarget(size, size, samples: 4);
        var (rgba, coverage) = LeafTexture(strokes: 700);
        using var leaves = gl.Renderer.CreateTexture(LeafSize, LeafSize, rgba);
        var camera = LookingAtOrigin(900f);
        var card = Masked(gl, FacingQuad(camera, Vector3.Zero, 100f), leaves);
        var scene = new Scene();
        for (var i = 0; i < 64; i++)
        {
            var offset = (camera.Right * (((i % 8) - 3.5f) * 110f)) + (camera.Up * (((i / 8) - 3.5f) * 110f));
            scene.Add(card, Matrix4x4.CreateTranslation(offset), (uint)(i + 1));
        }

        gl.Renderer.Settings = Daylight();
        var (x0, y0) = ToPixel(camera, (-camera.Right + camera.Up) * 450f, gl.Target);
        var (x1, y1) = ToPixel(camera, (camera.Right - camera.Up) * 450f, gl.Target);
        var pixelCm = 2f * 900f * MathF.Tan(30f * MathF.PI / 180f) / size;
        var start = camera.Position;
        double Instability(RenderTarget target)
        {
            byte[]? previous = null;
            var sum = 0.0;
            for (var frame = 0; frame < 8; frame++)
            {
                camera.Position = start + (camera.Right * (pixelCm * frame / 8f));
                gl.Renderer.Render(target, scene, camera);
                var image = target.ReadColorRgba();
                if (previous is not null)
                {
                    sum += MeanDelta(previous, image, size, x0, y0, x1, y1);
                }

                previous = image;
            }

            camera.Position = start;
            return sum / 7.0;
        }

        var sharp = Instability(gl.Target);
        var antiAliased = Instability(smooth);
        output.WriteLine($"leaf coverage {coverage:P1}, {smooth.Samples} samples; mean |delta luminance| per 1/8 px move: 1 sample {sharp:F4}, {smooth.Samples} samples {antiAliased:F4} ({antiAliased / sharp:P0})");
        Assert.True(sharp > 0.002, $"the 1-sample frames should flicker ({sharp:F4})");
        Assert.True(antiAliased <= sharp * 0.6, $"anti-aliased {antiAliased:F4} vs 1 sample {sharp:F4}");
    }

    [GlFact]
    public void FarFoliageKeepsItsCoverage()
    {
        // The share of a card its leaves cover, 512 px tall (mip 0) and 24 px tall (mip ~3.4). Cut by the raised far-mip
        // alpha, a distant card fills up (or, with a sparser texture, empties); anti-aliased it keeps the near density.
        const int size = 600;
        using var gl = GlHarness.Create(size, size);
        using var smooth = gl.Renderer.CreateTarget(size, size, samples: 4);
        var (rgba, coverage) = LeafTexture(strokes: 700);
        using var leaves = gl.Renderer.CreateTexture(LeafSize, LeafSize, rgba);
        var camera = LookingAtOrigin(1000f);
        camera.SetClipRange(10f, 100_000f);
        var scene = new Scene();
        var node = scene.Add(Masked(gl, FacingQuad(camera, Vector3.Zero, 1000f), leaves), Matrix4x4.Identity, 1);
        gl.Renderer.Settings = new RenderSettings { Lighting = false, ShowGrid = false, ClearColor = Vector4.One };
        var pixelCm = 2f * MathF.Tan(30f * MathF.PI / 180f) / size; // per cm of distance
        double Share(RenderTarget target, float pixelsTall)
        {
            var distance = 1000f / (pixelsTall * pixelCm);
            node.LocalTransform = Matrix4x4.CreateTranslation(camera.Forward * (distance - 1000f));
            gl.Renderer.Render(target, scene, camera);
            var image = target.ReadColorRgba();
            var centre = camera.Forward * (distance - 1000f);
            var (x0, y0) = ToPixel(camera, centre + ((-camera.Right + camera.Up) * 500f), target);
            var (x1, y1) = ToPixel(camera, centre + ((camera.Right - camera.Up) * 500f), target);
            var (sum, n) = (0.0, 0);
            for (var y = y0 + 1; y < y1 - 1; y++)
            {
                for (var x = x0 + 1; x < x1 - 1; x++)
                {
                    sum += (255.0 - image[(((y * size) + x) * 4)]) / (255.0 - 40.0); // red: 255 open, 40 leaf
                    n++;
                }
            }

            return sum / n;
        }

        var (near1, far1) = (Share(gl.Target, 512f), Share(gl.Target, 24f));
        var (near4, far4) = (Share(smooth, 512f), Share(smooth, 24f));
        output.WriteLine($"leaf coverage {coverage:P1}; covered share near/far: 1 sample {near1:P1}/{far1:P1}, {smooth.Samples} samples {near4:P1}/{far4:P1}");
        Assert.InRange(far4, near4 * 0.8, near4 * 1.2);

        // In between, where the cut edge fades into the mip's share (mips 1 to 3), the density holds too.
        foreach (var tall in new[] { 128f, 80f, 48f })
        {
            var (single, multi) = (Share(gl.Target, tall), Share(smooth, tall));
            output.WriteLine($"{tall} px tall: 1 sample {single:P1}, {smooth.Samples} samples {multi:P1}");
            Assert.InRange(multi, near4 * 0.8, near4 * 1.2);
        }
    }

    [GlFact]
    public void PassesWorkOnAMultisampledTarget()
    {
        // The sky, grid, overlay, the sea over its copy of the scene, the ID buffer and the copy to the host's framebuffer
        // read the same from a 4-sample target as from a single-sample one, edges aside.
        const int size = 128;
        using var gl = GlHarness.Create(size, size);
        using var smooth = gl.Renderer.CreateTarget(size, size, samples: 4);
        Assert.Equal(4, smooth.Samples);
        using (var odd = gl.Renderer.CreateTarget(16, 16, samples: 3))
        {
            Assert.Equal(2, odd.Samples); // rounded down to a power of two
            Assert.True(odd.Resize(16, 16, 1));
            Assert.Equal(1, odd.Samples);
            Assert.False(odd.Resize(16, 16, 1));
        }

        var camera = new FlyCamera { FieldOfView = 60f };
        camera.SetClipRange(10f, 100_000f);
        camera.Orbit(Vector3.Zero, 20f, -15f, 2000f);
        gl.Renderer.Settings = new RenderSettings { Sky = true, Exposure = 1f, ShowGrid = true, LightDirection = new Vector3(0.3f, -0.6f, 0.4f) };
        gl.Renderer.Overlay.Add(new OverlayLine(new Vector3(-500f, 100f, 0f), new Vector3(500f, 300f, 0f), new Vector4(1f, 0.5f, 0f, 1f), 3f));
        var empty = new Scene();
        AssertSameAwayFromEdges("sky, grid and overlay", Frame(gl.Target, empty), Frame(smooth, empty));
        gl.Renderer.Overlay.Clear();

        using var white = gl.Renderer.CreateTexture(1, 1, [255, 255, 255, 255]);
        var sea = new Scene();
        var bed = sea.Add(gl.Renderer.AddMesh(PrimitiveMeshes.Plane(4000f), MeshSpace.Gl, 1f, white), Matrix4x4.CreateTranslation(0f, -20f, 0f), 1);
        var water = gl.Renderer.AddMesh(PrimitiveMeshes.Plane(4000f), MeshSpace.Gl);
        gl.Renderer.Meshes[water.Id].Water = true;
        sea.Add(water, Matrix4x4.Identity, 0).Tint = new Vector4(0.035f, 0.12f, 0.17f, 0.75f);
        camera.Orbit(Vector3.Zero, 0f, -89f, 300f);
        gl.Renderer.Settings = new RenderSettings { SkyColor = new Vector3(0.5f), GroundColor = new Vector3(0.5f), LightColor = Vector3.One, LightDirection = new Vector3(0.3f, -1f, 0.2f), ShowGrid = false };
        AssertSameAwayFromEdges("shallow water", Frame(gl.Target, sea), Frame(smooth, sea));
        bed.LocalTransform = Matrix4x4.CreateTranslation(0f, -5000f, 0f);
        AssertSameAwayFromEdges("deep water", Frame(gl.Target, sea), Frame(smooth, sea));

        var grid = CubeGridScene.Build(gl.Renderer.AddMesh(PrimitiveMeshes.Cube(100f), MeshSpace.Gl), 8, 8);
        grid.FrameCamera(camera);
        gl.Renderer.Settings = new RenderSettings { LightDirection = new Vector3(0.3f, -1f, 0.2f), ShowGrid = false };
        AssertSameAwayFromEdges("cubes", Frame(gl.Target, grid.Scene), Frame(smooth, grid.Scene));
        for (var y = 4; y < size; y += 8)
        {
            for (var x = 4; x < size; x += 8)
            {
                Assert.Equal(gl.Renderer.Pick(gl.Target, grid.Scene, camera, x, y)?.InstanceId, gl.Renderer.Pick(smooth, grid.Scene, camera, x, y)?.InstanceId);
            }
        }

        // The host's copy (BlitTo) shows the resolved frame, as read back.
        var expected = Frame(smooth, grid.Scene);
        var api = gl.Context.Gl;
        var texture = api.GenTexture();
        api.BindTexture(Silk.NET.OpenGL.TextureTarget.Texture2D, texture);
        api.TexStorage2D(Silk.NET.OpenGL.TextureTarget.Texture2D, 1, Silk.NET.OpenGL.SizedInternalFormat.Rgba8, size, size);
        var host = api.GenFramebuffer();
        api.BindFramebuffer(Silk.NET.OpenGL.FramebufferTarget.Framebuffer, host);
        api.FramebufferTexture2D(Silk.NET.OpenGL.FramebufferTarget.Framebuffer, Silk.NET.OpenGL.FramebufferAttachment.ColorAttachment0, Silk.NET.OpenGL.TextureTarget.Texture2D, texture, 0);
        gl.Renderer.Render(smooth, grid.Scene, camera);
        smooth.BlitTo(host, size, size);
        var bottomUp = new byte[size * size * 4];
        api.BindFramebuffer(Silk.NET.OpenGL.FramebufferTarget.ReadFramebuffer, host);
        api.PixelStore(Silk.NET.OpenGL.PixelStoreParameter.PackAlignment, 1);
        api.ReadPixels<byte>(0, 0, size, size, Silk.NET.OpenGL.PixelFormat.Rgba, Silk.NET.OpenGL.PixelType.UnsignedByte, out bottomUp[0]);
        api.PixelStore(Silk.NET.OpenGL.PixelStoreParameter.PackAlignment, 4);
        api.BindFramebuffer(Silk.NET.OpenGL.FramebufferTarget.Framebuffer, 0);
        api.DeleteFramebuffer(host);
        api.DeleteTexture(texture);
        for (var y = 0; y < size; y++)
        {
            Assert.True(bottomUp.AsSpan((size - 1 - y) * size * 4, size * 4).SequenceEqual(expected.AsSpan(y * size * 4, size * 4)), $"row {y} of the host copy");
        }

        byte[] Frame(RenderTarget target, Scene scene)
        {
            gl.Renderer.Render(target, scene, camera);
            return target.ReadColorRgba();
        }

        void AssertSameAwayFromEdges(string what, byte[] single, byte[] multi)
        {
            var (worst, at) = (0, -1);
            for (var i = 0; i < size * size; i++)
            {
                var (x, y) = (i % size, i / size);
                if (NearEdge(single, x, y) || NearEdge(multi, x, y))
                {
                    continue;
                }

                for (var c = 0; c < 3; c++)
                {
                    var d = Math.Abs(single[(i * 4) + c] - multi[(i * 4) + c]);
                    (worst, at) = d > worst ? (d, i) : (worst, at);
                }
            }

            output.WriteLine($"{what}: largest difference away from edges {worst}/255");
            Assert.True(worst <= 3, $"{what}: pixel ({at % size}, {at / size}) differs by {worst}/255");
        }

        static bool NearEdge(byte[] image, int x, int y)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var (nx, ny) = (Math.Clamp(x + dx, 0, size - 1), Math.Clamp(y + dy, 0, size - 1));
                    for (var c = 0; c < 3; c++)
                    {
                        if (Math.Abs(image[(((y * size) + x) * 4) + c] - image[(((ny * size) + nx) * 4) + c]) > 3)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }

    [GlFact]
    public void MsaaCostBenchmark()
    {
        // The cost that decides the default of Settings > "Anti-aliased foliage": 1080p, 20 000 masked cards (a forest
        // seen from inside it) and the 64 x 64 cube grid, 60 frames each, CPU time to the end of the GPU work.
        const int width = 1920, height = 1080;
        using var gl = GlHarness.Create(width, height);
        using var smooth = gl.Renderer.CreateTarget(width, height, samples: 4);
        var (rgba, _) = LeafTexture(strokes: 700);
        using var leaves = gl.Renderer.CreateTexture(LeafSize, LeafSize, rgba);
        var card = Masked(gl, PrimitiveMeshes.Plane(150f), leaves);
        var forest = new Scene();
        var random = new Random(3);
        for (var i = 0; i < 20_000; i++)
        {
            var (x, z) = ((i % 140) * 150f, (i / 140) * 150f);
            var turn = Matrix4x4.CreateRotationX((float)(random.NextDouble() * Math.PI)) * Matrix4x4.CreateRotationY((float)(random.NextDouble() * Math.PI));
            forest.Add(card, turn * Matrix4x4.CreateTranslation(x, 100f + (float)(random.NextDouble() * 600f), z), (uint)(i + 1));
        }

        var forestCamera = new FlyCamera { FieldOfView = 60f };
        forestCamera.SetClipRange(10f, 100_000f);
        forestCamera.Orbit(new Vector3(10_500f, 300f, 10_700f), 30f, -5f, 9_000f); // 90 m inside the 210 m forest, toward its middle
        var cubes = CubeGridScene.Build(gl.Renderer.AddMesh(PrimitiveMeshes.Cube(100f), MeshSpace.Gl), 64, 64);
        var cubeCamera = new FlyCamera();
        cubes.FrameCamera(cubeCamera);
        gl.Renderer.Settings = RenderSettings.Outdoor with { Shadows = true };
        output.WriteLine($"{gl.Renderer.Info}, up to {gl.Renderer.Info.MaxSamples} samples");
        foreach (var (name, scene, camera) in new[] { ("20k masked cards", forest, forestCamera), ("64 x 64 cubes", cubes.Scene, cubeCamera) })
        {
            var (single, multi) = Measure(scene, camera);
            var stats = gl.Renderer.LastStats;
            var image = smooth.ReadColorRgba();
            var sky = image.AsSpan(0, 4).ToArray();
            var covered = 0;
            for (var i = 0; i < image.Length; i += 4)
            {
                covered += Math.Abs(image[i] - sky[0]) + Math.Abs(image[i + 1] - sky[1]) + Math.Abs(image[i + 2] - sky[2]) > 30 ? 1 : 0;
            }

            output.WriteLine($"{name} ({stats.Instances} drawn, {stats.Triangles} triangles, {covered / (double)(width * height):P0} of the screen): "
                + $"1 sample {single.Average:F2} ms (median {single.Median:F2}, p95 {single.P95:F2}), {smooth.Samples} samples "
                + $"{multi.Average:F2} ms (median {multi.Median:F2}, p95 {multi.P95:F2}): {multi.Average - single.Average:+0.00;-0.00} ms average, "
                + $"{multi.Median - single.Median:+0.00;-0.00} ms median");
            Assert.True(stats.Instances > 0 && covered > 0, name);
        }

        // The two targets take turns frame by frame, so a spike of other work on the GPU weighs on both alike.
        ((double Average, double Median, double P95) Single, (double Average, double Median, double P95) Multi) Measure(Scene scene, FlyCamera camera)
        {
            var api = gl.Context.Gl;
            var times = new[] { new List<double>(), new List<double>() };
            RenderTarget[] targets = [gl.Target, smooth];
            api.Finish();
            for (var frame = 0; frame < 130; frame++)
            {
                var target = targets[frame % 2];
                var clock = System.Diagnostics.Stopwatch.StartNew();
                gl.Renderer.Render(target, scene, camera);
                target.Resolve();
                api.Finish();
                if (frame >= 10)
                {
                    times[frame % 2].Add(clock.Elapsed.TotalMilliseconds);
                }
            }

            return (Summary(times[0]), Summary(times[1]));

            static (double, double, double) Summary(List<double> t)
            {
                t.Sort();
                return (t.Average(), t[t.Count / 2], t[(int)(t.Count * 0.95)]);
            }
        }
    }

    private static RenderSettings Daylight() => new()
    {
        SkyColor = new Vector3(0.5f),
        GroundColor = new Vector3(0.3f),
        LightColor = Vector3.One,
        LightDirection = new Vector3(0.3f, -1f, 0.2f),
        ShowGrid = false,
        ClearColor = new Vector4(0.7f, 0.8f, 0.9f, 1f),
    };

    private static double MeanDelta(byte[] a, byte[] b, int width, int x0, int y0, int x1, int y1)
    {
        var (sum, n) = (0.0, 0);
        for (var y = Math.Max(0, y0); y < Math.Min(width, y1); y++)
        {
            for (var x = Math.Max(0, x0); x < Math.Min(width, x1); x++)
            {
                var o = ((y * width) + x) * 4;
                sum += Math.Abs(Luminance(a[o..(o + 4)]) - Luminance(b[o..(o + 4)]));
                n++;
            }
        }

        return sum / Math.Max(1, n);
    }

    /// <summary>
    /// A 256² leaf texture: <paramref name="strokes"/> random strokes two texels wide (alpha 1) on alpha 0, one green
    /// throughout (UE's cooked foliage carries its colour into the gaps); returns the opaque share too.
    /// </summary>
    internal static (byte[] Rgba, double Coverage) LeafTexture(int strokes, int seed = 7)
    {
        var rgba = new byte[LeafSize * LeafSize * 4];
        for (var i = 0; i < LeafSize * LeafSize; i++)
        {
            (rgba[i * 4], rgba[(i * 4) + 1], rgba[(i * 4) + 2]) = ((byte)40, (byte)110, (byte)30);
        }

        var random = new Random(seed);
        for (var s = 0; s < strokes; s++)
        {
            var (x, y) = (random.Next(LeafSize), random.Next(LeafSize));
            var angle = random.NextDouble() * Math.PI;
            var length = 6 + random.Next(18);
            for (var t = 0; t <= length; t++)
            {
                var px = x + (int)Math.Floor(Math.Cos(angle) * t);
                var py = y + (int)Math.Floor(Math.Sin(angle) * t);
                for (var d = 0; d < 4; d++)
                {
                    var tx = (px + (d & 1) + LeafSize) % LeafSize;
                    var ty = (py + (d >> 1) + LeafSize) % LeafSize;
                    rgba[(((ty * LeafSize) + tx) * 4) + 3] = 255;
                }
            }
        }

        var opaque = 0;
        for (var i = 3; i < rgba.Length; i += 4)
        {
            opaque += rgba[i] == 255 ? 1 : 0;
        }

        return (rgba, opaque / (double)(LeafSize * LeafSize));
    }

    /// <summary>A masked card: the texture's alpha cuts it at <see cref="Cutoff"/> (UE's default opacity mask clip).</summary>
    internal static MeshHandle Masked(GlHarness gl, MeshData mesh, GpuTexture texture) =>
        gl.Renderer.AddMesh(PreparedMesh.From(mesh, MeshSpace.Gl), texture, materialAlphaCutoffs: new Dictionary<string, float> { [string.Empty] = Cutoff });

    /// <summary>A square of <paramref name="size"/> facing <paramref name="camera"/> at <paramref name="centre"/>; u runs along the camera's right, v down.</summary>
    internal static MeshData FacingQuad(FlyCamera camera, Vector3 centre, float size, Vector3? normal = null)
    {
        var (r, u) = (camera.Right * (size * 0.5f), camera.Up * (size * 0.5f));
        Vector3[] corners = [centre - r - u, centre + r - u, centre + r + u, centre - r + u];
        var n = normal ?? -camera.Forward;
        return MeshData.Create("Card", [.. corners.SelectMany(c => new[] { c.X, c.Y, c.Z })], [0u, 1u, 2u, 0u, 2u, 3u],
            [.. Enumerable.Repeat(new[] { n.X, n.Y, n.Z }, 4).SelectMany(v => v)], [0f, 1f, 1f, 1f, 1f, 0f, 0f, 0f]);
    }

    /// <summary>A quad through <paramref name="corners"/> whose normals all point straight up (a grass card's normals).</summary>
    private static MeshData UpNormalCard(Vector3[] corners, uint[] indices) =>
        MeshData.Create("UpNormalCard", [.. corners.SelectMany(c => new[] { c.X, c.Y, c.Z })], indices,
            [.. Enumerable.Repeat(new[] { 0f, 1f, 0f }, 4).SelectMany(v => v)], [0f, 1f, 1f, 1f, 1f, 0f, 0f, 0f]);

    internal static FlyCamera LookingAtOrigin(float distance)
    {
        var camera = new FlyCamera { FieldOfView = 60f };
        camera.SetClipRange(10f, 20_000f);
        camera.Orbit(Vector3.Zero, 30f, -10f, distance);
        return camera;
    }

    internal static (int X, int Y) ToPixel(FlyCamera camera, Vector3 world, RenderTarget target)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), camera.GetViewProjection(target.AspectRatio));
        var ndc = new Vector2(clip.X, clip.Y) / clip.W;
        return ((int)((ndc.X + 1f) * 0.5f * target.Width), (int)((1f - ndc.Y) * 0.5f * target.Height));
    }

    /// <summary>Rec. 709 luminance of an RGBA8 pixel as stored (0..1).</summary>
    internal static double Luminance(byte[] pixel) => ((0.2126 * pixel[0]) + (0.7152 * pixel[1]) + (0.0722 * pixel[2])) / 255.0;
}
