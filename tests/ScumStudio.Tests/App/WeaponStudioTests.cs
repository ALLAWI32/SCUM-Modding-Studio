using System.Numerics;
using Avalonia.Media;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Tuning;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>The Weapons studio round (r37) without game files: list order, gun look, paint strength, magazines apart, the person.</summary>
public sealed class WeaponStudioTests
{
    /// <summary>Owner: "the page opens on the dildo; make the AKM the default and the top, firearms first".</summary>
    [Fact]
    public void FirearmsComeFirstWithTheAkmOnTop()
    {
        string[] names =
        [
            "1H_Beam_Dildo", "1H_Dildo", "2H_Katana", "Weapon_590A11", "Weapon_98k_Karabiner", "Weapon_AK15", "Weapon_AK47", "Weapon_AKM",
            "Weapon_M1911", "Weapon_MP5", "Weapon_RPK-74", "Weapon_SVD_Dragunov", "Weapon_Improvised_Rifle", "Weapon_ImprovisedGrenadeLauncher",
            "Weapon_CarbonHunter", "Weapon_Parts_AKM", "Weapon_NewGunFromAnUpdate",
        ];
        var sorted = names.Select(n => (Name: n, Rank: WeaponFamilies.Rank(n, n.StartsWith("Weapon_Parts", StringComparison.Ordinal) ? "Weapon_Parts" : n.Contains("H_", StringComparison.Ordinal) ? ModdableAssets.MeleeCategory : "Ranged_Weapons")))
            .OrderBy(e => e.Rank.Family).ThenBy(e => e.Rank.Order).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => e.Name).ToList();

        Assert.Equal(["Weapon_AKM", "Weapon_AK47", "Weapon_AK15"], sorted.Take(3));
        Assert.True(sorted.IndexOf("Weapon_NewGunFromAnUpdate") < sorted.IndexOf("1H_Beam_Dildo"), "every firearm before the melee weapons");
        Assert.Equal("Weapon_Parts_AKM", sorted[^1]);
        Assert.Equal(WeaponFamily.Launcher, WeaponFamilies.FamilyOf("Weapon_ImprovisedGrenadeLauncher", "Ranged_Weapons"));
        Assert.Equal(WeaponFamily.Improvised, WeaponFamilies.FamilyOf("Weapon_Improvised_Rifle", "Ranged_Weapons"));
        Assert.Equal(WeaponFamily.MachineGun, WeaponFamilies.FamilyOf("Weapon_RPK-74", "Ranged_Weapons"));
        Assert.Equal(WeaponFamily.Melee, WeaponFamilies.FamilyOf("1H_Dildo", ModdableAssets.MeleeCategory));
    }

    /// <summary>
    /// Owner: "weapon textures look broken". The look carries the colour darkened by the packed map's occlusion and the
    /// metalness in alpha (the colour's alpha is the roughness, which goes into the range instead).
    /// </summary>
    [Fact]
    public void TheGunLookTakesOcclusionAndMetalFromThePackedMap()
    {
        // Pixel 0: red-brown wood, rough (alpha 230); pixel 1: grey metal, smooth (alpha 60).
        var colour = Image("T_Gun_D", [200, 100, 50, 230, 80, 80, 80, 60]);
        // AOMEC: wood fully lit and bare; metal half occluded and metallic.
        var packed = Image("T_Gun_M", [255, 0, 0, 128, 128, 255, 0, 128]);

        var look = WeaponLook.Bake(colour, packed, 0.15f, 1f, 0.75f);

        Assert.Equal([200, 100, 50, 0], look.Image.Rgba[..4]); // wood as it is, no metal
        Assert.Equal(255, look.Image.Rgba[7]);                   // metal where the packed map says
        Assert.InRange(look.Image.Rgba[4], 57, 60);              // grey 80 at half the light (linear), back in sRGB
        Assert.Equal(0.15f + (0.85f * 230f / 255f), look.Roughness.X, 3); // the wood's roughness
        Assert.Equal(0.15f + (0.85f * 60f / 255f), look.Roughness.Y, 3);  // the metal's
        Assert.Equal(0.75f, look.Metal);

        var flat = WeaponLook.Bake(colour, packed, 0.15f, 1f, 0.75f, flat: true);
        Assert.Equal([255, 255, 255, 0], flat.Image.Rgba[..4]); // the even coat: flat white, fully rough
        Assert.Equal(1f, flat.Roughness.X, 3);
    }

    /// <summary>Owner: "paint far too bright and saturated: blend with the base texture, a sensible default, an intensity slider".</summary>
    [Fact]
    public void AWeaponTintStartsGentleAndTheStrengthSliderBlendsIt()
    {
        var paint = Tint();
        Assert.False(paint.Plain); // over the gun's own print, not the flat white coat
        Assert.Equal(PaintMaterialViewModel.DefaultStrength, paint.Strength, 3);
        Assert.True(paint.HasStrength);

        paint.Colour = Color.FromRgb(255, 0, 0);
        var full = PaintMaterialViewModel.ToLinear(paint.Colour) * 2f; // the tint gain of this paint
        var s = (float)PaintMaterialViewModel.DefaultStrength;
        Assert.Equal(1f + ((full.X - 1f) * s), paint.LinearColour.X, 3);
        Assert.Equal(1f - s, paint.LinearColour.Y, 3); // green: from white towards none, 70 % of the way

        paint.Strength = 0;
        Assert.Equal(Vector4.One, paint.LinearColour); // nothing: the gun as the game has it
        paint.Strength = 1;
        Assert.Equal(full.X, paint.LinearColour.X, 3);

        var (value, text) = Assert.Single(paint.Changes());
        Assert.Same(paint.ColourValue, value);
        Assert.Equal(TunableValue.Format(paint.LinearColour.X, paint.LinearColour.Y, paint.LinearColour.Z, 1f), text);

        // After Apply the strength shown stays (the stored tint is what it and the colour make).
        paint.Strength = 0.4;
        var applied = TunableValue.Format(paint.LinearColour.X, paint.LinearColour.Y, paint.LinearColour.Z, 1f);
        paint.Rebase(v => v == paint.ColourValue ? applied : v.Committed);
        Assert.Equal(0.4, paint.Strength, 3);
        Assert.False(paint.IsPending);
    }

    /// <summary>Owner: "magazines are shown NEXT TO the weapon, not hidden inside". The gun mesh's magazine sections go under it, side by side.</summary>
    [Fact]
    public void TheGunsMagazinesAreLaidOutUnderIt()
    {
        var gun = Part("SK_Gun / MI_Gun", "/Game/W/MI_Gun.MI_Gun", new Vector3(-40, -3, -5), new Vector3(40, 3, 5));
        var drum = Part("SK_Gun / MI_RPK_New_Drum", "/Game/W/MI_RPK_New_Drum.MI_RPK_New_Drum", new Vector3(-5, -8, -20), new Vector3(10, 8, 0));
        var mag = Part("SK_Gun / MI_AK47_Magazine", "/Game/W/MI_AK47_Magazine.MI_AK47_Magazine", new Vector3(-3, -2, -25), new Vector3(8, 2, 0));
        var model = new PreviewModel("Weapon_Gun", [gun, drum, mag], new Dictionary<string, TextureImage>());

        var apart = ModulePageViewModel.WithMagazinesApart(model)!;

        Assert.Same(gun, apart.Parts[0]);
        var (gunMin, _) = ModulePageViewModel.UeBounds([apart.Parts[0]]);
        var (drumMin, drumMax) = ModulePageViewModel.UeBounds([apart.Parts[1]]);
        var (magMin, magMax) = ModulePageViewModel.UeBounds([apart.Parts[2]]);
        Assert.True(drumMax.Z < gunMin.Z && magMax.Z < gunMin.Z, "both under the gun");
        Assert.True(drumMax.X < magMin.X || magMax.X < drumMin.X, "side by side, not inside each other");
        Assert.True(ModulePageViewModel.IsMagazineMaterial(drum.Material) && !ModulePageViewModel.IsMagazineMaterial(gun.Material));
        var alone = model with { Parts = [gun] };
        Assert.Same(alone, ModulePageViewModel.WithMagazinesApart(alone)); // no magazine: as it is
    }

    /// <summary>Owner: "the box-made character next to the weapons". Off on the Weapons page (the grid stays), on for vehicles.</summary>
    [Fact]
    public void ThePersonIsOffBesideWeaponsAndOnBesideVehicles()
    {
        using var ctx = AppTestContext.Create();
        using var weapons = new WeaponsPageViewModel(ctx.Services);
        using var vehicles = new VehiclesPageViewModel(ctx.Services);
        Assert.False(weapons.ShowScaleFigure);
        Assert.True(vehicles.ShowScaleFigure);
        weapons.ShowScaleFigure = true;
        Assert.True(weapons.ShowScaleFigure);
        Assert.Equal(ScumStudio.App.Localization.Loc.T("Weapons.Paint.Together"), weapons.PaintTogetherText);
        Assert.Equal(ScumStudio.App.Localization.Loc.T("Weapons.Part.Magazine"), ModulePageViewModel.WeaponPartLabel("/Game/W/MI_RPK_New_Drum.MI_RPK_New_Drum", 0));
        Assert.Equal(ScumStudio.App.Localization.Loc.T("Weapons.Part.Body"), ModulePageViewModel.WeaponPartLabel("/Game/W/MI_AKM.MI_AKM", 0));
    }

    /// <summary>The names built from a family or a part (not literal keys the source scan finds) exist in English (and so in every language).</summary>
    [Fact]
    public void EveryFamilyAndPartHasAName()
    {
        var english = ScumStudio.App.Localization.Loc.Table("en");
        Assert.All(Enum.GetValues<WeaponFamily>(), f => Assert.True(english.ContainsKey(WeaponsPageViewModel.FamilyKey(f)), f.ToString()));
        Assert.All(new[] { "Magazine", "Stock", "Sight", "Muzzle", "Handguard", "Rail", "Bipod", "Strap", "Ammo", "Body", "Metal", "Furniture", "Other", "Whole" },
            p => Assert.True(english.ContainsKey("Weapons.Part." + p), p));
    }

    private static TextureImage Image(string name, byte[] rgba) => new(name, rgba.Length / 4, 1, rgba, "PF_B8G8R8A8", 0, rgba.Length / 4, 1, true, false);

    private static PaintMaterialViewModel Tint()
    {
        const string Package = "/Game/W/MI_Gun";
        var export = "MI_Gun";
        return new PaintMaterialViewModel(Package + ".MI_Gun", "Gun",
            new PaintValue(Package, new Tunable(export, "VectorParameterValues[Difuse_Colorization]", TunableKind.Color, string.Empty), string.Empty), null,
            new PaintValue(Package, new Tunable(export, "ScalarParameterValues[MetallicAmount]", TunableKind.Float, "0.75"), "0.75"), null,
            null, new PaintValue(Package, new Tunable(export, "TextureParameterValues[Color]", TunableKind.Text, "/Game/W/T_Gun_D.T_Gun_D"), "/Game/W/T_Gun_D.T_Gun_D"),
            tint: true, tintGain: 2f);
    }

    private static PreviewPart Part(string name, string material, Vector3 min, Vector3 max)
    {
        float[] positions = [min.X, min.Y, min.Z, max.X, min.Y, min.Z, max.X, max.Y, max.Z, min.X, max.Y, max.Z];
        var mesh = MeshData.Create(name, positions, [0, 1, 2, 0, 2, 3], [], []);
        return new PreviewPart(name, mesh, null, FTransform.Identity) { Material = material };
    }
}

/// <summary>The Weapons studio's part picking on the GPU: a paint row's eye selects the parts drawn with its material.</summary>
[Collection(ScumStudio.Tests.Rendering.GlCollection.Name)]
public sealed class WeaponStudioGlTests
{
    /// <summary>Owner: "let the user pick parts of the weapon one by one". A material path selects its parts; a vehicle attachment still selects its own.</summary>
    [ScumStudio.Tests.Rendering.GlFact]
    public void AMaterialSelectsThePartsDrawnWithIt()
    {
        using var gl = ScumStudio.Tests.Rendering.GlHarness.Create(64, 64);
        float[] box = [0, 0, 0, 10, 0, 0, 10, 10, 10, 0, 10, 10];
        PreviewPart Part(string material, string attachment = "") =>
            new(material, MeshData.Create(material, box, [0, 1, 2, 0, 2, 3]), null, FTransform.Identity) { Material = material, Attachment = attachment };
        var model = new PreviewModel("Gun", [Part("/Game/W/MI_Gun.MI_Gun"), Part("/Game/W/MI_Drum.MI_Drum"), Part("/Game/W/MI_Drum.MI_Drum"), Part("/Game/V/MI_Car.MI_Car", "/Game/V/BPC_Car_Door")],
            new Dictionary<string, TextureImage>());
        using var scene = PreviewScene.Upload(gl.Renderer, model);

        Assert.Equal(2, scene.Highlight("/Game/W/MI_Drum.MI_Drum"));
        Assert.Equal(1, scene.Highlight("/Game/V/BPC_Car_Door"));
        Assert.Equal(0, scene.Highlight(string.Empty));
    }
}
