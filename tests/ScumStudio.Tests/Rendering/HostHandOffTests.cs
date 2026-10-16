using System.Diagnostics;
using System.Numerics;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using Silk.NET.OpenGL;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Rendering;

/// <summary>
/// D03 (AMD cards crash as areas load; RTX cards do not). Avalonia's OpenGlControlBase calls only glFlush after
/// OnOpenGlRender, then its compositor (another context, on the render thread) copies the frame's texture with
/// CopyTexSubImage2D (Avalonia.Skia GlSkiaImportedImage.CopyToNewTexture, "automatic sync"). OpenGL promises another
/// context finished contents only after glFinish or a fence it waits on (GL 4.3 core, appendix D.3.1): the viewports'
/// last call, <see cref="Targets.RenderTarget.BlitTo"/>, must return with the frame complete on the GPU.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class HostHandOffTests(ITestOutputHelper output)
{
    [GlFact]
    public void TheCopyToTheHostReturnsWithTheWholeFrameCompleteOnTheGpu()
    {
        const int size = 2048;
        using var gl = GlHarness.Create(64, 64);
        var api = gl.Context.Gl;
        using var target = gl.Renderer.CreateTarget(size, size, samples: 4);

        // A frame that keeps the GPU busy for a while: 300 see-through cards over the whole view, every one shaded and blended.
        var scene = new Scene();
        var card = gl.Renderer.AddMesh(PrimitiveMeshes.Plane(4000f), MeshSpace.Gl);
        for (var i = 0; i < 300; i++)
        {
            scene.Add(card, Matrix4x4.CreateTranslation(0f, i * 0.5f, 0f), 0, "card").Tint = new Vector4(0.5f, 0.6f, 0.7f, 0.05f);
        }

        var camera = new FlyCamera();
        camera.SetClipRange(1f, 10_000f);
        camera.Orbit(Vector3.Zero, 0f, -89f, 300f);
        gl.Renderer.Settings = new RenderSettings { ShowGrid = false };

        var (hostTexture, host) = HostFramebuffer(api, size);
        try
        {
            gl.Renderer.Render(target, scene, camera); // shaders compiled, buffers placed: not measured
            api.Finish();
            var clock = Stopwatch.StartNew();
            gl.Renderer.Render(target, scene, camera);
            var frame = api.FenceSync(SyncCondition.SyncGpuCommandsComplete, SyncBehaviorFlags.None);
            target.BlitTo(host, size, size);
            var signalled = Status(api, frame) == GLEnum.Signaled;
            var handedOver = clock.Elapsed.TotalMilliseconds;
            api.Finish();
            output.WriteLine($"{gl.Renderer.Info}: frame handed over after {handedOver:0.0} ms, complete then: {signalled}, all done after {clock.Elapsed.TotalMilliseconds:0.0} ms");
            api.DeleteSync(frame);
            Assert.True(signalled, "BlitTo returned while the frame was still being drawn: the host's compositor may copy a half-drawn texture");
        }
        finally
        {
            api.DeleteFramebuffer(host);
            api.DeleteTexture(hostTexture);
        }
    }

    /// <summary>A framebuffer with an RGBA8 texture, like the one Avalonia passes to OnOpenGlRender.</summary>
    internal static (uint Texture, uint Framebuffer) HostFramebuffer(GL api, int size)
    {
        var texture = api.GenTexture();
        api.BindTexture(TextureTarget.Texture2D, texture);
        api.TexStorage2D(TextureTarget.Texture2D, 1, SizedInternalFormat.Rgba8, (uint)size, (uint)size);
        api.BindTexture(TextureTarget.Texture2D, 0);
        var framebuffer = api.GenFramebuffer();
        api.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        api.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
        api.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return (texture, framebuffer);
    }

    private static GLEnum Status(GL api, nint sync)
    {
        api.GetSync(sync, SyncParameterName.SyncStatus, 1, out _, out int status);
        return (GLEnum)status;
    }
}
