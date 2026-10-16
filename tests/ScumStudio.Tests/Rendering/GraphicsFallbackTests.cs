using System.Numerics;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.Gl;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Rendering.Targets;
using Silk.NET.OpenGL;
using Xunit;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// D03 (Pheidian, RX 9070 XT: crashes as areas load since 3.2.0): the decisions behind the graphics fallbacks, and the
/// fallback paths themselves run on this PC's GPU by turning features off the way a refusing AMD driver does.
/// </summary>
public sealed class GraphicsDecisionTests
{
    [Theory]
    [InlineData("NVIDIA Corporation", "NVIDIA GeForce RTX 5080/PCIe/SSE2", GpuVendor.Nvidia)]
    [InlineData("ATI Technologies Inc.", "AMD Radeon RX 9070 XT", GpuVendor.Amd)]
    [InlineData("ATI Technologies Inc.", "AMD Radeon(TM) Graphics", GpuVendor.Amd)]
    [InlineData("Intel", "Intel(R) UHD Graphics 770", GpuVendor.Intel)]
    [InlineData("Microsoft Corporation", "GDI Generic", GpuVendor.Software)]
    [InlineData("Mesa", "llvmpipe (LLVM 17.0.6, 256 bits)", GpuVendor.Software)]
    [InlineData("", "", GpuVendor.Unknown)]
    public void VendorIsReadFromTheDriverStrings(string vendor, string renderer, GpuVendor expected) =>
        Assert.Equal(expected, GpuClassifier.VendorOf(vendor, renderer));

    [Theory]
    [InlineData("Intel", "Intel(R) UHD Graphics 620", true)]
    [InlineData("Intel", "Intel(R) Iris(R) Xe Graphics", true)]
    [InlineData("Intel", "Intel(R) Arc(TM) Graphics", true)] // Core Ultra's built-in Arc
    [InlineData("Intel", "Intel(R) Arc(TM) 140V GPU (16GB)", true)]
    [InlineData("Intel", "Intel(R) Arc(TM) A770 Graphics", false)]
    [InlineData("Intel", "Intel(R) Arc(TM) B580 Graphics", false)]
    [InlineData("ATI Technologies Inc.", "AMD Radeon(TM) Graphics", true)]
    [InlineData("ATI Technologies Inc.", "AMD Radeon 780M Graphics", true)]
    [InlineData("ATI Technologies Inc.", "AMD Radeon(TM) 8060S Graphics", true)]
    [InlineData("ATI Technologies Inc.", "AMD Radeon(TM) Vega 8 Graphics", true)]
    [InlineData("ATI Technologies Inc.", "AMD Radeon RX 9070 XT", false)]
    [InlineData("ATI Technologies Inc.", "Radeon RX Vega 64", false)]
    [InlineData("ATI Technologies Inc.", "AMD Radeon PRO W7900", false)]
    [InlineData("NVIDIA Corporation", "NVIDIA GeForce RTX 4060 Laptop GPU/PCIe/SSE2", false)]
    [InlineData("Microsoft Corporation", "GDI Generic", false)]
    public void IntegratedGpusAreTold(string vendor, string renderer, bool integrated) =>
        Assert.Equal(integrated, GpuClassifier.IsIntegrated(vendor, renderer));

    [Theory]
    [InlineData(4, 32, null, null, 4)]
    [InlineData(3, 32, null, null, 2)] // a power of two
    [InlineData(4, 2, null, null, 2)] // GL_MAX_SAMPLES
    [InlineData(4, 0, null, null, 1)]
    [InlineData(1, 32, null, null, 1)]
    [InlineData(4, 8, new[] { 8, 4, 2 }, new[] { 8, 2 }, 2)] // the depth format does not list 4
    [InlineData(4, 8, new[] { 8 }, new[] { 8 }, 1)] // nothing up to 4 for both
    [InlineData(8, 8, new int[0], new[] { 8, 4 }, 8)] // a driver that did not answer for colour
    public void SampleCountsAreOnesBothFormatsList(int requested, int maxSamples, int[]? colour, int[]? depth, int expected) =>
        Assert.Equal(expected, GpuClassifier.ChooseSamples(requested, maxSamples, colour, depth));

    [Fact]
    public void AFeatureIsTurnedOffOnceWithItsFirstReason()
    {
        var fallbacks = new GraphicsFallbacks();
        var raised = new List<(GraphicsFeature, string)>();
        fallbacks.TurnedOff += (f, r) => raised.Add((f, r));
        Assert.False(fallbacks.IsOff(GraphicsFeature.Multisampling));
        Assert.True(fallbacks.TurnOff(GraphicsFeature.Multisampling, "first"));
        Assert.False(fallbacks.TurnOff(GraphicsFeature.Multisampling, "second"));
        Assert.True(fallbacks.IsOff(GraphicsFeature.Multisampling));
        Assert.Equal((GraphicsFeature.Multisampling, "first"), Assert.Single(raised));
        Assert.Equal("first", fallbacks.Snapshot()[GraphicsFeature.Multisampling]);
    }
}

/// <summary>The fallback paths on a real context (the PC has an RTX; the AMD refusals are simulated by turning features off).</summary>
[Collection(GlCollection.Name)]
public sealed class GraphicsFallbackTests(ITestOutputHelper output)
{
    [GlFact]
    public void WithoutMultisamplingTheTargetHasOneSampleAndMaskedLeavesStillDraw()
    {
        using var gl = GlHarness.Create(96, 96);
        output.WriteLine($"{gl.Renderer.Info}, up to {gl.Renderer.Info.MaxSamples} samples");
        Assert.True(gl.Renderer.Fallbacks.TurnOff(GraphicsFeature.Multisampling, "test: a driver that refuses multisampling"));
        using var target = gl.Renderer.CreateTarget(96, 96, samples: 4);
        Assert.Equal(1, target.Samples);
        var (leaf, gap) = DrawHalfLeaf(gl, target);
        Assert.True(leaf[1] > 100 && leaf[1] > gap[1] + 40, $"leaf {Show(leaf)} vs gap {Show(gap)}");
    }

    [GlFact]
    public void AMultisampledTargetDropsToOneSampleOnceMultisamplingIsTurnedOff()
    {
        // The viewport's target lives across frames: a refusal seen in one frame (the water's copy, a resolve) must take
        // effect at the next Resize, which the viewport calls every frame with the same size.
        using var gl = GlHarness.Create(64, 64);
        using var target = gl.Renderer.CreateTarget(64, 64, samples: 4);
        Assert.Equal(4, target.Samples);
        Assert.False(target.Resize(64, 64, 4));
        gl.Renderer.Fallbacks.TurnOff(GraphicsFeature.Multisampling, "test: the resolve was refused");
        Assert.True(target.Resize(64, 64, 4));
        Assert.Equal(1, target.Samples);
        var (leaf, _) = DrawHalfLeaf(gl, target);
        Assert.True(leaf[1] > 100, $"leaf {Show(leaf)}");
    }

    [GlFact]
    public void WithoutAlphaToCoverageAMultisampledTargetCutsLeavesByTheirMask()
    {
        using var gl = GlHarness.Create(96, 96);
        gl.Renderer.Fallbacks.TurnOff(GraphicsFeature.AlphaToCoverage, "test: a driver that mishandles alpha to coverage");
        using var target = gl.Renderer.CreateTarget(96, 96, samples: 4);
        Assert.Equal(4, target.Samples);
        var (leaf, gap) = DrawHalfLeaf(gl, target);
        Assert.True(leaf[1] > 100 && leaf[1] > gap[1] + 40, $"leaf {Show(leaf)} vs gap {Show(gap)}");
        Assert.False(gl.Context.Gl.IsEnabled(EnableCap.SampleAlphaToCoverage));
    }

    [GlFact]
    public void ARefusedCompressedUploadTurnsCompressionOffAndLeavesNoError()
    {
        using var gl = GlHarness.Create(32, 32);
        var renderer = gl.Renderer;
        var maxSize = gl.Context.Gl.GetInteger(GetPName.MaxTextureSize);
        // Wider than the driver allows: a real GL_INVALID_VALUE from glCompressedTexImage2D, as a driver refusing a block size.
        var width = maxSize * 2;
        var mip = new byte[GpuTexture.CompressedSize(CompressedFormat.Bc7, width, 4)];
        Assert.Null(renderer.CreateCompressedTexture(CompressedFormat.Bc7, width, 4, [mip], srgb: true));
        Assert.True(renderer.Fallbacks.IsOff(GraphicsFeature.CompressedTextures));
        output.WriteLine(renderer.Fallbacks.Snapshot()[GraphicsFeature.CompressedTextures]);
        Assert.Empty(GlErrors.Drain(gl.Context.Gl));

        // From now on nothing is uploaded compressed (the caller decodes to RGBA8), even a valid texture.
        Assert.Null(renderer.CreateCompressedTexture(CompressedFormat.Bc7, 4, 4, [new byte[16]], srgb: true));
    }

    [GlFact]
    public void AStaleGlErrorDoesNotFailAMeshUpload()
    {
        // Another upload (or the host) left an error on the shared context: AddMesh threw and the area never loaded.
        using var gl = GlHarness.Create(32, 32);
        gl.Context.Gl.Enable((EnableCap)0); // GL_INVALID_ENUM left behind
        var handle = gl.Renderer.AddMesh(PrimitiveMeshes.Cube(100f), MeshSpace.Gl);
        Assert.True(gl.Renderer.Meshes.ContainsKey(handle.Id));
    }

    [GlFact]
    public void AFrameThatThrowsHalfWayIsFollowedByAGoodOne()
    {
        // The viewport catches a failed frame and calls ResetState: the state the frame left (coverage on, blending, a
        // framebuffer bound) must not leak into the next frame or the host's drawing.
        using var gl = GlHarness.Create(64, 64);
        using var target = gl.Renderer.CreateTarget(64, 64, samples: 4);
        var api = gl.Context.Gl;
        target.BindColor();
        api.Enable(EnableCap.SampleAlphaToCoverage);
        api.Enable(EnableCap.Blend);
        api.Enable((EnableCap)0); // and an error
        gl.Renderer.ResetState();
        Assert.False(api.IsEnabled(EnableCap.SampleAlphaToCoverage));
        Assert.False(api.IsEnabled(EnableCap.Blend));
        Assert.Equal(0, api.GetInteger(GetPName.DrawFramebufferBinding));
        Assert.Empty(GlErrors.Drain(api));
        var (leaf, _) = DrawHalfLeaf(gl, target);
        Assert.True(leaf[1] > 100, $"leaf {Show(leaf)}");
    }

    /// <summary>Draws a camera-facing card whose left half is opaque green leaf and right half cut out, over the clear colour.</summary>
    private static (byte[] Leaf, byte[] Gap) DrawHalfLeaf(GlHarness gl, RenderTarget target)
    {
        const int size = 256;
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            rgba[(i * 4) + 1] = 200;
            rgba[(i * 4) + 3] = (byte)(i % size < size / 2 ? 255 : 0);
        }

        gl.UseUnlit();
        using var texture = gl.Renderer.CreateTexture(size, size, rgba);
        var camera = FoliageAntiAliasingTests.LookingAtOrigin(1000f);
        var scene = new Scene();
        scene.Add(FoliageAntiAliasingTests.Masked(gl, FoliageAntiAliasingTests.FacingQuad(camera, Vector3.Zero, 1200f), texture), Matrix4x4.Identity, 1, "leaves");
        gl.Renderer.Render(target, scene, camera);
        var pixels = target.ReadColorRgba();
        var leaf = FoliageAntiAliasingTests.ToPixel(camera, -camera.Right * 300f, target);
        var gap = FoliageAntiAliasingTests.ToPixel(camera, camera.Right * 300f, target);
        return (Pixel(leaf), Pixel(gap));

        byte[] Pixel((int X, int Y) p)
        {
            var o = ((p.Y * target.Width) + p.X) * 4;
            return pixels[o..(o + 4)];
        }
    }

    private static string Show(byte[] p) => string.Join(",", p);
}
