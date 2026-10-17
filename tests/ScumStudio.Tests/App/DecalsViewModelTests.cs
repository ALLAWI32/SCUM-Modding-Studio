using System.Numerics;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Mathematics;
using ScumStudio.Tests.Viewport;
using ScumStudio.Viewport;
using ScumStudio.Viewport.Decals;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Decals panel's view model on a synthetic car (a tube): a decal is dropped where the ray meets the body, dragged,
/// edited, each step undone on its own, and saved in the project's <c>decals/&lt;clone&gt;.json</c>.
/// </summary>
public sealed class DecalsViewModelTests
{
    private const string Body = "/Game/Cars/MI_Body.MI_Body";

    [Fact]
    public async Task ADecalIsPlacedDraggedAndUndoneStepByStep()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Decals");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(Car(), "BPC_MyCar");
        var changes = 0;
        page.DecalsChanged += (_, _) => changes++;

        Assert.False(page.PlaceDecal(Ray(0))); // nothing armed, no decal there
        page.ArmDecalCommand.Execute(page.DecalDesigns.Single(d => d.Design.Id == "flames-1"));
        Assert.True(page.PlaceDecal(Ray(0)));
        var placed = Assert.Single(page.Decals.Layers);
        Assert.Equal("flames-1", placed.DesignId);
        Assert.Equal(Body, placed.MaterialPath);
        Assert.Equal("Door_Left", placed.PartKey);
        Assert.InRange(placed.Position.Y, 49.5f, 50f);
        Assert.True(Vector3.Dot(placed.Normal, Vector3.UnitY) > 0.99f);
        Assert.Equal(40f, placed.WidthCm);
        Assert.Equal(40f / DecalLibrary.Find("flames-1")!.Aspect, placed.HeightCm, 3); // the design's own shape
        Assert.Equal(placed.Id, page.SelectedDecal?.Layer.Id);

        // A drag is one step, however many moves it has.
        Assert.True(page.DragDecal(Ray(10), commit: false));
        Assert.True(page.DecalsDragging);
        Assert.True(page.DragDecal(Ray(25), commit: true));
        Assert.False(page.DecalsDragging);
        Assert.Equal(25f, page.Decals.Layers.Single().Position.X, 2);
        Assert.False(page.DragDecal(new SurfaceRay(-1, new Vector3(0, 200, 300), -Vector3.UnitY), commit: true)); // off the car: stays

        page.UndoDecalCommand.Execute(null);
        Assert.Equal(0f, page.Decals.Layers.Single().Position.X, 2);
        page.UndoDecalCommand.Execute(null);
        Assert.Empty(page.Decals.Layers);
        Assert.False(page.CanUndoDecal);
        Assert.True(changes >= 5);
    }

    [Fact]
    public async Task SettingsAreOneStepPerPauseAndEveryStepIsSavedPerClone()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Decals");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(Car(), "BPC_MyCar");
        page.ArmDecalCommand.Execute(page.DecalDesigns.Single(d => d.Design.Id == "skull-plain"));
        Assert.True(page.PlaceDecal(Ray(0)));

        var layer = page.SelectedDecal!;
        layer.Size = 50; // a slider dragged: one step
        layer.Size = 60;
        layer.Opacity = 40;
        Assert.Equal(60f, page.Decals.Layers.Single().WidthCm);
        Assert.Equal(0.4f, page.Decals.Layers.Single().Opacity, 3);
        var file = DecalSet.PathOf(ctx.Services.Projects.Current!.DirectoryPath, "BPC_MyCar");
        Assert.Equal(0.4f, DecalSet.Load(ctx.Services.Projects.Current.DirectoryPath, "BPC_MyCar").Layers.Single().Opacity, 3);
        Assert.True(File.Exists(file));

        page.UndoDecalCommand.Execute(null); // the size and opacity edits were one pause
        Assert.Equal(40f, page.Decals.Layers.Single().WidthCm);
        Assert.Equal(1f, page.Decals.Layers.Single().Opacity);
        Assert.Equal(1f, DecalSet.Load(ctx.Services.Projects.Current.DirectoryPath, "BPC_MyCar").Layers.Single().Opacity);

        page.UseDecalTarget(Car(), "BPC_Other"); // another clone: its own decals
        Assert.Empty(page.Decals.Layers);
        page.UseDecalTarget(Car(), "BPC_MyCar");
        Assert.Single(page.Decals.Layers);
    }

    [Fact]
    public async Task ComposeDecalsBakesTheDecalsOfThatMaterialOnly()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Decals");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(Car(), "BPC_MyCar");
        page.ArmDecalCommand.Execute(page.DecalDesigns.Single(d => d.Design.Id == "shape-heart"));
        Assert.True(page.PlaceDecal(Ray(0)));

        var diffuse = Image(256, 128, 128, 128, srgb: true);
        var mask = Image(4, 0, 255, 0, srgb: false);
        var (baked, uncovered) = page.ComposeDecals(Body, diffuse, mask);
        var centre = (((256 / 2) * 256) + 1) * 4; // u ≈ 0, v = 0.5: under the decal
        Assert.NotEqual(128, baked.Rgba[centre + 1]);
        Assert.True(uncovered.Rgba[centre + 1] < 40, $"mask green {uncovered.Rgba[centre + 1]}: the paint still covers the decal");
        Assert.Equal(255, uncovered.Rgba[(((256 / 2) * 256) + 128) * 4 + 1]); // the far side keeps its paint
        Assert.Same(diffuse, page.ComposeDecals("/Game/Cars/MI_Glass.MI_Glass", diffuse, mask).Diffuse);
    }

    internal static SurfaceRay Ray(float x) => new(-1, new Vector3(x, 200, 0), -Vector3.UnitY);

    /// <summary>A "car": one door, a UV-unwrapped tube (radius 50 cm) round the X axis.</summary>
    internal static PreviewModel Car() => new("Car",
        [new PreviewPart("Door", DecalMeshes.Cylinder(50, 100, 128, 32), null, FTransform.Identity) { Material = Body, Attachment = "/Game/Cars/BPC_MyCar_Door_Left" }],
        new Dictionary<string, TextureImage>());

    private static TextureImage Image(int size, byte r, byte g, byte b, bool srgb)
    {
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, 255);
        }

        return new TextureImage("T", size, size, rgba, "PF_B8G8R8A8", 0, size, size, srgb, false);
    }
}
