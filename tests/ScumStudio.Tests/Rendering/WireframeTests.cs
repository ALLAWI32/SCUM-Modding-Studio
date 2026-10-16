using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using Xunit;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// seviscache #7: a wireframe view. The scene's triangles are drawn as lines (far fewer pixels than the shaded box), on the
/// plain and the anti-aliased (multisampled) target alike; the overlays (the gizmo) stay filled; the next shaded frame is
/// shaded again; the pick pass still sees the whole box.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class WireframeTests
{
    private const int Width = 160;
    private const int Height = 120;

    [GlFact]
    public void AWireframeFrameDrawsTheEdgesOnlyAndTheOverlayStaysFilled()
    {
        foreach (var samples in new[] { 1, 4 })
        {
            using var gl = GlHarness.Create(Width, Height);
            using var target = gl.Renderer.CreateTarget(Width, Height, samples);
            gl.UseUnlit();
            var scene = new Scene();
            scene.Add(gl.Renderer.AddMesh(PrimitiveMeshes.Cube(200f), MeshSpace.Gl), Matrix4x4.Identity, 1, "box");
            var camera = new FlyCamera();
            camera.Frame(new BoundingBox(new Vector3(-100f), new Vector3(100f)), (float)Width / Height, -135f, -30f, margin: 0.8f);

            gl.Renderer.Render(target, scene, camera);
            var shaded = Covered(target.ReadColorRgba());

            gl.Renderer.Settings = gl.Renderer.Settings with { Wireframe = true };
            var (overlayX, overlayY) = (Width / 8, Height / 8); // a corner the box does not reach
            var (origin, direction) = camera.ScreenRay(overlayX, overlayY, Width, Height);
            var at = origin + (direction * 50f);
            var colour = new Vector4(1f, 0f, 1f, 1f);
            gl.Renderer.OverlayTriangles.Add(new OverlayTriangle(at - (camera.Right * 5f) - (camera.Up * 5f), at + (camera.Right * 5f) - (camera.Up * 5f), at + (camera.Up * 5f), colour));
            gl.Renderer.Render(target, scene, camera);
            var rgba = target.ReadColorRgba();
            var wire = Covered(rgba);
            gl.Renderer.OverlayTriangles.Clear();

            Assert.True(shaded > 2000, $"{samples}x: the shaded box covers {shaded} pixels");
            Assert.True(wire > 50, $"{samples}x: the wireframe draws its edges ({wire} pixels)");
            Assert.True(wire < shaded / 2, $"{samples}x: lines, not faces: {wire} of {shaded} pixels");
            var o = ((overlayY * Width) + overlayX) * 4;
            Assert.True(rgba[o] > 200 && rgba[o + 1] < 60 && rgba[o + 2] > 200, $"{samples}x: the overlay stays filled: {rgba[o]},{rgba[o + 1]},{rgba[o + 2]}");

            // The pick pass ignores wireframe: a click in the middle of a face still finds the box.
            Assert.Equal(1u, gl.Renderer.Pick(target, scene, camera, Width / 2, Height / 2)?.InstanceId);

            // Shaded again.
            gl.Renderer.Settings = gl.Renderer.Settings with { Wireframe = false };
            gl.Renderer.Render(target, scene, camera);
            Assert.Equal(shaded, Covered(target.ReadColorRgba()));
        }
    }

    [Theory]
    [InlineData("4.6.0 NVIDIA 566.36", false)]
    [InlineData("4.6.0 Core Profile Context 24.10.1", false)]
    [InlineData("OpenGL ES 3.0.0 (ANGLE 2.1.0)", true)]
    public void OpenGlEsIsToldApart(string version, bool es)
    {
        Assert.Equal(es, new GlInfo("vendor", "renderer", version, 4, 6, true).IsEs);
    }

    /// <summary>Pixels not of the clear colour (black).</summary>
    private static int Covered(byte[] rgba)
    {
        var count = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i] + rgba[i + 1] + rgba[i + 2] > 30)
            {
                count++;
            }
        }

        return count;
    }
}
