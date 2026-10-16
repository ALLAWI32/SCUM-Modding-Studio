using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.Spawns;
using ScumStudio.Tests.Level;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// The App side (wip/p1copies-points) and the export side (wip/p1copies-export) together: a copied sentry's patrol point
/// and a house's indoor loot point, edited on the map, are in the exported level (igor8802, seviscache).
/// </summary>
public sealed class MapPointsExportComboTests
{
    [Fact]
    public async Task PointsEditedOnACopyAndOnATemplateLootPointReachTheExportedLevel()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticSpawnerLevel.WriteContent(game);
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Points");
        await map.LoadLevelsAsync([SyntheticSpawnerLevel.LevelPath]);
        map.ShowSpawns = true;

        // A copied sentry moved 1000 cm along X; its first patrol point dragged 50 cm along Y.
        var sentry = map.AllActors.Single(a => a.Name == SyntheticSpawnerLevel.Sentry);
        map.SelectedActor = sentry;
        map.DuplicateSelectedCommand.Execute(null);
        var copy = Assert.Single(map.AllActors, a => a.IsAdded);
        var root = map.SelectedRootWorld!.Value;
        map.ApplyDraggedTransform(copy.SelectableId, root with { Translation = root.Translation + new FVector(1000f, 0f, 0f) });
        map.SelectedInstanceKey = InstanceKey.Of(copy.SelectableId, SpawnPointArrays.PatrolPoints, InstanceKey.SpawnPointBase);
        Assert.True(map.IsSpawnPointSelected);
        var pin = map.SelectedRootWorld!.Value;
        map.ApplyDraggedTransform(copy.SelectableId, pin with { Translation = pin.Translation + new FVector(0f, 50f, 0f) });

        // The house's second indoor loot point (its Blueprint keeps the markers) dragged 70 cm up.
        var house = map.AllActors.Single(a => a.Name == SyntheticSpawnerLevel.House);
        map.SelectedActor = house;
        map.SelectedInstanceKey = InstanceKey.Of(house.SelectableId, SyntheticSpawnerLevel.HouseLoot, InstanceKey.SpawnPointBase - 1);
        Assert.True(map.IsSpawnPointSelected);
        var point = map.SelectedRootWorld!.Value;
        map.ApplyDraggedTransform(house.SelectableId, point with { Translation = point.Translation + new FVector(0f, 0f, 70f) });

        var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        Assert.DoesNotContain(result.Warnings, w => w.Contains("spawn point", StringComparison.OrdinalIgnoreCase) || w.Contains("not stored", StringComparison.Ordinal));
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var level = LevelDocument.Load(new Cue4ParseLevelReader(written), SyntheticSpawnerLevel.LevelPath);

        var copied = level.Actors.Single(a => a.Name == copy.Name);
        Assert.Equal(3, copied.PatrolPoints.Count);
        Assert.True(FVector.Distance(copied.PatrolPoints[0], SyntheticSpawnerLevel.Patrol[0] + new FVector(0f, 50f, 0f)) < 0.01f, copied.PatrolPoints[0].ToString());
        Assert.True(FVector.Distance(copied.PatrolPoints[2], SyntheticSpawnerLevel.Patrol[2]) < 0.01f);
        var stock = level.Actors.Single(a => a.Name == SyntheticSpawnerLevel.Sentry);
        Assert.True(FVector.Distance(stock.PatrolPoints[0], SyntheticSpawnerLevel.Patrol[0]) < 0.01f);

        var loot = level.FindActor(SyntheticSpawnerLevel.House)!.FindComponent(SyntheticSpawnerLevel.HouseLoot)!;
        Assert.False(loot.SpawnMarkersFromTemplate); // stored on the level's component now
        Assert.Equal(3, loot.SpawnMarkers.Count);
        Assert.True(FVector.Distance(loot.SpawnMarkers[1].Local.Translation, SyntheticSpawnerLevel.HouseMarkers[1] + new FVector(0f, 0f, 70f)) < 0.01f, loot.SpawnMarkers[1].Local.Translation.ToString());
        Assert.True(FVector.Distance(loot.SpawnMarkers[0].Local.Translation, SyntheticSpawnerLevel.HouseMarkers[0]) < 0.01f);
    }
}
