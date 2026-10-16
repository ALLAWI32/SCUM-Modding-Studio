using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Tests.Level;
using ScumStudio.Viewport;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.App;

/// <summary>Copies made on the Map carry every per-actor edit of their source (synthetic loose game files, no GL).</summary>
public sealed class MapCopyTests
{
    private const string Rock = SyntheticLevels.RockPackage + ".SM_Rock";
    private const string Boulder = "/Game/ConZ_Files/Models/Rocks/SM_Boulder.SM_Boulder";
    private const string Other = "/Game/ConZ_Files/Maps/The_Island/A_0_Other";
    private static readonly BendValue Bent = new(45f, Sway1: 120f, Legs: 300f);

    [Fact]
    public async Task DuplicatePasteAndExtendKeepTheBend()
    {
        // F082 (Discord, seviscache): a bent wall's Duplicate, Paste and Extend came out straight.
        using var ctx = AppTestContext.Create();
        using var map = await OpenAsync(ctx);
        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        Apply(ctx, new BendActorOp(house.Reference, 0f, Bent.Degrees, NewSway1: Bent.Sway1, NewLegs: Bent.Legs));
        map.RefreshEdits();
        var state = ctx.Services.Projects.Current!.State;

        map.SelectedActor = house;
        map.DuplicateSelectedCommand.Execute(null);
        var duplicate = AddedSelection(map);
        Assert.True(state.GetBendValue(duplicate.Reference).IsNearly(Bent));
        var step = Assert.IsType<BatchOp>(ctx.Services.Projects.Current!.Journal.Applied[^1].Op); // one undo step: the copy and its bend
        Assert.Equal(house.Reference, Assert.IsType<DuplicateActorOp>(step.Ops[0]).Source);
        Assert.IsType<BendActorOp>(Assert.Single(step.Ops.Skip(1)));

        map.SelectedActor = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        map.CopySelectedCommand.Execute(null);
        map.PasteCommand.Execute(null);
        var pasted = AddedSelection(map);
        Assert.NotEqual(duplicate.Name, pasted.Name);
        Assert.True(state.GetBendValue(pasted.Reference).IsNearly(Bent));

        map.SelectedActor = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        map.Extend();
        var extended = AddedSelection(map);
        Assert.True(state.GetBendValue(extended.Reference).IsNearly(Bent));
        Assert.Equal(3, state.AddedActors.Count);

        // The export draws the copy bent, as its source.
        var bounds = new ScumStudio.Core.Geometry.BoundingBox(new System.Numerics.Vector3(-300, -20, 0), new System.Numerics.Vector3(300, 20, 200));
        var solid = new BendMesh(bounds, null, [new ScumStudio.Assets.Meshes.CollisionBox(new FVector(0, 0, 100), FRotator.Zero, new FVector(600, 40, 200))], new ScumStudio.Formats.FGuid(1, 2, 3, 4));
        var request = ProjectExporter.PlanLevel(state, SyntheticLevels.LevelPath, house.Level, [], null, null, _ => solid);
        Assert.NotNull(request.StaticMeshAdds.Single(a => a.NewName == duplicate.Name).Spline);
        Assert.NotNull(request.StaticMeshAdds.Single(a => a.NewName == extended.Name).Spline);
    }

    [Fact]
    public async Task DuplicateRightAfterAScaleDragCarriesIt()
    {
        // F221 (seviscache): the orange handle's scale waits half a second before it is journaled; Ctrl+D within that
        // time copied the object as it was before the pull, and the scale landed on the source after the copy.
        using var ctx = AppTestContext.Create();
        using var map = await OpenAsync(ctx);
        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        map.SelectedActor = house;
        map.DragScale(house.Actor.WorldTransform with { Scale3D = new FVector(3, 3, 3) });

        map.DuplicateSelectedCommand.Execute(null);
        var copy = Assert.IsType<DuplicateActorOp>(Assert.Single(ctx.Services.Projects.Current!.State.AddedActors).Value);
        Assert.Equal(3f, copy.Transform.Scale.X, 0.001f);
        Assert.Equal(3f, copy.Transform.Scale.Z, 0.001f);
        Assert.StartsWith("Duplicate ", ctx.Services.Projects.History[0].Summary, StringComparison.Ordinal); // nothing journaled after it
        Assert.StartsWith("Transform A_0_TestLevel/StaticMeshActor_1", ctx.Services.Projects.History[1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateOfAReplacedAddedMeshDrawsTheReplacement()
    {
        // F076: a duplicate of an added mesh whose mesh was replaced came back with the old mesh.
        using var ctx = AppTestContext.Create();
        using var map = await OpenAsync(ctx);
        Apply(ctx, new AddStaticMeshActorOp(SyntheticLevels.LevelPath, "SM_Rock_Added", Rock, TransformValue.At(0f, 0f, 0f)));
        map.RefreshEdits();
        var added = map.AllActors.Single(a => a.Name == "SM_Rock_Added");
        Apply(ctx, new ReplaceMeshOp(added.Reference, null, Rock, Boulder));
        map.RefreshEdits();

        map.SelectedActor = map.AllActors.Single(a => a.Name == "SM_Rock_Added");
        map.DuplicateSelectedCommand.Execute(null);
        var state = ctx.Services.Projects.Current!.State;
        var copy = state.AddedActors.Values.OfType<AddStaticMeshActorOp>().Single(a => a.NewName != "SM_Rock_Added");
        Assert.Equal(Boulder, copy.StaticMesh);
        Assert.Null(state.GetMeshOverride(copy.Created)); // in the create op, not replayed on top of it
    }

    [Fact]
    public async Task PasteIntoAnotherLevelAndPartCopiesKeepTheirMaterials()
    {
        // #23 (owner) / F096, F054: a building pasted elsewhere, or a part of it duplicated, lost its repainted slots.
        using var ctx = AppTestContext.Create();
        using var map = await OpenAsync(ctx, withOther: true);
        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        Assert.NotNull(house.Actor.Root!.OverrideMaterials);
        map.SelectedActor = house;
        map.CopySelectedCommand.Execute(null);
        map.SelectedActor = map.AllActors.Single(a => a.Name == "BP_Lamp_C_9");
        map.PasteCommand.Execute(null);
        var state = ctx.Services.Projects.Current!.State;
        var pasted = Assert.Single(state.AddedActors.Values.OfType<AddStaticMeshActorOp>());
        Assert.Equal(Other, pasted.Level);
        Assert.Equal(house.Actor.Root.OverrideMaterials, pasted.OverrideMaterials);

        var door = house.Actor.FindComponent("Door")!;
        Assert.NotNull(door.OverrideMaterials);
        map.SelectedActor = house;
        map.SelectedInstanceKey = InstanceKey.Of(house.SelectableId, "Door", InstanceKey.Part);
        map.DuplicateSelectedCommand.Execute(null);
        var part = state.AddedActors.Values.OfType<AddStaticMeshActorOp>().Single(a => a.Level == SyntheticLevels.LevelPath);
        Assert.Equal(door.OverrideMaterials, part.OverrideMaterials);
    }

    [Fact]
    public async Task ACopyShowsAndExportsTheLootItGets()
    {
        // F204: the loot panel said "stock" for a copy the export gives its source's loot.
        const string preset = "/Game/ConZ_Files/Items/SpawnerPresets2/Examine_Locker.Examine_Locker_C";
        using var ctx = AppTestContext.Create();
        using var map = await OpenAsync(ctx);
        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        var lootable = new LootSetting(true, [preset]);
        Apply(ctx, new SetLootOp(house.Reference, null, new LootSetting(false, []), lootable));
        Apply(ctx, new DuplicateActorOp(house.Reference, "Old_Copy", house.Actor.Root!.Relative)); // a copy made before copies carried their edits
        map.RefreshEdits();

        map.SelectedActor = map.AllActors.Single(a => a.Name == "Old_Copy");
        Assert.True(map.IsLootable);
        Assert.Equal([preset], map.LootPresets.Select(p => p.Path));

        map.SelectedActor = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        map.DuplicateSelectedCommand.Execute(null);
        var copy = AddedSelection(map);
        Assert.True(map.IsLootable);
        var state = ctx.Services.Projects.Current!.State;
        Assert.Equal(lootable, state.GetLoot(copy.Reference));

        var request = ProjectExporter.PlanLevel(state, SyntheticLevels.LevelPath, house.Level, []);
        Assert.Equal(lootable, request.Loot.Single(l => l.Actor == "Old_Copy").Loot);
        Assert.Equal(lootable, request.Loot.Single(l => l.Actor == copy.Name).Loot);
    }

    [Fact]
    public async Task ACopySetBackToItsStockLootKeepsIt()
    {
        // F204 (review): a copy of a looted locker set back to its stock loot came back with its source's, on screen and in
        // the export (the setting equalled the copy's stock, so it was dropped, and the copy got its source's again).
        const string preset = "/Game/ConZ_Files/Items/SpawnerPresets2/Examine_Locker.Examine_Locker_C";
        using var ctx = AppTestContext.Create();
        using var map = await OpenAsync(ctx);
        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        var stock = new LootSetting(false, []);
        var lootable = new LootSetting(true, [preset]);
        Apply(ctx, new SetLootOp(house.Reference, null, stock, lootable));
        map.RefreshEdits();
        map.SelectedActor = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        map.DuplicateSelectedCommand.Execute(null);
        var copy = AddedSelection(map);
        Assert.True(map.IsLootable);

        map.RemoveLootPresetCommand.Execute(map.LootPresets.Single());
        Assert.False(map.IsLootable);
        Assert.Empty(map.LootPresets);
        var state = ctx.Services.Projects.Current!.State;
        var request = ProjectExporter.PlanLevel(state, SyntheticLevels.LevelPath, house.Level, []);
        Assert.Equal(stock, request.Loot.Single(l => l.Actor == copy.Name).Loot);
        Assert.Equal(lootable, request.Loot.Single(l => l.Actor == house.Name).Loot);
    }

    [Fact]
    public async Task ACopyIsDrawnWithTheEditsItCarries()
    {
        // F078 (review): a copy's carried part moves, instance moves and replaced parts were exported, but drawn as its source
        // stores them (the viewport clones the source's placements; only the level's own actors had their edits drawn).
        using var ctx = AppTestContext.Create();
        using var map = await OpenAsync(ctx);
        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        var rocks = map.AllActors.Single(a => a.Name == "Rocks_Actor");
        var state = ctx.Services.Projects.Current!.State;
        Apply(ctx, EditOpFactory.SetTransform(house.Level, house.Actor, house.Actor.FindComponent("Door")!.Relative with { Location = new FVector(100, 0, 300) }, state, "Door"));
        Apply(ctx, new ReplaceMeshOp(house.Reference, "Door", Rock, Boulder));
        Apply(ctx, EditOpFactory.SetInstanceTransform(rocks.Level, rocks.Actor, "Rocks", 1, TransformValue.At(50f, 60f, 70f), state));
        var away = new FVector(5000, 0, 0);
        foreach (var (source, name) in new[] { (house, "House_Copy"), (rocks, "Rocks_Copy") })
        {
            var relative = source.Actor.Root!.Relative;
            var created = new ActorRef(source.Level.PackagePath, name);
            Apply(ctx, new BatchOp("Duplicate", [new DuplicateActorOp(source.Reference, name, relative with { Location = relative.Location + away }), .. state.EditsOf(source.Reference).OpsOn(created, false)]));
        }

        map.RefreshEdits();
        var houseCopy = map.AllActors.Single(a => a.Name == "House_Copy");
        var rocksCopy = map.AllActors.Single(a => a.Name == "Rocks_Copy");
        var door = InstanceKey.Of(house.SelectableId, "Door", InstanceKey.Part);
        var copiedDoor = InstanceKey.Of(houseCopy.SelectableId, "Door", InstanceKey.Part);
        Assert.True((map.InstanceTransforms[copiedDoor].Translation - map.InstanceTransforms[door].Translation).Equals(away, 0.5f));
        Assert.Equal(Boulder, map.PartMeshes[copiedDoor]);
        var rock = InstanceKey.Of(rocks.SelectableId, "Rocks", 1);
        Assert.True((map.InstanceTransforms[InstanceKey.Of(rocksCopy.SelectableId, "Rocks", 1)].Translation - map.InstanceTransforms[rock].Translation).Equals(away, 0.5f));

        // The copy's door is picked, moved and copied where the copy has it, not where its source does.
        map.SelectedActor = houseCopy;
        map.SelectedInstanceKey = copiedDoor;
        Assert.True(map.SelectedRootWorld!.Value.Translation.Equals(map.InstanceTransforms[copiedDoor].Translation, 0.5f));
    }

    [Fact]
    public async Task ACopyOfACopyWorksAfterTheOriginalIsDeleted()
    {
        // F203: Duplicate of A_Copy failed once A was deleted.
        using var ctx = AppTestContext.Create();
        using var map = await OpenAsync(ctx);
        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        map.SelectedActor = house;
        map.DuplicateSelectedCommand.Execute(null);
        var copy = AddedSelection(map);
        Apply(ctx, new DeleteActorOp(house.Reference));
        map.RefreshEdits();

        map.SelectedActor = map.AllActors.Single(a => a.Name == copy.Name);
        map.DuplicateSelectedCommand.Execute(null);
        Assert.DoesNotContain(ctx.Services.Notifications.Toasts, t => t.Title == "Duplicate failed");
        Assert.Equal(2, ctx.Services.Projects.Current!.State.AddedActors.Count);
        Assert.NotEqual(copy.Name, AddedSelection(map).Name);
    }

    [Fact]
    public async Task AGroupPasteKeepsEachMembersEditsAndSelectsEveryCopy()
    {
        using var ctx = AppTestContext.Create();
        using var map = await OpenAsync(ctx);
        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        var lamp = map.AllActors.Single(a => a.Name == "BP_Lamp_C_1");
        Apply(ctx, new BendActorOp(house.Reference, 0f, Bent.Degrees, NewSway1: Bent.Sway1, NewLegs: Bent.Legs));
        map.RefreshEdits();
        map.SelectedActor = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        map.ToggleGroup(lamp.SelectableId, null);
        Assert.True(map.HasGroup);
        map.CopySelectedCommand.Execute(null);
        map.PasteCommand.Execute(null);

        var state = ctx.Services.Projects.Current!.State;
        var copies = state.AddedActors.Values.OfType<DuplicateActorOp>().ToList();
        Assert.Equal(2, copies.Count);
        Assert.True(state.GetBendValue(copies.Single(c => c.Source.Actor == house.Name).Created).IsNearly(Bent));
        Assert.True(state.GetBendValue(copies.Single(c => c.Source.Actor == lamp.Name).Created).IsStraight);
        Assert.Equal(2, map.GroupWorlds.Count); // both copies are the selection, not only the first
        Assert.True(map.SelectedActor!.IsAdded);
    }

    [Fact]
    public async Task APrefabPlacesItsPartsWithTheirEditsOnce()
    {
        // F076/F083: a prefab of a bent, re-meshed, looted house came back with its stock mesh and loot (and the bend was
        // added on top of what the copy took from the live original).
        const string preset = "/Game/ConZ_Files/Items/SpawnerPresets2/Examine_Locker.Examine_Locker_C";
        using var ctx = AppTestContext.Create();
        using var map = await OpenAsync(ctx);
        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        var lootable = new LootSetting(true, [preset]);
        Apply(ctx, new BendActorOp(house.Reference, 0f, Bent.Degrees, NewSway1: Bent.Sway1, NewLegs: Bent.Legs));
        Apply(ctx, new ReplaceMeshOp(house.Reference, null, Rock, Boulder));
        Apply(ctx, new SetLootOp(house.Reference, null, new LootSetting(false, []), lootable));
        map.RefreshEdits();
        map.SelectedActor = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");

        var prefab = map.BuildPrefab("Bent house")!;
        var part = Assert.Single(prefab.Parts);
        Assert.Equal(Boulder, part.Mesh);
        Assert.Equal([preset], part.Loot);
        Assert.Null(part.Materials); // a replaced mesh draws its own materials
        Assert.True(part.Edits!.Bend!.Value.IsNearly(Bent));
        ctx.Services.Prefabs.Save(prefab);
        map.RefreshPrefabs();
        map.Prefabs.Single().Place.Execute(null);

        var step = Assert.IsType<BatchOp>(ctx.Services.Projects.Current!.Journal.Applied[^1].Op);
        Assert.Single(step.Ops.OfType<BendActorOp>());
        var placed = step.Ops[0].GetPrimaryTarget()!;
        var state = ctx.Services.Projects.Current!.State;
        Assert.True(state.GetBendValue(placed).IsNearly(Bent));
        Assert.Equal(Boulder, state.GetMeshOverride(placed));
        Assert.Equal(lootable, state.GetLoot(placed));
    }

    private static async Task<MapPageViewModel> OpenAsync(AppTestContext ctx, bool withOther = false)
    {
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        if (withOther)
        {
            SyntheticLevels.WriteBlueprintOnlyLevel(game, "A_0_Other", SyntheticLevels.BlueprintPackage, "BP_Lamp_C_9", new FVector(10, 20, 30), new FRotator(0, 15, 0));
        }

        var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Copies");
        await map.LoadLevelsAsync(withOther ? [SyntheticLevels.LevelPath, Other] : [SyntheticLevels.LevelPath]);
        return map;
    }

    private static void Apply(AppTestContext ctx, EditOp op) => ctx.Services.Projects.Apply(op);

    private static ActorItemViewModel AddedSelection(MapPageViewModel map)
    {
        var selected = map.SelectedActor;
        Assert.NotNull(selected);
        Assert.True(selected.IsAdded);
        return selected;
    }
}
