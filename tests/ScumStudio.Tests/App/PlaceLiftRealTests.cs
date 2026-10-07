using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "add lifts as placeable assets linked to the mechanic". A car lift placed in the B_4 outpost is a copy of the
/// stock lift whose <c>_assignedTradePost</c> points at the outpost's mechanic (and the mechanic's post is not copied
/// along); a bike lift for a placed Mechanic trader points at that trader. Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class PlaceLiftRealTests
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";
    private const string Outpost = "/Game/ConZ_Files/Maps/The_Island/B_4_Outpost";
    private const string Mechanic = "BP_Outpost_CarShop_NPC_and_VehicleSpawner2_2";

    [Fact]
    public async Task ACarLiftInTheOutpostServesItsStockMechanic()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Lifts");
        await map.LoadLevelsAsync([Outpost]);
        var mechanic = map.AllActors.Single(a => a.Name == Mechanic);
        var aim = mechanic.Actor.WorldTransform.Translation + new FVector(600f, 0f, 0f);
        map.AimPointProvider = () => aim;
        var project = ctx.Services.Projects.Current!;

        // The Add object box offers the outpost's mechanic (named like its trader card) and the lift kinds.
        map.PrepareTraders();
        var item = Assert.Single(map.Mechanics, m => m.Post.Actor == Mechanic);
        Assert.StartsWith("Trader Mechanic B_4", item.Label, StringComparison.Ordinal);
        Assert.Contains(map.LiftKinds, k => k.Kind == "CarLift");
        var before = project.Journal.Applied.Count;
        Assert.True(await map.AddLiftAsync("CarLift", item.Post));
        Assert.Equal(before + 1, project.Journal.Applied.Count); // one history row

        var op = Assert.IsType<AddBlueprintActorOp>(project.Journal.Applied[^1].Op);
        Assert.Equal(Outpost, op.Level);
        Assert.EndsWith("BP_CarLift.BP_CarLift_C", op.ClassPath, StringComparison.Ordinal);
        Assert.Equal(Mechanic, op.Mechanic);
        Assert.Contains($"for {Mechanic}", op.Describe(), StringComparison.Ordinal);

        // Drawn where the camera aims (a same-level copy is drawn from the outpost's own lift), selected.
        var clone = Assert.Single(map.Clones, c => c.Name == op.NewName);
        Assert.True((clone.RootWorld.Translation - aim).Size() < 1f);
        Assert.Equal(op.NewName, map.SelectedActor?.Name);

        var result = await new ProjectExporter().ExportAsync(project, ctx.Services.Workspace.Catalog!,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });

        // The level: the lift points at the outpost's own mechanic; the mechanic's post was not copied along.
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var level = ModdableAssets.ReadPackage(written, Outpost);
        var actors = LevelPackageEditor.ReadActorList(level).Select(a => a.Name).ToList();
        Assert.Contains(op.NewName, actors);
        Assert.Single(actors, a => a?.StartsWith("BP_Outpost_CarShop_NPC_and_VehicleSpawner", StringComparison.Ordinal) == true);
        var lift = level.ReadProperties(Export(level, op.NewName));
        Assert.Equal(Export(level, Mechanic) + 1, Assert.IsType<ObjectValue>(lift.Find("_assignedTradePost")!.Value).Index);
        Assert.DoesNotContain(result.Warnings, w => w.Contains(op.NewName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABikeLiftForAPlacedMechanicPointsAtThatTrader()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Lifts");
        await map.LoadLevelsAsync([Farm]);
        var aim = map.AllActors[0].Actor.WorldTransform.Translation + new FVector(800f, 0f, 0f);
        map.AimPointProvider = () => aim;
        var project = ctx.Services.Projects.Current!;

        // No mechanic on the farm until a Mechanic trader is placed; then the box offers that trader.
        map.PrepareTraders();
        Assert.Empty(map.Mechanics);
        Assert.True(await map.AddTraderAsync("Mechanic", "A_3_Mechanic", "Outpost_A_3"));
        var trader = Assert.IsType<AddBlueprintActorOp>(project.Journal.Applied[^1].Op);
        map.PrepareTraders();
        var item = Assert.Single(map.Mechanics);
        Assert.Equal(trader.Created, item.Post);
        Assert.StartsWith("Trader Mechanic A_3", item.Label, StringComparison.Ordinal);

        Assert.True(await map.AddLiftAsync("BikeLift", item.Post));
        var lift = Assert.IsType<AddBlueprintActorOp>(project.Journal.Applied[^1].Op);
        Assert.Equal(Farm, lift.Level);
        Assert.EndsWith("BP_BikeLift_C", lift.ClassPath, StringComparison.Ordinal);
        Assert.Equal(trader.NewName, lift.Mechanic);

        var result = await new ProjectExporter().ExportAsync(project, ctx.Services.Workspace.Catalog!,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var level = ModdableAssets.ReadPackage(written, Farm);
        var props = level.ReadProperties(Export(level, lift.NewName));
        Assert.Equal(Export(level, trader.NewName) + 1, Assert.IsType<ObjectValue>(props.Find("_assignedTradePost")!.Value).Index);
        Assert.DoesNotContain(result.Warnings, w => w.Contains(lift.NewName, StringComparison.Ordinal));

        // Deleting the lift is the plain delete: it is not created.
        map.SelectedActor = map.AllActors.Single(a => a.Name == lift.NewName);
        map.DeleteSelectedCommand.Execute(null);
        var again = await new ProjectExporter().ExportAsync(project, ctx.Services.Workspace.Catalog!,
            new ExportOptions { OutputDirectory = ctx.Combine("out2"), WritePak = false });
        using var rewritten = AssetCatalog.OpenLoose(again.StagingDirectory);
        Assert.DoesNotContain(LevelPackageEditor.ReadActorList(ModdableAssets.ReadPackage(rewritten, Farm)), a => a.Name == lift.NewName);
    }

    private static int Export(CookedPackage package, string name) =>
        Enumerable.Range(0, package.Exports.Count).Single(i => package.ResolveName(package.Exports[i].ObjectName) == name);
}
