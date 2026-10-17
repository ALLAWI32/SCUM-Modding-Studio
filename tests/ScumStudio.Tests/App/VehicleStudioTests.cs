using System.Numerics;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using ScumStudio.App.Controls;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Mathematics;
using ScumStudio.Modding.Cloning;
using ScumStudio.Tests.Viewport;
using ScumStudio.Viewport;
using ScumStudio.Viewport.Decals;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-17) on the Vehicles studio, on synthetic data: a decal "stuck to the surface" reaches every part under it
/// whatever its material; a design dropped on the 3D view is armed and clicked where it was let go; an added sticker is
/// listed and armed, an unreadable one says so; the clone name rule keeps names clear of the FName number split.
/// </summary>
public sealed class VehicleStudioTests
{
    private const string Body = "/Game/Cars/MI_Body.MI_Body";
    private const string Fender = "/Game/Cars/MI_Fender.MI_Fender";

    [Fact]
    public async Task ADecalStuckToTheSurfaceGivesEveryPartUnderItItsPiece()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Wrap");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(TwoPanels(), "BPC_MyCar");
        page.ArmDecalCommand.Execute(page.DecalDesigns.Single(d => d.Design.Id == "stripes-racing"));
        Assert.True(page.PlaceDecal(DecalsViewModelTests.Ray(0)));
        var layer = Assert.Single(page.Decals.Layers);
        Assert.Equal(Body, layer.MaterialPath);

        var textures = new Dictionary<string, (TextureImage, TextureImage)> { [Body] = (Grey(), White()), [Fender] = (Grey(), White()) };
        async Task<IReadOnlyList<string>> Baked() =>
            [.. (await page.BakeDecalsAsync(m => textures.TryGetValue(m, out var t) ? t : null)).Where(b => Changed(b.Result.Diffuse)).Select(b => b.Material)];

        // As a sticker 300 cm wide, it stays on the panel it was put on (the fender is another material).
        page.SelectedDecal!.Size = 300;
        Assert.Equal([Body], await Baked());

        // Stuck to the surface: the fender beside it takes its piece of the same decal.
        page.SelectedDecal!.Wrap = true;
        Assert.True(page.Decals.Layers.Single().Wrap);
        Assert.Equal([Body, Fender], (await Baked()).Order(StringComparer.Ordinal));

        // It may grow to a whole vehicle (20 m); Ctrl+Z takes the settings back (edits in one pause are one step).
        page.SelectedDecal!.Size = 1500;
        Assert.Equal(1500f, page.Decals.Layers.Single().WidthCm);
        page.UndoDecalCommand.Execute(null);
        var back = Assert.Single(page.Decals.Layers);
        Assert.False(back.Wrap);
        Assert.True(back.WidthCm < 300f);
    }

    /// <summary>
    /// Review 3.5.0 (blocker): the decal list is rebuilt whenever a decal is placed or removed, and its rows did not take the
    /// layer's "Stick to the surface": the row said off, the size slider's end fell to 3 m, and re-selecting the 15 m decal in
    /// the real view cut it to 3 m and turned Wrap off. Now the rows keep both, whichever decal is picked in between.
    /// </summary>
    [AvaloniaFact]
    public async Task AWrappedDecalKeepsItsSizeAndWrapWhenTheListIsRebuiltAndPickedAgain()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Wrap");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(DecalsViewModelTests.Car(), "BPC_MyCar");
        var window = new Avalonia.Controls.Window { Width = 600, Height = 1000, Content = new ScumStudio.App.Views.Pages.VehicleStudioDecals { DataContext = page } };
        window.Show();
        try
        {
            HeadlessUi.Pump();
            page.ArmDecalCommand.Execute(page.DecalDesigns.Single(d => d.Design.Id == "stripes-racing"));
            Assert.True(page.PlaceDecal(DecalsViewModelTests.Ray(0)));
            HeadlessUi.Pump();
            var big = page.SelectedDecal!;
            big.Wrap = true;
            big.Size = 1500;
            HeadlessUi.Pump();
            var id = big.Layer.Id;

            // A second (small) decal rebuilds the list: the big one's row still says stuck, with the 20 m slider.
            page.ArmDecalCommand.Execute(page.DecalDesigns.Single(d => d.Design.Id == "stripes-racing"));
            Assert.True(page.PlaceDecal(DecalsViewModelTests.Ray(0)));
            HeadlessUi.Pump();
            var row = page.DecalLayers.Single(l => l.Layer.Id == id);
            Assert.True(row.Wrap);
            Assert.Equal(DecalLayerViewModel.MaxSizeCm, row.SizeSliderMax);
            var small = page.DecalLayers.Single(l => l.Layer.Id != id);
            var smallWidth = small.Layer.WidthCm;

            // Picked in the view, back and forth: neither decal changes.
            foreach (var pick in new[] { row, small, row, small, row })
            {
                page.SelectedDecal = pick;
                HeadlessUi.Pump();
            }

            Assert.Contains(page.Decals.Layers, l => l.Id == id && l.WidthCm == 1500f && l.Wrap);
            Assert.Contains(page.Decals.Layers, l => l.Id != id && l.WidthCm == smallWidth && !l.Wrap);

            // Any edit of the listed big decal keeps it stuck to the surface.
            page.SelectedDecal!.Rotation = 10;
            HeadlessUi.Pump();
            Assert.Contains(page.Decals.Layers, l => l.Id == id && l.WidthCm == 1500f && l.Wrap && l.RotationDeg == 10f);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Review 3.5.0: at the smallest window (1024x640) the Decals tab is about 350 px high, and the library above the layers
    /// took all of it: Apply decals and Undo were squashed to 5 px. The bar keeps its height and the rest scrolls.
    /// </summary>
    [AvaloniaFact]
    public async Task TheApplyBarKeepsItsHeightInTheSmallestDecalsTab()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Small");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(DecalsViewModelTests.Car(), "BPC_MyCar");
        var view = new ScumStudio.App.Views.Pages.VehicleStudioDecals { DataContext = page };
        var window = new Avalonia.Controls.Window { Width = 330, Height = 348, Content = view };
        window.Show();
        try
        {
            page.ArmDecalCommand.Execute(page.DecalDesigns.Single(d => d.Design.Id == "stripes-racing"));
            Assert.True(page.PlaceDecal(DecalsViewModelTests.Ray(0)));
            HeadlessUi.Pump();
            var apply = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Button>(view, "ApplyDecalsButton")!;
            var top = apply.TranslatePoint(new Point(0, 0), window)!.Value;
            Assert.True(apply.Bounds.Height >= 24, $"Apply decals is {apply.Bounds.Height} px high");
            Assert.True(top.Y >= 0 && top.Y + apply.Bounds.Height <= window.Height + 0.5, $"Apply decals at {top.Y}..{top.Y + apply.Bounds.Height}, the tab is {window.Height} high");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ADesignDroppedOnTheViewIsArmedAndClickedWhereItWasLetGo()
    {
        var view = new MeshPreview { Tool = PreviewTool.Surface };
        var item = new DecalDesignItem(DecalLibrary.Find("skull-plain")!, 1);
        DecalDesignItem? armed = null;
        view.SurfaceDrop += (_, e) => armed = e.Data.Get(MeshPreview.DecalDragFormat) as DecalDesignItem;
        var data = new DataObject();
        data.Set(MeshPreview.DecalDragFormat, item);
        var drop = new DragEventArgs(DragDrop.DropEvent, data, view, new Point(40, 30), KeyModifiers.None);

        view.OnDecalDrop(drop, new Point(40, 30));
        Assert.Same(item, armed);
        Assert.Equal(2, view.PendingSurfaceCount); // the press and the release of a click at the drop point
        Assert.True(drop.Handled);

        // Anything else, or another tool than the surface, is not taken.
        var other = new DataObject();
        other.Set("Text", "hello");
        var ignored = new DragEventArgs(DragDrop.DropEvent, other, view, new Point(1, 1), KeyModifiers.None);
        view.OnDecalDrop(ignored, new Point(1, 1));
        Assert.Equal(2, view.PendingSurfaceCount);
        view.Tool = PreviewTool.Orbit;
        var over = new DragEventArgs(DragDrop.DragOverEvent, data, view, new Point(1, 1), KeyModifiers.None);
        view.OnDecalDragOver(over);
        Assert.Equal(DragDropEffects.None, over.DragEffects);
    }

    [Fact]
    public async Task AnAddedStickerIsListedAndArmedAndAnUnreadableFileSaysSo()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Stickers");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(DecalsViewModelTests.Car(), "BPC_MyCar");
        Assert.Empty(page.StickerItems);

        var bad = ctx.Combine("not-a-picture.png");
        await File.WriteAllTextAsync(bad, "hello");
        Assert.False(page.ImportSticker(bad));
        Assert.Empty(page.StickerItems);

        var png = ctx.Combine("sticker.png");
        await ScumStudio.Rendering.Imaging.ImageExport.SavePngAsync(Enumerable.Repeat((byte)200, 32 * 32 * 4).ToArray(), 32, 32, png);
        Assert.True(page.ImportSticker(png));
        var sticker = Assert.Single(page.StickerItems);
        Assert.Same(sticker, page.ArmedDecal);
        Assert.Contains(sticker, page.DecalDesigns);
        Assert.True(page.PlaceDecal(DecalsViewModelTests.Ray(0)));
        Assert.Equal(sticker.StickerFile, page.Decals.Layers.Single().StickerFile);
    }

    /// <summary>Review 3.5.0: the Rager's "Name in game" box showed its text-table reference; such a name leaves the box empty.</summary>
    [Theory]
    [InlineData("[/Game/ConZ_Files/Items/StringTables/ST_UI_Items.ST_UI_Items:RagerItemContainer]", true)]
    [InlineData("AKM", false)]
    [InlineData("[Gold] Rager", false)]
    [InlineData("", false)]
    public void ANameFromTheGamesTextTableIsNotPutInTheNameBox(string caption, bool reference) =>
        Assert.Equal(reference, ModulePageViewModel.IsTextTableReference(caption));

    [Theory]
    [InlineData("Rager_1", "Rager_v1")]
    [InlineData("Rager_12", "Rager_v12")]
    [InlineData("Rager1", "Rager1")]
    [InlineData("Rager_v2", "Rager_v2")]
    [InlineData("Rager_01", "Rager_01")]
    public void ANameEndingInUnderscoreNumberIsSavedWithV(string typed, string saved)
    {
        Assert.Equal(saved, CloneFamilyPlanner.SafeName(typed));
        Assert.True(CloneFamilyPlanner.IsValidName(CloneFamilyPlanner.SafeName(typed)));
        Assert.Equal("Rager3", CloneFamilyPlanner.NumberedName("Rager", 3));
        Assert.Equal("SF19_v1", CloneFamilyPlanner.NumberedName("SF19", 1)); // a stem ending in a digit
        Assert.True(CloneFamilyPlanner.IsValidName(CloneFamilyPlanner.NumberedName("WolfsWagen", 1)));
    }

    /// <summary>Two panels side by side on the +Y side: the body tube and, after it along X, a fender tube of another material.</summary>
    private static PreviewModel TwoPanels() => new("Car",
    [
        new PreviewPart("Door", DecalMeshes.Cylinder(50, 100, 128, 32), null, FTransform.Identity) { Material = Body, Attachment = "/Game/Cars/BPC_MyCar_Door_Left" },
        new PreviewPart("Fender", DecalMeshes.Cylinder(50, 100, 128, 32), null, new FTransform(new FVector(110, 0, 0))) { Material = Fender, Attachment = "/Game/Cars/BPC_MyCar_Fender" },
    ], new Dictionary<string, TextureImage>());

    private static TextureImage Grey() => Image(128, 128, 128, 128, srgb: true);

    private static TextureImage White() => Image(128, 255, 255, 255, srgb: false);

    private static bool Changed(TextureImage baked) => baked.Rgba.Where((v, i) => i % 4 != 3).Any(v => v != 128);

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
