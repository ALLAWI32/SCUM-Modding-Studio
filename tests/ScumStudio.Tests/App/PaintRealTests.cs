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

            // The body keeps its gold across the kit change, and the plate follows it: in the plain finish (an even colour, as
            // bright as the body), or over its own darker print (brighter colour).
            var body = vehicles.Paints.Single(p => p.MaterialPath.EndsWith(".MI_Rager_Outer", StringComparison.Ordinal));
            var armour = vehicles.Paints.Single(p => p.IsAdded);
            Assert.Equal(PaintFinish.All.Single(f => f.Key == "Gold").Colour, body.Colour);
            Assert.Equal(body.Colour, armour.Colour);
            Assert.True(armour.Painted && armour.Plain && armour.Gain is > 0.05f and < 1f, $"gain {armour.Gain}");
            armour.Plain = false;
            Assert.True(armour.Gain > 1.5f, $"gain over the print {armour.Gain}");
            Assert.Contains(vehicles.Preview!.Parts, p => p.Material == armour.MaterialPath && p.TexturePath is not null);
            armour.Plain = true;
            Assert.Contains(vehicles.Preview!.Parts, p => p.Material == armour.MaterialPath && p.TexturePath is null);
            Assert.Contains(vehicles.Preview!.Parts, p => p.Material == armour.MaterialPath && p.Tint == armour.LinearColour && p.Surface.X == 1);

            vehicles.ApplyPaintCommand.Execute(null);
            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var info = new ScumStudio.Assets.Materials.MaterialInspector(written).Inspect("/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Materials/MI_WW_Armor.MI_WW_Armor");
            var a = info.Vectors.Single(v => v.Name == "Base Color A").Value;
            Assert.True(a.X > a.Y * 1.5f && a.Y > a.Z * 3f, $"armour colour {a}"); // gold, scaled by the gain
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
