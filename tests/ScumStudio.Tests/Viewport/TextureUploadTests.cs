using System.Numerics;
using ScumStudio.Assets.Textures;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;
using Xunit;

namespace ScumStudio.Tests.Viewport;

[Collection(GlCollection.Name)]
public sealed class TextureUploadTests
{
    // One BC1 block: both end colours pure red (RGB565 0xF800), every texel index 0.
    private static readonly byte[] RedBlock = [0x00, 0xF8, 0x00, 0xF8, 0, 0, 0, 0];

    private static TextureImage Bc1(int size, byte[] block)
    {
        var mips = new List<byte[]>();
        for (var s = size; s >= 1; s /= 2)
        {
            var blocks = Math.Max(1, s / 4) * Math.Max(1, s / 4);
            mips.Add(Enumerable.Range(0, blocks).SelectMany(_ => block).ToArray());
        }

        return new TextureImage("T_Test", size, size, [], "PF_DXT1", 0, size, size, IsSrgb: false, IsNormalMap: false) { CompressedMips = mips };
    }

    [GlFact]
    public void CookedBlocks_DrawTheirColour_AndASharperCopyReplacesTheCachedOne()
    {
        using var gl = GlHarness.Create(32, 32);
        gl.UseUnlit();
        var cache = new GpuMeshCache();
        var small = cache.Texture(gl.Renderer, "/Game/T_Test", Bc1(8, RedBlock));
        var plane = gl.Renderer.AddMesh(PrimitiveMeshes.Plane(400f), MeshSpace.Gl, 1f, small);
        var scene = new Scene();
        scene.Add(plane, Matrix4x4.Identity, 1, "plane");
        var camera = new FlyCamera();
        camera.Orbit(Vector3.Zero, 0f, -89f, 300f);

        gl.Renderer.Render(gl.Target, scene, camera);
        var red = gl.Pixel(gl.Target.ReadColorRgba(), 16, 16);
        Assert.True(red[0] > 240 && red[1] < 10 && red[2] < 10, $"{red[0]},{red[1]},{red[2]}");

        // The quality went up: the same path comes again at a larger size; the mesh draws with the new copy.
        var sharper = cache.Texture(gl.Renderer, "/Game/T_Test", Bc1(16, RedBlock));
        Assert.Equal(16, sharper.Width);
        Assert.Same(sharper, gl.Renderer.Meshes[plane.Id].Texture);
        Assert.Same(sharper, cache.Texture(gl.Renderer, "/Game/T_Test", Bc1(8, RedBlock))); // never back to the blurrier one

        // It is the same object, now sharp: a list still holding the first one (a staged scene, terrain detail) draws the
        // sharper image and never a deleted texture (owner, 2026-10-09: "Cannot access a disposed object").
        Assert.Same(small, sharper);
        gl.Renderer.Render(gl.Target, scene, camera);
        red = gl.Pixel(gl.Target.ReadColorRgba(), 16, 16);
        Assert.True(red[0] > 240 && red[1] < 10 && red[2] < 10, $"{red[0]},{red[1]},{red[2]}");
    }
}
