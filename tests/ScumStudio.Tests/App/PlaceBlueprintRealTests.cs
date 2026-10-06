using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;
using ScumStudio.Pak;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// Discord user igor: "It would be preferable to create blueprints like these, with the option to install them on the map"
/// (Assets, the drill press Blueprint selected, "Place in map" greyed). A Blueprint is a copy of one the game placed in any
/// level, an item a copy of a world item spawner set to spawn it. Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class PlaceBlueprintRealTests
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";
    private const string Items = "/Game/ConZ_Files/Items/Equipment/Active_Items/";

    [Fact]
    public async Task BlueprintsAndItemsArePlacedFromLevelsThatAreNotLoadedAndExport()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        var key = AesKeyText.FromEnvironmentOrStore()!;
        ctx.Services.Keys.Set(key);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Place");
        await map.LoadLevelsAsync([Farm]);
        var aim = map.AllActors[0].Actor.WorldTransform.Translation + new FVector(1000f, 0f, 0f);
        map.AimPointProvider = () => aim;
        var project = ctx.Services.Projects.Current!;

        // A Blueprint placed in another level: copied from there, drawn here with its meshes where the camera aims.
        Assert.True(map.AddObject("/Game/ConZ_Files/Models/Objects/Indoor/Furniture/Water_Tank_Office/BP_Water_Tank_Office_01"));
        Assert.True(await map.AddCompletion);
        var tank = Assert.IsType<AddBlueprintActorOp>(project.Journal.Applied[^1].Op);
        Assert.Equal(Farm, tank.Level);
        Assert.Equal("/Game/ConZ_Files/Maps/The_Island/B_0_Gas_Station_Cont", tank.Source.Level);
        Assert.Null(tank.Item);
        var clone = Assert.Single(map.Clones, c => c.Name == tank.NewName);
        Assert.True((clone.RootWorld.Translation - aim).Size() < 1f);
        Assert.Contains(clone.Placements!, p => p.MeshPath.EndsWith(".SM_Water_Tank_Office_01", StringComparison.Ordinal));
        Assert.Equal(tank.NewName, map.SelectedActor?.Name);

        // An item (by name, as typed in the Add object box): a copy of one of the island's drill press spawners.
        var drillAt = aim += new FVector(500f, 0f, 0f);
        Assert.True(map.AddObject("Work_Drillpress_01"));
        Assert.True(await map.AddCompletion);
        var drill = Assert.IsType<AddBlueprintActorOp>(project.Journal.Applied[^1].Op);
        Assert.Equal(PlacedBlueprintIndex.WorldItemSpawnerClass, drill.ClassPath);
        Assert.Equal(Items + "Work_Drillpress_01.Work_Drillpress_01_C", drill.Item);
        Assert.Contains("WorldItemSpawner (Work_Drillpress_01)", drill.Describe(), StringComparison.Ordinal);
        Assert.Contains(map.Clones.Single(c => c.Name == drill.NewName).Placements!, p => SpawnMarkers.IsMarker(p.MeshPath)); // its pin

        // An item no spawner of the island spawns (a chest; its _ES variant is the same item): the copy is set to it.
        var chestAt = aim += new FVector(500f, 0f, 0f);
        Assert.True(map.AddObject(Items + "Improvised_Wooden_Chest_ES"));
        Assert.True(await map.AddCompletion);
        var chest = Assert.IsType<AddBlueprintActorOp>(project.Journal.Applied[^1].Op);
        Assert.Equal(PlacedBlueprintIndex.WorldItemSpawnerClass, chest.ClassPath);
        Assert.Equal(Items + "Improvised_Wooden_Chest.Improvised_Wooden_Chest_C", chest.Item);

        // A copy of that spawner spawns the chest too (review: the copy lost its item and spawned the source's).
        map.SelectedActor = map.AllActors.Single(a => a.Name == chest.NewName);
        map.DuplicateSelectedCommand.Execute(null);
        Assert.Equal(chest.Item, Assert.IsType<AddBlueprintActorOp>(project.Journal.Applied[^1].Op).Item);

        // A bullet's class sits in an Items folder but is no item: it is never written into a spawner.
        var bullet = ScumStudio.Assets.Catalog.AssetDumper.Packages
            .First(p => p.ClassName == "Blueprint" && p.PackagePath.Contains("/Items/Ammunition/Ammunition_Class/", StringComparison.Ordinal)).PackagePath;
        var before = project.Journal.Applied.Count;
        map.AddObject(bullet);
        await map.AddCompletion;
        Assert.DoesNotContain(project.Journal.Applied.Skip(before), e => e.Op is AddBlueprintActorOp { Item: not null });

        // Assets: "Place in map" takes a Blueprint and the Map adds it.
        aim += new FVector(500f, 0f, 0f);
        using (var assets = new AssetsPageViewModel(ctx.Services, null, path => map.AddObject(path)))
        {
            await assets.LoadCompletion;
            const string Drill2 = Items + "Work_Drillpress_02";
            var className = ctx.Services.Workspace.Catalog!.GetMainClassName(Drill2); // as a folder tile resolves it
            assets.SelectedItem = new AssetItemViewModel(new PackageEntry(Drill2 + ".uasset", Drill2, className));
            Assert.True(assets.CanPlaceInMap);
            assets.PlaceInMapCommand.Execute(null);
            Assert.True(await map.AddCompletion);
            Assert.Equal(Items + "Work_Drillpress_02.Work_Drillpress_02_C", Assert.IsType<AddBlueprintActorOp>(project.Journal.Applied[^1].Op).Item);
            await assets.DetailsCompletion;
        }

        // The project opened again: the copies are drawn once their levels are read (in the background).
        using (var again = new MapPageViewModel(ctx.Services))
        {
            await again.LoadCompletion;
            await again.LoadLevelsAsync([Farm]);
            for (var i = 0; i < 300 && !again.Clones.Any(c => c.Name == tank.NewName && c.Placements is { Count: > 0 }); i++)
            {
                await Task.Delay(100);
            }

            var drawn = Assert.Single(again.Clones, c => c.Name == tank.NewName);
            Assert.Equal(clone.Placements!.Select(p => p.MeshPath), drawn.Placements!.Select(p => p.MeshPath));
        }

        // Exported: every copy arrives, the spawners spawn their items where they were put.
        var result = await new ProjectExporter().ExportAsync(project, ctx.Services.Workspace.Catalog!,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        Assert.Empty(result.Warnings);
        using var written = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = key, LooseOverlays = [result.StagingDirectory] });
        var exports = written.LoadPackage(Farm).GetExports().ToList();
        var level = LevelDocument.Load(new Cue4ParseLevelReader(written), Farm);
        Assert.NotNull(level.FindActor(tank.NewName));
        foreach (var (op, at, item) in new[] { (drill, drillAt, "Work_Drillpress_01"), (chest, chestAt, "Improvised_Wooden_Chest") })
        {
            var spawner = exports.Single(e => e.Outer?.Name == op.NewName && e.TryGetValue(out FSoftObjectPath _, "_item"));
            Assert.True(spawner.TryGetValue(out FSoftObjectPath path, "_item"));
            Assert.Equal(op.Item, path.AssetPathName.Text);
            var actor = level.FindActor(op.NewName)!;
            Assert.True((actor.WorldTransform.Translation - at).Size() < 1f);
            Assert.Equal(item, Assert.Single(actor.Components.SelectMany(c => c.SpawnMarkers)).Preset);
        }
    }
}
