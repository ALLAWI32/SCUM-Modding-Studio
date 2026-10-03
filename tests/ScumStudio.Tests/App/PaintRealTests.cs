using Avalonia.Headless.XUnit;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "make it gold, make it pink". Real game files only (<c>SCUM_PAKS</c>, key from this PC's store): the Rager's
/// body paint is found, a finish is applied and recorded, and the exported material carries the new colour.
/// </summary>
public sealed class PaintRealTests
{
    [AvaloniaFact]
    public async Task TheRagerCanBePaintedGoldAndTheModCarriesIt()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Gold Rager");
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_Rager"));
            await vehicles.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.HasPaints, TimeSpan.FromSeconds(30)));

            var outer = vehicles.Paints.Single(p => p.MaterialPath.EndsWith(".MI_Rager_Outer", StringComparison.Ordinal));
            Assert.True(outer.HasMetal && outer.HasClearCoat);
            outer.ApplyFinishCommand.Execute(PaintFinish.All.Single(f => f.Key == "Gold"));
            Assert.True(vehicles.ApplyPaintCommand.CanExecute(null));
            vehicles.ShowPreviewTab = true;
            HeadlessUi.SaveScreenshot(window, "vehicles-rager-paint");
            // The 3D view paints through the colour mask (body gold, bare parts as they were) and makes the paint shine.
            var key = "#paint/" + outer.MaterialPath;
            Assert.Contains(vehicles.Preview!.Parts, p => p.Material == outer.MaterialPath && p.TexturePath == key && p.Surface == new System.Numerics.Vector2(1, 1));
            var baked = vehicles.Preview.Textures[key];
            var (painted, r, g, b) = (0, 0L, 0L, 0L);
            for (var i = 0; i < baked.Rgba.Length; i += 4)
            {
                if (baked.Rgba[i + 3] > 200)
                {
                    (painted, r, g, b) = (painted + 1, r + baked.Rgba[i], g + baked.Rgba[i + 1], b + baked.Rgba[i + 2]);
                }
            }

            Assert.InRange(painted, baked.Rgba.Length / 4 / 4, baked.Rgba.Length / 4 * 9 / 10); // paint on part of the texture, not all
            Assert.True(r > g * 1.15 && g > b * 1.5, $"painted texels average {r / painted},{g / painted},{b / painted}: not gold");
            vehicles.ApplyPaintCommand.Execute(null);

            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var package = ScumStudio.Modding.Catalog.ModdableAssets.ReadPackage(written, "/Game/ConZ_Files/Models/Vehicles2/Pickup/Materials/MI_Rager_Outer");
            var tunables = TunableReader.Read(package);
            var colour = TunableValue.ParseFloats(tunables.Single(t => t.Path == "VectorParameterValues[0].ParameterValue").Value, 4);
            var metal = tunables.Single(t => t.Path == outer.MetalValue!.Tunable.Path).Value;
            var coat = tunables.Single(t => t.Path == outer.ClearCoatValue!.Tunable.Path).Value;
            Assert.InRange(colour[0], 0.9f, 1.01f); // gold: strong red, mid green, little blue (linear)
            Assert.InRange(colour[1], 0.4f, 0.7f);
            Assert.InRange(colour[2], 0.01f, 0.1f);
            Assert.Equal("1", metal);
            Assert.Equal("1", coat);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>Owner: "the Rager I can paint; the plane, its colour I cannot change". The Kinglet's body is car paint too.</summary>
    [AvaloniaFact]
    public async Task ThePlaneCanBePaintedLikeACar()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Gold Plane");
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_Kinglet_Duster"));
            await vehicles.PreviewCompletion;
            var painted = HeadlessUi.PumpUntil(() => vehicles.HasPaints, TimeSpan.FromSeconds(30));
            Assert.True(painted, "no paint; preview parts: " + string.Join(", ", vehicles.Preview?.Parts.Select(p => p.Material).Distinct() ?? ["(no preview)"]));
            var body = vehicles.Paints.First(p => p.MaterialPath.EndsWith(".MI_Plane_01_Body_A", StringComparison.Ordinal));
            Assert.True(body.HasSecond);

            // As the game's icon shows it: a navy body (colour A, the mask's green) with tan wings (colour B, where it is
            // also blue), not the black coat a multiply with its very dark texture gave.
            var key = "#paint/" + body.MaterialPath;
            var (navy, tan) = (Average(vehicles.Preview!.Textures[key], warm: false), Average(vehicles.Preview.Textures[key], warm: true));
            Assert.True(navy.B > navy.R && navy.B > 60, $"body {navy}");
            Assert.True(tan.R > tan.B && tan.R > 100, $"wings {tan}");

            // Gold on the body: the navy turns gold (warm, bright), the wings keep their tan (colour B is its own swatch).
            body.ApplyFinishCommand.Execute(PaintFinish.All.Single(f => f.Key == "Gold"));
            Assert.True(vehicles.ApplyPaintCommand.CanExecute(null));
            var cool = Average(vehicles.Preview!.Textures[key], warm: false);
            var gold = Average(vehicles.Preview.Textures[key], warm: true);
            Assert.True(gold.R > 150 && gold.R > gold.B * 2, $"gold body {gold}, cool texels left {cool}");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>Owner: "weapons: change their colours exactly like the car". The AK-47 is tinted gold and the mod carries it.</summary>
    [AvaloniaFact]
    public async Task TheAkCanBePaintedGoldAndTheModCarriesIt()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Gold AK");
            var weapons = (WeaponsPageViewModel)vm.NavigateTo("weapons")!;
            await weapons.LoadCompletion;
            Assert.True(await weapons.SelectAsync("Weapon_AK47"));
            await weapons.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => weapons.HasPaints, TimeSpan.FromSeconds(30)),
                "no paint; preview parts: " + string.Join(", ", weapons.Preview?.Parts.Select(p => p.Material).Distinct() ?? ["(no preview)"]));

            // Left alone it is the game's AK; gold is an even gold coat, full metal, drawn without the wood and steel print.
            var body = weapons.Paints.First(p => p.MaterialPath.EndsWith(".MI_AK47_01", StringComparison.Ordinal));
            Assert.True(body.IsTint && !body.Painted && body.Plain && !weapons.ApplyPaintCommand.CanExecute(null));
            Assert.Equal(0.75, body.Metal, 3); // the metal the AK stores
            body.ApplyFinishCommand.Execute(PaintFinish.All.Single(f => f.Key == "Gold"));
            Assert.Contains(weapons.Preview!.Parts, p => p.Material == body.MaterialPath && p.TexturePath is null && p.Tint == body.LinearColour);
            body.Plain = false; // over its own texture: lifted so the gold does not come out mud
            Assert.True(body.Gain > 1f, $"gain {body.Gain}");
            Assert.Contains(weapons.Preview!.Parts, p => p.Material == body.MaterialPath && p.TexturePath is not null && p.Tint == body.LinearColour);
            body.Plain = true;
            weapons.ApplyPaintCommand.Execute(null);

            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var info = new ScumStudio.Assets.Materials.MaterialInspector(written).Inspect("/Game/ConZ_Files/Models/Weapons/Ranged_Weapons/Automatic_Rifles/AK47/Textures/MI_AK47_01.MI_AK47_01");
            var tint = info.Vectors.Single(v => v.Name == "Difuse_Colorization").Value;
            Assert.InRange(tint.X, 0.9f, 1.01f); // gold, as picked (the even coat needs no lift)
            Assert.InRange(tint.Y, 0.4f, 0.7f);
            Assert.InRange(tint.Z, 0.01f, 0.1f);
            Assert.Equal(1f, info.Scalars.Single(s => s.Name == "MetallicAmount").Value);
            Assert.EndsWith("T_FlatWhiteColor_Dummy_01", info.Textures.Single(t => t.Name == "Color").TexturePath, StringComparison.Ordinal);
            Assert.Contains(info.Vectors, v => v.Name == "Rust Colorization"); // the rest of the material is untouched
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>
    /// Owner: "make it a new car, Rager 1, Rager 2, with its own colours; the default stays". A clone gets copies of the
    /// meshes that wear paint and of the paint; painting it changes those copies only.
    /// </summary>
    [AvaloniaFact]
    public async Task ACloneIsPaintedOnItsOwnAndTheStockRagerStays()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Gold clone");
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_Rager"));
            vehicles.CloneName = "RagerGold";
            await vehicles.CreateCloneAsync();
            Assert.Equal("BPC_RagerGold", vehicles.SelectedItem!.Name);
            var clone = ctx.Services.Projects.Current!.State.FindCloneOf(vehicles.SelectedItem.PackagePath)!;
            var outerCopy = clone.Packages.Single(p => p.Old.EndsWith("/MI_Rager_Outer", StringComparison.Ordinal)).New;
            Assert.EndsWith("/MI_RagerGold_Outer", outerCopy, StringComparison.Ordinal);
            Assert.Contains(clone.Packages, p => p.Old.Contains("/Models/", StringComparison.Ordinal) && p.New.Contains("RagerGold", StringComparison.Ordinal)); // a mesh copy

            await vehicles.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.HasPaints, TimeSpan.FromSeconds(30)));
            Assert.False(vehicles.CloneSharesPaint);
            Assert.Contains("#SpawnVehicle BPC_RagerGold", vehicles.PaintNote, StringComparison.Ordinal);
            var outer = vehicles.Paints.Single(p => p.MaterialPath.EndsWith(".MI_Rager_Outer", StringComparison.Ordinal));
            Assert.Equal(outerCopy, outer.ColourValue.Package); // the edit goes to the clone's copy
            outer.ApplyFinishCommand.Execute(PaintFinish.All.Single(f => f.Key == "Gold"));
            vehicles.ApplyPaintCommand.Execute(null);

            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            Assert.False(written.PackageExists("/Game/ConZ_Files/Models/Vehicles2/Pickup/Materials/MI_Rager_Outer")); // the stock paint is not in the mod
            var info = new ScumStudio.Assets.Materials.MaterialInspector(written).Inspect(outerCopy + "." + outerCopy[(outerCopy.LastIndexOf('/') + 1)..]);
            var gold = info.Vectors.Single(v => v.Name == "Base Color A").Value;
            Assert.InRange(gold.X, 0.9f, 1.01f);
            Assert.InRange(gold.Y, 0.4f, 0.7f);

            // A copied mesh wears the copied paint.
            var meshCopies = clone.Packages.Where(p => p.Old.Contains("/Models/", StringComparison.Ordinal) && !PackageMapLeaf(p.Old).StartsWith("MI_", StringComparison.Ordinal)).Select(p => p.New).ToList();
            Assert.Contains(meshCopies, m => ScumStudio.Modding.Catalog.ModdableAssets.ReadImportedPackages(written, m).Contains(outerCopy, StringComparer.OrdinalIgnoreCase));
            // ... and the clone's Blueprints show the copied meshes.
            var blueprints = clone.Packages.Select(p => p.New).Where(n => PackageMapLeaf(n).StartsWith("BPC_", StringComparison.Ordinal)).ToList();
            Assert.Contains(blueprints, b => ScumStudio.Modding.Catalog.ModdableAssets.ReadImportedPackages(written, b).Any(i => meshCopies.Contains(i, StringComparer.OrdinalIgnoreCase)));

            // A weapon clone gets its own paint too: its mesh and the AK's material, under its name.
            var ak = ScumStudio.Modding.Cloning.CloneFamilyPlanner.PlanItem(ctx.Services.Workspace.Catalog!, "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_AK47", "Weapon_AK47_Gold");
            Assert.Contains(ak.Packages, p => p.Key.EndsWith("/MI_AK47_01", StringComparison.Ordinal) && p.Value.EndsWith("/Weapon_AK47_Gold_MI_AK47_01", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }

        static string PackageMapLeaf(string path) => path[(path.LastIndexOf('/') + 1)..];
    }

    /// <summary>Mean sRGB of the fully painted texels of a baked paint that are warm (redder than blue), or cool.</summary>
    private static (int R, int G, int B) Average(ScumStudio.Assets.Textures.TextureImage baked, bool warm)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (var i = 0; i < baked.Rgba.Length; i += 4)
        {
            if (baked.Rgba[i + 3] > 250 && (baked.Rgba[i] > baked.Rgba[i + 2]) == warm)
            {
                (r, g, b, n) = (r + baked.Rgba[i], g + baked.Rgba[i + 1], b + baked.Rgba[i + 2], n + 1);
            }
        }

        return n == 0 ? (0, 0, 0) : ((int)(r / n), (int)(g / n), (int)(b / n));
    }

    /// <summary>Owner: "the armour you buy must take the same colour, so the car looks the same with it on".</summary>
    [AvaloniaFact]
    public async Task TheArmourTakesTheBodyPaintAndTheModAddsIt()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Gold Armour");
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_Rager"));
            await vehicles.PreviewCompletion;
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.HasPaints && vehicles.HasArmour, TimeSpan.FromSeconds(30)));

            var outer = vehicles.Paints.Single(p => p.MaterialPath.EndsWith(".MI_Rager_Outer", StringComparison.Ordinal));
            outer.ApplyFinishCommand.Execute(PaintFinish.All.Single(f => f.Key == "Gold"));
            vehicles.Armour = vehicles.ArmourChoices.Single(c => c.Key == "ArmorLight");
            Assert.True(HeadlessUi.PumpUntil(() => vehicles.Paints.Any(p => p.IsAdded), TimeSpan.FromSeconds(30)));

            // The body keeps its gold across the kit change, and the plate follows it: in the plain finish (an even colour), or
            // over its own print (the print adds its scratches, like the body's texture does).
            var body = vehicles.Paints.Single(p => p.MaterialPath.EndsWith(".MI_Rager_Outer", StringComparison.Ordinal));
            var armour = vehicles.Paints.Single(p => p.IsAdded);
            Assert.Equal(PaintFinish.All.Single(f => f.Key == "Gold").Colour, body.Colour);
            Assert.Equal(body.Colour, armour.Colour);
            Assert.True(armour.Painted && armour.Plain);
            armour.Plain = false;
            Assert.Contains(vehicles.Preview!.Parts, p => p.Material == armour.MaterialPath && p.TexturePath == "#paint/" + armour.MaterialPath);
            armour.Plain = true;
            Assert.Contains(vehicles.Preview!.Parts, p => p.Material == armour.MaterialPath && p.TexturePath is null);
            Assert.Contains(vehicles.Preview!.Parts, p => p.Material == armour.MaterialPath && p.Tint == armour.LinearColour && p.Surface.X == 1);

            vehicles.ApplyPaintCommand.Execute(null);
            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var info = new ScumStudio.Assets.Materials.MaterialInspector(written).Inspect("/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Materials/MI_WW_Armor.MI_WW_Armor");
            var a = info.Vectors.Single(v => v.Name == "Base Color A").Value;
            Assert.InRange(a.X, 0.9f, 1.01f); // the same gold as the body (the game shows a paint's colour itself)
            Assert.InRange(a.Y, 0.4f, 0.7f);
            Assert.InRange(a.Z, 0.01f, 0.1f);
            Assert.Equal(a, info.Vectors.Single(v => v.Name == "Base Color B").Value);
            Assert.EndsWith("T_FlatWhiteMask_Dummy_01", info.Textures.Single(t => t.Name == "Color Mask").TexturePath, StringComparison.Ordinal);
            Assert.Equal(1f, info.Scalars.Single(s => s.Name == "CarPaint Metalness").Value);
            Assert.EndsWith("T_FlatWhiteColor_Dummy_01", info.Textures.Single(t => t.Name == "Diffuse Map").TexturePath, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
