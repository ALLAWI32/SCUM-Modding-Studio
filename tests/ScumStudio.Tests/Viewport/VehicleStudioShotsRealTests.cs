using System.Numerics;
using ScumStudio.App.Controls;
using ScumStudio.Assets.Catalog;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Owner (2026-10-17): "the box man beside the vehicles looks bad". On the real game files (<c>SCUM_PAKS</c>, key from this
/// PC's store): the Rager in the Vehicles 3D view's sunlit preview, with the scale figure beside it. With
/// <c>SCUMSTUDIO_SCREENSHOTS</c> set the frame is saved there (outside the repository) for a look.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class VehicleStudioShotsRealTests(ITestOutputHelper output)
{
    private const int Width = 960;
    private const int Height = 600;

    [PaksFact]
    public async Task TheRagerStandsBesideTheGamesPrisonerInTheSunlitPreview()
    {
        Assert.Null(GlTestEnvironment.SkipReason);
        using var catalog = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var rager = ModdableAssets.Find(catalog).First(a => a.PackagePath.EndsWith("/BPC_Rager", StringComparison.OrdinalIgnoreCase)).PackagePath;
        var model = new MeshPreviewLoader(catalog) { TextureSize = 1024, ForGpu = true }.LoadBlueprint(rager)!;

        // The game's prisoner, standing, with his textures: 1.8 m, facing +X.
        var figure = ScaleFigure.Load(catalog);
        Assert.NotNull(figure);
        Assert.NotEmpty(figure.Parts);
        Assert.All(figure.Parts, p => Assert.True(p.TexturePath is not null && figure.Textures.ContainsKey(p.TexturePath), p.Name));
        var stands = ScaleFigure.Bounds(figure);
        output.WriteLine($"figure {stands.Size} cm, {figure.Triangles} triangles, {figure.Parts.Count} part(s)");
        Assert.InRange(stands.Size.Z, 171f, 189f);
        Assert.Equal(0f, stands.Min.Z, 1);
        Assert.InRange(stands.Size.Y, 30f, 110f); // shoulder to shoulder (a hand at each side), not a T-pose

        using var gl = GlHarness.Create(Width, Height);
        using var scene = PreviewScene.Upload(gl.Renderer, model);
        scene.FigureOf = () => figure;
        var car = scene.ModelBounds;
        scene.ShowScale = true;
        scene.Ground = true;
        Assert.True(scene.FigureShown);
        Assert.InRange(scene.Bounds.Size.Y, car.Size.Y, MathF.Max(car.Size.Y, 190f));
        output.WriteLine($"car {car.Size}, with the figure {scene.Bounds.Size}");

        var camera = new FlyCamera();
        var radius = scene.Bounds.Extent.Length();
        camera.Orbit(scene.Bounds.Center, PreviewScene.DefaultViewYaw, -12f, radius * 2.1f);
        camera.FitClipRange(radius * 2.1f, radius);
        gl.Renderer.Settings = MeshPreview.SettingsFor(PreviewLight.Sun, gl.Renderer.Settings, radius, -Vector3.UnitY) with
        {
            ShowGrid = true,
            GridHeight = car.Min.Y,
            GridCellSize = MeshPreview.GridCellFor(car.Size.Length()),
        };
        gl.Renderer.Render(gl.Target, scene.Scene, camera);
        var rgba = gl.Target.ReadColorRgba();
        if (Environment.GetEnvironmentVariable("SCUMSTUDIO_SCREENSHOTS") is { Length: > 0 } folder && folder is not "0")
        {
            Directory.CreateDirectory(folder);
            await ImageExport.SavePngAsync(rgba, Width, Height, Path.Combine(folder, "rager-scale-figure.png"));
        }
        }

    /// <summary>
    /// Renders <paramref name="model"/> in the sunlit preview from <paramref name="yaw"/> (no figure) and saves it as
    /// <paramref name="name"/> in <c>SCUMSTUDIO_SCREENSHOTS</c> when that is set; returns the frame (RGBA).
    /// </summary>
    internal static async Task<byte[]> ShotAsync(PreviewModel model, string name, float yaw = PreviewScene.DefaultViewYaw)
    {
        using var gl = GlHarness.Create(Width, Height);
        using var scene = PreviewScene.Upload(gl.Renderer, model);
        scene.FigureOf = () => null;
        scene.Ground = true;
        var camera = new FlyCamera();
        var radius = scene.Bounds.Extent.Length();
        camera.Orbit(scene.Bounds.Center, yaw, -14f, radius * 1.9f);
        camera.FitClipRange(radius * 1.9f, radius);
        gl.Renderer.Settings = MeshPreview.SettingsFor(PreviewLight.Sun, gl.Renderer.Settings, radius, -Vector3.UnitY) with { ShowGrid = false };
        gl.Renderer.Render(gl.Target, scene.Scene, camera);
        var rgba = gl.Target.ReadColorRgba();
        if (Environment.GetEnvironmentVariable("SCUMSTUDIO_SCREENSHOTS") is { Length: > 0 } folder && folder is not "0")
        {
            Directory.CreateDirectory(folder);
            await ImageExport.SavePngAsync(rgba, Width, Height, Path.Combine(folder, name));
        }

        return rgba;
    }
}
