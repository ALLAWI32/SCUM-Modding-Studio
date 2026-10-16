using System.Numerics;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;
using ScumStudio.Assets.Textures;
using ScumStudio.Tests.Assets;
using ScumStudio.Tests.Level;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// Spawn points on the map (synthetic loose game files, no GL): a copied sentry's patrol route (igor8802: "a duplicated
/// sentry's patrol route cannot be changed"), a house's indoor loot points with a gizmo (seviscache), the gizmo of a
/// turned spawner group's point, and Fit to ground on a point.
/// </summary>
public sealed class MapPointsCopyTests
{
    private const float Tolerance = 0.01f;

    [Fact]
    public async Task ACopiedSentrysPatrolPinIsRedrawnWhereTheCopyStandsAndFollowsIt()
    {
        using var ctx = AppTestContext.Create();
        using var map = await OpenAsync(ctx);
        var journal = ctx.Services.Projects.Current!.Journal;
        var sentry = map.AllActors.Single(a => a.Name == SyntheticSpawnerLevel.Sentry);
        map.SelectedActor = sentry;
        map.DuplicateSelectedCommand.Execute(null);
        var copy = Assert.Single(map.AllActors, a => a.IsAdded);
        Assert.Same(copy, map.SelectedActor);
        var root = map.SelectedRootWorld!.Value;
        map.ApplyDraggedTransform(copy.SelectableId, root with { Translation = root.Translation + new FVector(1000f, 0f, 0f) });

        // The copy's first patrol point, dragged 50 cm along Y: journaled on the copy and drawn where the copy stands.
        map.SelectedInstanceKey = InstanceKey.Of(copy.SelectableId, SpawnPointArrays.PatrolPoints, InstanceKey.SpawnPointBase);
        Assert.True(map.IsSpawnPointSelected);
        var stands = SyntheticSpawnerLevel.SentryAt + new FVector(1000f, 0f, 0f) + SyntheticSpawnerLevel.Patrol[0];
        var pin = map.SelectedRootWorld!.Value;
        Assert.True(FVector.Distance(pin.Translation, stands) < Tolerance);
        map.ApplyDraggedTransform(copy.SelectableId, pin with { Translation = stands + new FVector(0f, 50f, 0f) });
        var op = Assert.IsType<SetSpawnPointsOp>(journal.Applied[^1].Op);
        Assert.Equal(copy.Name, op.Target.Actor);
        var pins = Assert.Contains(copy.SelectableId, map.PinOverrides);
        Assert.Equal(3, pins.Count);
        Assert.True(FVector.Distance(pins.Single(p => p.SpawnPoint!.Value.Index == 0).World.Translation, stands + new FVector(0f, 50f, 0f)) < Tolerance);
        Assert.True(FVector.Distance(pins.Single(p => p.SpawnPoint!.Value.Index == 2).World.Translation,
            SyntheticSpawnerLevel.SentryAt + new FVector(1000f, 0f, 0f) + SyntheticSpawnerLevel.Patrol[2]) < Tolerance);
        Assert.DoesNotContain(sentry.SelectableId, map.PinOverrides); // the source's route is its own

        // The copy moves on: its pins go with it.
        map.SelectedInstanceKey = null;
        var moved = map.SelectedRootWorld!.Value;
        map.ApplyDraggedTransform(copy.SelectableId, moved with { Translation = moved.Translation + new FVector(0f, 0f, 300f) });
        Assert.True(FVector.Distance(map.PinOverrides[copy.SelectableId].Single(p => p.SpawnPoint!.Value.Index == 0).World.Translation,
            stands + new FVector(0f, 50f, 300f)) < Tolerance);
    }

    [Fact]
    public async Task ATurnedSpawnerGroupsPointIsMovedInItsComponentAsItIsNow()
    {
        using var ctx = AppTestContext.Create();
        using var map = await OpenAsync(ctx);
        var group = map.AllActors.Single(a => a.Name == SyntheticSpawnerLevel.Group);
        map.SelectedActor = group;
        map.EditRotation = "0, 90, 0";
        map.ApplyTransformCommand.Execute(null);

        // Turned a quarter: the component (0, 500, 0) off the root is now (-500, 0, 0) off it, point 1 (100, 0, 0) on it is (0, 100, 0).
        map.SelectedInstanceKey = InstanceKey.Of(group.SelectableId, SyntheticSpawnerLevel.GroupLoot, InstanceKey.SpawnPointBase - 1);
        Assert.True(map.IsSpawnPointSelected);
        var gizmo = map.SelectedRootWorld!.Value;
        Assert.True(FVector.Distance(gizmo.Translation, SyntheticSpawnerLevel.GroupAt + new FVector(-500f, 100f, 0f)) < Tolerance, gizmo.Translation.ToString());
        Assert.Equal(90f, gizmo.Rotation.Rotator().Yaw, 0.01f);

        // 2 m along the world's X is 2 m along the component's -Y.
        map.ApplyDraggedTransform(group.SelectableId, gizmo with { Translation = gizmo.Translation + new FVector(200f, 0f, 0f) });
        var op = Assert.IsType<SetSpawnPointsOp>(ctx.Services.Projects.Current!.Journal.Applied[^1].Op);
        Assert.Equal(SyntheticSpawnerLevel.GroupLoot, op.Component);
        Assert.True(FVector.Distance(op.New[1].Local.Location, SyntheticSpawnerLevel.GroupMarkers[1] + new FVector(0f, -200f, 0f)) < Tolerance, op.New[1].Local.Location.ToString());

        // The pin is drawn there: the viewport carries the group's pins (placed for its stored root) to its turned root.
        var pin = map.PinOverrides[group.SelectableId].Single(p => p.SpawnPoint!.Value.Index == 1);
        var drawn = pin.World.GetRelativeTransform(group.Actor.WorldTransform) * map.ActorTransforms[group.SelectableId];
        Assert.True(FVector.Distance(drawn.Translation, gizmo.Translation + new FVector(200f, 0f, 0f)) < Tolerance, drawn.Translation.ToString());
        map.SelectedInstanceKey = InstanceKey.Of(group.SelectableId, SyntheticSpawnerLevel.GroupLoot, InstanceKey.SpawnPointBase - 1);
        Assert.True(FVector.Distance(map.SelectedRootWorld!.Value.Translation, drawn.Translation) < Tolerance);
    }

    [Fact]
    public async Task AHousesIndoorLootPointIsAPointWithAGizmo()
    {
        using var ctx = AppTestContext.Create();
        using var map = await OpenAsync(ctx);
        var house = map.AllActors.Single(a => a.Name == SyntheticSpawnerLevel.House);
        var pins = map.PreparedScene!.Placements.Where(p => p.SelectableId == house.SelectableId && SpawnMarkers.KindOfMesh(p.MeshPath) == SpawnKind.Loot).ToList();
        Assert.Equal(3, pins.Count);
        Assert.All(pins, p => Assert.True(p.InstanceKey!.Value.IsSpawnPoint));

        map.SelectedActor = house;
        map.SelectedInstanceKey = pins.Single(p => p.SpawnPoint!.Value.Index == 0).PickKey(map.PickParts);
        Assert.True(map.IsSpawnPointSelected);
        Assert.False(map.IsLootPointSelected);
        Assert.NotNull(map.SelectedRootWorld);
        Assert.True(FVector.Distance(map.SelectedRootWorld.Value.Translation, SyntheticSpawnerLevel.HouseAt + SyntheticSpawnerLevel.HouseLootAt + SyntheticSpawnerLevel.HouseMarkers[0]) < Tolerance);
        Assert.True(map.DuplicateSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task FitToGroundSetsDownTheSelectedPointNotItsSpawnerOrBuilding()
    {
        using var ctx = AppTestContext.Create();
        using var map = await OpenAsync(ctx);
        var journal = ctx.Services.Projects.Current!.Journal;
        // Flat ground 40 cm high around the sentry (2000 x 2000 cm).
        map.WorldBackdrop = new PreparedLevelScene([], [], new Dictionary<string, PreparedMeshAsset>(), new Dictionary<string, TextureImage>(), [], [], [], TimeSpan.Zero)
        {
            HeightField = new TerrainHeightField([(LandscapeExtractorTests.Ramp(20, 100f, new Vector2(-1000f, -1000f), (_, _) => 40f), null)]),
        };

        // A sentry's patrol point comes down onto the ground; the sentry stays.
        var sentry = map.AllActors.Single(a => a.Name == SyntheticSpawnerLevel.Sentry);
        map.SelectedActor = sentry;
        map.SelectedInstanceKey = InstanceKey.Of(sentry.SelectableId, SpawnPointArrays.PatrolPoints, InstanceKey.SpawnPointBase - 2);
        map.FitToGroundCommand.Execute(null);
        var op = Assert.IsType<SetSpawnPointsOp>(Assert.Single(journal.Applied).Op);
        Assert.True(FVector.Distance(op.New[2].Local.Location, SyntheticSpawnerLevel.Patrol[2] with { Z = 40f - SyntheticSpawnerLevel.SentryAt.Z }) < Tolerance, op.New[2].Local.Location.ToString());
        Assert.True(FVector.Distance(op.New[0].Local.Location, SyntheticSpawnerLevel.Patrol[0]) < Tolerance);
        Assert.Empty(map.ActorTransforms);
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title == Loc.T("Map.Ground.PointFitted"));

        // A loot point the building's class keeps (a read-only pin): nothing moves, the toast says why.
        var house = map.AllActors.Single(a => a.Name == SyntheticSpawnerLevel.House);
        map.SelectedActor = house;
        map.SelectedInstanceKey = InstanceKey.Of(house.SelectableId, SyntheticSpawnerLevel.HouseLoot, InstanceKey.LootPoint);
        Assert.True(map.IsLootPointSelected);
        map.FitToGroundCommand.Execute(null);
        Assert.Single(journal.Applied);
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Message == Loc.T("Map.Ground.LootPointFixed"));
    }

    private static async Task<MapPageViewModel> OpenAsync(AppTestContext ctx)
    {
        var game = ctx.Combine("game");
        SyntheticSpawnerLevel.WriteContent(game);
        var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Points");
        await map.LoadLevelsAsync([SyntheticSpawnerLevel.LevelPath]);
        map.ShowSpawns = true;
        return map;
    }
}
