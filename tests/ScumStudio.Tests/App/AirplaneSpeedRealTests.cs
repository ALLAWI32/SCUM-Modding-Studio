using CUE4Parse.UE4.Assets.Exports;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Discord #modding-help, unknownskill (2026-10-19): "I change the speed of the plane but it does not apply, while the
/// Rager and the WolfsWagen are OK". A plane's pull is <c>_propellerForce</c> of the game's class <c>Airplane</c> (180000 in
/// the game's code, stored by no plane file), its top speed <c>_maxVelocity</c>; the engine and gear values a car's speed
/// comes from only roll the plane on the ground. On the real game files (<c>SCUM_PAKS</c>, and <c>SCUM_SERVER_PAKS</c> for the
/// server's cook): the Vehicles page shows the plane's pull and top speed as its key values with a note, a clone's new pull
/// and top speed are written into its class defaults (read back by the game's reader, CUE4Parse), and the stock planes stay
/// as they are. What only the game can show: how fast the clone flies.
/// </summary>
public sealed class AirplaneSpeedRealTests(ITestOutputHelper output)
{
    private const string Duster = "/Game/ConZ_Files/Vehicles/Airplane/Duster/BPC_Kinglet_Duster";
    private const string Fast = "/Game/ConZ_Files/Vehicles/Airplane/Duster/BPC_DusterFast";

    [PaksFact]
    public async Task APlanesPullAndTopSpeedAreItsKeyValuesAndItsCloneGetsTheNewOnes()
    {
        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = RealPaks.Folder });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Fast plane");
        using var page = new VehiclesPageViewModel(ctx.Services);
        await page.LoadCompletion;
        var game = ctx.Services.Workspace.Catalog!;

        // A car goes faster with its engine: those are its key values, and no plane note.
        Assert.True(await page.SelectAsync("BPC_Rager"));
        Assert.Equal(string.Empty, page.ValuesNote);
        Assert.Contains(page.Groups.SelectMany(g => g.Rows), r => r.Tunable.Name == "MaxRPM");

        // Both planes: the pull (the game's code) and the top speed (their file) are the key values; the wheels' engine is not.
        foreach (var plane in new[] { "BPC_Kinglet_Duster", "BPC_Kinglet_Mariner" })
        {
            Assert.True(await page.SelectAsync(plane));
            var key = page.Groups.SelectMany(g => g.Rows).ToList();
            output.WriteLine($"{plane}: {string.Join(", ", key.Select(r => $"{r.Tunable.Name}={r.StockValue}"))}");
            var force = Assert.Single(key, r => r.Tunable.Name == "_propellerForce");
            Assert.Equal(("180000", "/Script/SCUM.Airplane"), (force.StockValue, force.Tunable.InheritedFrom));
            Assert.Equal("8332.5", Assert.Single(key, r => r.Tunable.Name == "_maxVelocity").StockValue);
            Assert.DoesNotContain(key, r => r.Tunable.Name is "MaxRPM" or "IdleRPM" or "Ratio" or "ChassisMass");
            Assert.Contains("Propeller force", page.ValuesNote, StringComparison.Ordinal);
            Assert.Contains("8332.5 = 300 km/h", page.ValuesNote, StringComparison.Ordinal);
            page.KeyStatsOnly = false; // all values: the engine values are still there to change
            Assert.Contains(page.Groups.SelectMany(g => g.Rows), r => r.Tunable.Name == "MaxRPM");
            page.KeyStatsOnly = true;
        }

        // A clone: twice the pull and 450 km/h at most.
        Assert.True(await page.SelectAsync("BPC_Kinglet_Duster"));
        page.CloneName = "DusterFast";
        await page.CreateCloneAsync();
        Assert.Equal(Fast, page.SelectedItem!.PackagePath);

        // The clone's own engine part (it lists its attachments) says where the pull is.
        page.SelectedPart = page.Parts.Single(p => p.PackagePath.EndsWith("/BPC_DusterFast_Engine", StringComparison.Ordinal));
        await page.ValuesCompletion;
        Assert.Contains("Propeller force", page.ValuesNote, StringComparison.Ordinal);
        page.SelectedPart = page.Parts[0];
        await page.ValuesCompletion;
        Set(page, "_propellerForce", "180000", "360000");
        Set(page, "_maxVelocity", "8332.5", "12500");
        page.ApplyChanges();
        Assert.True(page.FindRow("_propellerForce")!.IsOverridden);

        var result = await new ProjectExporter().ExportAsync(project, game, new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        Assert.DoesNotContain(result.Warnings, w => w.Contains("DusterFast", StringComparison.Ordinal));
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var clone = Cdo(written, Fast, "Default__BPC_DusterFast_C");
        Assert.Equal((360000f, 12500f), (clone.GetOrDefault<float>("_propellerForce", -1f), clone.GetOrDefault<float>("_maxVelocity", -1f)));
        Assert.Equal(15000f, clone.GetOrDefault<float>("_reverseTaxiingForce", -1f)); // the rest as the stock plane

        // The game's planes are not in the mod; the stock Duster stores no pull of its own (the code's 180000 applies).
        Assert.False(written.PackageExists(Duster));
        var stock = Cdo(game, Duster, "Default__BPC_Kinglet_Duster_C");
        Assert.Equal((-1f, 8332.5f), (stock.GetOrDefault<float>("_propellerForce", -1f), stock.GetOrDefault<float>("_maxVelocity", -1f)));

        // The dedicated server's cook of the same project carries the same values (the server pushes the plane too).
        if (Environment.GetEnvironmentVariable("SCUM_SERVER_PAKS") is { Length: > 0 } serverPaks && Directory.Exists(serverPaks))
        {
            using var server = AssetCatalog.OpenPaks(serverPaks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
            var serverResult = await new ProjectExporter().ExportAsync(project, server,
                new ExportOptions { OutputDirectory = ctx.Combine("out-server"), WritePak = false, ClientCatalog = game }, ProjectSourceRole.Server);
            using var serverWritten = AssetCatalog.OpenLoose(serverResult.StagingDirectory);
            var serverClone = Cdo(serverWritten, Fast, "Default__BPC_DusterFast_C");
            Assert.Equal((360000f, 12500f), (serverClone.GetOrDefault<float>("_propellerForce", -1f), serverClone.GetOrDefault<float>("_maxVelocity", -1f)));
        }
    }

    /// <summary>Sets the row <paramref name="name"/> of the selected part (checking its stock value).</summary>
    private static void Set(ModulePageViewModel page, string name, string stock, string value)
    {
        var row = page.FindRow(name);
        Assert.True(row is not null, $"{page.SelectedPart?.PackagePath} has no {name}");
        Assert.True(row!.Tunable.CanEdit, $"{name} is read-only");
        Assert.Equal(stock, row.StockValue);
        row.Value = value;
        Assert.Null(row.Error);
    }

    private static UObject Cdo(AssetCatalog catalog, string package, string export) =>
        catalog.LoadPackage(package).GetExports().Single(e => e.Name == export);
}
