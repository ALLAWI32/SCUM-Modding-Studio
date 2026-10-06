using System.Globalization;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Pak;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;
using UeVector = CUE4Parse.UE4.Objects.Core.Math.FVector;

namespace ScumStudio.Tests.App;

/// <summary>
/// Discord igor: "There is no interaction with certain spawn objects." A house's fixed-item spawner (the drill press, stove
/// and fridge of a Tisno house) was a pin that could not be moved, the item it spawns was never drawn, and a car shop's
/// vehicle box picked nothing. Now the pin and the drawn item pick that spawner as a part of the building and move it.
/// Real game files only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class SpawnPartsRealTests
{
    internal const string Maps = "/Game/ConZ_Files/Maps/The_Island/";
    internal const string Tisno = Maps + "A_2_Tisno_02_Interior_03";

    [Fact]
    public void TheItemsASpawnerSpawnsAreDrawnAtItAndPickAsIt()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return;
        }

        var reader = new Cue4ParseLevelReader(catalog);
        var tisno = LevelDocument.Load(reader, Tisno);
        var placements = LevelScenePreparer.CollectPlacements(tisno, 0);
        var house = tisno.Actors.Single(a => a.Name == "BP_SB_House_B_INT_4");
        var houseId = LevelScenePreparer.SelectableIdOf(0, house);
        foreach (var (spawner, mesh) in new[] { ("WIS_Drillpress_01", ".SM_Drill_Press_01"), ("WIS_KitchenStove_01", ".SM_Stove01_RW"), ("WIS_Refrigerator_01", ".SM_Refrigerator01_closed") })
        {
            var component = house.FindComponent(spawner)!;
            var key = InstanceKey.Of(houseId, spawner, InstanceKey.Part);
            Assert.NotNull(component.SpawnMarkers.Single().ItemClassPath);
            Assert.True(SpawnMarkers.IsSpawnPart(house, component));

            // The item stands where it spawns and picks as the spawner, like the spawner's pin.
            var item = placements.Single(p => p.Spawner == spawner && p.MeshPath.EndsWith(mesh, StringComparison.Ordinal));
            Assert.True(FVector.Distance(item.World.Translation, component.WorldTransform.Translation) < 1f);
            Assert.Equal(key, item.InstanceKey);
            var pin = placements.Single(p => p.Spawner == spawner && SpawnMarkers.IsMarker(p.MeshPath));
            Assert.Equal(houseId, pin.SelectableId);
            Assert.Equal(key, pin.InstanceKey);
            Assert.Null(pin.LootMarker);
        }

        // A shelf's loot points keep their own keys (they show what spawns there, they move with the house).
        Assert.All(placements.Where(p => p.MeshPath == SpawnMarkers.MeshKey(SpawnKind.Loot) && p.Spawner is null && ReferenceEquals(p.Actor, house)),
            p => Assert.True(p.InstanceKey!.Value.IsLootPoint));

        // A drill press standing on its own (a native world spawner): drawn, and it picks the spawner actor itself.
        var factory = LevelDocument.Load(reader, Maps + "B_0_Brick_Factory_01");
        var standalone = factory.Actors.Single(a => a.Name == "Work_Drillpress_01");
        var press = LevelScenePreparer.CollectPlacements(factory, 0).Single(p => ReferenceEquals(p.Actor, standalone) && p.MeshPath.EndsWith(".SM_Drill_Press_01", StringComparison.Ordinal));
        Assert.True(FVector.Distance(press.World.Translation, standalone.WorldTransform.Translation) < 1f);
        Assert.Null(press.InstanceKey);
        Assert.Equal(LevelScenePreparer.SelectableIdOf(0, standalone), press.SelectableId);
    }

    [Fact]
    public async Task AHousesDrillPressSpawnerIsSelectedMovedAndExported()
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
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawners");
        await map.LoadLevelsAsync([Tisno]);
        map.ShowSpawns = true;

        // The drill press's pin picks the spawner as a part of the house: the gizmo stands at it.
        var house = map.AllActors.Single(a => a.Name == "BP_SB_House_B_INT_4");
        var spawner = house.Actor.FindComponent("WIS_Drillpress_01")!;
        var pin = map.PreparedScene!.Placements.Single(p => p.Spawner == spawner.Name && SpawnMarkers.IsMarker(p.MeshPath));
        Assert.Equal(house.SelectableId, pin.SelectableId);
        map.SelectedInstanceKey = pin.InstanceKey;
        map.SelectedActorId = house.SelectableId;
        Assert.True(map.HasSelectedPart);
        Assert.False(map.IsLootPointSelected);
        Assert.True(FVector.Distance(map.SelectedRootWorld!.Value.Translation, spawner.WorldTransform.Translation) < 1f);
        await Until(() => map.SpawnInfo.Count > 0);
        Assert.Contains(map.SpawnInfo, r => r.IsHeader && r.Text == "Always the same item: Work Drillpress 01");
        Assert.Contains(map.ActorProperties, r => r.Value.Contains("cannot be copied", StringComparison.Ordinal) || r.Value.Contains("Copy, Duplicate and Delete are off", StringComparison.Ordinal));

        // No mesh to copy, and a spawner scaled to nothing is not known to work in the game.
        Assert.False(map.DeleteSelectedCommand.CanExecute(null));
        Assert.False(map.DuplicateSelectedCommand.CanExecute(null));
        Assert.False(map.CopySelectedCommand.CanExecute(null));
        Assert.True(map.ApplyTransformCommand.CanExecute(null));

        // One metre along its own X: written to the house's stored component.
        var moved = spawner.Relative.Location + new FVector(100f, 0f, 0f);
        map.EditLocation = string.Create(CultureInfo.InvariantCulture, $"{moved.X}, {moved.Y}, {moved.Z}");
        map.ApplyTransformCommand.Execute(null);
        var op = Assert.IsType<SetTransformOp>(ctx.Services.Projects.Current!.Journal.Applied[^1].Op);
        Assert.Equal("WIS_Drillpress_01", op.Component);
        Assert.Equal(house.Name, op.Target.Actor);
        Assert.True(FVector.Distance(map.SelectedRootWorld!.Value.Translation, spawner.WorldTransform.Translation) is > 99f and < 101f);
        Assert.True(map.InstanceTransforms.ContainsKey(pin.InstanceKey!.Value));

        var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, ctx.Services.Workspace.Catalog!,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        Assert.True(written.TryLoadPackage(Tisno, out var package));
        var export = package.GetExports().Single(e => e.Name == "WIS_Drillpress_01" && e.Outer?.Name == house.Name);
        Assert.True(export.TryGetValue(out UeVector location, "RelativeLocation"));
        Assert.True(FVector.Distance(new FVector(location.X, location.Y, location.Z), moved) < 0.01f);
    }

    [Fact]
    public async Task ACarShopsVehicleBoxIsAPartAndItsLayerHidesOnlyThePins()
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
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Shops");
        await map.LoadLevelsAsync([Maps + "A_0_Outpost"]);
        map.ShowSpawns = true;

        // The blue pin picks the shop's box (it picked nothing: the click cleared the selection).
        var shop = map.AllActors.Single(a => a.Name == "BP_Outpost_CarShop_NPC_and_VehicleSpawner_5");
        var box = shop.Actor.FindComponent("VehicleSpawnBox")!;
        var pin = map.PreparedScene!.Placements.Single(p => p.Spawner == box.Name && ReferenceEquals(p.Actor, shop.Actor));
        Assert.Equal(SpawnMarkers.MeshKey(SpawnKind.Vehicle), pin.MeshPath);
        Assert.Equal(shop.SelectableId, pin.SelectableId);
        Assert.Equal(InstanceKey.Of(shop.SelectableId, box.Name, InstanceKey.Part), pin.InstanceKey);
        // A translucent box of the real size: the component's scale times its extent (a half size), over the metre-wide box mesh.
        Assert.Equal(box.WorldTransform.Scale3D.X * (box.BoxExtent ?? new FVector(32f)).X * 2f / SpawnMarkers.BoxSize, pin.World.Scale3D.X, 0.001f);
        Assert.Equal(SpawnShape.Box, SpawnMarkers.ShapeOf(SpawnKind.Vehicle));

        map.SelectedInstanceKey = pin.InstanceKey;
        map.SelectedActorId = shop.SelectableId;
        Assert.True(map.HasSelectedPart);
        Assert.True(FVector.Distance(map.SelectedRootWorld!.Value.Translation, box.WorldTransform.Translation) < 1f); // scaled 11 x 5 x 3.5 and turned
        Assert.False(map.DeleteSelectedCommand.CanExecute(null));
        var moved = box.Relative.Location + new FVector(0f, 300f, 0f);
        map.EditLocation = string.Create(CultureInfo.InvariantCulture, $"{moved.X}, {moved.Y}, {moved.Z}");
        map.ApplyTransformCommand.Execute(null);
        Assert.Equal(box.Name, Assert.IsType<SetTransformOp>(ctx.Services.Projects.Current!.Journal.Applied[^1].Op).Component);
        Assert.True(FVector.Distance(map.SelectedRootWorld!.Value.Translation, box.WorldTransform.Translation) is > 299f and < 301f);

        // Car shops off: only those pins go (the viewport hides pins by kind), not the shop, not the other pins.
        map.SpawnLayers.Single(l => l.Key == "CarShops").IsVisible = false;
        Assert.Contains(SpawnKind.Vehicle, map.HiddenPinKinds);
        Assert.DoesNotContain(SpawnKind.Trader, map.HiddenPinKinds);
        Assert.DoesNotContain(SpawnKind.Loot, map.HiddenPinKinds);
        Assert.DoesNotContain(shop.SelectableId, map.HiddenActorIds);
        Assert.DoesNotContain(pin.InstanceKey!.Value, map.HiddenInstanceKeys);
    }

    internal static AssetCatalog? Open() =>
        Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks)
            ? AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() })
            : null;

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 600 && !done(); i++)
        {
            await Task.Delay(100);
        }

        Assert.True(done());
    }
}

/// <summary>
/// The drawn scene of a moved spawn part: the pin and the item move together, the item keeps its place on the spawner,
/// the pin stays upright and small (a car shop's box is scaled 11 x 5 x 3.5). Real game files and OpenGL; a picture of the
/// selected drill press with <c>SCUMSTUDIO_SCREENSHOTS</c>.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class SpawnPartsViewportRealTests
{
    [GlFact]
    public async Task APinAndItsItemMoveTogetherAndThePinStaysSmall()
    {
        using var catalog = SpawnPartsRealTests.Open();
        if (catalog is null)
        {
            return;
        }

        var reader = new Cue4ParseLevelReader(catalog);
        var documents = new[] { SpawnPartsRealTests.Tisno, SpawnPartsRealTests.Maps + "A_0_Outpost" }.Select(l => LevelDocument.Load(reader, l)).ToList();
        var prepared = new LevelScenePreparer(catalog).Prepare(documents, new LevelSceneOptions { TextureSize = 256 });
        using var harness = GlHarness.Create(800, 600);
        using var level = LevelSceneUploader.Upload(harness.Renderer, prepared);

        var house = documents[0].Actors.Single(a => a.Name == "BP_SB_House_B_INT_4");
        var spawner = house.FindComponent("WIS_Drillpress_01")!;
        var key = InstanceKey.Of(LevelScenePreparer.SelectableIdOf(0, house), spawner.Name, InstanceKey.Part);
        var nodes = level.Scene.Nodes.Where(n => n.Tag is ScenePlacement { InstanceKey: { } k } && k == key).ToList();
        var item = nodes.Single(n => ((ScenePlacement)n.Tag!).MeshPath.EndsWith(".SM_Drill_Press_01", StringComparison.Ordinal));
        var pin = nodes.Single(n => SpawnMarkers.IsMarker(((ScenePlacement)n.Tag!).MeshPath));
        var pinWorld = ((ScenePlacement)pin.Tag!).World;
        await SaveAsync("spawn-drillpress-selected", spawner.WorldTransform);

        // Two metres over and turned a quarter: the press goes with the spawner, the marker moves and turns but keeps its size.
        var to = new FTransform(FQuat.MakeFromEuler(new FVector(0f, 0f, 90f)) * spawner.WorldTransform.Rotation, spawner.WorldTransform.Translation + new FVector(200f, 0f, 0f), FVector.One);
        level.SetInstanceTransform(key, to);
        RenderAssert.Near(UeToGl.ModelMatrix(((ScenePlacement)item.Tag!).World.GetRelativeTransform(spawner.WorldTransform) * to), item.LocalTransform, 1e-3f);
        RenderAssert.Near(UeToGl.ModelMatrix(to with { Scale3D = pinWorld.Scale3D }), pin.LocalTransform, 1e-3f);
        await SaveAsync("spawn-drillpress-moved", to);
        level.SetInstanceTransforms(null);
        Assert.Equal(((ScenePlacement)item.Tag!).GlModel, item.LocalTransform);
        Assert.Equal(((ScenePlacement)pin.Tag!).GlModel, pin.LocalTransform);

        // A car shop's box: moved with its 11 x 5 x 3.5 scale, the translucent box keeps its real size.
        var shop = documents[1].Actors.Single(a => a.Name == "BP_Outpost_CarShop_NPC_and_VehicleSpawner_5");
        var box = shop.FindComponent("VehicleSpawnBox")!;
        var boxKey = InstanceKey.Of(LevelScenePreparer.SelectableIdOf(1, shop), box.Name, InstanceKey.Part);
        var boxPin = level.Scene.Nodes.Single(n => n.Tag is ScenePlacement { InstanceKey: { } k } && k == boxKey);
        var boxTo = box.WorldTransform with { Translation = box.WorldTransform.Translation + new FVector(0f, 300f, 0f) };
        level.SetInstanceTransform(boxKey, boxTo);
        var boxSize = ((ScenePlacement)boxPin.Tag!).World.Scale3D;
        RenderAssert.Near(UeToGl.ModelMatrix(boxTo with { Scale3D = boxSize }), boxPin.LocalTransform, 1e-3f);
        Assert.Equal(box.WorldTransform.Scale3D.X * (box.BoxExtent ?? new FVector(32f)).X * 2f / SpawnMarkers.BoxSize, boxSize.X, 0.001f);

        async Task SaveAsync(string name, FTransform at)
        {
            if (Environment.GetEnvironmentVariable("SCUMSTUDIO_SCREENSHOTS") is not { Length: > 0 } folder)
            {
                return;
            }

            level.SelectInstance(key);
            var camera = new FlyCamera();
            camera.SetClipRange(5f, 1_000_000f);
            camera.Orbit(UeToGl.Point(at.Translation + new FVector(0f, 0f, 60f)), -60f, -25f, 320f);
            harness.Renderer.Render(harness.Target, level.Scene, camera);
            Directory.CreateDirectory(folder);
            await ImageExport.SavePngAsync(harness.Target.ReadColorRgba(), 800, 600, Path.Combine(folder, name + ".png"));
        }
    }
}
