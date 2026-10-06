using CUE4Parse.UE4.Assets.Objects;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Spawns;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using UeVector = CUE4Parse.UE4.Objects.Core.Math.FVector;

namespace ScumStudio.Tests.App;

/// <summary>
/// Discord JimTheCoffeeGuy: "sentry spawners have patrol points the tool shows but does not let you move, and you cannot
/// add or remove points". A sentry's patrol points (and a spawner group's loot points) are pins of their own now: the gizmo
/// moves one, Duplicate adds one beside it, Delete removes it, and the export rewrites the stored array. Real game files
/// only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class PatrolPointsRealTests
{
    private const string Barracks = "/Game/ConZ_Files/Maps/The_Island/D_0_Military_Barracks";

    [Fact]
    public async Task ASentrysPatrolPointIsMovedOneAddedOneDeletedAndExported()
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
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Patrol");
        await map.LoadLevelsAsync([Barracks]);
        map.ShowSpawns = true;
        var journal = ctx.Services.Projects.Current!.Journal;

        // SentrySpawner_0 stores 2 patrol points (SentryPatrolPoint.LocationRelativeToSentry); each is a translucent capsule of its own.
        var sentry = map.AllActors.Single(a => a.Name == "SentrySpawner_0");
        var stored = sentry.Actor.PatrolPoints;
        Assert.Equal(2, stored.Count);
        var array = Assert.Single(SpawnPointArrays.Of(sentry.Actor));
        Assert.Equal((null, SpawnPointArrays.PatrolPoints), (array.Component, array.Array));
        var pins = map.PreparedScene!.Placements.Where(p => p.SelectableId == sentry.SelectableId && p.MeshPath == SpawnMarkers.MeshKey(SpawnKind.Patrol)).ToList();
        Assert.Equal(2, pins.Count);
        Assert.All(pins, p => Assert.True(p.InstanceKey!.Value.IsSpawnPoint));
        Assert.Equal(SpawnShape.Capsule, SpawnMarkers.ShapeOf(SpawnKind.Patrol));
        Assert.Equal(SpawnMarkers.StandInAlpha, SpawnMarkers.Asset(SpawnKind.Patrol).MaterialTints.Values.Single().W);

        // 1. The second point goes 5 m along the sentry's X: the gizmo stood on it, the op carries the whole list.
        var second = pins.Single(p => p.SpawnPoint!.Value.Index == 1);
        map.SelectedInstanceKey = second.PickKey(map.PickParts);
        map.SelectedActorId = sentry.SelectableId;
        Assert.True(map.IsSpawnPointSelected);
        Assert.True(map.HasSelectedPart);
        Assert.True(map.DeleteSelectedCommand.CanExecute(null));
        Assert.True(map.DuplicateSelectedCommand.CanExecute(null));
        var before = map.SelectedRootWorld!.Value;
        Assert.True(FVector.Distance(before.Translation, sentry.Actor.WorldTransform.TransformPosition(stored[1])) < 0.01f);
        map.ApplyDraggedTransform(sentry.SelectableId, before with { Translation = sentry.Actor.WorldTransform.TransformPosition(stored[1] + new FVector(500f, 0f, 0f)) });
        var move = Assert.IsType<SetSpawnPointsOp>(Assert.Single(journal.Applied).Op);
        Assert.Null(move.Component);
        Assert.Equal(2, move.New.Count);
        Assert.True(FVector.Distance(move.New[1].Local.Location, stored[1] + new FVector(500f, 0f, 0f)) < 0.01f);
        Assert.Equal(1, move.New[1].Source);
        var overridden = Assert.Contains(sentry.SelectableId, map.PinOverrides);
        Assert.Equal(2, overridden.Count);
        Assert.True(FVector.Distance(overridden[1].World.Translation, before.Translation + sentry.Actor.WorldTransform.TransformVector(new FVector(500f, 0f, 0f))) < 0.01f);

        // 2. Duplicate adds a third point beside it (a copy of the stored second) and selects it.
        map.DuplicateSelectedCommand.Execute(null);
        var add = Assert.IsType<SetSpawnPointsOp>(journal.Applied[^1].Op);
        Assert.Equal(3, add.New.Count);
        Assert.Equal(1, add.New[2].Source);
        Assert.Equal(3, map.PinOverrides[sentry.SelectableId].Count);
        Assert.Equal(2, map.SelectedInstanceKey!.Value.Point);

        // 3. Delete removes the first point: two remain, both copies of the stored second.
        map.SelectedInstanceKey = InstanceKey.Of(sentry.SelectableId, SpawnPointArrays.PatrolPoints, InstanceKey.SpawnPointBase);
        Assert.True(map.IsSpawnPointSelected);
        map.DeleteSelectedCommand.Execute(null);
        var delete = Assert.IsType<SetSpawnPointsOp>(journal.Applied[^1].Op);
        Assert.Equal(2, delete.New.Count);
        Assert.All(delete.New, p => Assert.Equal(1, p.Source));
        Assert.Equal(2, map.PinOverrides[sentry.SelectableId].Count);
        Assert.False(map.IsSpawnPointSelected);

        // 4. Undo and redo keep the state exact.
        ctx.Services.Projects.Undo();
        Assert.Equal(3, map.PinOverrides[sentry.SelectableId].Count);
        ctx.Services.Projects.Redo();
        Assert.Equal(2, map.PinOverrides[sentry.SelectableId].Count);

        // 5. Exported: the actor's PatrolPoints array holds the two points, read back by CUE4Parse.
        var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        Assert.DoesNotContain(result.Warnings, w => w.Contains("spawn points", StringComparison.OrdinalIgnoreCase));
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        Assert.True(written.TryLoadPackage(Barracks, out var package));
        var export = package.GetExports().Single(e => e.Name == "SentrySpawner_0");
        Assert.True(export.TryGetValue(out FStructFallback[] points, "PatrolPoints"));
        Assert.Equal(2, points.Length);
        var locations = points.Select(p => p.Get<UeVector>("LocationRelativeToSentry")).Select(v => new FVector(v.X, v.Y, v.Z)).ToList();
        Assert.True(FVector.Distance(locations[0], stored[1] + new FVector(500f, 0f, 0f)) < 0.01f);
        Assert.True(FVector.Distance(locations[1], stored[1] + new FVector(600f, 0f, 0f)) < 0.01f);
        Assert.True(export.TryGetValue(out float activation, "ActivationDistance")); // the rest of the actor is untouched
        Assert.Equal(30000f, activation);
    }

    [Fact]
    public async Task ASpawnerGroupsLootPointIsMovedAndOneDeletedAndExported()
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
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Loot");
        await map.LoadLevelsAsync([Barracks]);
        map.ShowSpawns = true;

        // The barracks' ItemSpawnerGroup_1 stores its loot markers on its SpawnerComponent: each a crate of its own.
        var group = map.AllActors.Single(a => a.Name == "ItemSpawnerGroup_1");
        var component = group.Actor.FindComponent("SpawnerComponent")!;
        var markers = component.SpawnMarkers;
        Assert.True(markers.Count > 3);
        var array = Assert.Single(SpawnPointArrays.Of(group.Actor));
        Assert.Equal((component.Name, SpawnPointArrays.SpawnerMarkers), (array.Component, array.Array));
        var pins = map.PreparedScene!.Placements.Where(p => p.SelectableId == group.SelectableId && p.SpawnPoint is not null).ToList();
        Assert.Equal(markers.Count, pins.Count);
        Assert.All(pins, p => Assert.Equal(SpawnMarkers.MeshKey(SpawnKind.Loot), p.MeshPath));

        // The third point goes up 2 m; the first is deleted.
        map.SelectedInstanceKey = pins.Single(p => p.SpawnPoint!.Value.Index == 2).PickKey(map.PickParts);
        map.SelectedActorId = group.SelectableId;
        Assert.True(map.IsSpawnPointSelected);
        var world = map.SelectedRootWorld!.Value;
        map.ApplyDraggedTransform(group.SelectableId, world with { Translation = world.Translation + new FVector(0f, 0f, 200f) });
        map.SelectedInstanceKey = InstanceKey.Of(group.SelectableId, component.Name, InstanceKey.SpawnPointBase);
        map.DeleteSelectedCommand.Execute(null);
        var state = ctx.Services.Projects.Current!.State;
        var points = state.GetSpawnPoints(group.Reference, component.Name, SpawnPointArrays.SpawnerMarkers)!;
        Assert.Equal(markers.Count - 1, points.Count);
        Assert.Equal(2, points[1].Source);

        var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        Assert.True(written.TryLoadPackage(Barracks, out var package));
        var export = package.GetExports().Single(e => e.Name == component.Name && e.Outer?.Name == group.Name);
        Assert.True(export.TryGetValue(out FStructFallback[] stored, "SpawnerMarkers"));
        Assert.Equal(markers.Count - 1, stored.Length);
        var moved = stored[1].Get<FStructFallback>("Transform").Get<UeVector>("Translation");
        Assert.True(FVector.Distance(new FVector(moved.X, moved.Y, moved.Z), markers[2].Local.Translation + new FVector(0f, 0f, 200f)) < 0.1f);
        Assert.Equal(markers[2].Preset, stored[1].Get<FStructFallback>("SpawnerPreset").Get<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("Preset").Name.Replace("_C", string.Empty, StringComparison.Ordinal));
    }
}
