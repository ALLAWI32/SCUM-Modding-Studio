using Avalonia.Headless.XUnit;
using CUE4Parse.UE4.Objects.Engine.Curves;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Export;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "make the car faster, as a new car". A vehicle's engine pull is its torque curve: the Vehicles page shows each
/// key's torque by rpm, and a clone's curve is a copy of its own (the stock Rager keeps its engine).
/// Real game files only (<c>SCUM_PAKS</c>, key from this PC's store); the game's reader (CUE4Parse) reads the result.
/// </summary>
public sealed class EnginePowerRealTests
{
    [AvaloniaFact]
    public async Task ACloneGetsAStrongerEngine()
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
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Fast Rager");
            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_Rager"));
            vehicles.CloneName = "RagerFast";
            await vehicles.CreateCloneAsync();

            var engine = Assert.Single(vehicles.Parts, p => p.IsTorqueCurve);
            Assert.EndsWith("/RagerFast_EngineTorqueCurve", engine.PackagePath, StringComparison.Ordinal);
            vehicles.SelectedPart = engine;
            await vehicles.ValuesCompletion;
            var torque = vehicles.Groups.SelectMany(g => g.Rows).ToList();
            Assert.Equal(["Torque at 0 rpm", "Torque at 3250 rpm", "Torque at 7000 rpm"], torque.Select(r => r.Label));
            Assert.Equal("570", torque[1].Value);
            foreach (var row in torque)
            {
                row.Value = (float.Parse(row.Value, System.Globalization.CultureInfo.InvariantCulture) * 2f).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            vehicles.ApplyChangesCommand.Execute(null);
            var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var curve = written.LoadObject(engine.PackagePath + "." + engine.PackagePath[(engine.PackagePath.LastIndexOf('/') + 1)..]).GetOrDefault<FRichCurve>("FloatCurve");
            Assert.Equal([600f, 1140f, 800f], curve.Keys.Select(k => k.Value));
            Assert.Equal([0f, 3250f, 7000f], curve.Keys.Select(k => k.Time)); // only the torque changed
            Assert.False(written.PackageExists("/Game/ConZ_Files/Vehicles/Car/Rager/Curves/Rager_EngineTorqueCurve")); // the stock engine is not in the mod
            Assert.Contains(engine.PackagePath, ScumStudio.Modding.Catalog.ModdableAssets.ReadImportedPackages(written, vehicles.SelectedItem!.PackagePath), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
