using System.Numerics;
using ScumStudio.App.Services;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Tests.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ScumStudio.Tests.App;

/// <summary>"Share your mod": the picture (preview and thumbnail JPEGs within the upload limits) and the capture size.</summary>
public sealed class SharePictureTests
{
    [Fact]
    public void APreviewIsAJpegUnderTheLimit()
    {
        // Noise is the worst case for JPEG: the quality, and then the size, step down until it fits.
        var rgba = new byte[1920 * 1080 * 4];
        new Random(7).NextBytes(rgba);
        var pictures = SharePicture.FromRgba(rgba, 1920, 1080);

        Assert.Equal([0xFF, 0xD8, 0xFF], pictures.Preview[..3]);
        Assert.True(pictures.Preview.Length <= SharePicture.PreviewMaxBytes, $"preview {pictures.Preview.Length} bytes");
        Assert.Equal([0xFF, 0xD8, 0xFF], pictures.Thumb[..3]);
        Assert.True(pictures.Thumb.Length <= SharePicture.ThumbMaxBytes, $"thumb {pictures.Thumb.Length} bytes");
        var thumb = Image.Identify(pictures.Thumb);
        Assert.Equal((480, 270), (thumb.Width, thumb.Height));
        var preview = Image.Identify(pictures.Preview);
        Assert.Equal((pictures.Width, pictures.Height), (preview.Width, preview.Height));
        Assert.True(preview.Width <= 1920 && preview.Height <= 1080);
    }

    [Fact]
    public void AnImportedImageIsScaledDownKeepingItsShape()
    {
        var file = Path.Combine(Path.GetTempPath(), "scumstudio-share-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var image = new Image<Rgba32>(4000, 3000, new Rgba32(40, 90, 160)))
            {
                image.SaveAsPng(file);
            }

            var pictures = SharePicture.FromFile(file);
            var preview = Image.Identify(pictures.Preview);
            Assert.Equal((1440, 1080), (preview.Width, preview.Height)); // 4:3 kept, inside 1920 x 1080
            Assert.True(pictures.Preview.Length <= SharePicture.PreviewMaxBytes);
            Assert.Equal((480, 270), (Image.Identify(pictures.Thumb).Width, Image.Identify(pictures.Thumb).Height));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AFileThatIsNotAnImageIsRefused()
    {
        var file = Path.Combine(Path.GetTempPath(), "scumstudio-share-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllText(file, "not a picture");
        try
        {
            Assert.Throws<InvalidDataException>(() => SharePicture.FromFile(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void CaptureSizeFollowsTheViewAspect()
    {
        Assert.Equal((1920, 1080), ViewportCapture.SizeFor(16.0 / 9.0, 1080));
        Assert.Equal((2560, 1067), ViewportCapture.SizeFor(2.4, 1080)); // capped, the shape kept
        Assert.Equal((810, 1080), ViewportCapture.SizeFor(0.75, 1080));
        Assert.Equal((1920, 1080), ViewportCapture.SizeFor(double.NaN, 1080));
    }
}

/// <summary>The off-screen capture the Map viewport runs on its GL thread.</summary>
[Collection(GlCollection.Name)]
public sealed class ShareCaptureGlTests
{
    [GlFact]
    public void CaptureRendersAtTheAskedSize()
    {
        // The viewport's target stays 320 x 240; the capture is a second target of the asked size, same camera, no grid.
        using var gl = GlHarness.Create(320, 240);
        var quad = MeshData.Create("quad", [-100, -100, 0, 100, -100, 0, -100, 100, 0], [0u, 1u, 2u], [0, 0, 1, 0, 0, 1, 0, 0, 1], [0, 0, 1, 0, 0, 1]);
        var backdrop = new Scene();
        var level = new Scene();
        backdrop.Add(gl.Renderer.AddMesh(quad), Matrix4x4.Identity, 1, "a");
        level.Add(gl.Renderer.AddMesh(quad), Matrix4x4.CreateTranslation(50, 0, 0), 2, "b");
        var camera = new FlyCamera();
        camera.Frame(new BoundingBox(new Vector3(-150), new Vector3(150)), 4f / 3f, -135f, -30f);
        gl.Renderer.Settings = gl.Renderer.Settings with { ShowGrid = true };

        var (rgba, width, height) = ViewportCapture.RenderOffscreen(gl.Renderer, camera, backdrop, level, 640, 270);

        Assert.Equal((640, 270), (width, height));
        Assert.Equal(640 * 270 * 4, rgba.Length);
        Assert.Contains(rgba, b => b != 0);
        Assert.True(gl.Renderer.Settings.ShowGrid); // the user's grid setting is back
        Assert.Equal((320, 240), (gl.Target.Width, gl.Target.Height));
        var pictures = SharePicture.FromRgba(rgba, width, height);
        Assert.Equal((640, 270), (pictures.Width, pictures.Height));
    }
}
