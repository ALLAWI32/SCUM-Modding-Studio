using System.Numerics;
using Avalonia.Input;
using Avalonia.Media;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Textures;
using ScumStudio.Viewport.Decals;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner (2026-10-16): vehicle decals "like the game's tattoos, and more control from outside". On the synthetic car of
/// <see cref="DecalsViewModelTests"/> (a tube round the X axis, radius 50 cm): a game tattoo cut from an atlas is placed
/// and tinted like a design; the keys and the wheel move, turn and size the selected decal; "Other side" puts a mirrored
/// copy on the mirrored surface; hide and lock; exact position and size fields; presets and copying from another clone
/// give the same bake; an SVG becomes a sticker with a colour per colour. Each action is one undo step.
/// </summary>
public sealed class DecalToolsTests
{
    private const string Body = "/Game/Cars/MI_Body.MI_Body";

    [Fact]
    public void AGameTattooSitsInItsChannelAndCellAndIsCutToItsInk()
    {
        var tattoo = new GameTattoo(37, 1f, []); // green channel, cell 5: row 1, column 1
        Assert.Equal((1, 1, 1), (tattoo.Channel, tattoo.Column, tattoo.Row));
        Assert.Equal("game-tattoo-37", tattoo.Id);
        Assert.Equal(37, GameTattoos.IndexOf(tattoo.Id));
        Assert.Null(GameTattoos.IndexOf("flames-1"));

        var cut = GameTattoos.Cut(Atlas(tattoo), tattoo);
        Assert.InRange(cut.Width, 32, 36); // the 32 x 16 ink and a margin of a texel
        Assert.InRange(cut.Height, 16, 20);
        Assert.Single(cut.Planes);
        Assert.Equal(1f, cut.Planes[0][(cut.Height / 2 * cut.Width) + (cut.Width / 2)], 2);
        Assert.Equal(0f, cut.Planes[0][0]);
        var design = GameTattoos.Design(tattoo, cut);
        Assert.Equal(cut.Width / (float)cut.Height, design.Aspect, 3);
        Assert.Single(design.Regions); // one colour, as the game tints it
    }

    [Fact]
    public async Task AGameTattooIsPlacedWithItsShapeAndTintedLikeADesign()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Tattoos");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(DecalsViewModelTests.Car(), "BPC_MyCar");
        var tattoo = new GameTattoo(37, 1f, ["ArmUpperLeft"]);
        page.UseGameTattoos([tattoo], Atlas(tattoo));
        page.DecalCategory = ModulePageViewModel.DecalCategories.Single(c => c.Key == GameTattoos.Category);
        var item = Assert.Single(page.DecalDesigns);
        Assert.Same(tattoo, item.Tattoo);

        page.ArmDecalCommand.Execute(item);
        await page.EnsureGameTattooAsync(tattoo.Id);
        Assert.True(page.PlaceDecal(DecalsViewModelTests.Ray(0)));
        var layer = Assert.Single(page.Decals.Layers);
        Assert.Equal(tattoo.Id, layer.DesignId);
        Assert.InRange(layer.WidthCm / layer.HeightCm, 1.6f, 2.2f); // the ink's 2:1, not the cell's square
        Assert.False(page.SelectedDecal!.IsSticker);
        Assert.Single(page.SelectedDecal.Regions);

        var (diffuse, mask) = (DecalDragTimingTests.Image(256, 200, 200, 200, srgb: true), DecalDragTimingTests.Image(4, 0, 255, 0, srgb: false));
        var under = (((256 / 2) * 256) + 1) * 4;
        var ink = page.ComposeDecals(Body, diffuse, mask).Diffuse;
        Assert.True(ink.Rgba[under] < 60 && ink.Rgba[under + 1] < 60, "the game's dark ink");
        page.SelectedDecal.Regions[0].Colour = Colors.Red; // the tint, like the game's TattooColor
        var red = page.ComposeDecals(Body, diffuse, mask).Diffuse;
        Assert.True(red.Rgba[under] > 200 && red.Rgba[under + 1] < 60, $"red tint {red.Rgba[under]},{red.Rgba[under + 1]}");
    }

    [Fact]
    public async Task OtherSidePutsAMirroredCopyOnTheMirroredSurfaceAsOneStep()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Decals");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(DecalsViewModelTests.Car(), "BPC_MyCar");
        page.ArmDecalCommand.Execute(page.DecalDesigns.Single(d => d.Design.Id == "flames-1"));
        Assert.True(page.PlaceDecal(DecalsViewModelTests.Ray(12)));
        page.SelectedDecal!.Rotation = 20;
        var original = page.Decals.Layers.Single();

        Assert.True(page.MirrorToOtherSide());
        Assert.Equal(2, page.Decals.Layers.Count);
        var copy = page.Decals.Layers.Single(l => l.Id != original.Id);
        Assert.Equal(new Vector3(original.Position.X, -original.Position.Y, original.Position.Z), copy.Position, new VectorClose(0.6f));
        Assert.True(Vector3.Dot(copy.Normal, -Vector3.UnitY) > 0.99f);
        Assert.Equal(!original.FlipX, copy.FlipX);
        Assert.Equal(-20f, copy.RotationDeg);
        Assert.Equal((original.WidthCm, original.HeightCm, original.DesignId), (copy.WidthCm, copy.HeightCm, copy.DesignId));
        Assert.Equal(copy.Id, page.SelectedDecal!.Layer.Id);

        page.UndoDecalCommand.Execute(null); // one step
        Assert.Equal(original.Id, Assert.Single(page.Decals.Layers).Id);
    }

    [Fact]
    public async Task KeysAndTheWheelMoveTurnAndSizeTheSelectedDecalButNotALockedOne()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Decals");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(DecalsViewModelTests.Car(), "BPC_MyCar");
        page.StudioTab = StudioTab.Decals;
        page.ArmDecalCommand.Execute(page.DecalDesigns.Single(d => d.Design.Id == "skull-plain"));
        Assert.True(page.PlaceDecal(DecalsViewModelTests.Ray(0)));
        var start = page.Decals.Layers.Single();

        Assert.True(page.OnStudioKey(Key.Right, KeyModifiers.None)); // along the decal: +X on this side
        Assert.True(page.OnStudioKey(Key.Right, KeyModifiers.Shift));
        Assert.Equal(11f, page.Decals.Layers.Single().Position.X, 1);
        Assert.Equal(50f, page.Decals.Layers.Single().Position.Y, 0); // still on the surface
        Assert.True(page.OnStudioKey(Key.E, KeyModifiers.None));
        Assert.True(page.OnStudioWheel(1, KeyModifiers.Shift));
        Assert.Equal(10f, page.Decals.Layers.Single().RotationDeg, 3);
        Assert.True(page.OnStudioWheel(1, KeyModifiers.Control));
        Assert.Equal(start.WidthCm * 1.1f, page.Decals.Layers.Single().WidthCm, 2);
        Assert.Equal(start.HeightCm * 1.1f, page.Decals.Layers.Single().HeightCm, 2);
        Assert.True(page.OnStudioKey(Key.X, KeyModifiers.None));
        Assert.True(page.Decals.Layers.Single().FlipX);
        Assert.False(page.OnStudioWheel(1, KeyModifiers.None)); // the plain wheel zooms the camera
        Assert.Equal(11f, page.SelectedDecal!.X, 1); // the position fields follow

        page.UndoDecalCommand.Execute(null); // a burst of keys is one step, like a slider dragged
        Assert.Equal(start, page.Decals.Layers.Single());

        page.ToggleDecalLockedCommand.Execute(page.SelectedDecal);
        Assert.True(page.Decals.Layers.Single().Locked);
        Assert.False(page.OnStudioKey(Key.Right, KeyModifiers.None));
        Assert.False(page.OnStudioWheel(1, KeyModifiers.Control));
        Assert.False(page.OnStudioKey(Key.Delete, KeyModifiers.None));
        Assert.False(page.DragDecal(DecalsViewModelTests.Ray(20), commit: true));
        Assert.Equal(start.Position, page.Decals.Layers.Single().Position);
        page.SelectedDecal = null;
        page.ArmDecalCommand.Execute(null);
        Assert.False(page.PlaceDecal(DecalsViewModelTests.Ray(0))); // a locked decal cannot be picked in the view
        page.UndoDecalCommand.Execute(null);
        Assert.False(page.Decals.Layers.Single().Locked);
    }

    [Fact]
    public async Task AHiddenDecalStaysListedButIsNotBakedAndHideIsOneStep()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Decals");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(DecalsViewModelTests.Car(), "BPC_MyCar");
        page.ArmDecalCommand.Execute(page.DecalDesigns.Single(d => d.Design.Id == "shape-heart"));
        Assert.True(page.PlaceDecal(DecalsViewModelTests.Ray(0)));
        var (diffuse, mask) = (DecalDragTimingTests.Image(128, 128, 128, 128, srgb: true), DecalDragTimingTests.Image(4, 0, 255, 0, srgb: false));
        Assert.NotSame(diffuse, page.ComposeDecals(Body, diffuse, mask).Diffuse);

        page.ToggleDecalHiddenCommand.Execute(page.DecalLayers.Single());
        Assert.True(page.DecalLayers.Single().IsHidden);
        Assert.Same(diffuse, page.ComposeDecals(Body, diffuse, mask).Diffuse);
        page.UndoDecalCommand.Execute(null);
        Assert.False(page.Decals.Layers.Single().Hidden);
    }

    [Fact]
    public async Task ExactPositionAndSizeFieldsPutTheDecalOnTheSurface()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Decals");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(DecalsViewModelTests.Car(), "BPC_MyCar");
        page.ArmDecalCommand.Execute(page.DecalDesigns.Single(d => d.Design.Id == "flames-1"));
        Assert.True(page.PlaceDecal(DecalsViewModelTests.Ray(0)));
        var item = page.SelectedDecal!;
        Assert.Equal(50, item.Y, 0);

        item.X = 20;
        Assert.Equal(new Vector3(20, 50, 0), page.Decals.Layers.Single().Position, new VectorClose(0.6f));
        item.Y = 70; // off the car: back onto the surface along the normal
        Assert.Equal(50f, page.Decals.Layers.Single().Position.Y, 0);
        Assert.Equal(50, item.Y, 0);

        item.Height = 30; // stretches
        Assert.Equal((40f, 30f), (page.Decals.Layers.Single().WidthCm, page.Decals.Layers.Single().HeightCm));
        item.Size = 80; // the shape follows the width
        Assert.Equal((80f, 60f), (page.Decals.Layers.Single().WidthCm, page.Decals.Layers.Single().HeightCm));
        Assert.Equal(60, item.Height, 1);
    }

    [Fact]
    public async Task APresetOrAnotherClonesDecalsGiveTheSameBakeAndAreOneStep()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Decals");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(DecalsViewModelTests.Car(), "BPC_First");
        foreach (var (id, x) in new[] { ("flames-1", -20f), ("shape-heart", 25f) })
        {
            page.ArmDecalCommand.Execute(page.DecalDesigns.Single(d => d.Design.Id == id));
            Assert.True(page.PlaceDecal(DecalsViewModelTests.Ray(x)));
        }

        page.SelectedDecal!.Rotation = 30;
        var (diffuse, mask) = (DecalDragTimingTests.Image(256, 90, 120, 160, srgb: true), DecalDragTimingTests.Image(4, 0, 255, 0, srgb: false));
        var first = page.ComposeDecals(Body, diffuse, mask);
        Assert.True(page.SaveDecalPresetAs("Side flames"));
        Assert.Contains("Side flames", page.DecalPresets);

        page.UseDecalTarget(DecalsViewModelTests.Car(), "BPC_Second");
        Assert.Contains("BPC_First", page.DecalSourceClones);
        Assert.True(page.AddDecalPreset("Side flames"));
        Assert.Equal(2, page.Decals.Layers.Count);
        Assert.Empty(page.Decals.Layers.Select(l => l.Id).Intersect(DecalSet.Load(ctx.Services.Projects.Current!.DirectoryPath, "BPC_First").Layers.Select(l => l.Id)));
        var second = page.ComposeDecals(Body, diffuse, mask);
        Assert.Equal(first.Diffuse.Rgba, second.Diffuse.Rgba);
        Assert.Equal(first.Mask.Rgba, second.Mask.Rgba);
        page.UndoDecalCommand.Execute(null);
        Assert.Empty(page.Decals.Layers);

        Assert.True(page.CopyDecalsFrom("BPC_First"));
        Assert.Equal(first.Diffuse.Rgba, page.ComposeDecals(Body, diffuse, mask).Diffuse.Rgba);
        page.UndoDecalCommand.Execute(null);
        Assert.Same(diffuse, page.ComposeDecals(Body, diffuse, mask).Diffuse); // byte for byte the textures it had
    }

    [Fact]
    public void AnSvgDrawingBecomesADesignWithARegionPerColour()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 100" width="400" height="200">
              <rect x="0" y="0" width="100" height="100" fill="#ff0000"/>
              <g transform="translate(150 50)" style="fill:rgb(0,0,255)">
                <circle cx="0" cy="0" r="40"/>
              </g>
              <path d="m 110 10 h 20 v 20 a 10 10 0 0 1 -20 0 z" fill="#0000ff"/>
              <path d="M0 0 L10 10" fill="none"/>
            </svg>
            """;
        var design = SvgDecal.Parse(svg, "svg:test");
        Assert.Equal((200f, 100f), (design.Width, design.Height));
        Assert.Equal([0xFF0000u, 0x0000FFu], design.Regions.Select(r => r.Colour)); // the two blue shapes in a row are one region
        var raster = design.Rasterize(200);
        Assert.Equal(1f, raster.Planes[0][(50 * 200) + 50], 2); // inside the red square
        Assert.Equal(1f, raster.Planes[1][(50 * 200) + 150], 2); // inside the blue circle, moved by its group
        Assert.Equal(0f, raster.Planes[1][(95 * 200) + 195], 2); // outside everything
        Assert.Equal(0f, raster.Planes[0][(50 * 200) + 150], 2);
        Assert.Throws<FormatException>(() => SvgDecal.Parse("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", "svg:empty"));
        Assert.Throws<FormatException>(() => SvgDecal.Parse("not xml", "svg:bad"));
    }

    [Fact]
    public async Task AnImportedSvgIsAStickerWithColoursAndAPngKeepsItsTransparency()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Decals");
        using var page = new VehiclesPageViewModel(ctx.Services);
        page.UseDecalTarget(DecalsViewModelTests.Car(), "BPC_MyCar");
        var svg = ctx.Combine("logo.svg");
        await File.WriteAllTextAsync(svg, """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 20"><rect width="20" height="20" fill="#00ff00"/><rect x="20" width="20" height="20" fill="#ffffff"/></svg>""");
        Assert.True(page.ImportSticker(svg));
        Assert.True(page.PlaceDecal(DecalsViewModelTests.Ray(0)));
        var layer = page.Decals.Layers.Single();
        Assert.EndsWith(".svg", layer.StickerFile);
        Assert.Null(layer.DesignId);
        Assert.Equal(20f, layer.HeightCm, 2); // 40 cm wide, the drawing's 2:1
        Assert.False(page.SelectedDecal!.IsSticker); // its colours can be picked
        Assert.Equal(2, page.SelectedDecal.Regions.Count);

        var png = ctx.Combine("half.png");
        using (var image = new Image<Rgba32>(8, 4))
        {
            for (var x = 0; x < 8; x++)
            {
                for (var y = 0; y < 4; y++)
                {
                    image[x, y] = x < 4 ? new Rgba32(255, 0, 0, 255) : new Rgba32(0, 0, 0, 0);
                }
            }

            await image.SaveAsPngAsync(png);
        }

        Assert.True(page.ImportSticker(png));
        Assert.True(page.PlaceDecal(DecalsViewModelTests.Ray(-30)));
        var sticker = DecalLibrary.LoadSticker(Path.Combine(ctx.Services.Projects.Current!.DirectoryPath, page.Decals.Ordered[^1].StickerFile!));
        Assert.Equal(1f, sticker.Rgba[3]);
        Assert.Equal(0f, sticker.Rgba[(7 * 4) + 3]); // the clear half stays clear
        Assert.False(page.ImportSticker(ctx.Combine("missing.svg")));
    }

    /// <summary>A synthetic tattoo atlas (256 x 512: cells of 64 x 64) with a 32 x 16 block of ink where <paramref name="tattoo"/> sits.</summary>
    private static TextureImage Atlas(GameTattoo tattoo)
    {
        const int w = 256, h = 512;
        var rgba = new byte[w * h * 4];
        var (x0, y0) = ((tattoo.Column * 64) + 16, (tattoo.Row * 64) + 24);
        for (var y = y0; y < y0 + 16; y++)
        {
            for (var x = x0; x < x0 + 32; x++)
            {
                rgba[(((y * w) + x) * 4) + tattoo.Channel] = 255;
            }
        }

        for (var i = 3; i < rgba.Length; i += 4)
        {
            rgba[i] = 255;
        }

        return new TextureImage("Atlas", w, h, rgba, "PF_BC7", 0, w, h, false, false);
    }

    private sealed class VectorClose(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 x, Vector3 y) => Vector3.Distance(x, y) <= tolerance;

        public int GetHashCode(Vector3 obj) => 0;
    }
}
